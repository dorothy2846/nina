using System.Collections.Concurrent;
using NINA.Core.Enum;
using NINA.Equipment.Equipment.MyCamera;
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
        if (dev.Properties.TryGetValue("TELESCOPE_SLEW_RATE", out var sr))
        {
            slewRateLabel = sr.Elements.Values.FirstOrDefault(e => e.ValueOn)?.Name;
            // Preserve the driver's ordering (INDI defines it) but sort by leading integer for
            // the app's UI. AM5 is already "1x"…"10x"; older LX200 drivers might be "Guide/
            // Centering/Find/Max" and sort by an arbitrary stable order.
            availableSlewRates = sr.Elements.Values
                .Select(e => e.Name)
                .OrderBy(n =>
                {
                    var digits = new string(n.TakeWhile(char.IsDigit).ToArray());
                    return int.TryParse(digits, out var v) ? v : int.MaxValue;
                })
                .ThenBy(n => n, StringComparer.Ordinal)
                .ToArray();
        }
        double? variableSlewRate = null;
        if (dev.Properties.TryGetValue("VARIABLE_SLEW_RATE", out var vsr))
            variableSlewRate = vsr["RATE"]?.AsDouble;
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
            variableSlewRate,
            guideRate,
            canFindHome,
            canPark,
            canSetTracking
        };
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
    public async Task TelescopeMoveAsync(string deviceName, string direction, double? rateMultiplier, CancellationToken ct)
    {
        var client = _client; if (client == null) return;
        var dev = client.GetDevice(deviceName);

        if (dev != null && rateMultiplier.HasValue && dev.Properties.TryGetValue("TELESCOPE_SLEW_RATE", out var rateProp))
        {
            // Parse leading integer from each element name ("1x", "2x", …, "10x") and pick
            // the one closest to the requested multiplier. Clamps naturally to the driver's
            // exposed range (e.g. AM5 1–10).
            string? best = null;
            double bestDist = double.MaxValue;
            foreach (var el in rateProp.Elements.Values)
            {
                var digits = new string(el.Name.TakeWhile(char.IsDigit).ToArray());
                if (!int.TryParse(digits, out var v)) continue;
                var dist = Math.Abs(v - rateMultiplier.Value);
                if (dist < bestDist) { bestDist = dist; best = el.Name; }
            }
            if (best != null && rateProp[best]?.ValueOn != true)
            {
                var tuples = rateProp.Elements.Values.Select(e => (e.Name, e.Name == best)).ToArray();
                try { await client.SetSwitchManyAsync(deviceName, "TELESCOPE_SLEW_RATE", tuples, ct); } catch { }
            }
        }

        // On PIER_WEST the DEC axis is upside-down relative to PIER_EAST, so the AM5 driver's
        // MOTION_NORTH physically moves the tube toward decreasing DEC. Flip here so callers
        // get the celestial direction they asked for.
        var pierWest = dev?.Properties.TryGetValue("TELESCOPE_PIER_SIDE", out var ps) == true
                       && ps["PIER_WEST"]?.ValueOn == true;

        var dir = direction.ToLowerInvariant();
        if (dir == "north" || dir == "south")
        {
            bool wantNorth = (dir == "north") ^ pierWest;
            await client.SetSwitchManyAsync(deviceName, "TELESCOPE_MOTION_NS",
                new[] { ("MOTION_NORTH", wantNorth), ("MOTION_SOUTH", !wantNorth) }, ct);
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
            var timeout = TimeSpan.FromSeconds(Math.Max(10, exposureSeconds + 30));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            using var reg = cts.Token.Register(() => tcs.TrySetCanceled(cts.Token));
            return await tcs.Task;
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let IndiServerManager bring up indiserver first.
        try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); } catch { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _client = new IndiClient(_server.Host, _server.Port, _log);
                _client.DevicesChanged += OnDevicesChanged;
                await _client.ConnectAsync(stoppingToken);

                // Wait until cancelled or connection drops.
                while (!stoppingToken.IsCancellationRequested && _client.IsConnected)
                {
                    try { await Task.Delay(1000, stoppingToken); } catch { break; }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "INDI discovery iteration failed; retry in 3s");
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

            try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); } catch { break; }
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
}

public enum IndiDeviceKind { Camera, Telescope, Focuser, FilterWheel }
