namespace NINA.Headless.Models;

public record CoolingRequest
{
    public bool Enabled { get; init; }
    public double Temperature { get; init; } = -10.0;
}
