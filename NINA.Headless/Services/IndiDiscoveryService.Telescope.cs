// IndiDiscoveryService.Telescope.cs — telescope actuation for IndiDiscoveryService:
// slew/sync/park/tracking/motion/pulse-guide commands, slew-rate resolution helpers,
// and the shared astro math (LST, equatorial→horizontal conversion).

using NINA.Headless.Indi;

namespace NINA.Headless.Services;

public partial class IndiDiscoveryService
{
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

    // ----- Telescope control -----

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
        // Compound directions ("northeast" …) engage both axes in one call —
        // required for diagonals because this method stops all motion on
        // entry, so two sequential single-axis calls would cancel each other.
        var dir = direction.ToLowerInvariant();
        var wantNorth = dir.Contains("north");
        var wantSouth = dir.Contains("south");
        var wantWest = dir.Contains("west");
        var wantEast = dir.Contains("east");
        if (wantNorth || wantSouth)
        {
            await client.SetSwitchManyAsync(deviceName, "TELESCOPE_MOTION_NS",
                new[] { ("MOTION_NORTH", wantNorth), ("MOTION_SOUTH", wantSouth) }, ct);
        }
        if (wantWest || wantEast)
        {
            await client.SetSwitchManyAsync(deviceName, "TELESCOPE_MOTION_WE",
                new[] { ("MOTION_WEST", wantWest), ("MOTION_EAST", wantEast) }, ct);
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

    /// <summary>Send the mount home. Returns false when the driver has no
    /// TELESCOPE_HOME property AND emulation isn't possible — callers must
    /// surface that instead of reporting success (this used to silently
    /// no-op on such drivers, including the INDI simulator).</summary>
    public async Task<bool> TelescopeFindHomeAsync(string deviceName, CancellationToken ct)
    {
        var client = _client;
        if (client == null) return false;
        var dev = client.GetDevice(deviceName);
        if (dev == null) return false;

        if (dev.Properties.TryGetValue("TELESCOPE_HOME", out var home))
        {
            // ZWO AM5 uses "GO"; some drivers use "FIND_HOME".
            var element = home["GO"] != null ? "GO" : "FIND_HOME";
            await client.SetSwitchAsync(deviceName, "TELESCOPE_HOME", element, true, ct);
            ScheduleHomeSettle(deviceName);
            return true;
        }

        // Emulated CWD home for drivers without TELESCOPE_HOME: slew to hour
        // angle +6h at the celestial pole, then stop tracking once the goto
        // settles — a mount sitting at home shouldn't keep sidereal-tracking.
        if (!dev.Properties.TryGetValue("GEOGRAPHIC_COORD", out var geo)) return false;
        var lon = geo["LONG"]?.AsDouble;
        var lat = geo["LAT"]?.AsDouble;
        if (!lon.HasValue) return false;

        var homeRa = WrapHours(ComputeLstHours(lon.Value) - 6.0);
        var homeDec = (lat ?? 90) >= 0 ? 90.0 : -90.0;
        _log.LogInformation("Telescope: no TELESCOPE_HOME on {Device} — emulating CWD home (RA={Ra:F3}h, Dec={Dec})", deviceName, homeRa, homeDec);
        var ok = await TelescopeSlewAsync(deviceName, homeRa, homeDec, ct);
        if (!ok) return false;
        ScheduleHomeSettle(deviceName);
        return true;
    }

    /// <summary>After a home command (native or emulated), wait for the goto
    /// to finish and stop tracking. A mount sitting at home shouldn't keep
    /// sidereal-tracking — this is what made "Go Home" feel wrong: the sim
    /// reached home and immediately kept tracking away from it.</summary>
    private void ScheduleHomeSettle(string deviceName)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                // Bounded settle: wait for the goto to finish (EOD coord
                // property leaves Busy), then kill tracking.
                for (var i = 0; i < 90; i++)
                {
                    await Task.Delay(1000);
                    var d = _client?.GetDevice(deviceName);
                    var eq = d?.Properties.TryGetValue("EQUATORIAL_EOD_COORD", out var p) == true ? p : null;
                    if (eq == null || eq.State != IndiPropertyState.Busy) break;
                }
                await TelescopeTrackingAsync(deviceName, false, CancellationToken.None);
                _log.LogInformation("Telescope: home settled on {Device}; tracking stopped", deviceName);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Telescope: home settle failed on {Device}", deviceName);
            }
        });
    }

    private static string EscapeXml(string s)
        => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;");
}
