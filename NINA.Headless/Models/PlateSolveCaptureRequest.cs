namespace NINA.Headless.Models;

public record PlateSolveCaptureRequest
{
    public double ExposureTime { get; init; } = 2.0;
    public int Gain { get; init; } = 200;
    public int Binning { get; init; } = 2;
    public bool Sync { get; init; } = true;
    public double SearchRadius { get; init; } = 30;
    public int Downsample { get; init; }
    public int TimeoutSeconds { get; init; } = 60;
}
