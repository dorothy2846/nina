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
    // Defaults tuned for "can I see something?" rather than maximum frame rate: 500 ms exposure
    // gives the sensor enough photons to look like more than a black rectangle in a typical room
    // or dim sky. Fast target (e.g. planetary) can drop this via REST /stream/configure.
    private double _exposureSeconds = 0.5;
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

    public void Configure(double exposureSeconds, double maxFps)
    {
        if (exposureSeconds > 0) _exposureSeconds = exposureSeconds;
        if (maxFps > 0) _maxFps = maxFps;
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

        // Push gain up — without astronomy-style autostretch the raw sensor readings at short
        // exposures produce an almost-black JPEG. High gain with short exposure is the
        // planetary-imaging norm and matches what FireCapture does out of the box.
        try { await _indi.SetGainAsync(device, _streamGain, ct); } catch { }

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

        var client = _indi.Client;
        if (client != null && device != null)
        {
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

        // Fan out to MJPEG subscribers untouched. Always push to the H.264 transcoder
        // when it's running — its NaluReady event drives both transports: WebSocket
        // H264 (_h264Clients) AND WebRTC peers (WebRTCService.OnNaluReady). Gating
        // on `_h264Clients.IsEmpty` was a TODO leftover that silently starved every
        // WebRTC connection ("WebSocket now, WebRTC soon" — soon arrived).
        _ = BroadcastJpegAsync(bytes);
        if (_h264.IsRunning)
        {
            _ = _h264.PushJpegAsync(bytes, CancellationToken.None);
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
