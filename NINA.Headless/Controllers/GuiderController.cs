using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using NINA.Core.Model;
using NINA.Headless.Hubs;
using NINA.Headless.Models;
using NINA.Headless.Services;

namespace NINA.Headless.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class GuiderController : ControllerBase
{
    private readonly NinaStateService _state;
    private readonly IHubContext<NinaHub> _hub;

    public GuiderController(NinaStateService state, IHubContext<NinaHub> hub)
    {
        _state = state;
        _hub = hub;
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
        var info = _state.GuiderInfo;
        if (info == null)
        {
            return Ok(new { connected = false, state = "disconnected", graph = Array.Empty<object>() });
        }

        return Ok(new
        {
            connected = info.Connected,
            state = info.Connected ? "Running" : "Stopped",
            rmsRA = info.RMSError?.RA?.Arcseconds ?? 0,
            rmsDec = info.RMSError?.Dec?.Arcseconds ?? 0,
            rmsTotal = info.RMSError?.Total?.Arcseconds ?? 0,
            peakRA = info.RMSError?.PeakRA?.Arcseconds ?? 0,
            peakDec = info.RMSError?.PeakDec?.Arcseconds ?? 0,
            graph = _state.GuideHistory
        });
    }

    [HttpPost("connect")]
    public async Task<IActionResult> Connect([FromBody] ConnectRequest? request)
    {
        var success = await _state.GuiderMediator.Connect();
        _state.NotifyStateChanged("guider", _state.BuildGuiderStatus());
        await _hub.Clients.All.SendAsync("EquipmentStatus", _state.BuildEquipmentStatus());

        if (!success)
        {
            return StatusCode(503, new { success = false, message = "Failed to connect guider", deviceId = request?.DeviceId ?? "PHD2" });
        }

        return Ok(new
        {
            success = true,
            message = "Guider connected",
            deviceId = request?.DeviceId ?? "PHD2"
        });
    }

    [HttpPost("disconnect")]
    public async Task<IActionResult> Disconnect()
    {
        await _state.GuiderMediator.Disconnect();
        _state.NotifyStateChanged("guider", _state.BuildGuiderStatus());
        await _hub.Clients.All.SendAsync("EquipmentStatus", _state.BuildEquipmentStatus());

        return Ok(new
        {
            success = true,
            message = "Guider disconnected"
        });
    }

    [HttpPost("start")]
    public async Task<IActionResult> Start()
    {
        var success = await _state.GuiderMediator.StartGuiding(
            false,
            new Progress<ApplicationStatus>(),
            CancellationToken.None);

        if (!success)
        {
            return StatusCode(500, new { success = false, message = "Failed to start guiding" });
        }

        return Ok(new
        {
            success = true,
            message = "Guiding started"
        });
    }

    [HttpPost("stop")]
    public async Task<IActionResult> Stop()
    {
        var success = await _state.GuiderMediator.StopGuiding(CancellationToken.None);
        if (!success)
        {
            return StatusCode(500, new { success = false, message = "Failed to stop guiding" });
        }

        return Ok(new
        {
            success = true,
            message = "Guiding stopped"
        });
    }

    [HttpPost("dither")]
    public async Task<IActionResult> Dither([FromBody] DitherRequest request)
    {
        if (request.Pixels <= 0)
        {
            return BadRequest(new { success = false, message = "Pixels must be greater than 0" });
        }

        var success = await _state.GuiderMediator.Dither(CancellationToken.None);
        if (!success)
        {
            return StatusCode(500, new { success = false, message = "Dither failed" });
        }

        return Ok(new
        {
            success = true,
            message = $"Dither by {request.Pixels} pixels requested",
            pixels = request.Pixels
        });
    }
}
