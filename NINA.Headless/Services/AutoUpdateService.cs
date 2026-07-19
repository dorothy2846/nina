using System.Text.Json;

namespace NINA.Headless.Services;

/// <summary>
/// Unattended driver/system updates (the "internet available → keep drivers
/// fresh" half of the update story; SystemUpdateService is the manual half).
///
/// Policy:
///  - Checks apt every 6 hours (cheap `apt-get update` + candidate listing).
///  - Applies only when the observatory is IDLE: no live stream, no running
///    sequence, no update job already active. Imaging is never interrupted —
///    a busy check is retried on the next tick.
///  - After a successful apply the INDI server is restarted (again only while
///    idle) so upgraded driver binaries actually load; indiserver supervision
///    plus the app's reconnect flow handle the brief blip.
///  - On/off + timestamps persist in auto_update.json; the update controller
///    exposes GET/POST /api/v1/update/auto for the app.
/// macOS/dev boxes report "unsupported" from CheckAsync and the loop stays
/// quietly idle.
/// </summary>
public class AutoUpdateService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private readonly SystemUpdateService _updates;
    private readonly CameraStreamService _stream;
    private readonly SequenceExecutionService _sequence;
    private readonly IndiServerManager _indiServer;
    private readonly ILogger<AutoUpdateService> _log;

    private readonly object _lock = new();
    private readonly string _path;
    private State _state = new();

    public record State
    {
        public bool Enabled { get; init; } = true;
        public DateTime? LastCheckUtc { get; init; }
        public DateTime? LastApplyUtc { get; init; }
        public string? LastResult { get; init; }
        public int LastUpgradableCount { get; init; }
    }

    public AutoUpdateService(
        SystemUpdateService updates,
        CameraStreamService stream,
        SequenceExecutionService sequence,
        IndiServerManager indiServer,
        ILogger<AutoUpdateService> log)
    {
        _updates = updates;
        _stream = stream;
        _sequence = sequence;
        _indiServer = indiServer;
        _log = log;
        var dir = PlatformPaths.ConfigDir;
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "auto_update.json");
        Load();
    }

    public State Current { get { lock (_lock) return _state; } }

    public State SetEnabled(bool enabled)
    {
        lock (_lock)
        {
            _state = _state with { Enabled = enabled };
            Save();
            return _state;
        }
    }

    private bool IsObservatoryIdle()
        => !_stream.IsRunning
           && !_sequence.GetSnapshot().Running
           && _updates.CurrentJob?.Running != true;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the rest of the host settle before the first check.
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "AutoUpdate: tick failed");
            }

            try { await Task.Delay(TickInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        State snapshot;
        lock (_lock) snapshot = _state;
        if (!snapshot.Enabled) return;
        if (snapshot.LastCheckUtc is { } last && DateTime.UtcNow - last < CheckInterval) return;

        var check = await _updates.CheckAsync(ct);
        lock (_lock)
        {
            _state = _state with
            {
                LastCheckUtc = DateTime.UtcNow,
                LastUpgradableCount = check.Upgradable.Length,
                LastResult = check.Supported
                    ? (check.Upgradable.Length == 0 ? "up-to-date" : $"{check.Upgradable.Length} package(s) upgradable")
                    : $"unsupported: {check.Reason}"
            };
            Save();
        }

        if (!check.Supported || check.Upgradable.Length == 0) return;

        if (!IsObservatoryIdle())
        {
            _log.LogInformation("AutoUpdate: {Count} package(s) upgradable but observatory is busy — deferring",
                check.Upgradable.Length);
            // Re-check promptly once idle instead of waiting the full interval.
            lock (_lock) { _state = _state with { LastCheckUtc = DateTime.UtcNow - CheckInterval + TimeSpan.FromMinutes(30) }; Save(); }
            return;
        }

        _log.LogInformation("AutoUpdate: applying {Count} package update(s): {Packages}",
            check.Upgradable.Length, string.Join(", ", check.Upgradable.Select(u => u.Package)));

        var apply = _updates.BeginApply();
        if (!apply.Started)
        {
            _log.LogWarning("AutoUpdate: apply refused — {Reason}", apply.Reason);
            return;
        }

        // Wait for the job to finish (bounded).
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(30);
        while (_updates.CurrentJob?.Running == true && DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }

        var job = _updates.CurrentJob;
        var success = job is { Running: false, ExitCode: 0 };
        lock (_lock)
        {
            _state = _state with
            {
                LastApplyUtc = DateTime.UtcNow,
                LastResult = success ? "applied" : $"apply failed (exit {job?.ExitCode})"
            };
            Save();
        }

        if (!success)
        {
            _log.LogWarning("AutoUpdate: apply job did not succeed (exit {Exit}) — see update job log", job?.ExitCode);
            return;
        }

        // Load the freshly-installed driver binaries. Only while still idle —
        // if a session started mid-upgrade the restart waits for the next tick.
        if (IsObservatoryIdle())
        {
            _log.LogInformation("AutoUpdate: update applied — restarting INDI server to load new drivers");
            _indiServer.RestartServer();
        }
        else
        {
            _log.LogInformation("AutoUpdate: update applied; INDI restart deferred until idle");
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                _state = JsonSerializer.Deserialize<State>(File.ReadAllText(_path)) ?? new State();
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "AutoUpdate: failed to load {Path} — using defaults", _path);
            _state = new State();
        }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "AutoUpdate: failed to persist {Path}", _path);
        }
    }
}
