using NINA.Headless.Services.Remote;

namespace NINA.Headless.Services.Network;

/// <summary>
/// Hardware-free factory reset for the USB appliance. Counts boots that fail to
/// reach a "stable uptime" threshold; when three such boots happen back-to-back
/// within a short window, wipes AP config + paired devices so the owner can
/// recover a wedged device by just pulling power three times.
///
/// Mechanism:
///   On start:    read recent boot entries (&lt;2 min old). If ≥2 exist, this
///                is the 3rd consecutive rapid boot — trigger factory reset.
///                Otherwise append current timestamp.
///   After 60 s:  delete the entries file. Reaching 60 s of uptime is our
///                heuristic for "boot succeeded" — if the user is happy, they
///                won't be yanking power inside that window.
///
/// The counter file lives in <see cref="PlatformPaths.ConfigDir"/> which on the
/// USB image maps to <c>/persist/nina-headless</c> — the only RW path that
/// survives reboots. Don't put it in /tmp, that's tmpfs.
///
/// Clock skew: on first-boot-with-no-RTC (Pi without battery) timestamps can
/// jump backwards once NTP syncs. We treat any entry whose age is negative or
/// &gt; 5 min as stale and ignore it, so a clock correction mid-window doesn't
/// trigger a spurious reset.
/// </summary>
public class BootCounterService : IHostedService
{
    private readonly ApModeConfigStore _apStore;
    private readonly PairedDeviceStore _pairedStore;
    private readonly ILogger<BootCounterService> _log;
    private readonly string _counterPath;
    private readonly CancellationTokenSource _cts = new();

    private static readonly TimeSpan RapidWindow = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan StableUptime = TimeSpan.FromSeconds(60);
    private const int RapidBootThreshold = 2; // entries existing when we start → this is boot #3

    public BootCounterService(
        ApModeConfigStore apStore,
        PairedDeviceStore pairedStore,
        ILogger<BootCounterService> log)
    {
        _apStore = apStore;
        _pairedStore = pairedStore;
        _log = log;
        _counterPath = Path.Combine(PlatformPaths.ConfigDir, "boot-counter");
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var entries = ReadEntries();
            var now = DateTime.UtcNow;
            var recent = entries.Where(e => {
                var age = now - e;
                return age >= TimeSpan.Zero && age <= RapidWindow;
            }).ToList();

            if (recent.Count >= RapidBootThreshold)
            {
                _log.LogWarning("Rapid-boot factory reset: {Count} prior boots within {Window}",
                    recent.Count, RapidWindow);
                PerformFactoryReset();
                ClearEntries();
                return Task.CompletedTask;
            }

            recent.Add(now);
            WriteEntries(recent);
            _log.LogInformation("Boot counter: {Count}/{Threshold} within rapid window",
                recent.Count, RapidBootThreshold + 1);
        }
        catch (Exception ex)
        {
            // A failed counter read/write must NEVER block startup — the counter is a
            // recovery aid, not a correctness dependency.
            _log.LogWarning(ex, "Boot counter skipped");
        }

        _ = ClearAfterStableUptimeAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts.Cancel();
        return Task.CompletedTask;
    }

    private async Task ClearAfterStableUptimeAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(StableUptime, ct);
            ClearEntries();
            _log.LogDebug("Boot counter cleared after stable uptime");
        }
        catch (OperationCanceledException) { /* shutdown before stable — leave entries */ }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to clear boot counter"); }
    }

    private void PerformFactoryReset()
    {
        _apStore.ResetToDefaults();
        _pairedStore.Clear();
        _log.LogWarning("Factory reset complete: AP → {Ssid}, paired devices cleared",
            ApModeConfigStore.DefaultSsid);
    }

    private List<DateTime> ReadEntries()
    {
        if (!File.Exists(_counterPath)) return new();
        var result = new List<DateTime>();
        foreach (var line in File.ReadAllLines(_counterPath))
        {
            if (DateTime.TryParse(line, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                result.Add(dt.ToUniversalTime());
        }
        return result;
    }

    private void WriteEntries(IEnumerable<DateTime> entries)
    {
        var lines = entries.Select(e => e.ToUniversalTime().ToString("O"));
        File.WriteAllLines(_counterPath, lines);
    }

    private void ClearEntries()
    {
        try { if (File.Exists(_counterPath)) File.Delete(_counterPath); } catch { }
    }
}
