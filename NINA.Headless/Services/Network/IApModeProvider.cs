namespace NINA.Headless.Services.Network;

/// <summary>Platform abstraction for WiFi AP mode — the "field hotspot" pattern where the
/// server computer becomes its own WiFi network for phones to connect to on site.
/// Linux (production) uses NetworkManager/nmcli; macOS (dev) returns an unsupported stub
/// because Apple removed programmatic control in macOS 11+.</summary>
public interface IApModeProvider
{
    /// <summary>Can this machine host an AP at all? Checks adapter support, required
    /// binaries (nmcli), permissions. Called once at startup and whenever the user
    /// visits the AP settings screen.</summary>
    Task<ApCapabilities> GetCapabilitiesAsync(CancellationToken ct);

    Task<ApModeStatus> GetStatusAsync(CancellationToken ct);

    /// <summary>Switch adapter into AP mode with the given SSID/password. Returns true on
    /// success. Note: this call may drop the server's current LAN connectivity if the same
    /// adapter was providing it — by the time the response returns to the caller the
    /// connection may already be gone.</summary>
    Task<bool> EnableAsync(ApModeConfig config, CancellationToken ct);

    /// <summary>Stop AP mode and return adapter to client mode. NetworkManager will
    /// attempt to reconnect to the saved home WiFi automatically.</summary>
    Task<bool> DisableAsync(CancellationToken ct);
}

public record ApCapabilities(
    bool Supported,
    /// Human-readable reason when Supported=false (e.g. "nmcli not installed",
    /// "WiFi adapter doesn't support AP mode", "macOS — enable via System Settings").
    string? UnsupportedReason,
    /// Interface name the AP will run on (e.g. "wlan0"). Null if no usable adapter.
    string? InterfaceName);

public record ApModeStatus(
    bool Active,
    string? Ssid,
    string? InterfaceName,
    /// When Active=false and there's a reason worth surfacing (last attempt failed,
    /// adapter disappeared, etc.) this carries the short error.
    string? LastError);

/// <summary>Persisted AP configuration. Lives in <c>PlatformPaths.ConfigDir/ap.json</c>.
/// SSID and password are user-set; AutoFallback controls whether WifiFallbackOrchestrator
/// flips into AP mode after a failed-to-connect-to-home-WiFi grace period.
///
/// Mode is the operator-declared intent:
///   • Field — on-site use, fallback to AP when home WiFi isn't reachable. Losing the
///     current connection is expected and recoverable (user is physically present).
///   • Unattended — left running at the observatory for remote access. AP mode is a
///     kill-switch here because it takes the box off the internet; we never auto-fall
///     back, and instead rely on the OS network manager + rendezvous client retry
///     loops to reconnect indefinitely when the router blips.
/// </summary>
public enum OperationalMode
{
    Field,
    Unattended
}

public record ApModeConfig(
    string Ssid,
    string Password,
    bool AutoFallback = true,
    int FallbackGraceSeconds = 45,
    OperationalMode Mode = OperationalMode.Field)
{
    /// <summary>Effective auto-fallback: Unattended mode forces it off regardless of
    /// the persisted <see cref="AutoFallback"/>, so the flag can't accidentally brick
    /// a remote session by flipping into AP when the router reboots.</summary>
    public bool EffectiveAutoFallback => Mode == OperationalMode.Unattended ? false : AutoFallback;
}
