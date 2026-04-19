using System;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Headless.Services;

/// <summary>
/// Cross-platform mDNS/Bonjour broadcast so clients (iOS app, Touch'N'Stars)
/// can auto-discover this NINA Air instance on the local network.
///
/// - macOS: uses the dns-sd CLI (part of the OS, no dependencies).
/// - Linux: relies on avahi-daemon (already set up for nina-pi deployment).
///   This service attempts avahi-publish but falls back to a hint log.
/// - Windows: uses dns-sd if Bonjour Print Services is installed; otherwise
///   skipped (user can connect by static IP).
/// </summary>
public class MdnsBroadcastService : IHostedService
{
    // Unified brand across protocol surfaces — the same "astellar" string shows
    // up in mDNS service name, TXT app key, machineId prefix and rendezvous URL
    // so the user sees one consistent name instead of nina-air / beyond-stellar /
    // astellar colliding. Legacy "nina-air" values stay readable on the iOS side
    // for backward compatibility during transition.
    private const string ServiceName = "astellar";
    private const string ServiceType = "_http._tcp";
    private const int Port = 1888;

    private readonly ILogger<MdnsBroadcastService> _logger;
    private Process? _mdnsProcess;

    public MdnsBroadcastService(ILogger<MdnsBroadcastService> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var ip = GetLocalIPv4();
            if (ip == null)
            {
                _logger.LogWarning("mDNS: no non-loopback IPv4 interface found; broadcast disabled.");
                return Task.CompletedTask;
            }

            if (PlatformPaths.IsMacOS || PlatformPaths.IsWindows)
            {
                StartDnsSd();
            }
            else if (PlatformPaths.IsLinux)
            {
                StartAvahiPublish();
            }

            _logger.LogInformation("mDNS: broadcasting {Name}.{Type} at {IP}:{Port}",
                ServiceName, ServiceType, ip, Port);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "mDNS broadcast failed to start (non-fatal)");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_mdnsProcess is { HasExited: false })
            {
                _mdnsProcess.Kill(entireProcessTree: true);
                _mdnsProcess.Dispose();
            }
        }
        catch { /* best effort */ }
        return Task.CompletedTask;
    }

    // TXT records let the iOS NWBrowser filter out other generic _http._tcp services on
    // the LAN (routers, printers). Without these, discovery finds everything and the
    // client's "app=astellar" check strips our own service too.
    private const string TxtAppKey = "app=astellar";
    private const string TxtVersionKey = "version=1.0";

    private void StartDnsSd()
    {
        // dns-sd is shipped with macOS; on Windows it requires Bonjour Print Services.
        var psi = new ProcessStartInfo
        {
            FileName = "dns-sd",
            // dns-sd takes TXT records as trailing positional args: `-R name type domain port key=val key=val…`
            Arguments = $"-R {ServiceName} {ServiceType} local {Port} {TxtAppKey} {TxtVersionKey}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        try { _mdnsProcess = Process.Start(psi); }
        catch (Exception ex)
        {
            _logger.LogWarning("dns-sd not available ({Msg}). Clients must use static IP.", ex.Message);
        }
    }

    private void StartAvahiPublish()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "avahi-publish",
            // avahi-publish: `-s name type port txt1 txt2…` — same trailing-TXT convention.
            Arguments = $"-s {ServiceName} {ServiceType} {Port} {TxtAppKey} {TxtVersionKey}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        try { _mdnsProcess = Process.Start(psi); }
        catch (Exception ex)
        {
            _logger.LogInformation("avahi-publish not running ({Msg}); if avahi-daemon is active, " +
                "define the service in /etc/avahi/services/astellar.service.", ex.Message);
        }
    }

    private static IPAddress? GetLocalIPv4()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            foreach (var addr in ni.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(addr.Address))
                {
                    return addr.Address;
                }
            }
        }
        return null;
    }
}
