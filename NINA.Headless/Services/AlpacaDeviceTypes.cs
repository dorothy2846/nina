using ASCOM.Common;

namespace NINA.Headless.Services;

/// <summary>Bidirectional mapping between the server's <see cref="DeviceKind"/> and both
/// the Alpaca URL path segment ("camera", "telescope", …) and the ASCOM.Alpaca library's
/// <see cref="DeviceTypes"/> enum. Keeping this in one place prevents drift between
/// discovery (which classifies what it finds) and the client (which builds URLs for it).</summary>
public static class AlpacaDeviceTypes
{
    public static string UrlSegment(DeviceKind kind) => kind switch
    {
        DeviceKind.Camera      => "camera",
        DeviceKind.Telescope   => "telescope",
        DeviceKind.Focuser     => "focuser",
        DeviceKind.FilterWheel => "filterwheel",
        DeviceKind.Rotator     => "rotator",
        DeviceKind.Dome        => "dome",
        DeviceKind.Weather     => "observingconditions",
        DeviceKind.Switch      => "switch",
        DeviceKind.FlatPanel   => "covercalibrator",
        DeviceKind.SafetyMonitor => "safetymonitor",
        _ => throw new ArgumentException($"No Alpaca mapping for {kind}", nameof(kind))
    };

    public static bool TryFromAscom(DeviceTypes alpacaType, out DeviceKind kind)
    {
        switch (alpacaType)
        {
            case DeviceTypes.Camera:              kind = DeviceKind.Camera;      return true;
            case DeviceTypes.Telescope:           kind = DeviceKind.Telescope;   return true;
            case DeviceTypes.Focuser:             kind = DeviceKind.Focuser;     return true;
            case DeviceTypes.FilterWheel:         kind = DeviceKind.FilterWheel; return true;
            case DeviceTypes.Rotator:             kind = DeviceKind.Rotator;     return true;
            case DeviceTypes.Dome:                kind = DeviceKind.Dome;        return true;
            case DeviceTypes.ObservingConditions: kind = DeviceKind.Weather;     return true;
            case DeviceTypes.Switch:              kind = DeviceKind.Switch;      return true;
            case DeviceTypes.CoverCalibrator:     kind = DeviceKind.FlatPanel;   return true;
            case DeviceTypes.SafetyMonitor:       kind = DeviceKind.SafetyMonitor; return true;
            default:                              kind = DeviceKind.Camera;      return false;
        }
    }

    /// All kinds this server can discover via Alpaca, paired with the library enum.
    /// Discovery uses this to know which types to broadcast-poll.
    public static readonly IReadOnlyList<(DeviceKind Kind, DeviceTypes AscomType)> Discoverable = new[]
    {
        (DeviceKind.Camera,      DeviceTypes.Camera),
        (DeviceKind.Telescope,   DeviceTypes.Telescope),
        (DeviceKind.Focuser,     DeviceTypes.Focuser),
        (DeviceKind.FilterWheel, DeviceTypes.FilterWheel),
        (DeviceKind.Rotator,     DeviceTypes.Rotator),
        (DeviceKind.Dome,        DeviceTypes.Dome),
        (DeviceKind.Weather,     DeviceTypes.ObservingConditions),
        (DeviceKind.Switch,      DeviceTypes.Switch),
        (DeviceKind.FlatPanel,   DeviceTypes.CoverCalibrator),
        (DeviceKind.SafetyMonitor, DeviceTypes.SafetyMonitor),
    };
}
