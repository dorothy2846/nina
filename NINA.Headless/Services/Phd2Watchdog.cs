using NINA.Headless.Services.Remote;

namespace NINA.Headless.Services;

/// <summary>
/// Keeps the PHD2 connection alive across crashes / network blips.
/// Periodically pings <see cref="Phd2Service.IsConnectedToServer"/>; when
/// it's been false for more than the grace window AND the user had
/// previously asked PHD2 to start (we don't auto-launch if PHD2 was never
/// touched this session), tries to re-establish via
/// <see cref="Phd2Service.EnsureStartedAsync"/>.
///
/// Why a separate watchdog instead of inline retry: PHD2 sometimes hangs
/// without dropping the socket — heartbeat is the only honest signal.
/// And we want the user-visible "guider down" event surfaced regardless
/// of whether the next caller happens to retry.
/// </summary>
public class Phd2Watchdog : BackgroundService
{
    private readonly Phd2Service _phd2;
    private readonly RemoteEventBus _eventBus;
    private readonly ILogger<Phd2Watchdog> _log;

    /// True once any user code has called EnsureStartedAsync — without this
    /// the watchdog would be permanently trying to launch PHD2 even for
    /// users who haven't connected to the Guiding tab yet.
    private bool _userStartedSession;
    private DateTime? _lastSeenConnected;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    /// Grace window before we consider PHD2 "lost". PHD2's TCP socket can
    /// blip during long calibration runs; 30s avoids false positives.
    private static readonly TimeSpan LossGrace = TimeSpan.FromSeconds(30);

    public Phd2Watchdog(Phd2Service phd2, RemoteEventBus eventBus, ILogger<Phd2Watchdog> log)
    {
        _phd2 = phd2;
        _eventBus = eventBus;
        _log = log;
    }

    public void MarkUserStarted() => _userStartedSession = true;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Tick(stoppingToken); }
            catch (Exception ex) { _log.LogDebug(ex, "Phd2Watchdog: tick failed"); }
            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// Consecutive RPC probe failures while the socket looks fine. Two
    /// misses (~20s) = a modal dialog or hard hang; kill + relaunch is the
    /// only recovery that works for modals.
    private int _unresponsiveStrikes;

    private async Task Tick(CancellationToken ct)
    {
        if (_phd2.IsConnectedToServer)
        {
            _lastSeenConnected = DateTime.UtcNow;
            // Side-effect of an active connection: any caller that's
            // ever ensured-started is treated as "user wants this".
            if (_phd2.CurrentAppState != "Disconnected") _userStartedSession = true;

            // Socket-alive is not loop-alive: probe the RPC event loop.
            if (await _phd2.ProbeResponsiveAsync(ct))
            {
                _unresponsiveStrikes = 0;
                return;
            }
            _unresponsiveStrikes++;
            _log.LogWarning("Phd2Watchdog: RPC unresponsive (strike {N}/2) — modal dialog or hang suspected", _unresponsiveStrikes);
            if (_unresponsiveStrikes >= 2)
            {
                _unresponsiveStrikes = 0;
                try { _eventBus.Broadcast("Phd2Wedged", new { timestamp = DateTime.UtcNow }); }
                catch (Exception ex) { _log.LogDebug(ex, "Phd2Wedged broadcast threw"); }
                var recovered = await _phd2.ForceRestartAsync(ct);
                _log.LogWarning("Phd2Watchdog: force restart {Result}", recovered ? "succeeded" : "failed");
                try { _eventBus.Broadcast(recovered ? "Phd2Reconnected" : "Phd2Lost", new { timestamp = DateTime.UtcNow }); }
                catch (Exception ex) { _log.LogDebug(ex, "broadcast threw"); }
            }
            return;
        }

        if (!_userStartedSession) return;

        // Connection is down — wait grace before trying to recover.
        var lostAt = _lastSeenConnected ?? DateTime.UtcNow;
        if (DateTime.UtcNow - lostAt < LossGrace) return;

        _log.LogWarning("Phd2Watchdog: PHD2 connection lost > {Grace}s — attempting auto-restart", LossGrace.TotalSeconds);
        try
        {
            _eventBus.Broadcast("Phd2Lost", new { timestamp = DateTime.UtcNow });
        }
        catch (Exception ex) { _log.LogDebug(ex, "Phd2Lost broadcast threw"); }

        // EnsureStartedAsync handles "already running, just connect" vs
        // "launch then connect" internally — single call covers both.
        var ok = await _phd2.EnsureStartedAsync(ct);
        if (ok)
        {
            _lastSeenConnected = DateTime.UtcNow;
            _log.LogInformation("Phd2Watchdog: re-established PHD2 connection");
            try { _eventBus.Broadcast("Phd2Reconnected", new { timestamp = DateTime.UtcNow }); }
            catch (Exception ex) { _log.LogDebug(ex, "Phd2Reconnected broadcast threw"); }
        }
        else
        {
            _log.LogWarning("Phd2Watchdog: auto-restart failed; will retry next tick");
        }
    }
}
