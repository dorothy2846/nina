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
    private readonly OwnerAccountStore _owner;
    private readonly SupabaseAuthService _supabase;

    public RemoteController(RendezvousConfigStore store, ObservatoryIdentity identity, PairedDeviceStore paired,
        OwnerAccountStore owner, SupabaseAuthService supabase)
    {
        _store = store;
        _identity = identity;
        _paired = paired;
        _owner = owner;
        _supabase = supabase;
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
            }),
            // Account-based ownership snapshot. Surfacing both the owner UUID and
            // the co-owner list lets the iOS settings UI show "this is your
            // observatory" vs "you're a co-owner here" vs "unclaimed". UUIDs
            // aren't secrets — Supabase exposes them on every authenticated
            // request to its own API — so this stays on the unauthenticated
            // /config endpoint alongside paired devices.
            ownerAccount = _owner.Owner,
            coOwnerAccounts = _owner.ListCoOwners()
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

    public record CoOwnerRequest(string UserUuid);

    /// <summary>Owner-only: invite another Supabase account to share access to
    /// this observatory. The supplied UUID gets pushed onto the co-owner list;
    /// any subsequent authenticated request whose JWT resolves to that UUID
    /// passes IsAuthorizedAsync. Re-adding an existing co-owner is a no-op.
    /// Returns the updated list so the UI can refresh without a second call.</summary>
    [HttpPost("co-owners")]
    public async Task<IActionResult> AddCoOwner([FromBody] CoOwnerRequest req)
    {
        if (!await IsAuthorizedAsync()) return Forbid();
        if (string.IsNullOrWhiteSpace(req?.UserUuid))
            return BadRequest(new { error = "missing_userUuid" });
        var coOwners = _owner.AddCoOwner(req.UserUuid.Trim());
        return Ok(new { owner = _owner.Owner, coOwners });
    }

    /// <summary>Owner-only: revoke a co-owner. Idempotent — removing someone
    /// not on the list returns the unchanged list. Doesn't kill any active
    /// connection from the removed account; that drops naturally on the next
    /// request when IsAuthorizedAsync returns false.</summary>
    [HttpDelete("co-owners/{userUuid}")]
    public async Task<IActionResult> RemoveCoOwner(string userUuid)
    {
        if (!await IsAuthorizedAsync()) return Forbid();
        _owner.RemoveCoOwner(userUuid);
        return Ok(new { owner = _owner.Owner, coOwners = _owner.ListCoOwners() });
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
        _owner.Reset();
        _identity.Reset();
        return NoContent();
    }

    /// <summary>Authorize the caller via Supabase JWT first; fall back to the
    /// legacy LAN-trust bearer token when Supabase verify is unavailable
    /// (offline observatory, pre-account-era pairing). Fallback runs even
    /// for rendezvous-tunnelled requests because the tunnel forwards from
    /// loopback so we can't distinguish LAN from remote here — the gate is
    /// "possesses a valid token", whichever kind.</summary>
    private async Task<bool> IsAuthorizedAsync()
    {
        var auth = Request.Headers.Authorization.ToString();
        var token = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..].Trim() : "";
        if (string.IsNullOrEmpty(token)) return false;

        var uuid = await _supabase.VerifyAsync(token, HttpContext.RequestAborted);
        if (uuid == null) return _paired.Verify(token) != null;
        return _owner.IsAuthorized(uuid) || _owner.TryClaim(uuid);
    }
}
