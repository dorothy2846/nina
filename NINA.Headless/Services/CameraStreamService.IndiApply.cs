// CameraStreamService.IndiApply.cs — INDI property application: Configure/
// ConfigureAndApplyAsync intake, debounced live exposure/gain/offset writes, the
// ROI/binning STREAM_OFF/ON cycle, and live-exposure property resolution.

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
}
