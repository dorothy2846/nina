// Phd2Service.Lifecycle.cs — connection/process lifecycle concern of Phd2Service: locating
// the PHD2 install, launching it hidden (macOS System Events window keeper / Linux Xvfb),
// connecting to the JSON-RPC server on :4400, and disconnect/teardown.

using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace NINA.Headless.Services;

public partial class Phd2Service
{
    public enum LaunchResult
    {
        Connected,
        NotInstalled,
        ServerNotEnabled,
        Timeout
    }

    public async Task<LaunchResult> EnsureStartedDetailedAsync(CancellationToken ct)
    {
        if (IsConnectedToServer) return LaunchResult.Connected;

        var install = GetInstallPath();
        if (install == null) return LaunchResult.NotInstalled;

        await LaunchIfNeededAsync(ct, install);

        // Retry connect for up to 15s while PHD2 finishes starting its server.
        for (var i = 0; i < 30; i++)
        {
            if (ct.IsCancellationRequested) return LaunchResult.Timeout;
            try
            {
                var tcp = new TcpClient();
                await tcp.ConnectAsync("localhost", ServerPort, ct);
                _tcp = tcp;
                var stream = tcp.GetStream();
                _reader = new StreamReader(stream, Encoding.UTF8);
                _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _readTask = Task.Run(() => ReadLoopAsync(_cts.Token));
                _log.LogInformation("PHD2: connected to localhost:{Port}", ServerPort);
                lock (_stateLock) { _appState = "Connected"; }
                _ = Task.Run(async () => { try { await QueryInitialStateAsync(ct); } catch { } });
                return LaunchResult.Connected;
            }
            catch (SocketException)
            {
                try { await Task.Delay(500, ct); } catch { return LaunchResult.Timeout; }
            }
        }
        // PHD2 is running (we launched it) but the JSON-RPC server never bound. PHD2 doesn't
        // auto-enable the server on first run — user has to toggle Tools → Enable Server once.
        // Setting persists forever after that.
        return IsPhdRunning() ? LaunchResult.ServerNotEnabled : LaunchResult.Timeout;
    }

    public async Task<bool> EnsureStartedAsync(CancellationToken ct)
        => await EnsureStartedDetailedAsync(ct) == LaunchResult.Connected;

    private enum InstallKind { MacApp, LinuxBinary, WindowsExe }
    private record InstallInfo(string Path, InstallKind Kind);

    /// <summary>Whether a PHD2 install is discoverable on this machine —
    /// exposed for the installer service and /guider/install status.</summary>
    public static bool IsInstalled => GetInstallPath() != null;

    private static InstallInfo? GetInstallPath()
    {
        // macOS bundle locations.
        var macCandidates = new[]
        {
            "/Applications/PHD2.app",
            "/opt/homebrew/Caskroom/phd2/2.6.14/PHD2.app",
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications/PHD2.app")
        };
        foreach (var p in macCandidates)
        {
            if (Directory.Exists(System.IO.Path.Combine(p, "Contents/MacOS")))
                return new InstallInfo(p, InstallKind.MacApp);
        }

        // Linux binary locations. PPA installs usually land at /usr/bin/phd2, snap at
        // /snap/bin/phd2, self-builds in /usr/local/bin. We also accept a $PHD2_BINARY
        // override for distros that install elsewhere (e.g. AUR on Arch).
        if (PlatformPaths.IsLinux)
        {
            var env = Environment.GetEnvironmentVariable("PHD2_BINARY");
            if (!string.IsNullOrEmpty(env) && File.Exists(env))
                return new InstallInfo(env, InstallKind.LinuxBinary);
            foreach (var p in new[] { "/usr/bin/phd2", "/usr/local/bin/phd2", "/snap/bin/phd2" })
                if (File.Exists(p)) return new InstallInfo(p, InstallKind.LinuxBinary);
        }

        // Windows installer default locations (InnoSetup: PHDGuiding2).
        if (PlatformPaths.IsWindows)
        {
            var winCandidates = new[]
            {
                Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\PHDGuiding2\phd2.exe"),
                Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\PHDGuiding2\phd2.exe"),
            };
            foreach (var p in winCandidates)
                if (File.Exists(p)) return new InstallInfo(p, InstallKind.WindowsExe);
        }

        return null;
    }

    private static bool IsPhdRunning()
    {
        if (PlatformPaths.IsWindows)
        {
            try { return Process.GetProcessesByName("phd2").Length > 0; }
            catch { return false; }
        }

        // macOS process name is "PHD2" (capitalized from the app bundle's MacOS exec); Linux
        // distros ship the binary lowercase. pgrep -i handles both without branching.
        try
        {
            using var ps = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "/bin/sh",
                    Arguments = "-c \"pgrep -i -x phd2\"",
                    RedirectStandardOutput = true,
                    UseShellExecute = false
                }
            };
            ps.Start();
            var output = ps.StandardOutput.ReadToEnd();
            ps.WaitForExit(500);
            return !string.IsNullOrWhiteSpace(output);
        }
        catch { return false; }
    }

    /// <summary>True if /usr/bin/xvfb-run or $XVFB_RUN points at an executable — we use it
    /// to give PHD2 a virtual X11 display on headless Linux servers so its wxWidgets window
    /// layer can initialize without a real monitor. Without this, PHD2 would abort with
    /// "Cannot open display" on a server-class Linux box.</summary>
    private static string? FindXvfbRun()
    {
        var env = Environment.GetEnvironmentVariable("XVFB_RUN");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
        foreach (var p in new[] { "/usr/bin/xvfb-run", "/usr/local/bin/xvfb-run" })
            if (File.Exists(p)) return p;
        return null;
    }

    public async Task DisconnectAsync()
    {
        StopWindowKeeper();
        try { _cts?.Cancel(); } catch { }
        try { _writer?.Dispose(); } catch { }
        try { _reader?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
        _writer = null; _reader = null; _tcp = null;
        if (_readTask != null)
        {
            try { await _readTask; } catch { }
            _readTask = null;
        }
        lock (_stateLock) { _appState = "Disconnected"; }
    }

    private async Task LaunchIfNeededAsync(CancellationToken ct, InstallInfo install)
    {
        try
        {
            using var probe = new TcpClient();
            await probe.ConnectAsync("localhost", ServerPort, ct);
            return;
        }
        catch (SocketException) { /* not running, launch */ }

        if (IsPhdRunning())
        {
            _log.LogInformation("PHD2 already running but server not responding on :{Port}", ServerPort);
            return;
        }

        if (install.Kind == InstallKind.MacApp)
            await LaunchMacAsync(install.Path, ct);
        else if (install.Kind == InstallKind.WindowsExe)
            LaunchWindows(install.Path);
        else
            await LaunchLinuxAsync(install.Path, ct);
    }

    /// <summary>Windows launch — minimized so the desktop stays clean. Server
    /// enablement (Tools → Enable Server) persists in PHD2's own config, so
    /// after the user toggles it once, every subsequent auto-launch is
    /// headless-equivalent. If it's off, connect returns the explanatory 503.</summary>
    private void LaunchWindows(string exePath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Minimized
            };
            Process.Start(psi);
            _log.LogInformation("PHD2 launched (Windows, minimized) from {Path}", exePath);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "PHD2 Windows launch failed");
        }
    }

    /// <summary>Hard-recover a wedged PHD2: a modal dialog (bad gear config,
    /// unexpected prompt) blocks the whole RPC event loop while the socket
    /// stays open — the only way out is to kill and relaunch. Re-connects
    /// equipment afterward so the guider is usable without user action.</summary>
    public async Task<bool> ForceRestartAsync(CancellationToken ct)
    {
        _log.LogWarning("PHD2: force restart (RPC unresponsive — modal dialog or hang)");
        await DisconnectAsync();
        KillPhdProcess();
        try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { return false; }

        var result = await EnsureStartedDetailedAsync(ct);
        if (result != LaunchResult.Connected)
        {
            _log.LogWarning("PHD2: relaunch after force-kill failed ({Result})", result);
            return false;
        }
        var gear = await SetAllConnectedAsync(true, ct);
        if (!gear) _log.LogWarning("PHD2: restarted but equipment reconnect failed");
        return true;
    }

    private void KillPhdProcess()
    {
        try
        {
            if (PlatformPaths.IsWindows)
            {
                foreach (var p in Process.GetProcessesByName("phd2"))
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                }
                return;
            }
            using var kill = Process.Start(new ProcessStartInfo
            {
                FileName = "/bin/sh",
                Arguments = "-c \"pkill -9 -i -x phd2\"",
                UseShellExecute = false
            });
            kill?.WaitForExit(3000);
        }
        catch (Exception ex) { _log.LogDebug(ex, "PHD2 kill failed"); }
    }

    private async Task LaunchMacAsync(string phdApp, CancellationToken ct)
    {
        // Pre-seed the "Enable Server" + port in PHD2's preferences plist BEFORE launch. PHD2
        // reads the plist at startup — without this, the user would have to manually toggle
        // Tools → Enable Server on first run. macOS keys: /perf/start_server (bool), /perf/server_port (int).
        try
        {
            await RunCliAsync("/usr/bin/defaults", "write org.openphdguiding.phd2 \"/perf/start_server\" -bool true", ct);
            await RunCliAsync("/usr/bin/defaults", "write org.openphdguiding.phd2 \"/perf/server_port\" -int " + ServerPort, ct);
        }
        catch (Exception ex) { _log.LogDebug(ex, "PHD2 defaults write skipped"); }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/usr/bin/open",
                // -g: don't steal focus. -j: launch "hidden" (Dock still shows but no
                // auto-raise on cmd-tab). Combined with the System Events hide below the
                // user never sees PHD2 unless they explicitly hunt for it.
                Arguments = $"-gj \"{phdApp}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            _phdProcess = Process.Start(psi);
            _log.LogInformation("PHD2 launched from {Path}", phdApp);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to launch PHD2");
            return;
        }

        // PHD2 still renders its main window on launch even with open -gj, and re-shows
        // itself on state changes (set_connected opens a camera frame window, etc.) — so
        // a one-shot hide isn't enough. Kick off a keeper that repeatedly hides + minis +
        // moves windows off-screen for as long as PHD2 is alive.
        try { await Task.Delay(2500, ct); } catch { return; }
        StartWindowKeeper();
    }

    /// <summary>Background task that keeps PHD2 invisible. macOS-specific — Linux uses
    /// Xvfb which makes this unnecessary. Three techniques stacked for reliability:
    /// <list type="bullet">
    ///   <item>App-level hide via <c>System Events</c> (equivalent to Cmd-H). PHD2 un-hides
    ///   itself when opening new windows, so this must be re-applied.</item>
    ///   <item>Per-window miniaturize — catches child windows (camera frame, graph)
    ///   that aren't affected by the app-level hide.</item>
    ///   <item>Off-screen position — last-resort defense if System Events is denied
    ///   Automation permission; windows exist but are drawn at (-5000, -5000).</item>
    /// </list>
    /// Runs every 1 second while PHD2 is alive; stops on disconnect or process exit.</summary>
    /// AppleScript used by the macOS window keeper. Multi-line via <c>osascript -e</c> is
    /// fragile (embedded newlines, quote escaping in .NET's arg parser); a file path
    /// sidesteps all of that. Written once per process at first keeper start — the
    /// contents are static so rewriting on every reconnect is pure waste.
    private const string HideScript =
        "tell application \"System Events\"\n" +
        "  if (exists process \"PHD2\") then\n" +
        "    tell process \"PHD2\"\n" +
        "      try\n        set visible to false\n      end try\n" +
        "      try\n" +
        "        repeat with w in windows\n" +
        "          try\n            set position of w to {-5000, -5000}\n          end try\n" +
        "          try\n            set value of attribute \"AXMinimized\" of w to true\n          end try\n" +
        "        end repeat\n" +
        "      end try\n" +
        "    end tell\n" +
        "  end if\n" +
        "end tell\n";

    private static readonly string HideScriptPath =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nina-phd2-hide.scpt");
    private static int _hideScriptWritten;

    private void StartWindowKeeper()
    {
        if (!PlatformPaths.IsMacOS) return;
        _windowKeeperCts?.Cancel();
        _windowKeeperCts = new CancellationTokenSource();
        var ct = _windowKeeperCts.Token;

        if (Interlocked.Exchange(ref _hideScriptWritten, 1) == 0)
        {
            try { File.WriteAllText(HideScriptPath, HideScript); }
            catch (Exception ex) { _log.LogWarning(ex, "Failed to write PHD2 hide script"); _hideScriptWritten = 0; return; }
        }
        var scriptPath = HideScriptPath;

        // Note: we don't gate on `_phdProcess.HasExited` — `_phdProcess` is the `open`
        // invocation, not PHD2 itself. `open` dispatches to launchd and exits after ~1s
        // while PHD2 keeps running. The script is a no-op when PHD2 isn't running (the
        // `if (exists process "PHD2")` guard), so looping past PHD2's death is cheap and
        // StopWindowKeeper() via DisconnectAsync is the real stop signal.
        Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { await RunCliAsync("/usr/bin/osascript", $"\"{scriptPath}\"", ct); }
                catch { /* best-effort */ }
                try { await Task.Delay(1000, ct); } catch { return; }
            }
        }, ct);
    }

    private void StopWindowKeeper()
    {
        _windowKeeperCts?.Cancel();
        _windowKeeperCts = null;
    }

    /// <summary>Launch PHD2 on Linux. Two modes:
    ///   1. <c>$DISPLAY</c> is set and reachable → desktop Linux. Launch directly; the PHD2
    ///      window opens but we leave it minimized (PHD2 respects its own last-saved state).
    ///   2. No <c>$DISPLAY</c> → truly headless server. Run under <c>xvfb-run</c> which spins
    ///      up a virtual X11 display (in-memory framebuffer, no physical GPU or monitor
    ///      needed). The wxWidgets process thinks it has a display; there's just no actual
    ///      pixels sent anywhere. This is the standard cross-platform pattern for running
    ///      GUI-bound services on server Linux.
    /// In both cases PHD2's JSON-RPC server binds to :4400 and we connect the same way.</summary>
    private async Task LaunchLinuxAsync(string phdBinary, CancellationToken ct)
    {
        // Pre-seed PHD2 preferences. On Linux wxConfig stores them as an INI file under
        // ~/.config/PHD2/PHDGuidingV2. Using wx's gconf-style numbered keys via crudini
        // is possible but fragile; skip the prefs hack and document that first-run users
        // need to toggle Enable Server once in an interactive run. After that the setting
        // sticks in the INI and subsequent headless launches pick it up.
        var display = Environment.GetEnvironmentVariable("DISPLAY");
        var needXvfb = string.IsNullOrEmpty(display);
        var xvfbRun = needXvfb ? FindXvfbRun() : null;

        if (needXvfb && xvfbRun == null)
        {
            _log.LogWarning("Linux headless PHD2 launch requires xvfb-run ($DISPLAY is unset and /usr/bin/xvfb-run not found). Install with: apt install xvfb  —  or set $DISPLAY to an existing X server.");
            return;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                // xvfb-run wraps phd2: allocates a fresh display number (-a), runs the
                // wrapped process, tears down the virtual display when it exits.
                FileName = xvfbRun ?? phdBinary,
                Arguments = xvfbRun != null ? $"-a \"{phdBinary}\"" : string.Empty,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            _phdProcess = Process.Start(psi);
            _log.LogInformation("PHD2 launched {Mode} from {Path}", xvfbRun != null ? "under Xvfb" : "directly", phdBinary);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to launch PHD2 on Linux");
        }

        // Linux needs no separate "hide window" step — under Xvfb there's no visible window
        // to begin with, and on a desktop Linux the user's window manager honors whatever
        // state PHD2 was last closed in (we recommend minimized-on-start).
        await Task.CompletedTask;
    }

    private static async Task RunCliAsync(string exe, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi);
        if (p == null) return;
        await p.WaitForExitAsync(ct);
    }
}
