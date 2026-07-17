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
builder.Services.AddSingleton<SafetyMonitorMediator>();
builder.Services.AddSingleton<ISafetyMonitorMediator>(sp => sp.GetRequiredService<SafetyMonitorMediator>());
builder.Services.AddSingleton<FlatDeviceMediator>();
builder.Services.AddSingleton<IFlatDeviceMediator>(sp => sp.GetRequiredService<FlatDeviceMediator>());
builder.Services.AddSingleton<WeatherDataMediator>();
builder.Services.AddSingleton<IWeatherDataMediator>(sp => sp.GetRequiredService<WeatherDataMediator>());
builder.Services.AddSingleton<SwitchMediator>();
builder.Services.AddSingleton<ISwitchMediator>(sp => sp.GetRequiredService<SwitchMediator>());
builder.Services.AddSingleton<NinaStateService>();
builder.Services.AddSingleton<OneShotAiService>();
builder.Services.AddSingleton<SequencerService>();
builder.Services.AddSingleton<EquipmentSelectionService>();
builder.Services.AddSingleton<CameraSelectionService>();
builder.Services.AddSingleton<AlpacaClient>();
builder.Services.AddSingleton<AutoCalibrationOrchestrator>();
builder.Services.AddSingleton<SystemUpdateService>();

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
builder.Services.AddSingleton<NINA.Headless.Services.Remote.OwnerAccountStore>();
builder.Services.AddHttpClient(); // SupabaseAuthService consumes the named "supabase" client
builder.Services.AddSingleton<NINA.Headless.Services.Remote.SupabaseAuthService>();
builder.Services.AddSingleton<NINA.Headless.Services.Remote.RendezvousConfigStore>();
builder.Services.AddSingleton<NINA.Headless.Services.Remote.RemoteEventBus>();
builder.Services.AddSingleton<NINA.Headless.Services.Remote.RendezvousClient>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<NINA.Headless.Services.Remote.RendezvousClient>());
builder.Services.AddSingleton<Phd2Service>();
builder.Services.AddSingleton<IndiServerManager>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<IndiServerManager>());
// USB plug-and-play: watches the bus, auto-starts matching INDI drivers.
builder.Services.AddSingleton<NINA.Headless.Services.Usb.UsbAutoDetectService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<NINA.Headless.Services.Usb.UsbAutoDetectService>());
builder.Services.AddSingleton<IndiDiscoveryService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<IndiDiscoveryService>());
// Bridges INDI device state into NINA mediators so every IDeviceConsumer
// (NinaStateService etc.) sees connect/disconnect/status updates without
// each having to know about the INDI layer directly.
builder.Services.AddSingleton<NINA.Headless.Services.IndiToMediatorBridge>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<NINA.Headless.Services.IndiToMediatorBridge>());
// Detects "alive but unresponsive" drivers (CCD_EXPOSURE wedged BUSY,
// mount/focuser stuck Busy) that the process-CPU watchdog can't see.
builder.Services.AddSingleton<NINA.Headless.Services.IndiDriverWatchdog>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<NINA.Headless.Services.IndiDriverWatchdog>());
// Reacts to SafetyMonitor unsafe transitions — broadcast critical event
// and abort active capture so the sequencer can re-evaluate.
builder.Services.AddHostedService<NINA.Headless.Services.SafetyResponseService>();
// Keeps PHD2 connection alive — auto-reconnects after process crash or
// network blip so a sequence doesn't lose guiding silently.
builder.Services.AddSingleton<NINA.Headless.Services.Phd2Watchdog>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<NINA.Headless.Services.Phd2Watchdog>());
builder.Services.AddSingleton<CaptureStore>();
builder.Services.AddSingleton<H264Transcoder>();
builder.Services.AddSingleton<CameraStreamService>();

// Auto-recovery for hung INDI driver processes. Runs as a hosted service so
// it's active across the whole server lifetime, not gated on a controller hit.
builder.Services.AddHostedService<NINA.Headless.Services.DriverWatchdogService>();
builder.Services.AddSingleton<WebRTCService>();
builder.Services.AddSingleton<FlatWizardService>();
builder.Services.AddSingleton<CalibrationBatchService>();
builder.Services.AddSingleton<CalibrationLibrary>();
// PolarAlignmentController injects this; without the registration every
// polar-alignment request died with a DI 500 before reaching the action.
builder.Services.AddSingleton(new NINA.Headless.Services.HeadlessAstapSolver(NINA.Headless.Services.PlatformPaths.AstapPath));
builder.Services.AddSingleton<LiveStackService>();
builder.Services.AddSingleton<NINA.Headless.Services.Phd2InstallerService>();
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

// Eagerly resolve CameraStreamService so it subscribes to IndiDiscoveryService.DeviceConnected
// before any camera can come online. Without this, the service is only constructed on the
// first /ws/camera/stream or /api/v1/camera/stream/configure request — by which time the
// auto-start-on-connect window has already passed and the user pays the cold-start cost
// they were trying to avoid.
app.Services.GetRequiredService<NINA.Headless.Services.CameraStreamService>();

// Pre-launch the H.264 / VP8 transcoder at startup. ffmpeg + libvpx +
// filter-graph init takes 1-2 s on first launch; doing it once at server
// start instead of on every first viewer connect makes the cold-start
// stream visible in well under a second instead of 3-4 s. ffmpeg sits
// idle waiting on stdin until the first BLOB arrives — CPU cost is ~0.
app.Lifetime.ApplicationStarted.Register(() =>
{
    try
    {
        var h264 = app.Services.GetRequiredService<NINA.Headless.Services.H264Transcoder>();
        h264.Start(targetFps: 10, crf: 26);
        app.Logger.LogInformation("H264Transcoder pre-launched at startup");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "H264Transcoder pre-launch failed; will start lazily on first stream");
    }

    // Pre-warm the SIPSorcery WebRTC stack: build + close a throw-away
    // RTCPeerConnection so DTLS / SCTP / RTP modules JIT-compile + load
    // their root certificates now instead of during the first viewer's
    // /rtc/offer round-trip. Real measurement showed the very first peer
    // creation costing ~600 ms more than subsequent ones.
    _ = Task.Run(() =>
    {
        try
        {
            var dummy = new SIPSorcery.Net.RTCPeerConnection(
                new SIPSorcery.Net.RTCConfiguration { iceServers = new List<SIPSorcery.Net.RTCIceServer>() });
            dummy.Close("pre-warm");
            app.Logger.LogInformation("WebRTC factory pre-warmed");
        }
        catch (Exception ex)
        {
            app.Logger.LogDebug(ex, "WebRTC pre-warm failed (non-fatal)");
        }
    });
});

app.Run();
