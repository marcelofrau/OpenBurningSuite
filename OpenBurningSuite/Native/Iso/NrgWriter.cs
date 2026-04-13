// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenBurningSuite.Helpers;
using OpenBurningSuite.Models;

namespace OpenBurningSuite.Native.Iso;

/// <summary>
/// Writes Nero NRG v2 disc image files.
///
/// Generates NER5 (v2) format with 64-bit offsets.
/// Produces CUEX + DAOX + SINF + MTYP + END! chunk chain appended after
/// the raw disc data, followed by the NER5 footer.
///
/// Reference: Unofficial NRG specification — all integers are big-endian.
/// </summary>
public static class NrgWriter
{
    /// <summary>
    /// Wraps an existing ISO image file into NRG v2 format.
    /// The ISO data is copied as-is, then NRG chunk metadata is appended.
    /// </summary>
    /// <param name="isoPath">Path to the source ISO file (2048-byte cooked sectors).</param>
    /// <param name="nrgPath">Path for the output NRG file.</param>
    /// <param name="sectorSize">Sector size of the input data (default: 2048).</param>
    /// <param name="isAudio">Whether the input is an audio track (CD-DA).</param>
    public static void WriteFromIso(string isoPath, string nrgPath,
        int sectorSize = 2048, bool isAudio = false)
    {
        if (!File.Exists(isoPath))
            throw new FileNotFoundException($"Source ISO file not found: {isoPath}");

        long isoSize = new FileInfo(isoPath).Length;
        long sectorCount = isoSize / sectorSize;
        if (sectorCount <= 0)
            throw new InvalidOperationException("Source file is empty or smaller than one sector.");

        using var output = new FileStream(nrgPath, FileMode.Create, FileAccess.Write,
            FileShare.None, 65536, false);

        // 1) Copy ISO data verbatim
        using (var input = new FileStream(isoPath, FileMode.Open, FileAccess.Read,
            FileShare.Read, 65536, false))
        {
            input.CopyTo(output);
        }

        long dataEndOffset = output.Position;

        // 2) Write chunk chain after the disc data
        WriteChunkChain(output, isoSize, sectorSize, sectorCount, isAudio);
    }

    /// <summary>
    /// Wraps raw sector data from a stream into NRG v2 format.
    /// </summary>
    /// <param name="dataStream">Stream containing the raw disc data (positioned at start).</param>
    /// <param name="nrgPath">Path for the output NRG file.</param>
    /// <param name="sectorSize">Sector size of the input data.</param>
    /// <param name="sectorCount">Total number of sectors in the input data.</param>
    /// <param name="isAudio">Whether the input is an audio track.</param>
    public static void WriteFromStream(Stream dataStream, string nrgPath,
        int sectorSize, long sectorCount, bool isAudio = false)
    {
        long dataSize = sectorCount * sectorSize;

        using var output = new FileStream(nrgPath, FileMode.Create, FileAccess.Write,
            FileShare.None, 65536, false);

        // 1) Copy data
        var buffer = new byte[65536];
        long remaining = dataSize;
        while (remaining > 0)
        {
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int read = dataStream.Read(buffer, 0, toRead);
            if (read <= 0) break;
            output.Write(buffer, 0, read);
            remaining -= read;
        }

        // 2) Write chunk chain
        WriteChunkChain(output, dataSize, sectorSize, sectorCount, isAudio);
    }

    /// <summary>
    /// Asynchronously wraps an ISO file into NRG v2 format.
    /// </summary>
    public static async Task WriteFromIsoAsync(string isoPath, string nrgPath,
        int sectorSize = 2048, bool isAudio = false, CancellationToken ct = default)
    {
        await Task.Run(() => WriteFromIso(isoPath, nrgPath, sectorSize, isAudio), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Writes NRG v2 format with multi-track support.
    /// Takes a set of track descriptors and the raw data file.
    /// </summary>
    /// <param name="binPath">Path to the raw BIN data file containing all track data sequentially.</param>
    /// <param name="tracks">Track descriptors with offset, length, and mode information.</param>
    /// <param name="nrgPath">Path for the output NRG file.</param>
    public static void WriteFromBinCue(string binPath, NrgDaoTrack[] tracks, string nrgPath)
    {
        if (!File.Exists(binPath))
            throw new FileNotFoundException($"Source BIN file not found: {binPath}");

        long binSize = new FileInfo(binPath).Length;

        using var output = new FileStream(nrgPath, FileMode.Create, FileAccess.Write,
            FileShare.None, 65536, false);

        // Copy BIN data
        using (var input = new FileStream(binPath, FileMode.Open, FileAccess.Read,
            FileShare.Read, 65536, false))
        {
            input.CopyTo(output);
        }

        // Write multi-track chunk chain
        WriteMultiTrackChunkChain(output, binSize, tracks);
    }

    /// <summary>
    /// Asynchronously writes multi-track NRG from BIN/CUE.
    /// </summary>
    public static async Task WriteFromBinCueAsync(string binPath, NrgDaoTrack[] tracks,
        string nrgPath, CancellationToken ct = default)
    {
        await Task.Run(() => WriteFromBinCue(binPath, tracks, nrgPath), ct)
            .ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------
    //  Internal chunk chain writers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Writes a single-track NRG v2 chunk chain:
    /// CUEX + DAOX + SINF + MTYP + END! + NER5 footer.
    /// </summary>
    private static void WriteChunkChain(Stream output, long dataSize,
        int sectorSize, long sectorCount, bool isAudio)
    {
        long chunkChainOffset = output.Position;

        // Determine track parameters
        NrgTrackMode trackMode;
        NrgTocType tocType;
        byte cueModeValue;
        if (isAudio)
        {
            trackMode = NrgTrackMode.Audio;
            tocType = NrgTocType.Audio;
            cueModeValue = 0x01; // audio
        }
        else if (sectorSize == 2352)
        {
            trackMode = NrgTrackMode.RawData;
            tocType = NrgTocType.Data;
            cueModeValue = 0x41; // data
        }
        else
        {
            trackMode = NrgTrackMode.Data;
            tocType = NrgTocType.Data;
            cueModeValue = 0x41; // data
        }

        // CUEX chunk: 2 cue entries (track 1 index 0, track 1 index 1, lead-out)
        // Actually: index 0 for track, index 1 for track, index 1 for lead-out = 3 entries
        // Standard NRG: one index0 + one index1 per track + lead-out index1
        int cueEntryCount = 3;
        int cueChunkSize = cueEntryCount * 8;
        WriteBE32(output, Encoding.ASCII.GetBytes("CUEX"));
        WriteBE32(output, (uint)cueChunkSize);

        // Cue entry: track 1, index 0, LBA 0
        output.WriteByte(cueModeValue);
        output.WriteByte(0x01); // track 1 (BCD)
        output.WriteByte(0x00); // index 0
        output.WriteByte(0x00); // padding
        WriteBE32(output, 0);   // LBA 0

        // Cue entry: track 1, index 1, LBA 0
        output.WriteByte(cueModeValue);
        output.WriteByte(0x01);
        output.WriteByte(0x01); // index 1
        output.WriteByte(0x00);
        WriteBE32(output, 0);

        // Cue entry: lead-out (0xAA), index 1
        output.WriteByte(cueModeValue);
        output.WriteByte(0xAA); // lead-out
        output.WriteByte(0x01);
        output.WriteByte(0x00);
        WriteBE32(output, (uint)sectorCount);

        // DAOX chunk
        // Header: 4(dup size) + 14(UPC) + 2(TOC type) + 1(first) + 1(last) = 22
        // Per track: 12(ISRC) + 2(sector size) + 2(mode) + 2(unknown) + 8(idx0) + 8(idx1) + 8(end) = 42
        int daoxTrackSize = 42;
        int daoxChunkSize = 22 + daoxTrackSize;
        WriteBE32(output, Encoding.ASCII.GetBytes("DAOX"));
        WriteBE32(output, (uint)daoxChunkSize);

        // DAOX header
        WriteBE32(output, (uint)daoxChunkSize); // duplicate size
        WriteBytes(output, 14, 0);              // UPC (null)
        WriteBE16(output, (ushort)tocType);      // TOC type
        output.WriteByte(1);                     // first track
        output.WriteByte(1);                     // last track

        // DAOX track entry
        WriteBytes(output, 12, 0);               // ISRC (null)
        WriteBE16(output, (ushort)sectorSize);   // sector size
        WriteBE16(output, (ushort)trackMode);    // mode
        WriteBE16(output, 0x0001);               // unknown constant
        WriteBE64(output, 0);                    // index0 (0 = no pregap)
        WriteBE64(output, 0);                    // index1 (data starts at offset 0)
        WriteBE64(output, (ulong)dataSize);      // end offset

        // SINF chunk
        WriteBE32(output, Encoding.ASCII.GetBytes("SINF"));
        WriteBE32(output, 4);
        WriteBE32(output, 1); // 1 track in session

        // MTYP chunk — media type identifier
        // Per reverse-engineered NRG format: 0x01 = CD-ROM, 0x02 = DVD-ROM.
        // Set based on data size: CD images are < 900 MB, everything larger is DVD.
        WriteBE32(output, Encoding.ASCII.GetBytes("MTYP"));
        WriteBE32(output, 4);
        uint mediaType = dataSize > FormatHelper.CdImageSizeThresholdBytes ? 0x02u : 0x01u;
        WriteBE32(output, mediaType);

        // END! chunk
        WriteBE32(output, Encoding.ASCII.GetBytes("END!"));
        WriteBE32(output, 0);

        // NER5 footer (4-byte magic + 8-byte chunk offset)
        output.Write(Encoding.ASCII.GetBytes("NER5"));
        WriteBE64(output, (ulong)chunkChainOffset);
    }

    /// <summary>
    /// Writes a multi-track NRG v2 chunk chain.
    /// </summary>
    private static void WriteMultiTrackChunkChain(Stream output, long binDataSize,
        NrgDaoTrack[] tracks)
    {
        long chunkChainOffset = output.Position;

        if (tracks.Length == 0)
            throw new ArgumentException("At least one track is required.");

        // Determine overall TOC type from first track
        bool hasData = false;
        foreach (var t in tracks)
        {
            if (t.IsData) hasData = true;
        }
        NrgTocType tocType = hasData ? NrgTocType.Data : NrgTocType.Audio;

        // CUEX chunk: 2 entries per track + 1 lead-out = 2*N + 1
        int cueEntryCount = tracks.Length * 2 + 1;
        int cueChunkSize = cueEntryCount * 8;
        WriteBE32(output, Encoding.ASCII.GetBytes("CUEX"));
        WriteBE32(output, (uint)cueChunkSize);

        foreach (var track in tracks)
        {
            byte cueModeVal = track.IsAudio ? (byte)0x01 : (byte)0x41;
            byte bcdTrack = ToBcd(track.TrackNumber);

            // Index 0
            output.WriteByte(cueModeVal);
            output.WriteByte(bcdTrack);
            output.WriteByte(0x00);
            output.WriteByte(0x00);
            WriteBE32(output, (uint)track.StartLba);

            // Index 1
            output.WriteByte(cueModeVal);
            output.WriteByte(bcdTrack);
            output.WriteByte(0x01);
            output.WriteByte(0x00);
            WriteBE32(output, (uint)track.StartLba);
        }

        // Lead-out
        var lastTrack = tracks[^1];
        long leadOutLba = lastTrack.StartLba + lastTrack.SectorCount;
        byte lastMode = lastTrack.IsAudio ? (byte)0x01 : (byte)0x41;
        output.WriteByte(lastMode);
        output.WriteByte(0xAA);
        output.WriteByte(0x01);
        output.WriteByte(0x00);
        WriteBE32(output, (uint)leadOutLba);

        // DAOX chunk
        int daoxChunkSize = 22 + tracks.Length * 42;
        WriteBE32(output, Encoding.ASCII.GetBytes("DAOX"));
        WriteBE32(output, (uint)daoxChunkSize);

        WriteBE32(output, (uint)daoxChunkSize);
        WriteBytes(output, 14, 0);                // UPC
        WriteBE16(output, (ushort)tocType);
        output.WriteByte((byte)tracks[0].TrackNumber);
        output.WriteByte((byte)tracks[^1].TrackNumber);

        foreach (var track in tracks)
        {
            byte[] isrcBytes = new byte[12];
            if (!string.IsNullOrEmpty(track.Isrc))
                Encoding.ASCII.GetBytes(track.Isrc, 0, Math.Min(track.Isrc.Length, 12), isrcBytes, 0);
            output.Write(isrcBytes, 0, 12);

            WriteBE16(output, (ushort)track.SectorSize);
            WriteBE16(output, (ushort)track.Mode);
            WriteBE16(output, 0x0001);
            WriteBE64(output, (ulong)track.Index0Offset);
            WriteBE64(output, (ulong)track.Index1Offset);
            WriteBE64(output, (ulong)track.EndOffset);
        }

        // SINF chunk
        WriteBE32(output, Encoding.ASCII.GetBytes("SINF"));
        WriteBE32(output, 4);
        WriteBE32(output, (uint)tracks.Length);

        // MTYP chunk — media type identifier
        WriteBE32(output, Encoding.ASCII.GetBytes("MTYP"));
        WriteBE32(output, 4);
        WriteBE32(output, binDataSize > FormatHelper.CdImageSizeThresholdBytes ? 0x02u : 0x01u);

        // END! chunk
        WriteBE32(output, Encoding.ASCII.GetBytes("END!"));
        WriteBE32(output, 0);

        // NER5 footer
        output.Write(Encoding.ASCII.GetBytes("NER5"));
        WriteBE64(output, (ulong)chunkChainOffset);
    }

    // -----------------------------------------------------------------------
    //  Binary write helpers (big-endian)
    // -----------------------------------------------------------------------

    private static void WriteBE32(Stream s, byte[] value)
    {
        s.Write(value, 0, 4);
    }

    private static void WriteBE32(Stream s, uint value)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buf, value);
        s.Write(buf);
    }

    private static void WriteBE16(Stream s, ushort value)
    {
        Span<byte> buf = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buf, value);
        s.Write(buf);
    }

    private static void WriteBE64(Stream s, ulong value)
    {
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(buf, value);
        s.Write(buf);
    }

    private static void WriteBytes(Stream s, int count, byte value)
    {
        var buf = new byte[count];
        if (value != 0)
            Array.Fill(buf, value);
        s.Write(buf, 0, count);
    }

    private static byte ToBcd(int value)
    {
        if (value < 0 || value > 99) return 0;
        return (byte)(((value / 10) << 4) | (value % 10));
    }
}
