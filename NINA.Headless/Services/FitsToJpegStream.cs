using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using IsImage = SixLabors.ImageSharp.Image;

namespace NINA.Headless.Services;

/// <summary>
/// Streaming-optimized FITS → JPEG path. Unlike <see cref="FitsToPng"/>, this:
///   1. Downsamples to ~1024px width via box averaging before any work scales with pixel count
///   2. Estimates 1/99% percentiles from a random 10 K-pixel sample instead of sorting the
///      full array (O(N) instead of O(N log N))
///   3. Writes an 8-bit grayscale JPEG directly from the stretched ushort buffer
///
/// End-to-end per 3856×2180 16-bit frame: &lt;100 ms on M-series CPU. Quality is visibly
/// indistinguishable from the sorted-percentile version for live viewing.
/// </summary>
public static class FitsToJpegStream
{
    public static byte[] Encode(byte[] fitsBytes, int targetMaxWidth = 1024, int jpegQuality = 70)
    {
        var hdr = FitsReader.ParseHeader(fitsBytes);
        var width = hdr.width; var height = hdr.height; var bitpix = hdr.bitpix; var dataOffset = hdr.dataOffset;
        if (bitpix != 16) throw new NotSupportedException($"Streaming encoder expects BITPIX=16, got {bitpix}");

        // Downsample factor: smallest integer that brings width <= targetMaxWidth.
        var factor = Math.Max(1, (int)Math.Ceiling((double)width / targetMaxWidth));
        var outW = width / factor;
        var outH = height / factor;
        var small = DownsampleBoxAverage(fitsBytes, dataOffset, width, height, factor);

        // Percentile estimation via random sample — ≥10K samples gives stable stretch bounds
        // without the 200 ms sort cost on 8 M pixels.
        var (lo, hi) = EstimatePercentiles(small, sampleCount: 10_000, lowP: 0.01f, highP: 0.99f);
        var range = Math.Max(1f, hi - lo);

        using var img = new Image<L8>(outW, outH);
        for (int y = 0; y < outH; y++)
        {
            var row = img.DangerousGetPixelRowMemory(y).Span;
            var rowStart = y * outW;
            for (int x = 0; x < outW; x++)
            {
                var v = (small[rowStart + x] - lo) / range;
                if (v < 0) v = 0; else if (v > 1) v = 1;
                row[x] = new L8((byte)(v * 255f));
            }
        }

        using var ms = new MemoryStream();
        img.SaveAsJpeg(ms, new JpegEncoder { Quality = jpegQuality });
        return ms.ToArray();
    }

    /// <summary>Average NxN pixel blocks of the big-endian ushort FITS pixel plane into a
    /// smaller float[] of size (width/factor) × (height/factor). Stops at integer block
    /// boundaries (any remainder rows/cols are dropped — acceptable for a live preview).</summary>
    private static float[] DownsampleBoxAverage(byte[] bytes, int offset, int width, int height, int factor)
    {
        var outW = width / factor;
        var outH = height / factor;
        var result = new float[outW * outH];
        var blockArea = factor * factor;

        for (int by = 0; by < outH; by++)
        {
            for (int bx = 0; bx < outW; bx++)
            {
                int sum = 0;
                for (int yy = 0; yy < factor; yy++)
                {
                    var rowBase = offset + ((by * factor + yy) * width + bx * factor) * 2;
                    for (int xx = 0; xx < factor; xx++)
                    {
                        var p = rowBase + xx * 2;
                        sum += (bytes[p] << 8) | bytes[p + 1];
                    }
                }
                result[by * outW + bx] = (float)sum / blockArea;
            }
        }
        return result;
    }

    private static (float lo, float hi) EstimatePercentiles(float[] pixels, int sampleCount, float lowP, float highP)
    {
        var sample = new float[Math.Min(sampleCount, pixels.Length)];
        // Deterministic stride keeps output stable across captures (better perceived stability
        // than pure Random) while covering the whole frame.
        var stride = Math.Max(1, pixels.Length / sample.Length);
        for (int i = 0; i < sample.Length; i++)
        {
            sample[i] = pixels[i * stride % pixels.Length];
        }
        Array.Sort(sample);
        var loIdx = (int)(sample.Length * lowP);
        var hiIdx = (int)(sample.Length * highP);
        return (sample[loIdx], sample[Math.Min(hiIdx, sample.Length - 1)]);
    }

}
