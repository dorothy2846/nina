using Microsoft.AspNetCore.SignalR;
using NINA.Headless.Hubs;

namespace NINA.Headless.Services;

public class EquipmentStatusBroadcaster : BackgroundService
{
    private readonly IHubContext<NinaHub> _hub;
    private readonly NinaStateService _state;

    public EquipmentStatusBroadcaster(IHubContext<NinaHub> hub, NinaStateService state)
    {
        _hub = hub;
        _state = state;
        _state.StateChanged += OnStateChanged;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await _hub.Clients.All.SendAsync("StatusUpdate", new
            {
                timestamp = DateTime.UtcNow,
                camera = _state.BuildCameraStatus(),
                telescope = _state.BuildTelescopeStatus(),
                guider = _state.BuildGuiderStatus()
            }, stoppingToken);

            await Task.Delay(1000, stoppingToken);
        }
    }

    public override void Dispose()
    {
        _state.StateChanged -= OnStateChanged;
        base.Dispose();
    }

    private void OnStateChanged(string type, object data)
    {
        _ = _hub.Clients.Group(type).SendAsync("StateChanged", new { type, data });
        _ = _hub.Clients.All.SendAsync("EquipmentStatus", _state.BuildEquipmentStatus());
    }
}
