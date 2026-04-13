// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;

namespace OpenBurningSuite.Models;

/// <summary>
/// Represents parsed subchannel data from a .sub file.
///
/// Subchannel data layout (per ECMA-394 / Red Book):
/// Each sector has 96 bytes of interleaved P-W subchannel data.
/// The data is organized as 8 subchannel groups (P, Q, R, S, T, U, V, W),
/// each containing 12 bytes per sector, interleaved byte-by-byte.
///
/// Interleaved format (96 bytes per sector):
///   Byte  0: bits 7-0 = P7..P0 (P subchannel, first byte)
///   Byte  1: bits 7-0 = Q7..Q0 (Q subchannel, first byte)
///   ... alternating P-W for 12 groups = 96 bytes total
///
/// Actually, the standard CloneCD .sub format uses a simpler layout:
///   96 bytes per sector, containing P(12) + Q(12) + R(12) + S(12) + T(12) + U(12) + V(12) + W(12)
///   All channels are stored sequentially (not interleaved).
///
/// P subchannel (12 bytes): Pause/play flag
///   - All 0xFF in pause areas, all 0x00 in data areas
///
/// Q subchannel (12 bytes): Control/address information
///   - Byte 0: Control (4 bits) + ADR (4 bits)
///     Control bits: 0x00=2-channel audio, 0x01=2-channel audio+pre-emphasis,
///                   0x04=data track, 0x08=4-channel audio
///     ADR: 0x01=TOC data, 0x02=Media Catalog Number, 0x03=ISRC
///   - Bytes 1-9: Data (varies by ADR mode)
///     ADR 1: Track#(BCD), Index#(BCD), Min(BCD), Sec(BCD), Frame(BCD),
///             Zero, AMin(BCD), ASec(BCD), AFrame(BCD)
///     ADR 2: MCN (13 BCD digits), Zero, AFrame
///     ADR 3: ISRC (12 alphanumeric chars), Zero, AFrame
///   - Bytes 10-11: CRC-16 (CCITT, polynomial x^16+x^12+x^5+1)
///
/// R-W subchannels (72 bytes total): CD-TEXT, CD-G graphics, or zero-filled
/// </summary>
public class SubImage
{
    /// <summary>Path to the .sub file.</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>Total number of sectors (each sector has 96 bytes of subchannel data).</summary>
    public long SectorCount { get; set; }

    /// <summary>File size in bytes.</summary>
    public long FileSize { get; set; }

    /// <summary>Whether the file size is consistent (divisible by 96).</summary>
    public bool IsValid => string.IsNullOrEmpty(ErrorMessage) && FileSize > 0 && FileSize % SubchannelSizePerSector == 0;

    /// <summary>Error message if parsing failed.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Subchannel data format: "Interleaved" (P-W interleaved) or "Sequential" (P, Q, R-W sequential).</summary>
    public SubFormat Format { get; set; } = SubFormat.Sequential;

    /// <summary>Extracted Q subchannel entries for TOC reconstruction.</summary>
    public List<QSubchannelEntry> QEntries { get; set; } = new();

    /// <summary>Media Catalog Number (MCN/UPC/EAN) extracted from Q subchannel ADR=2.</summary>
    public string? MediaCatalogNumber { get; set; }

    /// <summary>ISRC codes per track extracted from Q subchannel ADR=3.</summary>
    public Dictionary<int, string> IsrcCodes { get; set; } = new();

    /// <summary>Whether CD-TEXT data is present in the R-W subchannels.</summary>
    public bool HasCdText { get; set; }

    /// <summary>Whether CD-G (CD+Graphics) data is present in the R-W subchannels.</summary>
    public bool HasCdG { get; set; }

    /// <summary>Track information derived from Q subchannel data.</summary>
    public List<SubTrackInfo> Tracks { get; set; } = new();

    /// <summary>Size of subchannel data per sector.</summary>
    public const int SubchannelSizePerSector = 96;

    /// <summary>Size of each subchannel (P, Q, R, S, T, U, V, W) per sector.</summary>
    public const int ChannelSizePerSector = 12;

    /// <summary>Returns a summary of the subchannel data.</summary>
    public override string ToString() =>
        $"SUB: {SectorCount} sectors, Format={Format}" +
        (HasCdText ? ", CD-TEXT" : "") +
        (HasCdG ? ", CD-G" : "") +
        (!string.IsNullOrEmpty(MediaCatalogNumber) ? $", MCN={MediaCatalogNumber}" : "");
}

/// <summary>Subchannel data storage format.</summary>
public enum SubFormat
{
    /// <summary>
    /// P, Q, R-W channels stored sequentially (12+12+72 = 96 bytes per sector).
    /// This is the standard CloneCD .sub format.
    /// </summary>
    Sequential,

    /// <summary>
    /// All 8 channels interleaved byte-by-byte (8×12 = 96 bytes per sector).
    /// Each of the 12 byte positions contains 8 bits, one per channel (P-W).
    /// Used by some disc reading software.
    /// </summary>
    Interleaved
}

/// <summary>
/// A Q subchannel data entry extracted from a .sub file.
/// </summary>
public class QSubchannelEntry
{
    /// <summary>Sector index (0-based) where this entry was found.</summary>
    public long SectorIndex { get; set; }

    /// <summary>Control field (4 bits): data type and copy permissions.</summary>
    public byte Control { get; set; }

    /// <summary>ADR field (4 bits): address mode (1=TOC, 2=MCN, 3=ISRC).</summary>
    public byte Adr { get; set; }

    /// <summary>Track number (BCD). 0x00=lead-in, 0xAA=lead-out.</summary>
    public byte TrackNumberBcd { get; set; }

    /// <summary>Track number (decoded from BCD).</summary>
    public int TrackNumber => BcdToBinary(TrackNumberBcd);

    /// <summary>Index number (BCD). 0x00=pregap, 0x01+=actual data.</summary>
    public byte IndexNumberBcd { get; set; }

    /// <summary>Index number (decoded from BCD).</summary>
    public int IndexNumber => BcdToBinary(IndexNumberBcd);

    /// <summary>Relative time within track: minutes (BCD).</summary>
    public byte RelMinBcd { get; set; }

    /// <summary>Relative time within track: seconds (BCD).</summary>
    public byte RelSecBcd { get; set; }

    /// <summary>Relative time within track: frames (BCD).</summary>
    public byte RelFrameBcd { get; set; }

    /// <summary>Absolute time on disc: minutes (BCD).</summary>
    public byte AbsMinBcd { get; set; }

    /// <summary>Absolute time on disc: seconds (BCD).</summary>
    public byte AbsSecBcd { get; set; }

    /// <summary>Absolute time on disc: frames (BCD).</summary>
    public byte AbsFrameBcd { get; set; }

    /// <summary>CRC-16 value from the subchannel data.</summary>
    public ushort Crc16 { get; set; }

    /// <summary>Whether the CRC-16 checksum is valid.</summary>
    public bool CrcValid { get; set; }

    /// <summary>Absolute LBA position (derived from absolute MSF).</summary>
    public int AbsoluteLba => MsfToLba(BcdToBinary(AbsMinBcd), BcdToBinary(AbsSecBcd), BcdToBinary(AbsFrameBcd));

    /// <summary>Whether this is a data track (vs audio).</summary>
    public bool IsDataTrack => (Control & 0x04) != 0;

    /// <summary>Converts BCD byte to binary.</summary>
    private static int BcdToBinary(byte bcd) => (bcd >> 4) * 10 + (bcd & 0x0F);

    /// <summary>Converts MSF to LBA (per Red Book: LBA = M*60*75 + S*75 + F - 150).</summary>
    private static int MsfToLba(int m, int s, int f) => m * 60 * 75 + s * 75 + f - 150;
}

/// <summary>
/// Track information derived from Q subchannel analysis.
/// </summary>
public class SubTrackInfo
{
    /// <summary>Track number (1-99).</summary>
    public int TrackNumber { get; set; }

    /// <summary>Start LBA of the track (from INDEX 01).</summary>
    public int StartLba { get; set; }

    /// <summary>Pregap LBA (from INDEX 00), -1 if no pregap.</summary>
    public int PregapLba { get; set; } = -1;

    /// <summary>Whether this is a data track.</summary>
    public bool IsData { get; set; }

    /// <summary>Control field value from Q subchannel.</summary>
    public byte Control { get; set; }

    /// <summary>ISRC code if available.</summary>
    public string? Isrc { get; set; }
}
