namespace NINA.Headless.Models;

public record MoveRequest
{
    public string Direction { get; init; } = "N";
    public double Rate { get; init; } = 1.0;
    public int Duration { get; init; } = 500;
}
