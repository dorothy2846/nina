using Microsoft.AspNetCore.Mvc;
using NINA.Headless.Services.Network;
using NINA.Headless.Services.Remote;

namespace NINA.Headless.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class NetworkController : ControllerBase
{
    private readonly IApModeProvider _ap;
    private readonly ApModeConfigStore _store;
    private readonly ApModeChangeService _change;
    private readonly PairedDeviceStore _paired;

    public NetworkController(IApModeProvider ap, ApModeConfigStore store, ApModeChangeService change, PairedDeviceStore paired)
    {
        _ap = ap;
        _store = store;
        _change = change;
        _paired = paired;
    }

    /// <summary>Bearer-token gate. AP settings reveal the WPA passphrase and can brick LAN
    /// access, so anyone touching these endpoints must be a paired device. Pairing itself
    /// is a separate unauthenticated flow gated by AP-mode proximity (see PairedDeviceStore).</summary>
    private bool IsAuthorized()
    {
        var auth = Request.Headers.Authorization.ToString();
        var token = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..].Trim() : "";
        return _paired.Verify(token) != null;
    }

    /// <summary>Capability probe + current status — iOS uses this to decide whether to
    /// show the AP settings section at all and, when shown, whether the toggle is active.</summary>
    [HttpGet("ap/status")]
    public async Task<IActionResult> ApStatus()
    {
        if (!IsAuthorized()) return Unauthorized();
        var caps = await _ap.GetCapabilitiesAsync(HttpContext.RequestAborted);
        var status = await _ap.GetStatusAsync(HttpContext.RequestAborted);
        var cfg = _store.Load();
        return Ok(new
        {
            supported = caps.Supported,
            unsupportedReason = caps.UnsupportedReason,
            interfaceName = caps.InterfaceName,
            active = status.Active,
            activeSsid = status.Ssid,
            lastError = status.LastError,
            config = new {
                ssid = cfg.Ssid,
                password = cfg.Password,
                autoFallback = cfg.AutoFallback,
                effectiveAutoFallback = cfg.EffectiveAutoFallback,
                mode = cfg.Mode.ToString().ToLowerInvariant()
            }
        });
    }

    public record ApConfigRequest(string? Ssid, string? Password, bool? AutoFallback);

    /// <summary>Update SSID / password / auto-fallback. If the AP is currently broadcasting
    /// and the SSID or password changed, the new config is applied atomically with
    /// verify-and-rollback via <see cref="ApModeChangeService"/> — a failed apply leaves
    /// the previous broadcasting config intact so the caller's device doesn't get
    /// stranded. Auto-fallback-only changes skip the apply (doesn't affect live AP).</summary>
    [HttpPost("ap/config")]
    public async Task<IActionResult> SaveApConfig([FromBody] ApConfigRequest req)
    {
        if (!IsAuthorized()) return Unauthorized();
        var current = _store.Load();
        var ssid = string.IsNullOrWhiteSpace(req.Ssid) ? current.Ssid : req.Ssid!.Trim();
        var password = string.IsNullOrWhiteSpace(req.Password) ? current.Password : req.Password!;
        // WPA2-PSK requires 8-63 chars. Short passwords get silently rejected by drivers,
        // so catch it here with a clear message.
        if (password.Length < 8 || password.Length > 63)
            return BadRequest(new { success = false, message = "비밀번호는 8자 이상 63자 이하여야 합니다." });
        if (ssid.Length == 0 || ssid.Length > 32)
            return BadRequest(new { success = false, message = "SSID는 1자 이상 32자 이하여야 합니다." });

        var next = current with
        {
            Ssid = ssid,
            Password = password,
            AutoFallback = req.AutoFallback ?? current.AutoFallback
        };

        var ssidOrPasswordChanged = next.Ssid != current.Ssid || next.Password != current.Password;
        var status = await _ap.GetStatusAsync(HttpContext.RequestAborted);
        if (ssidOrPasswordChanged && status.Active)
        {
            var result = await _change.ChangeAsync(next, HttpContext.RequestAborted);
            if (!result.Success)
            {
                return StatusCode(503, new {
                    success = false,
                    message = $"AP 적용 실패 ({result.ErrorReason}). 이전 설정으로 복원되었습니다.",
                    rolledBack = result.RolledBack,
                    appliedConfig = new { ssid = result.AppliedConfig.Ssid }
                });
            }
            return Ok(new { success = true, applied = true });
        }

        _store.Save(next);
        return Ok(new { success = true, applied = false });
    }

    /// <summary>Enter AP mode immediately. Goes through the change service so a misbehaving
    /// adapter / bad persisted config surfaces as a verify_timeout rather than silently
    /// leaving the AP down. Caller is warned that their current LAN connection may drop
    /// if the server's LAN connectivity was via the same WiFi adapter.</summary>
    [HttpPost("ap/enable")]
    public async Task<IActionResult> EnableAp()
    {
        if (!IsAuthorized()) return Unauthorized();
        var cfg = _store.Load();
        var result = await _change.ChangeAsync(cfg, HttpContext.RequestAborted);
        if (!result.Success)
        {
            return StatusCode(503, new {
                success = false,
                message = $"AP 시작 실패 ({result.ErrorReason}).",
                rolledBack = result.RolledBack
            });
        }
        return Ok(new { success = true });
    }

    [HttpPost("ap/disable")]
    public async Task<IActionResult> DisableAp()
    {
        if (!IsAuthorized()) return Unauthorized();
        var ok = await _ap.DisableAsync(HttpContext.RequestAborted);
        return Ok(new { success = ok });
    }

    public record ModeRequest(string Mode);

    /// <summary>Operator-intent switch. "unattended" disables AP auto-fallback so a
    /// router blip can't strand a remote session; "field" restores default behaviour
    /// for on-site use. iOS app sets this automatically when the user opts into
    /// remote WebRTC mode.</summary>
    [HttpPost("mode")]
    public IActionResult SetMode([FromBody] ModeRequest req)
    {
        if (!IsAuthorized()) return Unauthorized();
        if (!Enum.TryParse<OperationalMode>(req.Mode, ignoreCase: true, out var mode))
            return BadRequest(new { success = false, message = "mode는 field 또는 unattended" });
        var cur = _store.Load();
        _store.Save(cur with { Mode = mode });
        return Ok(new { success = true, mode = mode.ToString().ToLowerInvariant(), effectiveAutoFallback = (cur with { Mode = mode }).EffectiveAutoFallback });
    }

    [HttpGet("mode")]
    public IActionResult GetMode()
    {
        if (!IsAuthorized()) return Unauthorized();
        var cfg = _store.Load();
        return Ok(new { mode = cfg.Mode.ToString().ToLowerInvariant(), effectiveAutoFallback = cfg.EffectiveAutoFallback });
    }
}
