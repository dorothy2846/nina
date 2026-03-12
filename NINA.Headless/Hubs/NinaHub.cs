using Microsoft.AspNetCore.SignalR;
using NINA.Headless.Services;

namespace NINA.Headless.Hubs;

public class NinaHub : Hub
{
    private readonly NinaStateService _state;

    public NinaHub(NinaStateService state)
    {
        _state = state;
    }

    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.SendAsync("EquipmentStatus", _state.BuildEquipmentStatus());
        await Clients.Caller.SendAsync("GuideHistory", _state.GuideHistory);
        await base.OnConnectedAsync();
    }

    public async Task Subscribe(string eventType)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, eventType);
    }
}
