// IndiDiscoveryService.Lifecycle.cs — discovery/connection lifecycle for IndiDiscoveryService:
// the ExecuteAsync reconnect loop with its disconnect/reconnect events, device-list change
// handling, and the per-device CONNECTION connect/disconnect commands.

using NINA.Headless.Indi;

namespace NINA.Headless.Services;

public partial class IndiDiscoveryService
{
    /// <summary>Fires after the INDI socket dies (server crash, cable yank,
    /// indiserver restart). Subscribers (camera stream, capture controller)
    /// should treat in-flight operations as failed and tear down resources;
    /// <see cref="ClientReconnected"/> restores the steady state.</summary>
    public event Action<string>? ClientDisconnected;

    /// <summary>Fires after a successful reconnect — fresh socket, fresh
    /// getProperties subscription. Subscribers should re-resolve any
    /// per-device state they cached (Bayer pattern, gain ranges, etc.) and
    /// re-apply user-controlled settings (streaming exposure / binning) so
    /// the user doesn't need to know the link blipped.</summary>
    public event Action? ClientReconnected;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let IndiServerManager bring up indiserver first.
        try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); } catch { return; }

        // Exponential backoff so a flapping driver doesn't hammer indiserver
        // (capped at 30 s — long enough to ride out a typical service
        // restart, short enough that a real recovery is felt as snappy).
        int attempt = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            // Signal completed cleanly so we can wait on a single fence
            // instead of polling IsConnected. Disconnected event drains
            // through this TCS — instant detection, no polling lag.
            var dropped = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                _client = new IndiClient(_server.Host, _server.Port, _log);
                _client.DevicesChanged += OnDevicesChanged;
                _client.Disconnected += reason => dropped.TrySetResult(reason);
                await _client.ConnectAsync(stoppingToken);

                bool wasReconnect = attempt > 0;
                attempt = 0;
                if (wasReconnect)
                {
                    _log.LogInformation("INDI: reconnected");
                    // Re-spawn intent workers for any device the user had
                    // explicitly connected before the drop. Intent state is
                    // preserved across reconnects, so any device with
                    // ConnectionIntent.Connected gets its CONNECTION switch
                    // re-driven once the driver re-defs the property — the
                    // user's session survives a server restart.
                    RestoreIntentWorkers();
                    try { ClientReconnected?.Invoke(); }
                    catch (Exception ex) { _log.LogWarning(ex, "INDI: ClientReconnected handler threw"); }
                }

                // Block until the read loop signals disconnect (fault) OR
                // shutdown is requested. No polling.
                using var stopReg = stoppingToken.Register(() => dropped.TrySetResult("cancelled"));
                var reason = await dropped.Task;
                if (stoppingToken.IsCancellationRequested) break;
                _log.LogWarning("INDI: connection dropped — {Reason}", reason);
                try { ClientDisconnected?.Invoke(reason); }
                catch (Exception ex) { _log.LogWarning(ex, "INDI: ClientDisconnected handler threw"); }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "INDI: connect attempt {Attempt} failed", attempt + 1);
            }
            finally
            {
                if (_client != null)
                {
                    _client.DevicesChanged -= OnDevicesChanged;
                    try { await _client.DisposeAsync(); } catch { }
                    _client = null;
                }
            }

            // 1s, 2s, 4s, 8s, 16s, 30s cap.
            attempt++;
            var backoff = TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(attempt - 1, 5)));
            _log.LogInformation("INDI: retry in {Backoff}s (attempt {Next})", (int)backoff.TotalSeconds, attempt + 1);
            try { await Task.Delay(backoff, stoppingToken); } catch { break; }
        }
    }

    private void OnDevicesChanged()
    {
        if (_client == null) return;
        var devs = _client.Devices;

        UpdateKind(DeviceKind.Camera, devs.Where(d => d.IsCamera));
        UpdateKind(DeviceKind.Telescope, devs.Where(d => d.IsTelescope));
        UpdateKind(DeviceKind.Focuser, devs.Where(d => d.IsFocuser));
        UpdateKind(DeviceKind.FilterWheel, devs.Where(d => d.IsFilterWheel));
        UpdateKind(DeviceKind.Switch, devs.Where(d => d.IsSwitch));
        UpdateKind(DeviceKind.Weather, devs.Where(d => d.IsWeather));
        // Rotator/Dome/FlatPanel can be added when we expose IsRotator etc. flags

        DetectConnectionEdges(devs);
    }

    /// <summary>Fires when an INDI device transitions Disconnected → Connected (rising
    /// edge). The kind is best-guess from the device's IsCamera/IsTelescope flags.
    /// Subscribers run on the INDI client thread — keep handlers cheap or off-load.</summary>
    public event Action<DeviceKind, string>? DeviceConnected;

    private void DetectConnectionEdges(IReadOnlyList<IndiDevice> devs)
    {
        // Snapshot current set so we can drop entries for devices that disappeared
        // — that way an unplug+replug fires DeviceConnected again.
        var seen = new HashSet<string>();
        foreach (var dev in devs)
        {
            seen.Add(dev.Name);
            if (!dev.IsConnected) { _announcedConnected.Remove(dev.Name); continue; }
            if (!_announcedConnected.Add(dev.Name)) continue; // already announced

            DeviceKind? kind =
                dev.IsCamera ? DeviceKind.Camera :
                dev.IsTelescope ? DeviceKind.Telescope :
                dev.IsFocuser ? DeviceKind.Focuser :
                dev.IsFilterWheel ? DeviceKind.FilterWheel :
                dev.IsSwitch ? DeviceKind.Switch :
                dev.IsWeather ? DeviceKind.Weather : (DeviceKind?)null;
            if (kind is DeviceKind k)
            {
                try { DeviceConnected?.Invoke(k, dev.Name); }
                catch (Exception ex) { _log.LogWarning(ex, "DeviceConnected handler threw for {Device}", dev.Name); }
            }
        }
        _announcedConnected.RemoveWhere(name => !seen.Contains(name));
    }

    private void UpdateKind(DeviceKind kind, IEnumerable<IndiDevice> matching)
    {
        var descriptors = matching.Select(d => new EquipmentDescriptor(
            Id: $"indi:{d.Name}",
            Name: $"{d.Name} (INDI)",
            Kind: kind,
            Provider: EquipmentProvider.Indi,
            UniqueId: d.Name,
            Host: _server.Host,
            Port: _server.Port));
        _equipment.UpdateProviderDevices(kind, EquipmentProvider.Indi, descriptors);
    }

    private SemaphoreSlim GetLock(string deviceName)
    {
        lock (_deviceLocks)
        {
            if (!_deviceLocks.TryGetValue(deviceName, out var sem))
            {
                sem = new SemaphoreSlim(1, 1);
                _deviceLocks[deviceName] = sem;
            }
            return sem;
        }
    }

    /// <summary>Generic INDI device CONNECT — works for any kind (camera/telescope/focuser/wheel).</summary>
    // Connect/disconnect timeouts. Kept strictly under the HTTP client's default 30s budget
    // so callers don't see a client-side abort (HTTP 499). Primary 22s + 6s grace = 28s max.
    // AM5 reconnect (serial handshake on top of prior-session cleanup) empirically lands in
    // the 25–27s range; shorter budgets produced false timeouts even though the driver
    // eventually completed fine.
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(22);
    private static readonly TimeSpan DisconnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan BusySettleTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Result of a connect attempt. On failure, <see cref="Reason"/> carries a
    /// human-readable summary ("Driver not ready", "Cannot connect to /dev/ttyUSB0", etc.)
    /// built from any INDI <c>&lt;message&gt;</c> events the driver emitted during the attempt.
    /// Prefer <see cref="DriverMessages"/> for the raw list if the UI wants to show each line.</summary>
    public record ConnectResult(bool Ok, string? Reason, IReadOnlyList<string> DriverMessages);

    /// <summary>Drive CONNECTION.CONNECT=On and wait event-driven for the driver to publish
    /// IsConnected. Returns a ConnectResult carrying the reason on failure (from INDI message
    /// events emitted during the connect window, if any).</summary>
    public async Task<ConnectResult> ConnectDeviceAsync(string deviceName, CancellationToken ct)
    {
        var sem = GetLock(deviceName);
        await sem.WaitAsync(ct);
        // Timestamp before we touch INDI so GetMessagesSince captures only driver output
        // that's a direct response to this attempt, not stale noise from earlier.
        var startedAt = DateTime.UtcNow;
        try
        {
            var client = _client;
            if (client == null || !client.IsConnected)
                return new ConnectResult(false, "INDI client not connected to indiserver", Array.Empty<string>());

            var dev = client.GetDevice(deviceName);
            if (dev?.IsConnected == true) return new ConnectResult(true, null, Array.Empty<string>());
            if (dev == null)
                return new ConnectResult(false, $"Device '{deviceName}' not published by any driver — is it powered on and plugged in?", Array.Empty<string>());

            // If a previous disconnect is still propagating (CONNECTION state = Busy), the
            // driver often drops the new CONNECT we'd send. Give it a short window to settle.
            if (dev.Properties.TryGetValue("CONNECTION", out var connProp) &&
                connProp.State == IndiPropertyState.Busy)
            {
                await client.AwaitDeviceStateAsync(deviceName,
                    d => d.Properties.TryGetValue("CONNECTION", out var p) && p.State != IndiPropertyState.Busy,
                    BusySettleTimeout, ct);
            }

            await client.SetSwitchManyAsync(deviceName, "CONNECTION",
                new[] { ("CONNECT", true), ("DISCONNECT", false) }, ct);

            var ok = await client.AwaitDeviceStateAsync(deviceName,
                d => d.IsConnected,
                ConnectTimeout, ct);
            if (ok) return new ConnectResult(true, null, Array.Empty<string>());

            // Grace window: the AM5 driver often publishes CONNECT=On just past the primary
            // deadline because the serial handshake is right on the edge of our budget. Wait
            // up to 6s more (still under HTTP 30s client budget) before giving up.
            ok = await client.AwaitDeviceStateAsync(deviceName,
                d => d.IsConnected,
                TimeSpan.FromSeconds(6), ct);
            if (ok) return new ConnectResult(true, null, Array.Empty<string>());

            // Failure path: pull any driver messages emitted since the attempt began. Drivers
            // often explain the real cause here ("Cannot open /dev/ttyUSB0", "Camera model
            // not supported", "Filter wheel not responding").
            var messages = client.GetMessagesSince(deviceName, startedAt);
            var reason = messages.Count > 0
                ? messages.Last()  // most recent = usually the actual error
                : "Driver did not become ready within 28 s (no message from driver)";
            return new ConnectResult(false, reason, messages);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "INDI connect {Device} failed", deviceName);
            return new ConnectResult(false, ex.Message, Array.Empty<string>());
        }
        finally { sem.Release(); }
    }

    /// <summary>Drive CONNECTION.DISCONNECT=On and wait for the driver to fully settle
    /// (CONNECT=Off AND CONNECTION state=Ok/Idle). Returning before the property leaves Busy
    /// caused a reliable race where the very next Connect got dropped by the driver because
    /// it was still finishing the prior session's cleanup. Disconnect is best-effort; timeout
    /// still returns normally.</summary>
    public async Task DisconnectDeviceAsync(string deviceName, CancellationToken ct)
    {
        var sem = GetLock(deviceName);
        await sem.WaitAsync(ct);
        try
        {
            var client = _client;
            if (client == null || !client.IsConnected) return;

            var dev = client.GetDevice(deviceName);
            if (dev == null || !dev.IsConnected) return;

            await client.SetSwitchManyAsync(deviceName, "CONNECTION",
                new[] { ("CONNECT", false), ("DISCONNECT", true) }, ct);

            await client.AwaitDeviceStateAsync(deviceName,
                d => !d.IsConnected
                     && d.Properties.TryGetValue("CONNECTION", out var p)
                     && p.State != IndiPropertyState.Busy,
                DisconnectTimeout, ct);
        }
        catch (Exception ex) { _log.LogWarning(ex, "INDI disconnect {Device} failed", deviceName); }
        finally { sem.Release(); }
    }

    // Legacy aliases — older callers used the camera-specific names.
    public Task<ConnectResult> ConnectCameraAsync(string n, CancellationToken ct) => ConnectDeviceAsync(n, ct);
    public Task DisconnectCameraAsync(string n, CancellationToken ct) => DisconnectDeviceAsync(n, ct);
}
