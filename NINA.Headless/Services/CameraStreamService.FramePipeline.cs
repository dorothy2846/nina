// CameraStreamService.FramePipeline.cs — per-frame pipeline: CCD1 BLOB intake,
// single-flight debayer + gray-world WB, JPEG/BMP serialization, and fan-out to
// the JPEG/H.264 WebSocket clients plus the H264Transcoder.

using System.Collections.Concurrent;
using System.Net.WebSockets;
using NINA.Headless.Indi;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace NINA.Headless.Services;

public partial class CameraStreamService
{
    private void OnNaluEmitted(DateTime emittedAt)
    {
        var n = System.Threading.Interlocked.Increment(ref _naluEmitCount);
        if (_pendingEncode.TryDequeue(out var t))
        {
            t.EncoderEmitted = emittedAt;
        }
        else
        {
            System.Threading.Interlocked.Increment(ref _naluDequeueMissCount);
        }
        if (n % 30 == 1) _log.LogInformation("OnNaluEmitted fired: count={N}, pending={P}, miss={M}", n, _pendingEncode.Count, _naluDequeueMissCount);
    }

    private void OnBlobReceived(string device, string property, string element, byte[] bytes, string? format)
    {
        if (!_running || property != "CCD1") return;
        if (format != ".stream_jpg") return;

        // Re-poll binning every BinningRecheckEvery frames so a runtime change
        // (rare) is picked up without the per-frame cost of always polling.
        if (++_framesSinceBinningCheck >= BinningRecheckEvery)
        {
            var info = _indi.TryBuildCameraInfo(device);
            _hardwareBinned = info != null && (info.BinX > 1 || info.BinY > 1);
            _framesSinceBinningCheck = 0;
        }

        // If the worker is still chewing on the previous frame, drop this one.
        // This produces honest fps numbers — the rolling window only counts
        // frames we actually processed and broadcast.
        if (System.Threading.Interlocked.CompareExchange(ref _processingSlot, 1, 0) != 0)
        {
            System.Threading.Interlocked.Increment(ref _droppedFrames);
            return;
        }

        var blobReceived = DateTime.UtcNow;
        var frameNum = System.Threading.Interlocked.Increment(ref _frameCounter);
        _ = Task.Run(() => ProcessFrame(bytes, frameNum, blobReceived));
    }

    // MARK: Brightness stats (auto-exposure input)
    //
    // Sampled ~2×/s from the driver's 8-bit stream frames. This measures the
    // STREAM domain (the driver's linear 16→8 conversion), which is exactly
    // what the preview shows — good for "fill to 75%" targeting and clipping
    // detection, NOT an absolute 16-bit ADU meter.
    /// RobustPeakFill = 99.99th percentile — the brightness of the object's
    /// bright core while ignoring isolated hot pixels. This is the
    /// auto-exposure control variable: on a planet it sits on the disk; on
    /// anything it tracks "the brightest real thing in frame".
    public sealed record BrightnessStats(double PeakFill, double RobustPeakFill, double P999Fill, double MeanFill, DateTime SampledAt, long FrameNumber);
    private volatile BrightnessStats? _brightness;
    public BrightnessStats? Brightness => _brightness;
    private DateTime _lastBrightnessSample = DateTime.MinValue;

    private void SampleBrightness(byte[] jpegBytes, long frameNumber)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastBrightnessSample).TotalMilliseconds < 500) return;
        _lastBrightnessSample = now;
        try
        {
            var stats = ComputeBrightness(jpegBytes, now, frameNumber);
            if (stats != null) _brightness = stats;
        }
        catch { /* stats are best-effort; never break the frame path */ }
    }

    /// <summary>The measurement core — shared verbatim by the live sampler
    /// and the /stream/brightness/test verification endpoint so what we test
    /// IS what runs in production.</summary>
    public static BrightnessStats? ComputeBrightness(byte[] jpegBytes, DateTime now, long frameNumber)
    {
        using var img = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.L8>(jpegBytes);
        // 256-bin histogram over a 1-in-4 row subsample — cheap and stable.
        var bins = new int[256];
        long count = 0, sum = 0;
        for (int y = 0; y < img.Height; y += 4)
        {
            var row = img.DangerousGetPixelRowMemory(y).Span;
            for (int x = 0; x < row.Length; x++)
            {
                var v = row[x].PackedValue;
                bins[v]++;
                sum += v;
                count++;
            }
        }
        if (count == 0) return null;
        int peak = 255;
        while (peak > 0 && bins[peak] == 0) peak--;
        int Percentile(double q)
        {
            long acc = 0;
            long threshold = (long)(count * q);
            for (int v = 0; v < 256; v++)
            {
                acc += bins[v];
                if (acc >= threshold) return v;
            }
            return 255;
        }
        var p999 = Percentile(0.999);
        var robustPeak = Percentile(0.9999);
        return new BrightnessStats(peak / 255.0, robustPeak / 255.0, p999 / 255.0, (double)sum / count / 255.0, now, frameNumber);
    }

    private void ProcessFrame(byte[] bytes, long frameNumber, DateTime blobReceived)
    {
        try
        {
            SampleBrightness(bytes, frameNumber);
            // Decode + debayer ONCE. Hand the decoded RGB image to two
            // serializers below — one for the JPEG WebSocket fallback path
            // (rare in current iOS, kept for compatibility) and one for the
            // ffmpeg encoder. The encoder gets a BMP (essentially raw RGB +
            // tiny header), eliminating the JPEG round-trip that was costing
            // ~30 ms per frame for no benefit on a local pipe.
            using var rgb = (!string.IsNullOrEmpty(_bayerPattern) && !_hardwareBinned)
                ? TryDebayerToImage(bytes, _bayerPattern)
                : null;

            lock (_frameTimes)
            {
                var now = DateTime.UtcNow;
                _frameTimes.Enqueue(now);
                while (_frameTimes.Count > 0 && (now - _frameTimes.Peek()).TotalSeconds > 1.0) _frameTimes.Dequeue();
            }

            // Encode JPEG only if anyone's listening on the JPEG WebSocket.
            // Skipping the encode for the common case (only WebRTC connected)
            // saves another ~20-30 ms.
            byte[]? jpegBytes = null;
            if (!_jpegClients.IsEmpty)
            {
                if (rgb != null)
                {
                    using var jms = new MemoryStream();
                    rgb.SaveAsJpeg(jms, new JpegEncoder { Quality = 75 });
                    jpegBytes = jms.ToArray();
                }
                else
                {
                    // Mono / no-debayer: pass the camera's raw mosaic JPEG.
                    jpegBytes = bytes;
                }
            }
            var processingDone = DateTime.UtcNow;
            if (jpegBytes != null) _ = BroadcastJpegAsync(jpegBytes);

            // Producer-side drop: see EncoderBacklogMax for rationale.
            DateTime pushedAt;
            bool pushed = false;
            if (_h264.IsRunning)
            {
                if (_pendingEncode.Count < EncoderBacklogMax)
                {
                    pushedAt = DateTime.UtcNow;
                    byte[] feed;
                    if (rgb != null)
                    {
                        // BMP path — no DCT, no quantization. ffmpeg's BMP
                        // decoder is essentially memcpy on the receive side,
                        // matching our cheap encode here.
                        using var bms = new MemoryStream();
                        rgb.SaveAsBmp(bms);
                        feed = bms.ToArray();
                    }
                    else
                    {
                        // Mono path: convert camera mosaic JPEG to BMP so
                        // ffmpeg's input codec stays consistent (`bmp`).
                        feed = MonoJpegToBmp(bytes) ?? bytes;
                    }
                    _ = _h264.PushAsync(feed, CancellationToken.None);
                    pushed = true;
                }
                else
                {
                    System.Threading.Interlocked.Increment(ref _droppedFrames);
                    pushedAt = processingDone;
                }
            }
            else
            {
                pushedAt = processingDone;
            }

            // Only track frames that actually went to the encoder. Dropped
            // frames (producer-side rate-limit) would otherwise fill _timings
            // and make every diagnostic sample read encode=null because
            // they never got pushed.
            if (pushed)
            {
                var timing = new FrameTiming
                {
                    FrameNumber = frameNumber,
                    BlobReceived = blobReceived,
                    ProcessingDone = processingDone,
                    PushedToFfmpeg = pushedAt,
                };
                _timings.Enqueue(timing);
                while (_timings.Count > 60 && _timings.TryDequeue(out _)) { }
                _pendingEncode.Enqueue(timing);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "CameraStream: ProcessFrame failed");
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _processingSlot, 0);
        }
    }

    /// Fast 2×2-block debayer + gray-world WB. We previously tried full
    /// bilinear demosaic + box-filter downsample; on a 12 MP sensor that
    /// took ~120 ms per frame, choking the BLOB-receive thread and pushing
    /// effective fps below 3. The cheap 2×2 path runs in ~5–10 ms which fits
    /// the 10 fps budget. WB at the per-pixel multiply is what makes the
    /// output not look green; the demosaic itself only needs to be "good
    /// enough" because ffmpeg downscales to ≤640px wide downstream anyway.
    /// Heuristic mono-camera detection from the device name. Most astronomy
    /// OSC vendors brand their colour cameras with "-C" / "MC" / "MC Pro"
    /// suffixes, and mono variants with "-M" / "MM". Used as a fallback when
    /// the INDI driver doesn't expose CCD_CFA (PlayerOne is the notable case).
    private static bool LooksLikeMonoByName(string deviceName)
    {
        if (string.IsNullOrEmpty(deviceName)) return false;
        var n = deviceName.ToUpperInvariant();
        // Word-bounded -M / MM markers; avoid false-positives on "Mars-M" by
        // requiring an explicit suffix or "MONO" keyword.
        if (n.Contains("MONO")) return true;
        if (n.EndsWith("-M") || n.EndsWith(" MM") || n.EndsWith("-MM")) return true;
        if (n.Contains(" MM ") || n.Contains("-MM ") || n.Contains(" MM-")) return true;
        return false;
    }

    /// <summary>Decode a mono JPEG (no Bayer) and re-encode as BMP so the
    /// ffmpeg pipeline (configured for BMP input) accepts mono cameras
    /// without dynamic codec switching. Slightly more CPU than the old
    /// pass-through, but mono path is rare and this keeps the encoder
    /// pipeline uniform. Caller takes ownership of the returned bytes.</summary>
    private byte[]? MonoJpegToBmp(byte[] jpeg)
    {
        try
        {
            using var img = SixLabors.ImageSharp.Image.Load<L8>(jpeg);
            using var ms = new MemoryStream();
            img.SaveAsBmp(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }

    private SixLabors.ImageSharp.Image<Rgb24>? TryDebayerToImage(byte[] mosaicJpeg, string pattern)
    {
        SixLabors.ImageSharp.Image<Rgb24>? rgb = null;
        try
        {
            using var img = SixLabors.ImageSharp.Image.Load<L8>(mosaicJpeg);
            int w = img.Width;
            int h = img.Height;
            int step = StreamDecimate;
            int blockStride = step * 2; // each output pixel covers (step×2) source pixels per axis
            int outW = w / blockStride;
            int outH = h / blockStride;
            if (outW < 2 || outH < 2) return null;

            // Per-frame channel sums for WB. Sampled at 1-in-16 pixels
            // (every 4th in each axis) — enough for a stable mean without
            // a full pass.
            long rSum = 0, gSum = 0, bSum = 0;
            int sampleCount = 0;

            rgb = new SixLabors.ImageSharp.Image<Rgb24>(outW, outH);
            for (int y = 0; y < outH; y++)
            {
                int sy = y * blockStride;
                var src0 = img.DangerousGetPixelRowMemory(sy).Span;
                var src1 = img.DangerousGetPixelRowMemory(sy + 1).Span;
                var dst = rgb.DangerousGetPixelRowMemory(y).Span;
                for (int x = 0; x < outW; x++)
                {
                    int sx = x * blockStride;
                    byte tl = src0[sx].PackedValue;
                    byte tr = src0[sx + 1].PackedValue;
                    byte bl = src1[sx].PackedValue;
                    byte br = src1[sx + 1].PackedValue;

                    int rRaw, gRaw, bRaw;
                    switch (pattern)
                    {
                        case "RGGB":
                            rRaw = tl; gRaw = (tr + bl) >> 1; bRaw = br; break;
                        case "BGGR":
                            bRaw = tl; gRaw = (tr + bl) >> 1; rRaw = br; break;
                        case "GRBG":
                            gRaw = (tl + br) >> 1; rRaw = tr; bRaw = bl; break;
                        case "GBRG":
                            gRaw = (tl + br) >> 1; bRaw = tr; rRaw = bl; break;
                        default:
                            rRaw = gRaw = bRaw = (tl + tr + bl + br) >> 2; break;
                    }

                    float rf = rRaw * _wbR;
                    float gf = gRaw * _wbG;
                    float bf = bRaw * _wbB;
                    dst[x] = new Rgb24(
                        (byte)(rf < 0 ? 0 : rf > 255 ? 255 : rf),
                        (byte)(gf < 0 ? 0 : gf > 255 ? 255 : gf),
                        (byte)(bf < 0 ? 0 : bf > 255 ? 255 : bf));

                    if (((x | y) & 3) == 0)
                    {
                        rSum += rRaw; gSum += gRaw; bSum += bRaw; sampleCount++;
                    }
                }
            }

            // Update WB gains with EMA. Gray-world: scale each channel toward
            // the brightest channel's mean — equalises R/G/B without changing
            // overall brightness. This preserves the user's exposure/gain
            // settings; only the green tint is corrected. Capped at 4× so a
            // single-colour scene (foliage) can't blow out the other channels.
            if (sampleCount > 0)
            {
                float mr = (float)rSum / sampleCount;
                float mg = (float)gSum / sampleCount;
                float mb = (float)bSum / sampleCount;
                float maxMean = Math.Max(mr, Math.Max(mg, mb));
                if (maxMean > 1f)
                {
                    float tr = Math.Min(4f, maxMean / Math.Max(1f, mr));
                    float tg = Math.Min(4f, maxMean / Math.Max(1f, mg));
                    float tb = Math.Min(4f, maxMean / Math.Max(1f, mb));
                    if (!_wbInitialized)
                    {
                        _wbR = tr; _wbG = tg; _wbB = tb;
                        _wbInitialized = true;
                    }
                    else
                    {
                        _wbR = _wbR * 0.9f + tr * 0.1f;
                        _wbG = _wbG * 0.9f + tg * 0.1f;
                        _wbB = _wbB * 0.9f + tb * 0.1f;
                    }
                }
            }

            // Transfer ownership to caller — null out our local so the
            // finally block doesn't dispose it.
            var result = rgb;
            rgb = null;
            return result;
        }
        catch
        {
            return null;
        }
        finally
        {
            rgb?.Dispose();
        }
    }

    private void OnH264Nalu(byte[] nalu)
    {
        if (_h264Clients.IsEmpty) return;
        // Wire format per frame = 4-byte start code + NALU body, matching H.264 Annex B.
        // Clients can concatenate multiple frames into a CMSampleBuffer without re-framing.
        var framed = new byte[4 + nalu.Length];
        framed[0] = 0; framed[1] = 0; framed[2] = 0; framed[3] = 1;
        Buffer.BlockCopy(nalu, 0, framed, 4, nalu.Length);
        _ = BroadcastH264Async(framed);
    }

    private async Task BroadcastJpegAsync(byte[] jpegBytes)
    {
        var segment = new ArraySegment<byte>(jpegBytes);
        foreach (var kv in _jpegClients)
        {
            if (kv.Value.State != WebSocketState.Open) continue;
            try { await kv.Value.SendAsync(segment, WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None); }
            catch { }
        }
    }

    private async Task BroadcastH264Async(byte[] naluFramed)
    {
        var segment = new ArraySegment<byte>(naluFramed);
        foreach (var kv in _h264Clients)
        {
            if (kv.Value.State != WebSocketState.Open) continue;
            try { await kv.Value.SendAsync(segment, WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None); }
            catch { }
        }
    }
}
