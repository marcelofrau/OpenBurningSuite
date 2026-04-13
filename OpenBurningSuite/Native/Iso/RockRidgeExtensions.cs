// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Text;

namespace OpenBurningSuite.Native.Iso;

/// <summary>
/// Implements Rock Ridge Interchange Protocol (RRIP / IEEE P1282) extension records
/// for ISO 9660 images, following the System Use Sharing Protocol (SUSP / IEEE 1281).
/// </summary>
/// <remarks>
/// All multi-byte integer fields use ISO 9660 "both-endian" encoding (ECMA-119 §7.3.3):
/// little-endian uint32 followed by big-endian uint32, totaling 8 bytes per value.
/// </remarks>
public static class RockRidgeExtensions
{
    // ------------------------------------------------------------------
    // POSIX st_mode constants (IEEE Std 1003.1)
    // ------------------------------------------------------------------

    /// <summary>Directory file type (0o040000).</summary>
    public const uint S_IFDIR = 0x4000;

    /// <summary>Regular file type (0o100000).</summary>
    public const uint S_IFREG = 0x8000;

    /// <summary>Symbolic link file type (0o120000).</summary>
    public const uint S_IFLNK = 0xA000;

    /// <summary>Block device file type (0o060000).</summary>
    public const uint S_IFBLK = 0x6000;

    /// <summary>Character device file type (0o020000).</summary>
    public const uint S_IFCHR = 0x2000;

    /// <summary>Named pipe / FIFO file type (0o010000).</summary>
    public const uint S_IFIFO = 0x1000;

    /// <summary>Socket file type (0o140000).</summary>
    public const uint S_IFSOCK = 0xC000;

    /// <summary>Owner read permission (0o000400).</summary>
    public const uint S_IRUSR = 0x0100;

    /// <summary>Owner write permission (0o000200).</summary>
    public const uint S_IWUSR = 0x0080;

    /// <summary>Owner execute permission (0o000100).</summary>
    public const uint S_IXUSR = 0x0040;

    /// <summary>Group read permission (0o000040).</summary>
    public const uint S_IRGRP = 0x0020;

    /// <summary>Group write permission (0o000020).</summary>
    public const uint S_IWGRP = 0x0010;

    /// <summary>Group execute permission (0o000010).</summary>
    public const uint S_IXGRP = 0x0008;

    /// <summary>Others read permission (0o000004).</summary>
    public const uint S_IROTH = 0x0004;

    /// <summary>Others write permission (0o000002).</summary>
    public const uint S_IWOTH = 0x0002;

    /// <summary>Others execute permission (0o000001).</summary>
    public const uint S_IXOTH = 0x0001;

    /// <summary>Set-user-ID on execution (0o004000).</summary>
    public const uint S_ISUID = 0x0800;

    /// <summary>Set-group-ID on execution (0o002000).</summary>
    public const uint S_ISGID = 0x0400;

    /// <summary>Sticky bit (0o001000).</summary>
    public const uint S_ISVTX = 0x0200;

    /// <summary>Default permissions for directories (0o040755 = drwxr-xr-x).</summary>
    public const uint DefaultDirectoryMode = S_IFDIR | S_IRUSR | S_IWUSR | S_IXUSR
                                           | S_IRGRP | S_IXGRP | S_IROTH | S_IXOTH;

    /// <summary>Default permissions for regular files (0o100644 = -rw-r--r--).</summary>
    public const uint DefaultFileMode = S_IFREG | S_IRUSR | S_IWUSR | S_IRGRP | S_IROTH;

    /// <summary>Default permissions for symbolic links (0o120777 = lrwxrwxrwx).</summary>
    public const uint DefaultSymlinkMode = S_IFLNK | S_IRUSR | S_IWUSR | S_IXUSR
                                         | S_IRGRP | S_IWGRP | S_IXGRP
                                         | S_IROTH | S_IWOTH | S_IXOTH;

    /// <summary>Default permissions for block device files (0o060644 = brw-r--r--).</summary>
    public const uint DefaultBlockDeviceMode = S_IFBLK | S_IRUSR | S_IWUSR | S_IRGRP | S_IROTH;

    /// <summary>Default permissions for character device files (0o020644 = crw-r--r--).</summary>
    public const uint DefaultCharDeviceMode = S_IFCHR | S_IRUSR | S_IWUSR | S_IRGRP | S_IROTH;

    /// <summary>Default permissions for named pipes / FIFOs (0o010644 = prw-r--r--).</summary>
    public const uint DefaultFifoMode = S_IFIFO | S_IRUSR | S_IWUSR | S_IRGRP | S_IROTH;

    /// <summary>Default permissions for sockets (0o140755 = srwxr-xr-x).</summary>
    public const uint DefaultSocketMode = S_IFSOCK | S_IRUSR | S_IWUSR | S_IXUSR
                                         | S_IRGRP | S_IXGRP | S_IROTH | S_IXOTH;

    // ------------------------------------------------------------------
    // SUSP / RRIP string constants
    // ------------------------------------------------------------------

    private static ReadOnlySpan<byte> RripIdentifier => "RRIP_1991A"u8;

    private static ReadOnlySpan<byte> RripDescriptor =>
        "THE ROCK RIDGE INTERCHANGE PROTOCOL PROVIDES SUPPORT FOR POSIX FILE SYSTEM SEMANTICS"u8;

    private static ReadOnlySpan<byte> RripSource =>
        "PLEASE CONTACT DISC PUBLISHER FOR SPECIFICATION SOURCE. SEE PUBLISHER IDENTIFIER IN PRIMARY VOLUME DESCRIPTOR FOR CONTACT INFORMATION."u8;

    // ------------------------------------------------------------------
    // Low-level encoding helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Writes a 32-bit unsigned integer in ISO 9660 both-endian format (ECMA-119 §7.3.3).
    /// Produces 8 bytes: little-endian uint32 followed by big-endian uint32.
    /// </summary>
    /// <param name="buffer">Destination buffer.</param>
    /// <param name="offset">Byte position to start writing.</param>
    /// <param name="value">The 32-bit value to encode.</param>
    public static void WriteBothEndian32(byte[] buffer, int offset, uint value)
    {
        // Little-endian (ECMA-119 §7.3.1)
        buffer[offset]     = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
        // Big-endian (ECMA-119 §7.3.2)
        buffer[offset + 4] = (byte)(value >> 24);
        buffer[offset + 5] = (byte)(value >> 16);
        buffer[offset + 6] = (byte)(value >> 8);
        buffer[offset + 7] = (byte)value;
    }

    /// <summary>
    /// Writes an ISO 9660 short-form (7-byte) timestamp (ECMA-119 §9.1.5).
    /// Byte layout: years-since-1900, month, day, hour, minute, second, GMT-offset.
    /// </summary>
    /// <param name="buffer">Destination buffer.</param>
    /// <param name="offset">Byte position to start writing.</param>
    /// <param name="dt">The date/time value to encode (converted to UTC internally).</param>
    public static void WriteIso9660ShortTimestamp(byte[] buffer, int offset, DateTime dt)
    {
        dt = dt.ToUniversalTime();
        buffer[offset]     = (byte)(dt.Year - 1900);
        buffer[offset + 1] = (byte)dt.Month;
        buffer[offset + 2] = (byte)dt.Day;
        buffer[offset + 3] = (byte)dt.Hour;
        buffer[offset + 4] = (byte)dt.Minute;
        buffer[offset + 5] = (byte)dt.Second;
        buffer[offset + 6] = 0; // GMT offset in 15-minute intervals (0 = UTC)
    }

    /// <summary>
    /// Writes an ISO 9660 long-form (17-byte) timestamp (ECMA-119 §8.4.26.1).
    /// Format: 16 ASCII digit bytes ("YYYYMMDDHHmmsscc") + 1 GMT-offset byte.
    /// </summary>
    /// <param name="buffer">Destination buffer.</param>
    /// <param name="offset">Byte position to start writing.</param>
    /// <param name="dt">The date/time value to encode (converted to UTC internally).</param>
    public static void WriteIso9660LongTimestamp(byte[] buffer, int offset, DateTime dt)
    {
        dt = dt.ToUniversalTime();
        Encoding.ASCII.GetBytes(dt.ToString("yyyyMMddHHmmss")).CopyTo(buffer, offset);
        int hundredths = dt.Millisecond / 10;
        buffer[offset + 14] = (byte)('0' + hundredths / 10);
        buffer[offset + 15] = (byte)('0' + hundredths % 10);
        buffer[offset + 16] = 0; // GMT offset (0 = UTC)
    }

    // ------------------------------------------------------------------
    // SUSP entries (IEEE 1281)
    // ------------------------------------------------------------------

    /// <summary>
    /// Writes the SUSP Sharing Protocol Indicator (SP) entry.
    /// Must appear only in the root directory record's System Use Area.
    /// </summary>
    /// <param name="buffer">Destination buffer.</param>
    /// <param name="offset">Byte position to start writing.</param>
    /// <returns>Number of bytes written (always 7).</returns>
    public static int WriteSuspIndicator(byte[] buffer, int offset)
    {
        buffer[offset]     = (byte)'S';
        buffer[offset + 1] = (byte)'P';
        buffer[offset + 2] = 7;    // Length
        buffer[offset + 3] = 1;    // Version
        buffer[offset + 4] = 0xBE; // Check byte 1
        buffer[offset + 5] = 0xEF; // Check byte 2
        buffer[offset + 6] = 0;    // LEN_SKP (bytes to skip)
        return 7;
    }

    /// <summary>
    /// Writes the SUSP Extensions Reference (ER) entry identifying Rock Ridge (RRIP_1991A).
    /// Contains the extension identifier, human-readable descriptor, and source reference.
    /// </summary>
    /// <param name="buffer">Destination buffer.</param>
    /// <param name="offset">Byte position to start writing.</param>
    /// <returns>Number of bytes written.</returns>
    public static int WriteExtensionsReference(byte[] buffer, int offset)
    {
        ReadOnlySpan<byte> id = RripIdentifier;
        ReadOnlySpan<byte> desc = RripDescriptor;
        ReadOnlySpan<byte> src = RripSource;
        int totalLength = 8 + id.Length + desc.Length + src.Length;

        buffer[offset]     = (byte)'E';
        buffer[offset + 1] = (byte)'R';
        buffer[offset + 2] = (byte)totalLength; // Length
        buffer[offset + 3] = 1;                 // Version
        buffer[offset + 4] = (byte)id.Length;    // LEN_ID
        buffer[offset + 5] = (byte)desc.Length;  // LEN_DES
        buffer[offset + 6] = (byte)src.Length;   // LEN_SRC
        buffer[offset + 7] = 1;                  // EXT_VER

        int pos = offset + 8;
        id.CopyTo(buffer.AsSpan(pos));
        pos += id.Length;
        desc.CopyTo(buffer.AsSpan(pos));
        pos += desc.Length;
        src.CopyTo(buffer.AsSpan(pos));

        return totalLength;
    }

    /// <summary>
    /// Writes the SUSP System Use Terminator (ST) entry.
    /// Indicates the end of SUSP/RRIP entries in the System Use Area.
    /// </summary>
    /// <param name="buffer">Destination buffer.</param>
    /// <param name="offset">Byte position to start writing.</param>
    /// <returns>Number of bytes written (always 4).</returns>
    public static int WriteSystemUseTerminator(byte[] buffer, int offset)
    {
        buffer[offset]     = (byte)'S';
        buffer[offset + 1] = (byte)'T';
        buffer[offset + 2] = 4; // Length
        buffer[offset + 3] = 1; // Version
        return 4;
    }

    /// <summary>
    /// Writes the SUSP Padding Field (PD) entry (SUSP §5.4).
    /// Provides padding bytes in the System Use Area; the padding area is filled with zeros.
    /// </summary>
    /// <param name="buffer">Destination buffer.</param>
    /// <param name="offset">Byte position to start writing.</param>
    /// <param name="totalLength">Total length of the PD entry including header (must be ≥ 4).</param>
    /// <returns>Number of bytes written (equal to <paramref name="totalLength"/>).</returns>
    public static int WritePadding(byte[] buffer, int offset, int totalLength)
    {
        if (totalLength < 4)
            throw new ArgumentOutOfRangeException(nameof(totalLength), "PD entry length must be at least 4.");

        buffer[offset]     = (byte)'P';
        buffer[offset + 1] = (byte)'D';
        buffer[offset + 2] = (byte)totalLength;
        buffer[offset + 3] = 1; // Version

        // Fill padding area with zeros
        for (int i = 4; i < totalLength; i++)
            buffer[offset + i] = 0;

        return totalLength;
    }

    /// <summary>
    /// Writes the SUSP Extension Selector (ES) entry (SUSP §5.10).
    /// Selects which registered extension (identified by its sequence number) is in effect.
    /// </summary>
    /// <param name="buffer">Destination buffer.</param>
    /// <param name="offset">Byte position to start writing.</param>
    /// <param name="extensionSequence">Extension sequence number to activate.</param>
    /// <returns>Number of bytes written (always 5).</returns>
    public static int WriteExtensionSelector(byte[] buffer, int offset, byte extensionSequence)
    {
        buffer[offset]     = (byte)'E';
        buffer[offset + 1] = (byte)'S';
        buffer[offset + 2] = 5; // Length
        buffer[offset + 3] = 1; // Version
        buffer[offset + 4] = extensionSequence;
        return 5;
    }

    /// <summary>
    /// Writes the SUSP Continuation Entry (CE) (SUSP §5.1).
    /// Points to a Continuation Area for overflow System Use data that does not fit
    /// within the directory record's System Use Area.
    /// </summary>
    /// <param name="buffer">Destination buffer.</param>
    /// <param name="offset">Byte position to start writing.</param>
    /// <param name="blockLocation">Logical block number of the continuation area.</param>
    /// <param name="blockOffset">Byte offset within the logical block.</param>
    /// <param name="continuationLength">Length in bytes of the continuation data.</param>
    /// <returns>Number of bytes written (always 28).</returns>
    public static int WriteContinuationEntry(byte[] buffer, int offset,
        uint blockLocation, uint blockOffset, uint continuationLength)
    {
        buffer[offset]     = (byte)'C';
        buffer[offset + 1] = (byte)'E';
        buffer[offset + 2] = 28; // Length
        buffer[offset + 3] = 1;  // Version

        WriteBothEndian32(buffer, offset + 4, blockLocation);     // BP5-12:  Block Location
        WriteBothEndian32(buffer, offset + 12, blockOffset);      // BP13-20: Offset
        WriteBothEndian32(buffer, offset + 20, continuationLength); // BP21-28: Length

        return 28;
    }

    // ------------------------------------------------------------------
    // RRIP entries (IEEE P1282)
    // ------------------------------------------------------------------

    /// <summary>
    /// Writes the RRIP POSIX File Attributes (PX) entry (RRIP 1.12, 44 bytes).
    /// Encodes file mode, link count, owner/group IDs, and serial number.
    /// </summary>
    /// <param name="buffer">Destination buffer.</param>
    /// <param name="offset">Byte position to start writing.</param>
    /// <param name="isDirectory">If <c>true</c>, uses directory permissions (0o040755);
    /// otherwise uses regular file permissions (0o100644).</param>
    /// <param name="uid">POSIX user ID (st_uid).</param>
    /// <param name="gid">POSIX group ID (st_gid).</param>
    /// <param name="inode">POSIX serial number (st_ino).</param>
    /// <param name="nlinks">POSIX link count (st_nlink).</param>
    /// <returns>Number of bytes written (always 44).</returns>
    public static int WritePosixAttributes(byte[] buffer, int offset,
        bool isDirectory, uint uid, uint gid, uint inode, uint nlinks)
    {
        uint mode = isDirectory ? DefaultDirectoryMode : DefaultFileMode;
        return WritePosixAttributesCore(buffer, offset, mode, uid, gid, inode, nlinks);
    }

    /// <summary>
    /// Writes the RRIP POSIX File Attributes (PX) entry with an explicit POSIX mode value.
    /// Allows callers to specify arbitrary mode bits (file type + permissions).
    /// </summary>
    /// <param name="buffer">Destination buffer.</param>
    /// <param name="offset">Byte position to start writing.</param>
    /// <param name="mode">Raw POSIX st_mode value (file type + permission bits).</param>
    /// <param name="uid">POSIX user ID (st_uid).</param>
    /// <param name="gid">POSIX group ID (st_gid).</param>
    /// <param name="inode">POSIX serial number (st_ino).</param>
    /// <param name="nlinks">POSIX link count (st_nlink).</param>
    /// <returns>Number of bytes written (always 44).</returns>
    public static int WritePosixAttributes(byte[] buffer, int offset,
        uint mode, uint uid, uint gid, uint inode, uint nlinks)
    {
        return WritePosixAttributesCore(buffer, offset, mode, uid, gid, inode, nlinks);
    }

    /// <summary>
    /// Writes the RRIP Time Stamps (TF) entry using short-form (7-byte) timestamps.
    /// Records creation, modification, and access times (flags = 0x07).
    /// </summary>
    /// <param name="buffer">Destination buffer.</param>
    /// <param name="offset">Byte position to start writing.</param>
    /// <param name="creation">File creation time.</param>
    /// <param name="modification">File modification time.</param>
    /// <param name="access">File last-access time.</param>
    /// <returns>Number of bytes written (always 26).</returns>
    public static int WriteTimestamps(byte[] buffer, int offset,
        DateTime creation, DateTime modification, DateTime access)
    {
        // Flags 0x07: bits 0 (creation), 1 (modify), 2 (access); bit 7 clear = short form
        const byte flags = 0x07;
        const int length = 5 + 3 * 7; // 5-byte header + three 7-byte timestamps = 26

        buffer[offset]     = (byte)'T';
        buffer[offset + 1] = (byte)'F';
        buffer[offset + 2] = length;
        buffer[offset + 3] = 1;     // Version
        buffer[offset + 4] = flags;

        WriteIso9660ShortTimestamp(buffer, offset + 5, creation);
        WriteIso9660ShortTimestamp(buffer, offset + 12, modification);
        WriteIso9660ShortTimestamp(buffer, offset + 19, access);

        return length;
    }

    /// <summary>
    /// Writes the RRIP Time Stamps (TF) entry using long-form (17-byte) timestamps.
    /// The long-form flag (bit 7) is set automatically. Creation, modification, and access
    /// times are always included; attributes, backup, expiration, and effective times are
    /// included only when provided.
    /// </summary>
    /// <param name="buffer">Destination buffer.</param>
    /// <param name="offset">Byte position to start writing.</param>
    /// <param name="creation">File creation time.</param>
    /// <param name="modification">File modification time.</param>
    /// <param name="access">File last-access time.</param>
    /// <param name="attributes">Optional attributes change time.</param>
    /// <param name="backup">Optional backup time.</param>
    /// <param name="expiration">Optional expiration time.</param>
    /// <param name="effective">Optional effective time.</param>
    /// <returns>Number of bytes written.</returns>
    public static int WriteTimestampsLong(byte[] buffer, int offset,
        DateTime creation, DateTime modification, DateTime access,
        DateTime? attributes = null, DateTime? backup = null,
        DateTime? expiration = null, DateTime? effective = null)
    {
        // Bit 7 = long form; bits 0-6 indicate which timestamps are present
        byte flags = 0x80 | 0x01 | 0x02 | 0x04; // long-form + creation + modify + access
        int count = 3;

        if (attributes.HasValue) { flags |= 0x08; count++; }
        if (backup.HasValue)     { flags |= 0x10; count++; }
        if (expiration.HasValue) { flags |= 0x20; count++; }
        if (effective.HasValue)  { flags |= 0x40; count++; }

        int length = 5 + count * 17; // 5-byte header + N × 17-byte timestamps

        buffer[offset]     = (byte)'T';
        buffer[offset + 1] = (byte)'F';
        buffer[offset + 2] = (byte)length;
        buffer[offset + 3] = 1;     // Version
        buffer[offset + 4] = flags;

        int pos = offset + 5;
        WriteIso9660LongTimestamp(buffer, pos, creation);      pos += 17;
        WriteIso9660LongTimestamp(buffer, pos, modification);  pos += 17;
        WriteIso9660LongTimestamp(buffer, pos, access);        pos += 17;

        if (attributes.HasValue) { WriteIso9660LongTimestamp(buffer, pos, attributes.Value); pos += 17; }
        if (backup.HasValue)     { WriteIso9660LongTimestamp(buffer, pos, backup.Value);     pos += 17; }
        if (expiration.HasValue) { WriteIso9660LongTimestamp(buffer, pos, expiration.Value);  pos += 17; }
        if (effective.HasValue)  { WriteIso9660LongTimestamp(buffer, pos, effective.Value);   pos += 17; }

        return length;
    }

    /// <summary>
    /// Writes the RRIP Alternate Name (NM) entry. Handles name continuation
    /// by emitting multiple NM entries when the name exceeds 250 bytes.
    /// Recognizes "." (current) and ".." (parent) as special directory entries.
    /// </summary>
    /// <param name="buffer">Destination buffer.</param>
    /// <param name="offset">Byte position to start writing.</param>
    /// <param name="name">The POSIX filename to encode.</param>
    /// <returns>Total number of bytes written across all NM entries.</returns>
    public static int WriteAlternateName(byte[] buffer, int offset, string name)
    {
        if (name == ".")
            return WriteNmEntry(buffer, offset, 0x02, ReadOnlySpan<byte>.Empty);
        if (name == "..")
            return WriteNmEntry(buffer, offset, 0x04, ReadOnlySpan<byte>.Empty);

        byte[] nameBytes = Encoding.ASCII.GetBytes(name);
        int totalWritten = 0;
        int remaining = nameBytes.Length;
        int sourceOffset = 0;
        const int maxContentPerEntry = 250; // 255 max entry length − 5 byte header

        while (remaining > 0)
        {
            int chunk = Math.Min(remaining, maxContentPerEntry);
            bool continues = remaining - chunk > 0;
            byte flags = continues ? (byte)0x01 : (byte)0x00;

            int written = WriteNmEntry(buffer, offset + totalWritten, flags,
                nameBytes.AsSpan(sourceOffset, chunk));
            totalWritten += written;
            sourceOffset += chunk;
            remaining -= chunk;
        }

        return totalWritten;
    }

    /// <summary>
    /// Writes the RRIP Symbolic Link (SL) entry. Parses the target path into
    /// component records per RRIP §4.1.3, handling absolute paths, relative
    /// segments (".", ".."), and component continuation for long paths.
    /// </summary>
    /// <param name="buffer">Destination buffer.</param>
    /// <param name="offset">Byte position to start writing.</param>
    /// <param name="target">The symbolic link target path.</param>
    /// <returns>Total number of bytes written across all SL entries.</returns>
    public static int WriteSymbolicLink(byte[] buffer, int offset, string target)
    {
        // Parse target into component records: (flags, content)
        var components = new List<(byte Flags, byte[] Content)>();

        if (target.StartsWith('/'))
        {
            components.Add((0x08, Array.Empty<byte>())); // Root component
            target = target.TrimStart('/');
        }

        if (target.Length > 0)
        {
            string[] segments = target.Split('/', StringSplitOptions.RemoveEmptyEntries);
            foreach (string segment in segments)
            {
                switch (segment)
                {
                    case ".":
                        components.Add((0x02, Array.Empty<byte>()));
                        break;
                    case "..":
                        components.Add((0x04, Array.Empty<byte>()));
                        break;
                    default:
                        components.Add((0x00, Encoding.ASCII.GetBytes(segment)));
                        break;
                }
            }
        }

        int totalWritten = 0;
        int compIndex = 0;
        const int headerSize = 5;
        const int maxPayload = 250; // 255 − headerSize

        while (compIndex < components.Count)
        {
            int entryStart = offset + totalWritten;
            var payload = new byte[maxPayload];
            int payloadPos = 0;

            while (compIndex < components.Count)
            {
                var (flags, content) = components[compIndex];
                int compRecordSize = 2 + content.Length;

                if (payloadPos + compRecordSize > maxPayload)
                {
                    // Component doesn't fit — split its content if nothing else was written
                    if (payloadPos == 0 && content.Length > maxPayload - 2)
                    {
                        int chunk = maxPayload - 2;
                        payload[payloadPos] = (byte)(flags | 0x01); // CONTINUE
                        payload[payloadPos + 1] = (byte)chunk;
                        content.AsSpan(0, chunk).CopyTo(payload.AsSpan(payloadPos + 2));
                        payloadPos += 2 + chunk;
                        components[compIndex] = (flags, content[chunk..]);
                    }
                    break;
                }

                payload[payloadPos] = flags;
                payload[payloadPos + 1] = (byte)content.Length;
                if (content.Length > 0)
                    content.CopyTo(payload, payloadPos + 2);
                payloadPos += compRecordSize;
                compIndex++;
            }

            bool slContinues = compIndex < components.Count;
            int entryLength = headerSize + payloadPos;

            buffer[entryStart]     = (byte)'S';
            buffer[entryStart + 1] = (byte)'L';
            buffer[entryStart + 2] = (byte)entryLength;
            buffer[entryStart + 3] = 1; // Version
            buffer[entryStart + 4] = slContinues ? (byte)0x01 : (byte)0x00;

            Array.Copy(payload, 0, buffer, entryStart + headerSize, payloadPos);
            totalWritten += entryLength;
        }

        return totalWritten;
    }

    /// <summary>
    /// Writes the RRIP Child Link (CL) entry, pointing to a relocated child directory.
    /// Used together with PL and RE entries for deep directory relocation.
    /// </summary>
    /// <param name="buffer">Destination buffer.</param>
    /// <param name="offset">Byte position to start writing.</param>
    /// <param name="childLocation">Logical block address of the child directory.</param>
    /// <returns>Number of bytes written (always 12).</returns>
    public static int WriteChildLink(byte[] buffer, int offset, uint childLocation)
    {
        buffer[offset]     = (byte)'C';
        buffer[offset + 1] = (byte)'L';
        buffer[offset + 2] = 12; // Length
        buffer[offset + 3] = 1;  // Version
        WriteBothEndian32(buffer, offset + 4, childLocation);
        return 12;
    }

    /// <summary>
    /// Writes the RRIP Parent Link (PL) entry, pointing to the original parent
    /// of a relocated directory.
    /// </summary>
    /// <param name="buffer">Destination buffer.</param>
    /// <param name="offset">Byte position to start writing.</param>
    /// <param name="parentLocation">Logical block address of the original parent directory.</param>
    /// <returns>Number of bytes written (always 12).</returns>
    public static int WriteParentLink(byte[] buffer, int offset, uint parentLocation)
    {
        buffer[offset]     = (byte)'P';
        buffer[offset + 1] = (byte)'L';
        buffer[offset + 2] = 12; // Length
        buffer[offset + 3] = 1;  // Version
        WriteBothEndian32(buffer, offset + 4, parentLocation);
        return 12;
    }

    /// <summary>
    /// Writes the RRIP Relocated Directory (RE) entry, marking a directory record
    /// as relocated for deep directory support beyond 8 levels (ECMA-119 §6.8.2.1).
    /// </summary>
    /// <param name="buffer">Destination buffer.</param>
    /// <param name="offset">Byte position to start writing.</param>
    /// <returns>Number of bytes written (always 4).</returns>
    public static int WriteRelocatedDirectory(byte[] buffer, int offset)
    {
        buffer[offset]     = (byte)'R';
        buffer[offset + 1] = (byte)'E';
        buffer[offset + 2] = 4; // Length
        buffer[offset + 3] = 1; // Version
        return 4;
    }

    /// <summary>
    /// Writes the RRIP Rock Ridge Extensions in-use indicator (RR) entry.
    /// The flags byte is a bit field indicating which RRIP entries are recorded:
    /// bit 0 = PX, bit 1 = PN, bit 2 = SL, bit 3 = NM,
    /// bit 4 = CL, bit 5 = PL, bit 6 = RE, bit 7 = TF.
    /// </summary>
    /// <param name="buffer">Destination buffer.</param>
    /// <param name="offset">Byte position to start writing.</param>
    /// <param name="flags">Bit field indicating which RRIP extensions are present.</param>
    /// <returns>Number of bytes written (always 5).</returns>
    public static int WriteRockRidgeIndicator(byte[] buffer, int offset, byte flags)
    {
        buffer[offset]     = (byte)'R';
        buffer[offset + 1] = (byte)'R';
        buffer[offset + 2] = 5;     // Length
        buffer[offset + 3] = 1;     // Version
        buffer[offset + 4] = flags;
        return 5;
    }

    /// <summary>
    /// Writes the RRIP POSIX Device Number (PN) entry (RRIP §4.1.2, 20 bytes).
    /// Encodes the major and minor device numbers for block and character device files.
    /// </summary>
    /// <param name="buffer">Destination buffer.</param>
    /// <param name="offset">Byte position to start writing.</param>
    /// <param name="major">Device major number (dev_t high).</param>
    /// <param name="minor">Device minor number (dev_t low).</param>
    /// <returns>Number of bytes written (always 20).</returns>
    public static int WritePosixDeviceNumber(byte[] buffer, int offset, uint major, uint minor)
    {
        buffer[offset]     = (byte)'P';
        buffer[offset + 1] = (byte)'N';
        buffer[offset + 2] = 20; // Length
        buffer[offset + 3] = 1;  // Version

        WriteBothEndian32(buffer, offset + 4, major);  // BP5-12:  Dev_t High (major)
        WriteBothEndian32(buffer, offset + 12, minor); // BP13-20: Dev_t Low (minor)

        return 20;
    }

    /// <summary>
    /// Writes the RRIP Sparse File (SF) entry (RRIP §4.1.7, 24 bytes).
    /// Records the virtual file size for sparse files, allowing the actual file data
    /// to be smaller than the logical file size reported to the operating system.
    /// </summary>
    /// <param name="buffer">Destination buffer.</param>
    /// <param name="offset">Byte position to start writing.</param>
    /// <param name="virtualSizeHigh">High 32 bits of the virtual file size.</param>
    /// <param name="virtualSizeLow">Low 32 bits of the virtual file size.</param>
    /// <param name="tableDepth">Sparse table depth (number of indirection levels).</param>
    /// <returns>Number of bytes written (always 24).</returns>
    public static int WriteSparseFile(byte[] buffer, int offset,
        uint virtualSizeHigh, uint virtualSizeLow, byte tableDepth)
    {
        buffer[offset]     = (byte)'S';
        buffer[offset + 1] = (byte)'F';
        buffer[offset + 2] = 24; // Length
        buffer[offset + 3] = 1;  // Version

        WriteBothEndian32(buffer, offset + 4, virtualSizeHigh);   // BP5-12:  Virtual Size High
        WriteBothEndian32(buffer, offset + 12, virtualSizeLow);   // BP13-20: Virtual Size Low
        buffer[offset + 20] = tableDepth;                          // BP21:    Table Depth
        buffer[offset + 21] = 0;                                   // Reserved
        buffer[offset + 22] = 0;                                   // Reserved
        buffer[offset + 23] = 0;                                   // Reserved

        return 24;
    }

    // ------------------------------------------------------------------
    // Composite builders
    // ------------------------------------------------------------------

    /// <summary>
    /// Builds a complete Rock Ridge System Use Area for a directory record.
    /// Includes RR, PX, TF, and NM entries. Adds an SL entry when
    /// <paramref name="symlinkTarget"/> is not <c>null</c>.
    /// </summary>
    /// <param name="isDirectory">Whether the entry is a directory.</param>
    /// <param name="name">The POSIX filename.</param>
    /// <param name="uid">POSIX user ID.</param>
    /// <param name="gid">POSIX group ID.</param>
    /// <param name="inode">POSIX inode / serial number.</param>
    /// <param name="nlinks">POSIX hard link count.</param>
    /// <param name="creation">File creation time.</param>
    /// <param name="modification">File modification time.</param>
    /// <param name="access">File last-access time.</param>
    /// <param name="symlinkTarget">Symbolic link target, or <c>null</c> for non-symlinks.</param>
    /// <returns>Byte array containing all serialised RRIP entries.</returns>
    public static byte[] BuildRockRidgeEntries(bool isDirectory, string name,
        uint uid, uint gid, uint inode, uint nlinks,
        DateTime creation, DateTime modification, DateTime access,
        string? symlinkTarget)
    {
        bool isSymlink = symlinkTarget is not null;

        // RR flags: PX (0x01) | NM (0x08) | TF (0x80), plus SL (0x04) for symlinks
        byte rrFlags = 0x89;
        if (isSymlink)
            rrFlags |= 0x04;

        uint mode;
        if (isSymlink)
            mode = DefaultSymlinkMode;
        else if (isDirectory)
            mode = DefaultDirectoryMode;
        else
            mode = DefaultFileMode;

        // Generous working buffer — final array is trimmed to exact size
        var buffer = new byte[4096];
        int pos = 0;

        pos += WriteRockRidgeIndicator(buffer, pos, rrFlags);
        pos += WritePosixAttributesCore(buffer, pos, mode, uid, gid, inode, nlinks);
        pos += WriteTimestamps(buffer, pos, creation, modification, access);
        pos += WriteAlternateName(buffer, pos, name);

        if (isSymlink)
            pos += WriteSymbolicLink(buffer, pos, symlinkTarget!);

        var result = new byte[pos];
        Array.Copy(buffer, result, pos);
        return result;
    }

    /// <summary>
    /// Builds a complete Rock Ridge System Use Area for a directory record, with optional
    /// device number support. Includes RR, PX, TF, NM, and optionally PN and SL entries.
    /// When <paramref name="deviceMajor"/> and <paramref name="deviceMinor"/> are provided,
    /// a PN entry is emitted and the mode is set to block device mode.
    /// </summary>
    /// <param name="isDirectory">Whether the entry is a directory.</param>
    /// <param name="name">The POSIX filename.</param>
    /// <param name="uid">POSIX user ID.</param>
    /// <param name="gid">POSIX group ID.</param>
    /// <param name="inode">POSIX inode / serial number.</param>
    /// <param name="nlinks">POSIX hard link count.</param>
    /// <param name="creation">File creation time.</param>
    /// <param name="modification">File modification time.</param>
    /// <param name="access">File last-access time.</param>
    /// <param name="symlinkTarget">Symbolic link target, or <c>null</c> for non-symlinks.</param>
    /// <param name="deviceMajor">Optional major device number for device files.</param>
    /// <param name="deviceMinor">Optional minor device number for device files.</param>
    /// <returns>Byte array containing all serialised RRIP entries.</returns>
    public static byte[] BuildRockRidgeEntries(bool isDirectory, string name,
        uint uid, uint gid, uint inode, uint nlinks,
        DateTime creation, DateTime modification, DateTime access,
        string? symlinkTarget, uint? deviceMajor = null, uint? deviceMinor = null,
        uint? customMode = null)
    {
        bool isSymlink = symlinkTarget is not null;
        bool isDevice = deviceMajor.HasValue && deviceMinor.HasValue;

        // RR flags: PX (0x01) | NM (0x08) | TF (0x80), plus SL (0x04) and PN (0x02)
        byte rrFlags = 0x89;
        if (isSymlink)
            rrFlags |= 0x04;
        if (isDevice)
            rrFlags |= 0x02;

        uint mode;
        if (customMode.HasValue)
            mode = customMode.Value;
        else if (isDevice)
            mode = DefaultBlockDeviceMode;
        else if (isSymlink)
            mode = DefaultSymlinkMode;
        else if (isDirectory)
            mode = DefaultDirectoryMode;
        else
            mode = DefaultFileMode;

        var buffer = new byte[4096];
        int pos = 0;

        pos += WriteRockRidgeIndicator(buffer, pos, rrFlags);
        pos += WritePosixAttributesCore(buffer, pos, mode, uid, gid, inode, nlinks);

        if (isDevice)
            pos += WritePosixDeviceNumber(buffer, pos, deviceMajor!.Value, deviceMinor!.Value);

        pos += WriteTimestamps(buffer, pos, creation, modification, access);
        pos += WriteAlternateName(buffer, pos, name);

        if (isSymlink)
            pos += WriteSymbolicLink(buffer, pos, symlinkTarget!);

        var result = new byte[pos];
        Array.Copy(buffer, result, pos);
        return result;
    }

    /// <summary>
    /// Builds the complete System Use Area for the root directory record.
    /// Includes SP (sharing protocol indicator), ER (extensions reference),
    /// RR (Rock Ridge indicator), PX (POSIX attributes), TF (timestamps),
    /// and NM (alternate name for the current directory).
    /// </summary>
    /// <param name="inode">POSIX serial number (st_ino) for the root directory.</param>
    /// <returns>Byte array containing all serialised SUSP/RRIP entries.</returns>
    public static byte[] BuildRootDirectoryEntries(uint inode)
    {
        // SP + ER + RR + PX + TF + NM(.)
        var buffer = new byte[4096];
        int pos = 0;

        var now = DateTime.UtcNow;

        pos += WriteSuspIndicator(buffer, pos);
        pos += WriteExtensionsReference(buffer, pos);

        const byte rrFlags = 0x89; // PX | NM | TF
        pos += WriteRockRidgeIndicator(buffer, pos, rrFlags);
        pos += WritePosixAttributesCore(buffer, pos, DefaultDirectoryMode, 0, 0, inode, 2);
        pos += WriteTimestamps(buffer, pos, now, now, now);
        pos += WriteAlternateName(buffer, pos, ".");

        var result = new byte[pos];
        Array.Copy(buffer, result, pos);
        return result;
    }

    /// <summary>
    /// Builds the complete System Use Area for the root directory record with
    /// configurable ownership and timestamps.
    /// Includes SP, ER, RR, PX, TF, and NM entries.
    /// </summary>
    /// <param name="inode">POSIX serial number (st_ino) for the root directory.</param>
    /// <param name="uid">POSIX user ID (defaults to 0).</param>
    /// <param name="gid">POSIX group ID (defaults to 0).</param>
    /// <param name="creation">File creation time (defaults to <see cref="DateTime.UtcNow"/>).</param>
    /// <param name="modification">File modification time (defaults to <see cref="DateTime.UtcNow"/>).</param>
    /// <param name="access">File last-access time (defaults to <see cref="DateTime.UtcNow"/>).</param>
    /// <returns>Byte array containing all serialised SUSP/RRIP entries.</returns>
    public static byte[] BuildRootDirectoryEntries(uint inode, uint uid = 0, uint gid = 0,
        DateTime? creation = null, DateTime? modification = null, DateTime? access = null)
    {
        var now = DateTime.UtcNow;
        DateTime c = creation ?? now;
        DateTime m = modification ?? now;
        DateTime a = access ?? now;

        // SP + ER + RR + PX + TF + NM(.)
        var buffer = new byte[4096];
        int pos = 0;

        pos += WriteSuspIndicator(buffer, pos);
        pos += WriteExtensionsReference(buffer, pos);

        const byte rrFlags = 0x89; // PX | NM | TF
        pos += WriteRockRidgeIndicator(buffer, pos, rrFlags);
        pos += WritePosixAttributesCore(buffer, pos, DefaultDirectoryMode, uid, gid, inode, 2);
        pos += WriteTimestamps(buffer, pos, c, m, a);
        pos += WriteAlternateName(buffer, pos, ".");

        var result = new byte[pos];
        Array.Copy(buffer, result, pos);
        return result;
    }

    // ------------------------------------------------------------------
    // Private helpers
    // ------------------------------------------------------------------

    /// <summary>Core PX writer that accepts an explicit POSIX mode value.</summary>
    private static int WritePosixAttributesCore(byte[] buffer, int offset, uint mode,
        uint uid, uint gid, uint inode, uint nlinks)
    {
        buffer[offset]     = (byte)'P';
        buffer[offset + 1] = (byte)'X';
        buffer[offset + 2] = 44; // Length (RRIP 1.12)
        buffer[offset + 3] = 1;  // Version

        WriteBothEndian32(buffer, offset + 4, mode);    // BP5-12:  File Mode
        WriteBothEndian32(buffer, offset + 12, nlinks); // BP13-20: Links
        WriteBothEndian32(buffer, offset + 20, uid);    // BP21-28: User ID
        WriteBothEndian32(buffer, offset + 28, gid);    // BP29-36: Group ID
        WriteBothEndian32(buffer, offset + 36, inode);  // BP37-44: Serial Number

        return 44;
    }

    /// <summary>Writes a single NM entry with the given flags and content bytes.</summary>
    private static int WriteNmEntry(byte[] buffer, int offset,
        byte flags, ReadOnlySpan<byte> content)
    {
        int length = 5 + content.Length;

        buffer[offset]     = (byte)'N';
        buffer[offset + 1] = (byte)'M';
        buffer[offset + 2] = (byte)length;
        buffer[offset + 3] = 1;     // Version
        buffer[offset + 4] = flags;

        if (content.Length > 0)
            content.CopyTo(buffer.AsSpan(offset + 5));

        return length;
    }
}
