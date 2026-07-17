using System.Text;
using System.Text.Json;
using NINA.Headless.Services.Remote;

namespace NINA.Headless.Services;

/// <summary>Server-side sequence executor. The imaging plan used to run inside
/// the iOS app (slew → wait → capture loop), which meant a sleeping phone or a
/// dropped session killed the night's plan. The app now uploads the plan and
/// this service drives it through the server's OWN verified REST endpoints via
/// loopback — slew/settle, per-frame filter positioning, PHD2-gated dither,
/// capture — so execution survives the phone and any client can attach to watch
/// progress. Failures abort honestly with the reason in status/lastError.</summary>
public class SequenceExecutionService
{
    public record WireExposure(string? FilterName, double ExposureTimeSec, int Count,
                               int Binning, int Gain, int Offset, string? Type,
                               bool DitherEnabled, int DitherEveryN);
    public record WireTarget(string Name, double Ra, double Dec, List<WireExposure> Exposures);
    public record WirePlan(string Name, List<WireTarget> Targets);

    public record Snapshot(bool Running, string? PlanName, string? CurrentTarget, string? CurrentFilter,
                           int FramesCompleted, int FramesTotal, double Progress, string? LastError,
                           DateTime? StartedAt);

    private readonly object _lock = new();
    private readonly ILogger<SequenceExecutionService> _log;
    private readonly RemoteEventBus _events;
    private readonly HttpClient _loopback;

    private WirePlan? _plan;
    private CancellationTokenSource? _cts;
    private bool _running;
    private string? _currentTarget, _currentFilter, _lastError;
    private int _framesCompleted, _framesTotal;
    private DateTime? _startedAt;

    public SequenceExecutionService(ILogger<SequenceExecutionService> log, RemoteEventBus events)
    {
        _log = log;
        _events = events;
        _loopback = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:1888"), Timeout = TimeSpan.FromMinutes(10) };
    }

    public Snapshot GetSnapshot()
    {
        lock (_lock)
        {
            return new Snapshot(_running, _plan?.Name, _currentTarget, _currentFilter,
                _framesCompleted, _framesTotal,
                _framesTotal > 0 ? (double)_framesCompleted / _framesTotal : 0,
                _lastError, _startedAt);
        }
    }

    public bool LoadPlan(WirePlan plan)
    {
        if (plan.Targets.Count == 0 || plan.Targets.Any(t => t.Exposures.Count == 0)) return false;
        lock (_lock)
        {
            if (_running) return false;
            _plan = plan;
            _lastError = null;
            _framesCompleted = 0;
            _framesTotal = plan.Targets.Sum(t => t.Exposures
                .Where(e => string.IsNullOrEmpty(e.Type) || e.Type.Equals("light", StringComparison.OrdinalIgnoreCase))
                .Sum(e => Math.Max(0, e.Count)));
        }
        return true;
    }

    public bool Start()
    {
        lock (_lock)
        {
            if (_running || _plan == null) return false;
            _running = true;
            _lastError = null;
            _framesCompleted = 0;
            _startedAt = DateTime.UtcNow;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _ = Task.Run(() => RunAsync(token), token);
        }
        Broadcast();
        return true;
    }

    public bool Stop()
    {
        CancellationTokenSource? cts;
        lock (_lock)
        {
            if (!_running) return false;
            cts = _cts;
        }
        cts?.Cancel();
        // Abort any in-flight exposure so cancel is prompt, not after minutes.
        _ = _loopback.PostAsync("/api/v1/camera/abort", null);
        return true;
    }

    private async Task RunAsync(CancellationToken token)
    {
        WirePlan plan;
        lock (_lock) { plan = _plan!; }
        try
        {
            foreach (var target in plan.Targets)
            {
                token.ThrowIfCancellationRequested();
                lock (_lock) { _currentTarget = target.Name; }
                Broadcast();

                await SlewAndSettleAsync(target, token);

                foreach (var exp in target.Exposures)
                {
                    if (!(string.IsNullOrEmpty(exp.Type) || exp.Type!.Equals("light", StringComparison.OrdinalIgnoreCase)))
                        continue; // flats/darks run through their dedicated wizards
                    lock (_lock) { _currentFilter = string.IsNullOrEmpty(exp.FilterName) ? null : exp.FilterName; }

                    for (int frame = 0; frame < exp.Count; frame++)
                    {
                        token.ThrowIfCancellationRequested();
                        var dither = exp.DitherEnabled && frame > 0 && exp.DitherEveryN > 0
                                     && frame % exp.DitherEveryN == 0;
                        await CaptureFrameAsync(target.Name, exp, dither, token);
                        lock (_lock) { _framesCompleted++; }
                        Broadcast();
                    }
                }
            }
            lock (_lock) { _running = false; _currentTarget = null; _currentFilter = null; }
            _log.LogInformation("Sequence '{Plan}' completed: {Frames} frames", plan.Name, _framesCompleted);
        }
        catch (OperationCanceledException)
        {
            lock (_lock) { _running = false; _lastError = "Stopped by user"; }
            _log.LogInformation("Sequence '{Plan}' stopped by user at frame {Frame}", plan.Name, _framesCompleted);
        }
        catch (Exception ex)
        {
            lock (_lock) { _running = false; _lastError = ex.Message; }
            _log.LogWarning(ex, "Sequence '{Plan}' aborted", plan.Name);
        }
        Broadcast();
    }

    private async Task SlewAndSettleAsync(WireTarget target, CancellationToken token)
    {
        var body = JsonSerializer.Serialize(new { ra = target.Ra, dec = target.Dec });
        using var resp = await _loopback.PostAsync("/api/v1/telescope/slew",
            new StringContent(body, Encoding.UTF8, "application/json"), token);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Slew to '{target.Name}' failed: {await ReadMessageAsync(resp)}");

        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            await Task.Delay(1000, token);
            using var st = await _loopback.GetAsync("/api/v1/telescope/status", token);
            using var doc = JsonDocument.Parse(await st.Content.ReadAsStringAsync(token));
            if (doc.RootElement.TryGetProperty("slewing", out var sl)
                && sl.ValueKind != JsonValueKind.True)
                return;
        }
        throw new Exception($"Slew to '{target.Name}' did not settle within 120s");
    }

    private async Task CaptureFrameAsync(string targetName, WireExposure exp, bool dither, CancellationToken token)
    {
        var body = JsonSerializer.Serialize(new
        {
            exposureTime = exp.ExposureTimeSec,
            gain = exp.Gain,
            offset = exp.Offset,
            binning = exp.Binning,
            filter = string.IsNullOrEmpty(exp.FilterName) ? null : exp.FilterName,
            ditherPixels = dither ? 5.0 : 0.0,
            objectName = targetName
        });
        using var resp = await _loopback.PostAsync("/api/v1/camera/capture",
            new StringContent(body, Encoding.UTF8, "application/json"), token);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"Capture failed on '{targetName}': {await ReadMessageAsync(resp)}");
    }

    private static async Task<string> ReadMessageAsync(HttpResponseMessage resp)
    {
        try
        {
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("message", out var m)) return m.GetString() ?? resp.StatusCode.ToString();
        }
        catch { }
        return resp.StatusCode.ToString();
    }

    private void Broadcast()
    {
        var s = GetSnapshot();
        _events.Broadcast("SequenceUpdate", new
        {
            running = s.Running,
            progress = s.Progress,
            currentTarget = s.CurrentTarget,
            currentFilter = s.CurrentFilter,
            framesCompleted = s.FramesCompleted,
            framesTotal = s.FramesTotal,
            lastError = s.LastError,
            timestamp = DateTime.UtcNow
        });
    }
}
