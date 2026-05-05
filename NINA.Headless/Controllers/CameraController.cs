using Microsoft.AspNetCore.Mvc;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using NINA.Headless.Models;
using NINA.Headless.Services;
using NINA.Headless.Services.Remote;

namespace NINA.Headless.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class CameraController : ControllerBase
{
    private static readonly byte[] TransparentPngPlaceholder = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aGxQAAAAASUVORK5CYII=");

    private readonly NinaStateService _state;
    private readonly RemoteEventBus _eventBus;
    private readonly CameraSelectionService _cameraSelection;
    private readonly EquipmentSelectionService _equipment;
    private readonly IndiDiscoveryService _indi;
    private readonly CaptureStore _captures;
    private readonly CameraStreamService _stream;
    private readonly FlatWizardService _flatWizard;
    private readonly CalibrationBatchService _calibration;
    private readonly CalibrationLibrary _library;
    private readonly LiveStackService _liveStack;
    private readonly Phd2Service _phd2;

    public CameraController(NinaStateService state, RemoteEventBus eventBus, CameraSelectionService cameraSelection, EquipmentSelectionService equipment, IndiDiscoveryService indi, CaptureStore captures, CameraStreamService stream, FlatWizardService flatWizard, CalibrationBatchService calibration, CalibrationLibrary library, LiveStackService liveStack, Phd2Service phd2)
    {
        _state = state;
        _eventBus = eventBus;
        _cameraSelection = cameraSelection;
        _equipment = equipment;
        _indi = indi;
        _captures = captures;
        _stream = stream;
        _flatWizard = flatWizard;
        _calibration = calibration;
        _library = library;
        _liveStack = liveStack;
        _phd2 = phd2;
    }

    [HttpGet("info")]
    public IActionResult GetInfo()
    {
        var info = _state.CameraInfo;
        if (info == null)
        {
            return Ok(new { connected = false, name = "Not connected" });
        }

        // Capability probes — dew heater support varies widely across INDI drivers
        // (CCD_DEW_CONTROL vs AUX_HEATER_TOGGLE vs ANTI_DEW); the setter tries all
        // three, so "supported" = any of them present.
        var selected = _cameraSelection.GetSelected();
        var indiName = (_cameraSelection.IsConnected && selected?.Provider == CameraProvider.Indi) ? selected.UniqueId : null;
        var capabilities = new
        {
            canSetTemperature = info.CanSetTemperature,
            canAbort = indiName != null && _indi.DeviceHasProperty(indiName, "CCD_ABORT_EXPOSURE"),
            hasDewHeater = indiName != null && _indi.DeviceHasAnyProperty(indiName,
                "CCD_DEW_CONTROL", "AUX_HEATER_TOGGLE", "ANTI_DEW"),
            hasOffset = indiName != null && _indi.DeviceHasProperty(indiName, "CCD_OFFSET"),
            hasGain = indiName != null && _indi.DeviceHasProperty(indiName, "CCD_GAIN")
        };

        return Ok(new
        {
            name = info.Name,
            connected = info.Connected,
            temperature = info.Temperature,
            coolerPower = info.CoolerPower,
            gain = info.Gain,
            offset = info.Offset,
            binning = info.BinX,
            canSetTemperature = info.CanSetTemperature,
            sensorType = info.SensorType.ToString(),
            bitDepth = info.BitDepth,
            pixelSizeX = info.PixelSize,
            pixelSizeY = info.PixelSize,
            resolutionX = info.XSize,
            resolutionY = info.YSize,
            capabilities
        });
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        var info = _state.CameraInfo;
        var (exposureTime, exposureProgress) = _state.GetCameraExposureMetrics();

        if (info == null)
        {
            return Ok(new
            {
                connected = false,
                state = "disconnected",
                exposureTime = (double?)null,
                exposureProgress = (double?)null
            });
        }

        return Ok(new
        {
            // Name exposed here so the iOS CalibrationSheet can show "Library Camera: …"
            // without a separate device-list round-trip.
            name = info.Name,
            connected = info.Connected,
            cooling = info.CoolerOn,
            temperature = info.Temperature,
            targetTemperature = info.TemperatureSetPoint,
            coolerPower = info.CoolerPower,
            state = info.CameraState.ToString(),
            gain = info.Gain,
            offset = info.Offset,
            binning = info.BinX,
            exposureTime,
            exposureProgress
        });
    }

    [HttpGet("latest")]
    public IActionResult GetLatestImage()
    {
        if (_state.LatestImageData == null || _state.LatestImageData.Length == 0)
        {
            return NoContent();
        }

        return File(_state.LatestImageData, "image/png");
    }

    [HttpPost("connect")]
    public async Task<IActionResult> Connect([FromBody] ConnectRequest? request)
    {
        var deviceId = string.IsNullOrWhiteSpace(request?.DeviceId)
            ? CameraSelectionService.SimulatorId
            : request.DeviceId;

        var success = _cameraSelection.Connect(deviceId);
        if (!success)
        {
            return NotFound(new { success = false, message = $"Unknown camera deviceId '{deviceId}'", deviceId });
        }

        var selected = _cameraSelection.GetSelected();

        // For INDI devices, also actuate CONNECTION.CONNECT=On on the driver.
        if (selected?.Provider == CameraProvider.Indi)
        {
            var result = await _indi.ConnectCameraAsync(selected.UniqueId, HttpContext.RequestAborted);
            if (!result.Ok)
            {
                _cameraSelection.Disconnect();
                // Return the real driver reason so the iOS layer can show something useful
                // beyond "connect failed" — typically a line emitted by the INDI driver via
                // a <message> element just before it bailed.
                return StatusCode(503, new
                {
                    success = false,
                    message = result.Reason ?? "INDI driver did not become ready",
                    driverMessages = result.DriverMessages,
                    deviceId
                });
            }
        }

        _state.NotifyStateChanged("camera", _state.BuildCameraStatus());
        _eventBus.Broadcast("EquipmentStatus", _state.BuildEquipmentStatus());

        return Ok(new
        {
            success = true,
            message = "Camera connected",
            deviceId,
            name = selected?.Name
        });
    }

    [HttpPost("disconnect")]
    public async Task<IActionResult> Disconnect()
    {
        var selected = _cameraSelection.GetSelected();
        if (selected?.Provider == CameraProvider.Indi)
        {
            await _indi.DisconnectCameraAsync(selected.UniqueId, HttpContext.RequestAborted);
        }

        _cameraSelection.Disconnect();
        _state.NotifyStateChanged("camera", _state.BuildCameraStatus());
        _eventBus.Broadcast("EquipmentStatus", _state.BuildEquipmentStatus());

        return Ok(new
        {
            success = true,
            message = "Camera disconnected"
        });
    }

    [HttpPost("capture")]
    public async Task<IActionResult> Capture([FromBody] CaptureRequest request)
    {
        if (request.ExposureTime <= 0)
            return BadRequest(new { success = false, message = "ExposureTime must be greater than 0" });

        // Only the INDI path is wired to real hardware — NINA's CameraMediator has no registered
        // handler in this headless build, so Capture there returned Task.CompletedTask and the
        // app only ever saw a transparent placeholder. Route all captures through INDI direct.
        var selected = _equipment.GetSelected(DeviceKind.Camera);
        if (selected?.Provider != EquipmentProvider.Indi || !_equipment.IsConnected(DeviceKind.Camera))
            return StatusCode(503, new { success = false, message = "No INDI camera connected" });

        // INDI drivers (PlayerOne / ZWO / etc.) reject CCD_EXPOSURE while
        // CCD_VIDEO_STREAM is ON — the still capture times out at 30s.
        // We pause the streaming MODE (driver level) for the duration, but
        // CameraStreamService keeps the encoder + WebRTC pipeline alive by
        // re-pushing the last cached frame via its keepalive loop. The
        // viewfinder shows a held-but-not-frozen image; the user sees no
        // disconnect, capture runs at full precision, and the stream
        // resumes with fresh frames as soon as the driver releases the
        // sensor. Best of both worlds without the impossible "physically
        // simultaneous capture + stream" requirement.
        // Pre-capture dither — only when explicitly requested (sequencer
        // sets it for non-first Lights), only when PHD2 is actively
        // Guiding (otherwise the dither command no-ops or errors), and
        // only on Light frames. Dither itself blocks until PHD2 settles
        // so the next exposure starts on a clean star — that's the
        // whole point. Failures are non-fatal: a missed dither just
        // means slight pattern noise on stacked output, which is much
        // less bad than failing the capture.
        if (request.DitherPixels > 0
            && string.Equals(request.ImageType ?? "Light", "Light", StringComparison.OrdinalIgnoreCase)
            && _phd2.CurrentAppState == "Guiding")
        {
            try { await _phd2.DitherAsync(request.DitherPixels, HttpContext.RequestAborted); }
            catch (Exception ex) { _state.NotifyStateChanged("dither", new { failed = true, message = ex.Message }); }
        }

        bool resumeStreamAfter = _stream.IsRunning;
        if (resumeStreamAfter)
        {
            try { await _stream.PauseSensorAsync(HttpContext.RequestAborted); }
            catch { /* hiccup — capture still tries */ }
        }

        try
        {
            // Apply gain/offset before starting the exposure. Silently ignored if the driver
            // doesn't expose CCD_CONTROLS / CCD_GAIN / CCD_OFFSET.
            try { await _indi.SetGainAsync(selected.UniqueId, request.Gain, HttpContext.RequestAborted); } catch { }
            try { await _indi.SetOffsetAsync(selected.UniqueId, request.Offset, HttpContext.RequestAborted); } catch { }
            // Hardware binning destroys Bayer alignment on colour sensors —
            // the camera sums R+G+G+B per output pixel and the mosaic is gone.
            // Force the camera to 1×1 and apply the user's requested binning
            // as a server-side average AFTER debayering. Costs a slightly
            // slower sensor readout but preserves colour at any binning.
            if (request.Binning >= 1)
            {
                try { await _indi.SetBinningAsync(selected.UniqueId, 1, 1, HttpContext.RequestAborted); } catch { }
            }
            // Set CCD_FRAME_TYPE so the driver tags the FITS (and for dark/bias skips shutter
            // mechanics). Defaults to Light when the client doesn't specify.
            var imageType = string.IsNullOrWhiteSpace(request.ImageType) ? "Light" : request.ImageType!;
            await _indi.SetFrameTypeAsync(selected.UniqueId, imageType, HttpContext.RequestAborted);

            _state.MarkExposureStarted(request.ExposureTime);
            var (fitsBytes, _) = await _indi.CameraExposeAsync(selected.UniqueId, request.ExposureTime, HttpContext.RequestAborted);

            byte[] pngBytes;
            try { pngBytes = FitsToPng.Convert(fitsBytes, Math.Max(1, request.Binning)); }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, message = $"Image decode failed: {ex.Message}" });
            }

            // Camera identity — server-authoritative. We pull it from the currently selected
            // INDI device rather than trusting a client-supplied id so Dark/Bias library keys
            // can't be spoofed into the wrong camera's bucket.
            var entry = await _captures.SaveAsync(fitsBytes, pngBytes, new CaptureStore.SaveMetadata(
                ExposureSeconds: request.ExposureTime,
                Gain: request.Gain, Offset: request.Offset, Binning: request.Binning,
                ImageType: imageType, Filter: request.Filter,
                CameraId: selected.UniqueId, CameraName: selected.Name,
                PlanId: request.PlanId, PlanName: request.PlanName));
            // If live stacking is active and this is a Light, feed the frame in. Silent no-op
            // if the user hasn't enabled stacking — we don't want to waste cycles on every
            // capture just in case.
            if (imageType.Equals("Light", StringComparison.OrdinalIgnoreCase) && _liveStack.IsActive)
            {
                // Fire-and-forget — ProcessFrame does FITS parse + star detect + warp
                // (100-300 ms on 9 MP); running it on the response thread serializes back-to-back
                // captures against stacking and pushes the HTTP response out by that much.
                _ = Task.Run(() => _liveStack.ProcessFrame(fitsBytes, selected.UniqueId, request.PlanId, request.ExposureTime, request.Gain, request.Offset, request.Binning, request.Filter));
            }
            _state.LatestImageData = pngBytes;
            _state.NotifyStateChanged("camera", _state.BuildCameraStatus());
            _eventBus.Broadcast("StatusUpdate", new
            {
                timestamp = DateTime.UtcNow,
                camera = _state.BuildCameraStatus(),
                telescope = _state.BuildTelescopeStatus(),
                guider = _state.BuildGuiderStatus()
            });

            return Ok(new
            {
                success = true,
                message = "Capture complete",
                exposureTime = request.ExposureTime,
                gain = request.Gain,
                offset = request.Offset,
                binning = request.Binning,
                filter = request.Filter,
                captureId = entry.Id,
                thumbnailUrl = $"/api/v1/camera/captures/{entry.Id}/thumbnail",
                fullUrl = $"/api/v1/camera/captures/{entry.Id}/full",
                fitsUrl = $"/api/v1/camera/captures/{entry.Id}/fits"
            });
        }
        catch (OperationCanceledException)
        {
            return StatusCode(504, new { success = false, message = "Exposure timed out waiting for image data" });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { success = false, message = ex.Message });
        }
        finally
        {
            _state.MarkExposureFinished();
            if (resumeStreamAfter)
            {
                _ = Task.Run(async () =>
                {
                    try { await _stream.ResumeSensorAsync(CancellationToken.None); } catch { }
                });
            }
        }
    }

    [HttpGet("captures")]
    public IActionResult ListCaptures([FromQuery] int limit = 50)
    {
        var items = _captures.GetAll()
            .OrderByDescending(e => e.Timestamp)
            .Take(Math.Clamp(limit, 1, 200))
            .Select(e => new
            {
                id = e.Id,
                timestamp = e.Timestamp,
                exposureSeconds = e.ExposureSeconds,
                gain = e.Gain,
                offset = e.Offset,
                binning = e.Binning,
                imageType = e.ImageType,
                filter = e.Filter,
                // Carrying these through lets the Capture Library UI group and filter by
                // camera or by plan without a second round-trip.
                cameraId = e.CameraId,
                cameraName = e.CameraName,
                planId = e.PlanId,
                planName = e.PlanName,
                sharedWithPlanIds = e.SharedWithPlanIds,
                fitsSizeBytes = e.FitsSizeBytes,
                fullPngSizeBytes = e.FullPngSizeBytes,
                thumbnailUrl = $"/api/v1/camera/captures/{e.Id}/thumbnail",
                fullUrl = $"/api/v1/camera/captures/{e.Id}/full",
                fitsUrl = $"/api/v1/camera/captures/{e.Id}/fits"
            });
        return Ok(items);
    }

    [HttpGet("captures/{id}/thumbnail")]
    public IActionResult GetCaptureThumbnail(string id)
    {
        var e = _captures.GetById(id);
        if (e == null || !System.IO.File.Exists(e.ThumbnailPath)) return NotFound();
        var mime = e.ThumbnailPath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ? "image/jpeg" : "image/png";
        return PhysicalFile(e.ThumbnailPath, mime);
    }

    [HttpGet("captures/{id}/full")]
    public IActionResult GetCaptureFullPng(string id)
    {
        var e = _captures.GetById(id);
        if (e == null || !System.IO.File.Exists(e.FullPngPath)) return NotFound();
        return PhysicalFile(e.FullPngPath, "image/png");
    }

    [HttpDelete("captures/{id}")]
    public IActionResult DeleteCapture(string id)
    {
        // Look up the entry BEFORE deletion so the calibration library can invalidate the
        // master frame it belonged to. Without this, a cached master stays in memory with a
        // deleted frame still contributing to its mean — rebuilds happen only on next capture.
        var e = _captures.GetById(id);
        if (e == null) return NotFound(new { success = false });
        if (!_captures.DeleteById(id)) return NotFound(new { success = false });
        _library.NotifyCaptureAdded(e); // same invalidation semantics (any group-change wipes the master)
        return Ok(new { success = true });
    }

    [HttpGet("captures/{id}/fits")]
    public IActionResult GetCaptureFits(string id)
    {
        var e = _captures.GetById(id);
        if (e == null || !System.IO.File.Exists(e.FitsPath)) return NotFound();
        return PhysicalFile(e.FitsPath, "application/fits", $"{id}.fits");
    }

    [HttpGet("latest/thumbnail")]
    public IActionResult GetLatestThumbnail()
    {
        var e = _captures.GetLatest();
        if (e == null || !System.IO.File.Exists(e.ThumbnailPath)) return NoContent();
        var mime = e.ThumbnailPath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ? "image/jpeg" : "image/png";
        return PhysicalFile(e.ThumbnailPath, mime);
    }

    [HttpGet("latest/full")]
    public IActionResult GetLatestFullPng()
    {
        var e = _captures.GetLatest();
        if (e == null || !System.IO.File.Exists(e.FullPngPath)) return NoContent();
        return PhysicalFile(e.FullPngPath, "image/png");
    }

    [HttpGet("latest/fits")]
    public IActionResult GetLatestFits()
    {
        var e = _captures.GetLatest();
        if (e == null || !System.IO.File.Exists(e.FitsPath)) return NoContent();
        return PhysicalFile(e.FitsPath, "application/fits", $"{e.Id}.fits");
    }

    // ----- Calibration library -----

    /// <summary>Apply the best matching masters (dark/bias/flat) to a specific Light capture
    /// and return the calibrated PNG. 404 if not a Light, or if no matching masters exist.
    /// Recomputes on every call — masters are cached in memory, so this is ~200 ms for a
    /// typical 9 MP frame, worth it to keep the toggle fully dynamic.</summary>
    [HttpGet("captures/{id}/calibrated")]
    public IActionResult GetCaptureCalibrated(string id)
    {
        var e = _captures.GetById(id);
        if (e == null || !System.IO.File.Exists(e.FitsPath)) return NotFound();
        if (!e.ImageType.Equals("Light", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "Only Light frames can be calibrated" });
        byte[] fitsBytes;
        try { fitsBytes = System.IO.File.ReadAllBytes(e.FitsPath); } catch { return StatusCode(500); }
        var result = _library.TryCalibrate(new CalibrationLibrary.CalibrationInputs(
            fitsBytes, e.CameraId, e.PlanId, e.ExposureSeconds, e.Gain, e.Offset, e.Binning, e.Filter));
        if (result == null) return NotFound(new { error = "No matching calibration masters" });
        Response.Headers["X-Calibration-Dark"] = result.DarkApplied ? result.DarkFrames.ToString() : "0";
        Response.Headers["X-Calibration-Flat"] = result.FlatApplied ? result.FlatFrames.ToString() : "0";
        Response.Headers["X-Calibration-Bias"] = result.BiasApplied ? result.BiasFrames.ToString() : "0";
        return File(result.Png, "image/png");
    }

    [HttpGet("latest/calibrated")]
    public IActionResult GetLatestCalibrated()
    {
        var e = _captures.GetLatest();
        if (e == null) return NoContent();
        if (!e.ImageType.Equals("Light", StringComparison.OrdinalIgnoreCase)) return NoContent();
        return GetCaptureCalibrated(e.Id);
    }

    /// <summary>List calibration groups (darks/bias/flats indexed by their matching key)
    /// along with their frame counts and latest capture times. Backs the iOS library UI.
    ///
    /// Query params (both optional):
    ///   • cameraId — filter to Dark/Bias groups for one specific camera. The Capture Library
    ///     UI uses this to show a per-camera calibration shelf.
    ///   • planId   — filter to Flat groups for one specific plan. The Sequencer's per-plan
    ///     Flats card uses this.
    /// With no filter, returns every group regardless of type.</summary>
    [HttpGet("calibration/library/groups")]
    public IActionResult GetCalibrationGroups([FromQuery] string? cameraId = null, [FromQuery] string? planId = null)
    {
        IEnumerable<CalibrationLibrary.GroupInfo> groups = _library.EnumerateGroups();
        if (!string.IsNullOrWhiteSpace(cameraId))
            groups = groups.Where(g => g.Key.CameraId == cameraId);
        if (!string.IsNullOrWhiteSpace(planId))
            groups = groups.Where(g => g.Key.PlanId == planId);

        var payload = groups.Select(g => new
        {
            imageType = g.Key.ImageType,
            cameraId = g.Key.CameraId,
            cameraName = g.CameraName,
            planId = g.Key.PlanId,
            exposureMs = g.Key.ExposureMs,
            gain = g.Key.Gain,
            offset = g.Key.Offset,
            binning = g.Key.Binning,
            filter = g.Key.Filter,
            label = g.Key.DisplayLabel(),
            frameCount = g.FrameCount,
            latest = g.Latest
        }).ToList();
        return Ok(payload);
    }

    public record DeleteGroupRequest(string ImageType, string? CameraId, string? PlanId, int ExposureMs, int Gain, int Offset, int Binning, string? Filter);

    [HttpDelete("calibration/library/group")]
    public IActionResult DeleteCalibrationGroup([FromBody] DeleteGroupRequest req)
    {
        var key = new CalibrationLibrary.MatchKey(req.ImageType, req.CameraId, req.PlanId, req.ExposureMs, req.Gain, req.Offset, req.Binning, req.Filter);
        var count = _library.DeleteGroup(key);
        return Ok(new { deleted = count });
    }

    // ----- Live stacking -----

    [HttpPost("livestack/start")]
    public IActionResult LiveStackStart() { _liveStack.Start(); return Ok(new { success = true }); }

    [HttpPost("livestack/stop")]
    public IActionResult LiveStackStop() { _liveStack.Stop(); return Ok(new { success = true }); }

    [HttpPost("livestack/reset")]
    public IActionResult LiveStackReset() { _liveStack.Reset(); return Ok(new { success = true }); }

    [HttpGet("livestack/status")]
    public IActionResult LiveStackStatus() => Ok(_liveStack.Snapshot());

    [HttpGet("livestack/preview")]
    public IActionResult LiveStackPreview()
    {
        var png = _liveStack.BuildPreviewPng();
        if (png == null) return NoContent();
        return File(png, "image/png");
    }

    [HttpGet("stream/status")]
    public IActionResult StreamStatus() => Ok(new
    {
        running = _stream.IsRunning,
        clientCount = _stream.ClientCount,
        exposureSeconds = _stream.ExposureSeconds,
        maxFps = _stream.MaxFps,
        lastFps = _stream.LastFps,
        // lastFrameAgeMs grows when the driver stops delivering frames or
        // the server can't keep up. Surfaces real staleness so the iOS HUD
        // can show "STALE" when the camera is wedged instead of a fake fps.
        lastFrameAgeMs = _stream.LastFrameAgeMs,
        droppedFrames = _stream.DroppedFrames
    });

    [HttpPost("stream/start")]
    public async Task<IActionResult> StreamStart([FromBody] StreamConfigRequest? request)
    {
        // Streaming and still capture both drive CCD1. If a still is mid-exposure
        // and we flip CCD_VIDEO_STREAM=ON, the driver either rejects with BUSY or
        // tears the encoder mode and the capture BLOB never arrives cleanly. Refuse
        // up front so the client gets an actionable error instead of a wedge.
        if (_state.IsCaptureInFlight)
        {
            return StatusCode(409, new { success = false, message = "Capture in flight; cannot start stream" });
        }
        var exp = request?.ExposureSeconds ?? _stream.ExposureSeconds;
        var fps = request?.MaxFps ?? _stream.MaxFps;
        _stream.Configure(exp, fps, request?.BinX, request?.BinY, request?.Gain,
            request?.RoiW, request?.RoiH, request?.RoiCx, request?.RoiCy);
        try
        {
            await _stream.StartAsync(HttpContext.RequestAborted);
            return Ok(new { success = true, exposureSeconds = exp, maxFps = fps });
        }
        catch (Exception ex)
        {
            return StatusCode(503, new { success = false, message = ex.Message });
        }
    }

    [HttpPost("stream/stop")]
    public async Task<IActionResult> StreamStop()
    {
        await _stream.StopAsync();
        return Ok(new { success = true });
    }

    [HttpPost("stream/configure")]
    public async Task<IActionResult> StreamConfigure([FromBody] StreamConfigRequest request)
    {
        // ConfigureAndApplyAsync restarts the live stream when ROI / binning
        // changed — INDI drivers won't honor a mid-stream CCD_FRAME update,
        // so the user wouldn't actually see the smaller BLOB without it.
        await _stream.ConfigureAndApplyAsync(
            request.ExposureSeconds, request.MaxFps, request.BinX, request.BinY, request.Gain,
            request.RoiW, request.RoiH, request.RoiCx, request.RoiCy,
            HttpContext.RequestAborted);
        return Ok(new { success = true, exposureSeconds = _stream.ExposureSeconds, maxFps = _stream.MaxFps });
    }

    /// <summary>StreamConfigRequest fields all optional. ROI fields are
    /// fractions of sensor (0..1): RoiW/RoiH are window size, RoiCx/RoiCy
    /// are center position. iOS LiveView translates user gestures to these
    /// before posting; server does the sensor-pixel arithmetic.</summary>
    public record StreamConfigRequest(double ExposureSeconds, double MaxFps,
        int? BinX = null, int? BinY = null, int? Gain = null,
        double? RoiW = null, double? RoiH = null, double? RoiCx = null, double? RoiCy = null);

    // ----- SER/AVI recording (driver-native) -----
    // The INDI driver owns the file — we just flip switches. Default output:
    // ~/BeyondStellar/captures/videos/<session>.ser. Requires native streaming to be active
    // (POST /camera/stream/start) because the recorder taps the stream path.

    public record RecordStartRequest(string? Mode, int? DurationSeconds, int? FrameCount, string? Filename);

    // ----- Flat wizard -----

    public record FlatWizardStartRequest(
        int? TargetAdu, int? Tolerance, int? FramesPerFilter,
        int? Gain, int? Offset, int? Binning,
        bool? UseFlatPanel, int? PanelBrightness,
        double? MinExposure, double? MaxExposure,
        string? PlanId, string? PlanName,
        IReadOnlyList<string>? AdditionalPlanIds);

    [HttpPost("flat-wizard/start")]
    public async Task<IActionResult> FlatWizardStart([FromBody] FlatWizardStartRequest? req)
    {
        // Flats must be attached to a specific imaging plan — that's the whole point of the
        // plan-scoped flat library. Reject requests without one so we don't end up with
        // orphan flats that can't be auto-matched to any future Light.
        if (string.IsNullOrWhiteSpace(req?.PlanId))
        {
            return BadRequest(new { success = false, message = "planId is required — flats attach to a specific imaging plan" });
        }

        try
        {
            // Defaults match NINA's defaults (16-bit 50% saturation target, ±10% tolerance).
            var request = new FlatWizardService.FlatWizardRequest(
                TargetAdu:       req?.TargetAdu ?? 32000,
                Tolerance:       req?.Tolerance ?? 3000,
                FramesPerFilter: req?.FramesPerFilter ?? 20,
                Gain:            req?.Gain ?? 100,
                Offset:          req?.Offset ?? 10,
                Binning:         req?.Binning ?? 1,
                UseFlatPanel:    req?.UseFlatPanel ?? true,
                PanelBrightness: req?.PanelBrightness ?? 128,
                MinExposure:     req?.MinExposure ?? 0.1,
                MaxExposure:     req?.MaxExposure ?? 15.0,
                PlanId:          req!.PlanId,
                PlanName:        req.PlanName,
                AdditionalPlanIds: req.AdditionalPlanIds);
            await _flatWizard.StartAsync(request);
            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    [HttpPost("flat-wizard/continue")]
    public IActionResult FlatWizardContinue()
    {
        _flatWizard.ContinueManual();
        return Ok(new { success = true });
    }

    [HttpPost("flat-wizard/stop")]
    public IActionResult FlatWizardStop()
    {
        _flatWizard.Cancel();
        return Ok(new { success = true });
    }

    [HttpGet("flat-wizard/status")]
    public IActionResult FlatWizardStatus() => Ok(_flatWizard.Snapshot());

    // ----- Calibration batch (Dark / Bias) -----
    //
    // Flat has its own orchestrated wizard (exposure search, per-filter loop). Dark and
    // Bias are simple: capture N frames with the right IMAGETYP. Server-side so the loop
    // survives iOS backgrounding and WiFi blips.

    public record CalibrationStartRequest(
        string? ImageType,
        int? Count,
        double? ExposureTime,
        int? Gain,
        int? Offset,
        int? Binning);

    [HttpPost("calibration/start")]
    public async Task<IActionResult> CalibrationStart([FromBody] CalibrationStartRequest? req)
    {
        try
        {
            var type = (req?.ImageType ?? "Dark").Trim();
            var request = new CalibrationBatchService.CalibrationRequest(
                ImageType:    type,
                Count:        Math.Max(1, req?.Count ?? 20),
                ExposureTime: type.Equals("Bias", StringComparison.OrdinalIgnoreCase)
                                  ? 0.001
                                  : Math.Max(0.001, req?.ExposureTime ?? 30.0),
                Gain:         req?.Gain ?? 100,
                Offset:       req?.Offset ?? 10,
                Binning:      req?.Binning ?? 1);
            await _calibration.StartAsync(request);
            return Ok(new { success = true });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    [HttpPost("calibration/stop")]
    public IActionResult CalibrationStop()
    {
        _calibration.Cancel();
        return Ok(new { success = true });
    }

    [HttpGet("calibration/status")]
    public IActionResult CalibrationStatus() => Ok(_calibration.Snapshot());

    [HttpPost("record/start")]
    public async Task<IActionResult> RecordStart([FromBody] RecordStartRequest? request)
    {
        var selected = _equipment.GetSelected(DeviceKind.Camera);
        if (selected?.Provider != EquipmentProvider.Indi || !_equipment.IsConnected(DeviceKind.Camera))
            return StatusCode(503, new { success = false, message = "No INDI camera connected" });

        // Server-side safety cap: clients can request "manual" but we always
        // route through the duration path with a 60 s ceiling. This keeps a
        // forgotten Stop button from filling the disk with a 300 GB SER —
        // an actual incident on this build (twice). Callers who genuinely
        // want a longer take can pass duration explicitly up to the cap;
        // beyond that the client should compose multiple clips.
        const int kMaxDurationSeconds = 60;
        var requestedMode = (request?.Mode ?? "manual").ToLowerInvariant();
        var requestedDuration = request?.DurationSeconds ?? 0;
        IndiDiscoveryService.RecordMode mode;
        int durationSec;
        if (requestedMode == "frames")
        {
            mode = IndiDiscoveryService.RecordMode.Frames;
            durationSec = 0;
        }
        else
        {
            // Both "manual" (no caller-specified duration) and "duration"
            // collapse to bounded duration mode.
            mode = IndiDiscoveryService.RecordMode.Duration;
            durationSec = requestedDuration > 0
                ? Math.Min(requestedDuration, kMaxDurationSeconds)
                : kMaxDurationSeconds;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dir = Path.Combine(home, "BeyondStellar", "captures", "videos");
        var baseName = string.IsNullOrWhiteSpace(request?.Filename)
            ? "stream_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + "__T_"
            : request.Filename + "_" + "__T_";

        try
        {
            await _indi.StartRecordingAsync(selected.UniqueId, mode,
                durationSec, request?.FrameCount ?? 0,
                dir, baseName, HttpContext.RequestAborted);
            return Ok(new {
                success = true,
                message = "Recording started",
                mode = mode.ToString(),
                durationSeconds = durationSec,
                cappedFromManual = requestedMode == "manual",
                dir,
                filename = baseName
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    [HttpPost("record/stop")]
    public async Task<IActionResult> RecordStop()
    {
        var selected = _equipment.GetSelected(DeviceKind.Camera);
        if (selected?.Provider != EquipmentProvider.Indi || !_equipment.IsConnected(DeviceKind.Camera))
            return Ok(new { success = true, message = "No camera — nothing to stop" });
        await _indi.StopRecordingAsync(selected.UniqueId, HttpContext.RequestAborted);
        return Ok(new { success = true, message = "Recording stopped" });
    }

    [HttpGet("record/status")]
    public IActionResult RecordStatus()
    {
        var selected = _equipment.GetSelected(DeviceKind.Camera);
        if (selected?.Provider != EquipmentProvider.Indi)
            return Ok(new { running = false });
        var s = _indi.GetRecordingStatus(selected.UniqueId);
        // Camera-side BLOB delivery rate — this is the rate the SER file is actually
        // being written at, as opposed to the (capped) ffmpeg/WebRTC transmission fps
        // the viewfinder shows. Surfacing this lets the user judge whether their
        // exposure / gain settings are giving them the planetary fps they want.
        return Ok(new { running = s.running, mode = s.activeSwitch, dir = s.dir, filename = s.filename, fps = _stream.LastFps });
    }

    [HttpPost("abort")]
    public async Task<IActionResult> Abort()
    {
        var selected = _cameraSelection.GetSelected();
        if (_cameraSelection.IsConnected && selected?.Provider == CameraProvider.Indi)
        {
            var ok = await _indi.AbortExposureAsync(selected.UniqueId, HttpContext.RequestAborted);
            return Ok(new { success = ok, message = ok ? "Capture aborted" : "Driver doesn't expose CCD_ABORT_EXPOSURE" });
        }

        _state.CameraMediator.AbortExposure();
        return Ok(new { success = true, message = "Capture aborted" });
    }

    public record DewHeaterRequest(bool Enabled, int? PowerPercent);

    /// <summary>Dew heater control. Driver coverage is patchy (INDI's CCD_DEW_CONTROL,
    /// AUX_HEATER_TOGGLE, or ZWO's ANTI_DEW) — IndiDiscoveryService picks whichever the
    /// active camera exposes. Returns <c>success=false</c> when the driver has none of
    /// them so the UI can surface "not supported" rather than silently pretending.</summary>
    [HttpPost("dew-heater")]
    public async Task<IActionResult> SetDewHeater([FromBody] DewHeaterRequest request)
    {
        var selected = _cameraSelection.GetSelected();
        if (!_cameraSelection.IsConnected || selected?.Provider != CameraProvider.Indi)
            return BadRequest(new { success = false, message = "Camera not connected via INDI" });

        var ok = await _indi.SetDewHeaterAsync(selected.UniqueId, request.Enabled,
            request.PowerPercent, HttpContext.RequestAborted);
        return Ok(new {
            success = ok,
            message = ok ? null : "이 카메라 드라이버는 dew heater 제어를 지원하지 않습니다."
        });
    }

    [HttpPost("cooling")]
    public async Task<IActionResult> SetCooling([FromBody] CoolingRequest request)
    {
        var selected = _cameraSelection.GetSelected();

        // Route to INDI when an INDI camera is active — bypasses the stub mediator path.
        if (_cameraSelection.IsConnected && selected?.Provider == CameraProvider.Indi)
        {
            try
            {
                await _indi.SetCoolingAsync(selected.UniqueId, request.Enabled,
                    request.Enabled ? request.Temperature : null, HttpContext.RequestAborted);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, message = $"INDI cooling failed: {ex.Message}" });
            }

            _state.NotifyStateChanged("camera", _state.BuildCameraStatus());
            return Ok(new
            {
                success = true,
                message = request.Enabled ? $"Cooling enabled, target: {request.Temperature}C" : "Cooling disabled",
                enabled = request.Enabled,
                targetTemperature = request.Temperature
            });
        }

        bool success;
        if (request.Enabled)
        {
            success = await _state.CameraMediator.CoolCamera(
                request.Temperature,
                TimeSpan.FromMinutes(2),
                new Progress<ApplicationStatus>(),
                CancellationToken.None);
        }
        else
        {
            success = await _state.CameraMediator.WarmCamera(
                TimeSpan.FromMinutes(2),
                new Progress<ApplicationStatus>(),
                CancellationToken.None);
        }

        _state.NotifyStateChanged("camera", _state.BuildCameraStatus());

        if (!success)
        {
            return StatusCode(500, new { success = false, message = "Failed to update camera cooling state" });
        }

        return Ok(new
        {
            success = true,
            message = request.Enabled
                ? $"Cooling enabled, target: {request.Temperature}C"
                : "Cooling disabled",
            enabled = request.Enabled,
            targetTemperature = request.Temperature
        });
    }
}
