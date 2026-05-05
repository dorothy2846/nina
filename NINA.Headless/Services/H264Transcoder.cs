using System.Collections.Concurrent;
using System.Diagnostics;

namespace NINA.Headless.Services;

/// <summary>
/// MJPEG → H.264 transcoder using a long-lived ffmpeg subprocess. Feeds camera-driver JPEG
/// frames into stdin, reads the H.264 Annex B bit-stream from stdout, splits on NAL unit
/// start codes, and fans each NAL unit out via <see cref="NaluReady"/>.
///
/// Why ffmpeg-over-pipe instead of SIPSorceryMedia.FFmpeg:
///   - Host has FFmpeg 8 installed, binding packages lag at FFmpeg 4/5 ABI.
///   - One process survives per stream; per-frame startup cost is zero.
///   - Same subprocess is reusable for both WebSocket (current) and WebRTC (next step) —
///     the transport layer just consumes <see cref="NaluReady"/> differently.
///
/// Low-latency tuning: ultrafast preset, zerolatency tune, no B-frames, SPS/PPS injected
/// before every keyframe so any decoder that joins late can start rendering as soon as the
/// next IDR arrives.
/// </summary>
public class H264Transcoder : IAsyncDisposable
{
    private readonly ILogger<H264Transcoder> _log;
    private Process? _proc;
    private Stream? _stdin;
    private Task? _readerTask;
    private CancellationTokenSource? _readerCts;
    private readonly object _lock = new();
    // Last-seen ffmpeg output liveness signal. Set on every IVF frame the
    // reader loop pulls, read by the watchdog to spot stalls. UTC ticks +
    // Volatile so the cross-thread read doesn't tear without a lock.
    private long _lastFrameTicks;
    // Cached last-Start args so the watchdog can call Restart() without
    // having to thread the original target fps / crf through every site.
    private int _lastTargetFps;
    private int _lastCrf;

    public H264Transcoder(ILogger<H264Transcoder> log) { _log = log; }

    public bool IsRunning => _proc != null && !_proc.HasExited;

    /// <summary>UTC time of the most recent IVF frame emitted by ffmpeg.
    /// <see cref="DateTime.MinValue"/> until the first frame lands. Watchdog
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

    /// <summary>Invoked from the reader thread for every complete NAL unit, sans start code.
    /// First byte is the standard H.264 NAL header (forbidden-zero-bit + nal_ref_idc + nal_unit_type).
    /// Listeners must be quick; blocking here stalls the encoder's output pipe.</summary>
    public event Action<byte[]>? NaluReady;

    /// <summary>Fires once per access unit (= one decoded frame), driven by AUD
    /// NAL units that ffmpeg emits when -x264-params aud=1 is set. Subscribers
    /// use this to advance their RTP timestamp — same timestamp must be
    /// applied to every NAL inside one access unit so the decoder can
    /// reassemble the frame correctly. Without this, slice-max-size cuts a
    /// frame into ~10 NALs and each one was receiving a different timestamp,
    /// stalling the receiver's frame buffer indefinitely.</summary>
    public event Action? FrameBoundary;

    /// <summary>Fires with the wall-clock at the moment ffmpeg emits an IVF
    /// packet. Paired FIFO with PushJpegAsync calls, this gives encode
    /// latency = T_emit - T_push. CameraStreamService consumes it to fill
    /// in the FrameTiming.IvfEmitted field.</summary>
    public event Action<DateTime>? IvfEmitted;
    private int _ivfWarnedNoSub;
    private long _ivfReadCount;

    public void Start(int targetFps, int crf)
    {
        lock (_lock)
        {
            _lastTargetFps = targetFps;
            _lastCrf = crf;
            if (IsRunning) return;

            // Filter chain kept minimal so ffmpeg can't buffer behind us.
            // The previous chain had `normalize=smoothing=20` for autostretch,
            // which buffers 20 frames of histogram lookahead — at 10 fps that
            // is a *built-in 2 s latency* before the encoder ever sees a
            // frame. Tonemapping + WB now happen per-frame in the debayer
            // (zero buffering), so ffmpeg only needs to scale.
            // Use ArgumentList (not Arguments) so each token is passed
            // atomically — ProcessStartInfo.Arguments parses on whitespace
            // and DOES NOT respect single quotes, which broke drawtext
            // text expressions containing spaces.
            var argList = new[]
            {
                "-fflags", "nobuffer+discardcorrupt",
                "-flags", "low_delay",
                "-probesize", "32",
                "-analyzeduration", "0",
                "-f", "image2pipe",
                // BMP input, not MJPEG. Producer encodes raw RGB into a
                // BMP frame (essentially memcpy + tiny header). ffmpeg's
                // BMP decoder is the same — together this skips the JPEG
                // round-trip that was costing ~30 ms per frame for no
                // benefit on a local pipe.
                "-c:v", "bmp",
                // Frame-index timestamps starting at zero. Earlier the wall-
                // clock variant locked onto the BLOB-stream's first arrival
                // wall time and drifted ~14 s ahead of `elapsed`, which fed
                // chrome stale frames stamped with a fresh RTP timestamp —
                // visible as a multi-second growing latency. Fresh-zero PTS
                // avoids the lead entirely; RTP timestamping in
                // WebRTCService.OnFrameBoundary already pulls real wall
                // clock independently.
                "-avoid_negative_ts", "make_zero",
                "-i", "-",
                // Server-side downsample + latency-probe burn-in. The
                // drawtext expression escapes colons (`\:`) since drawtext's
                // own arg parser uses `:` as separator. No outer quotes
                // needed because ArgumentList passes the whole string atom.
                // Output 480 wide. libvpx software encode is the dominant
                // cost on hardware without a VP8 hardware encoder (most
                // dev Macs). Halving the pixel count from 640→480 cuts
                // encode CPU by ~40% which translates directly to lower
                // queue depth → lower end-to-end latency.
                "-vf", "scale='min(480,iw)':-2",
                "-c:v", "libvpx",
                "-deadline", "realtime",
                "-cpu-used", "16",
                "-threads", "4",
                "-tile-columns", "2",
                "-pix_fmt", "yuv420p",
                "-b:v", "600k",
                "-maxrate", "1000k",
                "-bufsize", "1000k",
                "-g", "2",
                "-error-resilient", "1",
                "-lag-in-frames", "0",
                "-auto-alt-ref", "0",
                "-fps_mode", "passthrough",
                "-flush_packets", "1",
                "-f", "ivf", "pipe:1"
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

            _readerCts = new CancellationTokenSource();
            _readerTask = Task.Run(() => ReadNaluLoopAsync(_proc.StandardOutput.BaseStream, _readerCts.Token));
            _ = Task.Run(async () =>
            {
                using var err = _proc.StandardError;
                while (!_proc.HasExited)
                {
                    var line = await err.ReadLineAsync();
                    if (line == null) break;
                    // Surface everything during the WebRTC-stream bring-up debug —
                    // the previous Error/failed-keyword filter swallowed format
                    // negotiation errors that blocked NALU output silently.
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
        _log.LogInformation("H264Transcoder: stopped");
    }

    /// <summary>Push one MJPEG frame into the encoder. Drops the frame if a
    /// previous push is still in-flight — without this guard, BLOBs from the
    /// camera (~2.5 fps) build up faster than ffmpeg can drain them and the
    /// stream lags by seconds. Live viewfinder semantics: latest frame wins,
    /// stale frames disappear.</summary>
    private int _pushInFlight;
    /// <summary>Feed one encoded frame (BMP per the configured input codec)
    /// to the ffmpeg encoder. Single-flight — if a previous push is still
    /// in flight (pipe full / encoder backed up), this drop silently rather
    /// than queue. The caller (CameraStreamService) layers an additional
    /// count-based backlog cap so encoder buffering can't grow unbounded.</summary>
    public Task PushAsync(byte[] frameBytes, CancellationToken ct)
    {
        var stdin = _stdin;
        if (stdin == null) return Task.CompletedTask;
        if (Interlocked.CompareExchange(ref _pushInFlight, 1, 0) != 0) return Task.CompletedTask;
        return Task.Run(async () =>
        {
            try
            {
                await stdin.WriteAsync(frameBytes, ct);
                await stdin.FlushAsync(ct);
            }
            catch (Exception ex) { _log.LogDebug(ex, "H264Transcoder: push failed"); }
            finally { Interlocked.Exchange(ref _pushInFlight, 0); }
        }, ct);
    }

    // Back-compat alias — earlier callers used the JPEG-specific name.
    [Obsolete("Use PushAsync; the input codec is now BMP, not JPEG.")]
    public Task PushJpegAsync(byte[] frameBytes, CancellationToken ct) => PushAsync(frameBytes, ct);

    // Read the IVF (VP8 raw frames) stream from ffmpeg.
    //   File header: 32 bytes, starts with "DKIF" magic.
    //   Per frame:   12-byte header [size:u32 LE][pts:u64 LE] + frame body.
    // Each frame is a complete VP8 access unit; emit one FrameBoundary +
    // NaluReady per frame (subscribers see a single self-contained payload
    // so SIPSorcery's VP8 RTP packetizer can wrap it without parameter-set
    // gymnastics).
    private async Task ReadNaluLoopAsync(Stream stdout, CancellationToken ct)
    {
        try
        {
            var fileHeader = new byte[32];
            if (!await ReadExactAsync(stdout, fileHeader, ct)) return;
            if (!(fileHeader[0] == (byte)'D' && fileHeader[1] == (byte)'K' && fileHeader[2] == (byte)'I' && fileHeader[3] == (byte)'F'))
            {
                _log.LogWarning("VideoTranscoder: IVF magic missing — got {B0:X2} {B1:X2} {B2:X2} {B3:X2}",
                    fileHeader[0], fileHeader[1], fileHeader[2], fileHeader[3]);
                return;
            }

            var frameHeader = new byte[12];
            while (!ct.IsCancellationRequested)
            {
                if (!await ReadExactAsync(stdout, frameHeader, ct)) return;
                int frameSize = frameHeader[0] | (frameHeader[1] << 8) | (frameHeader[2] << 16) | (frameHeader[3] << 24);
                if (frameSize <= 0 || frameSize > 4_000_000)
                {
                    _log.LogWarning("VideoTranscoder: unreasonable frame size {Size}, bailing", frameSize);
                    return;
                }
                var frame = new byte[frameSize];
                if (!await ReadExactAsync(stdout, frame, ct)) return;

                var emittedAt = DateTime.UtcNow;
                Volatile.Write(ref _lastFrameTicks, emittedAt.Ticks);
                _ivfReadCount++;
                if (_ivfReadCount <= 3 || _ivfReadCount % 30 == 0)
                    _log.LogInformation("ReadNaluLoop: IVF #{N} (subs={Has})", _ivfReadCount, IvfEmitted != null);

                try { FrameBoundary?.Invoke(); }
                catch (Exception ex) { _log.LogDebug(ex, "FrameBoundary handler threw"); }
                try { NaluReady?.Invoke(frame); }
                catch (Exception ex) { _log.LogDebug(ex, "NaluReady handler threw"); }
                var subs = IvfEmitted;
                if (subs == null && Volatile.Read(ref _ivfWarnedNoSub) == 0)
                {
                    Volatile.Write(ref _ivfWarnedNoSub, 1);
                    _log.LogWarning("H264Transcoder: IVF emitted but no subscribers (latency probe will show encode=pending)");
                }
                try { subs?.Invoke(emittedAt); }
                catch (Exception ex) { _log.LogDebug(ex, "IvfEmitted handler threw"); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogWarning(ex, "VideoTranscoder: reader loop error"); }
    }

    private static async Task<bool> ReadExactAsync(Stream s, byte[] buf, CancellationToken ct)
    {
        int total = 0;
        while (total < buf.Length)
        {
            int n = await s.ReadAsync(buf.AsMemory(total, buf.Length - total), ct);
            if (n == 0) return false;
            total += n;
        }
        return true;
    }

    /// <summary>Stop the current ffmpeg child (if any) and re-spawn with the
    /// last successful Start args. Used by the watchdog when ffmpeg appears
    /// alive but isn't producing frames. Caller is responsible for the gap
    /// between stop and start being acceptable — for our viewfinder use
    /// case it's a sub-second blip the keepalive loop covers transparently.</summary>
    public async Task RestartAsync()
    {
        int fps, crf;
        lock (_lock) { fps = _lastTargetFps; crf = _lastCrf; }
        await StopAsync();
        // Reset liveness so the next watchdog tick doesn't immediately
        // re-fire on a still-cold pipeline.
        Volatile.Write(ref _lastFrameTicks, 0);
        if (fps > 0) Start(fps, crf);
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
