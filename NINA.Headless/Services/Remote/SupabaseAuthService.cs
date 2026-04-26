using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NINA.Headless.Services.Remote;

/// <summary>
/// Verifies Supabase access tokens by delegating to Supabase's
/// <c>/auth/v1/user</c> endpoint. We don't crack the JWT locally — that
/// would tie us to Supabase's signing scheme (HS256 vs RS256, key rotation,
/// JWKS hydration) and add a JWT library dependency for what is fundamentally
/// a hosted-auth service. Trade-off: needs internet at verify time. The
/// in-process cache holds successful results for 10 min so a bursty session
/// (every API call carries the same token) doesn't re-hit Supabase.
///
/// On a no-internet observatory the verify call returns null and the caller
/// falls back to the legacy LAN-trust bearer token. Once internet is back,
/// the next request gets verified normally.
/// </summary>
public class SupabaseAuthService
{
    private readonly HttpClient _http;
    private readonly ILogger<SupabaseAuthService> _log;
    private readonly string _projectUrl;
    private readonly string _anonKey;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, (string userUuid, DateTime expires)> _cache = new();
    private DateTime _lastSweep = DateTime.MinValue;

    public SupabaseAuthService(IHttpClientFactory httpFactory, ILogger<SupabaseAuthService> log)
    {
        _http = httpFactory.CreateClient("supabase");
        _log = log;
        _projectUrl = Environment.GetEnvironmentVariable("NINA_SUPABASE_URL")
                       ?? "https://szedjvtewrpwobwioqzw.supabase.co";
        _anonKey = Environment.GetEnvironmentVariable("NINA_SUPABASE_ANON_KEY")
                       ?? "sb_publishable_TGmrWHFH71jhOvIkJSxtLg__Q1gZdhQ";
        _http.Timeout = TimeSpan.FromSeconds(8);
    }

    /// <summary>Returns the Supabase user UUID (the JWT's `sub` claim) if the
    /// access token verifies, else null. Cached. Network failures and any
    /// non-2xx response collapse to null — the caller treats null as "auth
    /// not available right now" and decides whether the LAN-trust fallback
    /// applies.</summary>
    public async Task<string?> VerifyAsync(string accessToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) return null;

        if (_cache.TryGetValue(accessToken, out var hit) && DateTime.UtcNow < hit.expires)
            return hit.userUuid;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{_projectUrl}/auth/v1/user");
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
            req.Headers.TryAddWithoutValidation("apikey", _anonKey);

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                if (resp.StatusCode == HttpStatusCode.Unauthorized)
                    _log.LogDebug("Supabase rejected token (401)");
                else
                    _log.LogDebug("Supabase verify returned {Status}", resp.StatusCode);
                return null;
            }

            var doc = await resp.Content.ReadFromJsonAsync<SupabaseUserResponse>(cancellationToken: ct);
            if (doc?.Id == null) return null;

            _cache[accessToken] = (doc.Id, DateTime.UtcNow + CacheTtl);
            MaybeSweepExpired();
            return doc.Id;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Supabase verify failed (likely offline)");
            return null;
        }
    }

    /// <summary>Drop expired entries — at most once per minute so concurrent
    /// fills don't all try to sweep. Bounded by client-token cardinality
    /// (typically 1–5 phones) so the linear scan is cheap when it runs.</summary>
    private void MaybeSweepExpired()
    {
        if (_cache.Count <= 64) return;
        var now = DateTime.UtcNow;
        if (now - _lastSweep < TimeSpan.FromMinutes(1)) return;
        _lastSweep = now;
        foreach (var kv in _cache)
            if (kv.Value.expires < now) _cache.TryRemove(kv.Key, out _);
    }

    private class SupabaseUserResponse
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }
    }
}
