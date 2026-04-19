namespace NINA.Headless.Services.Network;

/// <summary>
/// Reliable AP config change with verify-and-rollback. The failure mode we're
/// protecting against is: user issues a config change, the nmcli apply appears
/// to succeed, but the AP doesn't actually broadcast (wrong band, driver quirk,
/// password rejected by WPA module). On a USB-boot appliance that's unreachable
/// once the user's phone loses the AP, so we must revert to a known-good state
/// without human intervention.
///
/// Flow:
///   1. Snapshot current config to ap.previous.json
///   2. Persist new config to ap.json (optimistic)
///   3. Call IApModeProvider.EnableAsync with the new config
///   4. Poll GetStatusAsync until it reports Active=true with matching SSID (timeout)
///   5. On any failure: restore ap.json from ap.previous.json, call EnableAsync on it
///
/// After a successful change the rollback snapshot is cleared so the next change
/// starts from the new baseline.
/// </summary>
public class ApModeChangeService
{
    private readonly IApModeProvider _provider;
    private readonly ApModeConfigStore _store;
    private readonly ILogger<ApModeChangeService> _log;

    public ApModeChangeService(IApModeProvider provider, ApModeConfigStore store, ILogger<ApModeChangeService> log)
    {
        _provider = provider;
        _store = store;
        _log = log;
    }

    public record Result(bool Success, string? ErrorReason, ApModeConfig AppliedConfig, bool RolledBack);

    /// <summary>Total time budget for verify. nmcli usually brings the profile up in
    /// 3–6 seconds on a Pi; 15s leaves headroom for slower adapters without leaving the
    /// user's phone in limbo too long.</summary>
    private static readonly TimeSpan VerifyTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    public async Task<Result> ChangeAsync(ApModeConfig newConfig, CancellationToken ct)
    {
        var previous = _store.Load();

        // If the AP isn't currently active we still want a rollback target. Persisted
        // "previous" = whatever Load() returned, which is either the last saved config
        // or the factory defaults.
        _store.SavePrevious(previous);
        _store.Save(newConfig);

        _log.LogInformation("AP change: applying ssid={Ssid}", newConfig.Ssid);

        if (!await _provider.EnableAsync(newConfig, ct))
        {
            await RollbackAsync(previous, "apply_failed", ct);
            return new Result(false, "apply_failed", previous, RolledBack: true);
        }

        if (!await WaitForActiveAsync(newConfig.Ssid, ct))
        {
            await RollbackAsync(previous, "verify_timeout", ct);
            return new Result(false, "verify_timeout", previous, RolledBack: true);
        }

        _store.ClearPrevious();
        _log.LogInformation("AP change committed: ssid={Ssid}", newConfig.Ssid);
        return new Result(true, null, newConfig, RolledBack: false);
    }

    private async Task<bool> WaitForActiveAsync(string expectedSsid, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + VerifyTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (ct.IsCancellationRequested) return false;
            try
            {
                var status = await _provider.GetStatusAsync(ct);
                if (status.Active && string.Equals(status.Ssid, expectedSsid, StringComparison.Ordinal))
                    return true;
            }
            catch (Exception ex) { _log.LogDebug(ex, "GetStatusAsync poll failed; will retry"); }
            try { await Task.Delay(PollInterval, ct); } catch (OperationCanceledException) { return false; }
        }
        return false;
    }

    private async Task RollbackAsync(ApModeConfig previous, string reason, CancellationToken ct)
    {
        _log.LogWarning("AP rollback ({Reason}): restoring ssid={Ssid}", reason, previous.Ssid);
        _store.Save(previous);
        try
        {
            // Best effort — if this also fails the boot-time self-test (Layer 2) or the
            // 3× power-cycle reset (Layer 3) are the remaining safety nets.
            await _provider.EnableAsync(previous, ct);
        }
        catch (Exception ex) { _log.LogError(ex, "Rollback re-enable failed"); }
    }
}
