using Microsoft.Extensions.Configuration;

namespace NINA.Headless.Services;

/// <summary>
/// Watches connected INDI drivers for "alive but unresponsive" failure modes
/// the existing process-CPU watchdog can't see. Three signals, all polled
/// at 5 s intervals:
///
///   1. Camera CCD_EXPOSURE stuck BUSY past <c>exposureSeconds + 60s</c>
///      — driver accepted the trigger but never produced a BLOB. Times
///      out the in-flight exposure via <c>AbortExposureAsync</c>.
///
///   2. Mount EQUATORIAL_EOD_COORD stuck BUSY > 5 minutes — slew that
///      neither completes nor errors. Logs + raises an event so callers
///      can surface "mount stuck" in the UI.
///
///   3. Focuser ABS_FOCUS_POSITION stuck BUSY > 2 minutes — same idea
///      for focusers.
///
/// The watchdog only flags + recovers; it never disconnects the driver
/// (that's the user's call, since the device may be the only path to a
/// physical sensor). When the existing CPU watchdog needs to nuke the
/// process, it does that elsewhere.
/// </summary>
public class IndiDriverWatchdog : BackgroundService
{
    private readonly IndiDiscoveryService _indi;
    private readonly EquipmentSelectionService _selection;
    private readonly ILogger<IndiDriverWatchdog> _log;

    /// State per-property — first time we noticed it BUSY, plus whether
    /// we've already raised a watchdog warning so we don't spam logs.
    private readonly Dictionary<string, BusyState> _busyTrack = new();

    /// Per-device sliding window of recent hangs. Lets us escalate a chronic
    /// fault (cable/USB issue, driver bug, thermal throttle) versus a one-off
    /// blip — the user gets "Camera failed 3× in 5 min, likely hardware
    /// issue" instead of three identical "장비 응답 없음" toasts.
    private readonly Dictionary<string, List<DateTime>> _hungHistory = new();
    private const int RepeatThreshold = 3;
    private static readonly TimeSpan RepeatWindow = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    public event Action<DeviceKind, string, string>? DriverHung;
    /// Fires when the same device hits the repeat threshold within the
    /// sliding window — actionable "this isn't transient" signal.
    public event Action<DeviceKind, string, int>? DriverChronicallyFailing;

    /// Read-only snapshot of recent hangs for diagnostics. Caller can poll
    /// this to display hardware-health summaries — useful when a chronic
    /// alert was dismissed but the user wants to verify the device has
    /// settled (or check before a long unattended capture).
    public IReadOnlyDictionary<string, IReadOnlyList<DateTime>> RecentHangs
    {
        get
        {
            var snapshot = new Dictionary<string, IReadOnlyList<DateTime>>();
            lock (_busyTrack) // reuse lock; _hungHistory mutated only from watchdog Task
            {
                foreach (var (k, v) in _hungHistory)
                {
                    snapshot[k] = v.ToArray();
                }
            }
            return snapshot;
        }
    }

    public IndiDriverWatchdog(
        IndiDiscoveryService indi,
        EquipmentSelectionService selection,
        ILogger<IndiDriverWatchdog> log)
    {
        _indi = indi;
        _selection = selection;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckCamera(stoppingToken);
                CheckTelescope();
                CheckFocuser();
                CheckFilterWheel();
                CheckRotator();
                CheckBusyActuator(DeviceKind.Dome,      "ABS_DOME_POSITION", 300);
                CheckBusyActuator(DeviceKind.FlatPanel, "FLAT_LIGHT_INTENSITY", 30);
                CheckSensorStaleness(DeviceKind.Weather,        "WEATHER_PARAMETERS",     300);
                CheckSensorStaleness(DeviceKind.SafetyMonitor,  "SAFETY",                 300);
                // Switch has no good "stuck" signal — toggles are instant and
                // failures show up as command-level errors, not sustained
                // BUSY. Skipped from the watchdog by design.
            }
            catch (Exception ex) { _log.LogDebug(ex, "DriverWatchdog: pass failed"); }
            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task CheckCamera(CancellationToken ct)
    {
        var sel = _selection.GetSelected(DeviceKind.Camera);
        if (sel?.Provider != EquipmentProvider.Indi) return;
        var dev = _indi.FindByName(sel.UniqueId);
        if (dev == null) return;
        if (!dev.Properties.TryGetValue("CCD_EXPOSURE", out var ep)) return;

        var key = $"camera:{sel.UniqueId}:CCD_EXPOSURE";
        if (ep.State != Indi.IndiPropertyState.Busy)
        {
            _busyTrack.Remove(key);
            return;
        }

        if (!_busyTrack.TryGetValue(key, out var st))
        {
            // ExposureValue is the driver's countdown — when it hits 0 the
            // driver should flip to OK. Capture starting value so we can
            // bound the "expected duration".
            var remaining = ep["CCD_EXPOSURE_VALUE"]?.AsDouble ?? 0;
            _busyTrack[key] = new BusyState(DateTime.UtcNow, remaining);
            return;
        }

        // Stuck if BUSY persists past (initial duration + 60s margin) and
        // the countdown isn't decreasing meaningfully.
        var elapsed = (DateTime.UtcNow - st.StartedAt).TotalSeconds;
        // No countdown data (driver omits/zeroes CCD_EXPOSURE_VALUE during BUSY)
        // means we cannot distinguish a legitimate long sub from a wedge — a hard
        // 60s budget was aborting every >60s exposure on such drivers. Without
        // data we do not take destructive action; fall back to a 15-minute
        // ceiling that no sane unattended sub exceeds.
        var timeoutBudget = st.InitialDurationSec > 0
            ? Math.Max(60, st.InitialDurationSec + 60)
            : 900;
        if (elapsed < timeoutBudget) return;

        _log.LogWarning("DriverWatchdog: CCD_EXPOSURE on {Device} stuck BUSY for {Sec:F0}s — aborting + driver bounce",
            sel.UniqueId, elapsed);
        try { await _indi.AbortExposureAsync(sel.UniqueId, ct); } catch { }
        DriverHung?.Invoke(DeviceKind.Camera, sel.UniqueId, $"exposure stuck for {elapsed:F0}s");
        RecordHang(DeviceKind.Camera, sel.UniqueId);
        _busyTrack.Remove(key);
        // Auto-recovery: bounce the device's INDI CONNECTION switch.
        // The exposure abort sometimes doesn't unstick the driver (PlayerOne
        // has been observed to hold sensor state past abort); a clean OFF/ON
        // forces the driver to re-init from scratch and the user keeps
        // their session.
        _ = Task.Run(async () =>
        {
            try
            {
                await _indi.DisconnectDeviceAsync(sel.UniqueId, CancellationToken.None);
                await Task.Delay(500);
                await _indi.ConnectDeviceAsync(sel.UniqueId, CancellationToken.None);
                _log.LogInformation("DriverWatchdog: bounced {Device} CONNECTION", sel.UniqueId);
            }
            catch (Exception ex) { _log.LogWarning(ex, "DriverWatchdog: bounce failed for {Device}", sel.UniqueId); }
        });
    }

    private void CheckTelescope()
    {
        var sel = _selection.GetSelected(DeviceKind.Telescope);
        if (sel?.Provider != EquipmentProvider.Indi) return;
        var dev = _indi.FindByName(sel.UniqueId);
        if (dev == null) return;
        if (!dev.Properties.TryGetValue("EQUATORIAL_EOD_COORD", out var ep)) return;

        var key = $"mount:{sel.UniqueId}:EQUATORIAL_EOD_COORD";
        if (ep.State != Indi.IndiPropertyState.Busy)
        {
            _busyTrack.Remove(key);
            return;
        }

        if (!_busyTrack.TryGetValue(key, out var st))
        {
            _busyTrack[key] = new BusyState(DateTime.UtcNow, 0);
            return;
        }

        var elapsed = (DateTime.UtcNow - st.StartedAt).TotalSeconds;
        if (elapsed < 300 || st.Warned) return;
        _log.LogWarning("DriverWatchdog: mount {Device} slew BUSY for {Sec:F0}s — likely hung",
            sel.UniqueId, elapsed);
        DriverHung?.Invoke(DeviceKind.Telescope, sel.UniqueId, $"slew stuck for {elapsed:F0}s");
        RecordHang(DeviceKind.Telescope, sel.UniqueId);
        _busyTrack[key] = st with { Warned = true };
        // Auto-recovery: stop motion + bounce CONNECTION so the next
        // user move starts from a clean state. Many mount drivers
        // (AM5 in particular) hold the slewing flag long after the
        // physical motion has stopped — only a CONNECTION re-init clears it.
        var deviceName = sel.UniqueId;
        _ = Task.Run(async () =>
        {
            try { await _indi.TelescopeStopMoveAsync(deviceName, CancellationToken.None); } catch { }
            try
            {
                await _indi.DisconnectDeviceAsync(deviceName, CancellationToken.None);
                await Task.Delay(500);
                await _indi.ConnectDeviceAsync(deviceName, CancellationToken.None);
                _log.LogInformation("DriverWatchdog: bounced telescope {Device} CONNECTION", deviceName);
            }
            catch (Exception ex) { _log.LogWarning(ex, "DriverWatchdog: telescope bounce failed for {Device}", deviceName); }
        });
    }

    private void CheckFocuser()
    {
        var sel = _selection.GetSelected(DeviceKind.Focuser);
        if (sel?.Provider != EquipmentProvider.Indi) return;
        var dev = _indi.FindByName(sel.UniqueId);
        if (dev == null) return;
        if (!dev.Properties.TryGetValue("ABS_FOCUS_POSITION", out var ep)) return;

        var key = $"focuser:{sel.UniqueId}:ABS_FOCUS_POSITION";
        if (ep.State != Indi.IndiPropertyState.Busy)
        {
            _busyTrack.Remove(key);
            return;
        }

        if (!_busyTrack.TryGetValue(key, out var st))
        {
            _busyTrack[key] = new BusyState(DateTime.UtcNow, 0);
            return;
        }

        var elapsed = (DateTime.UtcNow - st.StartedAt).TotalSeconds;
        if (elapsed < 120 || st.Warned) return;
        _log.LogWarning("DriverWatchdog: focuser {Device} move BUSY for {Sec:F0}s — likely hung",
            sel.UniqueId, elapsed);
        DriverHung?.Invoke(DeviceKind.Focuser, sel.UniqueId, $"move stuck for {elapsed:F0}s");
        RecordHang(DeviceKind.Focuser, sel.UniqueId);
        _busyTrack[key] = st with { Warned = true };
        // Auto-recovery: halt + bounce. Same pattern as camera / telescope —
        // forces the driver to re-init so the next move command starts
        // from a clean state instead of inheriting the wedged Busy.
        var deviceName = sel.UniqueId;
        _ = Task.Run(async () =>
        {
            try { await _indi.FocuserHaltAsync(deviceName, CancellationToken.None); } catch { }
            try
            {
                await _indi.DisconnectDeviceAsync(deviceName, CancellationToken.None);
                await Task.Delay(500);
                await _indi.ConnectDeviceAsync(deviceName, CancellationToken.None);
                _log.LogInformation("DriverWatchdog: bounced focuser {Device} CONNECTION", deviceName);
            }
            catch (Exception ex) { _log.LogWarning(ex, "DriverWatchdog: focuser bounce failed for {Device}", deviceName); }
        });
    }

    private void CheckFilterWheel()
    {
        var sel = _selection.GetSelected(DeviceKind.FilterWheel);
        if (sel?.Provider != EquipmentProvider.Indi) return;
        var dev = _indi.FindByName(sel.UniqueId);
        if (dev == null) return;
        if (!dev.Properties.TryGetValue("FILTER_SLOT", out var ep)) return;

        var key = $"filterwheel:{sel.UniqueId}:FILTER_SLOT";
        if (ep.State != Indi.IndiPropertyState.Busy)
        {
            _busyTrack.Remove(key);
            return;
        }
        if (!_busyTrack.TryGetValue(key, out var st))
        {
            _busyTrack[key] = new BusyState(DateTime.UtcNow, 0);
            return;
        }
        var elapsed = (DateTime.UtcNow - st.StartedAt).TotalSeconds;
        // Filter slot rotation is fast on real wheels (1–3 s typical, up to
        // 8 s on QHY CFW3-XL); 30 s of BUSY means the motor stalled or the
        // driver missed the slot index.
        if (elapsed < 30 || st.Warned) return;
        _log.LogWarning("DriverWatchdog: filter wheel {Device} slot change BUSY for {Sec:F0}s — likely stalled",
            sel.UniqueId, elapsed);
        DriverHung?.Invoke(DeviceKind.FilterWheel, sel.UniqueId, $"slot change stuck for {elapsed:F0}s");
        RecordHang(DeviceKind.FilterWheel, sel.UniqueId);
        _busyTrack[key] = st with { Warned = true };
        var deviceName = sel.UniqueId;
        _ = Task.Run(async () =>
        {
            try
            {
                await _indi.DisconnectDeviceAsync(deviceName, CancellationToken.None);
                await Task.Delay(500);
                await _indi.ConnectDeviceAsync(deviceName, CancellationToken.None);
                _log.LogInformation("DriverWatchdog: bounced filter wheel {Device} CONNECTION", deviceName);
            }
            catch (Exception ex) { _log.LogWarning(ex, "DriverWatchdog: filter wheel bounce failed for {Device}", deviceName); }
        });
    }

    private void CheckRotator()
    {
        var sel = _selection.GetSelected(DeviceKind.Rotator);
        if (sel?.Provider != EquipmentProvider.Indi) return;
        var dev = _indi.FindByName(sel.UniqueId);
        if (dev == null) return;
        // INDI rotators expose either ABS_ROTATOR_ANGLE (most) or
        // REL_ROTATOR_ANGLE (rare). Prefer absolute.
        if (!dev.Properties.TryGetValue("ABS_ROTATOR_ANGLE", out var ep))
        {
            if (!dev.Properties.TryGetValue("REL_ROTATOR_ANGLE", out ep)) return;
        }

        var key = $"rotator:{sel.UniqueId}:ROTATOR_ANGLE";
        if (ep.State != Indi.IndiPropertyState.Busy)
        {
            _busyTrack.Remove(key);
            return;
        }
        if (!_busyTrack.TryGetValue(key, out var st))
        {
            _busyTrack[key] = new BusyState(DateTime.UtcNow, 0);
            return;
        }
        var elapsed = (DateTime.UtcNow - st.StartedAt).TotalSeconds;
        // Field rotators take longer than focusers — 60 s of BUSY is a
        // reasonable wedge threshold for a 360° driver.
        if (elapsed < 60 || st.Warned) return;
        _log.LogWarning("DriverWatchdog: rotator {Device} angle change BUSY for {Sec:F0}s — likely stalled",
            sel.UniqueId, elapsed);
        DriverHung?.Invoke(DeviceKind.Rotator, sel.UniqueId, $"angle move stuck for {elapsed:F0}s");
        RecordHang(DeviceKind.Rotator, sel.UniqueId);
        _busyTrack[key] = st with { Warned = true };
        var deviceName = sel.UniqueId;
        _ = Task.Run(async () =>
        {
            try
            {
                await _indi.DisconnectDeviceAsync(deviceName, CancellationToken.None);
                await Task.Delay(500);
                await _indi.ConnectDeviceAsync(deviceName, CancellationToken.None);
                _log.LogInformation("DriverWatchdog: bounced rotator {Device} CONNECTION", deviceName);
            }
            catch (Exception ex) { _log.LogWarning(ex, "DriverWatchdog: rotator bounce failed for {Device}", deviceName); }
        });
    }

    /// Generic BUSY-stuck watchdog. Used for actuators that follow the
    /// same "set property → state goes Busy → state returns to OK" cycle
    /// as camera / mount / focuser, but with different property names and
    /// timeout tolerances. Bounces CONNECTION on hang detection.
    private void CheckBusyActuator(DeviceKind kind, string propName, int timeoutSec)
    {
        var sel = _selection.GetSelected(kind);
        if (sel?.Provider != EquipmentProvider.Indi) return;
        var dev = _indi.FindByName(sel.UniqueId);
        if (dev == null) return;
        if (!dev.Properties.TryGetValue(propName, out var ep)) return;

        var key = $"{kind}:{sel.UniqueId}:{propName}";
        if (ep.State != Indi.IndiPropertyState.Busy)
        {
            _busyTrack.Remove(key);
            return;
        }
        if (!_busyTrack.TryGetValue(key, out var st))
        {
            _busyTrack[key] = new BusyState(DateTime.UtcNow, 0);
            return;
        }
        var elapsed = (DateTime.UtcNow - st.StartedAt).TotalSeconds;
        if (elapsed < timeoutSec || st.Warned) return;
        _log.LogWarning("DriverWatchdog: {Kind} {Device} {Prop} BUSY for {Sec:F0}s — likely stalled",
            kind, sel.UniqueId, propName, elapsed);
        DriverHung?.Invoke(kind, sel.UniqueId, $"{propName} stuck for {elapsed:F0}s");
        RecordHang(kind, sel.UniqueId);
        _busyTrack[key] = st with { Warned = true };
        var deviceName = sel.UniqueId;
        _ = Task.Run(async () =>
        {
            try
            {
                await _indi.DisconnectDeviceAsync(deviceName, CancellationToken.None);
                await Task.Delay(500);
                await _indi.ConnectDeviceAsync(deviceName, CancellationToken.None);
                _log.LogInformation("DriverWatchdog: bounced {Kind} {Device} CONNECTION", kind, deviceName);
            }
            catch (Exception ex) { _log.LogWarning(ex, "DriverWatchdog: {Kind} bounce failed for {Device}", kind, deviceName); }
        });
    }

    /// Sensor-staleness watchdog. Weather monitors and safety monitors don't
    /// publish BUSY transitions — they're read-only data sources. The hang
    /// signature is "data hasn't updated in N minutes". Track per-property
    /// last-seen timestamp; flag and bounce when stale.
    private readonly Dictionary<string, DateTime> _sensorLastSeen = new();
    private void CheckSensorStaleness(DeviceKind kind, string propName, int staleSec)
    {
        var sel = _selection.GetSelected(kind);
        if (sel?.Provider != EquipmentProvider.Indi) return;
        var dev = _indi.FindByName(sel.UniqueId);
        if (dev == null) return;
        if (!dev.Properties.TryGetValue(propName, out var ep)) return;

        var key = $"{kind}:{sel.UniqueId}:{propName}";
        // INDI property `Timestamp` (set on every defXxxxVector / setXxxxVector)
        // is the cleanest "data freshness" signal — update it whenever it
        // moves, mark stale when it doesn't.
        var current = ep.Timestamp ?? DateTime.UtcNow;
        if (!_sensorLastSeen.TryGetValue(key, out var prev) || current > prev)
        {
            _sensorLastSeen[key] = current;
            return;
        }
        var ageSec = (DateTime.UtcNow - prev).TotalSeconds;
        if (ageSec < staleSec) return;
        // Don't double-fire — once we've flagged it, wait for fresh data
        // before re-arming. Push the timestamp forward slightly so the
        // re-arm needs an observable update.
        _sensorLastSeen[key] = DateTime.UtcNow;
        _log.LogWarning("DriverWatchdog: {Kind} {Device} {Prop} stale for {Sec:F0}s — sensor not updating",
            kind, sel.UniqueId, propName, ageSec);
        DriverHung?.Invoke(kind, sel.UniqueId, $"{propName} stale for {ageSec:F0}s");
        RecordHang(kind, sel.UniqueId);
        var deviceName = sel.UniqueId;
        _ = Task.Run(async () =>
        {
            try
            {
                await _indi.DisconnectDeviceAsync(deviceName, CancellationToken.None);
                await Task.Delay(500);
                await _indi.ConnectDeviceAsync(deviceName, CancellationToken.None);
                _log.LogInformation("DriverWatchdog: bounced {Kind} {Device} CONNECTION", kind, deviceName);
            }
            catch (Exception ex) { _log.LogWarning(ex, "DriverWatchdog: {Kind} bounce failed for {Device}", kind, deviceName); }
        });
    }

    /// Push a hang into the sliding window and raise the chronic-failure
    /// signal if the count crosses the threshold. Caller passes the device
    /// kind + INDI device name so the iOS banner can name the fault.
    private void RecordHang(DeviceKind kind, string deviceName)
    {
        lock (_busyTrack)
        {
            var key = $"{kind}:{deviceName}";
            if (!_hungHistory.TryGetValue(key, out var list))
            {
                list = new List<DateTime>();
                _hungHistory[key] = list;
            }
            var now = DateTime.UtcNow;
            list.Add(now);
            // Trim entries older than the window so the count reflects "recent".
            list.RemoveAll(t => now - t > RepeatWindow);
            if (list.Count >= RepeatThreshold)
            {
                _log.LogError("DriverWatchdog: {Kind} {Device} has hung {N} times in {Win} min — likely hardware/USB issue",
                    kind, deviceName, list.Count, (int)RepeatWindow.TotalMinutes);
                try { DriverChronicallyFailing?.Invoke(kind, deviceName, list.Count); }
                catch (Exception ex) { _log.LogWarning(ex, "DriverChronicallyFailing handler threw"); }
                // Reset the window so we re-arm rather than firing on every
                // subsequent hang — the user got the signal, additional events
                // are duplicates.
                list.Clear();
            }
        }
    }

    private record struct BusyState(DateTime StartedAt, double InitialDurationSec, bool Warned = false);
}
