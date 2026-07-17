using Microsoft.AspNetCore.Mvc;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using NINA.Headless.Models;
using NINA.Headless.Services;
using NINA.Headless.Services.Remote;

namespace NINA.Headless.Controllers;

// Partial class — sibling files hold the endpoint groups:
//   CameraController.Captures.cs    — capture, capture library, abort
//   CameraController.Calibration.cs — calibration library, live stack, flat wizard, dark/bias batch
//   CameraController.Streaming.cs   — stream + SER/AVI recording
//   CameraController.Settings.cs    — cooling, dew heater
[ApiController]
[Route("api/v1/[controller]")]
public partial class CameraController : ControllerBase
{
    private static readonly byte[] TransparentPngPlaceholder = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aGxQAAAAASUVORK5CYII=");

    private readonly NinaStateService _state;
    private readonly RemoteEventBus _eventBus;
    private readonly CameraSelectionService _cameraSelection;
    private readonly EquipmentSelectionService _equipment;
    private readonly IndiDiscoveryService _indi;
    private readonly CaptureStore _captures;
    private readonly CameraStreamService _stream;
    private readonly FlatWizardService _flatWizard;
    private readonly CalibrationBatchService _calibration;
    private readonly CalibrationLibrary _library;
    private readonly LiveStackService _liveStack;
    private readonly Phd2Service _phd2;
    private readonly H264Transcoder _h264;

    public CameraController(NinaStateService state, RemoteEventBus eventBus, CameraSelectionService cameraSelection, EquipmentSelectionService equipment, IndiDiscoveryService indi, CaptureStore captures, CameraStreamService stream, FlatWizardService flatWizard, CalibrationBatchService calibration, CalibrationLibrary library, LiveStackService liveStack, Phd2Service phd2, H264Transcoder h264)
    {
        _state = state;
        _eventBus = eventBus;
        _cameraSelection = cameraSelection;
        _equipment = equipment;
        _indi = indi;
        _captures = captures;
        _stream = stream;
        _flatWizard = flatWizard;
        _calibration = calibration;
        _library = library;
        _liveStack = liveStack;
        _phd2 = phd2;
        _h264 = h264;
    }

    [HttpGet("info")]
    public IActionResult GetInfo()
    {
        var info = _state.CameraInfo;
        if (info == null)
        {
            return Ok(new { connected = false, name = "Not connected" });
        }

        // Capability probes — dew heater support varies widely across INDI drivers
        // (CCD_DEW_CONTROL vs AUX_HEATER_TOGGLE vs ANTI_DEW); the setter tries all
        // three, so "supported" = any of them present.
        var selected = _cameraSelection.GetSelected();
        var indiName = (_cameraSelection.IsConnected && selected?.Provider == CameraProvider.Indi) ? selected.UniqueId : null;
        var capabilities = new
        {
            canSetTemperature = info.CanSetTemperature,
            canAbort = indiName != null && _indi.DeviceHasProperty(indiName, "CCD_ABORT_EXPOSURE"),
            hasDewHeater = indiName != null && _indi.DeviceHasAnyProperty(indiName,
                "CCD_DEW_CONTROL", "AUX_HEATER_TOGGLE", "ANTI_DEW"),
            hasOffset = indiName != null && _indi.DeviceHasProperty(indiName, "CCD_OFFSET"),
            hasGain = indiName != null && _indi.DeviceHasProperty(indiName, "CCD_GAIN")
        };

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
            resolutionY = info.YSize,
            capabilities
        });
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        var info = _state.CameraInfo;
        var (exposureTime, exposureProgress) = _state.GetCameraExposureMetrics();

        if (info == null)
        {
            return Ok(new
            {
                connected = false,
                state = "disconnected",
                exposureTime = (double?)null,
                exposureProgress = (double?)null
            });
        }

        return Ok(new
        {
            // Name exposed here so the iOS CalibrationSheet can show "Library Camera: …"
            // without a separate device-list round-trip.
            name = info.Name,
            connected = info.Connected,
            cooling = info.CoolerOn,
            temperature = info.Temperature,
            targetTemperature = info.TemperatureSetPoint,
            coolerPower = info.CoolerPower,
            state = info.CameraState.ToString(),
            gain = info.Gain,
            offset = info.Offset,
            binning = info.BinX,
            exposureTime,
            exposureProgress
        });
    }

    [HttpPost("connect")]
    public async Task<IActionResult> Connect([FromBody] ConnectRequest? request)
    {
        if (string.IsNullOrWhiteSpace(request?.DeviceId))
        {
            return BadRequest(new { success = false, message = "deviceId is required" });
        }
        var deviceId = request.DeviceId;

        var success = _cameraSelection.Connect(deviceId);
        if (!success)
        {
            return NotFound(new { success = false, message = $"Unknown camera deviceId '{deviceId}'", deviceId });
        }

        var selected = _cameraSelection.GetSelected();

        // For INDI devices, also actuate CONNECTION.CONNECT=On on the driver.
        if (selected?.Provider == CameraProvider.Indi)
        {
            var result = await _indi.ConnectCameraAsync(selected.UniqueId, HttpContext.RequestAborted);
            if (!result.Ok)
            {
                _cameraSelection.Disconnect();
                // Return the real driver reason so the iOS layer can show something useful
                // beyond "connect failed" — typically a line emitted by the INDI driver via
                // a <message> element just before it bailed.
                return StatusCode(503, new
                {
                    success = false,
                    message = result.Reason ?? "INDI driver did not become ready",
                    driverMessages = result.DriverMessages,
                    deviceId
                });
            }
        }

        _state.NotifyStateChanged("camera", _state.BuildCameraStatus());
        _eventBus.Broadcast("EquipmentStatus", _state.BuildEquipmentStatus());

        return Ok(new
        {
            success = true,
            message = "Camera connected",
            deviceId,
            name = selected?.Name
        });
    }

    [HttpPost("disconnect")]
    public async Task<IActionResult> Disconnect()
    {
        var selected = _cameraSelection.GetSelected();
        if (selected?.Provider == CameraProvider.Indi)
        {
            await _indi.DisconnectCameraAsync(selected.UniqueId, HttpContext.RequestAborted);
        }

        _cameraSelection.Disconnect();
        _state.NotifyStateChanged("camera", _state.BuildCameraStatus());
        _eventBus.Broadcast("EquipmentStatus", _state.BuildEquipmentStatus());

        return Ok(new
        {
            success = true,
            message = "Camera disconnected"
        });
    }
}
