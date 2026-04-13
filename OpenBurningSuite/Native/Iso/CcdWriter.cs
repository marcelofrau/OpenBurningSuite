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
using OpenBurningSuite.Native.Optical;

namespace OpenBurningSuite.Native.Iso;

/// <summary>
/// Writer for CloneCD disc image files (.ccd + .img + .sub).
///
/// Creates the three-file CloneCD disc image format:
///   .ccd — INI-style text file describing the disc structure
///   .img — Raw 2352-byte sector data
///   .sub — (Optional) 96-byte interleaved P-W subchannel data
///
/// This writer supports:
/// - Creating CCD/IMG/SUB from TOC data and raw sector streams
/// - Creating CCD/IMG/SUB from existing BIN/CUE images
/// - Multi-session and multi-track disc layouts
/// - Audio, Mode 1, and Mode 2 track types
/// - Optional subchannel data capture
/// </summary>
public static class CcdWriter
{
    // -----------------------------------------------------------------------
    //  Constants
    // -----------------------------------------------------------------------

    /// <summary>CloneCD format version.</summary>
    private const int CcdVersion = 3;

    /// <summary>Raw sector size.</summary>
    private const int RawSectorSize = 2352;

    /// <summary>Subchannel data size per sector.</summary>
    private const int SubchannelSize = 96;

    // -----------------------------------------------------------------------
    //  Public API — Write from TOC data
    // -----------------------------------------------------------------------

    /// <summary>
    /// Creates a CCD/IMG/SUB image set from TOC data and a raw sector data stream.
    /// </summary>
    /// <param name="toc">Table of contents from the disc.</param>
    /// <param name="sectorDataStream">Stream containing raw 2352-byte sectors.</param>
    /// <param name="subchannelStream">Optional stream containing 96-byte subchannel data per sector. May be null.</param>
    /// <param name="ccdOutputPath">Destination path for the .ccd file.</param>
    /// <param name="totalSectors">Total number of sectors.</param>
    /// <param name="progress">Optional progress reporter (0–100).</param>
    public static void Write(
        TocData toc,
        Stream sectorDataStream,
        Stream? subchannelStream,
        string ccdOutputPath,
        uint totalSectors,
        IProgress<int>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(toc);
        ArgumentNullException.ThrowIfNull(sectorDataStream);

        var imgPath = Path.ChangeExtension(ccdOutputPath, ".img");
        var subPath = Path.ChangeExtension(ccdOutputPath, ".sub");

        // Write IMG file (raw sector data)
        using (var imgStream = new FileStream(imgPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
        {
            CopyStreamWithProgress(sectorDataStream, imgStream, totalSectors * (long)RawSectorSize, progress);
        }

        // Write SUB file if subchannel data is available
        bool hasSub = false;
        if (subchannelStream != null)
        {
            using var subStream = new FileStream(subPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
            CopyStreamWithProgress(subchannelStream, subStream, totalSectors * (long)SubchannelSize, null);
            hasSub = true;
        }

        // Generate and write CCD file
        var ccdContent = GenerateCcdFromToc(toc, totalSectors, hasSub);
        File.WriteAllText(ccdOutputPath, ccdContent, Encoding.ASCII);
    }

    /// <summary>Asynchronously writes CCD/IMG/SUB from TOC and streams.</summary>
    public static async Task WriteAsync(
        TocData toc,
        Stream sectorDataStream,
        Stream? subchannelStream,
        string ccdOutputPath,
        uint totalSectors,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            Write(toc, sectorDataStream, subchannelStream, ccdOutputPath, totalSectors, progress);
        }, ct).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------
    //  Public API — Write from BIN/CUE
    // -----------------------------------------------------------------------

    /// <summary>
    /// Creates a CCD/IMG/SUB image set from an existing BIN file and CCD image model.
    /// </summary>
    public static void WriteFromBin(
        string sourceBinPath,
        CcdImage imageModel,
        string ccdOutputPath,
        IProgress<int>? progress = null)
    {
        var imgPath = Path.ChangeExtension(ccdOutputPath, ".img");

        // Copy BIN to IMG (same format: raw 2352-byte sectors)
        CopyFile(sourceBinPath, imgPath, progress);

        // Generate and write CCD file from the model
        var ccdContent = GenerateCcdFromModel(imageModel);
        File.WriteAllText(ccdOutputPath, ccdContent, Encoding.ASCII);
    }

    // -----------------------------------------------------------------------
    //  Public API — Write from raw data
    // -----------------------------------------------------------------------

    /// <summary>
    /// Creates a CCD/IMG pair from raw sector data with a simple single-track layout.
    /// Used when reading a disc directly to CCD format.
    /// </summary>
    /// <param name="sectorDataPath">Path to the raw sector data file (.img).</param>
    /// <param name="ccdOutputPath">Destination path for the .ccd file.</param>
    /// <param name="toc">TOC data from the disc.</param>
    /// <param name="hasSubchannel">Whether subchannel data is included.</param>
    public static void WriteFromRawData(
        string sectorDataPath,
        string ccdOutputPath,
        TocData toc,
        bool hasSubchannel)
    {
        // Determine total sectors from file size
        var imgFileInfo = new FileInfo(sectorDataPath);
        int sectorSize = hasSubchannel ? RawSectorSize + SubchannelSize : RawSectorSize;
        uint totalSectors = (uint)(imgFileInfo.Length / sectorSize);

        // If subchannel data is interleaved, separate it into .sub file
        if (hasSubchannel && imgFileInfo.Length > 0)
        {
            var imgPath = Path.ChangeExtension(ccdOutputPath, ".img");
            var subPath = Path.ChangeExtension(ccdOutputPath, ".sub");

            SeparateSubchannelData(sectorDataPath, imgPath, subPath, totalSectors);
        }
        else
        {
            // No subchannel: rename/copy raw data to .img
            var imgPath = Path.ChangeExtension(ccdOutputPath, ".img");
            if (!string.Equals(sectorDataPath, imgPath, StringComparison.OrdinalIgnoreCase))
                File.Copy(sectorDataPath, imgPath, overwrite: true);
        }

        // Generate CCD content
        var ccdContent = GenerateCcdFromToc(toc, totalSectors, hasSubchannel);
        File.WriteAllText(ccdOutputPath, ccdContent, Encoding.ASCII);
    }

    // -----------------------------------------------------------------------
    //  CCD content generation
    // -----------------------------------------------------------------------

    /// <summary>
    /// Generates the CCD file content from TOC data.
    /// </summary>
    private static string GenerateCcdFromToc(TocData toc, uint totalSectors, bool hasSubchannel)
    {
        var sb = new StringBuilder();

        // Determine disc properties
        int sessionCount = 1; // Most CDs have single session
        var trackEntries = toc.Entries.Where(e => e.TrackNumber != 0xAA).ToList();
        var leadOut = toc.Entries.FirstOrDefault(e => e.TrackNumber == 0xAA);

        // Build entry list (includes A0, A1, A2 per session + one per track)
        var entries = BuildEntriesFromToc(toc);

        // [CloneCD]
        sb.AppendLine("[CloneCD]");
        sb.AppendLine($"Version={CcdVersion}");
        sb.AppendLine();

        // [Disc]
        sb.AppendLine("[Disc]");
        sb.AppendLine($"TocEntries={entries.Count}");
        sb.AppendLine($"Sessions={sessionCount}");
        sb.AppendLine("DataTracksScrambled=0");
        sb.AppendLine("CDTextLength=0");
        sb.AppendLine();

        // [Session 1]
        for (int s = 1; s <= sessionCount; s++)
        {
            sb.AppendLine($"[Session {s}]");
            // Determine pregap mode from first track in session
            var firstTrackInSession = trackEntries.FirstOrDefault();
            int pregapMode = firstTrackInSession != null && firstTrackInSession.IsData ? 1 : 0;
            sb.AppendLine($"PreGapMode={pregapMode}");
            sb.AppendLine($"PreGapSubC={(hasSubchannel ? 1 : 0)}");
            sb.AppendLine();
        }

        // [Entry N]
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            sb.AppendLine($"[Entry {i}]");
            sb.AppendLine($"Session={entry.Session}");
            sb.AppendLine($"Point=0x{entry.Point:x2}");
            sb.AppendLine($"ADR=0x{entry.Adr:x2}");
            sb.AppendLine($"Control=0x{entry.Control:x2}");
            sb.AppendLine($"TrackNo={entry.TrackNo}");
            sb.AppendLine($"AMin={entry.AMin}");
            sb.AppendLine($"ASec={entry.ASec}");
            sb.AppendLine($"AFrame={entry.AFrame}");
            sb.AppendLine($"ALBA={entry.Alba}");
            sb.AppendLine($"Zero={entry.Zero}");
            sb.AppendLine($"PMin={entry.PMin}");
            sb.AppendLine($"PSec={entry.PSec}");
            sb.AppendLine($"PFrame={entry.PFrame}");
            sb.AppendLine($"PLBA={entry.Plba}");
            sb.AppendLine();
        }

        // [TRACK N]
        foreach (var tocEntry in trackEntries)
        {
            sb.AppendLine($"[TRACK {tocEntry.TrackNumber}]");
            int mode = tocEntry.IsData ? 1 : 0;
            sb.AppendLine($"MODE={mode}");

            // INDEX 0 and INDEX 1
            // INDEX 0 = pregap start (if there's a gap before the track)
            // INDEX 1 = track data start
            sb.AppendLine($"INDEX 1={tocEntry.StartLba}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Generates CCD content from a CcdImage model.
    /// </summary>
    private static string GenerateCcdFromModel(CcdImage model)
    {
        var sb = new StringBuilder();

        // [CloneCD]
        sb.AppendLine("[CloneCD]");
        sb.AppendLine($"Version={model.Version}");
        sb.AppendLine();

        // [Disc]
        sb.AppendLine("[Disc]");
        sb.AppendLine($"TocEntries={model.Entries.Count}");
        sb.AppendLine($"Sessions={model.SessionCount}");
        sb.AppendLine($"DataTracksScrambled={(model.DataTracksScrambled ? 1 : 0)}");
        sb.AppendLine($"CDTextLength={model.CdTextLength}");
        if (!string.IsNullOrEmpty(model.Catalog))
            sb.AppendLine($"CATALOG={model.Catalog}");
        sb.AppendLine();

        // [Session N]
        foreach (var session in model.Sessions)
        {
            sb.AppendLine($"[Session {session.SessionNumber}]");
            sb.AppendLine($"PreGapMode={session.PreGapMode}");
            sb.AppendLine($"PreGapSubC={session.PreGapSubC}");
            sb.AppendLine();
        }

        // [Entry N]
        for (int i = 0; i < model.Entries.Count; i++)
        {
            var entry = model.Entries[i];
            sb.AppendLine($"[Entry {i}]");
            sb.AppendLine($"Session={entry.Session}");
            sb.AppendLine($"Point=0x{entry.Point:x2}");
            sb.AppendLine($"ADR=0x{entry.Adr:x2}");
            sb.AppendLine($"Control=0x{entry.Control:x2}");
            sb.AppendLine($"TrackNo={entry.TrackNo}");
            sb.AppendLine($"AMin={entry.AMin}");
            sb.AppendLine($"ASec={entry.ASec}");
            sb.AppendLine($"AFrame={entry.AFrame}");
            sb.AppendLine($"ALBA={entry.Alba}");
            sb.AppendLine($"Zero={entry.Zero}");
            sb.AppendLine($"PMin={entry.PMin}");
            sb.AppendLine($"PSec={entry.PSec}");
            sb.AppendLine($"PFrame={entry.PFrame}");
            sb.AppendLine($"PLBA={entry.Plba}");
            sb.AppendLine();
        }

        // [TRACK N]
        foreach (var track in model.Tracks.OrderBy(t => t.TrackNumber))
        {
            sb.AppendLine($"[TRACK {track.TrackNumber}]");
            sb.AppendLine($"MODE={(int)track.Mode}");

            if (!string.IsNullOrEmpty(track.Isrc))
                sb.AppendLine($"ISRC={track.Isrc}");

            foreach (var idx in track.Indices.OrderBy(kv => kv.Key))
                sb.AppendLine($"INDEX {idx.Key}={idx.Value}");

            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Builds CCD-format TOC entries from a TocData structure.
    /// Generates A0 (first track), A1 (last track), A2 (lead-out) entries,
    /// plus one entry per regular track.
    /// </summary>
    private static List<CcdEntry> BuildEntriesFromToc(TocData toc)
    {
        var entries = new List<CcdEntry>();
        var trackEntries = toc.Entries.Where(e => e.TrackNumber != 0xAA).ToList();
        var leadOut = toc.Entries.FirstOrDefault(e => e.TrackNumber == 0xAA);

        if (trackEntries.Count == 0) return entries;

        var firstTrack = trackEntries.First();
        var lastTrack = trackEntries.Last();

        // Determine disc type from first data track
        // 0x00 = CD-DA/CD-ROM, 0x20 = CD-ROM/XA (Mode 2)
        byte discTypePSec = firstTrack.IsData ? (byte)0x20 : (byte)0x00;

        // A0 — First track pointer
        entries.Add(new CcdEntry
        {
            Session = 1,
            Point = 0xA0,
            Adr = 1,
            Control = firstTrack.Control,
            TrackNo = 0,
            Alba = -150,
            PMin = firstTrack.TrackNumber,
            PSec = discTypePSec,
            PFrame = 0,
            Plba = LbaFromMsf(firstTrack.TrackNumber, discTypePSec, 0)
        });

        // A1 — Last track pointer
        entries.Add(new CcdEntry
        {
            Session = 1,
            Point = 0xA1,
            Adr = 1,
            Control = lastTrack.Control,
            TrackNo = 0,
            Alba = -150,
            PMin = lastTrack.TrackNumber,
            PSec = 0,
            PFrame = 0,
            Plba = LbaFromMsf(lastTrack.TrackNumber, 0, 0)
        });

        // A2 — Lead-out
        if (leadOut != null)
        {
            var (m, s, f) = LbaToMsf(leadOut.StartLba);
            entries.Add(new CcdEntry
            {
                Session = 1,
                Point = 0xA2,
                Adr = 1,
                Control = firstTrack.Control,
                TrackNo = 0,
                Alba = -150,
                PMin = m,
                PSec = s,
                PFrame = f,
                Plba = (int)leadOut.StartLba
            });
        }
        else
        {
            // Estimate lead-out from last track
            entries.Add(new CcdEntry
            {
                Session = 1,
                Point = 0xA2,
                Adr = 1,
                Control = firstTrack.Control,
                TrackNo = 0,
                Alba = -150,
                PMin = 0, PSec = 0, PFrame = 0, Plba = 0
            });
        }

        // Regular track entries
        foreach (var tocEntry in trackEntries)
        {
            var (m, s, f) = LbaToMsf(tocEntry.StartLba);
            entries.Add(new CcdEntry
            {
                Session = 1,
                Point = tocEntry.TrackNumber,
                Adr = 1,
                Control = tocEntry.Control,
                TrackNo = 0,
                Alba = -150,
                PMin = m,
                PSec = s,
                PFrame = f,
                Plba = (int)tocEntry.StartLba
            });
        }

        return entries;
    }

    // -----------------------------------------------------------------------
    //  Private helpers
    // -----------------------------------------------------------------------

    /// <summary>Converts LBA to MSF (with standard 150-frame offset).</summary>
    private static (byte min, byte sec, byte frame) LbaToMsf(uint lba)
    {
        long absoluteFrames = lba + 150;
        byte frame = (byte)(absoluteFrames % 75);
        long totalSeconds = absoluteFrames / 75;
        byte sec = (byte)(totalSeconds % 60);
        byte min = (byte)(totalSeconds / 60);
        return (min, sec, frame);
    }

    /// <summary>Converts MSF to LBA (with standard 150-frame offset).</summary>
    private static int LbaFromMsf(int min, int sec, int frame)
    {
        return (min * 60 + sec) * 75 + frame - 150;
    }

    /// <summary>
    /// Separates interleaved sector+subchannel data into separate .img and .sub files.
    /// Input format: [2352 bytes sector][96 bytes subchannel] repeated.
    /// </summary>
    private static void SeparateSubchannelData(string interleavedPath, string imgPath, string subPath,
        uint totalSectors)
    {
        using var input = new FileStream(interleavedPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var imgOut = new FileStream(imgPath, FileMode.Create, FileAccess.Write);
        using var subOut = new FileStream(subPath, FileMode.Create, FileAccess.Write);

        var sectorBuf = new byte[RawSectorSize];
        var subBuf = new byte[SubchannelSize];

        for (uint i = 0; i < totalSectors; i++)
        {
            int read = input.Read(sectorBuf, 0, RawSectorSize);
            if (read < RawSectorSize) break;
            imgOut.Write(sectorBuf, 0, RawSectorSize);

            read = input.Read(subBuf, 0, SubchannelSize);
            if (read < SubchannelSize) break;
            subOut.Write(subBuf, 0, SubchannelSize);
        }
    }

    /// <summary>Copies stream data with progress reporting.</summary>
    private static void CopyStreamWithProgress(Stream source, Stream dest, long expectedSize,
        IProgress<int>? progress)
    {
        var buffer = new byte[65536];
        long copied = 0;
        int read;

        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            dest.Write(buffer, 0, read);
            copied += read;
            if (expectedSize > 0)
                progress?.Report((int)(copied * 100 / expectedSize));
        }
    }

    /// <summary>Copies a file with progress reporting.</summary>
    private static void CopyFile(string source, string dest, IProgress<int>? progress)
    {
        using var srcStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var dstStream = new FileStream(dest, FileMode.Create, FileAccess.Write);
        CopyStreamWithProgress(srcStream, dstStream, srcStream.Length, progress);
    }
}
