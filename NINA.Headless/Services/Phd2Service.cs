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
/// that <see cref="GuiderController"/> surfaces to the iOS client.
/// Partial class — sibling files each hold one concern:
///   Phd2Service.Lifecycle.cs — install discovery, launch/hide, connect, disconnect.
///   Phd2Service.Rpc.cs — JSON-RPC send/await, read loop, event dispatch, snapshot.
///   Phd2Service.Commands.cs — guide/dither/stop + high-level RPC wrappers.
///   Phd2Service.Prefs.cs — prefs/INI writes, INDI bindings, profiles, full autotune.</summary>
public partial class Phd2Service
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

    public string CurrentAppState { get { lock (_stateLock) return _appState; } }
}
