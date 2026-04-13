// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenBurningSuite.Models;

/// <summary>
/// Detected sector format of a raw IMG image file.
/// IMG files are headerless raw sector dumps — the sector size is inferred
/// from the file size and common sector size divisibility.
/// </summary>
public enum ImgSectorFormat
{
    /// <summary>2048-byte cooked ISO 9660 sectors (Mode 1 user data only).</summary>
    Cooked2048 = 2048,
    /// <summary>2336-byte Mode 2 sectors (no sync/header, includes subheader).</summary>
    Mode2_2336 = 2336,
    /// <summary>2352-byte raw sectors (full sector with sync, header, ECC/EDC).</summary>
    Raw2352 = 2352,
    /// <summary>2448-byte raw sectors with 96-byte interleaved subchannel data.</summary>
    RawSub2448 = 2448,
    /// <summary>2056-byte sectors (2048 user data + 8-byte subheader).</summary>
    Mode2Form1_2056 = 2056,
    /// <summary>Unknown or undetectable sector format.</summary>
    Unknown = 0
}

/// <summary>
/// Detected track mode of a raw IMG image, determined by examining
/// the sync pattern and mode byte in raw sectors.
/// </summary>
public enum ImgTrackMode
{
    /// <summary>Audio track (CD-DA — all 2352 bytes are PCM audio).</summary>
    Audio = 0,
    /// <summary>Mode 1 data track (16-byte header, 2048 user data, 288 ECC/EDC).</summary>
    Mode1 = 1,
    /// <summary>Mode 2 data track (16-byte header, 2336 user data, no ECC).</summary>
    Mode2 = 2,
    /// <summary>Mode 2 Form 1 (24-byte header+subheader, 2048 user data, 280 ECC/EDC).</summary>
    Mode2Form1 = 3,
    /// <summary>Mode 2 Form 2 (24-byte header+subheader, 2328 user data, 4 EDC).</summary>
    Mode2Form2 = 4,
    /// <summary>Unknown mode.</summary>
    Unknown = -1
}

/// <summary>
/// Represents a track detected within a raw IMG file.
/// For single-track images this contains the entire file.
/// </summary>
public class ImgTrack
{
    /// <summary>Track number (1-based).</summary>
    public int TrackNumber { get; set; } = 1;

    /// <summary>Detected sector format.</summary>
    public ImgSectorFormat SectorFormat { get; set; }

    /// <summary>Detected track mode.</summary>
    public ImgTrackMode Mode { get; set; }

    /// <summary>Sector size in bytes.</summary>
    public int SectorSize => (int)SectorFormat;

    /// <summary>Start offset within the IMG file (bytes).</summary>
    public long FileOffset { get; set; }

    /// <summary>Length of track data in bytes.</summary>
    public long DataLength { get; set; }

    /// <summary>Number of sectors in this track.</summary>
    public long SectorCount => SectorSize > 0 ? DataLength / SectorSize : 0;

    /// <summary>Start LBA on disc.</summary>
    public long StartLba { get; set; }

    /// <summary>Whether sectors are raw (2352+ bytes).</summary>
    public bool IsRawSector => SectorSize >= 2352;

    /// <summary>Whether this track has subchannel data.</summary>
    public bool HasSubchannel => SectorSize >= 2448;

    /// <summary>Whether this is an audio track.</summary>
    public bool IsAudio => Mode == ImgTrackMode.Audio;

    /// <summary>Whether this is a data track.</summary>
    public bool IsData => Mode != ImgTrackMode.Audio;

    /// <summary>
    /// User data size per sector. 
    /// Audio = 2352, Mode1 raw = 2048, Mode2 raw = 2336, Cooked = sector size.
    /// </summary>
    public int UserDataSize => Mode switch
    {
        ImgTrackMode.Audio => 2352,
        ImgTrackMode.Mode1 when IsRawSector => 2048,
        ImgTrackMode.Mode2 when IsRawSector => 2336,
        ImgTrackMode.Mode2Form1 when IsRawSector => 2048,
        ImgTrackMode.Mode2Form2 when IsRawSector => 2328,
        _ => SectorSize
    };

    /// <summary>
    /// Offset within a raw sector where user data begins.
    /// Audio = 0, Mode 1 = 16, Mode 2 = 16, Mode 2 Form 1/2 = 24.
    /// </summary>
    public int UserDataOffset => Mode switch
    {
        ImgTrackMode.Audio => 0,
        ImgTrackMode.Mode1 when IsRawSector => 16,
        ImgTrackMode.Mode2 when IsRawSector => 16,
        ImgTrackMode.Mode2Form1 when IsRawSector => 24,
        ImgTrackMode.Mode2Form2 when IsRawSector => 24,
        _ => 0
    };

    /// <summary>Base sector size excluding subchannel data.</summary>
    public int BaseSectorSize => HasSubchannel ? SectorSize - 96 : SectorSize;
}

/// <summary>
/// Represents a parsed raw disc image file (.img).
///
/// IMG files are headerless raw sector dumps without metadata.
/// The sector format and track mode are detected by examining the file size
/// and (for raw sectors) the sync pattern and mode byte in the sector data.
///
/// IMG files may be:
/// - Standalone raw dumps (similar to ISO but potentially with raw sectors)
/// - Companion files to CloneCD .ccd descriptors (always 2352-byte raw sectors)
/// - Companion files to cdrdao .toc descriptors
/// </summary>
public class ImgImage
{
    /// <summary>Full path to the IMG file.</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>Total file size in bytes.</summary>
    public long FileSize { get; set; }

    /// <summary>Detected sector format for the primary track.</summary>
    public ImgSectorFormat SectorFormat { get; set; }

    /// <summary>Detected tracks within the image.</summary>
    public List<ImgTrack> Tracks { get; set; } = new();

    /// <summary>Whether a companion .ccd descriptor file exists.</summary>
    public bool HasCcdCompanion { get; set; }

    /// <summary>Path to the companion .ccd file, if present.</summary>
    public string CcdFilePath { get; set; } = string.Empty;

    /// <summary>Whether a companion .cue sheet file exists.</summary>
    public bool HasCueCompanion { get; set; }

    /// <summary>Path to the companion .cue file, if present.</summary>
    public string CueFilePath { get; set; } = string.Empty;

    /// <summary>Whether a companion .sub subchannel file exists.</summary>
    public bool HasSubFile { get; set; }

    /// <summary>Path to the companion .sub file, if present.</summary>
    public string SubFilePath { get; set; } = string.Empty;

    /// <summary>Whether the image was parsed successfully.</summary>
    public bool IsValid { get; set; }

    /// <summary>Error message if parsing failed, empty otherwise.</summary>
    public string ErrorMessage { get; set; } = string.Empty;

    // -----------------------------------------------------------------------
    //  Computed properties
    // -----------------------------------------------------------------------

    /// <summary>Total number of tracks.</summary>
    public int TrackCount => Tracks.Count;

    /// <summary>Number of audio tracks.</summary>
    public int AudioTrackCount => Tracks.Count(t => t.IsAudio);

    /// <summary>Number of data tracks.</summary>
    public int DataTrackCount => Tracks.Count(t => t.IsData);

    /// <summary>Whether the disc contains both audio and data tracks.</summary>
    public bool IsMixedMode => AudioTrackCount > 0 && DataTrackCount > 0;

    /// <summary>Total sector count across all tracks.</summary>
    public long TotalSectorCount => Tracks.Sum(t => t.SectorCount);

    /// <summary>Total user data size in bytes.</summary>
    public long TotalUserDataSize => Tracks.Sum(t => t.SectorCount * (long)t.UserDataSize);

    /// <summary>Total disc size in bytes.</summary>
    public long TotalDiscSize => Tracks.Sum(t => t.DataLength);

    /// <summary>Total audio duration in seconds.</summary>
    public double TotalAudioDurationSeconds =>
        Tracks.Where(t => t.IsAudio).Sum(t => t.SectorCount / 75.0);

    /// <summary>Gets the first data track, or null if none exist.</summary>
    public ImgTrack? FirstDataTrack => Tracks.FirstOrDefault(t => t.IsData);

    /// <summary>Gets the largest data track by data length.</summary>
    public ImgTrack? LargestDataTrack =>
        Tracks.Where(t => t.IsData).OrderByDescending(t => t.DataLength).FirstOrDefault();

    /// <summary>
    /// Sector size string for display.
    /// </summary>
    public string SectorFormatString => SectorFormat switch
    {
        ImgSectorFormat.Cooked2048 => "2048B (Cooked ISO)",
        ImgSectorFormat.Mode2_2336 => "2336B (Mode 2)",
        ImgSectorFormat.Raw2352 => "2352B (Raw)",
        ImgSectorFormat.RawSub2448 => "2448B (Raw + Subchannel)",
        ImgSectorFormat.Mode2Form1_2056 => "2056B (Mode 2 Form 1)",
        _ => "Unknown"
    };

    /// <summary>Generates a diagnostic summary of the IMG image.</summary>
    public string GetDiagnosticSummary()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"IMG Image: {FilePath}");
        sb.AppendLine($"File Size: {FileSize:N0} bytes");
        sb.AppendLine($"Sector Format: {SectorFormatString}");
        sb.AppendLine($"Tracks: {TrackCount} ({DataTrackCount} data, {AudioTrackCount} audio)");
        sb.AppendLine($"Total Sectors: {TotalSectorCount:N0}");
        sb.AppendLine($"Companion CCD: {(HasCcdCompanion ? CcdFilePath : "None")}");
        sb.AppendLine($"Companion CUE: {(HasCueCompanion ? CueFilePath : "None")}");
        sb.AppendLine($"Companion SUB: {(HasSubFile ? SubFilePath : "None")}");

        foreach (var track in Tracks)
        {
            string modeStr = track.Mode switch
            {
                ImgTrackMode.Audio => "Audio",
                ImgTrackMode.Mode1 => "Mode1",
                ImgTrackMode.Mode2 => "Mode2",
                ImgTrackMode.Mode2Form1 => "Mode2/Form1",
                ImgTrackMode.Mode2Form2 => "Mode2/Form2",
                _ => "Unknown"
            };
            sb.AppendLine($"  Track {track.TrackNumber}: {modeStr} {track.SectorSize}B " +
                $"LBA={track.StartLba} Sectors={track.SectorCount} " +
                $"Offset=0x{track.FileOffset:X}");
        }

        return sb.ToString();
    }
}
