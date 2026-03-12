using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Mvc;

namespace NINA.Headless.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class WifiController : ControllerBase
{
    private const string HostapdConfigPath = "/etc/hostapd/hostapd.conf";
    private const string HostapdBackupPath = "/etc/hostapd/hostapd.conf.bak";
    private const int RestartDelaySeconds = 3;

    private readonly ILogger<WifiController> _logger;

    public WifiController(ILogger<WifiController> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// GET /api/v1/wifi/config — Read current WiFi AP configuration.
    /// </summary>
    [HttpGet("config")]
    public IActionResult GetConfig()
    {
        if (!IsLinux())
        {
            return StatusCode(503, new { success = false, message = "WiFi AP management is only available on Linux." });
        }

        try
        {
            var config = ParseHostapdConfig();
            return Ok(new
            {
                ssid = config.GetValueOrDefault("ssid", "BeyondStellar"),
                channel = int.TryParse(config.GetValueOrDefault("channel", "6"), out var ch) ? ch : 6,
                maxClients = int.TryParse(config.GetValueOrDefault("max_num_sta", "5"), out var mc) ? mc : 5
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read hostapd config");
            return StatusCode(500, new { success = false, message = $"Failed to read WiFi config: {ex.Message}" });
        }
    }

    /// <summary>
    /// POST /api/v1/wifi/configure — Update WiFi AP SSID and/or password.
    /// Response is sent BEFORE hostapd restarts (3s delay), because restart kills the WiFi link.
    /// On failure, the previous config is restored from backup.
    /// </summary>
    [HttpPost("configure")]
    public IActionResult Configure([FromBody] WifiConfigureRequest request)
    {
        if (!IsLinux())
        {
            return StatusCode(503, new { success = false, message = "WiFi AP management is only available on Linux." });
        }

        // Validate inputs
        if (request.Ssid != null)
        {
            if (request.Ssid.Length < 1 || request.Ssid.Length > 32)
            {
                return BadRequest(new { success = false, message = "SSID must be 1-32 characters." });
            }
        }

        if (request.Password != null)
        {
            if (request.Password.Length < 8 || request.Password.Length > 63)
            {
                return BadRequest(new { success = false, message = "Password must be 8-63 characters." });
            }
        }

        if (request.Ssid == null && request.Password == null)
        {
            return BadRequest(new { success = false, message = "At least one of 'ssid' or 'password' must be provided." });
        }

        try
        {
            // 1. Backup current config
            System.IO.File.Copy(HostapdConfigPath, HostapdBackupPath, overwrite: true);
            _logger.LogInformation("Backed up hostapd config to {BackupPath}", HostapdBackupPath);

            // 2. Read and modify config
            var lines = System.IO.File.ReadAllLines(HostapdConfigPath);
            for (int i = 0; i < lines.Length; i++)
            {
                if (request.Ssid != null && lines[i].StartsWith("ssid="))
                {
                    lines[i] = $"ssid={request.Ssid}";
                }
                if (request.Password != null && lines[i].StartsWith("wpa_passphrase="))
                {
                    lines[i] = $"wpa_passphrase={request.Password}";
                }
            }

            // 3. Write new config
            System.IO.File.WriteAllLines(HostapdConfigPath, lines);
            _logger.LogInformation("Updated hostapd config: SSID={Ssid}", request.Ssid ?? "(unchanged)");

            // 4. Schedule hostapd restart AFTER response is sent
            //    This is critical: WiFi link drops on restart, so response must leave first.
            var newSsid = request.Ssid ?? ParseHostapdConfig().GetValueOrDefault("ssid", "BeyondStellar");
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(RestartDelaySeconds));
                    var result = await RunCommand("systemctl", "restart hostapd");
                    if (result.ExitCode != 0)
                    {
                        _logger.LogError("hostapd restart failed (exit {Code}): {Error}. Rolling back.", result.ExitCode, result.StdErr);
                        await RollbackConfig();
                    }
                    else
                    {
                        _logger.LogInformation("hostapd restarted successfully with new config");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "hostapd restart threw exception. Rolling back.");
                    await RollbackConfig();
                }
            });

            return Ok(new
            {
                success = true,
                ssid = newSsid,
                message = $"WiFi settings will apply in {RestartDelaySeconds} seconds. Reconnect to \"{newSsid}\".",
                reconnectDelay = RestartDelaySeconds
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update WiFi config");
            return StatusCode(500, new { success = false, message = $"Failed to update WiFi config: {ex.Message}" });
        }
    }

    /// <summary>
    /// GET /api/v1/wifi/status — Current WiFi AP status.
    /// </summary>
    [HttpGet("status")]
    public async Task<IActionResult> GetStatus()
    {
        if (!IsLinux())
        {
            return StatusCode(503, new { success = false, message = "WiFi AP management is only available on Linux." });
        }

        try
        {
            var config = ParseHostapdConfig();
            var ssid = config.GetValueOrDefault("ssid", "BeyondStellar");

            // Check if hostapd is active
            var statusResult = await RunCommand("systemctl", "is-active hostapd");
            var isActive = statusResult.StdOut.Trim() == "active";

            // Count connected clients via hostapd_cli
            int connectedClients = 0;
            if (isActive)
            {
                var clientResult = await RunCommand("hostapd_cli", "all_sta");
                if (clientResult.ExitCode == 0)
                {
                    // Each connected station has a MAC address line (xx:xx:xx:xx:xx:xx)
                    connectedClients = clientResult.StdOut
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .Count(line => line.Length == 17 && line.Count(c => c == ':') == 5);
                }
            }

            return Ok(new
            {
                active = isActive,
                ssid = ssid,
                connectedClients = connectedClients
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get WiFi status");
            return StatusCode(500, new { success = false, message = $"Failed to get WiFi status: {ex.Message}" });
        }
    }

    // ─── Helpers ─────────────────────────────────────────────

    private static bool IsLinux()
    {
        return RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
    }

    private static Dictionary<string, string> ParseHostapdConfig()
    {
        var config = new Dictionary<string, string>();
        foreach (var line in System.IO.File.ReadAllLines(HostapdConfigPath))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
                continue;

            var eqIndex = trimmed.IndexOf('=');
            if (eqIndex > 0)
            {
                var key = trimmed[..eqIndex].Trim();
                var value = trimmed[(eqIndex + 1)..].Trim();
                config[key] = value;
            }
        }
        return config;
    }

    private async Task RollbackConfig()
    {
        try
        {
            if (System.IO.File.Exists(HostapdBackupPath))
            {
                System.IO.File.Copy(HostapdBackupPath, HostapdConfigPath, overwrite: true);
                _logger.LogWarning("Rolled back hostapd config from backup");
                var result = await RunCommand("systemctl", "restart hostapd");
                if (result.ExitCode != 0)
                {
                    _logger.LogError("hostapd restart after rollback failed (exit {Code}): {Error}", result.ExitCode, result.StdErr);
                }
                else
                {
                    _logger.LogInformation("hostapd restarted with rolled-back config");
                }
            }
            else
            {
                _logger.LogError("No backup file found at {BackupPath} — cannot rollback", HostapdBackupPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Rollback itself failed");
        }
    }

    private static async Task<CommandResult> RunCommand(string command, string arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = command,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new CommandResult(process.ExitCode, stdout, stderr);
    }

    private record CommandResult(int ExitCode, string StdOut, string StdErr);
}

// ─── Request Model ──────────────────────────────────────────

public record WifiConfigureRequest
{
    public string? Ssid { get; init; }
    public string? Password { get; init; }
}
