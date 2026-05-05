using System.Collections.Concurrent;

namespace NINA.Headless.Indi;

/// <summary>
/// Minimal INDI property model.
/// An INDI device publishes properties ("vectors") of typed elements.
/// We track just enough to identify devices and read/write what we need
/// for the camera flow.
/// </summary>

public enum IndiPropertyType { Switch, Text, Number, Light, Blob }

public enum IndiPropertyState { Idle, Ok, Busy, Alert }

public enum IndiPropertyPerm { ReadOnly, WriteOnly, ReadWrite }

public enum IndiSwitchRule { OneOfMany, AtMostOne, AnyOfMany }

/// <summary>Single element inside a property vector.</summary>
public sealed class IndiElement
{
    public required string Name { get; init; }
    public string? Label { get; set; }
    public string Value { get; set; } = string.Empty;

    // For number elements
    public double? Min { get; set; }
    public double? Max { get; set; }
    public double? Step { get; set; }
    public string? Format { get; set; }

    public bool ValueOn => string.Equals(Value, "On", StringComparison.OrdinalIgnoreCase);
    public double AsDouble => double.TryParse(Value, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
}

/// <summary>A property vector (group of elements) published by an INDI device.</summary>
public sealed class IndiProperty
{
    public required string Device { get; init; }
    public required string Name { get; init; }
    public string? Label { get; set; }
    public string? Group { get; set; }
    public IndiPropertyType Type { get; init; }
    public IndiPropertyState State { get; set; } = IndiPropertyState.Idle;
    public IndiPropertyPerm Perm { get; set; } = IndiPropertyPerm.ReadOnly;
    public IndiSwitchRule Rule { get; set; } = IndiSwitchRule.AnyOfMany;
    /// Wall-clock UTC of the most recent def/set vector that touched this
    /// property. Used by the sensor-staleness watchdog to flag weather /
    /// safety monitors that have stopped publishing fresh readings.
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

    public DateTime? Timestamp => LastUpdated;

    public ConcurrentDictionary<string, IndiElement> Elements { get; } = new();

    public IndiElement? this[string elementName]
        => Elements.TryGetValue(elementName, out var e) ? e : null;
}

/// <summary>State of one INDI device (device = one driver's identity).</summary>
public sealed class IndiDevice
{
    public required string Name { get; init; }

    /// <summary>DRIVER_INTERFACE bitmask from INDI — tells us what kind of device this is.</summary>
    public int DriverInterface { get; set; }

    public ConcurrentDictionary<string, IndiProperty> Properties { get; } = new();

    public bool IsCamera => (DriverInterface & 0x2) != 0;       // CCD_INTERFACE
    public bool IsTelescope => (DriverInterface & 0x1) != 0;     // TELESCOPE_INTERFACE
    public bool IsFocuser => (DriverInterface & 0x8) != 0;       // FOCUSER_INTERFACE
    public bool IsFilterWheel => (DriverInterface & 0x10) != 0;  // FILTER_INTERFACE
    public bool IsSwitch => (DriverInterface & 0x8000) != 0;     // AUX_INTERFACE — powerbox/relay boards (Pegasus UPB, etc.)
    public bool IsWeather => (DriverInterface & 0x80) != 0;      // WEATHER_INTERFACE — observing conditions

    public bool IsConnected
    {
        get
        {
            if (!Properties.TryGetValue("CONNECTION", out var p)) return false;
            return p["CONNECT"]?.ValueOn ?? false;
        }
    }
}
