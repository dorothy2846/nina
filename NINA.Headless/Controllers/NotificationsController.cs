using Microsoft.AspNetCore.Mvc;
using NINA.Headless.Services.Remote;

namespace NINA.Headless.Controllers;

/// <summary>
/// APNs device-token registration + test push. Follows the appliance trust
/// model of the rest of the equipment API: pairing itself is open to the LAN
/// (see PairedDeviceStore), so gating registration behind a paired token adds
/// friction without adding security — an unpaired LAN phone could just pair
/// first. A paired token, when present, is still recorded so the push token
/// can be revoked together with its device.
/// </summary>
[ApiController]
[Route("api/v1/notifications")]
public class NotificationsController : ControllerBase
{
    public record RegisterRequest(string? DeviceToken, bool Sandbox);

    private readonly PairedDeviceStore _paired;
    private readonly PushTokenStore _tokens;
    private readonly ApnsPushService _apns;

    public NotificationsController(PairedDeviceStore paired, PushTokenStore tokens, ApnsPushService apns)
    {
        _paired = paired;
        _tokens = tokens;
        _apns = apns;
    }

    private PairedDevice? AuthorizedDevice()
    {
        var auth = Request.Headers.Authorization.ToString();
        var token = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..].Trim() : "";
        return _paired.Verify(token);
    }

    [HttpPost("register")]
    public IActionResult Register([FromBody] RegisterRequest? request)
    {
        if (string.IsNullOrWhiteSpace(request?.DeviceToken))
            return BadRequest(new { success = false, message = "deviceToken is required" });

        _tokens.Register(request.DeviceToken.Trim(), request.Sandbox, AuthorizedDevice()?.Id);
        return Ok(new { success = true, configured = _apns.IsConfigured });
    }

    [HttpPost("unregister")]
    public IActionResult Unregister([FromBody] RegisterRequest? request)
    {
        if (string.IsNullOrWhiteSpace(request?.DeviceToken))
            return BadRequest(new { success = false, message = "deviceToken is required" });

        var removed = _tokens.Unregister(request.DeviceToken.Trim());
        return Ok(new { success = true, removed });
    }

    [HttpGet("status")]
    public IActionResult Status()
    {
        return Ok(new
        {
            configured = _apns.IsConfigured,
            registeredDevices = _tokens.List().Count,
        });
    }

    /// <summary>Sends a real push to every registered device so the user can
    /// confirm the whole chain (server → APNs → lock screen) from the app.</summary>
    [HttpPost("test")]
    public async Task<IActionResult> Test()
    {
        var outcome = await _apns.NotifyAllAsync(
            "아스텔라 테스트 알림", "푸시 알림이 정상적으로 작동합니다.", "test",
            bypassThrottle: true, HttpContext.RequestAborted);
        return Ok(new
        {
            success = outcome.Sent > 0,
            sent = outcome.Sent,
            failed = outcome.Failed,
            error = outcome.LastError,
        });
    }
}
