using Microsoft.AspNetCore.Mvc;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Headless.Models;

namespace NINA.Headless.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class DomeController : ControllerBase
{
    private readonly IDomeMediator _dome;

    public DomeController(IDomeMediator dome)
    {
        _dome = dome;
    }

    [HttpGet("info")]
    public IActionResult GetInfo()
    {
        var info = _dome.GetInfo();
        if (info == null)
        {
            return Ok(new { connected = false, name = "Not connected" });
        }

        return Ok(new
        {
            connected = info.Connected,
            name = info.Name,
            azimuth = info.Azimuth,
            altitude = info.Altitude,
            atHome = info.AtHome,
            atPark = info.AtPark,
            shutterStatus = ToShutterStatus(info.ShutterStatus),
            slewing = info.Slewing,
            slaved = info.DriverFollowing || info.ApplicationFollowing
        });
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        var info = _dome.GetInfo();
        if (info == null)
        {
            return Ok(new { connected = false, state = "disconnected" });
        }

        return Ok(new
        {
            connected = info.Connected,
            azimuth = info.Azimuth,
            shutterStatus = ToShutterStatus(info.ShutterStatus),
            slewing = info.Slewing,
            atPark = info.AtPark,
            atHome = info.AtHome
        });
    }

    [HttpPost("connect")]
    public async Task<IActionResult> Connect([FromBody] ConnectRequest? request)
    {
        var success = await _dome.Connect();
        if (!success)
        {
            return StatusCode(503, new { success = false, message = "Failed to connect dome", deviceId = request?.DeviceId ?? "default" });
        }

        return Ok(new
        {
            success = true,
            message = "Dome connected",
            deviceId = request?.DeviceId ?? "default"
        });
    }

    [HttpPost("disconnect")]
    public async Task<IActionResult> Disconnect()
    {
        await _dome.Disconnect();
        return Ok(new
        {
            success = true,
            message = "Dome disconnected"
        });
    }

    [HttpPost("open")]
    public async Task<IActionResult> Open()
    {
        var success = await _dome.OpenShutter(CancellationToken.None);
        if (!success)
        {
            return StatusCode(500, new { success = false, message = "Failed to open dome shutter" });
        }

        return Ok(new
        {
            success = true,
            message = "Dome shutter opened"
        });
    }

    [HttpPost("close")]
    public async Task<IActionResult> Close()
    {
        var success = await _dome.CloseShutter(CancellationToken.None);
        if (!success)
        {
            return StatusCode(500, new { success = false, message = "Failed to close dome shutter" });
        }

        return Ok(new
        {
            success = true,
            message = "Dome shutter closed"
        });
    }

    [HttpPost("park")]
    public async Task<IActionResult> Park()
    {
        var success = await _dome.Park(CancellationToken.None);
        if (!success)
        {
            return StatusCode(500, new { success = false, message = "Failed to park dome" });
        }

        return Ok(new
        {
            success = true,
            message = "Dome parked"
        });
    }

    [HttpPost("home")]
    public async Task<IActionResult> Home()
    {
        var success = await _dome.FindHome(CancellationToken.None);
        if (!success)
        {
            return StatusCode(500, new { success = false, message = "Failed to home dome" });
        }

        return Ok(new
        {
            success = true,
            message = "Dome homed"
        });
    }

    [HttpPost("sync")]
    public async Task<IActionResult> Sync([FromBody] DomeSyncRequest request)
    {
        if (request.Azimuth is null)
        {
            return BadRequest(new { success = false, message = "Azimuth is required" });
        }

        var success = await _dome.SlewToAzimuth(request.Azimuth.Value, CancellationToken.None);
        if (!success)
        {
            return StatusCode(500, new { success = false, message = "Failed to sync dome azimuth" });
        }

        return Ok(new
        {
            success = true,
            message = $"Dome synced to azimuth {request.Azimuth.Value:F2}",
            azimuth = request.Azimuth.Value
        });
    }

    private static string ToShutterStatus(ShutterState state)
    {
        return state switch
        {
            ShutterState.ShutterOpen => "Open",
            ShutterState.ShutterClosed => "Closed",
            ShutterState.ShutterOpening => "Opening",
            ShutterState.ShutterClosing => "Closing",
            ShutterState.ShutterError => "Error",
            _ => "Unknown"
        };
    }
}
