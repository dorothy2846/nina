using NINA.Headless.Services.Remote;

namespace NINA.Headless.Services;

public class EquipmentStatusBroadcaster : BackgroundService
{
    private readonly RemoteEventBus _eventBus;
    private readonly NinaStateService _state;
    private readonly SequencerService _sequencer;

    private readonly IndiDriverWatchdog? _watchdog;
    private readonly IndiToMediatorBridge? _bridge;

    public EquipmentStatusBroadcaster(RemoteEventBus eventBus, NinaStateService state, SequencerService sequencer, IndiDriverWatchdog? watchdog = null, IndiToMediatorBridge? bridge = null)
    {
        _eventBus = eventBus;
        _state = state;
        _sequencer = sequencer;
        _watchdog = watchdog;
        _bridge = bridge;
        _state.StateChanged += OnStateChanged;
        _sequencer.StateChanged += OnSequencerStateChanged;
        if (_watchdog != null)
        {
            _watchdog.DriverHung += OnDriverHung;
            _watchdog.DriverChronicallyFailing += OnDriverChronicallyFailing;
        }
        if (_bridge != null) _bridge.OnIndiReconnected = OnIndiReconnected;
    }

    private void OnIndiReconnected(string reason)
    {
        // Lets iOS clear stale "driver hung" banners — the wedge that
        // produced them is over now. Single broadcast; iOS uses it as a
        // signal to reset transient alerts that haven't been re-fired.
        _eventBus.Broadcast("IndiReconnected", new
        {
            timestamp = DateTime.UtcNow,
            reason,
        });
    }

    private void OnDriverHung(DeviceKind kind, string device, string reason)
    {
        // One-shot push so the iOS app can surface a "driver wedged" banner
        // even before the next periodic StatusUpdate. Includes the reason
        // so the user sees what specifically failed (exposure timeout vs
        // mount slew stuck) rather than a generic "trouble" toast.
        _eventBus.Broadcast("DriverHung", new
        {
            timestamp = DateTime.UtcNow,
            kind = kind.ToString(),
            device,
            reason,
        });
    }

    private void OnDriverChronicallyFailing(DeviceKind kind, string device, int countInWindow)
    {
        _eventBus.Broadcast("DriverChronicallyFailing", new
        {
            timestamp = DateTime.UtcNow,
            kind = kind.ToString(),
            device,
            countInWindow,
            // Conservatively assume the watchdog's window is 5 min — we
            // don't expose the constant from the watchdog because nothing
            // else needs it; iOS only uses this to phrase the banner.
            windowMinutes = 5,
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var sequencerStatus = _sequencer.GetStatus();

            _eventBus.Broadcast("StatusUpdate", new
            {
                timestamp = DateTime.UtcNow,
                camera = _state.BuildCameraStatus(),
                telescope = _state.BuildTelescopeStatus(),
                guider = _state.BuildGuiderStatus()
            });

            _eventBus.Broadcast("SequenceUpdate", new
            {
                running = sequencerStatus.Running,
                progress = sequencerStatus.Progress,
                currentTarget = sequencerStatus.CurrentTarget,
                sequences = _sequencer.GetSequenceList(),
                timestamp = sequencerStatus.Timestamp
            });

            await Task.Delay(1000, stoppingToken);
        }
    }

    public override void Dispose()
    {
        _state.StateChanged -= OnStateChanged;
        _sequencer.StateChanged -= OnSequencerStateChanged;
        if (_watchdog != null)
        {
            _watchdog.DriverHung -= OnDriverHung;
            _watchdog.DriverChronicallyFailing -= OnDriverChronicallyFailing;
        }
        base.Dispose();
    }

    private void OnStateChanged(string type, object data)
    {
        _eventBus.BroadcastGroup(type, "StateChanged", new { type, data });
        _eventBus.Broadcast("EquipmentStatus", _state.BuildEquipmentStatus());
    }

    private void OnSequencerStateChanged(SequencerStateDto state)
    {
        _eventBus.Broadcast("SequenceUpdate", new
        {
            running = state.Running,
            progress = state.Progress,
            currentTarget = state.CurrentTarget,
            sequences = _sequencer.GetSequenceList(),
            timestamp = state.Timestamp
        });
    }
}
