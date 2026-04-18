using Microsoft.AspNetCore.SignalR;
using NINA.Headless.Hubs;

namespace NINA.Headless.Services.Remote;

/// <summary>
/// Fan-out for server-pushed events. The LAN path writes them to the SignalR hub so
/// browsers and the iOS app on the same WiFi receive them via the existing
/// <c>/ws/nina</c> connection; the remote path mirrors the same messages to any
/// WebRTC controllers currently holding a DataChannel. Centralising this lets call
/// sites stay transport-agnostic — no per-site awareness of whether a given client
/// is local or remote.
/// </summary>
public class RemoteEventBus
{
    private readonly IHubContext<NinaHub> _hub;
    private readonly List<IRemoteEventSink> _sinks = new();
    private readonly object _lock = new();

    public RemoteEventBus(IHubContext<NinaHub> hub) { _hub = hub; }

    /// <summary>Attach a sink (typically <see cref="RendezvousClient"/>) so it receives
    /// the same event stream as SignalR subscribers.</summary>
    public void AddSink(IRemoteEventSink sink)
    {
        lock (_lock) { if (!_sinks.Contains(sink)) _sinks.Add(sink); }
    }

    public void RemoveSink(IRemoteEventSink sink)
    {
        lock (_lock) { _sinks.Remove(sink); }
    }

    /// <summary>Broadcast an event to all transports. Fire-and-forget on the SignalR
    /// side — we don't want a slow WebSocket to block per-tick state updates.</summary>
    public void Broadcast(string topic, object payload)
    {
        _ = _hub.Clients.All.SendAsync(topic, payload);
        IRemoteEventSink[] snapshot;
        lock (_lock) { snapshot = _sinks.ToArray(); }
        foreach (var sink in snapshot)
        {
            try { sink.Emit(topic, payload); } catch { /* sink errors don't block others */ }
        }
    }

    /// <summary>Group-scoped SignalR broadcast retained for feature-level subscribers
    /// (e.g., per-device StateChanged) that don't want the full firehose. DataChannel
    /// doesn't have a group abstraction yet — we just mirror to every sink.</summary>
    public void BroadcastGroup(string group, string topic, object payload)
    {
        _ = _hub.Clients.Group(group).SendAsync(topic, payload);
        IRemoteEventSink[] snapshot;
        lock (_lock) { snapshot = _sinks.ToArray(); }
        foreach (var sink in snapshot)
        {
            try { sink.Emit(topic, payload); } catch { }
        }
    }
}

public interface IRemoteEventSink
{
    void Emit(string topic, object payload);
}
