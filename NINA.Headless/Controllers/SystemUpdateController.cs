using Microsoft.AspNetCore.Mvc;
using NINA.Headless.Services;
using NINA.Headless.Services.Remote;

namespace NINA.Headless.Controllers;

/// <summary>
/// APT-based Phase 1 update surface. Paired devices can list available upgrades and
/// kick off an apt-get install. Phase 2 will replace this with A/B image swap once
/// the USB appliance pipeline lands.
/// </summary>
[ApiController]
[Route("api/v1/system/[controller]")]
public class UpdateController : ControllerBase
{
    private readonly SystemUpdateService _updates;
    private readonly PairedDeviceStore _paired;

    public UpdateController(SystemUpdateService updates, PairedDeviceStore paired)
    {
        _updates = updates;
        _paired = paired;
    }

    /// <summary>Bearer token gate — apt-get can reboot the box, so only paired devices
    /// drive updates. Same shape as NetworkController's auth check.</summary>
    private bool IsAuthorized()
    {
        var auth = Request.Headers.Authorization.ToString();
        var token = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..].Trim() : "";
        return _paired.Verify(token) != null;
    }

    [HttpGet("check")]
    public async Task<IActionResult> Check()
    {
        if (!IsAuthorized()) return Unauthorized();
        var result = await _updates.CheckAsync(HttpContext.RequestAborted);
        return Ok(result);
    }

    [HttpPost("apply")]
    public IActionResult Apply()
    {
        if (!IsAuthorized()) return Unauthorized();
        var result = _updates.BeginApply();
        return Ok(result);
    }

    /// <summary>Poll the currently-running or most-recently-finished job. `null` when
    /// nothing has ever run. iOS polls this every second while an update is active.</summary>
    [HttpGet("status")]
    public IActionResult Status()
    {
        if (!IsAuthorized()) return Unauthorized();
        var job = _updates.CurrentJob;
        if (job == null) return Ok(new { active = false });
        return Ok(new
        {
            active = job.Running,
            jobId = job.Id,
            startedAt = job.StartedAt,
            finishedAt = job.FinishedAt,
            exitCode = job.ExitCode,
            logTail = job.Log.TakeLast(100).ToArray()
        });
    }
}
