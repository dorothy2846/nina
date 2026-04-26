using System.Diagnostics;

namespace NINA.Headless.Services;

/// <summary>
/// Auto-recovery for hung INDI driver processes.
///
/// Failure mode this targets: an `indi_*` driver child of indiserver gets
/// stuck in init or in a tight USB/SDK loop, burning CPU forever without
/// publishing INDI properties. The user previously had to inspect `ps` and
/// `kill -9 &lt;pid&gt;` by hand (PlayerOne SDK lockup, 28 min of pinned CPU).
///
/// Heuristic: a driver process running for &gt;60 s, sustaining &gt;15 % CPU
/// across 4 consecutive 30 s samples (= 2 min), while no INDI camera is
/// actively streaming, is treated as wedged and SIGKILL'd. indiserver's
/// default supervision policy re-spawns dead children, so the driver
/// reloads cleanly without our touching the FIFO.
///
/// Why those numbers:
///  - 60 s startup grace covers slow USB enumeration + driver init burst.
///  - 15 % CPU is well above an idle driver (&lt;1 %) and well below an
///    active CCD_VIDEO_STREAM driver (typically 30–80 %).
///  - 4 samples avoids killing a driver in the middle of a brief but
///    legitimate task (downloading a long exposure, autofocus sweep).
///  - Streaming check exempts the only legitimate sustained-CPU case we
///    have today; if more emerge (long PHD2 run, etc.) widen the carve-out.
/// </summary>
public class DriverWatchdogService : BackgroundService
{
    private readonly CameraStreamService _stream;
    private readonly H264Transcoder _h264;
    private readonly ILogger<DriverWatchdogService> _log;

    private const double HighCpuPercent = 15.0;
    private const int SamplesNeededToKill = 4;     // 4 × 30 s = 2 min sustained
    private const int MinAliveSecondsBeforeJudging = 60;
    // ffmpeg is silent → restart threshold. Encoder normally produces frames
    // every 100–200 ms while CameraStreamService is feeding it (the keepalive
    // loop guarantees a JPEG every 250 ms even when BLOBs go idle), so 10 s
    // of silence means the child is wedged on its input pipe.
    private static readonly TimeSpan TranscoderStallThreshold = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    // Per-PID rolling CPU history. Keys drop out when the process disappears
    // (indiserver respawned + assigned a new pid) so we don't accumulate junk.
    private readonly Dictionary<int, Queue<double>> _cpuHistory = new();

    public DriverWatchdogService(CameraStreamService stream, H264Transcoder h264, ILogger<DriverWatchdogService> log)
    {
        _stream = stream;
        _h264 = h264;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Don't fire immediately on boot — let indiserver come up + drivers
        // settle. Skips a noisy false alarm on every server start.
        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { TickOnce(); }
            catch (Exception ex) { _log.LogWarning(ex, "DriverWatchdog tick failed"); }
            try { await Task.Delay(TickInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void TickOnce()
    {
        CheckTranscoder();

        var samples = SnapshotIndiDrivers();
        var seenPids = new HashSet<int>();

        // Streaming exemption: if any camera is actively in CCD_VIDEO_STREAM=On,
        // its driver is allowed to burn CPU. We treat the carve-out as global
        // (all drivers spared) because the cost of a false negative is just
        // "we don't auto-recover this minute" while the cost of a false positive
        // is killing a legitimate stream mid-frame.
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

        // Drop history for pids that no longer exist (indiserver respawned).
        foreach (var stale in _cpuHistory.Keys.Where(k => !seenPids.Contains(k)).ToList())
            _cpuHistory.Remove(stale);
    }

    /// <summary>Restart ffmpeg if it claims to be running but has stopped
    /// emitting IVF frames despite the camera-stream pipeline being active.
    /// Skips when streaming is idle (no BLOB input → no expected output) or
    /// the encoder hasn't produced its first frame yet (cold start grace).
    /// </summary>
    private void CheckTranscoder()
    {
        if (!_h264.IsRunning) return;
        if (!_stream.IsRunning) return;
        var lastFrame = _h264.LastFrameAt;
        if (lastFrame == DateTime.MinValue) return; // never produced — let cold start ride
        var silence = DateTime.UtcNow - lastFrame;
        if (silence < TranscoderStallThreshold) return;
        _log.LogWarning("DriverWatchdog: ffmpeg silent for {Silence}s while stream is active — restarting transcoder",
            (int)silence.TotalSeconds);
        try { _h264.RestartAsync().GetAwaiter().GetResult(); }
        catch (Exception ex) { _log.LogWarning(ex, "DriverWatchdog: ffmpeg restart failed"); }
    }

    private IReadOnlyList<DriverSample> SnapshotIndiDrivers()
    {
        // ps gives the cleanest cross-platform-ish read of (pid, etime, %cpu, command).
        // BSD-style ps on macOS and procps on Linux both honor `-o pid=,etime=,pcpu=,comm=`.
        // We filter to processes whose comm starts with "indi_" — covers all driver
        // binaries (indi_playerone_ccd, indi_asi_ccd, indi_lx200am5, etc.) and skips
        // indiserver itself (named "indiserver").
        var psi = new ProcessStartInfo
        {
            FileName = "/bin/ps",
            Arguments = "-axo pid=,etime=,pcpu=,comm=",
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        var p = Process.Start(psi);
        if (p == null) return Array.Empty<DriverSample>();
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(2000);

        var result = new List<DriverSample>();
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            // pid etime pcpu comm — split on whitespace, but `etime` can be
            // "MM:SS", "HH:MM:SS", or "DD-HH:MM:SS"; comm is the trailing token.
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4) continue;
            if (!int.TryParse(parts[0], out var pid)) continue;
            var elapsed = ParseEtime(parts[1]);
            if (!double.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out var cpu)) continue;
            var comm = parts[parts.Length - 1];
            if (!comm.StartsWith("indi_", StringComparison.Ordinal)) continue;

            result.Add(new DriverSample(pid, elapsed, cpu, comm));
        }
        return result;
    }

    /// <summary>Parses the BSD-style `etime` formats: "MM:SS", "HH:MM:SS", or
    /// "DD-HH:MM:SS". Returns total elapsed seconds. Bad input → 0 (treated as
    /// "just started", which means the watchdog won't act on it).</summary>
    private static int ParseEtime(string s)
    {
        try
        {
            int days = 0;
            var rest = s;
            var dashIx = s.IndexOf('-');
            if (dashIx > 0)
            {
                days = int.Parse(s.Substring(0, dashIx));
                rest = s.Substring(dashIx + 1);
            }
            var parts = rest.Split(':');
            int h, m, sec;
            if (parts.Length == 3)
            {
                h = int.Parse(parts[0]);
                m = int.Parse(parts[1]);
                sec = int.Parse(parts[2]);
            }
            else if (parts.Length == 2)
            {
                h = 0;
                m = int.Parse(parts[0]);
                sec = int.Parse(parts[1]);
            }
            else return 0;
            return ((days * 24 + h) * 60 + m) * 60 + sec;
        }
        catch { return 0; }
    }

    private record DriverSample(int Pid, int ElapsedSeconds, double CpuPercent, string Command);
}
