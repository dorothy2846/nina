// IndiDiscoveryService.Status.cs — read-only snapshot builders for IndiDiscoveryService:
// anonymous JSON status envelopes for the controllers' /status endpoints, plus the
// per-device-kind TryBuild*Info builders that map INDI properties onto NINA's typed models.

using NINA.Core.Enum;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Equipment.MyFocuser;
using NINA.Equipment.Equipment.MyFilterWheel;
using NINA.Equipment.Equipment.MyRotator;
using NINA.Equipment.Equipment.MyDome;
using NINA.Equipment.Equipment.MyFlatDevice;
using NINA.Equipment.Equipment.MyWeatherData;
using NINA.Equipment.Equipment.MySafetyMonitor;
using NINA.Equipment.Equipment.MySwitch;
using NINA.Headless.Indi;

namespace NINA.Headless.Services;

public partial class IndiDiscoveryService
{
    // ----- Status snapshot helpers (used by controllers' /status endpoints) -----

    public object? BuildTelescopeStatus(string deviceName)
    {
        var dev = FindByName(deviceName);
        if (dev == null) return null;

        // Report connected==true whenever the driver state OR the user's last intent says so.
        // Without the intent fallback, AM5's delProperty burst during reconnect (CONNECTION is
        // absent from our cache for ~30s) made /status flicker between the full body and a
        // minimal "Not connected" body, which whiplashed the 3D model between two orientations
        // on every poll.
        var connected = EffectiveConnected(deviceName, dev);
        if (!connected) return new { connected = false, name = deviceName };

        double? ra = null, dec = null, alt = null, az = null;
        if (dev.Properties.TryGetValue("EQUATORIAL_EOD_COORD", out var eq))
        {
            ra = eq["RA"]?.AsDouble;
            dec = eq["DEC"]?.AsDouble;
        }

        double? siteLat = null, siteLong = null, siteElev = null;
        if (dev.Properties.TryGetValue("GEOGRAPHIC_COORD", out var geo))
        {
            siteLat = geo["LAT"]?.AsDouble;
            siteLong = geo["LONG"]?.AsDouble;
            siteElev = geo["ELEV"]?.AsDouble;
        }

        if (dev.Properties.TryGetValue("HORIZONTAL_COORD", out var hz))
        {
            alt = hz["ALT"]?.AsDouble;
            az = hz["AZ"]?.AsDouble;
        }
        else if (ra.HasValue && dec.HasValue && siteLat.HasValue && siteLong.HasValue)
        {
            // AM5 and most LX200 drivers don't publish HORIZONTAL_COORD; compute from RA/DEC/site.
            (alt, az) = EquatorialToHorizontal(ra.Value, dec.Value, siteLat.Value, siteLong.Value);
        }

        double? hourAngle = null;
        double? siderealTime = null;
        if (ra.HasValue && siteLong.HasValue)
        {
            var lstHours = ComputeLstHours(siteLong.Value);
            siderealTime = lstHours;
            var ha = lstHours - ra.Value;
            // Wrap to [-12, 12]
            while (ha > 12) ha -= 24;
            while (ha < -12) ha += 24;
            hourAngle = ha;
        }

        bool? tracking = null;
        if (dev.Properties.TryGetValue("TELESCOPE_TRACK_STATE", out var ts))
            tracking = ts["TRACK_ON"]?.ValueOn;

        bool? parked = null;
        if (_parkedTelescope.TryGetValue(deviceName, out var serverParked))
            parked = serverParked;
        else if (dev.Properties.TryGetValue("TELESCOPE_PARK", out var pk))
            parked = pk["PARK"]?.ValueOn;

        bool? slewing = dev.Properties.TryGetValue("EQUATORIAL_EOD_COORD", out var slewCheck)
            ? slewCheck.State == IndiPropertyState.Busy
            : null;

        // Manual axis motion (direction-pad hold). Distinct from `slewing`
        // (goto): the iOS 3D uses it to release its pole rest-pose snap the
        // moment the user starts nudging, so real axis motion renders.
        bool moving = false;
        if (dev.Properties.TryGetValue("TELESCOPE_MOTION_NS", out var mns))
            moving |= mns["MOTION_NORTH"]?.ValueOn == true || mns["MOTION_SOUTH"]?.ValueOn == true;
        if (dev.Properties.TryGetValue("TELESCOPE_MOTION_WE", out var mwe))
            moving |= mwe["MOTION_WEST"]?.ValueOn == true || mwe["MOTION_EAST"]?.ValueOn == true;

        string? pierSide = null;
        if (dev.Properties.TryGetValue("TELESCOPE_PIER_SIDE", out var ps))
            pierSide = (ps["PIER_EAST"]?.ValueOn == true) ? "East" : (ps["PIER_WEST"]?.ValueOn == true ? "West" : null);

        // Slew rate sources differ per driver. AM5 exposes all three:
        //   TELESCOPE_SLEW_RATE   — OneOfMany switch ("1x" … "10x"), drives MOTION_NS/WE pulses
        //   VARIABLE_SLEW_RATE    — numeric arcsec/sec for GOTO slews
        //   GUIDE_RATE            — numeric fraction of sidereal for guide pulses
        string? slewRateLabel = null;
        string[]? availableSlewRates = null;
        string[]? slewRateDisplayLabels = null;
        if (dev.Properties.TryGetValue("TELESCOPE_SLEW_RATE", out var sr))
        {
            slewRateLabel = sr.Elements.Values.FirstOrDefault(e => e.ValueOn)?.Name;
            // Sort by the leading numeric prefix (handles "0.5x", "1x", "10x",
            // "400x"). Semantic names ("SLEW_GUIDE") fall to the end in the
            // INDI canonical order: GUIDE → CENTERING → FIND → MAX.
            var orderedElements = sr.Elements.Values
                .OrderBy(e => ResolveElementRate(e) ?? double.MaxValue)
                .ThenBy(e => SemanticOrder(e.Name))
                .ThenBy(e => e.Name, StringComparer.Ordinal)
                .ToArray();
            availableSlewRates = orderedElements.Select(e => e.Name).ToArray();
            // Parallel array of friendly display labels — INDI's <defSwitch
            // label="..."> attribute. Falls back to the name when the driver
            // didn't supply a label.
            slewRateDisplayLabels = orderedElements
                .Select(e => string.IsNullOrEmpty(e.Label) ? e.Name : e.Label!)
                .ToArray();
        }
        double? variableSlewRate = null;
        double? variableSlewRateMin = null;
        double? variableSlewRateMax = null;
        if (dev.Properties.TryGetValue("VARIABLE_SLEW_RATE", out var vsr))
        {
            variableSlewRate = vsr["RATE"]?.AsDouble;
            variableSlewRateMin = vsr["RATE"]?.Min;
            variableSlewRateMax = vsr["RATE"]?.Max;
        }
        double? guideRate = null;
        if (dev.Properties.TryGetValue("GUIDE_RATE", out var gr))
            guideRate = gr["RATE"]?.AsDouble;

        var canFindHome = dev.Properties.ContainsKey("TELESCOPE_HOME");
        var canPark = dev.Properties.ContainsKey("TELESCOPE_PARK");
        var canSetTracking = dev.Properties.ContainsKey("TELESCOPE_TRACK_STATE");
        // AM5 doesn't expose a dedicated at-home flag; infer from HOME property idle state after GO.
        bool? atHome = null;

        return new
        {
            connected = true,
            name = deviceName,
            ra,
            dec,
            alt,
            az,
            tracking,
            parked,
            atHome,
            slewing,
            moving,
            pierSide,
            siteLatitude = siteLat,
            siteLongitude = siteLong,
            siteElevation = siteElev,
            alignmentMode = "GermanPolar",
            hourAngle,
            siderealTime,
            slewRateLabel,
            availableSlewRates,
            slewRateDisplayLabels,
            variableSlewRate,
            variableSlewRateMin,
            variableSlewRateMax,
            guideRate,
            canFindHome,
            canPark,
            canSetTracking
        };
    }

    public object? BuildFocuserStatus(string deviceName)
    {
        var dev = FindByName(deviceName);
        if (dev == null) return null;
        var connected = dev.IsConnected;
        if (!connected) return new { connected = false, name = deviceName };

        int? position = null;
        if (dev.Properties.TryGetValue("ABS_FOCUS_POSITION", out var p))
            position = (int)(p["FOCUS_ABSOLUTE_POSITION"]?.AsDouble ?? 0);

        double? temperature = null;
        if (dev.Properties.TryGetValue("FOCUS_TEMPERATURE", out var t))
            temperature = t["TEMPERATURE"]?.AsDouble;

        bool? isMoving = dev.Properties.TryGetValue("ABS_FOCUS_POSITION", out var p2)
            ? p2.State == IndiPropertyState.Busy
            : null;

        return new { connected = true, name = deviceName, position, temperature, isMoving };
    }

    /// <summary>Current mount RA/Dec as (hours, degrees). Null if the mount isn't published
    /// or hasn't emitted EQUATORIAL_EOD_COORD yet. Used by the auto-calibrate flow to save
    /// position before slewing to meridian.</summary>
    public (double RaHours, double DecDegrees)? GetTelescopePosition(string deviceName)
    {
        var dev = FindByName(deviceName);
        if (dev == null || !dev.Properties.TryGetValue("EQUATORIAL_EOD_COORD", out var eq)) return null;
        var ra = eq["RA"]?.AsDouble;
        var dec = eq["DEC"]?.AsDouble;
        return (ra.HasValue && dec.HasValue) ? (ra.Value, dec.Value) : null;
    }

    /// <summary>Current Local Sidereal Time in hours, derived from the mount's
    /// GEOGRAPHIC_COORD longitude. Meridian crossings are at RA == LST.</summary>
    public double? GetTelescopeLst(string deviceName)
    {
        var dev = FindByName(deviceName);
        if (dev == null || !dev.Properties.TryGetValue("GEOGRAPHIC_COORD", out var geo)) return null;
        var lon = geo["LONG"]?.AsDouble;
        return lon.HasValue ? ComputeLstHours(lon.Value) : null;
    }

    /// <summary>Status envelope for the flat panel. Matches iOS FlatPanelStatusResponse —
    /// brightness is null when the driver doesn't expose FLAT_LIGHT_INTENSITY.</summary>
    public object BuildFlatPanelStatus(EquipmentDescriptor selected)
    {
        var client = _client;
        int? brightness = null;
        bool? lightOn = null;
        if (client != null)
        {
            var dev = client.GetDevice(selected.UniqueId);
            if (dev != null && dev.Properties.TryGetValue("FLAT_LIGHT_INTENSITY", out var prop)
                && prop.Elements.TryGetValue("FLAT_LIGHT_INTENSITY_VALUE", out var elem)
                && double.TryParse(elem.Value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v))
            {
                brightness = (int)Math.Round(v);
            }
            if (dev != null && dev.Properties.TryGetValue("FLAT_LIGHT_CONTROL", out var sw))
                lightOn = sw["FLAT_LIGHT_ON"]?.ValueOn ?? false;
        }
        return new { connected = true, name = selected.Name, brightness, lightOn };
    }

    /// <summary>Status envelope for the Switch endpoints. Returned by both SwitchController
    /// and the aggregate /equipment/status so the shape stays consistent.</summary>
    public object BuildSwitchStatus(EquipmentDescriptor selected)
    {
        var channels = GetSwitchChannels(selected.UniqueId)
            .Select(c => new { key = c.Property, label = c.Label, on = c.On })
            .ToArray();
        return new { connected = true, name = selected.Name, channels };
    }

    public object BuildWeatherStatus(EquipmentDescriptor selected)
    {
        var r = GetWeatherReadings(selected.UniqueId);
        return new
        {
            connected = true,
            name = selected.Name,
            temperatureC = r.TemperatureC,
            humidityPct = r.HumidityPct,
            dewPointC = r.DewPointC,
            pressureHpa = r.PressureHpa,
            windSpeedMps = r.WindSpeedMps,
            cloudCoverPct = r.CloudCoverPct,
            skyBrightnessMagArcSec2 = r.SkyBrightnessMagArcSec2,
            skyTemperatureC = r.SkyTemperatureC
        };
    }

    public object? BuildFilterWheelStatus(string deviceName)
    {
        var dev = FindByName(deviceName);
        if (dev == null) return null;
        var connected = dev.IsConnected;
        if (!connected) return new { connected = false, name = deviceName };

        int? currentPosition = null;
        bool? isMoving = null;
        if (dev.Properties.TryGetValue("FILTER_SLOT", out var s))
        {
            currentPosition = (int)(s["FILTER_SLOT_VALUE"]?.AsDouble ?? 0);
            isMoving = s.State == IndiPropertyState.Busy;
        }

        string? currentFilterName = null;
        if (currentPosition.HasValue && dev.Properties.TryGetValue("FILTER_NAME", out var n))
        {
            currentFilterName = n[$"FILTER_SLOT_NAME_{currentPosition}"]?.Value;
        }

        return new { connected = true, name = deviceName, currentPosition, currentFilterName, isMoving };
    }

    /// <summary>Map current INDI CCD property values onto NINA's CameraInfo so the rest of the stack works unchanged.</summary>
    public CameraInfo? TryBuildCameraInfo(string deviceName)
    {
        var dev = _client?.GetDevice(deviceName);
        if (dev == null) return null;

        var info = new CameraInfo
        {
            Connected = dev.IsConnected,
            Name = deviceName,
            CameraState = dev.IsConnected ? CameraStates.Idle : CameraStates.NoState
        };

        if (!dev.IsConnected) return info;

        // Temperature + cooler
        if (dev.Properties.TryGetValue("CCD_TEMPERATURE", out var tp))
            info.Temperature = tp["CCD_TEMPERATURE_VALUE"]?.AsDouble ?? 0;

        if (dev.Properties.TryGetValue("CCD_COOLER", out var cp))
            info.CoolerOn = cp["COOLER_ON"]?.ValueOn ?? false;

        if (dev.Properties.TryGetValue("CCD_COOLER_POWER", out var cpp))
        {
            info.CoolerPower = cpp["CCD_COOLER_VALUE"]?.AsDouble ?? 0;
            // Drivers like PlayerOne don't expose a CCD_COOLER on/off switch;
            // infer cooling state from non-zero cooler power.
            if (!dev.Properties.ContainsKey("CCD_COOLER") && info.CoolerPower > 0)
                info.CoolerOn = true;
        }

        info.CanSetTemperature = dev.Properties.ContainsKey("CCD_TEMPERATURE");

        // Binning
        if (dev.Properties.TryGetValue("CCD_BINNING", out var bp))
        {
            info.BinX = (short)(bp["HOR_BIN"]?.AsDouble ?? 1);
            info.BinY = (short)(bp["VER_BIN"]?.AsDouble ?? 1);
        }

        // Gain/offset — driver-dependent property names; these cover common cases
        if (dev.Properties.TryGetValue("CCD_GAIN", out var gp))
            info.Gain = (int)(gp["GAIN"]?.AsDouble ?? 0);
        else if (dev.Properties.TryGetValue("CCD_CONTROLS", out var cc))
            info.Gain = (int)(cc["Gain"]?.AsDouble ?? 0);

        if (dev.Properties.TryGetValue("CCD_OFFSET", out var op))
            info.Offset = (int)(op["OFFSET"]?.AsDouble ?? 0);
        else if (dev.Properties.TryGetValue("CCD_CONTROLS", out var cc2))
            info.Offset = (int)(cc2["Offset"]?.AsDouble ?? 0);

        // Sensor
        if (dev.Properties.TryGetValue("CCD_INFO", out var ip))
        {
            info.XSize = (int)(ip["CCD_MAX_X"]?.AsDouble ?? 0);
            info.YSize = (int)(ip["CCD_MAX_Y"]?.AsDouble ?? 0);
            info.PixelSize = ip["CCD_PIXEL_SIZE"]?.AsDouble ?? ip["CCD_PIXEL_SIZE_X"]?.AsDouble ?? 0;
            info.BitDepth = (int)(ip["CCD_BITSPERPIXEL"]?.AsDouble ?? 16);
        }

        // Bayer pattern — INDI standard: CCD_CFA with text element CFA_TYPE
        // ("RGGB" / "BGGR" / "GRBG" / "GBRG"). Absence of the property means
        // mono sensor; default of CameraInfo.SensorType is Monochrome so we
        // only need to upgrade it for colour cameras.
        if (dev.Properties.TryGetValue("CCD_CFA", out var cfa))
        {
            var pattern = (cfa["CFA_TYPE"]?.Value ?? "").Trim().ToUpperInvariant();
            info.SensorType = pattern switch
            {
                "RGGB" => SensorType.RGGB,
                "BGGR" => SensorType.BGGR,
                "GRBG" => SensorType.GRBG,
                "GBRG" => SensorType.GBRG,
                _      => SensorType.Monochrome,
            };
        }

        // Exposure progress
        if (dev.Properties.TryGetValue("CCD_EXPOSURE", out var ep))
        {
            var remaining = ep["CCD_EXPOSURE_VALUE"]?.AsDouble ?? 0;
            info.IsExposing = ep.State == IndiPropertyState.Busy;
            if (info.IsExposing)
            {
                info.ExposureEndTime = DateTime.UtcNow.AddSeconds(remaining);
                info.CameraState = CameraStates.Exposing;
            }
        }

        return info;
    }

    /// <summary>Build NINA's typed <see cref="TelescopeInfo"/> from current
    /// INDI property values. Mirrors the anonymous-typed
    /// <see cref="BuildTelescopeStatus"/> but returns the proper
    /// strongly-typed model for mediator broadcast.</summary>
    public TelescopeInfo? TryBuildTelescopeInfo(string deviceName)
    {
        var dev = _client?.GetDevice(deviceName);
        if (dev == null) return null;
        var connected = EffectiveConnected(deviceName, dev);
        var info = new TelescopeInfo
        {
            Connected = connected,
            Name = deviceName,
        };
        if (!connected) return info;

        if (dev.Properties.TryGetValue("EQUATORIAL_EOD_COORD", out var eq))
        {
            info.RightAscension = eq["RA"]?.AsDouble ?? 0;
            info.Declination = eq["DEC"]?.AsDouble ?? 0;
            info.Slewing = eq.State == IndiPropertyState.Busy;
        }
        if (dev.Properties.TryGetValue("GEOGRAPHIC_COORD", out var geo))
        {
            info.SiteLatitude = geo["LAT"]?.AsDouble ?? 0;
            info.SiteLongitude = geo["LONG"]?.AsDouble ?? 0;
            info.SiteElevation = geo["ELEV"]?.AsDouble ?? 0;
        }
        if (dev.Properties.TryGetValue("HORIZONTAL_COORD", out var hz))
        {
            info.Altitude = hz["ALT"]?.AsDouble ?? 0;
            info.Azimuth = hz["AZ"]?.AsDouble ?? 0;
        }
        if (dev.Properties.TryGetValue("TELESCOPE_TRACK_STATE", out var ts))
            info.TrackingEnabled = ts["TRACK_ON"]?.ValueOn ?? false;
        if (_parkedTelescope.TryGetValue(deviceName, out var parked))
            info.AtPark = parked;
        else if (dev.Properties.TryGetValue("TELESCOPE_PARK", out var pk))
            info.AtPark = pk["PARK"]?.ValueOn ?? false;
        if (dev.Properties.TryGetValue("TELESCOPE_PIER_SIDE", out var ps))
        {
            info.SideOfPier = ps["PIER_EAST"]?.ValueOn == true ? PierSide.pierEast
                            : ps["PIER_WEST"]?.ValueOn == true ? PierSide.pierWest
                            : PierSide.pierUnknown;
        }
        return info;
    }

    public FilterWheelInfo? TryBuildFilterWheelInfo(string deviceName)
    {
        var dev = _client?.GetDevice(deviceName);
        if (dev == null) return null;
        var connected = EffectiveConnected(deviceName, dev);
        var info = new FilterWheelInfo { Connected = connected, Name = deviceName };
        if (!connected) return info;
        if (dev.Properties.TryGetValue("FILTER_SLOT", out var slot))
        {
            info.IsMoving = slot.State == IndiPropertyState.Busy;
        }
        return info;
    }

    public RotatorInfo? TryBuildRotatorInfo(string deviceName)
    {
        var dev = _client?.GetDevice(deviceName);
        if (dev == null) return null;
        var connected = EffectiveConnected(deviceName, dev);
        var info = new RotatorInfo { Connected = connected, Name = deviceName };
        if (!connected) return info;
        if (dev.Properties.TryGetValue("ABS_ROTATOR_ANGLE", out var ang))
        {
            info.Position = (float)(ang["ANGLE"]?.AsDouble ?? 0);
            info.MechanicalPosition = info.Position;
            info.IsMoving = ang.State == IndiPropertyState.Busy;
        }
        if (dev.Properties.TryGetValue("ROTATOR_REVERSE", out var rev))
        {
            info.CanReverse = true;
            info.Reverse = rev["INDI_ENABLED"]?.ValueOn ?? false;
        }
        return info;
    }

    public DomeInfo? TryBuildDomeInfo(string deviceName)
    {
        var dev = _client?.GetDevice(deviceName);
        if (dev == null) return null;
        var connected = EffectiveConnected(deviceName, dev);
        var info = new DomeInfo { Connected = connected, Name = deviceName };
        if (!connected) return info;
        if (dev.Properties.TryGetValue("ABS_DOME_POSITION", out var pos))
        {
            info.Azimuth = pos["DOME_ABSOLUTE_POSITION"]?.AsDouble ?? double.NaN;
            info.Slewing = pos.State == IndiPropertyState.Busy;
        }
        if (dev.Properties.TryGetValue("DOME_SHUTTER", out var sh))
        {
            var opening = sh["SHUTTER_OPEN"]?.ValueOn ?? false;
            info.ShutterStatus = sh.State == IndiPropertyState.Busy
                ? (opening ? NINA.Equipment.Interfaces.ShutterState.ShutterOpening : NINA.Equipment.Interfaces.ShutterState.ShutterClosing)
                : (opening ? NINA.Equipment.Interfaces.ShutterState.ShutterOpen : NINA.Equipment.Interfaces.ShutterState.ShutterClosed);
        }
        if (dev.Properties.TryGetValue("DOME_PARK", out var pk))
        {
            info.AtPark = (pk["PARK"]?.ValueOn ?? false) && pk.State != IndiPropertyState.Busy;
            if (pk.State == IndiPropertyState.Busy) info.Slewing = true;
        }
        return info;
    }

    public FlatDeviceInfo? TryBuildFlatDeviceInfo(string deviceName)
    {
        var dev = _client?.GetDevice(deviceName);
        if (dev == null) return null;
        var connected = EffectiveConnected(deviceName, dev);
        var info = new FlatDeviceInfo { Connected = connected, Name = deviceName };
        if (!connected) return info;
        if (dev.Properties.TryGetValue("FLAT_LIGHT_INTENSITY", out var br))
        {
            info.Brightness = (int)(br["FLAT_LIGHT_INTENSITY_VALUE"]?.AsDouble ?? 0);
        }
        if (dev.Properties.TryGetValue("FLAT_LIGHT_CONTROL", out var sw))
        {
            info.LightOn = sw["FLAT_LIGHT_ON"]?.ValueOn ?? false;
        }
        return info;
    }

    public WeatherDataInfo? TryBuildWeatherInfo(string deviceName)
    {
        var dev = _client?.GetDevice(deviceName);
        if (dev == null) return null;
        var connected = EffectiveConnected(deviceName, dev);
        var info = new WeatherDataInfo { Connected = connected, Name = deviceName };
        if (!connected) return info;
        if (dev.Properties.TryGetValue("WEATHER_PARAMETERS", out var w))
        {
            info.Temperature = w["WEATHER_TEMPERATURE"]?.AsDouble ?? double.NaN;
            info.Humidity = w["WEATHER_HUMIDITY"]?.AsDouble ?? double.NaN;
            info.Pressure = w["WEATHER_PRESSURE"]?.AsDouble ?? double.NaN;
            info.DewPoint = w["WEATHER_DEWPOINT"]?.AsDouble ?? double.NaN;
            info.WindSpeed = w["WEATHER_WIND_SPEED"]?.AsDouble ?? double.NaN;
            info.CloudCover = w["WEATHER_CLOUD_COVER"]?.AsDouble ?? double.NaN;
            info.SkyBrightness = w["WEATHER_SKY_BRIGHTNESS"]?.AsDouble ?? double.NaN;
            info.SkyTemperature = w["WEATHER_SKY_TEMPERATURE"]?.AsDouble ?? double.NaN;
        }
        return info;
    }

    public SafetyMonitorInfo? TryBuildSafetyMonitorInfo(string deviceName)
    {
        var dev = _client?.GetDevice(deviceName);
        if (dev == null) return null;
        var connected = EffectiveConnected(deviceName, dev);
        var info = new SafetyMonitorInfo { Connected = connected, Name = deviceName };
        if (!connected) return info;
        if (dev.Properties.TryGetValue("SAFETY", out var s))
        {
            info.IsSafe = s["SAFETY_STATE"]?.ValueOn ?? false;
        }
        return info;
    }

    public SwitchInfo? TryBuildSwitchInfo(string deviceName)
    {
        var dev = _client?.GetDevice(deviceName);
        if (dev == null) return null;
        var connected = EffectiveConnected(deviceName, dev);
        // Switch's WritableSwitches/ReadonlySwitches require concrete ISwitch
        // implementations — we leave them empty for now since broadcasting
        // just connection state already gates the consumer's "device present"
        // checks. Per-switch state is exposed via the existing /switch/* endpoints.
        return new SwitchInfo { Connected = connected, Name = deviceName };
    }

    public FocuserInfo? TryBuildFocuserInfo(string deviceName)
    {
        var dev = _client?.GetDevice(deviceName);
        if (dev == null) return null;
        var connected = EffectiveConnected(deviceName, dev);
        var info = new FocuserInfo
        {
            Connected = connected,
            Name = deviceName,
        };
        if (!connected) return info;

        if (dev.Properties.TryGetValue("ABS_FOCUS_POSITION", out var pos))
        {
            info.Position = (int)(pos["FOCUS_ABSOLUTE_POSITION"]?.AsDouble ?? 0);
            info.IsMoving = pos.State == IndiPropertyState.Busy;
        }
        if (dev.Properties.TryGetValue("FOCUS_TEMPERATURE", out var t))
        {
            info.Temperature = t["TEMPERATURE"]?.AsDouble ?? double.NaN;
        }
        else if (dev.Properties.TryGetValue("FOCUSER_TEMPERATURE", out var t2))
        {
            info.Temperature = t2["TEMPERATURE"]?.AsDouble ?? double.NaN;
        }
        if (dev.Properties.TryGetValue("FOCUS_STEP_SIZE", out var ss))
        {
            info.StepSize = ss["STEP_SIZE"]?.AsDouble ?? 0;
        }
        return info;
    }
}
