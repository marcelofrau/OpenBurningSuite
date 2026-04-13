// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;

namespace OpenBurningSuite.Models;

public class BurnJob
{
    public string ImagePath { get; set; } = string.Empty;
    /// <summary>
    /// Optional path to the CUE sheet file for BIN/CUE image burning.
    /// When set and using SAO/DAO write mode, the CUE sheet is used to
    /// generate the MMC CUE sheet for multi-track audio disc burning.
    /// </summary>
    public string CuePath { get; set; } = string.Empty;
    public string DevicePath { get; set; } = string.Empty;
    public string WriteMode { get; set; } = "TAO (Track At Once)";
    public string WriteSpeed { get; set; } = "Auto";
    public int Copies { get; set; } = 1;
    public bool EjectAfterBurn { get; set; } = true;
    /// <summary>Open the file explorer to show disc content when burn completes successfully.</summary>
    public bool ShowDiscContentAfterBurn { get; set; }
    public bool VerifyAfterBurn { get; set; }
    public bool SimulateOnly { get; set; }
    public bool BufferUnderrunProtection { get; set; } = true;
    public bool Overburn { get; set; }
    public long OverburnSizeMb { get; set; }
    public bool CloseDisc { get; set; } = true;
    public string MultiSessionMode { get; set; } = "Close (Single Session)";
    public string ForceMediaType { get; set; } = string.Empty;
    public int LayerBreakPosition { get; set; }
    public string BdRMode { get; set; } = "SRM (Sequential Recording)";

    // ----- Gaming patches & special format properties -----
    /// <summary>Apply LibCrypt copy protection patching for PS1 games using an SBI file.</summary>
    public bool LibCryptPatching { get; set; }
    /// <summary>Path to the LibCrypt .sbi subchannel patch file for PS1 games.</summary>
    public string SbiFilePath { get; set; } = string.Empty;
    /// <summary>Apply Sega Dreamcast GDI/CDI disc patching.</summary>
    public bool DreamcastGdiPatching { get; set; }
    /// <summary>Apply region-free patching to remove region restrictions.</summary>
    public bool RegionFreePatching { get; set; }
    /// <summary>Apply boot sector patching for compatibility with modified firmware.</summary>
    public bool BootSectorPatching { get; set; }
    /// <summary>Apply ESR patch to PS2 DVD images (injects DVD-Video structure for MechaCon bypass).</summary>
    public bool EsrPatching { get; set; }
    /// <summary>Apply Master Disc patch to PS2 images for MechaPwn DEX consoles.</summary>
    public bool MasterDiscPatching { get; set; }
    /// <summary>Region for Master Disc patch: "NTSC-J", "NTSC-U", "PAL", or "WORLD".</summary>
    public string MasterDiscRegion { get; set; } = "NTSC-U";
    /// <summary>Apply PSX 80 Minute patch (appends dummy sectors for 80-min CD-R compatibility).</summary>
    public bool Psx80MinutePatching { get; set; }
    /// <summary>Apply PSX Undither patch (removes GPU dithering from PS1 games).</summary>
    public bool PsxUndither { get; set; }

    // ----- Build on-the-fly properties -----
    /// <summary>
    /// When true, the source is a set of files/folders that will be built into
    /// a temporary ISO image and burned to disc in a single operation.
    /// When false (default), ImagePath must point to a pre-existing disc image file.
    /// </summary>
    public bool OnTheFly { get; set; }
    /// <summary>
    /// Source folder path for on-the-fly burning. All contents of this folder
    /// (including subdirectories) will be included in the disc image.
    /// Used when <see cref="OnTheFly"/> is true.
    /// </summary>
    public string SourceFolder { get; set; } = string.Empty;
    /// <summary>
    /// Individual source files and/or folders to include in the on-the-fly disc image.
    /// Each entry can be a file path or a directory path. When directories are added,
    /// their full contents (including subdirectories) are included.
    /// Used when <see cref="OnTheFly"/> is true and <see cref="SourceFolder"/> is empty.
    /// </summary>
    public List<string> SourceFiles { get; set; } = new();
    /// <summary>
    /// Volume label for the on-the-fly disc image.
    /// ISO 9660 limits this to 32 uppercase characters (ECMA-119 §8.4.1).
    /// </summary>
    public string VolumeLabel { get; set; } = string.Empty;
    /// <summary>
    /// Filesystem to use for on-the-fly disc image building.
    /// Values: "ISO 9660", "Joliet", "UDF", "ISO 9660 + Joliet", "ISO 9660 + UDF".
    /// </summary>
    public string FileSystem { get; set; } = "ISO 9660 + Joliet";

    /// <summary>
    /// Byte offset into <see cref="ImagePath"/> where the burn data begins.
    /// Used for direct burning of non-native image formats (MDF, CDI, etc.)
    /// where sector data starts at a non-zero offset within the file.
    /// Default: 0 (start of file).
    /// </summary>
    public long ImageStartOffset { get; set; }

    public BurnStatus Status { get; set; } = BurnStatus.Idle;
    public DateTime StartedAt { get; set; }
    public DateTime FinishedAt { get; set; }
}
