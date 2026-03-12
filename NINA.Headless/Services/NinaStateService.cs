using NINA.Core.Interfaces;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Equipment.MyGuider;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces.Mediator;

namespace NINA.Headless.Services;

public class NinaStateService : ICameraConsumer, ITelescopeConsumer, IGuiderConsumer
{
    private readonly object _guideLock = new();

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

    public event Action<string, object>? StateChanged;

    public void NotifyStateChanged(string type, object data)
    {
        StateChanged?.Invoke(type, data);
    }

    public object BuildCameraStatus()
    {
        var info = CameraInfo;
        return info == null
            ? new { connected = false as bool?, name = "Not connected", temperature = (double?)null }
            : new
            {
                connected = info.Connected,
                name = info.Name,
                temperature = info.Temperature,
                coolerPower = info.CoolerPower,
                gain = info.Gain,
                offset = info.Offset,
                binning = info.BinX,
                state = info.CameraState.ToString()
            };
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
}

public record GuidePoint(double RA, double Dec, DateTime Timestamp);
