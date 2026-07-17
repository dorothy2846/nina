using Microsoft.AspNetCore.Mvc;
using NINA.Headless.Services;

namespace NINA.Headless.Controllers;

/// <summary>One-tap planetary (lucky-imaging) capture: apply ROI + exposure/gain
/// to the running stream, then start a driver-native SER burst. Built entirely on
/// the verified stream-configure and RECORD_STREAM primitives — the previous
/// version of this controller was a mock that reported success without touching
/// the camera. Stop ends the recording and restores the full sensor frame so
/// subsequent stills aren't silently cropped to the planetary ROI.</summary>
[ApiController]
[Route("api/v1/camera/planetary-record")]
public class PlanetaryRecordController : ControllerBase
{
    private readonly CameraStreamService _stream;
    private readonly EquipmentSelectionService _equipment;
    private readonly IndiDiscoveryService _indi;

    public PlanetaryRecordController(CameraStreamService stream, EquipmentSelectionService equipment, IndiDiscoveryService indi)
    {
        _stream = stream;
        _equipment = equipment;
        _indi = indi;
    }

    /// <summary>Fractional ROI (0..1) like stream/configure; frameCount for a
    /// lucky-imaging burst (preferred) or durationSeconds for a timed clip.</summary>
    public record PlanetaryStartRequest(
        double? RoiW, double? RoiH, double? RoiCx, double? RoiCy,
        double? ExposureSeconds, int? Gain,
        int? FrameCount, int? DurationSeconds, string? Filename);

    [HttpPost("start")]
    public async Task<IActionResult> Start([FromBody] PlanetaryStartRequest? request)
    {
        var selected = _equipment.GetSelected(DeviceKind.Camera);
        if (selected?.Provider != EquipmentProvider.Indi || !_equipment.IsConnected(DeviceKind.Camera))
            return StatusCode(503, new { success = false, message = "No INDI camera connected" });

        // The recorder taps the native stream — make sure it's up.
        try { await _stream.StartAsync(HttpContext.RequestAborted); }
        catch (Exception ex) { return StatusCode(503, new { success = false, message = $"Stream start failed: {ex.Message}" }); }

        // ROI + exposure via the same path the LiveView slider uses (OFF/ON
        // sensor cycle inside). Defaults: 50% center crop, 10ms exposure —
        // sane planetary starting points when the caller sends nothing.
        var roiW = Math.Clamp(request?.RoiW ?? 0.5, 0.05, 1.0);
        var roiH = Math.Clamp(request?.RoiH ?? 0.5, 0.05, 1.0);
        await _stream.ConfigureAndApplyAsync(
            request?.ExposureSeconds ?? 0.01, 0 /* maxFps: driver-limited */,
            null, null, request?.Gain,
            roiW, roiH, request?.RoiCx ?? 0.5, request?.RoiCy ?? 0.5,
            HttpContext.RequestAborted);

        // ROI applies via a debounced OFF/ON sensor cycle — wait until the
        // driver actually reports the requested frame before rolling, or the
        // first frames of the burst land at the wrong resolution (or during
        // the cycle's stream gap).
        var size = _indi.GetSensorSize(selected.UniqueId);
        if (size.HasValue && (roiW < 0.999 || roiH < 0.999))
        {
            var expectW = Math.Max(8, (int)(size.Value.width * roiW)) & ~7;
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < deadline)
            {
                var frame = _indi.GetCcdFrame(selected.UniqueId);
                if (frame.HasValue && Math.Abs(frame.Value.width - expectW) <= 8) break;
                await Task.Delay(250, HttpContext.RequestAborted);
            }
        }

        // Burst: frames mode (exact SER frame count) unless a duration was asked.
        const int kMaxFrames = 20_000;
        const int kMaxDurationSeconds = 60;
        IndiDiscoveryService.RecordMode mode;
        int durationSec = 0, frameCount = 0;
        if (request?.DurationSeconds is > 0)
        {
            mode = IndiDiscoveryService.RecordMode.Duration;
            durationSec = Math.Min(request.DurationSeconds.Value, kMaxDurationSeconds);
        }
        else
        {
            mode = IndiDiscoveryService.RecordMode.Frames;
            frameCount = Math.Clamp(request?.FrameCount ?? 1000, 1, kMaxFrames);
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dir = Path.Combine(home, "BeyondStellar", "captures", "videos");
        var baseName = (string.IsNullOrWhiteSpace(request?.Filename) ? "planetary" : request.Filename)
                       + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + "__T_";

        try
        {
            await _indi.StartRecordingAsync(selected.UniqueId, mode, durationSec, frameCount,
                dir, baseName, HttpContext.RequestAborted);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { success = false, message = ex.Message });
        }

        return Ok(new
        {
            success = true,
            message = "Planetary recording started",
            roiW, roiH,
            exposureSeconds = _stream.ExposureSeconds,
            mode = mode.ToString(),
            frameCount,
            durationSeconds = durationSec,
            dir,
            filename = baseName
        });
    }

    [HttpPost("stop")]
    public async Task<IActionResult> Stop()
    {
        var selected = _equipment.GetSelected(DeviceKind.Camera);
        if (selected?.Provider != EquipmentProvider.Indi || !_equipment.IsConnected(DeviceKind.Camera))
            return Ok(new { success = true, message = "No camera — nothing to stop" });

        try { await _indi.StopRecordingAsync(selected.UniqueId, HttpContext.RequestAborted); }
        catch (Exception ex) { return StatusCode(500, new { success = false, message = ex.Message }); }

        // Restore the full sensor so the next still capture isn't a silent
        // 320x256 crop — the exact failure mode the mock left behind.
        await _stream.ConfigureAndApplyAsync(
            _stream.ExposureSeconds, 0, null, null, null,
            1.0, 1.0, 0.5, 0.5, HttpContext.RequestAborted);

        return Ok(new { success = true, message = "Planetary recording stopped; full frame restored" });
    }
}
