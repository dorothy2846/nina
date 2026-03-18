using Microsoft.AspNetCore.SignalR;
using NINA.Headless.Hubs;

namespace NINA.Headless.Services;

public class EquipmentStatusBroadcaster : BackgroundService
{
    private readonly IHubContext<NinaHub> _hub;
    private readonly NinaStateService _state;
    private readonly SequencerService _sequencer;

    public EquipmentStatusBroadcaster(IHubContext<NinaHub> hub, NinaStateService state, SequencerService sequencer)
    {
        _hub = hub;
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

            await _hub.Clients.All.SendAsync("StatusUpdate", new
            {
                timestamp = DateTime.UtcNow,
                camera = _state.BuildCameraStatus(),
                telescope = _state.BuildTelescopeStatus(),
                guider = _state.BuildGuiderStatus()
            }, stoppingToken);

            await _hub.Clients.All.SendAsync("SequenceUpdate", new
            {
                running = sequencerStatus.Running,
                progress = sequencerStatus.Progress,
                currentTarget = sequencerStatus.CurrentTarget,
                sequences = _sequencer.GetSequenceList(),
                timestamp = sequencerStatus.Timestamp
            }, stoppingToken);

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
        _ = _hub.Clients.Group(type).SendAsync("StateChanged", new { type, data });
        _ = _hub.Clients.All.SendAsync("EquipmentStatus", _state.BuildEquipmentStatus());
    }

    private void OnSequencerStateChanged(SequencerStateDto state)
    {
        _ = _hub.Clients.All.SendAsync("SequenceUpdate", new
        {
            running = state.Running,
            progress = state.Progress,
            currentTarget = state.CurrentTarget,
            sequences = _sequencer.GetSequenceList(),
            timestamp = state.Timestamp
        });
    }
}
