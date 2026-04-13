// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenBurningSuite.Models;

/// <summary>
/// Medium type identifiers stored in the MDS header.
/// Values match the Alcohol 120% MDS format specification.
/// </summary>
public enum MdsMediumType : ushort
{
    /// <summary>CD-ROM (pressed).</summary>
    CdRom = 0x00,
    /// <summary>CD-R (recordable).</summary>
    CdR = 0x01,
    /// <summary>CD-RW (rewritable).</summary>
    CdRw = 0x02,
    /// <summary>DVD-ROM (pressed).</summary>
    DvdRom = 0x10,
    /// <summary>DVD-R (recordable).</summary>
    DvdMinusR = 0x12
}

/// <summary>
/// Track mode identifiers as stored in the MDS track block.
/// Lower nibble encodes the mode; upper nibble is ignored by Alcohol.
/// </summary>
public enum MdsTrackMode : byte
{
    /// <summary>Mode 2 data (2336-byte sectors).</summary>
    Mode2 = 0x00,
    /// <summary>Audio track (CD-DA, 2352-byte sectors).</summary>
    Audio = 0x01,
    /// <summary>Mode 1 data (2048-byte sectors).</summary>
    Mode1 = 0x02,
    /// <summary>Mode 2 (alternate).</summary>
    Mode2Alt = 0x03,
    /// <summary>Mode 2 Form 1 (2048-byte user data).</summary>
    Mode2Form1 = 0x04,
    /// <summary>Mode 2 Form 2 (2328-byte user data).</summary>
    Mode2Form2 = 0x05,
    /// <summary>Mode 2 (alternate 2).</summary>
    Mode2Alt2 = 0x07
}

/// <summary>
/// Subchannel mode for MDS tracks.
/// </summary>
public enum MdsSubchannelMode : byte
{
    /// <summary>No subchannel data stored.</summary>
    None = 0x00,
    /// <summary>96-byte interleaved P-W subchannel data.</summary>
    PwInterleaved = 0x08
}

/// <summary>
/// MDS special point values used in lead-in/lead-out track entries.
/// These mirror the Q-subchannel POINT field from the disc TOC.
/// </summary>
public enum MdsPoint : byte
{
    /// <summary>Info about first track in session.</summary>
    TrackFirst = 0xA0,
    /// <summary>Info about last track in session.</summary>
    TrackLast = 0xA1,
    /// <summary>Info about session lead-out.</summary>
    TrackLeadOut = 0xA2
}

/// <summary>
/// Represents the extra block associated with a regular track entry.
/// Contains pregap and track length in sectors.
/// </summary>
public class MdsTrackExtraBlock
{
    /// <summary>Number of sectors in the pregap.</summary>
    public uint Pregap { get; set; }

    /// <summary>Number of sectors in the track data (excluding pregap).</summary>
    public uint Length { get; set; }
}

/// <summary>
/// Represents the footer block for a track, containing the reference
/// to the data file (MDF) that stores this track's sector data.
/// </summary>
public class MdsFooterBlock
{
    /// <summary>Offset within the MDS file to the data filename string.</summary>
    public uint FilenameOffset { get; set; }

    /// <summary>
    /// Whether the filename is stored as a wide-character (UTF-16LE) string.
    /// Non-zero means wide-char, zero means ASCII/UTF-8.
    /// </summary>
    public bool IsWideCharFilename { get; set; }

    /// <summary>The resolved data file name (typically *.mdf).</summary>
    public string DataFileName { get; set; } = string.Empty;
}

/// <summary>
/// Represents a single track block within an MDS session.
/// Encompasses both lead-in/TOC descriptor entries and regular data/audio tracks.
/// </summary>
public class MdsTrackBlock
{
    /// <summary>Track mode (Audio, Mode1, Mode2, etc.).</summary>
    public MdsTrackMode Mode { get; set; }

    /// <summary>Subchannel mode for this track.</summary>
    public MdsSubchannelMode SubchannelMode { get; set; }

    /// <summary>
    /// ADR/CTL combined byte from Q-subchannel.
    /// Upper nibble = ADR, lower nibble = CTL.
    /// </summary>
    public byte AdrCtl { get; set; }

    /// <summary>Track number field from TOC (TNO).</summary>
    public byte Tno { get; set; }

    /// <summary>
    /// Point field. For regular track entries this equals the track number.
    /// For special entries, values are 0xA0 (first track), 0xA1 (last track),
    /// 0xA2 (lead-out).
    /// </summary>
    public byte Point { get; set; }

    /// <summary>MSF minute from Q-subchannel.</summary>
    public byte Min { get; set; }

    /// <summary>MSF second from Q-subchannel.</summary>
    public byte Sec { get; set; }

    /// <summary>MSF frame from Q-subchannel.</summary>
    public byte Frame { get; set; }

    /// <summary>Zero field from Q-subchannel.</summary>
    public byte Zero { get; set; }

    /// <summary>PMSF minute (absolute time for track start).</summary>
    public byte PMin { get; set; }

    /// <summary>PMSF second.</summary>
    public byte PSec { get; set; }

    /// <summary>PMSF frame.</summary>
    public byte PFrame { get; set; }

    /// <summary>
    /// Offset within the MDS file to this track's extra block
    /// (pregap/length). Zero if no extra block.
    /// </summary>
    public uint ExtraOffset { get; set; }

    /// <summary>
    /// Sector size in bytes. Includes subchannel data if present.
    /// Common values: 2048, 2336, 2352, 2448.
    /// </summary>
    public ushort SectorSize { get; set; }

    /// <summary>Track start sector (PLBA — physical LBA).</summary>
    public uint StartSector { get; set; }

    /// <summary>
    /// Byte offset within the MDF data file where this track's sector data begins.
    /// 64-bit to support large disc images.
    /// </summary>
    public ulong StartOffset { get; set; }

    /// <summary>Number of data file fragments for this track.</summary>
    public uint NumberOfFiles { get; set; }

    /// <summary>Offset within the MDS file to the footer block(s).</summary>
    public uint FooterOffset { get; set; }

    /// <summary>Extra block (pregap + track length), if present.</summary>
    public MdsTrackExtraBlock? ExtraBlock { get; set; }

    /// <summary>Footer block(s) describing the data file reference.</summary>
    public List<MdsFooterBlock> Footers { get; set; } = new();

    /// <summary>Whether this is a regular track entry (not a TOC lead-in descriptor).</summary>
    public bool IsRegularTrack => Point > 0 && Point < 0xA0;

    /// <summary>Whether this track entry is a special point (first/last/lead-out).</summary>
    public bool IsSpecialPoint => Point >= 0xA0;

    /// <summary>
    /// The effective sector data size excluding subchannel.
    /// If subchannel is PW interleaved (96 bytes), subtracts 96 from SectorSize.
    /// </summary>
    public int MainSectorSize => SubchannelMode == MdsSubchannelMode.PwInterleaved
        ? SectorSize - 96
        : SectorSize;

    /// <summary>Size of subchannel data per sector in bytes (0 or 96).</summary>
    public int SubchannelSize => SubchannelMode == MdsSubchannelMode.PwInterleaved ? 96 : 0;

    /// <summary>
    /// The total number of sectors for this track (pregap + data).
    /// Only meaningful for regular track entries with an extra block.
    /// </summary>
    public long TotalSectors => ExtraBlock != null
        ? ExtraBlock.Pregap + ExtraBlock.Length
        : 0;

    /// <summary>
    /// Total byte length of sector data for this track in the MDF file.
    /// </summary>
    public long TotalLength => TotalSectors * SectorSize;

    /// <summary>
    /// The user data size per sector (excluding sync, header, ECC/EDC, subchannel).
    /// </summary>
    public int UserDataSize
    {
        get
        {
            int main = MainSectorSize;
            return main switch
            {
                2352 when (Mode == MdsTrackMode.Audio) => 2352,
                2352 when (Mode == MdsTrackMode.Mode1) => 2048,
                2352 when (Mode == MdsTrackMode.Mode2Form1) => 2048,
                2352 when (Mode == MdsTrackMode.Mode2Form2) => 2328,
                2352 when (Mode == MdsTrackMode.Mode2 || Mode == MdsTrackMode.Mode2Alt || Mode == MdsTrackMode.Mode2Alt2) => 2336,
                2048 => 2048,
                2336 => 2336,
                _ => main
            };
        }
    }

    /// <summary>
    /// Offset within a raw (2352-byte) sector where user data begins.
    /// Audio: 0, Mode1: 16, Mode2 variants: 24.
    /// </summary>
    public int UserDataOffset
    {
        get
        {
            int main = MainSectorSize;
            return main switch
            {
                2352 when (Mode == MdsTrackMode.Audio) => 0,
                2352 when (Mode == MdsTrackMode.Mode1) => 16,
                2352 when (Mode == MdsTrackMode.Mode2 || Mode == MdsTrackMode.Mode2Alt ||
                           Mode == MdsTrackMode.Mode2Alt2 || Mode == MdsTrackMode.Mode2Form1 ||
                           Mode == MdsTrackMode.Mode2Form2) => 24,
                _ => 0
            };
        }
    }

    /// <summary>Whether this track contains raw (2352+ byte) sectors.</summary>
    public bool IsRawSector => MainSectorSize >= 2352;

    /// <summary>Whether this is a data track (non-audio).</summary>
    public bool IsDataTrack => Mode != MdsTrackMode.Audio;
}

/// <summary>
/// Represents a session within an MDS disc image.
/// </summary>
public class MdsSession
{
    /// <summary>Session start sector (may be negative for pre-gap/lead-in, e.g. -150).</summary>
    public int SessionStart { get; set; }

    /// <summary>Session end sector.</summary>
    public int SessionEnd { get; set; }

    /// <summary>Session number (1-based).</summary>
    public ushort SessionNumber { get; set; }

    /// <summary>Total number of data blocks in this session (lead-in + regular).</summary>
    public byte NumAllBlocks { get; set; }

    /// <summary>Number of lead-in (non-track) data blocks.</summary>
    public byte NumNonTrackBlocks { get; set; }

    /// <summary>First track number in session.</summary>
    public ushort FirstTrack { get; set; }

    /// <summary>Last track number in session.</summary>
    public ushort LastTrack { get; set; }

    /// <summary>All track blocks in this session (including lead-in/TOC entries).</summary>
    public List<MdsTrackBlock> TrackBlocks { get; set; } = new();

    /// <summary>
    /// Gets only the regular (non-TOC) track entries from this session.
    /// These are the actual data/audio tracks.
    /// </summary>
    public IEnumerable<MdsTrackBlock> RegularTracks =>
        TrackBlocks.Where(t => t.IsRegularTrack);

    /// <summary>Number of regular tracks in this session.</summary>
    public int RegularTrackCount => RegularTracks.Count();
}

/// <summary>
/// Represents a fully parsed Alcohol 120% MDS/MDF disc image.
/// The MDS file contains the disc structure metadata (header, sessions, tracks),
/// while the MDF file contains the actual sector data.
/// </summary>
public class MdfImage
{
    /// <summary>Full path to the MDS (descriptor) file.</summary>
    public string MdsFilePath { get; set; } = string.Empty;

    /// <summary>Full path to the MDF (data) file.</summary>
    public string MdfFilePath { get; set; } = string.Empty;

    /// <summary>Size of the MDS file in bytes.</summary>
    public long MdsFileSize { get; set; }

    /// <summary>Size of the MDF file in bytes.</summary>
    public long MdfFileSize { get; set; }

    /// <summary>MDS format version (e.g. [1,3] for version 1.3).</summary>
    public byte[] Version { get; set; } = { 1, 0 };

    /// <summary>Medium type of the source disc.</summary>
    public MdsMediumType MediumType { get; set; }

    /// <summary>Sessions in the disc image.</summary>
    public List<MdsSession> Sessions { get; set; } = new();

    /// <summary>BCA (Burst Cutting Area) data for DVD, if present.</summary>
    public byte[] BcaData { get; set; } = Array.Empty<byte>();

    /// <summary>Whether the image was parsed successfully.</summary>
    public bool IsValid { get; set; }

    /// <summary>Error message if parsing failed, empty otherwise.</summary>
    public string ErrorMessage { get; set; } = string.Empty;

    /// <summary>Version string for display purposes.</summary>
    public string VersionString => $"MDS {Version[0]}.{Version[1]:X}";

    /// <summary>Total number of sessions.</summary>
    public int SessionCount => Sessions.Count;

    /// <summary>Total number of regular tracks across all sessions.</summary>
    public int TotalTrackCount => Sessions.Sum(s => s.RegularTrackCount);

    /// <summary>Whether this is a multi-session disc image.</summary>
    public bool IsMultiSession => SessionCount > 1;

    /// <summary>
    /// Gets all regular track blocks across all sessions.
    /// </summary>
    public IEnumerable<MdsTrackBlock> AllRegularTracks =>
        Sessions.SelectMany(s => s.RegularTracks);

    /// <summary>
    /// Gets all data (non-audio) tracks across all sessions.
    /// </summary>
    public IEnumerable<MdsTrackBlock> DataTracks =>
        AllRegularTracks.Where(t => t.IsDataTrack);

    /// <summary>
    /// Gets all audio tracks across all sessions.
    /// </summary>
    public IEnumerable<MdsTrackBlock> AudioTracks =>
        AllRegularTracks.Where(t => !t.IsDataTrack);

    /// <summary>
    /// Gets whether the disc contains both audio and data tracks.
    /// </summary>
    public bool IsMixedMode =>
        AllRegularTracks.Any(t => t.IsDataTrack) &&
        AllRegularTracks.Any(t => !t.IsDataTrack);

    /// <summary>
    /// Gets the total user data size across all regular tracks in bytes.
    /// </summary>
    public long TotalUserDataSize =>
        AllRegularTracks.Sum(t => (t.ExtraBlock?.Length ?? 0) * (long)t.UserDataSize);

    /// <summary>
    /// Gets the total disc size including sector headers, ECC/EDC, and subchannel data.
    /// </summary>
    public long TotalDiscSize =>
        AllRegularTracks.Sum(t => t.TotalLength);

    /// <summary>
    /// Gets the total duration of all audio tracks in seconds.
    /// Computed from sector count at the CD-DA frame rate (75 sectors/second).
    /// </summary>
    public double TotalAudioDurationSeconds =>
        AudioTracks.Sum(t => (t.ExtraBlock?.Length ?? 0) / 75.0);

    /// <summary>
    /// Gets the first data track, or null if none exist.
    /// </summary>
    public MdsTrackBlock? FirstDataTrack =>
        DataTracks.FirstOrDefault();

    /// <summary>
    /// Gets the largest data track by total length.
    /// </summary>
    public MdsTrackBlock? LargestDataTrack =>
        DataTracks.OrderByDescending(t => t.TotalLength).FirstOrDefault();

    /// <summary>Whether the medium is a DVD type.</summary>
    public bool IsDvd => MediumType == MdsMediumType.DvdRom || MediumType == MdsMediumType.DvdMinusR;

    /// <summary>Whether the medium is a CD type.</summary>
    public bool IsCd => MediumType == MdsMediumType.CdRom || MediumType == MdsMediumType.CdR || MediumType == MdsMediumType.CdRw;

    /// <summary>
    /// Gets a diagnostic summary of the parsed image for logging/display purposes.
    /// </summary>
    public string DiagnosticSummary
    {
        get
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"MDS Version: {VersionString}");
            sb.AppendLine($"Medium Type: {MediumType}");
            sb.AppendLine($"MDS File: {MdsFilePath} ({MdsFileSize:N0} bytes)");
            sb.AppendLine($"MDF File: {MdfFilePath} ({MdfFileSize:N0} bytes)");
            sb.AppendLine($"Sessions: {SessionCount}, Tracks: {TotalTrackCount}");

            foreach (var session in Sessions)
            {
                sb.AppendLine($"  Session {session.SessionNumber} " +
                    $"(start={session.SessionStart}, end={session.SessionEnd}, " +
                    $"{session.RegularTrackCount} tracks):");

                foreach (var track in session.RegularTracks)
                {
                    string modeStr = track.Mode switch
                    {
                        MdsTrackMode.Audio => "Audio",
                        MdsTrackMode.Mode1 => "Mode1",
                        MdsTrackMode.Mode2 or MdsTrackMode.Mode2Alt or MdsTrackMode.Mode2Alt2 => "Mode2",
                        MdsTrackMode.Mode2Form1 => "Mode2/Form1",
                        MdsTrackMode.Mode2Form2 => "Mode2/Form2",
                        _ => $"Unknown(0x{(byte)track.Mode:X2})"
                    };
                    string subStr = track.SubchannelMode != MdsSubchannelMode.None
                        ? " + PW subchannel (96B)"
                        : "";
                    string pregapStr = track.ExtraBlock != null && track.ExtraBlock.Pregap > 0
                        ? $" pregap={track.ExtraBlock.Pregap}"
                        : "";
                    string lengthStr = track.ExtraBlock != null
                        ? $" length={track.ExtraBlock.Length}"
                        : "";

                    sb.AppendLine($"    Track {track.Point}: {modeStr} {track.SectorSize}B " +
                        $"PLBA={track.StartSector} offset=0x{track.StartOffset:X}" +
                        $"{pregapStr}{lengthStr}{subStr}");
                }
            }
            return sb.ToString();
        }
    }
}
