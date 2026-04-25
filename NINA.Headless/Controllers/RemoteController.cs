using Microsoft.AspNetCore.Mvc;
using NINA.Headless.Services;
using NINA.Headless.Services.Remote;

namespace NINA.Headless.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class RemoteController : ControllerBase
{
    private readonly RendezvousConfigStore _store;
    private readonly ObservatoryIdentity _identity;
    private readonly PairedDeviceStore _paired;

    public RemoteController(RendezvousConfigStore store, ObservatoryIdentity identity, PairedDeviceStore paired)
    {
        _store = store;
        _identity = identity;
        _paired = paired;
    }

    /// <summary>Unauthenticated metadata read-out. iOS calls this during LAN
    /// discovery to learn the stable machineId + identity pubkey + host OS,
    /// then passes the pubkey forward so later remote connections can verify
    /// the observatory's identity via signed challenges. Also returns the list
    /// of currently paired devices (id + nickname + dates only — never the
    /// token hash or pubkey) so the iPhone pairing UI can show the owner who
    /// is already enrolled before they add another device.</summary>
    [HttpGet("config")]
    public IActionResult GetConfig()
    {
        var cfg = _store.Load();
        var paired = _paired.ListActive();
        return Ok(new
        {
            machineId = cfg.MachineId,
            rendezvousUrl = cfg.RendezvousUrl,
            enabled = cfg.Enabled,
            publicKey = Convert.ToBase64String(_identity.PublicKey),
            platform = PlatformPaths.PlatformName,
            hostname = Environment.MachineName,
            pairedCount = paired.Count,
            pairedDevices = paired.Select(d => new
            {
                id = d.Id,
                nickname = d.Nickname,
                pairedAt = d.PairedAt,
                lastSeenAt = d.LastSeenAt
            })
        });
    }

    public record UpdateRequest(string? RendezvousUrl, bool? Enabled);

    [HttpPost("config")]
    public async Task<IActionResult> UpdateConfig([FromBody] UpdateRequest req)
    {
        if (!await IsAuthorizedAsync()) return Forbid();
        var cur = _store.Load();
        var next = cur with
        {
            RendezvousUrl = string.IsNullOrWhiteSpace(req.RendezvousUrl) ? cur.RendezvousUrl : req.RendezvousUrl.Trim(),
            Enabled = req.Enabled ?? cur.Enabled
        };
        _store.Save(next);
        return Ok(new { success = true, machineId = next.MachineId, rendezvousUrl = next.RendezvousUrl, enabled = next.Enabled });
    }

    public record PairRequest(string DeviceId, string Nickname, string PublicKey);

    /// <summary>Initial pairing — single-owner LAN-trust model: anyone reachable
    /// on the LAN may pair, since presence on the WiFi is itself the credential.
    /// Returns a bearer token that iOS must carry on every subsequent
    /// authenticated call; the token never leaves the client again, and the
    /// server keeps only its SHA-256 hash.</summary>
    [HttpPost("pair")]
    public IActionResult Pair([FromBody] PairRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.DeviceId))
            return BadRequest(new { error = "invalid_request", message = "deviceId 필수" });

        // v1b accepts an empty PublicKey: the iOS client doesn't yet generate a
        // Curve25519 keypair — bearer-token auth on the signaling channel is
        // enough to lock down who can control the observatory. When v2 adds
        // challenge-response over the DataChannel we'll tighten this to require
        // a real pubkey.
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var (token, deviceId) = _paired.Pair(req.DeviceId, req.Nickname ?? "iPhone", req.PublicKey ?? "", ip);

        return CreatedAtAction(nameof(GetConfig), new
        {
            token,
            deviceId,
            machineId = _identity.MachineId,
            observatoryPublicKey = Convert.ToBase64String(_identity.PublicKey),
            observatory = new
            {
                nickname = Environment.MachineName,
                platform = PlatformPaths.PlatformName,
                hostname = Environment.MachineName
            }
        });
    }

    [HttpGet("paired-devices")]
    public async Task<IActionResult> ListPaired()
    {
        if (!await IsAuthorizedAsync()) return Forbid();
        var devices = _paired.ListActive();
        return Ok(devices.Select(d => new
        {
            id = d.Id,
            nickname = d.Nickname,
            pairedAt = d.PairedAt,
            lastSeenAt = d.LastSeenAt
        }));
    }

    [HttpDelete("paired-devices/{id}")]
    public async Task<IActionResult> Revoke(string id)
    {
        if (!await IsAuthorizedAsync()) return Forbid();
        var ok = _paired.Revoke(id);
        return ok ? NoContent() : NotFound();
    }

    public record FactoryResetRequest(string Confirm);

    /// <summary>Nuke the observatory's identity + paired device list. Used when
    /// the USB is handed off to a new owner or the iPhone list has been lost.
    /// Requires "RESET" as a double-confirm so a stolen token can't wipe the
    /// server with a single call.</summary>
    [HttpPost("factory-reset")]
    public async Task<IActionResult> FactoryReset([FromBody] FactoryResetRequest req)
    {
        if (!await IsAuthorizedAsync()) return Forbid();
        if (req.Confirm != "RESET") return BadRequest(new { error = "missing_confirm" });
        _paired.Clear();
        _identity.Reset();
        return NoContent();
    }

    /// <summary>Bearer token check against PairedDeviceStore. Only used for the
    /// admin endpoints on this controller — WebRTC signaling path does its own
    /// token check in RendezvousClient before accepting an offer.</summary>
    private Task<bool> IsAuthorizedAsync()
    {
        var auth = Request.Headers.Authorization.ToString();
        var token = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..].Trim() : "";
        return Task.FromResult(_paired.Verify(token) != null);
    }
}
