namespace NINA.Headless.Models;

public record SlewAltAzRequest
{
    public double Alt { get; init; }
    public double Az { get; init; }
}
