namespace NINA.Headless.Models;

public record MoveRequest
{
    public string Direction { get; init; } = "N";
    public double Rate { get; init; } = 1.0;
    public int Duration { get; init; } = 500;
    /// <summary>Optional. Addresses a specific TELESCOPE_SLEW_RATE switch
    /// element by name (e.g. "5x", "GUIDE"). Used when the iOS UI is bound
    /// to the driver's discrete switch elements.</summary>
    public string? RateName { get; init; }
    /// <summary>Optional. Numeric rate in sidereal multiples — written to
    /// VARIABLE_SLEW_RATE.RATE when the driver exposes it (clamped to the
    /// driver's min/max). On AM5 this is what actually drives motor speed,
    /// so the iOS curated chip strip (e.g. [1, 5, 50, 200, 800, MAX])
    /// sends this for an exact match instead of going through a 10-step
    /// switch that maps poorly. The switch is also synced best-effort.</summary>
    public double? SiderealRate { get; init; }
}
