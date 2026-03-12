using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using NINA.Headless.Hubs;
using NINA.Headless.Models;
using NINA.Headless.Services;

namespace NINA.Headless.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class CameraController : ControllerBase
{
    private readonly NinaStateService _state;
    private readonly IHubContext<NinaHub> _hub;

    public CameraController(NinaStateService state, IHubContext<NinaHub> hub)
    {
        _state = state;
        _hub = hub;
    }

    [HttpGet("info")]
    public IActionResult GetInfo()
    {
        var info = _state.CameraInfo;
        if (info == null)
        {
            return Ok(new { connected = false, name = "Not connected" });
        }

        return Ok(new
        {
            name = info.Name,
            connected = info.Connected,
            temperature = info.Temperature,
            coolerPower = info.CoolerPower,
            gain = info.Gain,
            offset = info.Offset,
            binning = info.BinX,
            canSetTemperature = info.CanSetTemperature,
            sensorType = info.SensorType.ToString(),
            bitDepth = info.BitDepth,
            pixelSizeX = info.PixelSize,
            pixelSizeY = info.PixelSize,
            resolutionX = info.XSize,
            resolutionY = info.YSize
        });
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        var info = _state.CameraInfo;
        if (info == null)
        {
            return Ok(new
            {
                connected = false,
                state = "disconnected"
            });
        }

        return Ok(new
        {
            connected = info.Connected,
            cooling = info.CoolerOn,
            temperature = info.Temperature,
            targetTemperature = info.TemperatureSetPoint,
            coolerPower = info.CoolerPower,
            state = info.CameraState.ToString(),
            gain = info.Gain,
            offset = info.Offset,
            binning = info.BinX
        });
    }

    [HttpPost("connect")]
    public async Task<IActionResult> Connect([FromBody] ConnectRequest? request)
    {
        var success = await _state.CameraMediator.Connect();
        _state.NotifyStateChanged("camera", _state.BuildCameraStatus());
        await _hub.Clients.All.SendAsync("EquipmentStatus", _state.BuildEquipmentStatus());

        if (!success)
        {
            return StatusCode(503, new { success = false, message = "Failed to connect camera", deviceId = request?.DeviceId ?? "default" });
        }

        return Ok(new
        {
            success = true,
            message = "Camera connected",
            deviceId = request?.DeviceId ?? "default"
        });
    }

    [HttpPost("disconnect")]
    public async Task<IActionResult> Disconnect()
    {
        await _state.CameraMediator.Disconnect();
        _state.NotifyStateChanged("camera", _state.BuildCameraStatus());
        await _hub.Clients.All.SendAsync("EquipmentStatus", _state.BuildEquipmentStatus());

        return Ok(new
        {
            success = true,
            message = "Camera disconnected"
        });
    }

    [HttpPost("capture")]
    public async Task<IActionResult> Capture([FromBody] CaptureRequest request)
    {
        if (request.ExposureTime <= 0)
        {
            return BadRequest(new { success = false, message = "ExposureTime must be greater than 0" });
        }

        var binning = (short)Math.Clamp(request.Binning, 1, short.MaxValue);
        var sequence = new CaptureSequence(
            request.ExposureTime,
            CaptureSequence.ImageTypes.LIGHT,
            null,
            new BinningMode(binning, binning),
            1);
        sequence.Gain = request.Gain;
        sequence.Offset = request.Offset;

        try
        {
            await _state.CameraMediator.Capture(sequence, CancellationToken.None, new Progress<ApplicationStatus>());
            _state.NotifyStateChanged("camera", _state.BuildCameraStatus());
            await _hub.Clients.All.SendAsync("StatusUpdate", new
            {
                timestamp = DateTime.UtcNow,
                camera = _state.BuildCameraStatus(),
                telescope = _state.BuildTelescopeStatus(),
                guider = _state.BuildGuiderStatus()
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { success = false, message = ex.Message });
        }

        return Ok(new
        {
            success = true,
            message = "Capture started",
            exposureTime = request.ExposureTime,
            gain = request.Gain,
            offset = request.Offset,
            binning = request.Binning,
            filter = request.Filter
        });
    }

    [HttpPost("abort")]
    public IActionResult Abort()
    {
        _state.CameraMediator.AbortExposure();

        return Ok(new
        {
            success = true,
            message = "Capture aborted"
        });
    }

    [HttpPost("cooling")]
    public async Task<IActionResult> SetCooling([FromBody] CoolingRequest request)
    {
        bool success;
        if (request.Enabled)
        {
            success = await _state.CameraMediator.CoolCamera(
                request.Temperature,
                TimeSpan.FromMinutes(2),
                new Progress<ApplicationStatus>(),
                CancellationToken.None);
        }
        else
        {
            success = await _state.CameraMediator.WarmCamera(
                TimeSpan.FromMinutes(2),
                new Progress<ApplicationStatus>(),
                CancellationToken.None);
        }

        _state.NotifyStateChanged("camera", _state.BuildCameraStatus());

        if (!success)
        {
            return StatusCode(500, new { success = false, message = "Failed to update camera cooling state" });
        }

        return Ok(new
        {
            success = true,
            message = request.Enabled
                ? $"Cooling enabled, target: {request.Temperature}C"
                : "Cooling disabled",
            enabled = request.Enabled,
            targetTemperature = request.Temperature
        });
    }
}
