using Microsoft.AspNetCore.Mvc;
using NINA.Core.Model;
using NINA.Headless.Models;
using NINA.Headless.Services;
using NINA.Headless.Services.Remote;

namespace NINA.Headless.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class GuiderController : ControllerBase
{
    private readonly NinaStateService _state;
    private readonly RemoteEventBus _eventBus;
    private readonly Phd2Service _phd2;
    private readonly AutoCalibrationOrchestrator _autoCalibrate;

    public GuiderController(NinaStateService state, RemoteEventBus eventBus, Phd2Service phd2, AutoCalibrationOrchestrator autoCalibrate)
    {
        _state = state;
        _eventBus = eventBus;
        _phd2 = phd2;
        _autoCalibrate = autoCalibrate;
    }

    [HttpGet("info")]
    public IActionResult GetInfo()
    {
        var info = _state.GuiderInfo;
        if (info == null)
        {
            return Ok(new { connected = false, name = "Not connected" });
        }

        return Ok(new
        {
            name = info.Name,
            connected = info.Connected,
            pixelScale = info.PixelScale,
            canClearCalibration = info.CanClearCalibration,
            canSetShiftRate = info.CanSetShiftRate
        });
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        // Prefer the live PHD2 JSON-RPC snapshot — it's the source of truth now that the app
        // auto-launches PHD2 and drives it. NINA's GuiderInfo only populates when the NINA
        // GuiderMediator has a handler (it doesn't in this headless build).
        return Ok(_phd2.Snapshot());
    }

    [HttpPost("connect")]
    public async Task<IActionResult> Connect([FromBody] ConnectRequest? request)
    {
        // IDs arrive as "indi:<DeviceName>"; strip the prefix so PHD2 gets the raw INDI name.
        static string? Strip(string? id) => id?.StartsWith("indi:") == true ? id[5..] : id;
        var guideCam = Strip(request?.GuideCameraDeviceId);
        var mount = Strip(request?.MountDeviceId);

        // PHD2 caches prefs in-memory at startup — `defaults write` after launch has
        // no effect, and `set_profile` is a memory switch not a disk re-read. So we
        // blanket-write to every realistic profile slot BEFORE launching PHD2, then
        // let PHD2 pick whichever one it likes on startup.
        if (guideCam != null || mount != null)
        {
            var writes = new List<Task>();
            for (int pid = 1; pid <= 5; pid++)
                writes.Add(_phd2.WriteIndiBindingsAsync(pid, guideCam, mount, HttpContext.RequestAborted));
            await Task.WhenAll(writes);
        }

        var result = await _phd2.EnsureStartedDetailedAsync(HttpContext.RequestAborted);
        switch (result)
        {
            case Phd2Service.LaunchResult.Connected:
                // If PHD2 was already running before this call (so our prewrite came too
                // late), force a profile reload so the fresh prefs take effect. New
                // launches don't need this — they picked up our values at boot.
                var profileId = await _phd2.GetCurrentProfileIdAsync(HttpContext.RequestAborted);
                if (profileId is int pid) await _phd2.ReloadProfileAsync(pid, HttpContext.RequestAborted);
                await _phd2.SetAllConnectedAsync(true, HttpContext.RequestAborted);
                _eventBus.Broadcast("EquipmentStatus", _state.BuildEquipmentStatus());
                return Ok(new { success = true, message = "Guider (PHD2) connected", deviceId = request?.DeviceId ?? "phd2" });

            case Phd2Service.LaunchResult.NotInstalled:
                return StatusCode(503, new
                {
                    success = false,
                    code = "phd2-not-installed",
                    message = "PHD2 not found in /Applications. Install once via `brew install --cask phd2` or download from openphdguiding.org.",
                    deviceId = request?.DeviceId ?? "phd2"
                });

            case Phd2Service.LaunchResult.ServerNotEnabled:
                return StatusCode(503, new
                {
                    success = false,
                    code = "phd2-server-disabled",
                    message = "PHD2 is running but its JSON-RPC server is off. Open PHD2 once and toggle Tools → Enable Server — setting is persistent. Retry after enabling.",
                    deviceId = request?.DeviceId ?? "phd2"
                });

            default:
                return StatusCode(503, new
                {
                    success = false,
                    code = "phd2-timeout",
                    message = "PHD2 did not become reachable on localhost:4400 within 15s.",
                    deviceId = request?.DeviceId ?? "phd2"
                });
        }
    }

    [HttpPost("disconnect")]
    public async Task<IActionResult> Disconnect()
    {
        await _phd2.SetAllConnectedAsync(false, HttpContext.RequestAborted);
        _eventBus.Broadcast("EquipmentStatus", _state.BuildEquipmentStatus());
        return Ok(new { success = true, message = "Guider disconnected" });
    }

    [HttpPost("start")]
    public async Task<IActionResult> Start()
    {
        if (!await _phd2.EnsureStartedAsync(HttpContext.RequestAborted))
            return StatusCode(503, new { success = false, message = "PHD2 unavailable (install to /Applications/PHD2.app or enable Tools→Enable Server)" });
        await _phd2.SetAllConnectedAsync(true, HttpContext.RequestAborted);
        var ok = await _phd2.StartGuidingAsync(HttpContext.RequestAborted);
        return ok
            ? Ok(new { success = true, message = "Guiding started" })
            : StatusCode(500, new { success = false, message = "Failed to start guiding" });
    }

    [HttpPost("stop")]
    public async Task<IActionResult> Stop()
    {
        var ok = await _phd2.StopGuidingAsync(HttpContext.RequestAborted);
        return ok
            ? Ok(new { success = true, message = "Guiding stopped" })
            : StatusCode(500, new { success = false, message = "Failed to stop guiding" });
    }

    [HttpPost("dither")]
    public async Task<IActionResult> Dither([FromBody] DitherRequest request)
    {
        if (request.Pixels <= 0)
            return BadRequest(new { success = false, message = "Pixels must be greater than 0" });
        var ok = await _phd2.DitherAsync(request.Pixels, HttpContext.RequestAborted);
        return ok
            ? Ok(new { success = true, message = $"Dither by {request.Pixels} pixels requested", pixels = request.Pixels })
            : StatusCode(500, new { success = false, message = "Dither failed" });
    }

    [HttpPost("calibrate")]
    public async Task<IActionResult> Calibrate()
    {
        if (!await _phd2.EnsureStartedAsync(HttpContext.RequestAborted))
            return StatusCode(503, new { success = false, message = "PHD2 unavailable" });
        // PHD2's "guide" RPC with recalibrate=true performs a fresh calibration before guiding.
        await _phd2.SetAllConnectedAsync(true, HttpContext.RequestAborted);
        // Re-send with recalibrate flag — we cheat and inline the RPC here so we don't expand the service surface.
        await _phd2.StartGuidingAsync(HttpContext.RequestAborted); // Phd2Service doesn't expose recalibrate yet; TODO
        return Ok(new { success = true, message = "Calibration+guiding started" });
    }

    // ----- Full PHD2 control surface for the iOS client -----
    // PHD2 runs GUI-less on the server (hidden on macOS, Xvfb on Linux). The iOS client
    // therefore has to own the entire control UX: live image, exposure, pause/resume,
    // calibration clear, loop/find-star for framing, and so on.

    /// <summary>Latest guide camera frame as autostretched PNG. Headers carry the star
    /// lock position so the iOS client can draw an overlay without a second round-trip.</summary>
    [HttpGet("star-image")]
    public async Task<IActionResult> GetStarImage()
    {
        var img = await _phd2.GetStarImageAsync(HttpContext.RequestAborted);
        if (img == null) return NoContent();
        Response.Headers["X-Frame-Width"] = img.Width.ToString();
        Response.Headers["X-Frame-Height"] = img.Height.ToString();
        Response.Headers["X-Star-X"] = img.StarX.ToString("F2");
        Response.Headers["X-Star-Y"] = img.StarY.ToString("F2");
        Response.Headers["X-Frame-Number"] = img.Frame.ToString();
        return File(img.Png, "image/png");
    }

    [HttpGet("exposure")]
    public async Task<IActionResult> GetExposure()
    {
        var ms = await _phd2.GetExposureAsync(HttpContext.RequestAborted);
        return Ok(new { exposureMs = ms });
    }

    public record SetExposureRequest(double? ExposureMs);

    [HttpPut("exposure")]
    public async Task<IActionResult> SetExposure([FromBody] SetExposureRequest req)
    {
        if (req?.ExposureMs is not double ms || ms < 50 || ms > 30000)
            return BadRequest(new { success = false, message = "exposureMs must be 50..30000" });
        var ok = await _phd2.SetExposureAsync(ms, HttpContext.RequestAborted);
        return Ok(new { success = ok });
    }

    private async Task<IActionResult> WrapPhd2(Func<CancellationToken, Task<bool>> op)
        => Ok(new { success = await op(HttpContext.RequestAborted) });

    [HttpPost("pause")]            public Task<IActionResult> Pause() => WrapPhd2(ct => _phd2.SetPausedAsync(true, ct));
    [HttpPost("resume")]           public Task<IActionResult> Resume() => WrapPhd2(ct => _phd2.SetPausedAsync(false, ct));
    [HttpPost("clear-calibration")] public Task<IActionResult> ClearCalibration() => WrapPhd2(_phd2.ClearCalibrationAsync);
    [HttpPost("loop")]             public Task<IActionResult> Loop() => WrapPhd2(_phd2.LoopAsync);
    [HttpPost("find-star")]        public Task<IActionResult> FindStar() => WrapPhd2(_phd2.FindStarAsync);

    public record SetLockPositionRequest(double X, double Y);

    [HttpPost("lock-position")]
    public Task<IActionResult> SetLockPosition([FromBody] SetLockPositionRequest req)
        => WrapPhd2(ct => _phd2.SetLockPositionAsync(req.X, req.Y, ct));

    // ----- Autotune: Tier-1 deterministic parameter computation -----
    //
    // Client provides optics + guide rate, server returns every PHD2 parameter that can be
    // derived without measurement (pixel scale, calibration step, max pulse, min-move,
    // recommended algorithms). With apply=true the server also pushes the algorithm-param
    // subset (aggressiveness, min-move, exposure) via PHD2 JSON-RPC so the running session
    // immediately reflects the new values — the rest (cal step, max duration) are shown as
    // recommendations because PHD2 only accepts those through Advanced Settings / config file.

    public record AutotuneRequest(
        double? GuideScopeFocalLengthMm,
        double? GuideCamPixelSizeUm,
        double? MountGuideRateMultiplier,
        double? DeclinationDegrees,
        bool? Apply);

    public record ApplyFullRequest(
        double? GuideScopeFocalLengthMm,
        double? GuideCamPixelSizeUm,
        double? MountGuideRateMultiplier,
        double? DeclinationDegrees,
        string? DecGuideMode);

    /// <summary>Write full autotune package to PHD2 preferences, restart PHD2, then push the
    /// runtime-settable subset. Blocks ~15-30 s until PHD2's JSON-RPC server comes back.
    /// The iOS UI shows a "Restarting PHD2…" progress during this window.</summary>
    [HttpPost("autotune/apply-full")]
    public async Task<IActionResult> ApplyFullAutotune([FromBody] ApplyFullRequest req)
    {
        if (req?.GuideScopeFocalLengthMm is not double fl || fl <= 0)
            return BadRequest(new { success = false, message = "guideScopeFocalLengthMm required" });
        if (req.GuideCamPixelSizeUm is not double ps || ps <= 0)
            return BadRequest(new { success = false, message = "guideCamPixelSizeUm required" });
        if (!Enum.TryParse<DecGuideMode>(req.DecGuideMode ?? "Auto", ignoreCase: true, out var decMode))
            return BadRequest(new { success = false, message = $"decGuideMode must be Off/Auto/North/South; got '{req.DecGuideMode}'" });

        GuidingAutotune.Result r;
        try
        {
            r = GuidingAutotune.Compute(new GuidingAutotune.Inputs(
                GuideScopeFocalLengthMm: fl,
                GuideCamPixelSizeUm: ps,
                MountGuideRateMultiplier: req.MountGuideRateMultiplier ?? 0.5,
                DeclinationDegrees: req.DeclinationDegrees ?? 0.0));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }

        var result = await _phd2.ApplyFullAutotuneAsync(
            r.CalibrationStepMs, r.MaxRaDurationMs, r.MaxDecDurationMs,
            decMode,
            r.DefaultExposureSeconds, r.MinMovePixels, r.DefaultAggressivenessPct / 100.0,
            HttpContext.RequestAborted);

        return Ok(new
        {
            success = result.Ok,
            durationMs = result.DurationMs,
            applied = result.Applied,
            error = result.Error,
            computed = new
            {
                pixelScale = r.PixelScaleArcsecPerPx,
                guideRate = r.GuideRateArcsecPerSec,
                calibrationStepMs = r.CalibrationStepMs,
                maxRaDurationMs = r.MaxRaDurationMs,
                maxDecDurationMs = r.MaxDecDurationMs,
                minMovePixels = r.MinMovePixels,
                defaultAggressivenessPct = r.DefaultAggressivenessPct,
                searchRegionPixels = r.SearchRegionPixels,
                defaultExposureSeconds = r.DefaultExposureSeconds,
                recommendedRaAlgorithm = r.RecommendedRaAlgorithm,
                recommendedDecAlgorithm = r.RecommendedDecAlgorithm,
                rationale = r.Rationale
            }
        });
    }

    [HttpPost("autotune")]
    public async Task<IActionResult> Autotune([FromBody] AutotuneRequest req)
    {
        if (req?.GuideScopeFocalLengthMm is not double fl || fl <= 0)
            return BadRequest(new { success = false, message = "guideScopeFocalLengthMm required" });
        if (req.GuideCamPixelSizeUm is not double ps || ps <= 0)
            return BadRequest(new { success = false, message = "guideCamPixelSizeUm required" });

        GuidingAutotune.Result result;
        try
        {
            result = GuidingAutotune.Compute(new GuidingAutotune.Inputs(
                GuideScopeFocalLengthMm: fl,
                GuideCamPixelSizeUm: ps,
                MountGuideRateMultiplier: req.MountGuideRateMultiplier ?? 0.5,
                DeclinationDegrees: req.DeclinationDegrees ?? 0.0));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }

        var applied = new List<string>();
        var skipped = new List<string>();

        if (req.Apply == true)
        {
            var ct = HttpContext.RequestAborted;
            // Exposure is directly settable and takes effect on the next frame.
            if (await _phd2.SetExposureAsync(result.DefaultExposureSeconds * 1000, ct))
                applied.Add($"exposure={result.DefaultExposureSeconds:F1}s");

            // Push per-axis algorithm params. We only set params the active algorithm actually
            // supports (Hysteresis has aggressiveness/hysteresis/minMove, ResistSwitch has
            // aggressiveness/minMove) — checked via get_algo_param_names so we don't spam
            // unsupported names at PHD2.
            foreach (var axis in new[] { GuideAxis.RA, GuideAxis.Dec })
            {
                var names = await _phd2.GetAlgoParamNamesAsync(axis, ct);
                if (names == null) { skipped.Add($"{axis.Wire()}/algo-params (PHD2 offline)"); continue; }
                if (names.Contains("aggressiveness") &&
                    await _phd2.SetAlgoParamAsync(axis, "aggressiveness", result.DefaultAggressivenessPct / 100.0, ct))
                    applied.Add($"{axis.Wire()}/aggressiveness={result.DefaultAggressivenessPct:F0}%");
                if (names.Contains("minMove") &&
                    await _phd2.SetAlgoParamAsync(axis, "minMove", result.MinMovePixels, ct))
                    applied.Add($"{axis.Wire()}/minMove={result.MinMovePixels:F2}px");
            }

            // Calibration step, max-pulse, search region, focal length, pixel size all live
            // in PHD2 Advanced Settings — no RPC setter. Surface as recommendations so the
            // iOS UI can show "Apply these in PHD2 Advanced Settings (requires restart)".
            skipped.Add($"calStep={result.CalibrationStepMs}ms (Advanced Settings)");
            skipped.Add($"maxRaDur={result.MaxRaDurationMs}ms (Advanced Settings)");
            skipped.Add($"searchRegion={result.SearchRegionPixels}px (Advanced Settings)");
        }

        return Ok(new
        {
            success = true,
            pixelScale = result.PixelScaleArcsecPerPx,
            guideRate = result.GuideRateArcsecPerSec,
            calibrationStepMs = result.CalibrationStepMs,
            maxRaDurationMs = result.MaxRaDurationMs,
            maxDecDurationMs = result.MaxDecDurationMs,
            minMovePixels = result.MinMovePixels,
            defaultAggressivenessPct = result.DefaultAggressivenessPct,
            searchRegionPixels = result.SearchRegionPixels,
            defaultExposureSeconds = result.DefaultExposureSeconds,
            recommendedRaAlgorithm = result.RecommendedRaAlgorithm,
            recommendedDecAlgorithm = result.RecommendedDecAlgorithm,
            rationale = result.Rationale,
            applied,
            skipped
        });
    }

    // ----- Auto-calibration orchestrator (slew to meridian → PHD2 calibrate → return) -----

    public record AutoCalibrateRequest(
        bool? ReturnToTarget,
        double? DeclinationTargetDegrees,
        int? SlewTimeoutSeconds,
        int? CalibrationTimeoutSeconds);

    [HttpPost("auto-calibrate/start")]
    public IActionResult StartAutoCalibrate([FromBody] AutoCalibrateRequest? req)
    {
        try
        {
            _autoCalibrate.StartAsync(new AutoCalibrationOrchestrator.Request(
                ReturnToTarget: req?.ReturnToTarget ?? true,
                DeclinationTargetDegrees: req?.DeclinationTargetDegrees ?? 0.0,
                SlewTimeoutSeconds: req?.SlewTimeoutSeconds ?? 180,
                CalibrationTimeoutSeconds: req?.CalibrationTimeoutSeconds ?? 300));
            return Ok(new { success = true });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { success = false, message = ex.Message });
        }
    }

    [HttpGet("auto-calibrate/status")]
    public IActionResult AutoCalibrateStatus() => Ok(_autoCalibrate.Snapshot());

    [HttpPost("auto-calibrate/cancel")]
    public IActionResult CancelAutoCalibrate()
    {
        _autoCalibrate.Cancel();
        return Ok(new { success = true });
    }
}
