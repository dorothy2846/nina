using System.Diagnostics;
using NINA.Headless.Services.Remote;

namespace NINA.Headless.Services;

/// <summary>
/// APT-backed update agent (Phase 1). Shells out to <c>apt-get</c> on Linux to list
/// and apply upgrades for the packages that make up an astellar install — nina-headless
/// itself plus libindi + vendor INDI drivers + PHD2.
///
/// Phase 1 assumes a writable root filesystem (standard Linux install on the operator's
/// mini-PC, pre-USB-appliance). Phase 2 will replace this with A/B image swap via
/// <c>rauc</c> once the USB image pipeline lands — at that point single-package apt
/// updates stop being the right granularity anyway.
///
/// On macOS (dev) every call short-circuits to "no updates available" so the iOS UI
/// stays exercisable without requiring a real package backend.
/// </summary>
public class SystemUpdateService
{
    private readonly ILogger<SystemUpdateService> _log;

    /// <summary>The set of apt packages we consider "part of astellar" for update
    /// purposes. Kept narrow so a poorly-timed tap doesn't pull unrelated system
    /// updates (kernel, libc) on top of whatever the user is doing.</summary>
    private static readonly string[] ManagedPackages = new[]
    {
        "nina-headless",
        "libindi1", "libindi-data", "libindi-plugins",
        "indi-bin",
        "indi-asi", "indi-playerone", "indi-qhy", "indi-pegasus",
        "indi-atik", "indi-sx", "indi-altair", "indi-toupbase",
        "indi-celestronaux", "indi-eqmod",
        "indi-3rdparty-libraries",
        "gphoto2", "phd2"
    };

    public SystemUpdateService(ILogger<SystemUpdateService> log)
    {
        _log = log;
    }

    public record Upgradable(string Package, string CurrentVersion, string CandidateVersion);
    public record CheckResult(bool Supported, string? Reason, Upgradable[] Upgradable);
    public record ApplyResult(bool Started, string? Reason, int? JobId);

    /// <summary>Current job state — null when nothing is running. The controller polls
    /// this from <c>/update/status</c> and streams log output over the WebSocket event
    /// bus, so the whole update session is observable from iOS.</summary>
    private UpdateJob? _job;
    private int _nextJobId = 1;
    private readonly object _lock = new();

    public class UpdateJob
    {
        public int Id { get; init; }
        public DateTime StartedAt { get; init; } = DateTime.UtcNow;
        public DateTime? FinishedAt { get; set; }
        public int? ExitCode { get; set; }
        public List<string> Log { get; } = new();
        public bool Running => FinishedAt == null;
    }

    public UpdateJob? CurrentJob { get { lock (_lock) return _job; } }

    public async Task<CheckResult> CheckAsync(CancellationToken ct)
    {
        if (!PlatformPaths.IsLinux)
            return new CheckResult(false, "Updates available on Linux production builds only.", Array.Empty<Upgradable>());
        if (!await CommandExistsAsync("apt-get", ct))
            return new CheckResult(false, "apt-get not found — not a Debian/Ubuntu system?", Array.Empty<Upgradable>());

        // apt-get update needs sudo; user-facing message if it fails beats silently
        // reporting "no updates" when the real answer is "we couldn't hit the repo".
        var update = await RunAsync("sudo", "-n apt-get update -qq", ct);
        if (update.ExitCode != 0)
            return new CheckResult(false, $"apt-get update failed: {FirstLine(update.Stderr)}", Array.Empty<Upgradable>());

        var list = await RunAsync("apt", "list --upgradable", ct);
        // apt list output: "package/repo version arch [upgradable from: old_version]"
        var upgradable = new List<Upgradable>();
        foreach (var line in list.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var slash = line.IndexOf('/');
            if (slash <= 0) continue;
            var pkg = line[..slash];
            if (!ManagedPackages.Contains(pkg, StringComparer.OrdinalIgnoreCase)) continue;

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            var candidate = parts[1];
            var from = "?";
            var idx = line.IndexOf("upgradable from:", StringComparison.Ordinal);
            if (idx > 0)
            {
                var tail = line[(idx + "upgradable from:".Length)..].TrimEnd(']', ' ').Trim();
                from = tail;
            }
            upgradable.Add(new Upgradable(pkg, from, candidate));
        }

        return new CheckResult(true, null, upgradable.ToArray());
    }

    /// <summary>Kick off an upgrade of our managed packages. Returns immediately with a
    /// job id; the actual work runs in the background. Caller polls <c>CurrentJob</c>
    /// or listens to the event bus. Only one job may run at a time — concurrent calls
    /// return the existing job id rather than starting a second apt-get.</summary>
    public ApplyResult BeginApply()
    {
        if (!PlatformPaths.IsLinux)
            return new ApplyResult(false, "Updates available on Linux production builds only.", null);

        lock (_lock)
        {
            if (_job?.Running == true)
                return new ApplyResult(false, "An update is already in progress.", _job.Id);

            var job = new UpdateJob { Id = _nextJobId++ };
            _job = job;
            _ = Task.Run(() => RunUpgradeAsync(job));
            return new ApplyResult(true, null, job.Id);
        }
    }

    private async Task RunUpgradeAsync(UpdateJob job)
    {
        try
        {
            var args = "-n apt-get install --only-upgrade -y " + string.Join(' ', ManagedPackages);
            _log.LogInformation("System update job {Id}: sudo {Args}", job.Id, args);
            var psi = new ProcessStartInfo
            {
                FileName = "sudo",
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                Environment = { ["DEBIAN_FRONTEND"] = "noninteractive" }
            };
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start apt-get");
            p.OutputDataReceived += (_, e) => { if (e.Data != null) AppendLog(job, e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) AppendLog(job, e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            await p.WaitForExitAsync();
            job.ExitCode = p.ExitCode;
        }
        catch (Exception ex)
        {
            AppendLog(job, $"[error] {ex.Message}");
            job.ExitCode = -1;
        }
        finally
        {
            job.FinishedAt = DateTime.UtcNow;
            _log.LogInformation("System update job {Id} finished exit={Code}", job.Id, job.ExitCode);
        }
    }

    private void AppendLog(UpdateJob job, string line)
    {
        lock (_lock)
        {
            // Cap log growth — a stuck apt can dump a lot. 2000 lines is plenty for
            // post-mortem; older entries rotate out.
            if (job.Log.Count >= 2000) job.Log.RemoveAt(0);
            job.Log.Add(line);
        }
    }

    private static async Task<bool> CommandExistsAsync(string cmd, CancellationToken ct)
    {
        var r = await RunAsync("/bin/sh", $"-c \"command -v {cmd}\"", ct);
        return r.ExitCode == 0 && !string.IsNullOrWhiteSpace(r.Stdout);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(string file, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {file}");
        var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return (p.ExitCode, await stdoutTask, await stderrTask);
    }

    private static string FirstLine(string s) => s.Split('\n', 2)[0].Trim();
}
