using Microsoft.AspNetCore.Mvc;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Headless.Models;
using NINA.Headless.Services;

namespace NINA.Headless.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class RotatorController : ControllerBase
{
    private readonly IRotatorMediator _rotator;
    private readonly EquipmentSelectionService _equipment;
    private readonly IndiDiscoveryService _indi;
    private readonly AlpacaClient _alpaca;

    public RotatorController(IRotatorMediator rotator, EquipmentSelectionService equipment, IndiDiscoveryService indi, AlpacaClient alpaca)
    {
        _rotator = rotator;
        _equipment = equipment;
        _indi = indi;
        _alpaca = alpaca;
    }

    [HttpGet("info")]
    public IActionResult GetInfo()
    {
        var selected = _equipment.GetSelected(DeviceKind.Rotator);
        if (selected?.Provider == EquipmentProvider.Indi)
        {
            var i = _indi.TryBuildRotatorInfo(selected.UniqueId);
            if (i != null)
                return Ok(new
                {
                    connected = i.Connected, name = i.Name, position = i.Position,
                    mechanicalPosition = i.MechanicalPosition, stepSize = i.StepSize,
                    isMoving = i.IsMoving, canReverse = i.CanReverse, reverse = i.Reverse
                });
        }
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
        var selected = _equipment.GetSelected(DeviceKind.Rotator);
        if (selected?.Provider == EquipmentProvider.Indi)
        {
            var i = _indi.TryBuildRotatorInfo(selected.UniqueId);
            if (i != null)
                return Ok(new { connected = i.Connected, position = i.Position, mechanicalPosition = i.MechanicalPosition, isMoving = i.IsMoving });
        }
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
    public Task<IActionResult> Connect([FromBody] ConnectRequest? request) =>
        IndiDeviceConnectFlow.ConnectAsync(_equipment, _indi, DeviceKind.Rotator, "Rotator", request, HttpContext.RequestAborted, _alpaca);

    [HttpPost("disconnect")]
    public async Task<IActionResult> Disconnect()
    {
        var selected = _equipment.GetSelected(DeviceKind.Rotator);
        if (selected?.Provider == EquipmentProvider.Indi)
            _indi.RecordConnectionIntent(selected.UniqueId, false);
            await _indi.DisconnectDeviceAsync(selected.UniqueId, HttpContext.RequestAborted);
        _equipment.Disconnect(DeviceKind.Rotator);
        return Ok(new { success = true, message = "Rotator disconnected" });
    }

    [HttpPost("move")]
    public async Task<IActionResult> Move([FromBody] RotatorMoveRequest request)
    {
        var selected = _equipment.GetSelected(DeviceKind.Rotator);
        if (_equipment.IsConnected(DeviceKind.Rotator) && selected?.Provider == EquipmentProvider.Indi)
        {
            var ok = await _indi.RotatorMoveAsync(selected.UniqueId, request.Position, HttpContext.RequestAborted);
            if (!ok) return StatusCode(503, new { success = false, message = "Rotator does not expose ABS_ROTATOR_ANGLE" });
            return Ok(new { success = true, message = $"Rotator moving to {request.Position:F2}", position = request.Position });
        }
        var movedTo = await _rotator.Move((float)request.Position, CancellationToken.None);
        return Ok(new
        {
            success = true,
            message = $"Rotator moved to {movedTo:F2}",
            position = movedTo
        });
    }

    [HttpPost("halt")]
    public async Task<IActionResult> Halt()
    {
        var selected = _equipment.GetSelected(DeviceKind.Rotator);
        if (_equipment.IsConnected(DeviceKind.Rotator) && selected?.Provider == EquipmentProvider.Indi)
        {
            await _indi.RotatorAbortAsync(selected.UniqueId, HttpContext.RequestAborted);
            return Ok(new { success = true, message = "Rotator halt requested" });
        }
        var device = _rotator.GetDevice() as IRotator;
        device?.Halt();

        return Ok(new
        {
            success = true,
            message = "Rotator halt requested"
        });
    }

    /// <summary>Move by mechanical (encoder) angle rather than sky position angle. Framing
    /// workflows use sky angle (/move); alignment / calibration workflows use mechanical.</summary>
    [HttpPost("moveMechanical")]
    public async Task<IActionResult> MoveMechanical([FromBody] RotatorMoveRequest request)
    {
        var selected = _equipment.GetSelected(DeviceKind.Rotator);
        if (_equipment.IsConnected(DeviceKind.Rotator) && selected?.Provider == EquipmentProvider.Indi)
        {
            var ok = await _indi.RotatorMoveAsync(selected.UniqueId, request.Position, HttpContext.RequestAborted);
            if (!ok) return StatusCode(503, new { success = false, message = "Rotator does not expose ABS_ROTATOR_ANGLE" });
            return Ok(new { success = true, message = $"Rotator mechanical moving to {request.Position:F2}", mechanicalPosition = request.Position });
        }
        var movedTo = await _rotator.MoveMechanical((float)request.Position, CancellationToken.None);
        return Ok(new { success = true, message = $"Rotator mechanical moved to {movedTo:F2}", mechanicalPosition = movedTo });
    }

    public record SyncRequest(double SkyAngle);

    /// <summary>Tell the rotator "your current mechanical position corresponds to this sky
    /// angle" — used after plate-solve to correct the sky/mechanical offset without moving.</summary>
    [HttpPost("sync")]
    public async Task<IActionResult> Sync([FromBody] SyncRequest request)
    {
        var selected = _equipment.GetSelected(DeviceKind.Rotator);
        if (_equipment.IsConnected(DeviceKind.Rotator) && selected?.Provider == EquipmentProvider.Indi)
        {
            var ok = await _indi.RotatorSyncAsync(selected.UniqueId, request.SkyAngle, HttpContext.RequestAborted);
            if (!ok) return StatusCode(503, new { success = false, message = "Rotator does not expose SYNC_ROTATOR_ANGLE" });
            return Ok(new { success = true, message = $"Synced sky angle to {request.SkyAngle:F2}" });
        }
        _rotator.Sync((float)request.SkyAngle);
        return Ok(new { success = true, message = $"Synced sky angle to {request.SkyAngle:F2}" });
    }

    public record ReverseRequest(bool Reverse);

    /// <summary>Flip the direction sense — some focal train / mirror combinations invert the
    /// rotator relative to the sky, so the driver needs to invert outgoing moves.</summary>
    [HttpPost("reverse")]
    public async Task<IActionResult> SetReverse([FromBody] ReverseRequest request)
    {
        var selected = _equipment.GetSelected(DeviceKind.Rotator);
        if (_equipment.IsConnected(DeviceKind.Rotator) && selected?.Provider == EquipmentProvider.Indi)
        {
            var ok = await _indi.RotatorReverseAsync(selected.UniqueId, request.Reverse, HttpContext.RequestAborted);
            if (!ok) return StatusCode(503, new { success = false, message = "Rotator does not expose ROTATOR_REVERSE" });
            return Ok(new { success = true, reverse = request.Reverse });
        }
        var device = _rotator.GetDevice() as IRotator;
        if (device == null) return StatusCode(503, new { success = false, message = "Rotator not connected" });
        device.Reverse = request.Reverse;
        return Ok(new { success = true, reverse = request.Reverse });
    }
}
