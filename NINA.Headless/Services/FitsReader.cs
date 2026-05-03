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
        var (w, h, bitpix, offset) = ParseHeader(bytes);
        return (ReadPixels(bytes, offset, w, h, bitpix), w, h);
    }

    public static (int width, int height, int bitpix, int dataOffset) ParseHeader(byte[] bytes)
    {
        int naxis1 = 0, naxis2 = 0, bitpix = 0;
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
                    return (naxis1, naxis2, bitpix, blockEnd);
                }
                Extract(card, "NAXIS1", ref naxis1);
                Extract(card, "NAXIS2", ref naxis2);
                Extract(card, "BITPIX", ref bitpix);
            }
            offset = blockEnd;
        }
        throw new InvalidDataException("FITS: END card not found");
    }

    public static float[] ReadPixels(byte[] bytes, int dataOffset, int width, int height, int bitpix)
    {
        var count = width * height;
        var px = new float[count];
        switch (bitpix)
        {
            case 8:
                for (int i = 0; i < count; i++) px[i] = bytes[dataOffset + i];
                break;
            case 16:
                // FITS stores big-endian signed 16 per standard; unsigned drivers apply BZERO=32768
                // externally but most raw sensor dumps are effectively unsigned ushort.
                for (int i = 0; i < count; i++)
                {
                    var b0 = bytes[dataOffset + i * 2];
                    var b1 = bytes[dataOffset + i * 2 + 1];
                    px[i] = (ushort)((b0 << 8) | b1);
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
                    px[i] = BitConverter.ToSingle(buf, 0);
                }
                break;
            default:
                throw new NotSupportedException($"FITS BITPIX={bitpix} not supported");
        }
        return px;
    }

    private static void Extract(string card, string key, ref int target)
    {
        if (!card.StartsWith(key)) return;
        var eq = card.IndexOf('=');
        if (eq < 0) return;
        var slash = card.IndexOf('/', eq);
        var valPart = (slash < 0 ? card[(eq + 1)..] : card[(eq + 1)..slash]).Trim();
        if (int.TryParse(valPart, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v))
            target = v;
    }
}
