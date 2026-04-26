using System.Diagnostics;

namespace NINA.Headless.Services;

/// <summary>
/// SIGKILLs an `indi_*` driver child of indiserver if it sustains &gt;15 % CPU
/// across 4 consecutive 30 s samples (= 2 min) while no INDI camera is
/// streaming, and restarts ffmpeg if its IVF output goes silent for &gt;10 s
/// while the stream pipeline is feeding it. indiserver respawns dead
/// drivers automatically; the ffmpeg restart re-runs <see cref="H264Transcoder.Start"/>
/// with the cached args.
///
/// 60 s startup grace covers slow USB enumeration. The streaming carve-out
/// exempts the only legitimate sustained-CPU case today; widen it if more
/// emerge (long PHD2 run, etc.).
/// </summary>
public class DriverWatchdogService : BackgroundService
{
    private readonly CameraStreamService _stream;
    private readonly H264Transcoder _h264;
    private readonly ILogger<DriverWatchdogService> _log;

    private const double HighCpuPercent = 15.0;
    private const int SamplesNeededToKill = 4;
    private const int MinAliveSecondsBeforeJudging = 60;
    private static readonly TimeSpan TranscoderStallThreshold = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    private readonly Dictionary<int, Queue<double>> _cpuHistory = new();

    public DriverWatchdogService(CameraStreamService stream, H264Transcoder h264, ILogger<DriverWatchdogService> log)
    {
        _stream = stream;
        _h264 = h264;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial settle so the first tick doesn't fire while indiserver
        // children are still in their normal init burst.
        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickOnceAsync(); }
            catch (Exception ex) { _log.LogWarning(ex, "DriverWatchdog tick failed"); }
            try { await Task.Delay(TickInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task TickOnceAsync()
    {
        await CheckTranscoderAsync();

        var samples = SnapshotIndiDrivers();
        var seenPids = new HashSet<int>();

        // Streaming exemption is global: cost of a false negative is one
        // missed minute, cost of a false positive is killing a live frame.
        var streamingActive = _stream.IsRunning;

        foreach (var s in samples)
        {
            seenPids.Add(s.Pid);

            if (s.ElapsedSeconds < MinAliveSecondsBeforeJudging) continue;

            if (!_cpuHistory.TryGetValue(s.Pid, out var hist))
            {
                hist = new Queue<double>(SamplesNeededToKill);
                _cpuHistory[s.Pid] = hist;
            }
            hist.Enqueue(s.CpuPercent);
            while (hist.Count > SamplesNeededToKill) hist.Dequeue();

            if (hist.Count < SamplesNeededToKill) continue;
            if (streamingActive) continue;
            if (!hist.All(c => c >= HighCpuPercent)) continue;

            _log.LogWarning(
                "DriverWatchdog: killing wedged driver pid={Pid} cmd={Cmd} elapsed={Elapsed}s cpu={Cpu} (no active stream)",
                s.Pid, s.Command, s.ElapsedSeconds, string.Join("/", hist.Select(c => c.ToString("F0"))));
            try
            {
                using var p = Process.GetProcessById(s.Pid);
                p.Kill();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "DriverWatchdog: kill of pid {Pid} failed", s.Pid);
            }
            _cpuHistory.Remove(s.Pid);
        }

        foreach (var stale in _cpuHistory.Keys.Where(k => !seenPids.Contains(k)).ToList())
            _cpuHistory.Remove(stale);
    }

    private async Task CheckTranscoderAsync()
    {
        if (!_h264.IsRunning) return;
        if (!_stream.IsRunning) return;
        var lastFrame = _h264.LastFrameAt;
        if (lastFrame == DateTime.MinValue) return;
        var silence = DateTime.UtcNow - lastFrame;
        if (silence < TranscoderStallThreshold) return;
        _log.LogWarning("DriverWatchdog: ffmpeg silent for {Silence}s while stream is active — restarting transcoder",
            (int)silence.TotalSeconds);
        try { await _h264.RestartAsync(); }
        catch (Exception ex) { _log.LogWarning(ex, "DriverWatchdog: ffmpeg restart failed"); }
    }

    private IReadOnlyList<DriverSample> SnapshotIndiDrivers()
    {
        // `ps` for %cpu (no portable .NET API for short-window CPU sampling
        // of foreign PIDs); start time we read from .NET to avoid a fragile
        // BSD/procps `etime` parser.
        var psi = new ProcessStartInfo
        {
            FileName = "/bin/ps",
            Arguments = "-axo pid=,pcpu=,comm=",
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        var p = Process.Start(psi);
        if (p == null) return Array.Empty<DriverSample>();
        var output = p.StandardOutput.ReadToEnd();

        var now = DateTime.UtcNow;
        var result = new List<DriverSample>();
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) continue;
            if (!int.TryParse(parts[0], out var pid)) continue;
            if (!double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var cpu)) continue;
            var comm = parts[parts.Length - 1];
            if (!comm.StartsWith("indi_", StringComparison.Ordinal)) continue;

            int elapsed;
            try
            {
                using var proc = Process.GetProcessById(pid);
                elapsed = (int)(now - proc.StartTime.ToUniversalTime()).TotalSeconds;
            }
            catch { continue; }

            result.Add(new DriverSample(pid, elapsed, cpu, comm));
        }
        return result;
    }

    private record DriverSample(int Pid, int ElapsedSeconds, double CpuPercent, string Command);
}
