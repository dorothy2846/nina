namespace HiPS.TileServer.Services;

public class TileProxyService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TileProxyService> _logger;

    public TileProxyService(IHttpClientFactory httpClientFactory, ILogger<TileProxyService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<byte[]?> FetchAsync(string survey, int order, int dir, int npix)
    {
        // Convert survey name: 2MASS_Color → 2MASS/Color for CDS URL
        var surveyPath = survey.Replace("_", "/");
        var relativePath = $"{surveyPath}/Norder{order}/Dir{dir}/Npix{npix}.jpg";

        try
        {
            var client = _httpClientFactory.CreateClient("cds");
            _logger.LogInformation("Fetching upstream tile: {Path}", relativePath);

            var response = await client.GetAsync(relativePath);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Upstream returned {StatusCode} for {Path}",
                    (int)response.StatusCode, relativePath);
                return null;
            }

            var data = await response.Content.ReadAsByteArrayAsync();
            _logger.LogInformation("Fetched tile: {Path} ({Size} bytes)", relativePath, data.Length);
            return data;
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("Upstream request timed out: {Path}", relativePath);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch upstream tile: {Path}", relativePath);
            return null;
        }
    }
}
