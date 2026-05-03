using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;

namespace NINA.Headless.Services;

/// <summary>Indexes Dark/Bias/Flat frames by capture settings and lazily produces mean-stacked
/// master frames for calibration. Masters are ephemeral (bounded LRU cache); FITS files on
/// disk are the source of truth and never modified.</summary>
public class CalibrationLibrary
{
    private readonly CaptureStore _captures;
    private readonly ILogger<CalibrationLibrary> _log;

    // Bounded LRU — 9MP master is ~36MB, so unbounded growth across exposure/gain combos
    // would add hundreds of MB per camera.
    private const int MaxCachedMasters = 8;
    private readonly object _mastersLock = new();
    private readonly Dictionary<MatchKey, Master> _masters = new();
    private readonly LinkedList<MatchKey> _mastersLru = new(); // front = most-recently used

    public CalibrationLibrary(CaptureStore captures, ILogger<CalibrationLibrary> log)
    {
        _captures = captures;
        _log = log;
    }

    /// <summary>Dark/Bias are keyed by CameraId; Flat is keyed by PlanId. The "other" id is
    /// always null on any given key, which partitions the library into per-camera reusable
    /// masters and per-plan session masters.</summary>
    public record MatchKey(string ImageType, string? CameraId, string? PlanId, int ExposureMs, int Gain, int Offset, int Binning, string? Filter)
    {
        public static MatchKey ForDark(string? cameraId, double expSec, int gain, int offset, int binning) =>
            new("Dark", cameraId, null, (int)Math.Round(expSec * 1000), gain, offset, binning, null);

        public static MatchKey ForBias(string? cameraId, int gain, int offset, int binning) =>
            new("Bias", cameraId, null, 0, gain, offset, binning, null);

        public static MatchKey ForFlat(string? planId, int gain, int offset, int binning, string? filter) =>
            new("Flat", null, planId, 0, gain, offset, binning, filter);

        public string DisplayLabel()
        {
            return ImageType switch
            {
                "Dark" => $"{FormatExposure(ExposureMs)} · G{Gain} · Bin{Binning}",
                "Bias" => $"G{Gain} · Bin{Binning}",
                "Flat" => string.IsNullOrEmpty(Filter)
                    ? $"G{Gain} · Bin{Binning}"
                    : $"G{Gain} · Bin{Binning} · {Filter}",
                _ => ToString()!
            };
        }

        private static string FormatExposure(int ms) =>
            ms < 1000 ? $"{ms}ms" : $"{ms / 1000.0:0.##}s";
    }

    public record Master(float[] Pixels, int Width, int Height, int FrameCount);

    /// <summary>Enumerate all known calibration groups, each with its frame count and latest
    /// capture time. Used by the Library UI to show what's available.</summary>
    public IReadOnlyList<GroupInfo> EnumerateGroups()
    {
        var entries = _captures.GetAll();
        var groups = new Dictionary<MatchKey, (int Count, DateTime Latest, string? CameraName)>();
        foreach (var e in entries)
        {
            foreach (var key in MakeKeys(e))
            {
                if (groups.TryGetValue(key, out var existing))
                    groups[key] = (existing.Count + 1,
                                   e.Timestamp > existing.Latest ? e.Timestamp : existing.Latest,
                                   existing.CameraName ?? e.CameraName);
                else
                    groups[key] = (1, e.Timestamp, e.CameraName);
            }
        }
        return groups
            .Select(kv => new GroupInfo(kv.Key, kv.Value.Count, kv.Value.Latest, kv.Value.CameraName))
            .OrderBy(g => g.Key.ImageType)
            .ThenByDescending(g => g.Latest)
            .ToList();
    }

    public record GroupInfo(MatchKey Key, int FrameCount, DateTime Latest, string? CameraName);

    /// <summary>Delete every capture belonging to a matching group. Used by the library UI's
    /// trash icon. Returns the count of files deleted.</summary>
    public int DeleteGroup(MatchKey key)
    {
        var entries = _captures.GetAll().Where(e => MakeKeys(e).Contains(key)).ToList();
        int deleted = 0;
        foreach (var e in entries)
        {
            if (_captures.DeleteById(e.Id)) deleted++;
        }
        if (deleted > 0)
        {
            // Deleted entries may have participated in more than one key (shared flats) —
            // invalidate every master they touched.
            foreach (var e in entries)
                foreach (var k in MakeKeys(e))
                    RemoveMaster(k);
        }
        return deleted;
    }

    private void RemoveMaster(MatchKey key)
    {
        lock (_mastersLock)
        {
            _masters.Remove(key);
            _mastersLru.Remove(key);
        }
    }

    /// <summary>Compute (or reuse) the master frame for a key. Returns null if no frames
    /// match or they can't be loaded.</summary>
    public Master? TryGetMaster(MatchKey key)
    {
        lock (_mastersLock)
        {
            if (_masters.TryGetValue(key, out var cached))
            {
                TouchLru_locked(key);
                return cached;
            }
        }

        // Include frames participating in this key via EITHER primary or shared association.
        var entries = _captures.GetAll().Where(e => MakeKeys(e).Contains(key)).ToList();
        if (entries.Count == 0) return null;

        // Streaming mean: read each FITS, accumulate sum[i] += px[i], increment count. First
        // frame pins the expected (W,H) — any frame with mismatched shape is skipped (happens
        // if user changed binning or ROI mid-session).
        int width = 0, height = 0;
        float[]? sum = null;
        int used = 0;

        foreach (var e in entries)
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(e.FitsPath); }
            catch (Exception ex) { _log.LogWarning(ex, "Master build: cannot read {Path}", e.FitsPath); continue; }

            float[] px;
            int w, h;
            try { (px, w, h) = FitsReader.Read(bytes); }
            catch (Exception ex) { _log.LogWarning(ex, "Master build: cannot parse {Path}", e.FitsPath); continue; }

            if (sum == null)
            {
                width = w; height = h;
                sum = new float[px.Length];
            }
            else if (w != width || h != height)
            {
                _log.LogWarning("Master build: frame {Path} has shape {W}x{H}, expected {EW}x{EH} — skipping", e.FitsPath, w, h, width, height);
                continue;
            }

            for (int i = 0; i < px.Length; i++) sum[i] += px[i];
            used++;
        }

        if (sum == null || used == 0) return null;

        // Divide-by-N in place to produce the mean.
        var inv = 1f / used;
        for (int i = 0; i < sum.Length; i++) sum[i] *= inv;

        var master = new Master(sum, width, height, used);
        lock (_mastersLock)
        {
            _masters[key] = master;
            TouchLru_locked(key);
            while (_masters.Count > MaxCachedMasters)
            {
                var victim = _mastersLru.Last;
                if (victim == null) break;
                _mastersLru.RemoveLast();
                _masters.Remove(victim.Value);
            }
        }
        _log.LogInformation("Master {Type} built: {Count} frames, {W}×{H}", key.ImageType, used, width, height);
        return master;
    }

    private void TouchLru_locked(MatchKey key)
    {
        _mastersLru.Remove(key);
        _mastersLru.AddFirst(key);
    }

    /// <summary>Apply calibration to a light frame. Returns calibrated pixel data plus
    /// autostretched PNG bytes. Null if no matching calibration masters exist.
    ///
    /// Formula:
    ///   flat_norm = (master_flat - master_bias) / mean(master_flat - master_bias)
    ///   calibrated = (light - master_dark) / flat_norm
    ///
    /// Gracefully degrades: missing dark → skip subtraction; missing flat → skip division.
    /// If both are missing, returns null (no point producing an unchanged copy).</summary>
    /// Inputs that identify a Light frame — the matching keys Dark/Bias/Flat masters are keyed
    /// against plus the frame bytes themselves. Groups the 7 parameters both calibration entry
    /// points used to take positionally.
    public record CalibrationInputs(byte[] LightFitsBytes, string? CameraId, string? PlanId, double ExposureSeconds, int Gain, int Offset, int Binning, string? Filter);

    public CalibrationResult? TryCalibrate(CalibrationInputs inputs)
    {
        var pixels = TryCalibratePixels(inputs);
        if (pixels == null) return null;
        var png = EncodeAutostretchPng(pixels.Pixels, pixels.Width, pixels.Height);
        return new CalibrationResult(
            Png: png,
            DarkApplied: pixels.DarkApplied,
            FlatApplied: pixels.FlatApplied,
            BiasApplied: pixels.BiasApplied,
            DarkFrames: pixels.DarkFrames,
            FlatFrames: pixels.FlatFrames,
            BiasFrames: pixels.BiasFrames);
    }

    /// <summary>Apply matching masters and return the calibrated pixel array. Same pipeline
    /// as <see cref="TryCalibrate"/> but skips the PNG encode so callers doing further
    /// processing (live stacking, plate solving, HFR analysis) can share the expensive part.
    ///
    /// When no masters match at all, falls through with `.DarkApplied/FlatApplied/BiasApplied
    /// = false` and the unmodified pixels. The caller decides whether that's useful (stacking
    /// says yes — uncalibrated frames still stack fine; the calibrated-PNG endpoint says no
    /// because there's nothing to show that's different from the raw thumbnail).</summary>
    public PixelResult? TryCalibratePixels(CalibrationInputs inputs, bool returnRawIfNoMasters = false)
    {
        float[] light;
        int width, height;
        try { (light, width, height) = FitsReader.Read(inputs.LightFitsBytes); }
        catch (Exception ex) { _log.LogWarning(ex, "Calibrate: cannot parse light FITS"); return null; }

        // Dark/Bias are keyed to the camera; Flat is keyed to the plan. If either id is
        // unknown (no camera selected, or no plan active), that category of master is
        // skipped rather than mis-matched.
        var darkMaster = string.IsNullOrEmpty(inputs.CameraId) ? null : TryGetMaster(MatchKey.ForDark(inputs.CameraId, inputs.ExposureSeconds, inputs.Gain, inputs.Offset, inputs.Binning));
        var biasMaster = string.IsNullOrEmpty(inputs.CameraId) ? null : TryGetMaster(MatchKey.ForBias(inputs.CameraId, inputs.Gain, inputs.Offset, inputs.Binning));
        var flatMaster = string.IsNullOrEmpty(inputs.PlanId) ? null : TryGetMaster(MatchKey.ForFlat(inputs.PlanId, inputs.Gain, inputs.Offset, inputs.Binning, inputs.Filter));

        // Shape mismatches mean the master was built at a different binning/ROI — can't apply.
        if (darkMaster != null && (darkMaster.Width != width || darkMaster.Height != height)) darkMaster = null;
        if (flatMaster != null && (flatMaster.Width != width || flatMaster.Height != height)) flatMaster = null;
        if (biasMaster != null && (biasMaster.Width != width || biasMaster.Height != height)) biasMaster = null;

        if (darkMaster == null && flatMaster == null)
        {
            if (!returnRawIfNoMasters) return null;
            return new PixelResult(light, width, height, false, false, false, 0, 0, 0);
        }

        // Build flat_norm if a flat is available. Subtract bias from flat (if bias available),
        // then divide by the mean so the average pixel is 1.0 — applying this to the light
        // changes only the relative illumination, not the overall brightness.
        float[]? flatNorm = null;
        if (flatMaster != null)
        {
            flatNorm = new float[flatMaster.Pixels.Length];
            double sum = 0;
            for (int i = 0; i < flatMaster.Pixels.Length; i++)
            {
                var v = flatMaster.Pixels[i] - (biasMaster?.Pixels[i] ?? 0);
                flatNorm[i] = v;
                sum += v;
            }
            var mean = (float)(sum / flatMaster.Pixels.Length);
            if (mean > 0.0001f)
            {
                var invMean = 1f / mean;
                for (int i = 0; i < flatNorm.Length; i++) flatNorm[i] *= invMean;
            }
            else
            {
                flatNorm = null;
            }
        }

        var calibrated = new float[light.Length];
        for (int i = 0; i < light.Length; i++)
        {
            var v = light[i];
            if (darkMaster != null) v -= darkMaster.Pixels[i];
            if (flatNorm != null)
            {
                var f = flatNorm[i];
                if (f > 0.01f) v /= f;
            }
            calibrated[i] = v;
        }

        return new PixelResult(
            Pixels: calibrated,
            Width: width, Height: height,
            DarkApplied: darkMaster != null,
            FlatApplied: flatNorm != null,
            BiasApplied: biasMaster != null,
            DarkFrames: darkMaster?.FrameCount ?? 0,
            FlatFrames: flatMaster?.FrameCount ?? 0,
            BiasFrames: biasMaster?.FrameCount ?? 0);
    }

    public record PixelResult(float[] Pixels, int Width, int Height,
        bool DarkApplied, bool FlatApplied, bool BiasApplied,
        int DarkFrames, int FlatFrames, int BiasFrames);

    public record CalibrationResult(byte[] Png, bool DarkApplied, bool FlatApplied, bool BiasApplied,
        int DarkFrames, int FlatFrames, int BiasFrames);

    /// <summary>Tell the library that a new capture arrived. If it's a calibration frame,
    /// invalidate its group's cached master so the next request rebuilds with the new data.</summary>
    public void NotifyCaptureAdded(CaptureEntry entry)
    {
        // Flats with SharedWithPlanIds invalidate every participating plan's cached master,
        // not just the primary.
        foreach (var key in MakeKeys(entry))
            RemoveMaster(key);
    }

    // MARK: Helpers

    /// Enumerate every MatchKey this entry participates in. Dark/Bias always have exactly
    /// one key; Flats can have multiple when SharedWithPlanIds is populated — the single
    /// physical frame then "appears" under each shared plan's Flat folder + contributes to
    /// each shared plan's master.
    private static IEnumerable<MatchKey> MakeKeys(CaptureEntry e)
    {
        // Skip partially-indexed entries (loaded from disk without captured metadata).
        if (e.Gain == 0 && e.ExposureSeconds == 0 && e.Binning == 0) yield break;

        switch (e.ImageType)
        {
            case "Dark":
                if (!string.IsNullOrEmpty(e.CameraId))
                    yield return MatchKey.ForDark(e.CameraId, e.ExposureSeconds, e.Gain, e.Offset, e.Binning);
                break;
            case "Bias":
                if (!string.IsNullOrEmpty(e.CameraId))
                    yield return MatchKey.ForBias(e.CameraId, e.Gain, e.Offset, e.Binning);
                break;
            case "Flat":
                if (string.IsNullOrEmpty(e.PlanId)) yield break;
                yield return MatchKey.ForFlat(e.PlanId, e.Gain, e.Offset, e.Binning, e.Filter);
                if (e.SharedWithPlanIds != null)
                {
                    foreach (var pid in e.SharedWithPlanIds)
                        if (!string.IsNullOrEmpty(pid) && pid != e.PlanId)
                            yield return MatchKey.ForFlat(pid, e.Gain, e.Offset, e.Binning, e.Filter);
                }
                break;
        }
    }


    /// <summary>Linear autostretch to 1%/99% percentile then write 8-bit grayscale PNG.
    /// Percentiles are estimated from a ~10k-pixel stride sample rather than a full sort
    /// (9 MP clone + Sort = ~200 ms; sampled version is ~2 ms and visually indistinguishable).</summary>
    public static byte[] EncodeAutostretchPng(float[] px, int width, int height)
    {
        const int targetSamples = 10_000;
        var step = Math.Max(1, px.Length / targetSamples);
        var sample = new float[(px.Length + step - 1) / step];
        for (int i = 0, j = 0; i < px.Length && j < sample.Length; i += step, j++)
            sample[j] = px[i];
        Array.Sort(sample);
        var lo = sample[(int)(sample.Length * 0.01)];
        var hi = sample[(int)(sample.Length * 0.99)];
        if (hi <= lo) hi = lo + 1f;

        using var img = new Image<L8>(width, height);
        var range = hi - lo;
        for (int y = 0; y < height; y++)
        {
            var row = img.DangerousGetPixelRowMemory(y).Span;
            for (int x = 0; x < width; x++)
            {
                var v = (px[y * width + x] - lo) / range;
                if (v < 0) v = 0; else if (v > 1) v = 1;
                row[x] = new L8((byte)(v * 255f));
            }
        }
        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        return ms.ToArray();
    }
}

