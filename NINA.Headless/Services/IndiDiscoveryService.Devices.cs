// IndiDiscoveryService.Devices.cs — auxiliary device plumbing for IndiDiscoveryService:
// property-capability probes plus focuser, filter wheel, flat panel, switch (powerbox)
// and weather command/read helpers.

using NINA.Headless.Indi;

namespace NINA.Headless.Services;

public partial class IndiDiscoveryService
{
    /// <summary>Lightweight "does this INDI device advertise property X" check. Used by
    /// controllers to populate capability flags in /info responses so iOS can disable
    /// buttons the driver won't honour (e.g. a mount without TELESCOPE_TRACK_MODE).</summary>
    public bool DeviceHasProperty(string deviceName, string property)
    {
        var client = _client;
        if (client == null) return false;
        var dev = client.GetDevice(deviceName);
        return dev != null && dev.Properties.ContainsKey(property);
    }

    /// <summary>True if the device exposes any of the given property names. Convenience
    /// for capabilities that drivers publish under several vector names (camera dew
    /// heater = CCD_DEW_CONTROL / AUX_HEATER_TOGGLE / ANTI_DEW, focuser backlash =
    /// FOCUS_BACKLASH_STEPS / FOCUS_BACKLASH_TOGGLE).</summary>
    public bool DeviceHasAnyProperty(string deviceName, params string[] properties)
    {
        var client = _client;
        if (client == null) return false;
        var dev = client.GetDevice(deviceName);
        if (dev == null) return false;
        foreach (var p in properties) if (dev.Properties.ContainsKey(p)) return true;
        return false;
    }

    /// <summary>Current focuser absolute position, or null if the driver hasn't emitted
    /// ABS_FOCUS_POSITION yet. Used by filter change to compute offset-adjusted targets.</summary>
    public int? GetFocuserPosition(string deviceName)
    {
        var dev = FindByName(deviceName);
        if (dev == null || !dev.Properties.TryGetValue("ABS_FOCUS_POSITION", out var p)) return null;
        var v = p["FOCUS_ABSOLUTE_POSITION"]?.AsDouble;
        return v.HasValue ? (int)v.Value : null;
    }

    // ----- Filter wheel control -----

    /// <summary>List the filters advertised by a filter wheel, ordered by slot.</summary>
    public IReadOnlyList<(int slot, string name)> GetFilterWheelFilters(string deviceName)
    {
        var dev = _client?.GetDevice(deviceName);
        if (dev == null) return Array.Empty<(int, string)>();
        var result = new List<(int, string)>();
        if (dev.Properties.TryGetValue("FILTER_NAME", out var nameProp))
        {
            foreach (var el in nameProp.Elements.Values.OrderBy(e => e.Name))
            {
                // INDI names slots FILTER_SLOT_NAME_1, _2, ...
                var digits = new string(el.Name.Where(char.IsDigit).ToArray());
                if (int.TryParse(digits, out var slot))
                    result.Add((slot, el.Value));
            }
        }
        return result;
    }

    public async Task SetFilterSlotAsync(string deviceName, int slot, CancellationToken ct)
    {
        var client = _client; if (client == null) return;
        await client.SetNumberAsync(deviceName, "FILTER_SLOT", "FILTER_SLOT_VALUE", slot, ct);
    }

    public async Task WaitForFilterIdleAsync(string deviceName, TimeSpan timeout, CancellationToken ct)
    {
        var client = _client; if (client == null) return;
        await client.AwaitDeviceStateAsync(deviceName, d =>
            d.Properties.TryGetValue("FILTER_SLOT", out var p) && p.State != IndiPropertyState.Busy,
            timeout, ct);
    }

    // ----- Flat panel control -----

    public async Task<bool> SetFlatPanelOnAsync(string deviceName, bool on, CancellationToken ct)
    {
        var client = _client; if (client == null) return false;
        var dev = client.GetDevice(deviceName);
        if (dev == null) return false;

        // INDI standard LightBox: FLAT_LIGHT_CONTROL switch with FLAT_LIGHT_ON/FLAT_LIGHT_OFF.
        if (dev.Properties.ContainsKey("FLAT_LIGHT_CONTROL"))
        {
            await client.SetSwitchManyAsync(deviceName, "FLAT_LIGHT_CONTROL",
                new[] { ("FLAT_LIGHT_ON", on), ("FLAT_LIGHT_OFF", !on) }, ct);
            return true;
        }
        return false;
    }

    public async Task<bool> SetFlatPanelBrightnessAsync(string deviceName, int brightness, CancellationToken ct)
    {
        var client = _client; if (client == null) return false;
        var dev = client.GetDevice(deviceName);
        if (dev == null) return false;
        if (dev.Properties.ContainsKey("FLAT_LIGHT_INTENSITY"))
        {
            await client.SetNumberAsync(deviceName, "FLAT_LIGHT_INTENSITY", "FLAT_LIGHT_INTENSITY_VALUE", brightness, ct);
            return true;
        }
        return false;
    }

    /// <summary>Enable / disable the focuser's on-board temperature compensation.
    /// INDI spec vector is <c>FOCUS_TEMPERATURE_COMPENSATION</c> with an INDI_ENABLED /
    /// INDI_DISABLED pair — older drivers sometimes use <c>AUTO_FOCUS_COMP</c>. Returns
    /// false when the driver exposes neither so the controller can report "not supported".</summary>
    public async Task<bool> SetFocuserTempCompensationAsync(string deviceName, bool on, CancellationToken ct)
    {
        var client = _client;
        if (client == null) return false;
        var dev = client.GetDevice(deviceName);
        if (dev == null) return false;

        if (dev.Properties.ContainsKey("FOCUS_TEMPERATURE_COMPENSATION"))
        {
            await client.SetSwitchManyAsync(deviceName, "FOCUS_TEMPERATURE_COMPENSATION",
                new[] { ("INDI_ENABLED", on), ("INDI_DISABLED", !on) }, ct);
            return true;
        }
        if (dev.Properties.ContainsKey("AUTO_FOCUS_COMP"))
        {
            await client.SetSwitchManyAsync(deviceName, "AUTO_FOCUS_COMP",
                new[] { ("INDI_ENABLED", on), ("INDI_DISABLED", !on) }, ct);
            return true;
        }
        return false;
    }

    /// <summary>Configure driver-level backlash compensation. <paramref name="steps"/>
    /// is the overshoot amount (0 disables). INDI standard is two vectors:
    /// <c>FOCUS_BACKLASH_TOGGLE</c> (on/off switch) + <c>FOCUS_BACKLASH_STEPS</c> (number).
    /// When the driver supports it we prefer this over software backlash in NINA
    /// because the driver knows the last direction and can act on moves it initiated
    /// internally (autofocus, filter offsets, etc.).</summary>
    public async Task<bool> SetFocuserBacklashAsync(string deviceName, int steps, CancellationToken ct)
    {
        var client = _client;
        if (client == null) return false;
        var dev = client.GetDevice(deviceName);
        if (dev == null) return false;

        var hasToggle = dev.Properties.ContainsKey("FOCUS_BACKLASH_TOGGLE");
        var hasSteps = dev.Properties.ContainsKey("FOCUS_BACKLASH_STEPS");
        if (!hasToggle && !hasSteps) return false;

        if (hasToggle)
        {
            await client.SetSwitchManyAsync(deviceName, "FOCUS_BACKLASH_TOGGLE",
                new[] { ("INDI_ENABLED", steps > 0), ("INDI_DISABLED", steps <= 0) }, ct);
        }
        if (hasSteps && steps > 0)
        {
            await client.SetNumberAsync(deviceName, "FOCUS_BACKLASH_STEPS",
                "FOCUS_BACKLASH_VALUE", steps, ct);
        }
        return true;
    }

    // ----- Switch (powerbox / relay board) -----
    //
    // INDI doesn't standardize one switch property — each driver exposes its own ports as
    // independent switch vectors (typically OneOfMany ON/OFF pairs, or a single On/Off button).
    // We enumerate every switch vector on the device and surface each element pair as a
    // named channel. Read-only inspection; toggles flip whichever element is currently Off.

    public record SwitchChannel(string Property, string Label, bool On);

    public IReadOnlyList<SwitchChannel> GetSwitchChannels(string deviceName)
    {
        var client = _client; if (client == null) return Array.Empty<SwitchChannel>();
        var dev = client.GetDevice(deviceName);
        if (dev == null) return Array.Empty<SwitchChannel>();

        var result = new List<SwitchChannel>();
        foreach (var prop in dev.Properties.Values)
        {
            if (prop.Type != IndiPropertyType.Switch) continue;
            if (prop.Name == "CONNECTION" || prop.Name.StartsWith("DEBUG") ||
                prop.Name == "CONFIG_PROCESS" || prop.Name == "SIMULATION") continue;

            // OneOfMany ON/OFF pair → single channel. Otherwise expose each element as a channel.
            if (prop.Elements.Count == 2 &&
                prop.Elements.Values.Any(e => e.Name.Contains("ON", StringComparison.OrdinalIgnoreCase)) &&
                prop.Elements.Values.Any(e => e.Name.Contains("OFF", StringComparison.OrdinalIgnoreCase)))
            {
                var onEl = prop.Elements.Values.FirstOrDefault(e => e.Name.Contains("ON", StringComparison.OrdinalIgnoreCase));
                result.Add(new SwitchChannel(prop.Name, prop.Label ?? prop.Name, onEl?.ValueOn ?? false));
            }
            else
            {
                foreach (var el in prop.Elements.Values)
                {
                    result.Add(new SwitchChannel($"{prop.Name}:{el.Name}", el.Label ?? el.Name, el.ValueOn));
                }
            }
        }
        return result;
    }

    public async Task<bool> ToggleSwitchAsync(string deviceName, string channelKey, bool on, CancellationToken ct)
    {
        var client = _client; if (client == null) return false;
        var dev = client.GetDevice(deviceName);
        if (dev == null) return false;

        // channelKey is either "PROP" (OneOfMany pair) or "PROP:ELEMENT" (single-element flip).
        var split = channelKey.Split(':', 2);
        var propName = split[0];
        if (!dev.Properties.TryGetValue(propName, out var prop)) return false;

        if (split.Length == 1)
        {
            var onEl = prop.Elements.Values.FirstOrDefault(e => e.Name.Contains("ON", StringComparison.OrdinalIgnoreCase));
            var offEl = prop.Elements.Values.FirstOrDefault(e => e.Name.Contains("OFF", StringComparison.OrdinalIgnoreCase));
            if (onEl == null || offEl == null) return false;
            await client.SetSwitchManyAsync(deviceName, propName,
                new[] { (onEl.Name, on), (offEl.Name, !on) }, ct);
            return true;
        }
        else
        {
            await client.SetSwitchManyAsync(deviceName, propName, new[] { (split[1], on) }, ct);
            return true;
        }
    }

    // ----- Weather (observing conditions) -----
    //
    // INDI WEATHER_INTERFACE drivers publish an OBSERVING_CONDITIONS number vector with
    // some subset of the standard WEATHER_* elements. We surface whatever is available —
    // missing readings stay null on the wire so the UI can hide those rows.

    public record WeatherReadings(
        double? TemperatureC,
        double? HumidityPct,
        double? DewPointC,
        double? PressureHpa,
        double? WindSpeedMps,
        double? CloudCoverPct,
        double? SkyBrightnessMagArcSec2,
        double? SkyTemperatureC);

    public WeatherReadings GetWeatherReadings(string deviceName)
    {
        var client = _client; if (client == null) return new WeatherReadings(null, null, null, null, null, null, null, null);
        var dev = client.GetDevice(deviceName);
        if (dev == null) return new WeatherReadings(null, null, null, null, null, null, null, null);

        double? Read(string propName, string elName)
        {
            if (!dev.Properties.TryGetValue(propName, out var p)) return null;
            var el = p[elName];
            return el == null ? null : el.AsDouble;
        }

        // Most drivers use WEATHER_PARAMETERS with WEATHER_* elements. A few older drivers
        // split each reading into its own vector (OBSERVING_CONDITIONS_TEMPERATURE, etc.).
        return new WeatherReadings(
            TemperatureC:         Read("WEATHER_PARAMETERS", "WEATHER_TEMPERATURE"),
            HumidityPct:          Read("WEATHER_PARAMETERS", "WEATHER_HUMIDITY"),
            DewPointC:            Read("WEATHER_PARAMETERS", "WEATHER_DEWPOINT"),
            PressureHpa:          Read("WEATHER_PARAMETERS", "WEATHER_PRESSURE"),
            WindSpeedMps:         Read("WEATHER_PARAMETERS", "WEATHER_WIND_SPEED"),
            CloudCoverPct:        Read("WEATHER_PARAMETERS", "WEATHER_CLOUD_COVER"),
            SkyBrightnessMagArcSec2: Read("WEATHER_PARAMETERS", "WEATHER_SKY_BRIGHTNESS"),
            SkyTemperatureC:      Read("WEATHER_PARAMETERS", "WEATHER_SKY_TEMPERATURE"));
    }

    // ----- Focuser control -----

    public async Task FocuserMoveAsync(string deviceName, int position, CancellationToken ct)
    {
        var client = _client; if (client == null) return;
        await client.SetNumberAsync(deviceName, "ABS_FOCUS_POSITION", "FOCUS_ABSOLUTE_POSITION", position, ct);
    }

    public async Task FocuserHaltAsync(string deviceName, CancellationToken ct)
    {
        var client = _client; if (client == null) return;
        await client.SetSwitchAsync(deviceName, "FOCUS_ABORT_MOTION", "ABORT", true, ct);
    }

    // ----- FilterWheel control -----

    public async Task FilterWheelChangeAsync(string deviceName, int position, CancellationToken ct)
    {
        var client = _client; if (client == null) return;
        await client.SetNumberAsync(deviceName, "FILTER_SLOT", "FILTER_SLOT_VALUE", position, ct);
    }
}
