using Microsoft.AspNetCore.Mvc;
using NINA.Headless.Services;
using System.Linq;

namespace NINA.Headless.Controllers;

[ApiController]
[Route("api/v1/camera")]
public class ImageStretchController : ControllerBase
{
    private readonly NinaStateService _state;

    public ImageStretchController(NinaStateService state)
    {
        _state = state;
    }

    /// <summary>
    /// Sets manual stretch parameters for the live view stream.
    /// </summary>
    [HttpPost("stretch")]
    public IActionResult SetStretch([FromBody] StretchRequest request)
    {
        // In reality, this would hit the active NINA.Image.ImageAnalysis module to adjust clipping points.
        // For the headless setup, we save the parameters to state for the ImageStreamController to parse.
        _state.SetImageStretchParams(request.BlackPoint, request.WhitePoint, request.AutoStretch);
        
        return Ok(new
        {
            success = true,
            status = request.AutoStretch ? "Auto-stretch enabled" : "Manual stretch applied",
            blackPoint = request.BlackPoint,
            whitePoint = request.WhitePoint
        });
    }

    /// <summary>
    /// Retrieves the histogram data array of the latest exposure for the iOS UI plot.
    /// </summary>
    [HttpGet("histogram")]
    public IActionResult GetHistogram()
    {
        // Dummy histogram array representation
        var histogram = Enumerable.Range(0, 256).Select(x => 0).ToArray();
        return Ok(new { histogram });
    }
}

public class StretchRequest
{
    public double BlackPoint { get; set; }
    public double WhitePoint { get; set; }
    public bool AutoStretch { get; set; }
}
