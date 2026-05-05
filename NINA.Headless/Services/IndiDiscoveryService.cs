using System.Collections.Concurrent;
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

/// <summary>
/// Maintains a persistent connection to the local indiserver,
/// tracks INDI devices, and feeds camera entries into CameraSelectionService.
/// </summary>
public class IndiDiscoveryService : BackgroundService
{
    private readonly IndiServerManager _server;
    private readonly EquipmentSelectionService _equipment;
    private readonly ILogger<IndiDiscoveryService> _log;

    private IndiClient? _client;

    // Server-side park state per device. AM5's TELESCOPE_PARK.PARK switch doubles as "slew-to-home"
    // which we intentionally avoid, so the INDI property doesn't reflect the user's park intent.
    // When parked here, motion endpoints (move/slew/home) are rejected until unpark.
    private readonly ConcurrentDictionary<string, bool> _parkedTelescope = new();

    public bool IsTelescopeParked(string deviceName) =>
        _parkedTelescope.TryGetValue(deviceName, out var p) && p;

    /// True when the device exists in the INDI device list and reports
    /// its CONNECTION switch as on. Used by the mediator bridge so a
    /// disconnected device flips the consumer-visible Connected flag.
    public bool IsTelescopeReady(string deviceName) => IsDeviceConnected(deviceName);
    public bool IsFocuserReady(string deviceName) => IsDeviceConnected(deviceName);
    private bool IsDeviceConnected(string deviceName)
    {
        var dev = _client?.GetDevice(deviceName);
        if (dev == null || !dev.IsConnected) return false;
        return IntentConnected(deviceName);
    }

    private bool IntentConnected(string deviceName)
    {
        lock (_intentLock)
        {
            return _intent.TryGetValue(deviceName, out var intent) && intent == ConnectionIntent.Connected;
        }
    }

    public IndiDiscoveryService(
        IndiServerManager server,
        EquipmentSelectionService equipment,
        ILogger<IndiDiscoveryService> log)
    {
        _server = server;
        _equipment = equipment;
        _log = log;

        // Reports "is this device connected?". Primary signal is the INDI CONNECTION.CONNECT
        // switch. Fallback: if the driver just bounced the whole device (AM5 emits a delProperty
        // on disconnect and takes 30+s to re-def CONNECTION), trust the user's last intent so
        // the app doesn't flicker to "disconnected" during an in-flight reconnect.
        _equipment.ConnectivityProvider = (_, uniqueId) =>
        {
            var dev = _client?.GetDevice(uniqueId);
            if (dev != null && dev.Properties.ContainsKey("CONNECTION"))
                return dev.IsConnected;

            lock (_intentLock)
            {
                return _intent.TryGetValue(uniqueId, out var intent) && intent == ConnectionIntent.Connected;
            }
        };
    }

    // ----- Connection intent + coalescing worker -----
    // One long-running background task per device converges the driver toward the latest
    // requested state. Rapid user toggles (Connect → Disconnect → Connect) previously queued
    // as three serialized INDI round-trips — the worker reads the current intent at every step
    // so intermediate flips are dropped. The HTTP handlers just set an intent and return 202.

    private enum ConnectionIntent { Connected, Disconnected }

    private readonly Dictionary<string, ConnectionIntent> _intent = new();
    private readonly Dictionary<string, Task> _intentWorker = new();
    private readonly object _intentLock = new();

    /// <summary>Force the INDI discovery loop to drop its current client and rebuild it.
    /// Use when the device list feels stale (e.g. plugged a camera in after the client
    /// handshake completed, or the driver republished properties without a clean delProperty
    /// cycle). Returns immediately; the ExecuteAsync loop naturally reconnects within ~3s.</summary>
    /// <summary>Given an INDI device name, returns the driver's executable name (e.g. <c>indi_asi_ccd</c>)
    /// from the device's DRIVER_INFO.DRIVER_EXEC property. Null if the device isn't known or the
    /// driver hasn't yet published DRIVER_INFO. This is what IndiServerManager.RestartDriverAsync
    /// needs to target a single driver without touching the rest of the rig.</summary>
    public string? GetDriverExec(string deviceName)
    {
        var client = _client;
        var dev = client?.GetDevice(deviceName);
        if (dev == null) return null;
        if (!dev.Properties.TryGetValue("DRIVER_INFO", out var prop)) return null;
        return prop["DRIVER_EXEC"]?.Value;
    }

    public async Task RequestRescanAsync(CancellationToken ct)
    {
        var client = _client;
        if (client == null) return;
        // Dispose flips IsConnected via _tcp.Dispose, which ExecuteAsync's inner while-loop
        // picks up on its next 1s tick. The loop's finally block then re-nulls _client and
        // re-enters the outer loop, which creates a fresh IndiClient and re-handshakes —
        // rebuilding the device list from scratch along the way.
        try { await client.DisposeAsync(); }
        catch (Exception ex) { _log.LogWarning(ex, "Rescan: client dispose failed"); }
        _log.LogInformation("INDI rescan requested — client disposed, loop will reconnect");
    }

    /// <summary>Ask the worker to drive <paramref name="deviceName"/> toward the given state.
    /// Returns immediately; the background task converges even if intents change mid-flight.</summary>
    public void RequestConnectionIntent(string deviceName, bool connected)
    {
        lock (_intentLock)
        {
            _intent[deviceName] = connected ? ConnectionIntent.Connected : ConnectionIntent.Disconnected;
            if (_intentWorker.TryGetValue(deviceName, out var existing) && !existing.IsCompleted) return;
            _intentWorker[deviceName] = Task.Run(() => IntentWorkerLoop(deviceName));
        }
    }

    /// On client reconnect the IntentWorkerLoops we had running all exited
    /// (their `client.IsConnected` check tripped). Re-spawn one per device
    /// that still has `Connected` intent so the driver gets re-armed as
    /// soon as it re-defs CONNECTION.
    private void RestoreIntentWorkers()
    {
        lock (_intentLock)
        {
            foreach (var (deviceName, intent) in _intent)
            {
                if (intent != ConnectionIntent.Connected) continue;
                if (_intentWorker.TryGetValue(deviceName, out var existing) && !existing.IsCompleted) continue;
                _intentWorker[deviceName] = Task.Run(() => IntentWorkerLoop(deviceName));
            }
        }
    }

    private async Task IntentWorkerLoop(string deviceName)
    {
        while (true)
        {
            ConnectionIntent target;
            lock (_intentLock)
            {
                if (!_intent.TryGetValue(deviceName, out target))
                {
                    _intentWorker.Remove(deviceName);
                    return;
                }
            }

            var client = _client;
            if (client == null || !client.IsConnected)
            {
                lock (_intentLock) { _intentWorker.Remove(deviceName); }
                return;
            }

            var dev = client.GetDevice(deviceName);
            var isConn = dev?.IsConnected ?? false;
            var wantConn = target == ConnectionIntent.Connected;

            if (isConn == wantConn)
            {
                // Already in target state. Keep the intent entry — ConnectivityProvider reads
                // it as a fallback when the driver temporarily drops CONNECTION from our cache
                // (AM5 emits a whole-device delProperty on disconnect and takes ~30s to re-def
                // CONNECTION). Just release the worker slot.
                lock (_intentLock)
                {
                    if (_intent.TryGetValue(deviceName, out var current) && current == target)
                    {
                        _intentWorker.Remove(deviceName);
                        return;
                    }
                }
                continue; // Intent changed after our snapshot — re-evaluate.
            }

            try
            {
                if (wantConn) await ConnectDeviceAsync(deviceName, CancellationToken.None);
                else          await DisconnectDeviceAsync(deviceName, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "IntentWorker step failed for {Device}", deviceName);
                await Task.Delay(500);
            }
            // Loop again — re-read intent in case user toggled while we were working.
        }
    }

    public IndiClient? Client => _client;

    public IReadOnlyList<IndiDevice> Devices => _client?.Devices ?? new List<IndiDevice>();

    /// <summary>List INDI devices of the given equipment kind, mapped to the JSON shape the app expects.</summary>
    public IEnumerable<object> ListDevices(IndiDeviceKind kind)
    {
        var devices = _client?.Devices ?? new List<IndiDevice>();
        return devices.Where(d => kind switch
        {
            IndiDeviceKind.Camera => d.IsCamera,
            IndiDeviceKind.Telescope => d.IsTelescope,
            IndiDeviceKind.Focuser => d.IsFocuser,
            IndiDeviceKind.FilterWheel => d.IsFilterWheel,
            _ => false
        }).Select(d => new { id = $"indi:{d.Name}", name = $"{d.Name} (INDI)" });
    }

    /// <summary>Look up an INDI device by its raw INDI name (UniqueId in EquipmentDescriptor).</summary>
    public IndiDevice? FindByName(string name) => _client?.GetDevice(name);

    public bool IsClientReady => _client?.IsConnected == true;

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
        var hasConnectionProp = dev.Properties.ContainsKey("CONNECTION");
        var connected = hasConnectionProp ? dev.IsConnected : IntentConnected(deviceName);
        if (!connected && hasConnectionProp) return new { connected = false, name = deviceName };

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

    /// <summary>Resolve an INDI rate element's "intended" numeric value.
    /// Prefers the element's LABEL (which encodes the human meaning, e.g.
    /// AM5 element name "10x" has label "1440x" — the actual ×sidereal
    /// multiplier) over the element NAME. Falls back to name when no label
    /// is published or the label has no numeric prefix.</summary>
    private static double? ResolveElementRate(IndiElement el)
    {
        if (!string.IsNullOrEmpty(el.Label))
        {
            var fromLabel = LeadingDecimal(el.Label!);
            if (fromLabel.HasValue) return fromLabel;
        }
        return LeadingDecimal(el.Name);
    }

    /// <summary>Parse a leading decimal number from a TELESCOPE_SLEW_RATE
    /// element name. Handles plain integers ("1x", "10x"), decimals
    /// ("0.5x", "1.5x"), and rejects pure semantic names ("SLEW_GUIDE",
    /// "MAX"). Returns null when no leading number is present.</summary>
    private static double? LeadingDecimal(string s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        int i = 0;
        bool seenDot = false;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsDigit(c)) { i++; continue; }
            if (c == '.' && !seenDot) { seenDot = true; i++; continue; }
            break;
        }
        if (i == 0) return null;
        var head = s.Substring(0, i);
        if (head == ".") return null;
        return double.TryParse(head, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v : (double?)null;
    }

    /// <summary>Sort semantic INDI rate names in their canonical order:
    /// GUIDE (slowest) → CENTERING → FIND → SLEW/MAX (fastest). Anything
    /// not on the canonical list falls between FIND and MAX in stable
    /// alphabetical order.</summary>
    private static int SemanticOrder(string name)
    {
        var u = name.ToUpperInvariant();
        if (u.Contains("GUIDE")) return 0;
        if (u.Contains("CENTERING") || u.Contains("CENTER")) return 1;
        if (u.Contains("FIND")) return 2;
        if (u.Contains("MAX") || u == "SLEW" || u.Contains("SLEW_MAX")) return 4;
        return 3;
    }

    private static double ComputeLstHours(double lonDeg)
    {
        var now = DateTime.UtcNow;
        var jd = now.ToOADate() + 2415018.5;
        var gmstDeg = 280.46061837 + 360.98564736629 * (jd - 2451545.0);
        gmstDeg %= 360; if (gmstDeg < 0) gmstDeg += 360;
        var lstDeg = (gmstDeg + lonDeg) % 360; if (lstDeg < 0) lstDeg += 360;
        return lstDeg / 15.0;
    }

    private static (double alt, double az) EquatorialToHorizontal(double raHours, double decDeg, double latDeg, double lonDeg)
    {
        var now = DateTime.UtcNow;
        var jd = now.ToOADate() + 2415018.5;
        var gmst = 280.46061837 + 360.98564736629 * (jd - 2451545.0);
        gmst %= 360; if (gmst < 0) gmst += 360;
        var lst = (gmst + lonDeg) % 360; if (lst < 0) lst += 360;
        var haDeg = lst - raHours * 15.0;

        var latR = latDeg * Math.PI / 180.0;
        var decR = decDeg * Math.PI / 180.0;
        var haR = haDeg * Math.PI / 180.0;

        var sinAlt = Math.Sin(decR) * Math.Sin(latR) + Math.Cos(decR) * Math.Cos(latR) * Math.Cos(haR);
        sinAlt = Math.Clamp(sinAlt, -1, 1);
        var alt = Math.Asin(sinAlt) * 180.0 / Math.PI;

        var cosAlt = Math.Cos(Math.Asin(sinAlt));
        var sinAz = -Math.Cos(decR) * Math.Sin(haR) / cosAlt;
        var cosAz = (Math.Sin(decR) - sinAlt * Math.Sin(latR)) / (cosAlt * Math.Cos(latR));
        var az = Math.Atan2(sinAz, cosAz) * 180.0 / Math.PI;
        if (az < 0) az += 360;
        return (alt, az);
    }

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

    // ----- Telescope control -----

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

    /// <summary>Poll EQUATORIAL_EOD_COORD.state until it's no longer Busy (slew complete)
    /// or the timeout elapses. INDI drivers flip this property to Busy during slew and
    /// back to Ok on arrival — the canonical way to detect slew completion.</summary>
    public async Task<bool> WaitForSlewCompleteAsync(string deviceName, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var dev = FindByName(deviceName);
            if (dev != null && dev.Properties.TryGetValue("EQUATORIAL_EOD_COORD", out var eq))
            {
                if (eq.State != IndiPropertyState.Busy) return true;
            }
            try { await Task.Delay(500, ct); } catch { return false; }
        }
        return false;
    }

    public async Task<bool> TelescopeSlewAsync(string deviceName, double raHours, double decDegrees, CancellationToken ct)
    {
        var client = _client; if (client == null) return false;
        // Set ON_COORD_SET to SLEW first so writing EQUATORIAL_EOD_COORD triggers a slew (not track or sync).
        await client.SetSwitchManyAsync(deviceName, "ON_COORD_SET",
            new[] { ("SLEW", true), ("TRACK", false), ("SYNC", false) }, ct);
        var xml = $"<newNumberVector device=\"{EscapeXml(deviceName)}\" name=\"EQUATORIAL_EOD_COORD\">" +
                  $"<oneNumber name=\"RA\">{raHours.ToString(System.Globalization.CultureInfo.InvariantCulture)}</oneNumber>" +
                  $"<oneNumber name=\"DEC\">{decDegrees.ToString(System.Globalization.CultureInfo.InvariantCulture)}</oneNumber>" +
                  $"</newNumberVector>";
        await client.SendAsync(xml, ct);
        return true;
    }

    /// <summary>Nudge the mount from its current position by the given delta (direction ∈ N/S/E/W, degrees).
    /// Uses a real slew — INDI TELESCOPE_MOTION_NS/WE on most drivers is a guide-rate pulse which isn't
    /// visible for casual use.</summary>
    public async Task<bool> TelescopeNudgeAsync(string deviceName, string direction, double degrees, CancellationToken ct)
    {
        var client = _client; if (client == null) return false;
        var dev = client.GetDevice(deviceName);
        if (dev == null || !dev.Properties.TryGetValue("EQUATORIAL_EOD_COORD", out var eq)) return false;

        var ra = eq["RA"]?.AsDouble ?? 0;      // hours
        var dec = eq["DEC"]?.AsDouble ?? 0;    // degrees

        var dir = direction.ToUpperInvariant();
        // Express the nudge in the mount's coordinate frame. Note: near the celestial pole
        // RA nudges produce tiny sky motion, but that's consistent with what an RA axis move does.
        double newRa = ra, newDec = dec;
        switch (dir)
        {
            case "N": newDec = Math.Clamp(dec + degrees, -90, 90); break;
            case "S": newDec = Math.Clamp(dec - degrees, -90, 90); break;
            case "E": newRa = WrapHours(ra + degrees / 15.0); break;
            case "W": newRa = WrapHours(ra - degrees / 15.0); break;
            default: return false;
        }
        return await TelescopeSlewAsync(deviceName, newRa, newDec, ct);
    }

    private static double WrapHours(double h)
    {
        h %= 24.0;
        if (h < 0) h += 24.0;
        return h;
    }

    public Task TelescopeTrackingAsync(string deviceName, bool enabled, CancellationToken ct)
    {
        var client = _client;
        if (client == null) return Task.CompletedTask;
        return client.SetSwitchManyAsync(deviceName, "TELESCOPE_TRACK_STATE",
            new[] { ("TRACK_ON", enabled), ("TRACK_OFF", !enabled) }, ct);
    }

    /// <summary>Select tracking rate — one of sidereal/solar/lunar/custom. INDI's
    /// TELESCOPE_TRACK_MODE is a OneOfMany switch with well-known elements
    /// <c>TRACK_SIDEREAL</c> / <c>TRACK_SOLAR</c> / <c>TRACK_LUNAR</c> / <c>TRACK_CUSTOM</c>.
    /// Returns false when the driver omits the vector (some push-to mounts don't expose it).</summary>
    public async Task<bool> TelescopeTrackRateAsync(string deviceName, string rate, CancellationToken ct)
    {
        var client = _client;
        if (client == null) return false;
        var dev = client.GetDevice(deviceName);
        if (dev == null || !dev.Properties.ContainsKey("TELESCOPE_TRACK_MODE")) return false;

        var element = rate.ToLowerInvariant() switch
        {
            "sidereal" => "TRACK_SIDEREAL",
            "solar" => "TRACK_SOLAR",
            "lunar" => "TRACK_LUNAR",
            "custom" => "TRACK_CUSTOM",
            _ => null
        };
        if (element == null) return false;

        await client.SetSwitchManyAsync(deviceName, "TELESCOPE_TRACK_MODE",
            new[] { ("TRACK_SIDEREAL", element == "TRACK_SIDEREAL"),
                    ("TRACK_SOLAR",    element == "TRACK_SOLAR"),
                    ("TRACK_LUNAR",    element == "TRACK_LUNAR"),
                    ("TRACK_CUSTOM",   element == "TRACK_CUSTOM") }, ct);
        return true;
    }

    /// <summary>Sync the mount's internal position to a given RA/Dec without physically
    /// slewing. Used after plate-solve to correct pointing errors. Flow: flip
    /// <c>ON_COORD_SET</c> from SLEW/TRACK to SYNC, publish the coordinate, flip it
    /// back so the next <c>TelescopeSlewAsync</c> slews again instead of syncing.</summary>
    public async Task TelescopeSyncAsync(string deviceName, double raHours, double decDegrees, CancellationToken ct)
    {
        var client = _client;
        if (client == null) return;

        await client.SetSwitchManyAsync(deviceName, "ON_COORD_SET",
            new[] { ("SYNC", true), ("SLEW", false), ("TRACK", false) }, ct);
        try
        {
            await client.SendAsync(
                $"<newNumberVector device=\"{EscapeXml(deviceName)}\" name=\"EQUATORIAL_EOD_COORD\">" +
                $"<oneNumber name=\"RA\">{raHours.ToString(System.Globalization.CultureInfo.InvariantCulture)}</oneNumber>" +
                $"<oneNumber name=\"DEC\">{decDegrees.ToString(System.Globalization.CultureInfo.InvariantCulture)}</oneNumber>" +
                $"</newNumberVector>", ct);
        }
        finally
        {
            // Restore default "slew and track" behaviour so the next coordinate publish
            // slews instead of re-syncing. Every subsequent slew would otherwise be a
            // sync, which is a footgun that has cost at least one three-hour session.
            try
            {
                await client.SetSwitchManyAsync(deviceName, "ON_COORD_SET",
                    new[] { ("SYNC", false), ("SLEW", false), ("TRACK", true) }, ct);
            }
            catch { /* best effort */ }
        }
    }

    public async Task TelescopeParkAsync(string deviceName, bool park, CancellationToken ct)
    {
        var client = _client;
        if (client == null) return;

        if (park)
        {
            // User-facing Park = freeze in place AND block further motion commands. We deliberately
            // do NOT send TELESCOPE_PARK.PARK because some mounts (e.g. ZWO AM5) interpret that as
            // "slew to preset home", which feels identical to the Home button. Instead we abort
            // current motion, kill tracking, and set a server-side parked flag that move/slew/home
            // controllers check to reject new requests until unpark.
            try { await client.SetSwitchAsync(deviceName, "TELESCOPE_ABORT_MOTION", "ABORT", true, ct); } catch { }
            await client.SetSwitchManyAsync(deviceName, "TELESCOPE_TRACK_STATE",
                new[] { ("TRACK_ON", false), ("TRACK_OFF", true) }, ct);
            _parkedTelescope[deviceName] = true;
        }
        else
        {
            _parkedTelescope[deviceName] = false;
            // Unpark: just make sure tracking is available again (no physical command needed
            // since we never sent PARK=On). If the driver has its own park flag set (e.g. from
            // a boot-time park state), clear it.
            var dev = client.GetDevice(deviceName);
            if (dev != null && dev.Properties.TryGetValue("TELESCOPE_PARK", out var pk) && pk["PARK"]?.ValueOn == true)
            {
                await client.SetSwitchManyAsync(deviceName, "TELESCOPE_PARK",
                    new[] { ("PARK", false), ("UNPARK", true) }, ct);
            }
        }
    }

    public Task TelescopeAbortAsync(string deviceName, CancellationToken ct)
    {
        var client = _client;
        if (client == null) return Task.CompletedTask;
        return client.SetSwitchAsync(deviceName, "TELESCOPE_ABORT_MOTION", "ABORT", true, ct);
    }

    /// <summary>Abort a running CCD exposure. INDI standard vector is
    /// <c>CCD_ABORT_EXPOSURE</c> with a single <c>ABORT</c> element — drivers either
    /// also honour the legacy <c>CCD1.CCD_ABORT_EXPOSURE</c> form or expose nothing;
    /// we try both. Returns true when at least one succeeded.</summary>
    public async Task<bool> AbortExposureAsync(string deviceName, CancellationToken ct)
    {
        var client = _client;
        if (client == null) return false;
        var dev = client.GetDevice(deviceName);
        if (dev == null) return false;

        if (dev.Properties.ContainsKey("CCD_ABORT_EXPOSURE"))
        {
            await client.SetSwitchAsync(deviceName, "CCD_ABORT_EXPOSURE", "ABORT", true, ct);
            return true;
        }
        return false;
    }

    /// <summary>Enable / disable the camera's dew heater. Driver variance is wide:
    /// some expose <c>CCD_DEW_CONTROL</c> (on/off switch), some expose
    /// <c>AUX_HEATER_TOGGLE</c>, and ZWO-style drivers use a number element under
    /// <c>ANTI_DEW</c> with intensity 0–100. We try each in order and return true
    /// as soon as one sticks.</summary>
    public async Task<bool> SetDewHeaterAsync(string deviceName, bool on, int? powerPercent, CancellationToken ct)
    {
        var client = _client;
        if (client == null) return false;
        var dev = client.GetDevice(deviceName);
        if (dev == null) return false;

        if (dev.Properties.ContainsKey("CCD_DEW_CONTROL"))
        {
            await client.SetSwitchManyAsync(deviceName, "CCD_DEW_CONTROL",
                new[] { ("INDI_ENABLED", on), ("INDI_DISABLED", !on) }, ct);
            return true;
        }
        if (dev.Properties.ContainsKey("AUX_HEATER_TOGGLE"))
        {
            await client.SetSwitchManyAsync(deviceName, "AUX_HEATER_TOGGLE",
                new[] { ("INDI_ENABLED", on), ("INDI_DISABLED", !on) }, ct);
            return true;
        }
        // ZWO ASI: ANTI_DEW number vector, 0 = off, 1-100 = heater power.
        if (dev.Properties.ContainsKey("ANTI_DEW"))
        {
            var value = on ? (powerPercent ?? 50) : 0;
            await client.SetNumberAsync(deviceName, "ANTI_DEW", "ANTI_DEW_VALUE", value, ct);
            return true;
        }
        return false;
    }

    /// <summary>Start an axis motion. <paramref name="direction"/> ∈ north/south/east/west.
    /// <paramref name="rateMultiplier"/> picks a TELESCOPE_SLEW_RATE switch by mapping the
    /// sidereal-multiple requested by the app (e.g. 10.0 → "10x") to the nearest available
    /// element. When <paramref name="rateMultiplier"/> is null, we keep whatever rate is
    /// currently selected rather than forcing max — so users who choose "2x" in the UI
    /// actually get 2x.
    ///
    /// Pier-side correction: AM5's driver does NOT auto-flip N/S when the mount is on the
    /// opposite pier, which makes MOTION_SOUTH actually increase DEC at PIER_WEST. We swap
    /// the button's intent here so that "North" always means "DEC increases toward +90°"
    /// regardless of mechanical pier orientation — matching every user expectation and the
    /// INDI spec.</summary>
    /// Set TELESCOPE_SLEW_RATE without triggering motion. Used by the iOS
    /// chip strip to "preview" a rate selection so the highlighted chip
    /// reflects the driver's actual state — without this, the rate change
    /// only happened as a side-effect of a button press, leaving chips
    /// stuck in the pending colour until the user moved the mount.
    public async Task TelescopeSetSlewRateAsync(string deviceName, string? rateName, double? siderealRate, CancellationToken ct)
    {
        var client = _client; if (client == null) return;
        var dev = client.GetDevice(deviceName);
        if (dev == null) return;

        // siderealRate path: caller picked a numeric rate from a curated chip
        // strip backed by VARIABLE_SLEW_RATE.RATE_MAX (e.g. AM5 1..1440).
        // Push the number directly; sync the discrete TELESCOPE_SLEW_RATE
        // switch to its closest digit-prefixed element for UI consistency.
        if (siderealRate.HasValue
            && dev.Properties.TryGetValue("VARIABLE_SLEW_RATE", out var vsr)
            && vsr["RATE"] != null)
        {
            var min = vsr["RATE"]?.Min ?? 0;
            var max = vsr["RATE"]?.Max ?? 1440;
            var clamped = Math.Clamp(siderealRate.Value, min, max);
            await client.SetNumberAsync(deviceName, "VARIABLE_SLEW_RATE", "RATE", clamped, ct);
            _log.LogInformation("Telescope: VARIABLE_SLEW_RATE→{Rate}× sidereal on {Device}", clamped, deviceName);

            // Best-effort: also flip the discrete switch to whatever digit-
            // prefixed element is closest. Drivers that don't expose the
            // switch just skip silently; drivers that only honor the switch
            // (no VARIABLE) get covered by the rateName branch below.
            if (dev.Properties.TryGetValue("TELESCOPE_SLEW_RATE", out var rp))
            {
                string? best = null;
                double bestDist = double.MaxValue;
                foreach (var el in rp.Elements.Values)
                {
                    // Match against the element's resolved value (label
                    // preferred over name) — AM5's element name "10x" has
                    // label "1440x", and we want the highlighted preset to
                    // reflect the actual sidereal multiplier the chip means.
                    var num = ResolveElementRate(el);
                    if (!num.HasValue || num.Value <= 0) continue;
                    var dist = Math.Abs(num.Value - clamped);
                    if (dist < bestDist) { bestDist = dist; best = el.Name; }
                }
                if (best != null)
                {
                    var tuples = rp.Elements.Values.Select(e => (e.Name, e.Name == best)).ToArray();
                    await client.SetSwitchManyAsync(deviceName, "TELESCOPE_SLEW_RATE", tuples, ct);
                }
            }
            return;
        }

        // rateName path (legacy, also used when caller deliberately wants a
        // semantic rate like "GUIDE"/"CENTERING" rather than a number).
        if (string.IsNullOrEmpty(rateName)) return;
        if (!dev.Properties.TryGetValue("TELESCOPE_SLEW_RATE", out var rateProp)) return;
        if (rateProp[rateName!] == null)
        {
            _log.LogWarning("Telescope: SLEW_RATE element '{Rate}' not exposed by {Device} (available: {List})",
                rateName, deviceName,
                string.Join(",", rateProp.Elements.Values.Select(e => e.Name)));
            return;
        }
        var tuples2 = rateProp.Elements.Values.Select(e => (e.Name, e.Name == rateName)).ToArray();
        await client.SetSwitchManyAsync(deviceName, "TELESCOPE_SLEW_RATE", tuples2, ct);
        _log.LogInformation("Telescope: SLEW_RATE set to {Rate} on {Device}", rateName, deviceName);

        if (dev.Properties.TryGetValue("VARIABLE_SLEW_RATE", out var vsr2) && vsr2["RATE"] != null)
        {
            // Resolve via the matched element's label-or-name — the named
            // rate "10x" on AM5 actually means 1440× sidereal per its label,
            // so we push that, not the integer-from-name.
            var matched = rateProp[rateName!];
            var num = matched != null ? ResolveElementRate(matched) : LeadingDecimal(rateName!);
            if (num.HasValue && num.Value > 0)
            {
                var min = vsr2["RATE"]?.Min ?? 0;
                var max = vsr2["RATE"]?.Max ?? 1440;
                var clamped = Math.Clamp(num.Value, min, max);
                await client.SetNumberAsync(deviceName, "VARIABLE_SLEW_RATE", "RATE", clamped, ct);
                _log.LogInformation("Telescope: VARIABLE_SLEW_RATE→{Rate}× sidereal on {Device}", clamped, deviceName);
            }
        }
    }

    /// Send an ST4-style pulse-guide command — short timed motion in the
    /// requested direction at the driver's current guide rate. The mount
    /// fires the pulse and self-stops; useful for backlash testing,
    /// guiding-port verification, and as the building block for an in-
    /// server guider that doesn't depend on PHD2.
    ///
    /// INDI standard properties:
    ///   TELESCOPE_TIMED_GUIDE_NS  — TIMED_GUIDE_N / TIMED_GUIDE_S (ms)
    ///   TELESCOPE_TIMED_GUIDE_WE  — TIMED_GUIDE_W / TIMED_GUIDE_E (ms)
    /// Direction param is "N" / "S" / "E" / "W" (matches mount move API).
    public async Task TelescopePulseGuideAsync(string deviceName, string direction, int durationMs, CancellationToken ct)
    {
        var client = _client; if (client == null) return;
        var d = direction.Trim().ToUpperInvariant();
        var (prop, elem) = d switch
        {
            "N" => ("TELESCOPE_TIMED_GUIDE_NS", "TIMED_GUIDE_N"),
            "S" => ("TELESCOPE_TIMED_GUIDE_NS", "TIMED_GUIDE_S"),
            "E" => ("TELESCOPE_TIMED_GUIDE_WE", "TIMED_GUIDE_E"),
            "W" => ("TELESCOPE_TIMED_GUIDE_WE", "TIMED_GUIDE_W"),
            _ => ("", ""),
        };
        if (string.IsNullOrEmpty(prop)) return;
        await client.SetNumberAsync(deviceName, prop, elem, durationMs, ct);
    }

    /// Read the mount's GUIDE_RATE values (RA/Dec rate in fraction of
    /// sidereal). Surfaces them so the iOS pulse-guide test UI can show
    /// the user "this pulse will move ~X arcsec" — important because
    /// mounts vary widely in default guide rate.
    public (double? raRate, double? decRate) TryGetGuideRate(string deviceName)
    {
        var dev = _client?.GetDevice(deviceName);
        if (dev == null) return (null, null);
        if (!dev.Properties.TryGetValue("GUIDE_RATE", out var gr)) return (null, null);
        return (gr["GUIDE_RATE_NS"]?.AsDouble ?? gr["GUIDE_RATE_WE"]?.AsDouble,
                gr["GUIDE_RATE_WE"]?.AsDouble ?? gr["GUIDE_RATE_NS"]?.AsDouble);
    }

    public async Task TelescopeMoveAsync(string deviceName, string direction, double? rateMultiplier, CancellationToken ct, string? rateName = null, double? siderealRate = null)
    {
        var client = _client; if (client == null) return;
        var dev = client.GetDevice(deviceName);
        if (dev == null) return;

        // Resolve TELESCOPE_SLEW_RATE switch element. Priority:
        //   1. Explicit rateName (caller picked a switch element directly)
        //   2. Closest digit-prefixed element to siderealRate (iOS curated chips)
        //   3. Closest digit-prefixed element to rateMultiplier (legacy)
        string? best = null;
        IndiProperty? rateProp = null;
        if (dev.Properties.TryGetValue("TELESCOPE_SLEW_RATE", out rateProp))
        {
            if (!string.IsNullOrEmpty(rateName) && rateProp[rateName!] != null)
            {
                best = rateName;
            }
            else if (!string.IsNullOrEmpty(rateName))
            {
                best = rateProp.Elements.Values
                    .FirstOrDefault(e => string.Equals(e.Name, rateName, StringComparison.OrdinalIgnoreCase))?.Name;
                if (best == null)
                {
                    _log.LogWarning("Telescope: rateName '{Name}' not exposed by {Device} (available: {List})",
                        rateName, deviceName,
                        string.Join(",", rateProp.Elements.Values.Select(e => e.Name)));
                }
            }
            var fallbackTarget = siderealRate ?? rateMultiplier;
            if (best == null && fallbackTarget.HasValue)
            {
                double bestDist = double.MaxValue;
                foreach (var el in rateProp.Elements.Values)
                {
                    var num = ResolveElementRate(el);
                    if (!num.HasValue || num.Value <= 0) continue;
                    var dist = Math.Abs(num.Value - fallbackTarget.Value);
                    if (dist < bestDist) { bestDist = dist; best = el.Name; }
                }
            }
        }

        // Many mount drivers cache the slew rate at the moment motion *starts* and
        // ignore TELESCOPE_SLEW_RATE updates while MOTION_NS/WE is active (AM5
        // confirmed). Stop motion → change rate → restart motion. Without this,
        // "tap 1x while holding N" left the mount slewing at whatever rate it
        // started with — the regression the user reported.
        await client.SetSwitchManyAsync(deviceName, "TELESCOPE_MOTION_NS",
            new[] { ("MOTION_NORTH", false), ("MOTION_SOUTH", false) }, ct);
        await client.SetSwitchManyAsync(deviceName, "TELESCOPE_MOTION_WE",
            new[] { ("MOTION_WEST", false), ("MOTION_EAST", false) }, ct);

        if (best != null && rateProp != null)
        {
            var tuples = rateProp.Elements.Values.Select(e => (e.Name, e.Name == best)).ToArray();
            await client.SetSwitchManyAsync(deviceName, "TELESCOPE_SLEW_RATE", tuples, ct);
            _log.LogInformation("Telescope: SLEW_RATE→{Rate} on {Device} before MOTION_{Dir} (rateName={ReqName}, mult={Mult})",
                best, deviceName, direction, rateName ?? "(none)", rateMultiplier?.ToString() ?? "(none)");
            // Event-driven settle: wait until the driver confirms the new element
            // is ON and the property state is OK. Bounded so a non-publishing
            // driver can't stall the press response.
            await client.AwaitDeviceStateAsync(deviceName, d =>
                d.Properties.TryGetValue("TELESCOPE_SLEW_RATE", out var p)
                    && p[best!]?.ValueOn == true
                    && p.State == IndiPropertyState.Ok,
                TimeSpan.FromMilliseconds(500), ct);
        }

        // Push numeric VARIABLE_SLEW_RATE (sidereal multiples). Priority:
        //   1. siderealRate (curated numeric chips — exact value)
        //   2. digits parsed from resolved switch element ("Nx" → N)
        //   3. rateMultiplier (legacy)
        // Many mounts (AM5 confirmed) decouple this from the switch — without
        // pushing the number, motor speed stays at whatever was last set.
        if (dev.Properties.TryGetValue("VARIABLE_SLEW_RATE", out var vsr) && vsr["RATE"] != null)
        {
            double? targetVar = siderealRate;
            if (targetVar == null && best != null && rateProp != null)
            {
                var matched = rateProp[best];
                var num = matched != null ? ResolveElementRate(matched) : LeadingDecimal(best);
                if (num.HasValue && num.Value > 0) targetVar = num.Value;
            }
            if (targetVar == null && rateMultiplier.HasValue && rateMultiplier.Value >= 1)
                targetVar = rateMultiplier.Value;

            if (targetVar.HasValue)
            {
                var min = vsr["RATE"]?.Min ?? 0;
                var max = vsr["RATE"]?.Max ?? 1440;
                var clamped = Math.Clamp(targetVar.Value, min, max);
                await client.SetNumberAsync(deviceName, "VARIABLE_SLEW_RATE", "RATE", clamped, ct);
                _log.LogInformation("Telescope: VARIABLE_SLEW_RATE→{Rate}× sidereal on {Device}", clamped, deviceName);
            }
        }

        // Direction passes straight through. The previous pier-side XOR was removed
        // because PIER_SIDE flips during a pole crossing without us re-evaluating,
        // which produced the "N goes one way, next N goes the other" bouncing.
        var dir = direction.ToLowerInvariant();
        if (dir == "north" || dir == "south")
        {
            await client.SetSwitchManyAsync(deviceName, "TELESCOPE_MOTION_NS",
                new[] { ("MOTION_NORTH", dir == "north"), ("MOTION_SOUTH", dir == "south") }, ct);
        }
        else if (dir == "east" || dir == "west")
        {
            await client.SetSwitchManyAsync(deviceName, "TELESCOPE_MOTION_WE",
                new[] { ("MOTION_WEST", dir == "west"), ("MOTION_EAST", dir == "east") }, ct);
        }
    }

    public async Task TelescopeStopMoveAsync(string deviceName, CancellationToken ct)
    {
        var client = _client; if (client == null) return;
        await client.SetSwitchManyAsync(deviceName, "TELESCOPE_MOTION_NS",
            new[] { ("MOTION_NORTH", false), ("MOTION_SOUTH", false) }, ct);
        await client.SetSwitchManyAsync(deviceName, "TELESCOPE_MOTION_WE",
            new[] { ("MOTION_WEST", false), ("MOTION_EAST", false) }, ct);
    }

    public Task TelescopeFindHomeAsync(string deviceName, CancellationToken ct)
    {
        var client = _client;
        if (client == null) return Task.CompletedTask;
        var dev = client.GetDevice(deviceName);
        var home = dev?.Properties.TryGetValue("TELESCOPE_HOME", out var h) == true ? h : null;
        // ZWO AM5 uses "GO"; some drivers use "FIND_HOME".
        var element = home?["GO"] != null ? "GO" : "FIND_HOME";
        return client.SetSwitchAsync(deviceName, "TELESCOPE_HOME", element, true, ct);
    }

    // ----- Camera SER/AVI recording (driver-native) -----
    // The INDI driver records video streams directly to disk via its RECORD_STREAM + RECORD_FILE
    // property vectors — output format is SER (planetary imaging standard) or MP4 depending
    // on driver/ENV. We just toggle the driver switches; no server-side muxer required.

    public enum RecordMode { Manual, Duration, Frames }

    public async Task StartRecordingAsync(string deviceName, RecordMode mode, int durationSeconds, int frameCount, string dir, string filename, CancellationToken ct)
    {
        var client = _client;
        if (client == null) throw new InvalidOperationException("INDI client not connected");

        // Make sure output dir exists — the driver crashes silently if it can't write.
        try { Directory.CreateDirectory(dir); } catch { }

        // 1. Point the driver at our directory + filename pattern.
        var xml = $"<newTextVector device=\"{Escape(deviceName)}\" name=\"RECORD_FILE\">" +
                  $"<oneText name=\"RECORD_FILE_DIR\">{Escape(dir)}</oneText>" +
                  $"<oneText name=\"RECORD_FILE_NAME\">{Escape(filename)}</oneText>" +
                  $"</newTextVector>";
        await client.SendAsync(xml, ct);

        // 2. For duration/frame modes, program the numeric target.
        if (mode == RecordMode.Duration)
            await client.SetNumberAsync(deviceName, "RECORD_OPTIONS", "RECORD_DURATION", durationSeconds, ct);
        else if (mode == RecordMode.Frames)
            await client.SetNumberAsync(deviceName, "RECORD_OPTIONS", "RECORD_FRAME_TOTAL", frameCount, ct);

        // 3. Flip the matching switch on. RECORD_STREAM is OneOfMany, so turning one on
        //    implicitly turns the others off.
        var onElement = mode switch
        {
            RecordMode.Duration => "RECORD_DURATION_ON",
            RecordMode.Frames   => "RECORD_FRAME_ON",
            _                   => "RECORD_ON"
        };
        var switches = new[]
        {
            ("RECORD_ON",          onElement == "RECORD_ON"),
            ("RECORD_DURATION_ON", onElement == "RECORD_DURATION_ON"),
            ("RECORD_FRAME_ON",    onElement == "RECORD_FRAME_ON"),
            ("RECORD_OFF",         false),
        };
        await client.SetSwitchManyAsync(deviceName, "RECORD_STREAM", switches, ct);
    }

    public async Task StopRecordingAsync(string deviceName, CancellationToken ct)
    {
        var client = _client; if (client == null) return;
        await client.SetSwitchManyAsync(deviceName, "RECORD_STREAM", new[]
        {
            ("RECORD_ON", false), ("RECORD_DURATION_ON", false),
            ("RECORD_FRAME_ON", false), ("RECORD_OFF", true)
        }, ct);
    }

    public (bool running, string? activeSwitch, string? dir, string? filename) GetRecordingStatus(string deviceName)
    {
        var client = _client;
        var dev = client?.GetDevice(deviceName);
        if (dev == null) return (false, null, null, null);

        string? active = null;
        if (dev.Properties.TryGetValue("RECORD_STREAM", out var rs))
        {
            foreach (var e in rs.Elements.Values)
                if (e.ValueOn && e.Name != "RECORD_OFF") active = e.Name;
        }
        string? dir = null, file = null;
        if (dev.Properties.TryGetValue("RECORD_FILE", out var rf))
        {
            dir = rf["RECORD_FILE_DIR"]?.Value;
            file = rf["RECORD_FILE_NAME"]?.Value;
        }
        return (active != null, active, dir, file);
    }

    // ----- Frame type / calibration (CCD_FRAME_TYPE) -----

    /// <summary>Set the INDI standard CCD_FRAME_TYPE switch so the driver writes the correct
    /// IMAGETYP keyword into the FITS header. Most drivers also use this to skip shutter
    /// actuation for dark/bias frames (ZWO/PlayerOne electronically close the sensor).</summary>
    public async Task SetFrameTypeAsync(string deviceName, string imageType, CancellationToken ct)
    {
        var client = _client; if (client == null) return;
        var element = imageType.ToUpperInvariant() switch
        {
            "DARK" => "FRAME_DARK",
            "BIAS" => "FRAME_BIAS",
            "FLAT" => "FRAME_FLAT",
            _      => "FRAME_LIGHT"
        };
        var all = new[] { "FRAME_LIGHT", "FRAME_DARK", "FRAME_BIAS", "FRAME_FLAT" };
        var tuples = all.Select(n => (n, n == element)).ToArray();
        try { await client.SetSwitchManyAsync(deviceName, "CCD_FRAME_TYPE", tuples, ct); }
        catch { /* not all drivers expose this — best-effort */ }
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

    /// <summary>Status envelope for the flat panel. Matches iOS FlatPanelStatusResponse —
    /// brightness is null when the driver doesn't expose FLAT_LIGHT_INTENSITY.</summary>
    public object BuildFlatPanelStatus(EquipmentDescriptor selected)
    {
        var client = _client;
        int? brightness = null;
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
        }
        return new { connected = true, name = selected.Name, brightness };
    }

    // ----- Switch (powerbox / relay board) -----
    //
    // INDI doesn't standardize one switch property — each driver exposes its own ports as
    // independent switch vectors (typically OneOfMany ON/OFF pairs, or a single On/Off button).
    // We enumerate every switch vector on the device and surface each element pair as a
    // named channel. Read-only inspection; toggles flip whichever element is currently Off.

    public record SwitchChannel(string Property, string Label, bool On);

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

    // ----- Camera exposure / image transfer -----

    private readonly ConcurrentDictionary<string, TaskCompletionSource<(byte[] bytes, string? format)>> _pendingExposure = new();
    private bool _blobHandlerRegistered;

    private void EnsureBlobHandler()
    {
        if (_blobHandlerRegistered || _client == null) return;
        _client.BlobReceived += (device, prop, el, bytes, format) =>
        {
            if (prop != "CCD1") return; // INDI convention: primary sensor BLOB property
            // Streaming and still capture share CCD1. Stream frames carry ".stream" /
            // ".stream_jpg" formats and belong to CameraStreamService — never to the
            // exposure TCS. Without this filter an orphaned stream wedges the next
            // capture: the stream BLOB resolves the TCS with garbage bytes, then the
            // real .fits BLOB arrives with no waiter and is dropped.
            if (format != null && format.StartsWith(".stream", StringComparison.Ordinal)) return;
            if (_pendingExposure.TryRemove(device, out var tcs)) tcs.TrySetResult((bytes, format));
        };
        _blobHandlerRegistered = true;
    }

    /// <summary>Start an exposure and await the BLOB delivery. Returns the raw bytes (usually
    /// a .fits file) and the driver-reported format string. Throws on timeout / driver error.</summary>
    public async Task<(byte[] bytes, string? format)> CameraExposeAsync(
        string deviceName, double exposureSeconds, CancellationToken ct)
    {
        var client = _client ?? throw new InvalidOperationException("INDI client not connected");
        EnsureBlobHandler();

        // Ensure indiserver streams BLOBs for this device (off by default).
        try { await client.EnableBlobAsync(deviceName, ct); } catch { }

        var tcs = new TaskCompletionSource<(byte[] bytes, string? format)>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingExposure[deviceName] = tcs;

        try
        {
            await client.SetNumberAsync(deviceName, "CCD_EXPOSURE", "CCD_EXPOSURE_VALUE", exposureSeconds, ct);

            // Wait for BLOB arrival. Budget = exposure + generous download margin.
            // Bumped from +30s to +90s because PlayerOne / large-sensor CMOS readout
            // (uncompressed 12MP+ FITS over USB) can comfortably take 30-60s on its
            // own; the previous margin tripped 504s on otherwise healthy 30s captures.
            var timeout = TimeSpan.FromSeconds(Math.Max(15, exposureSeconds + 90));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            using var reg = cts.Token.Register(() => tcs.TrySetCanceled(cts.Token));
            try
            {
                return await tcs.Task;
            }
            catch (OperationCanceledException)
            {
                // Tell the driver to abort the in-flight exposure. Without this,
                // a stuck driver keeps the BUSY state and the next capture is
                // refused or wedges the same way.
                try { await AbortExposureAsync(deviceName, CancellationToken.None); } catch { }
                throw;
            }
        }
        finally
        {
            _pendingExposure.TryRemove(deviceName, out _);
        }
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

    private static string EscapeXml(string s)
        => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;");

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

    // Tracks which devices we've already announced as connected. Lets DeviceConnected
    // fire exactly once per connect, including the boot path where the driver was
    // already CONNECT=On before we hooked DevicesChanged.
    private readonly HashSet<string> _announcedConnected = new();

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

    // --- Camera operations ---

    private readonly Dictionary<string, SemaphoreSlim> _deviceLocks = new();
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

    public async Task SetGainAsync(string deviceName, int gain, CancellationToken ct)
    {
        var client = _client; if (client == null) return;
        // PlayerOne exposes gain via CCD_CONTROLS.Gain; other drivers may use CCD_GAIN.GAIN.
        var dev = client.GetDevice(deviceName);
        if (dev?.Properties.ContainsKey("CCD_CONTROLS") == true && dev.Properties["CCD_CONTROLS"]["Gain"] != null)
            await client.SetNumberAsync(deviceName, "CCD_CONTROLS", "Gain", gain, ct);
        else if (dev?.Properties.ContainsKey("CCD_GAIN") == true)
            await client.SetNumberAsync(deviceName, "CCD_GAIN", "GAIN", gain, ct);
    }

    public async Task SetOffsetAsync(string deviceName, int offset, CancellationToken ct)
    {
        var client = _client; if (client == null) return;
        var dev = client.GetDevice(deviceName);
        if (dev?.Properties.ContainsKey("CCD_CONTROLS") == true && dev.Properties["CCD_CONTROLS"]["Offset"] != null)
            await client.SetNumberAsync(deviceName, "CCD_CONTROLS", "Offset", offset, ct);
        else if (dev?.Properties.ContainsKey("CCD_OFFSET") == true)
            await client.SetNumberAsync(deviceName, "CCD_OFFSET", "OFFSET", offset, ct);
    }

    public async Task SetBinningAsync(string deviceName, int binX, int binY, CancellationToken ct)
    {
        var client = _client; if (client == null) return;
        var xml = $"<newNumberVector device=\"{Escape(deviceName)}\" name=\"CCD_BINNING\">" +
                  $"<oneNumber name=\"HOR_BIN\">{binX}</oneNumber>" +
                  $"<oneNumber name=\"VER_BIN\">{binY}</oneNumber>" +
                  $"</newNumberVector>";
        await client.SendAsync(xml, ct);
    }

    /// <summary>Set CCD_FRAME (X/Y/WIDTH/HEIGHT) for sub-frame read-out.
    /// Planetary lucky-imaging only reads a small window around the target;
    /// the smaller the frame, the higher the achievable fps.</summary>
    public async Task SetSubFrameAsync(string deviceName, int x, int y, int width, int height, CancellationToken ct)
    {
        var client = _client; if (client == null) return;
        var xml = $"<newNumberVector device=\"{Escape(deviceName)}\" name=\"CCD_FRAME\">" +
                  $"<oneNumber name=\"X\">{x}</oneNumber>" +
                  $"<oneNumber name=\"Y\">{y}</oneNumber>" +
                  $"<oneNumber name=\"WIDTH\">{width}</oneNumber>" +
                  $"<oneNumber name=\"HEIGHT\">{height}</oneNumber>" +
                  $"</newNumberVector>";
        await client.SendAsync(xml, ct);
    }

    /// <summary>Read current sensor max resolution from CCD_INFO. Used to
    /// translate normalized ROI fractions into pixel coordinates.</summary>
    public (int width, int height)? GetSensorSize(string deviceName)
    {
        var client = _client; if (client == null) return null;
        var dev = client.GetDevice(deviceName);
        if (dev == null) return null;
        if (!dev.Properties.TryGetValue("CCD_INFO", out var info)) return null;
        var w = (int)(info["CCD_MAX_X"]?.AsDouble ?? 0);
        var h = (int)(info["CCD_MAX_Y"]?.AsDouble ?? 0);
        return (w > 0 && h > 0) ? (w, h) : null;
    }

    public async Task SetCoolingAsync(string deviceName, bool enabled, double? targetTemperature, CancellationToken ct)
    {
        var client = _client; if (client == null) return;
        var dev = client.GetDevice(deviceName);

        if (targetTemperature.HasValue && dev?.Properties.ContainsKey("CCD_TEMPERATURE") == true)
        {
            await client.SetNumberAsync(deviceName, "CCD_TEMPERATURE", "CCD_TEMPERATURE_VALUE", targetTemperature.Value, ct);
        }
        if (dev?.Properties.ContainsKey("CCD_COOLER") == true)
        {
            await client.SetSwitchManyAsync(deviceName, "CCD_COOLER",
                new[] { ("COOLER_ON", enabled), ("COOLER_OFF", !enabled) }, ct);
        }
    }

    private static string Escape(string s)
        => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;");

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
        var hasConn = dev.Properties.ContainsKey("CONNECTION");
        var connected = hasConn ? dev.IsConnected : IntentConnected(deviceName);
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
        var connected = dev.Properties.ContainsKey("CONNECTION") ? dev.IsConnected : IntentConnected(deviceName);
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
        var connected = dev.Properties.ContainsKey("CONNECTION") ? dev.IsConnected : IntentConnected(deviceName);
        var info = new RotatorInfo { Connected = connected, Name = deviceName };
        if (!connected) return info;
        if (dev.Properties.TryGetValue("ABS_ROTATOR_ANGLE", out var ang))
        {
            info.Position = (float)(ang["ANGLE"]?.AsDouble ?? 0);
            info.MechanicalPosition = info.Position;
            info.IsMoving = ang.State == IndiPropertyState.Busy;
        }
        return info;
    }

    public DomeInfo? TryBuildDomeInfo(string deviceName)
    {
        var dev = _client?.GetDevice(deviceName);
        if (dev == null) return null;
        var connected = dev.Properties.ContainsKey("CONNECTION") ? dev.IsConnected : IntentConnected(deviceName);
        var info = new DomeInfo { Connected = connected, Name = deviceName };
        if (!connected) return info;
        if (dev.Properties.TryGetValue("ABS_DOME_POSITION", out var pos))
        {
            info.Azimuth = pos["DOME_ABSOLUTE_POSITION"]?.AsDouble ?? double.NaN;
            info.Slewing = pos.State == IndiPropertyState.Busy;
        }
        return info;
    }

    public FlatDeviceInfo? TryBuildFlatDeviceInfo(string deviceName)
    {
        var dev = _client?.GetDevice(deviceName);
        if (dev == null) return null;
        var connected = dev.Properties.ContainsKey("CONNECTION") ? dev.IsConnected : IntentConnected(deviceName);
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
        var connected = dev.Properties.ContainsKey("CONNECTION") ? dev.IsConnected : IntentConnected(deviceName);
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
        var connected = dev.Properties.ContainsKey("CONNECTION") ? dev.IsConnected : IntentConnected(deviceName);
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
        var connected = dev.Properties.ContainsKey("CONNECTION") ? dev.IsConnected : IntentConnected(deviceName);
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
        var hasConn = dev.Properties.ContainsKey("CONNECTION");
        var connected = hasConn ? dev.IsConnected : IntentConnected(deviceName);
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

public enum IndiDeviceKind { Camera, Telescope, Focuser, FilterWheel }
