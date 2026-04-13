// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Runtime.InteropServices;
using OpenBurningSuite.Native.Platform;

namespace OpenBurningSuite.Helpers;

public static class PlatformHelper
{
    public static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    public static bool IsMacOS => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    public static string PlatformName =>
        IsLinux ? "Linux" : IsWindows ? "Windows" : IsMacOS ? "macOS" : "Unknown";

    /// <summary>Returns the default device path prefix for optical drives on this platform.</summary>
    public static string DefaultOpticalDevicePath =>
        IsLinux ? "/dev/sr0" : IsWindows ? "D:\\" : "/dev/rdisk2";

    /// <summary>Returns a user-friendly drive identifier from a device path.</summary>
    public static string FriendlyDrivePath(string devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath)) return "Unknown";
        if (IsWindows && devicePath.Length >= 2 && char.IsLetter(devicePath[0]) && devicePath[1] == ':')
            return devicePath[..2];
        return devicePath;
    }

    /// <summary>Returns the standard null device path for the current platform.</summary>
    public static string NullDevice => IsWindows ? "NUL" : "/dev/null";

    /// <summary>Checks whether we are running as root/Administrator using native P/Invoke.</summary>
    public static bool IsElevated => NativePrivilege.IsElevated;

    /// <summary>
    /// Opens the platform file explorer at the specified path.
    /// On Windows: explorer.exe, on macOS: open, on Linux: xdg-open.
    /// </summary>
    public static bool OpenFileExplorer(string path)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                UseShellExecute = true,
                CreateNoWindow = false
            };

            if (IsWindows)
            {
                psi.FileName = "explorer.exe";
                psi.Arguments = $"\"{path}\"";
            }
            else if (IsMacOS)
            {
                psi.FileName = "open";
                psi.Arguments = $"\"{path}\"";
            }
            else
            {
                psi.FileName = "xdg-open";
                psi.Arguments = $"\"{path}\"";
            }

            System.Diagnostics.Process.Start(psi);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
