// CameraController — streaming endpoints: live video stream start/stop/configure/status
// plus driver-native SER/AVI recording (start/stop/status).
// Partial of CameraController; fields and constructor live in CameraController.cs.

using Microsoft.AspNetCore.Mvc;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using NINA.Headless.Models;
using NINA.Headless.Services;
using NINA.Headless.Services.Remote;

namespace NINA.Headless.Controllers;

public partial class CameraController
{
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
        droppedFrames = _stream.DroppedFrames,
        // Encoder health: running=true with transcoderRunning=false means the
        // pipeline is broken mid-recovery; a climbing restart count means the
        // watchdog is fighting a flapping ffmpeg. Both are conditions the
        // client should see, not conditions to paper over.
        transcoderRunning = _h264.IsRunning,
        transcoderRestarts = _h264.RestartCount
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
        // Exposure / gain / offset are pushed live to the running stream;
        // ROI / binning trigger a brief sensor-side OFF/ON dance because
        // they change sensor topology.
        await _stream.ConfigureAndApplyAsync(
            request.ExposureSeconds, request.MaxFps, request.BinX, request.BinY, request.Gain,
            request.RoiW, request.RoiH, request.RoiCx, request.RoiCy,
            HttpContext.RequestAborted, request.Offset);
        return Ok(new { success = true, exposureSeconds = _stream.ExposureSeconds, maxFps = _stream.MaxFps });
    }

    /// <summary>StreamConfigRequest fields all optional. ROI fields are
    /// fractions of sensor (0..1): RoiW/RoiH are window size, RoiCx/RoiCy
    /// are center position. iOS LiveView translates user gestures to these
    /// before posting; server does the sensor-pixel arithmetic.</summary>
    public record StreamConfigRequest(double ExposureSeconds, double MaxFps,
        int? BinX = null, int? BinY = null, int? Gain = null,
        double? RoiW = null, double? RoiH = null, double? RoiCx = null, double? RoiCy = null,
        int? Offset = null);

    // ----- SER/AVI recording (driver-native) -----
    // The INDI driver owns the file — we just flip switches. Default output:
    // ~/BeyondStellar/captures/videos/<session>.ser. Requires native streaming to be active
    // (POST /camera/stream/start) because the recorder taps the stream path.

    public record RecordStartRequest(string? Mode, int? DurationSeconds, int? FrameCount, string? Filename);

    public record AutoExposeRequest(double? TargetFill, double? Tolerance);

    /// <summary>Converge stream exposure until the frame's robust peak
    /// brightness (99.99th percentile of the driver's 8-bit stream) sits at
    /// the target fill. Measures the STREAM domain — exactly what the
    /// preview shows — not absolute 16-bit ADU. Multiplicative convergence
    /// with clipping backoff; reports honestly when it can't converge
    /// (bounds hit, no frames) instead of pretending success.</summary>
    /// <summary>Current stream brightness stats — the auto-exposure control
    /// inputs, exposed for UI display and diagnostics.</summary>
    [HttpGet("stream/brightness")]
    public IActionResult StreamBrightness()
    {
        var b = _stream.Brightness;
        if (b == null) return Ok(new { available = false });
        return Ok(new
        {
            available = true,
            peakFill = b.PeakFill,
            robustPeakFill = b.RobustPeakFill,
            p999Fill = b.P999Fill,
            meanFill = b.MeanFill,
            sampledAt = b.SampledAt,
            frameNumber = b.FrameNumber,
        });
    }

    /// <summary>Verification endpoint: run the EXACT production brightness
    /// measurement on an uploaded JPEG. Lets synthetic frames (gray cards,
    /// planet disks, hot-pixel fields) validate the auto-exposure metric
    /// without a camera.</summary>
    [HttpPost("stream/brightness/test")]
    public async Task<IActionResult> BrightnessTest()
    {
        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms, HttpContext.RequestAborted);
        var stats = CameraStreamService.ComputeBrightness(ms.ToArray(), DateTime.UtcNow, -1);
        if (stats == null) return BadRequest(new { success = false, message = "not a decodable image" });
        return Ok(new { peakFill = stats.PeakFill, robustPeakFill = stats.RobustPeakFill,
                        p999Fill = stats.P999Fill, meanFill = stats.MeanFill });
    }

    [HttpPost("stream/auto-expose")]
    public async Task<IActionResult> AutoExpose([FromBody] AutoExposeRequest? request)
    {
        if (!_stream.IsRunning)
            return StatusCode(503, new { success = false, message = "Stream not running — start the live stream first" });

        var ct = HttpContext.RequestAborted;
        var target = Math.Clamp(request?.TargetFill ?? 0.75, 0.2, 0.95);
        var tol = Math.Clamp(request?.Tolerance ?? 0.05, 0.01, 0.2);
        const double minExp = 0.0002, maxExp = 0.5;

        var exposure = _stream.ExposureSeconds;
        double lastFill = -1;
        for (var iteration = 1; iteration <= 12; iteration++)
        {
            var stats = await WaitFreshBrightnessAsync(exposure, ct);
            if (stats == null)
                return StatusCode(503, new { success = false, message = "No fresh frames from the stream (4s timeout)" });

            lastFill = stats.RobustPeakFill;
            if (Math.Abs(lastFill - target) <= tol)
                return Ok(new { success = true, converged = true, exposureSeconds = exposure,
                                achievedFill = lastFill, iterations = iteration });

            double factor;
            if (stats.PeakFill >= 0.995 && lastFill >= 0.90)
                factor = 0.5;                                   // clipped — back off hard
            else
                factor = target / Math.Max(0.02, lastFill);     // linear sensor assumption
            factor = Math.Clamp(factor, 0.25, 4.0);

            var next = Math.Clamp(exposure * factor, minExp, maxExp);
            if (Math.Abs(next - exposure) / exposure < 0.02)
                return Ok(new { success = false, converged = false, exposureSeconds = exposure,
                                achievedFill = lastFill, iterations = iteration,
                                message = next >= maxExp * 0.98
                                    ? "노출 상한(0.5s)에서도 목표 밝기에 못 미칩니다 — 게인을 올리세요"
                                    : "노출 하한에서도 목표보다 밝습니다 — 게인을 낮추거나 필터를 쓰세요" });
            exposure = next;
            await _stream.ConfigureAndApplyAsync(exposure, 0, null, null, null, null, null, null, null, ct);
        }
        return Ok(new { success = false, converged = false, exposureSeconds = exposure,
                        achievedFill = lastFill, iterations = 12,
                        message = "12회 반복에도 수렴하지 않았습니다 (변광/시상 요동 가능)" });
    }

    /// <summary>Wait for a brightness sample taken AFTER the current exposure
    /// had time to reach the sensor (debounce + ~2 frame periods), so the
    /// loop never reasons about stale frames. Null on 4 s timeout.</summary>
    private async Task<CameraStreamService.BrightnessStats?> WaitFreshBrightnessAsync(double exposureSeconds, CancellationToken ct)
    {
        // 300 ms apply debounce + two frame periods at the new exposure.
        var settle = TimeSpan.FromMilliseconds(300 + Math.Max(100, exposureSeconds * 2000));
        var freshAfter = DateTime.UtcNow + settle;
        var deadline = DateTime.UtcNow + settle + TimeSpan.FromSeconds(4);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var stats = _stream.Brightness;
            if (stats != null && stats.SampledAt >= freshAfter) return stats;
            await Task.Delay(150, ct);
        }
        return null;
    }

    [HttpPost("record/start")]
    public async Task<IActionResult> RecordStart([FromBody] RecordStartRequest? request)
    {
        var selected = _equipment.GetSelected(DeviceKind.Camera);
        if (selected?.Provider != EquipmentProvider.Indi || !_equipment.IsConnected(DeviceKind.Camera))
            return StatusCode(503, new { success = false, message = "No INDI camera connected" });

        // Server-side safety cap: clients can request "manual" but we always
        // route through the duration path with a ceiling. This keeps a
        // forgotten Stop button from filling the disk with a 300 GB SER —
        // an actual incident on this build (twice). 300 s covers the longest
        // rotation-safe planetary clip (Saturn/Moon presets); beyond that
        // the client should compose multiple clips.
        const int kMaxDurationSeconds = 300;
        var requestedMode = (request?.Mode ?? "manual").ToLowerInvariant();
        var requestedDuration = request?.DurationSeconds ?? 0;
        IndiDiscoveryService.RecordMode mode;
        int durationSec;
        if (requestedMode == "frames")
        {
            // Frames mode is the lucky-imaging path — bursts are legitimately
            // large, but an unbounded count is the same fill-the-disk footgun
            // the duration cap exists for. 20k frames ≈ minutes of planetary
            // capture; beyond that, compose multiple takes.
            const int kMaxFrames = 20_000;
            if (request?.FrameCount is not (> 0 and <= kMaxFrames))
                return BadRequest(new { success = false, message = $"frameCount must be 1..{kMaxFrames}" });
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

    // ----- Recorded-file management -----
    //
    // SER takes are gigabytes each and land on the server disk; without a
    // listing/delete surface they accumulate invisibly until the disk fills.
    // Download is deliberately absent — multi-GB transfers to a phone make no
    // sense; takes are pulled off the box over SMB/USB for stacking on a PC.

    private static string VideosDir
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        "BeyondStellar", "captures", "videos");

    [HttpGet("record/files")]
    public IActionResult RecordFiles()
    {
        var dir = VideosDir;
        var files = Directory.Exists(dir)
            ? new DirectoryInfo(dir).GetFiles()
                .Where(f => f.Extension is ".ser" or ".avi")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => new
                {
                    name = f.Name,
                    sizeBytes = f.Length,
                    modifiedAt = f.LastWriteTimeUtc,
                })
                .ToArray()
            : Array.Empty<object>();

        long freeBytes = 0;
        try { freeBytes = new DriveInfo(Path.GetPathRoot(dir) ?? "/").AvailableFreeSpace; }
        catch { /* free-space is advisory */ }

        return Ok(new { files, freeBytes, dir });
    }

    public record DeleteRecordFileRequest(string? Name);

    [HttpPost("record/files/delete")]
    public IActionResult DeleteRecordFile([FromBody] DeleteRecordFileRequest? request)
    {
        var name = request?.Name;
        if (string.IsNullOrWhiteSpace(name))
            return BadRequest(new { success = false, message = "name is required" });
        // The name must be a bare file inside VideosDir — no separators, no
        // traversal, recording extensions only.
        if (name.Contains('/') || name.Contains('\\') || name.Contains("..") ||
            !(name.EndsWith(".ser", StringComparison.OrdinalIgnoreCase) ||
              name.EndsWith(".avi", StringComparison.OrdinalIgnoreCase)))
            return BadRequest(new { success = false, message = $"Invalid file name '{name}'" });

        var recording = _equipment.GetSelected(DeviceKind.Camera) is { Provider: EquipmentProvider.Indi } sel
                        && _indi.GetRecordingStatus(sel.UniqueId).running;
        if (recording)
            return Conflict(new { success = false, message = "Recording in progress — stop it before deleting files" });

        var path = Path.Combine(VideosDir, name);
        if (!System.IO.File.Exists(path))
            return NotFound(new { success = false, message = $"'{name}' not found" });

        System.IO.File.Delete(path);
        return Ok(new { success = true, message = $"'{name}' deleted" });
    }

    [HttpGet("record/status")]
    public IActionResult RecordStatus()
    {
        var selected = _equipment.GetSelected(DeviceKind.Camera);
        if (selected?.Provider != EquipmentProvider.Indi)
            return Ok(new { running = false });
        var s = _indi.GetRecordingStatus(selected.UniqueId);
        // fps = the driver's own capture-rate estimate (FPS.EST_FPS) — the rate
        // frames actually hit the SER file. The BLOB rate we observe client-side
        // is preview-capped by LIMITS_PREVIEW_FPS (hard-lowered to 5 during
        // recording) and would badly under-report planetary capture rates.
        return Ok(new
        {
            running = s.running,
            mode = s.activeSwitch,
            dir = s.dir,
            filename = s.filename,
            fps = s.captureFps ?? _stream.LastFps,
            previewFps = _stream.LastFps
        });
    }
}
