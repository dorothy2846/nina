namespace NINA.Headless.Services.Network;

/// <summary>
/// At server boot: grace period to let the OS attach to a saved home WiFi. If nothing's
/// reachable after <see cref="ApModeConfig.FallbackGraceSeconds"/>, enter AP mode so the
/// user can still find the server from their phone in the field. This is the ASIAIR
/// UX — plug in, turn on, phone auto-connects.
///
/// No-op if the user disabled AutoFallback, if AP mode is unsupported (macOS), or if
/// there's already an active LAN connection.
/// </summary>
public class WifiFallbackOrchestrator : BackgroundService
{
    private readonly IApModeProvider _ap;
    private readonly ApModeConfigStore _store;
    private readonly ILogger<WifiFallbackOrchestrator> _log;

    public WifiFallbackOrchestrator(IApModeProvider ap, ApModeConfigStore store, ILogger<WifiFallbackOrchestrator> log)
    {
        _ap = ap;
        _store = store;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var cfg = _store.Load();
        if (!cfg.EffectiveAutoFallback)
        {
            _log.LogInformation("AP auto-fallback disabled (mode={Mode}, explicit={Flag})", cfg.Mode, cfg.AutoFallback);
            return;
        }

        var caps = await _ap.GetCapabilitiesAsync(ct);
        if (!caps.Supported) { _log.LogInformation("AP auto-fallback skipped: {Reason}", caps.UnsupportedReason); return; }

        // Already in AP mode from a prior session? Leave it alone.
        var status = await _ap.GetStatusAsync(ct);
        if (status.Active) { _log.LogInformation("AP already active — fallback not needed"); return; }

        _log.LogInformation("Waiting {Sec}s for home WiFi before AP fallback…", cfg.FallbackGraceSeconds);
        try { await Task.Delay(TimeSpan.FromSeconds(cfg.FallbackGraceSeconds), ct); } catch { return; }

        if (HasUsableNetwork())
        {
            _log.LogInformation("Network reachable — skipping AP fallback");
            return;
        }

        _log.LogWarning("No network after grace period — entering AP mode");
        await _ap.EnableAsync(cfg, ct);
    }

    /// <summary>Heuristic: any up network interface with a non-link-local IP?
    /// Missing this check = we'd flip into AP mode even on a perfectly-connected machine
    /// during the grace window (rare but bad — drops the user's connection).</summary>
    private static bool HasUsableNetwork()
    {
        foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
            foreach (var addr in ni.GetIPProperties().UnicastAddresses)
            {
                var ip = addr.Address;
                if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                if (System.Net.IPAddress.IsLoopback(ip)) continue;
                var bytes = ip.GetAddressBytes();
                // Skip APIPA link-local 169.254/16 — means DHCP failed, not a real network.
                if (bytes[0] == 169 && bytes[1] == 254) continue;
                return true;
            }
        }
        return false;
    }
}
