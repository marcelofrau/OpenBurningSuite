// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OpenBurningSuite.Native.Iso;

public static class RockRidgeReader
{
    // ------------------------------------------------------------------
    // Public API
    // ------------------------------------------------------------------

    /// <summary>
    /// Parses all SUSP/RRIP entries from the System Use Area of an ISO 9660 directory record.
    /// </summary>
    /// <param name="systemUseArea">The raw bytes of the System Use Area.</param>
    /// <returns>A <see cref="RockRidgeEntry"/> containing all parsed metadata.</returns>
    public static RockRidgeEntry Parse(ReadOnlySpan<byte> systemUseArea)
    {
        var entry = new RockRidgeEntry();
        ParseEntries(systemUseArea, entry, isoStream: null);
        return entry;
    }

    /// <summary>
    /// Parses all SUSP/RRIP entries from the System Use Area of an ISO 9660 directory record,
    /// following any Continuation Entries (CE) using the provided ISO stream.
    /// </summary>
    /// <param name="systemUseArea">The raw bytes of the System Use Area.</param>
    /// <param name="isoStream">A readable, seekable stream of the ISO image (for CE lookups).</param>
    /// <returns>A <see cref="RockRidgeEntry"/> containing all parsed metadata.</returns>
    public static RockRidgeEntry Parse(ReadOnlySpan<byte> systemUseArea, Stream isoStream)
    {
        var entry = new RockRidgeEntry();
        ParseEntries(systemUseArea, entry, isoStream);
        return entry;
    }

    /// <summary>
    /// Parses SUSP/RRIP entries from a raw ISO 9660 directory record byte array.
    /// Extracts the System Use Area from the record and parses it.
    /// </summary>
    /// <param name="directoryRecord">The complete ISO 9660 directory record bytes.</param>
    /// <param name="isoStream">Optional ISO stream for following CE entries.</param>
    /// <returns>A <see cref="RockRidgeEntry"/> containing all parsed metadata.</returns>
    public static RockRidgeEntry ParseFromDirectoryRecord(byte[] directoryRecord, Stream? isoStream = null)
    {
        if (directoryRecord.Length < 34)
            return new RockRidgeEntry();

        byte recordLength = directoryRecord[0];
        byte nameLength = directoryRecord[32];

        // Validate that the recorded length doesn't exceed the actual buffer
        if (recordLength > directoryRecord.Length)
            recordLength = (byte)directoryRecord.Length;

        // System Use Area starts after the file identifier + padding byte
        // ECMA-119 §9.1.13: padding field present if LEN_FI is even
        int padding = (nameLength % 2 == 0) ? 1 : 0;
        int suaStart = 33 + nameLength + padding;

        if (suaStart >= recordLength)
            return new RockRidgeEntry();

        int suaLength = recordLength - suaStart;
        if (suaLength <= 0)
            return new RockRidgeEntry();

        var entry = new RockRidgeEntry();
        ParseEntries(directoryRecord.AsSpan(suaStart, suaLength), entry, isoStream);
        return entry;
    }

    /// <summary>
    /// Extracts the System Use Area bytes from a raw ISO 9660 directory record.
    /// </summary>
    /// <param name="directoryRecord">The complete ISO 9660 directory record bytes.</param>
    /// <returns>The System Use Area bytes, or an empty array if none.</returns>
    public static byte[] ExtractSystemUseArea(byte[] directoryRecord)
    {
        if (directoryRecord.Length < 34)
            return Array.Empty<byte>();

        byte recordLength = directoryRecord[0];
        byte nameLength = directoryRecord[32];

        // Validate that the recorded length doesn't exceed the actual buffer
        if (recordLength > directoryRecord.Length)
            recordLength = (byte)directoryRecord.Length;

        int padding = (nameLength % 2 == 0) ? 1 : 0;
        int suaStart = 33 + nameLength + padding;

        if (suaStart >= recordLength)
            return Array.Empty<byte>();

        int suaLength = recordLength - suaStart;
        if (suaLength <= 0)
            return Array.Empty<byte>();

        var result = new byte[suaLength];
        Array.Copy(directoryRecord, suaStart, result, 0, suaLength);
        return result;
    }

    /// <summary>
    /// Checks whether an ISO 9660 image uses Rock Ridge extensions by examining
    /// the root directory record's System Use Area for SP and ER entries.
    /// </summary>
    /// <param name="isoStream">A readable, seekable stream of the ISO image.</param>
    /// <returns><c>true</c> if Rock Ridge extensions are detected; otherwise <c>false</c>.</returns>
    public static bool DetectRockRidge(Stream isoStream)
    {
        if (isoStream == null || !isoStream.CanRead || !isoStream.CanSeek)
            return false;

        try
        {
            // Read PVD at sector 16
            const long pvdOffset = 16 * 2048;
            if (isoStream.Length < pvdOffset + 2048)
                return false;

            isoStream.Seek(pvdOffset, SeekOrigin.Begin);
            var pvd = new byte[2048];
            if (ReadFull(isoStream, pvd, 0, 2048) < 2048)
                return false;

            // Verify PVD signature
            if (pvd[0] != 0x01 || pvd[1] != (byte)'C' || pvd[2] != (byte)'D' ||
                pvd[3] != (byte)'0' || pvd[4] != (byte)'0' || pvd[5] != (byte)'1')
                return false;

            // Parse root directory record (starts at PVD offset 156)
            byte rootRecordLen = pvd[156];
            if (rootRecordLen < 34)
                return false;

            var rootRecord = new byte[rootRecordLen];
            Array.Copy(pvd, 156, rootRecord, 0, rootRecordLen);

            var entry = ParseFromDirectoryRecord(rootRecord, isoStream);
            return entry.SuspActive && entry.RockRidgeIdentified;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reads all directory entries with Rock Ridge metadata from the root directory
    /// of an ISO 9660 image.
    /// </summary>
    /// <param name="isoStream">A readable, seekable stream of the ISO image.</param>
    /// <returns>
    /// A list of tuples containing the directory record name (ISO 9660),
    /// the parsed Rock Ridge entry, and the raw directory record bytes.
    /// Returns an empty list if parsing fails.
    /// </returns>
    public static List<(string Iso9660Name, RockRidgeEntry RockRidge, byte[] RawRecord)>
        ReadRootDirectory(Stream isoStream)
    {
        var results = new List<(string, RockRidgeEntry, byte[])>();
        if (isoStream == null || !isoStream.CanRead || !isoStream.CanSeek)
            return results;

        try
        {
            // Read PVD at sector 16
            const long pvdOffset = 16 * 2048;
            if (isoStream.Length < pvdOffset + 2048)
                return results;

            isoStream.Seek(pvdOffset, SeekOrigin.Begin);
            var pvd = new byte[2048];
            if (ReadFull(isoStream, pvd, 0, 2048) < 2048)
                return results;

            // Verify PVD signature
            if (pvd[0] != 0x01 || pvd[1] != (byte)'C' || pvd[2] != (byte)'D' ||
                pvd[3] != (byte)'0' || pvd[4] != (byte)'0' || pvd[5] != (byte)'1')
                return results;

            // Root directory extent location and size from PVD
            uint rootLba = BitConverter.ToUInt32(pvd, 156 + 2);
            uint rootDataLen = BitConverter.ToUInt32(pvd, 156 + 10);

            if (rootLba == 0 || rootDataLen == 0)
                return results;

            return ReadDirectoryExtent(isoStream, rootLba, rootDataLen);
        }
        catch
        {
            return results;
        }
    }

    /// <summary>
    /// Reads all directory entries with Rock Ridge metadata from a directory extent.
    /// </summary>
    /// <param name="isoStream">A readable, seekable stream of the ISO image.</param>
    /// <param name="extentLba">Logical block address of the directory extent.</param>
    /// <param name="dataLength">Length of the directory extent in bytes.</param>
    /// <returns>A list of parsed directory entries.</returns>
    public static List<(string Iso9660Name, RockRidgeEntry RockRidge, byte[] RawRecord)>
        ReadDirectoryExtent(Stream isoStream, uint extentLba, uint dataLength)
    {
        var results = new List<(string, RockRidgeEntry, byte[])>();
        if (isoStream == null || !isoStream.CanRead || !isoStream.CanSeek)
            return results;

        try
        {
            long offset = (long)extentLba * 2048;
            if (offset + dataLength > isoStream.Length)
                return results;

            var dirData = new byte[dataLength];
            isoStream.Seek(offset, SeekOrigin.Begin);
            if (ReadFull(isoStream, dirData, 0, (int)dataLength) < (int)dataLength)
                return results;

            int pos = 0;
            while (pos < (int)dataLength)
            {
                if (dirData[pos] == 0)
                {
                    // Skip to next sector boundary
                    int sectorOff = pos % 2048;
                    int skip = (sectorOff == 0) ? 2048 : (2048 - sectorOff);
                    pos += skip;
                    continue;
                }

                byte recLen = dirData[pos];
                if (recLen < 33 || pos + recLen > (int)dataLength)
                    break;

                var record = new byte[recLen];
                Array.Copy(dirData, pos, record, 0, recLen);

                // Extract ISO 9660 name
                byte nameLen = record[32];
                string name;
                if (nameLen == 1 && record[33] == 0x00)
                    name = ".";
                else if (nameLen == 1 && record[33] == 0x01)
                    name = "..";
                else
                    name = Encoding.ASCII.GetString(record, 33, nameLen);

                var rrEntry = ParseFromDirectoryRecord(record, isoStream);
                results.Add((name, rrEntry, record));

                pos += recLen;
            }
        }
        catch
        {
            // Return whatever was parsed so far
        }

        return results;
    }

    /// <summary>
    /// Recursively reads all directory entries with Rock Ridge metadata
    /// from an ISO 9660 image, starting from the root directory.
    /// </summary>
    /// <param name="isoStream">A readable, seekable stream of the ISO image.</param>
    /// <param name="maxDepth">Maximum directory depth to traverse (default 256).</param>
    /// <returns>
    /// A flat list of all entries with their full path, Rock Ridge metadata,
    /// and whether they are directories.
    /// </returns>
    public static List<RockRidgeFileEntry> ReadAllEntries(Stream isoStream, int maxDepth = 256)
    {
        var allEntries = new List<RockRidgeFileEntry>();
        if (isoStream == null || !isoStream.CanRead || !isoStream.CanSeek)
            return allEntries;

        try
        {
            const long pvdOffset = 16 * 2048;
            if (isoStream.Length < pvdOffset + 2048)
                return allEntries;

            isoStream.Seek(pvdOffset, SeekOrigin.Begin);
            var pvd = new byte[2048];
            if (ReadFull(isoStream, pvd, 0, 2048) < 2048)
                return allEntries;

            if (pvd[0] != 0x01 || pvd[1] != (byte)'C' || pvd[2] != (byte)'D' ||
                pvd[3] != (byte)'0' || pvd[4] != (byte)'0' || pvd[5] != (byte)'1')
                return allEntries;

            uint rootLba = BitConverter.ToUInt32(pvd, 156 + 2);
            uint rootDataLen = BitConverter.ToUInt32(pvd, 156 + 10);

            if (rootLba == 0 || rootDataLen == 0)
                return allEntries;

            var visited = new HashSet<uint> { rootLba };
            ReadDirectoryRecursive(isoStream, rootLba, rootDataLen, "/", allEntries,
                visited, 0, maxDepth);
        }
        catch
        {
            // Return whatever was collected
        }

        return allEntries;
    }

    // ------------------------------------------------------------------
    // Private parsing
    // ------------------------------------------------------------------

    /// <summary>
    /// Core entry parser: walks SUSP/RRIP entries in the system use area,
    /// following CE entries as needed.
    /// </summary>
    private static void ParseEntries(ReadOnlySpan<byte> data, RockRidgeEntry entry, Stream? isoStream)
    {
        int pos = entry.SkipLength;
        int ceDepth = 0;
        const int maxCeDepth = 16; // Prevent infinite CE chains

        while (pos + 4 <= data.Length)
        {
            byte sig0 = data[pos];
            byte sig1 = data[pos + 1];
            byte len = data[pos + 2];
            byte ver = data[pos + 3];

            // Validate entry length
            if (len < 4 || pos + len > data.Length)
                break;

            // Check for padding byte (zero record length signals end of entries)
            if (sig0 == 0 && sig1 == 0)
                break;

            ushort sig = (ushort)((sig0 << 8) | sig1);
            ReadOnlySpan<byte> entryData = data.Slice(pos, len);

            switch (sig)
            {
                case 0x5350: // 'SP' — SUSP Sharing Protocol Indicator
                    ParseSp(entryData, entry);
                    break;

                case 0x4552: // 'ER' — Extensions Reference
                    ParseEr(entryData, entry);
                    break;

                case 0x4345: // 'CE' — Continuation Entry
                    if (isoStream != null && ceDepth < maxCeDepth)
                    {
                        var ceData = FollowContinuation(entryData, isoStream);
                        if (ceData != null)
                        {
                            ceDepth++;
                            ParseEntries(ceData, entry, isoStream);
                            ceDepth--;
                        }
                    }
                    break;

                case 0x5354: // 'ST' — System Use Terminator
                    return; // Stop parsing

                case 0x5044: // 'PD' — Padding
                    // Ignore padding entries
                    break;

                case 0x4553: // 'ES' — Extension Selector
                    // Acknowledged but no action needed for RRIP
                    break;

                case 0x5258: // 'RR' — Rock Ridge Indicator
                    ParseRr(entryData, entry);
                    break;

                case 0x5058: // 'PX' — POSIX File Attributes
                    ParsePx(entryData, entry);
                    break;

                case 0x504E: // 'PN' — POSIX Device Number
                    ParsePn(entryData, entry);
                    break;

                case 0x534C: // 'SL' — Symbolic Link
                    ParseSl(entryData, entry);
                    break;

                case 0x4E4D: // 'NM' — Alternate Name
                    ParseNm(entryData, entry);
                    break;

                case 0x434C: // 'CL' — Child Link
                    ParseCl(entryData, entry);
                    break;

                case 0x504C: // 'PL' — Parent Link
                    ParsePl(entryData, entry);
                    break;

                case 0x5245: // 'RE' — Relocated Directory
                    entry.IsRelocated = true;
                    break;

                case 0x5446: // 'TF' — Time Stamps
                    ParseTf(entryData, entry);
                    break;

                case 0x5346: // 'SF' — Sparse File (RRIP §4.1.7)
                    ParseSf(entryData, entry);
                    break;
            }

            pos += len;
        }
    }

    /// <summary>Parses SP (SUSP Sharing Protocol Indicator) entry.</summary>
    private static void ParseSp(ReadOnlySpan<byte> data, RockRidgeEntry entry)
    {
        // SP: sig(2) + len(1) + ver(1) + check1(1) + check2(1) + LEN_SKP(1) = 7 bytes
        if (data.Length < 7) return;

        byte check1 = data[4];
        byte check2 = data[5];

        // Verify check bytes (0xBE, 0xEF per SUSP §5.3)
        if (check1 == 0xBE && check2 == 0xEF)
        {
            entry.SuspActive = true;
            entry.SkipLength = data[6]; // LEN_SKP
        }
    }

    /// <summary>Parses ER (Extensions Reference) entry.</summary>
    private static void ParseEr(ReadOnlySpan<byte> data, RockRidgeEntry entry)
    {
        // ER: sig(2) + len(1) + ver(1) + LEN_ID(1) + LEN_DES(1) + LEN_SRC(1) + EXT_VER(1) + id + desc + src
        if (data.Length < 8) return;

        byte lenId = data[4];
        byte lenDes = data[5];
        byte lenSrc = data[6];

        if (data.Length < 8 + lenId + lenDes + lenSrc) return;

        string id = Encoding.ASCII.GetString(data.Slice(8, lenId));
        entry.ExtensionIdentifiers.Add(id);

        // Check for Rock Ridge identifier
        if (id.StartsWith("RRIP_1991A", StringComparison.Ordinal) ||
            id.StartsWith("IEEE_P1282", StringComparison.Ordinal) ||
            id.StartsWith("RRIP_", StringComparison.Ordinal))
        {
            entry.RockRidgeIdentified = true;
        }
    }

    /// <summary>Parses RR (Rock Ridge Indicator) entry.</summary>
    private static void ParseRr(ReadOnlySpan<byte> data, RockRidgeEntry entry)
    {
        // RR: sig(2) + len(1) + ver(1) + flags(1) = 5 bytes
        if (data.Length < 5) return;

        entry.RrFlags = data[4];
        entry.RockRidgeIdentified = true;
    }

    /// <summary>Parses PX (POSIX File Attributes) entry.</summary>
    private static void ParsePx(ReadOnlySpan<byte> data, RockRidgeEntry entry)
    {
        // PX (RRIP 1.12): sig(2) + len(1) + ver(1) + mode(8) + nlinks(8) + uid(8) + gid(8) + ino(8) = 44 bytes
        // PX (RRIP 1.10): sig(2) + len(1) + ver(1) + mode(8) + nlinks(8) + uid(8) + gid(8) = 36 bytes
        if (data.Length < 36) return;

        var attrs = new RockRidgePosixAttributes
        {
            Mode = ReadBothEndian32(data, 4),
            LinkCount = ReadBothEndian32(data, 12),
            Uid = ReadBothEndian32(data, 20),
            Gid = ReadBothEndian32(data, 28)
        };

        // RRIP 1.12 includes serial number
        if (data.Length >= 44)
            attrs.SerialNumber = ReadBothEndian32(data, 36);

        entry.PosixAttributes = attrs;
    }

    /// <summary>Parses PN (POSIX Device Number) entry.</summary>
    private static void ParsePn(ReadOnlySpan<byte> data, RockRidgeEntry entry)
    {
        // PN: sig(2) + len(1) + ver(1) + dev_t_high(8) + dev_t_low(8) = 20 bytes
        if (data.Length < 20) return;

        entry.DeviceNumber = new RockRidgeDeviceNumber
        {
            Major = ReadBothEndian32(data, 4),
            Minor = ReadBothEndian32(data, 12)
        };
    }

    /// <summary>Parses NM (Alternate Name) entry. Handles continuation flag.</summary>
    private static void ParseNm(ReadOnlySpan<byte> data, RockRidgeEntry entry)
    {
        // NM: sig(2) + len(1) + ver(1) + flags(1) + name_content
        if (data.Length < 5) return;

        byte flags = data[4];

        // Special names
        if ((flags & 0x02) != 0)
        {
            entry.AlternateName = ".";
            return;
        }
        if ((flags & 0x04) != 0)
        {
            entry.AlternateName = "..";
            return;
        }

        int nameLen = data.Length - 5;
        if (nameLen > 0)
        {
            string chunk = Encoding.ASCII.GetString(data.Slice(5, nameLen));

            if ((flags & 0x01) != 0)
            {
                // CONTINUE flag: append to existing name
                entry.AlternateName = (entry.AlternateName ?? "") + chunk;
            }
            else
            {
                // Final (or only) chunk
                entry.AlternateName = (entry.AlternateName ?? "") + chunk;
            }
        }
    }

    /// <summary>Parses SL (Symbolic Link) entry. Handles component records and continuation.</summary>
    private static void ParseSl(ReadOnlySpan<byte> data, RockRidgeEntry entry)
    {
        // SL: sig(2) + len(1) + ver(1) + flags(1) + component_records...
        if (data.Length < 5) return;

        entry.Symlink ??= new RockRidgeSymlink();

        int pos = 5;
        while (pos + 2 <= data.Length)
        {
            byte compFlags = data[pos];
            byte compLen = data[pos + 1];

            if (pos + 2 + compLen > data.Length)
                break;

            string component;
            if ((compFlags & 0x02) != 0)
                component = ".";
            else if ((compFlags & 0x04) != 0)
                component = "..";
            else if ((compFlags & 0x08) != 0)
                component = "/";
            else if (compLen > 0)
                component = Encoding.ASCII.GetString(data.Slice(pos + 2, compLen));
            else
                component = "";

            // Build path
            if (entry.Symlink.Target.Length == 0 || component == "/")
            {
                entry.Symlink.Target += component;
            }
            else if (entry.Symlink.Target.EndsWith('/'))
            {
                entry.Symlink.Target += component;
            }
            else if ((compFlags & 0x08) == 0) // Not root component
            {
                entry.Symlink.Target += "/" + component;
            }

            pos += 2 + compLen;
        }
    }

    /// <summary>Parses CL (Child Link) entry.</summary>
    private static void ParseCl(ReadOnlySpan<byte> data, RockRidgeEntry entry)
    {
        // CL: sig(2) + len(1) + ver(1) + child_location(8) = 12 bytes
        if (data.Length < 12) return;

        entry.ChildLink = ReadBothEndian32(data, 4);
    }

    /// <summary>Parses PL (Parent Link) entry.</summary>
    private static void ParsePl(ReadOnlySpan<byte> data, RockRidgeEntry entry)
    {
        // PL: sig(2) + len(1) + ver(1) + parent_location(8) = 12 bytes
        if (data.Length < 12) return;

        entry.ParentLink = ReadBothEndian32(data, 4);
    }

    /// <summary>Parses SF (Sparse File) entry.</summary>
    private static void ParseSf(ReadOnlySpan<byte> data, RockRidgeEntry entry)
    {
        // SF: sig(2) + len(1) + ver(1) + virtual_size_high(8) + virtual_size_low(8) + table_depth(1) = 21+ bytes
        if (data.Length < 21) return;

        entry.SparseFile = new RockRidgeSparseFile
        {
            VirtualSizeHigh = ReadBothEndian32(data, 4),
            VirtualSizeLow = ReadBothEndian32(data, 12),
            TableDepth = data[20]
        };
    }

    /// <summary>Parses TF (Time Stamps) entry.</summary>
    private static void ParseTf(ReadOnlySpan<byte> data, RockRidgeEntry entry)
    {
        // TF: sig(2) + len(1) + ver(1) + flags(1) + timestamps...
        if (data.Length < 5) return;

        byte flags = data[4];
        bool longForm = (flags & 0x80) != 0;
        int stampSize = longForm ? 17 : 7;

        var ts = new RockRidgeTimestamps { IsLongForm = longForm };
        int pos = 5;

        // Bit 0: Creation
        if ((flags & 0x01) != 0)
        {
            if (pos + stampSize <= data.Length)
                ts.Creation = ReadTimestamp(data, pos, longForm);
            pos += stampSize;
        }

        // Bit 1: Modification
        if ((flags & 0x02) != 0)
        {
            if (pos + stampSize <= data.Length)
                ts.Modification = ReadTimestamp(data, pos, longForm);
            pos += stampSize;
        }

        // Bit 2: Access
        if ((flags & 0x04) != 0)
        {
            if (pos + stampSize <= data.Length)
                ts.Access = ReadTimestamp(data, pos, longForm);
            pos += stampSize;
        }

        // Bit 3: Attributes
        if ((flags & 0x08) != 0)
        {
            if (pos + stampSize <= data.Length)
                ts.Attributes = ReadTimestamp(data, pos, longForm);
            pos += stampSize;
        }

        // Bit 4: Backup
        if ((flags & 0x10) != 0)
        {
            if (pos + stampSize <= data.Length)
                ts.Backup = ReadTimestamp(data, pos, longForm);
            pos += stampSize;
        }

        // Bit 5: Expiration
        if ((flags & 0x20) != 0)
        {
            if (pos + stampSize <= data.Length)
                ts.Expiration = ReadTimestamp(data, pos, longForm);
            pos += stampSize;
        }

        // Bit 6: Effective
        if ((flags & 0x40) != 0)
        {
            if (pos + stampSize <= data.Length)
                ts.Effective = ReadTimestamp(data, pos, longForm);
        }

        entry.Timestamps = ts;
    }

    /// <summary>
    /// Follows a CE (Continuation Entry) by reading data from the ISO stream.
    /// </summary>
    private static byte[]? FollowContinuation(ReadOnlySpan<byte> ceEntry, Stream isoStream)
    {
        // CE: sig(2) + len(1) + ver(1) + block_location(8) + offset(8) + length(8) = 28 bytes
        if (ceEntry.Length < 28) return null;

        uint blockLocation = ReadBothEndian32(ceEntry, 4);
        uint blockOffset = ReadBothEndian32(ceEntry, 12);
        uint ceLength = ReadBothEndian32(ceEntry, 20);

        if (ceLength == 0 || ceLength > 65536) // Sanity limit
            return null;

        long fileOffset = (long)blockLocation * 2048 + blockOffset;
        if (fileOffset + ceLength > isoStream.Length)
            return null;

        try
        {
            isoStream.Seek(fileOffset, SeekOrigin.Begin);
            var data = new byte[ceLength];
            if (ReadFull(isoStream, data, 0, (int)ceLength) < (int)ceLength)
                return null;
            return data;
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------
    // Encoding helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Reads a 32-bit unsigned integer from ISO 9660 both-endian format.
    /// Reads the little-endian value (first 4 bytes of the 8-byte field).
    /// </summary>
    private static uint ReadBothEndian32(ReadOnlySpan<byte> data, int offset)
    {
        if (offset + 4 > data.Length) return 0;

        return (uint)data[offset]
             | ((uint)data[offset + 1] << 8)
             | ((uint)data[offset + 2] << 16)
             | ((uint)data[offset + 3] << 24);
    }

    /// <summary>
    /// Reads an ISO 9660 short-form (7-byte) timestamp (ECMA-119 §9.1.5).
    /// </summary>
    private static DateTime ReadShortTimestamp(ReadOnlySpan<byte> data, int offset)
    {
        if (offset + 7 > data.Length)
            return DateTime.MinValue;

        int year = data[offset] + 1900;
        int month = data[offset + 1];
        int day = data[offset + 2];
        int hour = data[offset + 3];
        int minute = data[offset + 4];
        int second = data[offset + 5];
        int gmtOffset = (sbyte)data[offset + 6]; // 15-minute intervals

        // Validate ranges
        if (year < 1900 || year > 2155) year = 2000;
        if (month < 1 || month > 12) month = 1;
        if (day < 1 || day > 31) day = 1;
        if (hour < 0 || hour > 23) hour = 0;
        if (minute < 0 || minute > 59) minute = 0;
        if (second < 0 || second > 59) second = 0;

        try
        {
            var dt = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc);
            // Apply GMT offset (each unit = 15 minutes)
            if (gmtOffset >= -48 && gmtOffset <= 52)
                dt = dt.AddMinutes(-gmtOffset * 15);
            return dt;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>
    /// Reads an ISO 9660 long-form (17-byte) timestamp (ECMA-119 §8.4.26.1).
    /// Format: 16 ASCII digit bytes ("YYYYMMDDHHmmsscc") + 1 GMT-offset byte.
    /// </summary>
    private static DateTime ReadLongTimestamp(ReadOnlySpan<byte> data, int offset)
    {
        if (offset + 17 > data.Length)
            return DateTime.MinValue;

        try
        {
            var digits = Encoding.ASCII.GetString(data.Slice(offset, 16));

            // Check for all-zero or all-space timestamps (means "not specified")
            if (digits.Trim('0', ' ', '\0').Length == 0)
                return DateTime.MinValue;

            int year = int.Parse(digits.Substring(0, 4));
            int month = int.Parse(digits.Substring(4, 2));
            int day = int.Parse(digits.Substring(6, 2));
            int hour = int.Parse(digits.Substring(8, 2));
            int minute = int.Parse(digits.Substring(10, 2));
            int second = int.Parse(digits.Substring(12, 2));
            int hundredths = int.Parse(digits.Substring(14, 2));
            int gmtOffset = (sbyte)data[offset + 16];

            if (month < 1 || month > 12) month = 1;
            if (day < 1 || day > 31) day = 1;

            var dt = new DateTime(year, month, day, hour, minute, second,
                hundredths * 10, DateTimeKind.Utc);
            if (gmtOffset >= -48 && gmtOffset <= 52)
                dt = dt.AddMinutes(-gmtOffset * 15);
            return dt;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>Reads a timestamp in either short or long form.</summary>
    private static DateTime ReadTimestamp(ReadOnlySpan<byte> data, int offset, bool longForm)
    {
        return longForm
            ? ReadLongTimestamp(data, offset)
            : ReadShortTimestamp(data, offset);
    }

    /// <summary>Reads exactly <paramref name="count"/> bytes or as many as available.</summary>
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

    /// <summary>
    /// Recursively reads directory entries from an ISO 9660 directory extent.
    /// </summary>
    private static void ReadDirectoryRecursive(
        Stream isoStream, uint extentLba, uint dataLength, string parentPath,
        List<RockRidgeFileEntry> results, HashSet<uint> visited,
        int depth, int maxDepth)
    {
        if (depth > maxDepth) return;

        var entries = ReadDirectoryExtent(isoStream, extentLba, dataLength);

        foreach (var (isoName, rr, rawRecord) in entries)
        {
            // Skip "." and ".." entries
            if (isoName == "." || isoName == "..")
                continue;

            // Skip relocated directories (RE flag set)
            if (rr.IsRelocated)
                continue;

            byte fileFlags = rawRecord.Length > 25 ? rawRecord[25] : (byte)0;
            bool isDir = (fileFlags & 0x02) != 0;

            string effectiveName = rr.AlternateName ?? isoName;
            // Strip ISO 9660 version number (";1")
            int semi = effectiveName.IndexOf(';');
            if (semi >= 0) effectiveName = effectiveName[..semi];
            effectiveName = effectiveName.TrimEnd('.');

            string fullPath = parentPath.EndsWith('/')
                ? parentPath + effectiveName
                : parentPath + "/" + effectiveName;

            results.Add(new RockRidgeFileEntry
            {
                Path = fullPath,
                Iso9660Name = isoName,
                IsDirectory = isDir,
                RockRidge = rr
            });

            // Follow child link if present (CL entry for relocated directories)
            if (rr.ChildLink.HasValue && !visited.Contains(rr.ChildLink.Value))
            {
                visited.Add(rr.ChildLink.Value);
                // Read relocated directory extent — we need its data length
                // Read the first directory record to get the extent size
                long childOffset = (long)rr.ChildLink.Value * 2048;
                if (childOffset + 34 <= isoStream.Length)
                {
                    isoStream.Seek(childOffset, SeekOrigin.Begin);
                    var header = new byte[34];
                    if (ReadFull(isoStream, header, 0, 34) == 34)
                    {
                        uint childDataLen = BitConverter.ToUInt32(header, 10);
                        if (childDataLen > 0 && childDataLen < 16 * 1024 * 1024)
                        {
                            ReadDirectoryRecursive(isoStream, rr.ChildLink.Value, childDataLen,
                                fullPath, results, visited, depth + 1, maxDepth);
                        }
                    }
                }
                continue;
            }

            // Recurse into subdirectories
            if (isDir && rawRecord.Length > 14)
            {
                uint childLba = BitConverter.ToUInt32(rawRecord, 2);
                uint childLen = BitConverter.ToUInt32(rawRecord, 10);
                if (childLba > 0 && childLen > 0 && !visited.Contains(childLba))
                {
                    visited.Add(childLba);
                    ReadDirectoryRecursive(isoStream, childLba, childLen, fullPath,
                        results, visited, depth + 1, maxDepth);
                }
            }
        }
    }
}

