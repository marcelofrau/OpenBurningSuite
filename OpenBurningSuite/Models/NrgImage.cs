// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenBurningSuite.Models;

/// <summary>NRG file format version.</summary>
public enum NrgVersion
{
    /// <summary>NRG version 1 — 32-bit offsets, "NERO" footer ID.</summary>
    V1 = 1,
    /// <summary>NRG version 2 — 64-bit offsets, "NER5" footer ID. Introduced in Nero 5.5.</summary>
    V2 = 2
}

/// <summary>
/// NRG track mode values found in DAOI/DAOX and ETNF/ETN2 chunks.
/// Encodes the track type and sector format.
/// </summary>
public enum NrgTrackMode : ushort
{
    /// <summary>Data track (Mode 1, 2048-byte cooked sectors).</summary>
    Data = 0x0000,
    /// <summary>Mode 2 Form 1 data (2048-byte user data within Mode 2 XA).</summary>
    Mode2Form1 = 0x0300,
    /// <summary>Raw data track (2352-byte raw sectors).</summary>
    RawData = 0x0500,
    /// <summary>Raw Mode 2 Form 1 data (2352-byte raw sectors).</summary>
    RawMode2Form1 = 0x0600,
    /// <summary>Audio track (2352-byte CD-DA sectors).</summary>
    Audio = 0x0700,
    /// <summary>Raw data with sub-channel (2448-byte sectors).</summary>
    RawDataSubchannel = 0x0F00,
    /// <summary>Audio with sub-channel data (2448-byte sectors).</summary>
    AudioSubchannel = 0x1000,
    /// <summary>Raw Mode 2 Form 1 with sub-channel (2448-byte sectors).</summary>
    RawMode2Form1Subchannel = 0x1100
}

/// <summary>
/// NRG DAOI/DAOX TOC type values.
/// </summary>
public enum NrgTocType : ushort
{
    /// <summary>Audio disc.</summary>
    Audio = 0x0000,
    /// <summary>Data disc (Mode 1).</summary>
    Data = 0x0001,
    /// <summary>Mode 2 Form 1 data disc.</summary>
    Mode2Form1 = 0x2001
}

/// <summary>Represents a cue point from a CUES/CUEX chunk.</summary>
public class NrgCueEntry
{
    /// <summary>Mode byte (0x01=audio, 0x21=non-copy-protected audio, 0x41=data).</summary>
    public byte Mode { get; set; }

    /// <summary>Track number (BCD coded; 0xAA = lead-out).</summary>
    public byte TrackNumber { get; set; }

    /// <summary>Index number (BCD coded, typically 0 or 1).</summary>
    public byte IndexNumber { get; set; }

    /// <summary>LBA position in sectors (signed).</summary>
    public int Lba { get; set; }

    /// <summary>Whether this is the lead-out entry (track number 0xAA).</summary>
    public bool IsLeadOut => TrackNumber == 0xAA;

    /// <summary>Whether this is a data track cue point.</summary>
    public bool IsData => Mode == 0x41;

    /// <summary>Whether this is an audio track cue point.</summary>
    public bool IsAudio => Mode == 0x01 || Mode == 0x21;

    /// <summary>Decoded track number (BCD to integer).</summary>
    public int DecodedTrackNumber => IsLeadOut ? 0xAA : ((TrackNumber >> 4) * 10 + (TrackNumber & 0x0F));

    /// <summary>Decoded index number (BCD to integer).</summary>
    public int DecodedIndexNumber => (IndexNumber >> 4) * 10 + (IndexNumber & 0x0F);
}

/// <summary>
/// Represents a track from a DAOI/DAOX chunk (Disc-At-Once session track).
/// </summary>
public class NrgDaoTrack
{
    /// <summary>ISRC code (12 characters, or empty).</summary>
    public string Isrc { get; set; } = string.Empty;

    /// <summary>Sector size in bytes as stored in the image file.</summary>
    public int SectorSize { get; set; }

    /// <summary>Track mode encoding.</summary>
    public NrgTrackMode Mode { get; set; }

    /// <summary>Index0 (pre-gap) file offset in bytes.</summary>
    public long Index0Offset { get; set; }

    /// <summary>Index1 (track start) file offset in bytes.</summary>
    public long Index1Offset { get; set; }

    /// <summary>End of track file offset in bytes (exclusive — next byte after last sector).</summary>
    public long EndOffset { get; set; }

    /// <summary>Track number (1-based), assigned during parsing.</summary>
    public int TrackNumber { get; set; }

    /// <summary>Session number this track belongs to (1-based).</summary>
    public int SessionNumber { get; set; } = 1;

    /// <summary>Start LBA on disc (from cue data or computed).</summary>
    public long StartLba { get; set; }

    /// <summary>Track data length in bytes (EndOffset - Index1Offset).</summary>
    public long DataLength => EndOffset - Index1Offset;

    /// <summary>Pre-gap length in bytes (Index1Offset - Index0Offset).</summary>
    public long PregapLength => Index1Offset - Index0Offset;

    /// <summary>Number of data sectors (excluding pregap).</summary>
    public long SectorCount => SectorSize > 0 ? DataLength / SectorSize : 0;

    /// <summary>Number of pregap sectors.</summary>
    public long PregapSectors => SectorSize > 0 ? PregapLength / SectorSize : 0;

    /// <summary>Whether this track contains subchannel data.</summary>
    public bool HasSubchannel => SectorSize >= 2448;

    /// <summary>
    /// Base sector size excluding subchannel data.
    /// 2448 - 96 = 2352 for subchannel tracks, otherwise same as SectorSize.
    /// </summary>
    public int BaseSectorSize => HasSubchannel ? SectorSize - 96 : SectorSize;

    /// <summary>Whether this is an audio track.</summary>
    public bool IsAudio => Mode == NrgTrackMode.Audio || Mode == NrgTrackMode.AudioSubchannel;

    /// <summary>Whether this is a data track.</summary>
    public bool IsData => !IsAudio;

    /// <summary>
    /// User data size per sector.
    /// Audio=2352, Data(cooked)=2048, Raw=2048 (extracted from raw), Mode2Form1=2048.
    /// </summary>
    public int UserDataSize => Mode switch
    {
        NrgTrackMode.Audio or NrgTrackMode.AudioSubchannel => 2352,
        NrgTrackMode.Data => 2048,
        NrgTrackMode.Mode2Form1 => 2048,
        NrgTrackMode.RawData => 2048,
        NrgTrackMode.RawMode2Form1 or NrgTrackMode.RawMode2Form1Subchannel => 2048,
        NrgTrackMode.RawDataSubchannel => 2048,
        _ => BaseSectorSize
    };

    /// <summary>
    /// Offset within a raw sector where user data begins.
    /// Audio=0, Mode1 raw=16, Mode2 raw=24, Cooked=0.
    /// </summary>
    public int UserDataOffset => Mode switch
    {
        NrgTrackMode.Audio or NrgTrackMode.AudioSubchannel => 0,
        NrgTrackMode.RawData or NrgTrackMode.RawDataSubchannel => 16,
        NrgTrackMode.RawMode2Form1 or NrgTrackMode.RawMode2Form1Subchannel => 24,
        _ => 0 // Cooked sectors: user data starts at offset 0
    };

    /// <summary>Whether sectors are stored in raw (2352-byte) format.</summary>
    public bool IsRawSector => BaseSectorSize >= 2352;
}

/// <summary>
/// Represents a track from an ETNF/ETN2 chunk (Track-At-Once session track).
/// </summary>
public class NrgTaoTrack
{
    /// <summary>Track offset in image file (bytes).</summary>
    public long FileOffset { get; set; }

    /// <summary>Track data length in bytes.</summary>
    public long DataLength { get; set; }

    /// <summary>
    /// Track mode from ETNF/ETN2 chunk.
    /// Known values: 0x00=Data Mode 1 (2048B), 0x02=Mode 2 XA (2336B),
    /// 0x03=Mode 2 Form 1 (2336B), 0x05=Raw Data (2352B),
    /// 0x06=Raw Mode 2 (2352B), 0x07=Audio CD-DA (2352B).
    /// </summary>
    public int Mode { get; set; }

    /// <summary>Start LBA on disc (sectors, after 150-sector lead-in).</summary>
    public int StartLba { get; set; }

    /// <summary>Track length in sectors (ETN2 only).</summary>
    public int SectorCount { get; set; }

    /// <summary>Track number (1-based), assigned during parsing.</summary>
    public int TrackNumber { get; set; }

    /// <summary>Session number this track belongs to (1-based).</summary>
    public int SessionNumber { get; set; } = 1;

    /// <summary>Whether this is an audio track.</summary>
    public bool IsAudio => Mode == 0x07;

    /// <summary>Whether this is a data track.</summary>
    public bool IsData => !IsAudio;

    /// <summary>Whether sectors are stored in raw (2352-byte) format.</summary>
    public bool IsRawSector => Mode == 0x05 || Mode == 0x06;

    /// <summary>
    /// Sector size derived from track mode.
    /// 0x00=2048 (Data Mode 1), 0x02=2336 (Mode 2 XA), 0x03=2336 (Mode 2 Form Mix),
    /// 0x05=2352 (Raw Data), 0x06=2352 (Raw Mode 2), 0x07=2352 (Audio).
    /// </summary>
    public int SectorSize => Mode switch
    {
        0x07 => 2352, // Audio (CD-DA)
        0x05 => 2352, // Raw Data (Mode 1, full sector)
        0x06 => 2352, // Raw Mode 2 (full sector)
        0x02 => 2336, // Mode 2 XA
        0x03 => 2336, // Mode 2 Form 1/Mix
        _ => 2048     // Data (Mode 1, cooked)
    };

    /// <summary>
    /// User data size per sector.
    /// For raw sectors (0x05, 0x06), user data is 2048 bytes extracted from within
    /// the 2352-byte raw sector. For Mode 2 (0x02, 0x03), user data is 2048 bytes
    /// (Form 1 portion). Audio is 2352 bytes (all PCM).
    /// </summary>
    public int UserDataSize => Mode switch
    {
        0x07 => 2352, // Audio: all bytes are user data
        0x05 => 2048, // Raw Data: 2048 bytes user data at offset 16
        0x06 => 2048, // Raw Mode 2: 2048 bytes user data at offset 24
        0x02 => 2048, // Mode 2 XA: 2048 bytes Form 1 user data
        0x03 => 2048, // Mode 2 Form Mix: 2048 bytes Form 1 user data
        _ => 2048     // Data Mode 1: all 2048 bytes are user data
    };

    /// <summary>
    /// Offset within a sector where user data begins.
    /// Raw Mode 1 (0x05): 16 bytes (12 sync + 4 header).
    /// Raw Mode 2 (0x06): 24 bytes (12 sync + 4 header + 8 subheader).
    /// All others: 0 (user data starts at beginning of sector).
    /// </summary>
    public int UserDataOffset => Mode switch
    {
        0x05 => 16, // Raw Data: skip sync + header
        0x06 => 24, // Raw Mode 2: skip sync + header + subheader
        _ => 0      // Cooked/audio: user data starts at offset 0
    };

    /// <summary>
    /// Computed sector count. Uses the explicit SectorCount from ETN2 if available
    /// (non-zero), otherwise computes from DataLength / SectorSize.
    /// </summary>
    public long ComputedSectorCount => SectorCount > 0
        ? SectorCount
        : (SectorSize > 0 ? DataLength / SectorSize : 0);
}

/// <summary>Represents a session from a SINF chunk.</summary>
public class NrgSession
{
    /// <summary>Session number (1-based).</summary>
    public int SessionNumber { get; set; }

    /// <summary>Number of tracks in this session.</summary>
    public int TrackCount { get; set; }
}

/// <summary>
/// Represents a parsed Nero NRG disc image.
///
/// The NRG format uses IFF-style chunks stored at the end of the file
/// (footer-based). The disc data precedes the chunk chain.
///
/// Supports both NRG v1 (32-bit, "NERO" footer) and NRG v2 (64-bit, "NER5" footer).
/// </summary>
public class NrgImage
{
    /// <summary>NRG format version (V1 or V2).</summary>
    public NrgVersion Version { get; set; }

    /// <summary>Full path to the NRG file.</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>Total file size in bytes.</summary>
    public long FileSize { get; set; }

    /// <summary>Offset of the first chunk in the chunk chain.</summary>
    public long ChunkOffset { get; set; }

    /// <summary>Cue entries from CUES/CUEX chunks.</summary>
    public List<NrgCueEntry> CueEntries { get; set; } = new();

    /// <summary>DAO tracks from DAOI/DAOX chunks.</summary>
    public List<NrgDaoTrack> DaoTracks { get; set; } = new();

    /// <summary>TAO tracks from ETNF/ETN2 chunks.</summary>
    public List<NrgTaoTrack> TaoTracks { get; set; } = new();

    /// <summary>Session information from SINF chunks.</summary>
    public List<NrgSession> Sessions { get; set; } = new();

    /// <summary>Media type value from MTYP chunk (0 if not present).</summary>
    public int MediaType { get; set; }

    /// <summary>UPC/EAN from DAOI/DAOX chunks.</summary>
    public string Upc { get; set; } = string.Empty;

    /// <summary>TOC type from DAOI/DAOX chunks.</summary>
    public NrgTocType TocType { get; set; }

    /// <summary>CD-Text raw packs from CDTX chunk.</summary>
    public byte[] CdTextData { get; set; } = Array.Empty<byte>();

    /// <summary>Whether the disc is not closed (DINF chunk value).</summary>
    public bool IsDiscOpen { get; set; }

    /// <summary>Whether the image was parsed successfully.</summary>
    public bool IsValid { get; set; }

    /// <summary>Error message if parsing failed, empty otherwise.</summary>
    public string ErrorMessage { get; set; } = string.Empty;

    // -----------------------------------------------------------------------
    //  Computed properties
    // -----------------------------------------------------------------------

    /// <summary>Whether this image uses DAO (Disc-At-Once) recording.</summary>
    public bool IsDao => DaoTracks.Count > 0;

    /// <summary>Whether this image uses TAO (Track-At-Once) recording.</summary>
    public bool IsTao => TaoTracks.Count > 0 && DaoTracks.Count == 0;

    /// <summary>Total number of sessions.</summary>
    public int SessionCount => Sessions.Count > 0 ? Sessions.Count : 1;

    /// <summary>Total number of tracks across all sessions.</summary>
    public int TotalTrackCount => IsDao ? DaoTracks.Count : TaoTracks.Count;

    /// <summary>All tracks (DAO or TAO) as a unified enumerable.</summary>
    public int AudioTrackCount => IsDao
        ? DaoTracks.Count(t => t.IsAudio)
        : TaoTracks.Count(t => t.IsAudio);

    /// <summary>Number of data tracks.</summary>
    public int DataTrackCount => IsDao
        ? DaoTracks.Count(t => t.IsData)
        : TaoTracks.Count(t => t.IsData);

    /// <summary>Whether the disc contains both audio and data tracks.</summary>
    public bool IsMixedMode => AudioTrackCount > 0 && DataTrackCount > 0;

    /// <summary>Whether the disc is multi-session.</summary>
    public bool IsMultiSession => SessionCount > 1;

    /// <summary>Whether any track has CD-Text data.</summary>
    public bool HasCdText => CdTextData.Length > 0;

    /// <summary>Lead-out LBA from cue entries (0xAA track).</summary>
    public int LeadOutLba
    {
        get
        {
            var leadOut = CueEntries.FirstOrDefault(c => c.IsLeadOut && c.DecodedIndexNumber == 1);
            return leadOut?.Lba ?? 0;
        }
    }

    /// <summary>
    /// Total user data size across all tracks in bytes.
    /// </summary>
    public long TotalUserDataSize
    {
        get
        {
            if (IsDao)
                return DaoTracks.Sum(t => t.SectorCount * (long)t.UserDataSize);
            return TaoTracks.Sum(t => t.ComputedSectorCount * (long)t.UserDataSize);
        }
    }

    /// <summary>Total raw disc size in bytes (from track data lengths).</summary>
    public long TotalDiscSize
    {
        get
        {
            if (IsDao)
                return DaoTracks.Sum(t => t.DataLength);
            return TaoTracks.Sum(t => t.DataLength);
        }
    }

    /// <summary>Total audio duration in seconds (75 sectors/second for CD-DA).</summary>
    public double TotalAudioDurationSeconds
    {
        get
        {
            if (IsDao)
                return DaoTracks.Where(t => t.IsAudio).Sum(t => t.SectorCount / 75.0);
            return TaoTracks.Where(t => t.IsAudio).Sum(t => t.ComputedSectorCount / 75.0);
        }
    }

    /// <summary>Gets the first data track (DAO or TAO).</summary>
    public NrgDaoTrack? FirstDaoDataTrack => DaoTracks.FirstOrDefault(t => t.IsData);
    /// <summary>Gets the first TAO data track.</summary>
    public NrgTaoTrack? FirstTaoDataTrack => TaoTracks.FirstOrDefault(t => t.IsData);

    /// <summary>Gets the largest data track by data length (DAO).</summary>
    public NrgDaoTrack? LargestDaoDataTrack =>
        DaoTracks.Where(t => t.IsData).OrderByDescending(t => t.DataLength).FirstOrDefault();

    /// <summary>Gets the largest data track by data length (TAO).</summary>
    public NrgTaoTrack? LargestTaoDataTrack =>
        TaoTracks.Where(t => t.IsData).OrderByDescending(t => t.DataLength).FirstOrDefault();

    /// <summary>Version string for display purposes.</summary>
    public string VersionString => Version == NrgVersion.V2 ? "NRG v2 (NER5)" : "NRG v1 (NERO)";

    /// <summary>Generates a diagnostic summary of the NRG image.</summary>
    public string GetDiagnosticSummary()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"NRG Version: {VersionString}");
        sb.AppendLine($"File: {FilePath}");
        sb.AppendLine($"File Size: {FileSize:N0} bytes");
        sb.AppendLine($"Chunk Offset: 0x{ChunkOffset:X}");
        sb.AppendLine($"Sessions: {SessionCount}, Tracks: {TotalTrackCount}");
        sb.AppendLine($"Recording: {(IsDao ? "DAO" : "TAO")}");
        sb.AppendLine($"Media Type: {MediaType}");
        if (!string.IsNullOrEmpty(Upc))
            sb.AppendLine($"UPC: {Upc}");
        if (HasCdText)
            sb.AppendLine($"CD-Text: {CdTextData.Length} bytes ({CdTextData.Length / 18} packs)");
        sb.AppendLine($"Mixed Mode: {IsMixedMode}");
        sb.AppendLine($"Multi-Session: {IsMultiSession}");

        if (IsDao)
        {
            foreach (var track in DaoTracks)
            {
                string modeStr = track.Mode switch
                {
                    NrgTrackMode.Audio => "Audio",
                    NrgTrackMode.AudioSubchannel => "Audio+Sub",
                    NrgTrackMode.Data => "Data",
                    NrgTrackMode.Mode2Form1 => "Mode2/Form1",
                    NrgTrackMode.RawData => "Raw Data",
                    NrgTrackMode.RawMode2Form1 => "Raw Mode2/F1",
                    NrgTrackMode.RawDataSubchannel => "Raw+Sub",
                    NrgTrackMode.RawMode2Form1Subchannel => "Raw M2F1+Sub",
                    _ => $"0x{(ushort)track.Mode:X4}"
                };
                string isrcStr = !string.IsNullOrEmpty(track.Isrc) ? $" ISRC={track.Isrc}" : "";
                sb.AppendLine($"  Track {track.TrackNumber}: {modeStr} {track.SectorSize}B " +
                    $"LBA={track.StartLba} Sectors={track.SectorCount} " +
                    $"Offset=0x{track.Index1Offset:X}{isrcStr}");
            }
        }
        else
        {
            foreach (var track in TaoTracks)
            {
                string modeStr = track.Mode switch
                {
                    0x07 => "Audio",
                    0x06 => "Raw Mode2",
                    0x05 => "Raw Data",
                    0x03 => "Mode2 Mix",
                    0x02 => "Mode2 XA",
                    0x00 => "Data",
                    _ => $"0x{track.Mode:X2}"
                };
                sb.AppendLine($"  Track {track.TrackNumber}: {modeStr} {track.SectorSize}B " +
                    $"LBA={track.StartLba} Sectors={track.ComputedSectorCount} " +
                    $"Offset=0x{track.FileOffset:X}");
            }
        }

        return sb.ToString();
    }
}
