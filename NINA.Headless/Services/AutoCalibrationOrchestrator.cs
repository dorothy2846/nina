namespace NINA.Headless.Services;

/// <summary>
/// End-to-end PHD2 calibration automation. User taps one button; we:
///   1. Save current mount RA/Dec
///   2. Slew to meridian (HA=0) at target declination — best geometry for calibration
///   3. Wait for slew complete
///   4. Trigger PHD2 loop + find star + guide(recalibrate=true)
///   5. Wait for PHD2 AppState to reach Guiding (success) or Stopped (failure)
///   6. If returnToTarget: slew back to saved position + stop PHD2
///
/// Long-running — iOS polls <see cref="Snapshot"/> for phase + message. <see cref="Cancel"/>
/// aborts and attempts to return to saved position.
/// </summary>
public class AutoCalibrationOrchestrator
{
    public enum Phase
    {
        Idle, SavingPosition, Slewing, StartingGuide, FindingStar, Calibrating,
        ReturningToTarget, Done, Cancelled, Failed
    }

    private readonly EquipmentSelectionService _equipment;
    private readonly IndiDiscoveryService _indi;
    private readonly Phd2Service _phd2;
    private readonly ILogger<AutoCalibrationOrchestrator> _log;

    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private Task? _runTask;

    private Phase _phase = Phase.Idle;
    private string _message = "";
    private string? _error;
    private (double Ra, double Dec)? _savedPosition;
    private DateTime _startedAt;

    public AutoCalibrationOrchestrator(
        EquipmentSelectionService equipment,
        IndiDiscoveryService indi,
        Phd2Service phd2,
        ILogger<AutoCalibrationOrchestrator> log)
    {
        _equipment = equipment;
        _indi = indi;
        _phd2 = phd2;
        _log = log;
    }

    public record Request(
        bool ReturnToTarget = true,
        double DeclinationTargetDegrees = 0.0,
        int SlewTimeoutSeconds = 180,
        int CalibrationTimeoutSeconds = 300);

    public bool IsRunning { get { lock (_lock) return _runTask != null && !_runTask.IsCompleted; } }

    public object Snapshot()
    {
        lock (_lock)
        {
            var elapsed = _runTask == null ? 0 : (int)(DateTime.UtcNow - _startedAt).TotalSeconds;
            return new
            {
                running = IsRunning,
                phase = _phase.ToString(),
                message = _message,
                error = _error,
                elapsedSeconds = elapsed,
                savedRa = _savedPosition?.Ra,
                savedDec = _savedPosition?.Dec
            };
        }
    }

    public Task StartAsync(Request req)
    {
        lock (_lock)
        {
            if (IsRunning) throw new InvalidOperationException("Auto-calibrate already running");
            _cts = new CancellationTokenSource();
            _phase = Phase.Idle;
            _message = "Starting";
            _error = null;
            _savedPosition = null;
            _startedAt = DateTime.UtcNow;
            _runTask = Task.Run(() => RunAsync(req, _cts.Token));
        }
        return Task.CompletedTask;
    }

    public void Cancel()
    {
        lock (_lock) _cts?.Cancel();
    }

    private async Task RunAsync(Request req, CancellationToken ct)
    {
        try
        {
            var mount = _equipment.GetSelected(DeviceKind.Telescope);
            if (mount == null || !_equipment.IsConnected(DeviceKind.Telescope))
                throw new InvalidOperationException("Mount not connected. Connect the mount before auto-calibration.");
            if (mount.Provider != EquipmentProvider.Indi)
                throw new InvalidOperationException("Auto-calibrate currently supports INDI mounts only.");

            // 1. Save position for return.
            SetState(Phase.SavingPosition, "Saving current mount position");
            var pos = _indi.GetTelescopePosition(mount.UniqueId)
                ?? throw new InvalidOperationException("Mount did not publish RA/Dec — is it tracking?");
            lock (_lock) _savedPosition = (pos.RaHours, pos.DecDegrees);

            // 2. Slew to meridian (HA=0), user-specified dec (default 0°).
            var lst = _indi.GetTelescopeLst(mount.UniqueId)
                ?? throw new InvalidOperationException("Could not read LST — mount driver missing GEOGRAPHIC_COORD?");
            SetState(Phase.Slewing, $"Slewing to meridian (RA {lst:F2}h, dec {req.DeclinationTargetDegrees:+0.0;-0.0;0}°)");
            await _indi.TelescopeSlewAsync(mount.UniqueId, lst, req.DeclinationTargetDegrees, ct);
            var slewOk = await _indi.WaitForSlewCompleteAsync(mount.UniqueId, TimeSpan.FromSeconds(req.SlewTimeoutSeconds), ct);
            if (!slewOk) throw new TimeoutException($"Slew did not complete within {req.SlewTimeoutSeconds}s");

            // Brief settle so the mount's tracking restarts cleanly before PHD2 grabs a star.
            try { await Task.Delay(2000, ct); } catch { }

            // 3. PHD2: loop → find star → calibrate+guide.
            SetState(Phase.StartingGuide, "Starting PHD2 exposures");
            await _phd2.LoopAsync(ct);
            try { await Task.Delay(4000, ct); } catch { } // let looping settle so find_star has a valid frame

            SetState(Phase.FindingStar, "Auto-selecting guide star");
            await _phd2.FindStarAsync(ct);
            try { await Task.Delay(2000, ct); } catch { }

            SetState(Phase.Calibrating, $"PHD2 calibrating — measuring mount response (~2-3 min)");
            await _phd2.StartCalibrationAsync(ct);

            // Wait for PHD2 to transition out of Calibrating. "Guiding" = success,
            // "Stopped" / "LostLock" = failure.
            var finalState = await _phd2.WaitForAppStateAsync(
                new[] { "Guiding", "Stopped", "LostLock" },
                TimeSpan.FromSeconds(req.CalibrationTimeoutSeconds), ct);
            if (finalState == null)
                throw new TimeoutException($"PHD2 calibration did not finish within {req.CalibrationTimeoutSeconds}s");
            if (!string.Equals(finalState, "Guiding", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Calibration failed — PHD2 ended in {finalState}");

            // 4. Return to target (optional).
            if (req.ReturnToTarget && _savedPosition is { } saved)
            {
                SetState(Phase.ReturningToTarget, "Stopping guide + returning to original target");
                try { await _phd2.StopGuidingAsync(ct); } catch { /* best-effort */ }
                await _indi.TelescopeSlewAsync(mount.UniqueId, saved.Ra, saved.Dec, ct);
                await _indi.WaitForSlewCompleteAsync(mount.UniqueId, TimeSpan.FromSeconds(req.SlewTimeoutSeconds), ct);
            }
            else
            {
                try { await _phd2.StopGuidingAsync(ct); } catch { }
            }

            SetState(Phase.Done, "Auto-calibrate complete. PHD2 calibration is ready for this mount + target geometry.");
        }
        catch (OperationCanceledException)
        {
            SetState(Phase.Cancelled, "Cancelled by user");
            await TryReturnToSavedPositionAsync(req);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Auto-calibrate failed");
            SetState(Phase.Failed, ex.Message);
            lock (_lock) _error = ex.Message;
            await TryReturnToSavedPositionAsync(req);
        }
    }

    private async Task TryReturnToSavedPositionAsync(Request req)
    {
        if (!req.ReturnToTarget || _savedPosition is not { } saved) return;
        try
        {
            var mount = _equipment.GetSelected(DeviceKind.Telescope);
            if (mount == null) return;
            try { await _phd2.StopGuidingAsync(CancellationToken.None); } catch { }
            await _indi.TelescopeSlewAsync(mount.UniqueId, saved.Ra, saved.Dec, CancellationToken.None);
        }
        catch (Exception ex) { _log.LogDebug(ex, "Return-to-target after failure/cancel failed"); }
    }

    private void SetState(Phase p, string msg)
    {
        lock (_lock) { _phase = p; _message = msg; }
        _log.LogInformation("AutoCalibrate: {Phase} — {Msg}", p, msg);
    }
}
