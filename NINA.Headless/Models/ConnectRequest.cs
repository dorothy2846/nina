namespace NINA.Headless.Models;

public record ConnectRequest
{
    public string? DeviceId { get; init; }

    // Guider-specific: PHD2 needs an INDI guide camera + mount bound in its prefs before
    // launch. Other controllers ignore these. Values are INDI device names (not
    // `indi:DeviceName` ids — use the UniqueId from EquipmentDescriptor).
    public string? GuideCameraDeviceId { get; init; }
    public string? MountDeviceId { get; init; }
}
