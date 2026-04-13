// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenBurningSuite.Models;

/// <summary>
/// CloneCD disc type as stored in the CCD Session PreGapMode
/// and Entry A0 PSec fields.
/// </summary>
public enum CcdDiscType
{
    /// <summary>CD-DA or CD-ROM (Mode 1).</summary>
    CdDaOrCdRom = 0x00,
    /// <summary>CD-I (Green Book).</summary>
    CdI = 0x10,
    /// <summary>CD-ROM/XA (Mode 2).</summary>
    CdRomXa = 0x20
}

/// <summary>
/// CloneCD track mode as stored in the [TRACK N] MODE field.
/// </summary>
public enum CcdTrackMode
{
    /// <summary>Audio track (CD-DA).</summary>
    Audio = 0,
    /// <summary>Data track, Mode 1 (2048-byte user data, 2352-byte raw).</summary>
    Mode1 = 1,
    /// <summary>Data track, Mode 2 (2336-byte user data, 2352-byte raw).</summary>
    Mode2 = 2
}

/// <summary>Represents a single TOC entry from a CCD [Entry N] section.</summary>
public class CcdEntry
{
    /// <summary>Entry index (0-based, from the [Entry N] section header).</summary>
    public int EntryIndex { get; set; }

    /// <summary>Session number this entry belongs to (1-based).</summary>
    public int Session { get; set; } = 1;

    /// <summary>
    /// Point value. For regular tracks this is the track number (0x01–0x63).
    /// Special values: 0xA0 = first track info, 0xA1 = last track info, 0xA2 = lead-out.
    /// </summary>
    public int Point { get; set; }

    /// <summary>ADR sub-Q field (typically 0x01 for Mode-1 Q).</summary>
    public int Adr { get; set; } = 1;

    /// <summary>Control nibble from sub-Q. Bit 2 set = data track.</summary>
    public int Control { get; set; }

    /// <summary>Track number from sub-Q (0 for lead-in entries).</summary>
    public int TrackNo { get; set; }

    /// <summary>Absolute time — minutes component.</summary>
    public int AMin { get; set; }

    /// <summary>Absolute time — seconds component.</summary>
    public int ASec { get; set; }

    /// <summary>Absolute time — frames component.</summary>
    public int AFrame { get; set; }

    /// <summary>Absolute LBA (derived from AMin/ASec/AFrame, often negative for pregap).</summary>
    public int Alba { get; set; }

    /// <summary>Zero field (always 0 in practice).</summary>
    public int Zero { get; set; }

    /// <summary>Point time — minutes component.</summary>
    public int PMin { get; set; }

    /// <summary>Point time — seconds component.</summary>
    public int PSec { get; set; }

    /// <summary>Point time — frames component.</summary>
    public int PFrame { get; set; }

    /// <summary>Point LBA (track start LBA for regular tracks).</summary>
    public int Plba { get; set; }

    /// <summary>Returns true if this entry represents a regular track (Point 0x01–0x63).</summary>
    public bool IsTrackEntry => Point >= 0x01 && Point <= 0x63;

    /// <summary>Returns true if this is a data track (Control bit 2 set).</summary>
    public bool IsData => (Control & 0x04) != 0;

    /// <summary>Returns true if this is an audio track.</summary>
    public bool IsAudio => !IsData;

    /// <summary>Returns true if this is the first-track pointer (A0).</summary>
    public bool IsFirstTrackPointer => Point == 0xA0;

    /// <summary>Returns true if this is the last-track pointer (A1).</summary>
    public bool IsLastTrackPointer => Point == 0xA1;

    /// <summary>Returns true if this is the lead-out pointer (A2).</summary>
    public bool IsLeadOutPointer => Point == 0xA2;
}

/// <summary>Represents a session from a CCD [Session N] section.</summary>
public class CcdSession
{
    /// <summary>Session number (1-based).</summary>
    public int SessionNumber { get; set; } = 1;

    /// <summary>Pregap mode for this session (0=Audio, 1=Mode1, 2=Mode2).</summary>
    public int PreGapMode { get; set; }

    /// <summary>Pregap subchannel mode (0=none).</summary>
    public int PreGapSubC { get; set; }
}

/// <summary>Represents a track from a CCD [TRACK N] section.</summary>
public class CcdTrack
{
    /// <summary>Track number (1-based).</summary>
    public int TrackNumber { get; set; }

    /// <summary>Track mode: 0=Audio, 1=Mode1, 2=Mode2.</summary>
    public CcdTrackMode Mode { get; set; } = CcdTrackMode.Mode1;

    /// <summary>
    /// Track index positions. Key is the index number (0, 1, etc.),
    /// value is the sector position (LBA).
    /// INDEX 0 = pregap start, INDEX 1 = track start.
    /// </summary>
    public Dictionary<int, int> Indices { get; set; } = new();

    /// <summary>ISRC code for this track (12 characters, optional).</summary>
    public string Isrc { get; set; } = string.Empty;

    /// <summary>
    /// FLAGS field (DCP, 4CH, PRE, SCMS) as raw value.
    /// Not present in all CCD files.
    /// </summary>
    public int Flags { get; set; }

    /// <summary>Returns INDEX 1 (track start LBA), or INDEX 0 if INDEX 1 is not present.</summary>
    public int StartLba => Indices.TryGetValue(1, out var idx1) ? idx1
        : Indices.TryGetValue(0, out var idx0) ? idx0 : 0;

    /// <summary>Returns true if this is a data track.</summary>
    public bool IsData => Mode != CcdTrackMode.Audio;

    /// <summary>Returns true if this is an audio track.</summary>
    public bool IsAudio => Mode == CcdTrackMode.Audio;

    /// <summary>
    /// Raw sector size: always 2352 for CCD/IMG format.
    /// </summary>
    public int SectorSize => 2352;

    /// <summary>
    /// User data size depending on track mode.
    /// Audio/Mode1 raw = 2352, Mode1 cooked = 2048, Mode2 = 2336.
    /// For CCD, we always store raw 2352-byte sectors.
    /// </summary>
    public int UserDataSize => Mode switch
    {
        CcdTrackMode.Audio => 2352,
        CcdTrackMode.Mode1 => 2048,
        CcdTrackMode.Mode2 => 2336,
        _ => 2048
    };

    /// <summary>
    /// Offset into the raw sector where user data begins.
    /// Audio = 0 (entire sector is audio), Mode1 = 16, Mode2 = 16.
    /// </summary>
    public int UserDataOffset => Mode switch
    {
        CcdTrackMode.Audio => 0,
        CcdTrackMode.Mode1 => 16,
        CcdTrackMode.Mode2 => 16,
        _ => 16
    };
}

/// <summary>
/// Represents a parsed CloneCD disc image (.ccd + .img + optional .sub).
///
/// The CCD format is an INI-style text file created by CloneCD software
/// that describes the disc structure. It is always accompanied by:
/// - An .img file containing raw 2352-byte sectors
/// - An optional .sub file containing 96-byte interleaved P-W subchannel data
///
/// Supported CCD versions: 2 and 3.
/// </summary>
public class CcdImage
{
    /// <summary>CCD file version (2 or 3).</summary>
    public int Version { get; set; } = 3;

    /// <summary>Total number of TOC entries.</summary>
    public int TocEntries { get; set; }

    /// <summary>Number of sessions on the disc.</summary>
    public int SessionCount { get; set; } = 1;

    /// <summary>Whether data tracks are scrambled.</summary>
    public bool DataTracksScrambled { get; set; }

    /// <summary>CD-Text data length in bytes.</summary>
    public int CdTextLength { get; set; }

    /// <summary>Catalog number (MCN/UPC) if present.</summary>
    public string Catalog { get; set; } = string.Empty;

    /// <summary>All TOC entries parsed from [Entry N] sections.</summary>
    public List<CcdEntry> Entries { get; set; } = new();

    /// <summary>Session descriptors from [Session N] sections.</summary>
    public List<CcdSession> Sessions { get; set; } = new();

    /// <summary>Track descriptors from [TRACK N] sections.</summary>
    public List<CcdTrack> Tracks { get; set; } = new();

    /// <summary>Path to the .ccd file.</summary>
    public string CcdFilePath { get; set; } = string.Empty;

    /// <summary>Path to the .img file (raw sector data).</summary>
    public string ImgFilePath { get; set; } = string.Empty;

    /// <summary>Path to the .sub file (subchannel data), if present.</summary>
    public string SubFilePath { get; set; } = string.Empty;

    /// <summary>Whether the .sub file exists and is accessible.</summary>
    public bool HasSubchannel { get; set; }

    /// <summary>Whether parsing was successful.</summary>
    public bool IsValid { get; set; }

    /// <summary>Error message if parsing failed.</summary>
    public string ErrorMessage { get; set; } = string.Empty;

    // -----------------------------------------------------------------------
    //  Computed properties
    // -----------------------------------------------------------------------

    /// <summary>All regular track entries (Point 0x01–0x63).</summary>
    public IEnumerable<CcdEntry> TrackEntries => Entries.Where(e => e.IsTrackEntry);

    /// <summary>Number of tracks on the disc.</summary>
    public int TrackCount => Tracks.Count;

    /// <summary>Number of audio tracks.</summary>
    public int AudioTrackCount => Tracks.Count(t => t.IsAudio);

    /// <summary>Number of data tracks.</summary>
    public int DataTrackCount => Tracks.Count(t => t.IsData);

    /// <summary>Whether the disc contains both audio and data tracks.</summary>
    public bool IsMixedMode => AudioTrackCount > 0 && DataTrackCount > 0;

    /// <summary>Whether the disc is multi-session.</summary>
    public bool IsMultiSession => SessionCount > 1;

    /// <summary>First track number.</summary>
    public int FirstTrackNumber => Tracks.Count > 0 ? Tracks.Min(t => t.TrackNumber) : 1;

    /// <summary>Last track number.</summary>
    public int LastTrackNumber => Tracks.Count > 0 ? Tracks.Max(t => t.TrackNumber) : 1;

    /// <summary>Lead-out LBA from the A2 entry.</summary>
    public int LeadOutLba
    {
        get
        {
            var a2 = Entries.FirstOrDefault(e => e.IsLeadOutPointer);
            return a2?.Plba ?? 0;
        }
    }

    /// <summary>Total number of sectors on the disc (from lead-out).</summary>
    public long TotalSectors => LeadOutLba > 0 ? LeadOutLba : 0;

    /// <summary>Total disc size in bytes (raw sectors).</summary>
    public long TotalDiscSize => TotalSectors * 2352;

    /// <summary>
    /// Disc type derived from A0 entry PSec field.
    /// 0x00=CD-DA/CD-ROM, 0x10=CD-I, 0x20=CD-ROM/XA.
    /// </summary>
    public CcdDiscType DiscType
    {
        get
        {
            var a0 = Entries.FirstOrDefault(e => e.IsFirstTrackPointer);
            if (a0 == null) return CcdDiscType.CdDaOrCdRom;
            return (CcdDiscType)(a0.PSec & 0xF0);
        }
    }

    /// <summary>Gets a track by its number, or null if not found.</summary>
    public CcdTrack? GetTrackByNumber(int trackNumber) =>
        Tracks.FirstOrDefault(t => t.TrackNumber == trackNumber);

    /// <summary>Gets the first data track, or null if there are no data tracks.</summary>
    public CcdTrack? FirstDataTrack => Tracks.FirstOrDefault(t => t.IsData);

    /// <summary>
    /// Gets the length (in sectors) of a specific track.
    /// Determined by finding the next track's start or the lead-out.
    /// </summary>
    public int GetTrackLength(int trackNumber)
    {
        var track = GetTrackByNumber(trackNumber);
        if (track == null) return 0;

        var startLba = track.StartLba;

        // Find next track
        var nextTrack = Tracks
            .Where(t => t.TrackNumber > trackNumber)
            .OrderBy(t => t.TrackNumber)
            .FirstOrDefault();

        if (nextTrack != null)
        {
            // Use the next track's INDEX 0 if available (includes pregap),
            // otherwise INDEX 1
            var nextStart = nextTrack.Indices.TryGetValue(0, out var idx0)
                ? idx0 : nextTrack.StartLba;
            return nextStart - startLba;
        }

        // Last track: use lead-out
        var leadOut = LeadOutLba;
        return leadOut > startLba ? leadOut - startLba : 0;
    }

    /// <summary>Gets the largest data track by sector count.</summary>
    public CcdTrack? LargestDataTrack
    {
        get
        {
            CcdTrack? largest = null;
            int maxLen = 0;
            foreach (var t in Tracks.Where(t => t.IsData))
            {
                var len = GetTrackLength(t.TrackNumber);
                if (len > maxLen) { maxLen = len; largest = t; }
            }
            return largest;
        }
    }

    /// <summary>Total audio duration in seconds.</summary>
    public double TotalAudioDurationSeconds
    {
        get
        {
            long audioSectors = 0;
            foreach (var t in Tracks.Where(t => t.IsAudio))
                audioSectors += GetTrackLength(t.TrackNumber);
            return audioSectors / 75.0; // 75 sectors per second for CD audio
        }
    }

    /// <summary>Generates a diagnostic summary of the CCD image.</summary>
    public string GetDiagnosticSummary()
    {
        var lines = new List<string>
        {
            $"CCD Version: {Version}",
            $"Sessions: {SessionCount}",
            $"Tracks: {TrackCount} ({DataTrackCount} data, {AudioTrackCount} audio)",
            $"TOC Entries: {TocEntries}",
            $"Disc Type: {DiscType}",
            $"Lead-out: LBA {LeadOutLba}",
            $"Total Sectors: {TotalSectors}",
            $"Total Size: {TotalDiscSize:N0} bytes",
            $"Data Scrambled: {DataTracksScrambled}",
            $"CD-Text: {(CdTextLength > 0 ? $"{CdTextLength} bytes" : "None")}",
            $"Subchannel: {(HasSubchannel ? "Yes (.sub present)" : "No")}",
            $"Mixed Mode: {IsMixedMode}",
            $"Multi-Session: {IsMultiSession}"
        };

        if (!string.IsNullOrEmpty(Catalog))
            lines.Add($"Catalog: {Catalog}");

        lines.Add("");
        foreach (var track in Tracks)
        {
            var len = GetTrackLength(track.TrackNumber);
            lines.Add($"  Track {track.TrackNumber:D2}: {track.Mode}, " +
                $"LBA {track.StartLba}, {len} sectors" +
                (track.IsAudio ? $" ({len / 75.0:F1}s)" : $" ({(long)len * track.UserDataSize:N0} bytes)") +
                (!string.IsNullOrEmpty(track.Isrc) ? $", ISRC: {track.Isrc}" : ""));
        }

        return string.Join(Environment.NewLine, lines);
    }
}
