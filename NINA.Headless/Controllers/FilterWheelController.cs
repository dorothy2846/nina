using Microsoft.AspNetCore.Mvc;
using NINA.Core.Model.Equipment;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Headless.Models;
using System.Linq;

namespace NINA.Headless.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class FilterWheelController : ControllerBase
{
    private readonly IFilterWheelMediator _filterWheel;

    public FilterWheelController(IFilterWheelMediator filterWheel)
    {
        _filterWheel = filterWheel;
    }

    [HttpGet("info")]
    public IActionResult GetInfo()
    {
        var info = _filterWheel.GetInfo();
        var device = _filterWheel.GetDevice() as IFilterWheel;

        if (info == null)
        {
            return Ok(new { connected = false, name = "Not connected" });
        }

        var filters = device?.Filters?
            .Select(f => new { position = f.Position, name = f.Name })
            .ToArray();

        return Ok(new
        {
            connected = info.Connected,
            name = info.Name,
            filterCount = filters?.Length ?? 0,
            filters,
            currentPosition = info.SelectedFilter?.Position,
            currentFilterName = info.SelectedFilter?.Name
        });
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        var info = _filterWheel.GetInfo();
        if (info == null)
        {
            return Ok(new { connected = false, state = "disconnected" });
        }

        return Ok(new
        {
            connected = info.Connected,
            currentPosition = info.SelectedFilter?.Position,
            currentFilterName = info.SelectedFilter?.Name,
            isMoving = info.IsMoving
        });
    }

    [HttpPost("connect")]
    public async Task<IActionResult> Connect([FromBody] ConnectRequest? request)
    {
        var success = await _filterWheel.Connect();
        if (!success)
        {
            return StatusCode(503, new { success = false, message = "Failed to connect filter wheel", deviceId = request?.DeviceId ?? "default" });
        }

        return Ok(new
        {
            success = true,
            message = "Filter wheel connected",
            deviceId = request?.DeviceId ?? "default"
        });
    }

    [HttpPost("disconnect")]
    public async Task<IActionResult> Disconnect()
    {
        await _filterWheel.Disconnect();
        return Ok(new
        {
            success = true,
            message = "Filter wheel disconnected"
        });
    }

    [HttpPost("change")]
    public async Task<IActionResult> Change([FromBody] FilterChangeRequest request)
    {
        var device = _filterWheel.GetDevice() as IFilterWheel;
        var filter = device?.Filters?.FirstOrDefault(f => f.Position == request.Position);
        if (filter == null)
        {
            return BadRequest(new { success = false, message = $"Filter position {request.Position} not found" });
        }

        var changed = await _filterWheel.ChangeFilter(filter, CancellationToken.None);
        if (changed == null)
        {
            return StatusCode(500, new { success = false, message = "Filter change failed" });
        }

        return Ok(new
        {
            success = true,
            message = $"Filter changed to {changed.Name}",
            position = changed.Position,
            filterName = changed.Name
        });
    }
}
