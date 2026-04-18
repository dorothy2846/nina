using NINA.Headless.Services.Remote;

namespace NINA.Headless.Services;

public class EquipmentStatusBroadcaster : BackgroundService
{
    private readonly RemoteEventBus _eventBus;
    private readonly NinaStateService _state;
    private readonly SequencerService _sequencer;

    public EquipmentStatusBroadcaster(RemoteEventBus eventBus, NinaStateService state, SequencerService sequencer)
    {
        _eventBus = eventBus;
        _state = state;
        _sequencer = sequencer;
        _state.StateChanged += OnStateChanged;
        _sequencer.StateChanged += OnSequencerStateChanged;
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
