using System.Text.Json;

namespace NINA.Headless.Services;

/// <summary>HTTP client for ASCOM Alpaca devices. All Alpaca actions route through
/// <c>http://{host}:{port}/api/v1/{type}/{number}/{action}</c> — this class owns the
/// transport (ClientID/TransactionID, error-envelope parsing) and exposes only the
/// subset of actions the server needs.</summary>
public class AlpacaClient
{
    // One HttpClient for all Alpaca traffic — connection pooling, socket reuse. Static
    // to survive any future scope changes to AlpacaClient itself.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly ILogger<AlpacaClient> _log;
    private long _transactionId;

    public AlpacaClient(ILogger<AlpacaClient> log) { _log = log; }

    public Task SetConnectedAsync(EquipmentDescriptor desc, bool connected, CancellationToken ct) =>
        PutAsync(desc, "connected", new Dictionary<string, string> { ["Connected"] = connected ? "True" : "False" }, ct);

    public async Task<bool> GetConnectedAsync(EquipmentDescriptor desc, CancellationToken ct)
    {
        var result = await GetAsync(desc, "connected", ct);
        return result.ValueKind == JsonValueKind.True;
    }

    public async Task<string> GetNameAsync(EquipmentDescriptor desc, CancellationToken ct)
    {
        var result = await GetAsync(desc, "name", ct);
        return result.ValueKind == JsonValueKind.String ? result.GetString() ?? "" : "";
    }

    private async Task<JsonElement> GetAsync(EquipmentDescriptor desc, string action, CancellationToken ct)
    {
        var url = BuildUrl(desc, action, out var txId);
        var resp = await Http.GetAsync(url, ct);
        return ParseValue(await resp.Content.ReadAsStringAsync(ct));
    }

    private async Task PutAsync(EquipmentDescriptor desc, string action, Dictionary<string, string> form, CancellationToken ct)
    {
        var url = BuildUrl(desc, action, out var txId);
        // Alpaca PUT requires ClientID/ClientTransactionID in the form body — servers
        // that validate strictly reject query-only submissions.
        form["ClientID"] = "1";
        form["ClientTransactionID"] = txId.ToString();
        var resp = await Http.PutAsync(url, new FormUrlEncodedContent(form), ct);
        ParseValue(await resp.Content.ReadAsStringAsync(ct));
    }

    private string BuildUrl(EquipmentDescriptor desc, string action, out long txId)
    {
        if (desc.Provider != EquipmentProvider.Alpaca)
            throw new InvalidOperationException($"Not an Alpaca descriptor: {desc.Id}");
        txId = Interlocked.Increment(ref _transactionId);
        var type = AlpacaDeviceTypes.UrlSegment(desc.Kind);
        // Device number parsed from the Id's trailing ":N" segment (discovery format:
        // "alpaca:…:N"). Multi-instance drivers are rare — fallback 0 covers 99% of rigs.
        var num = 0;
        var lastColon = (desc.Id ?? "").LastIndexOf(':');
        if (lastColon > 0 && int.TryParse(desc.Id!.AsSpan(lastColon + 1), out var n)) num = n;
        return $"http://{desc.Host}:{desc.Port}/api/v1/{type}/{num}/{action}?ClientID=1&ClientTransactionID={txId}";
    }

    private static JsonElement ParseValue(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.TryGetProperty("ErrorNumber", out var errNum) && errNum.GetInt32() != 0)
        {
            var msg = root.TryGetProperty("ErrorMessage", out var em) ? em.GetString() : "(no message)";
            throw new InvalidOperationException($"Alpaca error {errNum.GetInt32()}: {msg}");
        }
        return root.TryGetProperty("Value", out var v) ? v.Clone() : default;
    }
}
