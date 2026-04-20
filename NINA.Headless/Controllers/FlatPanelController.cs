using Microsoft.AspNetCore.Mvc;
using NINA.Headless.Models;
using NINA.Headless.Services;

namespace NINA.Headless.Controllers;

/// <summary>
/// Flat panel / light box control. Targets the INDI <c>FLAT_LIGHT_CONTROL</c> (on/off
/// switch) and <c>FLAT_LIGHT_INTENSITY</c> (brightness number) vectors, which are the
/// standard for LightBox-interface drivers (Pegasus FlatMaster, Alnitak, etc.).
/// The FlatWizardService already uses the same INDI bindings during flat captures —
/// this controller is the direct-control surface for the iOS Equipment screen.
/// </summary>
[ApiController]
[Route("api/v1/[controller]")]
public class FlatPanelController : ControllerBase
{
    private readonly EquipmentSelectionService _equipment;
    private readonly IndiDiscoveryService _indi;
    private readonly AlpacaClient _alpaca;

    public FlatPanelController(EquipmentSelectionService equipment, IndiDiscoveryService indi, AlpacaClient alpaca)
    {
        _equipment = equipment;
        _indi = indi;
        _alpaca = alpaca;
    }

    [HttpGet("info")]
    public IActionResult GetInfo() => GetStatus();

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        if (!_equipment.IsConnected(DeviceKind.FlatPanel))
            return Ok(new { connected = false, name = "Not connected", brightness = (int?)null });

        var selected = _equipment.GetSelected(DeviceKind.FlatPanel);
        return selected == null
            ? Ok(new { connected = false, name = "Not connected", brightness = (int?)null })
            : Ok(_indi.BuildFlatPanelStatus(selected));
    }

    [HttpPost("connect")]
    public Task<IActionResult> Connect([FromBody] ConnectRequest? request) =>
        IndiDeviceConnectFlow.ConnectAsync(_equipment, _indi, DeviceKind.FlatPanel, "FlatPanel", request, HttpContext.RequestAborted, _alpaca);

    [HttpPost("disconnect")]
    public Task<IActionResult> Disconnect() =>
        IndiDeviceConnectFlow.DisconnectAsync(_equipment, _indi, DeviceKind.FlatPanel, "FlatPanel", HttpContext.RequestAborted, _alpaca);

    public record BrightnessRequest(int Brightness);

    /// <summary>Set brightness and turn the light on in a single call. Drivers typically
    /// auto-switch the light on the moment intensity is set, but we send both to be
    /// deterministic across firmware variants.</summary>
    [HttpPost("brightness")]
    public async Task<IActionResult> SetBrightness([FromBody] BrightnessRequest request)
    {
        var selected = _equipment.GetSelected(DeviceKind.FlatPanel);
        if (selected == null || !_equipment.IsConnected(DeviceKind.FlatPanel))
            return BadRequest(new { success = false, message = "Flat panel not connected" });

        var ok = await _indi.SetFlatPanelBrightnessAsync(selected.UniqueId, request.Brightness, HttpContext.RequestAborted);
        if (ok) await _indi.SetFlatPanelOnAsync(selected.UniqueId, true, HttpContext.RequestAborted);
        return Ok(new { success = ok });
    }

    [HttpPost("off")]
    public async Task<IActionResult> Off()
    {
        var selected = _equipment.GetSelected(DeviceKind.FlatPanel);
        if (selected == null || !_equipment.IsConnected(DeviceKind.FlatPanel))
            return BadRequest(new { success = false, message = "Flat panel not connected" });

        var ok = await _indi.SetFlatPanelOnAsync(selected.UniqueId, false, HttpContext.RequestAborted);
        return Ok(new { success = ok });
    }
}
