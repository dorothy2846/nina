using Microsoft.AspNetCore.Mvc;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Core.Model.Equipment;
using NINA.Headless.Models;
using NINA.Headless.Services;
using NINA.Headless.Services.Remote;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System;
using System.Linq;

namespace NINA.Headless.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class FocuserController : ControllerBase
{
    private static readonly object AutoFocusLock = new();
    private static CancellationTokenSource? _autoFocusCts;
    private static string? _afLastError;
    private static int? _afStepSizeOverride;
    private static double? _afExposureOverride;
    // One-shot calibration = the rig's real hyperbola from the last successful
    // V-curve fit (a = best HFR, b = curve width in steps). No magic constants.
    private static double? _aiCalibA, _aiCalibB;
    private static DateTime? _aiCalibAt;
    private static object? _afSnapshot;

    private readonly IFocuserMediator _focuser;
    private readonly NinaStateService _state;
    private readonly RemoteEventBus _eventBus;
    private readonly OneShotAiService _aiService;
    private readonly EquipmentSelectionService _equipment;
    private readonly IndiDiscoveryService _indi;
    private readonly AlpacaClient _alpaca;

    public FocuserController(IFocuserMediator focuser, NinaStateService state, RemoteEventBus eventBus,
        OneShotAiService aiService, EquipmentSelectionService equipment, IndiDiscoveryService indi, AlpacaClient alpaca)
    {
        _focuser = focuser;
        _state = state;
        _eventBus = eventBus;
        _aiService = aiService;
        _equipment = equipment;
        _indi = indi;
        _alpaca = alpaca;
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        var selected = _equipment.GetSelected(DeviceKind.Focuser);
        if (_equipment.IsConnected(DeviceKind.Focuser) && selected?.Provider == EquipmentProvider.Indi)
        {
            var s = _indi.BuildFocuserStatus(selected.UniqueId);
            if (s != null) return Ok(s);
        }
        return Ok(new { connected = false, name = "Not connected" });
    }

    [HttpPost("connect")]
    public Task<IActionResult> Connect([FromBody] ConnectRequest? request) =>
        IndiDeviceConnectFlow.ConnectAsync(_equipment, _indi, DeviceKind.Focuser, "Focuser", request, HttpContext.RequestAborted, _alpaca);

    [HttpPost("disconnect")]
    public async Task<IActionResult> Disconnect()
    {
        var selected = _equipment.GetSelected(DeviceKind.Focuser);
        if (selected?.Provider == EquipmentProvider.Indi)
            _indi.RecordConnectionIntent(selected.UniqueId, false);
            await _indi.DisconnectDeviceAsync(selected.UniqueId, HttpContext.RequestAborted);
        _equipment.Disconnect(DeviceKind.Focuser);
        return Ok(new { success = true, message = "Focuser disconnected" });
    }

    [HttpGet("info")]
    public IActionResult GetInfo()
    {
        var info = _focuser.GetInfo();
        if (info == null) return Ok(new { connected = false });

        // Feature probes — iOS greys out the matching control when the driver doesn't
        // expose the property. INDI standard names first; older drivers fall back via
        // the AUTO_FOCUS_COMP variant checked by the setter itself.
        var selected = _equipment.GetSelected(DeviceKind.Focuser);
        var indiName = (_equipment.IsConnected(DeviceKind.Focuser) && selected?.Provider == EquipmentProvider.Indi) ? selected.UniqueId : null;
        var capabilities = new
        {
            hasTemperature = indiName != null && _indi.DeviceHasProperty(indiName, "FOCUS_TEMPERATURE"),
            hasTempCompensation = indiName != null && _indi.DeviceHasAnyProperty(indiName,
                "FOCUS_TEMPERATURE_COMPENSATION", "AUTO_FOCUS_COMP"),
            hasBacklash = indiName != null && _indi.DeviceHasAnyProperty(indiName,
                "FOCUS_BACKLASH_STEPS", "FOCUS_BACKLASH_TOGGLE")
        };

        return Ok(new
        {
            connected = info.Connected,
            name = info.Name,
            position = info.Position,
            temperature = double.IsNaN(info.Temperature) ? (double?)null : info.Temperature,
            stepSize = info.StepSize,
            isMoving = info.IsMoving,
            capabilities
        });
    }

    [HttpPost("move")]
    public async Task<IActionResult> Move([FromBody] FocuserMoveRequest request)
    {
        var selected = _equipment.GetSelected(DeviceKind.Focuser);
        if (_equipment.IsConnected(DeviceKind.Focuser) && selected?.Provider == EquipmentProvider.Indi)
        {
            await _indi.FocuserMoveAsync(selected.UniqueId, request.Position, HttpContext.RequestAborted);
            return Ok(new { success = true, position = request.Position });
        }
        var movedTo = await _focuser.MoveFocuser(request.Position, CancellationToken.None);
        return Ok(new { success = true, position = movedTo });
    }

    [HttpPost("halt")]
    public async Task<IActionResult> Halt()
    {
        var selected = _equipment.GetSelected(DeviceKind.Focuser);
        if (_equipment.IsConnected(DeviceKind.Focuser) && selected?.Provider == EquipmentProvider.Indi)
        {
            await _indi.FocuserHaltAsync(selected.UniqueId, HttpContext.RequestAborted);
        }
        return Ok(new { success = true, message = "Halted" });
    }

    public record TempCompRequest(bool Enabled);

    [HttpPost("temp-compensation")]
    public async Task<IActionResult> SetTempCompensation([FromBody] TempCompRequest request)
    {
        var selected = _equipment.GetSelected(DeviceKind.Focuser);
        if (!_equipment.IsConnected(DeviceKind.Focuser) || selected?.Provider != EquipmentProvider.Indi)
            return BadRequest(new { success = false, message = "Focuser not connected via INDI" });

        var ok = await _indi.SetFocuserTempCompensationAsync(selected.UniqueId, request.Enabled, HttpContext.RequestAborted);
        return Ok(new {
            success = ok,
            message = ok ? null : "이 focuser 드라이버는 온도 보상을 지원하지 않습니다."
        });
    }

    public record BacklashRequest(int Steps);

    /// <summary>Push driver-level backlash compensation. Per-filter focus offsets and
    /// autofocus passes rely on this to reverse direction cleanly. <c>steps=0</c>
    /// disables.</summary>
    [HttpPost("backlash")]
    public async Task<IActionResult> SetBacklash([FromBody] BacklashRequest request)
    {
        var selected = _equipment.GetSelected(DeviceKind.Focuser);
        if (!_equipment.IsConnected(DeviceKind.Focuser) || selected?.Provider != EquipmentProvider.Indi)
            return BadRequest(new { success = false, message = "Focuser not connected via INDI" });

        var ok = await _indi.SetFocuserBacklashAsync(selected.UniqueId, Math.Max(0, request.Steps), HttpContext.RequestAborted);
        return Ok(new {
            success = ok,
            message = ok ? null : "이 focuser 드라이버는 backlash 설정을 지원하지 않습니다."
        });
    }

    public record AutofocusStartRequest(int? StepSize, double? ExposureSeconds);

    [HttpPost("autofocus/start")]
    public IActionResult StartAutoFocus([FromBody] AutofocusStartRequest? request)
    {
        lock (AutoFocusLock)
        {
            _autoFocusCts?.Cancel();
            _autoFocusCts = new CancellationTokenSource();
            _afLastError = null;
            _afSnapshot = null; // stale snapshot from a prior run must not leak into the new one
            // UI-supplied overrides; profile values remain the default.
            _afStepSizeOverride = request?.StepSize is > 0 ? request.StepSize : null;
            _afExposureOverride = request?.ExposureSeconds is > 0 ? request.ExposureSeconds : null;

            var token = _autoFocusCts.Token;
            _ = Task.Run(() => PerformAutofocusAsync(token), token);
        }

        return Ok(new { success = true, message = "Autofocus started" });
    }

    /// <summary>Real V-curve autofocus. The previous implementation SIMULATED
    /// the measurements (cosh curve around a mock 'perfect focus' derived
    /// from the start position) — it reported success on any hardware while
    /// measuring nothing. This one drives the INDI focuser directly, takes
    /// real INDI exposures, measures the median half-flux radius per point,
    /// and restores the starting position on any failure.</summary>
    private async Task PerformAutofocusAsync(CancellationToken token)
    {
        int? initialPosition = null;
        string? focuserName = null;
        try
        {
            var profile = ProfileSyncController.ActiveCloudProfile;
            int stepSize = _afStepSizeOverride ?? profile?.FocuserStepSize ?? 50;
            int initialOffsets = profile?.InitialOffsetSteps ?? 4;
            int backlash = profile?.Backlash ?? 100;
            double exposureSeconds = _afExposureOverride ?? 3.0;

            var focuserSel = _equipment.GetSelected(DeviceKind.Focuser);
            if (focuserSel?.Provider != EquipmentProvider.Indi || !_equipment.IsConnected(DeviceKind.Focuser))
                throw new Exception("Focuser not connected");
            focuserName = focuserSel.UniqueId;

            var cameraSel = _equipment.GetSelected(DeviceKind.Camera);
            if (cameraSel?.Provider != EquipmentProvider.Indi || !_equipment.IsConnected(DeviceKind.Camera))
                throw new Exception("Camera not connected");

            initialPosition = _indi.GetFocuserPosition(focuserName)
                ?? throw new Exception("Focuser reports no position");

            int outerBoundary = initialPosition.Value + (stepSize * initialOffsets);
            int totalPoints = (initialOffsets * 2) + 1;
            var points = new List<AutofocusMath.FocuserPoint>();

            await BroadcastAfStatusAsync(true, false, null, points);

            // 1. Move to the outer boundary with a backlash overshoot so every
            // measurement leg approaches from the same direction.
            await MoveFocuserAndSettleAsync(focuserName, outerBoundary + backlash, token);
            await MoveFocuserAndSettleAsync(focuserName, outerBoundary, token);

            int currentTarget = outerBoundary;
            for (int i = 0; i < totalPoints; i++)
            {
                token.ThrowIfCancellationRequested();

                if (i > 0)
                {
                    currentTarget -= stepSize;
                    await MoveFocuserAndSettleAsync(focuserName, currentTarget, token);
                }

                // Capture + measure. One retry on a starless frame (wind gust,
                // brief cloud); two in a row is a real condition worth failing on.
                double? hfr = await CaptureAndMeasureHfrAsync(cameraSel.UniqueId, exposureSeconds, token)
                           ?? await CaptureAndMeasureHfrAsync(cameraSel.UniqueId, exposureSeconds, token);
                if (hfr == null)
                    throw new Exception($"No stars detected at position {currentTarget} (clouds, cap on, or way out of focus)");

                points.Add(new AutofocusMath.FocuserPoint(currentTarget, hfr.Value));
                points = AutofocusMath.FilterOutliers(points);
                var fit = AutofocusMath.CalculateHyperbolicFit(points);
                await BroadcastAfStatusAsync(true, false, fit, points);
            }

            // 2. Final fit and move to computed best focus.
            var finalFit = AutofocusMath.CalculateHyperbolicFit(points);
            if (!finalFit.Success)
                throw new Exception("Hyperbolic fit failed — V-curve did not converge (bad seeing or wrong step size)");

            int targetFocus = (int)Math.Round(finalFit.P);
            await MoveFocuserAndSettleAsync(focuserName, targetFocus + backlash, token);
            await MoveFocuserAndSettleAsync(focuserName, targetFocus, token);

            lock (AutoFocusLock) { _aiCalibA = finalFit.A; _aiCalibB = finalFit.B; _aiCalibAt = DateTime.UtcNow; }
            Console.WriteLine($"[Autofocus] converged: best={targetFocus} minHFR={finalFit.MinimumHfd:F2} from {points.Count} points");
            await BroadcastAfStatusAsync(false, true, finalFit, points);
        }
        catch (Exception ex)
        {
            // Restore where the user left the focuser — a failed run must not
            // strand the imaging train hundreds of steps out of focus.
            if (initialPosition.HasValue && focuserName != null)
            {
                try { await MoveFocuserAndSettleAsync(focuserName, initialPosition.Value, CancellationToken.None); }
                catch { /* best effort */ }
            }
            lock (AutoFocusLock) { _afLastError = ex.Message; }
            _eventBus.Broadcast("AutofocusError", ex.Message);
            await BroadcastAfStatusAsync(false, false, null, new List<AutofocusMath.FocuserPoint>());
        }
    }

    /// <summary>INDI focuser move + bounded settle: poll until the reported
    /// position stops changing (and matches the target when the driver is
    /// exact), then a short mechanical settle.</summary>
    private async Task MoveFocuserAndSettleAsync(string focuserName, int target, CancellationToken token)
    {
        await _indi.FocuserMoveAsync(focuserName, target, token);
        int? last = null;
        for (int i = 0; i < 60; i++)
        {
            await Task.Delay(500, token);
            var pos = _indi.GetFocuserPosition(focuserName);
            if (pos.HasValue && pos == last && Math.Abs(pos.Value - target) <= 2) break;
            last = pos;
        }
        await Task.Delay(500, token);
    }

    /// <summary>INDI exposure → in-memory FITS → star detection → median HFR.
    /// Null when no stars were found (caller decides whether that's fatal).</summary>
    private async Task<double?> CaptureAndMeasureHfrAsync(string cameraName, double exposureSeconds, CancellationToken token)
    {
        var (bytes, _) = await _indi.CameraExposeAsync(cameraName, exposureSeconds, token);
        if (bytes == null || bytes.Length == 0) return null;

        float[] px; int w, h;
        try { (px, w, h) = FitsReader.Read(bytes); }
        catch { return null; }

        var stars = StarDetector.Detect(px, w, h, maxStars: 60);
        if (stars.Count == 0) return null;
        return AutofocusMath.MeasureMedianHfr(px, w, h, stars);
    }

    private async Task BroadcastAfStatusAsync(bool running, bool completed, AutofocusMath.HyperbolicFitResult? fit, List<AutofocusMath.FocuserPoint> points)
    {
        var payload = new
        {
            running,
            completed,
            points = points.Select(p => new { x = p.Position, y = p.Hfd, isOutlier = p.IsOutlier }),
            fitParameters = fit != null && fit.Success ? new { a = fit.A, b = fit.B, p = fit.P } : null,
            optimalPosition = fit != null && fit.Success ? fit.P : (double?)null,
            lastError = _afLastError
        };

        lock (AutoFocusLock) { _afSnapshot = payload; }
        _eventBus.Broadcast("AutofocusLiveUpdate", payload);
    }

    /// <summary>HTTP mirror of the AutofocusLiveUpdate event stream — pollers
    /// (and post-mortems) see the same points/fit plus WHY a run failed.</summary>
    [HttpGet("autofocus/status")]
    public IActionResult AutofocusStatus()
    {
        lock (AutoFocusLock)
        {
            return Ok(new
            {
                snapshot = _afSnapshot,
                lastError = _afLastError
            });
        }
    }

    [HttpPost("autofocus/stop")]
    public IActionResult StopAutoFocus()
    {
        lock (AutoFocusLock)
        {
            _autoFocusCts?.Cancel();
        }
        return Ok(new { success = true });
    }

    /// <summary>One-shot calibration is a full V-curve run — it measures the rig's
    /// real hyperbola (a, b), which is stored automatically on fit success and is what
    /// every subsequent one-shot uses. Monitor progress via autofocus status/events.</summary>
    [HttpPost("ai-autofocus/calibrate")]
    public IActionResult CalibrateAiOneShot()
    {
        var result = StartAutoFocus(null);
        return Ok(new { success = true, message = "Calibration V-curve run started — monitor /focuser/autofocus/status" });
    }

    [HttpPost("ai-autofocus/start")]
    public IActionResult StartAiOneShot()
    {
        lock (AutoFocusLock)
        {
            _autoFocusCts?.Cancel();
            _autoFocusCts = new CancellationTokenSource();

            var token = _autoFocusCts.Token;
            _ = Task.Run(() => PerformAiOneShotAsync(token), token);
        }
        return Ok(new { success = true, message = "AI Autofocus started" });
    }

    /// <summary>Real one-shot focus: two measured HFRs on the calibrated hyperbola
    /// pin down best focus without a full V-curve. A final confirmation exposure is
    /// mandatory — success is only reported when the measured HFR actually proves
    /// focus; anything else restores the initial position and reports the reason.</summary>
    private async Task PerformAiOneShotAsync(CancellationToken token)
    {
        int? initialPosition = null;
        string? focuserName = null;
        try
        {
            double a, b;
            lock (AutoFocusLock)
            {
                if (_aiCalibA is not double ca || _aiCalibB is not double cb)
                    throw new Exception("Not calibrated — run a V-curve autofocus (or ai-autofocus/calibrate) first; its fit is the calibration");
                a = ca; b = cb;
            }

            var profile = ProfileSyncController.ActiveCloudProfile;
            int stepSize = profile?.FocuserStepSize ?? 50;
            int backlash = profile?.Backlash ?? 100;
            double exposureSeconds = 3.0;

            var focuserSel = _equipment.GetSelected(DeviceKind.Focuser);
            if (focuserSel?.Provider != EquipmentProvider.Indi || !_equipment.IsConnected(DeviceKind.Focuser))
                throw new Exception("Focuser not connected");
            focuserName = focuserSel.UniqueId;

            var cameraSel = _equipment.GetSelected(DeviceKind.Camera);
            if (cameraSel?.Provider != EquipmentProvider.Indi || !_equipment.IsConnected(DeviceKind.Camera))
                throw new Exception("Camera not connected");

            initialPosition = _indi.GetFocuserPosition(focuserName)
                ?? throw new Exception("Focuser reports no position");
            int x0 = initialPosition.Value;

            _eventBus.Broadcast("AiAutofocusLiveUpdate", new { running = true, completed = false });

            async Task<double> MeasureAsync(int position)
            {
                var hfr = await CaptureAndMeasureHfrAsync(cameraSel.UniqueId, exposureSeconds, token)
                       ?? await CaptureAndMeasureHfrAsync(cameraSel.UniqueId, exposureSeconds, token);
                if (hfr == null) throw new Exception($"No stars detected at position {position} — cannot measure focus");
                return hfr.Value;
            }

            // HFR predicted by the calibrated hyperbola at distance (p - x):
            // cosh(asinh(t)) == sqrt(1 + t^2).
            double Predict(double p, double x) { var t = (p - x) / b; return a * Math.Sqrt(1 + t * t); }

            double y0 = await MeasureAsync(x0);

            if (y0 <= a * 1.08)
            {
                _eventBus.Broadcast("AiAutofocusLiveUpdate", new
                {
                    running = false, completed = true, stepsMoved = 0, finalPosition = x0,
                    initialHfr = y0, finalHfr = y0, message = "Already at best focus"
                });
                return;
            }

            // Probe: one short move inward disambiguates which side of focus we are on.
            int probe = Math.Max(2 * stepSize, 20);
            int x1 = x0 - probe;
            await MoveFocuserAndSettleAsync(focuserName, x1, token);
            double y1 = await MeasureAsync(x1);

            // Distance from focus implied by y0: |p - x0| = b * sqrt((y0/a)^2 - 1).
            double s0 = b * Math.Sqrt(Math.Max(0, (y0 / a) * (y0 / a) - 1));
            double cand1 = x0 - s0, cand2 = x0 + s0;
            double err1 = Math.Abs(Predict(cand1, x1) - y1);
            double err2 = Math.Abs(Predict(cand2, x1) - y1);
            double target = err1 <= err2 ? cand1 : cand2;
            double residual = Math.Min(err1, err2);

            // Both hypotheses disagreeing with the probe measurement means the
            // measurements do not sit on the calibrated curve (clouds, wind,
            // stale calibration) — refuse rather than move somewhere random.
            if (residual > Math.Max(0.35 * y1, 0.6))
                throw new Exception($"Measurements inconsistent with calibration (residual {residual:F2}px at probe) — conditions changed or calibration stale; run a V-curve");

            int targetPos = (int)Math.Round(target);
            int maxTravel = stepSize * 20;
            if (Math.Abs(targetPos - x0) > maxTravel)
                throw new Exception($"Computed correction {targetPos - x0} steps exceeds sanity limit ({maxTravel}) — run a V-curve instead");

            // Approach from the same (outward) side as the V-curve for backlash consistency.
            await MoveFocuserAndSettleAsync(focuserName, targetPos + backlash, token);
            await MoveFocuserAndSettleAsync(focuserName, targetPos, token);

            // Mandatory confirmation exposure — the only thing that can declare success.
            double yf = await MeasureAsync(targetPos);
            if (yf > a * 1.30)
                throw new Exception($"Confirmation frame HFR {yf:F2}px did not reach focus (calibrated best {a:F2}px) — restored initial position");

            Console.WriteLine($"[AiAutofocus] one-shot: {x0} -> {targetPos} HFR {y0:F2} -> {yf:F2} (calib a={a:F2} b={b:F1})");
            _eventBus.Broadcast("AiAutofocusLiveUpdate", new
            {
                running = false, completed = true,
                stepsMoved = targetPos - x0, finalPosition = targetPos,
                initialHfr = y0, finalHfr = yf
            });
        }
        catch (Exception ex)
        {
            if (initialPosition.HasValue && focuserName != null)
            {
                try { await MoveFocuserAndSettleAsync(focuserName, initialPosition.Value, CancellationToken.None); }
                catch { /* best effort */ }
            }
            Console.WriteLine($"[AiAutofocus] failed: {ex.Message}");
            _eventBus.Broadcast("AiAutofocusLiveUpdate", new { running = false, completed = false, error = ex.Message });
            _eventBus.Broadcast("AutofocusError", ex.Message);
        }
    }
}
