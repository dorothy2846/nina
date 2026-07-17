using System.Diagnostics;

namespace NINA.Headless.Services;

/// <summary>
/// SIGKILLs an `indi_*` driver child of indiserver if it sustains &gt;15 % CPU
/// across 4 consecutive 30 s samples (= 2 min) while no INDI camera is
/// streaming, and supervises the ffmpeg encoder while the stream is live:
/// dead process or &gt;10 s of output silence with input flowing → restart
/// (re-runs <see cref="H264Transcoder.Start"/> with the cached args); after
/// 3 fruitless restarts the stream is stopped so the failure surfaces.
/// Producer stalls (no frames reaching stdin) are logged but never
/// "fixed" by an encoder restart. indiserver respawns dead drivers
/// automatically.
///
/// 60 s startup grace covers slow USB enumeration. The streaming carve-out
/// exempts the only legitimate sustained-CPU case today; widen it if more
/// emerge (long PHD2 run, etc.).
/// </summary>
public class DriverWatchdogService : BackgroundService
{
    private readonly CameraStreamService _stream;
    private readonly Phd2Service _phd2;
    private readonly H264Transcoder _h264;
    private readonly ILogger<DriverWatchdogService> _log;

    private const double HighCpuPercent = 15.0;
    private const int SamplesNeededToKill = 4;
    private const int MinAliveSecondsBeforeJudging = 60;
    private static readonly TimeSpan TranscoderStallThreshold = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);
    /// <summary>Consecutive failed encoder recoveries before the watchdog
    /// stops flapping and surfaces the failure by stopping the stream.
    /// 3 attempts × 30 s ticks ≈ 90 s of dead encoder.</summary>
    private const int MaxConsecutiveTranscoderRestarts = 3;

    private int _transcoderStrikes;

    private readonly Dictionary<int, Queue<double>> _cpuHistory = new();

    public DriverWatchdogService(CameraStreamService stream, H264Transcoder h264, Phd2Service phd2, ILogger<DriverWatchdogService> log)
    {
        _phd2 = phd2;
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

        // Exemption is global: cost of a false negative is one missed minute,
        // cost of a false positive is killing a live frame — or, worse, the
        // guide camera mid-session: a PHD2 loop of short exposures holds >15%
        // CPU with the live-view stream off, which used to get the guide
        // driver SIGKILLed two minutes into autoguiding.
        var phd2State = _phd2.CurrentAppState;
        var streamingActive = _stream.IsRunning
            || phd2State is "Guiding" or "Calibrating" or "Looping";

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

    /// <summary>
    /// Encoder health, evaluated only while the stream claims to be live.
    /// Three distinct failure shapes, handled in order:
    ///   1. ffmpeg process dead              → respawn (strike)
    ///   2. no frames reaching ffmpeg stdin  → producer stall; restarting the
    ///      encoder can't help, so log honestly and leave the INDI side to
    ///      its own recovery — never mask it with a pointless respawn
    ///   3. input flowing but no NALs out    → ffmpeg wedged; restart (strike)
    /// The old check measured silence only from LastFrameAt, so an encoder
    /// that never produced its FIRST frame — including every post-restart
    /// wedge, since restart resets that timestamp — was invisible forever.
    /// Silence now falls back to the spawn time when no NAL has landed yet.
    /// After <see cref="MaxConsecutiveTranscoderRestarts"/> fruitless
    /// restarts the watchdog stops the stream instead of flapping ffmpeg
    /// forever: the client sees running=false and can act, per the
    /// no-hidden-failures rule.
    /// </summary>
    private async Task CheckTranscoderAsync()
    {
        if (!_stream.IsRunning)
        {
            _transcoderStrikes = 0;
            return;
        }

        var now = DateTime.UtcNow;

        if (_h264.IsRunning)
        {
            var lastPush = _h264.LastPushAt;
            var lastAttempt = _h264.LastPushAttemptAt;
            if (lastPush == DateTime.MinValue || now - lastPush > TranscoderStallThreshold)
            {
                // Frames ARE arriving (fresh attempts) but none complete a push:
                // ffmpeg stopped draining stdin — the classic encoder wedge. The
                // old classifier read only the frozen success clock and called
                // this a producer stall forever, so the most common wedge mode
                // never triggered recovery.
                if (lastAttempt != DateTime.MinValue && now - lastAttempt < TranscoderStallThreshold)
                {
                    _transcoderStrikes++;
                    _log.LogWarning(
                        "DriverWatchdog: frames arriving but none reach ffmpeg (stdin blocked) — encoder wedge, strike {Strikes}",
                        _transcoderStrikes);
                    if (_transcoderStrikes >= 2)
                    {
                        _transcoderStrikes = 0;
                        _log.LogWarning("DriverWatchdog: restarting wedged transcoder");
                        _ = Task.Run(() => _h264.RestartAsync());
                    }
                    return;
                }
                _log.LogWarning(
                    "DriverWatchdog: stream is active but no frames have reached ffmpeg for {Silence}s — upstream producer stall, not restarting the encoder",
                    lastPush == DateTime.MinValue ? "∞" : ((int)(now - lastPush).TotalSeconds).ToString());
                return;
            }

            var lastFrame = _h264.LastFrameAt;
            var silenceAnchor = lastFrame == DateTime.MinValue ? _h264.StartedAt : lastFrame;
            if (silenceAnchor == DateTime.MinValue) return; // start race; judge next tick
            var silence = now - silenceAnchor;
            if (silence < TranscoderStallThreshold)
            {
                _transcoderStrikes = 0;
                return;
            }
            _log.LogWarning("DriverWatchdog: ffmpeg silent for {Silence}s with input flowing (strike {Strike}/{Max})",
                (int)silence.TotalSeconds, _transcoderStrikes + 1, MaxConsecutiveTranscoderRestarts);
        }
        else
        {
            _log.LogWarning("DriverWatchdog: ffmpeg process died while stream is active (strike {Strike}/{Max})",
                _transcoderStrikes + 1, MaxConsecutiveTranscoderRestarts);
        }

        _transcoderStrikes++;
        if (_transcoderStrikes > MaxConsecutiveTranscoderRestarts)
        {
            _log.LogError(
                "DriverWatchdog: encoder still dead after {Max} restarts — stopping the stream so the failure is visible instead of flapping ffmpeg",
                MaxConsecutiveTranscoderRestarts);
            _transcoderStrikes = 0;
            try { await _stream.StopAsync(); }
            catch (Exception ex) { _log.LogWarning(ex, "DriverWatchdog: stream stop failed"); }
            return;
        }

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
        using var p = Process.Start(psi);
        if (p == null) return Array.Empty<DriverSample>();
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(3000);

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
