namespace NINA.Headless.Models;

public record MoveRequest
{
    public string Direction { get; init; } = "N";
    public int Rate { get; init; } = 1;
    public int Duration { get; init; } = 500;
}
