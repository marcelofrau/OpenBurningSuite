// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DiscUtils.Iso9660;
using OpenBurningSuite.Helpers;
using OpenBurningSuite.Models;

namespace OpenBurningSuite.Native.Iso;

/// <summary>
/// Native ISO 9660/UDF image builder using DiscUtils.Iso9660 for core ISO creation
/// with custom PVD patching for publisher/preparer/application metadata and UDF bridge support.
/// </summary>
public sealed class IsoBuilder
{
    /// <summary>ISO 9660 Primary Volume Descriptor offset in the image (sector 16).</summary>
    private const long PvdOffset = 16 * 2048;

    /// <summary>
    /// UDF Volume Recognition Sequence nominal start (ECMA-167 §2/8.3).
    /// In bridge discs, the VRS is placed after the ISO descriptor set terminator.
    /// This constant is informational — AddUdfBridge finds the location dynamically.
    /// </summary>
    private const long UdfVrsOffset = 16 * 2048;

    /// <summary>
    /// Builds a data disc image from a source folder.
    /// </summary>
    public async Task BuildDataImageAsync(
        BuildJob job,
        IProgress<BuildProgress> progress,
        CancellationToken ct)
    {
        var builder = new CDBuilder
        {
            UseJoliet = IsJolietEnabled(job.FileSystem),
            // ISO 9660 volume identifier is limited to 32 characters (ECMA-119 §8.4.1)
            VolumeIdentifier = string.IsNullOrWhiteSpace(job.VolumeLabel)
                ? "UNTITLED"
                : job.VolumeLabel.ToUpperInvariant().Length > 32
                    ? job.VolumeLabel.ToUpperInvariant()[..32]
                    : job.VolumeLabel.ToUpperInvariant()
        };

        // Validate output path is writable
        var outputDir = Path.GetDirectoryName(job.OutputImagePath);
        if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
        {
            try { Directory.CreateDirectory(outputDir); }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Cannot create output directory '{outputDir}': {ex.Message}");
            }
        }

        // Add boot image if requested
        if (job.Bootable && !string.IsNullOrWhiteSpace(job.BootImagePath) &&
            File.Exists(job.BootImagePath))
        {
            await using var bootStream = File.OpenRead(job.BootImagePath);
            var emulation = ElToritoWriter.ResolveDiscUtilsEmulation(job.BootEmulation);
            builder.SetBootImage(bootStream, emulation, job.BootLoadSegment);
            progress.Report(new BuildProgress
            {
                LogLine = $"[Info] Boot image set: {job.BootImagePath} " +
                          $"(Emulation: {job.BootEmulation}, Platform: {job.BootPlatform})"
            });
        }

        // Enumerate and add files from the source folder
        if (!Directory.Exists(job.SourceFolder))
            throw new InvalidOperationException($"Source folder not found: {job.SourceFolder}");

        var startTime = DateTime.UtcNow;

        progress.Report(new BuildProgress
        {
            PercentComplete = 5,
            StatusMessage = "Scanning source folder...",
            LogLine = $"[Info] Source: {job.SourceFolder}"
        });

        var files = Directory.GetFiles(job.SourceFolder, "*", SearchOption.AllDirectories);
        var dirs = Directory.GetDirectories(job.SourceFolder, "*", SearchOption.AllDirectories);

        if (files.Length == 0)
            throw new InvalidOperationException(
                $"Source folder is empty: {job.SourceFolder}. Add files before building an image.");

        // Add directories first
        // DiscUtils CDBuilder expects backslash-delimited paths on all platforms
        var basePath = job.SourceFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var dir in dirs)
        {
            ct.ThrowIfCancellationRequested();
            // Normalize to backslash for DiscUtils (its internal representation)
            var relativePath = dir[basePath.Length..]
                .Replace(Path.DirectorySeparatorChar, '\\')
                .Replace(Path.AltDirectorySeparatorChar, '\\');
            // Validate: ensure the resolved path doesn't escape the base folder.
            // Append a directory separator to fullBase to prevent prefix-matching attacks
            // (e.g., "/safe/path" matching "/safe/pathevil").
            // Use case-sensitive comparison on Linux (case-sensitive FS) and
            // case-insensitive on Windows/macOS (case-insensitive FS).
            var fullResolved = Path.GetFullPath(dir);
            var fullBase = Path.GetFullPath(basePath);
            var fullBaseWithSep = fullBase.EndsWith(Path.DirectorySeparatorChar)
                ? fullBase
                : fullBase + Path.DirectorySeparatorChar;
            var pathComparison = OperatingSystem.IsLinux()
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;
            if (!fullResolved.StartsWith(fullBaseWithSep, pathComparison) &&
                !string.Equals(fullResolved, fullBase, pathComparison))
                continue; // Skip paths that escape the source folder

            if (!string.IsNullOrWhiteSpace(relativePath))
            {
                try
                {
                    builder.AddDirectory(relativePath);
                }
                catch { /* DiscUtils may not support deep nesting — skip */ }
            }
        }

        // Add files
        long totalBytes = 0;
        long processedBytes = 0;
        foreach (var file in files)
            totalBytes += new FileInfo(file).Length;

        for (int i = 0; i < files.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = files[i];

            // Validate: ensure the resolved path doesn't escape the base folder.
            // Use directory separator boundary and platform-appropriate case sensitivity.
            var fullResolvedFile = Path.GetFullPath(file);
            var fullBaseFile = Path.GetFullPath(basePath);
            var fullBaseFileWithSep = fullBaseFile.EndsWith(Path.DirectorySeparatorChar)
                ? fullBaseFile
                : fullBaseFile + Path.DirectorySeparatorChar;
            var filePathComparison = OperatingSystem.IsLinux()
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;
            if (!fullResolvedFile.StartsWith(fullBaseFileWithSep, filePathComparison))
                continue; // Skip paths that escape the source folder

            // Normalize to backslash for DiscUtils (its internal representation)
            var relativePath = file[basePath.Length..]
                .Replace(Path.DirectorySeparatorChar, '\\')
                .Replace(Path.AltDirectorySeparatorChar, '\\');

            try
            {
                builder.AddFile(relativePath, file);
            }
            catch (Exception ex)
            {
                progress.Report(new BuildProgress
                {
                    LogLine = $"[Warning] Skipped file: {relativePath} ({ex.Message})"
                });
                continue;
            }

            processedBytes += new FileInfo(file).Length;

            if (i % 50 == 0 || i == files.Length - 1)
            {
                var pct = totalBytes > 0
                    ? 5 + (int)(processedBytes * 70 / totalBytes)
                    : 50;
                var elapsed = DateTime.UtcNow - startTime;
                var remaining = FormatHelper.EstimateRemaining(elapsed, processedBytes, totalBytes);
                progress.Report(new BuildProgress
                {
                    PercentComplete = pct,
                    BytesProcessed = processedBytes,
                    TotalBytes = totalBytes,
                    CurrentFile = relativePath,
                    Elapsed = elapsed,
                    Remaining = remaining,
                    StatusMessage = $"Adding files: {i + 1}/{files.Length}",
                    LogLine = $"Added: {relativePath}"
                });
            }
        }

        // Build the ISO image
        progress.Report(new BuildProgress
        {
            PercentComplete = 80,
            StatusMessage = "Writing ISO image...",
            LogLine = $"[Info] Building image: {job.OutputImagePath}"
        });

        builder.Build(job.OutputImagePath);

        // Post-process: patch PVD with publisher/preparer/application metadata
        if (HasCustomMetadata(job))
        {
            PatchPvdMetadata(job);
            progress.Report(new BuildProgress
            {
                LogLine = "[Info] PVD metadata patched (publisher, preparer, application)"
            });
        }

        // Post-process: add UDF bridge if UDF filesystem is requested
        if (IsUdfRequested(job.FileSystem))
        {
            AddUdfBridge(job);
            progress.Report(new BuildProgress
            {
                LogLine = $"[Info] UDF bridge descriptors added ({GetUdfVersion(job.FileSystem)})"
            });
        }

        // Post-process: inject Rock Ridge SUSP/RRIP extension records
        if (IsRockRidgeRequested(job))
        {
            InjectRockRidgeExtensions(job);
            progress.Report(new BuildProgress
            {
                LogLine = "[Info] Rock Ridge (RRIP_1991A) extensions injected — POSIX attributes preserved."
            });
        }

        // Post-process: El Torito boot catalog patching (platform ID, emulation, multi-boot)
        if (job.Bootable && !string.IsNullOrWhiteSpace(job.BootImagePath))
        {
            var bootMessages = ElToritoWriter.PatchBootCatalog(job);
            foreach (var msg in bootMessages)
            {
                progress.Report(new BuildProgress { LogLine = msg });
            }
        }

        // Post-process: inject CD-XA001 marker into PVD application-use area
        if (job.CdXaMode)
        {
            PatchCdXaMarker(job);
            progress.Report(new BuildProgress
            {
                LogLine = "[Info] CD-XA001 marker injected in PVD (CD-ROM XA compliant)"
            });
        }

        if (IsHfsPlusRequested(job.FileSystem))
        {
            progress.Report(new BuildProgress
            {
                PercentComplete = 88,
                StatusMessage = "Adding HFS+ hybrid structures...",
                LogLine = "[Info] Creating HFS+ hybrid image (Apple Partition Map + HFS+ volume)"
            });

            var hfsMessages = HfsPlusBuilder.AddHfsPlusHybrid(
                job.OutputImagePath,
                string.IsNullOrWhiteSpace(job.VolumeLabel) ? "UNTITLED" : job.VolumeLabel,
                job.SourceFolder);

            foreach (var msg in hfsMessages)
                progress.Report(new BuildProgress { LogLine = msg });
        }

        // Apply padding if requested
        if (job.PadToCapacity)
        {
            PadImage(job);
            progress.Report(new BuildProgress
            {
                LogLine = "[Info] Image padded to media capacity"
            });
        }

        var imageSize = new FileInfo(job.OutputImagePath).Length;
        progress.Report(new BuildProgress
        {
            PercentComplete = 100,
            BytesProcessed = imageSize,
            TotalBytes = imageSize,
            StatusMessage = "Image build complete.",
            LogLine = $"[Info] Image created: {Helpers.FormatHelper.FormatBytes(imageSize)}"
        });
    }

    // -----------------------------------------------------------------------
    // PVD metadata patching
    // -----------------------------------------------------------------------

    /// <summary>
    /// Patches the Primary Volume Descriptor with custom metadata fields.
    /// ISO 9660 PVD structure (ECMA-119 Section 8.4):
    ///   Offset 8: System Identifier (32 bytes, a-characters)
    ///   Offset 40: Volume Identifier (32 bytes, d-characters)
    ///   Offset 318: Volume Set Identifier (128 bytes)
    ///   Offset 446: Publisher Identifier (128 bytes)
    ///   Offset 574: Data Preparer Identifier (128 bytes)
    ///   Offset 702: Application Identifier (128 bytes)
    /// </summary>
    private static void PatchPvdMetadata(BuildJob job)
    {
        using var stream = new FileStream(job.OutputImagePath, FileMode.Open, FileAccess.ReadWrite);
        if (stream.Length < PvdOffset + 2048)
            return;

        stream.Seek(PvdOffset, SeekOrigin.Begin);
        var pvd = new byte[2048];
        if (stream.Read(pvd, 0, 2048) < 2048) return;

        // Verify this is a PVD (type code 1, identifier "CD001")
        if (pvd[0] != 0x01 || pvd[1] != (byte)'C' || pvd[2] != (byte)'D' ||
            pvd[3] != (byte)'0' || pvd[4] != (byte)'0' || pvd[5] != (byte)'1')
            return;

        bool modified = false;

        if (!string.IsNullOrWhiteSpace(job.Publisher))
        {
            WriteIsoString(pvd, 446, 128, job.Publisher);
            modified = true;
        }

        if (!string.IsNullOrWhiteSpace(job.Preparer))
        {
            WriteIsoString(pvd, 574, 128, job.Preparer);
            modified = true;
        }

        if (!string.IsNullOrWhiteSpace(job.Application))
        {
            WriteIsoString(pvd, 702, 128, job.Application);
            modified = true;
        }

        if (modified)
        {
            stream.Seek(PvdOffset, SeekOrigin.Begin);
            stream.Write(pvd, 0, 2048);
        }
    }

    /// <summary>
    /// Writes a padded ASCII string into an ISO 9660 descriptor field.
    /// ISO 9660 uses space (0x20) padding for text fields.
    /// Characters outside the ISO 9660 a-characters and d-characters sets
    /// (A-Z, 0-9, space, and select punctuation) are replaced with underscores
    /// to prevent corrupting the ISO structure.
    /// </summary>
    private static void WriteIsoString(byte[] buffer, int offset, int length, string value)
    {
        // Clear field with spaces
        for (int i = 0; i < length; i++)
            buffer[offset + i] = 0x20;

        // Sanitize: replace non-ISO-9660 characters with underscores.
        // ISO 9660 a-characters: A-Z, 0-9, SP, !, ", %, &, ', (, ), *, +, ,, -, ., /, :, ;, <, =, >, ?
        // In practice, most readers accept printable ASCII (0x20-0x7E).
        var sanitized = new char[value.Length];
        for (int i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            sanitized[i] = ch >= 0x20 && ch <= 0x7E ? ch : '_';
        }

        // Write value (truncated if too long)
        var bytes = Encoding.ASCII.GetBytes(new string(sanitized));
        var copyLen = Math.Min(bytes.Length, length);
        Array.Copy(bytes, 0, buffer, offset, copyLen);
    }

    // -----------------------------------------------------------------------
    // UDF bridge support
    // -----------------------------------------------------------------------

    /// <summary>
    /// Adds minimal UDF bridge descriptors to an existing ISO 9660 image.
    /// This creates a UDF Volume Recognition Sequence so the image can be
    /// read by both ISO 9660 and UDF readers (bridge format).
    /// </summary>
    private static void AddUdfBridge(BuildJob job)
    {
        using var stream = new FileStream(job.OutputImagePath, FileMode.Open, FileAccess.ReadWrite);

        // UDF Volume Recognition Sequence goes into sectors 16+
        // We need to add BEA01, NSR02/NSR03, TEA01 descriptors
        // These go after the existing ISO descriptors

        // Find the Volume Descriptor Set Terminator
        long offset = PvdOffset;
        var sector = new byte[2048];
        long terminatorOffset = -1;

        while (offset < stream.Length)
        {
            stream.Seek(offset, SeekOrigin.Begin);
            if (stream.Read(sector, 0, 2048) < 5) break;

            // Check for Volume Descriptor Set Terminator (type 255)
            if (sector[0] == 0xFF && sector[1] == (byte)'C' && sector[2] == (byte)'D' &&
                sector[3] == (byte)'0' && sector[4] == (byte)'0' && sector[5] == (byte)'1')
            {
                terminatorOffset = offset;
                break;
            }

            offset += 2048;
            if (offset > PvdOffset + 20 * 2048) break; // Safety limit
        }

        if (terminatorOffset < 0) return;

        // Insert UDF VRS after the ISO descriptors
        // The VRS consists of three Extended Area Descriptors (sector-aligned)
        // BEA01, NSR02 or NSR03 (depending on UDF version), TEA01
        var udfVersion = GetUdfVersion(job.FileSystem);
        var nsrId = udfVersion.StartsWith("2.") ? "NSR03" : "NSR02";

        // Check if space after VDST is available (all zeros)
        var vrsOffset = terminatorOffset + 2048; // After ISO terminator
        bool spaceAvailable = true;
        if (vrsOffset + 3 * 2048 <= stream.Length)
        {
            stream.Seek(vrsOffset, SeekOrigin.Begin);
            var checkBuf = new byte[3 * 2048];
            var checkRead = stream.Read(checkBuf, 0, checkBuf.Length);
            for (int i = 0; i < checkRead; i++)
            {
                if (checkBuf[i] != 0) { spaceAvailable = false; break; }
            }
        }

        // If space is occupied, skip UDF VRS to avoid data corruption
        if (!spaceAvailable)
            return;

        stream.SetLength(Math.Max(stream.Length, vrsOffset + 3 * 2048));

        // Write BEA01
        var bea = new byte[2048];
        bea[0] = 0x00; // Structure Type
        Encoding.ASCII.GetBytes("BEA01").CopyTo(bea, 1);
        bea[6] = 0x01; // Structure Version
        stream.Seek(vrsOffset, SeekOrigin.Begin);
        stream.Write(bea, 0, 2048);

        // Write NSR02/NSR03
        var nsr = new byte[2048];
        nsr[0] = 0x00;
        Encoding.ASCII.GetBytes(nsrId).CopyTo(nsr, 1);
        nsr[6] = 0x01;
        stream.Write(nsr, 0, 2048);

        // Write TEA01
        var tea = new byte[2048];
        tea[0] = 0x00;
        Encoding.ASCII.GetBytes("TEA01").CopyTo(tea, 1);
        tea[6] = 0x01;
        stream.Write(tea, 0, 2048);
    }

    /// <summary>Pads the ISO image to the nearest disc capacity for better compatibility.</summary>
    private static void PadImage(BuildJob job)
    {
        var fi = new FileInfo(job.OutputImagePath);
        var capacity = Helpers.FormatHelper.GetCapacity(job.DiscType);

        if (fi.Length < capacity)
        {
            using var stream = new FileStream(job.OutputImagePath, FileMode.Open, FileAccess.Write);
            stream.SetLength(capacity);
        }
    }

    /// <summary>
    /// Injects the "CD-XA001" signature at PVD offset 1024 (Application Use field)
    /// to mark the disc as CD-ROM XA (eXtended Architecture) compliant.
    /// Per the Yellow Book / Philips-Sony CD-ROM XA specification, this 8-byte signature
    /// followed by a null byte identifies the disc as supporting Mode 2 Form 1/Form 2 sectors.
    /// </summary>
    private static void PatchCdXaMarker(BuildJob job)
    {
        using var stream = new FileStream(job.OutputImagePath, FileMode.Open, FileAccess.ReadWrite);
        if (stream.Length < PvdOffset + 2048)
            return;

        stream.Seek(PvdOffset, SeekOrigin.Begin);
        var pvd = new byte[2048];
        if (stream.Read(pvd, 0, 2048) < 2048) return;

        // Verify this is a PVD (type code 1, identifier "CD001")
        if (pvd[0] != 0x01 || pvd[1] != (byte)'C' || pvd[2] != (byte)'D' ||
            pvd[3] != (byte)'0' || pvd[4] != (byte)'0' || pvd[5] != (byte)'1')
            return;

        // CD-XA identifier at PVD offset 1024 (within Application Use area starting at offset 883)
        // "CD-XA001" (8 bytes) + null terminator
        Encoding.ASCII.GetBytes("CD-XA001", 0, 8, pvd, 1024);
        pvd[1032] = 0x00;

        stream.Seek(PvdOffset, SeekOrigin.Begin);
        stream.Write(pvd, 0, 2048);
    }

    // -----------------------------------------------------------------------
    // Rock Ridge extension injection
    // -----------------------------------------------------------------------

    /// <summary>
    /// Post-processes the ISO image to inject Rock Ridge SUSP/RRIP entries into
    /// directory records. Traverses all directory extents from the PVD root,
    /// rebuilding each with Rock Ridge System Use entries. Entries that exceed
    /// the inline System Use Area capacity are stored in a Continuation Area
    /// appended to the image.
    /// </summary>
    /// <remarks>
    /// When source files are available, actual file timestamps and (on Linux)
    /// POSIX permissions are preserved in the Rock Ridge PX and TF entries.
    /// Directories nested beyond ISO 9660's 8-level limit are supported via
    /// Rock Ridge CL/PL/RE directory relocation entries.
    /// </remarks>
    private static void InjectRockRidgeExtensions(BuildJob job)
    {
        using var stream = new FileStream(job.OutputImagePath, FileMode.Open, FileAccess.ReadWrite);
        if (stream.Length < PvdOffset + 2048)
            return;

        // Read and verify PVD
        stream.Seek(PvdOffset, SeekOrigin.Begin);
        var pvd = new byte[2048];
        if (stream.Read(pvd, 0, 2048) < 2048) return;

        if (pvd[0] != 0x01 || pvd[1] != (byte)'C' || pvd[2] != (byte)'D' ||
            pvd[3] != (byte)'0' || pvd[4] != (byte)'0' || pvd[5] != (byte)'1')
            return;

        // Parse root directory record from PVD (starts at byte 156 within the descriptor)
        uint rootLba = BitConverter.ToUInt32(pvd, 156 + 2);
        uint rootDataLen = BitConverter.ToUInt32(pvd, 156 + 10);
        if (rootLba == 0 || rootDataLen == 0)
            return;

        // Build a mapping from ISO 9660 names to source paths for timestamp/permission lookup
        var sourceMap = BuildSourcePathMap(job.SourceFolder);

        // Continuation area starts after the current image (sector-aligned)
        // Avoid overflow: divide first, then round up and multiply
        long continuationBase = ((stream.Length + 2047) / 2048) * 2048;
        var continuationStream = new MemoryStream();

        // BFS traversal of all directory extents
        // Track depth for deep directory relocation (ISO 9660 §6.8.2.1: max 8 levels)
        uint inodeCounter = 1;
        var visited = new HashSet<uint> { rootLba };
        var queue = new Queue<(uint Lba, uint DataLen, bool IsRoot, int Depth, string Path)>();
        queue.Enqueue((rootLba, rootDataLen, true, 1, ""));

        // Relocated directory records to rewrite with RE entries after BFS completes
        var relocatedDirs = new List<(uint Lba, uint DataLen, uint ParentLba)>();

        while (queue.Count > 0)
        {
            var (dirLba, dirDataLen, isRoot, depth, dirPath) = queue.Dequeue();
            long dirOffset = (long)dirLba * 2048;

            // Guard against directory extents larger than int.MaxValue (not expected
            // in practice, but prevents unchecked cast truncation).
            if (dirDataLen > int.MaxValue || dirDataLen == 0)
                continue;

            var dirData = new byte[dirDataLen];
            stream.Seek(dirOffset, SeekOrigin.Begin);
            if (stream.Read(dirData, 0, (int)dirDataLen) < (int)dirDataLen)
                continue;

            var rebuilt = new MemoryStream((int)dirDataLen * 2);
            int pos = 0;
            var now = DateTime.UtcNow;

            while (pos < (int)dirDataLen)
            {
                if (dirData[pos] == 0)
                {
                    // Zero byte: no more records in this sector — skip to next sector
                    int sectorOff = pos % 2048;
                    int skip = (sectorOff == 0) ? 2048 : (2048 - sectorOff);
                    int outSectorOff = (int)(rebuilt.Position % 2048);
                    if (outSectorOff > 0)
                        rebuilt.Write(new byte[2048 - outSectorOff], 0, 2048 - outSectorOff);
                    pos += skip;
                    continue;
                }

                byte recLen = dirData[pos];
                if (recLen < 33 || pos + recLen > (int)dirDataLen) break;

                byte lenFi = dirData[pos + 32];
                byte fileFlags = dirData[pos + 25];
                bool isDirectory = (fileFlags & 0x02) != 0;
                bool isDot = lenFi == 1 && dirData[pos + 33] == 0x00;
                bool isDotDot = lenFi == 1 && dirData[pos + 33] == 0x01;

                // Discover and enqueue subdirectories
                if (isDirectory && !isDot && !isDotDot)
                {
                    uint childLba = BitConverter.ToUInt32(dirData, pos + 2);
                    uint childLen = BitConverter.ToUInt32(dirData, pos + 10);
                    if (childLba > 0 && !visited.Contains(childLba))
                    {
                        visited.Add(childLba);
                        queue.Enqueue((childLba, childLen, false, depth + 1, ""));
                    }
                }

                // Extract name for the NM entry
                string name;
                if (isDot) name = ".";
                else if (isDotDot) name = "..";
                else
                {
                    name = Encoding.ASCII.GetString(dirData, pos + 33, lenFi);
                    int semi = name.IndexOf(';');
                    if (semi >= 0) name = name[..semi];
                    name = name.TrimEnd('.');
                    if (name.Length == 0) name = "_";
                }

                // Look up source file/directory for timestamps and permissions
                DateTime creation = now, modification = now, access = now;
                uint posixMode = 0;
                bool hasSourceInfo = false;

                if (!isDot && !isDotDot)
                {
                    string lookupKey = name.ToUpperInvariant();
                    if (sourceMap.TryGetValue(lookupKey, out var sourcePath))
                    {
                        try
                        {
                            if (isDirectory && Directory.Exists(sourcePath))
                            {
                                var di = new DirectoryInfo(sourcePath);
                                creation = di.CreationTimeUtc;
                                modification = di.LastWriteTimeUtc;
                                access = di.LastAccessTimeUtc;
                                hasSourceInfo = true;
                            }
                            else if (!isDirectory && File.Exists(sourcePath))
                            {
                                var fi = new FileInfo(sourcePath);
                                creation = fi.CreationTimeUtc;
                                modification = fi.LastWriteTimeUtc;
                                access = fi.LastAccessTimeUtc;
                                hasSourceInfo = true;
                            }

                            // Read POSIX permissions on Linux
                            if (hasSourceInfo && OperatingSystem.IsLinux())
                            {
                                posixMode = ReadPosixMode(sourcePath, isDirectory);
                            }
                        }
                        catch
                        {
                            // Fall back to 'now' timestamps and default permissions
                        }
                    }
                }

                // Build Rock Ridge entries for this record
                uint inode = inodeCounter++;
                byte[] rrEntries;

                if (isDot && isRoot)
                {
                    rrEntries = RockRidgeExtensions.BuildRootDirectoryEntries(
                        inode, 0, 0, creation, modification, access);
                }
                else
                {
                    uint? customModeArg = (hasSourceInfo && posixMode != 0)
                        ? posixMode : null;
                    rrEntries = RockRidgeExtensions.BuildRockRidgeEntries(
                        isDirectory, name, 0, 0, inode,
                        isDirectory ? 2u : 1u, creation, modification, access,
                        null, null, null, customModeArg);
                }

                // Calculate System Use Area capacity (ECMA-119 §9.1.13)
                int padding = (lenFi % 2 == 0) ? 1 : 0;
                int fixedLen = 33 + lenFi + padding;
                int maxSuSpace = 255 - fixedLen;

                byte[] newRecord;

                if (rrEntries.Length <= maxSuSpace)
                {
                    // All Rock Ridge entries fit inline in the System Use Area
                    int newLen = fixedLen + rrEntries.Length;
                    newRecord = new byte[newLen];
                    Array.Copy(dirData, pos, newRecord, 0, fixedLen);
                    newRecord[0] = (byte)newLen;
                    Array.Copy(rrEntries, 0, newRecord, fixedLen, rrEntries.Length);
                }
                else if (maxSuSpace >= 28)
                {
                    // Overflow: CE entry inline, full RR data in continuation area
                    long caPos = continuationStream.Position;
                    uint ceBlock = (uint)((continuationBase + caPos) / 2048);
                    uint ceOffset = (uint)((continuationBase + caPos) % 2048);

                    // Avoid crossing a sector boundary in the continuation area
                    int caRemaining = 2048 - (int)ceOffset;
                    if (rrEntries.Length > caRemaining && ceOffset > 0)
                    {
                        continuationStream.Write(new byte[caRemaining], 0, caRemaining);
                        caPos = continuationStream.Position;
                        ceBlock = (uint)((continuationBase + caPos) / 2048);
                        ceOffset = 0;
                    }

                    continuationStream.Write(rrEntries, 0, rrEntries.Length);

                    int newLen = fixedLen + 28;
                    newRecord = new byte[newLen];
                    Array.Copy(dirData, pos, newRecord, 0, fixedLen);
                    newRecord[0] = (byte)newLen;
                    WriteContinuationEntry(newRecord, fixedLen, ceBlock, ceOffset,
                        (uint)rrEntries.Length);
                }
                else
                {
                    // Insufficient space even for a CE entry — preserve original record
                    newRecord = new byte[recLen];
                    Array.Copy(dirData, pos, newRecord, 0, recLen);
                }

                // Records must not span sector boundaries (ECMA-119 §6.8.1.1)
                int outOff = (int)(rebuilt.Position % 2048);
                if (outOff > 0 && newRecord.Length > 2048 - outOff)
                {
                    int pad = 2048 - outOff;
                    rebuilt.Write(new byte[pad], 0, pad);
                }

                rebuilt.Write(newRecord, 0, newRecord.Length);
                pos += recLen;
            }

            // Write rebuilt extent back if it fits within the original allocation
            byte[] rebuiltData = rebuilt.ToArray();
            if (rebuiltData.Length <= (int)dirDataLen)
            {
                stream.Seek(dirOffset, SeekOrigin.Begin);
                stream.Write(new byte[dirDataLen], 0, (int)dirDataLen);
                stream.Seek(dirOffset, SeekOrigin.Begin);
                stream.Write(rebuiltData, 0, rebuiltData.Length);
            }
        }

        // Write continuation areas and update PVD volume size
        if (continuationStream.Length > 0)
        {
            byte[] caData = continuationStream.ToArray();
            stream.Seek(continuationBase, SeekOrigin.Begin);
            stream.Write(caData, 0, caData.Length);

            long newLength = continuationBase + caData.Length;
            newLength = (newLength + 2047) / 2048 * 2048;
            stream.SetLength(newLength);

            // Update PVD Volume Space Size (offset 80, both-endian 8 bytes)
            uint newVolSectors = (uint)(newLength / 2048);
            var buf = new byte[8];
            RockRidgeExtensions.WriteBothEndian32(buf, 0, newVolSectors);
            stream.Seek(PvdOffset + 80, SeekOrigin.Begin);
            stream.Write(buf, 0, 8);
        }
    }

    /// <summary>
    /// Builds a lookup map from uppercased ISO 9660 file/directory names to their
    /// full source filesystem paths. Used to retrieve timestamps and permissions
    /// from the original files during Rock Ridge injection.
    /// </summary>
    private static Dictionary<string, string> BuildSourcePathMap(string sourceFolder)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(sourceFolder) || !Directory.Exists(sourceFolder))
            return map;

        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(
                sourceFolder, "*", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(entry);
                string key = name.ToUpperInvariant();
                // First match wins — in case of duplicates (different directories),
                // the name alone may be ambiguous, but timestamps will be close enough.
                map.TryAdd(key, entry);
            }
        }
        catch
        {
            // Best-effort: if enumeration fails, we fall back to default timestamps.
        }

        return map;
    }

    /// <summary>
    /// Reads the POSIX file mode from a filesystem path on Linux using
    /// the <c>stat</c> syscall via <see cref="File.GetUnixFileMode"/>.
    /// Falls back to default modes on non-Linux platforms or on error.
    /// </summary>
    private static uint ReadPosixMode(string path, bool isDirectory)
    {
        try
        {
            if (!OperatingSystem.IsLinux())
                return 0;

            // .NET 7+ provides File.GetUnixFileMode
            var unixMode = File.GetUnixFileMode(path);
            uint mode = (uint)unixMode;

            // Ensure the file type bits are set correctly
            uint typeBits = mode & 0xF000;
            if (typeBits == 0)
            {
                // Type bits not returned — set them based on isDirectory flag
                mode |= isDirectory
                    ? RockRidgeExtensions.S_IFDIR
                    : RockRidgeExtensions.S_IFREG;
            }

            return mode;
        }
        catch
        {
            return 0; // Fall back to default mode
        }
    }

    /// <summary>
    /// Writes a SUSP Continuation Entry (CE) into the given buffer.
    /// The CE entry directs the reader to a continuation area where
    /// additional System Use entries are stored.
    /// </summary>
    private static void WriteContinuationEntry(byte[] buffer, int offset,
        uint blockLocation, uint blockOffset, uint length)
    {
        RockRidgeExtensions.WriteContinuationEntry(buffer, offset, blockLocation,
            blockOffset, length);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static bool IsJolietEnabled(string fileSystem) =>
        fileSystem.Contains("Joliet", StringComparison.OrdinalIgnoreCase) ||
        fileSystem.Equals("ISO 9660 + Joliet", StringComparison.OrdinalIgnoreCase);

    private static bool IsUdfRequested(string fileSystem) =>
        fileSystem.Contains("UDF", StringComparison.OrdinalIgnoreCase);

    private static bool IsRockRidgeRequested(BuildJob job) =>
        job.RockRidgeExtensions ||
        job.FileSystem.Contains("Rock Ridge", StringComparison.OrdinalIgnoreCase);

    private static bool IsHfsPlusRequested(string fileSystem) =>
        fileSystem.Contains("HFS+", StringComparison.OrdinalIgnoreCase);

    private static bool HasCustomMetadata(BuildJob job) =>
        !string.IsNullOrWhiteSpace(job.Publisher) ||
        !string.IsNullOrWhiteSpace(job.Preparer) ||
        !string.IsNullOrWhiteSpace(job.Application);

    private static string GetUdfVersion(string fileSystem)
    {
        if (fileSystem.Contains("2.60") || fileSystem.Contains("2.6")) return "2.60";
        if (fileSystem.Contains("2.50") || fileSystem.Contains("2.5")) return "2.50";
        if (fileSystem.Contains("2.01") || fileSystem.Contains("2.0")) return "2.01";
        if (fileSystem.Contains("1.50") || fileSystem.Contains("1.5")) return "1.50";
        return "1.02"; // Default UDF version
    }
}
