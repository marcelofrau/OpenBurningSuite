// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using OpenBurningSuite.Native.Optical;
using OpenBurningSuite.Native.Scsi;

namespace OpenBurningSuite.Native.Platform;

public static partial class NativeEject
{
    /// <summary>
    /// macOS fallback: unmount then eject the disc via diskutil.
    /// Also tries SCSI eject with explicit tray unlock before diskutil fallback,
    /// since some drives require PREVENT/ALLOW MEDIUM REMOVAL before accepting
    /// the START STOP UNIT eject command.
    /// </summary>
    private static bool EjectMacOs(string devicePath)
    {
        // For IORegistry paths, try to resolve to a BSD device name.
        // IOKit SCSI eject (via OpticalDrive.Eject()) was already attempted above.
        // diskutil needs a BSD device path to operate.
        var effectivePath = devicePath;
        if (effectivePath.StartsWith("IOService:", StringComparison.Ordinal))
        {
            var resolved = MacOsScsiTransport.ResolveBsdNameFromIoRegistryPath(effectivePath);
            if (string.IsNullOrEmpty(resolved))
            {
                // No media / no BSD name — try one more SCSI eject attempt with
                // explicit unlock and UNIT ATTENTION clearing via IORegistry path
                try
                {
                    using var drive = new OpticalDrive(effectivePath);
                    drive.PreventMediumRemoval(false);
                    for (int i = 0; i < 3; i++)
                        drive.TestUnitReady();
                    if (drive.Eject()) return true;
                }
                catch { }
                return false;
            }
            effectivePath = resolved;
        }

        // Strip trailing slash from device path (e.g., "/dev/disk2/" → "/dev/disk2")
        effectivePath = effectivePath.TrimEnd('/');

        try
        {
            // First unmount all volumes on the disc to ensure clean ejection
            var unmountPsi = new System.Diagnostics.ProcessStartInfo("diskutil")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            unmountPsi.ArgumentList.Add("unmountDisk");
            unmountPsi.ArgumentList.Add(effectivePath);

            using (var unmountProc = new System.Diagnostics.Process { StartInfo = unmountPsi })
            {
                unmountProc.Start();
                // Drain stdout/stderr BEFORE WaitForExit to prevent pipe buffer deadlock
                unmountProc.StandardOutput.ReadToEnd();
                unmountProc.StandardError.ReadToEnd();
                if (!unmountProc.WaitForExit(10_000))
                {
                    try { if (!unmountProc.HasExited) unmountProc.Kill(); } catch { /* best effort */ }
                }
            }
        }
        catch { /* unmount is best effort */ }

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("diskutil")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("eject");
            psi.ArgumentList.Add(effectivePath);

            using var proc = new System.Diagnostics.Process { StartInfo = psi };
            proc.Start();
            // Drain stdout/stderr BEFORE WaitForExit to prevent pipe buffer deadlock
            proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(10_000))
            {
                try { if (!proc.HasExited) proc.Kill(); } catch { /* best effort */ }
            }
            else if (proc.ExitCode == 0)
            {
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }
}
