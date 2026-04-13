// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenBurningSuite.Models;

namespace OpenBurningSuite.Native.Iso;

/// <summary>
/// Parser for subchannel data (.sub) files.
///
/// The .sub file format stores 96 bytes of P-W subchannel data per sector.
/// This is the standard subchannel format used by CloneCD (CCD/IMG/SUB),
/// cdrdao, and other disc imaging tools.
///
/// Per ECMA-394 (CD-ROM specification) and Red Book (IEC 60908):
/// - P subchannel: 12 bytes — pause/play flag (0xFF = pause, 0x00 = play)
/// - Q subchannel: 12 bytes — control/address, TOC data, timestamps, CRC-16
/// - R-W subchannels: 72 bytes — CD-TEXT, CD-G graphics, or zero-filled
///
/// The parser extracts:
/// - Q subchannel TOC entries (track layout, indices, timing)
/// - Media Catalog Number (MCN/UPC/EAN) from Q ADR=2
/// - ISRC codes per track from Q ADR=3
/// - CD-TEXT detection from R-W subchannels
/// - CD-G (CD+Graphics) detection from R-W subchannels
/// </summary>
public static class SubParser
{
    /// <summary>Bytes per sector in a .sub file.</summary>
    private const int SubSectorSize = 96;

    /// <summary>Offset of Q subchannel within a sequential .sub sector.</summary>
    private const int QOffset = 12;

    /// <summary>Size of each individual subchannel per sector.</summary>
    private const int ChannelSize = 12;

    /// <summary>CD-TEXT pack identification byte in R-W subchannel.</summary>
    private const byte CdTextPackId = 0x80;

    /// <summary>CD-G graphics command byte in R subchannel.</summary>
    private const byte CdGCommandByte = 0x09;

    // -----------------------------------------------------------------------
    //  Public API — Parse
    // -----------------------------------------------------------------------

    /// <summary>
    /// Parses a .sub subchannel data file.
    /// </summary>
    /// <param name="subFilePath">Path to the .sub file.</param>
    /// <returns>Parsed subchannel image structure.</returns>
    public static SubImage Parse(string subFilePath)
    {
        var image = new SubImage { FilePath = subFilePath };

        try
        {
            if (!File.Exists(subFilePath))
            {
                image.ErrorMessage = $"SUB file not found: {subFilePath}";
                return image;
            }

            var fileInfo = new FileInfo(subFilePath);
            image.FileSize = fileInfo.Length;

            if (fileInfo.Length == 0)
            {
                image.ErrorMessage = "SUB file is empty.";
                return image;
            }

            if (fileInfo.Length % SubSectorSize != 0)
            {
                image.ErrorMessage = $"SUB file size ({fileInfo.Length}) is not a multiple of {SubSectorSize} bytes. " +
                    "File may be corrupt or in a non-standard format.";
                return image;
            }

            image.SectorCount = fileInfo.Length / SubSectorSize;

            // Detect format (interleaved vs sequential) by examining first few sectors
            using var stream = new FileStream(subFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            image.Format = DetectFormat(stream);
            stream.Seek(0, SeekOrigin.Begin);

            // Parse Q subchannel data from all sectors
            ParseQSubchannel(stream, image);

            // Detect CD-TEXT and CD-G from R-W subchannels
            stream.Seek(0, SeekOrigin.Begin);
            DetectRwContent(stream, image);

            // Build track list from Q subchannel entries
            BuildTrackList(image);
        }
        catch (Exception ex)
        {
            image.ErrorMessage = $"Failed to parse SUB file: {ex.Message}";
        }

        return image;
    }

    /// <summary>
    /// Asynchronously parses a .sub file.
    /// </summary>
    public static async Task<SubImage> ParseAsync(string subFilePath, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            return Parse(subFilePath);
        }, ct).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------
    //  Public API — Read raw subchannel data
    // -----------------------------------------------------------------------

    /// <summary>
    /// Reads the raw 96-byte subchannel data for a specific sector.
    /// </summary>
    /// <param name="subFilePath">Path to the .sub file.</param>
    /// <param name="sectorIndex">0-based sector index.</param>
    /// <returns>96 bytes of subchannel data, or null if out of range.</returns>
    public static byte[]? ReadSector(string subFilePath, long sectorIndex)
    {
        using var stream = new FileStream(subFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        long offset = sectorIndex * SubSectorSize;
        if (offset + SubSectorSize > stream.Length) return null;

        stream.Seek(offset, SeekOrigin.Begin);
        var data = new byte[SubSectorSize];
        int bytesRead = stream.Read(data, 0, SubSectorSize);
        return bytesRead == SubSectorSize ? data : null;
    }

    /// <summary>
    /// Reads the Q subchannel data (12 bytes) for a specific sector.
    /// </summary>
    /// <param name="subFilePath">Path to the .sub file.</param>
    /// <param name="sectorIndex">0-based sector index.</param>
    /// <param name="format">Subchannel data format of the file.</param>
    /// <returns>12 bytes of Q subchannel data, or null if out of range.</returns>
    public static byte[]? ReadQSubchannel(string subFilePath, long sectorIndex, SubFormat format = SubFormat.Sequential)
    {
        var rawData = ReadSector(subFilePath, sectorIndex);
        if (rawData == null) return null;

        return ExtractQChannel(rawData, format);
    }

    /// <summary>
    /// Extracts the Q subchannel bytes from a 96-byte subchannel sector.
    /// </summary>
    public static byte[] ExtractQChannel(byte[] sectorData, SubFormat format)
    {
        var q = new byte[ChannelSize];

        if (format == SubFormat.Sequential)
        {
            // Sequential: Q channel is at offset 12-23
            Buffer.BlockCopy(sectorData, QOffset, q, 0, ChannelSize);
        }
        else
        {
            // Interleaved: Q channel is bit 6 of each byte
            // Each of the 96 bytes contains one bit per channel
            // Byte layout: P(bit7) Q(bit6) R(bit5) S(bit4) T(bit3) U(bit2) V(bit1) W(bit0)
            for (int i = 0; i < ChannelSize; i++)
            {
                byte qByte = 0;
                for (int bit = 0; bit < 8; bit++)
                {
                    int byteIdx = i * 8 + bit;
                    if (byteIdx < 96 && (sectorData[byteIdx] & 0x40) != 0) // bit 6 = Q
                        qByte |= (byte)(0x80 >> bit);
                }
                q[i] = qByte;
            }
        }

        return q;
    }

    /// <summary>
    /// Extracts the P subchannel bytes from a 96-byte subchannel sector.
    /// </summary>
    public static byte[] ExtractPChannel(byte[] sectorData, SubFormat format)
    {
        var p = new byte[ChannelSize];

        if (format == SubFormat.Sequential)
        {
            // Sequential: P channel is at offset 0-11
            Buffer.BlockCopy(sectorData, 0, p, 0, ChannelSize);
        }
        else
        {
            // Interleaved: P channel is bit 7 of each byte
            for (int i = 0; i < ChannelSize; i++)
            {
                byte pByte = 0;
                for (int bit = 0; bit < 8; bit++)
                {
                    int byteIdx = i * 8 + bit;
                    if (byteIdx < 96 && (sectorData[byteIdx] & 0x80) != 0) // bit 7 = P
                        pByte |= (byte)(0x80 >> bit);
                }
                p[i] = pByte;
            }
        }

        return p;
    }

    /// <summary>
    /// Extracts the R-W subchannel bytes (72 bytes) from a 96-byte subchannel sector.
    /// </summary>
    public static byte[] ExtractRwChannels(byte[] sectorData, SubFormat format)
    {
        var rw = new byte[72]; // 6 channels × 12 bytes

        if (format == SubFormat.Sequential)
        {
            // Sequential: R-W channels at offset 24-95
            Buffer.BlockCopy(sectorData, 24, rw, 0, 72);
        }
        else
        {
            // Interleaved: R through W are bits 5 through 0
            for (int channel = 0; channel < 6; channel++)
            {
                int bitMask = 0x20 >> channel; // R=bit5, S=bit4, ..., W=bit0
                for (int i = 0; i < ChannelSize; i++)
                {
                    byte chByte = 0;
                    for (int bit = 0; bit < 8; bit++)
                    {
                        int byteIdx = i * 8 + bit;
                        if (byteIdx < 96 && (sectorData[byteIdx] & bitMask) != 0)
                            chByte |= (byte)(0x80 >> bit);
                    }
                    rw[channel * ChannelSize + i] = chByte;
                }
            }
        }

        return rw;
    }

    // -----------------------------------------------------------------------
    //  Public API — Deinterleave / Interleave
    // -----------------------------------------------------------------------

    /// <summary>
    /// Converts interleaved subchannel data to sequential format.
    /// </summary>
    /// <param name="interleaved">96 bytes of interleaved P-W data.</param>
    /// <returns>96 bytes of sequential P+Q+R-W data.</returns>
    public static byte[] DeinterleaveToSequential(byte[] interleaved)
    {
        if (interleaved.Length != SubSectorSize)
            throw new ArgumentException($"Expected {SubSectorSize} bytes, got {interleaved.Length}.");

        var sequential = new byte[SubSectorSize];

        // Extract each channel
        var p = ExtractPChannel(interleaved, SubFormat.Interleaved);
        var q = ExtractQChannel(interleaved, SubFormat.Interleaved);
        var rw = ExtractRwChannels(interleaved, SubFormat.Interleaved);

        Buffer.BlockCopy(p, 0, sequential, 0, ChannelSize);
        Buffer.BlockCopy(q, 0, sequential, ChannelSize, ChannelSize);
        Buffer.BlockCopy(rw, 0, sequential, 2 * ChannelSize, 72);

        return sequential;
    }

    /// <summary>
    /// Converts sequential subchannel data to interleaved format.
    /// </summary>
    /// <param name="sequential">96 bytes of sequential P+Q+R-W data.</param>
    /// <returns>96 bytes of interleaved P-W data.</returns>
    public static byte[] InterleaveFromSequential(byte[] sequential)
    {
        if (sequential.Length != SubSectorSize)
            throw new ArgumentException($"Expected {SubSectorSize} bytes, got {sequential.Length}.");

        var interleaved = new byte[SubSectorSize];

        // Each output byte contains one bit per channel (P=bit7 ... W=bit0)
        for (int byteIdx = 0; byteIdx < SubSectorSize; byteIdx++)
        {
            int channelIdx = byteIdx / ChannelSize; // Which channel (0=P, 1=Q, 2=R, ... 7=W)
            int bitOffset = byteIdx % ChannelSize;   // Byte position within channel

            if (channelIdx >= 8) break;

            byte srcByte = sequential[channelIdx * ChannelSize + bitOffset];
            int bitShift = 7 - channelIdx; // P=bit7, Q=bit6, ... W=bit0

            for (int bit = 0; bit < 8; bit++)
            {
                if ((srcByte & (0x80 >> bit)) != 0)
                {
                    int destIdx = bitOffset * 8 + bit;
                    if (destIdx < SubSectorSize)
                        interleaved[destIdx] |= (byte)(1 << bitShift);
                }
            }
        }

        return interleaved;
    }

    // -----------------------------------------------------------------------
    //  Public API — Validation
    // -----------------------------------------------------------------------

    /// <summary>
    /// Validates a .sub file for integrity.
    /// </summary>
    /// <param name="image">Parsed SubImage to validate.</param>
    /// <returns>List of validation issues (empty if valid).</returns>
    public static List<string> ValidateImage(SubImage image)
    {
        var issues = new List<string>();

        if (!image.IsValid)
        {
            issues.Add(image.ErrorMessage ?? "Unknown parse error");
            return issues;
        }

        if (image.SectorCount == 0)
            issues.Add("SUB file contains no sectors.");

        if (image.Tracks.Count == 0 && image.QEntries.Count > 0)
            issues.Add("Q subchannel data found but no tracks could be reconstructed.");

        // Check for Q subchannel CRC errors
        int crcErrors = image.QEntries.Count(e => !e.CrcValid);
        if (crcErrors > 0)
        {
            double errorRate = (double)crcErrors / image.QEntries.Count * 100;
            if (errorRate > 10)
                issues.Add($"High Q subchannel CRC error rate: {crcErrors}/{image.QEntries.Count} ({errorRate:F1}%).");
            else if (crcErrors > 0)
                issues.Add($"Minor Q subchannel CRC errors: {crcErrors}/{image.QEntries.Count} ({errorRate:F1}%).");
        }

        // Check for non-sequential track numbering
        var trackNums = image.Tracks.Select(t => t.TrackNumber).OrderBy(n => n).ToList();
        for (int i = 1; i < trackNums.Count; i++)
        {
            if (trackNums[i] != trackNums[i - 1] + 1)
                issues.Add($"Non-sequential track numbering: track {trackNums[i - 1]} followed by track {trackNums[i]}.");
        }

        return issues;
    }

    // -----------------------------------------------------------------------
    //  Private — Format detection
    // -----------------------------------------------------------------------

    /// <summary>
    /// Detects whether the .sub file uses interleaved or sequential format.
    /// Heuristic: In sequential format, the P channel (bytes 0-11) should be
    /// either all 0x00 or all 0xFF. In interleaved format, each byte contains
    /// bits from all channels, so the pattern is different.
    /// </summary>
    private static SubFormat DetectFormat(Stream stream)
    {
        var sector = new byte[SubSectorSize];
        int sectorsToCheck = Math.Min(10, (int)(stream.Length / SubSectorSize));

        for (int i = 0; i < sectorsToCheck; i++)
        {
            stream.Seek((long)i * SubSectorSize, SeekOrigin.Begin);
            if (stream.Read(sector, 0, SubSectorSize) < SubSectorSize) break;

            // In sequential format, P channel (first 12 bytes) should be uniform
            bool pAllZero = true, pAllFF = true;
            for (int j = 0; j < ChannelSize; j++)
            {
                if (sector[j] != 0x00) pAllZero = false;
                if (sector[j] != 0xFF) pAllFF = false;
            }

            // If P channel is uniform (all 0x00 or all 0xFF), this looks sequential
            if (pAllZero || pAllFF)
            {
                // Additional check: try to parse Q channel from sequential position
                // and verify it looks like valid Q data (ADR should be 1, 2, or 3)
                byte adr = (byte)(sector[QOffset] & 0x0F);
                if (adr >= 1 && adr <= 3)
                    return SubFormat.Sequential;
            }
        }

        // If we couldn't confirm sequential, try interleaved detection
        stream.Seek(0, SeekOrigin.Begin);
        if (stream.Read(sector, 0, SubSectorSize) == SubSectorSize)
        {
            // In interleaved format, extract Q channel and check ADR
            var q = ExtractQChannel(sector, SubFormat.Interleaved);
            byte adr = (byte)(q[0] & 0x0F);
            if (adr >= 1 && adr <= 3)
                return SubFormat.Interleaved;
        }

        // Default to sequential (most common)
        return SubFormat.Sequential;
    }

    // -----------------------------------------------------------------------
    //  Private — Q subchannel parsing
    // -----------------------------------------------------------------------

    /// <summary>
    /// Parses Q subchannel data from all sectors in the .sub file.
    /// </summary>
    private static void ParseQSubchannel(Stream stream, SubImage image)
    {
        var sectorBuf = new byte[SubSectorSize];
        long sectorIndex = 0;

        // Sample Q data: read every sector for small files, every Nth for large files
        int sampleInterval = image.SectorCount > 100_000 ? 75 : 1;

        while (stream.Position + SubSectorSize <= stream.Length)
        {
            int bytesRead = stream.Read(sectorBuf, 0, SubSectorSize);
            if (bytesRead < SubSectorSize) break;

            if (sectorIndex % sampleInterval == 0 || image.QEntries.Count < 1000)
            {
                var q = ExtractQChannel(sectorBuf, image.Format);
                var entry = ParseQEntry(q, sectorIndex);

                if (entry != null)
                {
                    image.QEntries.Add(entry);

                    // Extract MCN from ADR=2
                    if (entry.Adr == 2 && string.IsNullOrEmpty(image.MediaCatalogNumber))
                    {
                        image.MediaCatalogNumber = ExtractMcn(q);
                    }

                    // Extract ISRC from ADR=3
                    if (entry.Adr == 3 && entry.TrackNumber > 0)
                    {
                        var isrc = ExtractIsrc(q);
                        if (!string.IsNullOrEmpty(isrc) && !image.IsrcCodes.ContainsKey(entry.TrackNumber))
                        {
                            image.IsrcCodes[entry.TrackNumber] = isrc;
                        }
                    }
                }
            }

            sectorIndex++;
        }
    }

    /// <summary>
    /// Parses a Q subchannel entry from 12 bytes of Q data.
    /// </summary>
    private static QSubchannelEntry? ParseQEntry(byte[] q, long sectorIndex)
    {
        if (q.Length < ChannelSize) return null;

        byte controlAdr = q[0];
        byte adr = (byte)(controlAdr & 0x0F);
        byte control = (byte)((controlAdr >> 4) & 0x0F);

        // Only process ADR 1 (TOC position data) for track entries
        if (adr != 1) return new QSubchannelEntry
        {
            SectorIndex = sectorIndex,
            Control = control,
            Adr = adr,
            CrcValid = VerifyCrc16(q)
        };

        return new QSubchannelEntry
        {
            SectorIndex = sectorIndex,
            Control = control,
            Adr = adr,
            TrackNumberBcd = q[1],
            IndexNumberBcd = q[2],
            RelMinBcd = q[3],
            RelSecBcd = q[4],
            RelFrameBcd = q[5],
            // q[6] is zero/reserved
            AbsMinBcd = q[7],
            AbsSecBcd = q[8],
            AbsFrameBcd = q[9],
            Crc16 = (ushort)((q[10] << 8) | q[11]),
            CrcValid = VerifyCrc16(q)
        };
    }

    /// <summary>
    /// Extracts Media Catalog Number from Q subchannel ADR=2 data.
    /// MCN is stored as 13 BCD digits in bytes 1-7 of the Q channel.
    /// </summary>
    private static string? ExtractMcn(byte[] q)
    {
        if (q.Length < 10) return null;

        var mcn = new char[13];
        int charIdx = 0;

        for (int i = 1; i <= 6 && charIdx < 13; i++)
        {
            byte bcd = q[i];
            mcn[charIdx++] = (char)('0' + ((bcd >> 4) & 0x0F));
            if (charIdx < 13)
                mcn[charIdx++] = (char)('0' + (bcd & 0x0F));
        }

        // Last digit from byte 7 high nibble
        if (charIdx < 13)
            mcn[charIdx] = (char)('0' + ((q[7] >> 4) & 0x0F));

        var mcnStr = new string(mcn);
        // Validate: MCN should be all digits
        return mcnStr.All(char.IsDigit) ? mcnStr : null;
    }

    /// <summary>
    /// Extracts ISRC code from Q subchannel ADR=3 data.
    /// ISRC is stored as 12 characters (5 alphanumeric + 7 numeric) in bytes 1-9.
    /// </summary>
    private static string? ExtractIsrc(byte[] q)
    {
        if (q.Length < 10) return null;

        var isrc = new char[12];

        // First 5 chars: 6-bit packed alphanumeric (A-Z + 0-9)
        int bits = (q[1] << 24) | (q[2] << 16) | (q[3] << 8) | q[4];
        for (int i = 0; i < 5; i++)
        {
            int val = (bits >> (26 - i * 6)) & 0x3F;
            isrc[i] = val < 10 ? (char)('0' + val) : (char)('A' + val - 10);
        }

        // Last 7 chars: BCD digits
        isrc[5] = (char)('0' + ((q[5] >> 4) & 0x0F));
        isrc[6] = (char)('0' + (q[5] & 0x0F));
        isrc[7] = (char)('0' + ((q[6] >> 4) & 0x0F));
        isrc[8] = (char)('0' + (q[6] & 0x0F));
        isrc[9] = (char)('0' + ((q[7] >> 4) & 0x0F));
        isrc[10] = (char)('0' + (q[7] & 0x0F));
        isrc[11] = (char)('0' + ((q[8] >> 4) & 0x0F));

        var isrcStr = new string(isrc);
        return isrcStr.All(c => char.IsLetterOrDigit(c)) ? isrcStr : null;
    }

    // -----------------------------------------------------------------------
    //  Private — R-W subchannel analysis
    // -----------------------------------------------------------------------

    /// <summary>
    /// Detects CD-TEXT and CD-G content in R-W subchannels.
    /// </summary>
    private static void DetectRwContent(Stream stream, SubImage image)
    {
        var sectorBuf = new byte[SubSectorSize];
        int sectorsToCheck = (int)Math.Min(750, image.SectorCount); // Check first 10 seconds

        for (int i = 0; i < sectorsToCheck; i++)
        {
            if (stream.Read(sectorBuf, 0, SubSectorSize) < SubSectorSize) break;

            var rw = ExtractRwChannels(sectorBuf, image.Format);

            // CD-TEXT: R channel starts with pack type >= 0x80
            if (rw.Length >= ChannelSize && (rw[0] & 0x80) != 0)
            {
                image.HasCdText = true;
            }

            // CD-G: check for CD-G instruction in R channel
            // CD-G packets have command byte 0x09 (SC instruction)
            if (rw.Length >= 2 && (rw[0] & 0x3F) == CdGCommandByte)
            {
                image.HasCdG = true;
            }

            if (image.HasCdText && image.HasCdG)
                break; // Found both, no need to continue
        }
    }

    // -----------------------------------------------------------------------
    //  Private — Track list reconstruction
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds a track list from Q subchannel entries.
    /// </summary>
    private static void BuildTrackList(SubImage image)
    {
        var trackDict = new Dictionary<int, SubTrackInfo>();

        foreach (var entry in image.QEntries.Where(e => e.Adr == 1 && e.TrackNumber > 0 && e.TrackNumber < 100))
        {
            if (!trackDict.TryGetValue(entry.TrackNumber, out var track))
            {
                track = new SubTrackInfo
                {
                    TrackNumber = entry.TrackNumber,
                    StartLba = entry.AbsoluteLba,
                    IsData = entry.IsDataTrack,
                    Control = entry.Control
                };
                trackDict[entry.TrackNumber] = track;
            }

            // Update start LBA from INDEX 01
            if (entry.IndexNumber == 1 && entry.AbsoluteLba >= 0)
            {
                track.StartLba = entry.AbsoluteLba;
            }

            // Record pregap LBA from INDEX 00
            if (entry.IndexNumber == 0 && entry.AbsoluteLba >= 0)
            {
                track.PregapLba = entry.AbsoluteLba;
            }
        }

        // Add ISRC codes to tracks
        foreach (var (trackNum, isrc) in image.IsrcCodes)
        {
            if (trackDict.TryGetValue(trackNum, out var track))
                track.Isrc = isrc;
        }

        image.Tracks = trackDict.Values.OrderBy(t => t.TrackNumber).ToList();
    }

    // -----------------------------------------------------------------------
    //  Private — CRC-16 verification
    // -----------------------------------------------------------------------

    /// <summary>
    /// Verifies the CRC-16 (CCITT) of Q subchannel data.
    /// Polynomial: x^16 + x^12 + x^5 + 1 (0x1021)
    /// The CRC covers bytes 0-9 and is stored inverted in bytes 10-11.
    /// </summary>
    private static bool VerifyCrc16(byte[] q)
    {
        if (q.Length < 12) return false;

        ushort crc = 0;
        for (int i = 0; i < 10; i++)
        {
            crc ^= (ushort)(q[i] << 8);
            for (int bit = 0; bit < 8; bit++)
            {
                if ((crc & 0x8000) != 0)
                    crc = (ushort)((crc << 1) ^ 0x1021);
                else
                    crc <<= 1;
            }
        }

        // CRC is stored inverted (per Red Book spec)
        ushort storedCrc = (ushort)((q[10] << 8) | q[11]);
        return crc == (ushort)~storedCrc;
    }
}
