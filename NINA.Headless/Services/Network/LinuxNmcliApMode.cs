using System.Diagnostics;

namespace NINA.Headless.Services.Network;

/// <summary>
/// Linux AP mode via NetworkManager's <c>nmcli</c>. Creates a reusable connection profile
/// named <c>beyondstellar-ap</c> with <c>ipv4.method shared</c> — NetworkManager bundles
/// dnsmasq, so DHCP + DNS on 192.168.4.0/24 come for free. Clients connect to the SSID,
/// get an IP, reach the server at 192.168.4.1:1888.
///
/// Requires: NetworkManager + nmcli + a WiFi adapter that advertises "AP" in its `iw list`
/// supported interface modes. Most modern chipsets (Broadcom 43xx on Pi 3+, Intel AX200,
/// Realtek 8188/8192) support this. Old USB sticks sometimes don't.
/// </summary>
public class LinuxNmcliApMode : IApModeProvider
{
    private const string ProfileName = "beyondstellar-ap";

    private readonly ILogger<LinuxNmcliApMode> _log;
    public LinuxNmcliApMode(ILogger<LinuxNmcliApMode> log) { _log = log; }

    public async Task<ApCapabilities> GetCapabilitiesAsync(CancellationToken ct)
    {
        // 1. nmcli present?
        if (!await CommandExistsAsync("nmcli", ct))
            return new ApCapabilities(false, "nmcli (NetworkManager) 미설치. `apt install network-manager` 필요.", null);

        // 2. At least one WiFi device managed by NM?
        var devList = await RunAsync("nmcli", "-t -f DEVICE,TYPE,STATE device", ct);
        var wifiDevice = devList.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split(':'))
            .Where(p => p.Length >= 2 && p[1] == "wifi")
            .Select(p => p[0])
            .FirstOrDefault();
        if (wifiDevice == null)
            return new ApCapabilities(false, "관리되는 WiFi 어댑터가 없음. 외장 USB WiFi 또는 내장 어댑터 필요.", null);

        // 3. Does the adapter support AP mode? `iw list` output includes a "Supported
        // interface modes" section that lists "AP" when capable. Not running iw (not
        // installed) is non-fatal — assume yes and let nmcli surface the real error.
        if (await CommandExistsAsync("iw", ct))
        {
            var iwList = await RunAsync("iw", "list", ct);
            if (iwList.ExitCode == 0 && !iwList.Stdout.Contains(" AP\n", StringComparison.Ordinal)
                                   && !iwList.Stdout.Contains("\tAP\n", StringComparison.Ordinal))
            {
                return new ApCapabilities(false, $"어댑터 {wifiDevice}가 AP 모드를 지원하지 않음.", wifiDevice);
            }
        }

        return new ApCapabilities(true, null, wifiDevice);
    }

    public async Task<ApModeStatus> GetStatusAsync(CancellationToken ct)
    {
        // Active profiles — nmcli con show --active prints one line per profile.
        var active = await RunAsync("nmcli", "-t -f NAME,DEVICE,TYPE con show --active", ct);
        foreach (var line in active.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(':');
            if (parts.Length >= 2 && parts[0] == ProfileName)
            {
                // Pull the SSID from the profile details
                var details = await RunAsync("nmcli", $"-t -f 802-11-wireless.ssid con show {ProfileName}", ct);
                var ssid = details.Stdout.Split(':', 2).LastOrDefault()?.Trim() ?? "";
                return new ApModeStatus(true, ssid, parts[1], null);
            }
        }
        return new ApModeStatus(false, null, null, null);
    }

    public async Task<bool> EnableAsync(ApModeConfig config, CancellationToken ct)
    {
        var caps = await GetCapabilitiesAsync(ct);
        if (!caps.Supported) { _log.LogWarning("AP mode unsupported: {Reason}", caps.UnsupportedReason); return false; }
        var iface = caps.InterfaceName!;

        // Idempotent: wipe any stale profile first so changed SSID/password take effect.
        await RunAsync("nmcli", $"con delete {ProfileName}", ct); // ignore failure if absent

        var ssid = EscapeArg(config.Ssid);
        var pwd = EscapeArg(config.Password);
        var add = await RunAsync("nmcli",
            $"con add type wifi ifname {iface} con-name {ProfileName} " +
            $"autoconnect yes ssid {ssid} " +
            $"802-11-wireless.mode ap 802-11-wireless.band bg " +
            $"ipv4.method shared " +
            $"wifi-sec.key-mgmt wpa-psk wifi-sec.psk {pwd}", ct);
        if (add.ExitCode != 0)
        {
            _log.LogWarning("nmcli con add failed: {Err}", add.Stderr);
            return false;
        }

        var up = await RunAsync("nmcli", $"con up {ProfileName}", ct);
        if (up.ExitCode != 0)
        {
            _log.LogWarning("nmcli con up failed: {Err}", up.Stderr);
            return false;
        }
        _log.LogInformation("AP mode enabled on {Iface} — SSID {Ssid}", iface, config.Ssid);
        return true;
    }

    public async Task<bool> DisableAsync(CancellationToken ct)
    {
        var down = await RunAsync("nmcli", $"con down {ProfileName}", ct);
        // Don't delete the profile — user probably wants the same AP next time.
        // NetworkManager will auto-reconnect to any priority-higher saved WiFi now that
        // the AP profile is no longer active.
        _log.LogInformation("AP mode disabled (exit={Code})", down.ExitCode);
        return down.ExitCode == 0;
    }

    // --- helpers ---

    private static async Task<bool> CommandExistsAsync(string cmd, CancellationToken ct)
    {
        var r = await RunAsync("/bin/sh", $"-c \"command -v {cmd}\"", ct);
        return r.ExitCode == 0 && !string.IsNullOrWhiteSpace(r.Stdout);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(string file, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {file}");
        var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return (p.ExitCode, await stdoutTask, await stderrTask);
    }

    /// <summary>Wrap SSID/password for the shell-interpreted nmcli arg list. Simple quoting —
    /// strip any quote chars from input (already validated at the controller layer, but
    /// defense in depth).</summary>
    private static string EscapeArg(string raw)
    {
        var cleaned = raw.Replace("\"", "").Replace("'", "").Replace("`", "").Replace("$", "");
        return $"\"{cleaned}\"";
    }
}
