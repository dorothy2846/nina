using Microsoft.AspNetCore.Mvc;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Headless.Models;
using NINA.Headless.Services;

namespace NINA.Headless.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class EquipmentController : ControllerBase
{
    private readonly NinaStateService _state;
    private readonly ISafetyMonitorMediator _safety;
    private readonly EquipmentSelectionService _equipment;
    private readonly IndiDiscoveryService _indi;
    private readonly IndiServerManager _indiServer;
    private readonly Phd2Service _phd2;
    private readonly IndiDriverWatchdog _watchdog;

    public EquipmentController(
        NinaStateService state,
        ISafetyMonitorMediator safety,
        EquipmentSelectionService equipment,
        IndiDiscoveryService indi,
        IndiServerManager indiServer,
        Phd2Service phd2,
        IndiDriverWatchdog watchdog)
    {
        _state = state;
        _safety = safety;
        _equipment = equipment;
        _indi = indi;
        _indiServer = indiServer;
        _phd2 = phd2;
        _watchdog = watchdog;
    }

    /// <summary>Diagnostics: recent driver-hang history per device. Lets
    /// the iOS app render a "hardware health" summary so the user can
    /// inspect frequency / pattern of failures before a long capture.</summary>
    [HttpGet("driverHealth")]
    public IActionResult DriverHealth()
    {
        var now = DateTime.UtcNow;
        var devices = _watchdog.RecentHangs.Select(kv => new
        {
            key = kv.Key,
            count = kv.Value.Count,
            lastHangAt = kv.Value.Count > 0 ? (DateTime?)kv.Value[^1] : null,
            ageSecondsSinceLast = kv.Value.Count > 0 ? (int)(now - kv.Value[^1]).TotalSeconds : -1,
        }).ToArray();
        return Ok(new { generatedAt = now, devices });
    }

    private static object[] AsList(IEnumerable<EquipmentDescriptor> devs)
        => devs.Select(d => (object)new { id = d.Id, name = d.Name }).ToArray();

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        return Ok(new
        {
            camera = _state.BuildCameraStatus(),  // fed by IndiToMediatorBridge's 1 Hz camera broadcast
            telescope = StatusOf(DeviceKind.Telescope, () => _indi.BuildTelescopeStatus(_equipment.GetSelected(DeviceKind.Telescope)!.UniqueId)),
            focuser = StatusOf(DeviceKind.Focuser, () => _indi.BuildFocuserStatus(_equipment.GetSelected(DeviceKind.Focuser)!.UniqueId)),
            filterWheel = StatusOf(DeviceKind.FilterWheel, () => _indi.BuildFilterWheelStatus(_equipment.GetSelected(DeviceKind.FilterWheel)!.UniqueId)),
            rotator = StatusOf(DeviceKind.Rotator, () => null),
            dome = StatusOf(DeviceKind.Dome, () => null),
            // Guider state comes from Phd2Service (live JSON-RPC socket), NOT the NINA
            // GuiderMediator — we bypass NINA's guider orchestration and drive PHD2
            // directly, so BuildGuiderStatus never sees "connected" and iOS's
            // card would stay stuck on "Connecting…" forever while polling /equipment/status.
            guider = _phd2.Snapshot(),
            flatDevice = StatusOf(DeviceKind.FlatPanel, () => null),
            safetyMonitor = BuildSafetyMonitorStatus(),
            weather = BuildWeatherStatus(),
            @switch = BuildSwitchStatus()
        });
    }

    private object BuildSafetyMonitorStatus()
    {
        var info = _safety.GetInfo();
        if (info == null || !info.Connected) return new { connected = false, name = "Not connected", isSafe = (bool?)null };
        return new { connected = true, name = info.Name, isSafe = info.IsSafe };
    }

    private object BuildSwitchStatus()
    {
        if (!_equipment.IsConnected(DeviceKind.Switch)) return new { connected = false, name = "Not connected" };
        var sel = _equipment.GetSelected(DeviceKind.Switch);
        return sel == null ? new { connected = false, name = "Not connected" } : _indi.BuildSwitchStatus(sel);
    }

    private object BuildWeatherStatus()
    {
        if (!_equipment.IsConnected(DeviceKind.Weather)) return new { connected = false, name = "Not connected" };
        var sel = _equipment.GetSelected(DeviceKind.Weather);
        return sel == null ? new { connected = false, name = "Not connected" } : _indi.BuildWeatherStatus(sel);
    }

    /// <summary>Build per-device status: empty/disconnected unless EquipmentSelectionService says it's connected.</summary>
    private object StatusOf(DeviceKind kind, Func<object?> indiBuilder)
    {
        if (!_equipment.IsConnected(kind)) return new { connected = false, name = "Not connected" };
        var selected = _equipment.GetSelected(kind);
        if (selected?.Provider == EquipmentProvider.Indi)
        {
            return indiBuilder() ?? new { connected = true, name = selected.Name };
        }
        return new { connected = true, name = selected?.Name ?? "Unknown" };
    }

    [HttpGet("devices")]
    public IActionResult GetDevices()
    {
        return Ok(new
        {
            cameras = AsList(_equipment.GetAvailable(DeviceKind.Camera)),
            telescopes = AsList(_equipment.GetAvailable(DeviceKind.Telescope)),
            focusers = AsList(_equipment.GetAvailable(DeviceKind.Focuser)),
            filterWheels = AsList(_equipment.GetAvailable(DeviceKind.FilterWheel)),
            rotators = AsList(_equipment.GetAvailable(DeviceKind.Rotator)),
            domes = AsList(_equipment.GetAvailable(DeviceKind.Dome)),
            guiders = new[]
            {
                new { id = "phd2", name = "PHD2" }
            },
            flatDevices = AsList(_equipment.GetAvailable(DeviceKind.FlatPanel)),
            safetyMonitors = AsList(_equipment.GetAvailable(DeviceKind.SafetyMonitor)),
            weather = AsList(_equipment.GetAvailable(DeviceKind.Weather)),
            switches = AsList(_equipment.GetAvailable(DeviceKind.Switch))
        });
    }

    // ----- Device rescan / INDI server restart -----
    //
    // Two escalating levels for "my camera/scope isn't showing up":
    //   • /rescan — drops the INDI client and lets the background loop reconnect. Rebuilds
    //     the device list from what the driver currently publishes. Handles ~90% of the
    //     "plugged in after the app started" cases.
    //   • /indi/restart — kills and relaunches the whole indiserver child process. Use when
    //     a driver is wedged and the rescan didn't help.

    [HttpPost("rescan")]
    public async Task<IActionResult> Rescan()
    {
        await _indi.RequestRescanAsync(HttpContext.RequestAborted);
        return Ok(new { success = true, message = "INDI client reconnecting — device list will refresh in ~3s" });
    }

    [HttpPost("indi/restart")]
    public async Task<IActionResult> RestartIndiServer()
    {
        try
        {
            _indiServer.RestartServer();
            // Also force the discovery client to reconnect — otherwise it would hold onto
            // a stale connection pointing at the old process socket.
            await _indi.RequestRescanAsync(HttpContext.RequestAborted);
            return Ok(new { success = true, message = "indiserver restarted — drivers reloaded" });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    public record RestartDriverRequest(string? DeviceId, string? DriverName);

    /// <summary>Restart a single driver child process. This is the per-device version of the
    /// nuclear /indi/restart: only the named driver dies and respawns, every other driver
    /// (and any operation they're running — slewing, guiding, exposing on another camera)
    /// is untouched.
    ///
    /// Accepts either:
    ///   • deviceId — server looks up DRIVER_INFO.DRIVER_EXEC for that device.
    ///   • driverName — raw driver binary (e.g. "indi_asi_ccd"). Fallback for when the
    ///     device isn't currently published (the driver is wedged and never got that far).</summary>
    [HttpPost("driver/restart")]
    public async Task<IActionResult> RestartDriver([FromBody] RestartDriverRequest? req)
    {
        string? driverName = req?.DriverName;
        if (string.IsNullOrWhiteSpace(driverName) && !string.IsNullOrWhiteSpace(req?.DeviceId))
        {
            // deviceId may be "indi:Player One CCD Uranus-C PRO" (our app-side id) or the
            // raw INDI device name. Strip the "indi:" prefix if present.
            var devName = req!.DeviceId!.StartsWith("indi:", StringComparison.Ordinal)
                ? req.DeviceId.Substring(5)
                : req.DeviceId;
            driverName = _indi.GetDriverExec(devName);
        }

        if (string.IsNullOrWhiteSpace(driverName))
        {
            return BadRequest(new { success = false, message = "Could not resolve driver name from request — send either driverName directly or a deviceId for a currently-published device" });
        }

        try
        {
            await _indiServer.RestartDriverAsync(driverName, HttpContext.RequestAborted);
            return Ok(new { success = true, message = $"Driver {driverName} restart queued", driverName });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }
}
