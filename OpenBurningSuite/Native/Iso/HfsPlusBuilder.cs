// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OpenBurningSuite.Native.Iso;

/// <summary>
/// Post-processes an ISO 9660 image to add HFS+ hybrid filesystem structures,
/// creating an Apple-compatible hybrid disc image that can be read on both
/// ISO 9660 and HFS+ systems (macOS, classic Mac OS 8.1+).
///
/// Layout overview:
///   Sector 0:     Driver Descriptor Record (DDR)
///   Sectors 1–N:  Apple Partition Map (APM) entries
///   Sector 16+:   ISO 9660 descriptors (untouched)
///   After ISO:    HFS+ volume (Volume Header, Allocation File, Extents Overflow,
///                 Catalog B-Tree, embedded file data reusing ISO extents)
///
/// References:
///   - Apple Technical Note TN1150: "HFS Plus Volume Format"
///   - Inside Macintosh: Devices — "SCSI Manager 4.3" (Apple Partition Map)
///   - ECMA-119 (ISO 9660) system area usage (sectors 0–15)
/// </summary>
public static class HfsPlusBuilder
{
    // -----------------------------------------------------------------------
    //  Constants
    // -----------------------------------------------------------------------

    /// <summary>Apple Partition Map magic signature "PM" (big-endian 0x504D).</summary>
    private const ushort ApmSignature = 0x504D;

    /// <summary>Driver Descriptor Record magic signature "ER" (big-endian 0x4552).</summary>
    private const ushort DdrSignature = 0x4552;

    /// <summary>HFS+ Volume Header signature "H+" (big-endian 0x482B).</summary>
    private const ushort HfsPlusSignature = 0x482B;

    /// <summary>HFS+ version 4 (Mac OS X).</summary>
    private const ushort HfsPlusVersion = 4;

    /// <summary>Allocation block size for HFS+ (4096 bytes = 2 sectors).</summary>
    private const uint AllocationBlockSize = 4096;

    /// <summary>B-tree node size for the Catalog File (4096 bytes per TN1150).</summary>
    private const ushort CatalogNodeSize = 4096;

    /// <summary>B-tree node size for the Extents Overflow File (4096 bytes).</summary>
    private const ushort ExtentsNodeSize = 4096;

    /// <summary>B-tree node size for the Attributes File (4096 bytes).</summary>
    private const ushort AttributesNodeSize = 4096;

    /// <summary>HFS+ Catalog Node ID for the root folder.</summary>
    private const uint RootFolderCnid = 2;

    /// <summary>HFS+ Catalog Node ID for the extents overflow file.</summary>
    private const uint ExtentsFileCnid = 3;

    /// <summary>HFS+ Catalog Node ID for the catalog file.</summary>
    private const uint CatalogFileCnid = 4;

    /// <summary>HFS+ Catalog Node ID for the allocation file.</summary>
    private const uint AllocationFileCnid = 6;

    /// <summary>HFS+ Catalog Node ID for the attributes file.</summary>
    private const uint AttributesFileCnid = 8;

    /// <summary>First assignable Catalog Node ID for user files/folders.</summary>
    private const uint FirstUserCnid = 16;

    // Catalog record types (per TN1150 §3.2)
    private const ushort RecordTypeFolder = 0x0001;
    private const ushort RecordTypeFile = 0x0002;
    private const ushort RecordTypeFolderThread = 0x0003;
    private const ushort RecordTypeFileThread = 0x0004;

    /// <summary>Sector size in bytes.</summary>
    private const int SectorSize = 2048;

    /// <summary>
    /// Maximum directory extent size to read during ISO tree parsing (10 MB).
    /// Protects against excessive memory allocation from malformed images.
    /// </summary>
    private const int MaxDirectorySizeBytes = 10_000_000;

    // HFS+ epoch: 1904-01-01 00:00:00 UTC
    private static readonly DateTime HfsEpoch = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // -----------------------------------------------------------------------
    //  Public API
    // -----------------------------------------------------------------------

    /// <summary>
    /// Adds HFS+ hybrid structures to an existing ISO 9660 image file.
    /// The ISO system area (sectors 0–15) is used for the Apple Partition Map.
    /// HFS+ metadata is appended after the existing ISO data.
    /// </summary>
    /// <param name="isoPath">Path to the ISO 9660 image to modify.</param>
    /// <param name="volumeLabel">Volume label for the HFS+ volume.</param>
    /// <param name="sourceFolder">
    /// Source folder from which files were added to the ISO. Used to build the
    /// HFS+ catalog by enumerating the same files. If null or empty, the catalog
    /// will contain only the root folder.
    /// </param>
    /// <returns>List of log messages describing the operations performed.</returns>
    public static List<string> AddHfsPlusHybrid(string isoPath, string volumeLabel,
        string? sourceFolder)
    {
        var log = new List<string>();

        using var stream = new FileStream(isoPath, FileMode.Open, FileAccess.ReadWrite);

        // Read the ISO PVD to find file extents for cross-referencing
        var isoFiles = ParseIsoDirectoryTree(stream);
        log.Add($"[Info] ISO 9660 file tree parsed: {isoFiles.Count} entries");

        // Calculate layout positions
        long isoEndOffset = stream.Length;
        // Align HFS+ volume start to allocation block boundary
        long hfsPlusStartOffset = AlignUp(isoEndOffset, AllocationBlockSize);
        uint hfsPlusStartBlock = (uint)(hfsPlusStartOffset / AllocationBlockSize);

        // Truncate volume label to HFS+ maximum (255 Unicode characters per TN1150 §3.2)
        if (volumeLabel.Length > 255)
            volumeLabel = volumeLabel[..255];

        // Build catalog entries from source folder or ISO file tree
        var catalogEntries = BuildCatalogEntries(sourceFolder, isoFiles, volumeLabel);
        log.Add($"[Info] HFS+ catalog: {catalogEntries.Count} entries " +
                $"({catalogEntries.FindAll(e => e.IsFolder).Count} folders, " +
                $"{catalogEntries.FindAll(e => !e.IsFolder).Count} files)");

        // Build the catalog B-tree
        var catalogData = BuildCatalogBTree(catalogEntries);
        uint catalogBlocks = (uint)((catalogData.Length + AllocationBlockSize - 1) / AllocationBlockSize);

        // Build the extents overflow B-tree (empty but valid)
        var extentsData = BuildExtentsOverflowBTree();
        uint extentsBlocks = (uint)((extentsData.Length + AllocationBlockSize - 1) / AllocationBlockSize);

        // Build the attributes B-tree (empty but valid)
        var attributesData = BuildAttributesBTree();
        uint attributesBlocks = (uint)((attributesData.Length + AllocationBlockSize - 1) / AllocationBlockSize);

        // Calculate total allocation blocks for the HFS+ volume
        // Layout: [Extents] [Catalog] [Attributes] [Allocation Bitmap]
        // The allocation bitmap comes last and its size depends on total blocks
        uint metadataBlocks = extentsBlocks + catalogBlocks + attributesBlocks;
        // Reserve blocks for Volume Header (block 0 is VH) + alternate VH (last block)
        // Plus allocation bitmap (1 bit per block, rounded up to AllocationBlockSize)
        // Estimate total blocks first, then refine
        uint totalEstimatedBlocks = hfsPlusStartBlock + metadataBlocks + 2 /* VH + alt VH */;
        uint bitmapBytes = (totalEstimatedBlocks + 7) / 8;
        uint bitmapBlocks = (bitmapBytes + AllocationBlockSize - 1) / AllocationBlockSize;
        uint totalBlocks = hfsPlusStartBlock + metadataBlocks + bitmapBlocks + 2;

        // Recalculate bitmap with final total
        bitmapBytes = (totalBlocks + 7) / 8;
        bitmapBlocks = (bitmapBytes + AllocationBlockSize - 1) / AllocationBlockSize;
        totalBlocks = hfsPlusStartBlock + metadataBlocks + bitmapBlocks + 2;

        // Block allocation map (within HFS+ region):
        //   Block hfsPlusStartBlock + 0: reserved (VH is at +1024 bytes from volume start)
        //   Block hfsPlusStartBlock + 1: Extents Overflow start
        uint extentsStartBlock = hfsPlusStartBlock + 1;
        uint catalogStartBlock = extentsStartBlock + extentsBlocks;
        uint attributesStartBlock = catalogStartBlock + catalogBlocks;
        uint bitmapStartBlock = attributesStartBlock + attributesBlocks;

        // Build allocation bitmap
        var bitmapData = BuildAllocationBitmap(totalBlocks, hfsPlusStartBlock,
            extentsStartBlock, extentsBlocks,
            catalogStartBlock, catalogBlocks,
            attributesStartBlock, attributesBlocks,
            bitmapStartBlock, bitmapBlocks);

        // Build the HFS+ Volume Header
        uint freeBlocks = totalBlocks - hfsPlusStartBlock - metadataBlocks - bitmapBlocks - 2;
        var volumeHeader = BuildVolumeHeader(
            volumeLabel, totalBlocks, freeBlocks,
            extentsStartBlock, extentsBlocks, (uint)extentsData.Length,
            catalogStartBlock, catalogBlocks, (uint)catalogData.Length,
            AllocationFileCnid, bitmapStartBlock, bitmapBlocks, (uint)bitmapData.Length,
            attributesStartBlock, attributesBlocks, (uint)attributesData.Length,
            (uint)catalogEntries.FindAll(e => !e.IsFolder).Count,
            (uint)catalogEntries.FindAll(e => e.IsFolder).Count);

        // Write Apple Partition Map into ISO system area (sectors 0–15)
        WriteApplePartitionMap(stream, totalBlocks, hfsPlusStartBlock);
        log.Add("[Info] Apple Partition Map written to ISO system area (sectors 0–15)");

        // Extend the file and write HFS+ structures
        long volumeHeaderOffset = hfsPlusStartOffset + 1024; // VH at +1024 from volume start
        long extentsOffset = (long)extentsStartBlock * AllocationBlockSize;
        long catalogOffset = (long)catalogStartBlock * AllocationBlockSize;
        long attributesOffset = (long)attributesStartBlock * AllocationBlockSize;
        long bitmapOffset = (long)bitmapStartBlock * AllocationBlockSize;
        // Alternate VH is at 1024 bytes before the end of the volume
        // (second-to-last 512-byte sector per TN1150 §3)
        long altVhOffset = (long)totalBlocks * AllocationBlockSize - 1024;

        // Ensure file is large enough
        long requiredSize = (long)totalBlocks * AllocationBlockSize;
        if (stream.Length < requiredSize)
            stream.SetLength(requiredSize);

        // Write Volume Header at offset +1024 from volume start
        stream.Seek(volumeHeaderOffset, SeekOrigin.Begin);
        stream.Write(volumeHeader, 0, volumeHeader.Length);
        log.Add($"[Info] HFS+ Volume Header written at offset 0x{volumeHeaderOffset:X}");

        // Write Extents Overflow B-tree
        stream.Seek(extentsOffset, SeekOrigin.Begin);
        stream.Write(extentsData, 0, extentsData.Length);

        // Write Catalog B-tree
        stream.Seek(catalogOffset, SeekOrigin.Begin);
        stream.Write(catalogData, 0, catalogData.Length);

        // Write Attributes B-tree
        stream.Seek(attributesOffset, SeekOrigin.Begin);
        stream.Write(attributesData, 0, attributesData.Length);

        // Write Allocation Bitmap
        stream.Seek(bitmapOffset, SeekOrigin.Begin);
        stream.Write(bitmapData, 0, bitmapData.Length);

        // Write Alternate Volume Header at second-to-last sector
        stream.Seek(altVhOffset, SeekOrigin.Begin);
        stream.Write(volumeHeader, 0, volumeHeader.Length);
        log.Add("[Info] Alternate Volume Header written");

        log.Add($"[Info] HFS+ hybrid complete: {totalBlocks} allocation blocks, " +
                $"{catalogEntries.Count} catalog entries");

        return log;
    }

    // -----------------------------------------------------------------------
    //  Apple Partition Map
    // -----------------------------------------------------------------------

    /// <summary>
    /// Writes the Driver Descriptor Record (sector 0) and Apple Partition Map
    /// entries (sectors 1–N) into the ISO 9660 system area.
    /// The system area (sectors 0–15) is unused by ISO 9660 and available for
    /// platform-specific data per ECMA-119 §6.2.1.
    /// </summary>
    private static void WriteApplePartitionMap(Stream stream, uint totalBlocks,
        uint hfsPlusStartBlock)
    {
        // Sector 0: Driver Descriptor Record (DDR)
        var ddr = new byte[SectorSize];
        WriteBE16(ddr, 0, DdrSignature);          // sbSig = 0x4552 ("ER")
        WriteBE16(ddr, 2, 512);                    // sbBlkSize = 512 (per Inside Macintosh: Devices)
        // sbBlkCount in sbBlkSize (512-byte) units: each 4096-byte allocation block = 8 × 512
        WriteBE32(ddr, 4, totalBlocks * (AllocationBlockSize / 512)); // sbBlkCount
        WriteBE16(ddr, 8, 0);                      // sbDevType
        WriteBE16(ddr, 10, 0);                     // sbDevId
        WriteBE32(ddr, 12, 0);                     // sbData
        WriteBE16(ddr, 16, 0);                     // sbDrvrCount (no drivers)

        stream.Seek(0, SeekOrigin.Begin);
        stream.Write(ddr, 0, ddr.Length);

        // Partition Map entries (sectors 1–4)
        // APM block units match the DDR sbBlkSize (512 bytes).
        // Each 2048-byte ISO sector = 4 × 512 blocks.
        // Each 4096-byte HFS+ allocation block = 8 × 512 blocks.
        const uint blocksPerSector = SectorSize / 512;         // 4
        const uint blocksPerAllocBlock = AllocationBlockSize / 512; // 8

        // Entry 1: The partition map itself (sectors 1–4 = blocks 4–19)
        var pmSelf = new byte[SectorSize];
        WritePartitionMapEntry(pmSelf, 4, 1 * blocksPerSector, 4 * blocksPerSector,
            "Apple_partition_map", "Apple");

        // Entry 2: ISO 9660 partition (whole volume for ISO readability)
        var pmIso = new byte[SectorSize];
        WritePartitionMapEntry(pmIso, 4, 0, hfsPlusStartBlock * blocksPerAllocBlock,
            "Apple_ISO", "ISO 9660");

        // Entry 3: HFS+ partition (from HFS+ start to end)
        var pmHfs = new byte[SectorSize];
        uint hfsPlusBlocks = totalBlocks - hfsPlusStartBlock;
        WritePartitionMapEntry(pmHfs, 4,
            hfsPlusStartBlock * blocksPerAllocBlock,
            hfsPlusBlocks * blocksPerAllocBlock,
            "Apple_HFS", "HFS+");

        // Entry 4: Free/unused space entry (required to fill map count)
        var pmFree = new byte[SectorSize];
        WritePartitionMapEntry(pmFree, 4, 0, 0, "Apple_Free", "");

        stream.Seek(SectorSize, SeekOrigin.Begin);      // Sector 1
        stream.Write(pmSelf, 0, pmSelf.Length);
        stream.Seek(SectorSize * 2, SeekOrigin.Begin);  // Sector 2
        stream.Write(pmIso, 0, pmIso.Length);
        stream.Seek(SectorSize * 3, SeekOrigin.Begin);  // Sector 3
        stream.Write(pmHfs, 0, pmHfs.Length);
        stream.Seek(SectorSize * 4, SeekOrigin.Begin);  // Sector 4
        stream.Write(pmFree, 0, pmFree.Length);
    }

    /// <summary>Writes a single Apple Partition Map entry.</summary>
    private static void WritePartitionMapEntry(byte[] buf, int totalEntries,
        uint startBlock, uint blockCount, string type, string name)
    {
        WriteBE16(buf, 0, ApmSignature);                 // pmSig = "PM"
        WriteBE16(buf, 2, 0);                            // pmSigPad
        WriteBE32(buf, 4, (uint)totalEntries);           // pmMapEntries
        WriteBE32(buf, 8, startBlock);                   // pmPyPartStart
        WriteBE32(buf, 12, blockCount);                  // pmPartBlkCnt
        WriteAsciiPadded(buf, 16, 32, type);             // pmPartType
        WriteAsciiPadded(buf, 48, 32, name);             // pmPartName
        WriteBE32(buf, 80, 0);                           // pmLgDataStart
        WriteBE32(buf, 84, blockCount);                  // pmDataCnt
        WriteBE32(buf, 88, 0x40000077);                  // pmPartStatus (valid, allocated, readable, writable)
    }

    // -----------------------------------------------------------------------
    //  HFS+ Volume Header
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds a 512-byte HFS+ Volume Header per TN1150 §3.
    /// </summary>
    private static byte[] BuildVolumeHeader(
        string volumeLabel, uint totalBlocks, uint freeBlocks,
        uint extentsStartBlock, uint extentsBlockCount, uint extentsLogicalSize,
        uint catalogStartBlock, uint catalogBlockCount, uint catalogLogicalSize,
        uint allocationFileCnid, uint bitmapStartBlock, uint bitmapBlockCount, uint bitmapLogicalSize,
        uint attributesStartBlock, uint attributesBlockCount, uint attributesLogicalSize,
        uint fileCount, uint folderCount)
    {
        var vh = new byte[512];

        uint now = ToHfsTimestamp(DateTime.UtcNow);

        WriteBE16(vh, 0, HfsPlusSignature);          // signature "H+"
        WriteBE16(vh, 2, HfsPlusVersion);             // version
        WriteBE32(vh, 4, 0x00000100);                 // attributes: kHFSVolumeUnmountedBit
        WriteBE32(vh, 8, 0x31302E30);                 // lastMountedVersion "10.0"
        WriteBE32(vh, 12, 0);                         // journalInfoBlock (no journal)

        WriteBE32(vh, 16, now);                       // createDate
        WriteBE32(vh, 20, now);                       // modifyDate
        WriteBE32(vh, 24, 0);                         // backupDate
        WriteBE32(vh, 28, now);                       // checkedDate

        WriteBE32(vh, 32, fileCount);                 // fileCount
        WriteBE32(vh, 36, folderCount);               // folderCount

        WriteBE32(vh, 40, AllocationBlockSize);       // blockSize
        WriteBE32(vh, 44, totalBlocks);               // totalBlocks
        WriteBE32(vh, 48, freeBlocks);                // freeBlocks
        WriteBE32(vh, 52, FirstUserCnid);             // nextAllocation
        WriteBE32(vh, 56, 0x00010000);                // rsrcClumpSize (64 KB)
        WriteBE32(vh, 60, 0x00010000);                // dataClumpSize (64 KB)
        WriteBE32(vh, 64, FirstUserCnid +
            (uint)fileCount + (uint)folderCount);     // nextCatalogID

        WriteBE32(vh, 68, 0);                         // writeCount
        WriteBE64(vh, 72, 0);                         // encodingsBitmap

        // finderInfo[0..31]: 32 bytes of Finder info (all zeros for data disc)

        // Allocation File fork descriptor at offset 112
        WriteForkData(vh, 112, bitmapLogicalSize,
            bitmapBlockCount, bitmapStartBlock, bitmapBlockCount);

        // Extents Overflow File fork descriptor at offset 192
        WriteForkData(vh, 192, extentsLogicalSize,
            extentsBlockCount, extentsStartBlock, extentsBlockCount);

        // Catalog File fork descriptor at offset 272
        WriteForkData(vh, 272, catalogLogicalSize,
            catalogBlockCount, catalogStartBlock, catalogBlockCount);

        // Attributes File fork descriptor at offset 352
        WriteForkData(vh, 352, attributesLogicalSize,
            attributesBlockCount, attributesStartBlock, attributesBlockCount);

        // Startup File fork descriptor at offset 432 (unused, all zeros)

        return vh;
    }

    /// <summary>Writes an HFS+ fork data descriptor (80 bytes) at the given offset.</summary>
    private static void WriteForkData(byte[] buf, int offset, uint logicalSize,
        uint totalBlocks, uint extentStartBlock, uint extentBlockCount)
    {
        WriteBE64(buf, offset, logicalSize);              // logicalSize
        WriteBE32(buf, offset + 8, 0);                    // clumpSize
        WriteBE32(buf, offset + 12, totalBlocks);         // totalBlocks
        // Extent 0 (first extent descriptor, 8 bytes each)
        WriteBE32(buf, offset + 16, extentStartBlock);    // startBlock
        WriteBE32(buf, offset + 20, extentBlockCount);    // blockCount
        // Extents 1–7 are zero (no additional extents needed)
    }

    // -----------------------------------------------------------------------
    //  Catalog B-Tree
    // -----------------------------------------------------------------------

    /// <summary>
    /// Represents a file or folder entry for the HFS+ catalog.
    /// </summary>
    private sealed class CatalogEntry
    {
        public uint Cnid;
        public uint ParentCnid;
        public string Name = string.Empty;
        public bool IsFolder;
        public uint DataStartBlock;
        public uint DataBlockCount;
        public long DataLogicalSize;
    }

    /// <summary>
    /// Builds catalog entries from the source folder tree, mapping ISO file extents
    /// so that HFS+ file records point to the same disc sectors as their ISO counterparts.
    /// </summary>
    private static List<CatalogEntry> BuildCatalogEntries(
        string? sourceFolder, Dictionary<string, IsoFileExtent> isoFiles, string volumeLabel)
    {
        var entries = new List<CatalogEntry>();
        uint nextCnid = FirstUserCnid;

        // Root folder entry
        entries.Add(new CatalogEntry
        {
            Cnid = RootFolderCnid,
            ParentCnid = 1, // Parent of root is root-parent CNID 1
            Name = volumeLabel.Length > 0 ? volumeLabel : "UNTITLED",
            IsFolder = true
        });

        if (string.IsNullOrEmpty(sourceFolder) || !Directory.Exists(sourceFolder))
            return entries;

        var basePath = sourceFolder.TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);

        // Map from relative path → CNID for parent lookup
        var pathToCnid = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
        {
            [""] = RootFolderCnid
        };

        // Enumerate directories first
        try
        {
            foreach (var dir in Directory.GetDirectories(basePath, "*", SearchOption.AllDirectories))
            {
                var relative = GetRelativePath(basePath, dir);
                var parentRelative = GetParentRelative(relative);
                uint parentCnid = pathToCnid.GetValueOrDefault(parentRelative, RootFolderCnid);

                var cnid = nextCnid++;
                pathToCnid[relative] = cnid;

                entries.Add(new CatalogEntry
                {
                    Cnid = cnid,
                    ParentCnid = parentCnid,
                    Name = Path.GetFileName(dir),
                    IsFolder = true
                });
            }
        }
        catch { /* best effort */ }

        // Enumerate files
        try
        {
            foreach (var file in Directory.GetFiles(basePath, "*", SearchOption.AllDirectories))
            {
                var relative = GetRelativePath(basePath, file);
                var parentRelative = GetParentRelative(relative);
                uint parentCnid = pathToCnid.GetValueOrDefault(parentRelative, RootFolderCnid);
                var fileName = Path.GetFileName(file);

                // Look up ISO extent for this file
                var isoKey = relative.Replace(Path.DirectorySeparatorChar, '\\')
                    .Replace(Path.AltDirectorySeparatorChar, '\\');
                if (isoKey.StartsWith('\\')) isoKey = isoKey[1..];

                uint dataStartBlock = 0;
                uint dataBlockCount = 0;
                long dataLogicalSize = 0;

                if (isoFiles.TryGetValue(isoKey, out var extent))
                {
                    // Convert ISO sector LBA to HFS+ allocation block
                    dataStartBlock = (uint)((long)extent.Lba * SectorSize / AllocationBlockSize);
                    dataLogicalSize = extent.DataLength;
                    dataBlockCount = (uint)((dataLogicalSize + AllocationBlockSize - 1) / AllocationBlockSize);
                }
                else
                {
                    // File not found in ISO tree — use file size for metadata
                    try { dataLogicalSize = new FileInfo(file).Length; } catch { /* ignore */ }
                }

                entries.Add(new CatalogEntry
                {
                    Cnid = nextCnid++,
                    ParentCnid = parentCnid,
                    Name = fileName,
                    IsFolder = false,
                    DataStartBlock = dataStartBlock,
                    DataBlockCount = dataBlockCount,
                    DataLogicalSize = dataLogicalSize
                });
            }
        }
        catch { /* best effort */ }

        return entries;
    }

    /// <summary>
    /// Builds the HFS+ Catalog B-tree containing all file and folder records.
    /// The catalog uses a B-tree with 4096-byte nodes. Each leaf node contains
    /// both a catalog record (folder/file) and its thread record.
    /// </summary>
    private static byte[] BuildCatalogBTree(List<CatalogEntry> entries)
    {
        // Build all key-record pairs for the B-tree
        var records = new List<(byte[] Key, byte[] Data)>();
        uint now = ToHfsTimestamp(DateTime.UtcNow);

        foreach (var entry in entries)
        {
            // Catalog record (keyed by parentCnid + name)
            var catalogKey = BuildCatalogKey(entry.ParentCnid, entry.Name);
            byte[] catalogData;

            if (entry.IsFolder)
            {
                catalogData = BuildFolderRecord(entry.Cnid, now);
            }
            else
            {
                catalogData = BuildFileRecord(entry.Cnid, now,
                    entry.DataLogicalSize, entry.DataStartBlock, entry.DataBlockCount);
            }
            records.Add((catalogKey, catalogData));

            // Thread record (keyed by CNID + empty name)
            var threadKey = BuildCatalogKey(entry.Cnid, "");
            var threadData = BuildThreadRecord(entry.IsFolder, entry.ParentCnid, entry.Name);
            records.Add((threadKey, threadData));
        }

        // Sort records by key (parent CNID first, then Unicode name)
        records.Sort((a, b) => CompareCatalogKeys(a.Key, b.Key));

        // Pack records into B-tree leaf nodes
        return PackBTree(records, CatalogNodeSize, kBTreeTypeCatalog);
    }

    /// <summary>Builds an HFS+ catalog key (parentID + Unicode name).</summary>
    private static byte[] BuildCatalogKey(uint parentCnid, string name)
    {
        // HFS+ catalog key: keyLength (2) + parentID (4) + nodeName (2 + 2*len)
        var unicodeName = name.Length > 0 ? Encoding.BigEndianUnicode.GetBytes(name) : Array.Empty<byte>();
        ushort nameLen = (ushort)(unicodeName.Length / 2);
        ushort keyLength = (ushort)(6 + 2 + unicodeName.Length); // parentID + nameLen + nameChars

        var key = new byte[2 + keyLength];
        WriteBE16(key, 0, keyLength);
        WriteBE32(key, 2, parentCnid);
        WriteBE16(key, 6, nameLen);
        if (unicodeName.Length > 0)
            Array.Copy(unicodeName, 0, key, 8, unicodeName.Length);

        return key;
    }

    /// <summary>Builds an HFS+ catalog folder record (88 bytes).</summary>
    private static byte[] BuildFolderRecord(uint cnid, uint timestamp)
    {
        var rec = new byte[88];
        WriteBE16(rec, 0, RecordTypeFolder);     // recordType
        WriteBE16(rec, 2, 0);                     // flags
        WriteBE32(rec, 4, 0);                     // valence (child count, updated below is optional)
        WriteBE32(rec, 8, cnid);                  // folderID
        WriteBE32(rec, 12, timestamp);            // createDate
        WriteBE32(rec, 16, timestamp);            // contentModDate
        WriteBE32(rec, 20, timestamp);            // attributeModDate
        WriteBE32(rec, 24, timestamp);            // accessDate
        WriteBE32(rec, 28, 0);                    // backupDate
        // Permissions (16 bytes at offset 32): ownerID, groupID, adminFlags, ownerFlags, fileMode
        WriteBE32(rec, 32, 0);                    // ownerID = 0 (root)
        WriteBE32(rec, 36, 0);                    // groupID = 0
        WriteBE32(rec, 44, 0x41ED);               // fileMode = drwxr-xr-x (0755 + S_IFDIR)
        // userInfo (16 bytes at offset 48) and finderInfo (16 bytes at offset 64) = zeros
        WriteBE32(rec, 80, 0);                    // textEncoding
        WriteBE32(rec, 84, 0);                    // folderCount (subfolder count)
        return rec;
    }

    /// <summary>Builds an HFS+ catalog file record (248 bytes).</summary>
    private static byte[] BuildFileRecord(uint cnid, uint timestamp,
        long dataLogicalSize, uint dataStartBlock, uint dataBlockCount)
    {
        var rec = new byte[248];
        WriteBE16(rec, 0, RecordTypeFile);       // recordType
        WriteBE16(rec, 2, 0);                     // flags
        WriteBE32(rec, 4, 0);                     // reserved
        WriteBE32(rec, 8, cnid);                  // fileID
        WriteBE32(rec, 12, timestamp);            // createDate
        WriteBE32(rec, 16, timestamp);            // contentModDate
        WriteBE32(rec, 20, timestamp);            // attributeModDate
        WriteBE32(rec, 24, timestamp);            // accessDate
        WriteBE32(rec, 28, 0);                    // backupDate
        // Permissions (16 bytes at offset 32)
        WriteBE32(rec, 32, 0);                    // ownerID
        WriteBE32(rec, 36, 0);                    // groupID
        WriteBE32(rec, 44, 0x81A4);               // fileMode = -rw-r--r-- (0644 + S_IFREG)
        // userInfo (16 bytes at offset 48) = zeros
        // finderInfo (16 bytes at offset 64) = zeros
        WriteBE32(rec, 80, 0);                    // textEncoding

        // Data fork (80 bytes starting at offset 88)
        WriteBE64(rec, 88, (ulong)dataLogicalSize);  // logicalSize
        WriteBE32(rec, 96, 0);                         // clumpSize
        WriteBE32(rec, 100, dataBlockCount);            // totalBlocks
        // Extent 0
        WriteBE32(rec, 104, dataStartBlock);           // startBlock
        WriteBE32(rec, 108, dataBlockCount);           // blockCount
        // Extents 1–7 = zeros

        // Resource fork (80 bytes starting at offset 168) = all zeros (no resource fork)

        return rec;
    }

    /// <summary>Builds an HFS+ catalog thread record.</summary>
    private static byte[] BuildThreadRecord(bool isFolder, uint parentCnid, string name)
    {
        var unicodeName = name.Length > 0 ? Encoding.BigEndianUnicode.GetBytes(name) : Array.Empty<byte>();
        ushort nameLen = (ushort)(unicodeName.Length / 2);

        // Thread record: type (2) + reserved (2) + parentID (4) + nameLen (2) + nameChars
        int recLen = 10 + unicodeName.Length;
        var rec = new byte[recLen];

        WriteBE16(rec, 0, isFolder ? RecordTypeFolderThread : RecordTypeFileThread);
        WriteBE16(rec, 2, 0);                      // reserved
        WriteBE32(rec, 4, parentCnid);              // parentID
        WriteBE16(rec, 8, nameLen);
        if (unicodeName.Length > 0)
            Array.Copy(unicodeName, 0, rec, 10, unicodeName.Length);

        return rec;
    }

    // -----------------------------------------------------------------------
    //  Extents Overflow B-Tree
    // -----------------------------------------------------------------------

    /// <summary>Builds an empty but valid HFS+ Extents Overflow B-tree.</summary>
    private static byte[] BuildExtentsOverflowBTree()
    {
        return PackBTree(new List<(byte[] Key, byte[] Data)>(),
            ExtentsNodeSize, kBTreeTypeExtents);
    }

    // -----------------------------------------------------------------------
    //  Attributes B-Tree
    // -----------------------------------------------------------------------

    /// <summary>Builds an empty but valid HFS+ Attributes B-tree.</summary>
    private static byte[] BuildAttributesBTree()
    {
        return PackBTree(new List<(byte[] Key, byte[] Data)>(),
            AttributesNodeSize, kBTreeTypeAttributes);
    }

    // -----------------------------------------------------------------------
    //  Allocation Bitmap
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds the HFS+ allocation bitmap marking used blocks.
    /// </summary>
    private static byte[] BuildAllocationBitmap(uint totalBlocks,
        uint hfsPlusStartBlock,
        uint extentsStart, uint extentsCount,
        uint catalogStart, uint catalogCount,
        uint attributesStart, uint attributesCount,
        uint bitmapStart, uint bitmapCount)
    {
        uint bitmapBytes = (totalBlocks + 7) / 8;
        uint bitmapSize = AlignUp(bitmapBytes, AllocationBlockSize);
        var bitmap = new byte[bitmapSize];

        // Mark all blocks from 0 to hfsPlusStartBlock as used (ISO area)
        MarkRange(bitmap, 0, hfsPlusStartBlock + 1);

        // Mark metadata blocks as used
        MarkRange(bitmap, extentsStart, extentsCount);
        MarkRange(bitmap, catalogStart, catalogCount);
        MarkRange(bitmap, attributesStart, attributesCount);
        MarkRange(bitmap, bitmapStart, bitmapCount);

        // Mark the last block (alternate volume header) as used
        SetBit(bitmap, totalBlocks - 1);

        return bitmap;
    }

    private static void MarkRange(byte[] bitmap, uint start, uint count)
    {
        for (uint i = start; i < start + count; i++)
            SetBit(bitmap, i);
    }

    private static void SetBit(byte[] bitmap, uint block)
    {
        uint byteIndex = block / 8;
        int bitIndex = 7 - (int)(block % 8); // MSB first
        if (byteIndex < (uint)bitmap.Length)
            bitmap[byteIndex] |= (byte)(1 << bitIndex);
    }

    // -----------------------------------------------------------------------
    //  B-Tree packing
    // -----------------------------------------------------------------------

    // B-tree types per TN1150 §4.1
    private const byte kBTreeTypeExtents = 0;
    private const byte kBTreeTypeCatalog = 1;
    private const byte kBTreeTypeAttributes = 13;

    /// <summary>
    /// Packs key-record pairs into a valid HFS+ B-tree structure.
    /// Creates a header node, optional map node, and leaf nodes.
    /// For small catalogs (< a few thousand files), a single-level tree suffices.
    /// </summary>
    private static byte[] PackBTree(List<(byte[] Key, byte[] Data)> records,
        ushort nodeSize, byte treeType)
    {
        // Minimum: header node + 1 leaf node (even if empty)
        // Header node = node 0
        // Leaf nodes start at node 1

        var leafNodes = new List<byte[]>();
        var currentNode = new MemoryStream(nodeSize);
        // Reserve 14 bytes for node descriptor at start of each leaf node
        currentNode.Write(new byte[14], 0, 14);
        int recordCountInNode = 0;
        var offsets = new List<ushort>();
        offsets.Add(14); // First record starts after node descriptor

        foreach (var (key, data) in records)
        {
            int recordLen = key.Length + data.Length;
            // Each record needs its data + 2 bytes for offset in the offset array
            int spaceNeeded = recordLen;
            int currentUsed = (int)currentNode.Position;
            int offsetsSize = (offsets.Count + 2) * 2; // +1 for this record, +1 for free space offset
            int available = nodeSize - currentUsed - offsetsSize;

            if (spaceNeeded > available && recordCountInNode > 0)
            {
                // Finish current node and start a new one
                leafNodes.Add(FinishLeafNode(currentNode, offsets, nodeSize, recordCountInNode));
                currentNode = new MemoryStream(nodeSize);
                currentNode.Write(new byte[14], 0, 14);
                recordCountInNode = 0;
                offsets = new List<ushort> { 14 };
            }

            currentNode.Write(key, 0, key.Length);
            currentNode.Write(data, 0, data.Length);
            recordCountInNode++;
            offsets.Add((ushort)currentNode.Position);
        }

        // Finish the last leaf node
        if (recordCountInNode > 0 || leafNodes.Count == 0)
        {
            leafNodes.Add(FinishLeafNode(currentNode, offsets, nodeSize, recordCountInNode));
        }

        // Total nodes = 1 (header) + 1 (map) + leafNodes
        int totalNodes = 2 + leafNodes.Count;
        var treeData = new byte[totalNodes * nodeSize];

        // Build header node (node 0)
        BuildHeaderNode(treeData, 0, nodeSize, (uint)totalNodes,
            (uint)leafNodes.Count > 0 ? 2u : 0u, // firstLeafNode
            (uint)leafNodes.Count > 0 ? (uint)(totalNodes - 1) : 0u, // lastLeafNode
            (uint)records.Count, 1, // treeDepth (1 if any records, else 0)
            (uint)(totalNodes - 1), // rootNode = first leaf = node 2 (for single-level tree)
            nodeSize, treeType);

        // Build map node (node 1) — marks which nodes are allocated
        BuildMapNode(treeData, nodeSize, (uint)totalNodes);

        // Copy leaf nodes (starting at node 2)
        for (int i = 0; i < leafNodes.Count; i++)
        {
            int nodeOffset = (2 + i) * nodeSize;
            Array.Copy(leafNodes[i], 0, treeData, nodeOffset, nodeSize);

            // Set forward/backward links
            uint nodeIndex = (uint)(2 + i);
            uint fLink = (i < leafNodes.Count - 1) ? nodeIndex + 1 : 0;
            uint bLink = (i > 0) ? nodeIndex - 1 : 0;
            WriteBE32(treeData, nodeOffset, fLink);          // fLink
            WriteBE32(treeData, nodeOffset + 4, bLink);      // bLink
        }

        return treeData;
    }

    /// <summary>Finalizes a leaf node by writing the node descriptor and offset array.</summary>
    private static byte[] FinishLeafNode(MemoryStream nodeData, List<ushort> offsets,
        ushort nodeSize, int recordCount)
    {
        var node = new byte[nodeSize];
        var data = nodeData.ToArray();
        Array.Copy(data, 0, node, 0, Math.Min(data.Length, nodeSize));

        // Node descriptor (14 bytes at start):
        // fLink (4), bLink (4), kind (1), height (1), numRecords (2), reserved (2)
        node[8] = 0xFF; // kind = leaf (-1 signed = 0xFF)
        node[9] = 1;    // height = 1 (leaf level)
        WriteBE16(node, 10, (ushort)recordCount); // numRecords

        // Write offset array at the end of the node (grows backward from end)
        // Offsets are stored in reverse order: last offset first
        int offsetPos = nodeSize - 2;
        foreach (var off in offsets)
        {
            if (offsetPos < 0) break;
            WriteBE16(node, offsetPos, off);
            offsetPos -= 2;
        }

        return node;
    }

    /// <summary>Builds the B-tree header node (node 0).</summary>
    private static void BuildHeaderNode(byte[] tree, int offset, ushort nodeSize,
        uint totalNodes, uint firstLeafNode, uint lastLeafNode,
        uint leafRecords, uint treeDepth, uint rootNode, ushort btNodeSize,
        byte treeType)
    {
        // Node descriptor (14 bytes)
        WriteBE32(tree, offset, 0);                     // fLink = 0
        WriteBE32(tree, offset + 4, 0);                 // bLink = 0
        tree[offset + 8] = 0x01;                        // kind = header
        tree[offset + 9] = 0;                           // height = 0
        WriteBE16(tree, offset + 10, 3);                // numRecords = 3 (header, user data, map)
        WriteBE16(tree, offset + 12, 0);                // reserved

        // Record 0: B-tree header record (106 bytes, starting at offset 14)
        int hdrOffset = offset + 14;
        if (leafRecords == 0) treeDepth = 0;
        if (leafRecords == 0) rootNode = 0;
        WriteBE16(tree, hdrOffset, (ushort)treeDepth);           // treeDepth
        WriteBE32(tree, hdrOffset + 2, rootNode);                // rootNode
        WriteBE32(tree, hdrOffset + 6, leafRecords);             // leafRecords
        WriteBE32(tree, hdrOffset + 10, firstLeafNode);          // firstLeafNode
        WriteBE32(tree, hdrOffset + 14, lastLeafNode);           // lastLeafNode
        WriteBE16(tree, hdrOffset + 18, btNodeSize);             // nodeSize
        WriteBE16(tree, hdrOffset + 20, 256);                    // maxKeyLength
        WriteBE32(tree, hdrOffset + 22, totalNodes);             // totalNodes
        uint usedNodes = 2 + (firstLeafNode > 0 ? lastLeafNode - firstLeafNode + 1 : 0);
        WriteBE32(tree, hdrOffset + 26, totalNodes - usedNodes); // freeNodes
        WriteBE16(tree, hdrOffset + 30, 0);                      // reserved1
        WriteBE32(tree, hdrOffset + 32, 0);                      // clumpSize
        tree[hdrOffset + 36] = treeType;                            // btreeType
        tree[hdrOffset + 37] = 0xBC;                             // keyCompareType = binary comparison (HFS+ Unicode)
        WriteBE32(tree, hdrOffset + 38, 0x00000002);             // attributes: kBTBigKeysMask

        // Record 1: 128 bytes of user data record (zeros)
        // Record 2: 256 bytes of map record (one bit per node)

        // Write offset array at end of header node
        int offPos = offset + nodeSize - 2;
        WriteBE16(tree, offPos, 14);                     // Record 0 offset
        offPos -= 2;
        WriteBE16(tree, offPos, (ushort)(14 + 106));     // Record 1 offset
        offPos -= 2;
        WriteBE16(tree, offPos, (ushort)(14 + 106 + 128)); // Record 2 offset
        offPos -= 2;
        WriteBE16(tree, offPos, (ushort)(14 + 106 + 128 + 256)); // Free space offset
    }

    /// <summary>Builds a B-tree map node (node 1) with allocation bitmap.</summary>
    private static void BuildMapNode(byte[] tree, ushort nodeSize, uint totalNodes)
    {
        int offset = nodeSize; // Node 1 starts at nodeSize bytes
        // Node descriptor
        WriteBE32(tree, offset, 0);         // fLink
        WriteBE32(tree, offset + 4, 0);     // bLink
        tree[offset + 8] = 0x02;            // kind = map
        tree[offset + 9] = 0;               // height
        WriteBE16(tree, offset + 10, 1);    // numRecords = 1
        WriteBE16(tree, offset + 12, 0);    // reserved

        // Map record: one bit per node, mark all used nodes
        int mapStart = offset + 14;
        for (uint i = 0; i < totalNodes && (mapStart + i / 8) < offset + nodeSize - 4; i++)
        {
            int byteIdx = mapStart + (int)(i / 8);
            int bitIdx = 7 - (int)(i % 8);
            tree[byteIdx] |= (byte)(1 << bitIdx);
        }

        // Offset array
        int offPos = offset + nodeSize - 2;
        WriteBE16(tree, offPos, 14);  // Record 0 offset
        offPos -= 2;
        int mapRecordEnd = mapStart + (int)((totalNodes + 7) / 8);
        WriteBE16(tree, offPos, (ushort)(mapRecordEnd - offset)); // Free space offset
    }

    /// <summary>
    /// Compares two HFS+ catalog keys for B-tree ordering.
    /// Uses binary comparison mode (keyCompareType = 0xBC) for Unicode keys.
    /// Sort order: parent CNID first, then binary Unicode name comparison.
    /// </summary>
    private static int CompareCatalogKeys(byte[] a, byte[] b)
    {
        // Key format: keyLength (2) + parentID (4) + nameLength (2) + name (2*nameLength)
        uint parentA = ReadBE32(a, 2);
        uint parentB = ReadBE32(b, 2);
        if (parentA != parentB)
            return parentA.CompareTo(parentB);

        ushort nameLenA = ReadBE16(a, 6);
        ushort nameLenB = ReadBE16(b, 6);
        int minLen = Math.Min(nameLenA, nameLenB);

        for (int i = 0; i < minLen; i++)
        {
            ushort charA = ReadBE16(a, 8 + i * 2);
            ushort charB = ReadBE16(b, 8 + i * 2);
            if (charA != charB) return charA.CompareTo(charB);
        }

        return nameLenA.CompareTo(nameLenB);
    }

    // -----------------------------------------------------------------------
    //  ISO 9660 Directory Tree Parser
    // -----------------------------------------------------------------------

    /// <summary>
    /// Represents a file extent from the ISO 9660 directory tree.
    /// </summary>
    internal readonly struct IsoFileExtent
    {
        public readonly uint Lba;
        public readonly uint DataLength;
        public readonly string Name;

        public IsoFileExtent(uint lba, uint dataLength, string name)
        {
            Lba = lba;
            DataLength = dataLength;
            Name = name;
        }
    }

    /// <summary>
    /// Parses the ISO 9660 directory tree to extract file locations and sizes.
    /// This allows the HFS+ catalog to reference the same physical disc sectors,
    /// creating a true hybrid image without duplicating file data.
    /// </summary>
    private static Dictionary<string, IsoFileExtent> ParseIsoDirectoryTree(Stream stream)
    {
        var files = new Dictionary<string, IsoFileExtent>(StringComparer.OrdinalIgnoreCase);

        if (stream.Length < 17 * SectorSize)
            return files;

        // Read PVD (sector 16)
        stream.Seek(16 * SectorSize, SeekOrigin.Begin);
        var pvd = new byte[SectorSize];
        if (stream.Read(pvd, 0, SectorSize) < SectorSize)
            return files;

        // Verify PVD signature
        if (pvd[0] != 0x01 || pvd[1] != (byte)'C' || pvd[2] != (byte)'D' ||
            pvd[3] != (byte)'0' || pvd[4] != (byte)'0' || pvd[5] != (byte)'1')
            return files;

        // Parse root directory record
        uint rootLba = BitConverter.ToUInt32(pvd, 156 + 2);
        uint rootLen = BitConverter.ToUInt32(pvd, 156 + 10);

        if (rootLba == 0 || rootLen == 0)
            return files;

        // BFS traversal
        const int MaxDirectories = 100_000; // Safety limit against malicious ISOs
        var visited = new HashSet<uint>();
        var queue = new Queue<(uint Lba, uint Len, string Path)>();
        queue.Enqueue((rootLba, rootLen, ""));
        visited.Add(rootLba);

        while (queue.Count > 0)
        {
            if (visited.Count > MaxDirectories) break;
            var (dirLba, dirLen, dirPath) = queue.Dequeue();
            // Safety limit: skip directories larger than 10 MB to prevent excessive
            // memory allocation from malformed ISO images. Valid ISO 9660 directories
            // rarely exceed a few hundred KB even with thousands of entries.
            if (dirLen > MaxDirectorySizeBytes) continue;

            var dirData = new byte[dirLen];
            stream.Seek((long)dirLba * SectorSize, SeekOrigin.Begin);
            if (stream.Read(dirData, 0, (int)dirLen) < (int)dirLen)
                continue;

            int pos = 0;
            while (pos < (int)dirLen)
            {
                if (dirData[pos] == 0)
                {
                    int skip = SectorSize - (pos % SectorSize);
                    pos += skip;
                    continue;
                }

                byte recLen = dirData[pos];
                if (recLen < 33 || pos + recLen > (int)dirLen) break;

                byte lenFi = dirData[pos + 32];
                byte fileFlags = dirData[pos + 25];
                bool isDir = (fileFlags & 0x02) != 0;

                // Skip . and .. entries
                if (lenFi == 1 && (dirData[pos + 33] == 0x00 || dirData[pos + 33] == 0x01))
                {
                    pos += recLen;
                    continue;
                }

                string name = Encoding.ASCII.GetString(dirData, pos + 33, lenFi);
                int semi = name.IndexOf(';');
                if (semi >= 0) name = name[..semi];
                name = name.TrimEnd('.');

                uint entryLba = BitConverter.ToUInt32(dirData, pos + 2);
                uint entryLen = BitConverter.ToUInt32(dirData, pos + 10);

                string fullPath = string.IsNullOrEmpty(dirPath)
                    ? name
                    : dirPath + "\\" + name;

                if (isDir)
                {
                    if (entryLba > 0 && !visited.Contains(entryLba))
                    {
                        visited.Add(entryLba);
                        queue.Enqueue((entryLba, entryLen, fullPath));
                    }
                }
                else
                {
                    files[fullPath] = new IsoFileExtent(entryLba, entryLen, name);
                }

                pos += recLen;
            }
        }

        return files;
    }

    // -----------------------------------------------------------------------
    //  Helpers
    // -----------------------------------------------------------------------

    private static string GetRelativePath(string basePath, string fullPath)
    {
        var relative = fullPath.Substring(basePath.Length);
        if (relative.StartsWith(Path.DirectorySeparatorChar) ||
            relative.StartsWith(Path.AltDirectorySeparatorChar))
            relative = relative[1..];
        return relative;
    }

    private static string GetParentRelative(string relativePath)
    {
        int lastSep = relativePath.LastIndexOfAny(new[] {
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
        return lastSep >= 0 ? relativePath[..lastSep] : "";
    }

    private static uint ToHfsTimestamp(DateTime utc)
    {
        if (utc < HfsEpoch) return 0;
        var span = utc - HfsEpoch;
        // HFS+ timestamps are unsigned 32-bit seconds since 1904-01-01
        double totalSeconds = span.TotalSeconds;
        if (totalSeconds > uint.MaxValue) return uint.MaxValue;
        return (uint)totalSeconds;
    }

    private static long AlignUp(long value, long alignment)
    {
        return ((value + alignment - 1) / alignment) * alignment;
    }

    private static uint AlignUp(uint value, uint alignment)
    {
        return ((value + alignment - 1) / alignment) * alignment;
    }

    // -----------------------------------------------------------------------
    //  Big-endian I/O (HFS+ is entirely big-endian)
    // -----------------------------------------------------------------------

    private static void WriteBE16(byte[] buf, int offset, ushort value)
    {
        buf[offset] = (byte)(value >> 8);
        buf[offset + 1] = (byte)value;
    }

    private static void WriteBE32(byte[] buf, int offset, uint value)
    {
        buf[offset] = (byte)(value >> 24);
        buf[offset + 1] = (byte)(value >> 16);
        buf[offset + 2] = (byte)(value >> 8);
        buf[offset + 3] = (byte)value;
    }

    private static void WriteBE64(byte[] buf, int offset, ulong value)
    {
        WriteBE32(buf, offset, (uint)(value >> 32));
        WriteBE32(buf, offset + 4, (uint)value);
    }

    private static ushort ReadBE16(byte[] buf, int offset)
    {
        return (ushort)((buf[offset] << 8) | buf[offset + 1]);
    }

    private static uint ReadBE32(byte[] buf, int offset)
    {
        return (uint)((buf[offset] << 24) | (buf[offset + 1] << 16) |
                       (buf[offset + 2] << 8) | buf[offset + 3]);
    }

    private static void WriteAsciiPadded(byte[] buf, int offset, int length, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        int copyLen = Math.Min(bytes.Length, length);
        Array.Copy(bytes, 0, buf, offset, copyLen);
        // Rest is already zeroed
    }
}
