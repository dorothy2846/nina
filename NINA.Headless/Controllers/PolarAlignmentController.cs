using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using NINA.Astrometry;
using NINA.Core.Model.Equipment;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Headless.Services;
using NINA.Headless.Services.Remote;
using NINA.Equipment.Model;
using NINA.Core.Model;

namespace NINA.Headless.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class PolarAlignmentController : ControllerBase
{
    private static readonly object SyncRoot = new();
    private static CancellationTokenSource? _runCts;

    private static bool _running;
    private static int _step;
    private static int _totalSteps = 3;
    private static bool _completed;
    private static PolarAlignmentMath.PolarAlignmentResult? _baseError;
    private static object? _latestLiveUpdate;
    private static double _baseAlt3;
    private static double _baseAz3;

    private readonly NinaStateService _state;
    private readonly HeadlessAstapSolver _solver;
    private readonly RemoteEventBus _eventBus;

    public PolarAlignmentController(NinaStateService state, HeadlessAstapSolver solver, RemoteEventBus eventBus)
    {
        _state = state;
        _solver = solver;
        _eventBus = eventBus;
    }

    [HttpPost("start")]
    public IActionResult Start()
    {
        // Polar alignment math is meaningless without the real site latitude —
        // refuse to start rather than silently compute against a wrong location.
        if (ResolveSiteCoordinates() == null)
        {
            return BadRequest(new
            {
                success = false,
                message = "Site coordinates unavailable: mount reports no GEOGRAPHIC_COORD and no equipment profile has been synced."
            });
        }

        lock (SyncRoot)
        {
            if (_running) return BadRequest(new { success = false, message = "Already running" });

            _runCts?.Cancel();
            _runCts = new CancellationTokenSource();

            _running = true;
            _step = 1;
            _totalSteps = 3;
            _completed = false;
            _baseError = null;
            _latestLiveUpdate = null;

            var token = _runCts.Token;
            _ = Task.Run(() => PolarAlignmentRoutineAsync(token), token);
        }

        return Ok(new { success = true, message = "Polar alignment started" });
    }

    private async Task PolarAlignmentRoutineAsync(CancellationToken token)
    {
        double[] raPts = new double[3];
        double[] decPts = new double[3];

        try
        {
            var (latitude, longitude) = ResolveSiteCoordinates()
                ?? throw new InvalidOperationException("Site coordinates unavailable");
            for (int i = 0; i < 3; i++)
            {
                lock (SyncRoot) { _step = i + 1; }
                await BroadcastStatusAsync();

                // 1. Capture
                var sequence = new CaptureSequence(4.0, CaptureSequence.ImageTypes.LIGHT, null, new BinningMode(2, 2), 1);
                await _state.CameraMediator.Capture(sequence, token, new Progress<ApplicationStatus>());
                
                var tempDir = PlatformPaths.IsLinux && Directory.Exists("/dev/shm")
                    ? "/dev/shm" : Path.GetTempPath();
                var tempImage = Path.Combine(tempDir, $"pa_{Guid.NewGuid()}.jpg");
                await System.IO.File.WriteAllBytesAsync(tempImage, _state.LatestImageData!, token);

                // 2. Solve
                var focalLength = ResolveFocalLength();
                var pixelSize = _state.CameraInfo?.PixelSize ?? 3.76;
                var solve = await _solver.SolveAsync(tempImage, focalLength, pixelSize, 
                    i == 0 ? double.NaN : raPts[i-1], i == 0 ? double.NaN : decPts[i-1]); // Fast local solve after point 1

                if (!solve.Success && i == 0)
                {
                    // Fallback to Blind Solve on step 1
                    solve = await _solver.SolveAsync(tempImage, focalLength, pixelSize);
                }

                if (!solve.Success) throw new Exception($"Solving failed at step {i + 1}");

                raPts[i] = solve.Coordinates.RA;
                decPts[i] = solve.Coordinates.Dec;

                // 3. Slew RA by 15 degrees (1 hour) for next point
                if (i < 2)
                {
                    var newRa = raPts[i] + 1.0; 
                    if (newRa >= 24.0) newRa -= 24.0;
                    await _state.TelescopeMediator.SlewToCoordinatesAsync(new Coordinates(newRa, decPts[i], Epoch.JNOW, Coordinates.RAType.Hours), token);
                }
            }

            // 4. Calculate Base Error
            lock (SyncRoot)
            {
                _baseError = PolarAlignmentMath.CalculateError(
                    raPts[0], decPts[0], raPts[1], decPts[1], raPts[2], decPts[2],
                    latitude, longitude);
                
                DateTime nowUtc = DateTime.UtcNow;
                double lst = AstroUtil.GetLocalSiderealTime(nowUtc, longitude);
                double hourAngleHours = AstroUtil.GetHourAngle(lst, raPts[2]);
                double hourAngleDeg = hourAngleHours * 15.0;
                _baseAlt3 = AstroUtil.GetAltitude(hourAngleDeg, latitude, decPts[2]);
                _baseAz3 = AstroUtil.GetAzimuth(hourAngleDeg, _baseAlt3, latitude, decPts[2]);
                
                _completed = true;
            }

            await BroadcastStatusAsync();

            // 5. Continuous Live Update Loop (SignalR)
            while (!token.IsCancellationRequested)
            {
                var sequence = new CaptureSequence(2.0, CaptureSequence.ImageTypes.LIGHT, null, new BinningMode(2, 2), 1);
                await _state.CameraMediator.Capture(sequence, token, new Progress<ApplicationStatus>());
                
                var tempDir = PlatformPaths.IsLinux && Directory.Exists("/dev/shm")
                    ? "/dev/shm" : Path.GetTempPath();
                var tempImage = Path.Combine(tempDir, $"pa_live_{Guid.NewGuid()}.jpg");
                await System.IO.File.WriteAllBytesAsync(tempImage, _state.LatestImageData!, token);

                // Fast local solve around last known point
                var focalLength = ResolveFocalLength();
                var pixelSize = _state.CameraInfo?.PixelSize ?? 3.76;
                var solve = await _solver.SolveAsync(tempImage, focalLength, pixelSize, raPts[2], decPts[2]);
                
                if (solve.Success)
                {
                    DateTime nowUtc = DateTime.UtcNow;
                    double lst = AstroUtil.GetLocalSiderealTime(nowUtc, longitude);
                    double hourAngleHours = AstroUtil.GetHourAngle(lst, solve.Coordinates.RA);
                    double hourAngleDeg = hourAngleHours * 15.0;
                    double curAlt = AstroUtil.GetAltitude(hourAngleDeg, latitude, solve.Coordinates.Dec);
                    double curAz = AstroUtil.GetAzimuth(hourAngleDeg, curAlt, latitude, solve.Coordinates.Dec);

                    // How much did the physical knobs move the scope?
                    double deltaAltArgMin = (curAlt - _baseAlt3) * 60.0;
                    double deltaAzArcMin = (curAz - _baseAz3) * 60.0;

                    // Remaining Error
                    double newAltError = _baseError.AltitudeErrorArcMin - deltaAltArgMin;
                    double newAzError = _baseError.AzimuthErrorArcMin - deltaAzArcMin;
                    double newTotalError = Math.Sqrt(newAltError * newAltError + newAzError * newAzError);

                    var liveUpdate = new
                    {
                        altitudeErrorArcMin = Math.Round(newAltError, 2),
                        azimuthErrorArcMin = Math.Round(newAzError, 2),
                        totalErrorArcMin = Math.Round(newTotalError, 2),
                        altitudeCorrectionHint = newAltError > 0 ? "고도를 낮추세요 (Down)" : "고도를 높이세요 (Up)",
                        azimuthCorrectionHint = newAzError > 0 ? "방위각을 서쪽(왼쪽)으로" : "방위각을 동쪽(오른쪽)으로"
                    };

                    lock (SyncRoot) { _latestLiveUpdate = liveUpdate; }
                    _eventBus.Broadcast("PolarAlignmentLiveUpdate", liveUpdate);
                }
            }
        }
        catch (Exception ex)
        {
            // Abort
            lock (SyncRoot) { _running = false; _completed = false; }
            await BroadcastStatusAsync();
            _eventBus.Broadcast("PolarAlignmentError", $"Routine failed or was aborted: {ex.Message}");
        }
    }

    /// Observing-site coordinates: prefer what the mount itself reports
    /// (GEOGRAPHIC_COORD → TelescopeInfo), fall back to the phone-synced
    /// equipment profile. (0,0) from either source means "not configured".
    private (double Latitude, double Longitude)? ResolveSiteCoordinates()
    {
        var info = _state.TelescopeInfo;
        if (info != null && (info.SiteLatitude != 0 || info.SiteLongitude != 0))
            return (info.SiteLatitude, info.SiteLongitude);

        var profile = ProfileSyncController.ActiveCloudProfile;
        if (profile != null && (profile.SiteLatitude != 0 || profile.SiteLongitude != 0))
            return (profile.SiteLatitude, profile.SiteLongitude);

        return null;
    }

    /// Focal length for the solver's FOV hint — profile-synced value when the
    /// phone has pushed one, otherwise a conservative 400 mm default (a wrong
    /// hint slows the solve but doesn't corrupt the result).
    private static double ResolveFocalLength()
    {
        var fl = ProfileSyncController.ActiveCloudProfile?.TelescopeFocalLength ?? 0;
        return fl > 0 ? fl : 400.0;
    }

    private Task BroadcastStatusAsync()
    {
        _eventBus.Broadcast("PolarAlignmentStatus", new
        {
            running = _running,
            step = _step,
            totalSteps = _totalSteps,
            completed = _completed,
            baseError = _baseError
        });
        return Task.CompletedTask;
    }

    [HttpGet("liveupdate")]
    public IActionResult GetLiveUpdate()
    {
        lock (SyncRoot)
        {
            if (_latestLiveUpdate == null) 
                return NotFound(new { success = false, message = "No live update available yet." });
            
            return Ok(_latestLiveUpdate);
        }
    }

    [HttpGet("status")]
    public IActionResult Status()
    {
        lock (SyncRoot)
        {
            return Ok(new
            {
                running = _running,
                step = _step,
                totalSteps = _totalSteps,
                completed = _completed,
                baseError = _baseError
            });
        }
    }

    [HttpPost("stop")]
    public IActionResult Stop()
    {
        lock (SyncRoot)
        {
            _runCts?.Cancel();
            _running = false;
        }

        return Ok(new { success = true, message = "Polar alignment stopped" });
    }
}
