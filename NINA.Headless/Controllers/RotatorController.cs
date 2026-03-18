using Microsoft.AspNetCore.Mvc;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Headless.Models;

namespace NINA.Headless.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class RotatorController : ControllerBase
{
    private readonly IRotatorMediator _rotator;

    public RotatorController(IRotatorMediator rotator)
    {
        _rotator = rotator;
    }

    [HttpGet("info")]
    public IActionResult GetInfo()
    {
        var info = _rotator.GetInfo();
        if (info == null)
        {
            return Ok(new { connected = false, name = "Not connected" });
        }

        return Ok(new
        {
            connected = info.Connected,
            name = info.Name,
            position = info.Position,
            mechanicalPosition = info.MechanicalPosition,
            stepSize = info.StepSize,
            isMoving = info.IsMoving,
            canReverse = info.CanReverse,
            reverse = info.Reverse
        });
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        var info = _rotator.GetInfo();
        if (info == null)
        {
            return Ok(new { connected = false, state = "disconnected" });
        }

        return Ok(new
        {
            connected = info.Connected,
            position = info.Position,
            mechanicalPosition = info.MechanicalPosition,
            isMoving = info.IsMoving
        });
    }

    [HttpPost("connect")]
    public async Task<IActionResult> Connect([FromBody] ConnectRequest? request)
    {
        var success = await _rotator.Connect();
        if (!success)
        {
            return StatusCode(503, new { success = false, message = "Failed to connect rotator", deviceId = request?.DeviceId ?? "default" });
        }

        return Ok(new
        {
            success = true,
            message = "Rotator connected",
            deviceId = request?.DeviceId ?? "default"
        });
    }

    [HttpPost("disconnect")]
    public async Task<IActionResult> Disconnect()
    {
        await _rotator.Disconnect();
        return Ok(new
        {
            success = true,
            message = "Rotator disconnected"
        });
    }

    [HttpPost("move")]
    public async Task<IActionResult> Move([FromBody] RotatorMoveRequest request)
    {
        var movedTo = await _rotator.Move((float)request.Position, CancellationToken.None);
        return Ok(new
        {
            success = true,
            message = $"Rotator moved to {movedTo:F2}",
            position = movedTo
        });
    }

    [HttpPost("halt")]
    public IActionResult Halt()
    {
        var device = _rotator.GetDevice() as IRotator;
        device?.Halt();

        return Ok(new
        {
            success = true,
            message = "Rotator halt requested"
        });
    }
}
