using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace NINA.Headless.Services;

/// <summary>PHD2 guide axis. Wire value is the literal string the JSON-RPC protocol expects
/// in <c>set_algo_param</c> / <c>get_algo_param_names</c>.</summary>
public enum GuideAxis { RA, Dec }

public static class GuideAxisExtensions
{
    public static string Wire(this GuideAxis axis) => axis == GuideAxis.RA ? "ra" : "dec";
}

/// <summary>Dec guiding mode. Wire values match PHD2 <c>set_dec_guide_mode</c> accepted strings.</summary>
public enum DecGuideMode { Off, Auto, North, South }

/// <summary>PHD2 JSON-RPC client + event stream parser. Auto-launches PHD2 hidden (macOS
/// System Events / Linux Xvfb), connects to port 4400, and exposes the subset of PHD2 RPC
/// that <see cref="GuiderController"/> surfaces to the iOS client.</summary>
public class Phd2Service
{
    public const int ServerPort = 4400;

    private readonly ILogger<Phd2Service> _log;
    private readonly object _lock = new();
    private TcpClient? _tcp;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private int _requestId;
    private Task? _readTask;
    private CancellationTokenSource? _cts;
    private Process? _phdProcess;
    private CancellationTokenSource? _windowKeeperCts;

    // Parsed state cache. Updated by ReadLoopAsync, read by REST handlers.
    private readonly object _stateLock = new();
    private string _appState = "Disconnected";
    private double? _lastDx, _lastDy;
    private double? _lastRaDuration, _lastDecDuration;
    private double? _starMass;
    private double? _snr;
    private double? _pixelScale;
    private readonly Queue<GuideStep> _recentSteps = new();
    // id → TaskCompletionSource so RPC callers can await the matching `result` line. Each
    // request gets a unique id; the read loop plucks it out and fires the waiter.
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pendingRpc = new();

    public Phd2Service(ILogger<Phd2Service> log)
    {
        _log = log;
    }

    public bool IsConnectedToServer => _tcp?.Connected == true;

    public record GuideStep(DateTime At, double Dx, double Dy, double? RaDuration, double? DecDuration);

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

    private enum InstallKind { MacApp, LinuxBinary }
    private record InstallInfo(string Path, InstallKind Kind);

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

        return null;
    }

    private static bool IsPhdRunning()
    {
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

    public async Task<bool> SetAllConnectedAsync(bool connected, CancellationToken ct)
    {
        if (!await EnsureStartedAsync(ct)) return false;
        await SendRpcAsync("set_connected", new object[] { connected }, ct);
        return true;
    }

    public async Task<bool> StartGuidingAsync(CancellationToken ct)
    {
        if (!IsConnectedToServer) return false;
        var param = new { settle = new { pixels = 2.0, time = 5, timeout = 40 }, recalibrate = false };
        await SendRpcAsync("guide", new[] { (object)param }, ct);
        return true;
    }

    /// <summary>Trigger PHD2 to re-calibrate + guide. PHD2 clears its existing calibration
    /// data, pulses the mount in each direction, measures the response, then enters guiding.
    /// Caller should poll <see cref="CurrentAppState"/> or await <see cref="WaitForAppStateAsync"/>.</summary>
    public async Task<bool> StartCalibrationAsync(CancellationToken ct)
    {
        if (!IsConnectedToServer) return false;
        await SendRpcAsync("clear_calibration", new object[] { "both" }, ct);
        var param = new { settle = new { pixels = 2.0, time = 5, timeout = 60 }, recalibrate = true };
        await SendRpcAsync("guide", new[] { (object)param }, ct);
        return true;
    }

    public string CurrentAppState { get { lock (_stateLock) return _appState; } }

    /// <summary>Poll PHD2's reported AppState until it matches any of the expected values
    /// (or the timeout elapses). Used by the auto-calibrate orchestrator to block until
    /// PHD2 transitions from Calibrating → Guiding (success) or Stopped (failure).</summary>
    public async Task<string?> WaitForAppStateAsync(string[] expectedStates, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var state = CurrentAppState;
            if (expectedStates.Contains(state, StringComparer.OrdinalIgnoreCase)) return state;
            try { await Task.Delay(500, ct); } catch { return null; }
        }
        return null;
    }

    public async Task<bool> StopGuidingAsync(CancellationToken ct)
    {
        if (!IsConnectedToServer) return false;
        await SendRpcAsync("stop_capture", Array.Empty<object>(), ct);
        return true;
    }

    public async Task<bool> DitherAsync(double pixels, CancellationToken ct)
    {
        if (!IsConnectedToServer) return false;
        // amount + raOnly=false + settle-after = standard PHD2 dither args
        var param = new object[]
        {
            pixels, false, new { pixels = 2.0, time = 5, timeout = 40 }
        };
        await SendRpcAsync("dither", param, ct);
        return true;
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

    private async Task QueryInitialStateAsync(CancellationToken ct)
    {
        // These update state via incoming "jsonrpc":"2.0" "result" replies, handled the same way
        // as events in ReadLoopAsync.
        await SendRpcAsync("get_app_state", Array.Empty<object>(), ct);
        await SendRpcAsync("get_pixel_scale", Array.Empty<object>(), ct);
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
        else
            await LaunchLinuxAsync(install.Path, ct);
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

    // MARK: High-level RPC wrappers

    public record StarImage(byte[] Png, int Width, int Height, double StarX, double StarY, int Frame);

    /// <summary>Fetch the latest guide camera frame + selected star position from PHD2. The
    /// RPC returns raw 16-bit little-endian pixels which we base64-decode, autostretch, and
    /// PNG-encode so the iOS client can drop it straight into an Image view.</summary>
    public async Task<StarImage?> GetStarImageAsync(CancellationToken ct)
    {
        if (!IsConnectedToServer) return null;
        try
        {
            var result = await SendRpcAndAwaitAsync("get_star_image", Array.Empty<object>(), ct);
            if (result.ValueKind != JsonValueKind.Object) return null;
            int w = result.GetProperty("width").GetInt32();
            int h = result.GetProperty("height").GetInt32();
            int frame = result.TryGetProperty("frame", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetInt32() : 0;
            double sx = 0, sy = 0;
            if (result.TryGetProperty("star_pos", out var sp) && sp.ValueKind == JsonValueKind.Array && sp.GetArrayLength() >= 2)
            {
                sx = sp[0].GetDouble();
                sy = sp[1].GetDouble();
            }
            var b64 = result.GetProperty("pixels").GetString();
            if (string.IsNullOrEmpty(b64)) return null;
            var raw = Convert.FromBase64String(b64);
            if (raw.Length < 2 * w * h) return null;
            var pixels = new float[w * h];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = (ushort)(raw[2 * i] | (raw[2 * i + 1] << 8));
            var png = CalibrationLibrary.EncodeAutostretchPng(pixels, w, h);
            return new StarImage(png, w, h, sx, sy, frame);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "get_star_image failed");
            return null;
        }
    }

    public async Task<double?> GetExposureAsync(CancellationToken ct)
    {
        if (!IsConnectedToServer) return null;
        try
        {
            var r = await SendRpcAndAwaitAsync("get_exposure", Array.Empty<object>(), ct);
            // PHD2 returns milliseconds as a number.
            return r.ValueKind == JsonValueKind.Number ? r.GetDouble() : (double?)null;
        }
        catch { return null; }
    }

    public async Task<bool> SetExposureAsync(double exposureMs, CancellationToken ct)
    {
        if (!IsConnectedToServer) return false;
        try { await SendRpcAsync("set_exposure", new object[] { (int)exposureMs }, ct); return true; }
        catch { return false; }
    }

    public async Task<bool> SetPausedAsync(bool paused, CancellationToken ct)
    {
        if (!IsConnectedToServer) return false;
        // PHD2 set_paused signature: (bool paused, string dec_option). dec_option="full" pauses both.
        try { await SendRpcAsync("set_paused", new object[] { paused, "full" }, ct); return true; }
        catch { return false; }
    }

    public async Task<bool> ClearCalibrationAsync(CancellationToken ct)
    {
        if (!IsConnectedToServer) return false;
        try { await SendRpcAsync("clear_calibration", new object[] { "both" }, ct); return true; }
        catch { return false; }
    }

    /// <summary>Start the exposure-only loop (no guiding) — equivalent to PHD2's "Loop"
    /// button. Used for framing / focusing the guide camera before calibration.</summary>
    public async Task<bool> LoopAsync(CancellationToken ct)
    {
        if (!IsConnectedToServer) return false;
        try { await SendRpcAsync("loop", Array.Empty<object>(), ct); return true; }
        catch { return false; }
    }

    public async Task<bool> FindStarAsync(CancellationToken ct)
    {
        if (!IsConnectedToServer) return false;
        try { await SendRpcAsync("find_star", Array.Empty<object>(), ct); return true; }
        catch { return false; }
    }

    public async Task<bool> SetLockPositionAsync(double x, double y, CancellationToken ct)
    {
        if (!IsConnectedToServer) return false;
        // set_lock_position(x, y, exact=true) — exact locks at that precise pixel, else PHD2
        // snaps to the nearest star within search region.
        try { await SendRpcAsync("set_lock_position", new object[] { x, y, true }, ct); return true; }
        catch { return false; }
    }

    /// <summary>Push one algorithm parameter to PHD2. `axis` is "ra" or "dec", `name` is one
    /// of the params reported by get_algo_param_names for the currently-active algorithm
    /// (e.g. "aggressiveness", "hysteresis", "minMove" for Hysteresis). Values out of range
    /// are silently clamped server-side by PHD2.</summary>
    public async Task<bool> SetAlgoParamAsync(GuideAxis axis, string name, double value, CancellationToken ct)
    {
        if (!IsConnectedToServer) return false;
        try { await SendRpcAsync("set_algo_param", new object[] { axis.Wire(), name, value }, ct); return true; }
        catch { return false; }
    }

    public async Task<string[]?> GetAlgoParamNamesAsync(GuideAxis axis, CancellationToken ct)
    {
        if (!IsConnectedToServer) return null;
        try
        {
            var result = await SendRpcAndAwaitAsync("get_algo_param_names", new object[] { axis.Wire() }, ct);
            if (result.ValueKind != JsonValueKind.Array) return null;
            var list = new List<string>();
            foreach (var item in result.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String) list.Add(item.GetString()!);
            return list.ToArray();
        }
        catch { return null; }
    }

    public async Task<bool> SetDecGuideModeAsync(DecGuideMode mode, CancellationToken ct)
    {
        if (!IsConnectedToServer) return false;
        try { await SendRpcAsync("set_dec_guide_mode", new object[] { mode.ToString() }, ct); return true; }
        catch { return false; }
    }

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
