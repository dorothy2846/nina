namespace HiPS.TileServer.Services;

public class TileCache
{
    private readonly string _cacheDir;
    private readonly long _maxCacheSizeBytes;
    private readonly ILogger<TileCache> _logger;

    public TileCache(IConfiguration configuration, ILogger<TileCache> logger)
    {
        _cacheDir = configuration.GetValue<string>("HiPS:CacheDir") ?? "/var/cache/hips-tileserver";
        var maxCacheSizeGB = configuration.GetValue<int>("HiPS:MaxCacheSizeGB", 20);
        _maxCacheSizeBytes = (long)maxCacheSizeGB * 1024 * 1024 * 1024;
        _logger = logger;

        Directory.CreateDirectory(_cacheDir);
        _logger.LogInformation("TileCache initialized: {CacheDir}, max {MaxGB} GB", _cacheDir, maxCacheSizeGB);
    }

    private string GetFilePath(string survey, int order, int dir, int npix)
    {
        return Path.Combine(_cacheDir, survey, $"Norder{order}", $"Dir{dir}", $"Npix{npix}.jpg");
    }

    public Task<Stream?> TryGetAsync(string survey, int order, int dir, int npix)
    {
        var path = GetFilePath(survey, order, dir, npix);

        if (!File.Exists(path))
        {
            return Task.FromResult<Stream?>(null);
        }

        try
        {
            // Update last access time for LRU
            File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
            Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Task.FromResult<Stream?>(stream);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read cached tile: {Path}", path);
            return Task.FromResult<Stream?>(null);
        }
    }

    public async Task StoreAsync(string survey, int order, int dir, int npix, byte[] data)
    {
        var path = GetFilePath(survey, order, dir, npix);
        var directory = Path.GetDirectoryName(path)!;

        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(path, data);
            _logger.LogDebug("Cached tile: {Path} ({Size} bytes)", path, data.Length);

            // Check cache size and evict if needed
            _ = Task.Run(() => EvictIfNeeded());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cache tile: {Path}", path);
        }
    }

    public long GetCacheSizeBytes()
    {
        try
        {
            var dirInfo = new DirectoryInfo(_cacheDir);
            if (!dirInfo.Exists) return 0;
            return dirInfo.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch
        {
            return 0;
        }
    }

    private void EvictIfNeeded()
    {
        try
        {
            var currentSize = GetCacheSizeBytes();
            if (currentSize <= _maxCacheSizeBytes) return;

            _logger.LogInformation("Cache size {SizeMB} MB exceeds limit, evicting oldest files...",
                currentSize / (1024 * 1024));

            var dirInfo = new DirectoryInfo(_cacheDir);
            var files = dirInfo.EnumerateFiles("*", SearchOption.AllDirectories)
                .OrderBy(f => f.LastAccessTimeUtc)
                .ToList();

            foreach (var file in files)
            {
                if (currentSize <= _maxCacheSizeBytes * 9 / 10) // Evict to 90%
                    break;

                try
                {
                    var size = file.Length;
                    file.Delete();
                    currentSize -= size;
                    _logger.LogDebug("Evicted: {File}", file.FullName);
                }
                catch
                {
                    // Skip files that can't be deleted
                }
            }

            _logger.LogInformation("Cache eviction complete. New size: {SizeMB} MB",
                currentSize / (1024 * 1024));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cache eviction failed");
        }
    }
}
