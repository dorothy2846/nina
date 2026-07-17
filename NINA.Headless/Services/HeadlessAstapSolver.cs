using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NINA.Astrometry;
using NINA.Core.Utility;
using NINA.PlateSolving;

namespace NINA.Headless.Services;

/// <summary>
/// A custom, iPhone-controlled ASTAP wrapper for NINA.Headless.
/// Injects robust CLI parameters specific for heavy light-pollution (City Skies).
/// </summary>
public class HeadlessAstapSolver
{
    private readonly string _executablePath;

    public HeadlessAstapSolver(string executablePath)
    {
        _executablePath = executablePath;
    }

    /// <summary>Whether the ASTAP executable actually exists. Callers should
    /// refuse to start solve-dependent workflows with a clear message instead
    /// of letting every solve silently "fail" (which reads as bad sky/optics).</summary>
    public bool IsAvailable => File.Exists(_executablePath);
    public string ExecutablePath => _executablePath;

    public async Task<PlateSolveResult> SolveAsync(
        string imageFilePath, 
        double focalLengthMm, 
        double pixelSizeUm, 
        double targetRa = double.NaN, 
        double targetDec = double.NaN)
    {
        var result = new PlateSolveResult { Success = false };

        if (!File.Exists(_executablePath))
        {
            return result; // Or throw exception
        }

        var fov = CalculateFov(focalLengthMm, pixelSizeUm, 4000); // approx 4000px height for typical camera
        var outputFilePath = Path.Combine(Path.GetDirectoryName(imageFilePath)!, Path.GetFileNameWithoutExtension(imageFilePath) + ".ini");

        var args = new List<string>
        {
            $"-f \"{imageFilePath}\"",
            $"-fov {fov.ToString(CultureInfo.InvariantCulture)}",
            "-z 0", // Auto downsample (Crucial for noisy city images)
            "-s 500", // Max stars (Limits noise processing)
            "-speed slow" // *NEW*: Tells ASTAP to try harder and ignore faint noise (City Mode)
        };

        if (!double.IsNaN(targetRa) && !double.IsNaN(targetDec))
        {
            args.Add($"-r 15"); // 15 degrees narrow search radius for extremely fast local solving
            args.Add($"-ra {Math.Round(targetRa, 6).ToString(CultureInfo.InvariantCulture)}");
            args.Add($"-spd {Math.Round(targetDec + 90.0, 6).ToString(CultureInfo.InvariantCulture)}");
        }
        else
        {
            args.Add("-r 180"); // Full blind solve
        }

        var processStartInfo = new ProcessStartInfo
        {
            FileName = _executablePath,
            Arguments = string.Join(" ", args),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(processStartInfo);
        if (process != null)
        {
            await process.WaitForExitAsync();
        }

        if (File.Exists(outputFilePath))
        {
            var dict = File.ReadLines(outputFilePath)
                .Where(line => !string.IsNullOrWhiteSpace(line) && line.Contains('='))
                .Select(line => line.Split(new[] { '=' }, 2))
                .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim());

            if (dict.TryGetValue("PLTSOLVD", out var solved) && solved == "T")
            {
                result.Success = true;
                result.Coordinates = new Coordinates(
                    double.Parse(dict["CRVAL1"], CultureInfo.InvariantCulture),
                    double.Parse(dict["CRVAL2"], CultureInfo.InvariantCulture),
                    Epoch.J2000,
                    Coordinates.RAType.Degrees
                );
                
                // Parse pixel scale
                if (dict.TryGetValue("CD1_2", out var cd1_2Str) && dict.TryGetValue("CD2_2", out var cd2_2Str))
                {
                    double.TryParse(cd1_2Str, NumberStyles.Any, CultureInfo.InvariantCulture, out double cr1y);
                    double.TryParse(cd2_2Str, NumberStyles.Any, CultureInfo.InvariantCulture, out double cr2y);
                    result.Pixscale = AstroUtil.DegreeToArcsec(Math.Sqrt(cr1y * cr1y + cr2y * cr2y));
                }
            }
            
            // Cleanup ini file
            try { File.Delete(outputFilePath); } catch { }
        }

        return result;
    }

    private double CalculateFov(double fl, double pz, int sensorHeight)
    {
        if (fl <= 0 || pz <= 0) return 1.0;
        var pzMm = pz / 1000.0;
        var sensorHeightMm = sensorHeight * pzMm;
        return (sensorHeightMm / fl) * (180.0 / Math.PI); // Fov in degrees
    }
}
