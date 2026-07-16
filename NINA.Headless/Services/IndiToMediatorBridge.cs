using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Equipment.MyFocuser;
using NINA.Equipment.Interfaces.Mediator;
// Aliased to avoid conflicts with local Headless types (Models/DomeInfo.cs)
// that share the same simple name.
using FilterWheelInfo = NINA.Equipment.Equipment.MyFilterWheel.FilterWheelInfo;
using RotatorInfo = NINA.Equipment.Equipment.MyRotator.RotatorInfo;
using DomeInfo = NINA.Equipment.Equipment.MyDome.DomeInfo;
using FlatDeviceInfo = NINA.Equipment.Equipment.MyFlatDevice.FlatDeviceInfo;
using WeatherDataInfo = NINA.Equipment.Equipment.MyWeatherData.WeatherDataInfo;
using SafetyMonitorInfo = NINA.Equipment.Equipment.MySafetyMonitor.SafetyMonitorInfo;
using SwitchInfo = NINA.Equipment.Equipment.MySwitch.SwitchInfo;

namespace NINA.Headless.Services;

/// <summary>
/// Bridges our INDI discovery layer to NINA's mediator pattern. Periodically
/// builds <see cref="CameraInfo"/> / <see cref="TelescopeInfo"/> /
/// <see cref="FocuserInfo"/> from the connected INDI device and broadcasts
/// them through the NINA mediators — driving every <c>IDeviceConsumer</c>
/// (most importantly <see cref="NinaStateService"/>) without rewriting
/// controllers to implement <c>ICamera</c>/<c>ITelescope</c>/<c>IFocuser</c>
/// faithfully.
///
/// Why this is the right cut: the controllers' command path is already
/// stable on top of <see cref="IndiDiscoveryService"/>, and re-routing it
/// through full ICamera adapters would mean ~2000 lines of NINA view-model
/// reimplementation for almost no observable behaviour change. Status
/// broadcasting, on the other hand, is what plumbs reliability signals
/// (Connected, IsExposing, Temperature, slew state) to the rest of the
/// stack — that's the part NINA's pattern actually buys us, and it's small.
/// </summary>
public class IndiToMediatorBridge : BackgroundService
{
    private readonly IndiDiscoveryService _indi;
    private readonly EquipmentSelectionService _selection;
    private readonly ICameraMediator _cameraMediator;
    private readonly ITelescopeMediator _telescopeMediator;
    private readonly IFocuserMediator _focuserMediator;
    private readonly IFilterWheelMediator _fwMediator;
    private readonly IRotatorMediator _rotatorMediator;
    private readonly IDomeMediator _domeMediator;
    private readonly IFlatDeviceMediator _flatMediator;
    private readonly IWeatherDataMediator _weatherMediator;
    private readonly ISafetyMonitorMediator _safetyMediator;
    private readonly ISwitchMediator _switchMediator;
    private readonly ILogger<IndiToMediatorBridge> _log;

    public IndiToMediatorBridge(
        IndiDiscoveryService indi,
        EquipmentSelectionService selection,
        ICameraMediator cameraMediator,
        ITelescopeMediator telescopeMediator,
        IFocuserMediator focuserMediator,
        IFilterWheelMediator fwMediator,
        IRotatorMediator rotatorMediator,
        IDomeMediator domeMediator,
        IFlatDeviceMediator flatMediator,
        IWeatherDataMediator weatherMediator,
        ISafetyMonitorMediator safetyMediator,
        ISwitchMediator switchMediator,
        ILogger<IndiToMediatorBridge> log)
    {
        _indi = indi;
        _selection = selection;
        _cameraMediator = cameraMediator;
        _telescopeMediator = telescopeMediator;
        _focuserMediator = focuserMediator;
        _fwMediator = fwMediator;
        _rotatorMediator = rotatorMediator;
        _domeMediator = domeMediator;
        _flatMediator = flatMediator;
        _weatherMediator = weatherMediator;
        _safetyMediator = safetyMediator;
        _switchMediator = switchMediator;
        _log = log;
    }

    /// Forward INDI reconnect events to the WebSocket layer so iOS can
    /// clear stale "driver hung" banners once the underlying issue has
    /// resolved. Set by the broadcaster when this service is constructed.
    public Action<string>? OnIndiReconnected;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Broadcast cadence — slow enough to keep CPU negligible, fast
        // enough that the UI feels live (1 Hz matches the iOS status
        // poller). Disconnect/reconnect events bypass this and broadcast
        // synchronously so the user sees the state change immediately.
        _indi.ClientDisconnected += OnDisconnect;
        _indi.ClientReconnected += OnReconnected;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                BroadcastAll();
                try { await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            _indi.ClientDisconnected -= OnDisconnect;
            _indi.ClientReconnected -= OnReconnected;
        }
    }

    private void OnReconnected()
    {
        BroadcastAll();
        try { OnIndiReconnected?.Invoke("indiserver reachable"); }
        catch (Exception ex) { _log.LogDebug(ex, "Bridge: OnIndiReconnected handler threw"); }
    }

    private void OnDisconnect(string reason)
    {
        // Push an explicit "disconnected" CameraInfo so consumers stop
        // showing stale temperature / gain readouts. Without this, the
        // UI keeps the last good values and looks like it's still live.
        try { _cameraMediator.Broadcast(DisconnectedCameraInfo()); } catch { }
        try { _telescopeMediator.Broadcast(DisconnectedTelescopeInfo()); } catch { }
        try { _focuserMediator.Broadcast(DisconnectedFocuserInfo()); } catch { }
    }

    private void BroadcastAll()
    {
        try { BroadcastCamera(); } catch (Exception ex) { _log.LogDebug(ex, "Bridge: camera broadcast failed"); }
        try { BroadcastTelescope(); } catch (Exception ex) { _log.LogDebug(ex, "Bridge: telescope broadcast failed"); }
        try { BroadcastFocuser(); } catch (Exception ex) { _log.LogDebug(ex, "Bridge: focuser broadcast failed"); }
        try { BroadcastFilterWheel(); } catch (Exception ex) { _log.LogDebug(ex, "Bridge: filter wheel broadcast failed"); }
        try { BroadcastRotator(); } catch (Exception ex) { _log.LogDebug(ex, "Bridge: rotator broadcast failed"); }
        try { BroadcastDome(); } catch (Exception ex) { _log.LogDebug(ex, "Bridge: dome broadcast failed"); }
        try { BroadcastFlatDevice(); } catch (Exception ex) { _log.LogDebug(ex, "Bridge: flat device broadcast failed"); }
        try { BroadcastWeather(); } catch (Exception ex) { _log.LogDebug(ex, "Bridge: weather broadcast failed"); }
        try { BroadcastSafety(); } catch (Exception ex) { _log.LogDebug(ex, "Bridge: safety monitor broadcast failed"); }
        try { BroadcastSwitch(); } catch (Exception ex) { _log.LogDebug(ex, "Bridge: switch broadcast failed"); }
    }

    private void BroadcastFilterWheel()
    {
        var sel = _selection.GetSelected(DeviceKind.FilterWheel);
        if (sel?.Provider != EquipmentProvider.Indi) { _fwMediator.Broadcast(new FilterWheelInfo { Connected = false, Name = "Not connected" }); return; }
        _fwMediator.Broadcast(_indi.TryBuildFilterWheelInfo(sel.UniqueId) ?? new FilterWheelInfo { Connected = false, Name = "Not connected" });
    }
    private void BroadcastRotator()
    {
        var sel = _selection.GetSelected(DeviceKind.Rotator);
        if (sel?.Provider != EquipmentProvider.Indi) { _rotatorMediator.Broadcast(new RotatorInfo { Connected = false, Name = "Not connected" }); return; }
        _rotatorMediator.Broadcast(_indi.TryBuildRotatorInfo(sel.UniqueId) ?? new RotatorInfo { Connected = false, Name = "Not connected" });
    }
    private void BroadcastDome()
    {
        var sel = _selection.GetSelected(DeviceKind.Dome);
        if (sel?.Provider != EquipmentProvider.Indi) { _domeMediator.Broadcast(new DomeInfo { Connected = false, Name = "Not connected" }); return; }
        _domeMediator.Broadcast(_indi.TryBuildDomeInfo(sel.UniqueId) ?? new DomeInfo { Connected = false, Name = "Not connected" });
    }
    private void BroadcastFlatDevice()
    {
        var sel = _selection.GetSelected(DeviceKind.FlatPanel);
        if (sel?.Provider != EquipmentProvider.Indi) { _flatMediator.Broadcast(new FlatDeviceInfo { Connected = false, Name = "Not connected" }); return; }
        _flatMediator.Broadcast(_indi.TryBuildFlatDeviceInfo(sel.UniqueId) ?? new FlatDeviceInfo { Connected = false, Name = "Not connected" });
    }
    private void BroadcastWeather()
    {
        var sel = _selection.GetSelected(DeviceKind.Weather);
        if (sel?.Provider != EquipmentProvider.Indi) { _weatherMediator.Broadcast(new WeatherDataInfo { Connected = false, Name = "Not connected" }); return; }
        _weatherMediator.Broadcast(_indi.TryBuildWeatherInfo(sel.UniqueId) ?? new WeatherDataInfo { Connected = false, Name = "Not connected" });
    }
    private void BroadcastSafety()
    {
        var sel = _selection.GetSelected(DeviceKind.SafetyMonitor);
        if (sel?.Provider != EquipmentProvider.Indi) { _safetyMediator.Broadcast(new SafetyMonitorInfo { Connected = false, Name = "Not connected" }); return; }
        _safetyMediator.Broadcast(_indi.TryBuildSafetyMonitorInfo(sel.UniqueId) ?? new SafetyMonitorInfo { Connected = false, Name = "Not connected" });
    }
    private void BroadcastSwitch()
    {
        var sel = _selection.GetSelected(DeviceKind.Switch);
        if (sel?.Provider != EquipmentProvider.Indi) { _switchMediator.Broadcast(new SwitchInfo { Connected = false, Name = "Not connected" }); return; }
        _switchMediator.Broadcast(_indi.TryBuildSwitchInfo(sel.UniqueId) ?? new SwitchInfo { Connected = false, Name = "Not connected" });
    }

    private void BroadcastCamera()
    {
        var sel = _selection.GetSelected(DeviceKind.Camera);
        if (sel?.Provider != EquipmentProvider.Indi)
        {
            _cameraMediator.Broadcast(DisconnectedCameraInfo());
            return;
        }
        var info = _indi.TryBuildCameraInfo(sel.UniqueId) ?? DisconnectedCameraInfo();
        _cameraMediator.Broadcast(info);
    }

    private void BroadcastTelescope()
    {
        var sel = _selection.GetSelected(DeviceKind.Telescope);
        if (sel?.Provider != EquipmentProvider.Indi)
        {
            _telescopeMediator.Broadcast(DisconnectedTelescopeInfo());
            return;
        }
        var info = _indi.TryBuildTelescopeInfo(sel.UniqueId) ?? DisconnectedTelescopeInfo();
        _telescopeMediator.Broadcast(info);
    }

    private void BroadcastFocuser()
    {
        var sel = _selection.GetSelected(DeviceKind.Focuser);
        if (sel?.Provider != EquipmentProvider.Indi)
        {
            _focuserMediator.Broadcast(DisconnectedFocuserInfo());
            return;
        }
        var info = _indi.TryBuildFocuserInfo(sel.UniqueId) ?? DisconnectedFocuserInfo();
        _focuserMediator.Broadcast(info);
    }

    private static CameraInfo DisconnectedCameraInfo() => new() { Connected = false, Name = "Not connected" };
    private static TelescopeInfo DisconnectedTelescopeInfo() => new() { Connected = false, Name = "Not connected" };
    private static FocuserInfo DisconnectedFocuserInfo() => new() { Connected = false, Name = "Not connected" };
}
