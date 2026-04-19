using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NINA.Headless.Services.Network;

namespace NINA.Headless.Services.Remote;

public class PairedDevice
{
    public string Id { get; set; } = "";
    public string Nickname { get; set; } = "";
    /// <summary>Base64 Ed25519 public key sent by the iPhone during pairing.</summary>
    public string PublicKey { get; set; } = "";
    /// <summary>sha256(token) hex — we never store the plaintext token; iPhone
    /// is the only copy, so a stolen paired_devices.json can't be used to
    /// impersonate the paired phone.</summary>
    public string TokenHash { get; set; } = "";
    public DateTime PairedAt { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public bool Revoked { get; set; }
    public string? IpAtPairing { get; set; }
}

public class PairedDevicesFile
{
    [JsonPropertyName("devices")]
    public List<PairedDevice> Devices { get; set; } = new();
}

/// <summary>
/// Persistent list of iPhones paired to this observatory, stored alongside the
/// identity in <c>paired_devices.json</c>. Each pair gets a random 32-byte
/// bearer token; we keep only the SHA-256 hash so the file is inert if copied.
/// Auto-approve logic gates the initial pair to either (a) observatory in AP
/// mode — physical-proximity trust — or (b) empty pair list (appliance-fresh).
/// </summary>
public class PairedDeviceStore
{
    private readonly string _path;
    private readonly ILogger<PairedDeviceStore> _log;
    private readonly IApModeProvider _ap;
    private readonly object _lock = new();
    private PairedDevicesFile _data;

    public PairedDeviceStore(ILogger<PairedDeviceStore> log, IApModeProvider ap)
    {
        _log = log;
        _ap = ap;
        var baseDir = PlatformPaths.IsWindows
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        var dir = Path.Combine(baseDir, "nina-headless");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "paired_devices.json");
        _data = Load();
    }

    private PairedDevicesFile Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                return JsonSerializer.Deserialize<PairedDevicesFile>(json) ?? new();
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to read paired_devices.json"); }
        return new PairedDevicesFile();
    }

    private void Persist()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to save paired_devices.json"); }
    }

    public IReadOnlyList<PairedDevice> ListActive()
    {
        lock (_lock) return _data.Devices.Where(d => !d.Revoked).ToList();
    }

    /// <summary>Pair a new device. Caller must have already passed
    /// <see cref="IsPairingAllowedAsync"/>. Returns the plaintext token — the
    /// only time it ever exists on this side. Caller (Controller) passes it
    /// back to iPhone and throws away the variable.</summary>
    public (string token, string deviceId) Pair(string deviceId, string nickname, string pubkeyBase64, string? ipAtPairing)
    {
        var tokenBytes = new byte[32];
        RandomNumberGenerator.Fill(tokenBytes);
        var token = Convert.ToBase64String(tokenBytes);
        var tokenHash = Sha256Hex(tokenBytes);

        var entry = new PairedDevice
        {
            Id = deviceId,
            Nickname = nickname,
            PublicKey = pubkeyBase64,
            TokenHash = tokenHash,
            PairedAt = DateTime.UtcNow,
            IpAtPairing = ipAtPairing
        };

        lock (_lock)
        {
            _data.Devices.RemoveAll(d => d.Id == deviceId);
            _data.Devices.Add(entry);
            Persist();
        }
        _log.LogInformation("Paired device {Id} ({Nickname})", deviceId, nickname);
        return (token, deviceId);
    }

    /// <summary>Verify a bearer token. Returns the matching device if valid,
    /// updates LastSeenAt. Null = unknown token or device revoked.</summary>
    public PairedDevice? Verify(string token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        string tokenHash;
        try { tokenHash = Sha256Hex(Convert.FromBase64String(token)); }
        catch { return null; }

        lock (_lock)
        {
            var device = _data.Devices.FirstOrDefault(d => !d.Revoked && d.TokenHash == tokenHash);
            if (device != null)
            {
                device.LastSeenAt = DateTime.UtcNow;
                Persist();
            }
            return device;
        }
    }

    public bool Revoke(string deviceId)
    {
        lock (_lock)
        {
            var device = _data.Devices.FirstOrDefault(d => d.Id == deviceId);
            if (device == null) return false;
            device.Revoked = true;
            Persist();
            _log.LogInformation("Revoked device {Id}", deviceId);
            return true;
        }
    }

    public void Clear()
    {
        lock (_lock) { _data.Devices.Clear(); Persist(); }
    }

    /// <summary>Gate for the unauthenticated /pair endpoint. Allow iff the
    /// observatory is currently hosting its AP (physical proximity trust) OR
    /// we've never paired anyone before (appliance-fresh). Anything else
    /// requires the remote admin flow (coming later).</summary>
    public async Task<PairingEligibility> GetEligibilityAsync(CancellationToken ct)
    {
        var status = await _ap.GetStatusAsync(ct);
        if (status.Active) return new(true, "ap-mode");

        lock (_lock)
        {
            if (_data.Devices.Count(d => !d.Revoked) == 0)
                return new(true, "no-paired-devices");
        }
        return new(false, "pairing-closed");
    }

    public record PairingEligibility(bool Allowed, string Reason);

    private static string Sha256Hex(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();
    }
}
