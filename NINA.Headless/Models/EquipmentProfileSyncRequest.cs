namespace NINA.Headless.Models;

public record EquipmentProfileSyncRequest
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = "Default Profile";
    
    // Telescope
    public double TelescopeFocalLength { get; init; }
    public double TelescopeAperture { get; init; }
    
    // Guide Scope
    public double GuideFocalLength { get; init; }
    
    // Focuser
    public int FocuserStepSize { get; init; }
    public int InitialOffsetSteps { get; init; }
    public int Backlash { get; init; }
    public int SettleTimeMs { get; init; }
    
    // Site
    public double SiteLatitude { get; init; }
    public double SiteLongitude { get; init; }
}
