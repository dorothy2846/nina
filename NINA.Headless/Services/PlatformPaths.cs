using System;
using System.IO;
using System.Runtime.InteropServices;

namespace NINA.Headless.Services;

/// <summary>
/// Path resolution for NINA Headless. Production target is Linux (USB-booted
/// appliance); macOS is the developer build/run environment. The macOS branches
/// exist to keep the dev inner loop fast and are not shipped.
/// </summary>
public static class PlatformPaths
{
    public static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
    public static bool IsMacOS => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary>Short platform identifier for logs and `/api/v1/health` responses.</summary>
    public static string PlatformName
    {
        get
        {
            var arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
            if (IsLinux) return $"linux-{arch}";
            if (IsMacOS) return $"osx-{arch}";
            return $"unsupported-{arch}";
        }
    }

    /// <summary>ASTAP plate solver binary. Override via <c>NINA_ASTAP_PATH</c>.</summary>
    public static string AstapPath
    {
        get
        {
            var envPath = Environment.GetEnvironmentVariable("NINA_ASTAP_PATH");
            if (!string.IsNullOrEmpty(envPath)) return envPath;
            if (IsMacOS) return "/Applications/ASTAP.app/Contents/MacOS/astap";
            // Linux default — xvfb-run wraps it so the GUI-linked build stays
            // happy on a headless appliance without an X server.
            return "/usr/bin/xvfb-run -a astap";
        }
    }

    /// <summary>Temp directory. Linux prefers /tmp (tmpfs) for large throwaway
    /// files like plate-solve inputs; macOS dev falls back to the OS default.</summary>
    public static string TempDir
        => IsLinux ? "/tmp" : Path.GetTempPath();

    /// <summary>Application data directory (profiles, logs, catalogs).</summary>
    public static string AppDataDir
    {
        get
        {
            var envDir = Environment.GetEnvironmentVariable("NINA_DATA_DIR");
            if (!string.IsNullOrEmpty(envDir)) return envDir;

            var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(baseDir, "NINA.Headless");
        }
    }

    /// <summary>Persistent config directory — <c>~/.config/nina-headless</c> on
    /// both Linux and macOS. Used by rendezvous config, identity keypair,
    /// paired devices, AP settings. Overridden to <c>/persist/nina-headless</c>
    /// on the USB-boot image via env var so read-only OS + RW persist works.</summary>
    public static string ConfigDir
    {
        get
        {
            var envDir = Environment.GetEnvironmentVariable("NINA_CONFIG_DIR");
            if (!string.IsNullOrEmpty(envDir)) return envDir;
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".config", "nina-headless");
        }
    }

    /// <summary>PHD2 executable. Override via <c>NINA_PHD2_PATH</c>.</summary>
    public static string Phd2Path
    {
        get
        {
            var envPath = Environment.GetEnvironmentVariable("NINA_PHD2_PATH");
            if (!string.IsNullOrEmpty(envPath)) return envPath;
            if (IsMacOS) return "/Applications/PHD2.app/Contents/MacOS/PHD2";
            return "phd2"; // Linux — rely on PATH
        }
    }
}
