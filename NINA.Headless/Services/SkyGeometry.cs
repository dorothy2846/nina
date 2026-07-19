namespace NINA.Headless.Services;

/// <summary>
/// Minimal solar-position + alt/az math for sequence scheduling: "is it
/// astronomically dark yet" and "has the target sunk below X degrees".
/// NOAA low-precision solar algorithm (~0.01° — twilight gating needs far
/// less) so the scheduler doesn't need a dependency on the full NINA
/// astrometry stack.
/// </summary>
public static class SkyGeometry
{
    private const double Deg2Rad = Math.PI / 180.0;
    private const double Rad2Deg = 180.0 / Math.PI;

    /// <summary>Astronomical twilight threshold: sun below -18°.</summary>
    public const double AstronomicalTwilightSunAltDeg = -18.0;

    private static double JulianDate(DateTime utc) =>
        utc.ToOADate() + 2415018.5;

    /// <summary>Greenwich mean sidereal time in degrees.</summary>
    public static double GmstDeg(DateTime utc)
    {
        var d = JulianDate(utc) - 2451545.0;
        var gmst = 280.46061837 + 360.98564736629 * d;
        gmst %= 360.0;
        return gmst < 0 ? gmst + 360.0 : gmst;
    }

    /// <summary>Sun geocentric RA/Dec in degrees (low-precision ecliptic model).</summary>
    public static (double RaDeg, double DecDeg) SunRaDec(DateTime utc)
    {
        var d = JulianDate(utc) - 2451545.0;
        var g = (357.529 + 0.98560028 * d) * Deg2Rad;      // mean anomaly
        var q = 280.459 + 0.98564736 * d;                   // mean longitude
        var lambda = (q + 1.915 * Math.Sin(g) + 0.020 * Math.Sin(2 * g)) * Deg2Rad;
        var e = (23.439 - 0.00000036 * d) * Deg2Rad;        // obliquity

        var ra = Math.Atan2(Math.Cos(e) * Math.Sin(lambda), Math.Cos(lambda)) * Rad2Deg;
        if (ra < 0) ra += 360.0;
        var dec = Math.Asin(Math.Sin(e) * Math.Sin(lambda)) * Rad2Deg;
        return (ra, dec);
    }

    /// <summary>Altitude in degrees of a J2000-ish RA/Dec as seen from lat/lon at
    /// <paramref name="utc"/>. Precession ignored — scheduling tolerance is
    /// tenths of a degree over decades, irrelevant against a whole-degree gate.</summary>
    public static double AltitudeDeg(DateTime utc, double raDeg, double decDeg, double latDeg, double lonDeg)
    {
        var lstDeg = GmstDeg(utc) + lonDeg;
        var haRad = ((lstDeg - raDeg + 540.0) % 360.0 - 180.0) * Deg2Rad;
        var latRad = latDeg * Deg2Rad;
        var decRad = decDeg * Deg2Rad;
        var sinAlt = Math.Sin(latRad) * Math.Sin(decRad)
                   + Math.Cos(latRad) * Math.Cos(decRad) * Math.Cos(haRad);
        return Math.Asin(Math.Clamp(sinAlt, -1.0, 1.0)) * Rad2Deg;
    }

    public static double SunAltitudeDeg(DateTime utc, double latDeg, double lonDeg)
    {
        var (ra, dec) = SunRaDec(utc);
        return AltitudeDeg(utc, ra, dec, latDeg, lonDeg);
    }

    /// <summary>Next time (from <paramref name="fromUtc"/>) the sun drops below
    /// the astronomical twilight altitude, or null if it doesn't within 24h
    /// (polar summer). 1-minute march is plenty for a scheduling estimate.</summary>
    public static DateTime? NextAstronomicalTwilight(DateTime fromUtc, double latDeg, double lonDeg)
    {
        if (SunAltitudeDeg(fromUtc, latDeg, lonDeg) < AstronomicalTwilightSunAltDeg) return fromUtc;
        for (var t = fromUtc; t < fromUtc.AddHours(24); t = t.AddMinutes(1))
        {
            if (SunAltitudeDeg(t, latDeg, lonDeg) < AstronomicalTwilightSunAltDeg) return t;
        }
        return null;
    }
}
