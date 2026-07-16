using System.Diagnostics;

namespace NINA.Headless.Services;

/// <summary>
/// Spawns and monitors a local indiserver process with a configured driver list.
///
/// On macOS/Linux we use the bundled `indiserver` binary. The list of drivers
/// is passed on the command line. For development, defaults include
/// indi_simulator_ccd so the app has something to enumerate out of the box.
///
/// Configuration (env vars):
///   NINA_INDI_SERVER_BIN   path to indiserver (default: /opt/homebrew/bin/indiserver, else "indiserver" on PATH)
///   NINA_INDI_DRIVERS      space-separated driver list (default: "indi_simulator_ccd indi_simulator_telescope")
///   NINA_INDI_LIB_DIR      DYLD_LIBRARY_PATH for the driver process (macOS bottle needs this)
///   NINA_INDI_PORT         indiserver listen port (default: 7624)
/// </summary>
public class IndiServerManager : BackgroundService
{
    private readonly ILogger<IndiServerManager> _log;
    private Process? _proc;
    /// <summary>Path to the FIFO indiserver reads runtime commands from. Populated on the
    /// first successful StartServer() call. Writing "stop X" / "start X" lines to it tells
    /// indiserver to kill or spawn the named driver child process — lets us refresh one
    /// wedged driver (say, a camera that missed its hot-plug window) without kicking the
    /// mount or guider off their in-progress operations.</summary>
    private string? _fifoPath;

    /// <summary>All drivers currently expected to be running: the base launch
    /// list plus any started at runtime (USB auto-detect, manual add from the
    /// app). Dynamic entries survive an indiserver restart — StartServer folds
    /// them into the relaunch arguments.</summary>
    private readonly object _driversLock = new();
    private readonly List<string> _baseDrivers = new();
    private readonly HashSet<string> _dynamicDrivers = new();

    public string Host { get; } = Environment.GetEnvironmentVariable("NINA_INDI_HOST") ?? "localhost";
    public int Port { get; } = int.TryParse(Environment.GetEnvironmentVariable("NINA_INDI_PORT"), out var p) ? p : 7624;

    public IReadOnlyList<string> RunningDrivers
    {
        get { lock (_driversLock) return _baseDrivers.Concat(_dynamicDrivers).Distinct().ToList(); }
    }

    public IndiServerManager(ILogger<IndiServerManager> log)
    {
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                StartServer();
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "indiserver failed; restart in 5s");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); } catch { break; }
            }
        }
        StopServer();
    }

    private void StartServer()
    {
        StopServer();

        var bin = Environment.GetEnvironmentVariable("NINA_INDI_SERVER_BIN")
                 ?? (File.Exists("/opt/homebrew/bin/indiserver") ? "/opt/homebrew/bin/indiserver" : "indiserver");

        // Default driver set: real hardware drivers commonly used on this rig.
        // Add more by setting NINA_INDI_DRIVERS env var; USB auto-detect and
        // the app's manual add start further drivers at runtime via the FIFO.
        var baseList = Environment.GetEnvironmentVariable("NINA_INDI_DRIVERS")
                     ?? "indi_playerone_ccd indi_lx200am5 indi_asi_ccd indi_asi_focuser indi_asi_wheel";
        string drivers;
        lock (_driversLock)
        {
            _baseDrivers.Clear();
            _baseDrivers.AddRange(baseList.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            drivers = string.Join(' ', _baseDrivers.Concat(_dynamicDrivers).Distinct());
        }

        // FIFO for runtime driver management. Must be mkfifo'd before indiserver launches
        // since indiserver opens it for reading at startup. We regenerate each launch so a
        // crashed FIFO (broken pipe, leaked inode) doesn't poison the new process.
        EnsureFreshFifo();

        var psi = new ProcessStartInfo
        {
            FileName = bin,
            Arguments = $"-p {Port} -f \"{_fifoPath}\" -v {drivers}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // macOS Homebrew libindi doesn't set rpath for drivers; pass the lib dir explicitly.
        var libDir = Environment.GetEnvironmentVariable("NINA_INDI_LIB_DIR");
        if (string.IsNullOrEmpty(libDir) && Directory.Exists("/opt/homebrew/Cellar/libindi"))
        {
            var first = Directory.EnumerateDirectories("/opt/homebrew/Cellar/libindi").FirstOrDefault();
            if (first != null) libDir = Path.Combine(first, "lib");
        }
        if (!string.IsNullOrEmpty(libDir))
        {
            psi.Environment["DYLD_LIBRARY_PATH"] = libDir;
            psi.Environment["LD_LIBRARY_PATH"] = libDir;
        }

        _proc = Process.Start(psi);
        if (_proc == null)
        {
            throw new InvalidOperationException($"Failed to launch indiserver ({bin})");
        }

        _proc.OutputDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) _log.LogDebug("[indiserver] {Line}", e.Data); };
        _proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) _log.LogDebug("[indiserver] {Line}", e.Data); };
        _proc.BeginOutputReadLine();
        _proc.BeginErrorReadLine();

        _log.LogInformation("indiserver started (pid={Pid}) on port {Port} with drivers: {Drivers}",
            _proc.Id, Port, drivers);
    }

    private void StopServer()
    {
        try
        {
            if (_proc is { HasExited: false })
            {
                _proc.Kill(entireProcessTree: true);
                _proc.WaitForExit(3000);
            }
            _proc?.Dispose();
        }
        catch { /* best effort */ }
        _proc = null;
    }

    public override void Dispose()
    {
        StopServer();
        if (_fifoPath != null && File.Exists(_fifoPath))
        {
            try { File.Delete(_fifoPath); } catch { }
        }
        base.Dispose();
    }

    /// <summary>Create a fresh named-pipe on disk and record the path for indiserver's -f flag.
    /// Uses the mkfifo(1) command (macOS has no built-in .NET API for named pipes in the
    /// Unix FIFO sense — the System.IO.Pipes classes are a different thing).</summary>
    private void EnsureFreshFifo()
    {
        var dir = Path.GetTempPath();
        var path = Path.Combine(dir, $"nina-indi-{Environment.ProcessId}.fifo");
        try { if (File.Exists(path)) File.Delete(path); } catch { }

        var mkfifo = Process.Start(new ProcessStartInfo
        {
            FileName = "/usr/bin/mkfifo",
            Arguments = $"\"{path}\"",
            UseShellExecute = false,
            RedirectStandardError = true,
        });
        if (mkfifo == null) throw new InvalidOperationException("Failed to invoke mkfifo");
        mkfifo.WaitForExit(2000);
        if (mkfifo.ExitCode != 0)
        {
            var err = mkfifo.StandardError.ReadToEnd();
            throw new InvalidOperationException($"mkfifo failed: {err}");
        }
        _fifoPath = path;
        _log.LogInformation("Created indiserver FIFO at {Path}", path);
    }

    /// <summary>Restart a single driver child process without disturbing the other drivers.
    /// Writes <c>stop &lt;driver&gt;</c> then <c>start &lt;driver&gt;</c> to the FIFO that indiserver
    /// is reading from. The brief pause between them gives indiserver time to reap the old
    /// child and flush any pending state before the respawn.
    ///
    /// Safe alongside active operations on OTHER drivers — a mount in the middle of a slew,
    /// PHD2 actively guiding, etc., are untouched.</summary>
    public async Task RestartDriverAsync(string driverName, CancellationToken ct)
    {
        if (_fifoPath == null || !File.Exists(_fifoPath))
            throw new InvalidOperationException("indiserver FIFO not available (server not started yet?)");
        if (string.IsNullOrWhiteSpace(driverName))
            throw new ArgumentException("driverName required", nameof(driverName));

        // Basic safety: indiserver's FIFO parser splits on whitespace — no shell metachars to
        // worry about, but we still reject anything that isn't a plausible driver name.
        if (driverName.Any(c => !(char.IsLetterOrDigit(c) || c == '_' || c == '-')))
            throw new ArgumentException($"Invalid driver name '{driverName}'", nameof(driverName));

        _log.LogInformation("Per-driver restart: {Driver}", driverName);

        // Opening a FIFO for write blocks until a reader (indiserver) opens it. If indiserver
        // has crashed, this would hang — guard with a timeout-limited task.
        async Task WriteLineAsync(string line)
        {
            await using var fs = new FileStream(_fifoPath, FileMode.Open, FileAccess.Write, FileShare.Read,
                bufferSize: 512, options: FileOptions.Asynchronous);
            var bytes = System.Text.Encoding.UTF8.GetBytes(line + "\n");
            await fs.WriteAsync(bytes, ct);
            await fs.FlushAsync(ct);
        }

        await WriteLineAsync($"stop {driverName}");
        await Task.Delay(500, ct);
        await WriteLineAsync($"start {driverName}");
    }

    /// <summary>Start one more driver in the running indiserver without touching
    /// the others (USB auto-detect / manual add from the app). Returns false if
    /// the driver is already in the running set. The driver is remembered so an
    /// indiserver restart relaunches it too.</summary>
    public async Task<bool> StartDriverAsync(string driverName, CancellationToken ct)
    {
        ValidateDriverName(driverName);
        lock (_driversLock)
        {
            if (_baseDrivers.Contains(driverName) || _dynamicDrivers.Contains(driverName))
                return false;
            _dynamicDrivers.Add(driverName);
        }
        try
        {
            await WriteFifoAsync($"start {driverName}", ct);
            _log.LogInformation("indiserver: started driver {Driver} via FIFO", driverName);
            return true;
        }
        catch
        {
            lock (_driversLock) _dynamicDrivers.Remove(driverName);
            throw;
        }
    }

    /// <summary>Stop a runtime-started driver. Base-list drivers are refused —
    /// they exist because this rig depends on them; stopping those is what
    /// NINA_INDI_DRIVERS is for.</summary>
    public async Task<bool> StopDriverAsync(string driverName, CancellationToken ct)
    {
        ValidateDriverName(driverName);
        lock (_driversLock)
        {
            if (!_dynamicDrivers.Contains(driverName))
                return false;
            _dynamicDrivers.Remove(driverName);
        }
        await WriteFifoAsync($"stop {driverName}", ct);
        _log.LogInformation("indiserver: stopped driver {Driver} via FIFO", driverName);
        return true;
    }

    private static void ValidateDriverName(string driverName)
    {
        if (string.IsNullOrWhiteSpace(driverName))
            throw new ArgumentException("driverName required", nameof(driverName));
        if (driverName.Any(c => !(char.IsLetterOrDigit(c) || c == '_' || c == '-')))
            throw new ArgumentException($"Invalid driver name '{driverName}'", nameof(driverName));
    }

    /// Opening a FIFO for write blocks until a reader (indiserver) opens it —
    /// guard against a crashed indiserver by requiring the FIFO to exist.
    private async Task WriteFifoAsync(string line, CancellationToken ct)
    {
        if (_fifoPath == null || !File.Exists(_fifoPath))
            throw new InvalidOperationException("indiserver FIFO not available (server not started yet?)");
        await using var fs = new FileStream(_fifoPath, FileMode.Open, FileAccess.Write, FileShare.Read,
            bufferSize: 512, options: FileOptions.Asynchronous);
        var bytes = System.Text.Encoding.UTF8.GetBytes(line + "\n");
        await fs.WriteAsync(bytes, ct);
        await fs.FlushAsync(ct);
    }

    /// <summary>Nuclear option for device discovery: kill the entire indiserver child process
    /// and start it again with the same driver set. Used when the INDI client reconnect
    /// (the lighter-weight rescan) hasn't surfaced expected hardware — usually because a
    /// driver is wedged and needs a fresh load. Caller typically pairs this with the
    /// IndiDiscoveryService client re-establishment.</summary>
    public void RestartServer()
    {
        _log.LogInformation("indiserver: manual restart requested");
        try { StartServer(); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "indiserver restart failed");
            throw;
        }
    }
}
