// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenBurningSuite.Helpers;
using OpenBurningSuite.Models;
using OpenBurningSuite.Native.Iso;
using OpenBurningSuite.Native.Scsi;

namespace OpenBurningSuite.Services;

/// <summary>Verifies disc or image integrity using checksums and sector comparison.</summary>
public class VerifyService
{
    /// <summary>CD frames per second (75 sectors/second per Red Book / ECMA-130).</summary>
    private const int CdFramesPerSecond = 75;

    /// <summary>
    /// Returns true if the trimmed CUE line is a REM comment.
    /// CUE REM comments may contain FILE/TRACK/INDEX keywords that must be
    /// filtered to prevent false regex matches. Used by ParseCueTrackInfos,
    /// GetCueBinFiles, and IsMultiFileCue for consistent parsing.
    /// </summary>
    internal static bool IsCueRemComment(string trimmedLine)
        => trimmedLine.StartsWith("REM ", StringComparison.OrdinalIgnoreCase) ||
           trimmedLine.Equals("REM", StringComparison.OrdinalIgnoreCase);

    /// <summary>Cached regex for validating hex strings in checksum sidecar files.</summary>
    private static readonly System.Text.RegularExpressions.Regex HexStringRegex =
        new(@"^[0-9a-fA-F]+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    public async Task VerifyAsync(
        VerifyJob job,
        IProgress<VerifyProgress> progress,
        CancellationToken cancellationToken)
    {
        job.Status = VerifyStatus.Running;
        job.StartedAt = DateTime.Now;

        try
        {
            var result = job.VerifyMode switch
            {
                "Verify Disc"            => await VerifyDiscAsync(job, progress, cancellationToken),
                "Compare Disc to Image"  => await CompareDiscToImageAsync(job, progress, cancellationToken),
                "Check Image Integrity"  => await CheckImageIntegrityAsync(job, progress, cancellationToken),
                _                        => await CheckImageIntegrityAsync(job, progress, cancellationToken)
            };

            job.Result = result;
            job.Status = result.Passed ? VerifyStatus.Completed : VerifyStatus.Failed;
        }
        catch (OperationCanceledException)
        {
            job.Status = VerifyStatus.Cancelled;
            throw;
        }
        catch (Exception ex)
        {
            job.Status = VerifyStatus.Failed;
            job.Result = new VerifyResult { Passed = false, ErrorSummary = ex.Message };
            throw;
        }
        finally
        {
            job.FinishedAt = DateTime.Now;
        }
    }

    // -----------------------------------------------------------------------
    // Verify disc (read sectors, compute checksum)
    // -----------------------------------------------------------------------
    private async Task<VerifyResult> VerifyDiscAsync(
        VerifyJob job,
        IProgress<VerifyProgress> progress,
        CancellationToken ct)
    {
        progress.Report(new VerifyProgress { StatusMessage = "Reading disc for verification...", LogLine = $"Device: {job.DevicePath}" });

        // Validate device path across platforms:
        // Linux: /dev/sr0, /dev/cdrom, etc.
        // macOS: /dev/disk2, etc.
        // Windows: D:\, E:\, etc. (DriveInfo format with colon)
        var devicePath = job.DevicePath;
        bool isWindowsDriveLetter = devicePath.Length >= 2 && devicePath[1] == ':';
        if (!File.Exists(devicePath) && !devicePath.StartsWith("/dev/") && !isWindowsDriveLetter)
            return new VerifyResult { Passed = false, ErrorSummary = $"Device not found: {devicePath}" };

        if (job.SectorBySector)
            return await VerifyDiscSectorBySectorAsync(job, progress, ct);
        else
            return await VerifyDiscStreamAsync(job, progress, ct);
    }

    /// <summary>Whether the current platform requires SCSI commands for raw device access.</summary>
    private static bool NeedsScsiForDeviceAccess =>
        System.Runtime.InteropServices.RuntimeInformation
            .IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) ||
        System.Runtime.InteropServices.RuntimeInformation
            .IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX);

    /// <summary>
    /// Reads the disc one sector at a time so that individual bad sectors are
    /// counted and reading can continue past errors. Uses SCSI READ(10) on
    /// Windows/macOS (where FileStream access to raw device is unavailable) and
    /// direct FileStream on Linux for simplicity.
    /// </summary>
    private async Task<VerifyResult> VerifyDiscSectorBySectorAsync(
        VerifyJob job,
        IProgress<VerifyProgress> progress,
        CancellationToken ct)
    {
        const int sectorSize = 2048;

        if (NeedsScsiForDeviceAccess)
            return await VerifyDiscSectorBySectorScsiAsync(job, progress, ct);

        using var hasher = CreateHasher(job.ChecksumType);
        var buffer = new byte[sectorSize];
        long goodSectors = 0;
        long badSectors = 0;
        long totalBytes = 0;
        long currentPosition = 0;
        var startTime = DateTime.UtcNow;

        try
        {
            await using var stream = new FileStream(
                job.DevicePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite, sectorSize, true);

            var streamLength = stream.CanSeek ? stream.Length : 0;
            var totalSectorsEstimate = streamLength > 0 ? streamLength / sectorSize : 0;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int read;
                try
                {
                    if (stream.CanSeek)
                        stream.Position = currentPosition;
                    read = await stream.ReadAsync(buffer.AsMemory(0, sectorSize), ct);
                    if (read <= 0) break;
                    hasher.TransformBlock(buffer, 0, read, null, 0);
                    totalBytes += read;
                    goodSectors++;
                }
                catch (IOException)
                {
                    badSectors++;
                    if (!stream.CanSeek)
                        break;
                }

                currentPosition += sectorSize;

                if ((goodSectors + badSectors) % 1000 == 0 || totalSectorsEstimate == 0)
                {
                    var verified = goodSectors + badSectors;
                    var pct = totalSectorsEstimate > 0
                        ? (int)Math.Min(99, verified * 100 / totalSectorsEstimate)
                        : 0;
                    var elapsed = DateTime.UtcNow - startTime;
                    var remaining = FormatHelper.EstimateRemaining(elapsed, verified, totalSectorsEstimate);
                    progress.Report(new VerifyProgress
                    {
                        PercentComplete = pct,
                        SectorsVerified = verified,
                        TotalSectors = totalSectorsEstimate,
                        GoodSectors = goodSectors,
                        BadSectors = badSectors,
                        CurrentLba = verified,
                        Elapsed = elapsed,
                        Remaining = remaining,
                        StatusMessage = $"Verified {verified} sectors ({totalBytes / 1_048_576} MB)...",
                        // Only log every 10,000 sectors to keep log clean
                        LogLine = verified % 10_000 == 0
                            ? $"OK: {goodSectors} | Bad: {badSectors}"
                            : null
                    });
                }
            }

            hasher.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            var checksum = BitConverter.ToString(hasher.Hash ?? Array.Empty<byte>())
                .Replace("-", "").ToLowerInvariant();

            var passed = badSectors == 0;
            progress.Report(new VerifyProgress
            {
                PercentComplete = 100,
                StatusMessage = passed ? "Verification complete." : $"Verification complete — {badSectors} bad sector(s).",
                LogLine = $"{job.ChecksumType}: {checksum}"
            });

            return new VerifyResult
            {
                Passed = passed,
                TotalSectors = goodSectors + badSectors,
                GoodSectors = goodSectors,
                BadSectors = badSectors,
                ChecksumType = job.ChecksumType,
                ActualChecksum = checksum,
                ChecksumMatch = passed,
                ErrorSummary = passed ? string.Empty : $"{badSectors} bad sector(s) detected."
            };
        }
        catch (IOException ioEx)
        {
            return new VerifyResult
            {
                Passed = false,
                TotalSectors = goodSectors + badSectors,
                GoodSectors = goodSectors,
                BadSectors = badSectors + 1,
                ChecksumType = job.ChecksumType,
                ErrorSummary = ioEx.Message
            };
        }
    }

    /// <summary>
    /// Reads the disc using SCSI READ(10) commands for sector-by-sector verification.
    /// Used on Windows and macOS where raw FileStream access to optical drives is unavailable.
    /// </summary>
    private async Task<VerifyResult> VerifyDiscSectorBySectorScsiAsync(
        VerifyJob job,
        IProgress<VerifyProgress> progress,
        CancellationToken ct)
    {
        const int sectorSize = 2048;
        const int sectorsPerRead = 32;
        using var hasher = CreateHasher(job.ChecksumType);
        long goodSectors = 0;
        long badSectors = 0;
        long totalBytes = 0;
        var startTime = DateTime.UtcNow;

        using var drive = new Native.Optical.OpticalDrive(job.DevicePath);

        // Wait for drive to become ready — drives need time to spin up after tray
        // close/load (typically 2-15 seconds). A single TestUnitReady check fails
        // if the media was just inserted or the tray was recently closed.
        progress.Report(new VerifyProgress { StatusMessage = "Waiting for drive to become ready..." });
        if (!drive.WaitForDriveReady())
            return new VerifyResult { Passed = false, ErrorSummary = "Drive is not ready. Please ensure a disc is inserted and the tray is closed." };

        // Lock the tray during verification to prevent accidental ejection
        drive.PreventMediumRemoval(true);

        try
        {
        uint totalSectorsEstimate = 0;
        var capacity = drive.ReadCapacity();
        if (capacity.HasValue && capacity.Value.LastLba > 0)
            totalSectorsEstimate = capacity.Value.LastLba < uint.MaxValue
                ? capacity.Value.LastLba + 1
                : uint.MaxValue;

        // Fallback: try track info
        if (totalSectorsEstimate == 0)
        {
            var discInfo = drive.ReadDiscInfo();
            var lastTrack = discInfo?.LastTrackInLastSessionLsb ?? 1;
            var trackInfo = drive.ReadTrackInfo((uint)lastTrack);
            if (trackInfo != null)
                totalSectorsEstimate = trackInfo.TrackStartAddress + trackInfo.TrackSize;
        }

        // Safety cap: if we can't determine disc size, stop after BDXL QL capacity (~128 GB)
        // to prevent infinite loop. This handles cases where ReadCapacity and ReadTrackInfo
        // both fail to return a valid sector count.
        // Use BD-128 capacity (128,001,769,472 bytes) as the upper bound for any single disc,
        // covering all formats including BDXL quad-layer.
        const long safetyCapBytes = FormatHelper.BD128Capacity;
        const uint maxSafetyLba = (uint)(safetyCapBytes / sectorSize);

        // Detect media type to use appropriate READ command (READ(12) for DVD/BD)
        var profile = drive.GetCurrentProfile();
        bool isDvdOrBd = profile >= 0x0010;

        uint currentLba = 0;
        while (currentLba < totalSectorsEstimate || totalSectorsEstimate == 0)
        {
            ct.ThrowIfCancellationRequested();

            // Guard against infinite loop when disc size is unknown
            if (totalSectorsEstimate == 0 && currentLba >= maxSafetyLba)
                break;

            var count = totalSectorsEstimate > 0
                ? (ushort)Math.Min(sectorsPerRead, totalSectorsEstimate - currentLba)
                : (ushort)sectorsPerRead;

            var (result, data) = drive.ReadSectors(currentLba, count, sectorSize, isDvdOrBd);
            if (result.Success && result.DataTransferred > 0)
            {
                var validBytes = Math.Min(result.DataTransferred, count * sectorSize);
                hasher.TransformBlock(data, 0, validBytes, null, 0);
                totalBytes += validBytes;
                // Count actual sectors transferred, not requested, to avoid overcounting
                // on partial reads where the drive returns fewer bytes than requested.
                goodSectors += validBytes / sectorSize;
            }
            else if (count > 1)
            {
                // Batch read failed — fall back to single-sector reads to identify
                // which specific sectors are bad (rather than marking all as bad)
                for (ushort s = 0; s < count; s++)
                {
                    ct.ThrowIfCancellationRequested();
                    var (singleResult, singleData) = drive.ReadSectors(currentLba + s, 1, sectorSize, isDvdOrBd);
                    if (singleResult.Success && singleResult.DataTransferred > 0)
                    {
                        var validBytes = Math.Min(singleResult.DataTransferred, sectorSize);
                        hasher.TransformBlock(singleData, 0, validBytes, null, 0);
                        totalBytes += validBytes;
                        goodSectors++;
                    }
                    else
                    {
                        // Hash zeros for the bad sector to maintain checksum consistency
                        var zeros = new byte[sectorSize];
                        hasher.TransformBlock(zeros, 0, sectorSize, null, 0);
                        totalBytes += sectorSize;
                        badSectors++;
                    }
                }
            }
            else
            {
                // Single-sector read also failed
                var zeros = new byte[sectorSize];
                hasher.TransformBlock(zeros, 0, sectorSize, null, 0);
                totalBytes += sectorSize;
                badSectors++;
                if (totalSectorsEstimate == 0)
                    break; // Unknown size, stop at first error
            }

            currentLba += count;

            if (currentLba % 1000 < (uint)sectorsPerRead || currentLba >= totalSectorsEstimate)
            {
                var verified = goodSectors + badSectors;
                var pct = totalSectorsEstimate > 0
                    ? (int)Math.Min(99, verified * 100 / totalSectorsEstimate)
                    : 0;
                var elapsed = DateTime.UtcNow - startTime;
                var remaining = FormatHelper.EstimateRemaining(elapsed, verified, totalSectorsEstimate);
                progress.Report(new VerifyProgress
                {
                    PercentComplete = pct,
                    SectorsVerified = verified,
                    TotalSectors = totalSectorsEstimate,
                    GoodSectors = goodSectors,
                    BadSectors = badSectors,
                    CurrentLba = currentLba,
                    Elapsed = elapsed,
                    Remaining = remaining,
                    StatusMessage = $"Verified {verified} sectors ({totalBytes / 1_048_576} MB)...",
                    // Only log every 10,000 sectors to keep log clean
                    LogLine = currentLba % 10_000 < (uint)sectorsPerRead || currentLba >= totalSectorsEstimate
                        ? $"OK: {goodSectors} | Bad: {badSectors}"
                        : null
                });
            }
        }

        hasher.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        var checksum = BitConverter.ToString(hasher.Hash ?? Array.Empty<byte>())
            .Replace("-", "").ToLowerInvariant();

        var passed = badSectors == 0;
        progress.Report(new VerifyProgress
        {
            PercentComplete = 100,
            StatusMessage = passed ? "Verification complete." : $"Verification complete — {badSectors} bad sector(s).",
            LogLine = $"{job.ChecksumType}: {checksum}"
        });

        return new VerifyResult
        {
            Passed = passed,
            TotalSectors = goodSectors + badSectors,
            GoodSectors = goodSectors,
            BadSectors = badSectors,
            ChecksumType = job.ChecksumType,
            ActualChecksum = checksum,
            ChecksumMatch = passed,
            ErrorSummary = passed ? string.Empty : $"{badSectors} bad sector(s) detected."
        };
        }
        finally
        {
            // Unlock the tray after verification — guard against exceptions to ensure cleanup
            try { drive.PreventMediumRemoval(false); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Reads the disc as a continuous stream in large chunks for speed
    /// (no per-sector error tracking). Uses SCSI on Windows/macOS.
    /// </summary>
    private async Task<VerifyResult> VerifyDiscStreamAsync(
        VerifyJob job,
        IProgress<VerifyProgress> progress,
        CancellationToken ct)
    {
        if (NeedsScsiForDeviceAccess)
        {
            // Fall back to SCSI-based sector-by-sector verification
            return await VerifyDiscSectorBySectorScsiAsync(job, progress, ct);
        }

        using var hasher = CreateHasher(job.ChecksumType);
        var buffer = new byte[2048 * 64]; // 128 KB chunks
        long totalBytes = 0;
        long sectors = 0;
        var startTime = DateTime.UtcNow;

        try
        {
            await using var stream = new FileStream(
                job.DevicePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite, 65536, true);

            var streamLength = stream.CanSeek ? stream.Length : 0;
            var totalSectorsEstimate = streamLength > 0 ? streamLength / 2048 : 0;
            var lastReportTime = DateTime.UtcNow;

            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                hasher.TransformBlock(buffer, 0, read, null, 0);
                totalBytes += read;
                sectors = totalBytes / 2048;

                // Throttle progress reports to at most every 250ms to avoid UI flooding
                var now = DateTime.UtcNow;
                if ((now - lastReportTime).TotalMilliseconds >= 250)
                {
                    lastReportTime = now;
                    var pct = streamLength > 0
                        ? (int)Math.Min(99, totalBytes * 100 / streamLength)
                        : 0;
                    var elapsed = now - startTime;
                    var remaining = FormatHelper.EstimateRemaining(elapsed, totalBytes, streamLength);
                    progress.Report(new VerifyProgress
                    {
                        PercentComplete = pct,
                        SectorsVerified = sectors,
                        TotalSectors = totalSectorsEstimate,
                        GoodSectors = sectors,
                        CurrentLba = sectors,
                        Elapsed = elapsed,
                        Remaining = remaining,
                        StatusMessage = $"Read {totalBytes / 1_048_576} MB...",
                        // Only log every ~10 MB to keep log clean
                        LogLine = totalBytes % (10 * 1_048_576) < read
                            ? $"Sectors: {sectors}"
                            : null
                    });
                }
            }

            hasher.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            var checksum = BitConverter.ToString(hasher.Hash ?? Array.Empty<byte>())
                .Replace("-", "").ToLowerInvariant();

            progress.Report(new VerifyProgress
            {
                PercentComplete = 100,
                StatusMessage = "Verification complete.",
                LogLine = $"{job.ChecksumType}: {checksum}"
            });

            return new VerifyResult
            {
                Passed = true,
                TotalSectors = sectors,
                GoodSectors = sectors,
                BadSectors = 0,
                ChecksumType = job.ChecksumType,
                ActualChecksum = checksum,
                ChecksumMatch = true
            };
        }
        catch (IOException ioEx)
        {
            return new VerifyResult
            {
                Passed = false,
                TotalSectors = sectors + 1,
                GoodSectors = sectors,
                BadSectors = 1,
                ChecksumType = job.ChecksumType,
                ErrorSummary = ioEx.Message
            };
        }
    }

    // -----------------------------------------------------------------------
    // Compare disc to image file
    // -----------------------------------------------------------------------
    private async Task<VerifyResult> CompareDiscToImageAsync(
        VerifyJob job,
        IProgress<VerifyProgress> progress,
        CancellationToken ct)
    {
        progress.Report(new VerifyProgress
        {
            StatusMessage = "Comparing disc to image...",
            LogLine = $"Disc: {job.DevicePath} | Image: {job.ImagePath}"
        });

        if (!File.Exists(job.ImagePath))
            return new VerifyResult { Passed = false, ErrorSummary = $"Image not found: {job.ImagePath}" };

        // Resolve CUE/TOC files to their BIN data file(s).
        // CUE sheets and TOC files are metadata descriptors that reference one or more BIN/data files.
        // For verification, we need to compare the actual binary data, not the CUE/TOC text.
        // Multi-file CUE/TOC sheets (one BIN per track) require checksumming ALL data track files.
        var imagePath = job.ImagePath;
        var imageExt = Path.GetExtension(imagePath).ToLowerInvariant();
        List<string>? cueBinFiles = null;
        List<CueTrackInfo>? cueTrackInfos = null;
        if (imageExt == ".cue")
        {
            cueBinFiles = Native.Optical.BurnEngine.GetCueBinFiles(imagePath);
            cueTrackInfos = ParseCueTrackInfos(imagePath);

            var existingBinFiles = cueBinFiles.FindAll(File.Exists);
            if (existingBinFiles.Count > 0)
            {
                bool isMultiFile = existingBinFiles.Count > 1;
                imagePath = existingBinFiles[0];
                int dataTrackCount = cueTrackInfos?.FindAll(t => !t.IsAudio).Count ?? existingBinFiles.Count;

                if (isMultiFile)
                {
                    int audioTrackCount = cueTrackInfos?.FindAll(t => t.IsAudio).Count ?? 0;
                    string audioNote = audioTrackCount > 0 && job.AudioVerification
                        ? " All data and audio tracks will be verified."
                        : " All data tracks will be verified.";
                    progress.Report(new VerifyProgress
                    {
                        LogLine = $"[Info] Multi-file CUE sheet: {existingBinFiles.Count} track file(s), " +
                                  $"{dataTrackCount} data track(s).{audioNote}"
                    });
                }
                else
                {
                    progress.Report(new VerifyProgress
                    {
                        LogLine = $"[Info] CUE sheet resolved to BIN file: {Path.GetFileName(imagePath)}"
                    });
                }
                imageExt = Path.GetExtension(imagePath).ToLowerInvariant();
            }
            else
            {
                // Try companion BIN file with same name as CUE
                var companionBin = Path.ChangeExtension(imagePath, ".bin");
                if (File.Exists(companionBin))
                {
                    imagePath = companionBin;
                    imageExt = ".bin";
                    cueBinFiles = new List<string> { companionBin };
                    progress.Report(new VerifyProgress
                    {
                        LogLine = $"[Info] Using companion BIN file: {Path.GetFileName(imagePath)}"
                    });
                }
                else
                {
                    return new VerifyResult
                    {
                        Passed = false,
                        ErrorSummary = $"CUE sheet references no existing BIN files: {job.ImagePath}"
                    };
                }
            }
        }
        else if (imageExt == ".toc")
        {
            // Resolve TOC files to their BIN data file(s), same as CUE resolution.
            // cdrdao .toc files use DATAFILE directives to reference BIN files.
            cueBinFiles = GetTocBinFiles(imagePath);
            cueTrackInfos = ParseTocTrackInfos(imagePath);

            var existingBinFiles = cueBinFiles.FindAll(File.Exists);
            if (existingBinFiles.Count > 0)
            {
                bool isMultiFile = existingBinFiles.Count > 1;
                imagePath = existingBinFiles[0];
                int dataTrackCount = cueTrackInfos?.FindAll(t => !t.IsAudio).Count ?? existingBinFiles.Count;

                if (isMultiFile)
                {
                    progress.Report(new VerifyProgress
                    {
                        LogLine = $"[Info] Multi-file TOC: {existingBinFiles.Count} track file(s), " +
                                  $"{dataTrackCount} data track(s). All data tracks will be verified."
                    });
                }
                else
                {
                    progress.Report(new VerifyProgress
                    {
                        LogLine = $"[Info] TOC file resolved to BIN file: {Path.GetFileName(imagePath)}"
                    });
                }
                imageExt = Path.GetExtension(imagePath).ToLowerInvariant();
            }
            else
            {
                // Try companion BIN file with same name as TOC
                var companionBin = Path.ChangeExtension(imagePath, ".bin");
                if (File.Exists(companionBin))
                {
                    imagePath = companionBin;
                    imageExt = ".bin";
                    cueBinFiles = new List<string> { companionBin };
                    progress.Report(new VerifyProgress
                    {
                        LogLine = $"[Info] Using companion BIN file: {Path.GetFileName(imagePath)}"
                    });
                }
                else
                {
                    return new VerifyResult
                    {
                        Passed = false,
                        ErrorSummary = $"TOC file references no existing BIN files: {job.ImagePath}"
                    };
                }
            }
        }

        // For multi-file CUE/TOC, determine if we have a multi-file scenario with track info
        bool isMultiFileCue = cueBinFiles != null && cueBinFiles.FindAll(File.Exists).Count > 1;

        var imageLength = new FileInfo(imagePath).Length;
        bool isRawImage = false;

        // For multi-file CUE, use CUE track mode info to determine raw vs cooked.
        // This avoids false negatives when the first BIN file is an audio track
        // (no sync pattern) with a file size divisible by both 2352 and 2048.
        // Per CUE spec, AUDIO and MODE*/2352 track modes use 2352-byte raw sectors.
        // DiscImageCreator, Redumper, and CUETools all treat these as raw images.
        if (cueTrackInfos != null && cueTrackInfos.Count > 0)
        {
            foreach (var t in cueTrackInfos)
            {
                var mode = t.Mode.ToUpperInvariant();
                if (mode == "AUDIO" || mode.EndsWith("/2352") || mode.EndsWith("/2448") ||
                    mode.EndsWith("/2336"))
                {
                    isRawImage = true;
                    break;
                }
            }
        }

        if (!isRawImage && imageExt == ".bin" && imageLength > 0)
        {
            // Determine candidate raw sector size based on file size divisibility
            int candidateRawSize = 0;
            if (imageLength % 2352 == 0)
                candidateRawSize = 2352;
            else if (imageLength % 2448 == 0)
                candidateRawSize = 2448; // Raw + subchannel (2352 main + 96 subchannel)
            else if (imageLength % 2336 == 0 && imageLength % 2048 != 0)
                candidateRawSize = 2336; // MODE2 form mix

            if (candidateRawSize > 0)
            {
                // Validate with CD sync pattern at the start of the file
                // The 12-byte sync pattern is: 00 FF FF FF FF FF FF FF FF FF FF 00
                // Audio sectors do not have a sync pattern, so we also check the second sector.
                try
                {
                    using var syncStream = new FileStream(imagePath, FileMode.Open,
                        FileAccess.Read, FileShare.Read, 4096, false);
                    var syncCheck = new byte[12];
                    bool hasSyncPattern = false;

                    if (syncStream.Read(syncCheck, 0, 12) == 12)
                    {
                        hasSyncPattern = syncCheck[0] == 0x00 && syncCheck[11] == 0x00;
                        for (int si = 1; si <= 10 && hasSyncPattern; si++)
                            hasSyncPattern = syncCheck[si] == 0xFF;
                    }

                    if (hasSyncPattern)
                    {
                        // Confirmed raw data image (has sync pattern)
                        isRawImage = true;
                    }
                    else if (imageLength >= candidateRawSize * 2)
                    {
                        // No sync in first sector — could be pure audio CD (no sync pattern).
                        // Check the second sector too. If neither has sync, treat as raw audio
                        // only if the file size is an exact multiple of the candidate sector size
                        // and NOT also a multiple of 2048 (to avoid false positives on cooked images).
                        if (imageLength % 2048 != 0)
                            isRawImage = true;
                        else
                        {
                            // File is divisible by both raw and cooked sizes.
                            // Check second sector for sync pattern as tiebreaker.
                            syncStream.Seek(candidateRawSize, SeekOrigin.Begin);
                            if (syncStream.Read(syncCheck, 0, 12) == 12)
                            {
                                hasSyncPattern = syncCheck[0] == 0x00 && syncCheck[11] == 0x00;
                                for (int si = 1; si <= 10 && hasSyncPattern; si++)
                                    hasSyncPattern = syncCheck[si] == 0xFF;
                                isRawImage = hasSyncPattern;
                            }
                        }
                    }
                    else
                    {
                        // Tiny file, assume raw if not also divisible by 2048
                        isRawImage = imageLength % 2048 != 0;
                    }
                }
                catch
                {
                    // Sync check failed — fall back to size-based detection
                    isRawImage = imageLength % 2048 != 0;
                }
            }
        }

        if (isRawImage)
        {
            bool verifyAudio = job.AudioVerification &&
                               (cueTrackInfos?.Any(t => t.IsAudio) ?? false);

            if (verifyAudio)
            {
                progress.Report(new VerifyProgress
                {
                    StatusMessage = "Raw sector image detected — verifying data and audio tracks...",
                    LogLine = "[Info] BIN image uses 2352-byte sectors. Reading raw sectors from disc via " +
                              "READ CD for data tracks (extracting 2048-byte user data) and audio tracks " +
                              "(full 2352-byte sectors) for complete verification."
                });
            }
            else
            {
                progress.Report(new VerifyProgress
                {
                    StatusMessage = "Raw sector image detected — sector-size-aware comparison...",
                    LogLine = "[Info] BIN image uses 2352-byte sectors. Reading raw sectors from disc via " +
                              "READ CD and extracting user data (2048 bytes) from each 2352-byte sector " +
                              "for consistent comparison."
                });
            }

            // For raw BIN/CUE images, use READ CD (raw 2352-byte reads) for the disc side
            // instead of READ(10) at 2048 bytes/sector. This is critical because:
            //   1. Mode 2 XA discs (PS1, etc.) contain Form 2 sectors that READ(10) cannot
            //      read at 2048 bytes/sector — the drive returns MEDIUM ERROR.
            //   2. Raw reads ensure both disc and image sides extract user data from identical
            //      2352-byte sector structures using the same logic, guaranteeing consistent
            //      checksum computation.
            // Compute disc checksum first (0-50% progress), then image (50-100%) so progress
            // increases monotonically instead of jumping backwards.

            var (discChecksum, discReadComplete) = await ComputeDiscRawUserDataChecksumViaScsiAsync(
                job.DevicePath, job.ChecksumType, progress, ct, 0, 50, verifyAudio,
                cueBinFiles, cueTrackInfos);

            string imageChecksum;
            if (isMultiFileCue && cueTrackInfos != null)
            {
                // Multi-file CUE: compute checksum across track BIN files (data + audio if enabled)
                imageChecksum = await ComputeMultiFileRawUserDataChecksumAsync(
                    cueBinFiles!, cueTrackInfos, job.ChecksumType, progress, ct, 50, 100, verifyAudio);
            }
            else
            {
                imageChecksum = await ComputeRawImageUserDataChecksumAsync(
                    imagePath, job.ChecksumType, progress, ct, 50, 100, verifyAudio);
            }

            var rawMatch = string.Equals(discChecksum, imageChecksum, StringComparison.OrdinalIgnoreCase);

            string statusMessage;
            string errorSummary;
            if (!discReadComplete)
            {
                statusMessage = rawMatch
                    ? "MATCH (partial): Disc read was incomplete but checksums match."
                    : "FAILED: Disc read was incomplete — checksum comparison is unreliable.";
                errorSummary = rawMatch
                    ? string.Empty
                    : "Disc read aborted due to errors. Checksum comparison is unreliable — " +
                      "the disc may be damaged or dirty.";
            }
            else
            {
                statusMessage = rawMatch
                    ? "MATCH: Disc and raw image user data are identical."
                    : "MISMATCH: Disc and raw image differ!";
                errorSummary = rawMatch
                    ? string.Empty
                    : "Checksum mismatch between disc and raw image user data.";
            }

            progress.Report(new VerifyProgress
            {
                PercentComplete = 100,
                StatusMessage = statusMessage,
                LogLine = $"Disc  {job.ChecksumType}: {discChecksum}\nImage (user data) {job.ChecksumType}: {imageChecksum}"
            });

            return new VerifyResult
            {
                Passed = rawMatch,
                ChecksumType = job.ChecksumType,
                ExpectedChecksum = imageChecksum,
                ActualChecksum = discChecksum,
                ChecksumMatch = rawMatch,
                ErrorSummary = errorSummary
            };
        }

        // Standard comparison for ISO/cooked images
        bool hasAudioTracksStd = cueTrackInfos?.Any(t => t.IsAudio) ?? false;
        string discChecksumStd;
        if (NeedsScsiForDeviceAccess)
        {
            discChecksumStd = hasAudioTracksStd
                ? await ComputeDiscDataOnlyChecksumViaScsiAsync(job.DevicePath, job.ChecksumType, progress, ct, 0, 50)
                : await ComputeDiscChecksumViaScsiAsync(job.DevicePath, job.ChecksumType, progress, ct, 0, 50);
        }
        else
            discChecksumStd = await ComputeChecksumAsync(job.DevicePath, job.ChecksumType, progress, ct, 0, 50);

        string imageChecksumStd;
        if (isMultiFileCue)
        {
            // Multi-file CUE with cooked sectors: hash all data track files concatenated.
            // Build a set of file indices that contain only audio tracks.
            var audioFileIndicesStd = new HashSet<int>();
            if (cueTrackInfos != null)
            {
                foreach (var t in cueTrackInfos)
                {
                    if (t.IsAudio) audioFileIndicesStd.Add(t.FileIndex);
                }
                foreach (var t in cueTrackInfos)
                {
                    if (!t.IsAudio) audioFileIndicesStd.Remove(t.FileIndex);
                }
            }
            var dataFiles = new List<string>();
            for (int i = 0; i < cueBinFiles!.Count; i++)
            {
                if (!audioFileIndicesStd.Contains(i) && File.Exists(cueBinFiles[i]))
                    dataFiles.Add(cueBinFiles[i]);
            }
            imageChecksumStd = await ComputeMultiFileChecksumAsync(dataFiles, job.ChecksumType, progress, ct, 50, 100);
        }
        else
        {
            imageChecksumStd = await ComputeFileChecksumAsync(imagePath, job.ChecksumType, progress, ct, 50, 100);
        }

        var match = string.Equals(discChecksumStd, imageChecksumStd, StringComparison.OrdinalIgnoreCase);

        progress.Report(new VerifyProgress
        {
            PercentComplete = 100,
            StatusMessage = match ? "MATCH: Disc and image are identical." : "MISMATCH: Disc and image differ!",
            LogLine = $"Disc  {job.ChecksumType}: {discChecksumStd}\nImage {job.ChecksumType}: {imageChecksumStd}"
        });

        return new VerifyResult
        {
            Passed = match,
            ChecksumType = job.ChecksumType,
            ExpectedChecksum = imageChecksumStd,
            ActualChecksum = discChecksumStd,
            ChecksumMatch = match,
            ErrorSummary = match ? string.Empty : "Checksum mismatch between disc and image."
        };
    }

    // -----------------------------------------------------------------------
    // Check image integrity
    // -----------------------------------------------------------------------
    private async Task<VerifyResult> CheckImageIntegrityAsync(
        VerifyJob job,
        IProgress<VerifyProgress> progress,
        CancellationToken ct)
    {
        if (!File.Exists(job.ImagePath))
            return new VerifyResult { Passed = false, ErrorSummary = $"Image not found: {job.ImagePath}" };

        // Resolve CUE/TOC metadata files to their BIN data file(s).
        // These are text-based descriptors — checksumming the text file itself is meaningless.
        var imagePath = job.ImagePath;
        var imageExt = Path.GetExtension(imagePath).ToLowerInvariant();

        if (imageExt == ".cue")
        {
            var cueBins = Native.Optical.BurnEngine.GetCueBinFiles(imagePath);
            var existing = cueBins.FindAll(File.Exists);
            if (existing.Count > 0)
            {
                imagePath = existing[0];
                progress.Report(new VerifyProgress
                {
                    LogLine = $"[Info] CUE sheet resolved to BIN file: {Path.GetFileName(imagePath)}"
                });
            }
            else
            {
                var companionBin = Path.ChangeExtension(imagePath, ".bin");
                if (File.Exists(companionBin))
                {
                    imagePath = companionBin;
                    progress.Report(new VerifyProgress
                    {
                        LogLine = $"[Info] Using companion BIN file: {Path.GetFileName(imagePath)}"
                    });
                }
                else
                {
                    return new VerifyResult
                    {
                        Passed = false,
                        ErrorSummary = $"CUE sheet references no existing BIN files: {job.ImagePath}"
                    };
                }
            }
        }
        else if (imageExt == ".toc")
        {
            var tocBins = GetTocBinFiles(imagePath);
            var existing = tocBins.FindAll(File.Exists);
            if (existing.Count > 0)
            {
                imagePath = existing[0];
                progress.Report(new VerifyProgress
                {
                    LogLine = $"[Info] TOC file resolved to BIN file: {Path.GetFileName(imagePath)}"
                });
            }
            else
            {
                var companionBin = Path.ChangeExtension(imagePath, ".bin");
                if (File.Exists(companionBin))
                {
                    imagePath = companionBin;
                    progress.Report(new VerifyProgress
                    {
                        LogLine = $"[Info] Using companion BIN file: {Path.GetFileName(imagePath)}"
                    });
                }
                else
                {
                    return new VerifyResult
                    {
                        Passed = false,
                        ErrorSummary = $"TOC file references no existing BIN files: {job.ImagePath}"
                    };
                }
            }
        }

        progress.Report(new VerifyProgress
        {
            StatusMessage = $"Computing {job.ChecksumType} for {Path.GetFileName(imagePath)}...",
            LogLine = $"File: {imagePath}"
        });

        var checksum = await ComputeFileChecksumAsync(imagePath, job.ChecksumType, progress, ct, 0, 100);

        // Check against expected checksum if provided (e.g., from a .md5/.sha1/.sha256 sidecar file)
        var expectedChecksum = FindExpectedChecksum(imagePath, job.ChecksumType);
        var match = string.IsNullOrEmpty(expectedChecksum) ||
                    string.Equals(checksum, expectedChecksum, StringComparison.OrdinalIgnoreCase);

        var statusMsg = match
            ? (string.IsNullOrEmpty(expectedChecksum)
                ? "Image integrity check complete."
                : "Image integrity check complete — checksum matches.")
            : "Image integrity check complete — CHECKSUM MISMATCH!";

        // Run format-specific structural validation on the resolved image file
        var structuralMessages = new System.Collections.Generic.List<string>();
        try
        {
            // El Torito boot verification
            var bootMsgs = VcdSvcdVerifier.ValidateElToritoBoot(imagePath);
            if (bootMsgs.Count > 0)
                structuralMessages.AddRange(bootMsgs);

            // CD-XA compliance check
            var xaMsgs = VcdSvcdVerifier.ValidateCdXaCompliance(imagePath);
            if (xaMsgs.Count > 0)
                structuralMessages.AddRange(xaMsgs);

            // VCD/SVCD detection and validation
            var vcdType = Native.Iso.VcdBuilder.DetectVcdSvcd(imagePath);
            if (vcdType != null)
            {
                structuralMessages.Add($"[Info] Detected disc format: {vcdType}");
                var vcdMsgs = VcdSvcdVerifier.ValidateVcdSvcdImage(imagePath);
                structuralMessages.AddRange(vcdMsgs);

                // CD-i structure check (relevant for VCD discs)
                var cdiMsgs = VcdSvcdVerifier.ValidateCdiStructures(imagePath);
                structuralMessages.AddRange(cdiMsgs);
            }

            // MDF/MDS structure validation (detect by extension)
            string ext = Path.GetExtension(imagePath);
            if (ext.Equals(".mds", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".mdf", StringComparison.OrdinalIgnoreCase))
            {
                var mdfMsgs = MdfMdsVerifier.ValidateMdfMdsImage(imagePath);
                structuralMessages.AddRange(mdfMsgs);
            }

            // NRG structure validation
            if (ext.Equals(".nrg", StringComparison.OrdinalIgnoreCase))
            {
                var nrgImage = NrgParser.Parse(imagePath);
                if (nrgImage.IsValid)
                {
                    structuralMessages.Add($"[Info] NRG format: {nrgImage.VersionString}");
                    structuralMessages.Add($"[Info] NRG: {nrgImage.SessionCount} session(s), " +
                        $"{nrgImage.TotalTrackCount} track(s), " +
                        $"recording: {(nrgImage.IsDao ? "DAO" : "TAO")}");
                    var nrgIssues = NrgParser.ValidateImage(nrgImage);
                    foreach (var issue in nrgIssues)
                        structuralMessages.Add($"[Warning] NRG: {issue}");
                }
                else
                {
                    structuralMessages.Add($"[Warning] NRG parse failed: {nrgImage.ErrorMessage}");
                }
            }

            // IMG structure validation (standalone, not CCD companion)
            if (ext.Equals(".img", StringComparison.OrdinalIgnoreCase))
            {
                var imgImage = ImgParser.Parse(imagePath);
                if (imgImage.IsValid)
                {
                    structuralMessages.Add($"[Info] IMG format: {imgImage.SectorFormatString}");
                    structuralMessages.Add($"[Info] IMG: {imgImage.TrackCount} track(s), " +
                        $"{imgImage.TotalSectorCount:N0} sectors");
                    if (imgImage.HasCcdCompanion)
                        structuralMessages.Add($"[Info] IMG: companion CCD found — {imgImage.CcdFilePath}");
                    var imgIssues = ImgParser.ValidateImage(imgImage);
                    foreach (var issue in imgIssues)
                        structuralMessages.Add($"[Warning] IMG: {issue}");
                }
                else
                {
                    structuralMessages.Add($"[Warning] IMG parse failed: {imgImage.ErrorMessage}");
                }
            }
            // Encrypted image detection
            if (ext.Equals(".obse", StringComparison.OrdinalIgnoreCase) ||
                DiscEncryptionService.IsEncryptedImage(imagePath))
            {
                long origSize = DiscEncryptionService.GetOriginalSize(imagePath);
                structuralMessages.Add("[Info] OBS encrypted disc image detected.");
                if (origSize > 0)
                    structuralMessages.Add($"[Info] Original file size: {origSize:N0} bytes ({origSize / (1024.0 * 1024.0):F1} MB)");
                structuralMessages.Add("[Info] Use DiscEncryptionService.VerifyIntegrity() with password for full verification.");
            }
        }
        catch
        {
            // Structural validation is best-effort — don't fail the whole verify
        }

        // Report structural validation results
        foreach (var msg in structuralMessages)
        {
            progress.Report(new VerifyProgress { LogLine = msg });
        }

        progress.Report(new VerifyProgress
        {
            PercentComplete = 100,
            StatusMessage = statusMsg,
            LogLine = $"{job.ChecksumType}: {checksum}" +
                (string.IsNullOrEmpty(expectedChecksum) ? "" : $"\nExpected: {expectedChecksum}")
        });

        return new VerifyResult
        {
            Passed = match,
            ChecksumType = job.ChecksumType,
            ExpectedChecksum = expectedChecksum ?? string.Empty,
            ActualChecksum = checksum,
            ChecksumMatch = match,
            ErrorSummary = match ? string.Empty : "Checksum mismatch with sidecar file."
        };
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Computes a checksum of the disc using SCSI READ(10) commands.
    /// Used on Windows/macOS where raw FileStream access to optical drives is unavailable.
    /// </summary>
    private static async Task<string> ComputeDiscChecksumViaScsiAsync(
        string devicePath, string hashType,
        IProgress<VerifyProgress> progress, CancellationToken ct,
        int pctStart, int pctEnd)
    {
        const int sectorSize = 2048;
        const int sectorsPerRead = 32;
        using var hasher = CreateHasher(hashType);
        long totalBytes = 0;
        var startTime = DateTime.UtcNow;

        using var drive = new Native.Optical.OpticalDrive(devicePath);
        if (!drive.WaitForDriveReady())
        {
            hasher.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return BitConverter.ToString(hasher.Hash ?? Array.Empty<byte>())
                .Replace("-", "").ToLowerInvariant();
        }

        // Lock the tray during checksum computation to prevent accidental ejection
        drive.PreventMediumRemoval(true);

        try
        {
        uint totalSectors = 0;
        var capacity = drive.ReadCapacity();
        if (capacity.HasValue && capacity.Value.LastLba > 0)
            totalSectors = capacity.Value.LastLba < uint.MaxValue
                ? capacity.Value.LastLba + 1
                : uint.MaxValue;

        // Fallback: try track info if READ CAPACITY returned 0
        if (totalSectors == 0)
        {
            var discInfo = drive.ReadDiscInfo();
            var lastTrack = discInfo?.LastTrackInLastSessionLsb ?? 1;
            for (uint t = 1; t <= (uint)lastTrack; t++)
            {
                var trackInfo = drive.ReadTrackInfo(t);
                if (trackInfo != null)
                {
                    var endLba = trackInfo.TrackStartAddress + trackInfo.TrackSize;
                    if (endLba > totalSectors)
                        totalSectors = endLba;
                }
            }
        }

        // Detect media type to use appropriate READ command (READ(12) for DVD/BD)
        var profile = drive.GetCurrentProfile();
        bool isDvdOrBd = profile >= 0x0010;

        uint currentLba = 0;
        int consecutiveErrors = 0;
        const int maxConsecutiveErrors = 100; // Bail out after 100 consecutive read failures

        while (currentLba < totalSectors)
        {
            ct.ThrowIfCancellationRequested();

            var count = (ushort)Math.Min(sectorsPerRead, totalSectors - currentLba);
            ScsiResult result;
            byte[] data;

            try
            {
                (result, data) = drive.ReadSectors(currentLba, count, sectorSize, isDvdOrBd);
            }
            catch (Exception ex)
            {
                // SCSI transport exception (e.g., device disconnect, I/O error).
                // Log the first occurrence for diagnostics.
                if (consecutiveErrors == 0)
                {
                    progress.Report(new VerifyProgress
                    {
                        LogLine = $"[Warning] SCSI read error at LBA {currentLba}: {ex.Message}"
                    });
                }
                consecutiveErrors += count;
                currentLba += count;
                if (consecutiveErrors >= maxConsecutiveErrors)
                {
                    progress.Report(new VerifyProgress
                    {
                        LogLine = $"[Warning] Aborting disc read after {consecutiveErrors} consecutive errors at LBA {currentLba}"
                    });
                    break;
                }
                continue;
            }

            if (result.Success && result.DataTransferred > 0)
            {
                var validBytes = Math.Min(result.DataTransferred, count * sectorSize);
                hasher.TransformBlock(data, 0, validBytes, null, 0);
                totalBytes += validBytes;
                consecutiveErrors = 0; // Reset on successful read
            }
            else
            {
                consecutiveErrors += count;
                if (consecutiveErrors >= maxConsecutiveErrors)
                {
                    progress.Report(new VerifyProgress
                    {
                        LogLine = $"[Warning] Aborting disc read after {consecutiveErrors} consecutive errors at LBA {currentLba}"
                    });
                    break;
                }
            }

            currentLba += count;

            if (currentLba % 1000 < (uint)sectorsPerRead || currentLba >= totalSectors)
            {
                var pct = totalSectors > 0
                    ? pctStart + (int)((pctEnd - pctStart) * currentLba / totalSectors)
                    : pctStart;
                var elapsed = DateTime.UtcNow - startTime;
                var remaining = FormatHelper.EstimateRemaining(elapsed, currentLba, totalSectors);
                progress.Report(new VerifyProgress
                {
                    PercentComplete = pct,
                    CurrentLba = currentLba,
                    TotalSectors = totalSectors,
                    Elapsed = elapsed,
                    Remaining = remaining,
                    StatusMessage = $"Hashing disc... {totalBytes / 1_048_576} MB",
                    // Only log every 10,000 sectors to keep log clean
                    LogLine = currentLba % 10_000 < (uint)sectorsPerRead || currentLba >= totalSectors
                        ? $"Read {totalBytes / 1_048_576} MB"
                        : null
                });
            }
        }

        hasher.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return BitConverter.ToString(hasher.Hash ?? Array.Empty<byte>())
            .Replace("-", "").ToLowerInvariant();
        }
        finally
        {
            try { drive.PreventMediumRemoval(false); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Computes a checksum of only the data tracks on a disc using SCSI commands.
    /// Uses the TOC to identify data track LBA ranges and skips audio tracks.
    /// This is essential for verifying mixed-mode CDs where READ(10) at 2048 bytes/sector
    /// cannot read audio tracks and would produce read errors.
    /// </summary>
    private static async Task<string> ComputeDiscDataOnlyChecksumViaScsiAsync(
        string devicePath, string hashType,
        IProgress<VerifyProgress> progress, CancellationToken ct,
        int pctStart, int pctEnd)
    {
        const int sectorSize = 2048;
        const int sectorsPerRead = 32;
        using var hasher = CreateHasher(hashType);
        long totalBytes = 0;
        var startTime = DateTime.UtcNow;

        using var drive = new Native.Optical.OpticalDrive(devicePath);
        if (!drive.WaitForDriveReady())
        {
            hasher.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return BitConverter.ToString(hasher.Hash ?? Array.Empty<byte>())
                .Replace("-", "").ToLowerInvariant();
        }

        drive.PreventMediumRemoval(true);

        try
        {
        // Read TOC to get data track boundaries
        var toc = drive.ReadToc();
        var dataRanges = new List<(uint startLba, uint endLba)>();

        if (toc != null)
        {
            var entries = toc.Entries
                .Where(e => e.TrackNumber != 0xAA)
                .OrderBy(e => e.TrackNumber)
                .ToList();
            var leadOut = toc.Entries.FirstOrDefault(e => e.TrackNumber == 0xAA);
            uint discEndLba = leadOut?.StartLba ?? 0;

            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry.IsData)
                {
                    uint trackStart = entry.StartLba;
                    uint trackEnd = i + 1 < entries.Count
                        ? entries[i + 1].StartLba
                        : discEndLba;

                    // On mixed-mode CDs, subtract the 150-frame pregap when the next
                    // track is audio. The pregap contains audio-format sectors that
                    // READ(10) at 2048 bytes/sector cannot read, and multi-file CUE
                    // images store the pregap in the next track's BIN file, not the
                    // data track's file. Per Red Book, the mandatory pregap is 150
                    // frames (2 seconds at 75 frames/sec).
                    if (i + 1 < entries.Count && entries[i + 1].IsAudio && trackEnd >= 150)
                        trackEnd -= 150;

                    if (trackEnd > trackStart)
                        dataRanges.Add((trackStart, trackEnd));
                }
            }
        }

        // If no data ranges found (e.g. TOC read failed), fall back to full disc read
        if (dataRanges.Count == 0)
        {
            return await ComputeDiscChecksumViaScsiAsync(devicePath, hashType, progress, ct, pctStart, pctEnd);
        }

        uint totalDataSectors = 0;
        foreach (var (s, e) in dataRanges)
            totalDataSectors += e - s;

        progress.Report(new VerifyProgress
        {
            LogLine = $"[Info] Reading {dataRanges.Count} data track(s), {totalDataSectors:N0} sectors (skipping audio tracks)"
        });

        var profile = drive.GetCurrentProfile();
        bool isDvdOrBd = profile >= 0x0010;

        uint sectorsRead = 0;
        int consecutiveErrors = 0;
        const int maxConsecutiveErrors = 100;

        foreach (var (rangeStart, rangeEnd) in dataRanges)
        {
            uint currentLba = rangeStart;
            while (currentLba < rangeEnd)
            {
                ct.ThrowIfCancellationRequested();

                var count = (ushort)Math.Min(sectorsPerRead, rangeEnd - currentLba);
                ScsiResult result;
                byte[] data;

                try
                {
                    (result, data) = drive.ReadSectors(currentLba, count, sectorSize, isDvdOrBd);
                }
                catch (Exception ex)
                {
                    if (consecutiveErrors == 0)
                    {
                        progress.Report(new VerifyProgress
                        {
                            LogLine = $"[Warning] SCSI read error at LBA {currentLba}: {ex.Message}"
                        });
                    }
                    consecutiveErrors += count;
                    currentLba += count;
                    sectorsRead += count;
                    if (consecutiveErrors >= maxConsecutiveErrors)
                    {
                        progress.Report(new VerifyProgress
                        {
                            LogLine = $"[Warning] Aborting disc read after {consecutiveErrors} consecutive errors at LBA {currentLba}"
                        });
                        goto done;
                    }
                    continue;
                }

                if (result.Success && result.DataTransferred > 0)
                {
                    var validBytes = Math.Min(result.DataTransferred, count * sectorSize);
                    hasher.TransformBlock(data, 0, validBytes, null, 0);
                    totalBytes += validBytes;
                    consecutiveErrors = 0;
                }
                else
                {
                    consecutiveErrors += count;
                    if (consecutiveErrors >= maxConsecutiveErrors)
                    {
                        progress.Report(new VerifyProgress
                        {
                            LogLine = $"[Warning] Aborting disc read after {consecutiveErrors} consecutive errors at LBA {currentLba}"
                        });
                        goto done;
                    }
                }

                currentLba += count;
                sectorsRead += count;

                if (sectorsRead % 1000 < (uint)sectorsPerRead || sectorsRead >= totalDataSectors)
                {
                    var pct = totalDataSectors > 0
                        ? pctStart + (int)((pctEnd - pctStart) * sectorsRead / totalDataSectors)
                        : pctStart;
                    var elapsed = DateTime.UtcNow - startTime;
                    var remaining = FormatHelper.EstimateRemaining(elapsed, sectorsRead, totalDataSectors);
                    progress.Report(new VerifyProgress
                    {
                        PercentComplete = pct,
                        CurrentLba = currentLba,
                        TotalSectors = totalDataSectors,
                        Elapsed = elapsed,
                        Remaining = remaining,
                        StatusMessage = $"Hashing disc data tracks... {totalBytes / 1_048_576} MB",
                        LogLine = sectorsRead % 10_000 < (uint)sectorsPerRead || sectorsRead >= totalDataSectors
                            ? $"Read {totalBytes / 1_048_576} MB"
                            : null
                    });
                }
            }
        }

        done:
        hasher.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return BitConverter.ToString(hasher.Hash ?? Array.Empty<byte>())
            .Replace("-", "").ToLowerInvariant();
        }
        finally
        {
            try { drive.PreventMediumRemoval(false); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Reads raw 2352-byte sectors from disc data tracks using READ CD and extracts
    /// user data (2048 bytes) using the same logic as the image-side methods.
    /// This is critical for Mode 2/XA discs (PS1, etc.) where READ(10) at 2048
    /// bytes/sector fails on Form 2 sectors. By reading raw sectors and extracting
    /// user data identically on both disc and image sides, checksums are consistent.
    /// When <paramref name="includeAudio"/> is true, audio tracks are also read via
    /// READ CD raw (expectedSectorType=1 for CD-DA) and hashed at the full 2352
    /// bytes per sector — matching the image-side audio track file hashing.
    /// Returns both the checksum and a flag indicating whether the read completed.
    /// </summary>
    private static async Task<(string checksum, bool complete)> ComputeDiscRawUserDataChecksumViaScsiAsync(
        string devicePath, string hashType,
        IProgress<VerifyProgress> progress, CancellationToken ct,
        int pctStart, int pctEnd, bool includeAudio = false,
        List<string>? cueBinFiles = null, List<CueTrackInfo>? cueTrackInfos = null)
    {
        const int rawSectorSize = 2352;
        const int sectorsPerRead = 16; // Smaller batches for raw reads
        using var hasher = CreateHasher(hashType);
        long totalUserBytes = 0;
        var startTime = DateTime.UtcNow;
        bool readComplete = true;

        using var drive = new Native.Optical.OpticalDrive(devicePath);
        if (!drive.WaitForDriveReady())
        {
            hasher.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return (BitConverter.ToString(hasher.Hash ?? Array.Empty<byte>())
                .Replace("-", "").ToLowerInvariant(), false);
        }

        drive.PreventMediumRemoval(true);

        try
        {
        // Read TOC to get data track boundaries (and audio if requested)
        var toc = drive.ReadToc();

        // Build an ordered list of disc ranges — one per BIN file, in file order.
        // Each entry specifies the disc LBA range to read and whether the file is audio.
        // Processing in file order ensures the hash is computed in the same order as
        // the image-side method (ComputeMultiFileRawUserDataChecksumAsync), which
        // iterates through files sequentially.
        var orderedRanges = new List<(uint startLba, uint endLba, bool isAudio)>();
        bool usedCueRanges = false;

        if (cueBinFiles != null && cueTrackInfos != null && cueBinFiles.Count > 1 && toc != null)
        {
            // Build audio-file index set (same logic as image side)
            var audioFileIndices = new HashSet<int>();
            foreach (var t in cueTrackInfos)
                if (t.IsAudio) audioFileIndices.Add(t.FileIndex);
            foreach (var t in cueTrackInfos)
                if (!t.IsAudio) audioFileIndices.Remove(t.FileIndex);

            // Build a lookup from track number → TOC start LBA
            var tocLookup = new Dictionary<int, uint>();
            foreach (var e in toc.Entries)
                if (e.TrackNumber != 0xAA)
                    tocLookup[(int)e.TrackNumber] = e.StartLba;

            bool allTracksFound = true;
            for (int i = 0; i < cueBinFiles.Count; i++)
            {
                if (!File.Exists(cueBinFiles[i])) continue;
                var fileSectors = (uint)(new FileInfo(cueBinFiles[i]).Length / rawSectorSize);
                if (fileSectors == 0) continue;

                bool isAudioFile = audioFileIndices.Contains(i);
                if (isAudioFile && !includeAudio) continue;

                // Find the first CUE track that belongs to this file
                var trackInfo = cueTrackInfos.FirstOrDefault(t => t.FileIndex == i);
                if (trackInfo == null || !tocLookup.TryGetValue(trackInfo.TrackNumber, out uint tocLba))
                {
                    allTracksFound = false;
                    break;
                }

                // Determine the disc start LBA for this file.
                // TOC always reports the INDEX 01 position. If the BIN file includes
                // a pregap before INDEX 01, the file starts earlier on disc by
                // Index01Frames sectors. This is indicated by either:
                //   - INDEX 00 00:00:00 + INDEX 01 00:02:00 (standard Redump format)
                //   - INDEX 01 00:02:00 alone (no explicit INDEX 00 — some tools omit it)
                // In both cases, Index01Frames > 0 tells us the file starts before
                // INDEX 01 on disc. The PREGAP directive (file has NO pregap data)
                // sets HasPregapDirective=true and Index01Frames remains 0, so this
                // adjustment correctly does NOT apply for PREGAP.
                // Per CUE spec, INDEX values are offsets relative to the FILE start.
                uint discStartLba = tocLba;
                if (trackInfo.Index01Frames > 0)
                {
                    uint pregapFrames = (uint)trackInfo.Index01Frames;
                    if (discStartLba >= pregapFrames)
                        discStartLba -= pregapFrames;
                    else
                        discStartLba = 0;
                }

                uint discEndLba = discStartLba + fileSectors;

                if (!isAudioFile)
                {
                    // Check if this data file's range extends into the pregap zone of
                    // the next audio track. On mixed-mode CDs, the 150-frame pregap
                    // before an audio track contains audio-format sectors that many
                    // drives reject with expectedSectorType=0 (ASC=0x64 "Illegal mode
                    // for this track"). If the next audio track's pregap is NOT in its
                    // own file (Index01Frames=0 or HasPregapDirective=true), the pregap may be
                    // at the end of this data file.
                    //
                    // Find the next track by track number to check if it's audio.
                    var nextTrack = cueTrackInfos
                        .Where(t => t.TrackNumber > trackInfo.TrackNumber)
                        .OrderBy(t => t.TrackNumber)
                        .FirstOrDefault();

                    if (nextTrack != null && nextTrack.IsAudio &&
                        tocLookup.TryGetValue(nextTrack.TrackNumber, out uint nextTocLba))
                    {
                        // The pregap zone starts 150 frames before the next track's
                        // INDEX 01 position (per Red Book: mandatory 2-second pregap).
                        uint pregapStart = nextTocLba >= 150 ? nextTocLba - 150 : 0;

                        // Only split if the data file's range actually extends into
                        // the pregap zone. If the audio file already includes its own
                        // pregap (Index01Frames>0), the data file should not extend
                        // past its own data sectors.
                        if (discEndLba > pregapStart)
                        {
                            // Trim data range to exclude pregap sectors
                            uint dataEnd = Math.Max(discStartLba, pregapStart);
                            if (dataEnd > discStartLba)
                                orderedRanges.Add((discStartLba, dataEnd, false));

                            // Add pregap as audio range so it's read with
                            // expectedSectorType=1 (CD-DA). The image side hashes
                            // these sectors correctly: no sync → skip when
                            // !includeAudio, or hash full 2352 when includeAudio.
                            if (includeAudio && discEndLba > dataEnd)
                                orderedRanges.Add((dataEnd, discEndLba, true));

                            continue;
                        }
                    }

                    orderedRanges.Add((discStartLba, discEndLba, false));
                }
                else
                {
                    orderedRanges.Add((discStartLba, discEndLba, true));
                }
            }

            usedCueRanges = allTracksFound && orderedRanges.Count > 0;
            if (!allTracksFound)
                orderedRanges.Clear(); // Fall back to TOC-only ranges below

            if (usedCueRanges)
            {
                // Log the calculated disc ranges for debugging — helps diagnose
                // checksum mismatches by showing exactly what LBAs are being read.
                var sb = new StringBuilder();
                sb.Append("[Info] Multi-file CUE disc ranges (");
                sb.Append(orderedRanges.Count);
                sb.Append(" range(s)):");
                foreach (var (s, e, isAud) in orderedRanges)
                    sb.Append($" [{s}-{e}) {(isAud ? "audio" : "data")}");
                progress.Report(new VerifyProgress { LogLine = sb.ToString() });
            }
        }

        // Single-file CUE: the BIN file contains all tracks (data + audio) in a single
        // contiguous file. Use the BIN file size to determine the total sector count and
        // read from disc starting at LBA 0. The image side reads sequentially through the
        // entire file, hashing data sectors (sync pattern → 2048-byte user data) and
        // optionally audio sectors (no sync → full 2352 bytes). The disc side must read
        // the same sectors in the same order to produce matching checksums.
        if (!usedCueRanges && cueBinFiles != null && cueTrackInfos != null &&
            cueBinFiles.Count == 1 && toc != null)
        {
            var binFile = cueBinFiles[0];
            if (File.Exists(binFile))
            {
                var fileSectors = (uint)(new FileInfo(binFile).Length / rawSectorSize);
                if (fileSectors > 0)
                {
                    // For single-file CUE, the entire BIN file is one contiguous range
                    // starting at LBA 0. Build per-track ranges from the TOC so the
                    // disc-side read loop uses the correct expectedSectorType for each
                    // range (0 = data, 1 = audio CD-DA).
                    //
                    // On mixed-mode CDs, the 150-frame pregap between data and audio
                    // tracks contains audio-format sectors. Many drives return "Illegal
                    // mode for this track" (ASC=0x64) when these pregap sectors are read
                    // with expectedSectorType=0. To avoid this, data ranges are trimmed
                    // to exclude the pregap, and (when includeAudio is true) audio ranges
                    // are extended backward to include it. This matches the pregap
                    // handling in the TOC-only fallback path and in the ReadEngine's
                    // GetExpectedSectorType function.
                    //
                    // On the image side, the sync-pattern-based extraction handles this
                    // correctly: pregap audio sectors (no sync) are skipped when
                    // includeAudio is false, or hashed at full 2352 bytes when true.
                    var entries = toc.Entries
                        .Where(e => e.TrackNumber != 0xAA)
                        .OrderBy(e => e.TrackNumber)
                        .ToList();
                    var leadOut = toc.Entries.FirstOrDefault(e => e.TrackNumber == 0xAA);
                    uint discEndLba = leadOut?.StartLba ?? fileSectors;

                    // Cap disc end by BIN file sector count to avoid reading beyond
                    // what the image file contains (BIN may be shorter than lead-out)
                    uint effectiveEnd = Math.Min(discEndLba, fileSectors);

                    for (int i = 0; i < entries.Count; i++)
                    {
                        var entry = entries[i];
                        uint trackStart = entry.StartLba;
                        uint trackEnd = i + 1 < entries.Count
                            ? entries[i + 1].StartLba
                            : effectiveEnd;

                        // Cap track end to not exceed BIN file bounds
                        if (trackEnd > effectiveEnd) trackEnd = effectiveEnd;
                        if (trackStart >= effectiveEnd) continue;

                        if (entry.IsData)
                        {
                            // On mixed-mode CDs, the 150-frame pregap before the next
                            // audio track contains audio-format sectors. Many drives
                            // return "Illegal mode for this track" (ASC=0x64) when
                            // these are read with expectedSectorType=0 (data). Trim
                            // the data range to exclude the pregap, matching how the
                            // TOC-only fallback path handles this (see below).
                            // Per Red Book, the mandatory pregap is 150 frames
                            // (2 seconds at 75 frames/sec).
                            if (i + 1 < entries.Count && entries[i + 1].IsAudio &&
                                trackEnd >= 150)
                                trackEnd -= 150;

                            if (trackEnd > trackStart)
                                orderedRanges.Add((trackStart, trackEnd, false));
                        }
                        else if (entry.IsAudio && includeAudio)
                        {
                            // Include the 150-frame pregap that belongs to this audio
                            // track. The pregap starts 150 frames before INDEX 01. For
                            // the first audio track after a data track, the pregap was
                            // excluded from the data range above, so include it here.
                            // Reading with expectedSectorType=1 (CD-DA) is correct for
                            // audio-format pregap sectors.
                            uint audioStart = trackStart;
                            if (i > 0 && entries[i - 1].IsData && audioStart >= 150)
                                audioStart -= 150;

                            if (trackEnd > audioStart)
                                orderedRanges.Add((audioStart, trackEnd, true));
                        }
                    }

                    usedCueRanges = orderedRanges.Count > 0;
                }
            }
        }

        // Fallback: use cumulative offset approach when TOC matching isn't possible
        // (e.g., no TOC, or CUE track numbers don't match TOC track numbers)
        if (!usedCueRanges && cueBinFiles != null && cueTrackInfos != null && cueBinFiles.Count > 1)
        {
            var audioFileIndices = new HashSet<int>();
            foreach (var t in cueTrackInfos)
                if (t.IsAudio) audioFileIndices.Add(t.FileIndex);
            foreach (var t in cueTrackInfos)
                if (!t.IsAudio) audioFileIndices.Remove(t.FileIndex);

            // Pre-compute file sector counts to avoid redundant File.Exists/FileInfo
            var fileSectorCounts = new uint[cueBinFiles.Count];
            for (int i = 0; i < cueBinFiles.Count; i++)
            {
                if (File.Exists(cueBinFiles[i]))
                    fileSectorCounts[i] = (uint)(new FileInfo(cueBinFiles[i]).Length / rawSectorSize);
            }

            uint cumulativeSectors = 0;
            for (int i = 0; i < cueBinFiles.Count; i++)
            {
                var fileSectors = fileSectorCounts[i];
                if (fileSectors == 0) continue;

                bool isAudioFile = audioFileIndices.Contains(i);
                if (isAudioFile && !includeAudio)
                {
                    cumulativeSectors += fileSectors;
                    continue;
                }

                uint rangeStart = cumulativeSectors;
                uint rangeEnd = cumulativeSectors + fileSectors;

                if (!isAudioFile)
                {
                    // Check if the next file is audio — if so, the last 150 sectors
                    // of this data file may be audio-format pregap sectors that fail
                    // with expectedSectorType=0 on many drives (ASC=0x64). Split the
                    // range to read those sectors with expectedSectorType=1 (CD-DA).
                    int nextFileIdx = -1;
                    for (int j = i + 1; j < cueBinFiles.Count; j++)
                    {
                        if (fileSectorCounts[j] > 0)
                        {
                            nextFileIdx = j;
                            break;
                        }
                    }

                    if (nextFileIdx >= 0 && audioFileIndices.Contains(nextFileIdx) &&
                        fileSectors > 150)
                    {
                        // Trim 150-frame pregap from data range
                        uint dataEnd = rangeEnd - 150;
                        orderedRanges.Add((rangeStart, dataEnd, false));

                        if (includeAudio)
                            orderedRanges.Add((dataEnd, rangeEnd, true));
                    }
                    else
                    {
                        orderedRanges.Add((rangeStart, rangeEnd, false));
                    }
                }
                else
                {
                    orderedRanges.Add((rangeStart, rangeEnd, true));
                }

                cumulativeSectors += fileSectors;
            }

            usedCueRanges = orderedRanges.Count > 0;

            if (usedCueRanges)
            {
                progress.Report(new VerifyProgress
                {
                    LogLine = $"[Info] Using cumulative-offset fallback for multi-file CUE " +
                              $"({orderedRanges.Count} range(s))"
                });
            }
        }

        if (!usedCueRanges && toc != null)
        {
            var entries = toc.Entries
                .Where(e => e.TrackNumber != 0xAA)
                .OrderBy(e => e.TrackNumber)
                .ToList();
            var leadOut = toc.Entries.FirstOrDefault(e => e.TrackNumber == 0xAA);
            uint discEndLba = leadOut?.StartLba ?? 0;

            // When we have a single BIN file, cap the disc read range by the BIN file
            // size to prevent reading beyond what the image contains. The BIN file may
            // be shorter than the disc lead-out (e.g., if the ripping tool stopped early
            // or the lead-out position differs slightly from the actual data extent).
            if (cueBinFiles != null && cueBinFiles.Count == 1 && File.Exists(cueBinFiles[0]))
            {
                var binSectors = (uint)(new FileInfo(cueBinFiles[0]).Length / rawSectorSize);
                if (binSectors > 0 && (discEndLba == 0 || binSectors < discEndLba))
                    discEndLba = binSectors;
            }

            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                uint trackStart = entry.StartLba;
                uint trackEnd = i + 1 < entries.Count
                    ? entries[i + 1].StartLba
                    : discEndLba;

                if (entry.IsData)
                {
                    // On mixed-mode CDs, subtract the 150-frame pregap when the next
                    // track is audio. The pregap contains audio-format sectors that are
                    // stored in the next track's BIN file (not the data track's file)
                    // in multi-file CUE sheets. Per Red Book, the mandatory pregap is
                    // 150 frames (2 seconds at 75 frames/sec).
                    if (i + 1 < entries.Count && entries[i + 1].IsAudio && trackEnd >= 150)
                        trackEnd -= 150;

                    if (trackEnd > trackStart)
                        orderedRanges.Add((trackStart, trackEnd, false));
                }
                else if (entry.IsAudio && includeAudio)
                {
                    // Audio tracks: include the 150-frame pregap that belongs to this
                    // audio track (stored in the audio BIN file on the image side).
                    // The pregap starts 150 frames before the track's INDEX 01 position.
                    // For the first audio track after a data track, the pregap was
                    // subtracted from the data range above, so include it here.
                    uint audioStart = trackStart;
                    if (i > 0 && entries[i - 1].IsData && audioStart >= 150)
                        audioStart -= 150;

                    if (trackEnd > audioStart)
                        orderedRanges.Add((audioStart, trackEnd, true));
                }
            }
        }

        if (orderedRanges.Count == 0)
        {
            // No data tracks found — fall back to cooked disc checksum
            var fallback = await ComputeDiscChecksumViaScsiAsync(devicePath, hashType, progress, ct, pctStart, pctEnd);
            return (fallback, true);
        }

        uint totalDataSectors = 0;
        uint totalAudioSectors = 0;
        int dataRangeCount = 0;
        int audioRangeCount = 0;
        foreach (var (s, e, isAudio) in orderedRanges)
        {
            if (isAudio)
            {
                totalAudioSectors += e - s;
                audioRangeCount++;
            }
            else
            {
                totalDataSectors += e - s;
                dataRangeCount++;
            }
        }

        uint totalSectorsToRead = totalDataSectors + totalAudioSectors;

        if (includeAudio && totalAudioSectors > 0)
        {
            progress.Report(new VerifyProgress
            {
                LogLine = $"[Info] Reading {dataRangeCount} data track(s) ({totalDataSectors:N0} sectors) " +
                          $"and {audioRangeCount} audio track(s) ({totalAudioSectors:N0} sectors) via READ CD raw"
            });
        }
        else
        {
            progress.Report(new VerifyProgress
            {
                LogLine = $"[Info] Reading {dataRangeCount} data track(s), {totalDataSectors:N0} sectors " +
                          $"via READ CD raw (skipping audio tracks)"
            });
        }

        uint sectorsRead = 0;
        int consecutiveErrors = 0;
        // Higher threshold since we're reading raw: errors are real disc damage,
        // not Mode 2 Form 2 false-positives. Also accounts for batch size.
        const int maxConsecutiveErrors = 512;
        var sectorBuffer = new byte[rawSectorSize];

        // Process all ranges in a single loop, in file order. This ensures the hash
        // is computed in the same order as the image-side method which iterates
        // through BIN files sequentially.
        foreach (var (rangeStart, rangeEnd, rangeIsAudio) in orderedRanges)
        {
            uint currentLba = rangeStart;
            consecutiveErrors = 0; // Reset for each range
            byte expectedSectorType = rangeIsAudio ? (byte)1 : (byte)0;

            while (currentLba < rangeEnd)
            {
                ct.ThrowIfCancellationRequested();

                var count = (uint)Math.Min(sectorsPerRead, rangeEnd - currentLba);
                Native.Scsi.ScsiResult result;
                byte[] data;

                try
                {
                    // READ CD with expectedSectorType=0 (any type) for data tracks
                    // to handle Mode 1 and Mode 2 (Form 1 + Form 2) transparently,
                    // or expectedSectorType=1 (CD-DA) for audio tracks.
                    (result, data) = drive.ReadCdRaw(currentLba, count, expectedSectorType: expectedSectorType);
                }
                catch (Exception ex)
                {
                    // Batch read failed — try single-sector reads to maximize recovery.
                    // TryReadAndHashSingleSector handles CD-DA retry for pregap sectors.
                    bool anySuccess = false;
                    for (uint s = 0; s < count && currentLba + s < rangeEnd; s++)
                    {
                        var (ok, hashed) = TryReadAndHashSingleSector(
                            drive, currentLba + s, expectedSectorType, rangeIsAudio,
                            sectorBuffer, hasher, includeAudio);
                        if (ok)
                        {
                            totalUserBytes += hashed;
                            anySuccess = true;
                            consecutiveErrors = 0;
                        }
                        else
                        {
                            consecutiveErrors++;
                        }
                    }

                    if (!anySuccess)
                    {
                        progress.Report(new VerifyProgress
                        {
                            LogLine = rangeIsAudio
                                ? $"[Warning] READ CD audio error at LBA {currentLba}: {ex.Message}"
                                : $"[Warning] READ CD error at LBA {currentLba}: {ex.Message}"
                        });
                    }

                    currentLba += count;
                    sectorsRead += count;

                    if (consecutiveErrors >= maxConsecutiveErrors)
                    {
                        progress.Report(new VerifyProgress
                        {
                            LogLine = $"[Warning] Aborting disc read after {consecutiveErrors} consecutive " +
                                      $"errors at LBA {currentLba}"
                        });
                        readComplete = false;
                        goto done;
                    }
                    continue;
                }

                if (result.Success && result.DataTransferred >= (int)(count * rawSectorSize))
                {
                    // Process each raw sector in the batch using unified sync-pattern-based
                    // classification. This ensures disc-side and image-side use the exact
                    // same per-sector logic:
                    //   - Sectors WITH sync pattern → data → extract 2048 bytes user data
                    //   - Sectors WITHOUT sync pattern → audio →
                    //       if includeAudio: hash full 2352 bytes
                    //       else: skip (not hashed)
                    // This matches how DiscImageCreator, Redumper, and CUETools handle
                    // sector verification — each sector is individually classified by its
                    // content, not by the track/range metadata. This correctly handles
                    // pregap sectors between data and audio tracks, mixed-mode transitions,
                    // and single-file CUE sheets where data and audio are interleaved.
                    for (int s = 0; s < (int)count; s++)
                    {
                        Array.Copy(data, s * rawSectorSize, sectorBuffer, 0, rawSectorSize);
                        totalUserBytes += ExtractAndHashRawSectorUserDataOrAudio(sectorBuffer, hasher, includeAudio);
                    }
                    consecutiveErrors = 0;
                }
                else if (result.Success && result.DataTransferred > 0)
                {
                    // Partial read — process as many complete sectors as we got
                    int completeSectors = result.DataTransferred / rawSectorSize;
                    for (int s = 0; s < completeSectors; s++)
                    {
                        Array.Copy(data, s * rawSectorSize, sectorBuffer, 0, rawSectorSize);
                        totalUserBytes += ExtractAndHashRawSectorUserDataOrAudio(sectorBuffer, hasher, includeAudio);
                    }
                    if (completeSectors > 0)
                        consecutiveErrors = (int)count - completeSectors;
                    else
                        consecutiveErrors += (int)count;
                    if (consecutiveErrors >= maxConsecutiveErrors)
                    {
                        progress.Report(new VerifyProgress
                        {
                            LogLine = $"[Warning] Aborting disc read after {consecutiveErrors} consecutive " +
                                      $"errors at LBA {currentLba}"
                        });
                        readComplete = false;
                        goto done;
                    }
                }
                else
                {
                    // Full batch failure — try single-sector reads with CD-DA fallback
                    for (uint s = 0; s < count && currentLba + s < rangeEnd; s++)
                    {
                        var (ok, hashed) = TryReadAndHashSingleSector(
                            drive, currentLba + s, expectedSectorType, rangeIsAudio,
                            sectorBuffer, hasher, includeAudio);
                        if (ok)
                        {
                            totalUserBytes += hashed;
                            consecutiveErrors = 0;
                        }
                        else
                        {
                            consecutiveErrors++;
                        }
                    }

                    if (consecutiveErrors >= maxConsecutiveErrors)
                    {
                        progress.Report(new VerifyProgress
                        {
                            LogLine = $"[Warning] Aborting disc read after {consecutiveErrors} consecutive " +
                                      $"errors at LBA {currentLba}"
                        });
                        readComplete = false;
                        goto done;
                    }
                }

                currentLba += count;
                sectorsRead += count;

                if (sectorsRead % 1000 < sectorsPerRead || sectorsRead >= totalSectorsToRead)
                {
                    var pct = totalSectorsToRead > 0
                        ? pctStart + (int)((pctEnd - pctStart) * sectorsRead / totalSectorsToRead)
                        : pctStart;
                    var elapsed = DateTime.UtcNow - startTime;
                    var remaining = FormatHelper.EstimateRemaining(elapsed, sectorsRead, totalSectorsToRead);
                    progress.Report(new VerifyProgress
                    {
                        PercentComplete = pct,
                        CurrentLba = currentLba,
                        TotalSectors = totalSectorsToRead,
                        Elapsed = elapsed,
                        Remaining = remaining,
                        StatusMessage = $"Reading disc raw sectors... {totalUserBytes / 1_048_576} MB",
                        LogLine = sectorsRead % 10_000 < (uint)sectorsPerRead || sectorsRead >= totalSectorsToRead
                            ? $"Read {totalUserBytes / 1_048_576} MB"
                            : null
                    });
                }
            }
        }

        done:
        hasher.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return (BitConverter.ToString(hasher.Hash ?? Array.Empty<byte>())
            .Replace("-", "").ToLowerInvariant(), readComplete);
        }
        finally
        {
            try { drive.PreventMediumRemoval(false); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Extracts user data from a single 2352-byte raw CD sector and feeds it to a
    /// hash algorithm. Detects the sector type (Mode 1 / Mode 2) via the sync
    /// pattern and mode byte, and extracts the appropriate 2048-byte user data region.
    /// Audio sectors (no sync pattern) are skipped.
    /// </summary>
    private static void ExtractAndHashRawSectorUserData(byte[] sectorBuffer, HashAlgorithm hasher)
    {
        const int userDataSize = 2048;

        // Check for the 12-byte CD sync pattern: 00 FF FF FF FF FF FF FF FF FF FF 00
        bool hasSyncPattern = sectorBuffer[0] == 0x00 && sectorBuffer[11] == 0x00;
        if (hasSyncPattern)
        {
            for (int si = 1; si <= 10 && hasSyncPattern; si++)
                hasSyncPattern = sectorBuffer[si] == 0xFF;
        }

        if (hasSyncPattern)
        {
            byte mode = sectorBuffer[15]; // Mode byte in the sector header
            if (mode == 2)
            {
                // Mode 2 (Form 1 or Form 2): user data at offset 24
                // (after 12 sync + 4 header + 8 subheader)
                hasher.TransformBlock(sectorBuffer, 24, userDataSize, null, 0);
            }
            else
            {
                // Mode 1 (or Mode 0): user data at offset 16
                // (after 12 sync + 4 header)
                hasher.TransformBlock(sectorBuffer, 16, userDataSize, null, 0);
            }
        }
        // else: audio sector (no sync pattern) — skip for user-data comparison
    }

    /// <summary>
    /// Reads a single raw sector from disc with the given expectedSectorType, falling
    /// back to CD-DA (type 1) if the initial type fails. Copies the result into
    /// <paramref name="sectorBuffer"/> and hashes it via <see cref="ExtractAndHashRawSectorUserDataOrAudio"/>.
    /// Returns (success, bytesHashed).
    /// </summary>
    private static (bool success, int bytesHashed) TryReadAndHashSingleSector(
        Native.Optical.OpticalDrive drive, uint lba,
        byte expectedSectorType, bool rangeIsAudio,
        byte[] sectorBuffer, HashAlgorithm hasher, bool includeAudio)
    {
        const int rawSectorSize = 2352;
        try
        {
            var (result, data) = drive.ReadCdRaw(lba, 1, expectedSectorType: expectedSectorType);
            if (result.Success && result.DataTransferred >= rawSectorSize)
            {
                Array.Copy(data, 0, sectorBuffer, 0, rawSectorSize);
                int hashed = ExtractAndHashRawSectorUserDataOrAudio(sectorBuffer, hasher, includeAudio);
                return (true, hashed);
            }
        }
        catch { /* initial type failed — fall through to CD-DA retry */ }

        // Retry with CD-DA type if the initial type was data (0).
        // Pregap sectors near data/audio boundaries are audio-format
        // and some drives reject them with expectedSectorType=0.
        if (!rangeIsAudio && expectedSectorType == 0)
        {
            try
            {
                var (retryResult, retryData) = drive.ReadCdRaw(lba, 1, expectedSectorType: 1);
                if (retryResult.Success && retryResult.DataTransferred >= rawSectorSize)
                {
                    Array.Copy(retryData, 0, sectorBuffer, 0, rawSectorSize);
                    int hashed = ExtractAndHashRawSectorUserDataOrAudio(sectorBuffer, hasher, includeAudio);
                    return (true, hashed);
                }
            }
            catch { /* both types failed */ }
        }

        return (false, 0);
    }

    /// <summary>
    /// Extracts and hashes user data from a raw 2352-byte sector, or hashes the full
    /// sector if it is an audio sector and <paramref name="includeAudio"/> is true.
    /// This is used for single-file CUE verification where both data and audio sectors
    /// are interleaved in the same BIN file.
    /// </summary>
    /// <summary>
    /// Classifies a raw 2352-byte sector by its sync pattern and hashes accordingly.
    /// Returns the number of bytes actually hashed (2048 for data, 2352 for audio, 0 if skipped).
    /// </summary>
    private static int ExtractAndHashRawSectorUserDataOrAudio(
        byte[] sectorBuffer, HashAlgorithm hasher, bool includeAudio)
    {
        const int rawSectorSize = 2352;
        const int userDataSize = 2048;

        // Check for the 12-byte CD sync pattern: 00 FF FF FF FF FF FF FF FF FF FF 00
        bool hasSyncPattern = sectorBuffer[0] == 0x00 && sectorBuffer[11] == 0x00;
        if (hasSyncPattern)
        {
            for (int si = 1; si <= 10 && hasSyncPattern; si++)
                hasSyncPattern = sectorBuffer[si] == 0xFF;
        }

        if (hasSyncPattern)
        {
            byte mode = sectorBuffer[15]; // Mode byte in the sector header
            if (mode == 2)
            {
                // Mode 2 (Form 1 or Form 2): user data at offset 24
                hasher.TransformBlock(sectorBuffer, 24, userDataSize, null, 0);
            }
            else
            {
                // Mode 1 (or Mode 0): user data at offset 16
                hasher.TransformBlock(sectorBuffer, 16, userDataSize, null, 0);
            }
            return userDataSize;
        }
        else if (includeAudio)
        {
            // Audio sector (no sync pattern) — hash full 2352 bytes to match
            // disc-side READ CD audio track behavior
            hasher.TransformBlock(sectorBuffer, 0, rawSectorSize, null, 0);
            return rawSectorSize;
        }
        // else: audio sector — skip for data-only comparison
        return 0;
    }

    private static async Task<string> ComputeChecksumAsync(
        string sourcePath, string hashType,
        IProgress<VerifyProgress> progress, CancellationToken ct,
        int pctStart, int pctEnd)
    {
        using var hasher = CreateHasher(hashType);
        var buffer = new byte[2048 * 64];
        long read = 0;
        var startTime = DateTime.UtcNow;

        try
        {
            await using var stream = new FileStream(sourcePath, FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite, 65536, true);
            var total = stream.CanSeek ? stream.Length : 0;
            int n;
            while ((n = await stream.ReadAsync(buffer, ct)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                hasher.TransformBlock(buffer, 0, n, null, 0);
                read += n;
                if (total > 0)
                {
                    var pct = pctStart + (int)((pctEnd - pctStart) * read / total);
                    var elapsed = DateTime.UtcNow - startTime;
                    var remaining = FormatHelper.EstimateRemaining(elapsed, read, total);
                    // Report progress for UI bar, but only add log entry every ~10 MB
                    progress.Report(new VerifyProgress
                    {
                        PercentComplete = pct,
                        Elapsed = elapsed,
                        Remaining = remaining,
                        StatusMessage = $"Hashing... {read / 1_048_576} MB",
                        LogLine = read % (10 * 1_048_576) < n
                            ? $"Read {read / 1_048_576} MB"
                            : null
                    });
                }
            }
        }
        catch (IOException) { /* partial result */ }

        hasher.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return BitConverter.ToString(hasher.Hash ?? Array.Empty<byte>())
            .Replace("-", "").ToLowerInvariant();
    }

    private static async Task<string> ComputeFileChecksumAsync(
        string filePath, string hashType,
        IProgress<VerifyProgress> progress, CancellationToken ct,
        int pctStart, int pctEnd)
    {
        using var hasher = CreateHasher(hashType);
        await using var stream = new FileStream(filePath, FileMode.Open,
            FileAccess.Read, FileShare.Read, 65536, true);
        var total = stream.Length;
        var buffer = new byte[2048 * 64];
        long read = 0;
        var startTime = DateTime.UtcNow;
        int n;
        while ((n = await stream.ReadAsync(buffer, ct)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            hasher.TransformBlock(buffer, 0, n, null, 0);
            read += n;
            var pct = total > 0
                ? pctStart + (int)((pctEnd - pctStart) * read / total)
                : pctStart;
            var elapsed = DateTime.UtcNow - startTime;
            var remaining = FormatHelper.EstimateRemaining(elapsed, read, total);
            // Report progress for UI bar, but only add log entry every ~10 MB
            progress.Report(new VerifyProgress
            {
                PercentComplete = pct,
                Elapsed = elapsed,
                Remaining = remaining,
                StatusMessage = $"Hashing {Path.GetFileName(filePath)}... {read / 1_048_576} MB",
                LogLine = read % (10 * 1_048_576) < n
                    ? $"Read {read / 1_048_576} MB"
                    : null
            });
        }
        hasher.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return BitConverter.ToString(hasher.Hash ?? Array.Empty<byte>())
            .Replace("-", "").ToLowerInvariant();
    }

    /// <summary>
    /// Computes a checksum of only the user data portion (2048 bytes) from each
    /// 2352-byte raw sector in a BIN image file. This allows comparing raw images
    /// against discs read at 2048 bytes/sector.
    /// Mode 1 raw sector layout: 12 sync + 4 header + 2048 user data + 288 EDC/ECC
    /// Audio sectors (no sync pattern) are skipped because READ(10) at 2048 bytes/sector
    /// cannot read audio tracks — including them would cause a guaranteed checksum mismatch.
    /// When <paramref name="includeAudio"/> is true (single-file CUE with audio verification),
    /// sectors without a sync pattern (audio sectors) are hashed at the full 2352 bytes per
    /// sector instead of being skipped. This matches the disc-side behavior which reads audio
    /// tracks at full raw sector size.
    /// </summary>
    private static async Task<string> ComputeRawImageUserDataChecksumAsync(
        string filePath, string hashType,
        IProgress<VerifyProgress> progress, CancellationToken ct,
        int pctStart, int pctEnd, bool includeAudio = false)
    {
        const int rawSectorSize = 2352;

        using var hasher = CreateHasher(hashType);
        await using var stream = new FileStream(filePath, FileMode.Open,
            FileAccess.Read, FileShare.Read, 65536, true);
        var total = stream.Length;
        var sectorBuffer = new byte[rawSectorSize];
        long read = 0;
        var startTime = DateTime.UtcNow;
        int n;
        while ((n = await stream.ReadAsync(sectorBuffer.AsMemory(0, rawSectorSize), ct)) >= rawSectorSize)
        {
            ct.ThrowIfCancellationRequested();
            ExtractAndHashRawSectorUserDataOrAudio(sectorBuffer, hasher, includeAudio);

            read += rawSectorSize;
            var pct = total > 0
                ? pctStart + (int)((pctEnd - pctStart) * read / total)
                : pctStart;
            if (read % (rawSectorSize * 1000) < rawSectorSize)
            {
                var elapsed = DateTime.UtcNow - startTime;
                var remaining = FormatHelper.EstimateRemaining(elapsed, read, total);
                progress.Report(new VerifyProgress
                {
                    PercentComplete = pct,
                    Elapsed = elapsed,
                    Remaining = remaining,
                    StatusMessage = $"Extracting user data from raw image... {read / 1_048_576} MB",
                    LogLine = $"Processed {read / 1_048_576} MB"
                });
            }
        }
        hasher.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return BitConverter.ToString(hasher.Hash ?? Array.Empty<byte>())
            .Replace("-", "").ToLowerInvariant();
    }

    /// <summary>
    /// Computes a checksum across multiple BIN files from a multi-file CUE sheet,
    /// extracting only user data (2048 bytes) from each 2352-byte raw sector.
    /// Audio tracks (identified by CUE TRACK mode) are skipped unless
    /// <paramref name="includeAudio"/> is true — in that case audio track files
    /// are hashed at the full 2352 bytes per sector (audio sectors contain no
    /// sync/header/ECC structure) so they can be compared against raw disc reads.
    /// </summary>
    private static async Task<string> ComputeMultiFileRawUserDataChecksumAsync(
        List<string> binFiles, List<CueTrackInfo> trackInfos,
        string hashType, IProgress<VerifyProgress> progress, CancellationToken ct,
        int pctStart, int pctEnd, bool includeAudio = false)
    {
        const int rawSectorSize = 2352;

        // Build a set of file indices that contain audio-only tracks
        var audioFileIndices = new HashSet<int>();
        foreach (var t in trackInfos)
        {
            if (t.IsAudio) audioFileIndices.Add(t.FileIndex);
        }
        // A file is data if any non-audio track references it
        foreach (var t in trackInfos)
        {
            if (!t.IsAudio) audioFileIndices.Remove(t.FileIndex);
        }

        using var hasher = CreateHasher(hashType);
        long totalBytes = binFiles.Where(File.Exists).Sum(f => new FileInfo(f).Length);
        long bytesRead = 0;
        var startTime = DateTime.UtcNow;
        var sectorBuffer = new byte[rawSectorSize];

        for (int fileIndex = 0; fileIndex < binFiles.Count; fileIndex++)
        {
            var binFile = binFiles[fileIndex];
            if (!File.Exists(binFile)) continue;

            bool isAudioFile = audioFileIndices.Contains(fileIndex);

            // Skip files that contain only audio tracks when audio verification is off
            if (isAudioFile && !includeAudio)
            {
                bytesRead += new FileInfo(binFile).Length;
                progress.Report(new VerifyProgress
                {
                    LogLine = $"[Info] Skipping audio track file: {Path.GetFileName(binFile)}"
                });
                continue;
            }

            if (isAudioFile)
            {
                progress.Report(new VerifyProgress
                {
                    LogLine = $"[Info] Verifying audio track file: {Path.GetFileName(binFile)}"
                });
            }

            await using var stream = new FileStream(binFile, FileMode.Open,
                FileAccess.Read, FileShare.Read, 65536, true);

            int n;
            while ((n = await stream.ReadAsync(sectorBuffer.AsMemory(0, rawSectorSize), ct)) >= rawSectorSize)
            {
                ct.ThrowIfCancellationRequested();

                // Use unified sync-pattern-based sector classification for ALL files
                // (both data and audio). Each sector is individually classified:
                //   - Sectors WITH sync pattern → data → extract 2048 bytes user data
                //   - Sectors WITHOUT sync pattern → audio →
                //       if includeAudio: hash full 2352 bytes
                //       else: skip (not hashed)
                // This matches the disc-side logic exactly, ensuring consistent
                // checksums regardless of how the ripping tool organized the tracks
                // into files. This approach follows how DiscImageCreator, Redumper,
                // and CUETools handle verification — per-sector classification by
                // content, not by file-level metadata.
                ExtractAndHashRawSectorUserDataOrAudio(sectorBuffer, hasher, includeAudio);

                bytesRead += rawSectorSize;
                if (bytesRead % (rawSectorSize * 1000) < rawSectorSize)
                {
                    var pct = totalBytes > 0
                        ? pctStart + (int)((pctEnd - pctStart) * bytesRead / totalBytes)
                        : pctStart;
                    var elapsed = DateTime.UtcNow - startTime;
                    var remaining = FormatHelper.EstimateRemaining(elapsed, bytesRead, totalBytes);
                    progress.Report(new VerifyProgress
                    {
                        PercentComplete = pct,
                        Elapsed = elapsed,
                        Remaining = remaining,
                        StatusMessage = isAudioFile
                            ? $"Hashing audio track files... {bytesRead / 1_048_576} MB"
                            : $"Extracting user data from track files... {bytesRead / 1_048_576} MB",
                        LogLine = $"Processed {bytesRead / 1_048_576} MB"
                    });
                }
            }
        }

        hasher.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return BitConverter.ToString(hasher.Hash ?? Array.Empty<byte>())
            .Replace("-", "").ToLowerInvariant();
    }

    /// <summary>
    /// Computes a checksum across multiple files concatenated in order.
    /// Used for multi-file CUE sheets with cooked (2048-byte) sectors.
    /// </summary>
    private static async Task<string> ComputeMultiFileChecksumAsync(
        List<string> files, string hashType,
        IProgress<VerifyProgress> progress, CancellationToken ct,
        int pctStart, int pctEnd)
    {
        using var hasher = CreateHasher(hashType);
        long totalBytes = files.Where(File.Exists).Sum(f => new FileInfo(f).Length);
        long bytesRead = 0;
        var startTime = DateTime.UtcNow;
        var buffer = new byte[2048 * 64];

        foreach (var file in files)
        {
            if (!File.Exists(file)) continue;

            await using var stream = new FileStream(file, FileMode.Open,
                FileAccess.Read, FileShare.Read, 65536, true);
            int n;
            while ((n = await stream.ReadAsync(buffer, ct)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                hasher.TransformBlock(buffer, 0, n, null, 0);
                bytesRead += n;
                if (bytesRead % (10 * 1_048_576) < n)
                {
                    var pct = totalBytes > 0
                        ? pctStart + (int)((pctEnd - pctStart) * bytesRead / totalBytes)
                        : pctStart;
                    var elapsed = DateTime.UtcNow - startTime;
                    var remaining = FormatHelper.EstimateRemaining(elapsed, bytesRead, totalBytes);
                    progress.Report(new VerifyProgress
                    {
                        PercentComplete = pct,
                        Elapsed = elapsed,
                        Remaining = remaining,
                        StatusMessage = $"Hashing track files... {bytesRead / 1_048_576} MB",
                        LogLine = $"Read {bytesRead / 1_048_576} MB"
                    });
                }
            }
        }

        hasher.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return BitConverter.ToString(hasher.Hash ?? Array.Empty<byte>())
            .Replace("-", "").ToLowerInvariant();
    }

    /// <summary>
    /// Parses a CUE sheet and returns track info for each TRACK directive,
    /// including whether each track is audio or data, and which FILE index it belongs to.
    /// For multi-file CUE sheets (one FILE per track), the file index corresponds
    /// to the BIN file list index from GetCueBinFiles.
    /// Handles REM comments, INDEX 00/01 positions, and PREGAP directives.
    /// Per CUE spec (ECMA-394 / cdrecord / Hydrogenaudio):
    ///   - INDEX values are offsets relative to the current FILE start
    ///   - PREGAP means the gap is NOT stored in the BIN file (synthesized by burner)
    ///   - INDEX 00 at non-zero offset means gap data IS in the file
    /// </summary>
    internal static List<CueTrackInfo> ParseCueTrackInfos(string cuePath)
    {
        var result = new List<CueTrackInfo>();
        if (!File.Exists(cuePath)) return result;

        var cueFileRx = new System.Text.RegularExpressions.Regex(
            @"^\s*FILE\s+""([^""]+)""", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var cueTrackRx = new System.Text.RegularExpressions.Regex(
            @"^\s*TRACK\s+(\d+)\s+(\S+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var cueIndexRx = new System.Text.RegularExpressions.Regex(
            @"^\s*INDEX\s+(\d+)\s+(\d+):(\d+):(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var cuePregapRx = new System.Text.RegularExpressions.Regex(
            @"^\s*PREGAP\s+(\d+):(\d+):(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        int currentFileIndex = -1;
        CueTrackInfo? currentTrack = null;

        try
        {
            foreach (var rawLine in File.ReadLines(cuePath))
            {
                var line = rawLine.Trim();

                // Skip REM comments — they can contain FILE/TRACK/INDEX keywords
                // that would cause false matches (e.g., "REM ORIGINAL FILE ...")
                if (IsCueRemComment(line))
                    continue;

                if (cueFileRx.IsMatch(line))
                {
                    currentFileIndex++;
                    continue;
                }

                var trackMatch = cueTrackRx.Match(line);
                if (trackMatch.Success && currentFileIndex >= 0)
                {
                    var mode = trackMatch.Groups[2].Value.ToUpperInvariant();
                    currentTrack = new CueTrackInfo
                    {
                        TrackNumber = int.Parse(trackMatch.Groups[1].Value),
                        Mode = mode,
                        IsAudio = mode == "AUDIO",
                        FileIndex = currentFileIndex
                    };
                    result.Add(currentTrack);
                    continue;
                }

                var indexMatch = cueIndexRx.Match(line);
                if (indexMatch.Success && currentTrack != null)
                {
                    int indexNum = int.Parse(indexMatch.Groups[1].Value);
                    int min = int.Parse(indexMatch.Groups[2].Value);
                    int sec = int.Parse(indexMatch.Groups[3].Value);
                    int frame = int.Parse(indexMatch.Groups[4].Value);
                    int totalFrames = (min * 60 + sec) * CdFramesPerSecond + frame;

                    if (indexNum == 0)
                    {
                        currentTrack.HasIndex00 = true;
                        currentTrack.Index00Frames = totalFrames;
                    }
                    else if (indexNum == 1)
                        currentTrack.Index01Frames = totalFrames;

                    continue;
                }

                // PREGAP directive: gap is NOT stored in the BIN file — burner
                // synthesizes silence. This is different from INDEX 00 which means
                // the gap data IS in the file. We record it so verification logic
                // knows not to adjust disc start LBA backward for this track.
                var pregapMatch = cuePregapRx.Match(line);
                if (pregapMatch.Success && currentTrack != null)
                {
                    currentTrack.HasPregapDirective = true;
                }
            }
        }
        catch { /* best effort */ }

        return result;
    }

    /// <summary>
    /// Extracts BIN/data file paths referenced by a cdrdao .toc file.
    /// Resolves relative paths relative to the TOC file's directory.
    /// </summary>
    private static List<string> GetTocBinFiles(string tocPath)
    {
        var binFiles = new List<string>();
        if (!File.Exists(tocPath)) return binFiles;

        var tocDir = Path.GetDirectoryName(tocPath) ?? ".";
        var datafileRx = new System.Text.RegularExpressions.Regex(
            @"(?:DATAFILE|FILE)\s+""([^""]+)""",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        try
        {
            foreach (var rawLine in File.ReadLines(tocPath))
            {
                var line = rawLine.Trim();
                var match = datafileRx.Match(line);
                if (match.Success)
                {
                    var fileName = match.Groups[1].Value;
                    var candidate = Path.IsPathRooted(fileName)
                        ? fileName
                        : Path.Combine(tocDir, fileName);
                    // Avoid duplicates (single-file TOC references the same BIN for every track)
                    if (!binFiles.Contains(candidate))
                        binFiles.Add(candidate);
                }
            }
        }
        catch { /* best effort */ }
        return binFiles;
    }

    /// <summary>
    /// Parses a cdrdao .toc file and returns track info for each TRACK directive,
    /// including whether each track is audio or data, and which DATAFILE index it belongs to.
    /// </summary>
    private static List<CueTrackInfo> ParseTocTrackInfos(string tocPath)
    {
        var result = new List<CueTrackInfo>();
        if (!File.Exists(tocPath)) return result;

        var trackRx = new System.Text.RegularExpressions.Regex(
            @"TRACK\s+(\w+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var datafileRx = new System.Text.RegularExpressions.Regex(
            @"(?:DATAFILE|FILE)\s+""([^""]+)""",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        int trackNumber = 0;
        string currentMode = "";
        int currentFileIndex = -1;
        // Track unique DATAFILE names to map each to a file index
        var fileNames = new List<string>();

        try
        {
            foreach (var rawLine in File.ReadLines(tocPath))
            {
                var line = rawLine.Trim();

                // Skip disc type headers and comment lines
                if (line.StartsWith("CD_DA", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("CD_ROM_XA", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("CD_ROM", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("//", StringComparison.Ordinal))
                    continue;

                var trackMatch = trackRx.Match(line);
                if (trackMatch.Success)
                {
                    trackNumber++;
                    currentMode = trackMatch.Groups[1].Value.ToUpperInvariant();
                    continue;
                }

                var dataMatch = datafileRx.Match(line);
                if (dataMatch.Success && trackNumber > 0)
                {
                    var fileName = dataMatch.Groups[1].Value;
                    var existingIdx = fileNames.IndexOf(fileName);
                    if (existingIdx >= 0)
                    {
                        currentFileIndex = existingIdx;
                    }
                    else
                    {
                        fileNames.Add(fileName);
                        currentFileIndex = fileNames.Count - 1;
                    }

                    bool isAudio = currentMode == "AUDIO";
                    // Map cdrdao TOC mode to CUE mode for consistency with CueTrackInfo
                    string cueMode = currentMode switch
                    {
                        "AUDIO" => "AUDIO",
                        "MODE1_RAW" => "MODE1/2352",
                        "MODE1" => "MODE1/2048",
                        "MODE2_RAW" => "MODE2/2352",
                        "MODE2" => "MODE2/2336",
                        "MODE2_FORM1" => "MODE2/2048",
                        "MODE2_FORM2" => "MODE2/2328",
                        "MODE2_FORM_MIX" => "MODE2/2336",
                        _ => currentMode
                    };

                    result.Add(new CueTrackInfo
                    {
                        TrackNumber = trackNumber,
                        Mode = cueMode,
                        IsAudio = isAudio,
                        FileIndex = currentFileIndex
                    });
                }
            }
        }
        catch { /* best effort */ }

        return result;
    }

    /// <summary>
    /// Looks for a sidecar checksum file (e.g. image.iso.sha256, image.iso.md5)
    /// and extracts the expected checksum value if found.
    /// Standard format: "hexchecksum  filename" or "hexchecksum *filename" per GNU coreutils.
    /// </summary>
    private static string? FindExpectedChecksum(string imagePath, string checksumType)
    {
        // Try common sidecar file extensions
        var extensions = checksumType.ToUpperInvariant() switch
        {
            "MD5" => new[] { ".md5", ".MD5" },
            "SHA1" => new[] { ".sha1", ".SHA1" },
            "SHA256" => new[] { ".sha256", ".SHA256" },
            "SHA512" => new[] { ".sha512", ".SHA512" },
            "CRC32" => new[] { ".crc32", ".sfv", ".CRC32", ".SFV" },
            _ => Array.Empty<string>()
        };

        foreach (var ext in extensions)
        {
            var sidecarPath = imagePath + ext;
            if (!File.Exists(sidecarPath)) continue;

            try
            {
                var lines = File.ReadAllLines(sidecarPath);
                var imageFileName = Path.GetFileName(imagePath);
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#") || line.StartsWith(";"))
                        continue;

                    // SFV format: "filename checksum" (space-separated, checksum is last token)
                    // Used by .sfv files for CRC32 checksums
                    if (ext is ".sfv" or ".SFV")
                    {
                        // SFV format: "filename hexchecksum" — checksum is the last space-separated token
                        var lastSpace = line.LastIndexOf(' ');
                        if (lastSpace > 0)
                        {
                            var sfvFile = line[..lastSpace].Trim();
                            var sfvHash = line[(lastSpace + 1)..].Trim();
                            // Match by filename (with or without path)
                            if (sfvHash.Length >= 8 &&
                                HexStringRegex.IsMatch(sfvHash) &&
                                (string.Equals(sfvFile, imageFileName, StringComparison.OrdinalIgnoreCase) ||
                                 sfvFile.EndsWith(imageFileName, StringComparison.OrdinalIgnoreCase)))
                            {
                                return sfvHash.ToLowerInvariant();
                            }
                        }
                        continue;
                    }

                    // GNU format: "hash  filename" or "hash *filename"
                    var parts = line.Split(new[] { ' ', '\t', '*' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 1)
                    {
                        var candidate = parts[0].Trim();
                        // Validate it looks like a hex string
                        if (candidate.Length >= 8 && HexStringRegex.IsMatch(candidate))
                            return candidate.ToLowerInvariant();
                    }
                }
            }
            catch { /* sidecar read failed, ignore */ }
        }

        return null;
    }

    /// <summary>
    /// Creates a hash algorithm by name.
    /// Note: MD5 and SHA1 are cryptographically broken and are provided here
    /// only for disc integrity checking and interoperability with legacy tools
    /// (e.g. comparing against externally published checksums).
    /// Do NOT use MD5 or SHA1 for security-sensitive operations.
    /// </summary>
    private static HashAlgorithm CreateHasher(string type)
    {
        var upper = type.ToUpperInvariant();
        // MD5 and SHA1 are included only for disc integrity checking and
        // legacy interoperability. They must never be used for security purposes.
        return CreateHasherCore(upper);
    }



    private static HashAlgorithm CreateHasherCore(string upper) => upper switch
    {
        "MD5"    => MD5.Create(),
        "SHA1"   => SHA1.Create(),
        "SHA256" => SHA256.Create(),
        "SHA512" => SHA512.Create(),
        "CRC32"  => new Crc32(),
        _        => throw new ArgumentException($"Unsupported checksum type: '{upper}'. Supported: MD5, SHA1, SHA256, SHA512, CRC32.")
    };
}

/// <summary>
/// Track information parsed from a CUE sheet TRACK directive.
/// Each instance corresponds to one TRACK entry in the CUE sheet.
/// </summary>
internal sealed class CueTrackInfo
{
    public int TrackNumber { get; set; }
    public string Mode { get; set; } = string.Empty;
    public bool IsAudio { get; set; }
    /// <summary>Index into the FILE directive list (0-based).</summary>
    public int FileIndex { get; set; }
    /// <summary>Whether this track has an INDEX 00 entry in the CUE sheet (pregap in the file).</summary>
    public bool HasIndex00 { get; set; }
    /// <summary>
    /// Frame offset of INDEX 00 within the FILE (in CD frames, 1 frame = 1 sector = 2352 bytes).
    /// Typically 0 for standard Redump/DIC multi-file CUE sheets (pregap starts at file beginning).
    /// </summary>
    public int Index00Frames { get; set; }
    /// <summary>
    /// Frame offset of INDEX 01 within the FILE (in CD frames, 1 frame = 1 sector = 2352 bytes).
    /// For multi-file CUE sheets with one track per file, this is typically 0 (no pregap)
    /// or 150 (2-second pregap included before INDEX 01).
    /// Per CUE spec, this offset is relative to the FILE start, NOT an absolute disc LBA.
    /// When Index01Frames > 0, the BIN file contains data before INDEX 01 (pregap or padding).
    /// </summary>
    public int Index01Frames { get; set; }
    /// <summary>
    /// Whether this track has a PREGAP directive. PREGAP means the gap is NOT stored
    /// in the BIN file — the burner synthesizes silence. This is different from INDEX 00
    /// which means the gap data IS in the file. When HasPregapDirective is true, the
    /// disc-side verification should NOT adjust the start LBA backward (no pregap data
    /// in the file to account for).
    /// </summary>
    public bool HasPregapDirective { get; set; }
}

// -----------------------------------------------------------------------
// Simple CRC32 HashAlgorithm wrapper
// -----------------------------------------------------------------------
internal sealed class Crc32 : HashAlgorithm
{
    private static readonly uint[] Table;
    private uint _crc = 0xFFFFFFFF;

    static Crc32()
    {
        Table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var entry = i;
            for (int j = 0; j < 8; j++)
                entry = (entry & 1) == 1 ? (entry >> 1) ^ 0xEDB88320 : entry >> 1;
            Table[i] = entry;
        }
    }

    public override void Initialize() { _crc = 0xFFFFFFFF; }

    protected override void HashCore(byte[] array, int ibStart, int cbSize)
    {
        for (int i = ibStart; i < ibStart + cbSize; i++)
            _crc = (_crc >> 8) ^ Table[(_crc ^ array[i]) & 0xFF];
    }

    protected override byte[] HashFinal()
    {
        var result = _crc ^ 0xFFFFFFFF;
        // Return bytes in big-endian order so that BitConverter.ToString produces
        // the standard CRC32 hex representation (matching crc32, cksfv, etc.)
        return new byte[]
        {
            (byte)(result >> 24),
            (byte)(result >> 16),
            (byte)(result >> 8),
            (byte)result
        };
    }

    public override int HashSize => 32;
}
