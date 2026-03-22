using Microsoft.AspNetCore.Mvc;
using NINA.Headless.Models;
using NINA.Headless.Services;

namespace NINA.Headless.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public class ProfileSyncController : ControllerBase
{
    private static readonly object SyncRoot = new();
    
    // Globally holds the currently active cloud-synced profile
    public static EquipmentProfileSyncRequest? ActiveCloudProfile { get; private set; }

    private readonly NinaStateService _state;

    public ProfileSyncController(NinaStateService state)
    {
        _state = state;
    }

    [HttpPost("sync")]
    public IActionResult SyncProfile([FromBody] EquipmentProfileSyncRequest request)
    {
        if (request == null)
        {
            return BadRequest(new { success = false, message = "Invalid profile data" });
        }

        lock (SyncRoot)
        {
            ActiveCloudProfile = request;
            
            // In a full implementation, you might want to also push these values
            // down into the NINA.Profile.ProfileManager tree so native NINA tools
            // also pick them up.
            
            // Log for headless terminal visibility
            System.Console.WriteLine($"[ProfileSync] Received new profile: {request.Name}");
            System.Console.WriteLine($"   Telescope FL: {request.TelescopeFocalLength}mm");
            System.Console.WriteLine($"   Focuser Step: {request.FocuserStepSize}");
            System.Console.WriteLine($"   Site Lat: {request.SiteLatitude}, Lon: {request.SiteLongitude}");
        }

        return Ok(new 
        { 
            success = true, 
            message = $"Profile '{request.Name}' successfully synced to NINA.Headless.",
            appliedProfile = request 
        });
    }

    [HttpGet("current")]
    public IActionResult GetCurrentProfile()
    {
        lock (SyncRoot)
        {
            if (ActiveCloudProfile == null)
            {
                return NotFound(new { success = false, message = "No profile currently synced." });
            }
            return Ok(ActiveCloudProfile);
        }
    }
}
