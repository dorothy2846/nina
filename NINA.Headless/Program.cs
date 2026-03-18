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
builder.Services.AddSingleton<SequencerService>();
builder.Services.AddHostedService<SimulatorService>();
builder.Services.AddHostedService<EquipmentStatusBroadcaster>();

// Configure Kestrel to listen on port 1888 (Touch'N'Stars compatible)
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(1888);
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseRouting();
app.UseAuthorization();
app.MapControllers();
app.MapHub<NinaHub>("/ws/nina");

// Health check endpoint
app.MapGet("/api/v1/health", () => new { status = "ok", version = "1.0.0", platform = "linux-x64" });

// Captive portal responses — trick iOS/Android into thinking WiFi has internet
// This prevents devices from disconnecting from the no-internet WiFi AP
app.MapGet("/hotspot-detect.html", () => Results.Content("Success", "text/plain"));           // iOS: captive.apple.com
app.MapGet("/library/test/success.html", () => Results.Content("Success", "text/plain"));     // iOS alternate
app.MapGet("/generate_204", () => Results.StatusCode(204));                                    // Android: connectivitycheck.gstatic.com
app.MapGet("/gen_204", () => Results.StatusCode(204));                                         // Android alternate

// Eagerly resolve NinaStateService so it registers as mediator consumer on startup
app.Services.GetRequiredService<NinaStateService>();

app.Run();
