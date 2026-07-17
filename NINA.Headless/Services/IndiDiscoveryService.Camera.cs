// IndiDiscoveryService.Camera.cs — camera actuation for IndiDiscoveryService:
// exposure start/abort + BLOB delivery, SER/AVI recording, frame type, dew heater,
// and sensor settings (gain/offset/binning/sub-frame/cooling).

using NINA.Headless.Indi;

namespace NINA.Headless.Services;

public partial class IndiDiscoveryService
{
    /// <summary>Abort a running CCD exposure. INDI standard vector is
    /// <c>CCD_ABORT_EXPOSURE</c> with a single <c>ABORT</c> element — drivers either
    /// also honour the legacy <c>CCD1.CCD_ABORT_EXPOSURE</c> form or expose nothing;
    /// we try both. Returns true when at least one succeeded.</summary>
    public async Task<bool> AbortExposureAsync(string deviceName, CancellationToken ct)
    {
        var client = _client;
        if (client == null) return false;
        var dev = client.GetDevice(deviceName);
        if (dev == null) return false;

        if (dev.Properties.ContainsKey("CCD_ABORT_EXPOSURE"))
        {
            await client.SetSwitchAsync(deviceName, "CCD_ABORT_EXPOSURE", "ABORT", true, ct);
            return true;
        }
        return false;
    }

    /// <summary>Enable / disable the camera's dew heater. Driver variance is wide:
    /// some expose <c>CCD_DEW_CONTROL</c> (on/off switch), some expose
    /// <c>AUX_HEATER_TOGGLE</c>, and ZWO-style drivers use a number element under
    /// <c>ANTI_DEW</c> with intensity 0–100. We try each in order and return true
    /// as soon as one sticks.</summary>
    public async Task<bool> SetDewHeaterAsync(string deviceName, bool on, int? powerPercent, CancellationToken ct)
    {
        var client = _client;
        if (client == null) return false;
        var dev = client.GetDevice(deviceName);
        if (dev == null) return false;

        if (dev.Properties.ContainsKey("CCD_DEW_CONTROL"))
        {
            await client.SetSwitchManyAsync(deviceName, "CCD_DEW_CONTROL",
                new[] { ("INDI_ENABLED", on), ("INDI_DISABLED", !on) }, ct);
            return true;
        }
        if (dev.Properties.ContainsKey("AUX_HEATER_TOGGLE"))
        {
            await client.SetSwitchManyAsync(deviceName, "AUX_HEATER_TOGGLE",
                new[] { ("INDI_ENABLED", on), ("INDI_DISABLED", !on) }, ct);
            return true;
        }
        // ZWO ASI: ANTI_DEW number vector, 0 = off, 1-100 = heater power.
        if (dev.Properties.ContainsKey("ANTI_DEW"))
        {
            var value = on ? (powerPercent ?? 50) : 0;
            await client.SetNumberAsync(deviceName, "ANTI_DEW", "ANTI_DEW_VALUE", value, ct);
            return true;
        }
        return false;
    }

    // ----- Camera SER/AVI recording (driver-native) -----
    // The INDI driver records video streams directly to disk via its RECORD_STREAM + RECORD_FILE
    // property vectors — output format is SER (planetary imaging standard) or MP4 depending
    // on driver/ENV. We just toggle the driver switches; no server-side muxer required.

    public enum RecordMode { Manual, Duration, Frames }

    public async Task StartRecordingAsync(string deviceName, RecordMode mode, int durationSeconds, int frameCount, string dir, string filename, CancellationToken ct)
    {
        var client = _client;
        if (client == null) throw new InvalidOperationException("INDI client not connected");

        // Make sure output dir exists — the driver crashes silently if it can't write.
        try { Directory.CreateDirectory(dir); } catch { }

        // 1. Point the driver at our directory + filename pattern.
        var xml = $"<newTextVector device=\"{Escape(deviceName)}\" name=\"RECORD_FILE\">" +
                  $"<oneText name=\"RECORD_FILE_DIR\">{Escape(dir)}</oneText>" +
                  $"<oneText name=\"RECORD_FILE_NAME\">{Escape(filename)}</oneText>" +
                  $"</newTextVector>";
        await client.SendAsync(xml, ct);

        // 2. For duration/frame modes, program the numeric target.
        if (mode == RecordMode.Duration)
            await client.SetNumberAsync(deviceName, "RECORD_OPTIONS", "RECORD_DURATION", durationSeconds, ct);
        else if (mode == RecordMode.Frames)
            await client.SetNumberAsync(deviceName, "RECORD_OPTIONS", "RECORD_FRAME_TOTAL", frameCount, ct);

        // 3. Flip the matching switch on. RECORD_STREAM is OneOfMany, so turning one on
        //    implicitly turns the others off.
        var onElement = mode switch
        {
            RecordMode.Duration => "RECORD_DURATION_ON",
            RecordMode.Frames   => "RECORD_FRAME_ON",
            _                   => "RECORD_ON"
        };
        var switches = new[]
        {
            ("RECORD_ON",          onElement == "RECORD_ON"),
            ("RECORD_DURATION_ON", onElement == "RECORD_DURATION_ON"),
            ("RECORD_FRAME_ON",    onElement == "RECORD_FRAME_ON"),
            ("RECORD_OFF",         false),
        };
        await client.SetSwitchManyAsync(deviceName, "RECORD_STREAM", switches, ct);

        // Throttle the preview while recording: the SER file gets every frame
        // regardless (the recorder taps the stream driver-side), but each BLOB
        // sent our way costs parse CPU that planetary-rate capture can't spare.
        // Best-effort — remember the old cap so StopRecording restores it.
        try
        {
            var dev = client.GetDevice(deviceName);
            if (dev != null && dev.Properties.TryGetValue("LIMITS", out var limits))
            {
                var current = limits["LIMITS_PREVIEW_FPS"]?.AsDouble;
                if (current is > RecordingPreviewFps)
                {
                    lock (_previewFpsLock) _savedPreviewFps[deviceName] = current.Value;
                    await client.SetNumberAsync(deviceName, "LIMITS", "LIMITS_PREVIEW_FPS", RecordingPreviewFps, ct);
                    _log.LogInformation("Recording: preview capped to {Fps} fps (was {Prev})", RecordingPreviewFps, current);
                }
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "Recording: preview-fps cap failed (driver may not expose LIMITS)"); }
    }

    /// Preview BLOB rate while a recording runs. Enough for framing; leaves
    /// the CPU to the driver's SER writer.
    private const double RecordingPreviewFps = 5;
    private readonly object _previewFpsLock = new();
    private readonly Dictionary<string, double> _savedPreviewFps = new();

    public async Task StopRecordingAsync(string deviceName, CancellationToken ct)
    {
        var client = _client; if (client == null) return;
        await client.SetSwitchManyAsync(deviceName, "RECORD_STREAM", new[]
        {
            ("RECORD_ON", false), ("RECORD_DURATION_ON", false),
            ("RECORD_FRAME_ON", false), ("RECORD_OFF", true)
        }, ct);

        await RestorePreviewCapAsync(deviceName, ct);
    }

    /// <summary>Frames-mode recordings stop DRIVER-side when the burst count is
    /// reached — StopRecordingAsync never runs. Called from OnDevicesChanged so
    /// the RECORD_STREAM switch flipping to OFF triggers the cap restore.</summary>
    internal void CheckPreviewCapRestore()
    {
        List<string> capped;
        lock (_previewFpsLock)
        {
            if (_savedPreviewFps.Count == 0) return;
            capped = _savedPreviewFps.Keys.ToList();
        }
        foreach (var dev in capped)
        {
            if (!GetRecordingStatus(dev).running)
                _ = Task.Run(() => RestorePreviewCapAsync(dev, CancellationToken.None));
        }
    }

    /// Restore the preview cap StartRecordingAsync lowered. Idempotent — the
    /// saved entry is consumed on first restore.
    private async Task RestorePreviewCapAsync(string deviceName, CancellationToken ct)
    {
        double saved;
        lock (_previewFpsLock)
        {
            if (!_savedPreviewFps.Remove(deviceName, out saved)) return;
        }
        var client = _client;
        if (client == null) return;
        try
        {
            await client.SetNumberAsync(deviceName, "LIMITS", "LIMITS_PREVIEW_FPS", saved, ct);
            _log.LogInformation("Recording: preview cap restored to {Fps} fps", saved);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Recording: preview-fps restore failed"); }
    }

    public (bool running, string? activeSwitch, string? dir, string? filename, double? captureFps) GetRecordingStatus(string deviceName)
    {
        var client = _client;
        var dev = client?.GetDevice(deviceName);
        if (dev == null) return (false, null, null, null, null);

        string? active = null;
        if (dev.Properties.TryGetValue("RECORD_STREAM", out var rs))
        {
            foreach (var e in rs.Elements.Values)
                if (e.ValueOn && e.Name != "RECORD_OFF") active = e.Name;
        }
        string? dir = null, file = null;
        if (dev.Properties.TryGetValue("RECORD_FILE", out var rf))
        {
            dir = rf["RECORD_FILE_DIR"]?.Value;
            file = rf["RECORD_FILE_NAME"]?.Value;
        }
        // Driver-side capture rate (StreamManager's FPS vector). This is the
        // rate frames hit the SER file — the client BLOB rate is preview-capped
        // by LIMITS_PREVIEW_FPS and says nothing about the recording.
        double? captureFps = null;
        if (dev.Properties.TryGetValue("FPS", out var fps))
            captureFps = fps["EST_FPS"]?.AsDouble;
        return (active != null, active, dir, file, captureFps);
    }

    // ----- Frame type / calibration (CCD_FRAME_TYPE) -----

    /// <summary>Set the INDI standard CCD_FRAME_TYPE switch so the driver writes the correct
    /// IMAGETYP keyword into the FITS header. Most drivers also use this to skip shutter
    /// actuation for dark/bias frames (ZWO/PlayerOne electronically close the sensor).</summary>
    public async Task SetFrameTypeAsync(string deviceName, string imageType, CancellationToken ct)
    {
        var client = _client; if (client == null) return;
        var element = imageType.ToUpperInvariant() switch
        {
            "DARK" => "FRAME_DARK",
            "BIAS" => "FRAME_BIAS",
            "FLAT" => "FRAME_FLAT",
            _      => "FRAME_LIGHT"
        };
        var all = new[] { "FRAME_LIGHT", "FRAME_DARK", "FRAME_BIAS", "FRAME_FLAT" };
        var tuples = all.Select(n => (n, n == element)).ToArray();
        try { await client.SetSwitchManyAsync(deviceName, "CCD_FRAME_TYPE", tuples, ct); }
        catch { /* not all drivers expose this — best-effort */ }
    }

    // The client this handler is attached to. A reconnect creates a brand-new
    // IndiClient with an empty event list; comparing instances (instead of a
    // sticky bool, the old bug) re-registers on the new client — otherwise every
    // capture after an indiserver bounce hung to its full timeout, forever.
    // (Verified live: kill indiserver -> reconnect -> capture dead until restart.)
    private IndiClient? _blobHandlerClient;
    private readonly object _blobHandlerLock = new();

    private void EnsureBlobHandler()
    {
        var client = _client;
        if (client == null) return;
        lock (_blobHandlerLock)
        {
            if (ReferenceEquals(_blobHandlerClient, client)) return;
            client.BlobReceived += (device, prop, el, bytes, format) =>
            {
                if (prop != "CCD1") return; // INDI convention: primary sensor BLOB property
                // Streaming and still capture share CCD1. Stream frames carry ".stream" /
                // ".stream_jpg" formats and belong to CameraStreamService — never to the
                // exposure TCS. Without this filter an orphaned stream wedges the next
                // capture: the stream BLOB resolves the TCS with garbage bytes, then the
                // real .fits BLOB arrives with no waiter and is dropped.
                if (format != null && format.StartsWith(".stream", StringComparison.Ordinal)) return;
                if (_pendingExposure.TryRemove(device, out var tcs)) tcs.TrySetResult((bytes, format));
            };
            _blobHandlerClient = client;
        }
    }

    /// <summary>Start an exposure and await the BLOB delivery. Returns the raw bytes (usually
    /// a .fits file) and the driver-reported format string. Throws on timeout / driver error.</summary>
    public async Task<(byte[] bytes, string? format)> CameraExposeAsync(
        string deviceName, double exposureSeconds, CancellationToken ct)
    {
        var client = _client ?? throw new InvalidOperationException("INDI client not connected");
        EnsureBlobHandler();

        // One exposure per device at a time. Two overlapping captures used to
        // overwrite each other's completion source: the loser hung to its full
        // timeout and its abort then killed the winner's in-flight exposure.
        var gate = _exposureGates.GetOrAdd(deviceName, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {

        // Ensure indiserver streams BLOBs for this device (off by default).
        try { await client.EnableBlobAsync(deviceName, ct); } catch { }

        var tcs = new TaskCompletionSource<(byte[] bytes, string? format)>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingExposure[deviceName] = tcs;

        try
        {
            await client.SetNumberAsync(deviceName, "CCD_EXPOSURE", "CCD_EXPOSURE_VALUE", exposureSeconds, ct);

            // Wait for BLOB arrival. Budget = exposure + generous download margin.
            // Bumped from +30s to +90s because PlayerOne / large-sensor CMOS readout
            // (uncompressed 12MP+ FITS over USB) can comfortably take 30-60s on its
            // own; the previous margin tripped 504s on otherwise healthy 30s captures.
            var timeout = TimeSpan.FromSeconds(Math.Max(15, exposureSeconds + 90));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            using var reg = cts.Token.Register(() => tcs.TrySetCanceled(cts.Token));
            try
            {
                return await tcs.Task;
            }
            catch (OperationCanceledException)
            {
                // Tell the driver to abort the in-flight exposure. Without this,
                // a stuck driver keeps the BUSY state and the next capture is
                // refused or wedges the same way.
                try { await AbortExposureAsync(deviceName, CancellationToken.None); } catch { }
                throw;
            }
        }
        finally
        {
            // Remove OUR completion source only — by this point no other call
            // can own the slot (serialized above), but the guard is cheap and
            // prevents a late BLOB from a timed-out capture resolving the NEXT
            // capture with the previous exposure's image.
            _pendingExposure.TryRemove(deviceName, out _);
        }

        }
        finally { gate.Release(); }
    }

    public async Task SetGainAsync(string deviceName, int gain, CancellationToken ct)
    {
        var client = _client; if (client == null) return;
        // PlayerOne exposes gain via CCD_CONTROLS.Gain; other drivers use CCD_GAIN.GAIN.
        // The element list under CCD_CONTROLS arrives over multiple INDI def messages
        // after CONNECT — early callers (stream-start during camera connect) can hit
        // this with the property registered but Gain element not yet populated. Wait
        // up to 2 s for one of the known shapes to appear; without this the
        // server's gain override silently no-ops on the first stream of a session.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            var dev = client.GetDevice(deviceName);
            if (dev?.Properties.ContainsKey("CCD_CONTROLS") == true && dev.Properties["CCD_CONTROLS"]["Gain"] != null)
            {
                await client.SetNumberAsync(deviceName, "CCD_CONTROLS", "Gain", gain, ct);
                return;
            }
            if (dev?.Properties.ContainsKey("CCD_GAIN") == true && dev.Properties["CCD_GAIN"]["GAIN"] != null)
            {
                await client.SetNumberAsync(deviceName, "CCD_GAIN", "GAIN", gain, ct);
                return;
            }
            try { await Task.Delay(100, ct); } catch (OperationCanceledException) { return; }
        }
        _log.LogWarning("SetGainAsync: no gain property found on {Device} after 2 s wait — request gain={Gain} dropped", deviceName, gain);
    }

    public async Task SetOffsetAsync(string deviceName, int offset, CancellationToken ct)
    {
        var client = _client; if (client == null) return;
        var dev = client.GetDevice(deviceName);
        if (dev?.Properties.ContainsKey("CCD_CONTROLS") == true && dev.Properties["CCD_CONTROLS"]["Offset"] != null)
            await client.SetNumberAsync(deviceName, "CCD_CONTROLS", "Offset", offset, ct);
        else if (dev?.Properties.ContainsKey("CCD_OFFSET") == true)
            await client.SetNumberAsync(deviceName, "CCD_OFFSET", "OFFSET", offset, ct);
    }

    public async Task SetBinningAsync(string deviceName, int binX, int binY, CancellationToken ct)
    {
        var client = _client; if (client == null) return;
        var xml = $"<newNumberVector device=\"{Escape(deviceName)}\" name=\"CCD_BINNING\">" +
                  $"<oneNumber name=\"HOR_BIN\">{binX}</oneNumber>" +
                  $"<oneNumber name=\"VER_BIN\">{binY}</oneNumber>" +
                  $"</newNumberVector>";
        await client.SendAsync(xml, ct);
    }

    /// <summary>Set CCD_FRAME (X/Y/WIDTH/HEIGHT) for sub-frame read-out.
    /// Planetary lucky-imaging only reads a small window around the target;
    /// the smaller the frame, the higher the achievable fps.</summary>
    public async Task SetSubFrameAsync(string deviceName, int x, int y, int width, int height, CancellationToken ct)
    {
        var client = _client; if (client == null) return;
        var xml = $"<newNumberVector device=\"{Escape(deviceName)}\" name=\"CCD_FRAME\">" +
                  $"<oneNumber name=\"X\">{x}</oneNumber>" +
                  $"<oneNumber name=\"Y\">{y}</oneNumber>" +
                  $"<oneNumber name=\"WIDTH\">{width}</oneNumber>" +
                  $"<oneNumber name=\"HEIGHT\">{height}</oneNumber>" +
                  $"</newNumberVector>";
        await client.SendAsync(xml, ct);
    }

    /// <summary>Read current sensor max resolution from CCD_INFO. Used to
    /// translate normalized ROI fractions into pixel coordinates.</summary>
    public (int width, int height)? GetSensorSize(string deviceName)
    {
        var client = _client; if (client == null) return null;
        var dev = client.GetDevice(deviceName);
        if (dev == null) return null;
        if (!dev.Properties.TryGetValue("CCD_INFO", out var info)) return null;
        var w = (int)(info["CCD_MAX_X"]?.AsDouble ?? 0);
        var h = (int)(info["CCD_MAX_Y"]?.AsDouble ?? 0);
        return (w > 0 && h > 0) ? (w, h) : null;
    }

    /// <summary>Current CCD_FRAME (subframe) as reported by the driver — the
    /// applied ROI, as opposed to CCD_INFO's full sensor size.</summary>
    public (int width, int height)? GetCcdFrame(string deviceName)
    {
        var client = _client; if (client == null) return null;
        var dev = client.GetDevice(deviceName);
        if (dev == null) return null;
        if (!dev.Properties.TryGetValue("CCD_FRAME", out var frame)) return null;
        var w = (int)(frame["WIDTH"]?.AsDouble ?? 0);
        var h = (int)(frame["HEIGHT"]?.AsDouble ?? 0);
        return (w > 0 && h > 0) ? (w, h) : null;
    }

    public async Task SetCoolingAsync(string deviceName, bool enabled, double? targetTemperature, CancellationToken ct)
    {
        var client = _client; if (client == null) return;
        var dev = client.GetDevice(deviceName);

        if (targetTemperature.HasValue && dev?.Properties.ContainsKey("CCD_TEMPERATURE") == true)
        {
            await client.SetNumberAsync(deviceName, "CCD_TEMPERATURE", "CCD_TEMPERATURE_VALUE", targetTemperature.Value, ct);
        }
        if (dev?.Properties.ContainsKey("CCD_COOLER") == true)
        {
            await client.SetSwitchManyAsync(deviceName, "CCD_COOLER",
                new[] { ("COOLER_ON", enabled), ("COOLER_OFF", !enabled) }, ct);
        }
    }

    private static string Escape(string s)
        => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;");
}
