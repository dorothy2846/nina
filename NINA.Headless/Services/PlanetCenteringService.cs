using System.Collections.Concurrent;
using SixLabors.ImageSharp.Advanced;

namespace NINA.Headless.Services;

/// <summary>
/// BETA: keeps a planet centered during long high-magnification video runs by
/// pulse-guiding the mount on the planet's own centroid — the same idea as
/// guiding on a star, with the planet disk as the guide object. This is the
/// INDI-compatible answer to FireCapture's AutoAlign: sensor-ROI tracking is
/// impossible mid-clip on INDI (ROI changes cycle the stream), but mount
/// pulses work while recording.
///
/// Honesty constraints baked in:
///  - REFUSES to run without a successful calibration (two measured pulses
///    build the pixel↔pulse matrix; no direction guessing, ever)
///  - Centroid requires a real disk (≥25 px above threshold) — "no planet
///    found" is reported, not silently tolerated
///  - Seeing jitter is filtered with a median over 3 samples and a deadband;
///    every pulse issued is counted and visible in status
/// </summary>
public class PlanetCenteringService
{
    public enum Phase { Idle, Calibrating, Centering, Failed }

    public sealed record Centroid(double X, double Y, int PixelCount, int Width, int Height);

    public sealed record Status(
        string Phase, string? Message,
        double? ErrorPx, double? CentroidX, double? CentroidY,
        int PulsesIssued, double? CalEastDxPerSec, double? CalEastDyPerSec,
        double? CalNorthDxPerSec, double? CalNorthDyPerSec);

    private readonly CameraStreamService _stream;
    private readonly IndiDiscoveryService _indi;
    private readonly EquipmentSelectionService _equipment;
    private readonly ILogger<PlanetCenteringService> _log;

    private readonly object _lock = new();
    private Phase _phase = Phase.Idle;
    private string? _message;
    private CancellationTokenSource? _cts;
    private double? _lastErrorPx, _lastCx, _lastCy;
    private int _pulses;

    // Calibration matrix: pixel displacement per second of pulse, per axis.
    private double _eDx, _eDy, _nDx, _nDy;
    private bool _calibrated;

    // Tuning
    private const int CalPulseMs = 1500;
    private const double DeadbandPx = 30;
    private const int MaxPulseMs = 800;
    private const int MinDiskPixels = 25;

    public PlanetCenteringService(CameraStreamService stream, IndiDiscoveryService indi,
                                  EquipmentSelectionService equipment, ILogger<PlanetCenteringService> log)
    {
        _stream = stream;
        _indi = indi;
        _equipment = equipment;
        _log = log;
    }

    public Status GetStatus()
    {
        lock (_lock)
        {
            return new Status(_phase.ToString(), _message, _lastErrorPx, _lastCx, _lastCy, _pulses,
                _calibrated ? _eDx : null, _calibrated ? _eDy : null,
                _calibrated ? _nDx : null, _calibrated ? _nDy : null);
        }
    }

    public bool Start(out string? error)
    {
        lock (_lock)
        {
            if (_phase is Phase.Calibrating or Phase.Centering) { error = "이미 실행 중입니다"; return false; }
            if (!_stream.IsRunning) { error = "스트림이 꺼져 있습니다 — 라이브 영상을 먼저 시작하세요"; return false; }
            var mount = _equipment.GetSelected(DeviceKind.Telescope);
            if (mount == null || !_equipment.IsConnected(DeviceKind.Telescope))
            { error = "마운트가 연결되어 있지 않습니다"; return false; }

            _phase = Phase.Calibrating;
            _message = "캘리브레이션 준비";
            _pulses = 0;
            _calibrated = false;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _ = Task.Run(() => RunAsync(mount.UniqueId, token), token);
            error = null;
            return true;
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _cts?.Cancel();
            _phase = Phase.Idle;
            _message = "중지됨";
        }
    }

    private async Task RunAsync(string mountId, CancellationToken ct)
    {
        try
        {
            // ---- Calibration: measure how E and N pulses move the centroid.
            SetState(Phase.Calibrating, "기준 위치 측정 중");
            var origin = await MedianCentroidAsync(3, ct)
                ?? throw new Exception("행성을 찾을 수 없습니다 (밝은 원반 없음)");

            SetState(Phase.Calibrating, "동쪽 펄스 측정 중");
            var afterE = await PulseAndMeasureAsync(mountId, "E", CalPulseMs, ct)
                ?? throw new Exception("동쪽 펄스 후 행성을 놓쳤습니다");
            _eDx = (afterE.X - origin.X) / (CalPulseMs / 1000.0);
            _eDy = (afterE.Y - origin.Y) / (CalPulseMs / 1000.0);

            SetState(Phase.Calibrating, "북쪽 펄스 측정 중");
            var afterN = await PulseAndMeasureAsync(mountId, "N", CalPulseMs, ct)
                ?? throw new Exception("북쪽 펄스 후 행성을 놓쳤습니다");
            _nDx = (afterN.X - afterE.X) / (CalPulseMs / 1000.0);
            _nDy = (afterN.Y - afterE.Y) / (CalPulseMs / 1000.0);

            // Matrix must be invertible: both axes must actually move the
            // image, and not along the same line.
            var det = _eDx * _nDy - _eDy * _nDx;
            var eMag = Math.Sqrt(_eDx * _eDx + _eDy * _eDy);
            var nMag = Math.Sqrt(_nDx * _nDx + _nDy * _nDy);
            if (eMag < 2 || nMag < 2 || Math.Abs(det) < 0.05 * eMag * nMag)
                throw new Exception($"캘리브레이션 실패 — 펄스가 화면을 충분히 움직이지 못했습니다 (E {eMag:F1}px/s, N {nMag:F1}px/s). 가이드 속도를 확인하세요");

            _calibrated = true;
            _log.LogInformation("PlanetCentering: calibrated E=({EDx:F1},{EDy:F1}) N=({NDx:F1},{NDy:F1}) px/s",
                _eDx, _eDy, _nDx, _nDy);

            // ---- Centering loop.
            SetState(Phase.Centering, "센터링 동작 중");
            while (!ct.IsCancellationRequested)
            {
                var c = await MedianCentroidAsync(3, ct);
                if (c == null)
                {
                    SetState(Phase.Centering, "행성 상실 — 재검출 대기");
                    await Task.Delay(2000, ct);
                    continue;
                }

                var errX = c.X - c.Width / 2.0;
                var errY = c.Y - c.Height / 2.0;
                var errMag = Math.Sqrt(errX * errX + errY * errY);
                lock (_lock) { _lastErrorPx = errMag; _lastCx = c.X; _lastCy = c.Y; }

                if (errMag > DeadbandPx)
                {
                    // Solve M · [tE, tN] = -err  (t in seconds).
                    var det2 = _eDx * _nDy - _eDy * _nDx;
                    var tE = (-errX * _nDy - -errY * _nDx) / det2;
                    var tN = (_eDx * -errY - _eDy * -errX) / det2;

                    await IssuePulseAsync(mountId, tE, "E", "W", ct);
                    await IssuePulseAsync(mountId, tN, "N", "S", ct);
                    SetState(Phase.Centering, $"보정 펄스 ({errMag:F0}px 오차)");
                }
                else
                {
                    SetState(Phase.Centering, $"센터 유지 중 ({errMag:F0}px)");
                }

                await Task.Delay(2500, ct);
            }
        }
        catch (OperationCanceledException) { /* stopped */ }
        catch (Exception ex)
        {
            _log.LogWarning("PlanetCentering failed: {Error}", ex.Message);
            SetState(Phase.Failed, ex.Message);
        }
    }

    private async Task IssuePulseAsync(string mountId, double seconds, string positiveDir, string negativeDir, CancellationToken ct)
    {
        var ms = (int)Math.Round(Math.Abs(seconds) * 1000);
        if (ms < 30) return;                       // below mount response floor
        ms = Math.Min(ms, MaxPulseMs);             // never over-correct in one go
        var dir = seconds >= 0 ? positiveDir : negativeDir;
        await _indi.TelescopePulseGuideAsync(mountId, dir, ms, ct);
        lock (_lock) { _pulses++; }
        // Wait out the pulse so the next measurement isn't mid-motion.
        await Task.Delay(ms + 200, ct);
    }

    private async Task<Centroid?> PulseAndMeasureAsync(string mountId, string dir, int ms, CancellationToken ct)
    {
        await _indi.TelescopePulseGuideAsync(mountId, dir, ms, ct);
        lock (_lock) { _pulses++; }
        await Task.Delay(ms + 700, ct);            // settle
        return await MedianCentroidAsync(3, ct);
    }

    /// <summary>Median of N centroid samples ~400 ms apart — seeing-jitter
    /// filter. Null if the planet isn't reliably detected.</summary>
    private async Task<Centroid?> MedianCentroidAsync(int samples, CancellationToken ct)
    {
        var xs = new List<double>();
        var ys = new List<double>();
        Centroid? last = null;
        for (var i = 0; i < samples; i++)
        {
            var frame = _stream.LatestFrame;
            if (frame != null)
            {
                var c = ComputeCentroid(frame.Value.Jpeg);
                if (c != null) { xs.Add(c.X); ys.Add(c.Y); last = c; }
            }
            if (i < samples - 1) await Task.Delay(400, ct);
        }
        if (xs.Count < (samples + 1) / 2 || last == null) return null;
        xs.Sort(); ys.Sort();
        return new Centroid(xs[xs.Count / 2], ys[ys.Count / 2], last.PixelCount, last.Width, last.Height);
    }

    /// <summary>Threshold centroid of the brightest object. Shared verbatim
    /// with the /planetary-center/test endpoint so what we test IS what runs.
    /// Requires ≥ MinDiskPixels above threshold — a hot pixel or noise floor
    /// never passes as a "planet".</summary>
    public static Centroid? ComputeCentroid(byte[] jpegBytes)
    {
        try
        {
            using var img = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.L8>(jpegBytes);
            // Robust peak (99.99th percentile) for the threshold reference.
            var bins = new int[256];
            long count = 0;
            for (int y = 0; y < img.Height; y += 2)
            {
                var row = img.DangerousGetPixelRowMemory(y).Span;
                for (int x = 0; x < row.Length; x += 2) { bins[row[x].PackedValue]++; count++; }
            }
            if (count == 0) return null;
            long acc = 0; int robustPeak = 0;
            long thresholdCount = (long)(count * 0.9999);
            for (int v = 0; v < 256; v++) { acc += bins[v]; if (acc >= thresholdCount) { robustPeak = v; break; } }

            var threshold = Math.Max(16, (int)(robustPeak * 0.4));
            double sx = 0, sy = 0, mass = 0;
            int pixels = 0;
            for (int y = 0; y < img.Height; y++)
            {
                var row = img.DangerousGetPixelRowMemory(y).Span;
                for (int x = 0; x < row.Length; x++)
                {
                    int v = row[x].PackedValue;
                    if (v >= threshold)
                    {
                        sx += (double)x * v;
                        sy += (double)y * v;
                        mass += v;
                        pixels++;
                    }
                }
            }
            if (pixels < MinDiskPixels || mass <= 0) return null;
            return new Centroid(sx / mass, sy / mass, pixels, img.Width, img.Height);
        }
        catch { return null; }
    }

    private void SetState(Phase phase, string message)
    {
        lock (_lock) { _phase = phase; _message = message; }
    }
}
