namespace NINA.Headless.Models;

public record ConnectRequest
{
    public string? DeviceId { get; init; }
}
