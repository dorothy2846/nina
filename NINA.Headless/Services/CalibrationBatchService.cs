namespace NINA.Headless.Services;

/// <summary>Straight "capture N frames with ImageType=Dark|Bias" loop. No binary search
/// (unlike FlatWizard). The user-facing "cover the scope" gate is a client-side confirm
/// before Start is called.</summary>
public class CalibrationBatchService
{
    public enum Phase { Idle, Capturing, Done, Cancelled, Failed }

    private readonly IndiDiscoveryService _indi;
    private readonly EquipmentSelectionService _equipment;
    private readonly CaptureStore _captures;
    private readonly CalibrationLibrary _library;
    private readonly ILogger<CalibrationBatchService> _log;

    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private Task? _runTask;

    private Phase _phase = Phase.Idle;
    private string _message = "";
    private string _imageType = "Dark";
    private int _totalFrames;
    private int _framesCaptured;
    private double _exposureTime;

    public CalibrationBatchService(IndiDiscoveryService indi, EquipmentSelectionService equipment, CaptureStore captures, CalibrationLibrary library, ILogger<CalibrationBatchService> log)
    {
        _indi = indi;
        _equipment = equipment;
        _captures = captures;
        _library = library;
        _log = log;
    }

    public bool IsRunning
    {
        get { lock (_lock) return _runTask != null && !_runTask.IsCompleted; }
    }

    public object Snapshot()
    {
        lock (_lock)
        {
            return new
            {
                running = IsRunning,
                phase = _phase.ToString(),
                message = _message,
                imageType = _imageType,
                totalFrames = _totalFrames,
                framesCaptured = _framesCaptured,
                exposureTime = _exposureTime
            };
        }
    }

    public record CalibrationRequest(
        string ImageType,
        int Count,
        double ExposureTime,
        int Gain,
        int Offset,
        int Binning);

    public Task StartAsync(CalibrationRequest req)
    {
        // Reject anything other than Dark/Bias — Flat has its own orchestrated service.
        var type = (req.ImageType ?? "").Trim();
        if (!type.Equals("Dark", StringComparison.OrdinalIgnoreCase) &&
            !type.Equals("Bias", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Calibration batch supports Dark or Bias, got '{req.ImageType}'");
        }
        if (req.Count < 1) throw new ArgumentException("Count must be ≥ 1");

        lock (_lock)
        {
            if (IsRunning) throw new InvalidOperationException("Calibration batch already running");
            _cts = new CancellationTokenSource();
            _phase = Phase.Capturing;
            _message = "Starting";
            _imageType = type;
            _totalFrames = req.Count;
            _framesCaptured = 0;
            _exposureTime = req.ExposureTime;
            _runTask = Task.Run(() => RunAsync(req with { ImageType = type }, _cts.Token));
        }
        return Task.CompletedTask;
    }

    public void Cancel()
    {
        lock (_lock) { _cts?.Cancel(); }
    }

    private async Task RunAsync(CalibrationRequest req, CancellationToken ct)
    {
        try
        {
            var camera = _equipment.GetSelected(DeviceKind.Camera);
            if (camera?.Provider != EquipmentProvider.Indi || !_equipment.IsConnected(DeviceKind.Camera))
                throw new InvalidOperationException("Camera not connected");

            // Configure sensor once — gain/offset/binning don't change between frames.
            await _indi.SetGainAsync(camera.UniqueId, req.Gain, ct);
            await _indi.SetOffsetAsync(camera.UniqueId, req.Offset, ct);
            await _indi.SetBinningAsync(camera.UniqueId, req.Binning, req.Binning, ct);
            await _indi.SetFrameTypeAsync(camera.UniqueId, req.ImageType, ct);

            for (int i = 0; i < req.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                SetState(Phase.Capturing, $"{req.ImageType} {i + 1}/{req.Count} @ {FormatExposure(req.ExposureTime)}");

                // Re-assert frame type each iteration — some INDI drivers silently reset it
                // between exposures, which would otherwise corrupt the IMAGETYP header.
                await _indi.SetFrameTypeAsync(camera.UniqueId, req.ImageType, ct);
                var (fitsBytes, _) = await _indi.CameraExposeAsync(camera.UniqueId, req.ExposureTime, ct);

                byte[] png;
                try { png = FitsToPng.Convert(fitsBytes); }
                catch { png = Array.Empty<byte>(); }

                if (png.Length > 0)
                {
                    // Dark/Bias library is camera-scoped — stamp with the running camera.
                    var entry = await _captures.SaveAsync(fitsBytes, png, new CaptureStore.SaveMetadata(
                        ExposureSeconds: req.ExposureTime,
                        Gain: req.Gain, Offset: req.Offset, Binning: req.Binning,
                        ImageType: req.ImageType,
                        CameraId: camera.UniqueId, CameraName: camera.Name));
                    // Invalidate any cached master for this group so the next calibration
                    // request rebuilds with the frame we just added.
                    _library.NotifyCaptureAdded(entry);
                }

                _framesCaptured = i + 1;
            }

            SetState(Phase.Done, $"Captured {_framesCaptured} {req.ImageType.ToLower()} frames");
        }
        catch (OperationCanceledException)
        {
            SetState(Phase.Cancelled, $"Cancelled after {_framesCaptured} frame(s)");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Calibration batch failed");
            SetState(Phase.Failed, ex.Message);
        }
    }

    private static string FormatExposure(double sec) =>
        sec < 1 ? $"{sec * 1000:0}ms" : $"{sec:0.##}s";

    private void SetState(Phase p, string msg)
    {
        lock (_lock) { _phase = p; _message = msg; }
        _log.LogInformation("Calibration: {Phase} — {Msg}", p, msg);
    }
}
