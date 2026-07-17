using Microsoft.AspNetCore.Mvc;
using NINA.Headless.Models;

namespace NINA.Headless.Services;

/// <summary>
/// Shared connect/disconnect plumbing for the per-kind device controllers (Focuser, FilterWheel,
/// Dome, Rotator, FlatPanel). They all follow the same shape: validate deviceId, mark selected,
/// issue CONNECT on the INDI side, propagate driver messages on failure. Pulling the logic here
/// keeps the controllers thin and ensures all kinds surface the same error format.
/// </summary>
public static class IndiDeviceConnectFlow
{
    public static async Task<IActionResult> ConnectAsync(
        EquipmentSelectionService equipment,
        IndiDiscoveryService indi,
        DeviceKind kind,
        string label,
        ConnectRequest? request,
        CancellationToken ct,
        AlpacaClient? alpaca = null)
    {
        var deviceId = request?.DeviceId;
        if (string.IsNullOrWhiteSpace(deviceId))
            return new BadRequestObjectResult(new { success = false, message = "deviceId required" });

        if (!equipment.Connect(kind, deviceId))
            return new NotFoundObjectResult(new { success = false, message = $"Unknown {label.ToLower()} '{deviceId}'", deviceId });

        var selected = equipment.GetSelected(kind);
        try
        {
            if (selected?.Provider == EquipmentProvider.Indi)
            {
                var result = await indi.ConnectDeviceAsync(selected.UniqueId, ct);
                // Intent mirrors what we told the user: connected on success, not-connected
                // on a surfaced failure (a background retry after an error we already
                // reported would be surprising).
                indi.RecordConnectionIntent(selected.UniqueId, result.Ok);
                if (!result.Ok)
                {
                    equipment.Disconnect(kind);
                    return new ObjectResult(new
                    {
                        success = false,
                        message = result.Reason ?? "INDI driver did not become ready",
                        driverMessages = result.DriverMessages,
                        deviceId
                    })
                    { StatusCode = 503 };
                }
            }
            else if (selected?.Provider == EquipmentProvider.Alpaca && alpaca != null)
            {
                // Alpaca drivers handle the USB/network handshake themselves; we just flip
                // Connected=true and trust the driver. Errors bubble as 503 with the
                // Alpaca-provided message so iOS shows the underlying cause.
                await alpaca.SetConnectedAsync(selected, true, ct);
            }
        }
        catch (Exception ex)
        {
            equipment.Disconnect(kind);
            return new ObjectResult(new { success = false, message = ex.Message, deviceId }) { StatusCode = 503 };
        }
        return new OkObjectResult(new { success = true, message = $"{label} connected", deviceId, name = selected?.Name });
    }

    public static async Task<IActionResult> DisconnectAsync(
        EquipmentSelectionService equipment,
        IndiDiscoveryService indi,
        DeviceKind kind,
        string label,
        CancellationToken ct,
        AlpacaClient? alpaca = null)
    {
        var selected = equipment.GetSelected(kind);
        try
        {
            if (selected?.Provider == EquipmentProvider.Indi)
            {
                indi.RecordConnectionIntent(selected.UniqueId, false);
                await indi.DisconnectDeviceAsync(selected.UniqueId, ct);
            }
            else if (selected?.Provider == EquipmentProvider.Alpaca && alpaca != null)
                await alpaca.SetConnectedAsync(selected, false, ct);
        }
        catch { /* best-effort — still flip our local state so the user can retry */ }
        equipment.Disconnect(kind);
        return new OkObjectResult(new { success = true, message = $"{label} disconnected" });
    }
}
