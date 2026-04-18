using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;

namespace NINA.Headless.Services.Network;

/// <summary>
/// Windows AP mode via the modern Mobile Hotspot feature. The only reliable programmatic
/// path is WinRT's <c>Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager</c>.
/// Rather than multi-targeting the whole project to <c>net10.0-windows10.0.19041</c>, we
/// shell out to a PowerShell script that loads the WinRT types — works on plain .NET
/// without extra TFMs.
///
/// Requires: Windows 10 1803+, admin rights (granted at install time via service registration
/// as LocalSystem), a WiFi adapter that supports Mobile Hotspot. Driver support is broader
/// than the old <c>netsh hostednetwork</c> since Mobile Hotspot leverages Wi-Fi Direct.
///
/// Security: this runs PowerShell with scripts from the server's own binary directory.
/// Scripts are read-only and shipped with the app — no user input is interpolated into
/// the script text, parameters go via argv.
/// </summary>
[SupportedOSPlatform("windows")]
public class WindowsMobileHotspotApMode : IApModeProvider
{
    private readonly ILogger<WindowsMobileHotspotApMode> _log;

    public WindowsMobileHotspotApMode(ILogger<WindowsMobileHotspotApMode> log) { _log = log; }

    public async Task<ApCapabilities> GetCapabilitiesAsync(CancellationToken ct)
    {
        if (!await CommandExistsAsync("powershell.exe", ct))
            return new ApCapabilities(false, "powershell.exe 미발견 — 최소 Windows 10 필요.", null);

        // Rather than running a full WinRT probe for capability detection (slow, ~2s cold
        // start per invoke), we assume the feature is present on Windows 10 1803+.
        // The actual Enable call will surface any adapter-specific failure.
        return new ApCapabilities(true, null, "WiFi");
    }

    public async Task<ApModeStatus> GetStatusAsync(CancellationToken ct)
    {
        var script = HotspotPowerShell("status");
        var r = await RunPowerShellAsync(script, Array.Empty<string>(), ct);
        // Script prints one line per status: "active|<ssid>" or "inactive".
        var line = r.Stdout.Trim();
        if (line.StartsWith("active|", StringComparison.OrdinalIgnoreCase))
        {
            var ssid = line[7..];
            return new ApModeStatus(true, ssid, "WiFi", null);
        }
        return new ApModeStatus(false, null, null,
            r.ExitCode != 0 ? FirstLine(r.Stderr) : null);
    }

    public async Task<bool> EnableAsync(ApModeConfig config, CancellationToken ct)
    {
        var script = HotspotPowerShell("start");
        var r = await RunPowerShellAsync(script, new[] { config.Ssid, config.Password }, ct);
        if (r.ExitCode != 0)
        {
            _log.LogWarning("Mobile Hotspot start failed: {Err}", FirstLine(r.Stderr));
            return false;
        }
        _log.LogInformation("Mobile Hotspot started — SSID {Ssid}", config.Ssid);
        return true;
    }

    public async Task<bool> DisableAsync(CancellationToken ct)
    {
        var script = HotspotPowerShell("stop");
        var r = await RunPowerShellAsync(script, Array.Empty<string>(), ct);
        _log.LogInformation("Mobile Hotspot stopped (exit={Code})", r.ExitCode);
        return r.ExitCode == 0;
    }

    // --- PowerShell bridge ---
    //
    // Script is inlined so the app ships self-contained (no external .ps1 file to lose in
    // packaging). Action: "start" | "stop" | "status". Positional args via $args.
    private static string HotspotPowerShell(string action) =>
        "$ErrorActionPreference = 'Stop'\n" +
        "$action = '" + action + "'\n" +
        "Add-Type -AssemblyName System.Runtime.WindowsRuntime\n" +
        "$null = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager,Windows.Networking.NetworkOperators,ContentType=WindowsRuntime]\n" +
        "$null = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringAccessPointConfiguration,Windows.Networking.NetworkOperators,ContentType=WindowsRuntime]\n" +
        "$null = [Windows.Networking.Connectivity.NetworkInformation,Windows.Networking.Connectivity,ContentType=WindowsRuntime]\n" +
        // Helper: WinRT IAsyncOperation<T> → synchronous wait (requires Runtime.WindowsRuntime).
        "function Await($task, $resultType) {\n" +
        "  $asTask = ([System.WindowsRuntimeSystemExtensions].GetMethods() | ? { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0].MakeGenericMethod($resultType)\n" +
        "  $netTask = $asTask.Invoke($null, @($task))\n" +
        "  $netTask.Wait(-1)\n" +
        "  $netTask.Result\n" +
        "}\n" +
        "function AwaitAction($task) {\n" +
        "  $m = [System.WindowsRuntimeSystemExtensions].GetMethods() | ? { $_.Name -eq 'AsTask' -and $_.IsGenericMethod -eq $false -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncAction' }\n" +
        "  $netTask = $m[0].Invoke($null, @($task))\n" +
        "  $netTask.Wait(-1)\n" +
        "}\n" +
        "$profile = [Windows.Networking.Connectivity.NetworkInformation]::GetInternetConnectionProfile()\n" +
        "if ($profile -eq $null) { Write-Error 'No active internet connection profile — attach an Ethernet uplink first.'; exit 2 }\n" +
        "$tm = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager]::CreateFromConnectionProfile($profile)\n" +
        "switch ($action) {\n" +
        "  'status' {\n" +
        "    if ($tm.TetheringOperationalState -eq 'On') {\n" +
        "      $cfg = $tm.GetCurrentAccessPointConfiguration()\n" +
        "      Write-Output (\"active|\" + $cfg.Ssid)\n" +
        "    } else { Write-Output 'inactive' }\n" +
        "    exit 0\n" +
        "  }\n" +
        "  'start' {\n" +
        "    if ($args.Count -lt 2) { Write-Error 'Need SSID and passphrase'; exit 2 }\n" +
        "    $cfg = New-Object Windows.Networking.NetworkOperators.NetworkOperatorTetheringAccessPointConfiguration\n" +
        "    $cfg.Ssid = $args[0]\n" +
        "    $cfg.Passphrase = $args[1]\n" +
        "    AwaitAction $tm.ConfigureAccessPointAsync($cfg)\n" +
        "    $result = Await $tm.StartTetheringAsync() ([Windows.Networking.NetworkOperators.NetworkOperatorTetheringOperationResult])\n" +
        "    if ($result.Status -ne 'Success') { Write-Error ('StartTethering: ' + $result.Status); exit 3 }\n" +
        "    exit 0\n" +
        "  }\n" +
        "  'stop' {\n" +
        "    $result = Await $tm.StopTetheringAsync() ([Windows.Networking.NetworkOperators.NetworkOperatorTetheringOperationResult])\n" +
        "    if ($result.Status -ne 'Success') { Write-Error ('StopTethering: ' + $result.Status); exit 3 }\n" +
        "    exit 0\n" +
        "  }\n" +
        "  default { Write-Error ('unknown action ' + $action); exit 2 }\n" +
        "}\n";

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunPowerShellAsync(string scriptText, string[] args, CancellationToken ct)
    {
        // -EncodedCommand avoids PowerShell's quoting hell: pass a UTF-16 base64 blob
        // and PowerShell decodes it losslessly.
        var bytes = Encoding.Unicode.GetBytes(scriptText);
        var encoded = Convert.ToBase64String(bytes);
        var argList = string.Join(" ", args.Select(a => $"\"{a.Replace("\"", "\\\"")}\""));
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded} {argList}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start powershell.exe");
        var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return (p.ExitCode, await stdoutTask, await stderrTask);
    }

    private static async Task<bool> CommandExistsAsync(string cmd, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "where.exe",
            Arguments = cmd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return false;
            await p.WaitForExitAsync(ct);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static string FirstLine(string s) =>
        s.Split('\n', 2)[0].Trim();
}
