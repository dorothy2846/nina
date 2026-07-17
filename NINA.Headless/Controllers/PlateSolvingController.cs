using Microsoft.AspNetCore.Mvc;
using NINA.Headless.Models;
using NINA.Headless.Services;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Equipment.Model;
using NINA.Astrometry;
using NINA.Core.Model.Equipment;

namespace NINA.Headless.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class PlateSolvingController : ControllerBase
{
    private static readonly object SyncRoot = new();
    private static object? _lastResult;

    private readonly NinaStateService _state;
    private readonly EquipmentSelectionService _equipment;
    private readonly IndiDiscoveryService _indi;

    public PlateSolvingController(NinaStateService state, EquipmentSelectionService equipment, IndiDiscoveryService indi)
    {
        _state = state;
        _equipment = equipment;
        _indi = indi;
    }

    /// <summary>INDI-direct exposure to a temp file for ASTAP — the NINA
    /// CameraMediator has no headless handler, so LatestImageData was always
    /// null and both endpoints solved a 100-byte dummy file.</summary>
    private async Task<string> CaptureToTempAsync(double exposureSeconds, CancellationToken token)
    {
        var selected = _equipment.GetSelected(DeviceKind.Camera);
        if (selected?.Provider != EquipmentProvider.Indi || !_equipment.IsConnected(DeviceKind.Camera))
            throw new InvalidOperationException("No INDI camera connected for plate solving");
        var (bytes, format) = await _indi.CameraExposeAsync(selected.UniqueId, exposureSeconds, token);
        if (bytes == null || bytes.Length == 0)
            throw new InvalidOperationException("Camera returned no image data");
        var ext = string.IsNullOrEmpty(format) ? "fits" : format.TrimStart('.');
        var tempDir = PlatformPaths.IsLinux && Directory.Exists("/dev/shm") ? "/dev/shm" : Path.GetTempPath();
        var tempImage = Path.Combine(tempDir, $"solve_{Guid.NewGuid()}.{ext}");
        await System.IO.File.WriteAllBytesAsync(tempImage, bytes, token);
        return tempImage;
    }

    [HttpGet("status")]
    public IActionResult Status()
    {
        lock (SyncRoot)
        {
            return Ok(new
            {
                astapAvailable = true,
                lastResult = _lastResult
            });
        }
    }

    [HttpPost("capture-and-solve")]
    public async Task<IActionResult> CaptureAndSolve([FromBody] PlateSolveCaptureRequest request)
    {
        if (request.ExposureTime <= 0)
        {
            return BadRequest(new { success = false, errorMessage = "ExposureTime must be greater than 0" });
        }

        var telescope = _state.TelescopeInfo;
        var camera = _state.CameraInfo;

        var astapPath = PlatformPaths.AstapPath;

        var solver = new HeadlessAstapSolver(astapPath);

        string tempImage;
        try { tempImage = await CaptureToTempAsync(request.ExposureTime, HttpContext.RequestAborted); }
        catch (Exception ex) { return StatusCode(503, new { success = false, errorMessage = ex.Message }); }

        // 3. Run the Robust City-Solver ASTAP Pipeline
        // Profile-synced focal length when the phone has pushed one; 400 mm
        // FOV hint otherwise (wrong hint slows the solve, doesn't corrupt it).
        var profileFl = ProfileSyncController.ActiveCloudProfile?.TelescopeFocalLength ?? 0;
        var focalLength = profileFl > 0 ? profileFl : 400.0;
        var pixelSize = camera?.PixelSize ?? 3.76;
        
        double hintRaDeg = double.NaN, hintDecDeg = double.NaN;
        if (telescope != null && !double.IsNaN(telescope.RightAscension))
        {
            var (raJ2000, decJ2000) = PolarAlignmentController.ToJ2000(telescope.RightAscension, telescope.Declination);
            hintRaDeg = raJ2000; hintDecDeg = decJ2000; // ASTAP -ra is in HOURS
        }
        var solveResult = await solver.SolveAsync(tempImage, focalLength, pixelSize, hintRaDeg, hintDecDeg);
        if (!solveResult.Success)
            solveResult = await solver.SolveAsync(tempImage, focalLength, pixelSize, double.NaN, double.NaN); // blind fallback

        // Cleanup
        try { System.IO.File.Delete(tempImage); } catch { }

        if (!solveResult.Success)
        {
            return Ok(new { success = false, errorMessage = "ASTAP Blind Solve Failed. Check focal length and star visibility." });
        }

        // 4. Update state tracking
        var result = new
        {
            success = true,
            // Solver reports RA in hours; API speaks degrees (J2000).
            ra = solveResult.Coordinates.RA * 15.0,
            dec = solveResult.Coordinates.Dec,
            pixelScale = solveResult.Pixscale,
            rotation = solveResult.PositionAngle,
            flipped = solveResult.Flipped,
            epoch = "J2000",
            errorMessage = (string?)null
        };

        lock (SyncRoot)
        {
            _lastResult = result;
        }

        return Ok(result);
    }

    [HttpPost("center")]
    public async Task<IActionResult> Center([FromBody] PlateSolveCenterRequest request)
    {
        if (request.MaxAttempts <= 0)
        {
            return BadRequest(new { success = false, message = "MaxAttempts must be greater than 0" });
        }

        var telescopeInfo = _state.TelescopeInfo;
        var cameraInfo = _state.CameraInfo;

        if (telescopeInfo?.Connected != true || cameraInfo?.Connected != true)
        {
            return StatusCode(503, new { success = false, message = "Camera and Telescope must be connected for centering." });
        }

        var profileFl = ProfileSyncController.ActiveCloudProfile?.TelescopeFocalLength ?? 0;
        var focalLength = profileFl > 0 ? profileFl : 400.0;
        var pixelSize = cameraInfo.PixelSize;
        var solver = new HeadlessAstapSolver(PlatformPaths.AstapPath);

        // Target arrives in J2000 degrees (SkyMap catalog frame) — the solve is
        // J2000 too, so separation math stays in J2000. Only the mount speaks
        // epoch-of-date: convert at the sync/slew boundary.
        var targetRaRad = request.TargetRa * Math.PI / 180.0;
        var targetDecRad = request.TargetDec * Math.PI / 180.0;
        var (targetRaJnowH, targetDecJnow) = PolarAlignmentController.ToJnow(request.TargetRa / 15.0, request.TargetDec);

        int attempt = 0;
        double separationDeg = double.MaxValue;
        bool isCentered = false;

        while (attempt < request.MaxAttempts)
        {
            attempt++;

            // 1. Capture (INDI-direct — the mediator path never produced data headless)
            string tempImage;
            try { tempImage = await CaptureToTempAsync(request.ExposureTime > 0 ? request.ExposureTime : 5.0, HttpContext.RequestAborted); }
            catch (Exception ex) { return StatusCode(503, new { success = false, message = $"Capture failed: {ex.Message}" }); }

            // 2. Solve Image
            // ASTAP -ra hint is in HOURS; blind fallback covers a badly lost mount.
            var solveResult = await solver.SolveAsync(tempImage, focalLength, pixelSize, request.TargetRa / 15.0, request.TargetDec);
            if (!solveResult.Success)
                solveResult = await solver.SolveAsync(tempImage, focalLength, pixelSize, double.NaN, double.NaN);
            try { System.IO.File.Delete(tempImage); } catch { }

            if (!solveResult.Success)
            {
                return StatusCode(500, new { success = false, message = $"ASTAP Blind Solve Failed on attempt {attempt}." });
            }

            // 3. Calculate Separation (Haversine formula approx)
            var actualRaRad = solveResult.Coordinates.RA * 15.0 * Math.PI / 180.0; // solver RA is hours
            var actualDecRad = solveResult.Coordinates.Dec * Math.PI / 180.0;
            var a = Math.Pow(Math.Sin((actualDecRad - targetDecRad) / 2), 2) + Math.Cos(targetDecRad) * Math.Cos(actualDecRad) * Math.Pow(Math.Sin((actualRaRad - targetRaRad) / 2), 2);
            var distanceRad = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
            separationDeg = distanceRad * 180.0 / Math.PI;

            // 0.05 degrees ~ 3 arcmin tolerance
            if (separationDeg <= (request.Tolerance > 0 ? request.Tolerance : 0.05))
            {
                isCentered = true;
                break;
            }

            // 4. Sync where we actually are (solve J2000 -> JNOW), then slew to target.
            var telSel = _equipment.GetSelected(DeviceKind.Telescope);
            if (telSel?.Provider != EquipmentProvider.Indi)
                return StatusCode(503, new { success = false, message = "INDI telescope required for centering" });
            var (solveRaJnowH, solveDecJnow) = PolarAlignmentController.ToJnow(solveResult.Coordinates.RA, solveResult.Coordinates.Dec);
            await _indi.TelescopeSyncAsync(telSel.UniqueId, solveRaJnowH, solveDecJnow, HttpContext.RequestAborted);
            await _indi.TelescopeSlewAsync(telSel.UniqueId, targetRaJnowH, targetDecJnow, HttpContext.RequestAborted);
            await Task.Delay(3000); 
        }

        return Ok(new
        {
            success = isCentered,
            message = isCentered ? "Centered successfully on target" : "Failed to center within tolerance",
            attempts = attempt,
            separation = separationDeg,
            ra = request.TargetRa,
            dec = request.TargetDec
        });
    }
}
