using System.Diagnostics;

namespace NINA.Headless.Services;

/// <summary>
/// BMP → H.264 transcoder using a long-lived ffmpeg subprocess. Producer hands
/// raw RGB frames wrapped in a BMP header to <see cref="PushAsync"/>; ffmpeg
/// emits an Annex B H.264 bit-stream on stdout, which the reader loop splits
/// on NAL unit start codes and fans out via <see cref="NaluReady"/>.
///
/// Why ffmpeg-over-pipe instead of SIPSorceryMedia.FFmpeg: host runs FFmpeg 8
/// while the bindings track FFmpeg 4/5 ABI, and one long-lived process amortises
/// the per-frame encoder startup cost to zero.
/// </summary>
public class H264Transcoder : IAsyncDisposable
{
    private readonly ILogger<H264Transcoder> _log;
    private Process? _proc;
    private Stream? _stdin;
    private Task? _readerTask;
    private CancellationTokenSource? _readerCts;
    private readonly object _lock = new();
    private long _lastFrameTicks;
    private long _lastPushTicks;
    private long _startedAtTicks;
    private int _restartCount;
    private int _lastTargetFps;
    private int _lastCrf;

    public H264Transcoder(ILogger<H264Transcoder> log) { _log = log; }

    public bool IsRunning => _proc != null && !_proc.HasExited;

    /// <summary>UTC time of the most recent NAL emitted by ffmpeg, or
    /// <see cref="DateTime.MinValue"/> until the first one lands. Watchdog
    /// reads this; a too-old value while the encoder claims to be running
    /// indicates ffmpeg has wedged on input.</summary>
    public DateTime LastFrameAt
    {
        get
        {
            var ticks = Volatile.Read(ref _lastFrameTicks);
            return ticks == 0 ? DateTime.MinValue : new DateTime(ticks, DateTimeKind.Utc);
        }
    }

    /// <summary>UTC time of the most recent frame successfully written to
    /// ffmpeg stdin, or <see cref="DateTime.MinValue"/> until the first one.
    /// Watchdog input-side twin of <see cref="LastFrameAt"/>: output silence
    /// only implicates ffmpeg when input is actually flowing.</summary>
    private long _lastPushAttemptTicks;
    public DateTime LastPushAttemptAt
    {
        get { var t = Volatile.Read(ref _lastPushAttemptTicks); return t == 0 ? DateTime.MinValue : new DateTime(t, DateTimeKind.Utc); }
    }

    public DateTime LastPushAt
    {
        get
        {
            var ticks = Volatile.Read(ref _lastPushTicks);
            return ticks == 0 ? DateTime.MinValue : new DateTime(ticks, DateTimeKind.Utc);
        }
    }

    /// <summary>UTC time the current ffmpeg child was spawned, or
    /// <see cref="DateTime.MinValue"/> when not running. First-frame deadline
    /// anchor: after Start/Restart, <see cref="LastFrameAt"/> is empty and
    /// silence must be measured from here.</summary>
    public DateTime StartedAt
    {
        get
        {
            var ticks = Volatile.Read(ref _startedAtTicks);
            return ticks == 0 ? DateTime.MinValue : new DateTime(ticks, DateTimeKind.Utc);
        }
    }

    /// <summary>Total watchdog-driven restarts since process start. Surfaced
    /// in /camera/stream/status so a flapping encoder is visible, not silent.</summary>
    public int RestartCount => Volatile.Read(ref _restartCount);

    /// <summary>Invoked from the reader thread for every complete NAL unit, sans start code.
    /// First byte is the standard H.264 NAL header (forbidden-zero-bit + nal_ref_idc + nal_unit_type).
    /// Listeners must be quick; blocking here stalls the encoder's output pipe.</summary>
    public event Action<byte[]>? NaluReady;

    /// <summary>Wall-clock at the moment ffmpeg emits a NAL. Paired FIFO with
    /// <see cref="PushAsync"/> calls, this gives encode latency
    /// (T_emit - T_push). CameraStreamService consumes it for frame timing.</summary>
    public event Action<DateTime>? NaluEmitted;
    private long _naluCount;

    public void Start(int targetFps, int crf)
    {
        lock (_lock)
        {
            _lastTargetFps = targetFps;
            _lastCrf = crf;
            if (IsRunning) return;

            // Use ArgumentList (not Arguments) so each token is passed
            // atomically — ProcessStartInfo.Arguments parses on whitespace
            // and DOES NOT respect single quotes.
            var argList = new[]
            {
                "-fflags", "nobuffer+discardcorrupt",
                "-flags", "low_delay",
                "-probesize", "32",
                "-analyzeduration", "0",
                "-f", "image2pipe",
                "-c:v", "bmp",
                "-avoid_negative_ts", "make_zero",
                "-i", "-",
                // 640 wide keeps the H.264 quality budget where it matters
                // (edges, focus stars) and trims a few ms off per-frame
                // pipeline time vs 720p.
                "-vf", "scale='min(640,iw)':-2",
                "-c:v", "h264_videotoolbox",
                "-realtime", "1",
                "-allow_sw", "1",
                "-pix_fmt", "yuv420p",
                "-profile:v", "high",
                "-b:v", "1800k",
                "-maxrate", "3000k",
                "-bufsize", "3000k",
                // Keyframe every 20 frames ≈ 2 s at 10 fps.
                "-g", "20",
                "-bf", "0",
                "-bsf:v", "dump_extra=freq=keyframe",
                "-fps_mode", "passthrough",
                "-flush_packets", "1",
                "-f", "h264", "pipe:1"
            };

            var psi = new ProcessStartInfo("ffmpeg")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var a in argList) psi.ArgumentList.Add(a);
            _log.LogInformation("H264Transcoder: launching ffmpeg {Args}", string.Join(' ', argList));
            _proc = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg failed to start");
            _stdin = _proc.StandardInput.BaseStream;
            Volatile.Write(ref _startedAtTicks, DateTime.UtcNow.Ticks);

            _readerCts = new CancellationTokenSource();
            _readerTask = Task.Run(() => ReadNaluLoopAsync(_proc.StandardOutput.BaseStream, _readerCts.Token));
            _ = Task.Run(async () =>
            {
                using var err = _proc.StandardError;
                while (!_proc.HasExited)
                {
                    var line = await err.ReadLineAsync();
                    if (line == null) break;
                    _log.LogInformation("ffmpeg: {Line}", line);
                }
            });
        }
    }

    public async Task StopAsync()
    {
        Process? proc;
        CancellationTokenSource? readerCts;
        Task? readerTask;
        lock (_lock)
        {
            proc = _proc;
            readerCts = _readerCts;
            readerTask = _readerTask;
            _proc = null;
            _readerCts = null;
            _readerTask = null;
            _stdin = null;
        }
        if (proc == null) return;

        try { proc.StandardInput.Close(); } catch { }
        try { if (!proc.WaitForExit(500)) proc.Kill(entireProcessTree: true); } catch { }
        try { readerCts?.Cancel(); } catch { }
        if (readerTask != null) { try { await readerTask; } catch { } }
        proc.Dispose();
        Volatile.Write(ref _startedAtTicks, 0);
        _log.LogInformation("H264Transcoder: stopped");
    }

    private int _pushInFlight;
    /// <summary>Feed one BMP-wrapped frame to ffmpeg. Single-flight: if a
    /// previous push is still draining (pipe full / encoder backed up), drop
    /// silently rather than queue. Caller layers an additional count-based
    /// backlog cap so encoder buffering can't grow unbounded.</summary>
    public Task PushAsync(byte[] frameBytes, CancellationToken ct)
    {
        var stdin = _stdin;
        if (stdin == null) return Task.CompletedTask;
        // Attempt timestamp moves even when the slot is busy — the watchdog
        // uses attempts-fresh-but-success-stale to tell "ffmpeg stopped
        // draining stdin" (encoder wedge, restartable) apart from "no frames
        // arriving" (producer stall, not the encoder's fault).
        Volatile.Write(ref _lastPushAttemptTicks, DateTime.UtcNow.Ticks);
        if (Interlocked.CompareExchange(ref _pushInFlight, 1, 0) != 0) return Task.CompletedTask;
        return Task.Run(async () =>
        {
            try
            {
                await stdin.WriteAsync(frameBytes, ct);
                await stdin.FlushAsync(ct);
                Volatile.Write(ref _lastPushTicks, DateTime.UtcNow.Ticks);
            }
            catch (Exception ex) { _log.LogDebug(ex, "H264Transcoder: push failed"); }
            finally { Interlocked.Exchange(ref _pushInFlight, 0); }
        }, ct);
    }

    /// Read an Annex B H.264 NAL stream from ffmpeg stdout. Each NAL unit is
    /// preceded by either `00 00 01` or `00 00 00 01`; we scan, accumulate
    /// the bytes between start codes, and emit via <see cref="NaluReady"/>.
    private async Task ReadNaluLoopAsync(Stream stdout, CancellationToken ct)
    {
        var buf = new byte[64 * 1024];
        var nalBuilder = new System.IO.MemoryStream();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await stdout.ReadAsync(buf.AsMemory(0, buf.Length), ct);
                if (n == 0) return;

                int i = 0;
                while (i < n)
                {
                    int sc = FindStartCode(buf, i, n, out int scLen);
                    if (sc < 0)
                    {
                        nalBuilder.Write(buf, i, n - i);
                        break;
                    }
                    if (sc > i) nalBuilder.Write(buf, i, sc - i);
                    EmitAccumulatedNalu(nalBuilder);
                    i = sc + scLen;
                }
            }
            EmitAccumulatedNalu(nalBuilder);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogWarning(ex, "H264Transcoder: reader loop error"); }
    }

    private static int FindStartCode(byte[] buf, int start, int end, out int scLen)
    {
        scLen = 0;
        for (int j = start; j + 2 < end; j++)
        {
            if (buf[j] == 0 && buf[j + 1] == 0)
            {
                if (buf[j + 2] == 1) { scLen = 3; return j; }
                if (buf[j + 2] == 0 && j + 3 < end && buf[j + 3] == 1) { scLen = 4; return j; }
            }
        }
        return -1;
    }

    private void EmitAccumulatedNalu(System.IO.MemoryStream nalBuilder)
    {
        if (nalBuilder.Length == 0) return;
        var nal = nalBuilder.ToArray();
        nalBuilder.SetLength(0);

        var emittedAt = DateTime.UtcNow;
        Volatile.Write(ref _lastFrameTicks, emittedAt.Ticks);
        byte nalType = (byte)(nal[0] & 0x1F);

        try { NaluReady?.Invoke(nal); }
        catch (Exception ex) { _log.LogDebug(ex, "NaluReady handler threw"); }
        try { NaluEmitted?.Invoke(emittedAt); }
        catch (Exception ex) { _log.LogDebug(ex, "NaluEmitted handler threw"); }

        _naluCount++;
        if (_naluCount <= 6 || _naluCount % 60 == 0)
            _log.LogInformation("ReadNaluLoop: NAL #{N} type={T} size={S}", _naluCount, nalType, nal.Length);
    }

    /// <summary>Stop the current ffmpeg child (if any) and re-spawn with the
    /// last successful Start args. Used by the watchdog when ffmpeg appears
    /// alive but isn't producing frames.</summary>
    public async Task RestartAsync()
    {
        int fps, crf;
        lock (_lock) { fps = _lastTargetFps; crf = _lastCrf; }
        await StopAsync();
        Volatile.Write(ref _lastFrameTicks, 0);
        Volatile.Write(ref _lastPushTicks, 0);
        Volatile.Write(ref _lastPushAttemptTicks, 0);
        Volatile.Write(ref _startedAtTicks, 0);
        Interlocked.Increment(ref _restartCount);
        if (fps > 0) Start(fps, crf);
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
