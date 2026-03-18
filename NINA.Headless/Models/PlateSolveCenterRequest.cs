namespace NINA.Headless.Models;

public record PlateSolveCenterRequest
{
    public double TargetRa { get; init; }
    public double TargetDec { get; init; }
    public double ExposureTime { get; init; } = 2.0;
    public int Gain { get; init; } = 200;
    public int Binning { get; init; } = 2;
    public int MaxAttempts { get; init; } = 5;
    public double ThresholdDegrees { get; init; } = 0.01;
}
