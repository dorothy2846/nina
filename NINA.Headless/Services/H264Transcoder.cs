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
                "-c:v", "libx264",
                "-preset", "ultrafast",
                "-tune", "zerolatency",
                "-crf", crf.ToString(),
                "-pix_fmt", "yuv420p",
                "-g", (targetFps * 2).ToString(),
                "-bf", "0",
                "-bsf:v", "dump_extra=freq=keyframe",
                "-f", "h264", "pipe:1"
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

    // Read the Annex B stream and emit one NAL unit per event. Uses a growing buffer with a
    // simple 2-pointer scan for start codes. Start codes can be 3 bytes (00 00 01) or 4 bytes
    // (00 00 00 01); we accept either and always strip them before raising the event.
    private async Task ReadNaluLoopAsync(Stream stdout, CancellationToken ct)
    {
        var buffer = new List<byte>(1 << 16);
        var chunk = new byte[8192];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var n = await stdout.ReadAsync(chunk, ct);
                if (n == 0) return;
                buffer.AddRange(new ArraySegment<byte>(chunk, 0, n));

                // Walk the buffer, emit NALUs whose start + next-start we have seen.
                var bytes = buffer.ToArray(); // OK-ish: NALU boundaries come quickly, buffer stays small
                int pos = FindStartCode(bytes, 0, out int startCodeLen);
                if (pos < 0) continue;

                int consumed = 0;
                while (true)
                {
                    int next = FindStartCode(bytes, pos + startCodeLen, out int nextStartLen);
                    if (next < 0)
                    {
                        // Keep from `pos` onward for the next iteration.
                        consumed = pos;
                        break;
                    }
                    int naluStart = pos + startCodeLen;
                    int naluEnd = next;
                    var nalu = new byte[naluEnd - naluStart];
                    Array.Copy(bytes, naluStart, nalu, 0, nalu.Length);
                    try { NaluReady?.Invoke(nalu); }
                    catch (Exception ex) { _log.LogDebug(ex, "NaluReady handler threw"); }

                    pos = next;
                    startCodeLen = nextStartLen;
                }

                if (consumed > 0)
                {
                    buffer.RemoveRange(0, consumed);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogWarning(ex, "H264Transcoder: reader loop error"); }
    }

    private static int FindStartCode(byte[] data, int from, out int codeLen)
    {
        codeLen = 0;
        for (int i = from; i + 2 < data.Length; i++)
        {
            if (data[i] == 0 && data[i + 1] == 0)
            {
                if (data[i + 2] == 1) { codeLen = 3; return i; }
                if (i + 3 < data.Length && data[i + 2] == 0 && data[i + 3] == 1) { codeLen = 4; return i; }
            }
        }
        return -1;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
