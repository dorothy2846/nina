using Microsoft.AspNetCore.Mvc;
using NINA.Headless.Services.Network;

namespace NINA.Headless.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class NetworkController : ControllerBase
{
    private readonly IApModeProvider _ap;
    private readonly ApModeConfigStore _store;

    public NetworkController(IApModeProvider ap, ApModeConfigStore store)
    {
        _ap = ap;
        _store = store;
    }

    /// <summary>Capability probe + current status — iOS uses this to decide whether to
    /// show the AP settings section at all and, when shown, whether the toggle is active.</summary>
    [HttpGet("ap/status")]
    public async Task<IActionResult> ApStatus()
    {
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

    [HttpPost("ap/config")]
    public IActionResult SaveApConfig([FromBody] ApConfigRequest req)
    {
        var current = _store.Load();
        var ssid = string.IsNullOrWhiteSpace(req.Ssid) ? current.Ssid : req.Ssid!.Trim();
        var password = string.IsNullOrWhiteSpace(req.Password) ? current.Password : req.Password!;
        // WPA2-PSK requires 8-63 chars. Short passwords get silently rejected by drivers,
        // so catch it here with a clear message.
        if (password.Length < 8 || password.Length > 63)
            return BadRequest(new { success = false, message = "비밀번호는 8자 이상 63자 이하여야 합니다." });
        if (ssid.Length == 0 || ssid.Length > 32)
            return BadRequest(new { success = false, message = "SSID는 1자 이상 32자 이하여야 합니다." });

        var cfg = current with
        {
            Ssid = ssid,
            Password = password,
            AutoFallback = req.AutoFallback ?? current.AutoFallback
        };
        _store.Save(cfg);
        return Ok(new { success = true });
    }

    /// <summary>Enter AP mode immediately. Caller is warned that their current connection
    /// may drop — if the server's LAN connectivity was via the same WiFi adapter, this
    /// call's response may fail to reach the client.</summary>
    [HttpPost("ap/enable")]
    public async Task<IActionResult> EnableAp()
    {
        var cfg = _store.Load();
        var ok = await _ap.EnableAsync(cfg, HttpContext.RequestAborted);
        return Ok(new { success = ok });
    }

    [HttpPost("ap/disable")]
    public async Task<IActionResult> DisableAp()
    {
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
        if (!Enum.TryParse<OperationalMode>(req.Mode, ignoreCase: true, out var mode))
            return BadRequest(new { success = false, message = "mode는 field 또는 unattended" });
        var cur = _store.Load();
        _store.Save(cur with { Mode = mode });
        return Ok(new { success = true, mode = mode.ToString().ToLowerInvariant(), effectiveAutoFallback = (cur with { Mode = mode }).EffectiveAutoFallback });
    }

    [HttpGet("mode")]
    public IActionResult GetMode()
    {
        var cfg = _store.Load();
        return Ok(new { mode = cfg.Mode.ToString().ToLowerInvariant(), effectiveAutoFallback = cfg.EffectiveAutoFallback });
    }
}
