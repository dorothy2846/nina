using Microsoft.AspNetCore.Mvc;
using NINA.Headless.Services;

namespace NINA.Headless.Controllers;

/// <summary>
/// Driver diagnostics dump. When a user reports "my gear doesn't work quite right",
/// we usually need to see what INDI properties the driver actually publishes — we
/// can't enumerate every vendor's property naming variants in advance, so capability
/// probes miss occasionally. This endpoint returns a snapshot of every connected
/// device's property map so the user can attach it to a GitHub issue.
///
/// Shape is intentionally verbose (one row per property + elements) rather than
/// compact JSON, so a copy-pasted gist is human-readable.
/// </summary>
[ApiController]
[Route("api/v1/[controller]")]
public class DiagnosticsController : ControllerBase
{
    private readonly IndiDiscoveryService _indi;
    private readonly EquipmentSelectionService _equipment;

    public DiagnosticsController(IndiDiscoveryService indi, EquipmentSelectionService equipment)
    {
        _indi = indi;
        _equipment = equipment;
    }

    /// <summary>Full INDI property dump plus what our capability probes see. Not
    /// authenticated at the controller level — piggybacks on the same bearer-token
    /// gate the rest of the admin-ish endpoints use once we wire it in middleware.</summary>
    [HttpGet]
    public IActionResult Get()
    {
        var client = _indi.Client;
        if (client == null)
            return Ok(new { indiConnected = false, devices = Array.Empty<object>() });

        var devices = client.Devices
            .Select(d => BuildDeviceSnapshot(d.Name))
            .Where(d => d != null)
            .ToArray();

        return Ok(new
        {
            indiConnected = true,
            generatedAt = DateTime.UtcNow,
            devices
        });
    }

    private object? BuildDeviceSnapshot(string name)
    {
        var client = _indi.Client;
        var dev = client?.GetDevice(name);
        if (dev == null) return null;

        var properties = dev.Properties.Values
            .OrderBy(p => p.Name)
            .Select(p => new
            {
                name = p.Name,
                type = p.Type.ToString(),
                state = p.State.ToString(),
                perm = p.Perm.ToString(),
                elements = p.Elements.Values
                    .OrderBy(e => e.Name)
                    .Select(e => new { e.Name, e.Value })
                    .ToArray()
            })
            .ToArray();

        // Include the subset of our capability probes' verdict so readers can see
        // "we probed X and saw false" in one place alongside the raw property list.
        var probeVerdict = new
        {
            // Probe names here mirror the ones used in each controller's /info
            // capability block — this is deliberately duplicated rather than imported
            // from the controllers so the diagnostic format stays stable when we add
            // new probes.
            hasCcdDewControl = dev.Properties.ContainsKey("CCD_DEW_CONTROL"),
            hasAuxHeaterToggle = dev.Properties.ContainsKey("AUX_HEATER_TOGGLE"),
            hasAntiDew = dev.Properties.ContainsKey("ANTI_DEW"),
            hasCcdAbortExposure = dev.Properties.ContainsKey("CCD_ABORT_EXPOSURE"),
            hasFocusTempComp = dev.Properties.ContainsKey("FOCUS_TEMPERATURE_COMPENSATION"),
            hasAutoFocusComp = dev.Properties.ContainsKey("AUTO_FOCUS_COMP"),
            hasFocusBacklashSteps = dev.Properties.ContainsKey("FOCUS_BACKLASH_STEPS"),
            hasTelescopeTrackMode = dev.Properties.ContainsKey("TELESCOPE_TRACK_MODE"),
            hasFlatLightControl = dev.Properties.ContainsKey("FLAT_LIGHT_CONTROL"),
            hasFlatLightIntensity = dev.Properties.ContainsKey("FLAT_LIGHT_INTENSITY")
        };

        return new
        {
            name,
            driverInterface = dev.DriverInterface,
            connected = dev.IsConnected,
            isCamera = dev.IsCamera,
            isTelescope = dev.IsTelescope,
            isFocuser = dev.IsFocuser,
            isFilterWheel = dev.IsFilterWheel,
            isSwitch = dev.IsSwitch,
            isWeather = dev.IsWeather,
            propertyCount = dev.Properties.Count,
            properties,
            probeVerdict
        };
    }
}
