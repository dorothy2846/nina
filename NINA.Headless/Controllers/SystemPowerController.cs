using Microsoft.AspNetCore.Mvc;
using NINA.Headless.Services.Remote;

namespace NINA.Headless.Controllers;

/// <summary>
/// Remote power actions for unattended observatories: restart the server
/// process (systemd relaunches it in production) or reboot the machine.
/// Both are paired-device-token gated like the update endpoints — anyone who
/// can reboot the box can end a night's imaging.
/// </summary>
[ApiController]
[Route("api/v1/system/power")]
public class SystemPowerController : ControllerBase
{
    private readonly PairedDeviceStore _paired;
    private readonly ILogger<SystemPowerController> _log;
    private readonly IHostApplicationLifetime _lifetime;

    public SystemPowerController(PairedDeviceStore paired, ILogger<SystemPowerController> log, IHostApplicationLifetime lifetime)
    {
        _paired = paired;
        _log = log;
        _lifetime = lifetime;
    }

    private bool IsAuthorized()
    {
        var auth = Request.Headers.Authorization.ToString();
        var token = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..].Trim() : "";
        return _paired.Verify(token) != null;
    }

    /// <summary>Graceful process restart. Production runs under systemd with
    /// Restart=always, so exiting relaunches with fresh state; a dev shell
    /// without a supervisor simply stops (the response says so honestly).</summary>
    [HttpPost("restart-server")]
    public IActionResult RestartServer()
    {
        if (!IsAuthorized()) return Unauthorized();
        var supervised = System.IO.File.Exists("/run/systemd/system") || Environment.GetEnvironmentVariable("INVOCATION_ID") != null;
        _log.LogWarning("Power: remote server restart requested (supervised={Supervised})", supervised);
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(800));
            _lifetime.StopApplication();
        });
        return Ok(new
        {
            success = true,
            supervised,
            message = supervised
                ? "서버를 재시작합니다. 잠시 후 자동으로 다시 연결됩니다."
                : "서버 프로세스를 종료합니다. 이 환경에는 자동 재시작 관리자가 없어 수동 기동이 필요할 수 있습니다."
        });
    }

    /// <summary>Full machine reboot (Linux only, needs passwordless sudo for
    /// reboot as provisioned on the deployment image).</summary>
    [HttpPost("reboot")]
    public async Task<IActionResult> Reboot()
    {
        if (!IsAuthorized()) return Unauthorized();
        if (!OperatingSystem.IsLinux())
            return StatusCode(501, new { success = false, message = "기기 재부팅은 Linux 서버에서만 지원됩니다." });

        _log.LogWarning("Power: remote machine reboot requested");
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "sudo",
            Arguments = "-n systemctl reboot",
            RedirectStandardError = true,
        };
        using var proc = System.Diagnostics.Process.Start(psi);
        if (proc == null)
            return StatusCode(500, new { success = false, message = "reboot 명령을 시작하지 못했습니다." });
        await proc.WaitForExitAsync(HttpContext.RequestAborted);
        if (proc.ExitCode != 0)
        {
            var err = await proc.StandardError.ReadToEndAsync();
            return StatusCode(500, new { success = false, message = $"재부팅 실패: {err.Trim()}" });
        }
        return Ok(new { success = true, message = "기기를 재부팅합니다. 부팅 후 자동으로 다시 연결됩니다." });
    }
}
