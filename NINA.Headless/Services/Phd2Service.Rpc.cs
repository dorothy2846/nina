// Phd2Service.Rpc.cs — JSON-RPC plumbing concern of Phd2Service: request send/await
// correlation, the socket read loop, event/result line dispatch, and building the
// state/status snapshot consumed by REST handlers.

using System.Text.Json;

namespace NINA.Headless.Services;

public partial class Phd2Service
{
    public object Snapshot()
    {
        lock (_stateLock)
        {
            var arr = _recentSteps.ToArray();
            // Rolling RMS over last 20 steps — matches what PHD2 shows on its own graph.
            double rmsRa = 0, rmsDec = 0, peakRa = 0, peakDec = 0;
            if (arr.Length > 0)
            {
                double sqRa = 0, sqDec = 0;
                foreach (var s in arr)
                {
                    sqRa += s.Dx * s.Dx;
                    sqDec += s.Dy * s.Dy;
                    var absDx = Math.Abs(s.Dx);
                    var absDy = Math.Abs(s.Dy);
                    if (absDx > peakRa) peakRa = absDx;
                    if (absDy > peakDec) peakDec = absDy;
                }
                rmsRa = Math.Sqrt(sqRa / arr.Length);
                rmsDec = Math.Sqrt(sqDec / arr.Length);
            }
            return new
            {
                connected = IsConnectedToServer,
                state = _appState,
                appState = _appState,
                running = _appState == "Guiding",
                pixelScale = _pixelScale,
                starMass = _starMass,
                snr = _snr,
                lastDx = _lastDx,
                lastDy = _lastDy,
                lastRaDuration = _lastRaDuration,
                lastDecDuration = _lastDecDuration,
                // Keys match GuiderStatusResponse in the iOS app (rmsRA uppercase A).
                rmsRA = rmsRa,
                rmsDec,
                rmsTotal = Math.Sqrt(rmsRa * rmsRa + rmsDec * rmsDec),
                peakRA = peakRa,
                peakDec,
                steps = arr.Select(s => new { t = s.At, dx = s.Dx, dy = s.Dy }).ToArray()
            };
        }
    }

    private async Task QueryInitialStateAsync(CancellationToken ct)
    {
        // These update state via incoming "jsonrpc":"2.0" "result" replies, handled the same way
        // as events in ReadLoopAsync.
        await SendRpcAsync("get_app_state", Array.Empty<object>(), ct);
        await SendRpcAsync("get_pixel_scale", Array.Empty<object>(), ct);
    }

    private async Task SendRpcAsync(string method, object[] parameters, CancellationToken ct)
    {
        if (_writer == null) throw new InvalidOperationException("Not connected to PHD2");
        int id;
        lock (_lock) id = ++_requestId;
        var payload = JsonSerializer.Serialize(new { method, @params = parameters, id });
        await _writer.WriteLineAsync(payload.AsMemory(), ct);
    }

    /// <summary>Send RPC and await its matching `{ "result": ..., "id": N }` reply. Used
    /// wherever the caller needs a value back (get_star_image, get_exposure, get_algo_param,
    /// etc.). Times out after 10s by default so a wedged PHD2 doesn't deadlock the HTTP
    /// request thread.</summary>
    private async Task<JsonElement> SendRpcAndAwaitAsync(string method, object[] parameters, CancellationToken ct, TimeSpan? timeout = null)
    {
        if (_writer == null) throw new InvalidOperationException("Not connected to PHD2");
        int id;
        lock (_lock) id = ++_requestId;
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRpc[id] = tcs;
        try
        {
            var payload = JsonSerializer.Serialize(new { method, @params = parameters, id });
            await _writer.WriteLineAsync(payload.AsMemory(), ct);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linkedCts.CancelAfter(timeout ?? TimeSpan.FromSeconds(10));
            try { return await tcs.Task.WaitAsync(linkedCts.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"PHD2 RPC '{method}' timed out");
            }
        }
        finally { _pendingRpc.TryRemove(id, out _); }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        if (_reader == null) return;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await _reader.ReadLineAsync(ct);
                if (line == null) break;
                HandleLine(line);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogWarning(ex, "PHD2 read loop error"); }
    }

    /// <summary>Parses one PHD2 JSON line. Two shapes:
    ///   event: { "Event": "GuideStep", "Timestamp": …, "dx": …, "dy": …, ... }
    ///   rpc-result: { "jsonrpc":"2.0", "result": ..., "id": N }
    /// Both update the same state snapshot.</summary>
    private void HandleLine(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (root.TryGetProperty("Event", out var evt))
            {
                var name = evt.GetString();
                switch (name)
                {
                    case "GuideStep":
                        RecordGuideStep(root);
                        break;
                    case "AppState":
                    case "GuidingDithered":
                    case "SettleDone":
                    case "StartGuiding":
                    case "StartCalibration":
                    case "CalibrationComplete":
                    case "Paused":
                    case "LoopingExposures":
                    case "LoopingExposuresStopped":
                    case "GuidingStopped":
                    case "StarLost":
                        if (root.TryGetProperty("State", out var st))
                            lock (_stateLock) _appState = st.GetString() ?? _appState;
                        else if (name == "StartGuiding")   lock (_stateLock) _appState = "Guiding";
                        else if (name == "GuidingStopped") lock (_stateLock) _appState = "Stopped";
                        else if (name == "Paused")         lock (_stateLock) _appState = "Paused";
                        else if (name == "StarLost")       lock (_stateLock) _appState = "LostLock";
                        else if (name == "StartCalibration") lock (_stateLock) _appState = "Calibrating";
                        break;
                }
                return;
            }

            // PHD2 error responses ({"error":{...},"id":N}) previously never
            // resolved the pending waiter — callers hit the 10s timeout and the
            // actual reason ("invalid settle params" etc.) evaporated.
            if (root.TryGetProperty("error", out var rpcError))
            {
                if (root.TryGetProperty("id", out var errIdEl) && errIdEl.TryGetInt32(out var errId)
                    && _pendingRpc.TryRemove(errId, out var errTcs))
                {
                    var msg = rpcError.TryGetProperty("message", out var m) ? m.GetString() : rpcError.GetRawText();
                    errTcs.TrySetException(new InvalidOperationException($"PHD2: {msg}"));
                }
                else
                {
                    _log.LogWarning("PHD2 RPC error (no waiter): {Error}", rpcError.GetRawText());
                }
                return;
            }

            if (root.TryGetProperty("result", out var result))
            {
                // Fire any pending RPC waiter for this id so SendRpcAndAwaitAsync callers
                // can consume the typed result. We clone the JsonElement via a string round-
                // trip because the underlying JsonDocument goes out of scope at end of this
                // function and would otherwise throw ObjectDisposedException on later access.
                if (root.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var rpcId)
                    && _pendingRpc.TryRemove(rpcId, out var tcs))
                {
                    var cloned = JsonDocument.Parse(result.GetRawText()).RootElement;
                    tcs.TrySetResult(cloned);
                }

                // Keep the legacy quick-state updates working for the fire-and-forget calls
                // that don't use SendRpcAndAwaitAsync (get_app_state, get_pixel_scale on connect).
                if (result.ValueKind == JsonValueKind.String)
                    lock (_stateLock) _appState = result.GetString() ?? _appState;
                else if (result.ValueKind == JsonValueKind.Number)
                    lock (_stateLock) _pixelScale = result.GetDouble();
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "PHD2: failed to parse line: {Line}", line);
        }
    }

    private void RecordGuideStep(JsonElement root)
    {
        double dx = root.TryGetProperty("dx", out var dxEl) && dxEl.ValueKind == JsonValueKind.Number ? dxEl.GetDouble() : 0;
        double dy = root.TryGetProperty("dy", out var dyEl) && dyEl.ValueKind == JsonValueKind.Number ? dyEl.GetDouble() : 0;
        double? raDur = root.TryGetProperty("RADuration", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetDouble() : (double?)null;
        double? decDur = root.TryGetProperty("DECDuration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : (double?)null;
        double? mass = root.TryGetProperty("StarMass", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetDouble() : (double?)null;
        double? snr = root.TryGetProperty("SNR", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : (double?)null;

        var step = new GuideStep(DateTime.UtcNow, dx, dy, raDur, decDur);
        lock (_stateLock)
        {
            _lastDx = dx; _lastDy = dy;
            _lastRaDuration = raDur; _lastDecDuration = decDur;
            _starMass = mass; _snr = snr;
            _recentSteps.Enqueue(step);
            while (_recentSteps.Count > 120) _recentSteps.Dequeue(); // ~4 min history at 2s cadence
            // A GuideStep without an explicit AppState implies we're still guiding.
            if (_appState != "Paused" && _appState != "LostLock") _appState = "Guiding";
        }
    }
}
