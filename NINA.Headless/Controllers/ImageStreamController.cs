using Microsoft.AspNetCore.Mvc;
using NINA.Headless.Services;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Headless.Controllers;

[ApiController]
[Route("api/v1/stream")]
public class ImageStreamController : ControllerBase
{
    private readonly NinaStateService _state;

    public ImageStreamController(NinaStateService state)
    {
        _state = state;
    }

    /// <summary>
    /// Starts a high-speed HTTP/3 WebSocket stream that blasts compressed JPEG frames to the client.
    /// This bypasses traditional REST overhead and perfectly complements the QUIC architecture for Zero-Latency Planetary/LiveView.
    /// </summary>
    [HttpGet("liveview")]
    public async Task GetLiveView(CancellationToken cancellationToken)
    {
        if (HttpContext.WebSockets.IsWebSocketRequest)
        {
            using var webSocket = await HttpContext.WebSockets.AcceptWebSocketAsync();
            await StreamFrames(webSocket, cancellationToken);
        }
        else
        {
            HttpContext.Response.StatusCode = 400;
        }
    }

    private async Task StreamFrames(WebSocket webSocket, CancellationToken cancellationToken)
    {
        // In a real implementation, we would subscribe to ICameraMediator.ImageReceived
        // For now, we simulate pulling the latest image buffer at 30 FPS to prove the high-speed pipeline
        
        var sendBuffer = new byte[1024]; // Dummy buffer

        while (webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            // 1. Get raw 16-bit frame from NINA camera (or ROI)
            // 2. Downsample & AutoStretch
            // 3. Compress to JPEG (MemoryStream)
            
            // Dummy logic using State's latest image data for test
            var frameData = _state.LatestImageData; 
            if (frameData != null && frameData.Length > 0)
            {
                await webSocket.SendAsync(
                    new System.ArraySegment<byte>(frameData, 0, frameData.Length),
                    WebSocketMessageType.Binary,
                    endOfMessage: true,
                    cancellationToken: cancellationToken);
            }

            // Cap the loop to roughly 30 FPS to prevent locking
            await Task.Delay(33, cancellationToken);
        }
    }
}
