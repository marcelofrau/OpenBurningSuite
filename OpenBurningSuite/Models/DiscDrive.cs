// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System.Collections.Generic;

namespace OpenBurningSuite.Models;

public class DiscDrive
{
    public string DevicePath { get; set; } = string.Empty;   // e.g. /dev/sr0 or D:\
    public string DriveLetter { get; set; } = string.Empty;  // Windows drive letter
    public string DriveModel { get; set; } = string.Empty;
    public string DriveType { get; set; } = string.Empty;    // CD-ROM, DVD-ROM, BD-ROM, etc.
    public string Status { get; set; } = string.Empty;       // Ready, No Media, Tray Open, etc.
    public bool CanRead { get; set; }
    public bool CanWrite { get; set; }
    public bool CanReadDVD { get; set; }
    public bool CanWriteDVD { get; set; }
    public bool CanReadBluRay { get; set; }
    public bool CanWriteBluRay { get; set; }
    public bool CanReadHDDVD { get; set; }
    public bool CanWriteHDDVD { get; set; }
    public List<string> SupportedWriteSpeeds { get; set; } = new();
    public List<string> SupportedReadSpeeds { get; set; } = new();
    public List<string> SupportedReadFormats { get; set; } = new();
    public List<string> SupportedWriteFormats { get; set; } = new();
    public DiscMedia? CurrentMedia { get; set; }

    // --- Detailed per-format profile capabilities ---
    public DriveProfileCapabilities Profiles { get; set; } = new();

    // --- Additional feature capabilities ---
    public DriveFeatureCapabilities Features { get; set; } = new();

    // --- Hardware info ---
    public string VendorId { get; set; } = string.Empty;
    public string BusType { get; set; } = string.Empty;
    public string FirmwareRevision { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
    public string VendorSpecificInfo { get; set; } = string.Empty;
    public int BufferSizeKiB { get; set; }

    public string DisplayName =>
        string.IsNullOrWhiteSpace(DriveLetter)
            ? (string.IsNullOrWhiteSpace(DevicePath) ? DriveModel : $"{DevicePath} ({DriveModel})")
            : $"{DriveLetter} - {DriveModel}";

    public override string ToString() => DisplayName;
}
