namespace NINA.Headless.Models;

public record ConnectAllRequest
{
    public string? CameraId { get; init; }
    public string? TelescopeId { get; init; }
    public string? FocuserId { get; init; }
    public string? FilterWheelId { get; init; }
    public string? GuiderId { get; init; }
    public string? RotatorId { get; init; }
    public string? DomeId { get; init; }
}
