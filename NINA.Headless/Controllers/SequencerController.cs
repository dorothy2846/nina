using Microsoft.AspNetCore.Mvc;
using NINA.Headless.Services;

namespace NINA.Headless.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class SequencerController : ControllerBase
{
    private readonly NinaStateService _state;

    public SequencerController(NinaStateService state)
    {
        _state = state;
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        var camera = _state.CameraInfo;
        var telescope = _state.TelescopeInfo;
        var guider = _state.GuiderInfo;

        var running = (camera?.IsExposing ?? false) || (telescope?.Slewing ?? false) || (guider?.Connected ?? false);

        return Ok(new
        {
            running,
            progress = running ? 0.5 : 0.0,
            currentTarget = (string?)null,
            currentStep = running ? "equipment-active" : "idle",
            estimatedFinish = (string?)null,
            totalTargets = 0,
            completedTargets = 0,
            totalExposures = 0,
            completedExposures = 0,
            equipment = _state.BuildEquipmentStatus(),
            timestamp = DateTime.UtcNow
        });
    }

    [HttpPost("start")]
    public IActionResult Start()
    {
        return Ok(new
        {
            success = true,
            message = "Sequence start requested (stub)"
        });
    }

    [HttpPost("stop")]
    public IActionResult Stop()
    {
        return Ok(new
        {
            success = true,
            message = "Sequence stop requested (stub)"
        });
    }

    [HttpPost("reset")]
    public IActionResult Reset()
    {
        return Ok(new
        {
            success = true,
            message = "Sequence reset requested (stub)"
        });
    }

    [HttpGet("targets")]
    public IActionResult GetTargets()
    {
        return Ok(new
        {
            targets = new[]
            {
                new
                {
                    name = "M31 - Andromeda Galaxy (stub)",
                    ra = 10.6847,
                    dec = 41.2687,
                    status = "pending",
                    progress = 0.0,
                    exposures = new[]
                    {
                        new { filter = "L", exposureTime = 300.0, count = 20, completed = 0 },
                        new { filter = "R", exposureTime = 180.0, count = 15, completed = 0 },
                        new { filter = "G", exposureTime = 180.0, count = 15, completed = 0 },
                        new { filter = "B", exposureTime = 180.0, count = 15, completed = 0 }
                    }
                }
            }
        });
    }
}
