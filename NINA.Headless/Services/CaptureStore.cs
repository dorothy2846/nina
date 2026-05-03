using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Processing;
using IsImage = SixLabors.ImageSharp.Image;

namespace NINA.Headless.Services;

/// <summary>
/// Persists captured frames to disk in three formats: original FITS (archival), full-size
/// 8-bit PNG (display/zoom), and a thumbnail PNG sized for the phone screen.
///
/// Files share a timestamp-based Id so the client can ask for a specific format without the
/// server remembering more than a lightweight index. Old entries beyond MaxEntries are pruned
/// whenever a new capture finishes.
/// </summary>
public class CaptureStore
{
    // Thumbnail tuned for phone display: 1024px covers retina-dense screens at natural zoom,
    // JPEG quality 82 keeps the file around 100 KB for typical astro frames (8-bit grayscale
    // PNG of the same dimensions was 4–5× larger). FITS is always kept uncompressed for
    // archival purposes — compressed FITS (rice, gzip) would slow every save.
    public const int ThumbnailMaxWidth = 1024;
    private const int JpegQualityThumbnail = 82;
    // Full PNG uses max zlib compression (9) — a one-time CPU cost at save for a 20–30%
    // smaller file the client downloads on demand (zoom, FITS is a separate fetch).
    private const int PngCompressionLevelFull = 9;
    private const int MaxEntries = 200;

    private readonly string _root;
    private readonly List<CaptureEntry> _entries = new();
    private readonly object _lock = new();
    private int _sequenceToday;
    private DateOnly _sequenceDate;

    public CaptureStore()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _root = Path.Combine(home, "BeyondStellar", "captures");
        Directory.CreateDirectory(_root);
        LoadExistingIndex();
    }

    public string Root => _root;

    public IReadOnlyList<CaptureEntry> GetAll()
    {
        lock (_lock) return _entries.ToList();
    }

    public CaptureEntry? GetLatest()
    {
        lock (_lock) return _entries.LastOrDefault();
    }

    public CaptureEntry? GetById(string id)
    {
        lock (_lock) return _entries.FirstOrDefault(e => e.Id == id);
    }

    /// <summary>Write FITS + re-compressed full PNG + JPEG thumbnail for a single capture.
    /// The incoming `fullPngBytes` is the autostretched 8-bit PNG from the capture pipeline;
    /// we recompress at max zlib level for storage/transfer savings. Fails closed — any I/O
    /// error rolls back the whole entry.</summary>
    /// Everything a caller sets on a capture aside from the frame bytes themselves. Groups
    /// the long tail of optional tagging (camera, plan, shared-with) so callers don't pass
    /// 10 named args.
    public record SaveMetadata(
        double ExposureSeconds,
        int Gain,
        int Offset,
        int Binning,
        string ImageType = "Light",
        string? Filter = null,
        string? CameraId = null,
        string? CameraName = null,
        string? PlanId = null,
        string? PlanName = null,
        IReadOnlyList<string>? SharedWithPlanIds = null);

    public Task<CaptureEntry> SaveAsync(byte[] fitsBytes, byte[] fullPngBytes, SaveMetadata meta)
        => SaveAsync(fitsBytes, fullPngBytes,
            meta.ExposureSeconds, meta.Gain, meta.Offset, meta.Binning,
            meta.ImageType, meta.Filter,
            meta.CameraId, meta.CameraName,
            meta.PlanId, meta.PlanName, meta.SharedWithPlanIds);

    public async Task<CaptureEntry> SaveAsync(byte[] fitsBytes, byte[] fullPngBytes, double exposureSeconds, int gain, int offset, int binning, string imageType = "Light", string? filter = null, string? cameraId = null, string? cameraName = null, string? planId = null, string? planName = null, IReadOnlyList<string>? sharedWithPlanIds = null)
    {
        var now = DateTime.Now;
        var id = NextId(now);
        var dir = _root;

        // Calibration frames get a type suffix so they're obvious on disk and in listings —
        // nothing ruins a stacking run like mistaking a 30s dark for a 30s light frame.
        var typeSuffix = imageType.Equals("Light", StringComparison.OrdinalIgnoreCase)
            ? "" : "_" + imageType.ToUpperInvariant();
        var filterSuffix = string.IsNullOrWhiteSpace(filter) ? "" : "_" + filter;
        var baseName = id + typeSuffix + filterSuffix;

        var fitsPath = Path.Combine(dir, baseName + ".fits");
        var pngPath = Path.Combine(dir, baseName + ".png");
        var thumbPath = Path.Combine(dir, baseName + ".thumb.jpg");
        // Sidecar JSON carries the metadata that doesn't fit the filename encoding — notably
        // the camera identifiers (Dark/Bias library keying) and the plan id (Flat keying).
        // Written alongside the FITS so LoadExistingIndex can rebuild the full state after
        // a server restart.
        var metaPath = Path.Combine(dir, baseName + ".meta.json");

        try
        {
            await File.WriteAllBytesAsync(fitsPath, fitsBytes);
            await RecompressFullPngAsync(fullPngBytes, pngPath);
            await WriteThumbnailJpegAsync(fullPngBytes, thumbPath);
            await WriteMetaSidecarAsync(metaPath, exposureSeconds, gain, offset, binning, imageType, filter, cameraId, cameraName, planId, planName, sharedWithPlanIds);
        }
        catch
        {
            TryDelete(fitsPath); TryDelete(pngPath); TryDelete(thumbPath); TryDelete(metaPath);
            throw;
        }

        var entry = new CaptureEntry(
            Id: id,
            Timestamp: now,
            FitsPath: fitsPath,
            FullPngPath: pngPath,
            ThumbnailPath: thumbPath,
            ExposureSeconds: exposureSeconds,
            Gain: gain,
            Offset: offset,
            Binning: binning,
            ImageType: imageType,
            Filter: filter,
            FitsSizeBytes: fitsBytes.Length,
            FullPngSizeBytes: fullPngBytes.Length,
            CameraId: cameraId,
            CameraName: cameraName,
            PlanId: planId,
            PlanName: planName,
            SharedWithPlanIds: sharedWithPlanIds);

        lock (_lock)
        {
            _entries.Add(entry);
            PruneLocked();
        }
        return entry;
    }

    /// <summary>Capture metadata that filename encoding can't carry: camera identifiers and
    /// plan id. Written as JSON next to the FITS so it survives restart and is human-readable
    /// for debugging. Intentionally small — we don't repeat anything already in the filename
    /// or FITS header.</summary>
    private record CaptureSidecar(
        double ExposureSeconds,
        int Gain,
        int Offset,
        int Binning,
        string ImageType,
        string? Filter,
        string? CameraId,
        string? CameraName,
        string? PlanId,
        string? PlanName,
        IReadOnlyList<string>? SharedWithPlanIds);

    private static async Task WriteMetaSidecarAsync(string path, double exposureSeconds, int gain, int offset, int binning, string imageType, string? filter, string? cameraId, string? cameraName, string? planId, string? planName, IReadOnlyList<string>? sharedWithPlanIds)
    {
        var sidecar = new CaptureSidecar(exposureSeconds, gain, offset, binning, imageType, filter, cameraId, cameraName, planId, planName, sharedWithPlanIds);
        var json = System.Text.Json.JsonSerializer.Serialize(sidecar, new System.Text.Json.JsonSerializerOptions { WriteIndented = false });
        await File.WriteAllTextAsync(path, json);
    }

    private static CaptureSidecar? TryReadSidecar(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            return System.Text.Json.JsonSerializer.Deserialize<CaptureSidecar>(json);
        }
        catch
        {
            return null;
        }
    }

    private static async Task WriteThumbnailJpegAsync(byte[] sourcePng, string destPath)
    {
        using var img = IsImage.Load(sourcePng);
        if (img.Width > ThumbnailMaxWidth)
        {
            var scale = (double)ThumbnailMaxWidth / img.Width;
            img.Mutate(x => x.Resize(ThumbnailMaxWidth, (int)(img.Height * scale)));
        }
        using var fs = File.Create(destPath);
        await img.SaveAsJpegAsync(fs, new JpegEncoder { Quality = JpegQualityThumbnail });
    }

    private static async Task RecompressFullPngAsync(byte[] sourcePng, string destPath)
    {
        using var img = IsImage.Load(sourcePng);
        using var fs = File.Create(destPath);
        await img.SaveAsPngAsync(fs, new PngEncoder
        {
            CompressionLevel = (PngCompressionLevel)PngCompressionLevelFull,
            BitDepth = PngBitDepth.Bit8,
            ColorType = PngColorType.Grayscale
        });
    }

    /// <summary>Remove a single capture by id. Deletes the FITS + PNG + thumbnail from
    /// disk and drops the entry from the in-memory index. Returns false if the id isn't
    /// known.</summary>
    public bool DeleteById(string id)
    {
        CaptureEntry? entry;
        lock (_lock)
        {
            entry = _entries.FirstOrDefault(e => e.Id == id);
            if (entry == null) return false;
            _entries.Remove(entry);
        }
        TryDelete(entry.FitsPath);
        TryDelete(entry.FullPngPath);
        TryDelete(entry.ThumbnailPath);
        // Sidecar path derives from the FITS path minus the ".fits" extension.
        var baseName = Path.GetFileNameWithoutExtension(entry.FitsPath);
        TryDelete(Path.Combine(_root, baseName + ".meta.json"));
        return true;
    }

    private string NextId(DateTime at)
    {
        lock (_lock)
        {
            var today = DateOnly.FromDateTime(at);
            if (today != _sequenceDate)
            {
                _sequenceDate = today;
                _sequenceToday = 0;
            }
            _sequenceToday++;
            return $"{at:yyyyMMdd_HHmmss}_{_sequenceToday:D3}";
        }
    }

    private void LoadExistingIndex()
    {
        foreach (var fits in Directory.GetFiles(_root, "*.fits"))
        {
            var baseName = Path.GetFileNameWithoutExtension(fits);
            var png = Path.Combine(_root, baseName + ".png");
            var thumbJpg = Path.Combine(_root, baseName + ".thumb.jpg");
            var thumbPng = Path.Combine(_root, baseName + ".thumb.png"); // legacy
            var thumb = File.Exists(thumbJpg) ? thumbJpg
                      : (File.Exists(thumbPng) ? thumbPng : null);
            if (thumb == null || !File.Exists(png)) continue;
            // Parse the calibration-type suffix back out so listings can show/filter by type
            // without re-opening the FITS. Accepts "<id>", "<id>_DARK", "<id>_FLAT_<filter>", etc.
            var parts = baseName.Split('_');
            string id = baseName;
            string imageType = "Light";
            string? filter = null;
            var knownTypes = new[] { "DARK", "BIAS", "FLAT" };
            // We generate IDs as yyyyMMdd_HHmmss_NNN (3 parts joined by '_').
            if (parts.Length >= 3)
            {
                id = string.Join("_", parts.Take(3));
                if (parts.Length >= 4 && knownTypes.Contains(parts[3]))
                {
                    imageType = char.ToUpper(parts[3][0]) + parts[3].Substring(1).ToLower();
                    if (parts.Length >= 5) filter = string.Join("_", parts.Skip(4));
                }
            }
            var info = new FileInfo(fits);
            // Prefer sidecar values when present — they carry the full metadata that the
            // filename + disk can't (camera id, plan id, exposure/gain). Pre-sidecar entries
            // fall back to zeros and still show up in the library listing for viewing even
            // though they can't be used as calibration masters (TryMakeKey will reject them).
            var sidecar = TryReadSidecar(Path.Combine(_root, baseName + ".meta.json"));
            _entries.Add(new CaptureEntry(
                Id: baseName,
                Timestamp: info.LastWriteTime,
                FitsPath: fits,
                FullPngPath: png,
                ThumbnailPath: thumb,
                ExposureSeconds: sidecar?.ExposureSeconds ?? 0,
                Gain: sidecar?.Gain ?? 0,
                Offset: sidecar?.Offset ?? 0,
                Binning: sidecar?.Binning ?? 0,
                ImageType: sidecar?.ImageType ?? imageType,
                Filter: sidecar?.Filter ?? filter,
                FitsSizeBytes: info.Length,
                FullPngSizeBytes: new FileInfo(png).Length,
                CameraId: sidecar?.CameraId,
                CameraName: sidecar?.CameraName,
                PlanId: sidecar?.PlanId,
                PlanName: sidecar?.PlanName,
                SharedWithPlanIds: sidecar?.SharedWithPlanIds));
        }
        _entries.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
    }

    private void PruneLocked()
    {
        while (_entries.Count > MaxEntries)
        {
            var old = _entries[0];
            _entries.RemoveAt(0);
            TryDelete(old.FitsPath);
            TryDelete(old.FullPngPath);
            TryDelete(old.ThumbnailPath);
            var baseName = Path.GetFileNameWithoutExtension(old.FitsPath);
            TryDelete(Path.Combine(_root, baseName + ".meta.json"));
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}

public record CaptureEntry(
    string Id,
    DateTime Timestamp,
    string FitsPath,
    string FullPngPath,
    string ThumbnailPath,
    double ExposureSeconds,
    int Gain,
    int Offset,
    int Binning,
    string ImageType,
    string? Filter,
    long FitsSizeBytes,
    long FullPngSizeBytes,
    // Added for the calibration library model split:
    //   • Dark / Bias are camera-level resources — keyed by CameraId so each camera has its
    //     own reusable masters across sessions.
    //   • Flat is tied to a specific imaging plan — keyed by PlanId so dust/flexure captured
    //     for one plan doesn't accidentally get applied to a different target's lights.
    // Both fields are optional so older captures (loaded from disk without sidecar metadata)
    // continue to appear in the library view even if they can't be used as masters.
    string? CameraId = null,
    string? CameraName = null,
    string? PlanId = null,
    // Human-readable plan name captured at the moment the frame was taken. Snapshotting it
    // here (rather than looking up by PlanId later) means renamed or deleted plans still
    // show a meaningful folder label in the library browser.
    string? PlanName = null,
    // Additional plans this frame is ALSO associated with. Populated for Flat frames when
    // the user captures one flat set and shares it across multiple same-session plans
    // (same optical train → same flats, but different targets). Library matching treats
    // the frame as belonging to both its primary PlanId and every id in this list.
    IReadOnlyList<string>? SharedWithPlanIds = null);
