using Microsoft.AspNetCore.Mvc;
using NINA.Headless.Models;
using NINA.Headless.Services;

namespace NINA.Headless.Controllers;

/// <summary>
/// Power box / relay control. INDI aggregates switches via AUX_INTERFACE-flagged drivers
/// (Pegasus UPB, DeepSkyDad, Arduino Relay, etc.). Each driver exposes its own set of
/// switch vectors — IndiDiscoveryService flattens them into a channel list the UI can
/// render as individual toggles.
/// </summary>
[ApiController]
[Route("api/v1/[controller]")]
public class SwitchController : ControllerBase
{
    private readonly EquipmentSelectionService _equipment;
    private readonly IndiDiscoveryService _indi;
    private readonly AlpacaClient _alpaca;

    public SwitchController(EquipmentSelectionService equipment, IndiDiscoveryService indi, AlpacaClient alpaca)
    {
        _equipment = equipment;
        _indi = indi;
        _alpaca = alpaca;
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        if (!_equipment.IsConnected(DeviceKind.Switch))
            return Ok(new { connected = false, name = "Not connected", channels = Array.Empty<object>() });

        var selected = _equipment.GetSelected(DeviceKind.Switch);
        return selected == null
            ? Ok(new { connected = false, name = "Not connected", channels = Array.Empty<object>() })
            : Ok(_indi.BuildSwitchStatus(selected));
    }

    [HttpPost("connect")]
    public Task<IActionResult> Connect([FromBody] ConnectRequest? request) =>
        IndiDeviceConnectFlow.ConnectAsync(_equipment, _indi, DeviceKind.Switch, "Switch", request, HttpContext.RequestAborted, _alpaca);

    [HttpPost("disconnect")]
    public Task<IActionResult> Disconnect() =>
        IndiDeviceConnectFlow.DisconnectAsync(_equipment, _indi, DeviceKind.Switch, "Switch", HttpContext.RequestAborted, _alpaca);

    public record ToggleRequest(string Key, bool On);

    [HttpPost("toggle")]
    public async Task<IActionResult> Toggle([FromBody] ToggleRequest request)
    {
        var selected = _equipment.GetSelected(DeviceKind.Switch);
        if (selected == null || !_equipment.IsConnected(DeviceKind.Switch))
            return BadRequest(new { success = false, message = "Switch not connected" });

        var ok = await _indi.ToggleSwitchAsync(selected.UniqueId, request.Key, request.On, HttpContext.RequestAborted);
        return Ok(new { success = ok });
    }
}
