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

    public H264Transcoder(ILogger<H264Transcoder> log) { _log = log; }

    public bool IsRunning => _proc != null && !_proc.HasExited;

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

    public void Start(int targetFps, int crf)
    {
        lock (_lock)
        {
            if (IsRunning) return;

            // `normalize` ≈ linear min/max autostretch but smoothed over a window of frames
            // so bright flashes (meteor, plane) don't slam the gain. Drives dim/astronomy
            // scenes out of near-black into something visible without any client work.
            // `eq=gamma=0.8` adds a gentle midtone boost so starfields read as stars, not
            // near-black pixels that look identical to the background.
            var args = string.Join(' ', new[]
            {
                "-fflags", "nobuffer",
                "-flags", "low_delay",
                "-f", "image2pipe",
                "-c:v", "mjpeg",
                "-r", targetFps.ToString(),
                "-i", "-",
                // Scale to 1280-wide *first* so libx264 isn't asked to encode a full-sensor
                // 3856×2180 frame at 10 fps — that produces ~100 Mbps of H.264 which the
                // browser/iOS decoder can't keep up with (visual = black canvas, even though
                // the WebRTC peer says "connected"). Aspect-preserved, even-rounded height.
                "-vf", "scale=1280:-2,normalize=smoothing=20,eq=gamma=0.8",
                // VP8 instead of H.264. H.264 stack hit chrome decoder rejecting
                // every frame regardless of profile-level-id rewrites, AUD
                // insertion, slice-size capping, or frame-boundary detection —
                // SIPSorcery's H.264 RTP packetizer marker/timestamp behavior is
                // the suspected wall. VP8 frames are self-contained access
                // units (no SPS/PPS/parameter-set dance), and SIPSorcery's VP8
                // packetizer is the well-trodden path on the project.
                "-c:v", "libvpx",
                "-deadline", "realtime",
                "-cpu-used", "8",
                "-pix_fmt", "yuv420p",
                "-b:v", "1500k",
                "-maxrate", "2000k",
                "-bufsize", "3000k",
                "-g", Math.Max(2, targetFps / 2).ToString(),
                "-error-resilient", "1",
                "-f", "ivf", "pipe:1"
            });

            var psi = new ProcessStartInfo("ffmpeg", args)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            _log.LogInformation("H264Transcoder: launching ffmpeg {Args}", args);
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

    /// <summary>Push one MJPEG frame into the encoder. Silently dropped if the encoder isn't
    /// running (broadcast racing against shutdown).</summary>
    public async Task PushJpegAsync(byte[] jpegBytes, CancellationToken ct)
    {
        var stdin = _stdin;
        if (stdin == null) return;
        try
        {
            await stdin.WriteAsync(jpegBytes, ct);
            await stdin.FlushAsync(ct);
        }
        catch (Exception ex) { _log.LogDebug(ex, "H264Transcoder: push failed"); }
    }

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

                try { FrameBoundary?.Invoke(); }
                catch (Exception ex) { _log.LogDebug(ex, "FrameBoundary handler threw"); }
                try { NaluReady?.Invoke(frame); }
                catch (Exception ex) { _log.LogDebug(ex, "NaluReady handler threw"); }
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

    public async ValueTask DisposeAsync() => await StopAsync();
}
