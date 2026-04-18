using System.Text.Json;

namespace NINA.Headless.Services.Network;

/// <summary>Persists AP SSID/password between runs. Plain JSON in the OS's standard
/// user-config path. Password is stored in cleartext — this file is already user-writable
/// only, and "secret" here means "keeps neighbors out of the AP", not cryptographic secrecy.</summary>
public class ApModeConfigStore
{
    private readonly string _path;
    private readonly ILogger<ApModeConfigStore> _log;

    public ApModeConfigStore(ILogger<ApModeConfigStore> log)
    {
        _log = log;
        var baseDir = PlatformPaths.IsWindows
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        var dir = Path.Combine(baseDir, "nina-headless");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "ap.json");
    }

    public ApModeConfig Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                var cfg = JsonSerializer.Deserialize<ApModeConfig>(json);
                if (cfg != null) return cfg;
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to read AP config; using defaults"); }
        return DefaultConfig();
    }

    public void Save(ApModeConfig cfg)
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to save AP config"); }
    }

    /// <summary>Default SSID "BeyondStellar-{hostname}" + a random 10-char alphanumeric
    /// password. Passphrase length matches PHD2-style field-use hotspots and is short
    /// enough to type into iPhone WiFi settings manually when the QR code flow isn't
    /// available. Excludes visually ambiguous chars (0/O/1/I/l).</summary>
    private static ApModeConfig DefaultConfig()
    {
        var host = Environment.MachineName.Replace(' ', '-').ToLowerInvariant();
        var ssid = $"BeyondStellar-{host}";
        return new ApModeConfig(ssid, GeneratePassword(), AutoFallback: true);
    }

    private static string GeneratePassword()
    {
        const string alphabet = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKMNPQRSTUVWXYZ23456789";
        var rng = new Random();
        return new string(Enumerable.Range(0, 10).Select(_ => alphabet[rng.Next(alphabet.Length)]).ToArray());
    }
}
