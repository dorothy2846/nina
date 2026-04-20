using Microsoft.AspNetCore.Mvc;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Headless.Models;
using NINA.Headless.Services;

namespace NINA.Headless.Controllers;

/// <summary>
/// Safety monitor. Single binary input — "is it safe to be imaging?" — surfaced to iOS so
/// the unattended-mode sequencer (or the operator, watching the app) can abort and park
/// when the monitor trips. Hardware variants: cloud/rain sensors, roof-switch contacts,
/// watchdog links to a weather station. The driver does the interpretation; we just
/// relay the IsSafe boolean.
/// </summary>
[ApiController]
[Route("api/v1/[controller]")]
public class SafetyMonitorController : ControllerBase
{
    private readonly ISafetyMonitorMediator _safety;
    private readonly EquipmentSelectionService _equipment;
    private readonly IndiDiscoveryService _indi;
    private readonly AlpacaClient _alpaca;

    public SafetyMonitorController(ISafetyMonitorMediator safety,
        EquipmentSelectionService equipment, IndiDiscoveryService indi, AlpacaClient alpaca)
    {
        _safety = safety;
        _equipment = equipment;
        _indi = indi;
        _alpaca = alpaca;
    }

    [HttpGet("info")]
    public IActionResult GetInfo() => GetStatus();

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        var info = _safety.GetInfo();
        if (info == null || !info.Connected)
            return Ok(new { connected = false, name = "Not connected", isSafe = (bool?)null });

        return Ok(new
        {
            connected = true,
            name = info.Name,
            isSafe = info.IsSafe
        });
    }

    [HttpPost("connect")]
    public Task<IActionResult> Connect([FromBody] ConnectRequest? request) =>
        IndiDeviceConnectFlow.ConnectAsync(_equipment, _indi, DeviceKind.SafetyMonitor, "Safety monitor", request, HttpContext.RequestAborted, _alpaca);

    [HttpPost("disconnect")]
    public Task<IActionResult> Disconnect() =>
        IndiDeviceConnectFlow.DisconnectAsync(_equipment, _indi, DeviceKind.SafetyMonitor, "Safety monitor", HttpContext.RequestAborted, _alpaca);
}
