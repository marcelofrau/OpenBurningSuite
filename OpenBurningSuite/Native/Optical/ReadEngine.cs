// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenBurningSuite.Helpers;
using OpenBurningSuite.Models;
using OpenBurningSuite.Native.Iso;
using OpenBurningSuite.Native.Scsi;

namespace OpenBurningSuite.Native.Optical;

/// <summary>
/// Native disc reading engine using SCSI/MMC commands or direct device I/O
/// to read optical media to image files.
/// </summary>
public sealed class ReadEngine
{
    /// <summary>Number of sectors to read per READ(10) command (cooked 2048-byte sectors).</summary>
    private const int SectorsPerRead = 32;

    /// <summary>
    /// Maximum data transfer size per SCSI command in bytes.
    /// USB optical drives commonly limit transfers to 64 KB via their SCSI miniport
    /// driver's MaximumTransferLength. Exceeding this causes DeviceIoControl to fail
    /// (Win32 error, not a SCSI error), which surfaces as Status=0xFF. SATA/ATAPI
    /// drives typically support 128-256 KB, but 64 KB is safe for all adapters.
    /// </summary>
    private const int MaxRawTransferBytes = 65536;

    /// <summary>Bytes per sector for raw CD audio (Red Book 2352-byte sectors).</summary>
    private const int RawCdSectorSize = 2352;

    /// <summary>
    /// Standard pregap length in frames (sectors) for data↔audio track transitions.
    /// Per Red Book / Yellow Book, the mandatory pregap between tracks of different
    /// types is 150 frames (2 seconds at 75 frames/sec). The pregap sectors have the
    /// format of the NEXT track (audio silence for data→audio transitions).
    /// </summary>
    private const uint StandardPregapFrames = 150;

    /// <summary>
    /// Expected Sector Type values to try when ASC=0x64 "Illegal mode for this track"
    /// occurs. The order prioritises CD-DA (audio) first because the most common cause
    /// on Mixed Mode CDs is reading audio/pregap sectors with a data-type expectation.
    /// Values per MMC-5 §6.26:
    ///   1 = CD-DA (audio), 0 = All Types, 2 = Mode 1, 5 = Mode 2 Formless.
    /// </summary>
    private static readonly byte[] SectorTypeFallbacks = { 1, 0, 2, 5 };

    /// <summary>
    /// CD sector sync pattern (12 bytes): 00 FF FF FF FF FF FF FF FF FF FF 00.
    /// Present at the start of every data sector (Mode 1 and Mode 2) but absent
    /// from audio sectors. Used to identify data sectors and read the mode byte
    /// at offset 15 (after sync + 3-byte MSF address).
    /// </summary>
    private static readonly byte[] CdSyncPattern =
    {
        0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        0xFF, 0xFF, 0xFF, 0x00
    };

    /// <summary>
    /// Reads a disc to an ISO image file using SCSI READ(10) commands.
    /// </summary>
    public async Task ReadToIsoAsync(
        ReadJob job,
        IProgress<ReadProgress> progress,
        CancellationToken ct)
    {
        // Report progress immediately so the UI activates before any blocking operations.
        // Without this, the drive open + WaitForDriveReady() can delay the first progress
        // report by several seconds, making the UI appear unresponsive while the drive spins up.
        progress.Report(new ReadProgress
        {
            PercentComplete = 0,
            StatusMessage = "Initializing drive...",
            LogLine = $"[Info] Opening device: {job.DevicePath}"
        });

        using var drive = new OpticalDrive(job.DevicePath);

        // Wait for drive readiness — drives need time to spin up after tray close/load
        progress.Report(new ReadProgress { StatusMessage = "Waiting for drive to become ready..." });
        if (!drive.WaitForDriveReady())
            throw new InvalidOperationException("Drive is not ready. Please insert a disc and close the tray.");

        // Lock the tray during reading to prevent accidental ejection
        drive.PreventMediumRemoval(true);

        try
        {
        // Detect media type for accurate speed calculations
        var profile = drive.GetCurrentProfile();
        var baseSpeedBps = GetBaseSpeedForProfile(profile);

        // Set read speed if specified
        if (!SetReadSpeed(drive, job.ReadSpeed, profile))
        {
            progress.Report(new ReadProgress
            {
                LogLine = $"[Warning] Could not set read speed to {job.ReadSpeed}x — drive may use default speed"
            });
        }

        // Configure error recovery mode page if requested
        if (job.ErrorRecovery == "Yes")
        {
            drive.SetErrorRecovery((byte)job.RetryCount, transferBadBlocks: true);
        }
        else if (job.ErrorRecovery == "No")
        {
            drive.SetErrorRecovery(0, transferBadBlocks: false);
        }

        // Read disc info to determine total sectors
        var discInfo = drive.ReadDiscInfo();
        uint totalSectors = 0;

        // Try READ CAPACITY first for the most accurate sector count
        var capacity = drive.ReadCapacity();
        if (capacity.HasValue && capacity.Value.LastLba > 0)
        {
            // Guard against uint overflow: if LastLba is uint.MaxValue, adding 1 wraps to 0.
            // Cap at uint.MaxValue (4 billion sectors ≈ 8 TB at 2048 bytes/sector — far beyond any optical disc).
            totalSectors = capacity.Value.LastLba < uint.MaxValue
                ? capacity.Value.LastLba + 1
                : uint.MaxValue;
        }

        // Fallback: try to get total sectors from track info across all tracks.
        // Iterate backwards from the last track; take the maximum end LBA across
        // all tracks (don't break after the first track found, since multi-session
        // or multi-track discs may have the largest end LBA on an earlier track).
        if (totalSectors == 0)
        {
            var lastTrack = discInfo?.LastTrackInLastSessionLsb ?? 1;

            for (uint t = (uint)lastTrack; t >= 1; t--)
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

        // Fallback: estimate from file size if device file is accessible
        if (totalSectors == 0)
        {
            try
            {
                await using var probeStream = new FileStream(
                    job.DevicePath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite, 4096, false);
                if (probeStream.CanSeek && probeStream.Length > 0)
                    totalSectors = (uint)(probeStream.Length / job.SectorSize);
            }
            catch { /* continue with 0, progress will be indeterminate */ }
        }

        progress.Report(new ReadProgress
        {
            StatusMessage = $"Reading disc to {Path.GetFileName(job.OutputPath)}...",
            TotalSectors = totalSectors,
            LogLine = $"[Info] Total sectors: {totalSectors}, sector size: {job.SectorSize}, media: {OpticalDrive.ProfileToMediaType(profile)}"
        });

        // Mixed Mode CD detection: ISO format cannot represent audio tracks.
        // READ(10)/READ(12) with 2048-byte sectors will fail on audio track sectors
        // (audio sectors have no Mode 1/2 header structure). Detect this situation
        // by reading the TOC and checking for both data and audio tracks.
        // When detected, restrict the read to data-only LBA ranges.
        // Also read the TOC for raw sector reads to enable track-type-aware
        // expectedSectorType selection in READ CD commands.
        bool isCdMedia = profile < 0x0010;
        uint dataOnlyEndLba = 0; // 0 = not restricted (read all sectors)
        TocData? cdToc = null;
        if (isCdMedia)
        {
            cdToc = drive.ReadToc();
        }
        if (isCdMedia && job.SectorSize != 2352)
        {
            if (cdToc != null && cdToc.Entries.Count > 0)
            {
                bool hasData = false;
                bool hasAudio = false;
                foreach (var entry in cdToc.Entries)
                {
                    if (entry.TrackNumber == 0xAA) continue;
                    if (entry.IsData) hasData = true;
                    if (entry.IsAudio) hasAudio = true;
                }

                if (hasData && hasAudio)
                {
                    // Mixed Mode CD detected — find the end of the last data track.
                    // For ISO output, we can only read data track sectors.
                    // Audio tracks will be skipped entirely.
                    progress.Report(new ReadProgress
                    {
                        LogLine = "[Warning] Mixed Mode CD detected — ISO format can only read data tracks. " +
                                  "Audio tracks will be skipped. Use BIN/CUE format for a complete disc copy."
                    });

                    // Find the LBA range of data tracks. For a typical Mixed Mode CD,
                    // Track 1 (data) is followed by audio tracks. For Enhanced CD (CD-Extra),
                    // audio tracks come first and data is in a later session.
                    // We collect all contiguous data track ranges.
                    uint maxDataEndLba = 0;
                    foreach (var entry in cdToc.Entries)
                    {
                        if (entry.TrackNumber == 0xAA || entry.IsAudio) continue;
                        var trackEnd = GetTrackEndLba(cdToc, entry);
                        if (trackEnd > maxDataEndLba)
                            maxDataEndLba = trackEnd;
                    }

                    if (maxDataEndLba > 0 && maxDataEndLba < totalSectors)
                    {
                        dataOnlyEndLba = maxDataEndLba;
                        var skippedSectors = totalSectors - dataOnlyEndLba;
                        progress.Report(new ReadProgress
                        {
                            LogLine = $"[Info] Reading data tracks only: {dataOnlyEndLba} sectors " +
                                      $"(skipping {skippedSectors} audio track sectors)"
                        });
                        totalSectors = dataOnlyEndLba;
                    }
                }
            }
        }

        // Determine if we use SCSI commands or direct file I/O.
        // READ CD (BEh) is a CD-specific command per MMC-5 §6.26 — it MUST NOT be used
        // for DVD, Blu-ray, or HD DVD media. Those media types use standard READ(10)/READ(12)
        // with 2048-byte cooked sectors. Raw 2352-byte sector reads (with sync, header,
        // EDC/ECC) only apply to CD media.
        //
        // JitterCorrection is only meaningful for raw CD audio reads (BIN/CUE format)
        // where ReadToBinCueAsync already handles it internally. For ISO format reads,
        // jitter correction is not applicable — cooked sector reads already have full
        // ECC from the drive hardware.
        if (isCdMedia && job.SectorSize == 2352)
        {
            // Use SCSI READ CD for raw sector reads (2352 bytes per sector).
            // This produces a raw image — for standard ISO 9660 use 2048-byte sectors.
            progress.Report(new ReadProgress
            {
                LogLine = "[Info] Using raw 2352-byte sector reads (SCSI READ CD)"
            });
            await ReadRawSectorsAsync(drive, job, totalSectors, baseSpeedBps, profile, cdToc, progress, ct);
        }
        else
        {
            // DVD/BD/HD DVD sectors are always 2048 bytes — they don't have the
            // raw 2352-byte sector structure (sync + header + user data + EDC/ECC)
            // that CD sectors have. Override if a raw sector size was requested.
            if (!isCdMedia && job.SectorSize != 2048)
            {
                progress.Report(new ReadProgress
                {
                    LogLine = $"[Info] Overriding sector size from {job.SectorSize} to 2048 for {OpticalDrive.ProfileToMediaType(profile)} media"
                });
                job.SectorSize = 2048;
            }

            // On Windows, device paths like "D:\" cannot be opened as a FileStream for raw reads.
            // On macOS, /dev/diskN requires exclusive access via IOKit and can't be opened as FileStream.
            // Use SCSI READ commands for both. On Linux, /dev/srX can be opened directly,
            // so we prefer FileStream for its simplicity.
            // Use READ(12) for DVD/BD media (32-bit transfer length, reduced command overhead)
            // and READ(10) for CD media (standard 16-bit transfer length).
            bool useScsiRead = System.Runtime.InteropServices.RuntimeInformation
                .IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) ||
                System.Runtime.InteropServices.RuntimeInformation
                .IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX);
            bool isDvdOrBd = profile >= 0x0010;
            if (useScsiRead)
                await ReadViaScsiAsync(drive, job, totalSectors, baseSpeedBps, profile, isDvdOrBd, progress, ct);
            else
                await ReadViaFileStreamAsync(job, totalSectors, baseSpeedBps, progress, ct);
        }

        // Generate MD5/SHA-1 hashes for dump verification if requested
        if (job.GenerateHashes)
            await ComputeAndReportHashesAsync(job.OutputPath, progress, ct);
        }
        finally
        {
            // Unlock the tray after reading — guard against exceptions to ensure cleanup
            try { drive.PreventMediumRemoval(false); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Reads a disc to BIN/CUE format using SCSI READ CD commands for raw sectors.
    /// </summary>
    public async Task ReadToBinCueAsync(
        ReadJob job,
        IProgress<ReadProgress> progress,
        CancellationToken ct)
    {
        // Report progress immediately so the UI activates before any blocking operations.
        progress.Report(new ReadProgress
        {
            PercentComplete = 0,
            StatusMessage = "Initializing drive...",
            LogLine = $"[Info] Opening device: {job.DevicePath}"
        });

        using var drive = new OpticalDrive(job.DevicePath);

        progress.Report(new ReadProgress { StatusMessage = "Waiting for drive to become ready..." });
        if (!drive.WaitForDriveReady())
            throw new InvalidOperationException("Drive is not ready. Please insert a disc and close the tray.");

        // Lock the tray during reading to prevent accidental ejection
        drive.PreventMediumRemoval(true);

        try
        {

        // Detect media type and set read speed
        var profile = drive.GetCurrentProfile();
        var baseSpeedBps = GetBaseSpeedForProfile(profile);
        if (!SetReadSpeed(drive, job.ReadSpeed, profile))
        {
            progress.Report(new ReadProgress
            {
                LogLine = $"[Warning] Could not set read speed to {job.ReadSpeed}x — drive may use default speed"
            });
        }

        // Configure error recovery mode page if requested
        if (job.ErrorRecovery == "Yes")
        {
            drive.SetErrorRecovery((byte)job.RetryCount, transferBadBlocks: true);
        }
        else if (job.ErrorRecovery == "No")
        {
            drive.SetErrorRecovery(0, transferBadBlocks: false);
        }

        // Read TOC to determine track layout
        var toc = drive.ReadToc();
        if (toc == null || toc.Entries.Count == 0)
            throw new InvalidOperationException("Could not read Table of Contents.");

        progress.Report(new ReadProgress
        {
            StatusMessage = $"Reading disc ({toc.Entries.Count} track descriptors)...",
            LogLine = $"[Info] TOC: tracks {toc.FirstTrack}-{toc.LastTrack}"
        });

        // Determine sector count from lead-out track (track 0xAA)
        uint totalSectors = 0;
        foreach (var entry in toc.Entries)
        {
            if (entry.TrackNumber == 0xAA) // Lead-out
            {
                totalSectors = entry.StartLba;
                break;
            }
        }

        if (totalSectors == 0 && toc.Entries.Count > 0)
        {
            // Estimate from last track. If the last entry is lead-out (0xAA),
            // use its StartLba directly (should have been caught above).
            // Otherwise add 2 seconds (150 frames) as a rough estimate.
            var lastEntry = toc.Entries[^1];
            totalSectors = lastEntry.TrackNumber == 0xAA
                ? lastEntry.StartLba
                : lastEntry.StartLba + 150;
        }

        // Determine subchannel reading parameters
        bool includeSubchannel = job.SubchannelMode != "None";

        // C2 error flag support for byte-perfect CD dumping.
        // When enabled, the drive returns a 294-byte bitmap per sector identifying
        // which individual bytes had uncorrectable C2-level errors. This matches
        // the approach used by redumper and other disc preservation tools.
        int c2ErrorMode = job.C2ErrorFlags ? 1 : 0; // 1 = C2 Error Block Data (294 bytes)
        int c2BytesPerSector = c2ErrorMode == 1 ? 294 : 0;
        long totalC2Errors = 0; // cumulative C2 error byte count across all sectors

        if (job.C2ErrorFlags)
        {
            progress.Report(new ReadProgress
            {
                LogLine = "[Info] C2 error flags enabled — 294-byte error bitmap per sector for byte-level error detection"
            });
        }

        // Drive read offset logging
        if (job.DriveReadOffset != 0)
        {
            progress.Report(new ReadProgress
            {
                LogLine = $"[Info] Drive read offset: {job.DriveReadOffset} samples ({job.DriveReadOffset * 4} bytes)"
            });
        }

        // Build per-track file information for multi-file BIN/CUE output.
        // For multi-track discs (Mixed Mode CDs, multi-track audio), each track gets
        // its own .bin file per Redump convention. On Mixed Mode CDs, audio tracks
        // include their 150-frame pregap at the beginning of the file.
        var trackFileInfos = BuildTrackFileInfos(toc, job.OutputPath);
        bool multiFile = trackFileInfos.Count > 1;

        // Build sorted list of track boundary LBAs from the TOC, including
        // pregap-adjusted boundaries for data↔audio transitions. A READ CD command
        // must not span a track boundary on Mixed Mode CDs because drives return
        // "Illegal mode for this track" (ASC=0x64, ASCQ=0x00) when a single command
        // crosses from a data track to an audio track (or vice versa). On Mixed Mode
        // CDs (e.g., PS1 discs), the actual sector format changes at the pregap start
        // (150 frames before the audio track's INDEX 01), not at the TOC StartLba.
        // For multi-file output, file boundaries are also added so batches never
        // cross into a different track's output file.
        var multiFileStartLbas = multiFile
            ? trackFileInfos.ConvertAll(tf => tf.FileStartLba)
            : null;
        var trackBoundaries = BuildTrackBoundaries(toc, multiFileStartLbas);

        // Audio paranoia: build audio track LBA ranges for multi-pass jitter correction.
        // When AudioParanoia is enabled, audio track sectors are read multiple times and
        // compared to ensure data consistency — essential for scratched or degraded discs.
        // Data track sectors are always read with a single pass (data tracks have strong ECC).
        bool useParanoia = job.AudioParanoia;
        // List of (startLba, endLba) pairs for audio tracks
        var audioRanges = new System.Collections.Generic.List<(uint Start, uint End)>();
        if (useParanoia)
        {
            foreach (var entry in toc.Entries)
            {
                if (entry.TrackNumber == 0xAA || !entry.IsAudio) continue;
                var trackLen = GetTrackLength(toc, entry);
                if (trackLen > 0)
                    audioRanges.Add((entry.StartLba, entry.StartLba + trackLen));
            }
            if (audioRanges.Count > 0)
            {
                progress.Report(new ReadProgress
                {
                    LogLine = $"[Info] Audio paranoia enabled — multi-pass jitter correction for {audioRanges.Count} audio track(s)"
                });
            }
        }

        // Clear UNIT ATTENTION that may be pending after SET ERROR RECOVERY / SET CD SPEED.
        // These mode-changing commands can trigger a UNIT ATTENTION (SK=06h, ASC=29h/2Ah)
        // on the next data-transfer command. Draining it here avoids failing the first
        // real read. Two TEST UNIT READY calls: one to trigger the UA, one to clear it.
        drive.TestUnitReady();
        drive.TestUnitReady();

        // ---- Test read: validate READ CD works before committing to the full dump ----
        // This catches drive incompatibilities early (unsupported subchannel mode, C2
        // flags, or READ CD itself not supported). Without this, the main loop would
        // fail on every sector and either zero-fill the entire image or abort.
        {
            // Determine the first readable LBA from the TOC (usually 0, but use the
            // first track's start LBA in case it differs, e.g. multi-session discs).
            uint testLba = 0;
            foreach (var e in toc.Entries)
            {
                if (e.TrackNumber != 0xAA) { testLba = e.StartLba; break; }
            }

            var testSectorType = GetExpectedSectorType(toc, testLba);
            var (testResult, _) = drive.ReadCdRaw(
                testLba, 1, testSectorType, includeSubchannel,
                job.SubchannelMode ?? "RW", c2ErrorMode);

            if (!testResult.Success)
            {
                progress.Report(new ReadProgress
                {
                    LogLine = $"[Warning] Initial READ CD test failed at LBA {testLba} " +
                              $"({testResult.ErrorDescription})"
                });

                // If C2 error flags were requested, try without them first
                if (c2ErrorMode != 0)
                {
                    var (testNoC2, _) = drive.ReadCdRaw(
                        testLba, 1, testSectorType, includeSubchannel,
                        job.SubchannelMode ?? "RW", 0);
                    if (testNoC2.Success)
                    {
                        progress.Report(new ReadProgress
                        {
                            LogLine = "[Warning] Drive does not support C2 error flags — disabling. " +
                                      "Read will proceed without byte-level error detection."
                        });
                        c2ErrorMode = 0;
                        c2BytesPerSector = 0;
                    }
                }

                // If subchannel was requested and still failing, try without subchannel.
                // Use c2ErrorMode=0 to isolate the subchannel variable — if c2 was already
                // disabled above, this is a no-op; if c2 passed, we still need to rule it
                // out so the test result reflects subchannel support alone.
                if (includeSubchannel)
                {
                    var (testNoSub, _) = drive.ReadCdRaw(
                        testLba, 1, testSectorType, false, "RW", 0);
                    if (testNoSub.Success)
                    {
                        // No-subchannel + no-C2 works. Now check if C2 alone is supported.
                        includeSubchannel = false;
                        job.SubchannelMode = "None";

                        if (c2ErrorMode != 0)
                        {
                            var (testC2Only, _) = drive.ReadCdRaw(
                                testLba, 1, testSectorType, false, "RW", c2ErrorMode);
                            if (!testC2Only.Success)
                            {
                                progress.Report(new ReadProgress
                                {
                                    LogLine = $"[Warning] Drive does not support subchannel mode '{job.SubchannelMode}' " +
                                              "or C2 error flags — disabling both."
                                });
                                c2ErrorMode = 0;
                                c2BytesPerSector = 0;
                            }
                            else
                            {
                                progress.Report(new ReadProgress
                                {
                                    LogLine = $"[Warning] Drive does not support subchannel reading — " +
                                              "disabling subchannel. C2 error flags remain active."
                                });
                            }
                        }
                        else
                        {
                            progress.Report(new ReadProgress
                            {
                                LogLine = $"[Warning] Drive does not support subchannel reading — " +
                                          "disabling. Subchannel data (e.g. LibCrypt) will not be preserved."
                            });
                        }
                    }
                    else
                    {
                        // Both with and without subchannel fail — try a completely
                        // minimal READ CD (no subchannel, no C2)
                        var (testMinimal, _) = drive.ReadCdRaw(
                            testLba, 1, testSectorType, false, "RW", 0);
                        if (testMinimal.Success)
                        {
                            progress.Report(new ReadProgress
                            {
                                LogLine = "[Warning] Drive requires minimal READ CD parameters — " +
                                          "disabling subchannel and C2 error flags."
                            });
                            includeSubchannel = false;
                            job.SubchannelMode = "None";
                            c2ErrorMode = 0;
                            c2BytesPerSector = 0;
                        }
                        else
                        {
                            // Everything failed — throw a clear diagnostic error
                            throw new InvalidOperationException(
                                $"Drive cannot read this disc with READ CD command. " +
                                $"Test read at LBA {testLba} failed: {testMinimal.ErrorDescription}. " +
                                $"This may indicate the drive does not support raw sector reads, " +
                                $"the disc is unreadable, or the drive requires different parameters.");
                        }
                    }
                }
                else
                {
                    // No subchannel, check if a minimal read works
                    var (testMinimal, _) = drive.ReadCdRaw(
                        testLba, 1, testSectorType, false, "RW", 0);
                    if (!testMinimal.Success)
                    {
                        throw new InvalidOperationException(
                            $"Drive cannot read this disc with READ CD command. " +
                            $"Test read at LBA {testLba} failed: {testMinimal.ErrorDescription}. " +
                            $"This may indicate the drive does not support raw sector reads, " +
                            $"the disc is unreadable, or the drive requires different parameters.");
                    }
                    // Minimal read works — C2 was the issue (already disabled above)
                }

                progress.Report(new ReadProgress
                {
                    LogLine = "[Info] Test read passed with adjusted parameters — proceeding with disc read."
                });
            }
        }

        // Read all sectors with READ CD command (raw 2352-byte sectors)
        // Per MMC-5 §6.26, returned data order per sector is:
        // [main channel (2352)] [C2 data (0/294/296)] [subchannel (0/96)]
        int mainSectorSize = 2352;
        int fullSectorSize = mainSectorSize + c2BytesPerSector + (includeSubchannel ? 96 : 0);
        // BIN file contains only main channel data (2352 bytes/sector) per standard BIN/CUE format.
        // Subchannel data goes to a separate .sub file for compatibility with emulators, burning
        // software (e.g. cdrdao, ImgBurn), and disc preservation tools (e.g. DiscImageCreator, Redumper).
        int outputSectorSize = mainSectorSize;
        long totalBytes = (long)totalSectors * outputSectorSize;
        long bytesRead = 0;
        long errorSectorCount = 0;
        long c2ErrorSectorCount = 0; // sectors with any C2 errors (for summary)
        const int maxC2LogEntries = 50; // throttle per-sector C2 logging to prevent log flooding
        uint currentLba = 0;
        var startTime = DateTime.Now;
        int retryCount = job.RetryCount;

        // Adaptive batch size: compute the maximum number of raw sectors per READ CD
        // command that fits within the safe transfer limit. USB optical drives commonly
        // limit transfers to 64 KB; exceeding this causes IOCTL failures (Status=0xFF).
        // For raw + subchannel: 65536 / 2448 = 26 sectors
        // For raw + C2 + subchannel: 65536 / 2742 = 23 sectors
        int rawSectorsPerRead = Math.Max(1, MaxRawTransferBytes / fullSectorSize);
        // Cap to SectorsPerRead (32) for drives that support larger transfers
        rawSectorsPerRead = Math.Min(rawSectorsPerRead, SectorsPerRead);
        // Save original batch size for gradual recovery after transport error reductions
        int originalSectorsPerRead = rawSectorsPerRead;
        int consecutiveSuccessfulReads = 0;

        // Open the first track's output file. For multi-file output, this is
        // "<baseName> (Track 01).bin"; for single-file, it's the original path.
        // Manual stream management is needed because multi-file output switches
        // streams at track boundaries during the read loop.
        int currentTrackFileIndex = 0;
        var outputStream = new FileStream(
            trackFileInfos[0].FilePath, FileMode.Create, FileAccess.Write,
            FileShare.None, 65536, true);

        // Write subchannel data to a separate .sub file (standard BIN/CUE practice).
        // This keeps the BIN file at 2352 bytes/sector for maximum compatibility.
        // Tools like DiscImageCreator, Redumper, and cdrdao all use separate subchannel files.
        var subPath = includeSubchannel ? Path.ChangeExtension(job.OutputPath, ".sub") : null;
        FileStream? subStream = includeSubchannel
            ? new FileStream(subPath!, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true)
            : null;

        try
        {

        try
        {
        while (currentLba < totalSectors)
        {
            ct.ThrowIfCancellationRequested();

            // Multi-file output: switch to the next track's file when we cross
            // a track boundary. Each track's file starts at its FileStartLba.
            if (multiFile && currentTrackFileIndex + 1 < trackFileInfos.Count &&
                currentLba >= trackFileInfos[currentTrackFileIndex + 1].FileStartLba)
            {
                await outputStream.FlushAsync(ct);
                await outputStream.DisposeAsync();
                currentTrackFileIndex++;
                outputStream = new FileStream(
                    trackFileInfos[currentTrackFileIndex].FilePath,
                    FileMode.Create, FileAccess.Write, FileShare.None, 65536, true);
            }

            var sectorsToRead = (uint)Math.Min(rawSectorsPerRead, totalSectors - currentLba);

            // Clamp batch to current track boundary to prevent "Illegal mode for this track"
            // (ASC=0x64) errors. Drives reject READ CD commands that span a data↔audio track
            // boundary because the sector format changes. This is the primary cause of read
            // failures on Mixed Mode CDs (e.g., PS1 discs with data track 1 + audio tracks).
            foreach (var boundary in trackBoundaries)
            {
                if (boundary > currentLba && boundary < currentLba + sectorsToRead)
                {
                    sectorsToRead = boundary - currentLba;
                    break;
                }
            }

            bool readSuccess = false;
            uint actualSectorsRead = sectorsToRead; // tracks actual sectors for LBA advancement

            // Check if current LBA range is within an audio track for paranoia mode.
            // Audio paranoia performs multi-pass reads with data comparison to detect
            // and correct jitter errors that are common on audio tracks (which lack
            // the strong ECC that data tracks have).
            bool isAudioRange = false;
            if (useParanoia && audioRanges.Count > 0)
            {
                foreach (var (start, end) in audioRanges)
                {
                    if (currentLba >= start && currentLba < end)
                    {
                        isAudioRange = true;
                        break;
                    }
                }
            }

            if (isAudioRange)
            {
                // Audio paranoia path: read multiple times and pick the best result
                int paranoiaPasses = job.JitterCorrection ? 3 : 2;
                byte[]? bestData = null;
                int bestConfidence = 0;
                bool anySuccess = false;
                var sectorType = GetExpectedSectorType(toc, currentLba);
                var triedSectorTypes = new System.Collections.Generic.HashSet<byte> { sectorType };

                for (int pass = 0; pass < paranoiaPasses; pass++)
                {
                    var (result, data) = drive.ReadCdRaw(
                        currentLba, sectorsToRead,
                        sectorType,
                        includeSubchannel,
                        job.SubchannelMode ?? "RW",
                        c2ErrorMode);

                    // Transport-level failure in paranoia path: reduce batch size
                    // and allow the drive to recover before retrying.
                    if (result.Status == 0xFF)
                    {
                        if (sectorsToRead > 1)
                        {
                            sectorsToRead = Math.Max(1, sectorsToRead / 2);
                            rawSectorsPerRead = (int)Math.Min(rawSectorsPerRead, sectorsToRead);
                            progress.Report(new ReadProgress
                            {
                                LogLine = $"[Warning] Transport error at LBA {currentLba} " +
                                          $"({result.ErrorDescription}), reducing batch size to {sectorsToRead} sector(s)"
                            });
                        }
                        else
                        {
                            progress.Report(new ReadProgress
                            {
                                LogLine = $"[Warning] Transport error at LBA {currentLba} " +
                                          $"({result.ErrorDescription}), recovering..."
                            });
                        }
                        // Allow the drive to recover after a transport error
                        drive.TestUnitReady();
                        await Task.Delay(2000, ct);
                        drive.TestUnitReady();
                        // Re-issue SET CD SPEED after recovery: some drives reset their speed
                        // target after errors or long pauses, causing speed ramp-up that triggers
                        // further transport errors (semaphore timeouts on USB bridges).
                        SetReadSpeed(drive, job.ReadSpeed, profile);
                        pass = -1; // restart paranoia passes with reduced batch size
                        bestData = null;
                        bestConfidence = 0;
                        anySuccess = false;
                        continue;
                    }

                    // "Illegal mode for this track" (ASC=0x64): try the next
                    // expectedSectorType from the fallback list. This handles edge
                    // cases where the TOC-based type doesn't match what the drive
                    // expects (e.g., pregap zones, multi-session discs).
                    if (IsIllegalModeForTrackError(result))
                    {
                        byte? nextType = null;
                        foreach (var ft in SectorTypeFallbacks)
                        {
                            if (!triedSectorTypes.Contains(ft))
                            {
                                nextType = ft;
                                break;
                            }
                        }
                        if (nextType.HasValue)
                        {
                            progress.Report(new ReadProgress
                            {
                                LogLine = $"[Warning] Paranoia sector type mismatch at LBA {currentLba} — " +
                                          $"switching expectedSectorType from {sectorType} to {nextType.Value}"
                            });
                            sectorType = nextType.Value;
                            triedSectorTypes.Add(sectorType);
                            // Also reduce to single-sector if we haven't already, to avoid
                            // mixed-type batches in transition zones
                            if (sectorsToRead > 1)
                                sectorsToRead = 1;
                            pass = -1;
                            bestData = null;
                            bestConfidence = 0;
                            anySuccess = false;
                            continue;
                        }
                    }

                    if (result.Success && result.DataTransferred > 0)
                    {
                        anySuccess = true;
                        if (bestData == null)
                        {
                            bestData = data;
                            bestConfidence = 1;
                        }
                        else
                        {
                            // Compare main channel data between reads for consistency.
                            // Only compare main channel bytes (not C2/subchannel) since
                            // subchannel data may vary between reads.
                            var compareLen = (int)Math.Min(
                                (long)sectorsToRead * mainSectorSize,
                                Math.Min(data.Length, bestData.Length));
                            bool match;
                            if (c2BytesPerSector > 0 || includeSubchannel)
                            {
                                // With C2/subchannel, compare only main channel portions
                                match = true;
                                for (uint s = 0; s < sectorsToRead && match; s++)
                                {
                                    int off = (int)(s * fullSectorSize);
                                    int mainBytes = Math.Min(mainSectorSize,
                                        Math.Min(data.Length - off, bestData.Length - off));
                                    if (mainBytes <= 0) break;
                                    if (!data.AsSpan(off, mainBytes).SequenceEqual(
                                            bestData.AsSpan(off, mainBytes)))
                                        match = false;
                                }
                            }
                            else
                            {
                                // No C2/subchannel — compare entire buffer
                                match = data.AsSpan(0, compareLen).SequenceEqual(
                                    bestData.AsSpan(0, compareLen));
                            }

                            if (match)
                            {
                                bestConfidence++;
                                if (bestConfidence >= paranoiaPasses) break;
                            }
                            else
                            {
                                // Data mismatch — use the latest read and reset confidence
                                bestData = data;
                                bestConfidence = 1;
                            }
                        }
                    }
                }

                if (anySuccess && bestData != null)
                {
                    // Write the best data using the same C2/subchannel handling as normal path
                    if (c2BytesPerSector > 0)
                    {
                        for (uint s = 0; s < sectorsToRead; s++)
                        {
                            int baseOffset = (int)(s * fullSectorSize);
                            if (baseOffset + mainSectorSize > bestData.Length) break;

                            int mainBytes = Math.Min(mainSectorSize, bestData.Length - baseOffset);
                            await outputStream.WriteAsync(bestData.AsMemory(baseOffset, mainBytes), ct);

                            int c2Offset = baseOffset + mainSectorSize;
                            if (c2Offset + c2BytesPerSector <= bestData.Length)
                            {
                                int sectorC2Errors = CountC2Errors(bestData, c2Offset, c2BytesPerSector);
                                if (sectorC2Errors > 0)
                                {
                                    totalC2Errors += sectorC2Errors;
                                    c2ErrorSectorCount++;
                                    if (c2ErrorSectorCount <= maxC2LogEntries)
                                    {
                                        progress.Report(new ReadProgress
                                        {
                                            LogLine = $"[C2] LBA {currentLba + s}: {sectorC2Errors} byte-level C2 error(s) detected"
                                        });
                                    }
                                    else if (c2ErrorSectorCount == maxC2LogEntries + 1)
                                    {
                                        progress.Report(new ReadProgress
                                        {
                                            LogLine = $"[C2] Further per-sector C2 reports suppressed (>{maxC2LogEntries} sectors). Summary at end."
                                        });
                                    }
                                }
                            }

                            if (includeSubchannel && subStream != null)
                            {
                                int subOffset = baseOffset + mainSectorSize + c2BytesPerSector;
                                int subBytes = Math.Min(96, bestData.Length - subOffset);
                                if (subBytes > 0)
                                    await subStream.WriteAsync(bestData.AsMemory(subOffset, subBytes), ct);
                            }
                        }
                        bytesRead += sectorsToRead * outputSectorSize;
                    }
                    else if (includeSubchannel && subStream != null)
                    {
                        // No C2 but subchannel: split main + subchannel
                        for (uint s = 0; s < sectorsToRead; s++)
                        {
                            int baseOffset = (int)(s * fullSectorSize);
                            if (baseOffset + mainSectorSize > bestData.Length) break;

                            int mainBytes = Math.Min(mainSectorSize, bestData.Length - baseOffset);
                            await outputStream.WriteAsync(bestData.AsMemory(baseOffset, mainBytes), ct);

                            int subOffset = baseOffset + mainSectorSize;
                            int subBytes = Math.Min(96, bestData.Length - subOffset);
                            if (subBytes > 0)
                                await subStream.WriteAsync(bestData.AsMemory(subOffset, subBytes), ct);
                        }
                        bytesRead += sectorsToRead * outputSectorSize;
                    }
                    else
                    {
                        // No subchannel, no C2: write directly
                        var validBytes = Math.Min(bestData.Length,
                            (int)Math.Min(sectorsToRead * (long)outputSectorSize, int.MaxValue));
                        await outputStream.WriteAsync(bestData.AsMemory(0, validBytes), ct);
                        bytesRead += validBytes;
                    }
                    actualSectorsRead = sectorsToRead;
                    readSuccess = true;
                }
                // If paranoia reads all failed, fall through to error handling below
            }
            else
            {
                // Standard read path (data tracks or no paranoia)
                var sectorType = GetExpectedSectorType(toc, currentLba);
                var triedSectorTypes = new System.Collections.Generic.HashSet<byte> { sectorType };
                for (int retry = 0; retry <= retryCount; retry++)
                {
                    var (result, data) = drive.ReadCdRaw(
                        currentLba, sectorsToRead,
                        sectorType,
                        includeSubchannel,
                        job.SubchannelMode ?? "RW",
                        c2ErrorMode);

                    if (result.Success && result.DataTransferred > 0)
                    {
                        if (c2BytesPerSector > 0)
                        {
                            // C2 mode: data is [main(2352)][c2(294)][sub(0/96)] per sector.
                            // Extract C2 error data for tracking, write only main+sub to output.
                            for (uint s = 0; s < sectorsToRead; s++)
                            {
                                int baseOffset = (int)(s * fullSectorSize);
                                if (baseOffset + mainSectorSize > data.Length) break;

                                // Write main channel data (2352 bytes)
                                int mainBytes = Math.Min(mainSectorSize, data.Length - baseOffset);
                                await outputStream.WriteAsync(data.AsMemory(baseOffset, mainBytes), ct);

                                // Check C2 error flags
                                int c2Offset = baseOffset + mainSectorSize;
                                if (c2Offset + c2BytesPerSector <= data.Length)
                                {
                                    int sectorC2Errors = CountC2Errors(data, c2Offset, c2BytesPerSector);
                                    if (sectorC2Errors > 0)
                                    {
                                        totalC2Errors += sectorC2Errors;
                                        c2ErrorSectorCount++;
                                        // Throttle per-sector C2 logging to prevent log flooding on damaged discs
                                        if (c2ErrorSectorCount <= maxC2LogEntries)
                                        {
                                            progress.Report(new ReadProgress
                                            {
                                                LogLine = $"[C2] LBA {currentLba + s}: {sectorC2Errors} byte-level C2 error(s) detected"
                                            });
                                        }
                                        else if (c2ErrorSectorCount == maxC2LogEntries + 1)
                                        {
                                            progress.Report(new ReadProgress
                                            {
                                                LogLine = $"[C2] Further per-sector C2 reports suppressed (>{maxC2LogEntries} sectors). Summary at end."
                                            });
                                        }
                                    }
                                }

                                // Write subchannel data to separate .sub file
                                if (includeSubchannel && subStream != null)
                                {
                                    int subOffset = baseOffset + mainSectorSize + c2BytesPerSector;
                                    int subBytes = Math.Min(96, data.Length - subOffset);
                                    if (subBytes > 0)
                                        await subStream.WriteAsync(data.AsMemory(subOffset, subBytes), ct);
                                }
                            }
                            bytesRead += sectorsToRead * outputSectorSize;
                        }
                        else if (includeSubchannel && subStream != null)
                        {
                            // No C2 but subchannel enabled: data is [main(2352)][sub(96)] per sector.
                            // Write main channel to BIN and subchannel to .sub file separately.
                            for (uint s = 0; s < sectorsToRead; s++)
                            {
                                int baseOffset = (int)(s * fullSectorSize);
                                if (baseOffset + mainSectorSize > data.Length) break;

                                // Write main channel data to BIN (2352 bytes)
                                int mainBytes = Math.Min(mainSectorSize, data.Length - baseOffset);
                                await outputStream.WriteAsync(data.AsMemory(baseOffset, mainBytes), ct);

                                // Write subchannel data to .sub file (96 bytes)
                                int subOffset = baseOffset + mainSectorSize;
                                int subBytes = Math.Min(96, data.Length - subOffset);
                                if (subBytes > 0)
                                    await subStream.WriteAsync(data.AsMemory(subOffset, subBytes), ct);
                            }
                            bytesRead += sectorsToRead * outputSectorSize;
                        }
                        else
                        {
                            // No C2, no subchannel: write data directly
                            var validBytes = Math.Min(result.DataTransferred,
                                (int)Math.Min(sectorsToRead * (long)outputSectorSize, int.MaxValue));
                            // Clamp to actual buffer length to prevent overrun
                            validBytes = Math.Min(validBytes, data.Length);
                            await outputStream.WriteAsync(data.AsMemory(0, validBytes), ct);
                            bytesRead += validBytes;
                        }
                        // Track actual sectors read to advance LBA correctly on partial reads.
                        // outputSectorSize is always > 0 (2352+ for raw reads).
                        // Ensure at least 1 sector advancement to prevent infinite loop.
                        actualSectorsRead = sectorsToRead;
                        readSuccess = true;
                        break;
                    }

                    // Transport-level failure (Status=0xFF): the IOCTL itself failed, not the
                    // SCSI command. This typically means the transfer size exceeds the adapter's
                    // MaximumTransferLength (common on USB drives with 64KB limit), or a semaphore
                    // timeout (error 121) when the drive stops responding. Halve the batch size
                    // and allow the drive to recover before retrying.
                    if (result.Status == 0xFF && sectorsToRead > 1)
                    {
                        sectorsToRead = Math.Max(1, sectorsToRead / 2);
                        rawSectorsPerRead = (int)Math.Min(rawSectorsPerRead, sectorsToRead);
                        progress.Report(new ReadProgress
                        {
                            LogLine = $"[Warning] Transport error at LBA {currentLba} " +
                                      $"({result.ErrorDescription}), reducing batch size to {sectorsToRead} sector(s)"
                        });
                        // Allow the drive to recover after a transport error. Semaphore timeouts
                        // (error 121) indicate the drive stopped responding; TEST UNIT READY clears
                        // any pending state, and the delay gives the drive time to reset its firmware.
                        // 2-second delay gives USB drives more recovery time than the previous 1 second.
                        drive.TestUnitReady();
                        await Task.Delay(2000, ct);
                        drive.TestUnitReady();
                        // Re-issue SET CD SPEED after recovery: some drives reset their speed
                        // target after errors or long pauses, causing speed ramp-up that triggers
                        // further transport errors (semaphore timeouts on USB bridges).
                        SetReadSpeed(drive, job.ReadSpeed, profile);
                        retry = -1; // restart retry loop with reduced batch size
                        continue;
                    }

                    // "Illegal mode for this track" (ASC=0x64): the expectedSectorType
                    // doesn't match the actual sector type. This occurs on Mixed Mode CDs
                    // at data↔audio transition zones (including pregaps). Try the next
                    // expectedSectorType from the fallback list. Also reduce to single-sector
                    // mode to avoid mixed-type batches in transition zones.
                    if (IsIllegalModeForTrackError(result))
                    {
                        byte? nextType = null;
                        foreach (var ft in SectorTypeFallbacks)
                        {
                            if (!triedSectorTypes.Contains(ft))
                            {
                                nextType = ft;
                                break;
                            }
                        }
                        if (nextType.HasValue)
                        {
                            progress.Report(new ReadProgress
                            {
                                LogLine = $"[Warning] Sector type mismatch at LBA {currentLba} — " +
                                          $"switching expectedSectorType from {sectorType} to {nextType.Value}"
                            });
                            sectorType = nextType.Value;
                            triedSectorTypes.Add(sectorType);
                            // Reduce to single-sector mode: in transition zones the batch may
                            // contain sectors of different types, so no single expectedSectorType
                            // works for the entire batch. Single-sector reads isolate each sector.
                            if (sectorsToRead > 1)
                                sectorsToRead = 1;
                            retry = -1; // restart retry loop with corrected sector type
                            continue;
                        }
                    }

                    if (retry < retryCount)
                    {
                        // Transport errors (Status=0xFF) at single-sector level also need
                        // full recovery: TUR + delay + TUR + SET CD SPEED. Without this,
                        // the short retry delay (200-400ms) is insufficient for USB drives
                        // to recover from semaphore timeouts (error 121).
                        if (result.Status == 0xFF)
                        {
                            progress.Report(new ReadProgress
                            {
                                LogLine = $"[Warning] Transport error at LBA {currentLba} " +
                                          $"({result.ErrorDescription}), retry {retry + 1}/{retryCount}"
                            });
                            drive.TestUnitReady();
                            await Task.Delay(2000, ct);
                            drive.TestUnitReady();
                            SetReadSpeed(drive, job.ReadSpeed, profile);
                        }
                        else
                        {
                            progress.Report(new ReadProgress
                            {
                                LogLine = $"[Warning] Read error at LBA {currentLba} " +
                                          $"({result.ErrorDescription}), retry {retry + 1}/{retryCount}"
                            });
                            // Brief delay between retries to allow the drive to recover.
                            // Optical drives need time to re-seek after read errors; immediate
                            // retries often fail repeatedly. 200ms base + 100ms per attempt
                            // gives the drive progressively more recovery time.
                            await Task.Delay(200 + retry * 100, ct);
                        }
                    }
                }
            } // end standard read path

            if (!readSuccess)
            {
                if (job.ErrorRecovery == "Yes" || job.ReadBadSectors)
                {
                    // Batch read failed — try single-sector reads to pinpoint
                    // specific bad sectors (same approach as SCSI/raw readers)
                    if (sectorsToRead > 1)
                    {
                        for (uint s = 0; s < sectorsToRead; s++)
                        {
                            ct.ThrowIfCancellationRequested();
                            bool sectorOk = false;

                            var singleSectorType = GetExpectedSectorType(toc, currentLba + s);
                            var singleTriedTypes = new System.Collections.Generic.HashSet<byte> { singleSectorType };
                            for (int sRetry = 0; sRetry <= retryCount; sRetry++)
                            {
                                var (singleResult, singleData) = drive.ReadCdRaw(
                                    currentLba + s, 1, singleSectorType,
                                    includeSubchannel,
                                    job.SubchannelMode ?? "RW",
                                    c2ErrorMode);
                                if (singleResult.Success)
                                {
                                    if (c2BytesPerSector > 0)
                                    {
                                        // Write main channel, check C2, write subchannel to .sub
                                        int mainBytes = Math.Min(mainSectorSize, singleData.Length);
                                        await outputStream.WriteAsync(singleData.AsMemory(0, mainBytes), ct);
                                        if (mainSectorSize + c2BytesPerSector <= singleData.Length)
                                        {
                                            int c2Errs = CountC2Errors(singleData, mainSectorSize, c2BytesPerSector);
                                            if (c2Errs > 0) totalC2Errors += c2Errs;
                                        }
                                        if (includeSubchannel && subStream != null)
                                        {
                                            int subOff = mainSectorSize + c2BytesPerSector;
                                            int subBytes = Math.Min(96, singleData.Length - subOff);
                                            if (subBytes > 0)
                                                await subStream.WriteAsync(singleData.AsMemory(subOff, subBytes), ct);
                                        }
                                        bytesRead += outputSectorSize;
                                    }
                                    else if (includeSubchannel && subStream != null)
                                    {
                                        // No C2 but subchannel: split main + sub
                                        int mainBytes = Math.Min(mainSectorSize, singleData.Length);
                                        await outputStream.WriteAsync(singleData.AsMemory(0, mainBytes), ct);
                                        int subOff = mainSectorSize;
                                        int subBytes = Math.Min(96, singleData.Length - subOff);
                                        if (subBytes > 0)
                                            await subStream.WriteAsync(singleData.AsMemory(subOff, subBytes), ct);
                                        bytesRead += outputSectorSize;
                                    }
                                    else
                                    {
                                        var validBytes = Math.Min(singleResult.DataTransferred, outputSectorSize);
                                        await outputStream.WriteAsync(singleData.AsMemory(0, validBytes), ct);
                                        bytesRead += validBytes;
                                    }
                                    sectorOk = true;
                                    break;
                                }

                                // ASC=0x64: try next expectedSectorType for this single sector.
                                // In data↔audio transition zones, GetExpectedSectorType may return
                                // the wrong type for pregap or post-gap sectors.
                                if (IsIllegalModeForTrackError(singleResult))
                                {
                                    byte? nextSingleType = null;
                                    foreach (var ft in SectorTypeFallbacks)
                                    {
                                        if (!singleTriedTypes.Contains(ft))
                                        {
                                            nextSingleType = ft;
                                            break;
                                        }
                                    }
                                    if (nextSingleType.HasValue)
                                    {
                                        singleSectorType = nextSingleType.Value;
                                        singleTriedTypes.Add(singleSectorType);
                                        sRetry = -1; // restart retries with new type
                                        continue;
                                    }
                                }

                                // Delay between single-sector retries
                                if (sRetry < retryCount)
                                    await Task.Delay(200 + sRetry * 100, ct);
                            }

                            if (!sectorOk)
                            {
                                await outputStream.WriteAsync(new byte[outputSectorSize], ct);
                                if (includeSubchannel && subStream != null)
                                    await subStream.WriteAsync(new byte[96], ct);
                                bytesRead += outputSectorSize;
                                errorSectorCount++;
                                progress.Report(new ReadProgress
                                {
                                    ErrorSectors = errorSectorCount,
                                    LogLine = $"[Error] Unreadable sector at LBA {currentLba + s}, filled with zeros"
                                });
                            }
                        }
                    }
                    else
                    {
                        // Single sector already failed all retries
                        var zeroBytes = (int)(sectorsToRead * outputSectorSize);
                        await outputStream.WriteAsync(new byte[zeroBytes], ct);
                        if (includeSubchannel && subStream != null)
                            await subStream.WriteAsync(new byte[(int)(sectorsToRead * 96)], ct);
                        bytesRead += zeroBytes;
                        errorSectorCount += sectorsToRead;
                        progress.Report(new ReadProgress
                        {
                            ErrorSectors = errorSectorCount,
                            LogLine = $"[Error] Unreadable sector(s) at LBA {currentLba}, filled with zeros"
                        });
                    }
                }
                else
                {
                    throw new InvalidOperationException(
                        $"Read error at LBA {currentLba}. Set error recovery to continue past bad sectors.");
                }
            }

            // Advance LBA by actual sectors read (not requested) to handle partial reads correctly.
            // On error with recovery, all sectorsToRead were handled (read or zero-filled).
            currentLba += readSuccess ? actualSectorsRead : sectorsToRead;

            // Gradual batch size recovery: after transport errors reduce rawSectorsPerRead,
            // try to restore it after consecutive successful reads. This avoids permanent
            // slowdowns from a single transport error in an otherwise healthy read.
            if (readSuccess)
            {
                consecutiveSuccessfulReads++;
                if (consecutiveSuccessfulReads >= 50 && rawSectorsPerRead < originalSectorsPerRead)
                {
                    rawSectorsPerRead = Math.Min(rawSectorsPerRead * 2, originalSectorsPerRead);
                    consecutiveSuccessfulReads = 0;
                }
            }
            else
            {
                consecutiveSuccessfulReads = 0;
            }

            // Report progress with speed calculation
            var elapsed = DateTime.Now - startTime;
            var pct = totalSectors > 0 ? (int)(currentLba * 100 / totalSectors) : 0;
            var bytesPerSec = elapsed.TotalSeconds > 0 ? bytesRead / elapsed.TotalSeconds : 0;
            // For raw reads (BIN/CUE), the bytes-per-sector is 2352 (not 2048 as in cooked mode).
            // The "x" speed multiplier must account for this: 1x CD = 75 sectors/sec × 2352 bytes
            // = 176,400 bytes/sec for raw, not 153,600 (= 150 KB/s × 1024 for cooked).
            // Without this correction, the displayed speed is inflated by ~15% (2352/2048).
            var rawBaseSpeedBps = baseSpeedBps > 0
                ? baseSpeedBps * ((double)outputSectorSize / 2048.0)
                : FormatHelper.CdBaseSpeedKBs * 1024 * ((double)outputSectorSize / 2048.0);
            var speedX = rawBaseSpeedBps > 0 ? bytesPerSec / rawBaseSpeedBps : 0;
            var remainingSectors = totalSectors - currentLba;
            var eta = bytesPerSec > 0
                ? TimeSpan.FromSeconds((double)remainingSectors * outputSectorSize / bytesPerSec)
                : TimeSpan.Zero;

            progress.Report(new ReadProgress
            {
                PercentComplete = Math.Min(pct, 99),
                SectorsRead = currentLba,
                TotalSectors = totalSectors,
                ErrorSectors = errorSectorCount,
                CurrentSpeedX = speedX,
                Elapsed = elapsed,
                Remaining = eta,
                StatusMessage = $"Reading: sector {currentLba}/{totalSectors} ({pct}%)",
                // Only log every ~100 reads to keep log clean
                LogLine = currentLba % (rawSectorsPerRead * 100) < sectorsToRead
                    ? $"LBA {currentLba}: {bytesRead / 1_048_576} MB read, {speedX:F1}x"
                    : null
            });
        }

        // Lead-out overread: attempt to read sectors beyond the lead-out position.
        // This captures data in the run-out area for complete disc preservation.
        // Stop gracefully after consecutive read failures (most drives will error
        // when they reach the physical end of the disc).
        if (job.LeadOutOverread)
        {
            progress.Report(new ReadProgress
            {
                LogLine = $"[Info] Attempting lead-out overread past LBA {currentLba}..."
            });

            const int maxConsecutiveErrors = 3;
            const int maxOverreadSectors = 150; // ~2 seconds of CD audio
            int consecutiveErrors = 0;
            int overreadSectors = 0;

            while (consecutiveErrors < maxConsecutiveErrors && overreadSectors < maxOverreadSectors)
            {
                ct.ThrowIfCancellationRequested();

                var overreadSectorType = GetExpectedSectorType(toc, currentLba);
                var (result, data) = drive.ReadCdRaw(
                    currentLba, 1, overreadSectorType, includeSubchannel,
                    job.SubchannelMode ?? "RW", c2ErrorMode);

                if (result.Success && result.DataTransferred > 0)
                {
                    consecutiveErrors = 0;
                    if (c2BytesPerSector > 0)
                    {
                        int mainBytes = Math.Min(mainSectorSize, data.Length);
                        await outputStream.WriteAsync(data.AsMemory(0, mainBytes), ct);
                        if (includeSubchannel && subStream != null && data.Length > mainSectorSize + c2BytesPerSector)
                        {
                            int subOff = mainSectorSize + c2BytesPerSector;
                            int subBytes = Math.Min(96, data.Length - subOff);
                            if (subBytes > 0)
                                await subStream.WriteAsync(data.AsMemory(subOff, subBytes), ct);
                        }
                    }
                    else if (includeSubchannel && subStream != null)
                    {
                        // No C2 but subchannel: split main + sub
                        int mainBytes = Math.Min(mainSectorSize, data.Length);
                        await outputStream.WriteAsync(data.AsMemory(0, mainBytes), ct);
                        int subOff = mainSectorSize;
                        int subBytes = Math.Min(96, data.Length - subOff);
                        if (subBytes > 0)
                            await subStream.WriteAsync(data.AsMemory(subOff, subBytes), ct);
                    }
                    else
                    {
                        int validBytes = Math.Min(result.DataTransferred, outputSectorSize);
                        await outputStream.WriteAsync(data.AsMemory(0, validBytes), ct);
                    }
                    bytesRead += outputSectorSize;
                    overreadSectors++;
                    currentLba++;
                }
                else
                {
                    consecutiveErrors++;
                }
            }

            if (overreadSectors > 0)
            {
                progress.Report(new ReadProgress
                {
                    LogLine = $"[Info] Lead-out overread: captured {overreadSectors} additional sector(s)"
                });
            }
            else
            {
                progress.Report(new ReadProgress
                {
                    LogLine = "[Info] Lead-out overread: no additional sectors could be read"
                });
            }
        }

        }  // end try (subStream)
        finally
        {
            if (subStream != null)
            {
                await subStream.DisposeAsync();
            }
        }

        // Flush the final output stream so that DetectTrackModesFromBin can read
        // the sector headers from the BIN file(s) for Mode 1/2 detection.
        await outputStream.FlushAsync(ct);

        }  // end try (outputStream)
        finally
        {
            // Ensure the output stream is always disposed, even on exceptions.
            // FileStream.DisposeAsync is idempotent, so this is safe if the stream
            // was already disposed during a file switch in the multi-file path.
            await outputStream.DisposeAsync();
        }

        if (job.OutputFormat == "BIN/CUE" || job.OutputFormat == "BIN/CUE/SBI" ||
            job.OutputFormat == "VCD" || job.OutputFormat == "SVCD" || job.OutputFormat == "XSVCD")
        {
            var cuePath = Path.ChangeExtension(job.OutputPath, ".cue");
            GenerateCueFromToc(toc, job.OutputPath, cuePath, multiFile);
            progress.Report(new ReadProgress
            {
                LogLine = $"[Info] CUE sheet generated: {cuePath}"
            });

            if (multiFile)
            {
                foreach (var tf in trackFileInfos)
                {
                    progress.Report(new ReadProgress
                    {
                        LogLine = $"[Info] Track {tf.TrackNumber:D2} written to: {Path.GetFileName(tf.FilePath)}"
                    });
                }
            }

            if (subPath != null)
            {
                progress.Report(new ReadProgress
                {
                    LogLine = $"[Info] Subchannel data written to: {subPath}"
                });
            }
        }
        else if (job.OutputFormat == "TOC/BIN")
        {
            // Generate TOC file for TOC/BIN format
            // TOC/BIN always uses single-file format (binPath = first track file or original)
            var tocBinPath = multiFile ? trackFileInfos[0].FilePath : job.OutputPath;
            var tocPath = Path.ChangeExtension(job.OutputPath, ".toc");
            GenerateTocFile(toc, tocBinPath, tocPath);
            progress.Report(new ReadProgress
            {
                LogLine = $"[Info] TOC file generated: {tocPath}"
            });

            if (subPath != null)
            {
                progress.Report(new ReadProgress
                {
                    LogLine = $"[Info] Subchannel data written to: {subPath}"
                });
            }
        }

        progress.Report(new ReadProgress
        {
            PercentComplete = 100,
            SectorsRead = totalSectors,
            TotalSectors = totalSectors,
            StatusMessage = "Read complete.",
            LogLine = $"[Info] Successfully read {FormatHelper.FormatBytes(bytesRead)}" +
                (totalC2Errors > 0 ? $" — {totalC2Errors} C2 error byte(s) detected" : "") +
                (errorSectorCount > 0 ? $", {errorSectorCount} unreadable sector(s)" : "")
        });

        // Generate MD5/SHA-1 hashes for dump verification if requested
        if (job.GenerateHashes)
        {
            foreach (var tf in trackFileInfos)
            {
                await ComputeAndReportHashesAsync(tf.FilePath, progress, ct);
            }
        }

        }
        finally
        {
            // Unlock the tray after reading — guard against exceptions to ensure cleanup
            try { drive.PreventMediumRemoval(false); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Reads a disc to CloneCD format (CCD/IMG/SUB) using SCSI READ CD commands.
    /// Creates three files: .ccd (control), .img (raw sectors), .sub (subchannel).
    /// </summary>
    public async Task ReadToCcdAsync(
        ReadJob job,
        IProgress<ReadProgress> progress,
        CancellationToken ct)
    {
        // Report progress immediately so the UI activates before any blocking operations.
        progress.Report(new ReadProgress
        {
            PercentComplete = 0,
            StatusMessage = "Initializing drive...",
            LogLine = $"[Info] Opening device: {job.DevicePath}"
        });

        using var drive = new OpticalDrive(job.DevicePath);

        progress.Report(new ReadProgress { StatusMessage = "Waiting for drive to become ready..." });
        if (!drive.WaitForDriveReady())
            throw new InvalidOperationException("Drive is not ready. Please insert a disc and close the tray.");

        // Lock the tray during reading
        drive.PreventMediumRemoval(true);

        try
        {

        // Detect media type and set read speed
        var profile = drive.GetCurrentProfile();
        var baseSpeedBps = GetBaseSpeedForProfile(profile);
        if (!SetReadSpeed(drive, job.ReadSpeed, profile))
        {
            progress.Report(new ReadProgress
            {
                LogLine = $"[Warning] Could not set read speed to {job.ReadSpeed}x — drive may use default speed"
            });
        }

        // Configure error recovery
        if (job.ErrorRecovery == "Yes")
            drive.SetErrorRecovery((byte)job.RetryCount, transferBadBlocks: true);
        else if (job.ErrorRecovery == "No")
            drive.SetErrorRecovery(0, transferBadBlocks: false);

        // Read TOC
        var toc = drive.ReadToc();
        if (toc == null || toc.Entries.Count == 0)
            throw new InvalidOperationException("Could not read Table of Contents.");

        progress.Report(new ReadProgress
        {
            StatusMessage = $"Reading disc to CCD/IMG/SUB ({toc.Entries.Count} TOC entries)...",
            LogLine = $"[Info] CCD output — TOC: tracks {toc.FirstTrack}-{toc.LastTrack}"
        });

        // Determine total sectors from lead-out
        uint totalSectors = 0;
        foreach (var entry in toc.Entries)
        {
            if (entry.TrackNumber == 0xAA)
            {
                totalSectors = entry.StartLba;
                break;
            }
        }

        if (totalSectors == 0 && toc.Entries.Count > 0)
        {
            var lastEntry = toc.Entries[^1];
            totalSectors = lastEntry.TrackNumber == 0xAA
                ? lastEntry.StartLba
                : lastEntry.StartLba + 150;
        }

        // Always read subchannel for CCD format (it's a key feature of CloneCD)
        bool readSubchannel = job.SubchannelMode != "None";
        int imgSectorSize = 2352;
        int subSectorSize = readSubchannel ? 96 : 0;
        // C2 error support for CCD format — same as BIN/CUE
        int c2ErrorMode = job.C2ErrorFlags ? 1 : 0;
        int c2BytesPerSector = c2ErrorMode == 1 ? 294 : 0;
        long totalC2Errors = 0;
        long c2ErrorSectorCount = 0;
        const int maxC2LogEntries = 50;
        // Full sector size includes C2 data in the drive response
        int fullSectorSize = imgSectorSize + c2BytesPerSector + subSectorSize;
        long bytesRead = 0;
        long errorSectorCount = 0;
        uint currentLba = 0;
        var startTime = DateTime.Now;
        int retryCount = job.RetryCount;

        if (job.C2ErrorFlags)
        {
            progress.Report(new ReadProgress
            {
                LogLine = "[Info] C2 error flags enabled for CCD read — byte-level error detection active"
            });
        }

        // Build sorted list of track boundary LBAs including pregap-adjusted boundaries
        // (same approach as ReadToBinCueAsync — prevents batches from crossing data↔audio transitions).
        var trackBoundaries = BuildTrackBoundaries(toc);

        // Clear UNIT ATTENTION that may be pending after drive configuration commands
        drive.TestUnitReady();
        drive.TestUnitReady();

        // ---- Test read: validate READ CD works before committing to the full dump ----
        {
            uint testLba = 0;
            foreach (var e in toc.Entries)
            {
                if (e.TrackNumber != 0xAA) { testLba = e.StartLba; break; }
            }

            var testSectorType = GetExpectedSectorType(toc, testLba);
            var (testResult, _) = drive.ReadCdRaw(
                testLba, 1, testSectorType, readSubchannel,
                job.SubchannelMode != "None" ? job.SubchannelMode : "RW",
                c2ErrorMode);

            if (!testResult.Success)
            {
                progress.Report(new ReadProgress
                {
                    LogLine = $"[Warning] CCD: initial READ CD test failed at LBA {testLba} " +
                              $"({testResult.ErrorDescription})"
                });

                // Try without C2
                if (c2ErrorMode != 0)
                {
                    var (testNoC2, _) = drive.ReadCdRaw(
                        testLba, 1, testSectorType, readSubchannel,
                        job.SubchannelMode != "None" ? job.SubchannelMode : "RW", 0);
                    if (testNoC2.Success)
                    {
                        progress.Report(new ReadProgress
                        {
                            LogLine = "[Warning] CCD: drive does not support C2 error flags — disabling."
                        });
                        c2ErrorMode = 0;
                        c2BytesPerSector = 0;
                    }
                }

                // Try without subchannel (use c2=0 to isolate the subchannel variable)
                if (readSubchannel)
                {
                    var (testNoSub, _) = drive.ReadCdRaw(testLba, 1, testSectorType, false, "RW", 0);
                    if (testNoSub.Success)
                    {
                        readSubchannel = false;

                        // Check if C2 alone is supported
                        if (c2ErrorMode != 0)
                        {
                            var (testC2Only, _) = drive.ReadCdRaw(testLba, 1, testSectorType, false, "RW", c2ErrorMode);
                            if (!testC2Only.Success)
                            {
                                progress.Report(new ReadProgress
                                {
                                    LogLine = "[Warning] CCD: drive does not support subchannel or C2 — disabling both."
                                });
                                c2ErrorMode = 0;
                                c2BytesPerSector = 0;
                            }
                            else
                            {
                                progress.Report(new ReadProgress
                                {
                                    LogLine = "[Warning] CCD: drive does not support subchannel — " +
                                              "disabling. C2 error flags remain active."
                                });
                            }
                        }
                        else
                        {
                            progress.Report(new ReadProgress
                            {
                                LogLine = $"[Warning] CCD: drive does not support subchannel mode " +
                                          $"'{job.SubchannelMode}' — disabling subchannel reading."
                            });
                        }
                    }
                    else
                    {
                        var (testMinimal, _) = drive.ReadCdRaw(testLba, 1, testSectorType, false, "RW", 0);
                        if (testMinimal.Success)
                        {
                            progress.Report(new ReadProgress
                            {
                                LogLine = "[Warning] CCD: drive requires minimal READ CD parameters — " +
                                          "disabling subchannel and C2."
                            });
                            readSubchannel = false;
                            c2ErrorMode = 0;
                            c2BytesPerSector = 0;
                        }
                        else
                        {
                            throw new InvalidOperationException(
                                $"Drive cannot read this disc with READ CD command. " +
                                $"Test read at LBA {testLba} failed: {testMinimal.ErrorDescription}.");
                        }
                    }
                }
            }

            // Recalculate sizes after potential parameter changes
            subSectorSize = readSubchannel ? 96 : 0;
            fullSectorSize = imgSectorSize + c2BytesPerSector + subSectorSize;
        }

        // Adaptive batch size for CCD raw reads (same as BIN/CUE)
        int rawSectorsPerRead = Math.Max(1, MaxRawTransferBytes / fullSectorSize);
        rawSectorsPerRead = Math.Min(rawSectorsPerRead, SectorsPerRead);
        int originalSectorsPerRead = rawSectorsPerRead;
        int consecutiveSuccessfulReads = 0;

        // Derive file paths: .ccd -> .img and .sub
        var ccdPath = job.OutputPath;
        var imgPath = Path.ChangeExtension(ccdPath, ".img");
        var subPath = Path.ChangeExtension(ccdPath, ".sub");

        await using var imgStream = new FileStream(
            imgPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true);
        FileStream? subStream = readSubchannel
            ? new FileStream(subPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true)
            : null;

        try
        {
            while (currentLba < totalSectors)
            {
                ct.ThrowIfCancellationRequested();

                var sectorsToRead = (uint)Math.Min(rawSectorsPerRead, totalSectors - currentLba);

                // Clamp batch to current track boundary (same as ReadToBinCueAsync)
                foreach (var boundary in trackBoundaries)
                {
                    if (boundary > currentLba && boundary < currentLba + sectorsToRead)
                    {
                        sectorsToRead = boundary - currentLba;
                        break;
                    }
                }

                bool readSuccess = false;
                uint actualSectorsRead = sectorsToRead; // tracks actual sectors for LBA advancement

                var sectorType = GetExpectedSectorType(toc, currentLba);
                var triedSectorTypes = new System.Collections.Generic.HashSet<byte> { sectorType };
                for (int retry = 0; retry <= retryCount; retry++)
                {
                    var (result, data) = drive.ReadCdRaw(
                        currentLba, sectorsToRead,
                        sectorType,
                        readSubchannel,
                        job.SubchannelMode != "None" ? job.SubchannelMode : "RW",
                        c2ErrorMode);

                    if (result.Success && result.DataTransferred > 0)
                    {
                        if (readSubchannel || c2BytesPerSector > 0)
                        {
                            // Data per sector: [main(2352)][c2(0/294)][sub(0/96)]
                            for (uint s = 0; s < sectorsToRead; s++)
                            {
                                int baseOffset = (int)(s * fullSectorSize);

                                // Write sector data to IMG
                                int sectorBytes = Math.Min(imgSectorSize, data.Length - baseOffset);
                                if (sectorBytes > 0)
                                    await imgStream.WriteAsync(data.AsMemory(baseOffset, sectorBytes), ct);

                                // Check C2 error flags if present
                                if (c2BytesPerSector > 0)
                                {
                                    int c2Offset = baseOffset + imgSectorSize;
                                    if (c2Offset + c2BytesPerSector <= data.Length)
                                    {
                                        int c2Errs = CountC2Errors(data, c2Offset, c2BytesPerSector);
                                        if (c2Errs > 0)
                                        {
                                            totalC2Errors += c2Errs;
                                            c2ErrorSectorCount++;
                                            if (c2ErrorSectorCount <= maxC2LogEntries)
                                            {
                                                progress.Report(new ReadProgress
                                                {
                                                    LogLine = $"[C2] CCD LBA {currentLba + s}: {c2Errs} byte-level C2 error(s)"
                                                });
                                            }
                                            else if (c2ErrorSectorCount == maxC2LogEntries + 1)
                                            {
                                                progress.Report(new ReadProgress
                                                {
                                                    LogLine = $"[C2] Further per-sector C2 reports suppressed (>{maxC2LogEntries} sectors). Summary at end."
                                                });
                                            }
                                        }
                                    }
                                }

                                // Write subchannel data to SUB (after C2 data in the drive response)
                                if (readSubchannel && subStream != null)
                                {
                                    int subOffset = baseOffset + imgSectorSize + c2BytesPerSector;
                                    int subBytes = Math.Min(subSectorSize, data.Length - subOffset);
                                    if (subBytes > 0)
                                        await subStream.WriteAsync(data.AsMemory(subOffset, subBytes), ct);
                                }
                            }
                        }
                        else
                        {
                            // No subchannel or C2 — write raw sectors directly
                            var validBytes = Math.Min(result.DataTransferred,
                                (int)Math.Min(sectorsToRead * (long)imgSectorSize, int.MaxValue));
                            validBytes = Math.Min(validBytes, data.Length);
                            await imgStream.WriteAsync(data.AsMemory(0, validBytes), ct);
                        }

                        bytesRead += sectorsToRead * imgSectorSize;
                        actualSectorsRead = sectorsToRead;
                        readSuccess = true;
                        break;
                    }

                    // Transport-level failure: halve batch size and allow drive recovery
                    if (result.Status == 0xFF && sectorsToRead > 1)
                    {
                        sectorsToRead = Math.Max(1, sectorsToRead / 2);
                        rawSectorsPerRead = (int)Math.Min(rawSectorsPerRead, sectorsToRead);
                        progress.Report(new ReadProgress
                        {
                            LogLine = $"[Warning] CCD transport error at LBA {currentLba} " +
                                      $"({result.ErrorDescription}), reducing batch size to {sectorsToRead} sector(s)"
                        });
                        // Allow the drive to recover after a transport error
                        drive.TestUnitReady();
                        await Task.Delay(2000, ct);
                        drive.TestUnitReady();
                        // Re-issue SET CD SPEED to prevent drive speed ramp-up after recovery
                        SetReadSpeed(drive, job.ReadSpeed, profile);
                        retry = -1; // restart retry loop with reduced batch size
                        continue;
                    }

                    // "Illegal mode for this track" (ASC=0x64): try next expectedSectorType
                    if (IsIllegalModeForTrackError(result))
                    {
                        byte? nextType = null;
                        foreach (var ft in SectorTypeFallbacks)
                        {
                            if (!triedSectorTypes.Contains(ft))
                            {
                                nextType = ft;
                                break;
                            }
                        }
                        if (nextType.HasValue)
                        {
                            progress.Report(new ReadProgress
                            {
                                LogLine = $"[Warning] CCD sector type mismatch at LBA {currentLba} — " +
                                          $"switching expectedSectorType from {sectorType} to {nextType.Value}"
                            });
                            sectorType = nextType.Value;
                            triedSectorTypes.Add(sectorType);
                            if (sectorsToRead > 1)
                                sectorsToRead = 1;
                            retry = -1;
                            continue;
                        }
                    }

                    if (retry < retryCount)
                    {
                        progress.Report(new ReadProgress
                        {
                            LogLine = $"[Warning] CCD read error at LBA {currentLba} " +
                                      $"({result.ErrorDescription}), retry {retry + 1}/{retryCount}"
                        });
                        await Task.Delay(200 + retry * 100, ct);
                    }
                }

                if (!readSuccess)
                {
                    if (job.ErrorRecovery == "Yes" || job.ReadBadSectors)
                    {
                        // Single-sector fallback for bad sectors
                        for (uint s = 0; s < sectorsToRead; s++)
                        {
                            ct.ThrowIfCancellationRequested();
                            bool sectorOk = false;

                            var singleSectorType = GetExpectedSectorType(toc, currentLba + s);
                            var singleTriedTypes = new System.Collections.Generic.HashSet<byte> { singleSectorType };
                            for (int sRetry = 0; sRetry <= retryCount; sRetry++)
                            {
                                var (singleResult, singleData) = drive.ReadCdRaw(
                                    currentLba + s, 1, singleSectorType, readSubchannel,
                                    job.SubchannelMode != "None" ? job.SubchannelMode : "RW",
                                    c2ErrorMode);

                                if (singleResult.Success)
                                {
                                    // Write main channel (2352 bytes)
                                    var sectorBytes = Math.Min(imgSectorSize, singleData.Length);
                                    await imgStream.WriteAsync(singleData.AsMemory(0, sectorBytes), ct);

                                    // Check C2 error flags if present
                                    if (c2BytesPerSector > 0 && singleData.Length >= imgSectorSize + c2BytesPerSector)
                                    {
                                        int c2Errs = CountC2Errors(singleData, imgSectorSize, c2BytesPerSector);
                                        if (c2Errs > 0) totalC2Errors += c2Errs;
                                    }

                                    // Write subchannel (after C2 data)
                                    if (readSubchannel && subStream != null)
                                    {
                                        int subOff = imgSectorSize + c2BytesPerSector;
                                        if (singleData.Length > subOff)
                                        {
                                            var subBytes = Math.Min(subSectorSize, singleData.Length - subOff);
                                            await subStream.WriteAsync(singleData.AsMemory(subOff, subBytes), ct);
                                        }
                                    }
                                    bytesRead += imgSectorSize;
                                    sectorOk = true;
                                    break;
                                }

                                // ASC=0x64: try next sector type for this single sector
                                if (IsIllegalModeForTrackError(singleResult))
                                {
                                    byte? nextSingleType = null;
                                    foreach (var ft in SectorTypeFallbacks)
                                    {
                                        if (!singleTriedTypes.Contains(ft))
                                        {
                                            nextSingleType = ft;
                                            break;
                                        }
                                    }
                                    if (nextSingleType.HasValue)
                                    {
                                        singleSectorType = nextSingleType.Value;
                                        singleTriedTypes.Add(singleSectorType);
                                        sRetry = -1;
                                        continue;
                                    }
                                }

                                // Delay between single-sector retries
                                if (sRetry < retryCount)
                                    await Task.Delay(200 + sRetry * 100, ct);
                            }

                            if (!sectorOk)
                            {
                                await imgStream.WriteAsync(new byte[imgSectorSize], ct);
                                if (readSubchannel && subStream != null)
                                    await subStream.WriteAsync(new byte[subSectorSize], ct);
                                bytesRead += imgSectorSize;
                                errorSectorCount++;
                                progress.Report(new ReadProgress
                                {
                                    ErrorSectors = errorSectorCount,
                                    LogLine = $"[Error] CCD: unreadable sector at LBA {currentLba + s}, filled with zeros"
                                });
                            }
                        }
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            $"CCD read error at LBA {currentLba}. Enable error recovery to continue.");
                    }
                }

                // Advance by actual sectors read on success, or full count on error
                currentLba += readSuccess ? actualSectorsRead : sectorsToRead;

                // Gradual batch size recovery after transport error reductions
                if (readSuccess)
                {
                    consecutiveSuccessfulReads++;
                    if (consecutiveSuccessfulReads >= 50 && rawSectorsPerRead < originalSectorsPerRead)
                    {
                        rawSectorsPerRead = Math.Min(rawSectorsPerRead * 2, originalSectorsPerRead);
                        consecutiveSuccessfulReads = 0;
                    }
                }
                else
                {
                    consecutiveSuccessfulReads = 0;
                }

                // Progress reporting
                var elapsed = DateTime.Now - startTime;
                var pct = totalSectors > 0 ? (int)(currentLba * 100 / totalSectors) : 0;
                var bytesPerSec = elapsed.TotalSeconds > 0 ? bytesRead / elapsed.TotalSeconds : 0;
                // For raw reads (CCD), correct the base speed for 2352-byte sectors (same as BIN/CUE).
                var rawBaseSpeedBps = baseSpeedBps > 0
                    ? baseSpeedBps * ((double)imgSectorSize / 2048.0)
                    : FormatHelper.CdBaseSpeedKBs * 1024 * ((double)imgSectorSize / 2048.0);
                var speedX = rawBaseSpeedBps > 0 ? bytesPerSec / rawBaseSpeedBps : 0;
                var remainingSectors = totalSectors - currentLba;
                var eta = bytesPerSec > 0
                    ? TimeSpan.FromSeconds((double)remainingSectors * imgSectorSize / bytesPerSec)
                    : TimeSpan.Zero;

                progress.Report(new ReadProgress
                {
                    PercentComplete = Math.Min(pct, 99),
                    SectorsRead = currentLba,
                    TotalSectors = totalSectors,
                    ErrorSectors = errorSectorCount,
                    CurrentSpeedX = speedX,
                    Elapsed = elapsed,
                    Remaining = eta,
                    StatusMessage = $"CCD read: sector {currentLba}/{totalSectors} ({pct}%)",
                    // Only log every ~100 reads to keep log clean
                    LogLine = currentLba % (rawSectorsPerRead * 100) < sectorsToRead
                        ? $"LBA {currentLba}: {bytesRead / 1_048_576} MB read, {speedX:F1}x"
                        : null
                });
            }
        }
        finally
        {
            if (subStream != null)
                await subStream.DisposeAsync();
        }

        // Generate CCD control file from TOC data
        progress.Report(new ReadProgress
        {
            StatusMessage = "Generating CCD control file...",
            LogLine = "[Info] Writing CCD control file."
        });

        CcdWriter.WriteFromRawData(imgPath, ccdPath, toc, readSubchannel);

        progress.Report(new ReadProgress
        {
            PercentComplete = 100,
            SectorsRead = totalSectors,
            TotalSectors = totalSectors,
            StatusMessage = "CCD read complete.",
            LogLine = $"[Info] CCD: {FormatHelper.FormatBytes(bytesRead)} read" +
                (readSubchannel ? " (with subchannel)" : "") +
                (totalC2Errors > 0 ? $", {totalC2Errors} C2 error byte(s)" : "") +
                (errorSectorCount > 0 ? $", {errorSectorCount} error(s)" : "")
        });

        // Generate MD5/SHA-1 hashes for the IMG file (main disc data)
        if (job.GenerateHashes)
            await ComputeAndReportHashesAsync(imgPath, progress, ct);

        }
        finally
        {
            try { drive.PreventMediumRemoval(false); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Reads audio tracks with error correction using multiple read passes and sector verification.
    /// </summary>
    public async Task ReadAudioParanoiaAsync(
        ReadJob job,
        IProgress<ReadProgress> progress,
        CancellationToken ct)
    {
        // Report progress immediately so the UI activates before any blocking operations.
        progress.Report(new ReadProgress
        {
            PercentComplete = 0,
            StatusMessage = "Initializing drive...",
            LogLine = $"[Info] Opening device: {job.DevicePath}"
        });

        using var drive = new OpticalDrive(job.DevicePath);

        progress.Report(new ReadProgress { StatusMessage = "Waiting for drive to become ready..." });
        if (!drive.WaitForDriveReady())
            throw new InvalidOperationException("Drive is not ready. Please insert a disc and close the tray.");

        // Lock the tray during reading to prevent accidental ejection
        drive.PreventMediumRemoval(true);

        try
        {

        // Set read speed for audio extraction (lower speeds yield better results)
        var audioProfile = drive.GetCurrentProfile();
        if (!SetReadSpeed(drive, job.ReadSpeed, audioProfile))
        {
            progress.Report(new ReadProgress
            {
                LogLine = $"[Warning] Could not set read speed to {job.ReadSpeed}x — drive may use default speed"
            });
        }

        // Configure error recovery for audio extraction
        drive.SetErrorRecovery((byte)job.RetryCount, transferBadBlocks: true);

        var toc = drive.ReadToc();
        if (toc == null || toc.Entries.Count == 0)
            throw new InvalidOperationException("Could not read Table of Contents.");

        progress.Report(new ReadProgress
        {
            StatusMessage = "Reading audio disc with error correction...",
            LogLine = $"[Info] Audio paranoia mode: reading {job.DevicePath}"
        });

        // Log drive read offset for audio accuracy
        if (job.DriveReadOffset != 0)
        {
            progress.Report(new ReadProgress
            {
                LogLine = $"[Info] Drive read offset: {job.DriveReadOffset} samples " +
                          $"({job.DriveReadOffset * 4} bytes) — apply offset correction for byte-perfect rip"
            });
        }

        // Find audio tracks from TOC
        uint totalAudioSectors = 0;
        foreach (var entry in toc.Entries)
        {
            if (entry.IsAudio && entry.TrackNumber != 0xAA)
                totalAudioSectors += GetTrackLength(toc, entry);
        }

        // Read audio sectors with retry/verification
        uint sectorsRead = 0;
        var startTime = DateTime.Now;

        await using var outputStream = new FileStream(
            job.OutputPath, FileMode.Create, FileAccess.Write,
            FileShare.None, 65536, true);

        // Pre-calculate expected audio data size to determine header format upfront.
        // RF64 (80-byte header) is needed when the RIFF container size exceeds 32-bit limits.
        // Standard WAV uses a 44-byte header. If we write a 44-byte placeholder but
        // later need RF64, the 80-byte header would overwrite 36 bytes of audio data.
        // RF64 header layout: RF64(4) + size(4) + WAVE(4) + ds64(4) + chunkSize(4) +
        //   riffSize(8) + dataSize(8) + sampleCount(8) + tableLen(4) = 48 bytes,
        //   then fmt(4) + fmtSize(4) + fmtData(16) + data(4) + dataSize(4) = 32 bytes.
        //   Total = 48 + 32 = 80 bytes.
        long estimatedDataSize = (long)totalAudioSectors * RawCdSectorSize;
        bool expectRf64 = (estimatedDataSize + 36) > int.MaxValue;
        int wavHeaderSize = expectRf64 ? 80 : 44;

        // Write WAV header placeholder (will be updated at the end)
        var wavHeader = new byte[wavHeaderSize];
        await outputStream.WriteAsync(wavHeader, ct);

        // Adaptive batch size for audio raw reads
        int audioSectorsPerRead = Math.Max(1, MaxRawTransferBytes / RawCdSectorSize);
        audioSectorsPerRead = Math.Min(audioSectorsPerRead, SectorsPerRead);

        foreach (var entry in toc.Entries)
        {
            if (entry.TrackNumber == 0xAA || !entry.IsAudio) continue;
            ct.ThrowIfCancellationRequested();

            var trackLength = GetTrackLength(toc, entry);
            uint trackLba = entry.StartLba;

            for (uint i = 0; i < trackLength; i += (uint)audioSectorsPerRead)
            {
                ct.ThrowIfCancellationRequested();
                var count = (uint)Math.Min(audioSectorsPerRead, trackLength - i);

                byte[]? bestData = null;
                int bestConfidence = 0;

                // Paranoia: read each sector multiple times and pick the best result
                // True paranoia compares data between reads to verify consistency
                int passes = job.JitterCorrection ? 3 : 1;
                for (int pass = 0; pass < passes; pass++)
                {
                    var (result, data) = drive.ReadCdRaw(trackLba + i, count, 1, false); // 1 = CD-DA (audio)
                    if (result.Success)
                    {
                        if (bestData == null)
                        {
                            bestData = data;
                            bestConfidence = 1;
                        }
                        else
                        {
                            // Compare with previous read to verify data consistency.
                            // Clamp comparison length to the smaller of the two buffers
                            // to prevent out-of-bounds reads. Use long arithmetic to avoid
                            // overflow when count * 2352 exceeds int.MaxValue.
                            var validLen = (int)Math.Min((long)count * RawCdSectorSize, int.MaxValue);
                            var compareLen = Math.Min(validLen, Math.Min(data.Length, bestData.Length));
                            bool match = data.AsSpan(0, compareLen).SequenceEqual(bestData.AsSpan(0, compareLen));
                            if (match)
                            {
                                bestConfidence++;
                                // Need all remaining passes to agree for true paranoia confidence
                                if (bestConfidence >= passes) break;
                            }
                            else
                            {
                                // Data mismatch — use the latest read and reset confidence
                                bestData = data;
                                bestConfidence = 1;
                            }
                        }
                    }
                }

                if (bestData != null)
                {
                    var validBytes = Math.Min((int)(count * RawCdSectorSize), bestData.Length);
                    await outputStream.WriteAsync(bestData.AsMemory(0, validBytes), ct);
                }
                else
                {
                    // Zero-fill unreadable sectors
                    var zeroBytes = (int)(count * 2352);
                    await outputStream.WriteAsync(new byte[zeroBytes], ct);
                }

                sectorsRead += count;

                var elapsed = DateTime.Now - startTime;
                var pct = totalAudioSectors > 0
                    ? (int)(sectorsRead * 100 / totalAudioSectors) : 0;
                long bytesRead = (long)sectorsRead * RawCdSectorSize;
                var bytesPerSec = elapsed.TotalSeconds > 0 ? bytesRead / elapsed.TotalSeconds : 0;
                // Audio CD speed: 1x = 150 KiB/s (raw 2352-byte sectors at 75 sectors/sec)
                var speedX = bytesPerSec / (FormatHelper.CdBaseSpeedKBs * 1024);
                var remainingSectors = totalAudioSectors - sectorsRead;
                var eta = bytesPerSec > 0
                    ? TimeSpan.FromSeconds((double)remainingSectors * RawCdSectorSize / bytesPerSec)
                    : TimeSpan.Zero;

                progress.Report(new ReadProgress
                {
                    PercentComplete = Math.Min(pct, 99),
                    SectorsRead = sectorsRead,
                    TotalSectors = totalAudioSectors,
                    CurrentSpeedX = speedX,
                    Elapsed = elapsed,
                    Remaining = eta,
                    StatusMessage = $"Reading audio: {pct}% (track {entry.TrackNumber})",
                    // Only log every ~100 reads to keep log clean
                    LogLine = sectorsRead % (audioSectorsPerRead * 100) < count
                        ? $"Track {entry.TrackNumber}: sector {i}/{trackLength}"
                        : null
                });
            }
        }

        // Update WAV header with actual data size.
        // The header size was pre-calculated based on estimated audio data size.
        // Use the same header size to correctly compute the actual audio data size.
        long totalPosition = outputStream.Position;
        var dataSize = totalPosition - wavHeaderSize;
        outputStream.Position = 0;
        WriteWavHeader(outputStream, dataSize);
        await outputStream.FlushAsync(ct);

        progress.Report(new ReadProgress
        {
            PercentComplete = 100,
            SectorsRead = sectorsRead,
            TotalSectors = totalAudioSectors,
            StatusMessage = "Audio read complete.",
            LogLine = $"[Info] Audio extraction complete: {FormatHelper.FormatBytes(dataSize)}"
        });

        // Generate MD5/SHA-1 hashes for dump verification if requested
        if (job.GenerateHashes)
            await ComputeAndReportHashesAsync(job.OutputPath, progress, ct);

        }
        finally
        {
            // Unlock the tray after reading — guard against exceptions to ensure cleanup
            try { drive.PreventMediumRemoval(false); } catch { /* best effort */ }
        }
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Reads disc using SCSI READ(10) commands (for Windows where FileStream
    /// access to raw device is not available via drive letters).
    /// </summary>
    private static async Task ReadViaScsiAsync(
        OpticalDrive drive,
        ReadJob job,
        uint totalSectors,
        double baseSpeedBps,
        ushort profile,
        bool useDvdBdMode,
        IProgress<ReadProgress> progress,
        CancellationToken ct)
    {
        int sectorSize = job.SectorSize;
        long bytesRead = 0;
        long errorSectorCount = 0;
        uint sectorsRead = 0;
        var startTime = DateTime.Now;

        if (useDvdBdMode)
        {
            progress.Report(new ReadProgress
            {
                LogLine = "[Info] Using SCSI READ(12) for DVD/Blu-ray media (32-bit transfer length)"
            });
        }

        await using var outputStream = new FileStream(
            job.OutputPath, FileMode.Create, FileAccess.Write,
            FileShare.None, 65536, true);

        while (sectorsRead < totalSectors)
        {
            ct.ThrowIfCancellationRequested();

            var count = (uint)Math.Min(SectorsPerRead, totalSectors - sectorsRead);
            bool readOk = false;
            uint actualCount = count; // tracks actual sectors for LBA advancement

            for (int retry = 0; retry <= job.RetryCount; retry++)
            {
                var (result, data) = drive.ReadSectors(sectorsRead, count, sectorSize, useDvdBdMode);
                if (result.Success && result.DataTransferred > 0)
                {
                    var maxBytes = (int)Math.Min((long)count * sectorSize, int.MaxValue);
                    var validBytes = Math.Min(result.DataTransferred, maxBytes);
                    await outputStream.WriteAsync(data.AsMemory(0, validBytes), ct);
                    bytesRead += validBytes;
                    // Track actual sectors read to advance LBA correctly on partial reads.
                    // sectorSize is always > 0 (from job.SectorSize).
                    // Ensure at least 1 sector advancement to prevent infinite loop on
                    // partial reads that return fewer bytes than one sector.
                    actualCount = Math.Max(1u, (uint)(validBytes / sectorSize));
                    readOk = true;
                    break;
                }

                // Transport-level failure: reduce batch size and allow drive recovery
                if (result.Status == 0xFF && count > 1)
                {
                    count = Math.Max(1, count / 2);
                    progress.Report(new ReadProgress
                    {
                        LogLine = $"[Warning] Transport error at sector {sectorsRead} " +
                                  $"({result.ErrorDescription}), reducing batch size to {count} sector(s)"
                    });
                    drive.TestUnitReady();
                    await Task.Delay(1000, ct);
                    drive.TestUnitReady();
                    // Re-issue SET CD SPEED to prevent drive speed ramp-up after recovery
                    SetReadSpeed(drive, job.ReadSpeed, profile);
                    retry = -1; // restart retries with smaller batch
                    continue;
                }

                if (retry < job.RetryCount)
                {
                    progress.Report(new ReadProgress
                    {
                        LogLine = $"[Warning] Read error at sector {sectorsRead} " +
                                  $"({result.ErrorDescription}), retry {retry + 1}/{job.RetryCount}"
                    });
                    await Task.Delay(200 + retry * 100, ct);
                }
            }

            if (!readOk && count > 1)
            {
                // Batch read failed — fall back to single-sector reads to identify
                // which specific sectors are bad (rather than marking all as bad)
                for (uint s = 0; s < count; s++)
                {
                    ct.ThrowIfCancellationRequested();
                    bool sectorOk = false;

                    for (int sRetry = 0; sRetry <= job.RetryCount; sRetry++)
                    {
                        var (singleResult, singleData) = drive.ReadSectors(sectorsRead + s, 1, sectorSize, useDvdBdMode);
                        if (singleResult.Success && singleResult.DataTransferred > 0)
                        {
                            var validBytes = Math.Min(singleResult.DataTransferred, sectorSize);
                            await outputStream.WriteAsync(singleData.AsMemory(0, validBytes), ct);
                            bytesRead += validBytes;
                            sectorOk = true;
                            break;
                        }
                    }

                    if (!sectorOk)
                    {
                        if (job.ErrorRecovery == "Yes" || job.ReadBadSectors)
                        {
                            await outputStream.WriteAsync(new byte[sectorSize], ct);
                            bytesRead += sectorSize;
                            errorSectorCount++;
                            progress.Report(new ReadProgress
                            {
                                ErrorSectors = errorSectorCount,
                                LogLine = $"[Error] Unreadable sector at LBA {sectorsRead + s}, filled with zeros"
                            });
                        }
                        else
                        {
                            throw new InvalidOperationException(
                                $"Read error at sector {sectorsRead + s}. Enable error recovery to continue past bad sectors.");
                        }
                    }
                }
            }
            else if (!readOk)
            {
                if (job.ErrorRecovery == "Yes" || job.ReadBadSectors)
                {
                    var zeroBytes = count * sectorSize;
                    await outputStream.WriteAsync(new byte[zeroBytes], ct);
                    bytesRead += zeroBytes;
                    errorSectorCount += count;
                    progress.Report(new ReadProgress
                    {
                        ErrorSectors = errorSectorCount,
                        LogLine = $"[Error] Unreadable {count} sector(s) at LBA {sectorsRead}, filled with zeros"
                    });
                }
                else
                {
                    throw new InvalidOperationException(
                        $"Read error at sector {sectorsRead}. Enable error recovery to continue past bad sectors.");
                }
            }

            // Advance by actual sectors read on success, or full count on error
            // (error path handles all sectors via zero-fill or single-sector retry)
            sectorsRead += readOk ? actualCount : count;

            if (sectorsRead % 100 == 0 || sectorsRead >= totalSectors)
            {
                var elapsed = DateTime.Now - startTime;
                var pct = totalSectors > 0 ? (int)(sectorsRead * 100 / totalSectors) : 0;
                var bytesPerSec = elapsed.TotalSeconds > 0 ? bytesRead / elapsed.TotalSeconds : 0;
                var speedX = baseSpeedBps > 0 ? bytesPerSec / baseSpeedBps : bytesPerSec / (FormatHelper.CdBaseSpeedKBs * 1024);
                var remainingSectors = totalSectors - sectorsRead;
                var eta = bytesPerSec > 0
                    ? TimeSpan.FromSeconds((double)remainingSectors * sectorSize / bytesPerSec)
                    : TimeSpan.Zero;

                progress.Report(new ReadProgress
                {
                    PercentComplete = Math.Min(pct, 99),
                    SectorsRead = sectorsRead,
                    TotalSectors = totalSectors,
                    ErrorSectors = errorSectorCount,
                    CurrentSpeedX = speedX,
                    Elapsed = elapsed,
                    Remaining = eta,
                    StatusMessage = $"Reading: {FormatHelper.FormatBytes(bytesRead)} ({pct}%)",
                    // Only log every ~3000 sectors to keep log clean
                    LogLine = sectorsRead % 3000 < count || sectorsRead >= totalSectors
                        ? $"Sector {sectorsRead}/{totalSectors}: {bytesRead / 1_048_576} MB, {speedX:F1}x"
                        : null
                });
            }
        }

        progress.Report(new ReadProgress
        {
            PercentComplete = 100,
            SectorsRead = sectorsRead,
            TotalSectors = totalSectors,
            ErrorSectors = errorSectorCount,
            StatusMessage = errorSectorCount > 0
                ? $"Read complete with {errorSectorCount} error(s)."
                : "Read complete.",
            LogLine = $"[Info] Successfully read {FormatHelper.FormatBytes(bytesRead)}" +
                (errorSectorCount > 0 ? $" ({errorSectorCount} bad sector(s) zeroed)" : "")
        });
    }
    // -----------------------------------------------------------------------

    /// <summary>Reads disc using direct FileStream I/O.</summary>
    private static async Task ReadViaFileStreamAsync(
        ReadJob job,
        uint totalSectors,
        double baseSpeedBps,
        IProgress<ReadProgress> progress,
        CancellationToken ct)
    {
        int sectorSize = job.SectorSize;
        if (sectorSize <= 0)
            throw new ArgumentException("Sector size must be positive.", nameof(job));
        int bufferSize = SectorsPerRead * sectorSize;
        var buffer = new byte[bufferSize];
        long bytesRead = 0;
        long errorSectorCount = 0;
        uint sectorsRead = 0;
        var startTime = DateTime.Now;

        await using var inputStream = new FileStream(
            job.DevicePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite, bufferSize, true);
        await using var outputStream = new FileStream(
            job.OutputPath, FileMode.Create, FileAccess.Write,
            FileShare.None, bufferSize, true);

        // If totalSectors is 0, estimate from stream length
        if (totalSectors == 0 && inputStream.CanSeek)
            totalSectors = (uint)(inputStream.Length / sectorSize);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            int read;
            try
            {
                read = await inputStream.ReadAsync(buffer.AsMemory(0, bufferSize), ct);
                if (read <= 0) break;
            }
            catch (IOException)
            {
                if (job.ErrorRecovery == "Yes" || job.ReadBadSectors)
                {
                    // Skip bad sector(s), write zeros for one sector
                    Array.Clear(buffer, 0, sectorSize);
                    read = sectorSize;
                    errorSectorCount++;
                    if (inputStream.CanSeek)
                    {
                        // Align position to the next sector boundary after the failed read.
                        // The stream position after a failed read is undefined, so we
                        // calculate the next aligned position from sectorsRead.
                        var nextPosition = (long)(sectorsRead + 1) * sectorSize;
                        inputStream.Position = nextPosition;
                    }
                    else
                        break;

                    // Throttle error reports to avoid flooding progress with bad sector messages
                    if (errorSectorCount <= 10 || errorSectorCount % 100 == 0)
                    {
                        progress.Report(new ReadProgress
                        {
                            ErrorSectors = errorSectorCount,
                            LogLine = $"[Error] Unreadable sector at LBA ~{sectorsRead}, filled with zeros" +
                                (errorSectorCount > 10 ? $" ({errorSectorCount} total errors)" : "")
                        });
                    }
                }
                else
                {
                    throw;
                }
            }

            await outputStream.WriteAsync(buffer.AsMemory(0, read), ct);
            bytesRead += read;
            // Round up partial sectors to avoid getting stuck at 0 sectors on small reads
            sectorsRead += (uint)((read + sectorSize - 1) / sectorSize);

            // Report progress every 100 sectors, but log only every 1000
            if (sectorsRead % 100 == 0)
            {
                var elapsed = DateTime.Now - startTime;
                var pct = totalSectors > 0 ? (int)(sectorsRead * 100 / totalSectors) : 0;
                var bytesPerSec = elapsed.TotalSeconds > 0 ? bytesRead / elapsed.TotalSeconds : 0;
                var speedX = baseSpeedBps > 0 ? bytesPerSec / baseSpeedBps : bytesPerSec / (FormatHelper.CdBaseSpeedKBs * 1024);
                var remainingSectors = totalSectors > sectorsRead ? totalSectors - sectorsRead : 0;
                var eta = bytesPerSec > 0
                    ? TimeSpan.FromSeconds((double)remainingSectors * sectorSize / bytesPerSec)
                    : TimeSpan.Zero;

                progress.Report(new ReadProgress
                {
                    PercentComplete = Math.Min(pct, 99),
                    SectorsRead = sectorsRead,
                    TotalSectors = totalSectors,
                    ErrorSectors = errorSectorCount,
                    CurrentSpeedX = speedX,
                    Elapsed = elapsed,
                    Remaining = eta,
                    StatusMessage = $"Reading: {FormatHelper.FormatBytes(bytesRead)} ({pct}%)",
                    LogLine = sectorsRead % 3000 == 0
                        ? $"Sector {sectorsRead}/{totalSectors}: {bytesRead / 1_048_576} MB, {speedX:F1}x"
                        : null
                });
            }
        }

        progress.Report(new ReadProgress
        {
            PercentComplete = 100,
            SectorsRead = sectorsRead,
            TotalSectors = totalSectors,
            ErrorSectors = errorSectorCount,
            StatusMessage = errorSectorCount > 0
                ? $"Read complete with {errorSectorCount} error(s)."
                : "Read complete.",
            LogLine = $"[Info] Successfully read {FormatHelper.FormatBytes(bytesRead)}" +
                (errorSectorCount > 0 ? $" ({errorSectorCount} bad sector(s) zeroed)" : "")
        });
    }

    /// <summary>Reads raw sectors via SCSI READ CD commands.</summary>
    private static async Task ReadRawSectorsAsync(
        OpticalDrive drive,
        ReadJob job,
        uint totalSectors,
        double baseSpeedBps,
        ushort profile,
        TocData? toc,
        IProgress<ReadProgress> progress,
        CancellationToken ct)
    {
        bool includeSubchannel = job.SubchannelMode != "None";
        int sectorSize = 2352 + (includeSubchannel ? 96 : 0);
        long bytesRead = 0;
        long errorSectorCount = 0;
        uint sectorsRead = 0;
        var startTime = DateTime.Now;

        // Build sorted list of track boundary LBAs including pregap-adjusted boundaries
        var trackBoundaries = new System.Collections.Generic.List<uint>();
        if (toc != null)
        {
            trackBoundaries = BuildTrackBoundaries(toc);
        }

        // Adaptive batch size for raw reads (same limit as BIN/CUE path)
        int rawSectorsPerRead = Math.Max(1, MaxRawTransferBytes / sectorSize);
        rawSectorsPerRead = Math.Min(rawSectorsPerRead, SectorsPerRead);
        int originalSectorsPerRead = rawSectorsPerRead;
        int consecutiveSuccessfulReads = 0;

        await using var outputStream = new FileStream(
            job.OutputPath, FileMode.Create, FileAccess.Write,
            FileShare.None, 65536, true);

        while (sectorsRead < totalSectors)
        {
            ct.ThrowIfCancellationRequested();

            var count = (uint)Math.Min(rawSectorsPerRead, totalSectors - sectorsRead);

            // Clamp batch to current track boundary to prevent "Illegal mode for this track"
            foreach (var boundary in trackBoundaries)
            {
                if (boundary > sectorsRead && boundary < sectorsRead + count)
                {
                    count = boundary - sectorsRead;
                    break;
                }
            }
            bool readOk = false;
            uint actualCount = count; // tracks actual sectors for LBA advancement

            var sectorType = GetExpectedSectorType(toc, sectorsRead);
            var triedSectorTypes = new System.Collections.Generic.HashSet<byte> { sectorType };
            for (int retry = 0; retry <= job.RetryCount; retry++)
            {
                var (result, data) = drive.ReadCdRaw(sectorsRead, count, sectorType, includeSubchannel,
                    job.SubchannelMode ?? "RW");
                if (result.Success && result.DataTransferred > 0)
                {
                    var validBytes = Math.Min((int)(count * sectorSize), result.DataTransferred);
                    await outputStream.WriteAsync(data.AsMemory(0, validBytes), ct);
                    bytesRead += validBytes;
                    // Track actual sectors read to advance LBA correctly on partial reads.
                    // sectorSize is always > 0 (2352+ for raw reads).
                    // Ensure at least 1 sector advancement to prevent infinite loop on
                    // zero-data reads that report success.
                    actualCount = Math.Max(1u, (uint)(validBytes / sectorSize));
                    readOk = true;
                    break;
                }

                // Transport-level failure: reduce batch size and allow drive recovery
                if (result.Status == 0xFF && count > 1)
                {
                    count = Math.Max(1, count / 2);
                    rawSectorsPerRead = (int)Math.Min(rawSectorsPerRead, count);
                    progress.Report(new ReadProgress
                    {
                        LogLine = $"[Warning] Transport error at sector {sectorsRead} " +
                                  $"({result.ErrorDescription}), reducing batch size to {count} sector(s)"
                    });
                    drive.TestUnitReady();
                    await Task.Delay(2000, ct);
                    drive.TestUnitReady();
                    // Re-issue SET CD SPEED to prevent drive speed ramp-up after recovery
                    SetReadSpeed(drive, job.ReadSpeed, profile);
                    retry = -1;
                    continue;
                }

                // "Illegal mode for this track" (ASC=0x64): try next expectedSectorType
                if (IsIllegalModeForTrackError(result))
                {
                    byte? nextType = null;
                    foreach (var ft in SectorTypeFallbacks)
                    {
                        if (!triedSectorTypes.Contains(ft))
                        {
                            nextType = ft;
                            break;
                        }
                    }
                    if (nextType.HasValue)
                    {
                        progress.Report(new ReadProgress
                        {
                            LogLine = $"[Warning] Sector type mismatch at LBA {sectorsRead} — " +
                                      $"switching expectedSectorType from {sectorType} to {nextType.Value}"
                        });
                        sectorType = nextType.Value;
                        triedSectorTypes.Add(sectorType);
                        if (count > 1)
                            count = 1;
                        retry = -1;
                        continue;
                    }
                }

                if (retry < job.RetryCount)
                {
                    progress.Report(new ReadProgress
                    {
                        LogLine = $"[Warning] Raw read error at sector {sectorsRead} " +
                                  $"({result.ErrorDescription}), retry {retry + 1}/{job.RetryCount}"
                    });
                    await Task.Delay(200 + retry * 100, ct);
                }
            }

            if (!readOk && count > 1)
            {
                // Batch read failed — fall back to single-sector reads to pinpoint bad sectors
                for (uint s = 0; s < count; s++)
                {
                    ct.ThrowIfCancellationRequested();
                    bool sectorOk = false;

                    var singleSectorType = GetExpectedSectorType(toc, sectorsRead + s);
                    var singleTriedTypes = new System.Collections.Generic.HashSet<byte> { singleSectorType };
                    for (int sRetry = 0; sRetry <= job.RetryCount; sRetry++)
                    {
                        var (singleResult, singleData) = drive.ReadCdRaw(sectorsRead + s, 1, singleSectorType,
                            includeSubchannel, job.SubchannelMode ?? "RW");
                        if (singleResult.Success && singleResult.DataTransferred > 0)
                        {
                            var validBytes = Math.Min(singleResult.DataTransferred, sectorSize);
                            await outputStream.WriteAsync(singleData.AsMemory(0, validBytes), ct);
                            bytesRead += validBytes;
                            sectorOk = true;
                            break;
                        }

                        // ASC=0x64: try next sector type for this single sector
                        if (IsIllegalModeForTrackError(singleResult))
                        {
                            byte? nextSingleType = null;
                            foreach (var ft in SectorTypeFallbacks)
                            {
                                if (!singleTriedTypes.Contains(ft))
                                {
                                    nextSingleType = ft;
                                    break;
                                }
                            }
                            if (nextSingleType.HasValue)
                            {
                                singleSectorType = nextSingleType.Value;
                                singleTriedTypes.Add(singleSectorType);
                                sRetry = -1;
                                continue;
                            }
                        }

                        // Delay between single-sector retries
                        if (sRetry < job.RetryCount)
                            await Task.Delay(200 + sRetry * 100, ct);
                    }

                    if (!sectorOk)
                    {
                        if (job.ErrorRecovery == "Yes" || job.ReadBadSectors)
                        {
                            await outputStream.WriteAsync(new byte[sectorSize], ct);
                            bytesRead += sectorSize;
                            errorSectorCount++;
                            progress.Report(new ReadProgress
                            {
                                ErrorSectors = errorSectorCount,
                                LogLine = $"[Error] Unreadable raw sector at LBA {sectorsRead + s}, filled with zeros"
                            });
                        }
                        else
                        {
                            throw new InvalidOperationException(
                                $"Read error at sector {sectorsRead + s}. Enable error recovery to continue past bad sectors.");
                        }
                    }
                }
            }
            else if (!readOk)
            {
                if (job.ErrorRecovery == "Yes" || job.ReadBadSectors)
                {
                    var zeroBytes = (int)(count * sectorSize);
                    await outputStream.WriteAsync(new byte[zeroBytes], ct);
                    bytesRead += zeroBytes;
                    errorSectorCount += count;
                    progress.Report(new ReadProgress
                    {
                        ErrorSectors = errorSectorCount,
                        LogLine = $"[Error] Unreadable {count} raw sector(s) at LBA {sectorsRead}, filled with zeros"
                    });
                }
                else
                {
                    throw new InvalidOperationException(
                        $"Read error at sector {sectorsRead}.");
                }
            }

            // Advance by actual sectors read on success, or full count on error
            // (error path handles all sectors via zero-fill or single-sector retry)
            sectorsRead += readOk ? actualCount : count;

            // Gradual batch size recovery after transport error reductions
            if (readOk)
            {
                consecutiveSuccessfulReads++;
                if (consecutiveSuccessfulReads >= 50 && rawSectorsPerRead < originalSectorsPerRead)
                {
                    rawSectorsPerRead = Math.Min(rawSectorsPerRead * 2, originalSectorsPerRead);
                    consecutiveSuccessfulReads = 0;
                }
            }
            else
            {
                consecutiveSuccessfulReads = 0;
            }

            if (sectorsRead % 100 == 0 || sectorsRead >= totalSectors)
            {
                var elapsed = DateTime.Now - startTime;
                var pct = totalSectors > 0 ? (int)(sectorsRead * 100 / totalSectors) : 0;
                var bytesPerSec = elapsed.TotalSeconds > 0 ? bytesRead / elapsed.TotalSeconds : 0;
                // For raw reads, correct the base speed for 2352-byte sectors
                var rawBaseSpeedBps = baseSpeedBps > 0
                    ? baseSpeedBps * (2352.0 / 2048.0)
                    : FormatHelper.CdBaseSpeedKBs * 1024 * (2352.0 / 2048.0);
                var speedX = rawBaseSpeedBps > 0 ? bytesPerSec / rawBaseSpeedBps : 0;
                var remainingSectors = totalSectors - sectorsRead;
                var eta = bytesPerSec > 0
                    ? TimeSpan.FromSeconds((double)remainingSectors * sectorSize / bytesPerSec)
                    : TimeSpan.Zero;

                progress.Report(new ReadProgress
                {
                    PercentComplete = Math.Min(pct, 99),
                    SectorsRead = sectorsRead,
                    TotalSectors = totalSectors,
                    ErrorSectors = errorSectorCount,
                    CurrentSpeedX = speedX,
                    Elapsed = elapsed,
                    Remaining = eta,
                    StatusMessage = $"Reading raw sectors: {pct}%",
                    // Only log every ~3000 sectors to keep log clean
                    LogLine = sectorsRead % 3000 < count || sectorsRead >= totalSectors
                        ? $"Sector {sectorsRead}/{totalSectors}: {bytesRead / 1_048_576} MB, {speedX:F1}x"
                        : null
                });
            }
        }

        // Final progress report
        progress.Report(new ReadProgress
        {
            PercentComplete = 100,
            SectorsRead = sectorsRead,
            TotalSectors = totalSectors,
            ErrorSectors = errorSectorCount,
            StatusMessage = errorSectorCount > 0
                ? $"Raw sector read complete with {errorSectorCount} error(s)."
                : "Raw sector read complete.",
            LogLine = $"[Info] Successfully read {FormatHelper.FormatBytes(bytesRead)}" +
                (errorSectorCount > 0 ? $" ({errorSectorCount} bad sector(s) zeroed)" : "")
        });
    }

    /// <summary>
    /// Builds a sorted list of track boundary LBAs from the TOC, including
    /// pregap-adjusted boundaries for data↔audio transitions. On Mixed Mode CDs,
    /// the actual sector format change occurs at the pregap start (150 frames before
    /// the audio track's INDEX 01), not at the TOC-reported StartLba. Adding pregap
    /// boundaries prevents READ CD batches from spanning the actual format transition,
    /// which would cause "Illegal mode for this track" (ASC=0x64) errors.
    /// When <paramref name="multiFileStartLbas"/> is provided, those LBAs are also
    /// added as boundaries so that batch reads do not span track-file boundaries.
    /// </summary>
    private static System.Collections.Generic.List<uint> BuildTrackBoundaries(
        TocData toc,
        System.Collections.Generic.IEnumerable<uint>? multiFileStartLbas = null)
    {
        var boundaries = new System.Collections.Generic.List<uint>();
        TocEntry? prevTrack = null;
        foreach (var entry in toc.Entries)
        {
            if (entry.TrackNumber == 0xAA) continue;

            if (entry.StartLba > 0)
                boundaries.Add(entry.StartLba);

            // For data↔audio transitions, add a pregap-adjusted boundary.
            // The pregap before a track of a different type contains sectors in the
            // NEW track's format. Without this boundary, a batch could span from
            // data sectors into audio pregap sectors (or vice versa), causing ASC=0x64.
            if (prevTrack != null && prevTrack.IsData != entry.IsData &&
                entry.StartLba >= StandardPregapFrames)
            {
                var pregapBoundary = entry.StartLba - StandardPregapFrames;
                boundaries.Add(pregapBoundary);
            }

            prevTrack = entry;
        }

        // Add multi-file boundaries so batch reads never cross file boundaries.
        if (multiFileStartLbas != null)
        {
            foreach (var lba in multiFileStartLbas)
            {
                if (lba > 0)
                    boundaries.Add(lba);
            }
        }

        boundaries.Sort();

        // Remove duplicates (adjacent tracks may share boundaries)
        for (int i = boundaries.Count - 1; i > 0; i--)
        {
            if (boundaries[i] == boundaries[i - 1])
                boundaries.RemoveAt(i);
        }

        return boundaries;
    }

    /// <summary>
    /// Builds a list of per-track output file information for multi-file BIN/CUE output.
    /// Each track gets its own .bin file. On Mixed Mode CDs, audio tracks include their
    /// 150-frame (2-second) pregap at the beginning of the file, so the file starts at
    /// (StartLba - 150). This matches the Redump convention where each audio track's
    /// file contains INDEX 00 (pregap) followed by INDEX 01 (content).
    /// </summary>
    /// <param name="toc">TOC data with track entries.</param>
    /// <param name="basePath">Base output path (e.g., "C:\path\game.bin").</param>
    /// <returns>Sorted list of (TrackNumber, FileStartLba, HasPregap, FilePath) tuples.
    /// For single-track discs, returns a single entry with the original path.</returns>
    private static System.Collections.Generic.List<(byte TrackNumber, uint FileStartLba, bool HasPregap, string FilePath)>
        BuildTrackFileInfos(TocData toc, string basePath)
    {
        var result = new System.Collections.Generic.List<(byte TrackNumber, uint FileStartLba, bool HasPregap, string FilePath)>();

        // Count non-lead-out tracks
        int trackCount = 0;
        bool hasData = false, hasAudio = false;
        foreach (var e in toc.Entries)
        {
            if (e.TrackNumber == 0xAA) continue;
            trackCount++;
            if (e.IsData) hasData = true;
            if (e.IsAudio) hasAudio = true;
        }
        bool isMixedMode = hasData && hasAudio;

        if (trackCount <= 1)
        {
            // Single track: use original path, no multi-file
            byte tn = 1;
            foreach (var e in toc.Entries)
            {
                if (e.TrackNumber != 0xAA) { tn = e.TrackNumber; break; }
            }
            result.Add((tn, 0, false, basePath));
            return result;
        }

        // Multi-track: create per-track files
        var dir = Path.GetDirectoryName(basePath) ?? ".";
        var baseName = Path.GetFileNameWithoutExtension(basePath);

        foreach (var entry in toc.Entries)
        {
            if (entry.TrackNumber == 0xAA) continue;

            uint fileStartLba = entry.StartLba;
            bool hasPregap = false;

            // On Mixed Mode CDs, audio tracks include their pregap at the beginning
            // of the file. The pregap is 150 frames (2 seconds) before INDEX 01.
            // Track 1 never has a file-level pregap.
            if (isMixedMode && entry.IsAudio && entry.TrackNumber > 1 &&
                entry.StartLba >= StandardPregapFrames)
            {
                fileStartLba = entry.StartLba - StandardPregapFrames;
                hasPregap = true;
            }

            var trackPath = Path.Combine(dir, $"{baseName} (Track {entry.TrackNumber:D2}).bin");
            result.Add((entry.TrackNumber, fileStartLba, hasPregap, trackPath));
        }

        return result;
    }

    /// <summary>Gets the length of a track in sectors from the TOC.</summary>
    private static uint GetTrackLength(TocData toc, TocEntry entry)
    {
        // Find the next track's start LBA to determine this track's length
        uint nextLba = 0;
        bool foundCurrent = false;
        foreach (var e in toc.Entries)
        {
            if (foundCurrent)
            {
                nextLba = e.StartLba;
                break;
            }
            if (e.TrackNumber == entry.TrackNumber)
                foundCurrent = true;
        }

        if (nextLba > entry.StartLba)
            return nextLba - entry.StartLba;

        // Fallback: if the track is the last in the TOC with no subsequent entry
        // (lead-out missing), use the lead-out entry (0xAA) if available
        if (foundCurrent && nextLba == 0)
        {
            foreach (var e in toc.Entries)
            {
                if (e.TrackNumber == 0xAA && e.StartLba > entry.StartLba)
                    return e.StartLba - entry.StartLba;
            }
        }

        return 0;
    }

    /// <summary>
    /// Detects the sector mode (Mode 1 or Mode 2) for each data track by reading
    /// the raw sector header from the BIN file. The mode byte is at offset 15 within
    /// each 2352-byte raw sector (after the 12-byte sync pattern and 3-byte MSF address).
    /// This is the only reliable way to distinguish Mode 1 from Mode 2 — the TOC Control
    /// field only indicates data vs audio, not the sector mode.
    /// </summary>
    /// <param name="binPath">Path to the BIN file (single-file) or base output path (multi-file).</param>
    /// <param name="toc">TOC data with track entries.</param>
    /// <param name="multiFile">When true, reads from per-track BIN files instead of a single file.</param>
    /// <returns>Dictionary mapping track number to mode byte (1 or 2). Only data tracks
    /// with detectable modes are included.</returns>
    private static System.Collections.Generic.Dictionary<byte, byte> DetectTrackModesFromBin(
        string binPath, TocData toc, bool multiFile = false)
    {
        var modes = new System.Collections.Generic.Dictionary<byte, byte>();
        var header = new byte[16]; // Need 16 bytes: 12 sync + 3 MSF + 1 mode

        if (multiFile)
        {
            // Multi-file: each data track has its own .bin file with sector data starting at offset 0.
            var dir = Path.GetDirectoryName(binPath) ?? ".";
            var baseName = Path.GetFileNameWithoutExtension(binPath);

            foreach (var entry in toc.Entries)
            {
                if (entry.TrackNumber == 0xAA || entry.IsAudio) continue;

                var trackPath = Path.Combine(dir, $"{baseName} (Track {entry.TrackNumber:D2}).bin");
                if (!File.Exists(trackPath)) continue;

                try
                {
                    using var stream = new FileStream(trackPath, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite, 4096, false);
                    if (stream.Length < 16) continue;

                    int bytesRead = stream.Read(header, 0, 16);
                    if (bytesRead < 16) continue;

                    if (header.AsSpan(0, 12).SequenceEqual(CdSyncPattern))
                    {
                        byte mode = header[15];
                        if (mode == 1 || mode == 2)
                            modes[entry.TrackNumber] = mode;
                    }
                }
                catch { /* best effort */ }
            }
        }
        else
        {
            // Single-file: all tracks in one BIN file, sector at LBA * 2352.
            if (!File.Exists(binPath)) return modes;

            try
            {
                using var stream = new FileStream(binPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite, 4096, false);

                foreach (var entry in toc.Entries)
                {
                    if (entry.TrackNumber == 0xAA || entry.IsAudio) continue;

                    long offset = (long)entry.StartLba * RawCdSectorSize;
                    if (offset + 16 > stream.Length) continue;

                    stream.Position = offset;
                    int bytesRead = stream.Read(header, 0, 16);
                    if (bytesRead < 16) continue;

                    if (header.AsSpan(0, 12).SequenceEqual(CdSyncPattern))
                    {
                        byte mode = header[15];
                        if (mode == 1 || mode == 2)
                            modes[entry.TrackNumber] = mode;
                    }
                }
            }
            catch { /* best effort */ }
        }

        return modes;
    }

    /// <summary>Generates a CUE sheet from TOC data.</summary>
    /// <param name="toc">Table of Contents data.</param>
    /// <param name="binPath">Path to the BIN file (single-file) or base output path (multi-file).</param>
    /// <param name="cuePath">Path for the generated CUE file.</param>
    /// <param name="multiFile">When true, generates a multi-file CUE where each track references
    /// its own BIN file (Redump convention). When false, generates a single-file CUE.</param>
    private static void GenerateCueFromToc(TocData toc, string binPath, string cuePath, bool multiFile = false)
    {
        // Detect if this is a Mixed Mode disc (has both data and audio tracks).
        bool hasData = false, hasAudio = false;
        int trackCount = 0;
        foreach (var e in toc.Entries)
        {
            if (e.TrackNumber == 0xAA) continue;
            trackCount++;
            if (e.IsData) hasData = true;
            if (e.IsAudio) hasAudio = true;
        }
        bool isMixedMode = hasData && hasAudio;

        // Detect sector modes (Mode 1 vs Mode 2) from the BIN file's raw sector headers.
        // The TOC Control field cannot distinguish Mode 1 from Mode 2 — it only indicates
        // data vs audio. The mode byte is at offset 15 within each 2352-byte raw sector.
        var trackModes = DetectTrackModesFromBin(binPath, toc, multiFile);

        var baseName = Path.GetFileNameWithoutExtension(binPath);
        var sb = new StringBuilder();
        sb.AppendLine("REM Generated by OpenBurningSuite");

        if (multiFile)
        {
            // Multi-file CUE: each track gets its own FILE directive and .bin file.
            // Per Redump convention, each track file starts at its data content.
            // On Mixed Mode CDs, audio track files include their 150-frame pregap
            // at the beginning (INDEX 00 = 00:00:00, INDEX 01 = 00:02:00).
            for (int i = 0; i < toc.Entries.Count; i++)
            {
                var entry = toc.Entries[i];
                if (entry.TrackNumber == 0xAA) continue;

                var trackFileName = $"{baseName} (Track {entry.TrackNumber:D2}).bin";
                sb.AppendLine($"FILE \"{trackFileName}\" BINARY");

                string trackType = GetCueTrackType(entry, trackModes);
                sb.AppendLine($"  TRACK {entry.TrackNumber:D2} {trackType}");

                // On Mixed Mode CDs, audio tracks include their 150-frame pregap at
                // the beginning of their file. INDEX 00 is at the start (pregap) and
                // INDEX 01 is at 00:02:00 (150 frames = 2 seconds into the file).
                bool hasPregap = isMixedMode && entry.IsAudio && entry.TrackNumber > 1 &&
                                 entry.StartLba >= StandardPregapFrames;
                if (hasPregap)
                {
                    sb.AppendLine("    INDEX 00 00:00:00");
                    sb.AppendLine("    INDEX 01 00:02:00");
                }
                else
                {
                    sb.AppendLine("    INDEX 01 00:00:00");
                }
            }
        }
        else
        {
            // Single-file CUE: all tracks reference one BIN file with absolute MSF offsets.
            var binFileName = Path.GetFileName(binPath);
            sb.AppendLine($"FILE \"{binFileName}\" BINARY");

            for (int i = 0; i < toc.Entries.Count; i++)
            {
                var entry = toc.Entries[i];
                if (entry.TrackNumber == 0xAA) continue;

                string trackType = GetCueTrackType(entry, trackModes);
                sb.AppendLine($"  TRACK {entry.TrackNumber:D2} {trackType}");

                // Pregap handling for tracks after track 1.
                if (entry.TrackNumber > 1 && i > 0)
                {
                    TocEntry? prevTrack = null;
                    for (int j = i - 1; j >= 0; j--)
                    {
                        if (toc.Entries[j].TrackNumber != 0xAA) { prevTrack = toc.Entries[j]; break; }
                    }

                    if (prevTrack != null)
                    {
                        bool isTransition = prevTrack.IsData != entry.IsData;
                        bool needsPregap = isTransition || (isMixedMode && entry.IsAudio);

                        if (needsPregap && entry.StartLba >= StandardPregapFrames)
                        {
                            var gapLba = entry.StartLba - StandardPregapFrames;
                            sb.AppendLine($"    INDEX 00 {LbaToMsf(gapLba)}");
                        }
                        else if (!isTransition && entry.IsAudio && !isMixedMode)
                        {
                            var prevEnd = GetTrackEndLba(toc, prevTrack);
                            if (prevEnd > 0 && entry.StartLba > prevEnd)
                                sb.AppendLine($"    INDEX 00 {LbaToMsf(prevEnd)}");
                        }
                    }
                }
                else if (entry.TrackNumber == 1 && entry.StartLba >= StandardPregapFrames)
                {
                    sb.AppendLine("    INDEX 00 00:00:00");
                }

                sb.AppendLine($"    INDEX 01 {LbaToMsf(entry.StartLba)}");
            }
        }

        File.WriteAllText(cuePath, sb.ToString());
    }

    /// <summary>Converts an LBA to CUE sheet MSF format (MM:SS:FF).</summary>
    private static string LbaToMsf(uint lba)
    {
        var frames = lba % 75;
        var seconds = (lba / 75) % 60;
        var minutes = Math.Min(lba / 75 / 60, 99u);
        return $"{minutes:D2}:{seconds:D2}:{frames:D2}";
    }

    /// <summary>Determines the CUE track type string for a TOC entry.</summary>
    private static string GetCueTrackType(
        TocEntry entry,
        System.Collections.Generic.Dictionary<byte, byte> trackModes)
    {
        if (entry.IsAudio)
            return "AUDIO";
        if (trackModes.TryGetValue(entry.TrackNumber, out var mode))
            return mode == 2 ? "MODE2/2352" : "MODE1/2352";
        return "MODE1/2352";
    }

    /// <summary>Gets the end LBA of a track (start of next track or 0 if unknown).</summary>
    private static uint GetTrackEndLba(TocData toc, TocEntry entry)
    {
        bool foundCurrent = false;
        foreach (var e in toc.Entries)
        {
            if (foundCurrent)
                return e.StartLba;
            if (e.TrackNumber == entry.TrackNumber)
                foundCurrent = true;
        }
        return 0;
    }

    /// <summary>
    /// Determines the MMC Expected Sector Type for READ CD based on the track type
    /// at the given LBA. Returns 1 (CD-DA) for audio tracks, 0 (any type) for data tracks.
    ///
    /// Many optical drives return "Illegal mode for this track" (ASC=0x64, ASCQ=0x00)
    /// when expectedSectorType=0 (any) is used with main channel selection bits 0xF8
    /// (Sync+Header+SubHeader+UserData+EDC/ECC) on audio sectors. Audio sectors have no
    /// internal structure (Sync, Header, etc.) — they are pure 2352-byte PCM data.
    /// By explicitly requesting CD-DA (type 1) for audio tracks, the drive knows to
    /// return raw audio data without attempting to parse non-existent data fields.
    ///
    /// For data tracks, type 0 (any) is used because the exact mode (Mode 1 vs Mode 2)
    /// cannot be determined from the TOC alone, and drives handle expectedSectorType=0
    /// correctly for all data sector types.
    ///
    /// Pregap awareness: on Mixed Mode CDs, the 150-frame pregap before an audio track
    /// that follows a data track contains audio-format sectors. The TOC only lists the
    /// INDEX 01 position (actual content start), but the sector format changes at the
    /// pregap start (~150 frames earlier). Without this, reads in the pregap zone would
    /// use expectedSectorType=0 (data) and fail with ASC=0x64.
    /// </summary>
    private static byte GetExpectedSectorType(TocData? toc, uint lba)
    {
        if (toc == null) return 0;

        // Find the track that contains this LBA: the track with the highest
        // StartLba that is <= lba. TOC entries are typically ordered by track
        // number (ascending LBA), but we search exhaustively for robustness.
        TocEntry? matchedTrack = null;
        foreach (var entry in toc.Entries)
        {
            if (entry.TrackNumber == 0xAA) continue;
            if (entry.StartLba <= lba)
            {
                if (matchedTrack == null || entry.StartLba > matchedTrack.StartLba)
                    matchedTrack = entry;
            }
        }

        // CD-DA (audio): expectedSectorType=1
        if (matchedTrack != null && matchedTrack.IsAudio)
            return 1;

        // Check if the LBA is in the pregap of an upcoming audio track.
        // On Mixed Mode CDs (e.g., PS1 discs), the pregap between a data track and
        // an audio track contains audio-format sectors (silence). The TOC StartLba
        // for the audio track points to INDEX 01, but the sector format changes at
        // the pregap start (150 frames before INDEX 01, per Red Book / Yellow Book).
        // Without this check, sectors in the pregap zone would be read with
        // expectedSectorType=0 (data) and fail with ASC=0x64 on many drives.
        if (matchedTrack != null && matchedTrack.IsData)
        {
            foreach (var entry in toc.Entries)
            {
                if (entry.TrackNumber == 0xAA || !entry.IsAudio) continue;
                // Check if this audio track follows the matched data track
                // and the LBA is within its 150-frame pregap zone
                if (entry.StartLba > matchedTrack.StartLba &&
                    entry.StartLba > StandardPregapFrames &&
                    lba >= entry.StartLba - StandardPregapFrames &&
                    lba < entry.StartLba)
                {
                    return 1; // Pregap of audio track — use CD-DA type
                }
            }
        }

        // Data track or unknown: expectedSectorType=0 (any type)
        return 0;
    }

    /// <summary>
    /// Checks whether a SCSI error indicates "Illegal mode for this track"
    /// (SenseKey=0x05, ASC=0x64, ASCQ=0x00). This error occurs when the
    /// READ CD Expected Sector Type doesn't match the actual sector type on disc,
    /// which is common on Mixed Mode CDs when expectedSectorType=0 (any) is used
    /// for audio sectors.
    /// </summary>
    private static bool IsIllegalModeForTrackError(ScsiResult result)
    {
        // SCSI Check Condition: Status=0x02
        // Sense Key 0x05 = Illegal Request
        // ASC=0x64, ASCQ=0x00 = Illegal mode for this track
        return result.Status == 0x02 &&
               result.SenseKey == 0x05 &&
               result.Asc == 0x64 &&
               result.Ascq == 0x00;
    }

    /// <summary>Generates a cdrdao-compatible TOC file from TOC data.</summary>
    private static void GenerateTocFile(TocData toc, string binPath, string tocPath)
    {
        // Detect sector modes (Mode 1 vs Mode 2) from the BIN file's raw sector headers,
        // same as GenerateCueFromToc. This ensures the TOC file's TRACK directives match
        // the actual sector format on disc.
        var trackModes = DetectTrackModesFromBin(binPath, toc);

        var sb = new StringBuilder();
        bool hasAudio = false;
        bool hasData = false;
        bool hasMode2 = false;
        foreach (var e in toc.Entries)
        {
            if (e.TrackNumber == 0xAA) continue;
            if (e.IsAudio) hasAudio = true;
            if (e.IsData)
            {
                hasData = true;
                if (trackModes.TryGetValue(e.TrackNumber, out var m) && m == 2)
                    hasMode2 = true;
            }
        }

        // cdrdao disc type: CD_DA for audio-only, CD_ROM_XA for Mode 2 data,
        // CD_ROM for Mode 1 data or mixed mode.
        // Mixed Mode CDs (data + audio) must use CD_ROM or CD_ROM_XA, not CD_DA.
        if (hasMode2)
            sb.AppendLine("CD_ROM_XA");
        else if (hasData)
            sb.AppendLine("CD_ROM");
        else if (hasAudio)
            sb.AppendLine("CD_DA");
        else
            sb.AppendLine("CD_ROM");
        sb.AppendLine();

        foreach (var entry in toc.Entries)
        {
            if (entry.TrackNumber == 0xAA) continue;

            if (entry.IsData)
            {
                // Use detected mode from raw sector header for accurate track type
                if (trackModes.TryGetValue(entry.TrackNumber, out var mode) && mode == 2)
                    sb.AppendLine("TRACK MODE2_RAW");
                else
                    sb.AppendLine("TRACK MODE1_RAW");
            }
            else
                sb.AppendLine("TRACK AUDIO");

            // Calculate byte offset and length for this track's data in the BIN file
            var trackEndLba = GetTrackEndLba(toc, entry);
            if (trackEndLba > entry.StartLba)
            {
                var trackSectors = trackEndLba - entry.StartLba;
                var byteOffset = (long)entry.StartLba * 2352;
                var byteLength = (long)trackSectors * 2352;
                sb.AppendLine($"DATAFILE \"{Path.GetFileName(binPath)}\" #{byteOffset} {byteLength}");
            }
            else
            {
                // Fallback: no length information, read to end of file
                sb.AppendLine($"DATAFILE \"{Path.GetFileName(binPath)}\"");
            }
            sb.AppendLine();
        }

        File.WriteAllText(tocPath, sb.ToString());
    }

    /// <summary>
    /// Writes a WAV file header for 16-bit 44100Hz stereo audio.
    /// Uses standard RIFF format for data up to ~4 GB. For larger files,
    /// uses RF64 format (EBU Tech 3306) which supports 64-bit sizes.
    /// </summary>
    private static void WriteWavHeader(Stream stream, long dataSize)
    {
        const int sampleRate = 44100;
        const short channels = 2;
        const short bitsPerSample = 16;
        var byteRate = sampleRate * channels * bitsPerSample / 8;
        var blockAlign = (short)(channels * bitsPerSample / 8);

        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        // Use RF64 format for data exceeding standard WAV 32-bit limit (~4 GB).
        // Standard WAV: file = RIFF(12) + fmt(24) + data(8+dataSize) = 44 + dataSize
        //   riffSize = totalFileSize - 8 = dataSize + 36
        // RF64 adds a ds64 chunk (36 bytes): file = RF64(12) + ds64(36) + fmt(24) + data(8+dataSize) = 80 + dataSize
        //   riffSize = totalFileSize - 8 = dataSize + 72
        var standardRiffSize = dataSize + 36;
        bool useRf64 = standardRiffSize > int.MaxValue || dataSize > int.MaxValue;

        if (useRf64)
        {
            // RF64 riffSize accounts for the additional ds64 chunk (36 bytes)
            var rf64RiffSize = dataSize + 72;

            // RF64 header per EBU Tech 3306
            writer.Write(new[] { (byte)'R', (byte)'F', (byte)'6', (byte)'4' });
            writer.Write(-1);  // 0xFFFFFFFF indicates RF64
            writer.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E' });
            // ds64 chunk with 64-bit sizes
            writer.Write(new[] { (byte)'d', (byte)'s', (byte)'6', (byte)'4' });
            writer.Write(28);  // ds64 chunk size (28 bytes of payload data)
            writer.Write(rf64RiffSize);     // 64-bit RIFF size (total file - 8)
            writer.Write(dataSize);         // 64-bit data size
            writer.Write(0L);              // 64-bit sample count (optional, 0 = not specified)
            writer.Write(0);               // table length (no table entries)
        }
        else
        {
            writer.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
            writer.Write((int)standardRiffSize);    // File size - 8
            writer.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E' });
        }

        writer.Write(new[] { (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
        writer.Write(16);                   // Subchunk size
        writer.Write((short)1);             // PCM format
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);
        writer.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
        writer.Write(useRf64 ? -1 : (int)dataSize);  // RF64: 0xFFFFFFFF, else actual size
    }

    /// <summary>
    /// Returns the 1x base read speed in bytes/second for the given media profile.
    /// 1x CD = 150 KiB/s, 1x DVD = 1,385 KiB/s, 1x BD = 4,495 KiB/s, 1x HD DVD = 4,568 KiB/s.
    /// </summary>
    private static double GetBaseSpeedForProfile(ushort profile)
    {
        return profile switch
        {
            >= 0x0040 and <= 0x004F => FormatHelper.BdBaseSpeedKBs * 1024, // Blu-ray
            >= 0x0050 and <= 0x005F => FormatHelper.HdDvdBaseSpeedKBs * 1024, // HD DVD
            // DVD profiles span 0x0010-0x002F per MMC-5. Profiles 0x0030-0x003F are
            // reserved but should still use DVD base speed (not CD) since anything in
            // the 0x0010-0x003F range is DVD-family. This prevents misidentifying a
            // future profile as CD speed.
            >= 0x0010 and <= 0x003F => FormatHelper.DvdBaseSpeedKBs * 1024, // DVD
            _ => FormatHelper.CdBaseSpeedKBs * 1024                           // CD (default)
        };
    }

    /// <summary>
    /// Sets the drive read speed. Uses the dedicated SetReadSpeed on OpticalDrive
    /// which properly sets READ speed (not write speed).
    /// Returns true if speed was set (or was already Max/Auto), false if the drive
    /// rejected the speed change.
    /// </summary>
    private static bool SetReadSpeed(OpticalDrive drive, string speedStr, ushort profile)
    {
        if (string.IsNullOrWhiteSpace(speedStr)) return true;

        if (string.Equals(speedStr, FormatHelper.SpeedMax, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(speedStr, FormatHelper.SpeedAuto, StringComparison.OrdinalIgnoreCase))
            return true; // Default max speed

        // Trim trailing 'x'/'X' for robustness (write speeds use "4x" format)
        var numStr = speedStr.TrimEnd('x', 'X');
        if (!double.TryParse(numStr, System.Globalization.CultureInfo.InvariantCulture, out var mult))
            return true; // Unparseable, skip silently

        // Reject negative or zero multipliers
        if (mult <= 0) return true;

        var baseSpeed = profile switch
        {
            >= 0x0040 and <= 0x004F => FormatHelper.BdBaseSpeedKBs,
            >= 0x0050 and <= 0x005F => FormatHelper.HdDvdBaseSpeedKBs,
            // DVD profiles span 0x0010-0x002F per MMC-5. Extend to 0x003F to cover
            // reserved profiles in the DVD-family range (consistent with isDvdOrBd check).
            >= 0x0010 and <= 0x003F => FormatHelper.DvdBaseSpeedKBs,
            _ => FormatHelper.CdBaseSpeedKBs
        };
        var speedValue = mult * baseSpeed;
        if (speedValue > ushort.MaxValue) return true; // Use max speed if overflow
        var speedKBs = (ushort)speedValue;
        var isDvdOrBd = profile >= 0x0010;
        return drive.SetReadSpeed(speedKBs, isDvdOrBd);
    }

    /// <summary>
    /// Computes MD5 and SHA-1 hashes of a file and reports them via progress.
    /// Used for dump verification against known-good databases (e.g., Redump.org).
    /// </summary>
    private static async Task ComputeAndReportHashesAsync(
        string filePath,
        IProgress<ReadProgress> progress,
        CancellationToken ct)
    {
        const int hashBufferSize = 1_048_576; // 1 MB buffer for hash I/O

        try
        {
            progress.Report(new ReadProgress
            {
                LogLine = "[Info] Computing MD5 and SHA-1 hashes for dump verification..."
            });

            string md5Hash;
            string sha1Hash;

            // Compute MD5
            using (var md5 = MD5.Create())
            await using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, hashBufferSize, true))
            {
                var hash = await md5.ComputeHashAsync(stream, ct);
                md5Hash = Convert.ToHexString(hash).ToLowerInvariant();
            }

            // Compute SHA-1
            using (var sha1 = SHA1.Create())
            await using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, hashBufferSize, true))
            {
                var hash = await sha1.ComputeHashAsync(stream, ct);
                sha1Hash = Convert.ToHexString(hash).ToLowerInvariant();
            }

            progress.Report(new ReadProgress
            {
                LogLine = $"[Info] MD5:  {md5Hash}"
            });
            progress.Report(new ReadProgress
            {
                LogLine = $"[Info] SHA1: {sha1Hash}"
            });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            progress.Report(new ReadProgress
            {
                LogLine = $"[Warning] Hash computation failed: {ex.Message}"
            });
        }
    }

    /// <summary>
    /// Checks the number of C2 error bits set in a C2 error block data buffer.
    /// Each bit in the 294-byte buffer corresponds to one byte in the 2352-byte sector.
    /// Returns the number of bytes with uncorrectable C2 errors.
    /// </summary>
    private static int CountC2Errors(byte[] c2Data, int offset, int length)
    {
        int errorCount = 0;
        int end = Math.Min(offset + length, c2Data.Length);
        for (int i = offset; i < end; i++)
        {
            if (c2Data[i] != 0)
            {
                // Count bits set using Brian Kernighan's algorithm
                byte b = c2Data[i];
                while (b != 0)
                {
                    b &= (byte)(b - 1);
                    errorCount++;
                }
            }
        }
        return errorCount;
    }
}
