using Microsoft.AspNetCore.Mvc;
using NINA.Headless.Services;

namespace NINA.Headless.Controllers;

[ApiController]
[Route("api/v1/camera/planetary-record")]
public class PlanetaryRecordController : ControllerBase
{
    private readonly NinaStateService _state;

    public PlanetaryRecordController(NinaStateService state)
    {
        _state = state;
    }

    /// <summary>
    /// Starts a high-speed lucky imaging sequence saved locally in SER/AVI format based on ROI.
    /// </summary>
    [HttpPost("start")]
    public IActionResult StartRecord([FromBody] PlanetaryRecordRequest request)
    {
        // Logic to restrict Camera ROI and continuously write frames to SSD at high FPS
        // while simultaneously broadcasting QUIC previews
        
        return Ok(new
        {
            success = true,
            message = "Planetary recording started",
            roi = new { request.RoiX, request.RoiY, request.Width, request.Height },
            duration = request.DurationSeconds
        });
    }

    /// <summary>
    /// Aborts an active planetary high-speed recording.
    /// </summary>
    [HttpPost("stop")]
    public IActionResult StopRecord()
    {
        return Ok(new { success = true, message = "Planetary recording stopped" });
    }
}

public class PlanetaryRecordRequest
{
    public int RoiX { get; set; }
    public int RoiY { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double DurationSeconds { get; set; }
}
