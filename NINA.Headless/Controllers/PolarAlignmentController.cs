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
    private static string? _lastError;

    private readonly NinaStateService _state;
    private readonly HeadlessAstapSolver _solver;
    private readonly RemoteEventBus _eventBus;
    private readonly EquipmentSelectionService _equipment;
    private readonly IndiDiscoveryService _indi;
    private readonly CameraStreamService _stream;

    public PolarAlignmentController(
        NinaStateService state,
        HeadlessAstapSolver solver,
        RemoteEventBus eventBus,
        EquipmentSelectionService equipment,
        IndiDiscoveryService indi,
        CameraStreamService stream)
    {
        _state = state;
        _solver = solver;
        _eventBus = eventBus;
        _equipment = equipment;
        _indi = indi;
        _stream = stream;
    }

    public record PolarAlignStartRequest(double? ExposureSeconds);
    private static double? _paExposureOverride;

    [HttpPost("start")]
    public async Task<IActionResult> Start([FromBody] PolarAlignStartRequest? request)
    {
        _paExposureOverride = request?.ExposureSeconds is > 0 ? request.ExposureSeconds : null;
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

        // A missing ASTAP binary made every solve return a generic failure —
        // indistinguishable from clouds. Refuse up front with the real reason.
        if (!_solver.IsAvailable)
        {
            return StatusCode(503, new
            {
                success = false,
                message = $"ASTAP plate solver not found at '{_solver.ExecutablePath}' — install ASTAP (+ star database) on the server."
            });
        }

        // Still captures while CCD_VIDEO_STREAM is on wedge the driver (the
        // same conflict /camera/stream/start guards against, reversed) — stop
        // the live preview before the measurement captures begin.
        var stoppedStream = false;
        if (_stream.IsRunning)
        {
            await _stream.StopAsync(HttpContext.RequestAborted);
            stoppedStream = true;
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
            _lastError = null;

            var token = _runCts.Token;
            _ = Task.Run(() => PolarAlignmentRoutineAsync(token), token);
        }

        return Ok(new
        {
            success = true,
            message = stoppedStream ? "Polar alignment started (live preview stopped)" : "Polar alignment started"
        });
    }

    private async Task PolarAlignmentRoutineAsync(CancellationToken token)
    {
        double[] raPts = new double[3];
        double[] decPts = new double[3];

        try
        {
            var (latitude, longitude) = ResolveSiteCoordinates()
                ?? throw new InvalidOperationException("Site coordinates unavailable");

            // Measurement needs tracking ON: 4 s exposures on a non-tracking
            // mount trail by ~60″ and degrade (or kill) the solves. The mount
            // may legitimately arrive here tracking-off (e.g. fresh from home).
            await TrySetTrackingAsync(true, token);

            // Slew AWAY from the meridian so the RA legs can't cross it and
            // trigger a pier flip mid-routine — a flipped third point no
            // longer lies on the same axis circle and poisons the plane fit.
            double slewDirHours = 1.0;
            double? nextHintRa = null;

            // Leg-1 hint: the mount's own reported position (JNOW → J2000 for
            // ASTAP). A near solve is ~100× faster than blind, and the mount
            // usually knows where it points to well within the 15° search
            // radius. Blind solve remains the fallback.
            double leg1HintRa = double.NaN, leg1HintDec = double.NaN;
            var tinfo = _state.TelescopeInfo;
            if (tinfo != null && (tinfo.RightAscension != 0 || tinfo.Declination != 0))
            {
                (leg1HintRa, leg1HintDec) = ToJ2000(tinfo.RightAscension, tinfo.Declination);
            }

            for (int i = 0; i < 3; i++)
            {
                lock (SyncRoot) { _step = i + 1; }
                await BroadcastStatusAsync();

                // 1. Capture — INDI-direct FITS exposure. The old
                // CameraMediator.Capture path never populates image data in
                // this headless architecture (LatestImageData is only written
                // by the INDI capture pipeline), so PA could not actually
                // complete a single point before this change.
                var tempImage = await CaptureToTempAsync(_paExposureOverride ?? 4.0, token);

                // 2. Solve — hint with where we actually SLEWED to, not the
                // previous measurement (that hint was a fixed 15° off and
                // could fall outside the near-solve search radius).
                var focalLength = ResolveFocalLength();
                var pixelSize = _state.CameraInfo?.PixelSize ?? 3.76;
                var solve = await _solver.SolveAsync(tempImage, focalLength, pixelSize,
                    i == 0 ? leg1HintRa : nextHintRa ?? double.NaN,
                    i == 0 ? leg1HintDec : decPts[i - 1]);

                if (!solve.Success)
                {
                    // Blind fallback on EVERY leg, not just the first: a bad
                    // hint (mount pointing model, wrong profile focal length)
                    // shouldn't abort the routine when a full search can
                    // still recover the frame.
                    solve = await _solver.SolveAsync(tempImage, focalLength, pixelSize);
                }

                TryDeleteTemp(tempImage);
                if (!solve.Success) throw new Exception($"Solving failed at step {i + 1}");

                // ASTAP solves against a J2000 catalog, but everything below
                // (LST, hour angle, alt/az, the pole itself) is epoch-of-date.
                // Feeding J2000 into JNOW math shifted the derived pole by the
                // accumulated precession (~9′ in 2026) — the same order as the
                // errors being measured.
                var jnow = ToJnow(solve.Coordinates.RA, solve.Coordinates.Dec);
                raPts[i] = jnow.RaHours;
                decPts[i] = jnow.DecDeg;
                Console.WriteLine($"[PolarAlign] leg {i + 1} solved: J2000 {solve.Coordinates.RA:F4}h {solve.Coordinates.Dec:F4}d -> JNOW {jnow.RaHours:F4}h {jnow.DecDeg:F4}d");

                if (i == 0)
                {
                    // Pick the direction that moves |HA| AWAY from zero.
                    double lstNow = PolarAlignmentMath.LocalSiderealTimeHours(DateTime.UtcNow, longitude);
                    double ha1 = PolarAlignmentMath.HourAngleHours(lstNow, raPts[0]);
                    // West of meridian (HA>0): decreasing RA increases HA.
                    slewDirHours = ha1 >= 0 ? -1.0 : 1.0;
                }

                // 3. Slew RA by 1 hour (15°) away from the meridian for the next point
                if (i < 2)
                {
                    var newRa = raPts[i] + slewDirHours;
                    if (newRa >= 24.0) newRa -= 24.0;
                    if (newRa < 0) newRa += 24.0;
                    nextHintRa = newRa;
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
                double lst = PolarAlignmentMath.LocalSiderealTimeHours(nowUtc, longitude);
                double hourAngleDeg = PolarAlignmentMath.HourAngleHours(lst, raPts[2]) * 15.0;
                _baseAlt3 = PolarAlignmentMath.AltitudeDeg(hourAngleDeg, latitude, decPts[2]);
                _baseAz3 = PolarAlignmentMath.AzimuthDeg(hourAngleDeg, latitude, decPts[2]);
                
                _completed = true;
            }

            await BroadcastStatusAsync();

            // The knob-delta model below compares the CURRENT alt/az of the
            // pointing with point 3's alt/az. That subtraction only isolates
            // knob movement if the mount is frozen in hour angle — with
            // tracking ON the solved RA stays fixed while LST advances, so
            // alt/az drifts up to ~15′/min with NO knob input and the
            // displayed error walks away on its own. Stop tracking for the
            // adjustment phase; the pole region barely trails in 2 s subs.
            await TrySetTrackingAsync(false, token);

            // 5. Continuous Live Update Loop (SignalR)
            double hintRa = raPts[2], hintDec = decPts[2];
            int consecutiveFailures = 0;
            const int maxConsecutiveFailures = 8;
            while (!token.IsCancellationRequested)
            {
                var tempImage = await CaptureToTempAsync(2.0, token);

                // Fast local solve around the LAST SUCCESSFUL position —
                // with tracking off the field drifts ~15°/h in RA, so a fixed
                // point-3 hint would eventually fall out of the search radius.
                var focalLength = ResolveFocalLength();
                var pixelSize = _state.CameraInfo?.PixelSize ?? 3.76;
                var solve = await _solver.SolveAsync(tempImage, focalLength, pixelSize, hintRa, hintDec);
                TryDeleteTemp(tempImage);

                if (solve.Success)
                {
                    consecutiveFailures = 0;
                    // Hints stay in J2000 — they go straight back to ASTAP.
                    hintRa = solve.Coordinates.RA;
                    hintDec = solve.Coordinates.Dec;

                    // Math runs epoch-of-date (see measurement loop).
                    var jnowLive = ToJnow(solve.Coordinates.RA, solve.Coordinates.Dec);
                    DateTime nowUtc = DateTime.UtcNow;
                    double lst = PolarAlignmentMath.LocalSiderealTimeHours(nowUtc, longitude);
                    double hourAngleDeg = PolarAlignmentMath.HourAngleHours(lst, jnowLive.RaHours) * 15.0;
                    double curAlt = PolarAlignmentMath.AltitudeDeg(hourAngleDeg, latitude, jnowLive.DecDeg);
                    double curAz = PolarAlignmentMath.AzimuthDeg(hourAngleDeg, latitude, jnowLive.DecDeg);

                    // How much did the physical knobs move the scope?
                    double deltaAltArgMin = (curAlt - _baseAlt3) * 60.0;
                    double deltaAzArcMin = (curAz - _baseAz3) * 60.0;

                    // Remaining Error
                    double newAltError = _baseError.AltitudeErrorArcMin - deltaAltArgMin;
                    double newAzError = _baseError.AzimuthErrorArcMin - deltaAzArcMin;
                    double newTotalError = Math.Sqrt(newAltError * newAltError + newAzError * newAzError);

                    var (altHint, azHint) = PolarAlignmentMath.KnobHints(newAltError, newAzError, latitude);
                    var liveUpdate = new
                    {
                        altitudeErrorArcMin = Math.Round(newAltError, 2),
                        azimuthErrorArcMin = Math.Round(newAzError, 2),
                        totalErrorArcMin = Math.Round(newTotalError, 2),
                        altitudeCorrectionHint = altHint,
                        azimuthCorrectionHint = azHint
                    };

                    lock (SyncRoot) { _latestLiveUpdate = liveUpdate; }
                    _eventBus.Broadcast("PolarAlignmentLiveUpdate", liveUpdate);
                }
                else
                {
                    consecutiveFailures++;
                    if (consecutiveFailures >= maxConsecutiveFailures)
                        throw new Exception($"Live solving failed {maxConsecutiveFailures} times in a row (clouds, obstruction, or the field drifted out of range)");
                }

                // Pace the loop — back-to-back 2 s subs + ASTAP runs hammer
                // the camera and CPU with no benefit at hand-adjustment speed.
                await Task.Delay(1200, token);
            }
        }
        catch (OperationCanceledException)
        {
            // User-initiated stop — not an error.
            lock (SyncRoot) { _running = false; }
            await BroadcastStatusAsync();
        }
        catch (Exception ex)
        {
            // Abort
            lock (SyncRoot) { _running = false; _completed = false; _lastError = ex.Message; }
            await BroadcastStatusAsync();
            _eventBus.Broadcast("PolarAlignmentError", $"Routine failed or was aborted: {ex.Message}");
        }
    }

    /// <summary>INDI-direct exposure to a temp FITS file for ASTAP.</summary>
    private async Task<string> CaptureToTempAsync(double exposureSeconds, CancellationToken token)
    {
        var selected = _equipment.GetSelected(DeviceKind.Camera);
        if (selected?.Provider != EquipmentProvider.Indi || !_equipment.IsConnected(DeviceKind.Camera))
            throw new InvalidOperationException("No INDI camera connected for polar-alignment capture");

        var (bytes, format) = await _indi.CameraExposeAsync(selected.UniqueId, exposureSeconds, token);
        if (bytes == null || bytes.Length == 0)
            throw new InvalidOperationException("Camera returned no image data");

        var ext = string.IsNullOrEmpty(format) ? "fits" : format.TrimStart('.');
        var tempDir = PlatformPaths.IsLinux && Directory.Exists("/dev/shm")
            ? "/dev/shm" : Path.GetTempPath();
        var tempImage = Path.Combine(tempDir, $"pa_{Guid.NewGuid()}.{ext}");
        await System.IO.File.WriteAllBytesAsync(tempImage, bytes, token);
        return tempImage;
    }

    /// <summary>J2000 (plate-solve frame) → epoch-of-date for LST/alt-az math.
    /// NINA's Coordinates.Transform needs the native SOFA library, which the
    /// bundle only carries for win/linux — on macOS its type initializer
    /// throws, so we fall back to a manual IAU-1976 precession (arcsecond
    /// accuracy; the errors measured here are arcminutes).</summary>
    private static bool _sofaBroken;
    private static (double RaHours, double DecDeg) ToJnow(double raHours, double decDeg)
    {
        if (!_sofaBroken)
        {
            try
            {
                var c = new Coordinates(raHours, decDeg, Epoch.J2000, Coordinates.RAType.Hours).Transform(Epoch.JNOW);
                return (c.RA, c.Dec);
            }
            catch (TypeInitializationException) { _sofaBroken = true; }
        }
        return Precess(raHours, decDeg, toDate: true);
    }

    /// <summary>JNOW → J2000 for ASTAP hints (inverse of <see cref="ToJnow"/>).</summary>
    private static (double RaHours, double DecDeg) ToJ2000(double raHours, double decDeg)
    {
        if (!_sofaBroken)
        {
            try
            {
                var c = new Coordinates(raHours, decDeg, Epoch.JNOW, Coordinates.RAType.Hours).Transform(Epoch.J2000);
                return (c.RA, c.Dec);
            }
            catch (TypeInitializationException) { _sofaBroken = true; }
        }
        return Precess(raHours, decDeg, toDate: false);
    }

    /// <summary>IAU-1976 precession (ζ, z, θ rotation chain), J2000 ↔ date.</summary>
    private static (double RaHours, double DecDeg) Precess(double raHours, double decDeg, bool toDate)
    {
        var jd = PolarAlignmentMath.JulianDate(DateTime.UtcNow);
        var t = (jd - 2451545.0) / 36525.0;
        var zeta = (2306.2181 * t + 0.30188 * t * t) / 3600.0 * Math.PI / 180.0;
        var z = (2306.2181 * t + 1.09468 * t * t) / 3600.0 * Math.PI / 180.0;
        var theta = (2004.3109 * t - 0.42665 * t * t) / 3600.0 * Math.PI / 180.0;

        var ra = raHours * 15.0 * Math.PI / 180.0;
        var dec = decDeg * Math.PI / 180.0;
        var v = new[] { Math.Cos(dec) * Math.Cos(ra), Math.Cos(dec) * Math.Sin(ra), Math.Sin(dec) };

        double[] Rz(double a, double[] u) => new[]
        {
            Math.Cos(a) * u[0] + Math.Sin(a) * u[1],
            -Math.Sin(a) * u[0] + Math.Cos(a) * u[1],
            u[2]
        };
        double[] Ry(double a, double[] u) => new[]
        {
            Math.Cos(a) * u[0] - Math.Sin(a) * u[2],
            u[1],
            Math.Sin(a) * u[0] + Math.Cos(a) * u[2]
        };

        // J2000→date: Rz(−z)·Ry(θ)·Rz(−ζ); date→J2000 is the transpose chain.
        var w = toDate
            ? Rz(-z, Ry(theta, Rz(-zeta, v)))
            : Rz(zeta, Ry(-theta, Rz(z, v)));

        var outDec = Math.Asin(Math.Clamp(w[2], -1, 1)) * 180.0 / Math.PI;
        var outRa = Math.Atan2(w[1], w[0]) * 180.0 / Math.PI;
        if (outRa < 0) outRa += 360;
        return (outRa / 15.0, outDec);
    }

    private static void TryDeleteTemp(string path)
    {
        // Temp frames leaked into /dev/shm (RAM!) or tmp on every solve.
        try { System.IO.File.Delete(path); } catch { }
    }

    /// <summary>Best-effort tracking toggle via the INDI selection — polar
    /// alignment must not die just because a driver lacks TRACK_STATE.</summary>
    private async Task TrySetTrackingAsync(bool enabled, CancellationToken ct)
    {
        try
        {
            var selected = _equipment.GetSelected(DeviceKind.Telescope);
            if (selected?.Provider == EquipmentProvider.Indi)
                await _indi.TelescopeTrackingAsync(selected.UniqueId, enabled, ct);
        }
        catch
        {
            // Non-fatal: measurement still works, the live phase just keeps
            // the tracking caveat. The math comment documents the tradeoff.
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
            baseError = _baseError,
            lastError = _lastError
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
                baseError = _baseError,
                // Why the last run died — HTTP pollers previously only saw
                // running:false with no reason (the reason was SignalR-only).
                lastError = _lastError
            });
        }
    }

    [HttpPost("stop")]
    public async Task<IActionResult> Stop()
    {
        lock (SyncRoot)
        {
            _runCts?.Cancel();
            _running = false;
        }

        // The live phase turned tracking off (required by the knob-delta
        // math); hand the mount back tracking like TPPA does, so the user
        // can go straight to framing/imaging.
        await TrySetTrackingAsync(true, HttpContext.RequestAborted);

        return Ok(new { success = true, message = "Polar alignment stopped (tracking re-enabled)" });
    }
}
