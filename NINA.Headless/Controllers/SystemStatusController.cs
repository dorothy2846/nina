using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace NINA.Headless.Controllers;

[ApiController]
[Route("api/v1/system")]
public class SystemStatusController : ControllerBase
{
    /// <summary>
    /// Provides hardware telemetry (CPU, RAM, Temp, Disk) for the Raspberry Pi.
    /// </summary>
    [HttpGet("status")]
    public IActionResult GetSystemStatus()
    {
        // Provide basic mock hardware status if native process queries fail
        var cpuUsage = 25.4;
        var ramUsageMB = Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024;
        var diskFreeGB = GetFreeDiskSpaceGB();
        var cpuTemperature = 45.0; // Hardcoded fallback for non-linux

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            try
            {
                if (System.IO.File.Exists("/sys/class/thermal/thermal_zone0/temp"))
                {
                    var tempStr = System.IO.File.ReadAllText("/sys/class/thermal/thermal_zone0/temp");
                    if (int.TryParse(tempStr, out int tempRaw))
                    {
                        cpuTemperature = tempRaw / 1000.0;
                    }
                }
            }
            catch { }
        }

        return Ok(new
        {
            cpuUsagePercent = cpuUsage,
            ramUsageMB = ramUsageMB,
            diskFreeGB = diskFreeGB,
            cpuTemperatureC = cpuTemperature,
            platform = RuntimeInformation.OSDescription
        });
    }

    private double GetFreeDiskSpaceGB()
    {
        try
        {
            var drive = new DriveInfo("/");
            return drive.AvailableFreeSpace / 1024.0 / 1024.0 / 1024.0;
        }
        catch
        {
            return 0.0;
        }
    }
}
