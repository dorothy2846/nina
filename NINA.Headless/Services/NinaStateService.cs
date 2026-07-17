using NINA.Core.Interfaces;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Equipment.MyDome;
using NINA.Equipment.Equipment.MyFilterWheel;
using NINA.Equipment.Equipment.MyFlatDevice;
using NINA.Equipment.Equipment.MyFocuser;
using NINA.Equipment.Equipment.MyGuider;
using NINA.Equipment.Equipment.MyRotator;
using NINA.Equipment.Equipment.MySafetyMonitor;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Equipment.MyWeatherData;
using NINA.Equipment.Interfaces.Mediator;

namespace NINA.Headless.Services;

public class NinaStateService :
    ICameraConsumer, ITelescopeConsumer, IGuiderConsumer, IFocuserConsumer,
    IFilterWheelConsumer, IRotatorConsumer, IDomeConsumer, IFlatDeviceConsumer,
    IWeatherDataConsumer, ISafetyMonitorConsumer
{
    private readonly object _guideLock = new();
    private DateTime? _exposureStartTimeUtc;
    private double? _exposureDurationSeconds;

    public NinaStateService(
        ICameraMediator cameraMediator,
        ITelescopeMediator telescopeMediator,
        IGuiderMediator guiderMediator,
        IFocuserMediator focuserMediator,
        IFilterWheelMediator filterWheelMediator,
        IRotatorMediator rotatorMediator,
        IDomeMediator domeMediator,
        IFlatDeviceMediator flatMediator,
        IWeatherDataMediator weatherMediator,
        ISafetyMonitorMediator safetyMediator)
    {
        CameraMediator = cameraMediator;
        TelescopeMediator = telescopeMediator;
        GuiderMediator = guiderMediator;

        CameraMediator.RegisterConsumer(this);
        TelescopeMediator.RegisterConsumer(this);
        GuiderMediator.RegisterConsumer(this);
        GuiderMediator.GuideEvent += OnGuideEvent;
        focuserMediator.RegisterConsumer(this);
        filterWheelMediator.RegisterConsumer(this);
        rotatorMediator.RegisterConsumer(this);
        domeMediator.RegisterConsumer(this);
        flatMediator.RegisterConsumer(this);
        weatherMediator.RegisterConsumer(this);
        safetyMediator.RegisterConsumer(this);

        CameraInfo = CameraMediator.GetInfo();
        TelescopeInfo = TelescopeMediator.GetInfo();
        GuiderInfo = GuiderMediator.GetInfo();
        FocuserInfo = focuserMediator.GetInfo();
        FilterWheelInfo = filterWheelMediator.GetInfo();
        RotatorInfo = rotatorMediator.GetInfo();
        DomeInfo = domeMediator.GetInfo();
        FlatDeviceInfo = flatMediator.GetInfo();
        WeatherDataInfo = weatherMediator.GetInfo();
        SafetyMonitorInfo = safetyMediator.GetInfo();
    }

    public CameraInfo? CameraInfo { get; private set; }

    public TelescopeInfo? TelescopeInfo { get; private set; }

    public GuiderInfo? GuiderInfo { get; private set; }

    public FocuserInfo? FocuserInfo { get; private set; }
    public FilterWheelInfo? FilterWheelInfo { get; private set; }
    public RotatorInfo? RotatorInfo { get; private set; }
    public DomeInfo? DomeInfo { get; private set; }
    public FlatDeviceInfo? FlatDeviceInfo { get; private set; }
    public WeatherDataInfo? WeatherDataInfo { get; private set; }
    public SafetyMonitorInfo? SafetyMonitorInfo { get; private set; }

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
                temperature = Finite(info.Temperature),
                coolerPower = Finite(info.CoolerPower),
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

    public bool IsCaptureInFlight => _exposureStartTimeUtc.HasValue;

    /// <summary>NaN/Infinity are not representable in JSON — SignalR's System.Text.Json
    /// throws on them, which killed the /ws/nina connection during OnConnectedAsync and
    /// left the app in a silent reconnect loop showing everything disconnected. Any
    /// driver-sourced double goes through here: non-finite becomes null.</summary>
    private static double? Finite(double v) => double.IsFinite(v) ? v : (double?)null;

    public object BuildTelescopeStatus()
    {
        var info = TelescopeInfo;
        return info == null
            ? new { connected = false as bool?, name = "Not connected", ra = (double?)null, dec = (double?)null }
            : new
            {
                connected = info.Connected,
                name = info.Name,
                ra = Finite(info.RightAscension),
                dec = Finite(info.Declination),
                alt = Finite(info.Altitude),
                az = Finite(info.Azimuth),
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
                pixelScale = Finite(info.PixelScale),
                rmsRA = Finite(info.RMSError?.RA?.Arcseconds ?? 0),
                rmsDec = Finite(info.RMSError?.Dec?.Arcseconds ?? 0),
                rmsTotal = Finite(info.RMSError?.Total?.Arcseconds ?? 0)
            };
    }

    public object BuildEquipmentStatus() => new
    {
        camera = BuildCameraStatus(),
        telescope = BuildTelescopeStatus(),
        guider = BuildGuiderStatus(),
        focuser = BuildFocuserStatus(),
        filterWheel = BuildFilterWheelStatus(),
        rotator = BuildRotatorStatus(),
        dome = BuildDomeStatus(),
        flatDevice = BuildFlatDeviceStatus(),
        weather = BuildWeatherStatus(),
        safetyMonitor = BuildSafetyMonitorStatus()
    };

    public object BuildFocuserStatus() => FocuserInfo == null
        ? new { connected = false as bool?, name = "Not connected" }
        : new { connected = FocuserInfo.Connected, name = FocuserInfo.Name,
                position = FocuserInfo.Position, temperature = Finite(FocuserInfo.Temperature),
                isMoving = FocuserInfo.IsMoving, stepSize = FocuserInfo.StepSize };

    public object BuildFilterWheelStatus() => FilterWheelInfo == null
        ? new { connected = false as bool?, name = "Not connected" }
        : new { connected = FilterWheelInfo.Connected, name = FilterWheelInfo.Name,
                isMoving = FilterWheelInfo.IsMoving };

    public object BuildRotatorStatus() => RotatorInfo == null
        ? new { connected = false as bool?, name = "Not connected" }
        : new { connected = RotatorInfo.Connected, name = RotatorInfo.Name,
                position = RotatorInfo.Position, mechanicalPosition = RotatorInfo.MechanicalPosition,
                isMoving = RotatorInfo.IsMoving };

    public object BuildDomeStatus() => DomeInfo == null
        ? new { connected = false as bool?, name = "Not connected" }
        : new { connected = DomeInfo.Connected, name = DomeInfo.Name,
                azimuth = Finite(DomeInfo.Azimuth), slewing = DomeInfo.Slewing };

    public object BuildFlatDeviceStatus() => FlatDeviceInfo == null
        ? new { connected = false as bool?, name = "Not connected" }
        : new { connected = FlatDeviceInfo.Connected, name = FlatDeviceInfo.Name,
                lightOn = FlatDeviceInfo.LightOn, brightness = FlatDeviceInfo.Brightness };

    public object BuildWeatherStatus() => WeatherDataInfo == null
        ? new { connected = false as bool?, name = "Not connected" }
        : new { connected = WeatherDataInfo.Connected, name = WeatherDataInfo.Name,
                temperature = Finite(WeatherDataInfo.Temperature), humidity = Finite(WeatherDataInfo.Humidity),
                pressure = Finite(WeatherDataInfo.Pressure), dewPoint = Finite(WeatherDataInfo.DewPoint),
                windSpeed = Finite(WeatherDataInfo.WindSpeed), cloudCover = Finite(WeatherDataInfo.CloudCover),
                skyTemperature = Finite(WeatherDataInfo.SkyTemperature) };

    public object BuildSafetyMonitorStatus() => SafetyMonitorInfo == null
        ? new { connected = false as bool?, name = "Not connected" }
        : new { connected = SafetyMonitorInfo.Connected, name = SafetyMonitorInfo.Name,
                isSafe = SafetyMonitorInfo.IsSafe };

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

    void IDeviceConsumer<FocuserInfo>.UpdateDeviceInfo(FocuserInfo deviceInfo)
    {
        FocuserInfo = deviceInfo;
        NotifyStateChanged("focuser", BuildFocuserStatus());
    }
    // IFocuserConsumer extras — autofocus orchestration lives in
    // FocuserOrchestrator on the headless server, not here. State service
    // just reflects the device-info channel.
    public void UpdateEndAutoFocusRun(AutoFocusInfo info) { }
    public void UpdateUserFocused(FocuserInfo info)
    {
        FocuserInfo = info;
        NotifyStateChanged("focuser", BuildFocuserStatus());
    }
    void IDeviceConsumer<FilterWheelInfo>.UpdateDeviceInfo(FilterWheelInfo deviceInfo)
    {
        FilterWheelInfo = deviceInfo;
        NotifyStateChanged("filterWheel", BuildFilterWheelStatus());
    }
    void IDeviceConsumer<RotatorInfo>.UpdateDeviceInfo(RotatorInfo deviceInfo)
    {
        RotatorInfo = deviceInfo;
        NotifyStateChanged("rotator", BuildRotatorStatus());
    }
    void IDeviceConsumer<DomeInfo>.UpdateDeviceInfo(DomeInfo deviceInfo)
    {
        DomeInfo = deviceInfo;
        NotifyStateChanged("dome", BuildDomeStatus());
    }
    void IDeviceConsumer<FlatDeviceInfo>.UpdateDeviceInfo(FlatDeviceInfo deviceInfo)
    {
        FlatDeviceInfo = deviceInfo;
        NotifyStateChanged("flatDevice", BuildFlatDeviceStatus());
    }
    void IDeviceConsumer<WeatherDataInfo>.UpdateDeviceInfo(WeatherDataInfo deviceInfo)
    {
        WeatherDataInfo = deviceInfo;
        NotifyStateChanged("weather", BuildWeatherStatus());
    }
    void IDeviceConsumer<SafetyMonitorInfo>.UpdateDeviceInfo(SafetyMonitorInfo deviceInfo)
    {
        SafetyMonitorInfo = deviceInfo;
        NotifyStateChanged("safetyMonitor", BuildSafetyMonitorStatus());
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
            // PHD2 reports NaN distances around lost-star/settling states; a single
            // non-finite double in GuideHistory makes SignalR's JSON serializer throw
            // during OnConnectedAsync — the exact WS-handshake-death class fixed in
            // the status builders. Sanitize at the source.
            GuideHistory.Add(new GuidePoint(
                Finite(guideStep.RADistanceRaw) ?? 0,
                Finite(guideStep.DECDistanceRaw) ?? 0,
                DateTime.UtcNow));
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
