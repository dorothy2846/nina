using Microsoft.Extensions.Hosting;
using NINA.Core.Enum;
using NINA.Equipment.Equipment.MyCamera;
using NINA.Equipment.Equipment.MyGuider;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Headless.Models;
using NINA.WPF.Base.Mediator;

namespace NINA.Headless.Services;

public class SimulatorService : BackgroundService
{
    private const double BaseRightAscensionHours = 5.5833;
    private const double BaseDeclinationDegrees = -5.39;

    private readonly CameraMediator _cameraMediator;
    private readonly TelescopeMediator _telescopeMediator;
    private readonly GuiderMediator _guiderMediator;

    private readonly CameraInfo _cameraInfo;
    private readonly TelescopeInfo _telescopeInfo;
    private readonly GuiderInfo _guiderInfo;
    private readonly FilterWheelInfo _filterWheelInfo;
    private readonly DomeInfo _domeInfo;

    private double _focuserPosition = 5300.0;
    private double _focuserTemp = 20.0;
    private bool _focuserIsMoving = false;

    private double _rotatorPosition = 0.0;
    private double _rotatorMechanicalPosition = 0.0;
    private bool _rotatorIsMoving = false;

    public SimulatorService(
        CameraMediator cameraMediator,
        TelescopeMediator telescopeMediator,
        GuiderMediator guiderMediator)
    {
        _cameraMediator = cameraMediator;
        _telescopeMediator = telescopeMediator;
        _guiderMediator = guiderMediator;

        _cameraInfo = new CameraInfo
        {
            Connected = true,
            Name = "Simulator Camera",
            Temperature = -10.0,
            CoolerPower = 65.0,
            CoolerOn = true,
            TemperatureSetPoint = -10.0,
            Gain = 100,
            Offset = 30,
            BinX = 1,
            BinY = 1,
            BitDepth = 16,
            XSize = 4656,
            YSize = 3520,
            PixelSize = 3.76,
            SensorType = SensorType.Monochrome,
            CanSetTemperature = true,
            IsExposing = false,
            CameraState = CameraStates.Idle
        };

        _telescopeInfo = new TelescopeInfo
        {
            Connected = true,
            Name = "Simulator Mount",
            RightAscension = BaseRightAscensionHours,
            Declination = BaseDeclinationDegrees,
            Altitude = 45.0,
            Azimuth = 180.0,
            TrackingEnabled = true,
            AtPark = false,
            Slewing = false,
            SideOfPier = PierSide.pierWest,
            SiderealTime = DateTime.UtcNow.Hour + (DateTime.UtcNow.Minute / 60.0)
        };

        _guiderInfo = new GuiderInfo
        {
            Connected = true,
            Name = "Simulator Guider (PHD2)",
            PixelScale = 1.5,
            RMSError = new RMSError(0.42, 0.36, 0.92, 0.84, 0.55, 1.5)
        };

        _filterWheelInfo = new FilterWheelInfo
        {
            Connected = true,
            Name = "Simulator Filter Wheel",
            CurrentPosition = 0,
            IsMoving = false,
            Filters = new List<string> { "L-Pro", "Ha", "OIII", "SII", "R", "G", "B" }
        };

        _domeInfo = new DomeInfo
        {
            Connected = true,
            Name = "Simulator Dome",
            Azimuth = 180.0,
            Altitude = 0.0,
            AtHome = false,
            AtPark = false,
            ShutterStatus = "Open",
            Slewing = false,
            Slaved = false
        };
    }

    public (double Position, double Temperature, bool IsMoving) GetFocuserSimData()
    {
        return (_focuserPosition, _focuserTemp, _focuserIsMoving);
    }

    public void SetFocuserPosition(double position)
    {
        _focuserPosition = Math.Clamp(position, 5200.0, 5400.0);
        _focuserIsMoving = false;
    }

    public (double Position, double MechanicalPosition, bool IsMoving) GetRotatorSimData()
    {
        return (_rotatorPosition, _rotatorMechanicalPosition, _rotatorIsMoving);
    }

    public void SetRotatorPosition(double position)
    {
        _rotatorPosition = WrapDegrees(position);
        _rotatorIsMoving = false;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        BroadcastAll();

        while (!stoppingToken.IsCancellationRequested)
        {
            UpdateCamera();
            UpdateTelescope();
            UpdateGuider();
            UpdateFocuser();
            UpdateDome();
            UpdateRotator();
            BroadcastAll();

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }

    private void BroadcastAll()
    {
        _cameraMediator.Broadcast(_cameraInfo);
        _telescopeMediator.Broadcast(_telescopeInfo);
        _guiderMediator.Broadcast(_guiderInfo);
    }

    private void UpdateCamera()
    {
        var tempJitter = (Random.Shared.NextDouble() * 0.2) - 0.1;
        _cameraInfo.Temperature = Math.Clamp(_cameraInfo.Temperature + tempJitter, -10.5, -9.5);

        var powerJitter = (Random.Shared.NextDouble() * 3.0) - 1.5;
        _cameraInfo.CoolerPower = Math.Clamp(_cameraInfo.CoolerPower + powerJitter, 55.0, 75.0);
        _cameraInfo.ExposureEndTime = DateTime.UtcNow;
    }

    private void UpdateTelescope()
    {
        _telescopeInfo.SiderealTime = DateTime.UtcNow.Hour + (DateTime.UtcNow.Minute / 60.0) + (DateTime.UtcNow.Second / 3600.0);

        var trackingStep = _telescopeInfo.TrackingEnabled ? (1.0 / 3600.0) : 0.0;
        var randomDrift = (Random.Shared.NextDouble() * 0.00002) - 0.00001;
        _telescopeInfo.RightAscension = WrapHours(_telescopeInfo.RightAscension + trackingStep + randomDrift);

        var decJitter = (Random.Shared.NextDouble() * 0.00006) - 0.00003;
        _telescopeInfo.Declination = Math.Clamp(_telescopeInfo.Declination + decJitter, BaseDeclinationDegrees - 0.3, BaseDeclinationDegrees + 0.3);

        var azDrift = (Random.Shared.NextDouble() * 0.08) - 0.04;
        _telescopeInfo.Azimuth = WrapDegrees(_telescopeInfo.Azimuth + azDrift);

        var altJitter = (Random.Shared.NextDouble() * 0.08) - 0.04;
        _telescopeInfo.Altitude = Math.Clamp(_telescopeInfo.Altitude + altJitter, 40.0, 55.0);
    }

    private void UpdateGuider()
    {
        var correction = Random.Shared.NextDouble() < 0.25;
        var raRms = correction
            ? 0.2 + (Random.Shared.NextDouble() * 0.35)
            : 0.45 + (Random.Shared.NextDouble() * 0.5);
        var decRms = correction
            ? 0.2 + (Random.Shared.NextDouble() * 0.35)
            : 0.4 + (Random.Shared.NextDouble() * 0.45);

        var peakRa = raRms + 0.35 + (Random.Shared.NextDouble() * 0.25);
        var peakDec = decRms + 0.3 + (Random.Shared.NextDouble() * 0.25);
        var total = Math.Sqrt((raRms * raRms) + (decRms * decRms));

        _guiderInfo.RMSError = new RMSError(raRms, decRms, peakRa, peakDec, total, _guiderInfo.PixelScale);
    }

    private void UpdateFocuser()
    {
        var positionJitter = (Random.Shared.NextDouble() * 4.0) - 2.0;
        _focuserPosition = Math.Clamp(_focuserPosition + positionJitter, 5200.0, 5400.0);

        var tempJitter = (Random.Shared.NextDouble() * 0.1) - 0.05;
        _focuserTemp = Math.Clamp(_focuserTemp + tempJitter, 18.0, 22.0);
    }

    private void UpdateDome()
    {
        var azDrift = (Random.Shared.NextDouble() * 0.08) - 0.04;
        _domeInfo.Azimuth = WrapDegrees(_domeInfo.Azimuth + azDrift);
    }

    private void UpdateRotator()
    {
        // Simulate slow drift of ±0.1 degrees per tick
        var positionDrift = (Random.Shared.NextDouble() * 0.2) - 0.1;
        _rotatorPosition = WrapDegrees(_rotatorPosition + positionDrift);
        _rotatorMechanicalPosition = _rotatorPosition;
    }

    private static double WrapHours(double value)
    {
        var result = value % 24.0;
        return result < 0 ? result + 24.0 : result;
    }

    private static double WrapDegrees(double value)
    {
        var result = value % 360.0;
        return result < 0 ? result + 360.0 : result;
    }
}

public class FilterWheelInfo
{
    public bool Connected { get; set; } = true;
    public string Name { get; set; } = "Simulator Filter Wheel";
    public int CurrentPosition { get; set; } = 0;
    public bool IsMoving { get; set; } = false;
    public List<string> Filters { get; set; } = new() { "L-Pro", "Ha", "OIII", "SII", "R", "G", "B" };
}
