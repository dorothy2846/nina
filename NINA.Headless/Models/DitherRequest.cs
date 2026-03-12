namespace NINA.Headless.Models;

public record DitherRequest
{
    public double Pixels { get; init; } = 5.0;
}
