using System.Text.Json;

namespace NINA.Headless.Services;

/// <summary>
/// Automates flat-field frame acquisition. NINA's Flat Wizard pattern:
///   1. (optional) slew the mount to a safe flat position (flats typically taken with the
///      scope covered or aimed at a lit panel, not at sky).
///   2. (optional) turn the flat panel on at the requested intensity. If no panel is
///      connected we pause and the app's sheet prompts the user to set up a light source
///      manually; they click Continue to proceed.
///   3. For each filter (just one "current" slot if no filter wheel):
///      a. Binary-search the exposure time until the frame's mean ADU lands in
///         [target ± tolerance].
///      b. Capture <frameCount> flats at that exposure with IMAGETYP=FLAT and the filter
///         name embedded in the filename.
///   4. Turn the panel off, stop.
///
/// Dark and Bias don't need orchestration — they're just regular captures with
/// ImageType=Dark/Bias set in the request. The app handles the "cover the scope" prompt.
/// </summary>
public class FlatWizardService
{
    public enum Phase { Idle, Prepping, WaitingForUser, Measuring, Capturing, Done, Cancelled, Failed }

    private readonly IndiDiscoveryService _indi;
    private readonly EquipmentSelectionService _equipment;
    private readonly CaptureStore _captures;
    private readonly CalibrationLibrary _library;
    private readonly ILogger<FlatWizardService> _log;

    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private TaskCompletionSource? _userContinue;

    // Snapshot state
    private Phase _phase = Phase.Idle;
    private string _message = "";
    private string? _currentFilter;
    private int _totalFrames;
    private int _framesCaptured;
    private double? _measuredAdu;
    private double? _currentExposure;

    public FlatWizardService(IndiDiscoveryService indi, EquipmentSelectionService equipment, CaptureStore captures, CalibrationLibrary library, ILogger<FlatWizardService> log)
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
                currentFilter = _currentFilter,
                totalFrames = _totalFrames,
                framesCaptured = _framesCaptured,
                measuredAdu = _measuredAdu,
                currentExposure = _currentExposure
            };
        }
    }

    public record FlatWizardRequest(
        int TargetAdu,
        int Tolerance,
        int FramesPerFilter,
        int Gain,
        int Offset,
        int Binning,
        bool UseFlatPanel,
        int PanelBrightness,
        double MinExposure,
        double MaxExposure,
        /// UUID of the Sequencer plan these flats attach to. Required — flats must be plan-scoped
        /// so dust/flexure captured for one plan doesn't bleed into another plan's calibration.
        string? PlanId,
        string? PlanName,
        /// Extra plans to additionally tag the flats with at capture time. Useful when a
        /// single optical-train state covers multiple plans in the same session — capture
        /// one flat set, share across several plans' Light calibration.
        IReadOnlyList<string>? AdditionalPlanIds);

    public Task StartAsync(FlatWizardRequest req)
    {
        lock (_lock)
        {
            if (IsRunning) throw new InvalidOperationException("Flat wizard is already running");
            _cts = new CancellationTokenSource();
            _phase = Phase.Prepping;
            _message = "Preparing";
            _framesCaptured = 0;
            _runTask = Task.Run(() => RunAsync(req, _cts.Token));
        }
        return Task.CompletedTask;
    }

    public void Cancel()
    {
        lock (_lock)
        {
            _cts?.Cancel();
            _userContinue?.TrySetResult();
        }
    }

    /// <summary>Called when the user confirms the manual light source is in place.</summary>
    public void ContinueManual()
    {
        lock (_lock) _userContinue?.TrySetResult();
    }

    private async Task RunAsync(FlatWizardRequest req, CancellationToken ct)
    {
        try
        {
            var camera = _equipment.GetSelected(DeviceKind.Camera);
            if (camera?.Provider != EquipmentProvider.Indi || !_equipment.IsConnected(DeviceKind.Camera))
                throw new InvalidOperationException("Camera not connected");

            // Flat panel is optional — if the user attached one (via the Devices tab) we'll
            // drive it; otherwise we fall through to a manual wait.
            var panel = _equipment.GetSelected(DeviceKind.FlatPanel);
            var panelConnected = panel != null && _equipment.IsConnected(DeviceKind.FlatPanel);
            if (req.UseFlatPanel && panelConnected)
            {
                SetState(Phase.Prepping, "Turning on flat panel...");
                await _indi.SetFlatPanelBrightnessAsync(panel!.UniqueId, req.PanelBrightness, ct);
                await _indi.SetFlatPanelOnAsync(panel!.UniqueId, true, ct);
            }
            else
            {
                SetState(Phase.WaitingForUser, "Set up a flat light source, then tap Continue");
                _userContinue = new TaskCompletionSource();
                await _userContinue.Task;
                _userContinue = null;
                ct.ThrowIfCancellationRequested();
            }

            // Build the filter list. If no filter wheel, run once with a null slot.
            var filters = new List<(int slot, string name)>();
            var wheel = _equipment.GetSelected(DeviceKind.FilterWheel);
            var wheelConnected = wheel != null && _equipment.IsConnected(DeviceKind.FilterWheel);
            if (wheelConnected)
            {
                filters.AddRange(_indi.GetFilterWheelFilters(wheel!.UniqueId));
                if (filters.Count == 0) filters.Add((1, "Filter"));
            }
            else
            {
                filters.Add((0, ""));
            }

            _totalFrames = filters.Count * req.FramesPerFilter;

            foreach (var (slot, filterName) in filters)
            {
                ct.ThrowIfCancellationRequested();
                _currentFilter = filterName;

                if (wheelConnected && slot > 0)
                {
                    SetState(Phase.Prepping, $"Moving to filter {filterName} (slot {slot})");
                    await _indi.SetFilterSlotAsync(wheel!.UniqueId, slot, ct);
                    await _indi.WaitForFilterIdleAsync(wheel!.UniqueId, TimeSpan.FromSeconds(30), ct);
                }

                SetState(Phase.Measuring, $"Finding exposure for {(filterName.Length == 0 ? "current filter" : filterName)}");
                var exposure = await FindOptimalExposureAsync(camera.UniqueId, req, ct);
                _currentExposure = exposure;

                for (int i = 0; i < req.FramesPerFilter; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    SetState(Phase.Capturing, $"{filterName} — {i + 1}/{req.FramesPerFilter} @ {exposure:F2}s");
                    await CaptureFlatAsync(camera, exposure, req, filterName, ct);
                    _framesCaptured++;
                }
            }

            if (req.UseFlatPanel && panelConnected)
            {
                await _indi.SetFlatPanelOnAsync(panel!.UniqueId, false, ct);
            }

            SetState(Phase.Done, $"Captured {_framesCaptured} flats");
        }
        catch (OperationCanceledException)
        {
            SetState(Phase.Cancelled, "Cancelled");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Flat wizard failed");
            SetState(Phase.Failed, ex.Message);
        }
    }

    /// <summary>Binary-search the exposure time until the captured frame's mean ADU is within
    /// <c>Tolerance</c> of <c>TargetAdu</c>, bounded by MinExposure..MaxExposure. Returns the
    /// first in-tolerance exposure.</summary>
    private async Task<double> FindOptimalExposureAsync(string cameraId, FlatWizardRequest req, CancellationToken ct)
    {
        double lo = req.MinExposure, hi = req.MaxExposure;
        double guess = Math.Clamp(1.0, lo, hi);
        for (int iter = 0; iter < 8; iter++) // 8 iterations = ~256x dynamic range
        {
            ct.ThrowIfCancellationRequested();
            await _indi.SetGainAsync(cameraId, req.Gain, ct);
            await _indi.SetOffsetAsync(cameraId, req.Offset, ct);
            await _indi.SetBinningAsync(cameraId, req.Binning, req.Binning, ct);
            await _indi.SetFrameTypeAsync(cameraId, "Flat", ct);
            var (fitsBytes, _) = await _indi.CameraExposeAsync(cameraId, guess, ct);

            var mean = ComputeMeanAdu(fitsBytes);
            _measuredAdu = mean;
            _log.LogInformation("Flat wizard probe: exposure={Exp}s mean={Mean}", guess, mean);

            var err = mean - req.TargetAdu;
            if (Math.Abs(err) <= req.Tolerance) return guess;

            // Linear rescale assumption — good enough for flat panels within 2-3 iterations.
            var scale = req.TargetAdu / Math.Max(1.0, mean);
            var next = Math.Clamp(guess * scale, lo, hi);
            // Guard against oscillation: if we hit a boundary, stop.
            if (next == guess) return guess;
            guess = next;
        }
        return guess;
    }

    private async Task CaptureFlatAsync(EquipmentDescriptor camera, double exposure, FlatWizardRequest req, string filter, CancellationToken ct)
    {
        await _indi.SetGainAsync(camera.UniqueId, req.Gain, ct);
        await _indi.SetOffsetAsync(camera.UniqueId, req.Offset, ct);
        await _indi.SetBinningAsync(camera.UniqueId, req.Binning, req.Binning, ct);
        await _indi.SetFrameTypeAsync(camera.UniqueId, "Flat", ct);
        var (fitsBytes, _) = await _indi.CameraExposeAsync(camera.UniqueId, exposure, ct);
        byte[] png;
        try { png = FitsToPng.Convert(fitsBytes); } catch { png = Array.Empty<byte>(); }
        if (png.Length > 0)
        {
            // Flats are keyed by PlanId; CameraId rides along so the UI can still show it.
            var entry = await _captures.SaveAsync(fitsBytes, png, new CaptureStore.SaveMetadata(
                ExposureSeconds: exposure,
                Gain: req.Gain, Offset: req.Offset, Binning: req.Binning,
                ImageType: "Flat", Filter: filter,
                CameraId: camera.UniqueId, CameraName: camera.Name,
                PlanId: req.PlanId, PlanName: req.PlanName,
                SharedWithPlanIds: req.AdditionalPlanIds));
            _library.NotifyCaptureAdded(entry);
        }
    }

    /// <summary>Fast mean-ADU estimator — samples every 64th pixel so it finishes in <10ms
    /// on a full-resolution frame. Good enough to drive the exposure search.</summary>
    private static double ComputeMeanAdu(byte[] fitsBytes)
    {
        // Find data offset by scanning header for END card.
        int dataOffset = 0;
        int offset = 0;
        while (offset < fitsBytes.Length)
        {
            var blockEnd = offset + 2880;
            for (int rec = offset; rec < blockEnd && rec + 80 <= fitsBytes.Length; rec += 80)
            {
                if (fitsBytes[rec] == 'E' && fitsBytes[rec + 1] == 'N' && fitsBytes[rec + 2] == 'D')
                {
                    dataOffset = blockEnd;
                    goto foundEnd;
                }
            }
            offset = blockEnd;
        }
        foundEnd:
        if (dataOffset == 0) return 0;
        var len = fitsBytes.Length - dataOffset;
        if (len < 2) return 0;
        double sum = 0;
        long n = 0;
        for (int i = dataOffset; i + 1 < fitsBytes.Length; i += 128)
        {
            var v = (ushort)((fitsBytes[i] << 8) | fitsBytes[i + 1]);
            sum += v;
            n++;
        }
        return n > 0 ? sum / n : 0;
    }

    private void SetState(Phase p, string msg)
    {
        lock (_lock) { _phase = p; _message = msg; }
        _log.LogInformation("Flat wizard: {Phase} — {Msg}", p, msg);
    }
}
