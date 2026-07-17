using System;
using System.Collections.Generic;
using System.Linq;
using NINA.Core.Utility; // Ensure MathHelper lives here or we implement our own HCos/HArcsin

namespace NINA.Headless.Services;

/// <summary>
/// A pure, UI-agnostic mathematical model for V-Curve Autofocus calculations.
/// Adapted from NINA.WPF.Base to run purely in headless Linux environments without OxyPlot.
/// </summary>
public static class AutofocusMath
{
    public struct FocuserPoint
    {
        public double Position { get; set; }
        public double Hfd { get; set; }
        public bool IsOutlier { get; set; }
        public double Weight { get; set; } // Optional: 1.0 = normal, 0.0 = rejected
        
        public FocuserPoint(double position, double hfd, bool isOutlier = false, double weight = 1.0)
        {
            Position = position;
            Hfd = hfd;
            IsOutlier = isOutlier;
            Weight = weight;
        }
    }

    public class HyperbolicFitResult
    {
        public bool Success { get; set; }
        public double A { get; set; }
        public double B { get; set; }
        public double P { get; set; } // The perfect focus position (X axis)
        public double MinimumHfd { get; set; } // The HFD at perfect focus position (Y axis)
    }

    /// <summary>Median half-flux RADIUS (pixels) across detected stars — the
    /// real measurement the V-curve is built from. Per star: local background
    /// from the box border median, flux-weighted centroid, then the radius
    /// containing half the background-subtracted flux. Median across stars
    /// rejects hot pixels / tight doubles without needing per-star filtering.</summary>
    public static double? MeasureMedianHfr(float[] px, int width, int height, IReadOnlyList<StarDetector.Star> stars)
    {
        const int box = 15;
        var hfrs = new List<double>();

        foreach (var s in stars)
        {
            int cx = (int)Math.Round(s.X), cy = (int)Math.Round(s.Y);
            if (cx < box || cy < box || cx >= width - box || cy >= height - box) continue;

            // Local background: median of the box border ring.
            var border = new List<float>(8 * box + 4);
            for (int dx = -box; dx <= box; dx++)
            {
                border.Add(px[(cy - box) * width + cx + dx]);
                border.Add(px[(cy + box) * width + cx + dx]);
            }
            for (int dy = -box + 1; dy < box; dy++)
            {
                border.Add(px[(cy + dy) * width + cx - box]);
                border.Add(px[(cy + dy) * width + cx + box]);
            }
            border.Sort();
            float bg = border[border.Count / 2];

            // Flux-weighted centroid.
            double flux = 0, sumX = 0, sumY = 0;
            for (int dy = -box; dy <= box; dy++)
            for (int dx = -box; dx <= box; dx++)
            {
                var v = px[(cy + dy) * width + (cx + dx)] - bg;
                if (v <= 0) continue;
                flux += v;
                sumX += v * (cx + dx);
                sumY += v * (cy + dy);
            }
            if (flux <= 0) continue;
            double mx = sumX / flux, my = sumY / flux;

            // Radius that contains half the flux.
            var samples = new List<(double R, double V)>();
            for (int dy = -box; dy <= box; dy++)
            for (int dx = -box; dx <= box; dx++)
            {
                var v = px[(cy + dy) * width + (cx + dx)] - bg;
                if (v <= 0) continue;
                var r = Math.Sqrt((cx + dx - mx) * (cx + dx - mx) + (cy + dy - my) * (cy + dy - my));
                samples.Add((r, v));
            }
            samples.Sort((a, b) => a.R.CompareTo(b.R));
            double cum = 0, half = flux / 2.0, hfr = box;
            foreach (var (r, v) in samples)
            {
                cum += v;
                if (cum >= half) { hfr = r; break; }
            }
            hfrs.Add(hfr);
        }

        if (hfrs.Count == 0) return null;
        hfrs.Sort();
        return hfrs[hfrs.Count / 2];
    }

    /// <summary>
    /// Implements RANSAC-like or IQR-based outlier rejection to filter bad atmospheric HFD points.
    /// </summary>
    public static List<FocuserPoint> FilterOutliers(List<FocuserPoint> rawPoints)
    {
        if (rawPoints.Count < 4) return rawPoints; // Not enough points to statisically reject
        
        // Simple IQR based outlier rejection for V-curve: 
        // A single point that spikes massively compared to its neighbors is an outlier.
        var points = rawPoints.OrderBy(p => p.Position).ToList();
        
        for (int i = 1; i < points.Count - 1; i++)
        {
            double prevY = points[i-1].Hfd;
            double nextY = points[i+1].Hfd;
            double expectedY = (prevY + nextY) / 2.0;

            // If the HFD is completely disconnected from the neighborhood slope (e.g. cloud passed)
            if (points[i].Hfd > expectedY * 1.5) 
            {
                var p = points[i];
                p.IsOutlier = true;
                points[i] = p;
            }
        }
        
        return points;
    }

    /// <summary>
    /// Fits a hyperbola to the data points to find the theoretical minimum HFD (Perfect Focus).
    /// Hyperbola equation: y = a * cosh(asinh((p - x) / b))
    /// </summary>
    public static HyperbolicFitResult CalculateHyperbolicFit(List<FocuserPoint> allPoints)
    {
        var validPoints = allPoints.Where(dp => !dp.IsOutlier && dp.Hfd >= 0.1).ToList();
        
        if (validPoints.Count < 3) 
        {
            return new HyperbolicFitResult { Success = false };
        }

        // Iterative Brute-Force Convergence (Ripped out of OxyPlot/INPC)
        double lowestError = double.MaxValue;
        
        var lowestPoint = validPoints.OrderBy(p => p.Hfd).First();
        var highestPoint = validPoints.OrderByDescending(p => p.Hfd).First();

        double highestPosition = highestPoint.Position;
        double highestHfr = highestPoint.Hfd;
        double lowestPosition = lowestPoint.Position;
        double lowestHfr = lowestPoint.Hfd;

        if (highestPosition < lowestPosition) 
        {
            highestPosition = 2 * lowestPosition - highestPosition; 
        }

        double a = lowestHfr;
        double b = Math.Sqrt((highestPosition - lowestPosition) * (highestPosition - lowestPosition) * a * a / Math.Max(0.0001, (highestHfr * highestHfr - a * a)));
        double p = lowestPosition;

        double aRange = a;
        double bRange = b;
        double pRange = Math.Abs(highestPosition - lowestPosition);

        if (double.IsNaN(aRange) || double.IsNaN(bRange) || aRange <= 0 || bRange <= 0 || pRange <= 0) 
        {
            return new HyperbolicFitResult { Success = false };
        }

        int iterationCycles = 0;
        double oldError;

        do 
        {
            double p0 = p;
            double b0 = b;
            double a0 = a;

            aRange *= 0.5;
            bRange *= 0.5;
            pRange *= 0.5;

            double p1 = p0 - pRange;

            while (p1 <= p0 + pRange) 
            {
                double a1 = a0 - aRange;
                while (a1 <= a0 + aRange) 
                {
                    double b1 = b0 - bRange;
                    while (b1 <= b0 + bRange) 
                    {
                        double error1 = CalculateHyperbolicError(validPoints, p1, a1, b1);
                        if (error1 < lowestError) 
                        {
                            oldError = lowestError;
                            lowestError = error1;
                            a = a1;
                            b = b1;
                            p = p1;
                        }
                        b1 += bRange * 0.1;
                    }
                    a1 += aRange * 0.1;
                }
                p1 += pRange * 0.1;
            }
            iterationCycles++;
        } while (lowestError > 0.0001 && iterationCycles < 30);

        return new HyperbolicFitResult 
        {
            Success = true,
            A = a,
            B = b,
            P = Math.Round(p),
            MinimumHfd = a
        };
    }

    private static double CalculateHyperbolicError(List<FocuserPoint> points, double p, double a, double b)
    {
        double sumSq = 0;
        foreach (var dp in points)
        {
            double x = p - dp.Position;
            double t = MathHelper.HArcsin(x / b);
            double expectedHfd = a * MathHelper.HCos(t);
            sumSq += Math.Pow(expectedHfd - dp.Hfd, 2);
        }
        return Math.Sqrt(sumSq);
    }
}
