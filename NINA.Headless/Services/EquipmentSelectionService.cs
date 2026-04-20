using System.Collections.Concurrent;

namespace NINA.Headless.Services;

/// <summary>
/// Tracks available devices and the currently selected one for ALL equipment kinds.
///
/// Connection state is NOT stored here — it's derived from the underlying provider (INDI
/// CONNECTION.CONNECT, Alpaca Connected, etc.) via <see cref="ConnectivityProvider"/>. Keeping
/// a separate IsConnected flag caused sync bugs where the server believed the device was
/// disconnected while the INDI driver had actually completed connecting a second later.
///
/// Sources:
///   - INDI-discovered devices (added by IndiDiscoveryService for the relevant kind)
///   - Future: Alpaca, native SDK plugins
/// </summary>
public class EquipmentSelectionService
{
    /// <summary>Callback that reports the true connected state for a (kind, uniqueId) pair.
    /// Set once at startup by whichever service owns the underlying connection (IndiDiscoveryService
    /// for INDI devices). Returns false if no provider or the device isn't known.</summary>
    public Func<DeviceKind, string, bool>? ConnectivityProvider { get; set; }

    // Keep devices in the list for a minute even if a discovery pass doesn't see them. AM5's
    // serial handshake during a disconnect→connect cycle can take 25s while the driver
    // transiently republishes properties without DRIVER_INTERFACE set, which would otherwise
    // evict the device mid-reconnect and cause the follow-up Connect call to 404.
    private static readonly TimeSpan StickyGrace = TimeSpan.FromSeconds(60);

    private readonly object _lock = new();
    private readonly Dictionary<DeviceKind, Dictionary<string, EquipmentDescriptor>> _devices = new();
    private readonly Dictionary<DeviceKind, Dictionary<string, DateTime>> _lastSeen = new();
    private readonly Dictionary<DeviceKind, SelectionState> _state = new();

    public EquipmentSelectionService()
    {
        foreach (DeviceKind kind in Enum.GetValues<DeviceKind>())
        {
            _devices[kind] = new Dictionary<string, EquipmentDescriptor>();
            _lastSeen[kind] = new Dictionary<string, DateTime>();
            _state[kind] = new SelectionState();
        }
    }

    public event Action<DeviceKind>? StateChanged;

    public IReadOnlyList<EquipmentDescriptor> GetAvailable(DeviceKind kind)
    {
        lock (_lock)
        {
            return _devices[kind].Values
                .OrderBy(d => d.Name)
                .ToList();
        }
    }

    public string? GetSelectedId(DeviceKind kind)
    {
        lock (_lock) return _state[kind].SelectedId;
    }

    /// <summary>Derived from the underlying provider — no local flag. A device is "connected"
    /// iff it is selected AND the provider reports it connected right now.</summary>
    public bool IsConnected(DeviceKind kind)
    {
        EquipmentDescriptor? selected;
        lock (_lock)
        {
            var id = _state[kind].SelectedId;
            if (id == null || !_devices[kind].TryGetValue(id, out var d)) return false;
            selected = d;
        }
        return ConnectivityProvider?.Invoke(kind, selected.UniqueId) ?? false;
    }

    public EquipmentDescriptor? GetSelected(DeviceKind kind)
    {
        lock (_lock)
        {
            var id = _state[kind].SelectedId;
            if (id == null) return null;
            return _devices[kind].TryGetValue(id, out var d) ? d : null;
        }
    }

    public bool Select(DeviceKind kind, string deviceId)
    {
        lock (_lock)
        {
            if (!_devices[kind].ContainsKey(deviceId)) return false;
            _state[kind].SelectedId = deviceId;
        }
        StateChanged?.Invoke(kind);
        return true;
    }

    /// <summary>Record the user's selection. Connection itself is a separate operation driven
    /// by the controller (e.g. IndiDiscoveryService.ConnectDeviceAsync). Returns false if the
    /// deviceId isn't currently available.</summary>
    public bool Connect(DeviceKind kind, string deviceId) => Select(kind, deviceId);

    /// <summary>No-op on selection state (selection persists across disconnect so reconnect is
    /// one-click). Physical disconnect is the controller's responsibility.</summary>
    public void Disconnect(DeviceKind kind)
    {
        StateChanged?.Invoke(kind);
    }

    /// <summary>Replace devices coming from a single provider (e.g., INDI) for the given kind.
    /// Devices that disappeared stay in the list for a short grace period so transient driver
    /// restarts don't flicker the UI.</summary>
    public void UpdateProviderDevices(DeviceKind kind, EquipmentProvider provider, IEnumerable<EquipmentDescriptor> discovered)
    {
        var incoming = discovered.Where(d => d.Provider == provider).ToDictionary(d => d.Id);
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            var lastSeen = _lastSeen[kind];

            foreach (var kv in incoming)
            {
                _devices[kind][kv.Key] = kv.Value;
                lastSeen[kv.Key] = now;
            }

            // Drop only entries from this provider that haven't been seen for StickyGrace.
            foreach (var key in _devices[kind].Keys.ToList())
            {
                if (_devices[kind][key].Provider != provider) continue;
                if (incoming.ContainsKey(key)) continue;
                if (!lastSeen.TryGetValue(key, out var seen) || now - seen > StickyGrace)
                {
                    _devices[kind].Remove(key);
                    lastSeen.Remove(key);
                }
            }
        }
        StateChanged?.Invoke(kind);
    }

    private class SelectionState
    {
        public string? SelectedId;
    }
}

public enum DeviceKind
{
    Camera,
    Telescope,
    Focuser,
    FilterWheel,
    Rotator,
    Dome,
    Guider,
    FlatPanel,
    Switch,
    Weather,
    SafetyMonitor
}

public enum EquipmentProvider
{
    Indi,
    Alpaca,
    Native
}

public record EquipmentDescriptor(
    string Id,
    string Name,
    DeviceKind Kind,
    EquipmentProvider Provider,
    string UniqueId,
    string? Host = null,
    int? Port = null);
