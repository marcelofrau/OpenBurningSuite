// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

namespace OpenBurningSuite.Helpers;

/// <summary>
/// Defines the recommended read/write settings for a specific gaming console's disc format.
/// Each preset specifies sector size, subchannel mode, speed, error recovery, and write mode
/// optimized for accurate reading and burning of that system's discs.
/// </summary>
public class GamingPreset
{
    /// <summary>Display name of the gaming system (e.g., "PlayStation 1").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Sector size in bytes (2048 for data, 2352 for raw).</summary>
    public int SectorSize { get; set; } = 2048;

    /// <summary>Subchannel extraction mode ("None", "RW", "RW_RAW", "P-W").</summary>
    public string SubchannelMode { get; set; } = "None";

    /// <summary>Recommended read speed multiplier.</summary>
    public string ReadSpeed { get; set; } = "Max";

    /// <summary>Error recovery mode ("Yes", "No", "Abort").</summary>
    public string ErrorRecovery { get; set; } = "Yes";

    /// <summary>Number of retry attempts for failed sectors.</summary>
    public int Retries { get; set; } = 3;

    /// <summary>Whether to enable audio paranoia mode for audio tracks.</summary>
    public bool AudioParanoia { get; set; }

    /// <summary>Human-readable description of the preset and its rationale.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Write speed recommendation for burning gaming discs.</summary>
    public string WriteSpeed { get; set; } = "Auto";

    /// <summary>Write mode recommendation for burning gaming discs.</summary>
    public string WriteMode { get; set; } = "DAO (Disc At Once)";

    /// <summary>Whether the disc should be finalized after burning.</summary>
    public bool CloseDisc { get; set; } = true;

    /// <summary>
    /// Recommended output format for reading/copying this disc type.
    /// Must match one of the <see cref="FormatHelper.ReadOutputFormats"/> values.
    /// Defaults to "BIN/CUE" for CD-based raw reads, "ISO" for DVD/BD.
    /// </summary>
    public string OutputFormat { get; set; } = "BIN/CUE";
}
