// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System.Collections.Generic;
using OpenBurningSuite.Models;
using OpenBurningSuite.Native.Platform;

namespace OpenBurningSuite.Services;

/// <summary>Discovers optical drives and reads disc/media information using native APIs.</summary>
public class DiscDiscoveryService
{
    public List<DiscDrive> DiscoverDrives()
    {
        return NativeDeviceDiscovery.DiscoverDrives();
    }

    /// <summary>
    /// Detects the content type of the disc currently inserted in the specified drive.
    /// Uses the unified <see cref="DiscContentDetectionService"/> for comprehensive
    /// CD, DVD, and Blu-ray content classification.
    /// </summary>
    /// <param name="drive">The drive to analyze.</param>
    /// <returns>Comprehensive content information.</returns>
    public static DiscContentInfo DetectDiscContent(Native.Optical.OpticalDrive drive)
    {
        return DiscContentDetectionService.DetectFromDrive(drive);
    }

    /// <summary>
    /// Detects the content type of a disc image file (ISO, BIN, IMG, etc.).
    /// Uses the unified <see cref="DiscContentDetectionService"/> for comprehensive
    /// CD, DVD, and Blu-ray content classification.
    /// </summary>
    /// <param name="imagePath">Path to the disc image file.</param>
    /// <returns>Comprehensive content information.</returns>
    public static DiscContentInfo DetectImageContent(string imagePath)
    {
        return DiscContentDetectionService.DetectFromImage(imagePath);
    }
}
