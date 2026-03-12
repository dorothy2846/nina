namespace NINA.Headless.Models;

public record CaptureRequest
{
    public double ExposureTime { get; init; } = 1.0;
    public int Gain { get; init; } = 100;
    public int Offset { get; init; } = 10;
    public int Binning { get; init; } = 1;
    public string? Filter { get; init; }
}
