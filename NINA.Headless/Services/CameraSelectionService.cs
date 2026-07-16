namespace NINA.Headless.Services;

/// <summary>
/// Backward-compatible facade over EquipmentSelectionService that exposes
/// camera-only state. Keeps existing controllers compiling while we migrate.
/// </summary>
public class CameraSelectionService
{
    private readonly EquipmentSelectionService _equipment;

    public CameraSelectionService(EquipmentSelectionService equipment)
    {
        _equipment = equipment;
    }

    public string? SelectedId => _equipment.GetSelectedId(DeviceKind.Camera);
    public bool IsConnected => _equipment.IsConnected(DeviceKind.Camera);

    public IReadOnlyList<CameraDescriptor> GetAvailable()
    {
        return _equipment.GetAvailable(DeviceKind.Camera)
            .Select(ToCamera).ToList();
    }

    public CameraDescriptor? GetSelected()
    {
        var d = _equipment.GetSelected(DeviceKind.Camera);
        return d == null ? null : ToCamera(d);
    }

    public bool Connect(string deviceId) => _equipment.Connect(DeviceKind.Camera, deviceId);

    public void Disconnect() => _equipment.Disconnect(DeviceKind.Camera);

    public void UpdateIndiDevices(IEnumerable<CameraDescriptor> cameras)
    {
        var descriptors = cameras
            .Where(c => c.Provider == CameraProvider.Indi)
            .Select(c => new EquipmentDescriptor(c.Id, c.Name, DeviceKind.Camera, EquipmentProvider.Indi, c.UniqueId, c.Host, c.Port));
        _equipment.UpdateProviderDevices(DeviceKind.Camera, EquipmentProvider.Indi, descriptors);
    }

    public void UpdateAlpacaDevices(IEnumerable<CameraDescriptor> cameras)
    {
        var descriptors = cameras
            .Where(c => c.Provider == CameraProvider.Alpaca)
            .Select(c => new EquipmentDescriptor(c.Id, c.Name, DeviceKind.Camera, EquipmentProvider.Alpaca, c.UniqueId, c.Host, c.Port));
        _equipment.UpdateProviderDevices(DeviceKind.Camera, EquipmentProvider.Alpaca, descriptors);
    }

    private static CameraDescriptor ToCamera(EquipmentDescriptor d)
        => new(
            Id: d.Id,
            Name: d.Name,
            Provider: d.Provider switch
            {
                EquipmentProvider.Indi => CameraProvider.Indi,
                EquipmentProvider.Alpaca => CameraProvider.Alpaca,
                _ => CameraProvider.Simulator
            },
            Host: d.Host,
            Port: d.Port,
            DeviceNumber: null,
            UniqueId: d.UniqueId);
}

public enum CameraProvider { Simulator, Alpaca, Indi }

public record CameraDescriptor(
    string Id,
    string Name,
    CameraProvider Provider,
    string? Host,
    int? Port,
    int? DeviceNumber,
    string UniqueId);
