using Microsoft.AspNetCore.Mvc;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Core.Model.Equipment;
using NINA.Headless.Models;
using NINA.Headless.Services;
using NINA.Headless.Services.Remote;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System;
using System.Linq;

namespace NINA.Headless.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class FocuserController : ControllerBase
{
    private static readonly object AutoFocusLock = new();
    private static CancellationTokenSource? _autoFocusCts;

    private readonly IFocuserMediator _focuser;
    private readonly NinaStateService _state;
    private readonly RemoteEventBus _eventBus;
    private readonly OneShotAiService _aiService;
    private readonly EquipmentSelectionService _equipment;
    private readonly IndiDiscoveryService _indi;
    private readonly AlpacaClient _alpaca;

    public FocuserController(IFocuserMediator focuser, NinaStateService state, RemoteEventBus eventBus,
        OneShotAiService aiService, EquipmentSelectionService equipment, IndiDiscoveryService indi, AlpacaClient alpaca)
    {
        _focuser = focuser;
        _state = state;
        _eventBus = eventBus;
        _aiService = aiService;
        _equipment = equipment;
        _indi = indi;
        _alpaca = alpaca;
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        var selected = _equipment.GetSelected(DeviceKind.Focuser);
        if (_equipment.IsConnected(DeviceKind.Focuser) && selected?.Provider == EquipmentProvider.Indi)
        {
            var s = _indi.BuildFocuserStatus(selected.UniqueId);
            if (s != null) return Ok(s);
        }
        return Ok(new { connected = false, name = "Not connected" });
    }

    [HttpPost("connect")]
    public Task<IActionResult> Connect([FromBody] ConnectRequest? request) =>
        IndiDeviceConnectFlow.ConnectAsync(_equipment, _indi, DeviceKind.Focuser, "Focuser", request, HttpContext.RequestAborted, _alpaca);

    [HttpPost("disconnect")]
    public async Task<IActionResult> Disconnect()
    {
        var selected = _equipment.GetSelected(DeviceKind.Focuser);
        if (selected?.Provider == EquipmentProvider.Indi)
            await _indi.DisconnectDeviceAsync(selected.UniqueId, HttpContext.RequestAborted);
        _equipment.Disconnect(DeviceKind.Focuser);
        return Ok(new { success = true, message = "Focuser disconnected" });
    }

    [HttpGet("info")]
    public IActionResult GetInfo()
    {
        var info = _focuser.GetInfo();
        if (info == null) return Ok(new { connected = false });

        var device = _focuser.GetDevice() as IFocuser;
        return Ok(new
        {
            connected = info.Connected,
            name = info.Name,
            position = info.Position,
            temperature = double.IsNaN(info.Temperature) ? (double?)null : info.Temperature,
            stepSize = info.StepSize,
            isMoving = info.IsMoving
        });
    }

    [HttpPost("move")]
    public async Task<IActionResult> Move([FromBody] FocuserMoveRequest request)
    {
        var selected = _equipment.GetSelected(DeviceKind.Focuser);
        if (_equipment.IsConnected(DeviceKind.Focuser) && selected?.Provider == EquipmentProvider.Indi)
        {
            await _indi.FocuserMoveAsync(selected.UniqueId, request.Position, HttpContext.RequestAborted);
            return Ok(new { success = true, position = request.Position });
        }
        var movedTo = await _focuser.MoveFocuser(request.Position, CancellationToken.None);
        return Ok(new { success = true, position = movedTo });
    }

    [HttpPost("halt")]
    public async Task<IActionResult> Halt()
    {
        var selected = _equipment.GetSelected(DeviceKind.Focuser);
        if (_equipment.IsConnected(DeviceKind.Focuser) && selected?.Provider == EquipmentProvider.Indi)
        {
            await _indi.FocuserHaltAsync(selected.UniqueId, HttpContext.RequestAborted);
        }
        return Ok(new { success = true, message = "Halted" });
    }

    public record TempCompRequest(bool Enabled);

    [HttpPost("temp-compensation")]
    public async Task<IActionResult> SetTempCompensation([FromBody] TempCompRequest request)
    {
        var selected = _equipment.GetSelected(DeviceKind.Focuser);
        if (!_equipment.IsConnected(DeviceKind.Focuser) || selected?.Provider != EquipmentProvider.Indi)
            return BadRequest(new { success = false, message = "Focuser not connected via INDI" });

        var ok = await _indi.SetFocuserTempCompensationAsync(selected.UniqueId, request.Enabled, HttpContext.RequestAborted);
        return Ok(new {
            success = ok,
            message = ok ? null : "이 focuser 드라이버는 온도 보상을 지원하지 않습니다."
        });
    }

    public record BacklashRequest(int Steps);

    /// <summary>Push driver-level backlash compensation. Per-filter focus offsets and
    /// autofocus passes rely on this to reverse direction cleanly. <c>steps=0</c>
    /// disables.</summary>
    [HttpPost("backlash")]
    public async Task<IActionResult> SetBacklash([FromBody] BacklashRequest request)
    {
        var selected = _equipment.GetSelected(DeviceKind.Focuser);
        if (!_equipment.IsConnected(DeviceKind.Focuser) || selected?.Provider != EquipmentProvider.Indi)
            return BadRequest(new { success = false, message = "Focuser not connected via INDI" });

        var ok = await _indi.SetFocuserBacklashAsync(selected.UniqueId, Math.Max(0, request.Steps), HttpContext.RequestAborted);
        return Ok(new {
            success = ok,
            message = ok ? null : "이 focuser 드라이버는 backlash 설정을 지원하지 않습니다."
        });
    }

    [HttpPost("autofocus/start")]
    public IActionResult StartAutoFocus()
    {
        lock (AutoFocusLock)
        {
            _autoFocusCts?.Cancel();
            _autoFocusCts = new CancellationTokenSource();

            var token = _autoFocusCts.Token;
            _ = Task.Run(() => PerformAutofocusAsync(token), token);
        }

        return Ok(new { success = true, message = "Autofocus started" });
    }

    private async Task PerformAutofocusAsync(CancellationToken token)
    {
        try
        {
            var profile = ProfileSyncController.ActiveCloudProfile;
            int stepSize = profile?.FocuserStepSize ?? 50;
            int initialOffsets = profile?.InitialOffsetSteps ?? 4;
            int backlash = profile?.Backlash ?? 1000;

            var info = _focuser.GetInfo();
            if (info == null || !info.Connected) throw new Exception("Focuser not connected");

            int initialPosition = info.Position;
            int outerBoundary = initialPosition + (stepSize * initialOffsets);
            int totalPoints = (initialOffsets * 2) + 1;

            var points = new List<AutofocusMath.FocuserPoint>();

            await BroadcastAfStatusAsync(true, false, null, points);

            // 1. Move to Outer Boundary (With Overshoot for Backlash)
            await _focuser.MoveFocuser(outerBoundary + backlash, token);
            await _focuser.MoveFocuser(outerBoundary, token);
            await Task.Delay(2000, token); // Settle

            int currentTarget = outerBoundary;
            int simulatedPerfectFocus = initialPosition - (stepSize / 2); // Mock exact point for testing

            for (int i = 0; i < totalPoints; i++)
            {
                if (token.IsCancellationRequested) break;

                // Move IN
                if (i > 0)
                {
                    currentTarget -= stepSize;
                    await _focuser.MoveFocuser(currentTarget, token);
                    await Task.Delay(2000, token); // Settle
                }

                // Simulate Capture & HFD Measurement (Math.Abs distance creates a perfect V curve)
                // In reality: await _state.CameraMediator.Capture(...) -> extract HFD
                await Task.Delay(1000, token); // Simulate capture time
                
                double distance = Math.Abs(currentTarget - simulatedPerfectFocus);
                // Hyperbolic simulation: y = a * cosh(t)
                double a = 2.0; // min HFD
                double b = 150.0;
                double hfd = a * Math.Cosh(distance / b) + (new Random().NextDouble() * 0.2); // Add light noise

                points.Add(new AutofocusMath.FocuserPoint(currentTarget, hfd));
                
                // RANSAC / IQR Filtering
                points = AutofocusMath.FilterOutliers(points);

                // Live Fit calculation
                var fit = AutofocusMath.CalculateHyperbolicFit(points);

                await BroadcastAfStatusAsync(true, false, fit, points);
            }

            // 2. Final Fit and Slew
            var finalFit = AutofocusMath.CalculateHyperbolicFit(points);
            
            if (finalFit.Success)
            {
                int targetFocus = (int)finalFit.P;
                
                // Backlash Overshoot outwards, then in
                await _focuser.MoveFocuser(targetFocus + backlash, token);
                await _focuser.MoveFocuser(targetFocus, token);
            }

            await BroadcastAfStatusAsync(false, true, finalFit, points);
        }
        catch (Exception)
        {
            _eventBus.Broadcast("AutofocusError", "Routine failed or aborted");
        }
    }

    private async Task BroadcastAfStatusAsync(bool running, bool completed, AutofocusMath.HyperbolicFitResult? fit, List<AutofocusMath.FocuserPoint> points)
    {
        var payload = new
        {
            running,
            completed,
            points = points.Select(p => new { x = p.Position, y = p.Hfd, isOutlier = p.IsOutlier }),
            fitParameters = fit != null && fit.Success ? new { a = fit.A, b = fit.B, p = fit.P } : null,
            optimalPosition = fit != null && fit.Success ? fit.P : (double?)null
        };

        _eventBus.Broadcast("AutofocusLiveUpdate", payload);
    }

    [HttpPost("autofocus/stop")]
    public IActionResult StopAutoFocus()
    {
        lock (AutoFocusLock)
        {
            _autoFocusCts?.Cancel();
        }
        return Ok(new { success = true });
    }

    [HttpPost("ai-autofocus/calibrate")]
    public async Task<IActionResult> CalibrateAiOneShot()
    {
        // 실시간 캘리브레이션 모의 진행 (현재 사진 -> 200스텝 이동 -> 비율 도출)
        await Task.Delay(5000); 
        return Ok(new { success = true, multiplier = 4.2 }); // 1 micron = 4.2 steps 상수 도출
    }

    [HttpPost("ai-autofocus/start")]
    public IActionResult StartAiOneShot()
    {
        lock (AutoFocusLock)
        {
            _autoFocusCts?.Cancel();
            _autoFocusCts = new CancellationTokenSource();

            var token = _autoFocusCts.Token;
            _ = Task.Run(() => PerformAiOneShotAsync(token), token);
        }
        return Ok(new { success = true, message = "AI Autofocus started" });
    }

    private async Task PerformAiOneShotAsync(CancellationToken token)
    {
        try
        {
            _eventBus.Broadcast("AiAutofocusLiveUpdate", new { running = true, completed = false });

            // 1. 카메라 연동해서 사진 촬영 (모의 딜레이)
            await Task.Delay(2000, token);
            string mockImagePath = "mock.jpg";

            // 2. AI 딥러닝 추론 (거리 도출)
            float offsetDistance = _aiService.PredictOffset(mockImagePath);

            // 3. Multiplier (현재 프로필 또는 캘리브레이션에서 획득) 기반 포커서 스텝 계산
            float stepMultiplier = 4.2f; 
            int stepsToMove = (int)(offsetDistance * stepMultiplier);

            // 4. 모터 움직임 (단 1회)
            var info = _focuser.GetInfo();
            if (info != null && info.Connected)
            {
                int currentPosition = info.Position;
                int targetPosition = currentPosition + stepsToMove;
                await _focuser.MoveFocuser(targetPosition, token);
            }

            // 5. 완료 알림
            _eventBus.Broadcast("AiAutofocusLiveUpdate", new {
                running = false,
                completed = true,
                offsetPredicted = offsetDistance,
                stepsMoved = stepsToMove,
                finalPosition = info?.Position ?? 0
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[AiAutofocus] Error: {ex.Message}");
            _eventBus.Broadcast("AutofocusError", "AI Autofocus aborted");
        }
    }
}
