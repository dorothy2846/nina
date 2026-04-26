using System.Text.Json;
using System.Text.Json.Serialization;

namespace NINA.Headless.Services.Remote;

/// <summary>
/// Persistent record of which Supabase account owns this observatory. Uses
/// auto-claim semantics: the first Supabase user UUID that successfully
/// authenticates against this server gets recorded as the owner. Subsequent
/// authenticated calls match against that UUID; mismatches are rejected.
///
/// Co-owners (a second account explicitly invited by the owner) live in the
/// same file. The set is small (typically 1–3 entries) so a lock + JSON
/// rewrite on each change is the right scale of complexity.
///
/// The file lives in the same config dir as paired_devices.json. Wiping it
/// resets the observatory to "unowned" — the next account to log in claims
/// it. <see cref="Reset"/> is the explicit hook for the factory-reset path.
/// </summary>
public class OwnerAccountStore
{
    private readonly string _path;
    private readonly ILogger<OwnerAccountStore> _log;
    private readonly object _lock = new();
    private OwnerFile _data;

    public OwnerAccountStore(ILogger<OwnerAccountStore> log)
    {
        _log = log;
        var dir = PlatformPaths.ConfigDir;
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "owner_accounts.json");
        _data = Load();
    }

    private OwnerFile Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                return JsonSerializer.Deserialize<OwnerFile>(json) ?? new();
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to read owner_accounts.json"); }
        return new OwnerFile();
    }

    private void Persist()
    {
        try { File.WriteAllText(_path, JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to save owner_accounts.json"); }
    }

    /// <summary>Owner UUID, or null if no one has claimed this observatory yet.</summary>
    public string? Owner
    {
        get { lock (_lock) return _data.Owner; }
    }

    /// <summary>True iff <paramref name="userUuid"/> is the owner OR is on
    /// the explicit co-owner list. Used by every authenticated endpoint
    /// after the JWT has been verified by SupabaseAuthService.</summary>
    public bool IsAuthorized(string userUuid)
    {
        if (string.IsNullOrEmpty(userUuid)) return false;
        lock (_lock)
        {
            if (_data.Owner == userUuid) return true;
            return _data.CoOwners.Contains(userUuid);
        }
    }

    /// <summary>Auto-claim: if owner is unset, set it to <paramref name="userUuid"/>
    /// and return true (accept the request). If owner is already set, return
    /// false here — caller falls back to the explicit IsAuthorized check.
    /// First-account-wins is the simplest way to handle initial setup; the
    /// security boundary is "the first Supabase account to authenticate is
    /// trusted to own this hardware", which matches "this is on my WiFi /
    /// I'm holding the device" intuitively.</summary>
    public bool TryClaim(string userUuid)
    {
        if (string.IsNullOrEmpty(userUuid)) return false;
        lock (_lock)
        {
            if (_data.Owner != null) return false;
            _data.Owner = userUuid;
            Persist();
        }
        _log.LogInformation("Observatory claimed by Supabase user {UserUuid}", userUuid);
        return true;
    }

    /// <summary>Owner-invoked share. Adds the supplied UUID to the co-owner
    /// list. No-op if already owner or co-owner. Returns the updated list.</summary>
    public IReadOnlyList<string> AddCoOwner(string userUuid)
    {
        lock (_lock)
        {
            if (_data.Owner == userUuid) return _data.CoOwners.ToList();
            if (!_data.CoOwners.Contains(userUuid))
            {
                _data.CoOwners.Add(userUuid);
                Persist();
                _log.LogInformation("Added co-owner {UserUuid}", userUuid);
            }
            return _data.CoOwners.ToList();
        }
    }

    public bool RemoveCoOwner(string userUuid)
    {
        lock (_lock)
        {
            var removed = _data.CoOwners.Remove(userUuid);
            if (removed) { Persist(); _log.LogInformation("Removed co-owner {UserUuid}", userUuid); }
            return removed;
        }
    }

    public IReadOnlyList<string> ListCoOwners()
    {
        lock (_lock) return _data.CoOwners.ToList();
    }

    /// <summary>Drop the owner + co-owner list. Used by factory-reset alongside
    /// the existing PairedDeviceStore.Clear so a handed-off observatory has no
    /// residual identity tied to the prior user.</summary>
    public void Reset()
    {
        lock (_lock) { _data = new OwnerFile(); Persist(); }
        _log.LogInformation("Owner account store reset");
    }

    private class OwnerFile
    {
        [JsonPropertyName("owner")]
        public string? Owner { get; set; }

        [JsonPropertyName("coOwners")]
        public List<string> CoOwners { get; set; } = new();
    }
}
