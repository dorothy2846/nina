using System.Text.Json;

namespace NINA.Headless.Services.Remote;

/// <summary>RelayKey: random secret registered with the relay on first connect
/// (trust-on-first-use); the relay rejects any later observatory connect for
/// this MachineId that doesn't present the same key.</summary>
public record RendezvousConfig(string RendezvousUrl, string MachineId, bool Enabled, string? RelayKey = null);

/// <summary>Persists the observatory's rendezvous URL + connection preferences.
/// The <see cref="MachineId"/> is derived from <see cref="ObservatoryIdentity"/>'s
/// public key and not independently storable — it's mirrored here for logging
/// and backward compatibility but is always recomputed from the identity on load,
/// so a factory reset of the identity naturally invalidates this file's copy.</summary>
public class RendezvousConfigStore
{
    private readonly string _path;
    private readonly ILogger<RendezvousConfigStore> _log;
    private readonly ObservatoryIdentity _identity;
    private RendezvousConfig? _cached;

    public RendezvousConfigStore(ILogger<RendezvousConfigStore> log, ObservatoryIdentity identity)
    {
        _log = log;
        _identity = identity;
        var dir = PlatformPaths.ConfigDir;
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "rendezvous.json");
    }

    public RendezvousConfig Load()
    {
        if (_cached != null) return _cached;
        var machineId = _identity.MachineId;
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                var cfg = JsonSerializer.Deserialize<RendezvousConfig>(json);
                if (cfg != null)
                {
                    // Always pin the file's MachineId back to the identity's —
                    // identity is the source of truth; legacy "obs-xxx" strings
                    // get silently migrated to "astellar-xxx".
                    if (cfg.MachineId != machineId)
                    {
                        cfg = cfg with { MachineId = machineId };
                        Save(cfg);
                    }
                    if (string.IsNullOrEmpty(cfg.RelayKey))
                    {
                        cfg = cfg with { RelayKey = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)) };
                        Save(cfg);
                    }
                    _cached = cfg;
                    return cfg;
                }
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to read rendezvous config; generating defaults"); }

        var fresh = new RendezvousConfig("wss://astellar-rdv.koreasouth.cloudapp.azure.com", machineId, Enabled: true,
            RelayKey: Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)));
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
}
