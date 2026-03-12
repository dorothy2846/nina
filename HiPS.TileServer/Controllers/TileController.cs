using HiPS.TileServer.Services;
using Microsoft.AspNetCore.Mvc;

namespace HiPS.TileServer.Controllers;

[Route("hips")]
[ApiController]
public class TileController : ControllerBase
{
    private readonly TileCache _cache;
    private readonly TileProxyService _proxy;
    private readonly ILogger<TileController> _logger;

    public TileController(TileCache cache, TileProxyService proxy, ILogger<TileController> logger)
    {
        _cache = cache;
        _proxy = proxy;
        _logger = logger;
    }

    [HttpGet("{survey}/Norder{order}/Dir{dir}/Npix{npix}.jpg")]
    public async Task<IActionResult> GetTile(string survey, int order, int dir, int npix)
    {
        // Try cache first
        var cachedStream = await _cache.TryGetAsync(survey, order, dir, npix);
        if (cachedStream != null)
        {
            _logger.LogDebug("Cache HIT: {Survey}/Norder{Order}/Dir{Dir}/Npix{Npix}.jpg",
                survey, order, dir, npix);

            Response.Headers["X-Cache"] = "HIT";
            Response.Headers["Cache-Control"] = "public, max-age=31536000";
            return File(cachedStream, "image/jpeg");
        }

        // Cache miss — fetch from upstream
        _logger.LogInformation("Cache MISS: {Survey}/Norder{Order}/Dir{Dir}/Npix{Npix}.jpg",
            survey, order, dir, npix);

        var data = await _proxy.FetchAsync(survey, order, dir, npix);
        if (data == null)
        {
            return NotFound();
        }

        // Store in cache (fire and forget)
        _ = _cache.StoreAsync(survey, order, dir, npix, data);

        Response.Headers["X-Cache"] = "MISS";
        Response.Headers["Cache-Control"] = "public, max-age=31536000";
        return File(data, "image/jpeg");
    }
}
