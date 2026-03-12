using Microsoft.AspNetCore.Mvc;

namespace NINA.Headless.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class EquipmentController : ControllerBase
{
    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        return Ok(new
        {
            camera = new { connected = false, name = "ZWO ASI294MC Pro (Stub)" },
            telescope = new { connected = false, name = "Simulator (Stub)" },
            guider = new { connected = false, name = "PHD2 (Stub)" },
            focuser = new { connected = false, name = "No Focuser" },
            filterWheel = new { connected = false, name = "No Filter Wheel" },
            rotator = new { connected = false, name = "No Rotator" },
            dome = new { connected = false, name = "No Dome" },
            safetyMonitor = new { connected = false, name = "No Safety Monitor" },
            flatDevice = new { connected = false, name = "No Flat Device" },
            weather = new { connected = false, name = "No Weather Station" },
            @switch = new { connected = false, name = "No Switch" }
        });
    }

    [HttpGet("devices")]
    public IActionResult GetDevices()
    {
        return Ok(new
        {
            cameras = new[]
            {
                new { id = "asi294mc_stub", name = "ZWO ASI294MC Pro (Stub)" },
                new { id = "simulator", name = "N.I.N.A. Simulator" }
            },
            telescopes = new[]
            {
                new { id = "simulator", name = "N.I.N.A. Simulator" },
                new { id = "ascom_eqmod", name = "EQMOD ASCOM HEQ5/6 (Stub)" }
            },
            guiders = new[]
            {
                new { id = "phd2", name = "PHD2" },
                new { id = "internal", name = "N.I.N.A. Internal Guider" }
            },
            focusers = new[]
            {
                new { id = "simulator", name = "N.I.N.A. Simulator" }
            },
            filterWheels = new[]
            {
                new { id = "manual", name = "Manual Filter Wheel" },
                new { id = "simulator", name = "N.I.N.A. Simulator" }
            },
            rotators = Array.Empty<object>(),
            domes = Array.Empty<object>(),
            safetyMonitors = Array.Empty<object>(),
            flatDevices = Array.Empty<object>(),
            weather = Array.Empty<object>(),
            switches = Array.Empty<object>()
        });
    }
}
