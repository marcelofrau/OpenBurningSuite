// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System.Runtime.InteropServices;
using OpenBurningSuite.Native.Optical;

namespace OpenBurningSuite.Native.Platform;

/// <summary>
/// Native disc ejection using SCSI START STOP UNIT command on all platforms,
/// with platform-specific ioctl/API fallbacks.
/// Each platform's fallback implementation is in a dedicated partial-class file.
/// </summary>
public static partial class NativeEject
{
    /// <summary>Ejects the disc from the specified drive.</summary>
    public static bool EjectDisc(string devicePath)
    {
        // Try SCSI START STOP UNIT first (works on all platforms including macOS via IOKit)
        try
        {
            using var drive = new OpticalDrive(devicePath);
            // Unlock the tray first (PREVENT/ALLOW MEDIUM REMOVAL = allow)
            drive.PreventMediumRemoval(false);
            if (drive.Eject()) return true;
        }
        catch { }

        // Platform-specific fallbacks
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return EjectLinux(devicePath);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return EjectMacOs(devicePath);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return EjectWindows(devicePath);

        return false;
    }

    /// <summary>Loads/closes the disc tray.</summary>
    /// <remarks>
    /// On macOS, the SCSI START STOP UNIT (Load) command may fail via IOKit if the
    /// drive's user client is not properly initialized (e.g., after eject or when
    /// using an IORegistry path). The macOS fallback uses a retry with UNIT ATTENTION
    /// clearing and drive readiness polling, since macOS does not have a system-level
    /// tray-load command like diskutil. On Linux, CDROMCLOSETRAY ioctl is used as fallback.
    /// </remarks>
    public static bool LoadTray(string devicePath)
    {
        try
        {
            using var drive = new OpticalDrive(devicePath);
            // Clear any pending UNIT ATTENTION that could interfere with load
            drive.TestUnitReady();
            if (drive.LoadTray()) return true;

            // On macOS, IOKit may need a moment to re-initialize after eject.
            // Retry after clearing UNIT ATTENTION and waiting for drive readiness.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                // Drain any UNIT ATTENTION states (up to 3)
                for (int i = 0; i < 3; i++)
                    drive.TestUnitReady();

                // Wait for drive to become ready
                drive.WaitForDriveReady(maxWaitMs: 5_000, pollIntervalMs: 500);

                // Retry load
                if (drive.LoadTray()) return true;
            }
        }
        catch { }

        // Platform-specific fallbacks
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return LoadTrayWindows(devicePath);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return LoadTrayLinux(devicePath);

        // macOS: no system-level command for tray loading (diskutil doesn't support it).
        // The SCSI path above with retry is the only option on macOS.
        return false;
    }
}
