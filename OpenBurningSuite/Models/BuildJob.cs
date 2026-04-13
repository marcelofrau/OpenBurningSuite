// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;

namespace OpenBurningSuite.Models;

public class BuildJob
{
    public string DiscType { get; set; } = "CD";             // CD, DVD-5, DVD-9, BD-25, BD-50, etc.
    public string FileSystem { get; set; } = "ISO 9660";     // ISO 9660, Joliet, UDF, Rock Ridge, Mixed
    public string UdfVersion { get; set; } = "1.02";
    public string SourceFolder { get; set; } = string.Empty;
    public string OutputImagePath { get; set; } = string.Empty;
    public string VolumeLabel { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string Preparer { get; set; } = string.Empty;
    public string Application { get; set; } = string.Empty;
    public bool Bootable { get; set; }
    public string BootImagePath { get; set; } = string.Empty;
    /// <summary>
    /// El Torito boot emulation mode.
    /// Values: "NoEmulation", "Floppy1200", "Floppy1440", "Floppy2880", "HardDisk".
    /// </summary>
    public string BootEmulation { get; set; } = "NoEmulation";
    /// <summary>
    /// El Torito boot platform identifier.
    /// Values: "x86" (0x00), "PowerPC" (0x01), "Mac" (0x02), "EFI" (0xEF).
    /// </summary>
    public string BootPlatform { get; set; } = "x86";
    /// <summary>
    /// Load segment for the boot image (El Torito Initial Entry).
    /// 0 = default (0x07C0 for x86 BIOS).
    /// </summary>
    public int BootLoadSegment { get; set; }
    /// <summary>
    /// Number of 512-byte virtual sectors to load. 0 = auto-calculate from boot image size.
    /// </summary>
    public int BootSectorCount { get; set; }
    /// <summary>
    /// Patch the El Torito boot info table into the boot image.
    /// Used by ISOLINUX/GRUB-based boot loaders.
    /// </summary>
    public bool BootInfoTable { get; set; }
    /// <summary>
    /// Optional EFI boot image path for dual-boot (BIOS + EFI) El Torito discs.
    /// When set, a second boot entry is added to the El Torito boot catalog.
    /// </summary>
    public string EfiBootImagePath { get; set; } = string.Empty;
    public bool JolietLongFilenames { get; set; } = true;
    public bool RockRidgeExtensions { get; set; }
    public bool DeepDirectoryNesting { get; set; }
    public bool AllowLowercase { get; set; }
    public bool AllowSpecialChars { get; set; }
    public bool PadToCapacity { get; set; }
    public string InputCharset { get; set; } = "UTF-8";
    public string OutputCharset { get; set; } = "UTF-8";
    public bool SortFiles { get; set; }
    public List<AudioTrack> AudioTracks { get; set; } = new();
    public string CdTextAlbum { get; set; } = string.Empty;
    public string CdTextArtist { get; set; } = string.Empty;
    /// <summary>CD-TEXT songwriter (disc level).</summary>
    public string CdTextSongwriter { get; set; } = string.Empty;
    /// <summary>CD-TEXT composer (disc level).</summary>
    public string CdTextComposer { get; set; } = string.Empty;
    /// <summary>CD-TEXT arranger (disc level).</summary>
    public string CdTextArranger { get; set; } = string.Empty;
    /// <summary>CD-TEXT disc message.</summary>
    public string CdTextMessage { get; set; } = string.Empty;
    /// <summary>CD-TEXT genre text.</summary>
    public string CdTextGenre { get; set; } = string.Empty;
    /// <summary>
    /// Media Catalog Number (MCN) also known as UPC/EAN barcode.
    /// Must be exactly 13 digits per Red Book specification.
    /// </summary>
    public string MediaCatalogNumber { get; set; } = string.Empty;
    public string GamingSystem { get; set; } = string.Empty;

    // ----- VCD / SVCD specific properties -----
    /// <summary>Video standard for VCD/SVCD: NTSC, PAL, SECAM, or Film.</summary>
    public string VideoStandard { get; set; } = "NTSC";
    /// <summary>Video resolution for VCD/SVCD (e.g., "352x240 (NTSC)", "480x480 (NTSC)").</summary>
    public string VideoResolution { get; set; } = string.Empty;
    /// <summary>MPEG video files to include as VCD/SVCD playback items (tracks 2-99).</summary>
    public List<string> VideoFiles { get; set; } = new();
    /// <summary>Optional still image files for VCD 2.0 / SVCD slide show segments.</summary>
    public List<string> StillImageFiles { get; set; } = new();
    /// <summary>VCD version: "1.1" (basic) or "2.0" (with PBC). Default is 2.0.</summary>
    public string VcdVersion { get; set; } = "2.0";
    /// <summary>Enable Playback Control (PBC) for VCD 2.0 / SVCD.</summary>
    public bool PlaybackControl { get; set; } = true;
    /// <summary>Disc label for VCD/SVCD (written into the INFO.VCD/INFO.SVD file).</summary>
    public string VcdDiscLabel { get; set; } = string.Empty;
    /// <summary>Create scan data (SCANDATA.DAT) for fast forward/rewind support.</summary>
    public bool CreateScanData { get; set; } = true;
    /// <summary>Create search data (SEARCH.DAT) for chapter search support.</summary>
    public bool CreateSearchData { get; set; } = true;
    /// <summary>Include CD-i application stub for VCD playback on CD-i players.</summary>
    public bool IncludeCdiApplication { get; set; }
    /// <summary>
    /// Mark the ISO as CD-ROM XA compliant by injecting the "CD-XA001" signature
    /// in the PVD application-use area (offset 1024). Automatically enabled for VCD/SVCD.
    /// </summary>
    public bool CdXaMode { get; set; }

    // ----- DVD-Video / Blu-ray / BDAV authoring properties -----
    /// <summary>DVD-Video authoring job configuration. When set, triggers DVD-Video structure creation.</summary>
    public DvdAuthoringJob? DvdAuthoring { get; set; }
    /// <summary>Blu-ray authoring job configuration. When set, triggers BDMV structure creation.</summary>
    public BluRayAuthoringJob? BluRayAuthoring { get; set; }
    /// <summary>
    /// BDAV (Blu-ray Audio/Visual recording) authoring job configuration.
    /// When set, triggers BDAV structure creation instead of BDMV.
    /// BDAV is used for BD-R/BD-RE recording format per BDA specification Part 3-2.
    /// </summary>
    public BluRayAuthoringJob? BdavAuthoring { get; set; }

    public BuildStatus Status { get; set; } = BuildStatus.Idle;
    public DateTime StartedAt { get; set; }
    public DateTime FinishedAt { get; set; }
}
