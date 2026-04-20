using Microsoft.AspNetCore.Mvc;
using NINA.Headless.Services;

namespace NINA.Headless.Controllers;

[ApiController]
[Route("api/v1")]
public class AppConfigController : ControllerBase
{
    private static readonly object SyncRoot = new();

    private readonly CameraSelectionService _cameraSelection;
    private readonly IndiDiscoveryService _indi;

    public AppConfigController(CameraSelectionService cameraSelection, IndiDiscoveryService indi)
    {
        _cameraSelection = cameraSelection;
        _indi = indi;
    }

    private static CameraSettingsDto CameraSettings = new();
    private static FocuserSettingsDto FocuserSettings = new();
    private static TelescopeSettingsDto TelescopeSettings = new();
    private static GuiderSettingsDto GuiderSettings = new();
    private static DomeSettingsDto DomeSettings = new();
    private static RotatorSettingsDto RotatorSettings = new();
    private static MeridianFlipSettingsDto MeridianFlipSettings = new();
    private static PlateSolveSettingsDto PlateSolveSettings = new();
    private static FilterSettingsDto FilterWheelSettings = new()
    {
        Filters = []
    };

    /// <summary>Snapshot of the current filter settings for other controllers
    /// (FilterWheelController uses this to honor per-filter focus offsets). Returns a
    /// shallow copy so callers can't mutate the authoritative store.</summary>
    internal static FilterSettingsItemDto? GetFilterSetting(int position)
    {
        lock (SyncRoot)
        {
            return FilterWheelSettings.Filters.FirstOrDefault(f => f.Position == position);
        }
    }

    private static readonly List<ProfileInfoDto> Profiles =
    [
        new ProfileInfoDto { Id = "default", Name = "Default Profile", IsActive = true }
    ];

    private static ProfileActiveDto ActiveProfile = new()
    {
        Id = "default",
        Name = "Default Profile",
        IsActive = true,
        Latitude = 0,
        Longitude = 0,
        Elevation = 0
    };

    [HttpGet("camera/settings")]
    public IActionResult GetCameraSettings() => Ok(CameraSettings);

    [HttpPut("camera/settings")]
    public async Task<IActionResult> UpdateCameraSettings([FromBody] CameraSettingsDto request)
    {
        CameraSettingsDto previous;
        lock (SyncRoot)
        {
            previous = CameraSettings;
            CameraSettings = request;
        }

        // If an INDI camera is the current target, forward the changed fields to the driver.
        var selected = _cameraSelection.GetSelected();
        if (_cameraSelection.IsConnected && selected?.Provider == CameraProvider.Indi)
        {
            var ct = HttpContext.RequestAborted;
            try
            {
                if (request.Gain.HasValue && request.Gain != previous.Gain)
                    await _indi.SetGainAsync(selected.UniqueId, request.Gain.Value, ct);

                if (request.Offset.HasValue && request.Offset != previous.Offset)
                    await _indi.SetOffsetAsync(selected.UniqueId, request.Offset.Value, ct);

                if (request.Binning.HasValue && request.Binning != previous.Binning)
                    await _indi.SetBinningAsync(selected.UniqueId, request.Binning.Value, request.Binning.Value, ct);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, message = $"INDI apply failed: {ex.Message}" });
            }
        }

        return Ok(new { success = true, message = "Camera settings updated" });
    }

    [HttpGet("focuser/settings")]
    public IActionResult GetFocuserSettings() => Ok(FocuserSettings);

    [HttpPut("focuser/settings")]
    public IActionResult UpdateFocuserSettings([FromBody] FocuserSettingsDto request)
    {
        lock (SyncRoot) FocuserSettings = request;
        return Ok(new { success = true, message = "Focuser settings updated" });
    }

    [HttpGet("filterwheel/settings")]
    public IActionResult GetFilterWheelSettings() => Ok(FilterWheelSettings);

    [HttpPut("filterwheel/settings")]
    public IActionResult UpdateFilterWheelSettings([FromBody] FilterSettingsDto request)
    {
        lock (SyncRoot) FilterWheelSettings = request ?? new FilterSettingsDto { Filters = [] };
        return Ok(new { success = true, message = "Filter wheel settings updated" });
    }

    [HttpGet("telescope/settings")]
    public IActionResult GetTelescopeSettings() => Ok(TelescopeSettings);

    [HttpPut("telescope/settings")]
    public IActionResult UpdateTelescopeSettings([FromBody] TelescopeSettingsDto request)
    {
        lock (SyncRoot) TelescopeSettings = request;
        return Ok(new { success = true, message = "Telescope settings updated" });
    }

    [HttpGet("guider/settings")]
    public IActionResult GetGuiderSettings() => Ok(GuiderSettings);

    [HttpPut("guider/settings")]
    public IActionResult UpdateGuiderSettings([FromBody] GuiderSettingsDto request)
    {
        lock (SyncRoot) GuiderSettings = request;
        return Ok(new { success = true, message = "Guider settings updated" });
    }

    [HttpGet("dome/settings")]
    public IActionResult GetDomeSettings() => Ok(DomeSettings);

    [HttpPut("dome/settings")]
    public IActionResult UpdateDomeSettings([FromBody] DomeSettingsDto request)
    {
        lock (SyncRoot) DomeSettings = request;
        return Ok(new { success = true, message = "Dome settings updated" });
    }

    [HttpGet("rotator/settings")]
    public IActionResult GetRotatorSettings() => Ok(RotatorSettings);

    [HttpPut("rotator/settings")]
    public IActionResult UpdateRotatorSettings([FromBody] RotatorSettingsDto request)
    {
        lock (SyncRoot) RotatorSettings = request;
        return Ok(new { success = true, message = "Rotator settings updated" });
    }

    [HttpGet("settings/meridianflip")]
    public IActionResult GetMeridianFlipSettings() => Ok(MeridianFlipSettings);

    [HttpPut("settings/meridianflip")]
    public IActionResult UpdateMeridianFlipSettings([FromBody] MeridianFlipSettingsDto request)
    {
        lock (SyncRoot) MeridianFlipSettings = request;
        return Ok(new { success = true, message = "Meridian flip settings updated" });
    }

    [HttpGet("settings/platesolve")]
    public IActionResult GetPlateSolveSettings() => Ok(PlateSolveSettings);

    [HttpPut("settings/platesolve")]
    public IActionResult UpdatePlateSolveSettings([FromBody] PlateSolveSettingsDto request)
    {
        lock (SyncRoot) PlateSolveSettings = request;
        return Ok(new { success = true, message = "Plate solve settings updated" });
    }

    [HttpGet("profile/list")]
    public IActionResult GetProfiles()
    {
        lock (SyncRoot)
        {
            return Ok(Profiles.Select(p => new ProfileInfoDto { Id = p.Id, Name = p.Name, IsActive = p.Id == ActiveProfile.Id }).ToList());
        }
    }

    [HttpGet("profile/active")]
    public IActionResult GetActiveProfile()
    {
        lock (SyncRoot)
        {
            return Ok(ActiveProfile);
        }
    }

    [HttpPost("profile/switch")]
    public IActionResult SwitchProfile([FromBody] ProfileSwitchRequest request)
    {
        lock (SyncRoot)
        {
            var profile = Profiles.FirstOrDefault(p => p.Id == request.Id);
            if (profile == null)
            {
                return NotFound(new { success = false, message = "Profile not found" });
            }

            foreach (var p in Profiles)
            {
                p.IsActive = p.Id == request.Id;
            }

            ActiveProfile.Id = profile.Id;
            ActiveProfile.Name = profile.Name;
            ActiveProfile.IsActive = true;
        }

        return Ok(new { success = true, message = "Profile switched" });
    }

    [HttpPost("profile/create")]
    public IActionResult CreateProfile([FromBody] ProfileCreateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { success = false, message = "Profile name is required" });
        }

        lock (SyncRoot)
        {
            var id = Guid.NewGuid().ToString("N");
            Profiles.Add(new ProfileInfoDto
            {
                Id = id,
                Name = request.Name.Trim(),
                IsActive = false
            });
        }

        return Ok(new { success = true, message = "Profile created" });
    }

    [HttpDelete("profile/{id}")]
    public IActionResult DeleteProfile(string id)
    {
        lock (SyncRoot)
        {
            var profile = Profiles.FirstOrDefault(p => p.Id == id);
            if (profile == null)
            {
                return NotFound(new { success = false, message = "Profile not found" });
            }

            Profiles.Remove(profile);
            if (ActiveProfile.Id == id)
            {
                var fallback = Profiles.FirstOrDefault();
                ActiveProfile.Id = fallback?.Id;
                ActiveProfile.Name = fallback?.Name;
                ActiveProfile.IsActive = fallback != null;
            }
        }

        return Ok(new { success = true, message = "Profile deleted" });
    }

    [HttpPut("profile/astrometry")]
    public IActionResult UpdateAstrometry([FromBody] ProfileAstrometryRequest request)
    {
        lock (SyncRoot)
        {
            ActiveProfile.Latitude = request.Latitude;
            ActiveProfile.Longitude = request.Longitude;
            ActiveProfile.Elevation = request.Elevation;
        }

        return Ok(new { success = true, message = "Astrometry updated" });
    }
}

public class CameraSettingsDto
{
    public int? Gain { get; set; }
    public int? Offset { get; set; }
    public int? Binning { get; set; }
    public double? BitDepth { get; set; }
    public string? BayerPattern { get; set; }
    public double? CoolingDuration { get; set; }
    public double? WarmingDuration { get; set; }
    public double? Temperature { get; set; }
    public bool? DewHeaterOn { get; set; }
    public int? ReadoutMode { get; set; }
    public int? Timeout { get; set; }
    public double? PixelSize { get; set; }
}

public class FocuserSettingsDto
{
    public int? BacklashIn { get; set; }
    public int? BacklashOut { get; set; }
    public string? BacklashCompensationModel { get; set; }
    public int? AutoFocusStepSize { get; set; }
    public double? AutoFocusExposureTime { get; set; }
    public int? AutoFocusInitialOffsetSteps { get; set; }
    public string? AutoFocusMethod { get; set; }
    public string? AutoFocusCurveFitting { get; set; }
    public int? AutoFocusBinning { get; set; }
    public bool? AutoFocusDisableGuiding { get; set; }
    public int? FocuserSettleTime { get; set; }
    public int? AutoFocusTotalNumberOfAttempts { get; set; }
    public int? AutoFocusNumberOfFramesPerPoint { get; set; }
    public int? AutoFocusUseBrightestStars { get; set; }
    public double? AutoFocusInnerCropRatio { get; set; }
    public double? AutoFocusOuterCropRatio { get; set; }
    public double? RSquaredThreshold { get; set; }
    public bool? UseFilterWheelOffsets { get; set; }
    public int? AutoFocusTimeoutSeconds { get; set; }
}

public class TelescopeSettingsDto
{
    public double? FocalLength { get; set; }
    public double? FocalRatio { get; set; }
    public int? SettleTime { get; set; }
    public bool? NoSync { get; set; }
}

public class GuiderSettingsDto
{
    public double? DitherPixels { get; set; }
    public bool? DitherRAOnly { get; set; }
    public int? SettleTime { get; set; }
    public double? SettlePixels { get; set; }
    public int? SettleTimeout { get; set; }
    public bool? AutoRetryStartGuiding { get; set; }
    public string? Phd2ServerUrl { get; set; }
    public int? Phd2ServerPort { get; set; }
    public double? MaxY { get; set; }
}

public class DomeSettingsDto
{
    public double? AzimuthTolerance { get; set; }
    public bool? SyncDuringMountSlew { get; set; }
    public int? DomeSyncTimeoutSeconds { get; set; }
    public bool? CloseOnUnsafe { get; set; }
    public bool? ParkMountBeforeShutterMove { get; set; }
    public int? SettleTimeSeconds { get; set; }
    public bool? FindHomeBeforePark { get; set; }
}

public class RotatorSettingsDto
{
    public bool? Reverse { get; set; }
    public double? RotateDegrees { get; set; }
}

public class MeridianFlipSettingsDto
{
    public double? MinutesAfterMeridian { get; set; }
    public double? MaxMinutesAfterMeridian { get; set; }
    public bool? Recenter { get; set; }
    public int? SettleTime { get; set; }
    public bool? UseSideOfPier { get; set; }
    public double? PauseTimeBeforeMeridian { get; set; }
    public bool? AutoFocusAfterFlip { get; set; }
    public bool? RotateImageAfterFlip { get; set; }
}

public class PlateSolveSettingsDto
{
    public double? ExposureTime { get; set; }
    public int? Gain { get; set; }
    public int? Binning { get; set; }
    public double? SearchRadius { get; set; }
    public double? Threshold { get; set; }
    public double? RotationTolerance { get; set; }
    public int? NumberOfAttempts { get; set; }
    public double? ReattemptDelay { get; set; }
    public bool? Sync { get; set; }
    public bool? SlewToTarget { get; set; }
    public int? DownSampleFactor { get; set; }
    public int? MaxObjects { get; set; }
    public bool? BlindFailoverEnabled { get; set; }
}

public class FilterSettingsItemDto
{
    public string Name { get; set; } = string.Empty;
    public int Position { get; set; }
    public int FocusOffset { get; set; }
    public double AutoFocusExposureTime { get; set; }
}

public class FilterSettingsDto
{
    public List<FilterSettingsItemDto> Filters { get; set; } = [];
}

public class ProfileInfoDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; }
}

public class ProfileActiveDto
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public bool? IsActive { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public double? Elevation { get; set; }
}

public class ProfileSwitchRequest
{
    public string Id { get; set; } = string.Empty;
}

public class ProfileCreateRequest
{
    public string Name { get; set; } = string.Empty;
}

public class ProfileAstrometryRequest
{
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double Elevation { get; set; }
}
