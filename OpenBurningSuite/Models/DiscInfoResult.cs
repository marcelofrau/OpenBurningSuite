// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System.Collections.Generic;

namespace OpenBurningSuite.Models;

public class DiscInfoResult
{
    // --- General ---
    public string MediaType { get; set; } = string.Empty;
    public string DiscStatus { get; set; } = string.Empty;
    public string LastSessionState { get; set; } = string.Empty;
    public bool IsErasable { get; set; }
    public int Sessions { get; set; }
    public int Tracks { get; set; }
    public long FreeSectors { get; set; }
    public long FreeBytes { get; set; }
    public string FreeTimeFormatted { get; set; } = string.Empty;
    public long NextWritableAddress { get; set; }
    public string Mid { get; set; } = string.Empty;
    public string ManufacturerName { get; set; } = string.Empty;

    // --- Speed ---
    public List<string> SupportedReadSpeeds { get; set; } = new();
    public string CurrentReadSpeed { get; set; } = string.Empty;
    public List<string> SupportedWriteSpeeds { get; set; } = new();

    // --- Media-specific sections ---
    public AtipInfo? Atip { get; set; }

    public List<PhysicalFormatInfo> PhysicalFormats { get; set; } = new();

    // --- Drive ---
    public string Vendor { get; set; } = string.Empty;
    public string InterfaceType { get; set; } = string.Empty;
    public string DriveModel { get; set; } = string.Empty;
    public string Firmware { get; set; } = string.Empty;
    public string Serial { get; set; } = string.Empty;
    public int BufferSizeKiB { get; set; }

    // --- TOC / Track ---
    public Native.Optical.TocData? Toc { get; set; }
    public List<Native.Optical.TrackInfoData> TrackInfos { get; set; } = new();

    /// <summary>Data modes from READ HEADER command (keyed by track number).
    /// Needed because ReadTrackInfo may return DataMode=0 for pressed CD-ROMs.</summary>
    public Dictionary<ushort, byte> TrackDataModes { get; set; } = new();

    // --- Totals (computed from TOC LeadOut) ---
    public long TotalSectors { get; set; }
    public long DiscSizeBytes { get; set; }
    public string TotalTimeFormatted { get; set; } = string.Empty;

    // --- Extra ---
    public string PreRecordedManufacturerId { get; set; } = string.Empty;
    public string RecordingManagementArea { get; set; } = string.Empty;
}
