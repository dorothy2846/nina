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
    /// <summary>EndAtUtc / MinAltitudeDeg are per-target cutoffs: when either is
    /// hit between frames the plan moves on to the next target (Plan-Mode style)
    /// instead of finishing the frame count on a target that's setting.</summary>
    public record WireTarget(string Name, double Ra, double Dec, List<WireExposure> Exposures,
                             DateTime? EndAtUtc = null, double? MinAltitudeDeg = null);
    /// <summary>StartAtUtc waits for a wall-clock time; StartAtTwilight waits for
    /// astronomical darkness computed from the mount's site coordinates. Both may
    /// combine (whichever is later). FilterFocusOffsets maps filter name → focuser
    /// steps relative to any common reference; on filter change the focuser is
    /// nudged by the offset delta instead of re-running a full autofocus.</summary>
    public record WirePlan(string Name, List<WireTarget> Targets,
                           DateTime? StartAtUtc = null, bool StartAtTwilight = false,
                           Dictionary<string, int>? FilterFocusOffsets = null);

    public record Snapshot(bool Running, string? PlanName, string? CurrentTarget, string? CurrentFilter,
                           int FramesCompleted, int FramesTotal, double Progress, string? LastError,
                           DateTime? StartedAt, DateTime? WaitingUntil, string? WaitingReason);

    private readonly object _lock = new();
    private readonly ILogger<SequenceExecutionService> _log;
    private readonly RemoteEventBus _events;
    private readonly ApnsPushService _push;
    private readonly HttpClient _loopback;

    private WirePlan? _plan;
    private CancellationTokenSource? _cts;
    private bool _running;
    private string? _currentTarget, _currentFilter, _lastError;
    private int _framesCompleted, _framesTotal;
    private DateTime? _startedAt;
    private DateTime? _waitingUntil;
    private string? _waitingReason;

    public SequenceExecutionService(ILogger<SequenceExecutionService> log, RemoteEventBus events, ApnsPushService push)
    {
        _log = log;
        _events = events;
        _push = push;
        _loopback = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:1888"), Timeout = TimeSpan.FromMinutes(10) };
    }

    public Snapshot GetSnapshot()
    {
        lock (_lock)
        {
            return new Snapshot(_running, _plan?.Name, _currentTarget, _currentFilter,
                _framesCompleted, _framesTotal,
                _framesTotal > 0 ? (double)_framesCompleted / _framesTotal : 0,
                _lastError, _startedAt, _waitingUntil, _waitingReason);
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
            await WaitForStartConditionAsync(plan, token);

            string? previousFilter = null;
            foreach (var target in plan.Targets)
            {
                token.ThrowIfCancellationRequested();
                lock (_lock) { _currentTarget = target.Name; }
                Broadcast();

                if (await TargetCutoffReachedAsync(target, token) is string preReason)
                {
                    _log.LogInformation("Sequence: skipping '{Target}' — {Reason}", target.Name, preReason);
                    continue;
                }

                await SlewAndSettleAsync(target, token);

                var targetDone = false;
                foreach (var exp in target.Exposures)
                {
                    if (targetDone) break;
                    if (!(string.IsNullOrEmpty(exp.Type) || exp.Type!.Equals("light", StringComparison.OrdinalIgnoreCase)))
                        continue; // flats/darks run through their dedicated wizards
                    lock (_lock) { _currentFilter = string.IsNullOrEmpty(exp.FilterName) ? null : exp.FilterName; }

                    await ApplyFilterFocusOffsetAsync(plan, previousFilter, exp.FilterName, token);
                    previousFilter = exp.FilterName;

                    for (int frame = 0; frame < exp.Count; frame++)
                    {
                        token.ThrowIfCancellationRequested();
                        if (await TargetCutoffReachedAsync(target, token) is string reason)
                        {
                            _log.LogInformation("Sequence: leaving '{Target}' early — {Reason}", target.Name, reason);
                            targetDone = true;
                            break;
                        }
                        var dither = exp.DitherEnabled && frame > 0 && exp.DitherEveryN > 0
                                     && frame % exp.DitherEveryN == 0;
                        await CaptureFrameAsync(target.Name, exp, dither, token);
                        lock (_lock) { _framesCompleted++; }
                        Broadcast();
                    }
                }
            }
            lock (_lock) { _running = false; _currentTarget = null; _currentFilter = null; _waitingUntil = null; _waitingReason = null; }
            _log.LogInformation("Sequence '{Plan}' completed: {Frames} frames", plan.Name, _framesCompleted);
            _ = _push.NotifyAllAsync("촬영 완료",
                $"'{plan.Name}' 시퀀스가 끝났습니다 ({_framesCompleted}프레임).",
                "sequence", bypassThrottle: true);
        }
        catch (OperationCanceledException)
        {
            lock (_lock) { _running = false; _lastError = "Stopped by user"; _waitingUntil = null; _waitingReason = null; }
            _log.LogInformation("Sequence '{Plan}' stopped by user at frame {Frame}", plan.Name, _framesCompleted);
        }
        catch (Exception ex)
        {
            lock (_lock) { _running = false; _lastError = ex.Message; _waitingUntil = null; _waitingReason = null; }
            _log.LogWarning(ex, "Sequence '{Plan}' aborted", plan.Name);
            _ = _push.NotifyAllAsync("촬영 중단됨",
                $"'{plan.Name}' 시퀀스가 {_framesCompleted}프레임에서 실패했습니다: {ex.Message}",
                "sequence", bypassThrottle: true);
        }
        Broadcast();
    }

    /// <summary>Blocks until the plan's start condition is met. Twilight needs the
    /// mount's site coordinates; refusing to guess, it fails the plan honestly if
    /// they're unavailable rather than starting a "dark sky" plan in daylight.</summary>
    private async Task WaitForStartConditionAsync(WirePlan plan, CancellationToken token)
    {
        DateTime? startAt = plan.StartAtUtc?.ToUniversalTime();

        if (plan.StartAtTwilight)
        {
            var site = await GetSiteCoordinatesAsync(token)
                ?? throw new Exception("천문박명 시작을 예약했지만 마운트의 사이트 좌표를 읽을 수 없습니다. 마운트 연결 및 GPS/사이트 설정을 확인해 주세요.");
            var twilight = SkyGeometry.NextAstronomicalTwilight(DateTime.UtcNow, site.Lat, site.Lon)
                ?? throw new Exception("앞으로 24시간 내에 천문박명이 오지 않습니다 (백야 기간). 예약 시작을 끄고 다시 시도해 주세요.");
            if (startAt == null || twilight > startAt) startAt = twilight;
        }

        if (startAt == null || startAt <= DateTime.UtcNow) return;

        lock (_lock)
        {
            _waitingUntil = startAt;
            _waitingReason = plan.StartAtTwilight ? "천문박명 대기" : "예약 시각 대기";
        }
        Broadcast();
        _log.LogInformation("Sequence '{Plan}' waiting until {Time:u} ({Reason})",
            plan.Name, startAt, plan.StartAtTwilight ? "astronomical twilight" : "scheduled start");

        while (DateTime.UtcNow < startAt)
        {
            token.ThrowIfCancellationRequested();
            var remaining = startAt.Value - DateTime.UtcNow;
            await Task.Delay(remaining > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : remaining, token);
        }

        lock (_lock) { _waitingUntil = null; _waitingReason = null; }
        Broadcast();
    }

    /// <summary>Returns a human-readable reason when the target's cutoff (wall
    /// clock or minimum altitude) has been reached, else null. Altitude checks
    /// silently pass when site coordinates are unavailable — an unreachable GPS
    /// mid-plan shouldn't kill targets that a time cutoff would have kept.</summary>
    private async Task<string?> TargetCutoffReachedAsync(WireTarget target, CancellationToken token)
    {
        if (target.EndAtUtc is DateTime endAt && DateTime.UtcNow >= endAt.ToUniversalTime())
            return $"종료 시각 {endAt:HH:mm} UTC 도달";

        if (target.MinAltitudeDeg is double minAlt)
        {
            var site = await GetSiteCoordinatesAsync(token);
            if (site != null)
            {
                var alt = SkyGeometry.AltitudeDeg(DateTime.UtcNow, target.Ra, target.Dec, site.Value.Lat, site.Value.Lon);
                if (alt < minAlt)
                    return $"고도 {alt:F1}° < 최저 {minAlt:F0}°";
            }
        }
        return null;
    }

    private async Task<(double Lat, double Lon)?> GetSiteCoordinatesAsync(CancellationToken token)
    {
        try
        {
            using var st = await _loopback.GetAsync("/api/v1/telescope/status", token);
            using var doc = JsonDocument.Parse(await st.Content.ReadAsStringAsync(token));
            if (doc.RootElement.TryGetProperty("siteLatitude", out var lat) && lat.ValueKind == JsonValueKind.Number &&
                doc.RootElement.TryGetProperty("siteLongitude", out var lon) && lon.ValueKind == JsonValueKind.Number)
            {
                var la = lat.GetDouble();
                var lo = lon.GetDouble();
                if (Math.Abs(la) > 0.001 || Math.Abs(lo) > 0.001) return (la, lo);
            }
        }
        catch { /* mount unreachable — caller decides whether that's fatal */ }
        return null;
    }

    /// <summary>Nudges the focuser by the delta between two filters' stored focus
    /// offsets (Plan-Mode "filter offset AF": one good focus run + per-filter
    /// offsets replaces a full autofocus on every filter change). No-ops when
    /// offsets are absent, the filter didn't change, or the focuser is missing —
    /// a plan without offsets behaves exactly as before.</summary>
    private async Task ApplyFilterFocusOffsetAsync(WirePlan plan, string? fromFilter, string? toFilter, CancellationToken token)
    {
        if (plan.FilterFocusOffsets is not { Count: > 0 } offsets) return;
        if (string.IsNullOrEmpty(toFilter) || fromFilter == toFilter) return;
        // First filter of the night: the user focused with it, so it IS the reference.
        if (fromFilter == null) return;

        offsets.TryGetValue(fromFilter, out var fromOffset);
        offsets.TryGetValue(toFilter, out var toOffset);
        var delta = toOffset - fromOffset;
        if (delta == 0) return;

        try
        {
            using var st = await _loopback.GetAsync("/api/v1/focuser/status", token);
            using var doc = JsonDocument.Parse(await st.Content.ReadAsStringAsync(token));
            if (!doc.RootElement.TryGetProperty("connected", out var conn) || conn.ValueKind != JsonValueKind.True)
            {
                _log.LogWarning("Sequence: filter offset {Delta:+#;-#} steps skipped — focuser not connected", delta);
                return;
            }
            if (!doc.RootElement.TryGetProperty("position", out var posEl) || posEl.ValueKind != JsonValueKind.Number)
            {
                _log.LogWarning("Sequence: filter offset skipped — focuser position unknown");
                return;
            }
            var newPosition = posEl.GetInt32() + delta;
            var body = JsonSerializer.Serialize(new { position = newPosition });
            using var resp = await _loopback.PostAsync("/api/v1/focuser/move",
                new StringContent(body, Encoding.UTF8, "application/json"), token);
            if (!resp.IsSuccessStatusCode)
                throw new Exception(await ReadMessageAsync(resp));
            _log.LogInformation("Sequence: filter {From}→{To}, focuser {Delta:+#;-#} steps to {Pos}",
                fromFilter, toFilter, delta, newPosition);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Focus offset is an optimization; a failed nudge shouldn't kill the
            // night. Log loudly and continue with the current focus position.
            _log.LogWarning("Sequence: filter focus offset {From}→{To} failed: {Error}", fromFilter, toFilter, ex.Message);
        }
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
            waitingUntil = s.WaitingUntil,
            waitingReason = s.WaitingReason,
            timestamp = DateTime.UtcNow
        });
    }
}
