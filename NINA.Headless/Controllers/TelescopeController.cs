using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using NINA.Astrometry;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Equipment.Interfaces;
using NINA.Headless.Models;
using NINA.Headless.Hubs;
using NINA.Headless.Services;

namespace NINA.Headless.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class TelescopeController : ControllerBase
{
    private readonly NinaStateService _state;
    private readonly IHubContext<NinaHub> _hub;
    private readonly EquipmentSelectionService _equipment;
    private readonly IndiDiscoveryService _indi;

    public TelescopeController(NinaStateService state, IHubContext<NinaHub> hub,
        EquipmentSelectionService equipment, IndiDiscoveryService indi)
    {
        _state = state;
        _hub = hub;
        _equipment = equipment;
        _indi = indi;
    }

    [HttpGet("info")]
    public IActionResult GetInfo()
    {
        var info = _state.TelescopeInfo;
        if (info == null)
        {
            return Ok(new { connected = false, name = "Not connected" });
        }

        // TELESCOPE_TRACK_MODE is INDI-only and probed directly since push-to mounts
        // omit it; the rest come from NINA's driver adapter.
        var selected = _equipment.GetSelected(DeviceKind.Telescope);
        var indiName = (_equipment.IsConnected(DeviceKind.Telescope) && selected?.Provider == EquipmentProvider.Indi) ? selected.UniqueId : null;
        var capabilities = new
        {
            canSlew = info.CanSlew,
            canSlewAltAz = info.CanSlewAltAz,
            canPark = info.CanPark,
            canFindHome = info.CanFindHome,
            canSetTracking = info.CanSetTrackingEnabled,
            canSetTrackRate = indiName != null && _indi.DeviceHasProperty(indiName, "TELESCOPE_TRACK_MODE"),
            canPulseGuide = info.CanPulseGuide,
            canSync = true  // INDI ON_COORD_SET exposes SYNC on every goto mount; no runtime flag.
        };

        return Ok(new
        {
            name = info.Name,
            connected = info.Connected,
            ra = info.RightAscension,
            dec = info.Declination,
            alt = info.Altitude,
            az = info.Azimuth,
            tracking = info.TrackingEnabled,
            parked = info.AtPark,
            canSlew = info.CanSlew,
            canPark = info.CanPark,
            canSetTracking = info.CanSetTrackingEnabled,
            siderealTime = info.SiderealTime,
            siteLatitude = info.SiteLatitude,
            siteLongitude = info.SiteLongitude,
            siteElevation = info.SiteElevation,
            alignmentMode = info.AlignmentMode.ToString(),
            capabilities
        });
    }

    [HttpGet("debug")]
    public IActionResult Debug()
    {
        var selected = _equipment.GetSelected(DeviceKind.Telescope);
        var uniqueId = selected?.UniqueId;
        var isConnEq = _equipment.IsConnected(DeviceKind.Telescope);
        var indi = _indi.Client;
        var dev = uniqueId != null ? indi?.GetDevice(uniqueId) : null;
        string? connVal = null;
        string? disconnVal = null;
        string? propState = null;
        if (dev != null && dev.Properties.TryGetValue("CONNECTION", out var prop))
        {
            connVal = prop["CONNECT"]?.Value;
            disconnVal = prop["DISCONNECT"]?.Value;
            propState = prop.State.ToString();
        }
        var allProps = dev?.Properties.Keys.OrderBy(k => k).ToArray();
        return Ok(new
        {
            selectedId = selected?.Id,
            selectedUniqueId = uniqueId,
            equipmentSaysConnected = isConnEq,
            indiClientAlive = indi?.IsConnected,
            deviceFoundInClient = dev != null,
            deviceIsConnected = dev?.IsConnected,
            rawConnectValue = connVal,
            rawDisconnectValue = disconnVal,
            connectionState = propState,
            allPropertyNames = allProps,
            propertyCount = allProps?.Length
        });
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        // Prefer INDI when an INDI telescope is selected and connected.
        var selected = _equipment.GetSelected(DeviceKind.Telescope);
        if (_equipment.IsConnected(DeviceKind.Telescope) && selected?.Provider == EquipmentProvider.Indi)
        {
            var s = _indi.BuildTelescopeStatus(selected.UniqueId);
            if (s != null) return Ok(s);
        }

        return Ok(new { connected = false, name = "Not connected" });
    }

    [HttpPost("connect")]
    public IActionResult Connect([FromBody] ConnectRequest? request)
    {
        var deviceId = request?.DeviceId;
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return BadRequest(new { success = false, message = "deviceId required" });
        }

        if (!_equipment.Connect(DeviceKind.Telescope, deviceId))
        {
            return NotFound(new { success = false, message = $"Unknown telescope deviceId '{deviceId}'", deviceId });
        }

        var selected = _equipment.GetSelected(DeviceKind.Telescope);
        if (selected?.Provider == EquipmentProvider.Indi)
        {
            // Record the user's intent and let the coalescing worker drive the driver toward
            // it. Rapid toggles are collapsed by the worker (reads current intent at every
            // step), so no background queue builds up.
            _indi.RequestConnectionIntent(selected.UniqueId, connected: true);
        }

        return Accepted(new { success = true, message = "Connect requested; poll /status to observe connected state", deviceId, name = selected?.Name });
    }

    [HttpPost("disconnect")]
    public IActionResult Disconnect()
    {
        var selected = _equipment.GetSelected(DeviceKind.Telescope);
        if (selected?.Provider == EquipmentProvider.Indi)
        {
            _indi.RequestConnectionIntent(selected.UniqueId, connected: false);
        }
        return Accepted(new { success = true, message = "Disconnect requested" });
    }

    [HttpPost("slew")]
    public async Task<IActionResult> Slew([FromBody] SlewRequest request)
    {
        if (request.RA < 0 || request.RA >= 360)
            return BadRequest(new { success = false, message = "RA must be between 0 and 360 degrees" });
        if (request.Dec < -90 || request.Dec > 90)
            return BadRequest(new { success = false, message = "Dec must be between -90 and 90 degrees" });

        var selected = _equipment.GetSelected(DeviceKind.Telescope);
        if (!_equipment.IsConnected(DeviceKind.Telescope) || selected?.Provider != EquipmentProvider.Indi)
            return StatusCode(503, new { success = false, message = "Telescope not connected" });
        if (_indi.IsTelescopeParked(selected.UniqueId))
            return StatusCode(409, new { success = false, message = "Mount is parked. Unpark first." });

        // API passes RA in degrees; INDI expects hours.
        await _indi.TelescopeSlewAsync(selected.UniqueId, request.RA / 15.0, request.Dec, HttpContext.RequestAborted);
        return Ok(new { success = true, message = $"Slewing to RA={request.RA:F4}, Dec={request.Dec:F4}", ra = request.RA, dec = request.Dec });
    }

    [HttpPost("slewAltAz")]
    public async Task<IActionResult> SlewAltAz([FromBody] SlewAltAzRequest request)
    {
        if (request.Alt < 0 || request.Alt > 90)
        {
            return BadRequest(new { success = false, message = "Alt must be between 0 and 90 degrees" });
        }
        if (request.Az < 0 || request.Az >= 360)
        {
            return BadRequest(new { success = false, message = "Az must be between 0 and 360 degrees" });
        }

        var info = _state.TelescopeInfo;
        var topocentric = new TopocentricCoordinates(
            Angle.ByDegree(request.Az),
            Angle.ByDegree(request.Alt),
            Angle.ByDegree(info?.SiteLatitude ?? 0),
            Angle.ByDegree(info?.SiteLongitude ?? 0),
            info?.SiteElevation ?? 0);

        var success = await _state.TelescopeMediator.SlewToTopocentricCoordinates(topocentric, CancellationToken.None);
        _state.NotifyStateChanged("telescope", _state.BuildTelescopeStatus());

        if (!success)
        {
            return StatusCode(500, new { success = false, message = "Telescope alt/az slew failed" });
        }

        return Ok(new
        {
            success = true,
            message = $"Slewing to Alt={request.Alt:F2}, Az={request.Az:F2}",
            alt = request.Alt,
            az = request.Az
        });
    }

    [HttpPost("park")]
    public async Task<IActionResult> Park()
    {
        var selected = _equipment.GetSelected(DeviceKind.Telescope);
        if (!_equipment.IsConnected(DeviceKind.Telescope) || selected?.Provider != EquipmentProvider.Indi)
            return StatusCode(503, new { success = false, message = "Telescope not connected" });
        await _indi.TelescopeParkAsync(selected.UniqueId, true, HttpContext.RequestAborted);
        return Ok(new { success = true, message = "Park requested" });
    }

    [HttpPost("unpark")]
    public async Task<IActionResult> Unpark()
    {
        var selected = _equipment.GetSelected(DeviceKind.Telescope);
        if (_equipment.IsConnected(DeviceKind.Telescope) && selected?.Provider == EquipmentProvider.Indi)
        {
            await _indi.TelescopeParkAsync(selected.UniqueId, false, HttpContext.RequestAborted);
            return Ok(new { success = true, message = "Unpark requested" });
        }

        var success = await _state.TelescopeMediator.UnparkTelescope(new Progress<ApplicationStatus>(), CancellationToken.None);
        _state.NotifyStateChanged("telescope", _state.BuildTelescopeStatus());

        if (!success)
        {
            return StatusCode(500, new { success = false, message = "Failed to unpark telescope" });
        }

        return Ok(new
        {
            success = true,
            message = "Unpark requested"
        });
    }

    [HttpPost("tracking")]
    public async Task<IActionResult> SetTracking([FromBody] TrackingRequest request)
    {
        var selected = _equipment.GetSelected(DeviceKind.Telescope);
        if (!_equipment.IsConnected(DeviceKind.Telescope) || selected?.Provider != EquipmentProvider.Indi)
            return StatusCode(503, new { success = false, message = "Telescope not connected" });
        await _indi.TelescopeTrackingAsync(selected.UniqueId, request.Enabled, HttpContext.RequestAborted);
        return Ok(new { success = true, message = request.Enabled ? "Tracking enabled" : "Tracking disabled", enabled = request.Enabled });
    }

    public record TrackRateRequest(string Rate);

    /// <summary>Pick the tracking rate used when tracking is on. Values: sidereal (default for
    /// deep-sky), solar, lunar, custom. Drivers vary; push-to mounts may omit the vector.</summary>
    [HttpPost("trackRate")]
    public async Task<IActionResult> SetTrackRate([FromBody] TrackRateRequest request)
    {
        var selected = _equipment.GetSelected(DeviceKind.Telescope);
        if (!_equipment.IsConnected(DeviceKind.Telescope) || selected?.Provider != EquipmentProvider.Indi)
            return StatusCode(503, new { success = false, message = "Telescope not connected" });
        var ok = await _indi.TelescopeTrackRateAsync(selected.UniqueId, request.Rate ?? "sidereal", HttpContext.RequestAborted);
        return Ok(new {
            success = ok,
            rate = request.Rate,
            message = ok ? null : "이 마운트 드라이버는 tracking rate 선택을 지원하지 않습니다."
        });
    }

    public record SyncRequest(double RA, double Dec);

    /// <summary>Sync the mount's internal position to the given RA/Dec. Used after a plate-solve
    /// to tell the mount "you are actually here" without moving. Follow-on slews then calibrate
    /// from the corrected origin. Coordinate convention matches /slew: RA in degrees (0-360),
    /// Dec in degrees (-90..+90).</summary>
    [HttpPost("sync")]
    public async Task<IActionResult> Sync([FromBody] SyncRequest request)
    {
        if (request.RA < 0 || request.RA >= 360)
            return BadRequest(new { success = false, message = "RA must be between 0 and 360 degrees" });
        if (request.Dec < -90 || request.Dec > 90)
            return BadRequest(new { success = false, message = "Dec must be between -90 and 90 degrees" });

        var selected = _equipment.GetSelected(DeviceKind.Telescope);
        if (!_equipment.IsConnected(DeviceKind.Telescope) || selected?.Provider != EquipmentProvider.Indi)
            return StatusCode(503, new { success = false, message = "Telescope not connected" });
        if (_indi.IsTelescopeParked(selected.UniqueId))
            return StatusCode(409, new { success = false, message = "Mount is parked. Unpark first." });

        await _indi.TelescopeSyncAsync(selected.UniqueId, request.RA / 15.0, request.Dec, HttpContext.RequestAborted);
        return Ok(new { success = true, message = $"Synced to RA={request.RA:F4}, Dec={request.Dec:F4}" });
    }

    [HttpPost("move")]
    public async Task<IActionResult> Move([FromBody] MoveRequest request)
    {
        // Compound codes drive both axes at once (ASIAIR-style diagonal pad).
        var validDirections = new[] { "N", "S", "E", "W", "NE", "NW", "SE", "SW" };
        var dir = request.Direction.ToUpperInvariant();
        if (!validDirections.Contains(dir))
            return BadRequest(new { success = false, message = "Direction must be one of N, S, E, W, NE, NW, SE, SW" });

        var selected = _equipment.GetSelected(DeviceKind.Telescope);
        if (!_equipment.IsConnected(DeviceKind.Telescope) || selected?.Provider != EquipmentProvider.Indi)
            return StatusCode(503, new { success = false, message = "Telescope not connected" });
        if (_indi.IsTelescopeParked(selected.UniqueId))
            return StatusCode(409, new { success = false, message = "Mount is parked. Unpark first." });

        var dirName = dir switch
        {
            "N" => "north", "S" => "south", "E" => "east", "W" => "west",
            "NE" => "northeast", "NW" => "northwest", "SE" => "southeast", "SW" => "southwest",
            _ => "north"
        };

        // Interpret `rate` as a sidereal multiplier (1x = sidereal tracking speed). Values ≤ 1
        // or absent fall back to the driver's current selection (usually max). The driver's
        // TELESCOPE_SLEW_RATE OneOfMany switch caps at whatever the manufacturer exposes
        // (AM5 = 1–10); anything higher clamps to the max element.
        double? multiplier = request.Rate >= 1.0 ? request.Rate : null;
        await _indi.TelescopeMoveAsync(selected.UniqueId, dirName, multiplier, HttpContext.RequestAborted, request.RateName, request.SiderealRate);

        // If a duration is supplied (tap-to-nudge), sleep then stop. Otherwise keep moving until an
        // explicit /stopMove call.
        var duration = request.Duration > 0 ? request.Duration : 0;
        if (duration > 0)
        {
            await Task.Delay(duration);
            await _indi.TelescopeStopMoveAsync(selected.UniqueId, HttpContext.RequestAborted);
        }
        return Ok(new { success = true, message = $"Moving {dir} for {duration}ms", direction = dir, duration });
    }

    [HttpPost("stopMove")]
    public async Task<IActionResult> StopMove()
    {
        var selected = _equipment.GetSelected(DeviceKind.Telescope);
        if (_equipment.IsConnected(DeviceKind.Telescope) && selected?.Provider == EquipmentProvider.Indi)
        {
            await _indi.TelescopeStopMoveAsync(selected.UniqueId, HttpContext.RequestAborted);
        }
        return Ok(new { success = true, message = "Movement stopped" });
    }

    [HttpPost("home")]
    public async Task<IActionResult> Home()
    {
        var selected = _equipment.GetSelected(DeviceKind.Telescope);
        if (!_equipment.IsConnected(DeviceKind.Telescope) || selected?.Provider != EquipmentProvider.Indi)
            return StatusCode(503, new { success = false, message = "Telescope not connected" });
        if (_indi.IsTelescopeParked(selected.UniqueId))
            return StatusCode(409, new { success = false, message = "Mount is parked. Unpark first." });
        await _indi.TelescopeFindHomeAsync(selected.UniqueId, HttpContext.RequestAborted);
        return Ok(new { success = true, message = "Home requested" });
    }

    /// <summary>Set the mount's TELESCOPE_SLEW_RATE switch without sending
    /// any motion command. Lets the iOS chip strip preview a rate so the
    /// highlighted chip actually reflects the driver's selection.</summary>
    [HttpPost("setSlewRate")]
    public async Task<IActionResult> SetSlewRate([FromBody] SetSlewRateRequest request)
    {
        var selected = _equipment.GetSelected(DeviceKind.Telescope);
        if (!_equipment.IsConnected(DeviceKind.Telescope) || selected?.Provider != EquipmentProvider.Indi)
            return StatusCode(503, new { success = false, message = "Telescope not connected" });
        if (string.IsNullOrEmpty(request.RateName) && !request.SiderealRate.HasValue)
            return BadRequest(new { success = false, message = "rateName or siderealRate required" });
        await _indi.TelescopeSetSlewRateAsync(selected.UniqueId, request.RateName, request.SiderealRate, HttpContext.RequestAborted);
        return Ok(new { success = true, rateName = request.RateName, siderealRate = request.SiderealRate });
    }

    public record SetSlewRateRequest(string? RateName, double? SiderealRate);

    /// <summary>Send a pulse-guide (timed motion) command to the mount.
    /// Foundation for an in-server guider — used by iOS as a quick
    /// "test ST4 port" tool and by future guiding logic. Direction is
    /// N/S/E/W; durationMs typically 50–2000 ms per pulse.</summary>
    [HttpPost("pulseGuide")]
    public async Task<IActionResult> PulseGuide([FromBody] PulseGuideRequest request)
    {
        var selected = _equipment.GetSelected(DeviceKind.Telescope);
        if (!_equipment.IsConnected(DeviceKind.Telescope) || selected?.Provider != EquipmentProvider.Indi)
            return StatusCode(503, new { success = false, message = "Telescope not connected" });
        var dur = Math.Clamp(request.DurationMs, 1, 5000);
        await _indi.TelescopePulseGuideAsync(selected.UniqueId, request.Direction, dur, HttpContext.RequestAborted);
        return Ok(new { success = true, direction = request.Direction, durationMs = dur });
    }

    public record PulseGuideRequest(string Direction, int DurationMs);

    [HttpGet("guideRate")]
    public IActionResult GetGuideRate()
    {
        var selected = _equipment.GetSelected(DeviceKind.Telescope);
        if (!_equipment.IsConnected(DeviceKind.Telescope) || selected?.Provider != EquipmentProvider.Indi)
            return Ok(new { connected = false });
        var (ra, dec) = _indi.TryGetGuideRate(selected.UniqueId);
        return Ok(new { connected = true, raRate = ra, decRate = dec });
    }

    [HttpGet("axisRates")]
    public IActionResult AxisRates()
    {
        var telescope = _state.TelescopeMediator.GetDevice() as ITelescope;
        var axes = Enum.GetValues<TelescopeAxes>();
        var primaryAxis = axes.Length > 0 ? axes[0] : default;
        var secondaryAxis = axes.Length > 1 ? axes[1] : primaryAxis;

        var primary = telescope?.GetAxisRates(primaryAxis)?.Select(r => new { min = r.Item1, max = r.Item2 })
            ?? Enumerable.Empty<object>();
        var secondary = telescope?.GetAxisRates(secondaryAxis)?.Select(r => new { min = r.Item1, max = r.Item2 })
            ?? Enumerable.Empty<object>();

        return Ok(new
        {
            primary,
            secondary
        });
    }

    private static bool TryResolveAxisAndRate(string direction, int rate, out TelescopeAxes axis, out double signedRate)
    {
        axis = default;
        signedRate = 0;

        var normalized = direction.Trim().ToUpperInvariant();
        var axes = Enum.GetValues<TelescopeAxes>();
        if (axes.Length == 0)
        {
            return false;
        }

        var primaryAxis = axes[0];
        var secondaryAxis = axes.Length > 1 ? axes[1] : axes[0];
        var magnitude = Math.Abs(rate);

        switch (normalized)
        {
            case "N":
                axis = primaryAxis;
                signedRate = magnitude;
                return true;
            case "S":
                axis = primaryAxis;
                signedRate = -magnitude;
                return true;
            case "E":
                axis = secondaryAxis;
                signedRate = magnitude;
                return true;
            case "W":
                axis = secondaryAxis;
                signedRate = -magnitude;
                return true;
            default:
                return false;
        }
    }
}
