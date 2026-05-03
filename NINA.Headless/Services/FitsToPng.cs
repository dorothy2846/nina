using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;

namespace NINA.Headless.Services;

/// <summary>
/// FITS → PNG converter.
///   • Mono cameras → grayscale L8.
///   • Colour OSC cameras at 1×1 binning → bilinear debayer → full-sensor RGB.
///   • Colour OSC cameras at 2× / 3× / 4× hardware binning → mono path
///     (Bayer pattern collapses under hardware binning, so debayer would
///     produce false colour). The user gets a properly resolution-honouring
///     output that matches whatever they set in the app.
///
/// Linear autostretch (1 % / 99 %) keeps faint detail visible without per-frame
/// configuration.
/// </summary>
public static class FitsToPng
{
    /// <param name="softwareBinning">N×N average pooling applied AFTER debayer.
    /// Caller passes the user's intended binning here; hardware binning is
    /// kept at 1×1 to preserve the Bayer pattern. 1 = no downsample.</param>
    public static byte[] Convert(byte[] fitsBytes, int softwareBinning = 1)
    {
        var (px, width, height) = FitsReader.Read(fitsBytes);
        var bayerPattern = ExtractBayerPattern(fitsBytes);
        var hardwareBinning = ExtractBinning(fitsBytes);

        // Unified autostretch — compute clip points across all pixels so the
        // R/G/B channels keep their relative balance after the stretch.
        var sorted = (float[])px.Clone();
        Array.Sort(sorted);
        var lo = sorted[(int)(sorted.Length * 0.01)];
        var hi = sorted[(int)(sorted.Length * 0.99)];
        if (hi <= lo) hi = lo + 1f;

        var bin = Math.Max(1, softwareBinning);

        // Path selection:
        //   • No Bayer pattern (mono camera) → mono path with optional bin.
        //   • Hardware-binned colour (XBINNING > 1) → mono path; the Bayer
        //     pattern is destroyed and debayering would produce false colour.
        //     This shouldn't happen now that the controller forces 1×1, but
        //     guard against legacy or external FITS files.
        //   • Otherwise → bilinear debayer + optional software bin.
        if (string.IsNullOrEmpty(bayerPattern) || hardwareBinning > 1)
        {
            return EncodeMono(px, width, height, lo, hi, bin);
        }
        return EncodeColor(px, width, height, bayerPattern, lo, hi, bin);
    }

    // ---- Mono path ----

    private static byte[] EncodeMono(float[] px, int width, int height, float lo, float hi, int bin)
    {
        int outW = Math.Max(1, width / bin);
        int outH = Math.Max(1, height / bin);
        using var img = new Image<L8>(outW, outH);
        var range = hi - lo;
        for (int y = 0; y < outH; y++)
        {
            var row = img.DangerousGetPixelRowMemory(y).Span;
            for (int x = 0; x < outW; x++)
            {
                row[x] = new L8(StretchByte(SamplePixel(px, width, height, x, y, bin), lo, range));
            }
        }
        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        return ms.ToArray();
    }

    /// Average pool a bin × bin block from (x*bin, y*bin) of the source.
    private static float SamplePixel(float[] px, int srcW, int srcH, int x, int y, int bin)
    {
        if (bin <= 1) return px[y * srcW + x];
        int sx = x * bin, sy = y * bin;
        float sum = 0; int n = 0;
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
        return n == 0 ? 0 : sum / n;
    }

    // ---- Color path: bilinear debayer at full resolution ----

    /// Bilinear demosaic — for each Bayer-mosaic pixel, fill in the missing
    /// two channels by averaging the appropriate neighbours. Output is the
    /// same dimensions as the input when `bin` = 1, or `width/bin × height/bin`
    /// when the caller asked for software binning (average pool of bin × bin
    /// debayered RGB pixels per output pixel).
    private static byte[] EncodeColor(float[] px, int width, int height, string pattern, float lo, float hi, int bin)
    {
        int outW = Math.Max(1, width / bin);
        int outH = Math.Max(1, height / bin);
        using var img = new Image<Rgb24>(outW, outH);
        var range = hi - lo;
        for (int y = 0; y < outH; y++)
        {
            var row = img.DangerousGetPixelRowMemory(y).Span;
            for (int x = 0; x < outW; x++)
            {
                float rSum = 0, gSum = 0, bSum = 0;
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
                if (n > 0) { rSum /= n; gSum /= n; bSum /= n; }
                row[x] = new Rgb24(
                    StretchByte(rSum, lo, range),
                    StretchByte(gSum, lo, range),
                    StretchByte(bSum, lo, range)
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
    private static (float r, float g, float b) Demosaic(
        float[] px, int w, int h, int x, int y, string pattern)
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

        float center = px[y * w + x];
        float n  = px[Clamp(y - 1, h) * w + x];
        float s  = px[Clamp(y + 1, h) * w + x];
        float e  = px[y * w + Clamp(x + 1, w)];
        float wv = px[y * w + Clamp(x - 1, w)];
        float ne = px[Clamp(y - 1, h) * w + Clamp(x + 1, w)];
        float nw = px[Clamp(y - 1, h) * w + Clamp(x - 1, w)];
        float se = px[Clamp(y + 1, h) * w + Clamp(x + 1, w)];
        float sw = px[Clamp(y + 1, h) * w + Clamp(x - 1, w)];

        switch (role)
        {
            case 'R':
                return (center, (n + s + e + wv) * 0.25f, (ne + nw + se + sw) * 0.25f);
            case 'B':
                return ((ne + nw + se + sw) * 0.25f, (n + s + e + wv) * 0.25f, center);
            default: // 'g'
                if (gOnRedRow)
                    return ((e + wv) * 0.5f, center, (n + s) * 0.5f);
                else
                    return ((n + s) * 0.5f, center, (e + wv) * 0.5f);
        }
    }

    private static int Clamp(int v, int max)
    {
        if (v < 0) return 0;
        if (v >= max) return max - 1;
        return v;
    }

    private static byte StretchByte(float v, float lo, float range)
    {
        var n = (v - lo) / range;
        if (n < 0) n = 0; else if (n > 1) n = 1;
        return (byte)(n * 255f);
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
