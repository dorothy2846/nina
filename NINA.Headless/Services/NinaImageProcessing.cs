namespace NINA.Headless.Services;

/// <summary>
/// Port of NINA.Image's MTF auto-stretch — the same midtones-transfer
/// function + median/MAD-driven stretch map the desktop NINA app uses.
///
/// Why we re-host it here: NINA.Image's public stretch entry points
/// (`ImageUtility.Stretch`, `RenderedImage.Stretch`) work on
/// `BitmapSource`, a WPF type that's a no-op stub on Linux/macOS. We
/// can't run the WPF-bound pipeline server-side without dragging in
/// WPF, so we lift the *pure-math* part — `MidtonesTransferFunction` +
/// `GetStretchMap` + the median-MAD computation — and apply it to a
/// raw ushort buffer directly. Output matches NINA's stretch output
/// for the same pixels.
///
/// Source (algorithm-equivalent, copied for cross-platform use):
///   NINA.Image/ImageAnalysis/ImageUtility.cs  (MidtonesTransferFunction, GetStretchMap)
///   NINA.Image/ImageData/ImageStatistics.cs   (median + MAD via histogram)
/// </summary>
public static class NinaImageProcessing
{
    /// Default stretch parameters used by NINA's Auto-Stretch button.
    public const double DefaultTargetMedian = 0.25;
    public const double DefaultShadowsClipping = -2.8;

    public readonly struct Stats
    {
        public readonly double Median;
        public readonly double MedianAbsoluteDeviation;
        public readonly int BitDepth;
        public Stats(double median, double mad, int bitDepth)
        {
            Median = median; MedianAbsoluteDeviation = mad; BitDepth = bitDepth;
        }
    }

    /// Compute median + median-absolute-deviation via histogram, matching
    /// NINA.Image.ImageStatistics. O(N) over pixels + O(2^16) over the
    /// histogram bins.
    public static Stats ComputeStats(ushort[] pixels, int bitDepth)
    {
        var counts = new int[ushort.MaxValue + 1];
        for (int i = 0; i < pixels.Length; i++) counts[pixels[i]]++;

        long medianLength = pixels.Length / 2;
        long occurrences = 0;
        int median1 = 0, median2 = 0;
        for (int i = 0; i <= ushort.MaxValue; i++)
        {
            occurrences += counts[i];
            if (occurrences > medianLength)
            {
                median1 = i; median2 = i; break;
            }
            else if (occurrences == medianLength)
            {
                median1 = i;
                for (int j = i + 1; j <= ushort.MaxValue; j++)
                {
                    if (counts[j] > 0) { median2 = j; break; }
                }
                break;
            }
        }
        double median = (median1 + median2) / 2.0;

        // MAD via histogram, walking outward from the median bin.
        double mad = 0;
        occurrences = 0;
        int idxDown = median1, idxUp = median2;
        while (true)
        {
            if (idxDown >= 0 && idxDown != idxUp)
                occurrences += counts[idxDown] + counts[idxUp];
            else
                occurrences += counts[idxUp];

            if (occurrences > medianLength)
            {
                mad = Math.Abs(idxUp - median);
                break;
            }
            idxUp++;
            idxDown--;
            if (idxUp > ushort.MaxValue) break;
        }

        return new Stats(median, mad, bitDepth);
    }

    /// MTF — adjusts x for a given midtone balance. Identical to NINA's
    /// `ImageUtility.MidtonesTransferFunction`.
    public static double MidtonesTransfer(double midToneBalance, double x)
    {
        if (x > 0)
        {
            if (x < 1)
                return (midToneBalance - 1) * x / ((2 * midToneBalance - 1) * x - midToneBalance);
            return 1;
        }
        return 0;
    }

    private static double Normalize(double val, int bitDepth) => val / (double)((1L << bitDepth) - 1);
    private static ushort Denormalize(double val) => (ushort)(val * ushort.MaxValue + (val < 0.5 ? 0.5 : 0.0));

    /// Build the [0..65535] → [0..65535] LUT NINA uses for auto-stretch.
    /// Identical to `ImageUtility.GetStretchMap`. Apply by indexing:
    /// `stretchedValue = map[rawPixel]`.
    public static ushort[] BuildStretchMap(Stats stats, double targetHistogramMedianPercent = DefaultTargetMedian, double shadowsClipping = DefaultShadowsClipping)
    {
        var map = new ushort[ushort.MaxValue + 1];
        var normalizedMedian = Normalize(stats.Median, stats.BitDepth);
        var normalizedMAD = Normalize(stats.MedianAbsoluteDeviation, stats.BitDepth);

        // Degenerate-input guard. When MAD is essentially zero — the entire
        // channel is saturated, blank, or otherwise uniform — the MTF math
        // collapses to a 0-or-1 step function (every pixel snaps to black
        // or white). That produces the "looks like a single colour band"
        // output the user sees on overexposed daylight shots. Short-circuit
        // with an identity LUT so the user gets a uniform-but-honest frame
        // instead of a math artefact.
        if (normalizedMAD < 1e-6)
        {
            for (int i = 0; i < map.Length; i++) map[i] = (ushort)i;
            return map;
        }

        const double scaleFactor = 1.4826; // MAD → σ scale assuming normal noise.

        double shadows, midtones, highlights;
        if (normalizedMedian > 0.5)
        {
            // Inverted / overexposed branch.
            shadows = 0.0;
            highlights = normalizedMedian - shadowsClipping * normalizedMAD * scaleFactor;
            midtones = MidtonesTransfer(targetHistogramMedianPercent, 1.0 - (highlights - normalizedMedian));
        }
        else
        {
            shadows = normalizedMedian + shadowsClipping * normalizedMAD * scaleFactor;
            midtones = MidtonesTransfer(targetHistogramMedianPercent, normalizedMedian - shadows);
            highlights = 1.0;
        }

        for (int i = 0; i < map.Length; i++)
        {
            double value = Normalize(i, stats.BitDepth);
            map[i] = Denormalize(MidtonesTransfer(midtones, 1 - highlights + value - shadows));
        }
        return map;
    }
}
