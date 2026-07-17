using System.Diagnostics;
using System.Text;

namespace NINA.Headless.Services;

/// <summary>
/// Installs PHD2 on demand so the guider has no manual deployment step.
/// Platform channels (each pulls the latest packaged release):
///   Linux   — ppa:pch/phd2 (upstream's own PPA) + apt, plus xvfb for the
///             headless X display PHD2 needs on a server box.
///   Windows — winget (OpenPHDGuiding.PHD2), silent.
///   macOS   — Homebrew cask (dev machines).
/// Runs in the background (installs take minutes); progress/log/status are
/// polled via /guider/install/status. Failures keep the full log tail so
/// the client can show WHY, not just "failed".
/// </summary>
public class Phd2InstallerService
{
    private readonly ILogger<Phd2InstallerService> _log;
    private readonly object _lock = new();
    private bool _installing;
    private bool? _lastSucceeded;
    private string? _lastError;
    private readonly List<string> _logTail = new();
    private const int LogTailMax = 40;

    public Phd2InstallerService(ILogger<Phd2InstallerService> log) { _log = log; }

    public object Status
    {
        get
        {
            lock (_lock)
            {
                return new
                {
                    installed = Phd2Service.IsInstalled,
                    installing = _installing,
                    lastSucceeded = _lastSucceeded,
                    lastError = _lastError,
                    log = _logTail.ToArray()
                };
            }
        }
    }

    /// <summary>Kick off an install if one isn't already running. Returns
    /// false when already installing (caller should poll status).</summary>
    public bool TryBeginInstall()
    {
        lock (_lock)
        {
            if (_installing) return false;
            _installing = true;
            _lastSucceeded = null;
            _lastError = null;
            _logTail.Clear();
        }
        _ = Task.Run(InstallAsync);
        return true;
    }

    private async Task InstallAsync()
    {
        try
        {
            bool ok;
            if (PlatformPaths.IsLinux) ok = await InstallLinuxAsync();
            else if (PlatformPaths.IsWindows) ok = await InstallWindowsAsync();
            else if (PlatformPaths.IsMacOS) ok = await InstallMacAsync();
            else
            {
                Fail("Unsupported platform for automatic PHD2 install");
                return;
            }

            lock (_lock)
            {
                _installing = false;
                _lastSucceeded = ok && Phd2Service.IsInstalled;
                if (_lastSucceeded == false && _lastError == null)
                    _lastError = "Installer finished but PHD2 was not found afterwards";
            }
            _log.LogInformation("PHD2 install finished: {Result}", _lastSucceeded == true ? "success" : _lastError);
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
        }
    }

    private void Fail(string message)
    {
        lock (_lock)
        {
            _installing = false;
            _lastSucceeded = false;
            _lastError = message;
        }
        _log.LogWarning("PHD2 install failed: {Message}", message);
    }

    private async Task<bool> InstallLinuxAsync()
    {
        // Appliance images run the server as root; interactive dev boxes may
        // not — prefix sudo -n (non-interactive) when we aren't root.
        var sudo = Environment.UserName == "root" ? "" : "sudo -n ";

        // The PPA carries upstream's latest release; distro archives lag by
        // years. Best-effort: if add-apt-repository is missing or the PPA
        // fails (offline mirror), plain apt still installs the archive build.
        await RunAsync("/bin/sh", $"-c \"{sudo}apt-get install -y software-properties-common\"");
        await RunAsync("/bin/sh", $"-c \"{sudo}add-apt-repository -y ppa:pch/phd2 || true\"");
        await RunAsync("/bin/sh", $"-c \"{sudo}apt-get update -qq\"");
        var rc = await RunAsync("/bin/sh", $"-c \"{sudo}apt-get install -y phd2 xvfb\"");
        if (rc != 0) { Fail($"apt-get install phd2 xvfb exited {rc}"); return false; }
        return true;
    }

    private async Task<bool> InstallWindowsAsync()
    {
        var rc = await RunAsync("winget",
            "install --id=OpenPHDGuiding.PHD2 --exact --silent --accept-source-agreements --accept-package-agreements");
        if (rc != 0) { Fail($"winget install exited {rc} — install manually from openphdguiding.org if winget is unavailable"); return false; }
        return true;
    }

    private async Task<bool> InstallMacAsync()
    {
        var brew = File.Exists("/opt/homebrew/bin/brew") ? "/opt/homebrew/bin/brew"
                 : File.Exists("/usr/local/bin/brew") ? "/usr/local/bin/brew" : null;
        if (brew == null) { Fail("Homebrew not found — install from https://openphdguiding.org"); return false; }
        var rc = await RunAsync(brew, "install --cask phd2");
        if (rc != 0) { Fail($"brew install --cask phd2 exited {rc}"); return false; }
        return true;
    }

    private async Task<int> RunAsync(string file, string args)
    {
        AppendLog($"$ {file} {args}");
        var psi = new ProcessStartInfo
        {
            FileName = file,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi);
        if (p == null) { AppendLog("(failed to start process)"); return -1; }

        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        foreach (var line in (await stdout).Split('\n').Concat((await stderr).Split('\n')))
            if (!string.IsNullOrWhiteSpace(line)) AppendLog(line.TrimEnd());
        AppendLog($"(exit {p.ExitCode})");
        return p.ExitCode;
    }

    private void AppendLog(string line)
    {
        lock (_lock)
        {
            _logTail.Add(line);
            while (_logTail.Count > LogTailMax) _logTail.RemoveAt(0);
        }
    }
}
