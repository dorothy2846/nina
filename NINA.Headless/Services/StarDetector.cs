namespace NINA.Headless.Services;

/// <summary>Fast star detector for live-stacking: background/MAD noise estimate →
/// threshold → 3×3 local max → 5×5 intensity-weighted centroid. Translation-grade
/// precision (~0.2 px), not a source extractor.</summary>
public static class StarDetector
{
    public record Star(double X, double Y, float Peak, float Flux);

    public static List<Star> Detect(float[] pixels, int width, int height, int maxStars = 200, float sigmaThreshold = 5.0f, int edgeMargin = 8)
    {
        if (pixels.Length != width * height) throw new ArgumentException("pixel buffer size mismatch");

        var (bg, sigma) = EstimateBackgroundNoise(pixels);
        var threshold = bg + sigmaThreshold * sigma;

        // First pass: collect candidate local maxima. A candidate is any pixel above threshold
        // AND strictly greater than all 8 neighbors. A ≥-comparison would double-count flat-topped
        // saturated stars; > means we lose one pixel of a plateau but gain uniqueness.
        var candidates = new List<Star>(1024);
        int stride = width;
        for (int y = edgeMargin; y < height - edgeMargin; y++)
        {
            int row = y * stride;
            for (int x = edgeMargin; x < width - edgeMargin; x++)
            {
                var v = pixels[row + x];
                if (v <= threshold) continue;

                // 3×3 local-max check
                if (pixels[row + x - 1] >= v) continue;
                if (pixels[row + x + 1] >= v) continue;
                if (pixels[row + x - stride] >= v) continue;
                if (pixels[row + x + stride] >= v) continue;
                if (pixels[row + x - 1 - stride] >= v) continue;
                if (pixels[row + x + 1 - stride] >= v) continue;
                if (pixels[row + x - 1 + stride] >= v) continue;
                if (pixels[row + x + 1 + stride] >= v) continue;

                // 5×5 intensity-weighted centroid refinement. Summing after subtracting background
                // avoids the centroid being pulled toward the brighter edge of a PSF when the noise
                // floor is uneven.
                double sx = 0, sy = 0, sw = 0, flux = 0;
                for (int dy = -2; dy <= 2; dy++)
                {
                    int r = (y + dy) * stride;
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        var p = pixels[r + x + dx] - bg;
                        if (p <= 0) continue;
                        sx += p * (x + dx);
                        sy += p * (y + dy);
                        sw += p;
                        flux += p;
                    }
                }
                if (sw <= 0) continue;
                var cx = sx / sw;
                var cy = sy / sw;

                candidates.Add(new Star(cx, cy, v, (float)flux));
            }
        }

        // Deduplicate: two candidates within 3 px of each other are the same star (happens on
        // PSFs with a shoulder that narrowly passes the > check). Keep the brighter.
        candidates.Sort((a, b) => b.Peak.CompareTo(a.Peak));
        var deduped = new List<Star>(candidates.Count);
        const double dedupeRadiusSq = 9.0; // 3px
        foreach (var s in candidates)
        {
            bool keep = true;
            foreach (var existing in deduped)
            {
                var ddx = s.X - existing.X;
                var ddy = s.Y - existing.Y;
                if (ddx * ddx + ddy * ddy < dedupeRadiusSq) { keep = false; break; }
            }
            if (keep)
            {
                deduped.Add(s);
                if (deduped.Count >= maxStars) break;
            }
        }

        return deduped;
    }

    /// <summary>Sample-based background + noise estimation. Sampling every Nth pixel avoids
    /// an O(W·H·log(W·H)) sort on the full image — for live stacking we need this to be fast.
    /// MAD (median absolute deviation) is star-robust where naïve std-dev would be inflated.</summary>
    private static (float background, float sigma) EstimateBackgroundNoise(float[] pixels)
    {
        const int targetSamples = 10_000;
        var step = Math.Max(1, pixels.Length / targetSamples);
        var sample = new float[pixels.Length / step];
        for (int i = 0, j = 0; i < pixels.Length && j < sample.Length; i += step, j++)
            sample[j] = pixels[i];

        Array.Sort(sample);
        var median = sample[sample.Length / 2];

        // MAD → σ via 1.4826 scale factor (assuming Gaussian noise).
        var dev = new float[sample.Length];
        for (int i = 0; i < sample.Length; i++) dev[i] = Math.Abs(sample[i] - median);
        Array.Sort(dev);
        var mad = dev[dev.Length / 2];
        var sigma = mad * 1.4826f;
        if (sigma < 1f) sigma = 1f; // guard against all-zero frames
        return (median, sigma);
    }
}
