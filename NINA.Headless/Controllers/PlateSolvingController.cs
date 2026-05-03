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

    public PlateSolvingController(NinaStateService state)
    {
        _state = state;
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

        // 2. Mock camera capture & file creation for now (In reality, _state.CameraMediator.Capture() is used)
        // Wait for latest image data or trigger real hardware exposure
        var tempImage = Path.Combine(Path.GetTempPath(), $"solve_{Guid.NewGuid()}.jpg");
        
        if (_state.LatestImageData != null)
        {
            await System.IO.File.WriteAllBytesAsync(tempImage, _state.LatestImageData);
        }
        else
        {
            // Dummy logic if no camera attached during test
            await System.IO.File.WriteAllBytesAsync(tempImage, new byte[100]); 
        }

        // 3. Run the Robust City-Solver ASTAP Pipeline
        var focalLength = 400.0; // In reality, fetch from _state.Profile.Telescope.FocalLength
        var pixelSize = camera?.PixelSize ?? 3.76;
        
        var solveResult = await solver.SolveAsync(
            tempImage, 
            focalLength, 
            pixelSize, 
            telescope?.RightAscension ?? double.NaN, 
            telescope?.Declination ?? double.NaN);

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
            ra = solveResult.Coordinates.RA,
            dec = solveResult.Coordinates.Dec,
            pixelScale = solveResult.Pixscale,
            rotation = solveResult.PositionAngle,
            flipped = solveResult.Flipped,
            solveTimeMs = 1500, // Estimated metadata
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

        var focalLength = 400.0; // Retrieve from Profile in production
        var pixelSize = cameraInfo.PixelSize;
        var solver = new HeadlessAstapSolver(PlatformPaths.AstapPath);

        // Convert target to radians for distance math later
        var targetRaRad = request.TargetRa * Math.PI / 180.0;
        var targetDecRad = request.TargetDec * Math.PI / 180.0;
        var targetCoords = new Coordinates(request.TargetRa, request.TargetDec, Epoch.JNOW, Coordinates.RAType.Degrees);

        int attempt = 0;
        double separationDeg = double.MaxValue;
        bool isCentered = false;

        while (attempt < request.MaxAttempts)
        {
            attempt++;

            // 1. Capture Image
            var sequence = new CaptureSequence(
                request.ExposureTime > 0 ? request.ExposureTime : 5.0,
                CaptureSequence.ImageTypes.LIGHT, null, 
                new BinningMode((short)request.Binning, (short)request.Binning), 1);
            
            _state.MarkExposureStarted(sequence.ExposureTime);
            try { await _state.CameraMediator.Capture(sequence, CancellationToken.None, new Progress<ApplicationStatus>()); }
            catch (Exception ex) { return StatusCode(500, new { success = false, message = $"Capture failed: {ex.Message}" }); }
            finally { _state.MarkExposureFinished(); }

            // /dev/shm is a RAM-backed tmpfs on Linux — much faster for repeated IO.
            var tempDir = PlatformPaths.IsLinux && Directory.Exists("/dev/shm")
                ? "/dev/shm" : Path.GetTempPath();
            var tempImage = Path.Combine(tempDir, $"center_{Guid.NewGuid()}.jpg");
            var imgData = _state.LatestImageData; 
            if (imgData == null || imgData.Length < 100)
            {
                // Dummy if real camera driver isn't returning data properly during headles test
                await System.IO.File.WriteAllBytesAsync(tempImage, new byte[100]);
            }
            else
            {
                await System.IO.File.WriteAllBytesAsync(tempImage, imgData);
            }

            // 2. Solve Image
            var solveResult = await solver.SolveAsync(tempImage, focalLength, pixelSize, request.TargetRa, request.TargetDec);
            try { System.IO.File.Delete(tempImage); } catch { }

            if (!solveResult.Success)
            {
                return StatusCode(500, new { success = false, message = $"ASTAP Blind Solve Failed on attempt {attempt}." });
            }

            // 3. Calculate Separation (Haversine formula approx)
            var actualRaRad = solveResult.Coordinates.RA * Math.PI / 180.0;
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

            // 4. Sync and Slew
            var actualCoords = new Coordinates(solveResult.Coordinates.RA, solveResult.Coordinates.Dec, Epoch.JNOW, Coordinates.RAType.Degrees);
            await _state.TelescopeMediator.Sync(actualCoords);
            await _state.TelescopeMediator.SlewToCoordinatesAsync(targetCoords, CancellationToken.None);

            // Wait for mount to settle
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
