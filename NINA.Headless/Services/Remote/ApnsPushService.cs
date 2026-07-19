using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NINA.Headless.Services.Remote;

/// <summary>
/// Direct APNs HTTP/2 client — no third-party push relay, the observatory
/// talks to Apple itself with an ES256 provider token. Config lives in
/// <c>apns.json</c> ({keyPath,keyId,teamId,bundleId}); without it the service
/// stays disabled and says so once. Provider JWTs are cached ~50 minutes
/// (Apple requires 20–60). Dead tokens (410, BadDeviceToken) are pruned from
/// the store so one reinstalled phone doesn't produce a nightly error streak.
/// </summary>
public class ApnsPushService
{
    private record ApnsConfig(string KeyPath, string KeyId, string TeamId, string BundleId);

    public record SendOutcome(int Sent, int Failed, string? LastError);

    private readonly ILogger<ApnsPushService> _log;
    private readonly PushTokenStore _tokens;
    private readonly HttpClient _http;
    private readonly object _jwtLock = new();

    private readonly ApnsConfig? _config;
    private ECDsa? _key;
    private string? _cachedJwt;
    private DateTime _jwtIssuedAt = DateTime.MinValue;
    private bool _disabledLogged;

    // Per-category throttle so a flapping driver can't buzz the phone every
    // few seconds — the first alert is the actionable one.
    private readonly Dictionary<string, DateTime> _lastSentByCategory = new();
    private static readonly TimeSpan CategoryThrottle = TimeSpan.FromSeconds(60);

    public bool IsConfigured => _config != null && _key != null;

    public ApnsPushService(ILogger<ApnsPushService> log, PushTokenStore tokens)
    {
        _log = log;
        _tokens = tokens;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        var path = Path.Combine(PlatformPaths.ConfigDir, "apns.json");
        try
        {
            if (File.Exists(path))
            {
                var parsed = JsonSerializer.Deserialize<ApnsConfig>(File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (parsed != null && File.Exists(parsed.KeyPath))
                {
                    _key = ECDsa.Create();
                    _key.ImportFromPem(File.ReadAllText(parsed.KeyPath));
                    _config = parsed;
                    _log.LogInformation("APNs: configured (keyId={KeyId}, bundle={Bundle})", parsed.KeyId, parsed.BundleId);
                }
                else if (parsed != null)
                {
                    _log.LogWarning("APNs: key file not found at {Path} — push disabled", parsed.KeyPath);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning("APNs: config unusable ({Error}) — push disabled", ex.Message);
            _key = null;
            _config = null;
        }
    }

    private string ProviderJwt()
    {
        lock (_jwtLock)
        {
            if (_cachedJwt != null && DateTime.UtcNow - _jwtIssuedAt < TimeSpan.FromMinutes(50))
                return _cachedJwt;

            var header = JsonSerializer.Serialize(new { alg = "ES256", kid = _config!.KeyId });
            var payload = JsonSerializer.Serialize(new
            {
                iss = _config.TeamId,
                iat = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            });
            var signingInput = $"{B64Url(Encoding.UTF8.GetBytes(header))}.{B64Url(Encoding.UTF8.GetBytes(payload))}";
            var signature = _key!.SignData(Encoding.UTF8.GetBytes(signingInput), HashAlgorithmName.SHA256);
            _cachedJwt = $"{signingInput}.{B64Url(signature)}";
            _jwtIssuedAt = DateTime.UtcNow;
            return _cachedJwt;
        }
    }

    private static string B64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Send an alert to every registered device. Category throttling
    /// (60s) is applied unless <paramref name="bypassThrottle"/> — completion
    /// and test pushes always go out; watchdog/safety repeats are damped.</summary>
    public async Task<SendOutcome> NotifyAllAsync(string title, string body, string category,
        bool bypassThrottle = false, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            if (!_disabledLogged)
            {
                _log.LogInformation("APNs: not configured — skipping push '{Title}'", title);
                _disabledLogged = true;
            }
            return new SendOutcome(0, 0, "not configured");
        }

        if (!bypassThrottle)
        {
            lock (_jwtLock)
            {
                if (_lastSentByCategory.TryGetValue(category, out var last) &&
                    DateTime.UtcNow - last < CategoryThrottle)
                {
                    return new SendOutcome(0, 0, "throttled");
                }
                _lastSentByCategory[category] = DateTime.UtcNow;
            }
        }

        var targets = _tokens.List();
        if (targets.Count == 0) return new SendOutcome(0, 0, "no registered devices");

        int sent = 0, failed = 0;
        string? lastError = null;
        foreach (var target in targets)
        {
            var (ok, error, dead) = await SendOneAsync(target.Token, target.Sandbox, title, body, category, ct);
            if (ok)
            {
                sent++;
                _tokens.MarkSuccess(target.Token);
            }
            else
            {
                failed++;
                lastError = error;
                if (dead)
                {
                    _tokens.Unregister(target.Token);
                    _log.LogInformation("APNs: pruned dead token …{Suffix} ({Error})",
                        target.Token.Length > 8 ? target.Token[^8..] : target.Token, error);
                }
            }
        }

        if (failed > 0)
            _log.LogWarning("APNs: '{Title}' sent={Sent} failed={Failed} lastError={Error}", title, sent, failed, lastError);
        else
            _log.LogInformation("APNs: '{Title}' delivered to {Sent} device(s)", title, sent);
        return new SendOutcome(sent, failed, lastError);
    }

    private async Task<(bool Ok, string? Error, bool DeadToken)> SendOneAsync(
        string token, bool sandbox, string title, string body, string category, CancellationToken ct)
    {
        var host = sandbox ? "api.sandbox.push.apple.com" : "api.push.apple.com";
        var payload = JsonSerializer.Serialize(new
        {
            aps = new
            {
                alert = new { title, body },
                sound = "default",
                category,
                // one thread per category so repeated watchdog alerts stack
                // instead of flooding the lock screen
                thread_id = category,
            },
        });

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"https://{host}/3/device/{token}")
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            req.Headers.TryAddWithoutValidation("authorization", $"bearer {ProviderJwt()}");
            req.Headers.TryAddWithoutValidation("apns-topic", _config!.BundleId);
            req.Headers.TryAddWithoutValidation("apns-push-type", "alert");
            req.Headers.TryAddWithoutValidation("apns-priority", "10");

            using var resp = await _http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode) return (true, null, false);

            var responseBody = await resp.Content.ReadAsStringAsync(ct);
            string reason = "";
            try
            {
                using var doc = JsonDocument.Parse(responseBody);
                reason = doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
            }
            catch { /* non-JSON error body */ }

            var dead = resp.StatusCode == HttpStatusCode.Gone ||
                       reason is "BadDeviceToken" or "Unregistered" or "DeviceTokenNotForTopic";
            return (false, $"{(int)resp.StatusCode} {reason}", dead);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, false);
        }
    }
}
