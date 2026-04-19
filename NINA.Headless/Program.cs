using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Headless.Hubs;
using NINA.Headless.Services;
using NINA.WPF.Base.Mediator;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddSignalR(options =>
{
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
    options.HandshakeTimeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "NINA Headless API", Version = "v1" });
});

builder.Services.AddSingleton<CameraMediator>();
builder.Services.AddSingleton<ICameraMediator>(sp => sp.GetRequiredService<CameraMediator>());
builder.Services.AddSingleton<TelescopeMediator>();
builder.Services.AddSingleton<ITelescopeMediator>(sp => sp.GetRequiredService<TelescopeMediator>());
builder.Services.AddSingleton<GuiderMediator>();
builder.Services.AddSingleton<IGuiderMediator>(sp => sp.GetRequiredService<GuiderMediator>());
builder.Services.AddSingleton<FocuserMediator>();
builder.Services.AddSingleton<IFocuserMediator>(sp => sp.GetRequiredService<FocuserMediator>());
builder.Services.AddSingleton<FilterWheelMediator>();
builder.Services.AddSingleton<IFilterWheelMediator>(sp => sp.GetRequiredService<FilterWheelMediator>());
builder.Services.AddSingleton<RotatorMediator>();
builder.Services.AddSingleton<IRotatorMediator>(sp => sp.GetRequiredService<RotatorMediator>());
builder.Services.AddSingleton<DomeMediator>();
builder.Services.AddSingleton<IDomeMediator>(sp => sp.GetRequiredService<DomeMediator>());
builder.Services.AddSingleton<NinaStateService>();
builder.Services.AddSingleton<OneShotAiService>();
builder.Services.AddSingleton<SequencerService>();
builder.Services.AddSingleton<EquipmentSelectionService>();
builder.Services.AddSingleton<CameraSelectionService>();
builder.Services.AddSingleton<AlpacaClient>();
builder.Services.AddSingleton<AutoCalibrationOrchestrator>();

// AP-mode provider selection — Linux (production) uses nmcli; macOS (dev only)
// returns an unsupported stub. WifiFallbackOrchestrator then auto-enters AP mode
// on boot when no home WiFi is reachable (ASIAIR UX).
if (NINA.Headless.Services.PlatformPaths.IsLinux)
    builder.Services.AddSingleton<NINA.Headless.Services.Network.IApModeProvider, NINA.Headless.Services.Network.LinuxNmcliApMode>();
else
    builder.Services.AddSingleton<NINA.Headless.Services.Network.IApModeProvider, NINA.Headless.Services.Network.MacOsUnsupportedApMode>();
builder.Services.AddSingleton<NINA.Headless.Services.Network.ApModeConfigStore>();
builder.Services.AddSingleton<NINA.Headless.Services.Network.ApModeChangeService>();
builder.Services.AddHostedService<NINA.Headless.Services.Network.WifiFallbackOrchestrator>();
// Hardware-free factory reset — 3× rapid power-cycle wipes AP config + paired devices.
// Must register before anything that could cause the service to not reach 60 s of
// uptime on boot (the "stable uptime" signal the counter checks for).
builder.Services.AddHostedService<NINA.Headless.Services.Network.BootCounterService>();

// Remote access via rendezvous + WebRTC DataChannel. Observatory registers itself with
// our Azure signaling server on boot; iOS app (controller) dials in by machineId.
builder.Services.AddSingleton<NINA.Headless.Services.Remote.ObservatoryIdentity>();
builder.Services.AddSingleton<NINA.Headless.Services.Remote.PairedDeviceStore>();
builder.Services.AddSingleton<NINA.Headless.Services.Remote.RendezvousConfigStore>();
builder.Services.AddSingleton<NINA.Headless.Services.Remote.RemoteEventBus>();
builder.Services.AddSingleton<NINA.Headless.Services.Remote.RendezvousClient>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<NINA.Headless.Services.Remote.RendezvousClient>());
builder.Services.AddSingleton<Phd2Service>();
builder.Services.AddSingleton<IndiServerManager>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<IndiServerManager>());
builder.Services.AddSingleton<IndiDiscoveryService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<IndiDiscoveryService>());
builder.Services.AddSingleton<CaptureStore>();
builder.Services.AddSingleton<H264Transcoder>();
builder.Services.AddSingleton<CameraStreamService>();
builder.Services.AddSingleton<WebRTCService>();
builder.Services.AddSingleton<FlatWizardService>();
builder.Services.AddSingleton<CalibrationBatchService>();
builder.Services.AddSingleton<CalibrationLibrary>();
builder.Services.AddSingleton<LiveStackService>();
builder.Services.AddHostedService<SimulatorService>();
builder.Services.AddHostedService<EquipmentStatusBroadcaster>();
builder.Services.AddHostedService<AlpacaDiscoveryService>();

// Configure Kestrel to listen on port 1888
builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.ListenAnyIP(1888, listenOptions =>
    {
        // iOS 시뮬레이터에서 로컬 통신 시 자체 서명 인증서(SSL) 거부 문제를 피하기 위해 일반 HTTP로 개방
        listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1AndHttp2;
    });
});

// mDNS broadcast so BeyondStellar iOS auto-discovers this server on LAN.
builder.Services.AddHostedService<NINA.Headless.Services.MdnsBroadcastService>();

var app = builder.Build();

// Route SIPSorcery's internal logs through our ILoggerFactory so ICE / DTLS failures are
// visible. Without this, peer-state transitions go to stderr directly and never reach any
// of our log sinks.
SIPSorcery.LogFactory.Set(app.Services.GetRequiredService<ILoggerFactory>());

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseWebSockets(); // For SignalR fallback
app.UseRouting();
app.UseAuthorization();
app.MapControllers();
app.MapHub<NinaHub>("/ws/nina");

// Camera live stream WebSocket. Binary frames (JPEG) from server → client; client-sent
// messages are ignored for now. Auto-starts the capture loop on first client, auto-stops
// on last disconnect. Control (exposure, max FPS) goes through the REST endpoints.
app.Map("/ws/camera/stream", async (HttpContext ctx, NINA.Headless.Services.CameraStreamService stream) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    await stream.AddJpegClientAsync(ws, ctx.RequestAborted);
});

// H.264 Annex B transport. Each binary frame is one NAL unit preceded by a 4-byte start code.
// Clients concatenate NALUs into a decoder input buffer; CMVideoFormatDescription is built from
// the SPS/PPS NALUs that the encoder injects ahead of every keyframe.
app.Map("/ws/camera/stream/h264", async (HttpContext ctx, NINA.Headless.Services.CameraStreamService stream) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    await stream.AddH264ClientAsync(ws, ctx.RequestAborted);
});

// WebRTC signaling. Single HTTP POST for the offer/answer exchange — simpler than a
// dedicated signaling WebSocket and good enough for 1:1 peer.
app.MapPost("/api/v1/rtc/offer", async (HttpContext ctx, NINA.Headless.Services.WebRTCService rtc) =>
{
    using var reader = new StreamReader(ctx.Request.Body);
    var offerSdp = await reader.ReadToEndAsync();
    if (string.IsNullOrWhiteSpace(offerSdp)) return Results.BadRequest(new { error = "empty SDP" });
    try
    {
        var (answer, peerId) = await rtc.CreatePeerForOfferAsync(offerSdp);
        return Results.Ok(new { peerId, sdp = answer });
    }
    catch (Exception ex)
    {
        return Results.Problem(detail: ex.Message, statusCode: 500);
    }
});

app.MapPost("/api/v1/rtc/close/{peerId}", async (string peerId, NINA.Headless.Services.WebRTCService rtc) =>
{
    await rtc.ClosePeerAsync(peerId);
    return Results.Ok(new { success = true });
});

// Quick browser-based test page — lets us verify the server's WebRTC pipeline before
// building the iOS client. Served at /rtc-test.html.
app.MapGet("/rtc-test.html", () => Results.Content(WebRTCTestPage.Html, "text/html"));

// Health check endpoint
app.MapGet("/api/v1/health", () => new {
    status = "ok",
    version = "1.0.0",
    platform = NINA.Headless.Services.PlatformPaths.PlatformName
});

// Captive portal responses — trick iOS/Android into thinking WiFi has internet
// This prevents devices from disconnecting from the no-internet WiFi AP
app.MapGet("/hotspot-detect.html", () => Results.Content("Success", "text/plain"));           // iOS: captive.apple.com
app.MapGet("/library/test/success.html", () => Results.Content("Success", "text/plain"));     // iOS alternate
app.MapGet("/generate_204", () => Results.StatusCode(204));                                    // Android: connectivitycheck.gstatic.com
app.MapGet("/gen_204", () => Results.StatusCode(204));                                         // Android alternate

// Eagerly resolve NinaStateService so it registers as mediator consumer on startup
app.Services.GetRequiredService<NinaStateService>();

app.Run();
