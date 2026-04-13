// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System.Collections.Generic;
using System.Runtime.InteropServices;
using OpenBurningSuite.Models;

namespace OpenBurningSuite.Native.Platform;

/// <summary>
/// Native device discovery using direct /sys/proc filesystem access on Linux,
/// .NET APIs on Windows, and IOKit service enumeration on macOS.
/// Each platform's implementation is in a dedicated partial-class file.
/// </summary>
public static partial class NativeDeviceDiscovery
{
    /// <summary>UDF Primary Volume Descriptor sector location (ECMA-167 §2/8.3, sector 256).</summary>
    private const int UdfPvdSector = 256;

    /// <summary>Discovers optical drives on the current platform.</summary>
    public static List<DiscDrive> DiscoverDrives()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return DiscoverLinux();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return DiscoverWindows();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return DiscoverMacOS();
        return new List<DiscDrive>();
    }
}
