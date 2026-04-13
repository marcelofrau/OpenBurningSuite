// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenBurningSuite.Models;

namespace OpenBurningSuite.Native.Iso;

/// <summary>
/// Writer for Alcohol 120% MDS/MDF disc image files.
/// Creates an MDF data file containing raw sector data and an MDS descriptor file
/// containing the disc structure metadata (sessions, tracks, sector layout).
///
/// The MDS format uses the binary layout defined by Alcohol 120% software:
/// - Header: 88 bytes starting with "MEDIA DESCRIPTOR" signature
/// - Session blocks: 24 bytes each
/// - Track blocks: 80 bytes each (includes lead-in/TOC and regular tracks)
/// - Track extra blocks: 8 bytes each (pregap + track length)
/// - Footer blocks: 16 bytes each (MDF filename reference)
///
/// This writer supports:
/// - CD-ROM, CD-R, CD-RW, DVD-ROM, DVD-R medium types
/// - Multi-session and multi-track discs
/// - Audio, Mode 1, Mode 2, Mode 2 Form 1, Mode 2 Form 2 track modes
/// - Raw sectors (2352 bytes) and cooked sectors (2048/2336 bytes)
/// - 96-byte interleaved P-W subchannel data
/// </summary>
public static class MdfWriter
{
    // ------------------------------------------------------------------
    //  Constants (MDS binary layout sizes)
    // ------------------------------------------------------------------

    private const int HeaderSize = 88;
    private const int SessionBlockSize = 24;
    private const int TrackBlockSize = 80;
    private const int TrackExtraBlockSize = 8;
    private const int FooterBlockSize = 16;

    /// <summary>MDS file signature.</summary>
    private static readonly byte[] MdsSignature =
        Encoding.ASCII.GetBytes("MEDIA DESCRIPTOR");

    // ------------------------------------------------------------------
    //  Public API — Write from track layout
    // ------------------------------------------------------------------

    /// <summary>
    /// Writes MDF data and MDS descriptor files from raw sector data provided by a stream.
    /// This is the primary method used when reading a disc to MDF/MDS output.
    /// </summary>
    /// <param name="sectorDataStream">
    /// Stream containing the raw sector data to write to the MDF file.
    /// This stream is read sequentially and its data is written directly to the MDF.
    /// </param>
    /// <param name="mdfOutputPath">Destination path for the MDF (data) file.</param>
    /// <param name="mdsOutputPath">Destination path for the MDS (descriptor) file.</param>
    /// <param name="tracks">Track layout describing the disc structure.</param>
    /// <param name="mediumType">Type of the source disc medium.</param>
    /// <param name="progress">Optional progress reporter (0–100).</param>
    public static void Write(
        Stream sectorDataStream,
        string mdfOutputPath,
        string mdsOutputPath,
        List<MdfWriterTrack> tracks,
        MdsMediumType mediumType = MdsMediumType.CdRom,
        IProgress<int>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(sectorDataStream);
        ArgumentNullException.ThrowIfNull(tracks);

        if (tracks.Count == 0)
            throw new ArgumentException("At least one track is required.", nameof(tracks));

        // Write MDF file — copy all sector data from the input stream
        long mdfSize;
        using (var mdfStream = new FileStream(mdfOutputPath, FileMode.Create, FileAccess.Write))
        {
            CopyStreamWithProgress(sectorDataStream, mdfStream, progress);
            mdfSize = mdfStream.Length;
        }

        // Build and write MDS descriptor
        byte[] mdsData = BuildMdsDescriptor(tracks, mediumType, mdfOutputPath, mdfSize);
        File.WriteAllBytes(mdsOutputPath, mdsData);
    }

    /// <summary>Asynchronously writes MDF/MDS files from a sector data stream.</summary>
    public static async Task WriteAsync(
        Stream sectorDataStream,
        string mdfOutputPath,
        string mdsOutputPath,
        List<MdfWriterTrack> tracks,
        MdsMediumType mediumType = MdsMediumType.CdRom,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            Write(sectorDataStream, mdfOutputPath, mdsOutputPath, tracks, mediumType, progress);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes MDF data and MDS descriptor files directly from an existing image file.
    /// Useful for converting ISO/BIN/IMG files to MDF/MDS format.
    /// </summary>
    /// <param name="sourceImagePath">Path to the source image file.</param>
    /// <param name="mdfOutputPath">Destination path for the MDF file.</param>
    /// <param name="mdsOutputPath">Destination path for the MDS file.</param>
    /// <param name="tracks">Track layout describing the disc structure.</param>
    /// <param name="mediumType">Type of the source disc medium.</param>
    /// <param name="progress">Optional progress reporter (0–100).</param>
    public static void WriteFromFile(
        string sourceImagePath,
        string mdfOutputPath,
        string mdsOutputPath,
        List<MdfWriterTrack> tracks,
        MdsMediumType mediumType = MdsMediumType.CdRom,
        IProgress<int>? progress = null)
    {
        using var sourceStream = new FileStream(sourceImagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        Write(sourceStream, mdfOutputPath, mdsOutputPath, tracks, mediumType, progress);
    }

    /// <summary>
    /// Writes an MDF/MDS pair from a simple single-track data disc (ISO-like).
    /// This is a convenience method for the common case of a single data track
    /// with cooked 2048-byte sectors.
    /// </summary>
    /// <param name="sourceIsoPath">Path to the source ISO file.</param>
    /// <param name="mdfOutputPath">Destination path for the MDF file.</param>
    /// <param name="mdsOutputPath">Destination path for the MDS file.</param>
    /// <param name="sectorSize">Sector size (default 2048 for ISO data).</param>
    /// <param name="mediumType">Medium type (default CD-ROM).</param>
    /// <param name="progress">Optional progress reporter (0–100).</param>
    public static void WriteFromIso(
        string sourceIsoPath,
        string mdfOutputPath,
        string mdsOutputPath,
        int sectorSize = 2048,
        MdsMediumType mediumType = MdsMediumType.CdRom,
        IProgress<int>? progress = null)
    {
        long fileSize = new FileInfo(sourceIsoPath).Length;
        uint sectorCount = (uint)(fileSize / sectorSize);
        if (fileSize % sectorSize != 0) sectorCount++;

        var tracks = new List<MdfWriterTrack>
        {
            new()
            {
                TrackNumber = 1,
                SessionNumber = 1,
                Mode = MdsTrackMode.Mode1,
                SectorSize = (ushort)sectorSize,
                Pregap = 150, // Standard 2-second pregap
                Length = sectorCount,
                StartSector = 0,
                StartOffset = 0
            }
        };

        WriteFromFile(sourceIsoPath, mdfOutputPath, mdsOutputPath, tracks, mediumType, progress);
    }

    /// <summary>Asynchronously writes from ISO.</summary>
    public static async Task WriteFromIsoAsync(
        string sourceIsoPath,
        string mdfOutputPath,
        string mdsOutputPath,
        int sectorSize = 2048,
        MdsMediumType mediumType = MdsMediumType.CdRom,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            WriteFromIso(sourceIsoPath, mdfOutputPath, mdsOutputPath, sectorSize, mediumType, progress);
        }, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    //  Private — MDS descriptor construction
    // ------------------------------------------------------------------

    /// <summary>
    /// Builds the complete MDS binary descriptor from the track layout.
    /// </summary>
    private static byte[] BuildMdsDescriptor(
        List<MdfWriterTrack> tracks, MdsMediumType mediumType,
        string mdfOutputPath, long mdfSize)
    {
        // Group tracks by session
        var sessionGroups = tracks
            .GroupBy(t => t.SessionNumber)
            .OrderBy(g => g.Key)
            .ToList();

        int numSessions = sessionGroups.Count;

        // Calculate MDS file layout offsets
        // Header: 88 bytes
        // Session blocks: numSessions × 24 bytes
        // Track blocks: totalTrackBlocks × 80 bytes
        // Extra blocks: one per regular track × 8 bytes
        // Footer block: one × 16 bytes (shared by all tracks referencing same MDF)
        // Filename string: variable

        // Count total track blocks (lead-in entries + regular tracks)
        int totalTrackBlocks = 0;
        var sessionInfos = new List<SessionLayoutInfo>();

        foreach (var group in sessionGroups)
        {
            var regularTracks = group.OrderBy(t => t.TrackNumber).ToList();
            // Lead-in entries: 3 per session (FIRST, LAST, LEADOUT)
            int leadInBlocks = 3;
            int sessionTrackBlocks = leadInBlocks + regularTracks.Count;

            sessionInfos.Add(new SessionLayoutInfo
            {
                SessionNumber = group.Key,
                Tracks = regularTracks,
                LeadInBlocks = leadInBlocks,
                TotalBlocks = sessionTrackBlocks
            });

            totalTrackBlocks += sessionTrackBlocks;
        }

        int regularTrackCount = tracks.Count;

        int sessionsOffset = HeaderSize;
        int tracksOffset = sessionsOffset + numSessions * SessionBlockSize;
        int extrasOffset = tracksOffset + totalTrackBlocks * TrackBlockSize;
        int footerOffset = extrasOffset + regularTrackCount * TrackExtraBlockSize;
        int filenameOffset = footerOffset + FooterBlockSize;

        // Build filename string. The wildcard pattern "*.mdf" is the standard convention
        // used by Alcohol 120% and other MDS-compatible tools. When reading the MDS file,
        // parsers resolve "*.mdf" by replacing the MDS file's extension with .mdf,
        // allowing the MDS/MDF pair to be freely renamed together while maintaining the
        // reference. This matches the behavior documented in the libmirage MDS parser.
        string mdfFilename = "*.mdf";
        byte[] filenameBytes = Encoding.ASCII.GetBytes(mdfFilename + '\0');

        int totalMdsSize = filenameOffset + filenameBytes.Length;
        byte[] mds = new byte[totalMdsSize];

        // ------ Header (88 bytes) ------
        Array.Copy(MdsSignature, 0, mds, 0, 16);
        mds[16] = 1;   // Version major
        mds[17] = 0x03; // Version minor (1.3 is common)
        WriteUInt16LE(mds, 18, (ushort)mediumType);
        WriteUInt16LE(mds, 20, (ushort)numSessions);
        // bytes 22-23: dummy
        // bytes 24-25: BCA length (0 for CDs)
        // bytes 26-31: dummy
        // bytes 32-35: BCA data offset (0)
        // bytes 36-55: dummy/reserved
        // bytes 56-59: disc structures offset (0 for CDs)
        // bytes 60-71: dummy
        // bytes 72-75: dummy
        // bytes 76-79: dummy
        WriteUInt32LE(mds, 80, (uint)sessionsOffset); // sessions_blocks_offset
        // bytes 84-87: DPM blocks offset (0)

        // ------ Session blocks ------
        int pos = sessionsOffset;
        int currentTrackBlockOffset = tracksOffset;
        int currentExtraOffset = extrasOffset;

        foreach (var si in sessionInfos)
        {
            var firstTrack = si.Tracks.First();
            var lastTrack = si.Tracks.Last();

            // Compute session start/end sectors
            int sessionStart = firstTrack.StartSector > 0
                ? (int)firstTrack.StartSector - (int)firstTrack.Pregap
                : -150; // Standard pre-gap offset
            int sessionEnd = (int)(lastTrack.StartSector + lastTrack.Length);

            WriteInt32LE(mds, pos, sessionStart);
            WriteInt32LE(mds, pos + 4, sessionEnd);
            WriteUInt16LE(mds, pos + 8, (ushort)si.SessionNumber);
            mds[pos + 10] = (byte)si.TotalBlocks;           // num_all_blocks
            mds[pos + 11] = (byte)si.LeadInBlocks;          // num_nontrack_blocks
            WriteUInt16LE(mds, pos + 12, (ushort)firstTrack.TrackNumber); // first_track
            WriteUInt16LE(mds, pos + 14, (ushort)lastTrack.TrackNumber);  // last_track
            // pos + 16: 4 bytes dummy
            WriteUInt32LE(mds, pos + 20, (uint)currentTrackBlockOffset); // tracks_blocks_offset

            // ------ Lead-in track blocks for this session ------
            int trackPos = currentTrackBlockOffset;

            // A0 - first track info
            WriteLeadInTrackBlock(mds, trackPos, (byte)MdsPoint.TrackFirst,
                firstTrack, 0, 0);
            trackPos += TrackBlockSize;

            // A1 - last track info
            WriteLeadInTrackBlock(mds, trackPos, (byte)MdsPoint.TrackLast,
                lastTrack, 0, 0);
            trackPos += TrackBlockSize;

            // A2 - lead-out info
            WriteLeadInTrackBlock(mds, trackPos, (byte)MdsPoint.TrackLeadOut,
                lastTrack, 0, 0);
            trackPos += TrackBlockSize;

            // ------ Regular track blocks ------
            foreach (var track in si.Tracks)
            {
                WriteRegularTrackBlock(mds, trackPos, track,
                    (uint)currentExtraOffset, (uint)footerOffset);
                trackPos += TrackBlockSize;

                // Extra block (pregap + length)
                WriteUInt32LE(mds, currentExtraOffset, track.Pregap);
                WriteUInt32LE(mds, currentExtraOffset + 4, track.Length);
                currentExtraOffset += TrackExtraBlockSize;
            }

            currentTrackBlockOffset = trackPos;
            pos += SessionBlockSize;
        }

        // ------ Footer block (shared) ------
        WriteUInt32LE(mds, footerOffset, (uint)filenameOffset);
        // footer + 4: widechar_filename = 0 (ASCII)
        // footer + 8, +12: dummy

        // ------ Filename string ------
        Array.Copy(filenameBytes, 0, mds, filenameOffset, filenameBytes.Length);

        return mds;
    }

    /// <summary>Writes a lead-in (TOC descriptor) track block.</summary>
    private static void WriteLeadInTrackBlock(
        byte[] mds, int offset, byte point, MdfWriterTrack refTrack,
        uint extraOffset, uint footerOffset)
    {
        // mode: depends on track type
        mds[offset] = refTrack.IsAudio ? (byte)0xA9 : (byte)0xAA;
        mds[offset + 1] = 0; // subchannel = none
        mds[offset + 2] = (byte)((1 << 4) | (refTrack.IsAudio ? 0x00 : 0x04)); // ADR=1, CTL
        mds[offset + 3] = 0; // TNO = 0 for lead-in entries
        mds[offset + 4] = point;
        // MSF fields (min/sec/frame) — set to 0 for lead-in
        // PMSF for FIRST (A0): point to first track number
        if (point == (byte)MdsPoint.TrackFirst)
        {
            mds[offset + 9] = (byte)refTrack.TrackNumber; // PMin = first track
        }
        else if (point == (byte)MdsPoint.TrackLast)
        {
            mds[offset + 9] = (byte)refTrack.TrackNumber; // PMin = last track
        }
        else if (point == (byte)MdsPoint.TrackLeadOut)
        {
            // PMSF = lead-out start position in MSF
            long leadOutLba = refTrack.StartSector + refTrack.Length;
            var (m, s, f) = LbaToMsfComponents(leadOutLba);
            mds[offset + 9] = m;
            mds[offset + 10] = s;
            mds[offset + 11] = f;
        }
        // Remaining fields: zero (no extra, no footer, no start offset, no sector size)
    }

    /// <summary>Writes a regular track block.</summary>
    private static void WriteRegularTrackBlock(
        byte[] mds, int offset, MdfWriterTrack track,
        uint extraOffset, uint footerOffset)
    {
        // Mode byte (lower nibble encodes track mode)
        mds[offset] = (byte)track.Mode;

        // Subchannel mode
        mds[offset + 1] = track.SubchannelSize > 0
            ? (byte)MdsSubchannelMode.PwInterleaved
            : (byte)MdsSubchannelMode.None;

        // ADR/CTL: ADR=1 (mode-1 Q), CTL derived from track type
        byte ctl = track.IsAudio ? (byte)0x00 : (byte)0x04;
        mds[offset + 2] = (byte)((1 << 4) | ctl);

        // TNO = 0 for regular track entries in TOC
        mds[offset + 3] = 0;

        // Point = track number
        mds[offset + 4] = (byte)track.TrackNumber;

        // MSF fields — typically 0 for regular track blocks
        // Zero field
        mds[offset + 8] = 0;

        // PMSF — absolute time of track start
        var (pm, ps, pf) = LbaToMsfComponents(track.StartSector);
        mds[offset + 9] = pm;
        mds[offset + 10] = ps;
        mds[offset + 11] = pf;

        // Extra offset
        WriteUInt32LE(mds, offset + 12, extraOffset);

        // Sector size (including subchannel)
        WriteUInt16LE(mds, offset + 16, (ushort)(track.SectorSize + track.SubchannelSize));

        // bytes 18-35: reserved/unknown (18 bytes)

        // Start sector (PLBA)
        WriteUInt32LE(mds, offset + 36, (uint)track.StartSector);

        // Start offset in MDF (64-bit)
        WriteUInt64LE(mds, offset + 40, track.StartOffset);

        // Number of files (1 for single MDF)
        WriteUInt32LE(mds, offset + 48, 1);

        // Footer offset
        WriteUInt32LE(mds, offset + 52, footerOffset);

        // bytes 56-79: reserved (24 bytes)
    }

    // ------------------------------------------------------------------
    //  Private — Binary write helpers
    // ------------------------------------------------------------------

    private static void WriteUInt16LE(byte[] buf, int offset, ushort value)
    {
        buf[offset] = (byte)(value & 0xFF);
        buf[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    private static void WriteInt32LE(byte[] buf, int offset, int value)
    {
        buf[offset] = (byte)(value & 0xFF);
        buf[offset + 1] = (byte)((value >> 8) & 0xFF);
        buf[offset + 2] = (byte)((value >> 16) & 0xFF);
        buf[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    private static void WriteUInt32LE(byte[] buf, int offset, uint value)
    {
        buf[offset] = (byte)(value & 0xFF);
        buf[offset + 1] = (byte)((value >> 8) & 0xFF);
        buf[offset + 2] = (byte)((value >> 16) & 0xFF);
        buf[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    private static void WriteUInt64LE(byte[] buf, int offset, ulong value)
    {
        for (int i = 0; i < 8; i++)
        {
            buf[offset + i] = (byte)(value & 0xFF);
            value >>= 8;
        }
    }

    /// <summary>Converts LBA to MSF components (for pre-gap adjusted addresses, add 150).</summary>
    private static (byte min, byte sec, byte frame) LbaToMsfComponents(long lba)
    {
        // MSF addresses include the 2-second (150-frame) offset
        long absoluteFrames = lba + 150;
        if (absoluteFrames < 0) absoluteFrames = 0;

        byte frame = (byte)(absoluteFrames % 75);
        long totalSeconds = absoluteFrames / 75;
        byte sec = (byte)(totalSeconds % 60);
        byte min = (byte)(totalSeconds / 60);
        return (min, sec, frame);
    }

    /// <summary>Copies an entire stream with progress reporting.</summary>
    private static void CopyStreamWithProgress(Stream source, Stream dest, IProgress<int>? progress)
    {
        byte[] buf = new byte[81920];
        long copied = 0;
        long total = 0;

        // Try to get total length for progress
        try { total = source.Length; } catch { /* non-seekable stream */ }

        int read;
        while ((read = source.Read(buf, 0, buf.Length)) > 0)
        {
            dest.Write(buf, 0, read);
            copied += read;
            if (total > 0)
                progress?.Report((int)(copied * 100 / total));
        }
        progress?.Report(100);
    }

    // ------------------------------------------------------------------
    //  Helper types
    // ------------------------------------------------------------------

    /// <summary>Internal struct for session layout calculation.</summary>
    private class SessionLayoutInfo
    {
        public int SessionNumber { get; set; }
        public List<MdfWriterTrack> Tracks { get; set; } = new();
        public int LeadInBlocks { get; set; }
        public int TotalBlocks { get; set; }
    }
}

/// <summary>
/// Describes a track for use with <see cref="MdfWriter"/>.
/// Provides the information needed to construct MDS track blocks.
/// </summary>
public class MdfWriterTrack
{
    /// <summary>Track number (1-based).</summary>
    public int TrackNumber { get; set; }

    /// <summary>Session number (1-based).</summary>
    public int SessionNumber { get; set; } = 1;

    /// <summary>Track mode.</summary>
    public MdsTrackMode Mode { get; set; } = MdsTrackMode.Mode1;

    /// <summary>
    /// Sector size in bytes (excluding subchannel).
    /// Common values: 2048 (cooked Mode1), 2336 (cooked Mode2), 2352 (raw).
    /// </summary>
    public ushort SectorSize { get; set; } = 2048;

    /// <summary>
    /// Subchannel data size per sector (0 or 96).
    /// </summary>
    public int SubchannelSize { get; set; }

    /// <summary>Number of sectors in the pregap.</summary>
    public uint Pregap { get; set; }

    /// <summary>Number of sectors in the track data (excluding pregap).</summary>
    public uint Length { get; set; }

    /// <summary>Track start sector (LBA).</summary>
    public uint StartSector { get; set; }

    /// <summary>Byte offset in the MDF file where this track's data begins.</summary>
    public ulong StartOffset { get; set; }

    /// <summary>Whether this is an audio track.</summary>
    public bool IsAudio => Mode == MdsTrackMode.Audio;
}
