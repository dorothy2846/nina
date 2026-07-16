// CameraStreamService.Lifecycle.cs — stream lifecycle: INDI connect/disconnect/
// reconnect handling, StartAsync/StopAsync, sensor pause/resume around still
// captures, and WebSocket client subscribe/unsubscribe (which drives start/stop).

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

    public async Task StartAsync(CancellationToken ct)
    {
        lock (_stateLock)
        {
            if (_running) return;
        }

        // Rapid-cycle protection: pace full stop→start cycles against the
        // shared sensor-cycle clock (also stamped by the ROI/binning apply
        // path). The request still succeeds — it just waits out the budget.
        var sinceCycle = (DateTime.UtcNow - _lastSensorCycleAt).TotalMilliseconds;
        if (sinceCycle < MinStopStartIntervalMs)
        {
            var waitMs = MinStopStartIntervalMs - (int)sinceCycle;
            _log.LogInformation("CameraStream: pacing sensor re-arm by {Ms}ms (rapid-cycle protection)", waitMs);
            await Task.Delay(waitMs, ct);
            lock (_stateLock)
            {
                if (_running) return;
            }
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
        _lastSensorCycleAt = DateTime.UtcNow;

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
                _lastSensorCycleAt = DateTime.UtcNow;
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
            // Stamp the shared cycle clock (no delay here — pausing a still
            // capture must stay fast) so following full cycles are paced.
            _lastSensorCycleAt = DateTime.UtcNow;
            _log.LogInformation("CameraStream: sensor resumed");
        }
        catch (Exception ex) { _log.LogWarning(ex, "CameraStream: sensor resume failed"); }
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
