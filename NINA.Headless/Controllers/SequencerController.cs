using Microsoft.AspNetCore.Mvc;
using NINA.Headless.Services;

namespace NINA.Headless.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class SequencerController : ControllerBase
{
    private readonly NinaStateService _state;
    private readonly SequencerService _sequencer;

    public SequencerController(NinaStateService state, SequencerService sequencer)
    {
        _state = state;
        _sequencer = sequencer;
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        var status = _sequencer.GetStatus();

        return Ok(new
        {
            running = status.Running,
            progress = status.Progress,
            currentTarget = status.CurrentTarget,
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
        var success = _sequencer.Start();
        return Ok(new { success, message = success ? "Sequence started" : "Sequence already running" });
    }

    [HttpPost("stop")]
    public IActionResult Stop()
    {
        var success = _sequencer.Stop();
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
