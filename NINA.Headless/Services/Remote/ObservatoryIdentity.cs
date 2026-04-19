using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace NINA.Headless.Services.Remote;

/// <summary>
/// Permanent cryptographic identity of this observatory. Generated once on first
/// boot and persisted to <c>identity/ed25519.{key,pub}</c>; never rotated except
/// via an explicit factory reset. The short human-facing <see cref="MachineId"/>
/// is a derived label — iOS pairs against the public key, not the label, so the
/// identity survives network changes, hostname changes, even moving the USB stick
/// to a different host.
/// </summary>
public class ObservatoryIdentity
{
    private readonly string _dir;
    private readonly ILogger<ObservatoryIdentity> _log;
    private Ed25519PrivateKeyParameters _priv = null!;
    private Ed25519PublicKeyParameters _pub = null!;

    public string MachineId { get; private set; } = "";

    /// <summary>Raw 32-byte Ed25519 public key — primary identity anchor. iOS
    /// verifies observatory signatures against this.</summary>
    public byte[] PublicKey => _pub.GetEncoded();

    public ObservatoryIdentity(ILogger<ObservatoryIdentity> log)
    {
        _log = log;
        var baseDir = PlatformPaths.IsWindows
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        _dir = Path.Combine(baseDir, "nina-headless", "identity");
        Directory.CreateDirectory(_dir);
        LoadOrGenerate();
    }

    private void LoadOrGenerate()
    {
        var keyPath = Path.Combine(_dir, "ed25519.key");
        var pubPath = Path.Combine(_dir, "ed25519.pub");

        if (File.Exists(keyPath) && File.Exists(pubPath))
        {
            try
            {
                var privBytes = Convert.FromBase64String(File.ReadAllText(keyPath).Trim());
                var pubBytes = Convert.FromBase64String(File.ReadAllText(pubPath).Trim());
                _priv = new Ed25519PrivateKeyParameters(privBytes, 0);
                _pub = new Ed25519PublicKeyParameters(pubBytes, 0);
                MachineId = DeriveMachineId(pubBytes);
                _log.LogInformation("Loaded observatory identity: {MachineId}", MachineId);
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Corrupt identity files — regenerating");
            }
        }

        var gen = new Ed25519KeyPairGenerator();
        gen.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        var pair = gen.GenerateKeyPair();
        _priv = (Ed25519PrivateKeyParameters)pair.Private;
        _pub = (Ed25519PublicKeyParameters)pair.Public;
        MachineId = DeriveMachineId(_pub.GetEncoded());

        File.WriteAllText(keyPath, Convert.ToBase64String(_priv.GetEncoded()));
        File.WriteAllText(pubPath, Convert.ToBase64String(_pub.GetEncoded()));
        TryChmod600(keyPath);
        _log.LogInformation("Generated new observatory identity: {MachineId}", MachineId);
    }

    /// <summary>Sign a payload with the observatory's private key. Used when we
    /// need to prove authenticity to iOS — e.g., server pushing identity assertions
    /// over WebRTC so a compromised rendezvous can't impersonate us.</summary>
    public byte[] Sign(byte[] message)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, _priv);
        signer.BlockUpdate(message, 0, message.Length);
        return signer.GenerateSignature();
    }

    /// <summary>Factory reset — delete identity + paired devices and re-seed.</summary>
    public void Reset()
    {
        try { foreach (var f in Directory.GetFiles(_dir)) File.Delete(f); } catch { }
        LoadOrGenerate();
    }

    /// <summary>machineId = "astellar-" + base32(blake2b-256(pubkey)[:5]).
    /// 5 bytes → 8 base32 chars → collision-free for any realistic fleet, and
    /// short enough to read out loud ("astellar-k2h3pqrs").</summary>
    private static string DeriveMachineId(byte[] pubkey)
    {
        var digest = new Blake2bDigest(256);
        digest.BlockUpdate(pubkey, 0, pubkey.Length);
        var hash = new byte[32];
        digest.DoFinal(hash, 0);
        return "astellar-" + Base32Encode(hash[..5]);
    }

    /// <summary>Crockford-style lowercase base32 — skip 0/1/8 lookalikes so users
    /// reading machineId off a label don't mis-type.</summary>
    private static string Base32Encode(ReadOnlySpan<byte> bytes)
    {
        const string alphabet = "abcdefghjkmnpqrstvwxyz23456789";
        var sb = new System.Text.StringBuilder();
        int buf = 0, bits = 0;
        foreach (var b in bytes)
        {
            buf = (buf << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                sb.Append(alphabet[(buf >> bits) & 0x1F]);
            }
        }
        if (bits > 0) sb.Append(alphabet[(buf << (5 - bits)) & 0x1F]);
        return sb.ToString();
    }

    private static void TryChmod600(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
    }
}
