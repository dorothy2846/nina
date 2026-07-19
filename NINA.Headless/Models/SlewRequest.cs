namespace NINA.Headless.Models;

public record SlewRequest
{
    public double RA { get; init; }
    public double Dec { get; init; }
    /// <summary>Coordinate epoch of RA/Dec: "j2000" (default — SkyMap catalog
    /// frame, precessed to date at the mount boundary) or "jnow" for
    /// coordinates already in the current epoch (planets, solved positions).</summary>
    public string? Epoch { get; init; }
}
