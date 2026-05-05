using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;

namespace NINA.Headless.Services;

/// <summary>
/// FITS → PNG converter using NINA-equivalent MTF auto-stretch.
///
///   • Mono cameras → grayscale L8.
///   • Colour OSC cameras at 1×1 binning → bilinear debayer → RGB.
///   • Colour OSC cameras with hardware binning > 1 → mono path
///     (Bayer pattern collapses under hardware binning, debayering would
///     produce false colour).
///
/// Stretch is the same midtones-transfer / median-MAD algorithm NINA uses
/// — see <see cref="NinaImageProcessing"/>. The naive 1%/99% percentile
/// produced a flat midtone for any frame with low variance; MTF gives
/// proper astronomy-quality stretch.
/// </summary>
public static class FitsToPng
{
    /// <param name="softwareBinning">N×N average pooling applied AFTER debayer.
    /// Caller passes the user's intended binning here; hardware binning is
    /// kept at 1×1 to preserve the Bayer pattern. 1 = no downsample.</param>
    public static byte[] Convert(byte[] fitsBytes, int softwareBinning = 1)
    {
        var hdr = FitsReader.ParseHeader(fitsBytes);
        var pxFloat = FitsReader.ReadPixels(fitsBytes, hdr.dataOffset, hdr.width, hdr.height, hdr.bitpix, hdr.bzero, hdr.bscale);
        int width = hdr.width, height = hdr.height;
        var bayerPattern = ExtractBayerPattern(fitsBytes);
        var hardwareBinning = ExtractBinning(fitsBytes);

        int statsBitDepth = hdr.bitpix == 8 ? 8 : 16;
        ushort[] pxU16 = ToUshortPixels(pxFloat, hdr.bitpix);

        var bin = Math.Max(1, softwareBinning);

        // Path selection:
        //   • No Bayer pattern (mono camera) → single LUT, mono path.
        //   • Hardware-binned colour (XBINNING > 1) → mono path; the Bayer
        //     pattern is destroyed and debayering would produce false colour.
        //   • Otherwise → bilinear debayer + UNLINKED per-channel stretch.
        //     Linked (one LUT for R/G/B) lets the green channel — which is
        //     ~2× more sensitive on OSC sensors — saturate first while R/B
        //     are still in midtones, producing the green-cast washed-out
        //     output the user reported. Unlinked stretch builds a separate
        //     LUT per channel so each is brought to its own target median,
        //     equalising the colour balance.
        if (string.IsNullOrEmpty(bayerPattern) || hardwareBinning > 1)
        {
            var monoStats = NinaImageProcessing.ComputeStats(pxU16, statsBitDepth);
            var monoMap = NinaImageProcessing.BuildStretchMap(monoStats);
            return EncodeMono(pxU16, width, height, monoMap, bin);
        }
        var (mapR, mapG, mapB) = BuildPerChannelStretchMaps(pxU16, width, height, bayerPattern, statsBitDepth);
        return EncodeColor(pxU16, width, height, bayerPattern, mapR, mapG, mapB, bin);
    }

    /// Compute per-channel stats by sampling the Bayer mosaic at each
    /// channel's known positions, then build a separate stretch LUT for R,
    /// G, B. NINA's "unlinked" stretch flow.
    private static (ushort[] r, ushort[] g, ushort[] b) BuildPerChannelStretchMaps(
        ushort[] px, int w, int h, string pattern, int bitDepth)
    {
        // Half-res counts because each channel only occupies half the
        // rows (or half the cols for green). Allocate generously to avoid
        // bounds work in the hot loop.
        var rPx = new ushort[(w / 2 + 1) * (h / 2 + 1)];
        var gPx = new ushort[w * h];          // G is 50% of pixels
        var bPx = new ushort[(w / 2 + 1) * (h / 2 + 1)];
        int rN = 0, gN = 0, bN = 0;

        // Determine which (row-parity, col-parity) maps to which channel.
        for (int y = 0; y < h; y++)
        {
            bool er = (y & 1) == 0;
            for (int x = 0; x < w; x++)
            {
                bool ec = (x & 1) == 0;
                char role = pattern switch
                {
                    "RGGB" => er ? (ec ? 'R' : 'g') : (ec ? 'g' : 'B'),
                    "BGGR" => er ? (ec ? 'B' : 'g') : (ec ? 'g' : 'R'),
                    "GRBG" => er ? (ec ? 'g' : 'R') : (ec ? 'B' : 'g'),
                    "GBRG" => er ? (ec ? 'g' : 'B') : (ec ? 'R' : 'g'),
                    _      => 'g',
                };
                ushort v = px[y * w + x];
                if (role == 'R') rPx[rN++] = v;
                else if (role == 'B') bPx[bN++] = v;
                else gPx[gN++] = v;
            }
        }

        Array.Resize(ref rPx, rN);
        Array.Resize(ref gPx, gN);
        Array.Resize(ref bPx, bN);

        var rStats = NinaImageProcessing.ComputeStats(rPx, bitDepth);
        var gStats = NinaImageProcessing.ComputeStats(gPx, bitDepth);
        var bStats = NinaImageProcessing.ComputeStats(bPx, bitDepth);
        return (
            NinaImageProcessing.BuildStretchMap(rStats),
            NinaImageProcessing.BuildStretchMap(gStats),
            NinaImageProcessing.BuildStretchMap(bStats));
    }

    /// Convert raw FITS-decoded pixels (any bit depth) to ushort range
    /// 0..65535 so the NINA statistics + LUT operate on a uniform space.
    private static ushort[] ToUshortPixels(float[] px, int bitpix)
    {
        var result = new ushort[px.Length];
        if (bitpix == 8)
        {
            // 8-bit data: scale into 16-bit space (× 257) so the same LUT
            // covers it without separate code paths.
            for (int i = 0; i < px.Length; i++)
            {
                int v = (int)Math.Clamp(px[i], 0f, 255f) * 257;
                result[i] = (ushort)v;
            }
        }
        else if (bitpix == 16)
        {
            for (int i = 0; i < px.Length; i++)
            {
                int v = (int)Math.Clamp(px[i], 0f, 65535f);
                result[i] = (ushort)v;
            }
        }
        else // -32 (float) — normalise by max
        {
            float max = 0;
            for (int i = 0; i < px.Length; i++) if (px[i] > max) max = px[i];
            float scale = max > 0 ? 65535f / max : 1f;
            for (int i = 0; i < px.Length; i++)
            {
                int v = (int)Math.Clamp(px[i] * scale, 0f, 65535f);
                result[i] = (ushort)v;
            }
        }
        return result;
    }

    // ---- Mono path ----

    private static byte[] EncodeMono(ushort[] px, int width, int height, ushort[] stretchMap, int bin)
    {
        int outW = Math.Max(1, width / bin);
        int outH = Math.Max(1, height / bin);
        using var img = new Image<L8>(outW, outH);
        for (int y = 0; y < outH; y++)
        {
            var row = img.DangerousGetPixelRowMemory(y).Span;
            for (int x = 0; x < outW; x++)
            {
                ushort raw = SamplePixel(px, width, height, x, y, bin);
                ushort stretched = stretchMap[raw];
                // Down-sample 16 → 8 by taking the high byte (no extra rounding
                // needed; stretchMap already mapped to a perceptual midtone).
                row[x] = new L8((byte)(stretched >> 8));
            }
        }
        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        return ms.ToArray();
    }

    /// Average pool a bin × bin block from (x*bin, y*bin) of the source.
    private static ushort SamplePixel(ushort[] px, int srcW, int srcH, int x, int y, int bin)
    {
        if (bin <= 1) return px[y * srcW + x];
        int sx = x * bin, sy = y * bin;
        long sum = 0; int n = 0;
        for (int dy = 0; dy < bin; dy++)
        {
            int yy = sy + dy;
            if (yy >= srcH) break;
            for (int dx = 0; dx < bin; dx++)
            {
                int xx = sx + dx;
                if (xx >= srcW) break;
                sum += px[yy * srcW + xx];
                n++;
            }
        }
        return n == 0 ? (ushort)0 : (ushort)(sum / n);
    }

    // ---- Color path: bilinear debayer at full resolution ----

    /// Bilinear demosaic — for each Bayer-mosaic pixel, fill in the missing
    /// two channels by averaging the appropriate neighbours. Output is the
    /// same dimensions as the input when `bin` = 1, or `width/bin × height/bin`
    /// when the caller asked for software binning (average pool of bin × bin
    /// debayered RGB pixels per output pixel).
    private static byte[] EncodeColor(ushort[] px, int width, int height, string pattern, ushort[] mapR, ushort[] mapG, ushort[] mapB, int bin)
    {
        int outW = Math.Max(1, width / bin);
        int outH = Math.Max(1, height / bin);
        using var img = new Image<Rgb24>(outW, outH);
        for (int y = 0; y < outH; y++)
        {
            var row = img.DangerousGetPixelRowMemory(y).Span;
            for (int x = 0; x < outW; x++)
            {
                long rSum = 0, gSum = 0, bSum = 0;
                int n = 0;
                int sx = x * bin, sy = y * bin;
                for (int dy = 0; dy < bin; dy++)
                {
                    int yy = sy + dy;
                    if (yy >= height) break;
                    for (int dx = 0; dx < bin; dx++)
                    {
                        int xx = sx + dx;
                        if (xx >= width) break;
                        var (r, g, b) = Demosaic(px, width, height, xx, yy, pattern);
                        rSum += r; gSum += g; bSum += b;
                        n++;
                    }
                }
                ushort r16, g16, b16;
                if (n > 0)
                {
                    r16 = (ushort)(rSum / n);
                    g16 = (ushort)(gSum / n);
                    b16 = (ushort)(bSum / n);
                }
                else { r16 = g16 = b16 = 0; }
                row[x] = new Rgb24(
                    (byte)(mapR[r16] >> 8),
                    (byte)(mapG[g16] >> 8),
                    (byte)(mapB[b16] >> 8)
                );
            }
        }
        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        return ms.ToArray();
    }

    /// Returns (R, G, B) for the pixel at (x, y) given the Bayer pattern.
    /// Each Bayer pattern repeats every 2×2 pixels; the offset of each
    /// channel within that 2×2 block determines the neighbour averaging.
    private static (int r, int g, int b) Demosaic(
        ushort[] px, int w, int h, int x, int y, string pattern)
    {
        // Determine pixel role (R, G_red_row, G_blue_row, B) from pattern + parity.
        bool er = (y & 1) == 0; // even row?
        bool ec = (x & 1) == 0; // even col?

        // RGGB: (er,ec)=R, (er,!ec)=G_R, (!er,ec)=G_B, (!er,!ec)=B
        // BGGR: (er,ec)=B, (er,!ec)=G_B, (!er,ec)=G_R, (!er,!ec)=R
        // GRBG: (er,ec)=G_R, (er,!ec)=R, (!er,ec)=B, (!er,!ec)=G_B
        // GBRG: (er,ec)=G_B, (er,!ec)=B, (!er,ec)=R, (!er,!ec)=G_R
        char role = pattern switch
        {
            "RGGB" => er ? (ec ? 'R' : 'g') : (ec ? 'g' : 'B'),
            "BGGR" => er ? (ec ? 'B' : 'g') : (ec ? 'g' : 'R'),
            "GRBG" => er ? (ec ? 'g' : 'R') : (ec ? 'B' : 'g'),
            "GBRG" => er ? (ec ? 'g' : 'B') : (ec ? 'R' : 'g'),
            _      => 'g',
        };
        // Sub-role for 'g': is it on a red row or blue row? We need this
        // because for G pixels the R/B are pulled from different directions.
        // From the pattern above:
        //   RGGB: G_R is on row 0 (even), G_B is on row 1 (odd)
        //   BGGR: G_B is on row 0,         G_R is on row 1
        //   GRBG: G_R is on row 0,         G_B is on row 1
        //   GBRG: G_B is on row 0,         G_R is on row 1
        bool gOnRedRow = pattern switch
        {
            "RGGB" => er,
            "BGGR" => !er,
            "GRBG" => er,
            "GBRG" => !er,
            _      => true,
        };

        int center = px[y * w + x];
        int n  = px[Clamp(y - 1, h) * w + x];
        int s  = px[Clamp(y + 1, h) * w + x];
        int e  = px[y * w + Clamp(x + 1, w)];
        int wv = px[y * w + Clamp(x - 1, w)];
        int ne = px[Clamp(y - 1, h) * w + Clamp(x + 1, w)];
        int nw = px[Clamp(y - 1, h) * w + Clamp(x - 1, w)];
        int se = px[Clamp(y + 1, h) * w + Clamp(x + 1, w)];
        int sw = px[Clamp(y + 1, h) * w + Clamp(x - 1, w)];

        switch (role)
        {
            case 'R':
                return (center, (n + s + e + wv) >> 2, (ne + nw + se + sw) >> 2);
            case 'B':
                return ((ne + nw + se + sw) >> 2, (n + s + e + wv) >> 2, center);
            default: // 'g'
                if (gOnRedRow)
                    return ((e + wv) >> 1, center, (n + s) >> 1);
                else
                    return ((n + s) >> 1, center, (e + wv) >> 1);
        }
    }

    private static int Clamp(int v, int max)
    {
        if (v < 0) return 0;
        if (v >= max) return max - 1;
        return v;
    }

    // ---- FITS header lookups ----

    /// Pulls the Bayer pattern from the FITS primary header. Returns "" when
    /// absent (mono camera). Standard values: RGGB / BGGR / GRBG / GBRG.
    private static string ExtractBayerPattern(byte[] bytes)
    {
        return ExtractStringHeader(bytes, "BAYERPAT");
    }

    /// Pulls the X-axis binning factor (XBINNING / BINNING / BIN_X). 1 by
    /// default. INDI drivers and most camera vendors write this consistently.
    private static int ExtractBinning(byte[] bytes)
    {
        var v = ExtractIntHeader(bytes, "XBINNING");
        if (v <= 0) v = ExtractIntHeader(bytes, "BINNING");
        if (v <= 0) v = ExtractIntHeader(bytes, "BIN_X");
        return v <= 0 ? 1 : v;
    }

    private static string ExtractStringHeader(byte[] bytes, string key)
    {
        int offset = 0;
        while (offset < bytes.Length)
        {
            var blockEnd = offset + 2880;
            for (int rec = offset; rec < blockEnd && rec + 80 <= bytes.Length; rec += 80)
            {
                var card = Encoding.ASCII.GetString(bytes, rec, 80);
                if (card.StartsWith("END")) return "";
                if (!card.StartsWith(key)) continue;
                var eq = card.IndexOf('=');
                if (eq < 0) continue;
                var rest = card[(eq + 1)..];
                int q1 = rest.IndexOf('\'');
                if (q1 < 0) continue;
                int q2 = rest.IndexOf('\'', q1 + 1);
                if (q2 < 0) continue;
                return rest[(q1 + 1)..q2].Trim().ToUpperInvariant();
            }
            offset = blockEnd;
        }
        return "";
    }

    private static int ExtractIntHeader(byte[] bytes, string key)
    {
        int offset = 0;
        while (offset < bytes.Length)
        {
            var blockEnd = offset + 2880;
            for (int rec = offset; rec < blockEnd && rec + 80 <= bytes.Length; rec += 80)
            {
                var card = Encoding.ASCII.GetString(bytes, rec, 80);
                if (card.StartsWith("END")) return 0;
                if (!card.StartsWith(key)) continue;
                var eq = card.IndexOf('=');
                if (eq < 0) continue;
                var slash = card.IndexOf('/', eq);
                var valPart = (slash < 0 ? card[(eq + 1)..] : card[(eq + 1)..slash]).Trim();
                if (int.TryParse(valPart, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v))
                    return v;
            }
            offset = blockEnd;
        }
        return 0;
    }
}
