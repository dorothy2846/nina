namespace NINA.Headless.Models;

public record SlewRequest
{
    public double RA { get; init; }
    public double Dec { get; init; }
}
