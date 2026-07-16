// CameraController — settings endpoints: cooling (set point / warm-up) and
// dew heater control.
// Partial of CameraController; fields and constructor live in CameraController.cs.

using Microsoft.AspNetCore.Mvc;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using NINA.Headless.Models;
using NINA.Headless.Services;
using NINA.Headless.Services.Remote;

namespace NINA.Headless.Controllers;

public partial class CameraController
{
    public record DewHeaterRequest(bool Enabled, int? PowerPercent);

    /// <summary>Dew heater control. Driver coverage is patchy (INDI's CCD_DEW_CONTROL,
    /// AUX_HEATER_TOGGLE, or ZWO's ANTI_DEW) — IndiDiscoveryService picks whichever the
    /// active camera exposes. Returns <c>success=false</c> when the driver has none of
    /// them so the UI can surface "not supported" rather than silently pretending.</summary>
    [HttpPost("dew-heater")]
    public async Task<IActionResult> SetDewHeater([FromBody] DewHeaterRequest request)
    {
        var selected = _cameraSelection.GetSelected();
        if (!_cameraSelection.IsConnected || selected?.Provider != CameraProvider.Indi)
            return BadRequest(new { success = false, message = "Camera not connected via INDI" });

        var ok = await _indi.SetDewHeaterAsync(selected.UniqueId, request.Enabled,
            request.PowerPercent, HttpContext.RequestAborted);
        return Ok(new {
            success = ok,
            message = ok ? null : "이 카메라 드라이버는 dew heater 제어를 지원하지 않습니다."
        });
    }

    [HttpPost("cooling")]
    public async Task<IActionResult> SetCooling([FromBody] CoolingRequest request)
    {
        var selected = _cameraSelection.GetSelected();

        // Route to INDI when an INDI camera is active — bypasses the stub mediator path.
        if (_cameraSelection.IsConnected && selected?.Provider == CameraProvider.Indi)
        {
            try
            {
                await _indi.SetCoolingAsync(selected.UniqueId, request.Enabled,
                    request.Enabled ? request.Temperature : null, HttpContext.RequestAborted);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, message = $"INDI cooling failed: {ex.Message}" });
            }

            _state.NotifyStateChanged("camera", _state.BuildCameraStatus());
            return Ok(new
            {
                success = true,
                message = request.Enabled ? $"Cooling enabled, target: {request.Temperature}C" : "Cooling disabled",
                enabled = request.Enabled,
                targetTemperature = request.Temperature
            });
        }

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
