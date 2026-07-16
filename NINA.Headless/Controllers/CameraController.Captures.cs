// CameraController — capture endpoints: still capture, capture library retrieval
// (thumbnail / full PNG / FITS, latest variants), capture deletion, and exposure abort.
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
    [HttpGet("latest")]
    public IActionResult GetLatestImage()
    {
        if (_state.LatestImageData == null || _state.LatestImageData.Length == 0)
        {
            return NoContent();
        }

        return File(_state.LatestImageData, "image/png");
    }

    [HttpPost("capture")]
    public async Task<IActionResult> Capture([FromBody] CaptureRequest request)
    {
        if (request.ExposureTime <= 0)
            return BadRequest(new { success = false, message = "ExposureTime must be greater than 0" });

        // Only the INDI path is wired to real hardware — NINA's CameraMediator has no registered
        // handler in this headless build, so Capture there returned Task.CompletedTask and the
        // app only ever saw a transparent placeholder. Route all captures through INDI direct.
        var selected = _equipment.GetSelected(DeviceKind.Camera);
        if (selected?.Provider != EquipmentProvider.Indi || !_equipment.IsConnected(DeviceKind.Camera))
            return StatusCode(503, new { success = false, message = "No INDI camera connected" });

        // INDI drivers (PlayerOne / ZWO / etc.) reject CCD_EXPOSURE while
        // CCD_VIDEO_STREAM is ON — the still capture times out at 30s.
        // We pause the streaming MODE (driver level) for the duration, but
        // CameraStreamService keeps the encoder + WebRTC pipeline alive by
        // re-pushing the last cached frame via its keepalive loop. The
        // viewfinder shows a held-but-not-frozen image; the user sees no
        // disconnect, capture runs at full precision, and the stream
        // resumes with fresh frames as soon as the driver releases the
        // sensor. Best of both worlds without the impossible "physically
        // simultaneous capture + stream" requirement.
        // Pre-capture dither — only when explicitly requested (sequencer
        // sets it for non-first Lights), only when PHD2 is actively
        // Guiding (otherwise the dither command no-ops or errors), and
        // only on Light frames. Dither itself blocks until PHD2 settles
        // so the next exposure starts on a clean star — that's the
        // whole point. Failures are non-fatal: a missed dither just
        // means slight pattern noise on stacked output, which is much
        // less bad than failing the capture.
        if (request.DitherPixels > 0
            && string.Equals(request.ImageType ?? "Light", "Light", StringComparison.OrdinalIgnoreCase)
            && _phd2.CurrentAppState == "Guiding")
        {
            try { await _phd2.DitherAsync(request.DitherPixels, HttpContext.RequestAborted); }
            catch (Exception ex) { _state.NotifyStateChanged("dither", new { failed = true, message = ex.Message }); }
        }

        bool resumeStreamAfter = _stream.IsRunning;
        if (resumeStreamAfter)
        {
            try { await _stream.PauseSensorAsync(HttpContext.RequestAborted); }
            catch { /* hiccup — capture still tries */ }
        }

        try
        {
            // Apply gain/offset before starting the exposure. Silently ignored if the driver
            // doesn't expose CCD_CONTROLS / CCD_GAIN / CCD_OFFSET.
            try { await _indi.SetGainAsync(selected.UniqueId, request.Gain, HttpContext.RequestAborted); } catch { }
            try { await _indi.SetOffsetAsync(selected.UniqueId, request.Offset, HttpContext.RequestAborted); } catch { }
            // Hardware binning destroys Bayer alignment on colour sensors —
            // the camera sums R+G+G+B per output pixel and the mosaic is gone.
            // Force the camera to 1×1 and apply the user's requested binning
            // as a server-side average AFTER debayering. Costs a slightly
            // slower sensor readout but preserves colour at any binning.
            if (request.Binning >= 1)
            {
                try { await _indi.SetBinningAsync(selected.UniqueId, 1, 1, HttpContext.RequestAborted); } catch { }
            }
            // Set CCD_FRAME_TYPE so the driver tags the FITS (and for dark/bias skips shutter
            // mechanics). Defaults to Light when the client doesn't specify.
            var imageType = string.IsNullOrWhiteSpace(request.ImageType) ? "Light" : request.ImageType!;
            await _indi.SetFrameTypeAsync(selected.UniqueId, imageType, HttpContext.RequestAborted);

            _state.MarkExposureStarted(request.ExposureTime);
            var (fitsBytes, _) = await _indi.CameraExposeAsync(selected.UniqueId, request.ExposureTime, HttpContext.RequestAborted);

            byte[] pngBytes;
            try { pngBytes = FitsToPng.Convert(fitsBytes, Math.Max(1, request.Binning)); }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, message = $"Image decode failed: {ex.Message}" });
            }

            // Camera identity — server-authoritative. We pull it from the currently selected
            // INDI device rather than trusting a client-supplied id so Dark/Bias library keys
            // can't be spoofed into the wrong camera's bucket.
            var entry = await _captures.SaveAsync(fitsBytes, pngBytes, new CaptureStore.SaveMetadata(
                ExposureSeconds: request.ExposureTime,
                Gain: request.Gain, Offset: request.Offset, Binning: request.Binning,
                ImageType: imageType, Filter: request.Filter,
                CameraId: selected.UniqueId, CameraName: selected.Name,
                PlanId: request.PlanId, PlanName: request.PlanName));
            // If live stacking is active and this is a Light, feed the frame in. Silent no-op
            // if the user hasn't enabled stacking — we don't want to waste cycles on every
            // capture just in case.
            if (imageType.Equals("Light", StringComparison.OrdinalIgnoreCase) && _liveStack.IsActive)
            {
                // Fire-and-forget — ProcessFrame does FITS parse + star detect + warp
                // (100-300 ms on 9 MP); running it on the response thread serializes back-to-back
                // captures against stacking and pushes the HTTP response out by that much.
                _ = Task.Run(() => _liveStack.ProcessFrame(fitsBytes, selected.UniqueId, request.PlanId, request.ExposureTime, request.Gain, request.Offset, request.Binning, request.Filter));
            }
            _state.LatestImageData = pngBytes;
            _state.NotifyStateChanged("camera", _state.BuildCameraStatus());
            _eventBus.Broadcast("StatusUpdate", new
            {
                timestamp = DateTime.UtcNow,
                camera = _state.BuildCameraStatus(),
                telescope = _state.BuildTelescopeStatus(),
                guider = _state.BuildGuiderStatus()
            });

            return Ok(new
            {
                success = true,
                message = "Capture complete",
                exposureTime = request.ExposureTime,
                gain = request.Gain,
                offset = request.Offset,
                binning = request.Binning,
                filter = request.Filter,
                captureId = entry.Id,
                thumbnailUrl = $"/api/v1/camera/captures/{entry.Id}/thumbnail",
                fullUrl = $"/api/v1/camera/captures/{entry.Id}/full",
                fitsUrl = $"/api/v1/camera/captures/{entry.Id}/fits"
            });
        }
        catch (OperationCanceledException)
        {
            return StatusCode(504, new { success = false, message = "Exposure timed out waiting for image data" });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { success = false, message = ex.Message });
        }
        finally
        {
            _state.MarkExposureFinished();
            if (resumeStreamAfter)
            {
                _ = Task.Run(async () =>
                {
                    try { await _stream.ResumeSensorAsync(CancellationToken.None); } catch { }
                });
            }
        }
    }

    [HttpGet("captures")]
    public IActionResult ListCaptures([FromQuery] int limit = 50)
    {
        var items = _captures.GetAll()
            .OrderByDescending(e => e.Timestamp)
            .Take(Math.Clamp(limit, 1, 200))
            .Select(e => new
            {
                id = e.Id,
                timestamp = e.Timestamp,
                exposureSeconds = e.ExposureSeconds,
                gain = e.Gain,
                offset = e.Offset,
                binning = e.Binning,
                imageType = e.ImageType,
                filter = e.Filter,
                // Carrying these through lets the Capture Library UI group and filter by
                // camera or by plan without a second round-trip.
                cameraId = e.CameraId,
                cameraName = e.CameraName,
                planId = e.PlanId,
                planName = e.PlanName,
                sharedWithPlanIds = e.SharedWithPlanIds,
                fitsSizeBytes = e.FitsSizeBytes,
                fullPngSizeBytes = e.FullPngSizeBytes,
                thumbnailUrl = $"/api/v1/camera/captures/{e.Id}/thumbnail",
                fullUrl = $"/api/v1/camera/captures/{e.Id}/full",
                fitsUrl = $"/api/v1/camera/captures/{e.Id}/fits"
            });
        return Ok(items);
    }

    [HttpGet("captures/{id}/thumbnail")]
    public IActionResult GetCaptureThumbnail(string id)
    {
        var e = _captures.GetById(id);
        if (e == null || !System.IO.File.Exists(e.ThumbnailPath)) return NotFound();
        var mime = e.ThumbnailPath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ? "image/jpeg" : "image/png";
        return PhysicalFile(e.ThumbnailPath, mime);
    }

    [HttpGet("captures/{id}/full")]
    public IActionResult GetCaptureFullPng(string id)
    {
        var e = _captures.GetById(id);
        if (e == null || !System.IO.File.Exists(e.FullPngPath)) return NotFound();
        return PhysicalFile(e.FullPngPath, "image/png");
    }

    [HttpDelete("captures/{id}")]
    public IActionResult DeleteCapture(string id)
    {
        // Look up the entry BEFORE deletion so the calibration library can invalidate the
        // master frame it belonged to. Without this, a cached master stays in memory with a
        // deleted frame still contributing to its mean — rebuilds happen only on next capture.
        var e = _captures.GetById(id);
        if (e == null) return NotFound(new { success = false });
        if (!_captures.DeleteById(id)) return NotFound(new { success = false });
        _library.NotifyCaptureAdded(e); // same invalidation semantics (any group-change wipes the master)
        return Ok(new { success = true });
    }

    [HttpGet("captures/{id}/fits")]
    public IActionResult GetCaptureFits(string id)
    {
        var e = _captures.GetById(id);
        if (e == null || !System.IO.File.Exists(e.FitsPath)) return NotFound();
        return PhysicalFile(e.FitsPath, "application/fits", $"{id}.fits");
    }

    [HttpGet("latest/thumbnail")]
    public IActionResult GetLatestThumbnail()
    {
        var e = _captures.GetLatest();
        if (e == null || !System.IO.File.Exists(e.ThumbnailPath)) return NoContent();
        var mime = e.ThumbnailPath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ? "image/jpeg" : "image/png";
        return PhysicalFile(e.ThumbnailPath, mime);
    }

    [HttpGet("latest/full")]
    public IActionResult GetLatestFullPng()
    {
        var e = _captures.GetLatest();
        if (e == null || !System.IO.File.Exists(e.FullPngPath)) return NoContent();
        return PhysicalFile(e.FullPngPath, "image/png");
    }

    [HttpGet("latest/fits")]
    public IActionResult GetLatestFits()
    {
        var e = _captures.GetLatest();
        if (e == null || !System.IO.File.Exists(e.FitsPath)) return NoContent();
        return PhysicalFile(e.FitsPath, "application/fits", $"{e.Id}.fits");
    }

    [HttpPost("abort")]
    public async Task<IActionResult> Abort()
    {
        var selected = _cameraSelection.GetSelected();
        if (_cameraSelection.IsConnected && selected?.Provider == CameraProvider.Indi)
        {
            var ok = await _indi.AbortExposureAsync(selected.UniqueId, HttpContext.RequestAborted);
            return Ok(new { success = ok, message = ok ? "Capture aborted" : "Driver doesn't expose CCD_ABORT_EXPOSURE" });
        }

        _state.CameraMediator.AbortExposure();
        return Ok(new { success = true, message = "Capture aborted" });
    }
}
