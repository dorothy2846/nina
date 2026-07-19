using Microsoft.AspNetCore.Mvc;
using NINA.Headless.Services;

namespace NINA.Headless.Controllers;

/// <summary>BETA planet auto-centering — mount pulse-guiding on the planet's
/// own centroid. See PlanetCenteringService for the honesty constraints
/// (mandatory calibration, disk-size minimums, jitter filtering).</summary>
[ApiController]
[Route("api/v1/camera/planetary-center")]
public class PlanetCenteringController : ControllerBase
{
    private readonly PlanetCenteringService _centering;

    public PlanetCenteringController(PlanetCenteringService centering)
    {
        _centering = centering;
    }

    [HttpPost("start")]
    public IActionResult Start()
    {
        if (!_centering.Start(out var error))
            return StatusCode(409, new { success = false, message = error });
        return Ok(new { success = true, message = "캘리브레이션부터 시작합니다" });
    }

    [HttpPost("stop")]
    public IActionResult Stop()
    {
        _centering.Stop();
        return Ok(new { success = true });
    }

    [HttpGet("status")]
    public IActionResult Status() => Ok(_centering.GetStatus());

    /// <summary>Verification endpoint: run the EXACT production centroid
    /// measurement on an uploaded JPEG.</summary>
    [HttpPost("test")]
    public async Task<IActionResult> CentroidTest()
    {
        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms, HttpContext.RequestAborted);
        var c = PlanetCenteringService.ComputeCentroid(ms.ToArray());
        if (c == null) return Ok(new { found = false });
        return Ok(new { found = true, x = c.X, y = c.Y, pixelCount = c.PixelCount, width = c.Width, height = c.Height });
    }
}
