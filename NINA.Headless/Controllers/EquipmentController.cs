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
    private readonly ICameraMediator _camera;
    private readonly ITelescopeMediator _telescope;
    private readonly IGuiderMediator _guider;
    private readonly IFocuserMediator _focuser;
    private readonly IFilterWheelMediator _filterWheel;
    private readonly IRotatorMediator _rotator;
    private readonly IDomeMediator _dome;

    public EquipmentController(
        NinaStateService state,
        ICameraMediator camera,
        ITelescopeMediator telescope,
        IGuiderMediator guider,
        IFocuserMediator focuser,
        IFilterWheelMediator filterWheel,
        IRotatorMediator rotator,
        IDomeMediator dome)
    {
        _state = state;
        _camera = camera;
        _telescope = telescope;
        _guider = guider;
        _focuser = focuser;
        _filterWheel = filterWheel;
        _rotator = rotator;
        _dome = dome;
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        var focuserInfo = _focuser.GetInfo();
        var filterWheelInfo = _filterWheel.GetInfo();
        var rotatorInfo = _rotator.GetInfo();
        var domeInfo = _dome.GetInfo();

        return Ok(new
        {
            camera = _state.BuildCameraStatus(),
            telescope = _state.BuildTelescopeStatus(),
            guider = _state.BuildGuiderStatus(),
            focuser = new
            {
                connected = focuserInfo?.Connected ?? false,
                name = focuserInfo?.Name ?? "No Focuser",
                position = (int?)focuserInfo?.Position,
                temperature = focuserInfo != null && !double.IsNaN(focuserInfo.Temperature) ? focuserInfo.Temperature : null,
                isMoving = (bool?)focuserInfo?.IsMoving
            },
            filterWheel = new
            {
                connected = filterWheelInfo?.Connected ?? false,
                name = filterWheelInfo?.Name ?? "No Filter Wheel",
                currentPosition = filterWheelInfo?.SelectedFilter?.Position,
                currentFilterName = filterWheelInfo?.SelectedFilter?.Name,
                isMoving = (bool?)filterWheelInfo?.IsMoving
            },
            rotator = new
            {
                connected = rotatorInfo?.Connected ?? false,
                name = rotatorInfo?.Name ?? "No Rotator",
                position = (float?)rotatorInfo?.Position,
                mechanicalPosition = (float?)rotatorInfo?.MechanicalPosition,
                isMoving = (bool?)rotatorInfo?.IsMoving
            },
            dome = new
            {
                connected = domeInfo?.Connected ?? false,
                name = domeInfo?.Name ?? "No Dome",
                azimuth = domeInfo?.Azimuth,
                shutterStatus = domeInfo?.ShutterStatus.ToString() ?? "Unknown",
                slewing = (bool?)domeInfo?.Slewing,
                atPark = (bool?)domeInfo?.AtPark,
                atHome = (bool?)domeInfo?.AtHome
            },
            safetyMonitor = new { connected = false, name = "No Safety Monitor" },
            flatDevice = new { connected = false, name = "No Flat Device" },
            weather = new { connected = false, name = "No Weather Station" },
            @switch = new { connected = false, name = "No Switch" }
        });
    }

    [HttpGet("devices")]
    public IActionResult GetDevices()
    {
        return Ok(new
        {
            cameras = new[]
            {
                new { id = "asi294mc_stub", name = "ZWO ASI294MC Pro (Stub)" },
                new { id = "simulator", name = "N.I.N.A. Simulator" }
            },
            telescopes = new[]
            {
                new { id = "simulator", name = "N.I.N.A. Simulator" },
                new { id = "ascom_eqmod", name = "EQMOD ASCOM HEQ5/6 (Stub)" }
            },
            guiders = new[]
            {
                new { id = "phd2", name = "PHD2" },
                new { id = "internal", name = "N.I.N.A. Internal Guider" }
            },
            focusers = new[]
            {
                new { id = "simulator", name = "N.I.N.A. Simulator" }
            },
            filterWheels = new[]
            {
                new { id = "manual", name = "Manual Filter Wheel" },
                new { id = "simulator", name = "N.I.N.A. Simulator" }
            },
            rotators = new[]
            {
                new { id = "simulator", name = "N.I.N.A. Simulator" }
            },
            domes = new[]
            {
                new { id = "simulator", name = "N.I.N.A. Simulator" }
            },
            safetyMonitors = Array.Empty<object>(),
            flatDevices = Array.Empty<object>(),
            weather = Array.Empty<object>(),
            switches = Array.Empty<object>()
        });
    }

    [HttpPost("connect-all")]
    public async Task<IActionResult> ConnectAll([FromBody] ConnectAllRequest? request)
    {
        var results = new Dictionary<string, object>();

        var cameraConnected = await _camera.Connect();
        var cameraInfo = _state.CameraMediator.GetInfo();
        results["camera"] = new
        {
            success = cameraConnected,
            name = cameraInfo?.Name ?? request?.CameraId ?? "camera",
            message = cameraConnected ? "Connected" : "Failed to connect"
        };

        var telescopeConnected = await _telescope.Connect();
        var telescopeInfo = _state.TelescopeMediator.GetInfo();
        results["telescope"] = new
        {
            success = telescopeConnected,
            name = telescopeInfo?.Name ?? request?.TelescopeId ?? "telescope",
            message = telescopeConnected ? "Connected" : "Failed to connect"
        };

        var focuserConnected = await _focuser.Connect();
        var focuserInfo = _focuser.GetInfo();
        results["focuser"] = new
        {
            success = focuserConnected,
            name = focuserInfo?.Name ?? request?.FocuserId ?? "focuser",
            message = focuserConnected ? "Connected" : "Failed to connect"
        };

        var filterWheelConnected = await _filterWheel.Connect();
        var filterWheelInfo = _filterWheel.GetInfo();
        results["filterWheel"] = new
        {
            success = filterWheelConnected,
            name = filterWheelInfo?.Name ?? request?.FilterWheelId ?? "filterwheel",
            message = filterWheelConnected ? "Connected" : "Failed to connect"
        };

        var guiderConnected = await _guider.Connect();
        var guiderInfo = _state.GuiderMediator.GetInfo();
        results["guider"] = new
        {
            success = guiderConnected,
            name = guiderInfo?.Name ?? request?.GuiderId ?? "guider",
            message = guiderConnected ? "Connected" : "Failed to connect"
        };

        var rotatorConnected = await _rotator.Connect();
        var rotatorInfo = _rotator.GetInfo();
        results["rotator"] = new
        {
            success = rotatorConnected,
            name = rotatorInfo?.Name ?? request?.RotatorId ?? "rotator",
            message = rotatorConnected ? "Connected" : "Failed to connect"
        };

        var domeConnected = await _dome.Connect();
        var domeInfo = _dome.GetInfo();
        results["dome"] = new
        {
            success = domeConnected,
            name = domeInfo?.Name ?? request?.DomeId ?? "dome",
            message = domeConnected ? "Connected" : "Failed to connect"
        };

        _state.NotifyStateChanged("equipment", _state.BuildEquipmentStatus());
        return Ok(new { results });
    }

    [HttpPost("disconnect-all")]
    public async Task<IActionResult> DisconnectAll()
    {
        var results = new Dictionary<string, object>();

        await _state.CameraMediator.Disconnect();
        results["camera"] = new { success = true, message = "Disconnected" };

        await _state.TelescopeMediator.Disconnect();
        results["telescope"] = new { success = true, message = "Disconnected" };

        await _focuser.Disconnect();
        results["focuser"] = new { success = true, message = "Disconnected" };

        await _filterWheel.Disconnect();
        results["filterWheel"] = new { success = true, message = "Disconnected" };

        await _state.GuiderMediator.Disconnect();
        results["guider"] = new { success = true, message = "Disconnected" };

        await _rotator.Disconnect();
        results["rotator"] = new { success = true, message = "Disconnected" };

        await _dome.Disconnect();
        results["dome"] = new { success = true, message = "Disconnected" };

        _state.NotifyStateChanged("equipment", _state.BuildEquipmentStatus());
        return Ok(new { results });
    }
}
