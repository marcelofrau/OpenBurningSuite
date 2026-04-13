// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenBurningSuite.Models;

/// <summary>CDI image format version identifiers.</summary>
public enum CdiVersion
{
    /// <summary>CDI format version 2.0 (DiscJuggler 2).</summary>
    Version2 = 0,
    /// <summary>CDI format version 3.0 (DiscJuggler 3).</summary>
    Version3 = 1,
    /// <summary>CDI format version 3.5 (DiscJuggler 3.5+).</summary>
    Version35 = 2,
    /// <summary>CDI format version 4.0 (DiscJuggler 4).</summary>
    Version4 = 3,
    /// <summary>Unrecognized or unsupported CDI version.</summary>
    Unknown = -1
}

/// <summary>Track mode in a CDI image.</summary>
public enum CdiTrackMode
{
    /// <summary>Audio track (CD-DA, 2352 bytes/sector).</summary>
    Audio = 0,
    /// <summary>Mode 1 data track.</summary>
    Mode1 = 1,
    /// <summary>Mode 2 data track (XA form).</summary>
    Mode2 = 2
}

/// <summary>Subchannel data mode for a CDI track.</summary>
public enum CdiSubchannelMode
{
    /// <summary>No subchannel data stored.</summary>
    None = 0,
    /// <summary>P and Q subchannel data (16 bytes/sector appended).</summary>
    PQ = 1,
    /// <summary>Full raw subchannel data — P through W (96 bytes/sector appended).</summary>
    Raw = 2
}

/// <summary>Represents a single track within a CDI session.</summary>
public class CdiTrack
{
    /// <summary>Track number (1-based).</summary>
    public int TrackNumber { get; set; }

    /// <summary>Track mode (Audio, Mode1, Mode2).</summary>
    public CdiTrackMode Mode { get; set; }

    /// <summary>
    /// Sector size in bytes. Common values:
    /// 2048 (cooked Mode 1), 2336 (cooked Mode 2), 2352 (raw),
    /// 2368 (raw + PQ subchannel), 2448 (raw + full subchannel).
    /// </summary>
    public int SectorSize { get; set; }

    /// <summary>Byte offset in the CDI file where this track's sector data begins.</summary>
    public long FileOffset { get; set; }

    /// <summary>Total length of sector data in bytes.</summary>
    public long TotalLength { get; set; }

    /// <summary>Number of sectors in this track.</summary>
    public long SectorCount { get; set; }

    /// <summary>Pregap length in sectors (typically 150 = 2 seconds for CD).</summary>
    public int Pregap { get; set; }

    /// <summary>Session to which this track belongs (1-based).</summary>
    public int SessionNumber { get; set; }

    /// <summary>Starting LBA (Logical Block Address) of this track on disc.</summary>
    public long StartLba { get; set; }

    /// <summary>Subchannel data mode for this track.</summary>
    public CdiSubchannelMode SubchannelMode { get; set; } = CdiSubchannelMode.None;

    /// <summary>
    /// ISRC (International Standard Recording Code) for this track, if present.
    /// 12-character alphanumeric code per ISO 3901.
    /// </summary>
    public string Isrc { get; set; } = string.Empty;

    /// <summary>
    /// Whether this track is Mode 2 Form 1 (2048-byte user data within a Mode 2 sector).
    /// Only meaningful when <see cref="Mode"/> is <see cref="CdiTrackMode.Mode2"/>.
    /// </summary>
    public bool IsMode2Form1 { get; set; }

    /// <summary>
    /// Whether this track is Mode 2 Form 2 (2328-byte user data within a Mode 2 sector).
    /// Only meaningful when <see cref="Mode"/> is <see cref="CdiTrackMode.Mode2"/>.
    /// </summary>
    public bool IsMode2Form2 { get; set; }

    /// <summary>
    /// CD control field (4 bits). Encodes track attributes per Red Book:
    /// bit 0: pre-emphasis, bit 1: copy permitted,
    /// bit 2: data track (vs. audio), bit 3: four-channel audio.
    /// </summary>
    public byte Control { get; set; }

    /// <summary>
    /// Whether this track contains raw sector data (2352+ bytes) that needs
    /// header/ECC stripping to extract user data.
    /// </summary>
    public bool IsRawSector => SectorSize >= 2352;

    /// <summary>
    /// The base sector size excluding any appended subchannel data.
    /// For sectors with subchannel, this strips the 16-byte (PQ) or 96-byte (Raw) suffix.
    /// </summary>
    public int BaseSectorSize => SubchannelMode switch
    {
        CdiSubchannelMode.PQ => SectorSize - 16,
        CdiSubchannelMode.Raw => SectorSize - 96,
        _ => SectorSize
    };

    /// <summary>
    /// Size of subchannel data per sector in bytes.
    /// 0 for no subchannel, 16 for PQ, 96 for full raw.
    /// </summary>
    public int SubchannelSize => SubchannelMode switch
    {
        CdiSubchannelMode.PQ => 16,
        CdiSubchannelMode.Raw => 96,
        _ => 0
    };

    /// <summary>
    /// Size of user data per sector. For raw sectors, depends on mode:
    /// Audio=2352, Mode1=2048, Mode2=2336 (or 2048 for Form 1, 2328 for Form 2).
    /// For cooked sectors, equals <see cref="BaseSectorSize"/>.
    /// </summary>
    public int UserDataSize
    {
        get
        {
            int baseSz = BaseSectorSize;
            return baseSz switch
            {
                2352 when Mode == CdiTrackMode.Audio => 2352,
                2352 when Mode == CdiTrackMode.Mode1 => 2048,
                2352 when Mode == CdiTrackMode.Mode2 && IsMode2Form1 => 2048,
                2352 when Mode == CdiTrackMode.Mode2 && IsMode2Form2 => 2328,
                2352 when Mode == CdiTrackMode.Mode2 => 2336,
                2336 => 2336,
                _ => baseSz
            };
        }
    }

    /// <summary>
    /// Offset within a raw sector where user data begins.
    /// Audio: 0, Mode1: 16, Mode2: 24.
    /// </summary>
    public int UserDataOffset
    {
        get
        {
            int baseSz = BaseSectorSize;
            return baseSz switch
            {
                2352 when Mode == CdiTrackMode.Audio => 0,
                2352 when Mode == CdiTrackMode.Mode1 => 16,
                2352 when Mode == CdiTrackMode.Mode2 => 24,
                _ => 0
            };
        }
    }
}

/// <summary>Represents a session within a CDI image.</summary>
public class CdiSession
{
    /// <summary>Session number (1-based).</summary>
    public int SessionNumber { get; set; }

    /// <summary>Tracks in this session.</summary>
    public List<CdiTrack> Tracks { get; set; } = new();

    /// <summary>
    /// Media Catalog Number (MCN/UPC/EAN) for this session, if present.
    /// 13-digit numeric code per UPC/EAN standard.
    /// </summary>
    public string Mcn { get; set; } = string.Empty;
}

/// <summary>
/// Represents a parsed DiscJuggler CDI disc image.
/// Contains session and track layout, format version, and metadata.
/// </summary>
public class CdiImage
{
    /// <summary>CDI format version.</summary>
    public CdiVersion Version { get; set; } = CdiVersion.Unknown;

    /// <summary>Full path to the CDI file.</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>Total file size in bytes.</summary>
    public long FileSize { get; set; }

    /// <summary>Sessions in this image.</summary>
    public List<CdiSession> Sessions { get; set; } = new();

    /// <summary>Total number of tracks across all sessions.</summary>
    public int TotalTrackCount => Sessions.Sum(s => s.Tracks.Count);

    /// <summary>Total number of sessions.</summary>
    public int SessionCount => Sessions.Count;

    /// <summary>Whether the image was parsed successfully.</summary>
    public bool IsValid { get; set; }

    /// <summary>Error message if parsing failed, empty otherwise.</summary>
    public string ErrorMessage { get; set; } = string.Empty;

    /// <summary>Version string for display purposes.</summary>
    public string VersionString => Version switch
    {
        CdiVersion.Version2 => "CDI 2.0",
        CdiVersion.Version3 => "CDI 3.0",
        CdiVersion.Version35 => "CDI 3.5",
        CdiVersion.Version4 => "CDI 4.0",
        _ => "Unknown"
    };

    /// <summary>
    /// Gets whether any track in the image contains subchannel data.
    /// </summary>
    public bool HasSubchannelData =>
        Sessions.SelectMany(s => s.Tracks).Any(t => t.SubchannelMode != CdiSubchannelMode.None);

    /// <summary>
    /// Gets all tracks that have ISRC codes assigned.
    /// </summary>
    public IEnumerable<CdiTrack> TracksWithIsrc =>
        Sessions.SelectMany(s => s.Tracks).Where(t => !string.IsNullOrEmpty(t.Isrc));

    /// <summary>
    /// Gets the MCN (Media Catalog Number) from the first session that has one.
    /// </summary>
    public string Mcn =>
        Sessions.FirstOrDefault(s => !string.IsNullOrEmpty(s.Mcn))?.Mcn ?? string.Empty;

    /// <summary>
    /// Gets a diagnostic summary of the parsed image for logging/display purposes.
    /// </summary>
    public string DiagnosticSummary
    {
        get
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"CDI Version: {VersionString}");
            sb.AppendLine($"File: {FilePath}");
            sb.AppendLine($"File Size: {FileSize:N0} bytes");
            sb.AppendLine($"Sessions: {SessionCount}, Tracks: {TotalTrackCount}");
            if (!string.IsNullOrEmpty(Mcn))
                sb.AppendLine($"MCN: {Mcn}");

            foreach (var session in Sessions)
            {
                sb.AppendLine($"  Session {session.SessionNumber} ({session.Tracks.Count} tracks):");
                if (!string.IsNullOrEmpty(session.Mcn))
                    sb.AppendLine($"    MCN: {session.Mcn}");
                foreach (var track in session.Tracks)
                {
                    string modeStr = track.Mode switch
                    {
                        CdiTrackMode.Audio => "Audio",
                        CdiTrackMode.Mode1 => "Mode1",
                        CdiTrackMode.Mode2 when track.IsMode2Form1 => "Mode2/Form1",
                        CdiTrackMode.Mode2 when track.IsMode2Form2 => "Mode2/Form2",
                        CdiTrackMode.Mode2 => "Mode2",
                        _ => "Unknown"
                    };
                    string subStr = track.SubchannelMode != CdiSubchannelMode.None
                        ? $" +{track.SubchannelMode}"
                        : "";
                    string isrcStr = !string.IsNullOrEmpty(track.Isrc)
                        ? $" ISRC={track.Isrc}"
                        : "";
                    sb.AppendLine($"    Track {track.TrackNumber}: {modeStr} {track.SectorSize}B " +
                        $"LBA={track.StartLba} Sectors={track.SectorCount} " +
                        $"Pregap={track.Pregap}{subStr}{isrcStr}");
                }
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Gets all data tracks (Mode1 or Mode2) across all sessions.
    /// Useful for extracting ISO/data content.
    /// </summary>
    public IEnumerable<CdiTrack> DataTracks =>
        Sessions.SelectMany(s => s.Tracks).Where(t => t.Mode != CdiTrackMode.Audio);

    /// <summary>
    /// Gets all audio tracks across all sessions.
    /// </summary>
    public IEnumerable<CdiTrack> AudioTracks =>
        Sessions.SelectMany(s => s.Tracks).Where(t => t.Mode == CdiTrackMode.Audio);

    /// <summary>
    /// Gets all tracks across all sessions in order.
    /// </summary>
    public IEnumerable<CdiTrack> AllTracks =>
        Sessions.SelectMany(s => s.Tracks);

    /// <summary>
    /// Gets the total user data size across all tracks in bytes.
    /// </summary>
    public long TotalUserDataSize =>
        Sessions.SelectMany(s => s.Tracks).Sum(t => t.SectorCount * (long)t.UserDataSize);

    /// <summary>
    /// Gets whether this is a multi-session disc image.
    /// </summary>
    public bool IsMultiSession => SessionCount > 1;

    /// <summary>
    /// Gets the total disc size including sector headers, ECC/EDC, and subchannel data.
    /// </summary>
    public long TotalDiscSize =>
        Sessions.SelectMany(s => s.Tracks).Sum(t => t.TotalLength);

    /// <summary>
    /// Gets whether the disc contains both audio and data tracks (mixed-mode).
    /// </summary>
    public bool IsMixedMode =>
        Sessions.SelectMany(s => s.Tracks).Any(t => t.Mode == CdiTrackMode.Audio) &&
        Sessions.SelectMany(s => s.Tracks).Any(t => t.Mode != CdiTrackMode.Audio);

    /// <summary>
    /// Gets the total duration of all audio tracks in seconds.
    /// Computed from sector count and the CD-DA frame rate (75 sectors/second).
    /// </summary>
    public double TotalAudioDurationSeconds =>
        AudioTracks.Sum(t => t.SectorCount / 75.0);

    /// <summary>
    /// Retrieves a track by its 1-based track number across all sessions.
    /// </summary>
    /// <param name="trackNumber">1-based track number.</param>
    /// <returns>The matching track, or null if not found.</returns>
    public CdiTrack? GetTrackByNumber(int trackNumber) =>
        Sessions.SelectMany(s => s.Tracks).FirstOrDefault(t => t.TrackNumber == trackNumber);

    /// <summary>
    /// Gets the first data track in the image, or null if none exist.
    /// Useful for extracting ISO/filesystem content from the primary data track.
    /// </summary>
    public CdiTrack? FirstDataTrack =>
        Sessions.SelectMany(s => s.Tracks).FirstOrDefault(t => t.Mode != CdiTrackMode.Audio);

    /// <summary>
    /// Gets the largest data track by total length. This is typically the main
    /// data track in Dreamcast GD-ROM and other gaming disc images.
    /// </summary>
    public CdiTrack? LargestDataTrack =>
        DataTracks.OrderByDescending(t => t.TotalLength).FirstOrDefault();
}
