using Microsoft.AspNetCore.Mvc;
using NINA.Core.Model.Equipment;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Headless.Models;
using NINA.Headless.Services;
using System.Linq;

namespace NINA.Headless.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class FilterWheelController : ControllerBase
{
    private readonly IFilterWheelMediator _filterWheel;
    private readonly EquipmentSelectionService _equipment;
    private readonly IndiDiscoveryService _indi;
    private readonly AlpacaClient _alpaca;
    private readonly ILogger<FilterWheelController> _log;

    /// <summary>Last filter position that ChangeFilter successfully transitioned TO.
    /// Drives the focus-offset delta calculation — without it we'd have to re-query
    /// the wheel every call and rely on the driver's current position, which can lag
    /// the in-flight move. Reset on disconnect.</summary>
    private static int? _lastAppliedPosition;

    public FilterWheelController(IFilterWheelMediator filterWheel,
        EquipmentSelectionService equipment, IndiDiscoveryService indi, AlpacaClient alpaca,
        ILogger<FilterWheelController> log)
    {
        _filterWheel = filterWheel;
        _equipment = equipment;
        _indi = indi;
        _alpaca = alpaca;
        _log = log;
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
        var selected = _equipment.GetSelected(DeviceKind.FilterWheel);
        if (_equipment.IsConnected(DeviceKind.FilterWheel) && selected?.Provider == EquipmentProvider.Indi)
        {
            var s = _indi.BuildFilterWheelStatus(selected.UniqueId);
            if (s != null) return Ok(s);
        }
        return Ok(new { connected = false, name = "Not connected" });
    }

    [HttpPost("connect")]
    public Task<IActionResult> Connect([FromBody] ConnectRequest? request) =>
        IndiDeviceConnectFlow.ConnectAsync(_equipment, _indi, DeviceKind.FilterWheel, "Filter wheel", request, HttpContext.RequestAborted, _alpaca);

    [HttpPost("disconnect")]
    public async Task<IActionResult> Disconnect()
    {
        var selected = _equipment.GetSelected(DeviceKind.FilterWheel);
        if (selected?.Provider == EquipmentProvider.Indi)
            _indi.RecordConnectionIntent(selected.UniqueId, false);
            await _indi.DisconnectDeviceAsync(selected.UniqueId, HttpContext.RequestAborted);
        _equipment.Disconnect(DeviceKind.FilterWheel);
        _lastAppliedPosition = null;
        return Ok(new { success = true, message = "Filter wheel disconnected" });
    }

    [HttpPost("change")]
    public async Task<IActionResult> Change([FromBody] FilterChangeRequest request)
    {
        var fromPosition = _lastAppliedPosition;
        var selected = _equipment.GetSelected(DeviceKind.FilterWheel);
        if (_equipment.IsConnected(DeviceKind.FilterWheel) && selected?.Provider == EquipmentProvider.Indi)
        {
            await _indi.FilterWheelChangeAsync(selected.UniqueId, request.Position, HttpContext.RequestAborted);
            _lastAppliedPosition = request.Position;
            await ApplyFilterFocusOffsetAsync(fromPosition, request.Position, HttpContext.RequestAborted);
            return Ok(new { success = true, message = $"Filter change to position {request.Position}", position = request.Position });
        }

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
        _lastAppliedPosition = changed.Position;
        await ApplyFilterFocusOffsetAsync(fromPosition, changed.Position, HttpContext.RequestAborted);

        return Ok(new
        {
            success = true,
            message = $"Filter changed to {changed.Name}",
            position = changed.Position,
            filterName = changed.Name
        });
    }

    /// <summary>Move the focuser by the delta between the old filter's offset and the
    /// new one so par-focal filter sets stay sharp through a filter change. No-op when
    /// we don't know the previous filter (first change after connect), when the focuser
    /// isn't connected, or when both offsets are zero. Failures are swallowed — a
    /// failed offset move must not fail the filter change itself.</summary>
    private async Task ApplyFilterFocusOffsetAsync(int? fromPosition, int toPosition, CancellationToken ct)
    {
        if (fromPosition == null || fromPosition == toPosition) return;

        var fromOffset = AppConfigController.GetFilterSetting(fromPosition.Value)?.FocusOffset ?? 0;
        var toOffset = AppConfigController.GetFilterSetting(toPosition)?.FocusOffset ?? 0;
        var delta = toOffset - fromOffset;
        if (delta == 0) return;

        var focuser = _equipment.GetSelected(DeviceKind.Focuser);
        if (focuser == null || !_equipment.IsConnected(DeviceKind.Focuser)) return;
        if (focuser.Provider != EquipmentProvider.Indi) return;

        try
        {
            var currentPos = _indi.GetFocuserPosition(focuser.UniqueId);
            if (currentPos == null) return;
            await _indi.FocuserMoveAsync(focuser.UniqueId, currentPos.Value + delta, ct);
            _log.LogInformation("Filter offset applied: {Delta} steps ({From}→{To})", delta, fromOffset, toOffset);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Filter focus offset move failed");
        }
    }
}
