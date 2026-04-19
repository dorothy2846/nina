using System.Text.Json;

namespace NINA.Headless.Services.Remote;

public record RendezvousConfig(string RendezvousUrl, string MachineId, bool Enabled);

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
                    _cached = cfg;
                    return cfg;
                }
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to read rendezvous config; generating defaults"); }

        var fresh = new RendezvousConfig("wss://astellar.koreasouth.cloudapp.azure.com", machineId, Enabled: true);
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
