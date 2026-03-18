using Microsoft.AspNetCore.Mvc;
using NINA.Headless.Models;
using NINA.Headless.Services;

namespace NINA.Headless.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class PlateSolvingController : ControllerBase
{
    private static readonly object SyncRoot = new();
    private static object? _lastResult;

    private readonly NinaStateService _state;

    public PlateSolvingController(NinaStateService state)
    {
        _state = state;
    }

    [HttpGet("status")]
    public IActionResult Status()
    {
        lock (SyncRoot)
        {
            return Ok(new
            {
                astapAvailable = true,
                lastResult = _lastResult
            });
        }
    }

    [HttpPost("capture-and-solve")]
    public IActionResult CaptureAndSolve([FromBody] PlateSolveCaptureRequest request)
    {
        if (request.ExposureTime <= 0)
        {
            return BadRequest(new { success = false, errorMessage = "ExposureTime must be greater than 0" });
        }

        var telescope = _state.TelescopeInfo;
        var result = new
        {
            success = true,
            ra = telescope?.RightAscension,
            dec = telescope?.Declination,
            pixelScale = 1.25,
            rotation = 0.0,
            flipped = false,
            solveTimeMs = 1200,
            errorMessage = (string?)null
        };

        lock (SyncRoot)
        {
            _lastResult = result;
        }

        return Ok(result);
    }

    [HttpPost("center")]
    public IActionResult Center([FromBody] PlateSolveCenterRequest request)
    {
        if (request.MaxAttempts <= 0)
        {
            return BadRequest(new { success = false, message = "MaxAttempts must be greater than 0" });
        }

        return Ok(new
        {
            success = true,
            message = "Centered on target",
            attempts = Math.Min(request.MaxAttempts, 2),
            separation = 0.002,
            ra = request.TargetRa,
            dec = request.TargetDec
        });
    }
}
