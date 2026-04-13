// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenBurningSuite.Models;

namespace OpenBurningSuite.Native.Iso;

/// <summary>
/// Parser for Nero NRG disc image files.
///
/// The NRG format stores disc data followed by an IFF-style chunk chain
/// and a footer at the end of the file.
///
/// Supports both NRG v1 (32-bit offsets, "NERO" footer) and
/// NRG v2 (64-bit offsets, "NER5" footer).
///
/// Reference: Unofficial NRG specification from nrg2iso, PSXSPX, and community documentation.
/// All integer values in NRG chunks are big-endian unsigned.
/// </summary>
public static class NrgParser
{
    // NRG footer magic values
    private static readonly byte[] NeroV1Magic = Encoding.ASCII.GetBytes("NERO");
    private static readonly byte[] NeroV2Magic = Encoding.ASCII.GetBytes("NER5");

    // Standard CD sync pattern (12 bytes at start of each raw sector)
    private static readonly byte[] CdSyncPattern =
    {
        0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        0xFF, 0xFF, 0xFF, 0x00
    };

    /// <summary>Parses an NRG image file synchronously.</summary>
    public static NrgImage Parse(string nrgFilePath)
    {
        var image = new NrgImage { FilePath = nrgFilePath };
        try
        {
            if (!File.Exists(nrgFilePath))
            {
                image.ErrorMessage = $"File not found: {nrgFilePath}";
                return image;
            }

            using var stream = new FileStream(nrgFilePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 8192, false);
            ParseInternal(stream, image);
        }
        catch (Exception ex)
        {
            image.IsValid = false;
            image.ErrorMessage = $"Parse error: {ex.Message}";
        }
        return image;
    }

    /// <summary>Parses an NRG image file asynchronously.</summary>
    public static async Task<NrgImage> ParseAsync(string nrgFilePath, CancellationToken ct = default)
    {
        return await Task.Run(() => Parse(nrgFilePath), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Extracts user data from the largest data track in an NRG image to an ISO file.
    /// </summary>
    public static void ExtractToIso(string nrgFilePath, NrgImage image, string outputIsoPath,
        IProgress<int>? progress = null)
    {
        if (!image.IsValid)
            throw new InvalidOperationException($"NRG image is not valid: {image.ErrorMessage}");

        using var input = new FileStream(nrgFilePath, FileMode.Open, FileAccess.Read,
            FileShare.Read, 65536, false);
        using var output = new FileStream(outputIsoPath, FileMode.Create, FileAccess.Write,
            FileShare.None, 65536, false);

        if (image.IsDao)
        {
            var track = image.LargestDaoDataTrack ?? image.DaoTracks.FirstOrDefault();
            if (track == null)
                throw new InvalidOperationException("No data track found in NRG image.");
            ExtractDaoTrackUserData(input, output, track, progress);
        }
        else
        {
            var track = image.LargestTaoDataTrack ?? image.TaoTracks.FirstOrDefault();
            if (track == null)
                throw new InvalidOperationException("No data track found in NRG image.");
            ExtractTaoTrackUserData(input, output, track, progress);
        }
    }

    /// <summary>Asynchronously extracts user data to an ISO file.</summary>
    public static async Task ExtractToIsoAsync(string nrgFilePath, NrgImage image,
        string outputIsoPath, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        await Task.Run(() => ExtractToIso(nrgFilePath, image, outputIsoPath, progress), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Converts an NRG image to BIN/CUE format.
    /// </summary>
    public static void ConvertToBinCue(string nrgFilePath, NrgImage image,
        string outputBinPath, string outputCuePath, IProgress<int>? progress = null)
    {
        if (!image.IsValid)
            throw new InvalidOperationException($"NRG image is not valid: {image.ErrorMessage}");

        using var input = new FileStream(nrgFilePath, FileMode.Open, FileAccess.Read,
            FileShare.Read, 65536, false);
        using var output = new FileStream(outputBinPath, FileMode.Create, FileAccess.Write,
            FileShare.None, 65536, false);

        var cueLines = new List<string>();
        cueLines.Add($"FILE \"{Path.GetFileName(outputBinPath)}\" BINARY");

        if (image.IsDao)
            ConvertDaoToBin(input, output, image, cueLines, progress);
        else
            ConvertTaoToBin(input, output, image, cueLines, progress);

        File.WriteAllText(outputCuePath, string.Join(Environment.NewLine, cueLines) + Environment.NewLine);
    }

    /// <summary>Asynchronously converts to BIN/CUE format.</summary>
    public static async Task ConvertToBinCueAsync(string nrgFilePath, NrgImage image,
        string outputBinPath, string outputCuePath,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        await Task.Run(() => ConvertToBinCue(nrgFilePath, image, outputBinPath, outputCuePath, progress), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Extracts all data tracks from an NRG image to an output stream.
    /// </summary>
    public static void ExtractAllDataTracks(string nrgFilePath, NrgImage image,
        Stream outputStream, IProgress<int>? progress = null)
    {
        if (!image.IsValid)
            throw new InvalidOperationException($"NRG image is not valid: {image.ErrorMessage}");

        using var input = new FileStream(nrgFilePath, FileMode.Open, FileAccess.Read,
            FileShare.Read, 65536, false);

        if (image.IsDao)
        {
            var dataTracks = image.DaoTracks.Where(t => t.IsData).ToList();
            for (int i = 0; i < dataTracks.Count; i++)
            {
                var subProgress = progress != null
                    ? new Progress<int>(p => progress.Report(
                        (i * 100 + p) / dataTracks.Count))
                    : null;
                ExtractDaoTrackUserData(input, outputStream, dataTracks[i], subProgress);
            }
        }
        else
        {
            var dataTracks = image.TaoTracks.Where(t => t.IsData).ToList();
            for (int i = 0; i < dataTracks.Count; i++)
            {
                var subProgress = progress != null
                    ? new Progress<int>(p => progress.Report(
                        (i * 100 + p) / dataTracks.Count))
                    : null;
                ExtractTaoTrackUserData(input, outputStream, dataTracks[i], subProgress);
            }
        }
    }

    /// <summary>Asynchronously extracts all data tracks.</summary>
    public static async Task ExtractAllDataTracksAsync(string nrgFilePath, NrgImage image,
        Stream outputStream, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        await Task.Run(() => ExtractAllDataTracks(nrgFilePath, image, outputStream, progress), ct)
            .ConfigureAwait(false);
    }

    /// <summary>Validates an NRG image for integrity.</summary>
    public static List<string> ValidateImage(NrgImage image)
    {
        var issues = new List<string>();
        if (!image.IsValid)
        {
            issues.Add($"Image parse error: {image.ErrorMessage}");
            return issues;
        }

        if (image.TotalTrackCount == 0)
            issues.Add("No tracks found in NRG image.");

        if (image.IsDao)
        {
            foreach (var track in image.DaoTracks)
            {
                if (track.SectorSize <= 0)
                    issues.Add($"Track {track.TrackNumber}: invalid sector size {track.SectorSize}");
                if (track.DataLength <= 0)
                    issues.Add($"Track {track.TrackNumber}: invalid data length {track.DataLength}");
                if (track.EndOffset > image.FileSize)
                    issues.Add($"Track {track.TrackNumber}: end offset 0x{track.EndOffset:X} exceeds file size");
                if (track.DataLength % track.SectorSize != 0)
                    issues.Add($"Track {track.TrackNumber}: data length {track.DataLength} is not a multiple of sector size {track.SectorSize}");
            }
        }
        else
        {
            foreach (var track in image.TaoTracks)
            {
                if (track.SectorSize <= 0)
                    issues.Add($"Track {track.TrackNumber}: invalid sector size {track.SectorSize}");
                if (track.DataLength <= 0)
                    issues.Add($"Track {track.TrackNumber}: invalid data length {track.DataLength}");
                if (track.FileOffset + track.DataLength > image.FileSize)
                    issues.Add($"Track {track.TrackNumber}: extends beyond file size");
            }
        }

        if (image.ChunkOffset > image.FileSize)
            issues.Add($"Chunk offset 0x{image.ChunkOffset:X} exceeds file size");

        return issues;
    }

    /// <summary>Asynchronously validates an NRG image.</summary>
    public static async Task<List<string>> ValidateImageAsync(NrgImage image,
        CancellationToken ct = default)
    {
        return await Task.Run(() => ValidateImage(image), ct).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------
    //  Internal parsing logic
    // -----------------------------------------------------------------------

    private static void ParseInternal(FileStream stream, NrgImage image)
    {
        image.FileSize = stream.Length;
        if (image.FileSize < 12)
        {
            image.ErrorMessage = "File too small to be a valid NRG image.";
            return;
        }

        // Read footer: try V2 (NER5) first, then V1 (NERO)
        var footer = new byte[12];
        stream.Seek(-12, SeekOrigin.End);
        ReadExact(stream, footer, 0, 12);

        // Check for NER5 at offset 0 of the 12-byte footer
        if (footer[0] == NeroV2Magic[0] && footer[1] == NeroV2Magic[1] &&
            footer[2] == NeroV2Magic[2] && footer[3] == NeroV2Magic[3])
        {
            image.Version = NrgVersion.V2;
            image.ChunkOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(footer.AsSpan(4));
        }
        // Check for NERO at offset 4 of the 12-byte footer (V1 footer is 8 bytes)
        else if (footer[4] == NeroV1Magic[0] && footer[5] == NeroV1Magic[1] &&
                 footer[6] == NeroV1Magic[2] && footer[7] == NeroV1Magic[3])
        {
            image.Version = NrgVersion.V1;
            image.ChunkOffset = BinaryPrimitives.ReadUInt32BigEndian(footer.AsSpan(8));
        }
        else
        {
            image.ErrorMessage = "Not a valid NRG file: footer magic not found.";
            return;
        }

        if (image.ChunkOffset <= 0 || image.ChunkOffset >= image.FileSize)
        {
            image.ErrorMessage = $"Invalid chunk offset: 0x{image.ChunkOffset:X}";
            return;
        }

        // Parse the chunk chain
        stream.Seek(image.ChunkOffset, SeekOrigin.Begin);
        int sessionIndex = 0;
        int daoTrackIndex = 0;
        int taoTrackIndex = 0;

        while (stream.Position < image.FileSize - 8)
        {
            var chunkIdBuf = new byte[4];
            if (ReadExact(stream, chunkIdBuf, 0, 4) < 4) break;
            string chunkId = Encoding.ASCII.GetString(chunkIdBuf);

            var sizeBuf = new byte[4];
            if (ReadExact(stream, sizeBuf, 0, 4) < 4) break;
            uint chunkSize = BinaryPrimitives.ReadUInt32BigEndian(sizeBuf);

            if (chunkId == "END!")
                break;

            long chunkDataStart = stream.Position;

            switch (chunkId)
            {
                case "CUES":
                    ParseCuesV1(stream, chunkSize, image);
                    break;
                case "CUEX":
                    ParseCuesV2(stream, chunkSize, image);
                    break;
                case "DAOI":
                    ParseDaoiV1(stream, chunkSize, image, ref daoTrackIndex, sessionIndex);
                    sessionIndex++;
                    break;
                case "DAOX":
                    ParseDaoxV2(stream, chunkSize, image, ref daoTrackIndex, sessionIndex);
                    sessionIndex++;
                    break;
                case "ETNF":
                    ParseEtnfV1(stream, chunkSize, image, ref taoTrackIndex, sessionIndex);
                    sessionIndex++;
                    break;
                case "ETN2":
                    ParseEtn2V2(stream, chunkSize, image, ref taoTrackIndex, sessionIndex);
                    sessionIndex++;
                    break;
                case "SINF":
                    ParseSinf(stream, chunkSize, image);
                    break;
                case "MTYP":
                    ParseMtyp(stream, chunkSize, image);
                    break;
                case "CDTX":
                    ParseCdtx(stream, chunkSize, image);
                    break;
                case "DINF":
                    ParseDinf(stream, chunkSize, image);
                    break;
                // TOCT, RELO — skip unknown chunks
            }

            // Advance to next chunk (skip any unread data)
            stream.Seek(chunkDataStart + chunkSize, SeekOrigin.Begin);
        }

        // Cross-reference: assign start LBAs from cue entries to DAO tracks
        AssignLbasFromCues(image);

        image.IsValid = image.TotalTrackCount > 0;
        if (!image.IsValid && string.IsNullOrEmpty(image.ErrorMessage))
            image.ErrorMessage = "No tracks found in NRG image.";
    }

    // -----------------------------------------------------------------------
    //  Chunk parsers
    // -----------------------------------------------------------------------

    private static void ParseCuesV1(Stream stream, uint size, NrgImage image)
    {
        // V1 CUES: each entry is 8 bytes (mode, track#, index#, pad, LBA as 4-byte BE)
        int entryCount = (int)(size / 8);
        for (int i = 0; i < entryCount; i++)
        {
            var buf = new byte[8];
            ReadExact(stream, buf, 0, 8);

            image.CueEntries.Add(new NrgCueEntry
            {
                Mode = buf[0],
                TrackNumber = buf[1],
                IndexNumber = buf[2],
                // buf[3] = padding
                Lba = (int)BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(4))
            });
        }
    }

    private static void ParseCuesV2(Stream stream, uint size, NrgImage image)
    {
        // V2 CUEX: same structure as V1 (8 bytes per entry)
        int entryCount = (int)(size / 8);
        for (int i = 0; i < entryCount; i++)
        {
            var buf = new byte[8];
            ReadExact(stream, buf, 0, 8);

            image.CueEntries.Add(new NrgCueEntry
            {
                Mode = buf[0],
                TrackNumber = buf[1],
                IndexNumber = buf[2],
                Lba = (int)BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(4))
            });
        }
    }

    private static void ParseDaoiV1(Stream stream, uint size, NrgImage image,
        ref int trackIndex, int sessionNum)
    {
        // V1 DAOI structure:
        // 4 bytes: chunk size (LE duplicate)
        // 14 bytes: UPC
        // 4 bytes: TOC type
        // 1 byte: first track
        // 1 byte: last track
        // Per track (repeated):
        //   12 bytes: ISRC
        //   4 bytes: sector size
        //   4 bytes: mode
        //   4 bytes: index0 (pregap) offset
        //   4 bytes: index1 (start) offset
        //   4 bytes: end offset

        var header = new byte[24]; // 4 + 14 + 4 + 1 + 1
        ReadExact(stream, header, 0, 24);

        // Read UPC (14 bytes at offset 4)
        string upc = Encoding.ASCII.GetString(header, 4, 14).TrimEnd('\0');
        if (!string.IsNullOrWhiteSpace(upc) && IsValidAscii(upc))
            image.Upc = upc;

        image.TocType = (NrgTocType)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(18));
        int firstTrack = header[22];
        int lastTrack = header[23];

        int trackCount = lastTrack - firstTrack + 1;
        const int trackEntrySize = 12 + 4 + 4 + 4 + 4 + 4; // 32 bytes

        for (int t = 0; t < trackCount; t++)
        {
            var tbuf = new byte[trackEntrySize];
            ReadExact(stream, tbuf, 0, trackEntrySize);

            string isrc = Encoding.ASCII.GetString(tbuf, 0, 12).TrimEnd('\0');
            int sectorSize = (int)BinaryPrimitives.ReadUInt32BigEndian(tbuf.AsSpan(12));
            ushort mode = (ushort)BinaryPrimitives.ReadUInt32BigEndian(tbuf.AsSpan(16));
            long index0 = BinaryPrimitives.ReadUInt32BigEndian(tbuf.AsSpan(20));
            long index1 = BinaryPrimitives.ReadUInt32BigEndian(tbuf.AsSpan(24));
            long end = BinaryPrimitives.ReadUInt32BigEndian(tbuf.AsSpan(28));

            trackIndex++;
            image.DaoTracks.Add(new NrgDaoTrack
            {
                TrackNumber = trackIndex,
                SessionNumber = sessionNum + 1,
                Isrc = isrc,
                SectorSize = sectorSize,
                Mode = (NrgTrackMode)mode,
                Index0Offset = index0,
                Index1Offset = index1,
                EndOffset = end
            });
        }
    }

    private static void ParseDaoxV2(Stream stream, uint size, NrgImage image,
        ref int trackIndex, int sessionNum)
    {
        // V2 DAOX structure:
        // 4 bytes: chunk size (duplicate)
        // 13 bytes: UPC + 1 byte padding
        // 2 bytes: TOC type (16-bit)
        // 1 byte: first track
        // 1 byte: last track
        // Per track (repeated):
        //   12 bytes: ISRC
        //   2 bytes: sector size
        //   2 bytes: mode
        //   2 bytes: unknown (0x0001)
        //   8 bytes: index0 (64-bit)
        //   8 bytes: index1 (64-bit)
        //   8 bytes: end (64-bit)

        var header = new byte[22]; // 4 + 14 + 2 + 1 + 1
        ReadExact(stream, header, 0, 22);

        // UPC: 13 bytes at offset 4 + 1 byte padding
        string upc = Encoding.ASCII.GetString(header, 4, 13).TrimEnd('\0');
        if (!string.IsNullOrWhiteSpace(upc) && IsValidAscii(upc))
            image.Upc = upc;

        image.TocType = (NrgTocType)BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(18));
        int firstTrack = header[20];
        int lastTrack = header[21];

        int trackCount = lastTrack - firstTrack + 1;
        const int trackEntrySize = 12 + 2 + 2 + 2 + 8 + 8 + 8; // 42 bytes

        for (int t = 0; t < trackCount; t++)
        {
            var tbuf = new byte[trackEntrySize];
            ReadExact(stream, tbuf, 0, trackEntrySize);

            string isrc = Encoding.ASCII.GetString(tbuf, 0, 12).TrimEnd('\0');
            int sectorSize = BinaryPrimitives.ReadUInt16BigEndian(tbuf.AsSpan(12));
            ushort mode = BinaryPrimitives.ReadUInt16BigEndian(tbuf.AsSpan(14));
            // tbuf[16..17] = unknown (skip)
            long index0 = (long)BinaryPrimitives.ReadUInt64BigEndian(tbuf.AsSpan(18));
            long index1 = (long)BinaryPrimitives.ReadUInt64BigEndian(tbuf.AsSpan(26));
            long end = (long)BinaryPrimitives.ReadUInt64BigEndian(tbuf.AsSpan(34));

            trackIndex++;
            image.DaoTracks.Add(new NrgDaoTrack
            {
                TrackNumber = trackIndex,
                SessionNumber = sessionNum + 1,
                Isrc = isrc,
                SectorSize = sectorSize,
                Mode = (NrgTrackMode)mode,
                Index0Offset = index0,
                Index1Offset = index1,
                EndOffset = end
            });
        }
    }

    private static void ParseEtnfV1(Stream stream, uint size, NrgImage image,
        ref int trackIndex, int sessionNum)
    {
        // V1 ETNF: 20 bytes per track
        // 4 bytes: track offset (32-bit)
        // 4 bytes: track length (bytes)
        // 4 bytes: mode
        // 4 bytes: start LBA
        // 4 bytes: unknown
        int entryCount = (int)(size / 20);
        for (int i = 0; i < entryCount; i++)
        {
            var buf = new byte[20];
            ReadExact(stream, buf, 0, 20);

            trackIndex++;
            image.TaoTracks.Add(new NrgTaoTrack
            {
                TrackNumber = trackIndex,
                SessionNumber = sessionNum + 1,
                FileOffset = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(0)),
                DataLength = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(4)),
                Mode = (int)BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(8)),
                StartLba = (int)BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(12))
            });
        }
    }

    private static void ParseEtn2V2(Stream stream, uint size, NrgImage image,
        ref int trackIndex, int sessionNum)
    {
        // V2 ETN2: 32 bytes per track
        // 8 bytes: track offset (64-bit)
        // 8 bytes: track length (bytes, 64-bit)
        // 4 bytes: mode
        // 4 bytes: start LBA
        // 4 bytes: unknown
        // 4 bytes: track length (sectors)
        int entryCount = (int)(size / 32);
        for (int i = 0; i < entryCount; i++)
        {
            var buf = new byte[32];
            ReadExact(stream, buf, 0, 32);

            trackIndex++;
            image.TaoTracks.Add(new NrgTaoTrack
            {
                TrackNumber = trackIndex,
                SessionNumber = sessionNum + 1,
                FileOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan(0)),
                DataLength = (long)BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan(8)),
                Mode = (int)BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(16)),
                StartLba = (int)BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(20)),
                // buf[24..27] = unknown
                SectorCount = (int)BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(28))
            });
        }
    }

    private static void ParseSinf(Stream stream, uint size, NrgImage image)
    {
        // SINF: 4 bytes = track count for this session
        if (size < 4) return;
        var buf = new byte[4];
        ReadExact(stream, buf, 0, 4);

        image.Sessions.Add(new NrgSession
        {
            SessionNumber = image.Sessions.Count + 1,
            TrackCount = (int)BinaryPrimitives.ReadUInt32BigEndian(buf)
        });
    }

    private static void ParseMtyp(Stream stream, uint size, NrgImage image)
    {
        if (size < 4) return;
        var buf = new byte[4];
        ReadExact(stream, buf, 0, 4);
        image.MediaType = (int)BinaryPrimitives.ReadUInt32BigEndian(buf);
    }

    private static void ParseCdtx(Stream stream, uint size, NrgImage image)
    {
        if (size <= 0) return;
        image.CdTextData = new byte[size];
        ReadExact(stream, image.CdTextData, 0, (int)size);
    }

    private static void ParseDinf(Stream stream, uint size, NrgImage image)
    {
        if (size < 4) return;
        var buf = new byte[4];
        ReadExact(stream, buf, 0, 4);
        image.IsDiscOpen = BinaryPrimitives.ReadUInt32BigEndian(buf) != 0;
    }

    // -----------------------------------------------------------------------
    //  Post-processing helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Cross-references cue entries with DAO tracks to assign start LBAs.
    /// </summary>
    private static void AssignLbasFromCues(NrgImage image)
    {
        if (image.CueEntries.Count == 0 || image.DaoTracks.Count == 0) return;

        // Group cue entries by decoded track number, index 1 = track start
        foreach (var track in image.DaoTracks)
        {
            // Find index1 cue entry for this track
            // Track numbers in cues are BCD, so decode them
            var cue = image.CueEntries.FirstOrDefault(c =>
                !c.IsLeadOut &&
                c.DecodedTrackNumber == track.TrackNumber &&
                c.DecodedIndexNumber == 1);

            if (cue != null)
            {
                track.StartLba = cue.Lba;
            }
            else
            {
                // Fallback: compute LBA from file offset and sector size
                if (track.SectorSize > 0)
                    track.StartLba = track.Index1Offset / track.SectorSize;
            }
        }
    }

    // -----------------------------------------------------------------------
    //  Extraction helpers
    // -----------------------------------------------------------------------

    private static void ExtractDaoTrackUserData(Stream input, Stream output,
        NrgDaoTrack track, IProgress<int>? progress)
    {
        input.Seek(track.Index1Offset, SeekOrigin.Begin);
        long sectorCount = track.SectorCount;
        int sectorSize = track.SectorSize;
        int userDataSize = track.UserDataSize;
        int userDataOffset = track.UserDataOffset;

        var sectorBuf = new byte[sectorSize];
        for (long s = 0; s < sectorCount; s++)
        {
            ReadExact(input, sectorBuf, 0, sectorSize);

            if (track.IsRawSector && !track.IsAudio)
            {
                // Extract user data from raw sector
                output.Write(sectorBuf, userDataOffset, userDataSize);
            }
            else
            {
                // Cooked or audio: write the user data portion
                output.Write(sectorBuf, 0, userDataSize);
            }

            if (progress != null && sectorCount > 0 && s % 1000 == 0)
                progress.Report((int)(s * 100 / sectorCount));
        }
        progress?.Report(100);
    }

    private static void ExtractTaoTrackUserData(Stream input, Stream output,
        NrgTaoTrack track, IProgress<int>? progress)
    {
        input.Seek(track.FileOffset, SeekOrigin.Begin);
        long sectorCount = track.ComputedSectorCount;
        int sectorSize = track.SectorSize;
        int userDataSize = track.UserDataSize;
        int userDataOffset = track.UserDataOffset;

        var sectorBuf = new byte[sectorSize];
        for (long s = 0; s < sectorCount; s++)
        {
            ReadExact(input, sectorBuf, 0, sectorSize);

            // For raw sectors, user data starts at an offset within the sector;
            // for cooked/audio sectors, user data starts at offset 0.
            output.Write(sectorBuf, userDataOffset, userDataSize);

            if (progress != null && sectorCount > 0 && s % 1000 == 0)
                progress.Report((int)(s * 100 / sectorCount));
        }
        progress?.Report(100);
    }

    // -----------------------------------------------------------------------
    //  BIN/CUE conversion helpers
    // -----------------------------------------------------------------------

    private static void ConvertDaoToBin(Stream input, Stream output,
        NrgImage image, List<string> cueLines, IProgress<int>? progress)
    {
        long totalSectors = image.DaoTracks.Sum(t => t.SectorCount);
        long processedSectors = 0;

        foreach (var track in image.DaoTracks)
        {
            int cueSectorSize = track.BaseSectorSize;
            string modeStr = GetCueModeString(track.Mode, cueSectorSize);
            int cueTrackNumber = track.TrackNumber;
            cueLines.Add($"  TRACK {cueTrackNumber:D2} {modeStr}");

            // Calculate pregap
            if (track.PregapSectors > 0)
                cueLines.Add($"    PREGAP {SectorsToMsf(track.PregapSectors)}");

            // INDEX 01 at current output position
            long binOffset = output.Position;
            long idx01Frame = binOffset / cueSectorSize;
            cueLines.Add($"    INDEX 01 {SectorsToMsf(idx01Frame)}");

            // Copy track data
            input.Seek(track.Index1Offset, SeekOrigin.Begin);
            var buf = new byte[track.SectorSize];
            long sectors = track.SectorCount;

            for (long s = 0; s < sectors; s++)
            {
                ReadExact(input, buf, 0, track.SectorSize);
                // Write only the base sector data (strip subchannel if present)
                output.Write(buf, 0, cueSectorSize);

                processedSectors++;
                if (progress != null && totalSectors > 0 && processedSectors % 500 == 0)
                    progress.Report((int)(processedSectors * 100 / totalSectors));
            }
        }
        progress?.Report(100);
    }

    private static void ConvertTaoToBin(Stream input, Stream output,
        NrgImage image, List<string> cueLines, IProgress<int>? progress)
    {
        long totalSectors = image.TaoTracks.Sum(t => t.ComputedSectorCount);
        long processedSectors = 0;

        foreach (var track in image.TaoTracks)
        {
            int sectorSize = track.SectorSize;
            string modeStr = track.Mode switch
            {
                0x07 => "AUDIO",
                0x05 => "MODE1/2352",           // Raw Data (Mode 1)
                0x06 => "MODE2/2352",           // Raw Mode 2
                0x02 or 0x03 => "MODE2/2336",   // Mode 2 XA / Form Mix
                _ when sectorSize == 2048 => "MODE1/2048",
                _ when sectorSize == 2352 => "MODE1/2352",
                _ when sectorSize == 2336 => "MODE2/2336",
                _ => $"MODE1/{sectorSize}"
            };
            cueLines.Add($"  TRACK {track.TrackNumber:D2} {modeStr}");

            long binOffset = output.Position;
            long idx01Frame = binOffset / sectorSize;
            cueLines.Add($"    INDEX 01 {SectorsToMsf(idx01Frame)}");

            input.Seek(track.FileOffset, SeekOrigin.Begin);
            var buf = new byte[sectorSize];
            long sectors = track.ComputedSectorCount;

            for (long s = 0; s < sectors; s++)
            {
                ReadExact(input, buf, 0, sectorSize);
                output.Write(buf, 0, sectorSize);

                processedSectors++;
                if (progress != null && totalSectors > 0 && processedSectors % 500 == 0)
                    progress.Report((int)(processedSectors * 100 / totalSectors));
            }
        }
        progress?.Report(100);
    }

    private static string GetCueModeString(NrgTrackMode mode, int sectorSize)
    {
        return mode switch
        {
            NrgTrackMode.Audio or NrgTrackMode.AudioSubchannel => "AUDIO",
            NrgTrackMode.Data => "MODE1/2048",
            NrgTrackMode.RawData or NrgTrackMode.RawDataSubchannel => "MODE1/2352",
            NrgTrackMode.Mode2Form1 => "MODE2/2048",
            NrgTrackMode.RawMode2Form1 or NrgTrackMode.RawMode2Form1Subchannel => "MODE2/2352",
            _ => sectorSize >= 2352 ? "MODE1/2352" : $"MODE1/{sectorSize}"
        };
    }

    private static string SectorsToMsf(long sectors)
    {
        if (sectors < 0) sectors = 0;
        int frames = (int)(sectors % 75);
        int seconds = (int)((sectors / 75) % 60);
        int minutes = (int)(sectors / 75 / 60);
        return $"{minutes:D2}:{seconds:D2}:{frames:D2}";
    }

    private static int ReadExact(Stream stream, byte[] buffer, int offset, int count)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = stream.Read(buffer, offset + totalRead, count - totalRead);
            if (read == 0) break;
            totalRead += read;
        }
        return totalRead;
    }

    /// <summary>
    /// Validates that a string contains only printable ASCII characters (0x20-0x7E).
    /// </summary>
    private static bool IsValidAscii(string value)
    {
        foreach (char c in value)
        {
            if (c < 0x20 || c > 0x7E)
                return false;
        }
        return true;
    }
}
