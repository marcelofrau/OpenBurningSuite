// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using OpenBurningSuite.Helpers;

namespace OpenBurningSuite.Services;

/// <summary>
/// Service for Xbox ISO (XISO / XDVDFS) operations: creation, extraction, validation, and detection.
/// XISO uses the Xbox DVD File System (XDVDFS) with 2048-byte sectors.
/// The volume descriptor is located at sector 32 (offset 0x10000) and contains
/// the magic string "MICROSOFT*XBOX*MEDIA".
/// </summary>
public class XisoService
{
    /// <summary>XDVDFS magic identifier: "MICROSOFT*XBOX*MEDIA" (20 bytes).</summary>
    public static readonly byte[] XdvdfsMagic =
        Encoding.ASCII.GetBytes("MICROSOFT*XBOX*MEDIA");

    /// <summary>XDVDFS sector size: always 2048 bytes.</summary>
    public const int SectorSize = 2048;

    /// <summary>Sector number of the volume descriptor.</summary>
    public const int VolumeDescriptorSector = 32;

    /// <summary>Byte offset of the volume descriptor (sector 32 × 2048).</summary>
    public const long VolumeDescriptorOffset = (long)VolumeDescriptorSector * SectorSize;

    /// <summary>Offset within the volume descriptor where the magic string appears (byte 0).</summary>
    private const int MagicOffset = 0;

    /// <summary>Offset of root directory table sector (LE32) within the volume descriptor.</summary>
    private const int RootDirSectorOffset = 0x14;

    /// <summary>Offset of root directory table size (LE32) within the volume descriptor.</summary>
    private const int RootDirSizeOffset = 0x18;

    /// <summary>Offset of timestamp (LE64) within the volume descriptor.</summary>
    private const int TimestampOffset = 0x1C;

    /// <summary>Offset of the second copy of the magic at the end of the volume descriptor.</summary>
    private const int MagicEndOffset = 0x7EC;

    // =====================================================================
    // Directory entry structure:
    //   Offset 0  (2 bytes LE): Left subtree offset (in 4-byte units, 0xFFFF = none)
    //   Offset 2  (2 bytes LE): Right subtree offset (in 4-byte units, 0xFFFF = none)
    //   Offset 4  (4 bytes LE): Starting sector of file data
    //   Offset 8  (4 bytes LE): File size in bytes
    //   Offset 12 (1 byte):     File attributes (0x10 = directory, 0x80 = normal)
    //   Offset 13 (1 byte):     File name length
    //   Offset 14 (N bytes):    File name (ASCII, not null-terminated)
    // =====================================================================

    /// <summary>File attribute flag for directories in XDVDFS.</summary>
    private const byte DirEntryAttributeDirectory = 0x10;

    /// <summary>
    /// Represents a file or directory entry in an XDVDFS (Xbox ISO) image.
    /// </summary>
    public class XisoEntry
    {
        public string Name { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public uint StartSector { get; set; }
        public uint Size { get; set; }
        public bool IsDirectory { get; set; }
        public List<XisoEntry> Children { get; set; } = new();
    }

    /// <summary>
    /// Detects whether a file is a valid XISO (Xbox ISO) image by checking for
    /// the "MICROSOFT*XBOX*MEDIA" magic at sector 32.
    /// </summary>
    public static bool IsXiso(string imagePath)
    {
        if (!File.Exists(imagePath)) return false;

        try
        {
            using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length < VolumeDescriptorOffset + 20) return false;

            stream.Seek(VolumeDescriptorOffset + MagicOffset, SeekOrigin.Begin);
            var buf = new byte[20];
            if (stream.Read(buf, 0, 20) != 20) return false;

            return MatchesMagic(buf);
        }
        catch { return false; }
    }

    /// <summary>
    /// Reads the XDVDFS volume descriptor and returns root directory information.
    /// </summary>
    /// <returns>Tuple of (rootDirSector, rootDirSize, timestamp) or null if invalid.</returns>
    public static (uint RootSector, uint RootSize, long Timestamp)? ReadVolumeDescriptor(string imagePath)
    {
        if (!File.Exists(imagePath)) return null;

        try
        {
            using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length < VolumeDescriptorOffset + 0x24) return null;

            stream.Seek(VolumeDescriptorOffset, SeekOrigin.Begin);
            var vd = new byte[SectorSize];
            if (stream.Read(vd, 0, Math.Min(SectorSize, (int)(stream.Length - VolumeDescriptorOffset))) < 0x24)
                return null;

            // Verify magic
            var magic = new byte[20];
            Array.Copy(vd, MagicOffset, magic, 0, 20);
            if (!MatchesMagic(magic)) return null;

            uint rootSector = BitConverter.ToUInt32(vd, RootDirSectorOffset);
            uint rootSize = BitConverter.ToUInt32(vd, RootDirSizeOffset);
            long timestamp = BitConverter.ToInt64(vd, TimestampOffset);

            return (rootSector, rootSize, timestamp);
        }
        catch { return null; }
    }

    /// <summary>
    /// Lists all files and directories in an XISO image by reading the XDVDFS directory tree.
    /// </summary>
    public static List<XisoEntry> ListEntries(string imagePath)
    {
        var result = new List<XisoEntry>();
        var vd = ReadVolumeDescriptor(imagePath);
        if (vd == null) return result;

        try
        {
            using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var dirData = new byte[vd.Value.RootSize];
            long dirOffset = (long)vd.Value.RootSector * SectorSize;
            if (dirOffset + vd.Value.RootSize > stream.Length) return result;

            stream.Seek(dirOffset, SeekOrigin.Begin);
            stream.ReadExactly(dirData, 0, (int)vd.Value.RootSize);

            ParseDirectoryTree(stream, dirData, 0, "", result);
        }
        catch { /* best-effort */ }

        return result;
    }

    /// <summary>
    /// Extracts all files from an XISO image to the specified output directory.
    /// </summary>
    /// <returns>Null on success, or an error message string.</returns>
    public static string? Extract(string imagePath, string outputDir)
    {
        if (!File.Exists(imagePath))
            return "XISO file not found.";

        var vd = ReadVolumeDescriptor(imagePath);
        if (vd == null)
            return "Not a valid XISO image.";

        try
        {
            Directory.CreateDirectory(outputDir);

            using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var entries = ListEntries(imagePath);

            foreach (var entry in entries)
            {
                string targetPath = Path.Combine(outputDir, entry.FullPath.TrimStart('/'));

                if (entry.IsDirectory)
                {
                    Directory.CreateDirectory(targetPath);
                }
                else
                {
                    string? dir = Path.GetDirectoryName(targetPath);
                    if (dir != null) Directory.CreateDirectory(dir);

                    long dataOffset = (long)entry.StartSector * SectorSize;
                    if (dataOffset + entry.Size > stream.Length)
                        continue; // Skip truncated files

                    stream.Seek(dataOffset, SeekOrigin.Begin);
                    using var outFile = new FileStream(targetPath, FileMode.Create, FileAccess.Write);

                    // Copy in chunks
                    var buffer = new byte[Math.Min(65536, entry.Size)];
                    long remaining = entry.Size;
                    while (remaining > 0)
                    {
                        int toRead = (int)Math.Min(buffer.Length, remaining);
                        int read = stream.Read(buffer, 0, toRead);
                        if (read <= 0) break;
                        outFile.Write(buffer, 0, read);
                        remaining -= read;
                    }
                }
            }

            return null; // Success
        }
        catch (Exception ex) { return $"XISO extraction failed: {ex.Message}"; }
    }

    /// <summary>
    /// Creates an XISO image from a directory of files.
    /// Builds a valid XDVDFS filesystem with proper volume descriptor and directory tree.
    /// </summary>
    /// <param name="sourceDir">Source directory containing the Xbox game files.</param>
    /// <param name="outputPath">Path for the output XISO file.</param>
    /// <returns>Null on success, or an error message string.</returns>
    public static string? Create(string sourceDir, string outputPath)
    {
        if (!Directory.Exists(sourceDir))
            return "Source directory not found.";

        try
        {
            var allEntries = CollectEntries(sourceDir, "");
            if (allEntries.Count == 0)
                return "Source directory contains no files or subdirectories.";

            using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);

            // Reserve space for system area (sectors 0-31) and volume descriptor (sector 32)
            // Write zeros for sectors 0-31
            var zeroes = new byte[SectorSize];
            for (int i = 0; i < VolumeDescriptorSector; i++)
                stream.Write(zeroes, 0, SectorSize);

            // Write placeholder volume descriptor at sector 32 (will be finalized later)
            var vdPlaceholder = new byte[SectorSize];
            stream.Write(vdPlaceholder, 0, SectorSize);

            // Skip sector 33 (reserved for layout tool signature)
            stream.Write(zeroes, 0, SectorSize);

            // Build directory tree starting at sector 34
            uint currentSector = 34;

            // Build the directory table
            byte[] dirTable = BuildDirectoryTable(allEntries, currentSector);

            // The root directory starts at sector 34
            uint rootDirSector = 34;
            uint rootDirSize = (uint)dirTable.Length;

            // Pad directory table to sector boundary
            int dirTablePadded = ((dirTable.Length + SectorSize - 1) / SectorSize) * SectorSize;
            var dirTableBuf = new byte[dirTablePadded];
            Array.Copy(dirTable, dirTableBuf, dirTable.Length);

            stream.Write(dirTableBuf, 0, dirTablePadded);
            currentSector = 34 + (uint)(dirTablePadded / SectorSize);

            // Write file data
            foreach (var entry in allEntries)
            {
                if (entry.IsDirectory) continue;

                entry.StartSector = currentSector;
                string sourcePath = Path.Combine(sourceDir, entry.FullPath.TrimStart(Path.DirectorySeparatorChar, '/'));
                if (!File.Exists(sourcePath)) continue;

                using var fileStream = File.OpenRead(sourcePath);
                var buffer = new byte[65536];
                int read;
                long written = 0;
                while ((read = fileStream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    stream.Write(buffer, 0, read);
                    written += read;
                }

                // Pad to sector boundary
                int remainder = (int)(written % SectorSize);
                if (remainder > 0)
                {
                    var padding = new byte[SectorSize - remainder];
                    stream.Write(padding, 0, padding.Length);
                }

                currentSector += (uint)((written + SectorSize - 1) / SectorSize);
            }

            // Rebuild directory table with correct file sector offsets
            dirTable = BuildDirectoryTable(allEntries, currentSector);
            dirTableBuf = new byte[dirTablePadded];
            Array.Copy(dirTable, dirTableBuf, Math.Min(dirTable.Length, dirTablePadded));

            // Write finalized directory table at sector 34
            stream.Seek(34L * SectorSize, SeekOrigin.Begin);
            stream.Write(dirTableBuf, 0, dirTablePadded);

            // Write finalized volume descriptor at sector 32
            var vd = new byte[SectorSize];
            // Magic at offset 0
            Array.Copy(XdvdfsMagic, 0, vd, MagicOffset, XdvdfsMagic.Length);
            // Root directory sector (LE32) at offset 0x14
            vd[RootDirSectorOffset] = (byte)(rootDirSector & 0xFF);
            vd[RootDirSectorOffset + 1] = (byte)((rootDirSector >> 8) & 0xFF);
            vd[RootDirSectorOffset + 2] = (byte)((rootDirSector >> 16) & 0xFF);
            vd[RootDirSectorOffset + 3] = (byte)((rootDirSector >> 24) & 0xFF);
            // Root directory size (LE32) at offset 0x18
            vd[RootDirSizeOffset] = (byte)(rootDirSize & 0xFF);
            vd[RootDirSizeOffset + 1] = (byte)((rootDirSize >> 8) & 0xFF);
            vd[RootDirSizeOffset + 2] = (byte)((rootDirSize >> 16) & 0xFF);
            vd[RootDirSizeOffset + 3] = (byte)((rootDirSize >> 24) & 0xFF);
            // Timestamp (LE64) at offset 0x1C
            long timestamp = DateTime.UtcNow.ToFileTimeUtc();
            for (int i = 0; i < 8; i++)
                vd[TimestampOffset + i] = (byte)((timestamp >> (i * 8)) & 0xFF);
            // Second copy of magic at offset 0x7EC
            Array.Copy(XdvdfsMagic, 0, vd, MagicEndOffset, XdvdfsMagic.Length);

            stream.Seek(VolumeDescriptorOffset, SeekOrigin.Begin);
            stream.Write(vd, 0, SectorSize);

            return null; // Success
        }
        catch (Exception ex) { return $"XISO creation failed: {ex.Message}"; }
    }

    /// <summary>
    /// Validates an XISO image by checking the volume descriptor, magic strings,
    /// and directory structure integrity.
    /// </summary>
    /// <returns>List of validation messages (empty = valid).</returns>
    public static List<string> Validate(string imagePath)
    {
        var messages = new List<string>();

        if (!File.Exists(imagePath))
        {
            messages.Add("XISO file not found.");
            return messages;
        }

        var fi = new FileInfo(imagePath);
        if (fi.Length < VolumeDescriptorOffset + SectorSize)
        {
            messages.Add("File too small to be a valid XISO image.");
            return messages;
        }

        try
        {
            using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);

            // Check volume descriptor magic
            stream.Seek(VolumeDescriptorOffset, SeekOrigin.Begin);
            var vd = new byte[SectorSize];
            stream.ReadExactly(vd, 0, SectorSize);

            var magic = new byte[20];
            Array.Copy(vd, MagicOffset, magic, 0, 20);
            if (!MatchesMagic(magic))
                messages.Add("Invalid or missing MICROSOFT*XBOX*MEDIA magic at sector 32.");

            // Check second magic copy
            if (stream.Length >= VolumeDescriptorOffset + MagicEndOffset + 20)
            {
                var magic2 = new byte[20];
                Array.Copy(vd, MagicEndOffset, magic2, 0, 20);
                if (!MatchesMagic(magic2))
                    messages.Add("Warning: Second copy of magic at offset 0x7EC is missing or corrupt.");
            }

            // Read root directory info
            uint rootSector = BitConverter.ToUInt32(vd, RootDirSectorOffset);
            uint rootSize = BitConverter.ToUInt32(vd, RootDirSizeOffset);

            if (rootSector == 0)
                messages.Add("Root directory sector is zero — image may be corrupt.");
            if (rootSize == 0)
                messages.Add("Root directory size is zero — image may be empty or corrupt.");

            long rootOffset = (long)rootSector * SectorSize;
            if (rootOffset + rootSize > stream.Length)
                messages.Add("Root directory extends beyond end of file — image may be truncated.");

            // Check image size against Xbox disc capacities
            if (fi.Length > FormatHelper.DVD9Capacity)
                messages.Add("Warning: Image exceeds DVD-9 capacity.");
        }
        catch (Exception ex) { messages.Add($"Validation error: {ex.Message}"); }

        return messages;
    }

    // =====================================================================
    // Private helper methods
    // =====================================================================

    private static bool MatchesMagic(byte[] buf)
    {
        if (buf.Length < XdvdfsMagic.Length) return false;
        for (int i = 0; i < XdvdfsMagic.Length; i++)
            if (buf[i] != XdvdfsMagic[i]) return false;
        return true;
    }

    /// <summary>Parses the XDVDFS directory tree recursively from a directory table buffer.</summary>
    private static void ParseDirectoryTree(FileStream stream, byte[] dirData, int entryOffset,
        string parentPath, List<XisoEntry> result)
    {
        if (entryOffset < 0 || entryOffset + 14 > dirData.Length)
            return;

        // Read left/right subtree offsets (LE16, in 4-byte units; 0xFFFF = none)
        ushort leftOffset = (ushort)(dirData[entryOffset] | (dirData[entryOffset + 1] << 8));
        ushort rightOffset = (ushort)(dirData[entryOffset + 2] | (dirData[entryOffset + 3] << 8));

        // File start sector (LE32)
        uint startSector = BitConverter.ToUInt32(dirData, entryOffset + 4);
        // File size (LE32)
        uint fileSize = BitConverter.ToUInt32(dirData, entryOffset + 8);
        // Attributes
        byte attributes = dirData[entryOffset + 12];
        // Name length
        byte nameLen = dirData[entryOffset + 13];

        if (nameLen == 0 || entryOffset + 14 + nameLen > dirData.Length)
            return;

        string name = Encoding.ASCII.GetString(dirData, entryOffset + 14, nameLen);
        bool isDir = (attributes & DirEntryAttributeDirectory) != 0;
        string fullPath = parentPath + "/" + name;

        var entry = new XisoEntry
        {
            Name = name,
            FullPath = fullPath,
            StartSector = startSector,
            Size = fileSize,
            IsDirectory = isDir
        };
        result.Add(entry);

        // If directory, recursively parse its children
        if (isDir && fileSize > 0)
        {
            long childDirOffset = (long)startSector * SectorSize;
            if (childDirOffset + fileSize <= stream.Length)
            {
                var childDirData = new byte[fileSize];
                long savedPos = stream.Position;
                stream.Seek(childDirOffset, SeekOrigin.Begin);
                stream.ReadExactly(childDirData, 0, (int)fileSize);
                stream.Seek(savedPos, SeekOrigin.Begin);

                ParseDirectoryTree(stream, childDirData, 0, fullPath, entry.Children);
                result.AddRange(entry.Children);
            }
        }

        // Traverse left subtree
        if (leftOffset != 0xFFFF)
        {
            int leftByteOffset = leftOffset * 4;
            if (leftByteOffset < dirData.Length)
                ParseDirectoryTree(stream, dirData, leftByteOffset, parentPath, result);
        }

        // Traverse right subtree
        if (rightOffset != 0xFFFF)
        {
            int rightByteOffset = rightOffset * 4;
            if (rightByteOffset < dirData.Length)
                ParseDirectoryTree(stream, dirData, rightByteOffset, parentPath, result);
        }
    }

    /// <summary>Collects file/directory entries from a source directory for XISO creation.</summary>
    private static List<XisoEntry> CollectEntries(string baseDir, string relativePath)
    {
        var entries = new List<XisoEntry>();
        string fullDir = string.IsNullOrEmpty(relativePath)
            ? baseDir
            : Path.Combine(baseDir, relativePath);

        foreach (var dirPath in Directory.GetDirectories(fullDir))
        {
            string dirName = Path.GetFileName(dirPath);
            string relPath = string.IsNullOrEmpty(relativePath)
                ? dirName : relativePath + "/" + dirName;

            var dirEntry = new XisoEntry
            {
                Name = dirName,
                FullPath = "/" + relPath,
                IsDirectory = true
            };
            entries.Add(dirEntry);

            // Recursively collect children
            var children = CollectEntries(baseDir, relPath);
            dirEntry.Children.AddRange(children);
            entries.AddRange(children);
        }

        foreach (var filePath in Directory.GetFiles(fullDir))
        {
            string fileName = Path.GetFileName(filePath);
            string relPath = string.IsNullOrEmpty(relativePath)
                ? fileName : relativePath + "/" + fileName;
            var fileInfo = new FileInfo(filePath);

            entries.Add(new XisoEntry
            {
                Name = fileName,
                FullPath = "/" + relPath,
                Size = (uint)Math.Min(fileInfo.Length, uint.MaxValue),
                IsDirectory = false
            });
        }

        return entries;
    }

    /// <summary>
    /// Builds a binary directory table from a list of entries.
    /// Each entry is 14 + name_length bytes, padded to 4-byte alignment.
    /// </summary>
    private static byte[] BuildDirectoryTable(List<XisoEntry> entries, uint currentSector)
    {
        // Only include root-level entries (files and dirs, not recursively nested ones)
        var rootEntries = entries.Where(e =>
            e.FullPath.Count(c => c == '/') == 1 && !string.IsNullOrEmpty(e.Name)).ToList();

        if (rootEntries.Count == 0)
            return Array.Empty<byte>();

        // Calculate total size needed
        int totalSize = 0;
        foreach (var entry in rootEntries)
        {
            int entrySize = 14 + entry.Name.Length;
            // Pad to 4-byte boundary
            entrySize = (entrySize + 3) & ~3;
            totalSize += entrySize;
        }

        var table = new byte[totalSize];
        int offset = 0;

        for (int i = 0; i < rootEntries.Count; i++)
        {
            var entry = rootEntries[i];
            int entrySize = 14 + entry.Name.Length;
            entrySize = (entrySize + 3) & ~3;

            // Left/right subtree (0xFFFF = none for simple flat layout)
            table[offset] = 0xFF; table[offset + 1] = 0xFF; // Left
            // Right subtree points to next entry (in 4-byte units)
            if (i < rootEntries.Count - 1)
            {
                int nextOffset = offset + entrySize;
                ushort rightPtr = (ushort)(nextOffset / 4);
                table[offset + 2] = (byte)(rightPtr & 0xFF);
                table[offset + 3] = (byte)((rightPtr >> 8) & 0xFF);
            }
            else
            {
                table[offset + 2] = 0xFF; table[offset + 3] = 0xFF; // No right
            }

            // Start sector (LE32)
            uint sector = entry.StartSector;
            table[offset + 4] = (byte)(sector & 0xFF);
            table[offset + 5] = (byte)((sector >> 8) & 0xFF);
            table[offset + 6] = (byte)((sector >> 16) & 0xFF);
            table[offset + 7] = (byte)((sector >> 24) & 0xFF);

            // File size (LE32)
            table[offset + 8] = (byte)(entry.Size & 0xFF);
            table[offset + 9] = (byte)((entry.Size >> 8) & 0xFF);
            table[offset + 10] = (byte)((entry.Size >> 16) & 0xFF);
            table[offset + 11] = (byte)((entry.Size >> 24) & 0xFF);

            // Attributes
            table[offset + 12] = entry.IsDirectory ? DirEntryAttributeDirectory : (byte)0x80;

            // Name length
            table[offset + 13] = (byte)entry.Name.Length;

            // Name
            Encoding.ASCII.GetBytes(entry.Name, 0, entry.Name.Length, table, offset + 14);

            offset += entrySize;
        }

        return table;
    }
}
