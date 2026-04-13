// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenBurningSuite.Models;

namespace OpenBurningSuite.Services;

/// <summary>
/// Service for PS3 Blu-ray disc operations including:
/// - Blu-ray content type detection (movie, data, PS3/PS4/PS5 game)
/// - PARAM.SFO metadata parsing
/// - IRD file parsing (versions 6-9) for disc key extraction
/// - AES-128-CBC sector-level decryption
/// - Decrypted disc copy creation (for emulator use)
/// - Encrypted ISO decryption before burning
/// </summary>
/// <remarks>
/// PS3 disc encryption reference:
///   - Disc sectors are 2048 bytes
///   - Encryption uses AES-128-CBC with per-sector IV
///   - IV = 16-byte array with sector number in last 8 bytes (big-endian)
///   - Odd-numbered regions are encrypted, even-numbered regions are unencrypted
///   - Region map is stored in the first sector of the ISO (sector 0)
///   - The disc key is derived from IRD Data1 field using AES-128-CBC with fixed key/IV
///
/// IRD file format reference (psdevwiki.com/ps3/IRD):
///   - Magic: "3IRD" (0x33, 0x49, 0x52, 0x44) — always gzip-compressed
///   - Versions 6-9 supported
///   - Contains Data1 key (encrypted disc key), Data2 key (disc ID)
///   - Data1 → DiscKey via AES-128-CBC encrypt with D1AesKey/D1AesIV
///
/// PS3 disc structure (psdevwiki.com/ps3/Bluray_disc):
///   - PS3_GAME/PARAM.SFO — game metadata
///   - PS3_DISC.SFB — disc identification
///   - PS3_UPDATE/ — firmware update
///   - BDMV/ — Blu-ray movie structure (if movie disc)
/// </remarks>
public static class Ps3DiscService
{
    // -----------------------------------------------------------------------
    //  AES keys for IRD Data1/Data2 derivation (from psdevwiki.com/ps3/IRD)
    // -----------------------------------------------------------------------

    /// <summary>AES-128-CBC key for encrypting Data1 → DiscKey.</summary>
    private static readonly byte[] D1AesKey =
    {
        0x38, 0x0B, 0xCF, 0x0B, 0x53, 0x45, 0x5B, 0x3C,
        0x78, 0x17, 0xAB, 0x4F, 0xA3, 0xBA, 0x90, 0xED
    };

    /// <summary>AES-128-CBC IV for encrypting Data1 → DiscKey.</summary>
    private static readonly byte[] D1AesIV =
    {
        0x69, 0x47, 0x47, 0x72, 0xAF, 0x6F, 0xDA, 0xB3,
        0x42, 0x74, 0x3A, 0xEF, 0xAA, 0x18, 0x62, 0x87
    };

    /// <summary>IRD file magic bytes: "3IRD".</summary>
    private static readonly byte[] IrdMagic = { 0x33, 0x49, 0x52, 0x44 };

    /// <summary>PARAM.SFO magic bytes: "\0PSF".</summary>
    private static readonly byte[] SfoMagic = { 0x00, 0x50, 0x53, 0x46 };

    /// <summary>Blu-ray sector size.</summary>
    public const int SectorSize = 2048;

    // -----------------------------------------------------------------------
    //  Blu-ray content type detection
    // -----------------------------------------------------------------------

    /// <summary>
    /// Detects the content type of a Blu-ray disc image file or mounted disc.
    /// Examines the filesystem for BDMV (movie), PS3_GAME, PS4, PS5, or data structures.
    /// </summary>
    /// <param name="isoPath">Path to the ISO file.</param>
    /// <returns>Content information including type, title, and PS3 metadata.</returns>
    public static BluRayContentInfo DetectContent(string isoPath)
    {
        var info = new BluRayContentInfo();
        if (!File.Exists(isoPath)) return info;

        try
        {
            using var stream = new FileStream(isoPath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, false);
            return DetectContentFromStream(stream);
        }
        catch { return info; }
    }

    /// <summary>
    /// Detects the content type from a seekable stream (ISO image or disc device stream).
    /// </summary>
    internal static BluRayContentInfo DetectContentFromStream(Stream stream)
    {
        var info = new BluRayContentInfo();
        if (stream.Length < SectorSize * 17) return info;

        // Read ISO 9660 PVD at sector 16 for volume label and system identifier
        stream.Seek(16L * SectorSize, SeekOrigin.Begin);
        var pvd = new byte[SectorSize];
        if (stream.Read(pvd, 0, SectorSize) < SectorSize) return info;

        if (pvd[0] == 0x01 && pvd[1] == (byte)'C' && pvd[2] == (byte)'D' &&
            pvd[3] == (byte)'0' && pvd[4] == (byte)'0' && pvd[5] == (byte)'1')
        {
            info.VolumeLabel = Encoding.ASCII.GetString(pvd, 40, 32).Trim();
            var systemId = Encoding.ASCII.GetString(pvd, 8, 32).Trim();

            // Extract root directory record from PVD to scan directories
            // Root directory record at PVD offset 156, 34 bytes per ECMA-119
            if (pvd[156] >= 34)
            {
                int rootLba = pvd[156 + 2] | (pvd[156 + 3] << 8) |
                              (pvd[156 + 4] << 16) | (pvd[156 + 5] << 24);
                int rootLen = pvd[156 + 10] | (pvd[156 + 11] << 8) |
                              (pvd[156 + 12] << 16) | (pvd[156 + 13] << 24);

                if (rootLba > 0 && rootLen > 0)
                {
                    var dirs = ReadDirectoryNames(stream, rootLba, rootLen);
                    info.ContentType = ClassifyFromDirectories(dirs, stream.Length);

                    // If PS3 game detected, try to read PARAM.SFO
                    if (info.ContentType == BluRayContentType.Ps3Game)
                    {
                        ReadPs3Metadata(stream, rootLba, rootLen, info);
                        ReadRegionMap(stream, info);
                    }
                }
            }

            // Fallback: use system identifier for PlayStation detection
            if (info.ContentType == BluRayContentType.Unknown &&
                systemId.Contains("PLAYSTATION", StringComparison.OrdinalIgnoreCase))
            {
                info.ContentType = stream.Length > Helpers.FormatHelper.BD25Capacity
                    ? BluRayContentType.Ps4Game  // PS4/PS5 discs are typically >25GB
                    : BluRayContentType.Ps3Game;
            }
        }

        // If still unknown and file is on BD-size media, it's at least a data disc
        if (info.ContentType == BluRayContentType.Unknown && stream.Length > 0)
            info.ContentType = BluRayContentType.Data;

        return info;
    }

    /// <summary>
    /// Reads directory entry names from an ISO 9660 directory record.
    /// </summary>
    private static List<string> ReadDirectoryNames(Stream stream, int lba, int length)
    {
        var names = new List<string>();
        int sectorsToRead = (length + SectorSize - 1) / SectorSize;
        var dirData = new byte[sectorsToRead * SectorSize];

        stream.Seek((long)lba * SectorSize, SeekOrigin.Begin);
        int read = stream.Read(dirData, 0, dirData.Length);
        if (read == 0) return names;

        int offset = 0;
        while (offset < length && offset < read)
        {
            int recordLen = dirData[offset];
            if (recordLen == 0)
            {
                // Jump to next sector boundary
                offset = ((offset / SectorSize) + 1) * SectorSize;
                continue;
            }

            if (offset + recordLen > read) break;

            int nameLen = dirData[offset + 32];
            if (nameLen > 0 && nameLen <= recordLen - 33)
            {
                var name = Encoding.ASCII.GetString(dirData, offset + 33, nameLen)
                    .TrimEnd(';', '1', '\0', ' ');
                if (name.Length > 1) // Skip "." and ".." entries
                    names.Add(name);
            }

            offset += recordLen;
        }

        return names;
    }

    /// <summary>
    /// Classifies Blu-ray content type based on root directory names.
    /// Provides comprehensive differentiation between movie, audio, game,
    /// recording, interactive, 3D, UHD, and data Blu-ray content.
    /// </summary>
    private static BluRayContentType ClassifyFromDirectories(List<string> dirs, long imageSize)
    {
        bool hasPs3Game = dirs.Any(d => d.Equals("PS3_GAME", StringComparison.OrdinalIgnoreCase));
        bool hasPs3Disc = dirs.Any(d => d.Equals("PS3_DISC.SFB", StringComparison.OrdinalIgnoreCase));
        bool hasBdmv = dirs.Any(d => d.Equals("BDMV", StringComparison.OrdinalIgnoreCase));
        bool hasBdav = dirs.Any(d => d.Equals("BDAV", StringComparison.OrdinalIgnoreCase));
        bool hasBdjo = dirs.Any(d => d.Equals("BDJO", StringComparison.OrdinalIgnoreCase));
        bool hasJar = dirs.Any(d => d.Equals("JAR", StringComparison.OrdinalIgnoreCase));
        bool hasSsif = dirs.Any(d => d.Equals("SSIF", StringComparison.OrdinalIgnoreCase));
        bool hasAudioTs = dirs.Any(d => d.Equals("AUDIO_TS", StringComparison.OrdinalIgnoreCase));
        bool hasDcim = dirs.Any(d => d.Equals("DCIM", StringComparison.OrdinalIgnoreCase));
        bool hasAvchd = dirs.Any(d => d.Equals("AVCHD", StringComparison.OrdinalIgnoreCase));
        bool hasPs4App = dirs.Any(d => d.StartsWith("app", StringComparison.OrdinalIgnoreCase) &&
                                       dirs.Any(d2 => d2.Equals("bd", StringComparison.OrdinalIgnoreCase)));
        bool hasPs5Uds = dirs.Any(d => d.Equals("uds", StringComparison.OrdinalIgnoreCase));

        // ---- PlayStation detection ----
        if (hasPs3Game || hasPs3Disc)
            return BluRayContentType.Ps3Game;

        // PS4/PS5 detection
        if (hasPs4App)
        {
            if (hasPs5Uds || imageSize > Helpers.FormatHelper.BD50Capacity)
                return BluRayContentType.Ps5Game;
            return BluRayContentType.Ps4Game;
        }

        // ---- BDAV recording detection ----
        if (hasBdav || hasAvchd)
            return BluRayContentType.Bdav;

        // ---- BDMV-based content classification ----
        if (hasBdmv)
        {
            // 3D detection: SSIF directory indicates stereoscopic interleaved files
            if (hasSsif)
                return BluRayContentType.Movie3D;

            // UHD detection by size heuristic (> 50 GB + ~16 GB margin to avoid BD-50 false positives)
            if (imageSize > Helpers.FormatHelper.BD50Capacity + 16_000_000_000L)
                return BluRayContentType.UhdMovie;

            // BD-Audio: BDMV structure with AUDIO_TS indicates audio-focused disc
            if (hasAudioTs)
                return BluRayContentType.Audio;

            // Standard Blu-ray movie
            return BluRayContentType.Movie;
        }

        // ---- Photo disc detection ----
        // Only DCIM is a reliable standard photo directory indicator.
        // Generic names like "Photos" or "Pictures" are too common on data discs.
        if (hasDcim)
            return BluRayContentType.Photo;

        // ---- Data disc fallback ----
        if (dirs.Count > 0)
            return BluRayContentType.Data;

        return BluRayContentType.Unknown;
    }

    /// <summary>
    /// Reads PS3 metadata (PARAM.SFO) from the ISO filesystem.
    /// Navigates: root → PS3_GAME → PARAM.SFO
    /// </summary>
    private static void ReadPs3Metadata(Stream stream, int rootLba, int rootLen, BluRayContentInfo info)
    {
        try
        {
            // Find PS3_GAME directory entry
            var (ps3GameLba, ps3GameLen) = FindDirectoryEntry(stream, rootLba, rootLen, "PS3_GAME");
            if (ps3GameLba <= 0) return;

            // Find PARAM.SFO inside PS3_GAME
            var (sfoLba, sfoLen) = FindFileEntry(stream, ps3GameLba, ps3GameLen, "PARAM.SFO");
            if (sfoLba <= 0 || sfoLen <= 0) return;

            // Read and parse PARAM.SFO
            stream.Seek((long)sfoLba * SectorSize, SeekOrigin.Begin);
            int sfoReadLen = Math.Min(sfoLen, 64 * 1024); // Cap at 64KB
            var sfoData = new byte[sfoReadLen];
            if (stream.Read(sfoData, 0, sfoReadLen) < sfoReadLen) return;

            var sfoParams = ParseParamSfo(sfoData);

            if (sfoParams.TryGetValue("TITLE_ID", out var titleId))
                info.TitleId = titleId;
            if (sfoParams.TryGetValue("TITLE", out var title))
                info.Title = title;
            if (sfoParams.TryGetValue("VERSION", out var version))
                info.DiscVersion = version;
            if (sfoParams.TryGetValue("PS3_SYSTEM_VER", out var fwVer))
                info.FirmwareVersion = fwVer;
            if (sfoParams.TryGetValue("CATEGORY", out var category))
                info.Category = category;
        }
        catch { /* metadata reading is best-effort */ }
    }

    /// <summary>
    /// Reads the PS3 region map from sector 0 of the ISO.
    /// Per PS3 disc format: first 4 bytes (big-endian) = number of unencrypted regions.
    /// Total regions = 2 * unencrypted_count - 1.
    /// Region boundaries follow at offset 8.
    /// </summary>
    private static void ReadRegionMap(Stream stream, BluRayContentInfo info)
    {
        try
        {
            stream.Seek(0, SeekOrigin.Begin);
            var header = new byte[4];
            if (stream.Read(header, 0, 4) < 4) return;

            // Big-endian uint32: number of unencrypted regions
            int unencryptedCount = (header[0] << 24) | (header[1] << 16) | (header[2] << 8) | header[3];
            if (unencryptedCount <= 0 || unencryptedCount > 127) return;

            int totalRegions = 2 * unencryptedCount - 1;
            info.RegionCount = totalRegions;
            info.IsEncrypted = totalRegions > 1; // If more than 1 region, odd regions are encrypted
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Finds a directory entry by name within an ISO 9660 directory record.
    /// Returns (LBA, dataLength) of the found entry, or (0, 0) if not found.
    /// </summary>
    private static (int lba, int length) FindDirectoryEntry(Stream stream, int parentLba, int parentLen, string name)
    {
        return FindEntry(stream, parentLba, parentLen, name, isDirectory: true);
    }

    /// <summary>
    /// Finds a file entry by name within an ISO 9660 directory record.
    /// Returns (LBA, dataLength) of the found entry, or (0, 0) if not found.
    /// </summary>
    private static (int lba, int length) FindFileEntry(Stream stream, int parentLba, int parentLen, string name)
    {
        return FindEntry(stream, parentLba, parentLen, name, isDirectory: false);
    }

    private static (int lba, int length) FindEntry(Stream stream, int parentLba, int parentLen, string name, bool isDirectory)
    {
        int sectorsToRead = (parentLen + SectorSize - 1) / SectorSize;
        var dirData = new byte[sectorsToRead * SectorSize];

        stream.Seek((long)parentLba * SectorSize, SeekOrigin.Begin);
        int read = stream.Read(dirData, 0, dirData.Length);
        if (read == 0) return (0, 0);

        int offset = 0;
        while (offset < parentLen && offset < read)
        {
            int recordLen = dirData[offset];
            if (recordLen == 0)
            {
                offset = ((offset / SectorSize) + 1) * SectorSize;
                continue;
            }
            if (offset + recordLen > read) break;

            byte flags = dirData[offset + 25];
            bool entryIsDir = (flags & 0x02) != 0;

            int nameLen = dirData[offset + 32];
            if (nameLen > 0 && nameLen <= recordLen - 33)
            {
                var entryName = Encoding.ASCII.GetString(dirData, offset + 33, nameLen)
                    .TrimEnd(';', '1', '\0', ' ');

                if (entryName.Equals(name, StringComparison.OrdinalIgnoreCase) && entryIsDir == isDirectory)
                {
                    int entryLba = dirData[offset + 2] | (dirData[offset + 3] << 8) |
                                   (dirData[offset + 4] << 16) | (dirData[offset + 5] << 24);
                    int entryLen = dirData[offset + 10] | (dirData[offset + 11] << 8) |
                                   (dirData[offset + 12] << 16) | (dirData[offset + 13] << 24);
                    return (entryLba, entryLen);
                }
            }

            offset += recordLen;
        }

        return (0, 0);
    }

    // -----------------------------------------------------------------------
    //  PARAM.SFO parser
    // -----------------------------------------------------------------------

    /// <summary>
    /// Parses a PS3 PARAM.SFO binary file into a dictionary of key-value pairs.
    /// </summary>
    /// <remarks>
    /// PARAM.SFO format (psdevwiki.com/ps3/PARAM.SFO):
    ///   Header (20 bytes): magic (\0PSF), version, key table offset, data table offset, entry count
    ///   Index table: 16 bytes per entry (key offset, format, length, max length, data offset)
    ///   Key table: null-terminated UTF-8 strings
    ///   Data table: values (UTF-8 strings or binary data)
    /// </remarks>
    public static Dictionary<string, string> ParseParamSfo(byte[] data)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (data == null || data.Length < 20) return result;

        // Validate magic: \0PSF (0x00505346)
        if (data[0] != SfoMagic[0] || data[1] != SfoMagic[1] ||
            data[2] != SfoMagic[2] || data[3] != SfoMagic[3])
            return result;

        // Header fields (little-endian)
        int keyTableOffset = data[8] | (data[9] << 8) | (data[10] << 16) | (data[11] << 24);
        int dataTableOffset = data[12] | (data[13] << 8) | (data[14] << 16) | (data[15] << 24);
        int entryCount = data[16] | (data[17] << 8) | (data[18] << 16) | (data[19] << 24);

        if (entryCount <= 0 || entryCount > 1000) return result;
        if (keyTableOffset <= 0 || dataTableOffset <= 0) return result;
        if (keyTableOffset >= data.Length || dataTableOffset >= data.Length) return result;

        // Index table starts at offset 20, each entry is 16 bytes
        for (int i = 0; i < entryCount; i++)
        {
            int indexOffset = 20 + i * 16;
            if (indexOffset + 16 > data.Length) break;

            int keyOffset = data[indexOffset] | (data[indexOffset + 1] << 8);
            int format = data[indexOffset + 2] | (data[indexOffset + 3] << 8);
            int valueLen = data[indexOffset + 4] | (data[indexOffset + 5] << 8) |
                           (data[indexOffset + 6] << 16) | (data[indexOffset + 7] << 24);
            // maxLen at indexOffset + 8..11 (not needed for reading)
            int dataOffset = data[indexOffset + 12] | (data[indexOffset + 13] << 8) |
                             (data[indexOffset + 14] << 16) | (data[indexOffset + 15] << 24);

            // Read key name from key table
            int absKeyOffset = keyTableOffset + keyOffset;
            if (absKeyOffset >= data.Length) continue;

            int keyEnd = absKeyOffset;
            while (keyEnd < data.Length && data[keyEnd] != 0) keyEnd++;
            string key = Encoding.UTF8.GetString(data, absKeyOffset, keyEnd - absKeyOffset);

            // Read value from data table
            int absDataOffset = dataTableOffset + dataOffset;
            if (absDataOffset >= data.Length || absDataOffset + valueLen > data.Length) continue;

            string value;
            if (format == 0x0204 || format == 0x0004) // UTF-8 string (null-terminated or special null)
            {
                // Trim trailing null bytes
                int strLen = valueLen;
                while (strLen > 0 && data[absDataOffset + strLen - 1] == 0) strLen--;
                value = Encoding.UTF8.GetString(data, absDataOffset, strLen);
            }
            else if (format == 0x0404) // uint32
            {
                if (valueLen >= 4)
                {
                    uint intVal = (uint)(data[absDataOffset] | (data[absDataOffset + 1] << 8) |
                                         (data[absDataOffset + 2] << 16) | (data[absDataOffset + 3] << 24));
                    value = intVal.ToString();
                }
                else
                    value = string.Empty;
            }
            else
            {
                // Binary data — store as hex
                value = BitConverter.ToString(data, absDataOffset, Math.Min(valueLen, 64))
                    .Replace("-", "");
            }

            result[key] = value;
        }

        return result;
    }

    // -----------------------------------------------------------------------
    //  IRD file parser
    // -----------------------------------------------------------------------

    /// <summary>
    /// Result of parsing an IRD file.
    /// </summary>
    public class IrdData
    {
        /// <summary>IRD format version (6-9).</summary>
        public byte Version { get; set; }

        /// <summary>Title ID from the IRD (9 ASCII characters, no dash).</summary>
        public string TitleId { get; set; } = string.Empty;

        /// <summary>Game title from the IRD.</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>System update version (4 ASCII characters, e.g. "1.20").</summary>
        public string SystemVersion { get; set; } = string.Empty;

        /// <summary>Disc version (5 ASCII characters, e.g. "01.00").</summary>
        public string DiscVersion { get; set; } = string.Empty;

        /// <summary>App version (5 ASCII characters, e.g. "01.00").</summary>
        public string AppVersion { get; set; } = string.Empty;

        /// <summary>Data1 key (16 bytes) — encrypted disc key stored in the IRD.</summary>
        public byte[] Data1Key { get; set; } = Array.Empty<byte>();

        /// <summary>Data2 key (16 bytes) — encrypted disc ID stored in the IRD.</summary>
        public byte[] Data2Key { get; set; } = Array.Empty<byte>();

        /// <summary>PIC data (115 bytes).</summary>
        public byte[] Pic { get; set; } = Array.Empty<byte>();

        /// <summary>Derived disc key (16 bytes) — the actual AES key for sector decryption.</summary>
        public byte[] DiscKey { get; set; } = Array.Empty<byte>();

        /// <summary>Region MD5 hashes (16 bytes each).</summary>
        public byte[][] RegionHashes { get; set; } = Array.Empty<byte[]>();

        /// <summary>File sector offsets.</summary>
        public long[] FileKeys { get; set; } = Array.Empty<long>();

        /// <summary>File MD5 hashes (16 bytes each).</summary>
        public byte[][] FileHashes { get; set; } = Array.Empty<byte[]>();
    }

    /// <summary>
    /// Parses an IRD file and extracts all metadata including the disc decryption key.
    /// IRD files are gzip-compressed and contain the "3IRD" magic signature.
    /// </summary>
    /// <param name="irdPath">Path to the .ird file.</param>
    /// <returns>Parsed IRD data, or null if the file is invalid.</returns>
    public static IrdData? ParseIrdFile(string irdPath)
    {
        if (!File.Exists(irdPath)) return null;

        try
        {
            using var fs = new FileStream(irdPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return ParseIrdFromStream(fs);
        }
        catch { return null; }
    }

    /// <summary>
    /// Parses an IRD file from a stream. The stream should contain gzip-compressed IRD data.
    /// </summary>
    private static IrdData? ParseIrdFromStream(Stream stream)
    {
        try
        {
            using var gzStream = new GZipStream(stream, CompressionMode.Decompress);
            using var br = new BinaryReader(gzStream, Encoding.UTF8);

            // Read and validate magic "3IRD"
            var magic = br.ReadBytes(4);
            if (magic.Length != 4 || magic[0] != IrdMagic[0] || magic[1] != IrdMagic[1] ||
                magic[2] != IrdMagic[2] || magic[3] != IrdMagic[3])
                return null;

            var ird = new IrdData();

            // Version (1 byte)
            ird.Version = br.ReadByte();
            if (ird.Version < 6 || ird.Version > 9)
                return null;

            // Title ID (9 bytes, ASCII)
            ird.TitleId = Encoding.ASCII.GetString(br.ReadBytes(9));

            // Title: 1-byte length followed by that many UTF-8 bytes.
            // Must NOT use BinaryReader.ReadString() which uses 7-bit variable-length
            // encoding — the IRD format uses a simple single-byte length prefix.
            byte titleLen = br.ReadByte();
            ird.Title = Encoding.UTF8.GetString(br.ReadBytes(titleLen));

            // System version (4 bytes, ASCII)
            ird.SystemVersion = Encoding.ASCII.GetString(br.ReadBytes(4));

            // Disc version (5 bytes, ASCII)
            ird.DiscVersion = Encoding.ASCII.GetString(br.ReadBytes(5));

            // App version (5 bytes, ASCII)
            ird.AppVersion = Encoding.ASCII.GetString(br.ReadBytes(5));

            // UID for version 7 only
            if (ird.Version == 7)
                br.ReadUInt32(); // UID — not used here

            // Header (gzip-compressed ISO header)
            uint headerLen = br.ReadUInt32();
            br.ReadBytes((int)headerLen); // Skip header data

            // Footer (gzip-compressed ISO footer)
            uint footerLen = br.ReadUInt32();
            br.ReadBytes((int)footerLen); // Skip footer data

            // Region hashes
            byte regionCount = br.ReadByte();
            ird.RegionHashes = new byte[regionCount][];
            for (int i = 0; i < regionCount; i++)
                ird.RegionHashes[i] = br.ReadBytes(16);

            // File hashes
            uint fileCount = br.ReadUInt32();
            ird.FileKeys = new long[fileCount];
            ird.FileHashes = new byte[fileCount][];
            for (uint i = 0; i < fileCount; i++)
            {
                ird.FileKeys[i] = br.ReadInt64();
                ird.FileHashes[i] = br.ReadBytes(16);
            }

            // Unknown field (per reference implementation, always 0)
            br.ReadInt32();

            // PIC for version >= 9 is placed before Data1/Data2
            if (ird.Version >= 9)
                ird.Pic = br.ReadBytes(115);

            // Data1 key (16 bytes) and Data2 key (16 bytes)
            ird.Data1Key = br.ReadBytes(16);
            ird.Data2Key = br.ReadBytes(16);

            // PIC for version < 9 is placed after Data1/Data2
            if (ird.Version < 9)
                ird.Pic = br.ReadBytes(115);

            // Derive the disc key from Data1
            ird.DiscKey = DeriveDiscKeyFromData1(ird.Data1Key);

            return ird;
        }
        catch { return null; }
    }

    // -----------------------------------------------------------------------
    //  Disc key derivation and sector decryption
    // -----------------------------------------------------------------------

    /// <summary>
    /// Derives the actual disc decryption key from IRD Data1 by AES-128-CBC encrypting
    /// Data1 with the fixed D1AesKey/D1AesIV.
    /// </summary>
    /// <remarks>
    /// The naming is counter-intuitive: "encrypting" Data1 produces the disc key,
    /// while "decrypting" the disc key produces Data1. This matches the PS3 hardware
    /// behavior as documented in the ps3-disc-dumper reference implementation.
    /// </remarks>
    public static byte[] DeriveDiscKeyFromData1(byte[] data1)
    {
        if (data1 == null || data1.Length != 16)
            throw new ArgumentException("Data1 must be exactly 16 bytes.", nameof(data1));

        using var aes = Aes.Create();
        aes.Key = D1AesKey;
        aes.IV = D1AesIV;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;

        using var encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(data1, 0, 16);
    }

    /// <summary>
    /// Derives Data1 from a disc key by AES-128-CBC decrypting the disc key
    /// with the fixed D1AesKey/D1AesIV. Inverse of <see cref="DeriveDiscKeyFromData1"/>.
    /// </summary>
    public static byte[] DeriveData1FromDiscKey(byte[] discKey)
    {
        if (discKey == null || discKey.Length != 16)
            throw new ArgumentException("Disc key must be exactly 16 bytes.", nameof(discKey));

        using var aes = Aes.Create();
        aes.Key = D1AesKey;
        aes.IV = D1AesIV;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;

        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(discKey, 0, 16);
    }

    /// <summary>
    /// Computes the AES-128-CBC initialization vector for a given sector number.
    /// The IV is a 16-byte array where the sector number occupies the last 8 bytes
    /// in big-endian order (bytes 8-15).
    /// </summary>
    public static byte[] GetSectorIV(long sectorNumber)
    {
        var iv = new byte[16];
        for (int i = 15; i >= 8; i--)
        {
            iv[i] = (byte)(sectorNumber & 0xFF);
            sectorNumber >>= 8;
        }
        return iv;
    }

    /// <summary>
    /// Decrypts a single sector (2048 bytes) using AES-128-CBC.
    /// </summary>
    /// <param name="discKey">16-byte disc decryption key.</param>
    /// <param name="sectorData">Sector data (must be 2048 bytes).</param>
    /// <param name="sectorNumber">Logical sector number (for IV computation).</param>
    /// <returns>Decrypted sector data.</returns>
    public static byte[] DecryptSector(byte[] discKey, byte[] sectorData, long sectorNumber)
    {
        if (discKey == null || discKey.Length != 16)
            throw new ArgumentException("Disc key must be exactly 16 bytes.", nameof(discKey));
        if (sectorData == null || sectorData.Length != SectorSize)
            throw new ArgumentException($"Sector data must be exactly {SectorSize} bytes.", nameof(sectorData));

        var iv = GetSectorIV(sectorNumber);

        using var aes = Aes.Create();
        aes.Key = discKey;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;

        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(sectorData, 0, SectorSize);
    }

    /// <summary>
    /// Encrypts a single sector (2048 bytes) using AES-128-CBC.
    /// Used for re-encrypting sectors when needed.
    /// </summary>
    public static byte[] EncryptSector(byte[] discKey, byte[] sectorData, long sectorNumber)
    {
        if (discKey == null || discKey.Length != 16)
            throw new ArgumentException("Disc key must be exactly 16 bytes.", nameof(discKey));
        if (sectorData == null || sectorData.Length != SectorSize)
            throw new ArgumentException($"Sector data must be exactly {SectorSize} bytes.", nameof(sectorData));

        var iv = GetSectorIV(sectorNumber);

        using var aes = Aes.Create();
        aes.Key = discKey;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;

        using var encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(sectorData, 0, SectorSize);
    }

    // -----------------------------------------------------------------------
    //  Region map parsing
    // -----------------------------------------------------------------------

    /// <summary>
    /// Represents a disc region (encrypted or unencrypted).
    /// </summary>
    public class DiscRegion
    {
        /// <summary>Starting sector (inclusive).</summary>
        public long StartSector { get; set; }
        /// <summary>Ending sector (inclusive).</summary>
        public long EndSector { get; set; }
        /// <summary>Whether this region is encrypted (odd-numbered regions).</summary>
        public bool IsEncrypted { get; set; }
    }

    /// <summary>
    /// Parses the PS3 disc region map from sector 0 of the ISO.
    /// </summary>
    /// <remarks>
    /// Sector 0 layout (big-endian):
    ///   Bytes 0-3: Number of unencrypted regions
    ///   Bytes 8+: Region boundary sectors (4 bytes each, big-endian)
    ///   Even regions (0, 2, 4, ...) are unencrypted
    ///   Odd regions (1, 3, 5, ...) are encrypted
    /// </remarks>
    /// <param name="isoPath">Path to the ISO file.</param>
    /// <returns>List of regions, or empty list if the map cannot be parsed.</returns>
    public static List<DiscRegion> ParseRegionMap(string isoPath)
    {
        if (!File.Exists(isoPath)) return new List<DiscRegion>();

        try
        {
            using var stream = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return ParseRegionMapFromStream(stream);
        }
        catch { return new List<DiscRegion>(); }
    }

    /// <summary>
    /// Parses the PS3 disc region map from a seekable stream.
    /// </summary>
    /// <remarks>
    /// Region map structure (per LibIRD/ps3-disc-dumper reference):
    ///   - Offset 0: Number of unencrypted regions (4 bytes, big-endian)
    ///   - Offset 8: First boundary sector (4 bytes, big-endian)
    ///   - Each subsequent 4 bytes: next boundary sector
    ///
    /// For each region i (0-based):
    ///   - Even regions (unencrypted): start = prev boundary, end = next boundary
    ///   - Odd regions (encrypted):    start = prev boundary + 1, end = next boundary - 1
    /// </remarks>
    private static List<DiscRegion> ParseRegionMapFromStream(Stream stream)
    {
        var regions = new List<DiscRegion>();
        if (stream.Length < SectorSize) return regions;

        stream.Seek(0, SeekOrigin.Begin);
        var sector0 = new byte[SectorSize];
        if (stream.Read(sector0, 0, SectorSize) < SectorSize) return regions;

        // Number of unencrypted regions (big-endian uint32 at offset 0)
        int unencryptedCount = (sector0[0] << 24) | (sector0[1] << 16) | (sector0[2] << 8) | sector0[3];
        if (unencryptedCount <= 0 || unencryptedCount > 127) return regions;

        int totalRegions = 2 * unencryptedCount - 1;

        // Read boundaries from offset 8 — there are (totalRegions + 1) boundary values
        // Each is 4 bytes big-endian
        int offset = 8;

        // Read first boundary (used as the starting point for region 0)
        long prevBoundary = ReadBE32(sector0, offset);
        offset += 4;

        for (int i = 0; i < totalRegions; i++)
        {
            if (offset + 4 > SectorSize) break;

            long nextBoundary = ReadBE32(sector0, offset);
            offset += 4;

            long start, end;
            if (i % 2 == 0) // Even = unencrypted
            {
                start = prevBoundary;
                end = nextBoundary;
            }
            else // Odd = encrypted
            {
                start = prevBoundary + 1;
                end = nextBoundary - 1;
            }

            regions.Add(new DiscRegion
            {
                StartSector = start,
                EndSector = end,
                IsEncrypted = i % 2 == 1
            });

            prevBoundary = nextBoundary;
        }

        return regions;
    }

    /// <summary>Reads a big-endian 32-bit unsigned integer from a byte array.</summary>
    private static long ReadBE32(byte[] data, int offset)
    {
        return ((long)data[offset] << 24) | ((long)data[offset + 1] << 16) |
               ((long)data[offset + 2] << 8) | data[offset + 3];
    }

    /// <summary>
    /// Determines whether a given sector is in an encrypted region.
    /// </summary>
    public static bool IsSectorEncrypted(List<DiscRegion> regions, long sectorNumber)
    {
        foreach (var region in regions)
        {
            if (sectorNumber >= region.StartSector && sectorNumber <= region.EndSector)
                return region.IsEncrypted;
        }
        return false;
    }

    // -----------------------------------------------------------------------
    //  Disc key loading (from hex string, .dkey file, or IRD file)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Loads a disc key from a hex string (32 hex characters = 16 bytes).
    /// </summary>
    /// <param name="hexKey">Hex string of the disc key.</param>
    /// <returns>16-byte disc key, or null if invalid.</returns>
    public static byte[]? LoadDiscKeyFromHex(string hexKey)
    {
        if (string.IsNullOrWhiteSpace(hexKey)) return null;
        hexKey = hexKey.Trim().Replace(" ", "").Replace("-", "");
        if (hexKey.Length != 32) return null;

        try
        {
            var key = new byte[16];
            for (int i = 0; i < 16; i++)
                key[i] = Convert.ToByte(hexKey.Substring(i * 2, 2), 16);
            return key;
        }
        catch { return null; }
    }

    /// <summary>
    /// Loads a disc key from a .dkey file (text file containing 32 hex characters).
    /// </summary>
    /// <param name="dkeyPath">Path to the .dkey file.</param>
    /// <returns>16-byte disc key, or null if invalid.</returns>
    public static byte[]? LoadDiscKeyFromDkeyFile(string dkeyPath)
    {
        if (!File.Exists(dkeyPath)) return null;

        try
        {
            var content = File.ReadAllText(dkeyPath).Trim();
            return LoadDiscKeyFromHex(content);
        }
        catch { return null; }
    }

    /// <summary>
    /// Loads a disc key from an IRD file.
    /// </summary>
    /// <param name="irdPath">Path to the .ird file.</param>
    /// <returns>16-byte disc key, or null if the IRD is invalid.</returns>
    public static byte[]? LoadDiscKeyFromIrd(string irdPath)
    {
        var ird = ParseIrdFile(irdPath);
        return ird?.DiscKey;
    }

    // -----------------------------------------------------------------------
    //  Decrypted ISO creation (decrypt an encrypted PS3 ISO)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Progress information for PS3 disc decryption operations.
    /// </summary>
    public class Ps3DecryptionProgress
    {
        /// <summary>Percentage complete (0-100).</summary>
        public int PercentComplete { get; set; }
        /// <summary>Current sector being processed.</summary>
        public long CurrentSector { get; set; }
        /// <summary>Total number of sectors.</summary>
        public long TotalSectors { get; set; }
        /// <summary>Sectors decrypted so far.</summary>
        public long DecryptedSectors { get; set; }
        /// <summary>Status message.</summary>
        public string StatusMessage { get; set; } = string.Empty;
    }

    /// <summary>
    /// Decrypts an encrypted PS3 ISO image, writing the decrypted output to a new file.
    /// Only encrypted regions (odd-numbered) are decrypted; unencrypted regions are copied as-is.
    /// </summary>
    /// <param name="inputIsoPath">Path to the encrypted PS3 ISO.</param>
    /// <param name="outputIsoPath">Path for the decrypted output ISO.</param>
    /// <param name="discKey">16-byte disc decryption key.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Null on success, or an error message on failure.</returns>
    public static async Task<string?> DecryptIsoAsync(
        string inputIsoPath,
        string outputIsoPath,
        byte[] discKey,
        IProgress<Ps3DecryptionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(inputIsoPath))
            return $"Input ISO not found: {inputIsoPath}";
        if (discKey == null || discKey.Length != 16)
            return "Invalid disc key (must be 16 bytes).";

        try
        {
            using var inputStream = new FileStream(inputIsoPath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 64 * 1024, true);

            // Parse region map
            var regions = ParseRegionMapFromStream(inputStream);
            if (regions.Count == 0)
                return "Could not parse PS3 disc region map from ISO.";

            long totalSectors = inputStream.Length / SectorSize;
            long decryptedCount = 0;

            // Create output directory if needed
            var outputDir = Path.GetDirectoryName(outputIsoPath);
            if (!string.IsNullOrEmpty(outputDir))
                Directory.CreateDirectory(outputDir);

            using var outputStream = new FileStream(outputIsoPath, FileMode.Create, FileAccess.Write,
                FileShare.None, 64 * 1024, true);

            // Process sectors in batches for efficiency
            const int batchSectors = 256;
            var inputBuffer = new byte[batchSectors * SectorSize];
            var outputBuffer = new byte[batchSectors * SectorSize];
            long currentSector = 0;

            inputStream.Seek(0, SeekOrigin.Begin);

            using var aes = Aes.Create();
            aes.Key = discKey;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;

            while (currentSector < totalSectors)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int sectorsToRead = (int)Math.Min(batchSectors, totalSectors - currentSector);
                int bytesToRead = sectorsToRead * SectorSize;

                int bytesRead = await inputStream.ReadAsync(inputBuffer, 0, bytesToRead, cancellationToken);
                if (bytesRead == 0) break;

                int actualSectors = bytesRead / SectorSize;

                for (int i = 0; i < actualSectors; i++)
                {
                    long sectorNum = currentSector + i;
                    int bufOffset = i * SectorSize;

                    if (IsSectorEncrypted(regions, sectorNum))
                    {
                        // Each sector requires a new decryptor because CBC mode IV
                        // is unique per sector and cannot be changed after creation.
                        var iv = GetSectorIV(sectorNum);
                        aes.IV = iv;
                        using var decryptor = aes.CreateDecryptor();
                        var decrypted = decryptor.TransformFinalBlock(
                            inputBuffer, bufOffset, SectorSize);
                        Buffer.BlockCopy(decrypted, 0, outputBuffer, bufOffset, SectorSize);
                        decryptedCount++;
                    }
                    else
                    {
                        // Copy unencrypted sector as-is
                        Buffer.BlockCopy(inputBuffer, bufOffset, outputBuffer, bufOffset, SectorSize);
                    }
                }

                await outputStream.WriteAsync(outputBuffer, 0, bytesRead, cancellationToken);

                currentSector += actualSectors;

                progress?.Report(new Ps3DecryptionProgress
                {
                    PercentComplete = (int)(currentSector * 100 / totalSectors),
                    CurrentSector = currentSector,
                    TotalSectors = totalSectors,
                    DecryptedSectors = decryptedCount,
                    StatusMessage = $"Decrypting: sector {currentSector:N0} / {totalSectors:N0} " +
                                    $"({decryptedCount:N0} encrypted sectors processed)"
                });
            }

            // Handle any trailing bytes (partial sector at end)
            long remainder = inputStream.Length % SectorSize;
            if (remainder > 0)
            {
                var tailBuf = new byte[remainder];
                int tailRead = await inputStream.ReadAsync(tailBuf, 0, (int)remainder, cancellationToken);
                if (tailRead > 0)
                    await outputStream.WriteAsync(tailBuf, 0, tailRead, cancellationToken);
            }

            progress?.Report(new Ps3DecryptionProgress
            {
                PercentComplete = 100,
                CurrentSector = totalSectors,
                TotalSectors = totalSectors,
                DecryptedSectors = decryptedCount,
                StatusMessage = $"Decryption complete. {decryptedCount:N0} encrypted sectors decrypted."
            });

            return null; // Success
        }
        catch (OperationCanceledException)
        {
            // Clean up partial output on cancellation
            try { if (File.Exists(outputIsoPath)) File.Delete(outputIsoPath); } catch { }
            return "Decryption cancelled.";
        }
        catch (Exception ex)
        {
            // Clean up partial output on failure
            try { if (File.Exists(outputIsoPath)) File.Delete(outputIsoPath); } catch { }
            return $"Decryption failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Synchronous overload of <see cref="DecryptIsoAsync"/>.
    /// </summary>
    public static string? DecryptIso(
        string inputIsoPath,
        string outputIsoPath,
        byte[] discKey,
        IProgress<Ps3DecryptionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return DecryptIsoAsync(inputIsoPath, outputIsoPath, discKey, progress, cancellationToken)
            .GetAwaiter().GetResult();
    }

    // -----------------------------------------------------------------------
    //  Hex conversion helpers
    // -----------------------------------------------------------------------

    /// <summary>Converts a byte array to a lowercase hex string.</summary>
    public static string ToHexString(byte[] data)
    {
        if (data == null || data.Length == 0) return string.Empty;
        var sb = new StringBuilder(data.Length * 2);
        foreach (byte b in data)
            sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    /// <summary>Converts a hex string to a byte array. Returns null on invalid input.</summary>
    public static byte[]? FromHexString(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        hex = hex.Trim().Replace(" ", "").Replace("-", "");
        if (hex.Length == 0 || hex.Length % 2 != 0) return null;

        try
        {
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return bytes;
        }
        catch { return null; }
    }
}
