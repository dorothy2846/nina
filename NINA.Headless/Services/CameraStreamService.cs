using System.Collections.Concurrent;
using System.Net.WebSockets;
using NINA.Headless.Indi;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace NINA.Headless.Services;

/// <summary>
/// Live streaming for the camera tab. Drives the INDI driver's native video
/// path (CCD_VIDEO_STREAM=On, CCD_STREAM_ENCODER=MJPEG); the driver delivers
/// pre-encoded JPEG frames on the CCD1 BLOB. Each frame is debayered/WB'd in
/// process, broadcast to JPEG WebSocket clients raw, and BMP-wrapped into
/// <see cref="H264Transcoder"/> for H.264 fan-out (WebRTC + the H.264
/// WebSocket fallback).
///
/// Lifecycle: subscribing to the WS or attaching a WebRTC peer implicitly
/// starts the stream; the last unsubscribe stops it. REST control endpoints
/// let callers tune exposure, gain, ROI, and FPS cap.
/// </summary>
public partial class CameraStreamService : IAsyncDisposable
{
    private readonly IndiDiscoveryService _indi;
    private readonly EquipmentSelectionService _equipment;
    private readonly ILogger<CameraStreamService> _log;
    private readonly H264Transcoder _h264;

    // Two WebSocket fan-outs sharing the upstream camera capture:
    //   _jpegClients — debayered RGB JPEG frames (legacy fallback transport)
    //   _h264Clients — Annex B H.264 NALs, raw start-code framed
    // WebRTC peers consume the same H.264 stream from H264Transcoder over RTP.
    private readonly ConcurrentDictionary<Guid, WebSocket> _jpegClients = new();
    private readonly ConcurrentDictionary<Guid, WebSocket> _h264Clients = new();
    private readonly object _stateLock = new();
    private bool _running;
    private string? _streamingDevice;
    // 30 ms exposure: frame rate ceiling instead of exposure ceiling. With
    // INDI's 100-200 ms readout/blob overhead, longer exposures linearly
    // grow jitter-buffer floor on the receiver — chrome holds ~1.5 frame
    // intervals as safety. 30 ms exposure → ~10-15 fps → ~70-100 ms jitter
    // buffer instead of 350 ms. Gain stays high (300) so signal still reads
    // for daytime viewfinder framing. Photo mode unaffected.
    private double _exposureSeconds = 0.03;
    // Streaming-only binning. 4×4 default trades resolution for low-latency
    // viewfinder; user overrides via /stream/configure when they want full
    // sensor for focusing or planetary alignment. Photo capture path doesn't
    // touch this (still-capture binning is its own request param), and the
    // stream-stop hook restores 1×1 so a subsequent capture is unaffected.
    // Streaming binning/gain — null = "leave the camera's capture settings
    // alone, downsample server-side instead". Streaming should never disturb
    // the user's planetary or DSO configuration. They opt-in by passing
    // explicit binX/binY/gain to /stream/configure, in which case we
    // snapshot + restore around the stream session.
    private int? _streamBinX = null;
    private int? _streamBinY = null;
    private int? _streamGainOverride = null;
    private int? _streamOffsetOverride = null;
    private int? _savedBinX, _savedBinY, _savedGain, _savedOffset;
    // Normalized ROI (0..1) — fraction of sensor to read out for streaming.
    // Default = full sensor. Planetary imaging shrinks this so the driver
    // reads a small window and fps climbs proportionally (320×240 around
    // Jupiter delivers 100+ fps where 1280×960 caps near 10).
    private double _roiFracW = 1.0, _roiFracH = 1.0;
    private double _roiFracCX = 0.5, _roiFracCY = 0.5;
    private (int x, int y, int w, int h)? _savedSubFrame;
    private double _maxFps = 20;

    // FPS tracking — rolling 1-second window of frame arrival times.
    private readonly Queue<DateTime> _frameTimes = new();

    /// Was the stream running when the underlying INDI socket dropped?
    /// On reconnect we restore that state so a network blip / driver restart
    /// is invisible to the iOS viewer beyond a brief frozen frame.
    private bool _wasRunningPreDisconnect;

    public CameraStreamService(IndiDiscoveryService indi, EquipmentSelectionService equipment, ILogger<CameraStreamService> log, H264Transcoder h264)
    {
        _indi = indi;
        _equipment = equipment;
        _log = log;
        _h264 = h264;
        _h264.NaluReady += OnH264Nalu;
        _h264.NaluEmitted += OnNaluEmitted;

        // Auto-start the live stream the moment a camera finishes connecting.
        // Cuts first-viewer cold start by ~1–2 s — without it, the iOS app's
        // first /rtc/offer is what triggers CCD_VIDEO_STREAM=On and we then
        // wait on the sensor's first BLOB. Pre-warming means ffmpeg already
        // has frames flowing, so on first connect only the WebRTC handshake
        // and first H.264 keyframe remain (~0.5 s).
        _indi.DeviceConnected += OnIndiDeviceConnected;
        _indi.ClientDisconnected += OnIndiClientDisconnected;
        _indi.ClientReconnected += OnIndiClientReconnected;
    }

    public bool IsRunning => _running;
    public int ClientCount => _jpegClients.Count + _h264Clients.Count;
    public double ExposureSeconds => _exposureSeconds;
    public double MaxFps => _maxFps;
    public double LastFps
    {
        get
        {
            lock (_frameTimes)
            {
                var now = DateTime.UtcNow;
                while (_frameTimes.Count > 0 && (now - _frameTimes.Peek()).TotalSeconds > 1.0) _frameTimes.Dequeue();
                return _frameTimes.Count;
            }
        }
    }

    /// Milliseconds since the most recent frame was fully processed and
    /// broadcast. When the camera is occluded/stalled this grows
    /// monotonically — the value is the honest "how stale is what the user
    /// is looking at" number.
    public int? LastFrameAgeMs
    {
        get
        {
            lock (_frameTimes)
            {
                if (_frameTimes.Count == 0) return null;
                return (int)(DateTime.UtcNow - _frameTimes.Last()).TotalMilliseconds;
            }
        }
    }

    public long DroppedFrames => System.Threading.Interlocked.Read(ref _droppedFrames);

    /// <summary>Apply config + reload sensor settings WITHOUT touching the
    /// ffmpeg pipeline. Full StopAsync/StartAsync tore down ffmpeg and
    /// re-spawned it on every ROI tap (1-2 s cold start). Here the encoder
    /// stays running across the brief sensor pause; new sub-framed BLOBs
    /// land straight into the existing pipeline. Sub-second ROI swap.</summary>
    private CancellationTokenSource? _applyDebounceCts;
    private readonly object _applyLock = new();
    /// Slider-stop debounce. iOS already coalesces drag motion to ~20 Hz,
    /// and the patched playerone driver picks up each STREAMING_EXPOSURE
    /// write on the next worker iteration, so additional server-side
    /// debounce only adds perceived input lag without preventing any real
    /// problem. Set to a tiny value just to absorb burst arrivals.
    private const int ApplyDebounceMs = 10;
    /// Apply serialisation. ROI/binning changes drive a STREAM_OFF/ON
    /// dance; without this gate, two applies could race that dance and
    /// leave the camera stuck STREAM_OFF.
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    /// ROI/binning cycle gate. PlayerOne tolerates one OFF/ON cycle
    /// reliably but stalls if a second hits within ~1.5 s.
    private DateTime _lastSensorCycleAt = DateTime.MinValue;
    private const int MinCycleIntervalMs = 1500;
    /// Full stop→start pacing. Measured: three full stream cycles inside
    /// ~24 s USB-disconnects the PlayerOne entirely (CONNECT drops to Off);
    /// one cycle is reliably fine. 15 s spacing caps any 24 s window at two
    /// cycles (0 s, 15 s, 30 s…) with margin — StartAsync delays (never
    /// rejects) until the cooldown has elapsed.
    private const int MinStopStartIntervalMs = 15_000;

    /// Snapshot of the configuration at the moment ApplyToDriverAsync runs.
    /// Compared against the live `_*` fields to decide what changed since
    /// the last applied state — `prev*` here = "last successfully applied".
    private double _lastAppliedExp;
    private int? _lastAppliedGain;
    private int? _lastAppliedOffset;
    private double _lastAppliedRoiW = 1, _lastAppliedRoiH = 1, _lastAppliedRoiCX = 0.5, _lastAppliedRoiCY = 0.5;
    private int? _lastAppliedBinX, _lastAppliedBinY;

    /// Resolved at stream-start by inspecting the connected driver's actual
    /// properties. (propertyName, elementName, scaleToDriverUnits) — scale
    /// converts our user-facing "seconds" to whatever the driver wants
    /// (e.g., 1_000_000 for the PlayerOne CCD_CONTROLS["Exposure"]
    /// microseconds element).
    private (string propName, string elName, double scale)? _liveExposureProperty;

    /// Bayer pattern for the connected colour camera. Driver-side debayer
    /// isn't available on PlayerOne (CCD_VIDEO_FORMAT only exposes RAW8/16),
    /// so we receive a grayscale-encoded mosaic JPEG and debayer in process
    /// before fan-out. Empty string = mono camera, frame passes through.
    /// Refreshed on stream start from INDI CCD_CFA so a swap from colour
    /// to mono camera (or vice versa) doesn't paint mono frames green.
    private string _bayerPattern = "";

    /// Cached at stream start (and re-checked every ~30 frames as a safety
    /// net) so we don't pay for `TryBuildCameraInfo`'s ~15 dictionary lookups
    /// on every BLOB. Hardware binning destroys Bayer alignment, so we skip
    /// debayer when this is true.
    private bool _hardwareBinned;
    private int _framesSinceBinningCheck;
    private const int BinningRecheckEvery = 30;
    /// <summary>Max frames allowed to be in flight to the H.264 encoder
    /// before producer-side drop kicks in. 10 frames at 8 fps ≈ 1.25 s
    /// worst case but encoder normally drains far faster than it fills,
    /// so steady-state queue depth is 1-2. Going lower (tried 3) stalled
    /// ffmpeg's BMP demuxer at startup — image2pipe with bmp seems to
    /// need a warmup window of more than 3 frames before it produces
    /// output, even with -probesize 32 -analyzeduration 0.</summary>
    private const int EncoderBacklogMax = 10;
    /// <summary>Minimum interval between sensor OFF/ON cycles (ROI / binning /
    /// exposure changes that need a stream rearm). PlayerOne's driver
    /// disconnects the camera after rapid cycling — observed at the iOS-
    /// slider drag rate of 4 Hz. 800 ms gives the driver breathing room
    /// while still feeling responsive on a single deliberate adjustment.</summary>

    /// Single-flight in-process processing slot. The INDI client thread that
    /// raises BLOB events MUST NOT do CPU work — debayer + JPEG re-encode on
    /// 12 MP frames takes longer than the 100 ms BLOB cadence and stalls the
    /// driver socket. We hand the bytes off to a worker; if the worker is
    /// already busy, we drop the new frame on the floor. Honest backpressure:
    /// drops show up as low fps rather than as deceiving keepalive replays.
    private int _processingSlot; // 0 = idle, 1 = busy
    private long _droppedFrames;

    /// <summary>Per-frame timing samples for the latency probe. Each entry
    /// records when the frame entered each pipeline stage so /api/v1/
    /// diagnostics/streamLatency can show capture→encoder→emit decomposition.
    /// Bounded ring buffer (last 60 frames). <see cref="EncoderEmitted"/> is
    /// filled in asynchronously when the H264Transcoder reports a NAL —
    /// paired FIFO so frames map 1:1 in order (drop-tolerant since we use
    /// single-flight push).</summary>
    public sealed class FrameTiming
    {
        public long FrameNumber;
        public DateTime BlobReceived;
        public DateTime ProcessingDone;
        public DateTime PushedToFfmpeg;
        public DateTime? EncoderEmitted;
    }
    private readonly System.Collections.Concurrent.ConcurrentQueue<FrameTiming> _timings = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<FrameTiming> _pendingEncode = new();
    private long _frameCounter;
    public IReadOnlyList<FrameTiming> RecentFrameTimings => _timings.ToArray();

    private long _naluEmitCount;
    private long _naluDequeueMissCount;

    /// EMA-tracked per-channel WB gain. Targets the brightest channel's
    /// mean (gray-world) rather than a fixed exposure target — that way the
    /// overall brightness is whatever the user's gain/exposure settings
    /// produce, and we ONLY remove the green-channel sensitivity bias
    /// that paints OSC-camera daytime frames green. Previous fixed-target
    /// (TargetMean=96) was effectively auto-exposure, which silently
    /// reverted every gain change the user made — they'd drop gain, the
    /// image would dim for one frame, then the EMA pumped the gains back
    /// up to compensate, masking their adjustment.
    private float _wbR = 1f, _wbG = 1f, _wbB = 1f;
    private bool _wbInitialized;

    /// How many Bayer 2×2 blocks we skip between output samples.
    ///   1 = every block (output ≈ source/2)        — 12 MP sensor → ~6 MP RGB
    ///   2 = every other block (output ≈ source/4)  — 12 MP → ~1.5 MP RGB
    /// Stream output is bandwidth-capped at 640 px wide downstream by
    /// ffmpeg, so even a 12 MP camera produces output that gets thrown
    /// away. Decimating earlier means the JPEG encode + ffmpeg decode
    /// see a much smaller frame and we keep up with high-rate sensors
    /// (10 ms exposure on 1×1 binning) without dropping. Stills are
    /// untouched — they go through FitsToPng at full bilinear demosaic.
    private const int StreamDecimate = 4;

}
