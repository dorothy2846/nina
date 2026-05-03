namespace NINA.Headless.Services;

/// <summary>Deterministic PHD2 parameter computation from guide optics + mount guide rate,
/// matching the math PHD2's own "New Profile Wizard" + Calibration Step Calculator use.
/// Callers push results via set_algo_param, set_exposure, or config-file-and-restart.</summary>
public static class GuidingAutotune
{
    public const double SiderealRateArcsecPerSec = 15.041;

    public record Inputs(
        double GuideScopeFocalLengthMm,
        double GuideCamPixelSizeUm,
        double MountGuideRateMultiplier = 0.5,
        /// Target declination in degrees. RA pulse travel scales with cos(dec) — without
        /// compensation a step computed for the equator produces half the travel at dec 60°
        /// and near-zero near the pole. 0° (equator) is the safe, conservative default.
        double DeclinationDegrees = 0.0);

    public record Result(
        double PixelScaleArcsecPerPx,
        double GuideRateArcsecPerSec,
        int CalibrationStepMs,
        int MaxRaDurationMs,
        int MaxDecDurationMs,
        double MinMovePixels,
        double DefaultAggressivenessPct,
        int SearchRegionPixels,
        double DefaultExposureSeconds,
        string RecommendedRaAlgorithm,
        string RecommendedDecAlgorithm,
        string Rationale);

    public static Result Compute(Inputs input)
    {
        if (input.GuideScopeFocalLengthMm <= 0 || input.GuideCamPixelSizeUm <= 0)
            throw new ArgumentException("Guide scope focal length and pixel size must be positive");
        if (input.MountGuideRateMultiplier <= 0 || input.MountGuideRateMultiplier > 1.5)
            throw new ArgumentException("Guide rate multiplier must be 0 < x <= 1.5 (sidereal)");
        if (input.DeclinationDegrees < -90 || input.DeclinationDegrees > 90)
            throw new ArgumentException("Declination must be within -90..+90 degrees");

        var pixelScale = 206.265 * input.GuideCamPixelSizeUm / input.GuideScopeFocalLengthMm;
        var guideRate = SiderealRateArcsecPerSec * input.MountGuideRateMultiplier;

        // PHD2 convention (matches the in-app Calibration Step Calculator): target ~2 px
        // of star travel PER step × 12 steps = ~25 px total travel. The previous version
        // aimed for 12 px in a single pulse — 6× too long — which saturated max-duration
        // clamps on short focal lengths and overshot on strain-wave mounts.
        const double TargetTravelPxPerStep = 2.0;
        var targetTravelArcsec = TargetTravelPxPerStep * pixelScale;

        // Declination compensation: RA motion projects to cos(dec) on the sky. Without this
        // the pulse is too short near the pole and calibration reports "star didn't move".
        var decRad = input.DeclinationDegrees * Math.PI / 180.0;
        var cosDec = Math.Max(0.05, Math.Cos(decRad)); // floor prevents div-by-zero at ±90°
        var calStepRawMs = targetTravelArcsec / guideRate * 1000.0 / cosDec;

        // Lower bound 100ms covers belt-drive mounts with very short effective focal
        // lengths; upper 2500ms matches PHD2's default cap for per-step pulses.
        var calStepMs = Math.Clamp((int)Math.Round(calStepRawMs), 100, 2500);

        // Max pulse duration: PHD2 factory default is 2000ms; stays sane across mount
        // classes. Strain-wave mounts (AM5, NYX-101) prefer 300-600ms — users can tighten
        // via the manual tuning sliders after connect.
        var maxDur = 2000;
        var minMovePx = pixelScale > 3.0 ? 0.25 : (pixelScale > 1.5 ? 0.18 : 0.12);
        // 65% is intentionally conservative vs PHD2's 75% default — first-night-safe to
        // avoid oscillation on unknown mounts. Users can raise via sliders once they trust
        // the response.
        var aggressiveness = 65.0;
        var searchRegion = Math.Max(15, (int)Math.Round(pixelScale * 4.0));
        // Exposure scales coarsely with focal length: shorter scopes see faster PE, longer
        // scopes benefit from averaging seeing.
        var exposureSec = input.GuideScopeFocalLengthMm < 200 ? 1.5
                        : input.GuideScopeFocalLengthMm > 600 ? 3.0
                        : 2.0;

        var raAlgo = "Hysteresis";
        var decAlgo = "ResistSwitch";

        var rationale =
            $"Pixel scale {pixelScale:F2}\"/px (FL {input.GuideScopeFocalLengthMm:F0} mm ÷ " +
            $"{input.GuideCamPixelSizeUm:F2} µm × 206.265). " +
            $"Guide rate {guideRate:F1}\"/s = {input.MountGuideRateMultiplier:F2}× sidereal. " +
            $"Cal step {calStepMs} ms targets ~2 px/step over 12 steps (≈25 px total) " +
            $"at dec {input.DeclinationDegrees:+0.0;-0.0;0}° (÷cos dec). " +
            $"Min-move {minMovePx:F2} px ({(minMovePx * pixelScale):F2}\") filters seeing noise. " +
            $"Max pulse {maxDur} ms = PHD2 default. " +
            $"Aggressiveness {aggressiveness:F0}% is soft-start (PHD2 default 75%). " +
            $"Algorithms: RA Hysteresis, Dec ResistSwitch.";

        return new Result(
            PixelScaleArcsecPerPx: Math.Round(pixelScale, 3),
            GuideRateArcsecPerSec: Math.Round(guideRate, 3),
            CalibrationStepMs: calStepMs,
            MaxRaDurationMs: maxDur,
            MaxDecDurationMs: maxDur,
            MinMovePixels: Math.Round(minMovePx, 2),
            DefaultAggressivenessPct: aggressiveness,
            SearchRegionPixels: searchRegion,
            DefaultExposureSeconds: exposureSec,
            RecommendedRaAlgorithm: raAlgo,
            RecommendedDecAlgorithm: decAlgo,
            Rationale: rationale);
    }
}
