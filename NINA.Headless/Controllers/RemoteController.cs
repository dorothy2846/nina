using Microsoft.AspNetCore.Mvc;
using NINA.Headless.Services.Remote;

namespace NINA.Headless.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class RemoteController : ControllerBase
{
    private readonly RendezvousConfigStore _store;

    public RemoteController(RendezvousConfigStore store)
    {
        _store = store;
    }

    /// <summary>Pairing read-out: iOS app fetches this on LAN discovery to learn the
    /// stable machineId + rendezvous URL, then stashes both in Keychain so it can find
    /// the observatory over the internet next time.</summary>
    [HttpGet("config")]
    public IActionResult GetConfig()
    {
        var cfg = _store.Load();
        return Ok(new
        {
            machineId = cfg.MachineId,
            rendezvousUrl = cfg.RendezvousUrl,
            enabled = cfg.Enabled
        });
    }

    public record UpdateRequest(string? RendezvousUrl, bool? Enabled);

    /// <summary>Operator escape hatch — point the server at a different rendezvous
    /// instance or temporarily disable remote access. MachineId is immutable since
    /// changing it would orphan every paired iPhone.</summary>
    [HttpPost("config")]
    public IActionResult UpdateConfig([FromBody] UpdateRequest req)
    {
        var cur = _store.Load();
        var next = cur with
        {
            RendezvousUrl = string.IsNullOrWhiteSpace(req.RendezvousUrl) ? cur.RendezvousUrl : req.RendezvousUrl.Trim(),
            Enabled = req.Enabled ?? cur.Enabled
        };
        _store.Save(next);
        return Ok(new { success = true, machineId = next.MachineId, rendezvousUrl = next.RendezvousUrl, enabled = next.Enabled });
    }
}
