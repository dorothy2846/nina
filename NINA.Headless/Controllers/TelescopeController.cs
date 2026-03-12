using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using NINA.Astrometry;
using NINA.Core.Enum;
using NINA.Core.Model;
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

    public TelescopeController(NinaStateService state, IHubContext<NinaHub> hub)
    {
        _state = state;
        _hub = hub;
    }

    [HttpGet("info")]
    public IActionResult GetInfo()
    {
        var info = _state.TelescopeInfo;
        if (info == null)
        {
            return Ok(new { connected = false, name = "Not connected" });
        }

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
            alignmentMode = info.AlignmentMode.ToString()
        });
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        var info = _state.TelescopeInfo;
        if (info == null)
        {
            return Ok(new { connected = false, state = "disconnected" });
        }

        return Ok(new
        {
            connected = info.Connected,
            ra = info.RightAscension,
            dec = info.Declination,
            alt = info.Altitude,
            az = info.Azimuth,
            tracking = info.TrackingEnabled,
            parked = info.AtPark,
            slewing = info.Slewing,
            siderealTime = info.SiderealTime,
            pierSide = info.SideOfPier.ToString()
        });
    }

    [HttpPost("connect")]
    public async Task<IActionResult> Connect([FromBody] ConnectRequest? request)
    {
        var success = await _state.TelescopeMediator.Connect();
        _state.NotifyStateChanged("telescope", _state.BuildTelescopeStatus());
        await _hub.Clients.All.SendAsync("EquipmentStatus", _state.BuildEquipmentStatus());

        if (!success)
        {
            return StatusCode(503, new { success = false, message = "Failed to connect telescope", deviceId = request?.DeviceId ?? "default" });
        }

        return Ok(new
        {
            success = true,
            message = "Telescope connected",
            deviceId = request?.DeviceId ?? "default"
        });
    }

    [HttpPost("disconnect")]
    public async Task<IActionResult> Disconnect()
    {
        await _state.TelescopeMediator.Disconnect();
        _state.NotifyStateChanged("telescope", _state.BuildTelescopeStatus());
        await _hub.Clients.All.SendAsync("EquipmentStatus", _state.BuildEquipmentStatus());

        return Ok(new
        {
            success = true,
            message = "Telescope disconnected"
        });
    }

    [HttpPost("slew")]
    public async Task<IActionResult> Slew([FromBody] SlewRequest request)
    {
        if (request.RA < 0 || request.RA >= 360)
        {
            return BadRequest(new { success = false, message = "RA must be between 0 and 360 degrees" });
        }
        if (request.Dec < -90 || request.Dec > 90)
        {
            return BadRequest(new { success = false, message = "Dec must be between -90 and 90 degrees" });
        }

        var coordinates = new Coordinates(request.RA, request.Dec, Epoch.JNOW, Coordinates.RAType.Degrees);
        var success = await _state.TelescopeMediator.SlewToCoordinatesAsync(coordinates, CancellationToken.None);
        _state.NotifyStateChanged("telescope", _state.BuildTelescopeStatus());

        if (!success)
        {
            return StatusCode(500, new { success = false, message = "Telescope slew failed" });
        }

        return Ok(new
        {
            success = true,
            message = $"Slewing to RA={request.RA:F4}, Dec={request.Dec:F4}",
            ra = request.RA,
            dec = request.Dec
        });
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
        var success = await _state.TelescopeMediator.ParkTelescope(new Progress<ApplicationStatus>(), CancellationToken.None);
        _state.NotifyStateChanged("telescope", _state.BuildTelescopeStatus());

        if (!success)
        {
            return StatusCode(500, new { success = false, message = "Failed to park telescope" });
        }

        return Ok(new
        {
            success = true,
            message = "Park requested"
        });
    }

    [HttpPost("unpark")]
    public async Task<IActionResult> Unpark()
    {
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
    public IActionResult SetTracking([FromBody] TrackingRequest request)
    {
        var success = _state.TelescopeMediator.SetTrackingEnabled(request.Enabled);
        _state.NotifyStateChanged("telescope", _state.BuildTelescopeStatus());

        if (!success)
        {
            return StatusCode(500, new { success = false, message = "Failed to change tracking state" });
        }

        return Ok(new
        {
            success = true,
            message = request.Enabled ? "Tracking enabled" : "Tracking disabled",
            enabled = request.Enabled
        });
    }

    [HttpPost("move")]
    public async Task<IActionResult> Move([FromBody] MoveRequest request)
    {
        var validDirections = new[] { "N", "S", "E", "W" };
        if (!validDirections.Contains(request.Direction, StringComparer.OrdinalIgnoreCase))
        {
            return BadRequest(new { success = false, message = "Direction must be N, S, E, or W" });
        }

        if (!TryResolveAxisAndRate(request.Direction, request.Rate, out var axis, out var rate))
        {
            return BadRequest(new { success = false, message = "Direction must be N, S, E, or W" });
        }

        _state.TelescopeMediator.MoveAxis(axis, rate);
        if (request.Duration > 0)
        {
            await Task.Delay(request.Duration);
            _state.TelescopeMediator.MoveAxis(axis, 0);
        }

        return Ok(new
        {
            success = true,
            message = $"Moving {request.Direction} at rate {request.Rate} for {request.Duration}ms",
            direction = request.Direction,
            rate = request.Rate,
            duration = request.Duration
        });
    }

    [HttpPost("stopMove")]
    public IActionResult StopMove()
    {
        var axes = Enum.GetValues<TelescopeAxes>();
        foreach (var axis in axes)
        {
            _state.TelescopeMediator.MoveAxis(axis, 0);
        }

        return Ok(new
        {
            success = true,
            message = "Movement stopped"
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
