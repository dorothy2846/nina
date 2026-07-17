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
        var selected = _equipment.GetSelected(DeviceKind.Dome);
        if (selected?.Provider == EquipmentProvider.Indi)
        {
            var i = _indi.TryBuildDomeInfo(selected.UniqueId);
            if (i != null)
                return Ok(new
                {
                    connected = i.Connected, name = i.Name, azimuth = i.Azimuth,
                    altitude = i.Altitude, atHome = i.AtHome, atPark = i.AtPark,
                    shutterStatus = ToShutterStatus(i.ShutterStatus), slewing = i.Slewing,
                    slaved = false,
                    capabilities = new
                    {
                        canSetAzimuth = _indi.DeviceHasProperty(selected.UniqueId, "ABS_DOME_POSITION"),
                        canSetShutter = _indi.DeviceHasProperty(selected.UniqueId, "DOME_SHUTTER"),
                        canFindHome = false,
                        canPark = _indi.DeviceHasProperty(selected.UniqueId, "DOME_PARK"),
                        canSyncAzimuth = _indi.DeviceHasProperty(selected.UniqueId, "ABS_DOME_POSITION")
                    }
                });
        }
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
        var selected = _equipment.GetSelected(DeviceKind.Dome);
        if (selected?.Provider == EquipmentProvider.Indi)
        {
            var i = _indi.TryBuildDomeInfo(selected.UniqueId);
            if (i != null)
                return Ok(new
                {
                    connected = i.Connected, azimuth = i.Azimuth,
                    shutterStatus = ToShutterStatus(i.ShutterStatus),
                    slewing = i.Slewing, atPark = i.AtPark, atHome = i.AtHome
                });
        }
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
        var selected = _equipment.GetSelected(DeviceKind.Dome);
        if (_equipment.IsConnected(DeviceKind.Dome) && selected?.Provider == EquipmentProvider.Indi)
        {
            var ok = await _indi.DomeShutterAsync(selected.UniqueId, open: true, HttpContext.RequestAborted);
            if (!ok) return StatusCode(503, new { success = false, message = "Dome does not expose DOME_SHUTTER" });
            return Ok(new { success = true, message = "Dome shutter opening" });
        }
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
        var selected = _equipment.GetSelected(DeviceKind.Dome);
        if (_equipment.IsConnected(DeviceKind.Dome) && selected?.Provider == EquipmentProvider.Indi)
        {
            var ok = await _indi.DomeShutterAsync(selected.UniqueId, open: false, HttpContext.RequestAborted);
            if (!ok) return StatusCode(503, new { success = false, message = "Dome does not expose DOME_SHUTTER" });
            return Ok(new { success = true, message = "Dome shutter closing" });
        }
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
        var selected = _equipment.GetSelected(DeviceKind.Dome);
        if (_equipment.IsConnected(DeviceKind.Dome) && selected?.Provider == EquipmentProvider.Indi)
        {
            var ok = await _indi.DomeParkAsync(selected.UniqueId, park: true, HttpContext.RequestAborted);
            if (!ok) return StatusCode(503, new { success = false, message = "Dome does not expose DOME_PARK" });
            return Ok(new { success = true, message = "Dome parking" });
        }
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

    [HttpPost("unpark")]
    public async Task<IActionResult> Unpark()
    {
        var selected = _equipment.GetSelected(DeviceKind.Dome);
        if (_equipment.IsConnected(DeviceKind.Dome) && selected?.Provider == EquipmentProvider.Indi)
        {
            var ok = await _indi.DomeParkAsync(selected.UniqueId, park: false, HttpContext.RequestAborted);
            if (!ok) return StatusCode(503, new { success = false, message = "Dome does not expose DOME_PARK" });
            return Ok(new { success = true, message = "Dome unparking" });
        }
        return StatusCode(503, new { success = false, message = "Dome not connected" });
    }

    /// <summary>Emergency stop — halt any in-flight shutter move or azimuth slew. Different
    /// from /park (which parks + closes shutter) or /close (shutter only). Backs onto the
    /// underlying IDome.StopAll() so we cover both motors.</summary>
    [HttpPost("halt")]
    public async Task<IActionResult> Halt()
    {
        var selected = _equipment.GetSelected(DeviceKind.Dome);
        if (_equipment.IsConnected(DeviceKind.Dome) && selected?.Provider == EquipmentProvider.Indi)
        {
            await _indi.DomeAbortAsync(selected.UniqueId, HttpContext.RequestAborted);
            return Ok(new { success = true, message = "Dome halted" });
        }
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

        var selectedIndi = _equipment.GetSelected(DeviceKind.Dome);
        if (_equipment.IsConnected(DeviceKind.Dome) && selectedIndi?.Provider == EquipmentProvider.Indi)
        {
            var okIndi = await _indi.DomeGotoAzimuthAsync(selectedIndi.UniqueId, request.Azimuth.Value, HttpContext.RequestAborted);
            if (!okIndi) return StatusCode(503, new { success = false, message = "Dome does not expose ABS_DOME_POSITION" });
            return Ok(new { success = true, message = $"Dome slewing to azimuth {request.Azimuth.Value:F1}" });
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
