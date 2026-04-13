// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OpenBurningSuite.Models;

namespace OpenBurningSuite.Native.Iso;

/// <summary>
/// Writer for subchannel data (.sub) files.
///
/// Provides methods to:
/// - Write subchannel data from a stream or buffer
/// - Generate subchannel data from Q subchannel entries (TOC reconstruction)
/// - Convert between interleaved and sequential formats
/// - Merge subchannel data from different sources
/// - Generate dummy subchannel data for sectors without subchannel info
///
/// Per ECMA-394 / Red Book, each sector has 96 bytes of P-W subchannel data.
/// The standard .sub file stores these 96 bytes sequentially: P(12)+Q(12)+R-W(72).
/// </summary>
public static class SubWriter
{
    /// <summary>Bytes per sector in a .sub file.</summary>
    private const int SubSectorSize = 96;

    /// <summary>Size of each individual subchannel per sector.</summary>
    private const int ChannelSize = 12;

    // -----------------------------------------------------------------------
    //  Public API — Write
    // -----------------------------------------------------------------------

    /// <summary>
    /// Writes a .sub file from a source stream containing raw subchannel data.
    /// </summary>
    /// <param name="sourceStream">Source stream with 96 bytes per sector.</param>
    /// <param name="outputPath">Path to the output .sub file.</param>
    /// <param name="sectorCount">Number of sectors to write.</param>
    /// <param name="sourceFormat">Format of the source subchannel data.</param>
    /// <param name="outputFormat">Desired output format.</param>
    /// <param name="progress">Optional progress reporter (0-100).</param>
    public static void Write(Stream sourceStream, string outputPath, long sectorCount,
        SubFormat sourceFormat = SubFormat.Sequential,
        SubFormat outputFormat = SubFormat.Sequential,
        IProgress<int>? progress = null)
    {
        using var outStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        var sectorBuf = new byte[SubSectorSize];
        bool needConvert = sourceFormat != outputFormat;

        for (long i = 0; i < sectorCount; i++)
        {
            int bytesRead = ReadFull(sourceStream, sectorBuf, 0, SubSectorSize);
            if (bytesRead < SubSectorSize) break;

            if (needConvert)
            {
                var converted = sourceFormat == SubFormat.Interleaved
                    ? SubParser.DeinterleaveToSequential(sectorBuf)
                    : SubParser.InterleaveFromSequential(sectorBuf);
                outStream.Write(converted, 0, SubSectorSize);
            }
            else
            {
                outStream.Write(sectorBuf, 0, SubSectorSize);
            }

            if (i % 10000 == 0 && sectorCount > 0)
                progress?.Report((int)(i * 100 / sectorCount));
        }

        progress?.Report(100);
    }

    /// <summary>
    /// Asynchronously writes a .sub file.
    /// </summary>
    public static async Task WriteAsync(Stream sourceStream, string outputPath, long sectorCount,
        SubFormat sourceFormat = SubFormat.Sequential,
        SubFormat outputFormat = SubFormat.Sequential,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            Write(sourceStream, outputPath, sectorCount, sourceFormat, outputFormat, progress);
        }, ct).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------
    //  Public API — Generate dummy subchannel data
    // -----------------------------------------------------------------------

    /// <summary>
    /// Generates a .sub file with valid Q subchannel data for a disc layout.
    /// This creates proper P and Q subchannel data based on track definitions,
    /// with R-W subchannels zero-filled.
    /// </summary>
    /// <param name="outputPath">Path to the output .sub file.</param>
    /// <param name="tracks">Track information array.</param>
    /// <param name="totalSectors">Total number of sectors on the disc.</param>
    /// <param name="progress">Optional progress reporter (0-100).</param>
    public static void GenerateFromTracks(string outputPath, SubTrackInfo[] tracks,
        long totalSectors, IProgress<int>? progress = null)
    {
        using var outStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        var sectorBuf = new byte[SubSectorSize]; // Initialized to all zeros

        for (long sector = 0; sector < totalSectors; sector++)
        {
            Array.Clear(sectorBuf, 0, SubSectorSize);

            // Determine which track this sector belongs to
            int trackIdx = FindTrackForSector(tracks, (int)sector);

            if (trackIdx >= 0)
            {
                var track = tracks[trackIdx];
                int nextTrackStart = trackIdx + 1 < tracks.Length ? tracks[trackIdx + 1].StartLba : (int)totalSectors;

                // P subchannel: 0xFF in pregap, 0x00 in data
                bool inPregap = track.PregapLba >= 0 && sector < track.StartLba;
                byte pValue = inPregap ? (byte)0xFF : (byte)0x00;
                for (int j = 0; j < ChannelSize; j++)
                    sectorBuf[j] = pValue;

                // Q subchannel: ADR=1 TOC data
                int relLba = (int)sector - track.StartLba;
                int absLba = (int)sector;

                // Control/ADR byte
                byte control = track.Control;
                sectorBuf[ChannelSize] = (byte)((control << 4) | 0x01); // ADR=1

                // Track number (BCD)
                sectorBuf[ChannelSize + 1] = BinaryToBcd((byte)track.TrackNumber);

                // Index number (BCD)
                sectorBuf[ChannelSize + 2] = inPregap ? (byte)0x00 : (byte)0x01;

                // Relative time (BCD MSF)
                int relLbaAbs = Math.Abs(relLba);
                int relF = relLbaAbs % 75;
                int relS = (relLbaAbs / 75) % 60;
                int relM = relLbaAbs / 75 / 60;
                sectorBuf[ChannelSize + 3] = BinaryToBcd((byte)relM);
                sectorBuf[ChannelSize + 4] = BinaryToBcd((byte)relS);
                sectorBuf[ChannelSize + 5] = BinaryToBcd((byte)relF);

                // Zero byte
                sectorBuf[ChannelSize + 6] = 0x00;

                // Absolute time (BCD MSF) — LBA + 150 offset
                int absLba150 = absLba + 150;
                int absF = absLba150 % 75;
                int absS = (absLba150 / 75) % 60;
                int absM = absLba150 / 75 / 60;
                sectorBuf[ChannelSize + 7] = BinaryToBcd((byte)absM);
                sectorBuf[ChannelSize + 8] = BinaryToBcd((byte)absS);
                sectorBuf[ChannelSize + 9] = BinaryToBcd((byte)absF);

                // CRC-16 (inverted, per Red Book)
                ushort crc = CalculateCrc16(sectorBuf, ChannelSize, 10);
                ushort invertedCrc = (ushort)~crc;
                sectorBuf[ChannelSize + 10] = (byte)(invertedCrc >> 8);
                sectorBuf[ChannelSize + 11] = (byte)(invertedCrc & 0xFF);
            }

            outStream.Write(sectorBuf, 0, SubSectorSize);

            if (sector % 10000 == 0 && totalSectors > 0)
                progress?.Report((int)(sector * 100 / totalSectors));
        }

        progress?.Report(100);
    }

    /// <summary>
    /// Asynchronously generates a .sub file from track definitions.
    /// </summary>
    public static async Task GenerateFromTracksAsync(string outputPath, SubTrackInfo[] tracks,
        long totalSectors, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            GenerateFromTracks(outputPath, tracks, totalSectors, progress);
        }, ct).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------
    //  Public API — Convert format
    // -----------------------------------------------------------------------

    /// <summary>
    /// Converts a .sub file between interleaved and sequential formats.
    /// </summary>
    /// <param name="inputPath">Path to input .sub file.</param>
    /// <param name="outputPath">Path to output .sub file.</param>
    /// <param name="inputFormat">Format of the input file.</param>
    /// <param name="outputFormat">Desired output format.</param>
    /// <param name="progress">Optional progress reporter (0-100).</param>
    public static void ConvertFormat(string inputPath, string outputPath,
        SubFormat inputFormat, SubFormat outputFormat, IProgress<int>? progress = null)
    {
        using var inStream = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        long sectorCount = inStream.Length / SubSectorSize;
        Write(inStream, outputPath, sectorCount, inputFormat, outputFormat, progress);
    }

    /// <summary>
    /// Asynchronously converts a .sub file between formats.
    /// </summary>
    public static async Task ConvertFormatAsync(string inputPath, string outputPath,
        SubFormat inputFormat, SubFormat outputFormat,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            ConvertFormat(inputPath, outputPath, inputFormat, outputFormat, progress);
        }, ct).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------
    //  Public API — Merge subchannel data
    // -----------------------------------------------------------------------

    /// <summary>
    /// Merges a LibCrypt SBI patch file into existing subchannel data.
    /// SBI files contain corrected Q subchannel entries for specific sectors
    /// (used for PlayStation 1 copy protection patching).
    /// </summary>
    /// <param name="subFilePath">Path to the .sub file to patch (modified in place).</param>
    /// <param name="sbiFilePath">Path to the .sbi patch file.</param>
    /// <returns>Number of sectors patched.</returns>
    public static int ApplySbiPatch(string subFilePath, string sbiFilePath)
    {
        if (!File.Exists(subFilePath) || !File.Exists(sbiFilePath))
            return 0;

        using var sbiStream = new FileStream(sbiFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        // SBI header: "SBI\0" (4 bytes)
        var header = new byte[4];
        if (sbiStream.Read(header, 0, 4) < 4) return 0;
        if (header[0] != 'S' || header[1] != 'B' || header[2] != 'I' || header[3] != 0)
            return 0;

        using var subStream = new FileStream(subFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        int patchedCount = 0;

        // SBI entries: MSF(3 bytes BCD) + Q subchannel data (10 or 12 bytes)
        while (sbiStream.Position < sbiStream.Length)
        {
            var entryBuf = new byte[3];
            if (sbiStream.Read(entryBuf, 0, 3) < 3) break;

            // Convert BCD MSF to LBA
            int min = BcdToInt(entryBuf[0]);
            int sec = BcdToInt(entryBuf[1]);
            int frame = BcdToInt(entryBuf[2]);
            int lba = min * 60 * 75 + sec * 75 + frame - 150;

            // Read patch type byte
            int patchType = sbiStream.ReadByte();
            if (patchType < 0) break;

            // Read Q data (10 bytes for type 1, 12 bytes for type 2/3)
            int qDataLen = patchType == 1 ? 10 : 12;
            var qData = new byte[qDataLen];
            if (sbiStream.Read(qData, 0, qDataLen) < qDataLen) break;

            // Apply patch to .sub file
            long subOffset = (long)lba * SubSectorSize + ChannelSize; // Q channel starts at offset 12
            if (subOffset >= 0 && subOffset + qDataLen <= subStream.Length)
            {
                subStream.Seek(subOffset, SeekOrigin.Begin);
                subStream.Write(qData, 0, Math.Min(qDataLen, ChannelSize));
                patchedCount++;
            }
        }

        return patchedCount;
    }

    // -----------------------------------------------------------------------
    //  Public API — Write single sector
    // -----------------------------------------------------------------------

    /// <summary>
    /// Writes subchannel data for a single sector at the specified position.
    /// </summary>
    /// <param name="subFilePath">Path to the .sub file.</param>
    /// <param name="sectorIndex">0-based sector index.</param>
    /// <param name="subchannelData">96 bytes of subchannel data.</param>
    public static void WriteSector(string subFilePath, long sectorIndex, byte[] subchannelData)
    {
        if (subchannelData.Length != SubSectorSize)
            throw new ArgumentException($"Expected {SubSectorSize} bytes, got {subchannelData.Length}.");

        using var stream = new FileStream(subFilePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        long offset = sectorIndex * SubSectorSize;
        stream.Seek(offset, SeekOrigin.Begin);
        stream.Write(subchannelData, 0, SubSectorSize);
    }

    // -----------------------------------------------------------------------
    //  Private helpers
    // -----------------------------------------------------------------------

    /// <summary>Reads exactly count bytes from stream, retrying short reads.</summary>
    private static int ReadFull(Stream stream, byte[] buffer, int offset, int count)
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

    /// <summary>Finds which track a sector belongs to.</summary>
    private static int FindTrackForSector(SubTrackInfo[] tracks, int sectorLba)
    {
        for (int i = tracks.Length - 1; i >= 0; i--)
        {
            int trackStart = tracks[i].PregapLba >= 0 ? tracks[i].PregapLba : tracks[i].StartLba;
            if (sectorLba >= trackStart)
                return i;
        }
        return -1;
    }

    /// <summary>Converts binary value (0-99) to BCD.</summary>
    private static byte BinaryToBcd(byte value) => (byte)(((value / 10) << 4) | (value % 10));

    /// <summary>Converts BCD byte to integer.</summary>
    private static int BcdToInt(byte bcd) => (bcd >> 4) * 10 + (bcd & 0x0F);

    /// <summary>
    /// Calculates CRC-16 (CCITT) for Q subchannel data.
    /// Polynomial: x^16 + x^12 + x^5 + 1 (0x1021)
    /// </summary>
    private static ushort CalculateCrc16(byte[] data, int offset, int length)
    {
        ushort crc = 0;
        for (int i = 0; i < length; i++)
        {
            crc ^= (ushort)(data[offset + i] << 8);
            for (int bit = 0; bit < 8; bit++)
            {
                if ((crc & 0x8000) != 0)
                    crc = (ushort)((crc << 1) ^ 0x1021);
                else
                    crc <<= 1;
            }
        }
        return crc;
    }
}
