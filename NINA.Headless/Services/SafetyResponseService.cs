using NINA.Equipment.Equipment.MySafetyMonitor;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Headless.Services.Remote;

namespace NINA.Headless.Services;

/// <summary>
/// Reacts to SafetyMonitor state changes during a session. The mediator
/// broadcasts SafetyMonitorInfo on every poll; we look for the
/// safe → unsafe edge and:
///   1. Broadcast a high-priority "SafetyUnsafe" event over the WS bus so
///      iOS can throw an urgent banner / sound an alarm.
///   2. Abort any in-flight camera exposure — most unsafe events
///      (cloud roll-in, dome closing, dew sensor trip) make the current
///      frame useless anyway, and the abort releases the camera so the
///      sequencer can react to safety state without waiting on a stuck
///      capture.
///
/// We do *not* park the mount automatically here — that's a destructive
/// action the user should opt into via SequencerSettings.CloseOnUnsafe
/// (which the sequencer enforces). The job of this service is the
/// real-time "stop what you're doing right now" reflex; long-term parking
/// belongs to whoever owns the unattended session policy.
/// </summary>
public class SafetyResponseService : BackgroundService, ISafetyMonitorConsumer, IDisposable
{
    private readonly ISafetyMonitorMediator _safety;
    private readonly RemoteEventBus _eventBus;
    private readonly EquipmentSelectionService _equipment;
    private readonly IndiDiscoveryService _indi;
    private readonly NinaStateService _state;
    private readonly Remote.ApnsPushService _push;
    private readonly ILogger<SafetyResponseService> _log;

    /// Last observed IsSafe value. We need to detect the *transition*
    /// (safe → unsafe), not just "currently unsafe" — otherwise we'd
    /// re-fire the response on every status broadcast while the bad
    /// condition persists.
    private bool? _lastIsSafe;

    public SafetyResponseService(
        ISafetyMonitorMediator safety,
        RemoteEventBus eventBus,
        EquipmentSelectionService equipment,
        IndiDiscoveryService indi,
        NinaStateService state,
        Remote.ApnsPushService push,
        ILogger<SafetyResponseService> log)
    {
        _safety = safety;
        _eventBus = eventBus;
        _equipment = equipment;
        _indi = indi;
        _state = state;
        _push = push;
        _log = log;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _safety.RegisterConsumer(this);
        stoppingToken.Register(() => _safety.RemoveConsumer(this));
        return Task.CompletedTask;
    }

    public void UpdateDeviceInfo(SafetyMonitorInfo deviceInfo)
    {
        if (!deviceInfo.Connected)
        {
            // Device dropped — clear our latch so the next reconnect re-arms
            // the transition detector. Don't fire a "safe" event off a
            // disconnect; the iOS UI surfaces lost connection separately.
            _lastIsSafe = null;
            return;
        }

        var current = deviceInfo.IsSafe;
        var prev = _lastIsSafe;
        _lastIsSafe = current;

        // Transition: safe → unsafe (or first observation that's unsafe).
        if (prev != false && current == false)
        {
            HandleUnsafe(deviceInfo);
        }
        // Transition: unsafe → safe — informational, not actionable.
        else if (prev == false && current == true)
        {
            _log.LogInformation("SafetyMonitor: returned to SAFE on {Device}", deviceInfo.Name);
            try
            {
                _eventBus.Broadcast("SafetyRestored", new
                {
                    timestamp = DateTime.UtcNow,
                    device = deviceInfo.Name,
                });
            }
            catch (Exception ex) { _log.LogDebug(ex, "SafetyRestored broadcast threw"); }
        }
    }

    private void HandleUnsafe(SafetyMonitorInfo info)
    {
        _log.LogWarning("SafetyMonitor: UNSAFE detected on {Device} — aborting capture", info.Name);

        // 1. Push critical event — iOS prioritises this in its alert handler.
        try
        {
            _eventBus.Broadcast("SafetyUnsafe", new
            {
                timestamp = DateTime.UtcNow,
                device = info.Name,
                isExposing = _state.IsCaptureInFlight,
            });
        }
        catch (Exception ex) { _log.LogWarning(ex, "SafetyUnsafe broadcast threw"); }

        _ = _push.NotifyAllAsync("⚠️ 안전 경보",
            $"{info.Name} 이(가) 위험 상태를 감지했습니다. 진행 중인 노출을 중단합니다.",
            "safety", bypassThrottle: true);

        // 2. Abort the in-flight exposure if any. Not destructive — the
        // half-frame is discarded anyway because the unsafe condition
        // (clouds rolled in, etc.) made it unusable.
        if (_state.IsCaptureInFlight)
        {
            var cam = _equipment.GetSelected(DeviceKind.Camera);
            if (cam != null && cam.Provider == EquipmentProvider.Indi)
            {
                _ = Task.Run(async () =>
                {
                    try { await _indi.AbortExposureAsync(cam.UniqueId, CancellationToken.None); }
                    catch (Exception ex) { _log.LogWarning(ex, "SafetyResponse: abort exposure failed"); }
                });
            }
            else if (cam != null)
            {
                // Don't pretend: the abort reflex only reaches INDI cameras today.
                _log.LogWarning("SafetyResponse: cannot abort exposure on non-INDI camera ({Provider}) — unsafe-condition abort unsupported for this provider", cam.Provider);
            }
        }
    }
}
