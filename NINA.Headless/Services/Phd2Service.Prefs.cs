// Phd2Service.Prefs.cs — persistent-preferences concern of Phd2Service: the full autotune
// apply orchestration (shutdown → write prefs → relaunch → re-push), PHD2 prefs/INI file
// plumbing (paths, settle wait, section-aware upsert), INDI bindings, and profile RPCs.

using System.Diagnostics;
using System.Text.Json;

namespace NINA.Headless.Services;

public partial class Phd2Service
{
    /// <summary>Apply the full autotune package: persist Advanced Settings values (cal step,
    /// max pulse, dec mode) to PHD2's on-disk prefs, restart PHD2, then push the runtime-
    /// settable params (aggressiveness, min-move, exposure). Exposed as one HTTP call so the
    /// iOS UI can show a single "Restarting PHD2…" progress state — the caller doesn't
    /// have to orchestrate the three-step write / restart / re-push sequence.</summary>
    public record ApplyFullResult(bool Ok, long DurationMs, IReadOnlyList<string> Applied, string? Error);

    public async Task<ApplyFullResult> ApplyFullAutotuneAsync(
        int calibrationStepMs, int maxRaDurationMs, int maxDecDurationMs,
        DecGuideMode decGuideMode,
        double exposureSeconds, double minMovePixels, double aggressivenessFraction,
        CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var applied = new List<string>();

        // Order is critical: PHD2 uses wxFileConfig which rewrites the entire prefs file
        // on process exit. Writing BEFORE PHD2 terminates lets its shutdown clobber our
        // changes silently. Shut it down, wait for the file to settle, THEN write.
        if (IsConnectedToServer)
        {
            try { await SendRpcAsync("shutdown", Array.Empty<object>(), ct); } catch { }
            await DisconnectAsync();
        }
        if (_phdProcess != null && !_phdProcess.HasExited)
        {
            try { _phdProcess.Kill(entireProcessTree: true); } catch { }
            try { _phdProcess.WaitForExit(5000); } catch { }
            try { _phdProcess.Dispose(); } catch { }
            _phdProcess = null;
        }
        else
        {
            try
            {
                using var p = System.Diagnostics.Process.Start(new ProcessStartInfo
                {
                    FileName = "/bin/sh",
                    Arguments = "-c \"pkill -TERM -i -x phd2\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                });
                if (p != null) await p.WaitForExitAsync(ct);
            }
            catch { }
        }

        await WaitForPrefsFileSettledAsync(ct);

        var writeOk = await WritePhd2PrefsAsync(calibrationStepMs, maxRaDurationMs, maxDecDurationMs, decGuideMode, ct);
        if (!writeOk) return new ApplyFullResult(false, sw.ElapsedMilliseconds, applied,
            "Could not write PHD2 prefs (Mac: ~/Library/Preferences/PHDGuidingV2 Preferences; Linux: ~/.config/PHD2 not found)");
        applied.Add($"CalibrationDuration={calibrationStepMs}ms (config)");
        applied.Add($"MaxRaDuration={maxRaDurationMs}ms (config)");
        applied.Add($"MaxDecDuration={maxDecDurationMs}ms (config)");
        applied.Add($"DecGuideMode={decGuideMode} (config)");

        // Give the OS a moment to release port 4400.
        try { await Task.Delay(1500, ct); } catch { }

        // 30s budget covers cold-start on slower hardware (RPi).
        var relaunchStart = sw.ElapsedMilliseconds;
        LaunchResult lr = LaunchResult.Timeout;
        for (int attempt = 0; attempt < 2 && (sw.ElapsedMilliseconds - relaunchStart) < 30000; attempt++)
        {
            lr = await EnsureStartedDetailedAsync(ct);
            if (lr == LaunchResult.Connected) break;
            try { await Task.Delay(2000, ct); } catch { break; }
        }
        if (lr != LaunchResult.Connected)
            return new ApplyFullResult(false, sw.ElapsedMilliseconds, applied, $"PHD2 did not reconnect after restart ({lr})");
        applied.Add("PHD2 restarted + reconnected");

        // 4. Push runtime params now that we're back online. Exposure / dec-mode / per-axis
        // pushes are independent — run them in parallel.
        async Task PushAxis(GuideAxis axis)
        {
            var names = await GetAlgoParamNamesAsync(axis, ct);
            if (names == null) return;
            if (names.Contains("aggressiveness") &&
                await SetAlgoParamAsync(axis, "aggressiveness", aggressivenessFraction, ct))
                lock (applied) applied.Add($"{axis.Wire()}/aggressiveness={aggressivenessFraction * 100:F0}%");
            if (names.Contains("minMove") &&
                await SetAlgoParamAsync(axis, "minMove", minMovePixels, ct))
                lock (applied) applied.Add($"{axis.Wire()}/minMove={minMovePixels:F2}px");
        }

        var expTask = SetExposureAsync(exposureSeconds * 1000, ct);
        var decTask = SetDecGuideModeAsync(decGuideMode, ct);
        var raTask = PushAxis(GuideAxis.RA);
        var decAxisTask = PushAxis(GuideAxis.Dec);
        await Task.WhenAll(expTask, decTask, raTask, decAxisTask);
        if (await expTask) applied.Add($"exposure={exposureSeconds:F1}s");
        if (await decTask) applied.Add($"set_dec_guide_mode={decGuideMode}");

        return new ApplyFullResult(true, sw.ElapsedMilliseconds, applied, null);
    }

    /// <summary>Write PHD2's persistent preferences. Platform-specific plumbing:
    ///   macOS: CFPreferences via /usr/bin/defaults.
    ///   Linux: wxConfig INI at ~/.config/PHD2/PHDGuidingV2.
    /// Keys below come from PHD2's <c>/Scope/...</c> branch (scope.cpp pConfig calls) —
    /// these are what the Advanced Settings dialog writes and what PHD2 reads at startup.</summary>
    /// <summary>
    /// Points PHD2 at an INDI guide camera + mount. PHD2 stores these PER PROFILE under
    /// keys like `/profile/{id}/indi/INDIcam` — writing to the global `/camera/INDIcam`
    /// does nothing (confirmed via PHD2 debug log). Caller must provide the active profile
    /// id (from <see cref="GetCurrentProfileIdAsync"/>) and must also call
    /// <see cref="ReloadProfileAsync"/> afterward so the running PHD2 picks up the change —
    /// PHD2 only reads profile prefs when a profile is loaded, not continuously.
    /// </summary>
    public async Task<bool> WriteIndiBindingsAsync(int profileId, string? guideCameraDeviceName, string? mountDeviceName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(guideCameraDeviceName) && string.IsNullOrWhiteSpace(mountDeviceName))
            return true;

        // PHD2 on macOS does NOT use the macOS defaults system — it writes to a wxFileConfig
        // INI file at ~/Library/Preferences/PHDGuidingV2 Preferences (same format as Linux,
        // just at a different path). Found via lsof on the running PHD2 process — the plist
        // exists but PHD2 never opens it.
        var ini = PhdPrefsPath();
        if (ini == null || !File.Exists(ini))
        {
            _log.LogWarning("PHD2 prefs file not found at {Path} — run PHD2 once to create it", ini ?? "(unknown)");
            return false;
        }

        try
        {
            // LastMenuChoice must be set per-profile to "INDI Camera" / "INDI Mount"
            // otherwise PHD2's gear dialog treats the slot as empty and fails with
            // "m_pCamera == NULL" regardless of what's in indi/INDIcam.
            var profileIndi = new Dictionary<string, string>
            {
                ["INDIhost"] = "localhost",
                ["INDIport"] = "7624"
            };
            var updates = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
            {
                [$"profile/{profileId}/indi"] = profileIndi
            };
            if (!string.IsNullOrWhiteSpace(guideCameraDeviceName))
            {
                profileIndi["INDIcam"] = guideCameraDeviceName;
                updates[$"profile/{profileId}/camera"] = new() { ["LastMenuChoice"] = "INDI Camera" };
            }
            if (!string.IsNullOrWhiteSpace(mountDeviceName))
            {
                profileIndi["INDImount"] = mountDeviceName;
                updates[$"profile/{profileId}/scope"] = new() { ["LastMenuChoice"] = "INDI Mount" };
            }
            await UpsertIniSectionsAsync(ini, updates, ct);
            return true;
        }
        catch (Exception ex) { _log.LogWarning(ex, "PHD2 INDI bindings INI write failed"); return false; }
    }

    /// <summary>Poll the prefs file's mtime until it stops changing for 300ms — wxFileConfig's
    /// write-on-destructor can lag after the PHD2 process "exits". Without this guard our
    /// subsequent prefs write races the tail end of PHD2's shutdown flush.</summary>
    private static async Task WaitForPrefsFileSettledAsync(CancellationToken ct, int maxWaitMs = 3000)
    {
        var path = PhdPrefsPath();
        if (path == null || !File.Exists(path)) return;
        var deadline = DateTime.UtcNow.AddMilliseconds(maxWaitMs);
        var lastMtime = File.GetLastWriteTimeUtc(path);
        while (DateTime.UtcNow < deadline)
        {
            try { await Task.Delay(300, ct); } catch { return; }
            var now = File.GetLastWriteTimeUtc(path);
            if (now == lastMtime) return;
            lastMtime = now;
        }
    }

    private static string? PhdPrefsPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (PlatformPaths.IsMacOS) return System.IO.Path.Combine(home, "Library/Preferences/PHDGuidingV2 Preferences");
        if (PlatformPaths.IsLinux) return System.IO.Path.Combine(home, ".config/PHD2/PHDGuidingV2");
        return null;
    }

    /// <summary>Current PHD2 profile id via `get_profile` RPC. PHD2 auto-creates a temp
    /// profile on first launch when no last-used profile is set, so this is the safest
    /// way to get the id that's actually live.</summary>
    public async Task<int?> GetCurrentProfileIdAsync(CancellationToken ct)
    {
        if (!IsConnectedToServer) return null;
        try
        {
            var result = await SendRpcAndAwaitAsync("get_profile", Array.Empty<object>(), ct);
            if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("id", out var idEl))
                return idEl.GetInt32();
        }
        catch (Exception ex) { _log.LogDebug(ex, "get_profile failed"); }
        return null;
    }

    /// <summary>Force PHD2 to re-read prefs for the given profile. Must be called after
    /// <see cref="WriteIndiBindingsAsync"/> — otherwise the running PHD2 keeps the
    /// cached empty camera/mount and `set_connected true` fails with "equipment failed
    /// to connect: camera".</summary>
    public async Task<bool> ReloadProfileAsync(int profileId, CancellationToken ct)
    {
        if (!IsConnectedToServer) return false;
        try
        {
            await SendRpcAndAwaitAsync("set_profile", new object[] { profileId }, ct);
            return true;
        }
        catch (Exception ex) { _log.LogWarning(ex, "set_profile {Id} failed", profileId); return false; }
    }

    /// <summary>Section-aware INI upsert. Keys are added under their section if absent,
    /// replaced in place if present. Sections themselves are appended when missing.</summary>
    private static async Task UpsertIniSectionsAsync(string path, Dictionary<string, Dictionary<string, string>> sections, CancellationToken ct)
    {
        var lines = await File.ReadAllLinesAsync(path, ct);
        var output = new List<string>(lines.Length + sections.Sum(s => s.Value.Count));
        var pending = sections.ToDictionary(s => s.Key, s => new Dictionary<string, string>(s.Value, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        string? current = null;

        void FlushPending(string section)
        {
            if (pending.TryGetValue(section, out var kv) && kv.Count > 0)
            {
                foreach (var (k, v) in kv) output.Add($"{k}={v}");
                kv.Clear();
            }
        }

        foreach (var raw in lines)
        {
            var trimmed = raw.TrimStart();
            if (trimmed.StartsWith("["))
            {
                if (current != null) FlushPending(current);
                var end = trimmed.IndexOf(']');
                current = end > 0 ? trimmed[1..end] : null;
                output.Add(raw);
                continue;
            }
            if (current != null && pending.TryGetValue(current, out var kv))
            {
                var eq = raw.IndexOf('=');
                if (eq > 0)
                {
                    var key = raw[..eq].Trim();
                    if (kv.TryGetValue(key, out var v))
                    {
                        output.Add($"{key}={v}");
                        kv.Remove(key);
                        continue;
                    }
                }
            }
            output.Add(raw);
        }
        if (current != null) FlushPending(current);

        // Sections that never appeared in the file.
        foreach (var (section, kv) in pending.Where(kv => kv.Value.Count > 0))
        {
            output.Add($"[{section}]");
            foreach (var (k, v) in kv) output.Add($"{k}={v}");
        }

        await File.WriteAllLinesAsync(path, output, ct);
    }

    private async Task<bool> WritePhd2PrefsAsync(int calStepMs, int maxRaMs, int maxDecMs, DecGuideMode decMode, CancellationToken ct)
    {
        var ini = PhdPrefsPath();
        if (ini == null || !File.Exists(ini))
        {
            _log.LogWarning("PHD2 prefs file not found at {Path} — run PHD2 once to create it", ini ?? "(unknown)");
            return false;
        }
        try
        {
            var updates = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Scope"] = new()
                {
                    ["CalibrationDuration"] = calStepMs.ToString(),
                    ["MaxRaDuration"] = maxRaMs.ToString(),
                    ["MaxDecDuration"] = maxDecMs.ToString(),
                    ["DecGuideMode"] = ((int)decMode).ToString()
                }
            };
            await UpsertIniSectionsAsync(ini, updates, ct);
            return true;
        }
        catch (Exception ex) { _log.LogWarning(ex, "PHD2 prefs INI write failed"); return false; }
    }
}
