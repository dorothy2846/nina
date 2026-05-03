namespace NINA.Headless.Services;

/// <summary>Translation-only alignment of two star lists via brightness-anchored voting
/// on a 1-px histogram bucket (K² pairs, K=20). Tracks mount drift up to ~50 px; rotation
/// is out of scope.</summary>
public static class StarMatcher
{
    public record Match(double Dx, double Dy, int Votes, int StarsConsidered);

    /// <summary>Find the translation (dx, dy) that best aligns `current` onto `reference`.
    /// Returns null if no consistent translation was found (not enough matching stars, or
    /// the field has completely changed between frames).</summary>
    /// <param name="brightestK">Number of brightest stars from each list to consider. Too
    /// small = unstable; too large = pair count grows K² and noise-star pairs dominate.
    /// 20 is a good middle ground for typical 50–200-star frames.</param>
    /// <param name="searchRadius">Maximum translation to consider in pixels. Anything beyond
    /// this is discarded as a noise match. For tracked mounts, 50 px covers an entire night's
    /// drift on any reasonable setup.</param>
    /// <param name="minVotes">Minimum votes to accept the match. Fewer = too few stars in common.</param>
    public static Match? FindTranslation(
        IReadOnlyList<StarDetector.Star> reference,
        IReadOnlyList<StarDetector.Star> current,
        int brightestK = 20,
        double searchRadius = 50,
        int minVotes = 4)
    {
        if (reference.Count < minVotes || current.Count < minVotes) return null;

        var refTop = reference.Take(brightestK).ToArray();
        var curTop = current.Take(brightestK).ToArray();

        // Histogram of candidate (dx, dy) offsets, bucketed to 1-px. The mode wins.
        var votes = new Dictionary<(int, int), List<(double, double)>>();

        foreach (var r in refTop)
        {
            foreach (var c in curTop)
            {
                var dx = r.X - c.X;
                var dy = r.Y - c.Y;
                if (Math.Abs(dx) > searchRadius || Math.Abs(dy) > searchRadius) continue;
                var bucket = ((int)Math.Round(dx), (int)Math.Round(dy));
                if (!votes.TryGetValue(bucket, out var list))
                {
                    list = new List<(double, double)>();
                    votes[bucket] = list;
                }
                list.Add((dx, dy));
            }
        }

        if (votes.Count == 0) return null;

        // Winning bucket. For robustness we also count votes in the 8 neighbor buckets — if the
        // true offset lands exactly on a bucket boundary, two adjacent buckets share the votes
        // and neither has enough on its own.
        (int bx, int by) bestKey = (0, 0);
        int bestScore = 0;
        foreach (var key in votes.Keys)
        {
            int score = 0;
            for (int oy = -1; oy <= 1; oy++)
            {
                for (int ox = -1; ox <= 1; ox++)
                {
                    if (votes.TryGetValue((key.Item1 + ox, key.Item2 + oy), out var list))
                        score += list.Count;
                }
            }
            if (score > bestScore) { bestScore = score; bestKey = key; }
        }

        if (bestScore < minVotes) return null;

        // Refine to sub-pixel: mean of all pair offsets in the 3×3 neighborhood of the winner.
        double sumX = 0, sumY = 0; int n = 0;
        for (int oy = -1; oy <= 1; oy++)
        {
            for (int ox = -1; ox <= 1; ox++)
            {
                if (!votes.TryGetValue((bestKey.bx + ox, bestKey.by + oy), out var list)) continue;
                foreach (var (dx, dy) in list) { sumX += dx; sumY += dy; n++; }
            }
        }

        return new Match(sumX / n, sumY / n, bestScore, Math.Min(refTop.Length, curTop.Length));
    }
}
