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

        double axisDec = AstroUtil.ToDegree(decAxisRads);
        double axisRa = AstroUtil.DegreesToHours(AstroUtil.ToDegree(raAxisRads));

        // 4. Calculate Alt/Az coordinates of the physical axis TODAY at the CURRENT Sidereal Time
        DateTime nowUtc = DateTime.UtcNow;
        double lst = AstroUtil.GetLocalSiderealTime(nowUtc, longitudeDegrees);
        double hourAngleHours = AstroUtil.GetHourAngle(lst, axisRa);
        double hourAngleAngles = hourAngleHours * 15.0; // Convert hours to degrees for AstroUtil

        double axisAlt = AstroUtil.GetAltitude(hourAngleAngles, latitudeDegrees, axisDec);
        double axisAz = AstroUtil.GetAzimuth(hourAngleAngles, axisAlt, latitudeDegrees, axisDec);

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
        string altHint = altErrorArcMin > 0 ? "고도를 낮추세요 (Down)" : "고도를 높이세요 (Up)";
        string azHint = azErrorArcMin > 0 ? "방위각을 서쪽(왼쪽)으로 (Left / West)" : "방위각을 동쪽(오른쪽)으로 (Right / East)";

        if (latitudeDegrees < 0)
        {
            // Reverse hints for Southern Hemisphere
            altHint = altErrorArcMin > 0 ? "고도를 낮추세요 (Down)" : "고도를 높이세요 (Up)";
            azHint = azErrorArcMin > 0 ? "방위각을 동쪽(오른쪽)으로 (Right / East)" : "방위각을 서쪽(왼쪽)으로 (Left / West)";
        }

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

    private static Vector3D SphericalToCartesian(double raHours, double decDegrees)
    {
        double raRads = AstroUtil.ToRadians(AstroUtil.HoursToDegrees(raHours));
        double decRads = AstroUtil.ToRadians(decDegrees);

        return new Vector3D(
            Math.Cos(decRads) * Math.Cos(raRads),
            Math.Cos(decRads) * Math.Sin(raRads),
            Math.Sin(decRads)
        );
    }
}
