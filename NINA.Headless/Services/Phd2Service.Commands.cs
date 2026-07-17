// Phd2Service.Commands.cs — guiding commands concern of Phd2Service: guide / calibrate /
// dither / stop / loop plus the high-level RPC wrappers (star image, exposure, pause,
// lock position, algorithm parameters, dec guide mode).

using System.Text.Json;

namespace NINA.Headless.Services;

public partial class Phd2Service
{
    /// <summary>RPC-level liveness: a modal dialog blocks PHD2's event loop
    /// while the TCP socket stays connected, so socket state alone lies.
    /// A short-deadline get_app_state answers "is the loop actually alive".</summary>
    public async Task<bool> ProbeResponsiveAsync(CancellationToken ct)
    {
        if (!IsConnectedToServer) return false;
        try
        {
            await SendRpcAndAwaitAsync("get_app_state", Array.Empty<object>(), ct, TimeSpan.FromSeconds(5));
            return true;
        }
        catch { return false; }
    }

    public async Task<bool> SetAllConnectedAsync(bool connected, CancellationToken ct)
    {
        if (!await EnsureStartedAsync(ct)) return false;
        try
        {
            await SendRpcAndAwaitAsync("set_connected", new object[] { connected }, ct, TimeSpan.FromSeconds(30));
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning("PHD2 set_connected({Connected}) failed: {Message}", connected, ex.Message);
            return false;
        }
    }

    public async Task<bool> StartGuidingAsync(CancellationToken ct)
    {
        if (!IsConnectedToServer) return false;
        // PHD2's guide RPC takes POSITIONAL params [settle, recalibrate]. The
        // old code wrapped both in one object, which PHD2 parsed AS the
        // settle param → "invalid settle params" — and the fire-and-forget
        // send discarded that error, so the API reported success while PHD2
        // never started guiding.
        try
        {
            await SendRpcAndAwaitAsync("guide",
                new object[] { new { pixels = 2.0, time = 5, timeout = 40 }, false }, ct);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning("PHD2 guide failed: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>Trigger PHD2 to re-calibrate + guide. PHD2 clears its existing calibration
    /// data, pulses the mount in each direction, measures the response, then enters guiding.
    /// Caller should poll <see cref="CurrentAppState"/> or await <see cref="WaitForAppStateAsync"/>.</summary>
    public async Task<bool> StartCalibrationAsync(CancellationToken ct)
    {
        if (!IsConnectedToServer) return false;
        try
        {
            await SendRpcAndAwaitAsync("clear_calibration", new object[] { "both" }, ct);
            await SendRpcAndAwaitAsync("guide",
                new object[] { new { pixels = 2.0, time = 5, timeout = 60 }, true }, ct);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning("PHD2 calibrate failed: {Message}", ex.Message);
            return false;
        }
    }

    /// <summary>Poll PHD2's reported AppState until it matches any of the expected values
    /// (or the timeout elapses). Used by the auto-calibrate orchestrator to block until
    /// PHD2 transitions from Calibrating → Guiding (success) or Stopped (failure).</summary>
    public async Task<string?> WaitForAppStateAsync(string[] expectedStates, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var state = CurrentAppState;
            if (expectedStates.Contains(state, StringComparer.OrdinalIgnoreCase)) return state;
            try { await Task.Delay(500, ct); } catch { return null; }
        }
        return null;
    }

    public async Task<bool> StopGuidingAsync(CancellationToken ct)
    {
        if (!IsConnectedToServer) return false;
        await SendRpcAsync("stop_capture", Array.Empty<object>(), ct);
        return true;
    }

    public async Task<bool> DitherAsync(double pixels, CancellationToken ct)
    {
        if (!IsConnectedToServer) return false;
        // amount + raOnly=false + settle-after = standard PHD2 dither args
        var param = new object[]
        {
            pixels, false, new { pixels = 2.0, time = 5, timeout = 40 }
        };
        try
        {
            await SendRpcAndAwaitAsync("dither", param, ct, TimeSpan.FromSeconds(60));
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning("PHD2 dither failed: {Message}", ex.Message);
            return false;
        }
    }

    // MARK: High-level RPC wrappers

    public record StarImage(byte[] Png, int Width, int Height, double StarX, double StarY, int Frame);

    /// <summary>Fetch the latest guide camera frame + selected star position from PHD2. The
    /// RPC returns raw 16-bit little-endian pixels which we base64-decode, autostretch, and
    /// PNG-encode so the iOS client can drop it straight into an Image view.</summary>
    public async Task<StarImage?> GetStarImageAsync(CancellationToken ct)
    {
        if (!IsConnectedToServer) return null;
        try
        {
            var result = await SendRpcAndAwaitAsync("get_star_image", Array.Empty<object>(), ct);
            if (result.ValueKind != JsonValueKind.Object) return null;
            int w = result.GetProperty("width").GetInt32();
            int h = result.GetProperty("height").GetInt32();
            int frame = result.TryGetProperty("frame", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetInt32() : 0;
            double sx = 0, sy = 0;
            if (result.TryGetProperty("star_pos", out var sp) && sp.ValueKind == JsonValueKind.Array && sp.GetArrayLength() >= 2)
            {
                sx = sp[0].GetDouble();
                sy = sp[1].GetDouble();
            }
            var b64 = result.GetProperty("pixels").GetString();
            if (string.IsNullOrEmpty(b64)) return null;
            var raw = Convert.FromBase64String(b64);
            if (raw.Length < 2 * w * h) return null;
            var pixels = new float[w * h];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = (ushort)(raw[2 * i] | (raw[2 * i + 1] << 8));
            var png = CalibrationLibrary.EncodeAutostretchPng(pixels, w, h);
            return new StarImage(png, w, h, sx, sy, frame);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "get_star_image failed");
            return null;
        }
    }

    public async Task<double?> GetExposureAsync(CancellationToken ct)
    {
        if (!IsConnectedToServer) return null;
        try
        {
            var r = await SendRpcAndAwaitAsync("get_exposure", Array.Empty<object>(), ct);
            // PHD2 returns milliseconds as a number.
            return r.ValueKind == JsonValueKind.Number ? r.GetDouble() : (double?)null;
        }
        catch { return null; }
    }

    public async Task<bool> SetExposureAsync(double exposureMs, CancellationToken ct)
    {
        if (!IsConnectedToServer) return false;
        try { await SendRpcAsync("set_exposure", new object[] { (int)exposureMs }, ct); return true; }
        catch { return false; }
    }

    public async Task<bool> SetPausedAsync(bool paused, CancellationToken ct)
    {
        if (!IsConnectedToServer) return false;
        // PHD2 set_paused signature: (bool paused, string dec_option). dec_option="full" pauses both.
        try { await SendRpcAsync("set_paused", new object[] { paused, "full" }, ct); return true; }
        catch { return false; }
    }

    public async Task<bool> ClearCalibrationAsync(CancellationToken ct)
    {
        if (!IsConnectedToServer) return false;
        try { await SendRpcAsync("clear_calibration", new object[] { "both" }, ct); return true; }
        catch { return false; }
    }

    /// <summary>Start the exposure-only loop (no guiding) — equivalent to PHD2's "Loop"
    /// button. Used for framing / focusing the guide camera before calibration.</summary>
    public async Task<bool> LoopAsync(CancellationToken ct)
    {
        if (!IsConnectedToServer) return false;
        try { await SendRpcAsync("loop", Array.Empty<object>(), ct); return true; }
        catch { return false; }
    }

    public async Task<bool> FindStarAsync(CancellationToken ct)
    {
        if (!IsConnectedToServer) return false;
        try { await SendRpcAsync("find_star", Array.Empty<object>(), ct); return true; }
        catch { return false; }
    }

    public async Task<bool> SetLockPositionAsync(double x, double y, CancellationToken ct)
    {
        if (!IsConnectedToServer) return false;
        // set_lock_position(x, y, exact=true) — exact locks at that precise pixel, else PHD2
        // snaps to the nearest star within search region.
        try { await SendRpcAsync("set_lock_position", new object[] { x, y, true }, ct); return true; }
        catch { return false; }
    }

    /// <summary>Push one algorithm parameter to PHD2. `axis` is "ra" or "dec", `name` is one
    /// of the params reported by get_algo_param_names for the currently-active algorithm
    /// (e.g. "aggressiveness", "hysteresis", "minMove" for Hysteresis). Values out of range
    /// are silently clamped server-side by PHD2.</summary>
    public async Task<bool> SetAlgoParamAsync(GuideAxis axis, string name, double value, CancellationToken ct)
    {
        if (!IsConnectedToServer) return false;
        try { await SendRpcAsync("set_algo_param", new object[] { axis.Wire(), name, value }, ct); return true; }
        catch { return false; }
    }

    public async Task<string[]?> GetAlgoParamNamesAsync(GuideAxis axis, CancellationToken ct)
    {
        if (!IsConnectedToServer) return null;
        try
        {
            var result = await SendRpcAndAwaitAsync("get_algo_param_names", new object[] { axis.Wire() }, ct);
            if (result.ValueKind != JsonValueKind.Array) return null;
            var list = new List<string>();
            foreach (var item in result.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String) list.Add(item.GetString()!);
            return list.ToArray();
        }
        catch { return null; }
    }

    public async Task<bool> SetDecGuideModeAsync(DecGuideMode mode, CancellationToken ct)
    {
        if (!IsConnectedToServer) return false;
        try { await SendRpcAsync("set_dec_guide_mode", new object[] { mode.ToString() }, ct); return true; }
        catch { return false; }
    }
}
