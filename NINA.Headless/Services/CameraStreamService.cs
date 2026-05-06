using System.Collections.Concurrent;
using System.Net.WebSockets;
using NINA.Headless.Indi;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace NINA.Headless.Services;

/// <summary>
/// Live streaming for the camera tab. Drives the INDI driver's native video
/// path (CCD_VIDEO_STREAM=On, CCD_STREAM_ENCODER=MJPEG); the driver delivers
/// pre-encoded JPEG frames on the CCD1 BLOB. Each frame is debayered/WB'd in
/// process, broadcast to JPEG WebSocket clients raw, and BMP-wrapped into
/// <see cref="H264Transcoder"/> for H.264 fan-out (WebRTC + the H.264
/// WebSocket fallback).
///
/// Lifecycle: subscribing to the WS or attaching a WebRTC peer implicitly
/// starts the stream; the last unsubscribe stops it. REST control endpoints
/// let callers tune exposure, gain, ROI, and FPS cap.
/// </summary>
public class CameraStreamService : IAsyncDisposable
{
    private readonly IndiDiscoveryService _indi;
    private readonly EquipmentSelectionService _equipment;
    private readonly ILogger<CameraStreamService> _log;
    private readonly H264Transcoder _h264;

    // Two WebSocket fan-outs sharing the upstream camera capture:
    //   _jpegClients — debayered RGB JPEG frames (legacy fallback transport)
    //   _h264Clients — Annex B H.264 NALs, raw start-code framed
    // WebRTC peers consume the same H.264 stream from H264Transcoder over RTP.
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
    private int? _streamOffsetOverride = null;
    private int? _savedBinX, _savedBinY, _savedGain, _savedOffset;
    // Normalized ROI (0..1) — fraction of sensor to read out for streaming.
    // Default = full sensor. Planetary imaging shrinks this so the driver
    // reads a small window and fps climbs proportionally (320×240 around
    // Jupiter delivers 100+ fps where 1280×960 caps near 10).
    private double _roiFracW = 1.0, _roiFracH = 1.0;
    private double _roiFracCX = 0.5, _roiFracCY = 0.5;
    private (int x, int y, int w, int h)? _savedSubFrame;
    private double _maxFps = 20;

    // FPS tracking — rolling 1-second window of frame arrival times.
    private readonly Queue<DateTime> _frameTimes = new();

    /// Was the stream running when the underlying INDI socket dropped?
    /// On reconnect we restore that state so a network blip / driver restart
    /// is invisible to the iOS viewer beyond a brief frozen frame.
    private bool _wasRunningPreDisconnect;

    public CameraStreamService(IndiDiscoveryService indi, EquipmentSelectionService equipment, ILogger<CameraStreamService> log, H264Transcoder h264)
    {
        _indi = indi;
        _equipment = equipment;
        _log = log;
        _h264 = h264;
        _h264.NaluReady += OnH264Nalu;
        _h264.NaluEmitted += OnNaluEmitted;

        // Auto-start the live stream the moment a camera finishes connecting.
        // Cuts first-viewer cold start by ~1–2 s — without it, the iOS app's
        // first /rtc/offer is what triggers CCD_VIDEO_STREAM=On and we then
        // wait on the sensor's first BLOB. Pre-warming means ffmpeg already
        // has frames flowing, so on first connect only the WebRTC handshake
        // and first H.264 keyframe remain (~0.5 s).
        _indi.DeviceConnected += OnIndiDeviceConnected;
        _indi.ClientDisconnected += OnIndiClientDisconnected;
        _indi.ClientReconnected += OnIndiClientReconnected;
    }

    private void OnIndiClientDisconnected(string reason)
    {
        // INDI socket dead → driver state is gone; in-flight processing
        // can never produce a fresh frame. Tear down so we stop fanning
        // out stale data and so DroppedFrames doesn't keep growing.
        bool wasRunning;
        lock (_stateLock) { wasRunning = _running; _running = false; }
        _wasRunningPreDisconnect = wasRunning;
        if (wasRunning) _log.LogWarning("CameraStream: tearing down — INDI dropped ({Reason})", reason);
    }

    private void OnIndiClientReconnected()
    {
        if (!_wasRunningPreDisconnect) return;
        _wasRunningPreDisconnect = false;
        // Re-arm with the cached settings (exposure / gain / ROI / binning
        // are all still in our service state — nothing got cleared on
        // disconnect except the running flag and the sensor-side switches).
        _ = Task.Run(async () =>
        {
            try
            {
                await StartAsync(CancellationToken.None);
                _log.LogInformation("CameraStream: auto-resumed after INDI reconnect");
            }
            catch (Exception ex) { _log.LogWarning(ex, "CameraStream: resume after reconnect failed"); }
        });
    }

    private void OnIndiDeviceConnected(DeviceKind kind, string deviceName)
    {
        if (kind != DeviceKind.Camera) return;
        if (_running) return;
        // Fire-and-forget — DeviceConnected runs on the INDI client thread, and StartAsync
        // does INDI round-trips that we must not block that thread on.
        _ = Task.Run(async () =>
        {
            try
            {
                await StartAsync(CancellationToken.None);
                _log.LogInformation("CameraStream: auto-started after camera {Device} connected", deviceName);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "CameraStream: auto-start after camera {Device} connect failed (will retry on next viewer)", deviceName);
            }
        });
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

    /// Milliseconds since the most recent frame was fully processed and
    /// broadcast. When the camera is occluded/stalled this grows
    /// monotonically — the value is the honest "how stale is what the user
    /// is looking at" number.
    public int? LastFrameAgeMs
    {
        get
        {
            lock (_frameTimes)
            {
                if (_frameTimes.Count == 0) return null;
                return (int)(DateTime.UtcNow - _frameTimes.Last()).TotalMilliseconds;
            }
        }
    }

    public long DroppedFrames => System.Threading.Interlocked.Read(ref _droppedFrames);

    public void Configure(double exposureSeconds, double maxFps, int? binX = null, int? binY = null, int? gain = null,
                          double? roiFracW = null, double? roiFracH = null, double? roiFracCX = null, double? roiFracCY = null,
                          int? offset = null)
    {
        if (exposureSeconds > 0) _exposureSeconds = exposureSeconds;
        if (maxFps > 0) _maxFps = maxFps;
        if (binX is int bx && bx > 0) _streamBinX = bx;
        if (binY is int by && by > 0) _streamBinY = by;
        if (gain is int g && g >= 0) _streamGainOverride = g;
        if (offset is int o && o >= 0) _streamOffsetOverride = o;
        if (roiFracW is double rw) _roiFracW = Math.Clamp(rw, 0.05, 1.0);
        if (roiFracH is double rh) _roiFracH = Math.Clamp(rh, 0.05, 1.0);
        if (roiFracCX is double rcx) _roiFracCX = Math.Clamp(rcx, 0.0, 1.0);
        if (roiFracCY is double rcy) _roiFracCY = Math.Clamp(rcy, 0.0, 1.0);
    }

    /// <summary>Apply config + reload sensor settings WITHOUT touching the
    /// ffmpeg pipeline. Full StopAsync/StartAsync tore down ffmpeg and
    /// re-spawned it on every ROI tap (1-2 s cold start). Here the encoder
    /// stays running across the brief sensor pause; new sub-framed BLOBs
    /// land straight into the existing pipeline. Sub-second ROI swap.</summary>
    private CancellationTokenSource? _applyDebounceCts;
    private readonly object _applyLock = new();
    /// Slider-stop debounce. Coalesces a drag's worth of intermediate values
    /// into one apply when the user pauses.
    private const int ApplyDebounceMs = 350;
    /// Apply serialisation. ROI/binning changes drive a STREAM_OFF/ON
    /// dance; without this gate, two applies could race that dance and
    /// leave the camera stuck STREAM_OFF.
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    /// ROI/binning cycle gate. PlayerOne tolerates one OFF/ON cycle
    /// reliably but stalls if a second hits within ~1.5 s.
    private DateTime _lastSensorCycleAt = DateTime.MinValue;
    private const int MinCycleIntervalMs = 1500;

    public Task ConfigureAndApplyAsync(double exposureSeconds, double maxFps, int? binX, int? binY, int? gain,
                                       double? roiFracW, double? roiFracH, double? roiFracCX, double? roiFracCY,
                                       CancellationToken ct, int? offset = null)
    {
        // Store user intent immediately. The actual driver writes are
        // debounced — every new call cancels the prior pending apply, so
        // only the latest values reach the driver after the user pauses
        // for ApplyDebounceMs.
        Configure(exposureSeconds, maxFps, binX, binY, gain, roiFracW, roiFracH, roiFracCX, roiFracCY, offset);

        CancellationTokenSource newCts;
        lock (_applyLock)
        {
            _applyDebounceCts?.Cancel();
            _applyDebounceCts?.Dispose();
            _applyDebounceCts = newCts = new CancellationTokenSource();
        }
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(ApplyDebounceMs, newCts.Token); }
            catch (OperationCanceledException) { return; }
            // CancellationToken.None into the apply so a follow-up configure
            // can't cancel an in-progress STREAM_OFF→delay→ON dance and
            // leave the camera stuck STREAM_OFF.
            try { await ApplyToDriverAsync(CancellationToken.None); }
            catch (Exception ex) { _log.LogWarning(ex, "CameraStream: deferred apply failed"); }
        });
        return Task.CompletedTask;
    }

    /// Snapshot of the configuration at the moment ApplyToDriverAsync runs.
    /// Compared against the live `_*` fields to decide what changed since
    /// the last applied state — `prev*` here = "last successfully applied".
    private double _lastAppliedExp;
    private int? _lastAppliedGain;
    private int? _lastAppliedOffset;
    private double _lastAppliedRoiW = 1, _lastAppliedRoiH = 1, _lastAppliedRoiCX = 0.5, _lastAppliedRoiCY = 0.5;
    private int? _lastAppliedBinX, _lastAppliedBinY;

    private async Task ApplyToDriverAsync(CancellationToken ct)
    {
        // Serialise applies so two ROI/binning cycles can't overlap.
        await _applyGate.WaitAsync(ct);
        try { await ApplyToDriverLockedAsync(ct); }
        finally { _applyGate.Release(); }
    }

    private async Task ApplyToDriverLockedAsync(CancellationToken ct)
    {
        bool sensorTouched = _lastAppliedRoiW != _roiFracW || _lastAppliedRoiH != _roiFracH
            || _lastAppliedRoiCX != _roiFracCX || _lastAppliedRoiCY != _roiFracCY
            || _lastAppliedBinX != _streamBinX || _lastAppliedBinY != _streamBinY;
        bool exposureChanged = Math.Abs(_lastAppliedExp - _exposureSeconds) > 0.0001;
        bool gainChanged = _lastAppliedGain != _streamGainOverride;
        bool offsetChanged = _lastAppliedOffset != _streamOffsetOverride;

        bool isRunning;
        string? device;
        lock (_stateLock) { isRunning = _running; device = _streamingDevice; }
        if (!isRunning || device == null) return;

        var client = _indi.Client;
        if (client == null) return;

        _log.LogInformation("ConfigureApply: exp={Ec} gain={Gc} offset={Oc} sensor={St} exp={E}s gain={G} offset={O}",
            exposureChanged, gainChanged, offsetChanged, sensorTouched, _exposureSeconds, _streamGainOverride, _streamOffsetOverride);

        // Live writes — all CCD_CONTROLS + STREAMING_EXPOSURE updates push
        // directly to the running stream. Verified end-to-end with the
        // patched indi-3rdparty playerone driver (worker re-polls
        // Streamer->getTargetFPS each iteration): mid-stream property writes
        // change actual sensor integration time on the very next frame,
        // brightness probe confirms.
        if (exposureChanged && _liveExposureProperty is var prop && prop.HasValue)
        {
            var (propName, elName, scaleToDriverUnits) = prop.Value;
            try
            {
                await client.SetNumberAsync(device, propName, elName, _exposureSeconds * scaleToDriverUnits, ct);
                _lastAppliedExp = _exposureSeconds;
                _log.LogInformation("ConfigureApply: pushed {Prop}.{El}={Val}", propName, elName, _exposureSeconds * scaleToDriverUnits);
            }
            catch (Exception ex) { _log.LogWarning(ex, "CameraStream: live exposure set failed"); }
        }
        if (gainChanged && _streamGainOverride is int g)
        {
            try
            {
                await _indi.SetGainAsync(device, g, ct);
                _lastAppliedGain = _streamGainOverride;
                _log.LogInformation("ConfigureApply: pushed gain={G}", g);
            }
            catch (Exception ex) { _log.LogWarning(ex, "CameraStream: gain set failed"); }
        }
        if (offsetChanged && _streamOffsetOverride is int o)
        {
            try
            {
                await _indi.SetOffsetAsync(device, o, ct);
                _lastAppliedOffset = _streamOffsetOverride;
                _log.LogInformation("ConfigureApply: pushed offset={O}", o);
            }
            catch (Exception ex) { _log.LogWarning(ex, "CameraStream: offset set failed"); }
        }

        // ROI/binning genuinely change sensor topology and need an OFF/ON
        // cycle. PlayerOne tolerates ONE such cycle reliably; the cycle
        // gate enforces a minimum gap so two cannot stack.
        if (!sensorTouched) return;

        // Cycle gate. PlayerOne tolerates one OFF/ON cycle but stalls if a
        // second cycle hits within ~1.5 s. Wait out the remaining gap.
        var sinceLastCycle = (DateTime.UtcNow - _lastSensorCycleAt).TotalMilliseconds;
        if (sinceLastCycle < MinCycleIntervalMs)
        {
            var waitMs = MinCycleIntervalMs - (int)sinceLastCycle;
            _log.LogInformation("ConfigureApply: gating OFF/ON cycle by {Ms}ms", waitMs);
            try { await Task.Delay(waitMs, ct); } catch (OperationCanceledException) { return; }
        }
        _lastSensorCycleAt = DateTime.UtcNow;

        // STREAM_OFF first — wait the empirical settle time so PlayerOne
        // acks before any further writes hit the property bus.
        _log.LogInformation("ConfigureApply: STREAM_OFF for cycle (exp={E} sensor={S})", exposureChanged, sensorTouched);
        try
        {
            await client.SetSwitchManyAsync(device, "CCD_VIDEO_STREAM",
                new[] { ("STREAM_ON", false), ("STREAM_OFF", true) }, ct);
        }
        catch (Exception ex) { _log.LogWarning(ex, "CameraStream: stream OFF failed"); }
        try { await Task.Delay(200, ct); } catch (OperationCanceledException) { return; }

        // Now safe to write properties — stream is paused. Push the LATEST
        // exposure value (user may have moved slider while gated).
        if (_liveExposureProperty is var cycleProp && cycleProp.HasValue)
        {
            var (propName, elName, scaleToDriverUnits) = cycleProp.Value;
            try
            {
                await client.SetNumberAsync(device, propName, elName, _exposureSeconds * scaleToDriverUnits, ct);
                _log.LogInformation("ConfigureApply: pushed {Prop}.{El}={Val} during cycle", propName, elName, _exposureSeconds * scaleToDriverUnits);
                _lastAppliedExp = _exposureSeconds;
            }
            catch (Exception ex) { _log.LogWarning(ex, "CameraStream: cycle exposure set failed"); }
        }

        if (_streamBinX is int bx && _streamBinY is int by)
        {
            try { await _indi.SetBinningAsync(device, bx, by, ct); } catch { }
        }
        if (_roiFracW < 0.999 || _roiFracH < 0.999)
        {
            var size = _indi.GetSensorSize(device);
            if (size.HasValue)
            {
                var sw = size.Value.width;
                var sh = size.Value.height;
                var roiW = Math.Max(8, (int)(sw * _roiFracW)) & ~7;
                var roiH = Math.Max(8, (int)(sh * _roiFracH)) & ~7;
                var cx = (int)(sw * _roiFracCX);
                var cy = (int)(sh * _roiFracCY);
                var roiX = Math.Clamp(cx - roiW / 2, 0, sw - roiW);
                var roiY = Math.Clamp(cy - roiH / 2, 0, sh - roiH);
                try
                {
                    await _indi.SetSubFrameAsync(device, roiX, roiY, roiW, roiH, ct);
                    _log.LogInformation("CameraStream: live ROI swap to {W}×{H} at ({X},{Y})", roiW, roiH, roiX, roiY);
                }
                catch (Exception ex) { _log.LogWarning(ex, "CameraStream: live ROI set failed"); }
            }
        }
        else if (sensorTouched)
        {
            var size = _indi.GetSensorSize(device);
            if (size.HasValue)
            {
                try { await _indi.SetSubFrameAsync(device, 0, 0, size.Value.width, size.Value.height, ct); } catch { }
            }
        }

        try
        {
            await client.SetSwitchManyAsync(device, "CCD_VIDEO_STREAM",
                new[] { ("STREAM_ON", true), ("STREAM_OFF", false) }, ct);
            _log.LogInformation("ConfigureApply: STREAM_ON after cycle");
            _lastAppliedRoiW = _roiFracW; _lastAppliedRoiH = _roiFracH;
            _lastAppliedRoiCX = _roiFracCX; _lastAppliedRoiCY = _roiFracCY;
            _lastAppliedBinX = _streamBinX; _lastAppliedBinY = _streamBinY;
        }
        catch (Exception ex) { _log.LogWarning(ex, "CameraStream: stream ON after cycle failed"); }
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

        // Apply ROI sub-frame if the user shrunk it. Driver writes back
        // CCD_FRAME so subsequent BLOBs carry the smaller window. Restored
        // to full frame on stream stop. Capture path doesn't share this
        // (CameraController.Capture uses its own request params).
        if (_roiFracW < 0.999 || _roiFracH < 0.999)
        {
            var size = _indi.GetSensorSize(device);
            if (size.HasValue)
            {
                var sw = size.Value.width;
                var sh = size.Value.height;
                var roiW = Math.Max(8, (int)(sw * _roiFracW)) & ~7; // multiple of 8 for safety
                var roiH = Math.Max(8, (int)(sh * _roiFracH)) & ~7;
                var cx = (int)(sw * _roiFracCX);
                var cy = (int)(sh * _roiFracCY);
                var roiX = Math.Clamp(cx - roiW / 2, 0, sw - roiW);
                var roiY = Math.Clamp(cy - roiH / 2, 0, sh - roiH);
                _savedSubFrame = (0, 0, sw, sh);
                try
                {
                    await _indi.SetSubFrameAsync(device, roiX, roiY, roiW, roiH, ct);
                    _log.LogInformation("CameraStream: ROI {W}×{H} at ({X},{Y}) of sensor {SW}×{SH}", roiW, roiH, roiX, roiY, sw, sh);
                }
                catch (Exception ex) { _log.LogWarning(ex, "CameraStream: ROI set failed"); }
            }
        }

        // Touch the camera's binning/gain ONLY when the user explicitly asked
        // for streaming-side overrides. Default behavior: respect whatever
        // the user set up for capture (1×1 10ms planetary, 4×4 long-DSO,
        // whatever). Server-side downsample handles the latency budget;
        // INDI sensor state stays exactly as the user left it.
        bool overrideBinning = _streamBinX is int && _streamBinY is int;
        bool overrideGain = _streamGainOverride is int;
        bool overrideOffset = _streamOffsetOverride is int;
        if (overrideBinning || overrideGain || overrideOffset)
        {
            try
            {
                var info = _indi.TryBuildCameraInfo(device);
                if (info != null)
                {
                    _savedBinX = info.BinX;
                    _savedBinY = info.BinY;
                    _savedGain = info.Gain;
                    _savedOffset = info.Offset;
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
            if (overrideOffset)
            {
                try { await _indi.SetOffsetAsync(device, _streamOffsetOverride!.Value, ct); } catch { }
            }
        }

        // CCD_FAST_TOGGLE was tried here previously: PlayerOne flags it as
        // an "Experimental Feature" in its INDI driver and crashes the
        // driver process under sustained streaming. CCD_VIDEO_FORMAT → RAW8
        // also rejected with state=Alert mid- and pre-stream. Leave both.

        await client.SetSwitchManyAsync(device, "CCD_VIDEO_STREAM",
            new[] { ("STREAM_ON", true), ("STREAM_OFF", false) }, ct);

        lock (_stateLock)
        {
            _running = true;
            _streamingDevice = device;
        }

        // Resolve the live-exposure property AFTER STREAM_ON. PlayerOne's
        // INDI driver only registers STREAMING_EXPOSURE once the StreamManager
        // is active; resolving before STREAM_ON falls back to CCD_CONTROLS.
        // Exposure (capture-side, requires a stream re-arm to take effect on
        // the running stream — that's what produced the OFF/ON-dance kludge).
        _liveExposureProperty = ResolveLiveExposureProperty(device);
        if (_liveExposureProperty is var resolved && resolved.HasValue)
        {
            var (propName, elName, scale) = resolved.Value;
            _log.LogInformation("CameraStream: live-exposure property = {P}.{E} (scale ×{S})", propName, elName, scale);
            try
            {
                await client.SetNumberAsync(device, propName, elName, _exposureSeconds * scale, ct);
            }
            catch (Exception ex) { _log.LogWarning(ex, "CameraStream: initial exposure set failed"); }
        }
        else
        {
            _log.LogWarning("CameraStream: no live-exposure property found on device {Dev}", device);
        }

        // Force debayer-vs-passthrough decision on the very first frame
        // (instead of waiting `BinningRecheckEvery` frames at default false).
        _framesSinceBinningCheck = BinningRecheckEvery;

        // Resolve the Bayer pattern. Three signals, in priority order:
        //   1. INDI CCD_CFA → maps cleanly to SensorType.RGGB / BGGR / etc.
        //      Authoritative when present.
        //   2. Device name suffix — PlayerOne / ZWO OSC cameras name themselves
        //      "Uranus-C", "ASI533MC"; mono variants are "-M" / "MM". This is
        //      reliable when CCD_CFA isn't advertised (PlayerOne INDI omits it).
        //   3. Default RGGB — virtually every astronomy OSC sensor we'd
        //      stream from is RGGB, so the safe fallback for "colour camera,
        //      pattern unknown" is RGGB. Better to debayer with the wrong
        //      pattern (mild colour swap) than not debayer at all and leave
        //      the user with a grayscale mosaic on a colour camera.
        // The CameraInfo.SensorType default is Monochrome, so we cannot use
        // that as a "looks like mono" signal — the previous code did, which
        // made every PlayerOne stream look monochrome.
        var camInfo = _indi.TryBuildCameraInfo(device);
        bool nameMono = LooksLikeMonoByName(device);
        _bayerPattern = camInfo?.SensorType switch
        {
            NINA.Core.Enum.SensorType.RGGB => "RGGB",
            NINA.Core.Enum.SensorType.BGGR => "BGGR",
            NINA.Core.Enum.SensorType.GRBG => "GRBG",
            NINA.Core.Enum.SensorType.GBRG => "GBRG",
            // Default + "Color" + "Monochrome" all fall through to the name
            // heuristic. CameraInfo defaults to Monochrome on construction,
            // so the only way to trust SensorType==Monochrome is if the
            // device name agrees. Otherwise treat as RGGB.
            _ => nameMono ? "" : "RGGB"
        };
        _log.LogInformation("CameraStream: device='{Device}' sensor type={Sensor} nameMono={NameMono} bayer='{Pattern}'",
            device, camInfo?.SensorType, nameMono, _bayerPattern);

        // Reset WB gains so a fresh session converges from neutral instead
        // of carrying the previous camera's bias.
        _wbInitialized = false;
        _wbR = _wbG = _wbB = 1f;

        // Stream started fresh — driver now reflects the current `_exposureSeconds`,
        // gain, offset, ROI, binning. Sync the applied snapshots so the next
        // ApplyToDriver invocation correctly detects "no change" instead of
        // re-cycling the latest value.
        _lastAppliedExp = _exposureSeconds;
        _lastAppliedGain = _streamGainOverride;
        _lastAppliedOffset = _streamOffsetOverride;
        _lastAppliedRoiW = _roiFracW; _lastAppliedRoiH = _roiFracH;
        _lastAppliedRoiCX = _roiFracCX; _lastAppliedRoiCY = _roiFracCY;
        _lastAppliedBinX = _streamBinX; _lastAppliedBinY = _streamBinY;
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
            if (_savedOffset is int sof)
            {
                try { await _indi.SetOffsetAsync(device, sof, ct); } catch { }
            }
            if (_savedSubFrame is (int rx, int ry, int rw, int rh))
            {
                try { await _indi.SetSubFrameAsync(device, rx, ry, rw, rh, ct); }
                catch (Exception ex) { _log.LogWarning(ex, "CameraStream: ROI restore failed"); }
            }
            _savedBinX = _savedBinY = _savedGain = _savedOffset = null;
            _savedSubFrame = null;

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
    /// encoder + WebRTC peers stay attached. The receiver freezes on its
    /// last decoded frame for the duration of the capture rather than
    /// disconnecting.</summary>
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
    /// CCD_VIDEO_STREAM back on and BLOBs start flowing again.</summary>
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

    /// Resolved at stream-start by inspecting the connected driver's actual
    /// properties. (propertyName, elementName, scaleToDriverUnits) — scale
    /// converts our user-facing "seconds" to whatever the driver wants
    /// (e.g., 1_000_000 for the PlayerOne CCD_CONTROLS["Exposure"]
    /// microseconds element).
    private (string propName, string elName, double scale)? _liveExposureProperty;

    /// Detect which property the driver uses for live-stream exposure.
    /// Returns null when the driver doesn't expose a settable streaming
    /// exposure — in that case the user's slider is honestly inert and
    /// the warning log surfaces the situation.
    private (string, string, double)? ResolveLiveExposureProperty(string deviceName)
    {
        // Wait up to 2 s for the driver to publish either streaming-exposure
        // property. Same race as SetGainAsync — at stream-start right after
        // camera connect, property dict has the keys but elements stream in
        // separately and may arrive a beat later. Without the wait the server
        // silently can't push initial exposure on the first stream/start of
        // a session.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            var dev = _indi.Client?.GetDevice(deviceName);
            if (dev != null)
            {
                if (dev.Properties.TryGetValue("STREAMING_EXPOSURE", out var se)
                    && se["STREAMING_EXPOSURE_VALUE"] != null)
                    return ("STREAMING_EXPOSURE", "STREAMING_EXPOSURE_VALUE", 1.0);
                if (dev.Properties.TryGetValue("CCD_CONTROLS", out var cc)
                    && cc["Exposure"] != null)
                    return ("CCD_CONTROLS", "Exposure", 1_000_000.0);
            }
            Thread.Sleep(100);
        }
        return null;
    }

    /// Bayer pattern for the connected colour camera. Driver-side debayer
    /// isn't available on PlayerOne (CCD_VIDEO_FORMAT only exposes RAW8/16),
    /// so we receive a grayscale-encoded mosaic JPEG and debayer in process
    /// before fan-out. Empty string = mono camera, frame passes through.
    /// Refreshed on stream start from INDI CCD_CFA so a swap from colour
    /// to mono camera (or vice versa) doesn't paint mono frames green.
    private string _bayerPattern = "";

    /// Cached at stream start (and re-checked every ~30 frames as a safety
    /// net) so we don't pay for `TryBuildCameraInfo`'s ~15 dictionary lookups
    /// on every BLOB. Hardware binning destroys Bayer alignment, so we skip
    /// debayer when this is true.
    private bool _hardwareBinned;
    private int _framesSinceBinningCheck;
    private const int BinningRecheckEvery = 30;
    /// <summary>Max frames allowed to be in flight to the H.264 encoder
    /// before producer-side drop kicks in. 10 frames at 8 fps ≈ 1.25 s
    /// worst case but encoder normally drains far faster than it fills,
    /// so steady-state queue depth is 1-2. Going lower (tried 3) stalled
    /// ffmpeg's BMP demuxer at startup — image2pipe with bmp seems to
    /// need a warmup window of more than 3 frames before it produces
    /// output, even with -probesize 32 -analyzeduration 0.</summary>
    private const int EncoderBacklogMax = 10;
    /// <summary>Minimum interval between sensor OFF/ON cycles (ROI / binning /
    /// exposure changes that need a stream rearm). PlayerOne's driver
    /// disconnects the camera after rapid cycling — observed at the iOS-
    /// slider drag rate of 4 Hz. 800 ms gives the driver breathing room
    /// while still feeling responsive on a single deliberate adjustment.</summary>

    /// Single-flight in-process processing slot. The INDI client thread that
    /// raises BLOB events MUST NOT do CPU work — debayer + JPEG re-encode on
    /// 12 MP frames takes longer than the 100 ms BLOB cadence and stalls the
    /// driver socket. We hand the bytes off to a worker; if the worker is
    /// already busy, we drop the new frame on the floor. Honest backpressure:
    /// drops show up as low fps rather than as deceiving keepalive replays.
    private int _processingSlot; // 0 = idle, 1 = busy
    private long _droppedFrames;

    /// <summary>Per-frame timing samples for the latency probe. Each entry
    /// records when the frame entered each pipeline stage so /api/v1/
    /// diagnostics/streamLatency can show capture→encoder→emit decomposition.
    /// Bounded ring buffer (last 60 frames). <see cref="EncoderEmitted"/> is
    /// filled in asynchronously when the H264Transcoder reports a NAL —
    /// paired FIFO so frames map 1:1 in order (drop-tolerant since we use
    /// single-flight push).</summary>
    public sealed class FrameTiming
    {
        public long FrameNumber;
        public DateTime BlobReceived;
        public DateTime ProcessingDone;
        public DateTime PushedToFfmpeg;
        public DateTime? EncoderEmitted;
    }
    private readonly System.Collections.Concurrent.ConcurrentQueue<FrameTiming> _timings = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<FrameTiming> _pendingEncode = new();
    private long _frameCounter;
    public IReadOnlyList<FrameTiming> RecentFrameTimings => _timings.ToArray();

    private long _naluEmitCount;
    private long _naluDequeueMissCount;
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

    private void ProcessFrame(byte[] bytes, long frameNumber, DateTime blobReceived)
    {
        try
        {
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

    /// EMA-tracked per-channel WB gain. Targets the brightest channel's
    /// mean (gray-world) rather than a fixed exposure target — that way the
    /// overall brightness is whatever the user's gain/exposure settings
    /// produce, and we ONLY remove the green-channel sensitivity bias
    /// that paints OSC-camera daytime frames green. Previous fixed-target
    /// (TargetMean=96) was effectively auto-exposure, which silently
    /// reverted every gain change the user made — they'd drop gain, the
    /// image would dim for one frame, then the EMA pumped the gains back
    /// up to compensate, masking their adjustment.
    private float _wbR = 1f, _wbG = 1f, _wbB = 1f;
    private bool _wbInitialized;

    /// How many Bayer 2×2 blocks we skip between output samples.
    ///   1 = every block (output ≈ source/2)        — 12 MP sensor → ~6 MP RGB
    ///   2 = every other block (output ≈ source/4)  — 12 MP → ~1.5 MP RGB
    /// Stream output is bandwidth-capped at 640 px wide downstream by
    /// ffmpeg, so even a 12 MP camera produces output that gets thrown
    /// away. Decimating earlier means the JPEG encode + ffmpeg decode
    /// see a much smaller frame and we keep up with high-rate sensors
    /// (10 ms exposure on 1×1 binning) without dropping. Stills are
    /// untouched — they go through FitsToPng at full bilinear demosaic.
    private const int StreamDecimate = 4;

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
