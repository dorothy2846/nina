using Microsoft.AspNetCore.Mvc;
using NINA.Headless.Models;
using NINA.Headless.Services;

namespace NINA.Headless.Controllers;

/// <summary>
/// Weather / observing-conditions read-only endpoint. INDI weather drivers publish a
/// WEATHER_PARAMETERS number vector with a subset of temperature / humidity / dewpoint /
/// pressure / wind / cloud-cover / sky-brightness. We surface whatever the driver reports
/// and leave missing fields null so the UI hides those rows.
/// </summary>
[ApiController]
[Route("api/v1/[controller]")]
public class WeatherController : ControllerBase
{
    private readonly EquipmentSelectionService _equipment;
    private readonly IndiDiscoveryService _indi;
    private readonly AlpacaClient _alpaca;

    public WeatherController(EquipmentSelectionService equipment, IndiDiscoveryService indi, AlpacaClient alpaca)
    {
        _equipment = equipment;
        _indi = indi;
        _alpaca = alpaca;
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        if (!_equipment.IsConnected(DeviceKind.Weather))
            return Ok(new { connected = false, name = "Not connected" });

        var selected = _equipment.GetSelected(DeviceKind.Weather);
        return selected == null
            ? Ok(new { connected = false, name = "Not connected" })
            : Ok(_indi.BuildWeatherStatus(selected));
    }

    [HttpPost("connect")]
    public Task<IActionResult> Connect([FromBody] ConnectRequest? request) =>
        IndiDeviceConnectFlow.ConnectAsync(_equipment, _indi, DeviceKind.Weather, "Weather", request, HttpContext.RequestAborted, _alpaca);

    [HttpPost("disconnect")]
    public Task<IActionResult> Disconnect() =>
        IndiDeviceConnectFlow.DisconnectAsync(_equipment, _indi, DeviceKind.Weather, "Weather", HttpContext.RequestAborted, _alpaca);
}
