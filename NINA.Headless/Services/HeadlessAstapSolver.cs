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
    /// <summary>FOV (degrees) measured from the most recent successful solve.</summary>
    private static double _learnedFovDeg;

    public HeadlessAstapSolver(string executablePath)
    {
        _executablePath = executablePath;
    }

    /// <summary>Cheap FITS header sniff (first few 2880-byte blocks): image
    /// height and whether the file carries enough optics info (FOCALLEN +
    /// XPIXSZ/PIXSIZE1) for ASTAP to derive the FOV itself.</summary>
    private static (bool SelfDescribing, int? HeightPx) TryReadFitsGeometry(string path)
    {
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext != ".fits" && ext != ".fit" && ext != ".fts") return (false, null);

            using var fs = File.OpenRead(path);
            var buf = new byte[2880 * 4];
            var read = fs.Read(buf, 0, buf.Length);
            var header = System.Text.Encoding.ASCII.GetString(buf, 0, read);

            int? height = null;
            var naxis2 = System.Text.RegularExpressions.Regex.Match(header, @"NAXIS2\s*=\s*(\d+)");
            if (naxis2.Success) height = int.Parse(naxis2.Groups[1].Value);

            var hasFocal = System.Text.RegularExpressions.Regex.IsMatch(header, @"FOCALLEN\s*=");
            var hasPix = System.Text.RegularExpressions.Regex.IsMatch(header, @"(XPIXSZ|PIXSIZE1)\s*=");
            return (hasFocal && hasPix, height);
        }
        catch
        {
            return (false, null);
        }
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

        // FOV hint. The old code assumed a 4000px-tall sensor, which was up
        // to several × off for real cameras (1024–6000px) — and a badly wrong
        // -fov makes ASTAP fail outright. Preference order:
        //   1. FITS that self-describes (FOCALLEN + pixel size) → -fov 0,
        //      ASTAP computes it from the header itself.
        //   2. FITS with readable NAXIS2 → compute with the REAL height.
        //   3. Fallback: legacy 4000px assumption.
        var geometry = TryReadFitsGeometry(imageFilePath);
        var isBlind = double.IsNaN(targetRa) || double.IsNaN(targetDec);
        double fov;
        if (geometry.SelfDescribing || isBlind)
            // Self-describing FITS: ASTAP derives FOV from the header.
            // Blind solve: we know nothing — let ASTAP search scale too. A
            // wrong computed hint (profile focal length that doesn't match
            // the actual optics) makes blind solving fail outright.
            fov = 0;
        else if (_learnedFovDeg > 0)
            // Measured on a previous successful solve of this rig — beats
            // any profile-derived guess. Re-learned whenever a blind solve
            // succeeds, so an optics change self-heals.
            fov = _learnedFovDeg;
        else
            fov = CalculateFov(focalLengthMm, pixelSizeUm, geometry.HeightPx ?? 4000);
        var outputFilePath = Path.Combine(Path.GetDirectoryName(imageFilePath)!, Path.GetFileNameWithoutExtension(imageFilePath) + ".ini");

        var args = new List<string>
        {
            $"-f \"{imageFilePath}\"",
            $"-fov {fov.ToString(CultureInfo.InvariantCulture)}",
            // Downsample ×2. "-z 0" (none) was labeled 'crucial for noisy
            // city images' but empirically BROKE wide-field blind solving
            // outright (verified: identical frame fails at -z 0, solves at
            // -z 1/2 with hint or blind). ASTAP's own guidance is 2 for most
            // sensors.
            "-z 2",
            "-s 500", // Max stars (Limits noise processing)
            "-speed slow" // *NEW*: Tells ASTAP to try harder and ignore faint noise (City Mode)
        };

        // Star-database directory override (NINA_ASTAP_DB). The CLI build
        // looks in /usr/local/opt/astap by default on macOS.
        var dbDir = Environment.GetEnvironmentVariable("NINA_ASTAP_DB");
        if (!string.IsNullOrEmpty(dbDir))
        {
            args.Add($"-d \"{dbDir}\"");
        }

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

                    // Learn the actual FOV for future hinted solves.
                    if (result.Pixscale > 0 && geometry.HeightPx.HasValue)
                        _learnedFovDeg = result.Pixscale * geometry.HeightPx.Value / 3600.0;
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
