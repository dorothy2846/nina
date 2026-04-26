using System.Collections.Concurrent;
using System.Net.WebSockets;
using NINA.Headless.Indi;

namespace NINA.Headless.Services;

/// <summary>
/// Live streaming for the camera tab. Step B1 uses the INDI driver's native video streaming
/// path (CCD_VIDEO_STREAM=On, CCD_STREAM_ENCODER=MJPEG). The driver delivers pre-encoded
/// JPEG frames on the CCD1 BLOB at ~8–30 FPS depending on sensor + exposure, so we forward
/// bytes to every connected WebSocket client without touching the pixels. Later phases swap
/// this for H.264 (server-side VideoToolbox) and WebRTC transport without changing the
/// iOS-visible protocol if we keep this WS as a fallback.
///
/// Lifecycle: subscribing to the WS implicitly starts the stream; the last unsubscribe stops
/// it. REST control endpoints let callers tune exposure / max FPS.
/// </summary>
public class CameraStreamService : IAsyncDisposable
{
    private readonly IndiDiscoveryService _indi;
    private readonly EquipmentSelectionService _equipment;
    private readonly ILogger<CameraStreamService> _log;
    private readonly H264Transcoder _h264;

    // Two client fan-outs sharing one upstream camera capture:
    //   _jpegClients — raw MJPEG frames, low latency, higher bandwidth (Step B1 transport)
    //   _h264Clients — ffmpeg-transcoded H.264 Annex B, lower bandwidth (Step B2 transport)
    // Step B3 (WebRTC) will consume the same H.264 NALU stream as _h264Clients but over RTP
    // instead of raw WebSocket bytes.
    private readonly ConcurrentDictionary<Guid, WebSocket> _jpegClients = new();
    private readonly ConcurrentDictionary<Guid, WebSocket> _h264Clients = new();
    private readonly object _stateLock = new();
    private bool _running;
    private string? _streamingDevice;
    // 30 ms exposure: frame rate ceiling instead of exposure ceiling. With
    // INDI's 100-200 ms readout/blob overhead, longer exposures linearly
    // grow jitter-buffer floor on the receiver — chrome holds ~1.5 frame
    // intervals as safety. 30 ms exposure → ~10-15 fps → ~70-100 ms jitter
    // buffer instead of 350 ms. Gain stays high (300) so signal still reads
    // for daytime viewfinder framing. Photo mode unaffected.
    private double _exposureSeconds = 0.03;
    // Streaming-only binning. 4×4 default trades resolution for low-latency
    // viewfinder; user overrides via /stream/configure when they want full
    // sensor for focusing or planetary alignment. Photo capture path doesn't
    // touch this (still-capture binning is its own request param), and the
    // stream-stop hook restores 1×1 so a subsequent capture is unaffected.
    // Streaming binning/gain — null = "leave the camera's capture settings
    // alone, downsample server-side instead". Streaming should never disturb
    // the user's planetary or DSO configuration. They opt-in by passing
    // explicit binX/binY/gain to /stream/configure, in which case we
    // snapshot + restore around the stream session.
    private int? _streamBinX = null;
    private int? _streamBinY = null;
    private int? _streamGainOverride = null;
    private int? _savedBinX, _savedBinY, _savedGain;
    private CancellationTokenSource? _keepaliveCts;
    private double _maxFps = 10;
    private int _streamGain = 300;

    // FPS tracking — rolling 1-second window of frame arrival times.
    private readonly Queue<DateTime> _frameTimes = new();

    public CameraStreamService(IndiDiscoveryService indi, EquipmentSelectionService equipment, ILogger<CameraStreamService> log, H264Transcoder h264)
    {
        _indi = indi;
        _equipment = equipment;
        _log = log;
        _h264 = h264;
        _h264.NaluReady += OnH264Nalu;
    }

    public bool IsRunning => _running;
    public int ClientCount => _jpegClients.Count + _h264Clients.Count;
    public double ExposureSeconds => _exposureSeconds;
    public double MaxFps => _maxFps;
    public double LastFps
    {
        get
        {
            lock (_frameTimes)
            {
                var now = DateTime.UtcNow;
                while (_frameTimes.Count > 0 && (now - _frameTimes.Peek()).TotalSeconds > 1.0) _frameTimes.Dequeue();
                return _frameTimes.Count;
            }
        }
    }

    public void Configure(double exposureSeconds, double maxFps, int? binX = null, int? binY = null, int? gain = null)
    {
        if (exposureSeconds > 0) _exposureSeconds = exposureSeconds;
        if (maxFps > 0) _maxFps = maxFps;
        // Only set the override fields when the caller actually provided a
        // value — null leaves the camera's capture-side binning/gain
        // untouched and lets the server downsample the larger frames itself.
        if (binX is int bx && bx > 0) _streamBinX = bx;
        if (binY is int by && by > 0) _streamBinY = by;
        if (gain is int g && g >= 0) _streamGainOverride = g;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        lock (_stateLock)
        {
            if (_running) return;
        }

        // Start the H.264 transcoder regardless of which transport subscribes first — negligible
        // cost when no H.264 clients connect, and the ffmpeg pipeline is ready when they do.
        _h264.Start(targetFps: (int)Math.Round(_maxFps), crf: 26);

        var selected = _equipment.GetSelected(DeviceKind.Camera);
        if (selected?.Provider != EquipmentProvider.Indi || !_equipment.IsConnected(DeviceKind.Camera))
            throw new InvalidOperationException("No INDI camera connected");

        var client = _indi.Client ?? throw new InvalidOperationException("INDI client not ready");
        var device = selected.UniqueId;

        // Enable BLOB delivery and subscribe to the stream.
        await client.EnableBlobAsync(device, ct);
        client.BlobReceived += OnBlobReceived;

        // Switch encoder to MJPEG (native video-stream BLOBs carry format=".stream_jpg").
        try
        {
            await client.SetSwitchManyAsync(device, "CCD_STREAM_ENCODER",
                new[] { ("MJPEG", true), ("RAW", false) }, ct);
        }
        catch (Exception ex) { _log.LogWarning(ex, "CameraStream: could not set MJPEG encoder (driver may not expose it)"); }

        // Touch the camera's binning/gain ONLY when the user explicitly asked
        // for streaming-side overrides. Default behavior: respect whatever
        // the user set up for capture (1×1 10ms planetary, 4×4 long-DSO,
        // whatever). Server-side downsample handles the latency budget;
        // INDI sensor state stays exactly as the user left it.
        bool overrideBinning = _streamBinX is int && _streamBinY is int;
        bool overrideGain = _streamGainOverride is int;
        if (overrideBinning || overrideGain)
        {
            try
            {
                var info = _indi.TryBuildCameraInfo(device);
                if (info != null)
                {
                    _savedBinX = info.BinX;
                    _savedBinY = info.BinY;
                    _savedGain = info.Gain;
                }
            }
            catch (Exception ex) { _log.LogDebug(ex, "CameraStream: capture-settings snapshot failed"); }

            if (overrideBinning)
            {
                try { await _indi.SetBinningAsync(device, _streamBinX!.Value, _streamBinY!.Value, ct); }
                catch (Exception ex) { _log.LogWarning(ex, "CameraStream: binning {X}×{Y} set failed", _streamBinX, _streamBinY); }
            }
            if (overrideGain)
            {
                try { await _indi.SetGainAsync(device, _streamGainOverride!.Value, ct); } catch { }
            }
        }

        // Exposure for streaming mode lives on a SEPARATE property — setting CCD_EXPOSURE
        // triggers a one-shot still capture instead of updating the stream cadence.
        try
        {
            await client.SetNumberAsync(device, "STREAMING_EXPOSURE", "STREAMING_EXPOSURE_VALUE", _exposureSeconds, ct);
        }
        catch (Exception ex) { _log.LogWarning(ex, "CameraStream: could not set streaming exposure"); }

        await client.SetSwitchManyAsync(device, "CCD_VIDEO_STREAM",
            new[] { ("STREAM_ON", true), ("STREAM_OFF", false) }, ct);

        lock (_stateLock)
        {
            _running = true;
            _streamingDevice = device;
        }

        // Keepalive loop — pushes the last cached frame to the encoder
        // whenever the camera goes quiet for >500ms. Long still-captures
        // pause BLOB delivery; without this the WebRTC viewer would see a
        // frozen pipeline. With it, the user gets the most recent live
        // frame held until BLOBs resume. Stream session is now decoupled
        // from camera capture cadence.
        _keepaliveCts = new CancellationTokenSource();
        _ = Task.Run(() => KeepaliveLoopAsync(_keepaliveCts.Token));

        _log.LogInformation("CameraStream: native stream started on {Device} (exp={Exp}s)", device, _exposureSeconds);
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        string? device;
        lock (_stateLock)
        {
            if (!_running) return;
            device = _streamingDevice;
            _running = false;
            _streamingDevice = null;
        }

        // Tear down keepalive first so it can't race with the rest of stop.
        try { _keepaliveCts?.Cancel(); } catch { }
        _keepaliveCts = null;
        _lastJpeg = null;

        var client = _indi.Client;
        if (client != null && device != null)
        {
            // Restore the user's capture-side settings only when we actually
            // touched them (override path). Default-mode streams never wrote
            // to the camera so there's nothing to put back.
            if (_savedBinX is int sx && _savedBinY is int sy)
            {
                try { await _indi.SetBinningAsync(device, sx, sy, ct); }
                catch (Exception ex) { _log.LogWarning(ex, "CameraStream: binning {X}×{Y} restore failed", sx, sy); }
            }
            if (_savedGain is int g)
            {
                try { await _indi.SetGainAsync(device, g, ct); } catch { }
            }
            _savedBinX = _savedBinY = _savedGain = null;

            try
            {
                await client.SetSwitchManyAsync(device, "CCD_VIDEO_STREAM",
                    new[] { ("STREAM_ON", false), ("STREAM_OFF", true) }, ct);
            }
            catch (Exception ex) { _log.LogWarning(ex, "CameraStream: STREAM_OFF failed"); }

            client.BlobReceived -= OnBlobReceived;
        }

        await _h264.StopAsync();
        _log.LogInformation("CameraStream: native stream stopped");
    }

    /// <summary>Driver-level pause: turn off CCD_VIDEO_STREAM so a still
    /// capture can take the sensor, but keep `_running` true so the
    /// keepalive loop holds the encoder + WebRTC pipeline alive on the
    /// last cached frame. User sees a held image, never a dead pipeline.</summary>
    public async Task PauseSensorAsync(CancellationToken ct = default)
    {
        if (!_running) return;
        var device = _streamingDevice;
        var client = _indi.Client;
        if (client == null || device == null) return;
        try
        {
            await client.SetSwitchManyAsync(device, "CCD_VIDEO_STREAM",
                new[] { ("STREAM_ON", false), ("STREAM_OFF", true) }, ct);
            // The driver acknowledges STREAM_OFF asynchronously. Triggering
            // CCD_EXPOSURE before the sensor actually idles produces
            // ASI_ERROR_EXPOSURE_IN_PROGRESS / equivalents on every brand.
            // 350 ms covers ZWO/PlayerOne/QHY observed handover times.
            try { await Task.Delay(350, ct); } catch (OperationCanceledException) { }
            _log.LogInformation("CameraStream: sensor paused for capture");
        }
        catch (Exception ex) { _log.LogWarning(ex, "CameraStream: sensor pause failed"); }
    }

    /// <summary>Driver-level resume after a capture finishes — flip
    /// CCD_VIDEO_STREAM back on and BLOBs start flowing again. The
    /// keepalive loop quietly stops re-pushing the cached frame because
    /// the per-frame timestamps catch up.</summary>
    public async Task ResumeSensorAsync(CancellationToken ct = default)
    {
        if (!_running) return;
        var device = _streamingDevice;
        var client = _indi.Client;
        if (client == null || device == null) return;
        try
        {
            await client.SetSwitchManyAsync(device, "CCD_VIDEO_STREAM",
                new[] { ("STREAM_ON", true), ("STREAM_OFF", false) }, ct);
            _log.LogInformation("CameraStream: sensor resumed");
        }
        catch (Exception ex) { _log.LogWarning(ex, "CameraStream: sensor resume failed"); }
    }

    private byte[]? _lastJpeg;

    private void OnBlobReceived(string device, string property, string element, byte[] bytes, string? format)
    {
        if (!_running || property != "CCD1") return;
        if (format != ".stream_jpg") return;

        lock (_frameTimes)
        {
            var now = DateTime.UtcNow;
            _frameTimes.Enqueue(now);
            while (_frameTimes.Count > 0 && (now - _frameTimes.Peek()).TotalSeconds > 1.0) _frameTimes.Dequeue();
        }

        // Cache the latest frame so the keepalive loop can re-push it if the
        // camera goes quiet (long still-capture is the normal case where
        // BLOBs pause). Stream pipeline never starves; user sees the last
        // live frame instead of a frozen black canvas.
        _lastJpeg = bytes;

        _ = BroadcastJpegAsync(bytes);
        if (_h264.IsRunning)
        {
            _ = _h264.PushJpegAsync(bytes, CancellationToken.None);
        }
    }

    /// <summary>Background loop: when the camera hasn't pushed a fresh BLOB
    /// for 500ms (long still-capture in progress, driver hiccup, etc.), repush
    /// the last cached frame so the encoder + WebRTC pipeline stay alive.
    /// User sees a static "last frame" instead of a stuck/frozen browser.</summary>
    private async Task KeepaliveLoopAsync(CancellationToken ct)
    {
        var idleThreshold = TimeSpan.FromMilliseconds(500);
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(250, ct); } catch (OperationCanceledException) { return; }
            if (!_running) continue;
            if (_lastJpeg is null) continue;
            DateTime lastArrival;
            lock (_frameTimes)
            {
                if (_frameTimes.Count == 0) continue;
                lastArrival = _frameTimes.Last();
            }
            if (DateTime.UtcNow - lastArrival < idleThreshold) continue;
            // BLOBs have stopped. Re-push the cached frame to keep the
            // encoder + RTP loop ticking. Don't stamp _frameTimes — these
            // aren't real arrivals, just keepalive ticks.
            if (_h264.IsRunning)
            {
                _ = _h264.PushJpegAsync(_lastJpeg, CancellationToken.None);
            }
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

    public Task AddJpegClientAsync(WebSocket ws, CancellationToken ct)
        => AddClientInternalAsync(ws, ct, _jpegClients);

    public Task AddH264ClientAsync(WebSocket ws, CancellationToken ct)
        => AddClientInternalAsync(ws, ct, _h264Clients);

    // Preserved for backwards compatibility with Program.cs route binding.
    public Task AddClientAsync(WebSocket ws, CancellationToken ct) => AddJpegClientAsync(ws, ct);

    private async Task AddClientInternalAsync(WebSocket ws, CancellationToken ct, ConcurrentDictionary<Guid, WebSocket> bucket)
    {
        var id = Guid.NewGuid();
        bucket[id] = ws;
        if (ClientCount == 1)
        {
            try { await StartAsync(ct); }
            catch (Exception ex) { _log.LogWarning(ex, "CameraStream: auto-start failed"); }
        }
        try
        {
            await PumpClientAsync(ws, ct);
        }
        finally
        {
            bucket.TryRemove(id, out _);
            if (ClientCount == 0) { try { await StopAsync(); } catch { } }
        }
    }

    private static async Task PumpClientAsync(WebSocket ws, CancellationToken ct)
    {
        var buf = new byte[1024];
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            try { result = await ws.ReceiveAsync(buf, ct); }
            catch { return; }
            if (result.MessageType == WebSocketMessageType.Close)
            {
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "client closed", ct); } catch { }
                return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
