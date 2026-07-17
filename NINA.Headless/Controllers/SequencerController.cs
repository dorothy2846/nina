using Microsoft.AspNetCore.Mvc;
using NINA.Headless.Services;

namespace NINA.Headless.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class SequencerController : ControllerBase
{
    private readonly NinaStateService _state;
    private readonly SequencerService _sequencer;
    private readonly SequenceExecutionService _executor;

    public SequencerController(NinaStateService state, SequencerService sequencer, SequenceExecutionService executor)
    {
        _state = state;
        _sequencer = sequencer;
        _executor = executor;
    }

    /// <summary>Upload an imaging plan for SERVER-SIDE execution. The app used
    /// to drive the slew/capture loop itself, dying with the phone; now the
    /// plan runs here and any client can attach to watch progress.</summary>
    [HttpPost("plan")]
    public IActionResult UploadPlan([FromBody] SequenceExecutionService.WirePlan plan)
    {
        if (!_executor.LoadPlan(plan))
            return BadRequest(new { success = false, message = "Plan must have at least one target with exposures (or a sequence is already running)" });
        return Ok(new { success = true, message = $"Plan '{plan.Name}' loaded", targets = plan.Targets.Count });
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        var status = _sequencer.GetStatus();
        var exec = _executor.GetSnapshot();

        // The executor (server-side plans) wins when it has ever been loaded;
        // the legacy fields keep their shape for older clients.
        return Ok(new
        {
            running = exec.Running || status.Running,
            progress = exec.FramesTotal > 0 ? exec.Progress : status.Progress,
            currentTarget = exec.CurrentTarget ?? status.CurrentTarget,
            currentFilter = exec.CurrentFilter,
            framesCompleted = exec.FramesCompleted,
            framesTotal = exec.FramesTotal,
            planName = exec.PlanName,
            lastError = exec.LastError,
            startedAt = exec.StartedAt,
            estimatedFinish = (string?)null,
            equipment = _state.BuildEquipmentStatus(),
            timestamp = status.Timestamp
        });
    }

    [HttpGet("list")]
    public IActionResult GetSequenceList()
    {
        var sequences = _sequencer.GetSequenceList();
        return Ok(new Dictionary<string, object> { ["Sequences"] = sequences });
    }

    [HttpPost("start")]
    public IActionResult Start()
    {
        // Server-side executor first; legacy state-only path as fallback.
        if (_executor.Start())
            return Ok(new { success = true, message = "Sequence started (server-side)" });
        var success = _sequencer.Start();
        return Ok(new { success, message = success ? "Sequence started" : "Sequence already running or no plan loaded" });
    }

    [HttpPost("stop")]
    public IActionResult Stop()
    {
        var execStopped = _executor.Stop();
        var success = _sequencer.Stop() || execStopped;
        return Ok(new { success, message = success ? "Sequence stopped" : "No sequence running" });
    }

    [HttpPost("reset")]
    public IActionResult Reset()
    {
        _sequencer.Reset();
        return Ok(new { success = true, message = "Sequence reset" });
    }

    [HttpGet("targets")]
    public IActionResult GetTargets()
    {
        var sequences = _sequencer.GetSequenceList();
        var targets = sequences.Select(s => new
        {
            name = s.Name,
            status = s.Status,
            progress = s.TotalProgress,
            exposures = s.Items.Where(i => i.Type == "Exposure").Select(i => new
            {
                filter = i.Filter ?? "L",
                exposureTime = i.ExposureTime ?? 300.0,
                count = i.TotalCount,
                completed = i.CompletedCount
            })
        });

        return Ok(new
        {
            targets
        });
    }
}
