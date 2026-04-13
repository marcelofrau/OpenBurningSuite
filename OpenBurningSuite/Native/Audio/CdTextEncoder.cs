// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Text;

namespace OpenBurningSuite.Native.Audio;

/// <summary>
/// Encodes CD-Text data into binary pack format per Red Book (IEC 60908) Annex J
/// and MMC-3/MMC-5 (SCSI Multimedia Commands) READ/WRITE TOC/PMA/ATIP specifications.
///
/// CD-Text is stored in R-W subchannel packs during the lead-in area of an audio CD.
/// Each pack is 18 bytes: 4-byte header + 12 bytes text data + 2-byte CRC-16.
///
/// Pack types:
///   0x80 = Title (ALBUM_NAME for track 0, TRACK_NAME for tracks 1-99)
///   0x81 = Performer
///   0x82 = Songwriter
///   0x83 = Composer
///   0x84 = Arranger
///   0x85 = Message
///   0x86 = Disc Identification (ISRC-like for disc)
///   0x87 = Genre
///   0x88 = TOC Information
///   0x89 = Second TOC Information
///   0x8D = Closed Information (private use)
///   0x8E = UPC/EAN or ISRC per track
///   0x8F = Size Information (block descriptor)
/// </summary>
public static class CdTextEncoder
{
    /// <summary>CD-Text pack type identifiers per Red Book Annex J.</summary>
    public const byte PackTypeTitle      = 0x80;
    public const byte PackTypePerformer  = 0x81;
    public const byte PackTypeSongwriter = 0x82;
    public const byte PackTypeComposer   = 0x83;
    public const byte PackTypeArranger   = 0x84;
    public const byte PackTypeMessage    = 0x85;
    public const byte PackTypeDiscId     = 0x86;
    public const byte PackTypeGenre      = 0x87;
    public const byte PackTypeTocInfo    = 0x88;
    public const byte PackTypeTocInfo2   = 0x89;
    public const byte PackTypeUpcIsrc    = 0x8E;
    public const byte PackTypeSizeInfo   = 0x8F;

    /// <summary>Size of a single CD-Text pack in bytes.</summary>
    private const int PackSize = 18;

    /// <summary>Number of text data bytes per pack.</summary>
    private const int TextBytesPerPack = 12;

    /// <summary>Maximum number of tracks for CD-Text (Red Book allows 1-99).</summary>
    private const int MaxTracks = 99;

    /// <summary>
    /// Encodes complete CD-Text data for an audio CD into binary pack format.
    /// Returns the raw byte array suitable for writing to disc via SCSI WRITE TOC
    /// or embedding in a CUE sheet's CDTEXTFILE directive.
    /// </summary>
    /// <param name="albumTitle">Album/disc title (track 0 title).</param>
    /// <param name="albumPerformer">Album/disc performer (track 0 performer).</param>
    /// <param name="trackTitles">Per-track titles (index 0 = track 1).</param>
    /// <param name="trackPerformers">Per-track performers (index 0 = track 1).</param>
    /// <param name="songwriter">Optional songwriter text.</param>
    /// <param name="composer">Optional composer text.</param>
    /// <param name="arranger">Optional arranger text.</param>
    /// <param name="message">Optional disc message.</param>
    /// <param name="genre">Optional genre text.</param>
    /// <param name="discId">Optional disc identification string.</param>
    /// <param name="upcEan">Optional UPC/EAN code for the disc.</param>
    /// <param name="isrcCodes">Optional ISRC codes per track (index 0 = track 1).</param>
    /// <param name="firstTrackNumber">First track number (usually 1).</param>
    /// <param name="lastTrackNumber">Last track number.</param>
    /// <returns>Complete CD-Text binary data including size header.</returns>
    public static byte[] Encode(
        string albumTitle,
        string albumPerformer,
        string[]? trackTitles = null,
        string[]? trackPerformers = null,
        string? songwriter = null,
        string? composer = null,
        string? arranger = null,
        string? message = null,
        string? genre = null,
        string? discId = null,
        string? upcEan = null,
        string[]? isrcCodes = null,
        int firstTrackNumber = 1,
        int lastTrackNumber = 0)
    {
        trackTitles ??= Array.Empty<string>();
        trackPerformers ??= Array.Empty<string>();
        isrcCodes ??= Array.Empty<string>();

        if (lastTrackNumber <= 0)
            lastTrackNumber = Math.Max(firstTrackNumber, Math.Max(trackTitles.Length, trackPerformers.Length));

        var allPacks = new List<byte[]>();
        int sequenceCounter = 0;

        // Track count for size info
        var packTypeCounts = new Dictionary<byte, int>();
        int totalTracks = lastTrackNumber - firstTrackNumber + 1;

        // Encode each pack type that has data
        EncodeSinglePackType(allPacks, ref sequenceCounter, PackTypeTitle,
            albumTitle, trackTitles, totalTracks, packTypeCounts);

        EncodeSinglePackType(allPacks, ref sequenceCounter, PackTypePerformer,
            albumPerformer, trackPerformers, totalTracks, packTypeCounts);

        if (!string.IsNullOrEmpty(songwriter))
        {
            EncodeSinglePackType(allPacks, ref sequenceCounter, PackTypeSongwriter,
                songwriter, Array.Empty<string>(), totalTracks, packTypeCounts);
        }

        if (!string.IsNullOrEmpty(composer))
        {
            EncodeSinglePackType(allPacks, ref sequenceCounter, PackTypeComposer,
                composer, Array.Empty<string>(), totalTracks, packTypeCounts);
        }

        if (!string.IsNullOrEmpty(arranger))
        {
            EncodeSinglePackType(allPacks, ref sequenceCounter, PackTypeArranger,
                arranger, Array.Empty<string>(), totalTracks, packTypeCounts);
        }

        if (!string.IsNullOrEmpty(message))
        {
            EncodeSinglePackType(allPacks, ref sequenceCounter, PackTypeMessage,
                message, Array.Empty<string>(), totalTracks, packTypeCounts);
        }

        if (!string.IsNullOrEmpty(discId))
        {
            EncodeSinglePackType(allPacks, ref sequenceCounter, PackTypeDiscId,
                discId, Array.Empty<string>(), 0, packTypeCounts);
        }

        if (!string.IsNullOrEmpty(genre))
        {
            EncodeGenrePacks(allPacks, ref sequenceCounter, genre, packTypeCounts);
        }

        // Encode TOC Information (pack type 0x88) - required
        EncodeTocInfoPacks(allPacks, ref sequenceCounter, firstTrackNumber, lastTrackNumber, packTypeCounts);

        // Encode UPC/EAN and ISRC codes (pack type 0x8E)
        if (!string.IsNullOrEmpty(upcEan) || isrcCodes.Length > 0)
        {
            EncodeUpcIsrcPacks(allPacks, ref sequenceCounter, upcEan, isrcCodes, totalTracks, packTypeCounts);
        }

        // Encode Size Information (pack type 0x8F) - must be last
        EncodeSizeInfoPacks(allPacks, ref sequenceCounter, packTypeCounts,
            firstTrackNumber, lastTrackNumber, totalTracks);

        // Calculate CRC for all packs
        foreach (var pack in allPacks)
        {
            CalculatePackCrc(pack);
        }

        // Build final output: 4-byte header + all packs
        // Header: 2-byte total length (big-endian), 1-byte reserved, 1-byte reserved
        int totalPackBytes = allPacks.Count * PackSize;
        var result = new byte[4 + totalPackBytes];

        // Total length field includes itself (2 bytes) but standard says length of data following
        int dataLen = totalPackBytes + 2; // +2 for the reserved bytes
        result[0] = (byte)(dataLen >> 8);
        result[1] = (byte)(dataLen & 0xFF);
        result[2] = 0; // Reserved
        result[3] = 0; // Reserved

        for (int i = 0; i < allPacks.Count; i++)
        {
            Buffer.BlockCopy(allPacks[i], 0, result, 4 + i * PackSize, PackSize);
        }

        return result;
    }

    /// <summary>
    /// Encodes packs for a single text pack type (title, performer, etc.).
    /// Track 0 = disc/album level, tracks 1-99 = individual track level.
    /// Text that doesn't fit in one pack continues in the next pack.
    /// </summary>
    private static void EncodeSinglePackType(
        List<byte[]> allPacks,
        ref int sequenceCounter,
        byte packType,
        string discLevelText,
        string[] trackTexts,
        int totalTracks,
        Dictionary<byte, int> packTypeCounts)
    {
        var texts = new List<byte[]>();

        // Track 0 = disc/album level text
        texts.Add(EncodeText(discLevelText ?? string.Empty));

        // Tracks 1..N
        for (int t = 0; t < totalTracks; t++)
        {
            var text = t < trackTexts.Length ? trackTexts[t] : string.Empty;
            texts.Add(EncodeText(text ?? string.Empty));
        }

        // Serialize text entries into 12-byte pack payloads
        // Each text string is null-terminated; strings flow continuously across packs
        var textStream = new List<byte>();
        foreach (var textBytes in texts)
        {
            textStream.AddRange(textBytes);
            textStream.Add(0); // null terminator
        }

        int packsForType = 0;
        int offset = 0;
        int trackIndex = 0;

        while (offset < textStream.Count)
        {
            var pack = new byte[PackSize];
            pack[0] = packType;
            pack[1] = (byte)trackIndex; // Track number this pack starts with
            pack[2] = (byte)(sequenceCounter & 0xFF); // Sequence number
            // pack[3] = block/character position indicator
            // Bits 7-4: block number (0 for single block), Bits 3-0: character position
            // Character position indicates how many bytes of the current text field
            // have already been written in previous packs (capped to 0x0F per spec).
            int charPos = offset % TextBytesPerPack == 0
                ? 0
                : offset - FindLastNullBefore(textStream, offset);
            pack[3] = (byte)(charPos & 0x0F);

            int bytesToCopy = Math.Min(TextBytesPerPack, textStream.Count - offset);
            for (int i = 0; i < bytesToCopy; i++)
                pack[4 + i] = textStream[offset + i];

            // Pad remaining bytes with 0x00
            for (int i = bytesToCopy; i < TextBytesPerPack; i++)
                pack[4 + i] = 0x00;

            allPacks.Add(pack);
            sequenceCounter++;
            packsForType++;

            // Advance track index: count null terminators in the data we just wrote
            for (int i = offset; i < offset + bytesToCopy; i++)
            {
                if (textStream[i] == 0)
                    trackIndex++;
            }

            offset += bytesToCopy;
        }

        packTypeCounts[packType] = packsForType;
    }

    /// <summary>Finds the position after the last null byte before the given offset.</summary>
    private static int FindLastNullBefore(List<byte> stream, int offset)
    {
        for (int i = offset - 1; i >= 0; i--)
        {
            if (stream[i] == 0)
                return i + 1;
        }
        return 0;
    }

    /// <summary>
    /// Encodes genre packs (type 0x87).
    /// First 2 bytes are genre code (big-endian), followed by supplementary genre text.
    /// </summary>
    private static void EncodeGenrePacks(
        List<byte[]> allPacks,
        ref int sequenceCounter,
        string genreText,
        Dictionary<byte, int> packTypeCounts)
    {
        var pack = new byte[PackSize];
        pack[0] = PackTypeGenre;
        pack[1] = 0; // Track 0 (disc level)
        pack[2] = (byte)(sequenceCounter & 0xFF);
        pack[3] = 0;

        // Genre code: 0x0000 = not used / custom text
        pack[4] = 0x00;
        pack[5] = 0x00;

        // Genre supplementary text (up to 10 bytes in first pack)
        var textBytes = EncodeText(genreText);
        int toCopy = Math.Min(textBytes.Length, TextBytesPerPack - 2);
        Buffer.BlockCopy(textBytes, 0, pack, 6, toCopy);

        allPacks.Add(pack);
        sequenceCounter++;
        packTypeCounts[PackTypeGenre] = 1;
    }

    /// <summary>
    /// Encodes TOC Information packs (type 0x88).
    /// Contains first/last track numbers and lead-out point for the disc.
    /// </summary>
    private static void EncodeTocInfoPacks(
        List<byte[]> allPacks,
        ref int sequenceCounter,
        int firstTrack,
        int lastTrack,
        Dictionary<byte, int> packTypeCounts)
    {
        var pack = new byte[PackSize];
        pack[0] = PackTypeTocInfo;
        pack[1] = 0; // Track 0
        pack[2] = (byte)(sequenceCounter & 0xFF);
        pack[3] = 0;

        // Bytes 4-5: first and last track numbers
        pack[4] = (byte)firstTrack;
        pack[5] = (byte)lastTrack;

        // Bytes 6-8: Lead-out track start address (MSF) - set to 0 as placeholder
        // The actual address would be calculated from total disc duration
        pack[6] = 0; // Minutes
        pack[7] = 0; // Seconds
        pack[8] = 0; // Frames

        // Bytes 9-15: Track start times (first pack has tracks 1-6 or fewer)
        // Each uses 3 bytes MSF but we use 0 placeholders since the
        // actual values come from the TOC data at burn time
        for (int i = 9; i < 16; i++)
            pack[i] = 0;

        allPacks.Add(pack);
        sequenceCounter++;
        packTypeCounts[PackTypeTocInfo] = 1;
    }

    /// <summary>
    /// Encodes UPC/EAN and ISRC packs (type 0x8E).
    /// Track 0 = UPC/EAN (13 chars), Tracks 1-99 = ISRC codes (12 chars each).
    /// </summary>
    private static void EncodeUpcIsrcPacks(
        List<byte[]> allPacks,
        ref int sequenceCounter,
        string? upcEan,
        string[] isrcCodes,
        int totalTracks,
        Dictionary<byte, int> packTypeCounts)
    {
        int packsForType = 0;

        // UPC/EAN for disc (track 0) - 13 ASCII digits
        if (!string.IsNullOrEmpty(upcEan))
        {
            var pack = new byte[PackSize];
            pack[0] = PackTypeUpcIsrc;
            pack[1] = 0; // Track 0
            pack[2] = (byte)(sequenceCounter & 0xFF);
            pack[3] = 0;

            var upcBytes = Encoding.ASCII.GetBytes(upcEan.PadRight(13, '0')[..13]);
            Buffer.BlockCopy(upcBytes, 0, pack, 4, Math.Min(upcBytes.Length, TextBytesPerPack));

            allPacks.Add(pack);
            sequenceCounter++;
            packsForType++;
        }

        // ISRC codes per track - 12 ASCII characters each
        for (int t = 0; t < totalTracks && t < isrcCodes.Length; t++)
        {
            if (string.IsNullOrEmpty(isrcCodes[t])) continue;

            var pack = new byte[PackSize];
            pack[0] = PackTypeUpcIsrc;
            pack[1] = (byte)(t + 1); // Track number (1-based)
            pack[2] = (byte)(sequenceCounter & 0xFF);
            pack[3] = 0;

            var isrcBytes = Encoding.ASCII.GetBytes(isrcCodes[t].PadRight(12, '0')[..12]);
            Buffer.BlockCopy(isrcBytes, 0, pack, 4, Math.Min(isrcBytes.Length, TextBytesPerPack));

            allPacks.Add(pack);
            sequenceCounter++;
            packsForType++;
        }

        packTypeCounts[PackTypeUpcIsrc] = packsForType;
    }

    /// <summary>
    /// Encodes Size Information packs (type 0x8F).
    /// Three packs that describe the CD-Text block structure, character set,
    /// and pack counts per type. This must be the last pack type encoded.
    /// </summary>
    private static void EncodeSizeInfoPacks(
        List<byte[]> allPacks,
        ref int sequenceCounter,
        Dictionary<byte, int> packTypeCounts,
        int firstTrack,
        int lastTrack,
        int totalTracks)
    {
        // Size Info requires exactly 3 packs per block
        for (int sizePackIdx = 0; sizePackIdx < 3; sizePackIdx++)
        {
            var pack = new byte[PackSize];
            pack[0] = PackTypeSizeInfo;
            pack[1] = (byte)sizePackIdx;
            pack[2] = (byte)(sequenceCounter & 0xFF);
            pack[3] = 0;

            switch (sizePackIdx)
            {
                case 0:
                    // Pack 0: Character set, first/last track, copyright flags, pack counts (low byte)
                    pack[4] = 0x00; // Character set: ISO 8859-1
                    pack[5] = (byte)firstTrack;
                    pack[6] = (byte)lastTrack;
                    pack[7] = 0x00; // Copyright flags (0 = no copyright asserted)
                    // Pack count for types 0x80-0x87 (low byte of count)
                    for (int pt = 0; pt < 8; pt++)
                    {
                        byte type = (byte)(0x80 + pt);
                        pack[8 + pt] = (byte)(packTypeCounts.GetValueOrDefault(type, 0) & 0xFF);
                    }
                    break;

                case 1:
                    // Pack 1: Pack counts continued (0x88-0x8F) + last sequence numbers
                    // Types 0x88-0x8F
                    for (int pt = 0; pt < 8; pt++)
                    {
                        byte type = (byte)(0x88 + pt);
                        int count = type == PackTypeSizeInfo ? 3 : packTypeCounts.GetValueOrDefault(type, 0);
                        pack[4 + pt] = (byte)(count & 0xFF);
                    }

                    // Last sequence counter values for each block (up to 8 blocks).
                    // At this point, sequenceCounter is the seq# for pack index 1 (this pack).
                    // Two more packs remain (this one = sequenceCounter, next = sequenceCounter+1).
                    // So the last sequence number in block 0 = sequenceCounter + 1.
                    pack[12] = (byte)((sequenceCounter + 1) & 0xFF); // Block 0 last seq
                    for (int b = 1; b < 4; b++)
                        pack[12 + b] = 0; // Blocks 1-3 not used
                    break;

                case 2:
                    // Pack 2: Language codes for each block
                    pack[4] = 0x09; // Block 0: English (ISO 639-2 code)
                    for (int b = 1; b < 8; b++)
                        pack[4 + b] = 0x00; // Blocks 1-7 not used
                    // Remaining bytes are padding
                    break;
            }

            allPacks.Add(pack);
            sequenceCounter++;
        }

        packTypeCounts[PackTypeSizeInfo] = 3;
    }

    /// <summary>
    /// Encodes a text string to ISO 8859-1 bytes for CD-Text.
    /// Falls back to ASCII for characters not representable in ISO 8859-1.
    /// </summary>
    private static byte[] EncodeText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return Array.Empty<byte>();

        try
        {
            // Use ISO 8859-1 (Latin-1) encoding as specified by Red Book
            return Encoding.Latin1.GetBytes(text);
        }
        catch
        {
            // Fallback to ASCII, replacing non-ASCII chars with '?'
            return Encoding.ASCII.GetBytes(text);
        }
    }

    /// <summary>
    /// Calculates and writes the CRC-16 for a CD-Text pack.
    /// Uses the CRC-16-CCITT polynomial (x^16 + x^12 + x^5 + 1) with
    /// initial value 0xFFFF as specified in Red Book Annex J.
    /// The CRC is stored inverted (XOR 0xFFFF) in big-endian byte order
    /// in the last 2 bytes of the pack.
    /// </summary>
    private static void CalculatePackCrc(byte[] pack)
    {
        ushort crc = 0xFFFF;

        // CRC is calculated over the first 16 bytes (header + data)
        for (int i = 0; i < 16; i++)
        {
            crc ^= (ushort)(pack[i] << 8);
            for (int bit = 0; bit < 8; bit++)
            {
                if ((crc & 0x8000) != 0)
                    crc = (ushort)((crc << 1) ^ 0x1021);
                else
                    crc = (ushort)(crc << 1);
            }
        }

        // Invert and store big-endian per Red Book specification
        crc ^= 0xFFFF;
        pack[16] = (byte)(crc >> 8);
        pack[17] = (byte)(crc & 0xFF);
    }

    /// <summary>
    /// Generates a CD-Text binary file (.cdt) suitable for use with CDTEXTFILE directive
    /// in CUE sheets. This is the same binary format without the 4-byte SCSI header.
    /// </summary>
    public static byte[] EncodeToCdtFile(
        string albumTitle,
        string albumPerformer,
        string[]? trackTitles = null,
        string[]? trackPerformers = null,
        string? songwriter = null,
        string? composer = null,
        string? arranger = null,
        string? message = null,
        string? genre = null,
        string? discId = null,
        string? upcEan = null,
        string[]? isrcCodes = null,
        int firstTrackNumber = 1,
        int lastTrackNumber = 0)
    {
        var full = Encode(albumTitle, albumPerformer, trackTitles, trackPerformers,
            songwriter, composer, arranger, message, genre, discId, upcEan, isrcCodes,
            firstTrackNumber, lastTrackNumber);

        // Strip the 4-byte SCSI header to get raw pack data for .cdt files
        if (full.Length <= 4)
            return Array.Empty<byte>();

        var cdt = new byte[full.Length - 4];
        Buffer.BlockCopy(full, 4, cdt, 0, cdt.Length);
        return cdt;
    }
}
