// CameraController — calibration and stacking endpoints: calibration library
// (calibrated previews, groups), live stacking, flat wizard, and Dark/Bias batch.
// Partial of CameraController; fields and constructor live in CameraController.cs.

using Microsoft.AspNetCore.Mvc;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using NINA.Headless.Models;
using NINA.Headless.Services;
using NINA.Headless.Services.Remote;

namespace NINA.Headless.Controllers;

public partial class CameraController
{
    // ----- Calibration library -----

    /// <summary>Apply the best matching masters (dark/bias/flat) to a specific Light capture
    /// and return the calibrated PNG. 404 if not a Light, or if no matching masters exist.
    /// Recomputes on every call — masters are cached in memory, so this is ~200 ms for a
    /// typical 9 MP frame, worth it to keep the toggle fully dynamic.</summary>
    [HttpGet("captures/{id}/calibrated")]
    public IActionResult GetCaptureCalibrated(string id)
    {
        var e = _captures.GetById(id);
        if (e == null || !System.IO.File.Exists(e.FitsPath)) return NotFound();
        if (!e.ImageType.Equals("Light", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "Only Light frames can be calibrated" });
        byte[] fitsBytes;
        try { fitsBytes = System.IO.File.ReadAllBytes(e.FitsPath); } catch { return StatusCode(500); }
        var result = _library.TryCalibrate(new CalibrationLibrary.CalibrationInputs(
            fitsBytes, e.CameraId, e.PlanId, e.ExposureSeconds, e.Gain, e.Offset, e.Binning, e.Filter));
        if (result == null) return NotFound(new { error = "No matching calibration masters" });
        Response.Headers["X-Calibration-Dark"] = result.DarkApplied ? result.DarkFrames.ToString() : "0";
        Response.Headers["X-Calibration-Flat"] = result.FlatApplied ? result.FlatFrames.ToString() : "0";
        Response.Headers["X-Calibration-Bias"] = result.BiasApplied ? result.BiasFrames.ToString() : "0";
        return File(result.Png, "image/png");
    }

    [HttpGet("latest/calibrated")]
    public IActionResult GetLatestCalibrated()
    {
        var e = _captures.GetLatest();
        if (e == null) return NoContent();
        if (!e.ImageType.Equals("Light", StringComparison.OrdinalIgnoreCase)) return NoContent();
        return GetCaptureCalibrated(e.Id);
    }

    /// <summary>List calibration groups (darks/bias/flats indexed by their matching key)
    /// along with their frame counts and latest capture times. Backs the iOS library UI.
    ///
    /// Query params (both optional):
    ///   • cameraId — filter to Dark/Bias groups for one specific camera. The Capture Library
    ///     UI uses this to show a per-camera calibration shelf.
    ///   • planId   — filter to Flat groups for one specific plan. The Sequencer's per-plan
    ///     Flats card uses this.
    /// With no filter, returns every group regardless of type.</summary>
    [HttpGet("calibration/library/groups")]
    public IActionResult GetCalibrationGroups([FromQuery] string? cameraId = null, [FromQuery] string? planId = null)
    {
        IEnumerable<CalibrationLibrary.GroupInfo> groups = _library.EnumerateGroups();
        if (!string.IsNullOrWhiteSpace(cameraId))
            groups = groups.Where(g => g.Key.CameraId == cameraId);
        if (!string.IsNullOrWhiteSpace(planId))
            groups = groups.Where(g => g.Key.PlanId == planId);

        var payload = groups.Select(g => new
        {
            imageType = g.Key.ImageType,
            cameraId = g.Key.CameraId,
            cameraName = g.CameraName,
            planId = g.Key.PlanId,
            exposureMs = g.Key.ExposureMs,
            gain = g.Key.Gain,
            offset = g.Key.Offset,
            binning = g.Key.Binning,
            filter = g.Key.Filter,
            label = g.Key.DisplayLabel(),
            frameCount = g.FrameCount,
            latest = g.Latest
        }).ToList();
        return Ok(payload);
    }

    public record DeleteGroupRequest(string ImageType, string? CameraId, string? PlanId, int ExposureMs, int Gain, int Offset, int Binning, string? Filter);

    [HttpDelete("calibration/library/group")]
    public IActionResult DeleteCalibrationGroup([FromBody] DeleteGroupRequest req)
    {
        var key = new CalibrationLibrary.MatchKey(req.ImageType, req.CameraId, req.PlanId, req.ExposureMs, req.Gain, req.Offset, req.Binning, req.Filter);
        var count = _library.DeleteGroup(key);
        return Ok(new { deleted = count });
    }

    // ----- Live stacking -----

    [HttpPost("livestack/start")]
    public IActionResult LiveStackStart() { _liveStack.Start(); return Ok(new { success = true }); }

    [HttpPost("livestack/stop")]
    public IActionResult LiveStackStop() { _liveStack.Stop(); return Ok(new { success = true }); }

    [HttpPost("livestack/reset")]
    public IActionResult LiveStackReset() { _liveStack.Reset(); return Ok(new { success = true }); }

    [HttpGet("livestack/status")]
    public IActionResult LiveStackStatus() => Ok(_liveStack.Snapshot());

    [HttpGet("livestack/preview")]
    public IActionResult LiveStackPreview()
    {
        var png = _liveStack.BuildPreviewPng();
        if (png == null) return NoContent();
        return File(png, "image/png");
    }

    // ----- Flat wizard -----

    public record FlatWizardStartRequest(
        int? TargetAdu, int? Tolerance, int? FramesPerFilter,
        int? Gain, int? Offset, int? Binning,
        bool? UseFlatPanel, int? PanelBrightness,
        double? MinExposure, double? MaxExposure,
        string? PlanId, string? PlanName,
        IReadOnlyList<string>? AdditionalPlanIds);

    [HttpPost("flat-wizard/start")]
    public async Task<IActionResult> FlatWizardStart([FromBody] FlatWizardStartRequest? req)
    {
        // Flats must be attached to a specific imaging plan — that's the whole point of the
        // plan-scoped flat library. Reject requests without one so we don't end up with
        // orphan flats that can't be auto-matched to any future Light.
        if (string.IsNullOrWhiteSpace(req?.PlanId))
        {
            return BadRequest(new { success = false, message = "planId is required — flats attach to a specific imaging plan" });
        }

        try
        {
            // Defaults match NINA's defaults (16-bit 50% saturation target, ±10% tolerance).
            var request = new FlatWizardService.FlatWizardRequest(
                TargetAdu:       req?.TargetAdu ?? 32000,
                Tolerance:       req?.Tolerance ?? 3000,
                FramesPerFilter: req?.FramesPerFilter ?? 20,
                Gain:            req?.Gain ?? 100,
                Offset:          req?.Offset ?? 10,
                Binning:         req?.Binning ?? 1,
                UseFlatPanel:    req?.UseFlatPanel ?? true,
                PanelBrightness: req?.PanelBrightness ?? 128,
                MinExposure:     req?.MinExposure ?? 0.1,
                MaxExposure:     req?.MaxExposure ?? 15.0,
                PlanId:          req!.PlanId,
                PlanName:        req.PlanName,
                AdditionalPlanIds: req.AdditionalPlanIds);
            await _flatWizard.StartAsync(request);
            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    [HttpPost("flat-wizard/continue")]
    public IActionResult FlatWizardContinue()
    {
        _flatWizard.ContinueManual();
        return Ok(new { success = true });
    }

    [HttpPost("flat-wizard/stop")]
    public IActionResult FlatWizardStop()
    {
        _flatWizard.Cancel();
        return Ok(new { success = true });
    }

    [HttpGet("flat-wizard/status")]
    public IActionResult FlatWizardStatus() => Ok(_flatWizard.Snapshot());

    // ----- Calibration batch (Dark / Bias) -----
    //
    // Flat has its own orchestrated wizard (exposure search, per-filter loop). Dark and
    // Bias are simple: capture N frames with the right IMAGETYP. Server-side so the loop
    // survives iOS backgrounding and WiFi blips.

    public record CalibrationStartRequest(
        string? ImageType,
        int? Count,
        double? ExposureTime,
        int? Gain,
        int? Offset,
        int? Binning);

    [HttpPost("calibration/start")]
    public async Task<IActionResult> CalibrationStart([FromBody] CalibrationStartRequest? req)
    {
        try
        {
            var type = (req?.ImageType ?? "Dark").Trim();
            var request = new CalibrationBatchService.CalibrationRequest(
                ImageType:    type,
                Count:        Math.Max(1, req?.Count ?? 20),
                ExposureTime: type.Equals("Bias", StringComparison.OrdinalIgnoreCase)
                                  ? 0.001
                                  : Math.Max(0.001, req?.ExposureTime ?? 30.0),
                Gain:         req?.Gain ?? 100,
                Offset:       req?.Offset ?? 10,
                Binning:      req?.Binning ?? 1);
            await _calibration.StartAsync(request);
            return Ok(new { success = true });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { success = false, message = ex.Message });
        }
    }

    [HttpPost("calibration/stop")]
    public IActionResult CalibrationStop()
    {
        _calibration.Cancel();
        return Ok(new { success = true });
    }

    [HttpGet("calibration/status")]
    public IActionResult CalibrationStatus() => Ok(_calibration.Snapshot());
}
