namespace NINA.Headless.Models;

public record FocuserMoveRequest
{
    public int Position { get; init; }
}
