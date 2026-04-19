using System;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Headless.Services;

/// <summary>
/// mDNS/Bonjour broadcast so clients (iOS app, Touch'N'Stars) can auto-discover
/// this astellar instance on the local network.
///
/// - Linux (production): relies on avahi-daemon; shells out to avahi-publish.
/// - macOS (dev): uses the dns-sd CLI shipped with the OS.
/// </summary>
public class MdnsBroadcastService : IHostedService
{
    // Unified brand across protocol surfaces — the same "astellar" string shows
    // up in mDNS service name, TXT app key, machineId prefix and rendezvous URL.
    // The service instance name is "astellar-{hostname}" so iOS clients can
    // strip the "astellar-" prefix and derive the real .local hostname without
    // needing the TXT record (which NWBrowser frequently delivers empty).
    private static readonly string ServiceName = $"astellar-{SanitizeHostname(Environment.MachineName)}";
    private const string ServiceType = "_http._tcp";
    private const int Port = 1888;

    private static string SanitizeHostname(string raw)
    {
        // Match the mDNS .local hostname Mac's mdnsd / Linux avahi-daemon publish:
        // lowercase, spaces collapse to hyphens, other unsafe chars stripped.
        var sb = new System.Text.StringBuilder();
        foreach (var c in raw.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c == '-') sb.Append(c);
            else if (c == ' ' || c == '_') sb.Append('-');
        }
        return sb.ToString();
    }

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

            if (PlatformPaths.IsMacOS)
            {
                // Dev box: dns-sd ships with macOS.
                StartDnsSd();
            }
            else if (PlatformPaths.IsLinux)
            {
                // Production USB image: avahi-daemon + avahi-publish.
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
