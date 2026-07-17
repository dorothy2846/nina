using System;
using NINA.Astrometry;

namespace NINA.Headless.Services;

public class PolarAlignmentMath
{
    public struct Vector3D
    {
        public double X, Y, Z;
        public Vector3D(double x, double y, double z) { X = x; Y = y; Z = z; }

        public static Vector3D Subtract(Vector3D a, Vector3D b) => 
            new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

        public static Vector3D CrossProduct(Vector3D a, Vector3D b) =>
            new(a.Y * b.Z - a.Z * b.Y,
                a.Z * b.X - a.X * b.Z,
                a.X * b.Y - a.Y * b.X);

        public Vector3D Normalize()
        {
            double length = Math.Sqrt(X * X + Y * Y + Z * Z);
            return length > 0 ? new Vector3D(X / length, Y / length, Z / length) : this;
        }
    }

    public class PolarAlignmentResult
    {
        public double AxisRaHours { get; set; }
        public double AxisDecDegrees { get; set; }
        public double AltitudeErrorArcMin { get; set; }
        public double AzimuthErrorArcMin { get; set; }
        public double TotalErrorArcMin { get; set; }
        public string AltitudeCorrectionHint { get; set; } = string.Empty;
        public string AzimuthCorrectionHint { get; set; } = string.Empty;
    }

    // ----- Pure astro helpers (no NINA.Astrometry / SOFA native dependency:
    // the SOFA type initializer throws on macOS where the native lib isn't
    // bundled, and it took the whole polar-alignment routine down with it) --

    public static double JulianDate(DateTime utc) =>
        (utc - new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc)).TotalDays + 2451545.0;

    /// <summary>Local sidereal time in HOURS (IAU-1982 GMST, arcsecond-class).</summary>
    public static double LocalSiderealTimeHours(DateTime utc, double longitudeDeg)
    {
        var d = JulianDate(utc) - 2451545.0;
        var t = d / 36525.0;
        var gmst = 280.46061837 + 360.98564736629 * d + 0.000387933 * t * t - t * t * t / 38710000.0;
        var lst = (gmst + longitudeDeg) % 360.0;
        if (lst < 0) lst += 360.0;
        return lst / 15.0;
    }

    /// <summary>Hour angle in HOURS, wrapped to (−12, +12].</summary>
    public static double HourAngleHours(double lstHours, double raHours)
    {
        var ha = (lstHours - raHours) % 24.0;
        if (ha > 12) ha -= 24;
        if (ha <= -12) ha += 24;
        return ha;
    }

    public static double AltitudeDeg(double haDeg, double latDeg, double decDeg)
    {
        double haR = haDeg * Math.PI / 180, latR = latDeg * Math.PI / 180, decR = decDeg * Math.PI / 180;
        var sinAlt = Math.Sin(decR) * Math.Sin(latR) + Math.Cos(decR) * Math.Cos(latR) * Math.Cos(haR);
        return Math.Asin(Math.Clamp(sinAlt, -1, 1)) * 180 / Math.PI;
    }

    /// <summary>Azimuth in degrees from NORTH, clockwise.</summary>
    public static double AzimuthDeg(double haDeg, double latDeg, double decDeg)
    {
        double haR = haDeg * Math.PI / 180, latR = latDeg * Math.PI / 180, decR = decDeg * Math.PI / 180;
        var az = Math.Atan2(
            -Math.Cos(decR) * Math.Sin(haR),
            Math.Sin(decR) * Math.Cos(latR) - Math.Cos(decR) * Math.Cos(haR) * Math.Sin(latR));
        var deg = az * 180 / Math.PI;
        return deg < 0 ? deg + 360 : deg;
    }

    /// <summary>
    /// Calculates the Polar Alignment Error using 3 Plate Solved points.
    /// Points should be obtained by slewing the RA axis.
    /// </summary>
    public static PolarAlignmentResult CalculateError(
        double ra1, double dec1, 
        double ra2, double dec2, 
        double ra3, double dec3,
        double latitudeDegrees, double longitudeDegrees)
    {
        // 1. Convert RA(hours) and Dec(degrees) to 3D Cartesian coordinates on a unit sphere.
        var p1 = SphericalToCartesian(ra1, dec1);
        var p2 = SphericalToCartesian(ra2, dec2);
        var p3 = SphericalToCartesian(ra3, dec3);

        // 2. Find the normal vector of the plane containing the 3 points.
        // This normal vector points precisely to the mechanical axis of rotation of the mount.
        var v1 = Vector3D.Subtract(p2, p1);
        var v2 = Vector3D.Subtract(p3, p1);
        var normal = Vector3D.CrossProduct(v1, v2).Normalize();

        // Ensure the normal vector points to the Northern Hemisphere (Z > 0)
        // If in Southern Hemisphere, we would check Z < 0. (Assuming Northern for now)
        if (latitudeDegrees >= 0 && normal.Z < 0)
        {
            normal = new Vector3D(-normal.X, -normal.Y, -normal.Z);
        }
        else if (latitudeDegrees < 0 && normal.Z > 0)
        {
            normal = new Vector3D(-normal.X, -normal.Y, -normal.Z);
        }

        // 3. Convert the Normal Vector back to Equatorial Coordinates (RA / Dec of the physical axis)
        var decAxisRads = Math.Asin(normal.Z);
        var raAxisRads = Math.Atan2(normal.Y, normal.X);
        if (raAxisRads < 0) raAxisRads += 2 * Math.PI;

        double axisDec = decAxisRads * 180.0 / Math.PI;
        double axisRa = raAxisRads * 180.0 / Math.PI / 15.0;

        // 4. Calculate Alt/Az coordinates of the physical axis TODAY at the CURRENT Sidereal Time
        DateTime nowUtc = DateTime.UtcNow;
        double lst = LocalSiderealTimeHours(nowUtc, longitudeDegrees);
        double hourAngleAngles = HourAngleHours(lst, axisRa) * 15.0;

        double axisAlt = AltitudeDeg(hourAngleAngles, latitudeDegrees, axisDec);
        double axisAz = AzimuthDeg(hourAngleAngles, latitudeDegrees, axisDec);

        // 5. Calculate the Alt/Az of the TRUE Celestial Pole (NCP or SCP)
        double poleAlt = Math.Abs(latitudeDegrees);
        double poleAz = latitudeDegrees >= 0 ? 0.0 : 180.0; // 0 for North, 180 for South

        // 6. Calculate the Errors in ArcMinutes
        double altErrorDeg = axisAlt - poleAlt;
        // Adjust Azimuth for wrap-around.
        double azDiff = axisAz - poleAz;
        if (azDiff > 180) azDiff -= 360;
        if (azDiff < -180) azDiff += 360;
        double azErrorDeg = azDiff;

        double altErrorArcMin = altErrorDeg * 60.0;
        double azErrorArcMin = azErrorDeg * 60.0;
        double totalErrorArcMin = Math.Sqrt(altErrorArcMin * altErrorArcMin + azErrorArcMin * azErrorArcMin);

        // 7. Generate physical knob turning hints
        var (altHint, azHint) = KnobHints(altErrorArcMin, azErrorArcMin, latitudeDegrees);

        return new PolarAlignmentResult
        {
            AxisRaHours = axisRa,
            AxisDecDegrees = axisDec,
            AltitudeErrorArcMin = Math.Round(altErrorArcMin, 2),
            AzimuthErrorArcMin = Math.Round(azErrorArcMin, 2),
            TotalErrorArcMin = Math.Round(totalErrorArcMin, 2),
            AltitudeCorrectionHint = altHint,
            AzimuthCorrectionHint = azHint
        };
    }

    /// <summary>Physical knob hints from signed errors. Single source of
    /// truth — the base result AND every live update use this, so the
    /// direction advice can't diverge between phases. Altitude advice is
    /// hemisphere-independent (an axis pointing too high is lowered with the
    /// same knob everywhere); the azimuth advice flips south of the equator
    /// because "toward the pole" reverses left/right when facing south.
    /// The old code had a dead southern reassignment of the alt hint and NO
    /// hemisphere handling in the live loop.</summary>
    public static (string AltHint, string AzHint) KnobHints(double altErrorArcMin, double azErrorArcMin, double latitudeDegrees)
    {
        string altHint = altErrorArcMin > 0 ? "고도를 낮추세요 (Down)" : "고도를 높이세요 (Up)";
        string azHint;
        if (latitudeDegrees >= 0)
        {
            azHint = azErrorArcMin > 0
                ? "방위각을 서쪽(왼쪽)으로 (Left / West)"
                : "방위각을 동쪽(오른쪽)으로 (Right / East)";
        }
        else
        {
            azHint = azErrorArcMin > 0
                ? "방위각을 동쪽(오른쪽)으로 (Right / East)"
                : "방위각을 서쪽(왼쪽)으로 (Left / West)";
        }
        return (altHint, azHint);
    }

    private static Vector3D SphericalToCartesian(double raHours, double decDegrees)
    {
        double raRads = raHours * 15.0 * Math.PI / 180.0;
        double decRads = decDegrees * Math.PI / 180.0;

        return new Vector3D(
            Math.Cos(decRads) * Math.Cos(raRads),
            Math.Cos(decRads) * Math.Sin(raRads),
            Math.Sin(decRads)
        );
    }
}
