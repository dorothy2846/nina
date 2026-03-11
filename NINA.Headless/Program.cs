using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "NINA Headless API", Version = "v1" });
});

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

// Health check endpoint
app.MapGet("/api/v1/health", () => new { status = "ok", version = "1.0.0", platform = "linux-arm64" });

app.Run();
