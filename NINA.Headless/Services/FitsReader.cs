namespace NINA.Headless.Services;

/// <summary>
/// Shared FITS header parser + pixel loader for 2D monochrome frames with BITPIX ∈ {8, 16, -32}.
/// Not a general FITS reader (no WCS, no extensions, no debayering) — just enough for the
/// PNG encoder, the calibration pipeline, and the preview JPEG stream to share one parse.
/// </summary>
public static class FitsReader
{
    public static (float[] pixels, int width, int height) Read(byte[] bytes)
    {
        var hdr = ParseHeader(bytes);
        return (ReadPixels(bytes, hdr.dataOffset, hdr.width, hdr.height, hdr.bitpix, hdr.bzero, hdr.bscale), hdr.width, hdr.height);
    }

    public static (int width, int height, int bitpix, int dataOffset, double bzero, double bscale) ParseHeader(byte[] bytes)
    {
        int naxis1 = 0, naxis2 = 0, bitpix = 0;
        double bzero = 0, bscale = 1;
        int offset = 0;
        while (offset < bytes.Length)
        {
            var blockEnd = offset + 2880;
            for (int rec = offset; rec < blockEnd && rec + 80 <= bytes.Length; rec += 80)
            {
                var card = System.Text.Encoding.ASCII.GetString(bytes, rec, 80);
                if (card.StartsWith("END"))
                {
                    if (naxis1 == 0 || naxis2 == 0 || bitpix == 0)
                        throw new InvalidDataException("FITS: missing NAXIS1/NAXIS2/BITPIX");
                    return (naxis1, naxis2, bitpix, blockEnd, bzero, bscale);
                }
                ExtractInt(card, "NAXIS1", ref naxis1);
                ExtractInt(card, "NAXIS2", ref naxis2);
                ExtractInt(card, "BITPIX", ref bitpix);
                ExtractDouble(card, "BZERO", ref bzero);
                ExtractDouble(card, "BSCALE", ref bscale);
            }
            offset = blockEnd;
        }
        throw new InvalidDataException("FITS: END card not found");
    }

    /// <summary>Read pixels honouring BZERO + BSCALE so unsigned-shifted FITS
    /// (BITPIX=16, BZERO=32768 — the standard INDI/PlayerOne/ZWO output) come
    /// out as the ushort the driver actually captured. Without this, a
    /// saturated daylight frame (raw ushort 65000) reads back as 32232 and
    /// every pixel clusters in a tiny range — autostretch then maps the
    /// whole frame to black.</summary>
    public static float[] ReadPixels(byte[] bytes, int dataOffset, int width, int height, int bitpix, double bzero = 0, double bscale = 1)
    {
        var count = width * height;
        var px = new float[count];
        var scale = (float)bscale;
        var zero = (float)bzero;
        switch (bitpix)
        {
            case 8:
                for (int i = 0; i < count; i++) px[i] = bytes[dataOffset + i] * scale + zero;
                break;
            case 16:
                // FITS BITPIX=16 is SIGNED big-endian. Unsigned data is shifted
                // into signed range by BZERO=32768; we undo that here.
                for (int i = 0; i < count; i++)
                {
                    var b0 = bytes[dataOffset + i * 2];
                    var b1 = bytes[dataOffset + i * 2 + 1];
                    short s = (short)((b0 << 8) | b1);
                    px[i] = s * scale + zero;
                }
                break;
            case -32:
                var buf = new byte[4];
                for (int i = 0; i < count; i++)
                {
                    buf[0] = bytes[dataOffset + i * 4 + 3];
                    buf[1] = bytes[dataOffset + i * 4 + 2];
                    buf[2] = bytes[dataOffset + i * 4 + 1];
                    buf[3] = bytes[dataOffset + i * 4 + 0];
                    px[i] = BitConverter.ToSingle(buf, 0) * scale + zero;
                }
                break;
            default:
                throw new NotSupportedException($"FITS BITPIX={bitpix} not supported");
        }
        return px;
    }

    private static void ExtractInt(string card, string key, ref int target)
    {
        var v = ExtractValue(card, key);
        if (v == null) return;
        if (int.TryParse(v, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n))
            target = n;
    }

    private static void ExtractDouble(string card, string key, ref double target)
    {
        var v = ExtractValue(card, key);
        if (v == null) return;
        if (double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
            target = d;
    }

    private static string? ExtractValue(string card, string key)
    {
        if (!card.StartsWith(key)) return null;
        var eq = card.IndexOf('=');
        if (eq < 0) return null;
        var slash = card.IndexOf('/', eq);
        return (slash < 0 ? card[(eq + 1)..] : card[(eq + 1)..slash]).Trim();
    }
}
