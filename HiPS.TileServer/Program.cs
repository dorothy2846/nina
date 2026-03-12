using HiPS.TileServer.Services;

var builder = WebApplication.CreateBuilder(args);

// Kestrel: listen on port 8080
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(8080);
});

// Configuration
builder.Services.AddControllers();
builder.Services.AddOpenApi();

// HttpClientFactory — named client "cds"
builder.Services.AddHttpClient("cds", client =>
{
    var upstreamUrl = builder.Configuration.GetValue<string>("HiPS:UpstreamUrl")
                      ?? "https://alasky.cds.unistra.fr";
    client.BaseAddress = new Uri(upstreamUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("HiPS-TileServer/1.0.0");
});

// Services — singleton
builder.Services.AddSingleton<TileCache>();
builder.Services.AddSingleton<TileProxyService>();

var app = builder.Build();

// Health endpoint
app.MapGet("/api/v1/health", () => Results.Ok(new
{
    status = "ok",
    service = "hips-tileserver",
    version = "1.0.0"
}));

app.MapControllers();

app.Run();
