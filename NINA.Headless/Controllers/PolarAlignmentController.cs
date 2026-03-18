using Microsoft.AspNetCore.Mvc;

namespace NINA.Headless.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class PolarAlignmentController : ControllerBase
{
    private static readonly object SyncRoot = new();
    private static CancellationTokenSource? _runCts;

    private static bool _running;
    private static int _step;
    private static int _totalSteps = 3;
    private static bool _completed;
    private static double? _azimuthError;
    private static double? _altitudeError;
    private static string? _azimuthCorrection;
    private static string? _altitudeCorrection;
    private static double? _polarAlignError;

    [HttpPost("start")]
    public IActionResult Start()
    {
        lock (SyncRoot)
        {
            _runCts?.Cancel();
            _runCts = new CancellationTokenSource();

            _running = true;
            _step = 1;
            _totalSteps = 3;
            _completed = false;
            _azimuthError = null;
            _altitudeError = null;
            _azimuthCorrection = null;
            _altitudeCorrection = null;
            _polarAlignError = null;

            var token = _runCts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    for (var next = 2; next <= 3; next++)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(2), token);
                        lock (SyncRoot)
                        {
                            if (token.IsCancellationRequested) return;
                            _step = next;
                        }
                    }

                    lock (SyncRoot)
                    {
                        if (token.IsCancellationRequested) return;
                        _running = false;
                        _completed = true;
                        _azimuthError = 1.8;
                        _altitudeError = 1.1;
                        _polarAlignError = 2.1;
                        _azimuthCorrection = "Adjust azimuth west";
                        _altitudeCorrection = "Raise altitude slightly";
                    }
                }
                catch (TaskCanceledException)
                {
                    // ignored
                }
            }, token);
        }

        return Ok(new { success = true, message = "Polar alignment started" });
    }

    [HttpGet("status")]
    public IActionResult Status()
    {
        lock (SyncRoot)
        {
            return Ok(new
            {
                running = _running,
                step = _step,
                totalSteps = _totalSteps,
                completed = _completed,
                azimuthError = _azimuthError,
                altitudeError = _altitudeError,
                azimuthCorrection = _azimuthCorrection,
                altitudeCorrection = _altitudeCorrection,
                polarAlignError = _polarAlignError
            });
        }
    }

    [HttpPost("capture")]
    public IActionResult Capture()
    {
        lock (SyncRoot)
        {
            if (_running && _step < _totalSteps)
            {
                _step++;
            }

            if (_running && _step >= _totalSteps)
            {
                _running = false;
                _completed = true;
                _azimuthError = _azimuthError ?? 1.8;
                _altitudeError = _altitudeError ?? 1.1;
                _polarAlignError = _polarAlignError ?? 2.1;
                _azimuthCorrection ??= "Adjust azimuth west";
                _altitudeCorrection ??= "Raise altitude slightly";
            }
        }

        return Ok(new { success = true, message = "Polar alignment capture completed" });
    }

    [HttpPost("stop")]
    public IActionResult Stop()
    {
        lock (SyncRoot)
        {
            _runCts?.Cancel();
            _running = false;
        }

        return Ok(new { success = true, message = "Polar alignment stopped" });
    }
}
