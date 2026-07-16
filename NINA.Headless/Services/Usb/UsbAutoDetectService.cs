using System.Text.Json;

namespace NINA.Headless.Services.Usb;

/// <summary>One physical USB device as seen by the host.</summary>
public record UsbDeviceInfo(string VendorId, string ProductId, string? Product, string? Vendor)
{
    public string Key => $"{VendorId}:{ProductId}";
}

/// <summary>What we know about a detected device after catalog lookup.</summary>
public record DetectedUsbDevice(
    UsbDeviceInfo Device,
    string? VendorLabel,
    IReadOnlyList<string> SuggestedDrivers,
    bool IsSerialAdapter);

/// <summary>
/// Vendor-ID → INDI driver mapping for plug-and-play. VID-level (not PID)
/// on purpose: a vendor's cameras/focusers/wheels share the VID and an INDI
/// driver with no matching hardware just idles, so starting the vendor's
/// whole driver set is harmless and avoids maintaining a PID table.
/// Serial adapters (FTDI etc.) cannot identify the mount behind them —
/// those are surfaced to the app for a manual driver pick instead.
/// </summary>
public static class IndiDriverCatalog
{
    private sealed record VendorEntry(string Label, string[] Drivers);

    // VIDs cross-checked against indi-3rdparty udev rules.
    private static readonly Dictionary<string, VendorEntry> Vendors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["03c3"] = new("ZWO", new[] { "indi_asi_ccd", "indi_asi_focuser", "indi_asi_wheel" }),
        ["a0a0"] = new("Player One", new[] { "indi_playerone_ccd" }),
        ["1618"] = new("QHYCCD", new[] { "indi_qhy_ccd" }),
        ["0547"] = new("ToupTek", new[] { "indi_toupcam_ccd" }),
        ["04a9"] = new("Canon (gPhoto)", new[] { "indi_gphoto_ccd" }),
        ["04b0"] = new("Nikon (gPhoto)", new[] { "indi_gphoto_ccd" }),
    };

    // USB-serial bridge chips: the adapter tells us nothing about the mount
    // (or focuser, or flat panel) on the other end of the cable.
    private static readonly Dictionary<string, string> SerialChips = new(StringComparer.OrdinalIgnoreCase)
    {
        ["0403"] = "FTDI serial adapter",
        ["067b"] = "Prolific serial adapter",
        ["10c4"] = "Silicon Labs serial adapter",
        ["1a86"] = "WCH CH340 serial adapter",
    };

    /// <summary>Manual-add catalog for devices we cannot fingerprint over USB
    /// (mounts behind serial adapters, mostly). Shown by the app as a picker.</summary>
    public static readonly IReadOnlyList<(string Driver, string Label)> ManualDrivers = new (string, string)[]
    {
        ("indi_lx200am5",          "ZWO AM5 / AM3"),
        ("indi_eqmod",             "Sky-Watcher EQ (EQMod)"),
        ("indi_celestron_gps",     "Celestron"),
        ("indi_ioptronv3",         "iOptron"),
        ("indi_lx200ap_v2",        "Astro-Physics"),
        ("indi_lx200_OnStep",      "OnStep"),
        ("indi_lx200generic",      "Generic LX200"),
        ("indi_simulator_telescope", "Telescope Simulator (test)"),
        ("indi_simulator_ccd",     "CCD Simulator (test)"),
    };

    public static DetectedUsbDevice Classify(UsbDeviceInfo dev)
    {
        if (Vendors.TryGetValue(dev.VendorId, out var v))
            return new DetectedUsbDevice(dev, v.Label, v.Drivers, IsSerialAdapter: false);
        if (SerialChips.TryGetValue(dev.VendorId, out var label))
            return new DetectedUsbDevice(dev, label, Array.Empty<string>(), IsSerialAdapter: true);
        return new DetectedUsbDevice(dev, null, Array.Empty<string>(), IsSerialAdapter: false);
    }
}

/// <summary>
/// Polls the host's USB bus, classifies devices against the driver catalog,
/// and auto-starts the matching INDI drivers through indiserver's FIFO.
/// ASIAIR-style plug-and-play: plug a ZWO camera in, indi_asi_ccd comes up,
/// discovery surfaces it in the app a few seconds later.
///
/// Unplug does NOT auto-stop the driver: cameras (PlayerOne notably) drop
/// off the bus briefly during USB resets, and killing the driver mid-session
/// would turn a recoverable blip into a hard disconnect. Idle drivers cost
/// nothing.
/// </summary>
public class UsbAutoDetectService : BackgroundService
{
    private static readonly TimeSpan LinuxPollInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MacPollInterval = TimeSpan.FromSeconds(10);

    private readonly IndiServerManager _indiServer;
    private readonly ILogger<UsbAutoDetectService> _log;
    private readonly object _lock = new();
    private List<DetectedUsbDevice> _current = new();
    private readonly HashSet<string> _autoStarted = new();

    public UsbAutoDetectService(IndiServerManager indiServer, ILogger<UsbAutoDetectService> log)
    {
        _indiServer = indiServer;
        _log = log;
    }

    /// <summary>Snapshot for the API: everything currently on the bus, classified.</summary>
    public IReadOnlyList<DetectedUsbDevice> CurrentDevices
    {
        get { lock (_lock) return _current.ToList(); }
    }

    /// <summary>Drivers this service started (as opposed to the base launch list).</summary>
    public IReadOnlyCollection<string> AutoStartedDrivers
    {
        get { lock (_lock) return _autoStarted.ToList(); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = OperatingSystem.IsMacOS() ? MacPollInterval : LinuxPollInterval;
        // Let indiserver come up first — the FIFO must exist before we write to it.
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        var knownKeys = new HashSet<string>();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var devices = EnumerateUsb();
                var classified = devices.Select(IndiDriverCatalog.Classify).ToList();
                lock (_lock) _current = classified;

                foreach (var d in classified)
                {
                    var isNew = knownKeys.Add(d.Device.Key);
                    if (!isNew) continue;

                    if (d.SuggestedDrivers.Count > 0)
                    {
                        _log.LogInformation("USB: detected {Label} ({Key}) — starting {Drivers}",
                            d.VendorLabel, d.Device.Key, string.Join(' ', d.SuggestedDrivers));
                        foreach (var driver in d.SuggestedDrivers)
                        {
                            try
                            {
                                var started = await _indiServer.StartDriverAsync(driver, stoppingToken);
                                if (started) lock (_lock) _autoStarted.Add(driver);
                            }
                            catch (Exception ex)
                            {
                                _log.LogWarning(ex, "USB: failed to start {Driver}", driver);
                            }
                        }
                    }
                    else if (d.IsSerialAdapter)
                    {
                        _log.LogInformation("USB: serial adapter detected ({Label}, {Key}) — mount driver must be chosen in the app",
                            d.VendorLabel, d.Device.Key);
                    }
                }

                // Log unplugs (no driver stop — see class comment).
                var presentKeys = classified.Select(c => c.Device.Key).ToHashSet();
                foreach (var gone in knownKeys.Where(k => !presentKeys.Contains(k)).ToList())
                {
                    knownKeys.Remove(gone);
                    _log.LogInformation("USB: device {Key} unplugged (driver left running)", gone);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "USB poll failed");
            }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private List<UsbDeviceInfo> EnumerateUsb()
        => OperatingSystem.IsMacOS() ? EnumerateMac() : EnumerateLinuxSysfs();

    /// Linux: /sys/bus/usb/devices — no external tools, ~free to poll.
    private static List<UsbDeviceInfo> EnumerateLinuxSysfs()
    {
        var result = new List<UsbDeviceInfo>();
        const string root = "/sys/bus/usb/devices";
        if (!Directory.Exists(root)) return result;

        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            // Interface entries contain ':' (e.g. 1-1:1.0) — devices don't.
            if (Path.GetFileName(dir).Contains(':')) continue;
            var vidPath = Path.Combine(dir, "idVendor");
            var pidPath = Path.Combine(dir, "idProduct");
            if (!File.Exists(vidPath) || !File.Exists(pidPath)) continue;
            try
            {
                var vid = File.ReadAllText(vidPath).Trim();
                var pid = File.ReadAllText(pidPath).Trim();
                var product = TryReadAll(Path.Combine(dir, "product"));
                var vendor = TryReadAll(Path.Combine(dir, "manufacturer"));
                result.Add(new UsbDeviceInfo(vid, pid, product, vendor));
            }
            catch { /* device vanished mid-read — normal during hotplug */ }
        }
        return result;

        static string? TryReadAll(string p)
        {
            try { return File.Exists(p) ? File.ReadAllText(p).Trim() : null; }
            catch { return null; }
        }
    }

    /// macOS (dev machines): system_profiler JSON. Slow (~2 s) but polled at 10 s.
    private static List<UsbDeviceInfo> EnumerateMac()
    {
        var result = new List<UsbDeviceInfo>();
        var psi = new System.Diagnostics.ProcessStartInfo("/usr/sbin/system_profiler", "SPUSBDataType -json")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using var proc = System.Diagnostics.Process.Start(psi);
        if (proc == null) return result;
        var json = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(5000);

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("SPUSBDataType", out var buses))
            foreach (var bus in buses.EnumerateArray())
                Walk(bus, result);
        return result;

        static void Walk(JsonElement node, List<UsbDeviceInfo> acc)
        {
            if (node.TryGetProperty("vendor_id", out var vidEl) &&
                node.TryGetProperty("product_id", out var pidEl))
            {
                // Values look like "0x03c3" or "0x05ac (Apple Inc.)".
                var vid = ExtractHex(vidEl.GetString());
                var pid = ExtractHex(pidEl.GetString());
                if (vid != null && pid != null)
                {
                    var name = node.TryGetProperty("_name", out var n) ? n.GetString() : null;
                    var manufacturer = node.TryGetProperty("manufacturer", out var m) ? m.GetString() : null;
                    acc.Add(new UsbDeviceInfo(vid, pid, name, manufacturer));
                }
            }
            if (node.TryGetProperty("_items", out var items))
                foreach (var child in items.EnumerateArray())
                    Walk(child, acc);
        }

        static string? ExtractHex(string? raw)
        {
            if (raw == null) return null;
            var s = raw.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
            var end = s.IndexOf(' ');
            if (end > 0) s = s[..end];
            return s.Length == 4 ? s.ToLowerInvariant() : null;
        }
    }
}
