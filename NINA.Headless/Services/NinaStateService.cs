using NINA.Core.Interfaces;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Equipment.MyGuider;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces.Mediator;

namespace NINA.Headless.Services;

public class NinaStateService : ICameraConsumer, ITelescopeConsumer, IGuiderConsumer
{
    private readonly object _guideLock = new();
    private DateTime? _exposureStartTimeUtc;
    private double? _exposureDurationSeconds;

    public NinaStateService(
        ICameraMediator cameraMediator,
        ITelescopeMediator telescopeMediator,
        IGuiderMediator guiderMediator)
    {
        CameraMediator = cameraMediator;
        TelescopeMediator = telescopeMediator;
        GuiderMediator = guiderMediator;

        CameraMediator.RegisterConsumer(this);
        TelescopeMediator.RegisterConsumer(this);
        GuiderMediator.RegisterConsumer(this);
        GuiderMediator.GuideEvent += OnGuideEvent;

        CameraInfo = CameraMediator.GetInfo();
        TelescopeInfo = TelescopeMediator.GetInfo();
        GuiderInfo = GuiderMediator.GetInfo();
    }

    public CameraInfo? CameraInfo { get; private set; }

    public TelescopeInfo? TelescopeInfo { get; private set; }

    public GuiderInfo? GuiderInfo { get; private set; }

    public ICameraMediator CameraMediator { get; }

    public ITelescopeMediator TelescopeMediator { get; }

    public IGuiderMediator GuiderMediator { get; }

    public List<GuidePoint> GuideHistory { get; } = new();

    public byte[]? LatestImageData { get; set; }

    // Image Stretch parameters
    public double StretchBlackPoint { get; private set; } = 0.0;
    public double StretchWhitePoint { get; private set; } = 1.0;
    public bool AutoStretchEnabled { get; private set; } = true;

    public void SetImageStretchParams(double black, double white, bool autoEnabled)
    {
        StretchBlackPoint = black;
        StretchWhitePoint = white;
        AutoStretchEnabled = autoEnabled;
    }

    public event Action<string, object>? StateChanged;

    public void NotifyStateChanged(string type, object data)
    {
        StateChanged?.Invoke(type, data);
    }

    public object BuildCameraStatus()
    {
        var info = CameraInfo;
        var (exposureTime, exposureProgress) = GetCameraExposureMetrics();

        return info == null
            ? new
            {
                connected = false as bool?,
                name = "Not connected",
                temperature = (double?)null,
                exposureTime = (double?)null,
                exposureProgress = (double?)null
            }
            : new
            {
                connected = info.Connected,
                name = info.Name,
                temperature = info.Temperature,
                coolerPower = info.CoolerPower,
                gain = info.Gain,
                offset = info.Offset,
                binning = info.BinX,
                state = info.CameraState.ToString(),
                exposureTime,
                exposureProgress
            };
    }

    public (double? exposureTime, double? exposureProgress) GetCameraExposureMetrics()
    {
        var info = CameraInfo;
        if (info == null || !info.IsExposing)
        {
            return (null, null);
        }

        var nowUtc = DateTime.UtcNow;
        var exposureEndUtc = ToUtc(info.ExposureEndTime);
        var exposureTime = Math.Max(0d, (exposureEndUtc - nowUtc).TotalSeconds);

        double? exposureProgress = null;
        var totalSeconds = _exposureDurationSeconds;

        if ((!totalSeconds.HasValue || totalSeconds.Value <= 0d) &&
            _exposureStartTimeUtc.HasValue &&
            exposureEndUtc > _exposureStartTimeUtc.Value)
        {
            totalSeconds = (exposureEndUtc - _exposureStartTimeUtc.Value).TotalSeconds;
        }

        if (totalSeconds is > 0d)
        {
            var elapsedSeconds = totalSeconds.Value - exposureTime;
            exposureProgress = Math.Clamp(elapsedSeconds / totalSeconds.Value, 0d, 1d);
        }

        return (exposureTime, exposureProgress);
    }

    public void MarkExposureStarted(double exposureDurationSeconds)
    {
        _exposureStartTimeUtc = DateTime.UtcNow;
        _exposureDurationSeconds = exposureDurationSeconds > 0d ? exposureDurationSeconds : null;
    }

    public void MarkExposureFinished()
    {
        _exposureStartTimeUtc = null;
        _exposureDurationSeconds = null;
    }

    public object BuildTelescopeStatus()
    {
        var info = TelescopeInfo;
        return info == null
            ? new { connected = false as bool?, name = "Not connected", ra = (double?)null, dec = (double?)null }
            : new
            {
                connected = info.Connected,
                name = info.Name,
                ra = info.RightAscension,
                dec = info.Declination,
                alt = info.Altitude,
                az = info.Azimuth,
                tracking = info.TrackingEnabled,
                parked = info.AtPark,
                slewing = info.Slewing,
                pierSide = info.SideOfPier.ToString()
            };
    }

    public object BuildGuiderStatus()
    {
        var info = GuiderInfo;
        return info == null
            ? new { connected = false as bool?, name = "Not connected" }
            : new
            {
                connected = info.Connected,
                name = info.Name,
                pixelScale = info.PixelScale,
                rmsRA = info.RMSError?.RA?.Arcseconds ?? 0,
                rmsDec = info.RMSError?.Dec?.Arcseconds ?? 0,
                rmsTotal = info.RMSError?.Total?.Arcseconds ?? 0
            };
    }

    public object BuildEquipmentStatus() => new
    {
        camera = BuildCameraStatus(),
        telescope = BuildTelescopeStatus(),
        guider = BuildGuiderStatus()
    };

    void IDeviceConsumer<CameraInfo>.UpdateDeviceInfo(CameraInfo deviceInfo)
    {
        if (deviceInfo.IsExposing && !_exposureStartTimeUtc.HasValue)
        {
            _exposureStartTimeUtc = DateTime.UtcNow;
            var endUtc = ToUtc(deviceInfo.ExposureEndTime);
            var inferredDuration = (endUtc - _exposureStartTimeUtc.Value).TotalSeconds;
            if (inferredDuration > 0d)
            {
                _exposureDurationSeconds = inferredDuration;
            }
        }

        if (!deviceInfo.IsExposing)
        {
            MarkExposureFinished();
        }

        CameraInfo = deviceInfo;
        NotifyStateChanged("camera", BuildCameraStatus());
    }

    void IDeviceConsumer<TelescopeInfo>.UpdateDeviceInfo(TelescopeInfo deviceInfo)
    {
        TelescopeInfo = deviceInfo;
        NotifyStateChanged("telescope", BuildTelescopeStatus());
    }

    void IDeviceConsumer<GuiderInfo>.UpdateDeviceInfo(GuiderInfo deviceInfo)
    {
        GuiderInfo = deviceInfo;
        NotifyStateChanged("guider", BuildGuiderStatus());
    }

    public void Dispose()
    {
        GuiderMediator.GuideEvent -= OnGuideEvent;
        CameraMediator.RemoveConsumer(this);
        TelescopeMediator.RemoveConsumer(this);
        GuiderMediator.RemoveConsumer(this);
    }

    private void OnGuideEvent(object? sender, IGuideStep guideStep)
    {
        lock (_guideLock)
        {
            GuideHistory.Add(new GuidePoint(guideStep.RADistanceRaw, guideStep.DECDistanceRaw, DateTime.UtcNow));
            if (GuideHistory.Count > 100)
            {
                GuideHistory.RemoveAt(0);
            }
        }

        NotifyStateChanged("guide", new
        {
            point = GuideHistory.Count == 0 ? null : GuideHistory[^1],
            historyCount = GuideHistory.Count
        });
    }

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Local).ToUniversalTime()
    };
}

public record GuidePoint(double RA, double Dec, DateTime Timestamp);
