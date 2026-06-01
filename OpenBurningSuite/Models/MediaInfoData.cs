// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using OpenBurningSuite.Helpers;

namespace OpenBurningSuite.Models;

public class MediaInfoData
{
    // ── Current Profile ──
    public string CurrentProfile { get; set; } = string.Empty; // e.g. "CD-R", "DVD-R", "DVD+R DL", "DVD-ROM"

    // ── Disc Information ──
    public string DiscState { get; set; } = string.Empty;         // Empty, Complete
    public string LastSessionState { get; set; } = string.Empty;  // Empty, Complete
    public bool IsErasable { get; set; }
    public long Sectors { get; set; }             // Total sectors
    public long FreeSectors { get; set; }         // Free sectors (blank media)
    public long CapacityBytes { get; set; }       // Total bytes
    public long FreeBytes { get; set; }           // Free bytes (blank media)
    public string TimeMsf { get; set; } = string.Empty;           // MM:SS:FF format (DVD)
    public string FreeTimeMsf { get; set; } = string.Empty;      // Free time in MM:SS:FF (CD)
    public uint NextWritableAddress { get; set; }
    public string VolumeLabel { get; set; } = string.Empty;
    public string FileSystem { get; set; } = string.Empty;
    public int Sessions { get; set; }
    public int Tracks { get; set; }

    // ── MID (CD: from ATIP; DVD/BD: from Manufacturer Info) ──
    public string Mid { get; set; } = string.Empty;          // "97m15s05f" (CD) or "MCC 02RG20" (DVD)
    public string MidManufacturer { get; set; } = string.Empty; // "TDK Corp." from MID lookup

    // ── Supported / Current Speeds ──
    public List<string> SupportedReadSpeeds { get; set; } = new();
    public List<string> SupportedWriteSpeeds { get; set; } = new();
    public string CurrentReadSpeed { get; set; } = string.Empty; // "3,3x - 8x" or "6x; 8x"

    // ── ATIP Information (CD only) ──
    public string AtipDiscId { get; set; } = string.Empty;       // "97m15s05f"
    public string AtipManufacturer { get; set; } = string.Empty; // "TDK Corp."
    public string AtipLeadInStart { get; set; } = string.Empty;  // "97m15s05f"
    public string AtipLeadOutEnd { get; set; } = string.Empty;   // "79m59s74f"

    // ── TOC Information ──
    public List<TocEntryInfo> TocEntries { get; set; } = new();

    // ── Pre-recorded Information (format 0x0E) ──
    public string PreRecordedManufacturerId { get; set; } = string.Empty;

    // ── Recording Management Area ──
    public string RecordingManagementArea { get; set; } = string.Empty;

    // ── Physical Format Information (DVD/BD) ──
    public string BookType { get; set; } = string.Empty;
    public string PartVersion { get; set; } = string.Empty;
    public string DiscSize { get; set; } = string.Empty;
    public string MaxReadRate { get; set; } = string.Empty;
    public int NumberOfLayers { get; set; }
    public string TrackPath { get; set; } = string.Empty;
    public string LinearDensity { get; set; } = string.Empty;
    public string TrackDensity { get; set; } = string.Empty;
    public long DataAreaStartSector { get; set; }
    public long DataAreaEndSector { get; set; }
    public long LayerBreakSector { get; set; }

    // ── Manufacturer Information (from format 0x04) ──
    public string ManufacturerId { get; set; } = string.Empty;   // "MCC 02RG20"
    public string ManufacturerName { get; set; } = string.Empty; // from MID lookup

    // ── BD Specific ──
    public string BdMediaId { get; set; } = string.Empty;

    // ── DVD Unique Disc ID ──
    public string UniqueDiscId { get; set; } = string.Empty;

    // ── Protection ──
    public string CopyrightProtectionType { get; set; } = string.Empty;
    public string RegionManagementMask { get; set; } = string.Empty;
    public bool HasBca { get; set; }
    public int BcaDataSize { get; set; }

    // ── M-DISC ──
    public bool IsMDisc { get; set; }
    public string MDiscType { get; set; } = string.Empty;

    // ── Misc ──
    public bool IsDvdRam { get; set; }

    // ── Drive Hardware ──
    public string VendorId { get; set; } = string.Empty;
    public string FirmwareRevision { get; set; } = string.Empty;
    public string DriveModel { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
    public int BufferSizeKiB { get; set; }

    // ── Formatted Properties ──
    public string CapacityFormatted =>
        CapacityBytes > 0 ? FormatHelper.FormatBytes(CapacityBytes) : "—";

    public string LayersFormatted =>
        NumberOfLayers > 0
            ? $"{NumberOfLayers} layer{(NumberOfLayers > 1 ? "s" : "")}"
            : "—";

    public string SpeedsFormatted =>
        SupportedWriteSpeeds.Count > 0
            ? string.Join(", ", SupportedWriteSpeeds)
            : "—";

    public bool HasPhysicalFormatInfo => !string.IsNullOrEmpty(BookType);
    public bool HasManufacturerInfo => !string.IsNullOrEmpty(ManufacturerId);
    public bool HasCdInfo => !string.IsNullOrEmpty(AtipDiscId);
    public bool HasTocInfo => TocEntries.Count > 0;
}

public class TocEntryInfo
{
    public int SessionNumber { get; set; }
    public int TrackNumber { get; set; }
    public string Mode { get; set; } = string.Empty; // Mode 1, Mode 2, Audio
    public long StartLba { get; set; }
    public long EndLba { get; set; }
    public long SizeSectors { get; set; }
}
