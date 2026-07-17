using System.Collections.Concurrent;
using NINA.Core.Enum;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Equipment.MyFocuser;
using NINA.Equipment.Equipment.MyFilterWheel;
using NINA.Equipment.Equipment.MyRotator;
using NINA.Equipment.Equipment.MyDome;
using NINA.Equipment.Equipment.MyFlatDevice;
using NINA.Equipment.Equipment.MyWeatherData;
using NINA.Equipment.Equipment.MySafetyMonitor;
using NINA.Equipment.Equipment.MySwitch;
using NINA.Headless.Indi;

namespace NINA.Headless.Services;

/// <summary>
/// Maintains a persistent connection to the local indiserver,
/// tracks INDI devices, and feeds camera entries into CameraSelectionService.
/// </summary>
public partial class IndiDiscoveryService : BackgroundService
{
    private readonly IndiServerManager _server;
    private readonly EquipmentSelectionService _equipment;
    private readonly ILogger<IndiDiscoveryService> _log;

    private IndiClient? _client;

    // Server-side park state per device. AM5's TELESCOPE_PARK.PARK switch doubles as "slew-to-home"
    // which we intentionally avoid, so the INDI property doesn't reflect the user's park intent.
    // When parked here, motion endpoints (move/slew/home) are rejected until unpark.
    private readonly ConcurrentDictionary<string, bool> _parkedTelescope = new();

    public bool IsTelescopeParked(string deviceName) =>
        _parkedTelescope.TryGetValue(deviceName, out var p) && p;

    /// True when the device exists in the INDI device list and reports
    /// its CONNECTION switch as on. Used by the mediator bridge so a
    /// disconnected device flips the consumer-visible Connected flag.
    public bool IsTelescopeReady(string deviceName) => IsDeviceConnected(deviceName);
    public bool IsFocuserReady(string deviceName) => IsDeviceConnected(deviceName);
    private bool IsDeviceConnected(string deviceName)
    {
        var dev = _client?.GetDevice(deviceName);
        if (dev == null || !dev.IsConnected) return false;
        return IntentConnected(deviceName);
    }

    private bool IntentConnected(string deviceName)
    {
        lock (_intentLock)
        {
            return _intent.TryGetValue(deviceName, out var intent) && intent == ConnectionIntent.Connected;
        }
    }

    // Bounded intent fallback. When a driver bounces the whole device (AM5 emits a
    // whole-device delProperty on disconnect and takes ~30s to re-def CONNECTION), the
    // property vanishes from our cache; reporting "disconnected" during that window
    // whiplashed the UI on every poll. But an UNBOUNDED fallback is a lie — a stopped or
    // crashed driver would read "connected" forever. Trust the user's intent only for
    // this long after CONNECTION disappeared, then report the truth.
    private static readonly TimeSpan IntentFallbackGrace = TimeSpan.FromSeconds(90);
    private readonly Dictionary<string, DateTime> _connectionLostAt = new();

    /// <summary>Single source of truth for "is this device connected?" in status
    /// responses: the driver's CONNECTION switch when published, else the user's last
    /// intent — but only within <see cref="IntentFallbackGrace"/> of the property
    /// vanishing, so a dead driver eventually reports disconnected instead of
    /// masking the failure.</summary>
    internal bool EffectiveConnected(string deviceName, IndiDevice? dev = null)
    {
        dev ??= _client?.GetDevice(deviceName);
        if (dev != null && dev.Properties.ContainsKey("CONNECTION"))
        {
            lock (_intentLock) { _connectionLostAt.Remove(deviceName); }
            return dev.IsConnected;
        }
        lock (_intentLock)
        {
            if (!_intent.TryGetValue(deviceName, out var intent) || intent != ConnectionIntent.Connected)
                return false;
            if (!_connectionLostAt.TryGetValue(deviceName, out var lostAt))
            {
                _connectionLostAt[deviceName] = DateTime.UtcNow;
                return true;
            }
            return DateTime.UtcNow - lostAt < IntentFallbackGrace;
        }
    }

    public IndiDiscoveryService(
        IndiServerManager server,
        EquipmentSelectionService equipment,
        ILogger<IndiDiscoveryService> log)
    {
        _server = server;
        _equipment = equipment;
        _log = log;

        // Reports "is this device connected?". Primary signal is the INDI CONNECTION.CONNECT
        // switch. Fallback: if the driver just bounced the whole device (AM5 emits a delProperty
        // on disconnect and takes 30+s to re-def CONNECTION), trust the user's last intent so
        // the app doesn't flicker to "disconnected" during an in-flight reconnect.
        _equipment.ConnectivityProvider = (_, uniqueId) => EffectiveConnected(uniqueId);
    }

    // ----- Connection intent + coalescing worker -----
    // One long-running background task per device converges the driver toward the latest
    // requested state. Rapid user toggles (Connect → Disconnect → Connect) previously queued
    // as three serialized INDI round-trips — the worker reads the current intent at every step
    // so intermediate flips are dropped. The HTTP handlers just set an intent and return 202.

    private enum ConnectionIntent { Connected, Disconnected }

    private readonly Dictionary<string, ConnectionIntent> _intent = new();
    private readonly Dictionary<string, Task> _intentWorker = new();
    private readonly object _intentLock = new();

    /// <summary>Force the INDI discovery loop to drop its current client and rebuild it.
    /// Use when the device list feels stale (e.g. plugged a camera in after the client
    /// handshake completed, or the driver republished properties without a clean delProperty
    /// cycle). Returns immediately; the ExecuteAsync loop naturally reconnects within ~3s.</summary>
    /// <summary>Given an INDI device name, returns the driver's executable name (e.g. <c>indi_asi_ccd</c>)
    /// from the device's DRIVER_INFO.DRIVER_EXEC property. Null if the device isn't known or the
    /// driver hasn't yet published DRIVER_INFO. This is what IndiServerManager.RestartDriverAsync
    /// needs to target a single driver without touching the rest of the rig.</summary>
    public string? GetDriverExec(string deviceName)
    {
        var client = _client;
        var dev = client?.GetDevice(deviceName);
        if (dev == null) return null;
        if (!dev.Properties.TryGetValue("DRIVER_INFO", out var prop)) return null;
        return prop["DRIVER_EXEC"]?.Value;
    }

    public async Task RequestRescanAsync(CancellationToken ct)
    {
        var client = _client;
        if (client == null) return;
        // Dispose flips IsConnected via _tcp.Dispose, which ExecuteAsync's inner while-loop
        // picks up on its next 1s tick. The loop's finally block then re-nulls _client and
        // re-enters the outer loop, which creates a fresh IndiClient and re-handshakes —
        // rebuilding the device list from scratch along the way.
        try { await client.DisposeAsync(); }
        catch (Exception ex) { _log.LogWarning(ex, "Rescan: client dispose failed"); }
        _log.LogInformation("INDI rescan requested — client disposed, loop will reconnect");
    }

    /// <summary>Record the user's connect/disconnect intent WITHOUT spawning a worker —
    /// for controllers that actuate CONNECTION synchronously themselves (camera, focuser,
    /// filter wheel, dome, rotator). The recorded intent is what re-arms the device after
    /// a driver restart (<see cref="ReArmIntentWorkers"/>) and feeds the bounded status
    /// fallback (<see cref="EffectiveConnected"/>); without it those protections only
    /// covered the telescope, whose controller goes through the intent worker.</summary>
    public void RecordConnectionIntent(string deviceName, bool connected)
    {
        lock (_intentLock)
        {
            _intent[deviceName] = connected ? ConnectionIntent.Connected : ConnectionIntent.Disconnected;
        }
    }

    /// <summary>Ask the worker to drive <paramref name="deviceName"/> toward the given state.
    /// Returns immediately; the background task converges even if intents change mid-flight.</summary>
    public void RequestConnectionIntent(string deviceName, bool connected)
    {
        lock (_intentLock)
        {
            _intent[deviceName] = connected ? ConnectionIntent.Connected : ConnectionIntent.Disconnected;
            if (_intentWorker.TryGetValue(deviceName, out var existing) && !existing.IsCompleted) return;
            _intentWorker[deviceName] = Task.Run(() => IntentWorkerLoop(deviceName));
        }
    }

    /// On client reconnect the IntentWorkerLoops we had running all exited
    /// (their `client.IsConnected` check tripped). Re-spawn one per device
    /// that still has `Connected` intent so the driver gets re-armed as
    /// soon as it re-defs CONNECTION.
    private void RestoreIntentWorkers()
    {
        lock (_intentLock)
        {
            foreach (var (deviceName, intent) in _intent)
            {
                if (intent != ConnectionIntent.Connected) continue;
                if (_intentWorker.TryGetValue(deviceName, out var existing) && !existing.IsCompleted) continue;
                _intentWorker[deviceName] = Task.Run(() => IntentWorkerLoop(deviceName));
            }
        }
    }

    private async Task IntentWorkerLoop(string deviceName)
    {
        while (true)
        {
            ConnectionIntent target;
            lock (_intentLock)
            {
                if (!_intent.TryGetValue(deviceName, out target))
                {
                    _intentWorker.Remove(deviceName);
                    return;
                }
            }

            var client = _client;
            if (client == null || !client.IsConnected)
            {
                lock (_intentLock) { _intentWorker.Remove(deviceName); }
                return;
            }

            var dev = client.GetDevice(deviceName);
            var isConn = dev?.IsConnected ?? false;
            var wantConn = target == ConnectionIntent.Connected;

            if (isConn == wantConn)
            {
                // Already in target state. Keep the intent entry — ConnectivityProvider reads
                // it as a fallback when the driver temporarily drops CONNECTION from our cache
                // (AM5 emits a whole-device delProperty on disconnect and takes ~30s to re-def
                // CONNECTION). Just release the worker slot.
                lock (_intentLock)
                {
                    if (_intent.TryGetValue(deviceName, out var current) && current == target)
                    {
                        _intentWorker.Remove(deviceName);
                        return;
                    }
                }
                continue; // Intent changed after our snapshot — re-evaluate.
            }

            try
            {
                if (wantConn) await ConnectDeviceAsync(deviceName, CancellationToken.None);
                else          await DisconnectDeviceAsync(deviceName, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "IntentWorker step failed for {Device}", deviceName);
                await Task.Delay(500);
            }
            // Loop again — re-read intent in case user toggled while we were working.
        }
    }

    public IndiClient? Client => _client;

    public IReadOnlyList<IndiDevice> Devices => _client?.Devices ?? new List<IndiDevice>();

    /// <summary>List INDI devices of the given equipment kind, mapped to the JSON shape the app expects.</summary>
    public IEnumerable<object> ListDevices(IndiDeviceKind kind)
    {
        var devices = _client?.Devices ?? new List<IndiDevice>();
        return devices.Where(d => kind switch
        {
            IndiDeviceKind.Camera => d.IsCamera,
            IndiDeviceKind.Telescope => d.IsTelescope,
            IndiDeviceKind.Focuser => d.IsFocuser,
            IndiDeviceKind.FilterWheel => d.IsFilterWheel,
            _ => false
        }).Select(d => new { id = $"indi:{d.Name}", name = $"{d.Name} (INDI)" });
    }

    /// <summary>Look up an INDI device by its raw INDI name (UniqueId in EquipmentDescriptor).</summary>
    public IndiDevice? FindByName(string name) => _client?.GetDevice(name);

    public bool IsClientReady => _client?.IsConnected == true;

    // ----- Camera exposure / image transfer -----

    private readonly ConcurrentDictionary<string, TaskCompletionSource<(byte[] bytes, string? format)>> _pendingExposure = new();
    private bool _blobHandlerRegistered;

    // Tracks which devices we've already announced as connected. Lets DeviceConnected
    // fire exactly once per connect, including the boot path where the driver was
    // already CONNECT=On before we hooked DevicesChanged.
    private readonly HashSet<string> _announcedConnected = new();

    // --- Camera operations ---

    private readonly Dictionary<string, SemaphoreSlim> _deviceLocks = new();
}

public enum IndiDeviceKind { Camera, Telescope, Focuser, FilterWheel }
