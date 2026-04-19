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
        var dir = PlatformPaths.ConfigDir;
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

    /// <summary>Factory defaults: SSID <c>astellar-ap</c>, password <c>astellar</c>.
    /// Intentionally stable and documented so owners can recover after a factory reset
    /// (3× power-cycle) without a sticker or recovery sheet. The assumption is that the
    /// very first boot out of the box is trusted — owner immediately sets their own
    /// credentials via the API. The default password is 8 chars (WPA2-PSK minimum).</summary>
    public const string DefaultSsid = "astellar-ap";
    public const string DefaultPassword = "astellar";

    private static ApModeConfig DefaultConfig() =>
        new ApModeConfig(DefaultSsid, DefaultPassword, AutoFallback: true);

    /// <summary>Snapshot path for the apply-verify-rollback flow. Written atomically
    /// before a config change; restored if the new config fails to bring the AP up.</summary>
    private string PreviousPath => Path.Combine(Path.GetDirectoryName(_path)!, "ap.previous.json");

    public ApModeConfig? LoadPrevious()
    {
        try
        {
            if (File.Exists(PreviousPath))
                return JsonSerializer.Deserialize<ApModeConfig>(File.ReadAllText(PreviousPath));
        }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to read ap.previous.json"); }
        return null;
    }

    public void SavePrevious(ApModeConfig cfg)
    {
        try { File.WriteAllText(PreviousPath, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to save ap.previous.json"); }
    }

    public void ClearPrevious()
    {
        try { if (File.Exists(PreviousPath)) File.Delete(PreviousPath); } catch { }
    }

    /// <summary>Factory reset — delete ap.json (and the rollback snapshot). Next Load()
    /// returns <see cref="DefaultConfig"/>. Called by the 3× power-cycle recovery path.</summary>
    public void ResetToDefaults()
    {
        try { if (File.Exists(_path)) File.Delete(_path); } catch { }
        ClearPrevious();
        _log.LogWarning("AP config reset to factory defaults (ssid={Ssid})", DefaultSsid);
    }
}
