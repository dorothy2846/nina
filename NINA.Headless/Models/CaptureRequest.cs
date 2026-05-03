namespace NINA.Headless.Models;

public record CaptureRequest
{
    public double ExposureTime { get; init; } = 1.0;
    public int Gain { get; init; } = 100;
    public int Offset { get; init; } = 10;
    public int Binning { get; init; } = 1;
    public string? Filter { get; init; }
    /// Frame type in FITS IMAGETYP convention. Drives the INDI CCD_FRAME_TYPE switch so
    /// the driver tags the file correctly and (where supported) skips shutter operations
    /// for Dark/Bias/Flat frames.
    public string? ImageType { get; init; } // "Light" | "Dark" | "Bias" | "Flat"
    /// UUID of the Sequencer plan this capture belongs to. Only meaningful for Lights (so
    /// auto-calibration can find the matching flat library) and Flats (so the captured
    /// flats attach to the right plan). Server fills in CameraId itself from the currently
    /// selected INDI device — clients don't need to send it.
    public string? PlanId { get; init; }
    /// Human-readable plan name at capture time. Snapshotted so the library browser can show
    /// "M42 Orion Nebula" as a folder label without re-joining against a live plan list.
    public string? PlanName { get; init; }
}
