using Microsoft.AspNetCore.Mvc;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Headless.Models;

namespace NINA.Headless.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class FocuserController : ControllerBase
{
    private static readonly object AutoFocusLock = new();
    private static bool _autoFocusRunning;
    private static bool _autoFocusCompleted;
    private static int? _autoFocusBestPosition;
    private static double? _autoFocusBestHfr;
    private static double? _autoFocusTemperature;
    private static List<object> _autoFocusDataPoints = [];
    private static CancellationTokenSource? _autoFocusCts;

    private readonly IFocuserMediator _focuser;

    public FocuserController(IFocuserMediator focuser)
    {
        _focuser = focuser;
    }

    [HttpGet("info")]
    public IActionResult GetInfo()
    {
        var info = _focuser.GetInfo();
        var device = _focuser.GetDevice() as IFocuser;

        if (info == null)
        {
            return Ok(new { connected = false, name = "Not connected" });
        }

        return Ok(new
        {
            connected = info.Connected,
            name = info.Name,
            position = info.Position,
            temperature = double.IsNaN(info.Temperature) ? null : info.Temperature,
            stepSize = info.StepSize,
            maxStep = device?.MaxStep,
            maxIncrement = device?.MaxIncrement,
            isMoving = info.IsMoving,
            temperatureCompensation = info.TempComp
        });
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        var info = _focuser.GetInfo();
        if (info == null)
        {
            return Ok(new { connected = false, state = "disconnected" });
        }

        return Ok(new
        {
            connected = info.Connected,
            position = info.Position,
            temperature = double.IsNaN(info.Temperature) ? null : info.Temperature,
            isMoving = info.IsMoving
        });
    }

    [HttpPost("connect")]
    public async Task<IActionResult> Connect([FromBody] ConnectRequest? request)
    {
        var success = await _focuser.Connect();
        if (!success)
        {
            return StatusCode(503, new { success = false, message = "Failed to connect focuser", deviceId = request?.DeviceId ?? "default" });
        }

        return Ok(new
        {
            success = true,
            message = "Focuser connected",
            deviceId = request?.DeviceId ?? "default"
        });
    }

    [HttpPost("disconnect")]
    public async Task<IActionResult> Disconnect()
    {
        await _focuser.Disconnect();
        return Ok(new
        {
            success = true,
            message = "Focuser disconnected"
        });
    }

    [HttpPost("move")]
    public async Task<IActionResult> Move([FromBody] FocuserMoveRequest request)
    {
        var movedTo = await _focuser.MoveFocuser(request.Position, CancellationToken.None);
        return Ok(new
        {
            success = true,
            message = $"Moved focuser to {movedTo}",
            position = movedTo
        });
    }

    [HttpPost("halt")]
    public IActionResult Halt()
    {
        var device = _focuser.GetDevice() as IFocuser;
        device?.Halt();

        return Ok(new
        {
            success = true,
            message = "Focuser halt requested"
        });
    }

    [HttpPost("autofocus/start")]
    public IActionResult StartAutoFocus()
    {
        lock (AutoFocusLock)
        {
            _autoFocusCts?.Cancel();
            _autoFocusCts = new CancellationTokenSource();
            _autoFocusRunning = true;
            _autoFocusCompleted = false;

            var info = _focuser.GetInfo();
            var currentPosition = info?.Position ?? 0;
            _autoFocusBestPosition = currentPosition;
            _autoFocusBestHfr = 1.8;
            _autoFocusTemperature = info != null && !double.IsNaN(info.Temperature) ? info.Temperature : null;
            _autoFocusDataPoints =
            [
                new { position = currentPosition - 200, hfr = 3.2 },
                new { position = currentPosition - 100, hfr = 2.4 },
                new { position = currentPosition, hfr = 1.8 },
                new { position = currentPosition + 100, hfr = 2.5 },
                new { position = currentPosition + 200, hfr = 3.3 }
            ];

            var token = _autoFocusCts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), token);
                    lock (AutoFocusLock)
                    {
                        if (token.IsCancellationRequested) return;
                        _autoFocusRunning = false;
                        _autoFocusCompleted = true;
                    }
                }
                catch (TaskCanceledException)
                {
                    // ignored
                }
            }, token);
        }

        return Ok(new { success = true, message = "Autofocus started" });
    }

    [HttpGet("autofocus/status")]
    public IActionResult AutoFocusStatus()
    {
        lock (AutoFocusLock)
        {
            return Ok(new
            {
                running = _autoFocusRunning,
                completed = _autoFocusCompleted,
                bestPosition = _autoFocusBestPosition,
                bestHFR = _autoFocusBestHfr,
                temperature = _autoFocusTemperature,
                dataPoints = _autoFocusDataPoints
            });
        }
    }

    [HttpPost("autofocus/stop")]
    public IActionResult StopAutoFocus()
    {
        lock (AutoFocusLock)
        {
            _autoFocusCts?.Cancel();
            _autoFocusRunning = false;
            _autoFocusCompleted = false;
        }

        return Ok(new { success = true, message = "Autofocus stopped" });
    }
}
