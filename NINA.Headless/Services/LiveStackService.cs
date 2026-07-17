namespace NINA.Headless.Services;

/// <summary>Realtime stacking: user Start → each arriving Light is calibrated, aligned to
/// the reference via <see cref="StarMatcher"/>, bilinearly warped, and accumulated into a
/// running mean. Preview is autostretched on demand. Frames are rejected on shape mismatch
/// or &lt; 4 matching stars; 3 consecutive rejects log a warning.</summary>
public class LiveStackService
{
    private readonly CalibrationLibrary _library;
    private readonly ILogger<LiveStackService> _log;

    private readonly object _lock = new();

    // Session state — all mutated under _lock.
    private bool _active;
    private int _width, _height;
    private List<StarDetector.Star>? _referenceStars;
    // double accumulator: float's 24-bit mantissa stops absorbing per-frame
    // additions once a bright pixel's sum passes ~16.7M (~256 full-well frames)
    // — exactly the deep stacks where precision matters.
    private double[]? _sum;             // running sum of warped calibrated pixels
    private int[]? _count;             // per-pixel contribution count (edges see fewer frames)
    private int _framesAccepted;
    private int _framesRejected;
    private int _consecutiveRejects;
    private double _totalExposureSeconds;
    private DateTime _startedAt;
    private DateTime _lastFrameAt;
    private string? _lastRejectReason;
    private DateTime? _lastRejectAt;

    public LiveStackService(CalibrationLibrary library, ILogger<LiveStackService> log)
    {
        _library = library;
        _log = log;
    }

    public bool IsActive { get { lock (_lock) return _active; } }

    public object Snapshot()
    {
        lock (_lock)
        {
            return new
            {
                active = _active,
                framesAccepted = _framesAccepted,
                framesRejected = _framesRejected,
                totalExposureSeconds = _totalExposureSeconds,
                referenceSet = _referenceStars != null,
                width = _width,
                height = _height,
                startedAt = _active ? (DateTime?)_startedAt : null,
                lastFrameAt = _framesAccepted > 0 ? (DateTime?)_lastFrameAt : null,
                lastRejectReason = _lastRejectReason,
                lastRejectAt = _lastRejectAt
            };
        }
    }

    public void Start()
    {
        lock (_lock)
        {
            _active = true;
            _startedAt = DateTime.Now;
            // Don't wipe existing stack state — user might Stop/Start to pause. Reset() is
            // the explicit "throw it all away" button.
        }
        _log.LogInformation("Live stack started");
    }

    public void Stop()
    {
        lock (_lock) { _active = false; }
        _log.LogInformation("Live stack stopped (accepted={A} rejected={R})", _framesAccepted, _framesRejected);
    }

    public void Reset()
    {
        lock (_lock)
        {
            _referenceStars = null;
            _sum = null;
            _count = null;
            _width = _height = 0;
            _framesAccepted = 0;
            _framesRejected = 0;
            _consecutiveRejects = 0;
            _totalExposureSeconds = 0;
            _lastRejectReason = null;
            _lastRejectAt = null;
        }
        _log.LogInformation("Live stack reset");
    }

    /// <summary>Process a newly captured Light frame. Called from CameraController.Capture
    /// right after the FITS is persisted. Returns silently if stacking is not active.</summary>
    public void ProcessFrame(byte[] fitsBytes, string? cameraId, string? planId, double exposureSeconds, int gain, int offset, int binning, string? filter)
    {
        if (!IsActive) return;

        try
        {
            // Calibrate if masters match; otherwise fall back to raw pixels. Uncalibrated
            // stacking still improves SNR, just with residual dark/flat structure baked in.
            var px = _library.TryCalibratePixels(new CalibrationLibrary.CalibrationInputs(
                fitsBytes, cameraId, planId, exposureSeconds, gain, offset, binning, filter),
                returnRawIfNoMasters: true);
            if (px == null)
            {
                RecordReject("FITS parse failed");
                return;
            }

            var stars = StarDetector.Detect(px.Pixels, px.Width, px.Height);

            lock (_lock)
            {
                // First frame becomes the reference. Reset was the only way to get here with
                // _referenceStars == null while active.
                if (_referenceStars == null)
                {
                    _width = px.Width;
                    _height = px.Height;
                    _referenceStars = stars;
                    _sum = new double[px.Pixels.Length];
                    _count = new int[px.Pixels.Length];
                    for (int i = 0; i < px.Pixels.Length; i++)
                    {
                        _sum[i] = px.Pixels[i];
                        _count[i] = 1;
                    }
                    _framesAccepted = 1;
                    _totalExposureSeconds = exposureSeconds;
                    _lastFrameAt = DateTime.Now;
                    _consecutiveRejects = 0;
                    _log.LogInformation("Live stack reference set: {W}×{H}, {Stars} stars", _width, _height, stars.Count);
                    return;
                }

                if (px.Width != _width || px.Height != _height)
                {
                    RecordReject_locked($"Shape mismatch: frame {px.Width}×{px.Height}, stack {_width}×{_height}");
                    return;
                }

                var match = StarMatcher.FindTranslation(_referenceStars, stars);
                if (match == null)
                {
                    RecordReject_locked($"Alignment failed ({stars.Count} stars detected)");
                    return;
                }

                WarpAndAdd_locked(px.Pixels, match.Dx, match.Dy);
                _framesAccepted++;
                _totalExposureSeconds += exposureSeconds;
                _lastFrameAt = DateTime.Now;
                _consecutiveRejects = 0;
                _log.LogInformation("Live stack +1 frame (dx={Dx:F2}, dy={Dy:F2}, votes={V}/{K}), total {N} frames = {T:F0}s",
                    match.Dx, match.Dy, match.Votes, match.StarsConsidered, _framesAccepted, _totalExposureSeconds);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Live stack processing failed");
            RecordReject(ex.Message);
        }
    }

    /// <summary>Build the current stacked preview PNG. Returns null if no frames have been
    /// accepted yet (caller should return 204 / NoContent).</summary>
    public byte[]? BuildPreviewPng()
    {
        float[]? snapshot;
        int w, h;
        lock (_lock)
        {
            if (_sum == null || _count == null || _framesAccepted == 0) return null;
            snapshot = new float[_sum.Length];
            for (int i = 0; i < _sum.Length; i++)
            {
                var c = _count[i];
                snapshot[i] = c > 0 ? (float)(_sum[i] / c) : 0f;
            }
            w = _width;
            h = _height;
        }
        return CalibrationLibrary.EncodeAutostretchPng(snapshot, w, h);
    }

    // MARK: Warp + add

    /// <summary>Bilinear shift of the incoming pixel buffer by (dx, dy) and accumulate into
    /// the running sum. Caller MUST hold _lock.</summary>
    private void WarpAndAdd_locked(float[] src, double dx, double dy)
    {
        // For each output (reference) pixel (x, y), the matching source pixel is at
        // (x - dx, y - dy) — we're mapping the new frame onto the reference grid. If
        // src-space coords fall off the edge, skip that output pixel for this frame
        // (count array tracks edge coverage).
        var sum = _sum!;
        var count = _count!;
        int w = _width, h = _height;

        for (int y = 0; y < h; y++)
        {
            double srcY = y - dy;
            int fy = (int)Math.Floor(srcY);
            double fracY = srcY - fy;
            if (fy < 0 || fy + 1 >= h) continue;

            int rowOut = y * w;
            int rowA = fy * w;
            int rowB = rowA + w;
            double wY0 = 1.0 - fracY;
            double wY1 = fracY;

            for (int x = 0; x < w; x++)
            {
                double srcX = x - dx;
                int fx = (int)Math.Floor(srcX);
                if (fx < 0 || fx + 1 >= w) continue;

                double fracX = srcX - fx;
                double wX0 = 1.0 - fracX;
                double wX1 = fracX;

                var v =
                    src[rowA + fx]     * wX0 * wY0 +
                    src[rowA + fx + 1] * wX1 * wY0 +
                    src[rowB + fx]     * wX0 * wY1 +
                    src[rowB + fx + 1] * wX1 * wY1;

                sum[rowOut + x] += (float)v;
                count[rowOut + x]++;
            }
        }
    }

    private void RecordReject(string reason)
    {
        lock (_lock) RecordReject_locked(reason);
    }

    private void RecordReject_locked(string reason)
    {
        _framesRejected++;
        _consecutiveRejects++;
        _lastRejectReason = reason;
        _lastRejectAt = DateTime.Now;
        if (_consecutiveRejects == 3)
            _log.LogWarning("Live stack: 3 consecutive rejections — clouds or mid-slew? Last: {Reason}", reason);
        else
            _log.LogInformation("Live stack reject: {Reason}", reason);
    }
}
