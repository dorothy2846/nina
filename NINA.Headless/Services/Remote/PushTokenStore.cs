using System.Text.Json;
using System.Text.Json.Serialization;

namespace NINA.Headless.Services.Remote;

public class PushToken
{
    /// <summary>Hex-encoded APNs device token from the iPhone.</summary>
    public string Token { get; set; } = "";
    /// <summary>True when the token came from a debug build (APNs sandbox
    /// environment); release/TestFlight tokens go to production APNs.</summary>
    public bool Sandbox { get; set; }
    /// <summary>Paired device that registered this token, for revocation.</summary>
    public string? PairedDeviceId { get; set; }
    public DateTime RegisteredAt { get; set; }
    public DateTime? LastSuccessAt { get; set; }
}

public class PushTokensFile
{
    [JsonPropertyName("tokens")]
    public List<PushToken> Tokens { get; set; } = new();
}

/// <summary>
/// APNs device tokens registered by paired iPhones, persisted in
/// <c>push_tokens.json</c> next to the other observatory state. Tokens rotate
/// on the phone side (app reinstall, OS restore), so registration upserts by
/// token value and APNs 410/BadDeviceToken responses prune dead entries.
/// </summary>
public class PushTokenStore
{
    private readonly string _path;
    private readonly ILogger<PushTokenStore> _log;
    private readonly object _lock = new();
    private PushTokensFile _data;

    public PushTokenStore(ILogger<PushTokenStore> log)
    {
        _log = log;
        var dir = PlatformPaths.ConfigDir;
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "push_tokens.json");
        _data = Load();
    }

    private PushTokensFile Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var parsed = JsonSerializer.Deserialize<PushTokensFile>(File.ReadAllText(_path));
                if (parsed != null) return parsed;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning("Push token store unreadable, starting empty: {Error}", ex.Message);
        }
        return new PushTokensFile();
    }

    private void Save()
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _path, overwrite: true);
    }

    public void Register(string token, bool sandbox, string? pairedDeviceId)
    {
        lock (_lock)
        {
            var existing = _data.Tokens.FirstOrDefault(t => t.Token == token);
            if (existing != null)
            {
                existing.Sandbox = sandbox;
                existing.PairedDeviceId = pairedDeviceId ?? existing.PairedDeviceId;
            }
            else
            {
                _data.Tokens.Add(new PushToken
                {
                    Token = token,
                    Sandbox = sandbox,
                    PairedDeviceId = pairedDeviceId,
                    RegisteredAt = DateTime.UtcNow,
                });
                _log.LogInformation("Push: registered token …{Suffix} (sandbox={Sandbox})",
                    token.Length > 8 ? token[^8..] : token, sandbox);
            }
            Save();
        }
    }

    public bool Unregister(string token)
    {
        lock (_lock)
        {
            var removed = _data.Tokens.RemoveAll(t => t.Token == token) > 0;
            if (removed) Save();
            return removed;
        }
    }

    public void MarkSuccess(string token)
    {
        lock (_lock)
        {
            var entry = _data.Tokens.FirstOrDefault(t => t.Token == token);
            if (entry != null)
            {
                entry.LastSuccessAt = DateTime.UtcNow;
                Save();
            }
        }
    }

    public IReadOnlyList<PushToken> List()
    {
        lock (_lock) { return _data.Tokens.ToList(); }
    }
}
