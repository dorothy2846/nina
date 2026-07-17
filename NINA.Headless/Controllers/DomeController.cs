using Microsoft.AspNetCore.Mvc;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Headless.Models;
using NINA.Headless.Services;

namespace NINA.Headless.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class DomeController : ControllerBase
{
    private readonly IDomeMediator _dome;
    private readonly EquipmentSelectionService _equipment;
    private readonly IndiDiscoveryService _indi;
    private readonly AlpacaClient _alpaca;

    public DomeController(IDomeMediator dome, EquipmentSelectionService equipment, IndiDiscoveryService indi, AlpacaClient alpaca)
    {
        _dome = dome;
        _equipment = equipment;
        _indi = indi;
        _alpaca = alpaca;
    }

    [HttpGet("info")]
    public IActionResult GetInfo()
    {
        var info = _dome.GetInfo();
        if (info == null)
        {
            return Ok(new { connected = false, name = "Not connected" });
        }

        // Capability probes — slit-only / shutter-only domes have subsets; the UI
        // hides controls that would error on the hardware.
        var device = _dome.GetDevice() as IDome;
        var capabilities = new
        {
            canSetAzimuth = device?.CanSetAzimuth ?? false,
            canSetShutter = device?.CanSetShutter ?? false,
            canFindHome = device?.CanFindHome ?? false,
            canPark = device?.CanPark ?? false,
            canSyncAzimuth = device?.CanSyncAzimuth ?? false
        };

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
            slaved = info.DriverFollowing || info.ApplicationFollowing,
            capabilities
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
    public Task<IActionResult> Connect([FromBody] ConnectRequest? request) =>
        IndiDeviceConnectFlow.ConnectAsync(_equipment, _indi, DeviceKind.Dome, "Dome", request, HttpContext.RequestAborted, _alpaca);

    [HttpPost("disconnect")]
    public async Task<IActionResult> Disconnect()
    {
        var selected = _equipment.GetSelected(DeviceKind.Dome);
        if (selected?.Provider == EquipmentProvider.Indi)
            _indi.RecordConnectionIntent(selected.UniqueId, false);
            await _indi.DisconnectDeviceAsync(selected.UniqueId, HttpContext.RequestAborted);
        _equipment.Disconnect(DeviceKind.Dome);
        return Ok(new { success = true, message = "Dome disconnected" });
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

    /// <summary>Emergency stop — halt any in-flight shutter move or azimuth slew. Different
    /// from /park (which parks + closes shutter) or /close (shutter only). Backs onto the
    /// underlying IDome.StopAll() so we cover both motors.</summary>
    [HttpPost("halt")]
    public async Task<IActionResult> Halt()
    {
        if (_dome.GetDevice() is IDome device)
        {
            await device.StopAll();
            return Ok(new { success = true, message = "Dome halted" });
        }
        return StatusCode(503, new { success = false, message = "Dome not connected" });
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
