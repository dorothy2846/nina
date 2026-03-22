using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Headless.Hubs;

namespace NINA.Headless.Services
{
    public class SmartGuiderAiService : IDisposable
    {
        private readonly ITelescopeMediator _telescope;
        private readonly IHubContext<NinaHub> _hub;
        private InferenceSession? _session;
        private CancellationTokenSource? _guideCts;

        public bool IsRunning => _guideCts != null && !_guideCts.IsCancellationRequested;

        public SmartGuiderAiService(ITelescopeMediator telescope, IHubContext<NinaHub> hub)
        {
            _telescope = telescope;
            _hub = hub;
            try
            {
                string modelPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MLModels", "smart_guider_lstm.onnx");
                if (System.IO.File.Exists(modelPath))
                {
                    _session = new InferenceSession(modelPath);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SmartGuiderAiService] Failed to load ONNX model: {ex.Message}");
            }
        }

        public void StartAiGuiding()
        {
            if (IsRunning) return;
            
            _guideCts = new CancellationTokenSource();
            var token = _guideCts.Token;
            _ = Task.Run(() => GuidingLoopAsync(token), token);
        }

        public void StopAiGuiding()
        {
            if (_guideCts != null)
            {
                _guideCts.Cancel();
                _guideCts.Dispose();
                _guideCts = null;
            }
        }

        private async Task GuidingLoopAsync(CancellationToken token)
        {
            try
            {
                Random rng = new Random();
                
                await _hub.Clients.All.SendAsync("GuiderLiveUpdate", new { state = "AI_Starting" }, token);

                while (!token.IsCancellationRequested)
                {
                    // 1. Simulate image capture & centroid extraction
                    await Task.Delay(1000, token); // 1-second guide exposure

                    // 2. Mock AI LSTM Inference (predicting drift pattern)
                    double raErrorArcsec = (rng.NextDouble() - 0.5) * 1.5; // +/- 0.75"
                    double decErrorArcsec = (rng.NextDouble() - 0.5) * 1.2; // +/- 0.60"

                    double totalRms = Math.Sqrt((raErrorArcsec * raErrorArcsec + decErrorArcsec * decErrorArcsec) / 2.0);

                    // 3. Mock ITelescopeMediator PulseGuide (AI commanding mount)
                    // In real implementation: _telescope.PulseGuide(direction, duration)

                    // 4. Broadcast live telemetry data to iOS App via SignalR
                    var payload = new
                    {
                        connected = true,
                        state = "AI_Guiding",
                        rmsRA = Math.Round(Math.Abs(raErrorArcsec), 2),
                        rmsDec = Math.Round(Math.Abs(decErrorArcsec), 2),
                        rmsTotal = Math.Round(totalRms, 2),
                        peakRA = Math.Round(Math.Abs(raErrorArcsec) * 1.2, 2),
                        peakDec = Math.Round(Math.Abs(decErrorArcsec) * 1.2, 2),
                        aiModeActive = true,
                        aiConfidence = rng.Next(85, 100),
                        timestamp = DateTime.UtcNow
                    };

                    await _hub.Clients.All.SendAsync("GuiderLiveUpdate", payload, token);
                }
                
                await _hub.Clients.All.SendAsync("GuiderLiveUpdate", new { state = "AI_Stopped" }, CancellationToken.None);
            }
            catch (TaskCanceledException)
            {
                await _hub.Clients.All.SendAsync("GuiderLiveUpdate", new { state = "AI_Stopped" }, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SmartGuiderAiService] Exception in guiding loop: {ex.Message}");
                await _hub.Clients.All.SendAsync("GuiderError", "AI Guiding aborted unexpectedly", CancellationToken.None);
            }
        }

        public void Dispose()
        {
            StopAiGuiding();
            if (_session != null)
            {
                _session.Dispose();
            }
        }
    }
}
