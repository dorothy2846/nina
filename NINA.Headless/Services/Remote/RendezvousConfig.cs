using System.Security.Cryptography;
using System.Text.Json;

namespace NINA.Headless.Services.Remote;

public record RendezvousConfig(string RendezvousUrl, string MachineId, bool Enabled);

/// <summary>Persists the server's rendezvous URL + stable machineId used for remote
/// pairing. MachineId is generated once on first boot and stays forever — iOS app
/// tracks observatories by this ID, so rotating it would break saved servers.</summary>
public class RendezvousConfigStore
{
    private readonly string _path;
    private readonly ILogger<RendezvousConfigStore> _log;
    private RendezvousConfig? _cached;

    public RendezvousConfigStore(ILogger<RendezvousConfigStore> log)
    {
        _log = log;
        var baseDir = PlatformPaths.IsWindows
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        var dir = Path.Combine(baseDir, "nina-headless");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "rendezvous.json");
    }

    public RendezvousConfig Load()
    {
        if (_cached != null) return _cached;
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                var cfg = JsonSerializer.Deserialize<RendezvousConfig>(json);
                if (cfg != null && !string.IsNullOrWhiteSpace(cfg.MachineId))
                {
                    _cached = cfg;
                    return cfg;
                }
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to read rendezvous config; generating defaults"); }

        var fresh = Default();
        Save(fresh);
        _cached = fresh;
        return fresh;
    }

    public void Save(RendezvousConfig cfg)
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
            _cached = cfg;
        }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to save rendezvous config"); }
    }

    /// <summary>Default points at the production rendezvous server on Azure with TLS.
    /// Domain resolves to the same VM as the plain-text :8080 path so either will
    /// reach the same coordinator; we just prefer TLS for App Store compliance and
    /// App Transport Security defaults.</summary>
    private static RendezvousConfig Default() =>
        new("wss://astellar.koreasouth.cloudapp.azure.com", GenerateMachineId(), Enabled: true);

    /// <summary>obs-{hex8} — 32 bits of randomness. Collision-unlikely across
    /// any realistic observatory fleet; readable in logs and QR codes.</summary>
    private static string GenerateMachineId()
    {
        var bytes = RandomNumberGenerator.GetBytes(4);
        return "obs-" + Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
