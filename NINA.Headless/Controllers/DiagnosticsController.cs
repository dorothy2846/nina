using Microsoft.AspNetCore.Mvc;
using NINA.Headless.Services;

namespace NINA.Headless.Controllers;

/// <summary>
/// Driver diagnostics dump. When a user reports "my gear doesn't work quite right",
/// we usually need to see what INDI properties the driver actually publishes — we
/// can't enumerate every vendor's property naming variants in advance, so capability
/// probes miss occasionally. This endpoint returns a snapshot of every connected
/// device's property map so the user can attach it to a GitHub issue.
///
/// Shape is intentionally verbose (one row per property + elements) rather than
/// compact JSON, so a copy-pasted gist is human-readable.
/// </summary>
[ApiController]
[Route("api/v1/[controller]")]
public class DiagnosticsController : ControllerBase
{
    private readonly IndiDiscoveryService _indi;
    private readonly EquipmentSelectionService _equipment;
    private readonly CameraStreamService _stream;
    private readonly RingBufferLog _recentLog;
    private readonly IndiServerManager _indiServer;
    private readonly SequenceExecutionService _sequence;
    private readonly NINA.Headless.Services.Remote.PairedDeviceStore _paired;

    public DiagnosticsController(
        IndiDiscoveryService indi,
        EquipmentSelectionService equipment,
        CameraStreamService stream,
        RingBufferLog recentLog,
        IndiServerManager indiServer,
        SequenceExecutionService sequence,
        NINA.Headless.Services.Remote.PairedDeviceStore paired)
    {
        _indi = indi;
        _equipment = equipment;
        _stream = stream;
        _recentLog = recentLog;
        _indiServer = indiServer;
        _sequence = sequence;
        _paired = paired;
    }

    private bool IsAuthorized()
    {
        var auth = Request.Headers.Authorization.ToString();
        var token = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..].Trim() : "";
        return _paired.Verify(token) != null;
    }

    /// <summary>One-click support bundle: versions, uptime, recent log lines,
    /// driver/equipment state, stream + sequence snapshots. Returned as a
    /// plain-text attachment the app hands to the iOS share sheet — logs can
    /// carry paths/SSIDs, so this requires the paired-device bearer token.</summary>
    [HttpGet("bundle")]
    public IActionResult Bundle()
    {
        if (!IsAuthorized()) return Unauthorized();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== Astella diagnostics bundle ===");
        sb.AppendLine($"generatedAt: {DateTime.UtcNow:O}");
        sb.AppendLine($"version: {typeof(Program).Assembly.GetName().Version}");
        sb.AppendLine($"os: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        sb.AppendLine($"uptime: {DateTime.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime():g}");
        sb.AppendLine();

        sb.AppendLine("=== INDI ===");
        sb.AppendLine($"connected: {_indi.Client != null}");
        sb.AppendLine($"runningDrivers: {string.Join(' ', _indiServer.RunningDrivers)}");
        foreach (var d in _indi.Devices)
            sb.AppendLine($"device: {d.Name}");
        sb.AppendLine();

        sb.AppendLine("=== Stream ===");
        sb.AppendLine($"running: {_stream.IsRunning}");
        sb.AppendLine();

        var seq = _sequence.GetSnapshot();
        sb.AppendLine("=== Sequence ===");
        sb.AppendLine($"running: {seq.Running} plan: {seq.PlanName} target: {seq.CurrentTarget}");
        sb.AppendLine();

        sb.AppendLine($"=== Recent log (last {RingBufferLog.Capacity} lines max) ===");
        foreach (var line in _recentLog.Snapshot())
            sb.AppendLine(line);

        return File(
            System.Text.Encoding.UTF8.GetBytes(sb.ToString()),
            "text/plain",
            $"astella-diagnostics-{DateTime.UtcNow:yyyyMMdd-HHmmss}.txt");
    }

    /// <summary>Per-frame stage timings for the streaming pipeline. Returns
    /// the last 60 frames' (T_blob → T_processed → T_pushed_to_ffmpeg) plus
    /// derived deltas. Used to localize end-to-end latency:
    ///   capture+debayer = T_processed - T_blob
    ///   queue           = T_pushed - T_processed
    /// The remaining latency (encoder + WebRTC + iOS) is computed iOS-side
    /// from the burned-in server wall-clock vs iOS wall-clock at render time.
    /// </summary>
    [HttpGet("streamLatency")]
    public IActionResult StreamLatency()
    {
        var now = DateTime.UtcNow;
        var samples = _stream.RecentFrameTimings.Select(t => new
        {
            frame = t.FrameNumber,
            blobReceivedUtc = t.BlobReceived,
            ageMs = (int)(now - t.BlobReceived).TotalMilliseconds,
            captureMs = (int)(t.ProcessingDone - t.BlobReceived).TotalMilliseconds,
            queueMs = (int)(t.PushedToFfmpeg - t.ProcessingDone).TotalMilliseconds,
            encodeMs = t.EncoderEmitted.HasValue
                ? (int)(t.EncoderEmitted.Value - t.PushedToFfmpeg).TotalMilliseconds
                : (int?)null,
        }).ToArray();

        var captureAvg = samples.Length > 0 ? samples.Average(s => s.captureMs) : 0;
        var queueAvg = samples.Length > 0 ? samples.Average(s => s.queueMs) : 0;
        var encodedSamples = samples.Where(s => s.encodeMs.HasValue).ToArray();
        var encodeAvg = encodedSamples.Length > 0 ? encodedSamples.Average(s => s.encodeMs!.Value) : 0;

        // Server-side cumulative latency (capture → IVF emit). The remaining
        // budget (network + jitter buffer + decode + render) is iOS-side and
        // visible in the WebRTC stats HUD on the client.
        var serverCumulativeAvgMs = (int)(captureAvg + queueAvg + encodeAvg);

        return Ok(new
        {
            generatedAtUtc = now,
            count = samples.Length,
            avgCaptureMs = (int)captureAvg,
            avgQueueMs = (int)queueAvg,
            avgEncodeMs = (int)encodeAvg,
            serverCumulativeAvgMs,
            samples
        });
    }

    /// <summary>Full INDI property dump plus what our capability probes see. Not
    /// authenticated at the controller level — piggybacks on the same bearer-token
    /// gate the rest of the admin-ish endpoints use once we wire it in middleware.</summary>
    [HttpGet]
    public IActionResult Get()
    {
        var client = _indi.Client;
        if (client == null)
            return Ok(new { indiConnected = false, devices = Array.Empty<object>() });

        var devices = client.Devices
            .Select(d => BuildDeviceSnapshot(d.Name))
            .Where(d => d != null)
            .ToArray();

        return Ok(new
        {
            indiConnected = true,
            generatedAt = DateTime.UtcNow,
            devices
        });
    }

    private object? BuildDeviceSnapshot(string name)
    {
        var client = _indi.Client;
        var dev = client?.GetDevice(name);
        if (dev == null) return null;

        var properties = dev.Properties.Values
            .OrderBy(p => p.Name)
            .Select(p => new
            {
                name = p.Name,
                type = p.Type.ToString(),
                state = p.State.ToString(),
                perm = p.Perm.ToString(),
                elements = p.Elements.Values
                    .OrderBy(e => e.Name)
                    .Select(e => new { e.Name, e.Value })
                    .ToArray()
            })
            .ToArray();

        // Include the subset of our capability probes' verdict so readers can see
        // "we probed X and saw false" in one place alongside the raw property list.
        var probeVerdict = new
        {
            // Probe names here mirror the ones used in each controller's /info
            // capability block — this is deliberately duplicated rather than imported
            // from the controllers so the diagnostic format stays stable when we add
            // new probes.
            hasCcdDewControl = dev.Properties.ContainsKey("CCD_DEW_CONTROL"),
            hasAuxHeaterToggle = dev.Properties.ContainsKey("AUX_HEATER_TOGGLE"),
            hasAntiDew = dev.Properties.ContainsKey("ANTI_DEW"),
            hasCcdAbortExposure = dev.Properties.ContainsKey("CCD_ABORT_EXPOSURE"),
            hasFocusTempComp = dev.Properties.ContainsKey("FOCUS_TEMPERATURE_COMPENSATION"),
            hasAutoFocusComp = dev.Properties.ContainsKey("AUTO_FOCUS_COMP"),
            hasFocusBacklashSteps = dev.Properties.ContainsKey("FOCUS_BACKLASH_STEPS"),
            hasTelescopeTrackMode = dev.Properties.ContainsKey("TELESCOPE_TRACK_MODE"),
            hasFlatLightControl = dev.Properties.ContainsKey("FLAT_LIGHT_CONTROL"),
            hasFlatLightIntensity = dev.Properties.ContainsKey("FLAT_LIGHT_INTENSITY")
        };

        return new
        {
            name,
            driverInterface = dev.DriverInterface,
            connected = dev.IsConnected,
            isCamera = dev.IsCamera,
            isTelescope = dev.IsTelescope,
            isFocuser = dev.IsFocuser,
            isFilterWheel = dev.IsFilterWheel,
            isSwitch = dev.IsSwitch,
            isWeather = dev.IsWeather,
            propertyCount = dev.Properties.Count,
            properties,
            probeVerdict
        };
    }
}
