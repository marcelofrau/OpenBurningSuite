// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OpenBurningSuite.Helpers;
using OpenBurningSuite.Models;
using OpenBurningSuite.Native.Scsi;

namespace OpenBurningSuite.Native.Optical;

/// <summary>
/// Native disc burning engine using SCSI/MMC commands to write data to optical media.
/// </summary>
public sealed class BurnEngine
{
    /// <summary>Number of sectors to write per WRITE command (WRITE(10) for CD, WRITE(12) for DVD/BD).</summary>
    private const int SectorsPerWrite = 16; // 16 × 2048 = 32 KB per write

    /// <summary>
    /// Standard SAO/DAO pregap size in sectors (2 seconds × 75 frames/sec).
    /// Per Red Book / MMC, the first data sector of a CD session starts at
    /// MSF 00:02:00 (LBA 0), so the 150 frames before that are the pregap.
    /// </summary>
    private const int SaoPregapSectors = 150;

    /// <summary>Maximum number of retries for transient SCSI write errors.</summary>
    private static int WriteRetryMax => Services.SettingsService.Current.ScsiWriteRetries;

    /// <summary>Base delay in milliseconds between write retries (multiplied by attempt number).</summary>
    private const int WriteRetryBaseDelayMs = 500;

    /// <summary>Maximum delay in milliseconds between write retries.</summary>
    private const int WriteRetryMaxDelayMs = 5000;

    // Static compiled regex patterns for CUE sheet parsing (matches TocCueConverter convention)
    private static readonly System.Text.RegularExpressions.Regex CueFileRx =
        new(@"FILE\s+""([^""]+)""", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex CueTrackRx =
        new(@"TRACK\s+(\d+)\s+(\S+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex CueIndexRx =
        new(@"INDEX\s+(\d+)\s+(\d+):(\d+):(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Burns an image file to an optical disc using native SCSI commands.
    /// </summary>
    public async Task BurnImageAsync(
        BurnJob job,
        IProgress<BurnProgress> progress,
        CancellationToken ct)
    {
        // Validate inputs
        if (string.IsNullOrWhiteSpace(job.ImagePath))
            throw new ArgumentException("Image path must not be empty.");
        if (!File.Exists(job.ImagePath))
            throw new FileNotFoundException($"Image file not found: {job.ImagePath}");
        if (string.IsNullOrWhiteSpace(job.DevicePath))
            throw new ArgumentException("Device path must not be empty.");

        // Report progress immediately so the UI activates before any blocking operations.
        // Without this, the drive open + WaitForDriveReady() can delay the first progress
        // report by several seconds, making the UI appear unresponsive while the drive spins up.
        progress.Report(new BurnProgress
        {
            PercentComplete = 0,
            StatusMessage = "Initializing drive...",
            LogLine = $"[Info] Opening device: {job.DevicePath}"
        });

        // Native SCSI passthrough is the primary path on all platforms including macOS.
        // On macOS, the IOKit SCSITaskLib interface is used with kIOMMCDeviceUserClientTypeID
        // for optical drives. The proper sequence (per Apple's SCSITaskLib.h and cdrtools) is:
        //   1. DA unmount + claim → 2. IOCreatePlugInInterfaceForService →
        //   3. QueryInterface for MMCDeviceInterface → 4. GetSCSITaskDeviceInterface →
        //   5. ObtainExclusiveAccess → 6. SCSI commands
        // No automatic fallback to drutil/hdiutil — SCSI must work natively.

        using var drive = new OpticalDrive(job.DevicePath);

        // Wait for drive readiness — drives need time to spin up after tray close/load
        progress.Report(new BurnProgress { StatusMessage = "Waiting for drive to become ready..." });
        if (!drive.WaitForDriveReady())
            throw new InvalidOperationException("Drive is not ready. Please insert a disc and close the tray.");

        // Detect media and determine sector size
        var profile = drive.GetCurrentProfile();
        var mediaType = OpticalDrive.ProfileToMediaType(profile);
        var isDvdOrBd = profile >= 0x0010; // DVD and above
        var isBd = profile >= 0x0040 && profile <= 0x004F; // Blu-ray range
        var isHdDvd = profile >= 0x0050 && profile <= 0x005F; // HD DVD range
        var isDvdPlusRw = profile is 0x001A or 0x002A; // DVD+RW / DVD+RW DL
        var isDvdRam = profile is 0x0012 or 0x0052; // DVD-RAM / HD DVD-RAM
        // BD-RE includes all rewritable Blu-ray profiles: BD-RE, BD-RE DL, BDXL BD-RE TL/QL
        var isBdRe = profile is 0x0043 or 0x0046 or 0x004B or 0x004D;
        // BD-R includes all write-once Blu-ray profiles: BD-R SRM, BD-R RRM, BD-R DL, BDXL BD-R TL/QL
        var isBdR = profile is 0x0041 or 0x0042 or 0x0045 or 0x004A or 0x004C;
        var isHdDvdRw = profile == 0x0053; // HD DVD-RW (overwritable)
        // DVD-R/-RW Sequential and HD DVD-R profiles that require Mode Page 05h
        // (Write Parameters) to be set before WRITE commands. These use sequential
        // recording and the drive needs the write type configured via Mode Page 05h,
        // unlike DVD+R/+RW, DVD-RAM, BD-R/RE which handle write mode internally.
        // Per MMC-5 §7.5.4 and cdrecord/cdrtools, the write type must be set to
        // Incremental (0x00) for standard ISO burning on these media types.
        var isDvdMinusRSequential = profile is 0x0011  // DVD-R Sequential Recording
            or 0x0014  // DVD-RW Sequential Recording
            or 0x0015  // DVD-R DL Sequential Recording
            or 0x0016; // DVD-R DL Layer Jump Recording
        var isHdDvdR = profile is 0x0051  // HD DVD-R
            or 0x0058; // HD DVD-R DL
        // Aggregate flag for all overwritable media that don't support CLOSE TRACK/SESSION
        var isOverwritable = isDvdPlusRw || isDvdRam || isBdRe || isHdDvdRw;
        int sectorSize = 2048;

        // M-DISC detection: check if the inserted media is M-DISC (Millennial Disc).
        // M-DISC uses standard DVD+R / BD-R profiles but with an inorganic recording layer.
        // The media is identified via the manufacturer ID in the ADIP data.
        var isMDisc = drive.IsMDiscMedia();
        if (isMDisc)
        {
            var mDiscType = drive.GetMDiscMediaType();
            if (mDiscType != null)
                mediaType = mDiscType;
        }

        // Determine sector size based on write mode (raw modes use 2352+ bytes)
        if (!isDvdOrBd)
        {
            var (_, _, selectedBlockType) = ParseWriteMode(job.WriteMode);
            sectorSize = selectedBlockType switch
            {
                MmcCommands.BlockTypeRaw2352 => 2352,
                MmcCommands.BlockTypeRawPQ2368 => 2368,
                MmcCommands.BlockTypeRawPW2448 => 2448,
                MmcCommands.BlockTypeRawPW2448_R => 2448,
                MmcCommands.BlockTypeMode2_2336 => 2336,
                MmcCommands.BlockTypeMode2XA_2328 => 2328,
                MmcCommands.BlockTypeMode2XA_2332 => 2332,
                _ => 2048
            };
        }

        progress.Report(new BurnProgress
        {
            StatusMessage = $"Detected media: {mediaType}",
            LogLine = $"[Info] Media profile: 0x{profile:X4} ({mediaType})"
        });

        // Log M-DISC-specific information
        if (isMDisc)
        {
            progress.Report(new BurnProgress
            {
                LogLine = "[Info] M-DISC (Millennial Disc) archival media detected — " +
                          "inorganic recording layer for long-term preservation (1,000+ years)"
            });

            // Enforce M-DISC write speed limits to ensure reliable engraving
            // M-DISC DVD: max 4x, M-DISC BD-R: max 6x
            var maxMDiscSpeedKBs = isBd
                ? (ushort)(6 * FormatHelper.BdBaseSpeedKBs)
                : (ushort)(4 * FormatHelper.DvdBaseSpeedKBs);
            var requestedSpeed = ParseWriteSpeed(job.WriteSpeed, isDvdOrBd, isBd, isHdDvd);
            if (requestedSpeed > maxMDiscSpeedKBs && requestedSpeed != 0xFFFF)
            {
                progress.Report(new BurnProgress
                {
                    LogLine = $"[Warning] Requested write speed exceeds M-DISC maximum — " +
                              $"clamping to {(isBd ? "6x BD" : "4x DVD")} for reliable engraving"
                });
            }

            // Disable overburn for M-DISC — exceeding capacity risks damaging the archival layer
            if (job.Overburn)
            {
                job.Overburn = false;
                progress.Report(new BurnProgress
                {
                    LogLine = "[Warning] Overburn disabled for M-DISC — not supported on archival media"
                });
            }

            // Recommend verify after burn for archival media
            if (!job.VerifyAfterBurn)
            {
                progress.Report(new BurnProgress
                {
                    LogLine = "[Recommendation] Enable 'Verify After Burn' for M-DISC archival media " +
                              "to ensure data integrity of the permanent recording"
                });
            }
        }

        // Check disc status
        var discInfo = drive.ReadDiscInfo();
        if (discInfo == null)
            throw new InvalidOperationException("Could not read disc information.");

        if (discInfo.DiscStatus == 2 && !discInfo.Erasable)
            throw new InvalidOperationException("Disc is finalized and not rewritable.");

        // For DVD+RW/BD-RE/HD DVD-RW: overwritable media doesn't need blank check
        if (isOverwritable)
        {
            progress.Report(new BurnProgress
            {
                LogLine = $"[Info] Overwritable media detected — direct overwrite supported"
            });

            // Overwritable media (DVD+RW, DVD-RAM, BD-RE, HD DVD-RW) must be formatted
            // with FORMAT UNIT before the first write. Brand-new/factory-fresh discs
            // report DiscStatus=0 (Blank) and will reject WRITE with ASC=0x31
            // "Medium format corrupted" unless formatted first.
            // Already-formatted discs report DiscStatus=2 (Complete) with Erasable=true
            // and can be written directly (overwrite in place).
            if (discInfo.DiscStatus == 0)
            {
                progress.Report(new BurnProgress
                {
                    StatusMessage = "Formatting disc for first use...",
                    LogLine = "[Info] Unformatted overwritable media detected — formatting with FORMAT UNIT before writing"
                });

                ScsiResult formatResult;
                if (isDvdPlusRw)
                    formatResult = drive.FormatDvdPlusRw(quickFormat: true);
                else if (isDvdRam)
                    formatResult = drive.FormatDvdRam(quickFormat: true);
                else if (isBdRe)
                    formatResult = drive.FormatBdRe(quickFormat: true);
                else if (isHdDvdRw)
                    formatResult = drive.FormatHdDvdRw(quickFormat: true);
                else
                    formatResult = new ScsiResult { Status = 0xFF }; // unknown type

                if (!formatResult.Success)
                {
                    progress.Report(new BurnProgress
                    {
                        LogLine = $"[Warning] Quick format failed ({formatResult.ErrorDescription}), trying full format..."
                    });

                    // Retry with full format — some drives/media reject quick format
                    if (isDvdPlusRw)
                        formatResult = drive.FormatDvdPlusRw(quickFormat: false);
                    else if (isDvdRam)
                        formatResult = drive.FormatDvdRam(quickFormat: false);
                    else if (isBdRe)
                        formatResult = drive.FormatBdRe(quickFormat: false);
                    else if (isHdDvdRw)
                        formatResult = drive.FormatHdDvdRw(quickFormat: false);
                }

                if (formatResult.Success)
                {
                    progress.Report(new BurnProgress
                    {
                        LogLine = "[Info] Disc formatted successfully — ready for writing"
                    });

                    // Wait for drive to become ready after formatting.
                    // FORMAT UNIT with IMM=1 returns immediately; the drive continues
                    // formatting in the background. The drive reports NOT READY
                    // (ASC=0x04, ASCQ=0x04 "format in progress") until complete.
                    drive.WaitForDriveReady(maxWaitMs: 600_000, pollIntervalMs: 2000);
                }
                else
                {
                    throw new InvalidOperationException(
                        $"Cannot format disc for writing: {formatResult.ErrorDescription}. " +
                        "The disc may need to be formatted manually before burning.");
                }
            }
        }

        // Lock the tray during burning to prevent accidental ejection.
        // If the lock fails, warn but continue — some drives don't support this command,
        // and it's not strictly required for the burn to succeed.
        if (!drive.PreventMediumRemoval(true))
        {
            progress.Report(new BurnProgress
            {
                LogLine = "[Warning] Could not lock drive tray — ensure disc is not ejected during burn"
            });
        }

        // On Windows, lock and dismount the volume before writing.
        // This prevents the storage filter driver from blocking SCSI WRITE commands
        // on mounted overwritable media (DVD+RW, BD-RE, etc.).
        drive.PrepareForDestructiveOperation();

        // Track whether the drive transport was already released (e.g. by
        // the platform-fallback eject path) so the finally block can skip
        // redundant cleanup on a disposed drive.
        bool driveDisposed = false;
        // Track whether the burn completed successfully for conditional recovery.
        // On success, only basic cleanup is needed (quick TUR + unlock).
        // On error, full drive recovery is performed (TUR draining, REQUEST SENSE,
        // MECHANISM STATUS, WaitForDriveReady) to ensure the drive is usable.
        bool burnSucceeded = false;

        try
        {

        // Set write speed — apply M-DISC speed clamping if needed
        var writeSpeedKBs = ParseWriteSpeed(job.WriteSpeed, isDvdOrBd, isBd, isHdDvd);
        if (isMDisc)
        {
            // Clamp to M-DISC maximum: 4x DVD (5540 KB/s) or 6x BD (26970 KB/s)
            var maxSpeed = isBd
                ? (ushort)(6 * FormatHelper.BdBaseSpeedKBs)
                : (ushort)(4 * FormatHelper.DvdBaseSpeedKBs);
            if (writeSpeedKBs == 0xFFFF || writeSpeedKBs > maxSpeed)
                writeSpeedKBs = maxSpeed;
        }
        if (writeSpeedKBs > 0)
        {
            drive.SetWriteSpeed(writeSpeedKBs, isDvdOrBd);
            progress.Report(new BurnProgress
            {
                LogLine = $"[Info] Write speed set to {writeSpeedKBs} KB/s"
            });
        }

        // Track whether SAO/DAO mode is active — needed for pregap handling and
        // finalization behavior below.
        // In SAO/DAO mode the host must provide ALL session data (including the
        // 2-second / 150-frame pregap before Track 1) per MMC-5 §4.9.3.
        //
        // SAO (Session At Once) vs DAO (Disc At Once) per MMC-5 §4.9.3:
        //   Both use SCSI Write Type 0x02 and CUE sheet, but differ in finalization:
        //   - SAO: writes one session; multi-session IS possible (close session only)
        //   - DAO: writes entire disc; NO multi-session; disc always finalized
        //   cdrecord/cdrtools, InfraRecorder, and ImgBurn all treat these as distinct modes.
        bool saoModeActive = false;
        bool daoModeRequested = (job.WriteMode ?? string.Empty).Contains("DAO", StringComparison.OrdinalIgnoreCase)
                             && !(job.WriteMode ?? string.Empty).Contains("SAO", StringComparison.OrdinalIgnoreCase);
        // Tracks whether DVD-R/-RW Sequential or HD DVD-R DAO mode was actually
        // configured successfully in Mode Page 05h (not just requested).
        // When true, RESERVE TRACK must be sent before writing per MMC-5 §4.9.3.
        bool dvdDaoModeActive = false;

        // Set write parameters (mode page 05h) — CD and DVD-R/-RW Sequential media
        // DVD+R/+RW, DVD-RAM, BD-R/RE, and HD DVD-RW (overwritable) use profile-based
        // write parameters handled internally by the drive firmware and do NOT need
        // Mode Page 05h. However, DVD-R/-RW Sequential Recording and HD DVD-R require
        // Mode Page 05h to be configured with the correct write type before WRITE commands.
        if (!isDvdOrBd)
        {
            var (writeType, trackMode, blockType) = ParseWriteMode(job.WriteMode ?? string.Empty);

            // For SAO (Session-at-Once/DAO) writing, send CUE sheet before writing.
            // The CUE sheet tells the drive the track layout for the entire session.
            if (writeType == MmcCommands.WriteTypeSao)
            {
                byte[]? cueSheet = null;
                byte sessionFormat = 0x00; // CD-DA / CD-ROM default; updated from CUE first track

                // If a CUE file is provided (BIN/CUE audio disc burning), build
                // the MMC CUE sheet from it to support multi-track audio layouts.
                if (!string.IsNullOrWhiteSpace(job.CuePath) && File.Exists(job.CuePath))
                {
                    // Detect the first track's mode from the CUE file and set write
                    // parameters accordingly. The write parameters mode page must match
                    // the CUE sheet's track mode, otherwise the drive rejects the CUE
                    // sheet with "Command sequence error" (ASC=0x2C).
                    var firstTrackMode = DetectCueFirstTrackMode(job.CuePath);
                    if (firstTrackMode.HasValue)
                    {
                        trackMode = firstTrackMode.Value.TrackMode;
                        blockType = firstTrackMode.Value.BlockType;

                        // Update sector size to match the detected block type.
                        // Without this, the write loop reads the wrong chunk sizes
                        // from the image file (e.g. 2048 instead of 2352).
                        sectorSize = blockType switch
                        {
                            MmcCommands.BlockTypeRaw2352 => 2352,
                            MmcCommands.BlockTypeRawPQ2368 => 2368,
                            MmcCommands.BlockTypeRawPW2448 => 2448,
                            MmcCommands.BlockTypeRawPW2448_R => 2448,
                            MmcCommands.BlockTypeMode2_2336 => 2336,
                            MmcCommands.BlockTypeMode2XA_2328 => 2328,
                            MmcCommands.BlockTypeMode2XA_2332 => 2332,
                            _ => 2048
                        };

                        // Determine session format from the CUE first track's data form.
                        // CD-ROM XA (Mode 2) discs require session format 0x20.
                        // CDI discs require session format 0x10.
                        sessionFormat = firstTrackMode.Value.SessionFormat;
                    }

                    cueSheet = BuildSaoAudioCueSheet(job.CuePath);
                    if (cueSheet != null)
                    {
                        progress.Report(new BurnProgress
                        {
                            LogLine = $"[Info] Using CUE sheet from {Path.GetFileName(job.CuePath)} for multi-track SAO/DAO writing"
                        });
                    }
                }

                // Set write parameters AFTER detecting CUE track mode so they match.
                // Pass session format so Mode Page 05h byte 8 matches the CUE sheet content.
                var writeParamsOk = drive.SetWriteParameters(writeType, trackMode, blockType,
                    job.BufferUnderrunProtection, sessionFormat);
                if (!writeParamsOk)
                {
                    progress.Report(new BurnProgress
                    {
                        LogLine = "[Warning] Could not set write parameters via MODE SELECT — retrying"
                    });
                }

                // Fallback: build a minimal single-track CUE sheet from image size
                cueSheet ??= BuildSaoCueSheet(blockType, trackMode, sectorSize,
                    new FileInfo(job.ImagePath).Length);

                var cueResult = drive.SendCueSheet(cueSheet);

                // If CUE sheet rejected with "Command sequence error" (ASC=0x2C),
                // the write parameters may not have been set correctly.
                // Clear pending UNIT ATTENTION, re-set write parameters, and retry.
                if (!cueResult.Success && cueResult.Asc == 0x2C)
                {
                    drive.TestUnitReady();
                    drive.TestUnitReady();

                    // Retry write parameters — the MODE SENSE(6)/SELECT(6) fallback
                    // inside SetWriteParameters will be tried if MODE SENSE(10) failed.
                    var retryOk = drive.SetWriteParameters(writeType, trackMode, blockType,
                        job.BufferUnderrunProtection, sessionFormat);
                    if (retryOk)
                    {
                        cueResult = drive.SendCueSheet(cueSheet);
                    }
                }

                if (cueResult.Success)
                {
                    saoModeActive = true;
                    progress.Report(new BurnProgress
                    {
                        LogLine = "[Info] CUE sheet sent for SAO/DAO writing"
                    });
                }
                else
                {
                    progress.Report(new BurnProgress
                    {
                        LogLine = $"[Warning] CUE sheet rejected by drive ({cueResult.ErrorDescription}), falling back to TAO"
                    });
                    // Fall back to TAO if CUE sheet is rejected
                    drive.SetWriteParameters(MmcCommands.WriteTypeTao, trackMode, blockType,
                        job.BufferUnderrunProtection, sessionFormat);
                }
            }
            else
            {
                drive.SetWriteParameters(writeType, trackMode, blockType, job.BufferUnderrunProtection);
            }
        }
        else if (isDvdMinusRSequential || isHdDvdR)
        {
            // DVD-R/-RW Sequential Recording and HD DVD-R require Mode Page 05h
            // to configure the write type before WRITE commands. Without this,
            // the drive rejects WRITE with "Illegal mode for this track"
            // (SK=0x05, ASC=0x64, ASCQ=0x00).
            //
            // Per MMC-5 §7.5.4.8:
            //   Write Type 0x00 = Incremental Sequential Recording (multi-session possible)
            //   Write Type 0x02 = DAO (Disc At Once — no multi-session, disc finalized)
            //
            // When the user selects DAO mode, use Write Type 0x02 for DVD-R
            // to match cdrecord -dao behavior. Otherwise use Incremental (0x00)
            // for TAO/SAO/Incremental modes, matching cdrecord/growisofs defaults.
            byte dvdWriteType;
            byte dvdTrackMode;
            string writeTypeDesc;

            if (daoModeRequested)
            {
                dvdWriteType = MmcCommands.WriteTypeSao; // 0x02 = DAO for DVD-R per MMC-5
                dvdTrackMode = MmcCommands.TrackModeData; // 0x04 for DAO
                writeTypeDesc = "DAO (Disc At Once)";
            }
            else
            {
                dvdWriteType = MmcCommands.WriteTypeIncremental; // 0x00 = Incremental
                dvdTrackMode = MmcCommands.TrackModeDataInc; // 0x05 for Incremental
                writeTypeDesc = "Incremental Sequential Recording";
            }

            var dvdBlockType = MmcCommands.BlockTypeMode1_2048; // 0x08

            var dvdWriteParamsOk = drive.SetWriteParameters(dvdWriteType, dvdTrackMode,
                dvdBlockType, job.BufferUnderrunProtection);

            if (dvdWriteParamsOk)
            {
                if (daoModeRequested) dvdDaoModeActive = true;
                progress.Report(new BurnProgress
                {
                    LogLine = $"[Info] Write parameters set: {writeTypeDesc} (Mode Page 05h)"
                });
            }
            else
            {
                // Some drives may not expose Mode Page 05h for DVD media.
                // Try clearing any pending UNIT ATTENTION and retry once.
                drive.TestUnitReady();
                drive.TestUnitReady();
                var retryOk = drive.SetWriteParameters(dvdWriteType, dvdTrackMode,
                    dvdBlockType, job.BufferUnderrunProtection);

                if (retryOk)
                {
                    if (daoModeRequested) dvdDaoModeActive = true;
                    progress.Report(new BurnProgress
                    {
                        LogLine = $"[Info] Write parameters set on retry: {writeTypeDesc} (Mode Page 05h)"
                    });
                }
                else
                {
                    // If DAO failed, fall back to Incremental which has broader drive support
                    if (daoModeRequested)
                    {
                        progress.Report(new BurnProgress
                        {
                            LogLine = "[Warning] DVD-R DAO write type not supported by drive — falling back to Incremental"
                        });
                        var fallbackOk = drive.SetWriteParameters(MmcCommands.WriteTypeIncremental,
                            MmcCommands.TrackModeDataInc, dvdBlockType, job.BufferUnderrunProtection);
                        if (fallbackOk)
                        {
                            progress.Report(new BurnProgress
                            {
                                LogLine = "[Info] Write parameters set: Incremental Sequential Recording (Mode Page 05h) — fallback from DAO"
                            });
                        }
                    }
                    else
                    {
                        progress.Report(new BurnProgress
                        {
                            LogLine = "[Warning] Could not set DVD write parameters (Mode Page 05h) — " +
                                      "drive may handle write mode internally"
                        });
                    }
                }
            }
        }

        // Perform OPC (Optimal Power Calibration) — not supported on overwritable media.
        // OPC runs even in simulation mode so the drive calibrates laser power and
        // strategy for the specific media. The Simu bit on WRITE commands then uses
        // the calibrated laser at reduced power. Without OPC, the laser may not fire
        // at all during simulation, defeating the purpose.
        if (!isOverwritable)
        {
            drive.PerformOpc();
            progress.Report(new BurnProgress
            {
                LogLine = "[Info] Optimal Power Calibration complete"
            });

            // Wait for drive to settle after OPC before starting writes.
            // Some drives return OPC success while still calibrating internally.
            // Without this wait, the first WRITE may fail with "Not Ready,
            // long write in progress" (ASC=0x04, ASCQ=0x08).
            drive.WaitForDriveReady(maxWaitMs: 30_000, pollIntervalMs: 500);
        }

        // Configure BD-R write mode if applicable
        if (isBdR && !string.IsNullOrWhiteSpace(job.BdRMode))
        {
            var modeConfigured = drive.ConfigureBdRMode(job.BdRMode);
            progress.Report(new BurnProgress
            {
                LogLine = modeConfigured
                    ? $"[Info] BD-R mode configured: {job.BdRMode}"
                    : $"[Warning] Could not configure BD-R mode '{job.BdRMode}', using drive default"
            });
        }

        // For dual-layer media, log the layer break position if known
        // DVD-R DL Sequential (0x0015), DVD-R DL Layer Jump (0x0016),
        // DVD+RW DL (0x002A), DVD+R DL (0x002B), HD DVD-R DL (0x0058),
        // BD-R DL (both SRM 0x0041 and RRM 0x0042 can be dual-layer — detected by size),
        // BD-RE DL (0x0043 can also be dual-layer)
        var isDualLayer = profile is 0x0015 or 0x0016 or 0x002A or 0x002B or 0x0058
            or 0x0044 or 0x0045 or 0x0046; // BD-ROM DL, BD-R DL, BD-RE DL
        // Check BD-R/BD-RE dual-layer via layer info
        if (!isDualLayer && (isBdR || isBdRe))
        {
            var layerCheck = drive.GetLayerBreakPosition();
            if (layerCheck.HasValue)
                isDualLayer = true;
        }
        if (isDualLayer)
        {
            var layerBreak = drive.GetLayerBreakPosition();
            if (layerBreak.HasValue)
            {
                progress.Report(new BurnProgress
                {
                    LogLine = $"[Info] Dual-layer media: layer break at LBA {layerBreak.Value}"
                });
            }
            else if (job.LayerBreakPosition > 0)
            {
                progress.Report(new BurnProgress
                {
                    LogLine = $"[Info] User-specified layer break at LBA {job.LayerBreakPosition}"
                });
            }
        }

        // Get next writable address
        uint startLba = 0;
        var multiSession = job.MultiSessionMode ?? string.Empty;
        if (multiSession.Contains("Continue", StringComparison.OrdinalIgnoreCase))
        {
            var msInfo = drive.GetMultiSessionInfo();
            if (msInfo.HasValue)
            {
                startLba = msInfo.Value.NextWritableAddress;
                progress.Report(new BurnProgress
                {
                    LogLine = $"[Info] Multi-session: continuing at LBA {startLba}"
                });
            }
        }
        else if (isOverwritable)
        {
            // Overwritable media always starts at LBA 0
            startLba = 0;
        }
        else if (!saoModeActive)
        {
            // TAO / RAW / Packet modes: use NWA from track info
            var trackInfo = drive.ReadTrackInfo(1);
            if (trackInfo?.NwaValid == true)
                startLba = trackInfo.NextWritableAddress;
        }
        // SAO mode: startLba stays 0; pregap is written separately below.

        // -----------------------------------------------------------------
        // SAO/DAO pregap: per MMC-5 §4.9.3 the host must write ALL session
        // data excluding lead-in and lead-out.  The CUE sheet always includes
        // a 2-second (150-frame) pregap before Track 1, starting at absolute
        // MSF 00:00:00 which corresponds to LBA -150.  The drive rejects
        // WRITE at LBA 0 with "Invalid address for write" (ASC 0x21, ASCQ 0x02)
        // unless the pregap data is written first.
        // -----------------------------------------------------------------
        if (saoModeActive && !job.SimulateOnly)
        {
            // LBA -150 in unsigned 32-bit representation (two's complement: 0xFFFFFF6A)
            uint pregapStartLba = unchecked((uint)(-SaoPregapSectors));

            progress.Report(new BurnProgress
            {
                LogLine = $"[Info] Writing 2-second pregap ({SaoPregapSectors} sectors of silence) at LBA -150"
            });

            var pregapBuffer = new byte[SectorsPerWrite * sectorSize]; // zero-filled = silence
            int pregapWritten = 0;
            uint pregapLba = pregapStartLba;

            while (pregapWritten < SaoPregapSectors)
            {
                if (ct.IsCancellationRequested)
                    throw new OperationCanceledException(ct);

                var toWrite = Math.Min(SectorsPerWrite, SaoPregapSectors - pregapWritten);
                var dataLen = toWrite * sectorSize;

                var result = WriteSectorsWithRetry(drive, pregapLba, pregapBuffer,
                    sectorSize, toWrite, dataLen, useDvdBdMode: false);
                if (!result.Success)
                {
                    // Log drive diagnostics on pregap write failure
                    try
                    {
                        var (sk, a, aq) = drive.RequestSense();
                        if (sk != 0x00)
                        {
                            var senseDesc = new ScsiResult
                            {
                                Status = 0x02,
                                SenseData = BuildFixedSenseData(sk, a, aq)
                            }.ErrorDescription;
                            progress.Report(new BurnProgress
                            {
                                LogLine = $"[Error] Drive sense after pregap write failure: {senseDesc}"
                            });
                        }
                    }
                    catch { /* drive may be unresponsive */ }

                    throw new InvalidOperationException(
                        $"Write failed at LBA {pregapLba} (pregap): {result.ErrorDescription}");
                }

                pregapLba += (uint)toWrite;
                pregapWritten += toWrite;
            }

            // After writing 150 sectors from LBA -150, pregapLba wraps to 0.
            // Image data starts at LBA 0 — set startLba accordingly.
            startLba = 0;
        }

        // Open the image file and start writing
        await using var imageStream = new FileStream(
            job.ImagePath, FileMode.Open, FileAccess.Read,
            FileShare.Read, 65536, true);

        // Support direct burning from non-native formats (MDF, CDI, etc.)
        // where sector data starts at a non-zero offset within the file.
        if (job.ImageStartOffset > 0)
        {
            if (job.ImageStartOffset >= imageStream.Length)
                throw new InvalidOperationException(
                    $"Image start offset ({job.ImageStartOffset}) exceeds file size ({imageStream.Length}).");
            imageStream.Seek(job.ImageStartOffset, SeekOrigin.Begin);
        }

        var totalBytes = imageStream.Length - job.ImageStartOffset;
        var sectorCountLong = (totalBytes + sectorSize - 1) / sectorSize;
        if (sectorCountLong > uint.MaxValue)
            throw new InvalidOperationException(
                $"Image size ({FormatHelper.FormatBytes(totalBytes)}) exceeds maximum addressable sector count.");
        var totalSectors = (uint)sectorCountLong;

        // Validate image size against media capacity
        var mediaCapacity = drive.ReadCapacity();
        if (mediaCapacity.HasValue && mediaCapacity.Value.LastLba > 0)
        {
            // Use long arithmetic to prevent uint overflow when LastLba is near uint.MaxValue
            // and to prevent underflow when startLba > LastLba + 1.
            var totalMediaSectors = (long)mediaCapacity.Value.LastLba + 1;
            var availableSectors = Math.Max(0, totalMediaSectors - startLba);
            if (totalSectors > availableSectors)
            {
                var availableBytes = (long)availableSectors * mediaCapacity.Value.BlockLength;
                if (job.Overburn)
                {
                    // Overburn enabled — warn but proceed
                    // Validate against user-specified overburn limit if set.
                    // Default overburn limits vary by media type:
                    //   CD-R/RW: ~25 MB (typical overburn tolerance)
                    //   DVD±R/RW: ~10 MB (very limited overburn tolerance)
                    //   BD/HD DVD: 0 MB (overburn not supported)
                    long defaultOverburnMb;
                    if (OpticalDrive.IsProfileBluRay(profile) || OpticalDrive.IsProfileHdDvd(profile))
                        defaultOverburnMb = 0;
                    else if (OpticalDrive.IsProfileDvd(profile))
                        defaultOverburnMb = 10;
                    else
                        defaultOverburnMb = 25; // CD

                    var overburnLimitBytes = job.OverburnSizeMb > 0
                        ? job.OverburnSizeMb * 1_048_576L
                        : availableBytes + defaultOverburnMb * 1_048_576L;

                    if (totalBytes > overburnLimitBytes)
                    {
                        throw new InvalidOperationException(
                            $"Image size ({FormatHelper.FormatBytes(totalBytes)}) exceeds overburn limit " +
                            $"({FormatHelper.FormatBytes(overburnLimitBytes)}). Reduce image size or increase overburn limit.");
                    }

                    progress.Report(new BurnProgress
                    {
                        LogLine = $"[Warning] Overburning: image ({FormatHelper.FormatBytes(totalBytes)}) exceeds " +
                                  $"media capacity ({FormatHelper.FormatBytes(availableBytes)}). " +
                                  "Proceeding with overburn enabled."
                    });
                }
                else
                {
                    progress.Report(new BurnProgress
                    {
                        LogLine = $"[Warning] Image size ({FormatHelper.FormatBytes(totalBytes)}) exceeds " +
                                  $"available media capacity ({FormatHelper.FormatBytes(availableBytes)}). " +
                                  "The burn may fail. Enable overburn to proceed past capacity limits."
                    });
                }
            }
        }

        long bytesWritten = 0;
        long fileBytesRead = 0;
        uint currentLba = startLba;
        var startTime = DateTime.Now;

        progress.Report(new BurnProgress
        {
            StatusMessage = $"Burning {FormatHelper.FormatBytes(totalBytes)}...",
            TotalBytes = totalBytes,
            TotalSectors = totalSectors,
            CurrentLba = startLba,
            LogLine = $"[Info] Image size: {totalBytes} bytes, {totalSectors} sectors"
        });

        // Log initial buffer capacity
        var bufCap = drive.ReadBufferCapacity();
        if (bufCap.HasValue)
        {
            progress.Report(new BurnProgress
            {
                LogLine = $"[Info] Drive buffer: {bufCap.Value.TotalLength / 1024} KB total, {bufCap.Value.BlankLength / 1024} KB free"
            });
        }

        // -----------------------------------------------------------------
        // RESERVE TRACK for DVD-R/-RW Sequential and HD DVD-R in DAO mode.
        // Per MMC-5 §4.9.3.8, the DAO command sequence for DVD-R is:
        //   MODE SELECT (Page 05h, Write Type 0x02) → RESERVE TRACK → WRITE
        // Without RESERVE TRACK, the drive rejects the first WRITE with
        // "Command sequence error" (SK=0x05, ASC=0x2C, ASCQ=0x00) because
        // it does not know the track size. This matches cdrecord -dao and
        // growisofs behavior which always send RESERVE TRACK for DVD-R DAO.
        //
        // For Incremental mode (Write Type 0x00), RESERVE TRACK is optional
        // — the drive auto-allocates space on each WRITE. For CD SAO/DAO,
        // SEND CUE SHEET serves the equivalent role (already handled above).
        // -----------------------------------------------------------------
        if (dvdDaoModeActive && !job.SimulateOnly)
        {
            var reserveOk = drive.ReserveTrack(totalSectors);
            if (reserveOk)
            {
                progress.Report(new BurnProgress
                {
                    LogLine = $"[Info] Reserved track: {totalSectors} sectors for DAO writing"
                });
            }
            else
            {
                // If RESERVE TRACK fails, retry after clearing potential UNIT ATTENTION.
                // Some drives generate UNIT ATTENTION after OPC or mode changes.
                drive.TestUnitReady();
                drive.TestUnitReady();
                drive.WaitForDriveReady(maxWaitMs: 10_000, pollIntervalMs: 500);
                reserveOk = drive.ReserveTrack(totalSectors);

                if (reserveOk)
                {
                    progress.Report(new BurnProgress
                    {
                        LogLine = $"[Info] Reserved track on retry: {totalSectors} sectors for DAO writing"
                    });
                }
                else
                {
                    progress.Report(new BurnProgress
                    {
                        LogLine = "[Warning] RESERVE TRACK failed — the drive may reject subsequent WRITE commands. " +
                                  "Consider using Incremental write mode instead of DAO for this drive."
                    });
                }
            }
        }

        var buffer = new byte[SectorsPerWrite * sectorSize];
        int writeCount = 0;

        while (fileBytesRead < totalBytes)
        {
            // Check cancellation explicitly before each iteration to produce a single,
            // clean OperationCanceledException from our code rather than letting it
            // bubble out of ReadAsync (System.Private.CoreLib).
            if (ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);

            // Read data from image file
            var bytesToRead = (int)Math.Min(buffer.Length, totalBytes - fileBytesRead);
            int bytesRead;
            try
            {
                bytesRead = await imageStream.ReadAsync(buffer.AsMemory(0, bytesToRead), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Consolidate: throw a single OperationCanceledException from our code
                throw new OperationCanceledException(ct);
            }
            if (bytesRead <= 0) break;

            // Track how much of the source file we've consumed
            fileBytesRead += bytesRead;

            // Pad the last chunk to sector boundary.
            // Track the actual source bytes separately from padded bytes to avoid
            // writing padding data that inflates the bytesWritten counter.
            var actualDataBytes = bytesRead;
            if (bytesRead % sectorSize != 0)
            {
                var padded = ((bytesRead + sectorSize - 1) / sectorSize) * sectorSize;
                if (padded > buffer.Length)
                    throw new InvalidOperationException(
                        $"Padded sector size ({padded}) exceeds write buffer ({buffer.Length}). " +
                        $"This indicates a sector size mismatch.");
                Array.Clear(buffer, bytesRead, padded - bytesRead);
                bytesRead = padded;
            }

            var sectorsToWrite = bytesRead / sectorSize;

            if (!job.SimulateOnly)
            {
                // Write sectors to disc with retry for transient SCSI errors.
                // Transient "Not Ready" errors (e.g. ASC=0x04 "long write in progress"),
                // transport errors (IOCTL/semaphore timeouts), and medium/hardware errors
                // are retried with appropriate recovery (TUR draining, drive readiness waits).
                // Use WRITE(12) for DVD/BD media (32-bit transfer length, reduced command overhead)
                // Use WRITE(10) for CD media (standard 16-bit transfer length)
                var result = WriteSectorsWithRetry(drive, currentLba, buffer,
                    sectorSize, sectorsToWrite, bytesRead, useDvdBdMode: isDvdOrBd);
            if (!result.Success)
            {
                // Log detailed diagnostics before throwing — REQUEST SENSE and
                // MECHANISM STATUS help diagnose the root cause (bad media, failing
                // laser, USB bridge issues, etc.) matching ImgBurn/cdrecord behavior.
                try
                {
                    var (sk, a, aq) = drive.RequestSense();
                    if (sk != 0x00)
                    {
                        var senseDesc = new ScsiResult
                        {
                            Status = 0x02,
                            SenseData = BuildFixedSenseData(sk, a, aq)
                        }.ErrorDescription;
                        progress.Report(new BurnProgress
                        {
                            LogLine = $"[Error] Drive sense after write failure: {senseDesc}"
                        });
                    }
                }
                catch { /* drive may be unresponsive */ }

                try
                {
                    var mechStatus = drive.GetMechanismStatus();
                    if (mechStatus != null)
                    {
                        var stateDesc = mechStatus.MechanismState switch
                        {
                            0 => "Idle", 1 => "Playing", 2 => "Scanning",
                            3 => "Host Active", _ => $"Unknown({mechStatus.MechanismState})"
                        };
                        progress.Report(new BurnProgress
                        {
                            LogLine = $"[Error] Drive mechanism: State={stateDesc}, " +
                                      $"Fault={mechStatus.Fault}, DoorOpen={mechStatus.DoorOpen}"
                        });
                    }
                }
                catch { /* drive may be unresponsive */ }

                throw new InvalidOperationException(
                    $"Write failed at LBA {currentLba}: {result.ErrorDescription}");
            }
        }

            currentLba += (uint)sectorsToWrite;
            bytesWritten += actualDataBytes;
            writeCount++;

            // Periodic buffer capacity check (every 256 writes ≈ every 8 MB)
            if (!job.SimulateOnly && writeCount % 256 == 0)
            {
                var bc = drive.ReadBufferCapacity();
                if (bc.HasValue && bc.Value.TotalLength > 0)
                {
                    var pctFull = 100 - (int)(bc.Value.BlankLength * 100 / bc.Value.TotalLength);
                    progress.Report(new BurnProgress
                    {
                        LogLine = $"[Info] Drive buffer: {pctFull}% full ({bc.Value.BlankLength / 1024} KB free)"
                    });
                }
            }

            // Report progress using file bytes for accurate percentage
            var elapsed = DateTime.Now - startTime;
            var pct = totalBytes > 0 ? (int)(fileBytesRead * 100 / totalBytes) : 0;
            // Speed in bytes/second based on actual file data read (not padded bytes written)
            // 1x CD = 150 KiB/s; 1x DVD = 1,385 KiB/s; 1x BD = 4,495 KiB/s; 1x HD DVD = 4,568 KiB/s
            var bytesPerSec = elapsed.TotalSeconds > 0 ? fileBytesRead / elapsed.TotalSeconds : 0;
            var baseSpeedBps = isBd ? FormatHelper.BdBaseSpeedKBs * 1024
                : isHdDvd ? FormatHelper.HdDvdBaseSpeedKBs * 1024
                : isDvdOrBd ? FormatHelper.DvdBaseSpeedKBs * 1024
                : FormatHelper.CdBaseSpeedKBs * 1024;
            var speedX = baseSpeedBps > 0 ? bytesPerSec / baseSpeedBps : 0;

            // Estimate remaining time
            var remainingBytes = totalBytes - fileBytesRead;
            var eta = bytesPerSec > 0
                ? TimeSpan.FromSeconds(remainingBytes / bytesPerSec)
                : TimeSpan.Zero;

            progress.Report(new BurnProgress
            {
                PercentComplete = Math.Min(pct, 99),
                // BytesWritten represents source file progress for UI display
                BytesWritten = fileBytesRead,
                TotalBytes = totalBytes,
                CurrentLba = currentLba,
                TotalSectors = totalSectors,
                CurrentSpeedX = speedX,
                Elapsed = elapsed,
                Remaining = eta,
                StatusMessage = $"Writing: {FormatHelper.FormatBytes(fileBytesRead)} of " +
                               $"{FormatHelper.FormatBytes(totalBytes)} ({pct}%)",
                // Only log every 256 writes (~8 MB) to avoid flooding the log textbox
                LogLine = writeCount % 256 == 0
                    ? $"LBA {currentLba}: {fileBytesRead / 1_048_576} MB written, {speedX:F1}x"
                    : null
            });
        }

        // Synchronize cache (flush write buffer)
        if (!job.SimulateOnly)
        {
            progress.Report(new BurnProgress
            {
                StatusMessage = "Flushing write cache...",
                LogLine = "[Info] Synchronizing cache..."
            });
            var syncOk = drive.SynchronizeCache();

            // If SynchronizeCache fails, the write buffer may not have been flushed.
            // On macOS IOKit, transport-level failures can occur transiently. Retry
            // after waiting for drive readiness to clear the condition.
            //
            // IMPORTANT: Do NOT use StartStopUnit(true) here. On macOS, StartStopUnit
            // can cause IOKit to re-evaluate the device state, potentially tearing down
            // and recreating the user client. This invalidates cached vtable pointers
            // (from IOCreatePlugInInterfaceForService / QueryInterface), and subsequent
            // vtable calls crash with SIGILL. All subsequent commands (CLOSE TRACK,
            // CLOSE SESSION, Eject) would also fail. Use gentle recovery only:
            // TUR draining + WaitForDriveReady polling.
            if (!syncOk)
            {
                progress.Report(new BurnProgress
                {
                    LogLine = "[Warning] Synchronize cache failed — retrying after drive recovery..."
                });
                drive.TestUnitReady();
                drive.TestUnitReady();
                drive.WaitForDriveReady(maxWaitMs: 30_000, pollIntervalMs: 1000);
                syncOk = drive.SynchronizeCache();
                if (!syncOk)
                {
                    progress.Report(new BurnProgress
                    {
                        LogLine = "[Warning] Synchronize cache retry also failed — " +
                                  "write data may be partially buffered"
                    });
                }
            }

            // Wait for drive to become ready after synchronize cache.
            // On macOS, SynchronizeCache uses IMM=1 and polls internally, but the
            // drive may still need additional time to finalize internal state (updating
            // write pointers, NWA, etc.). On Linux/Windows, IMM=0 waits for the buffer
            // to flush, but the drive may still need time to settle.
            // Sending CLOSE TRACK immediately can fail with "Not Ready" (SK=0x02,
            // ASC=0x04) or "Command Sequence Error" (ASC=0x2C) on some drives.
            // Use async wait so progress updates continue and cancellation is responsive.
            var cacheReady = await WaitForDriveReadyAsync(drive, progress, ct,
                maxWaitMs: 60_000, pollIntervalMs: 1000, statusPrefix: "Flushing write cache");
            if (!cacheReady)
            {
                progress.Report(new BurnProgress
                {
                    LogLine = "[Warning] Drive did not become ready after cache flush within 60s — continuing"
                });
            }
        }

        // Close track/session
        // Overwritable media (DVD+RW, DVD-RAM, BD-RE, HD DVD-RAM, HD DVD-RW) don't
        // support CLOSE TRACK/SESSION — only SYNCHRONIZE CACHE is needed (done above).
        if (!job.SimulateOnly && !isOverwritable)
        {
            var isMultiSessionStart = multiSession.Contains("Start", StringComparison.OrdinalIgnoreCase);
            var isMultiSessionContinue = multiSession.Contains("Continue", StringComparison.OrdinalIgnoreCase);

            // DAO (Disc At Once) mode is inherently single-session: the entire disc
            // is written and finalized in one operation. Multi-session is not compatible.
            // SAO (Session At Once) mode writes one session at a time; multi-session IS
            // technically possible with SAO (close session, leave disc appendable).
            // Per MMC-5 §4.9.3 and cdrecord/ImgBurn behavior:
            //   -dao: single-session only, disc always finalized
            //   -sao: supports multi-session (close session without finalizing disc)
            //
            // Note: daoModeRequested is checked alone (not with saoModeActive) because
            // saoModeActive only gets set for CDs after CUE sheet is sent, but DAO mode
            // also applies to DVD-R where saoModeActive is never set.
            if (daoModeRequested && (isMultiSessionStart || isMultiSessionContinue))
            {
                progress.Report(new BurnProgress
                {
                    LogLine = "[Warning] Multi-session is not compatible with DAO (Disc At Once) write mode — " +
                              "treating as single-session with disc finalization"
                });
                isMultiSessionStart = false;
                isMultiSessionContinue = false;
            }

            // Determine media-type-aware finalization timeout.
            // Finalization times vary significantly by media type:
            //   CD-R/RW:  ~1–2 minutes (lead-out is short)
            //   DVD±R/RW: ~2–5 minutes (larger lead-out, TOC updates)
            //   BD-R:     ~5–20 minutes (large lead-out, extensive internal processing)
            //   HD DVD-R: ~3–10 minutes
            // Use generous timeouts to avoid premature timeout on slow drives or media.
            int finalizeTimeoutMs = isBd ? 1_200_000      // 20 minutes for BD-R
                : isHdDvd ? 600_000                       // 10 minutes for HD DVD
                : isDvdOrBd ? 600_000                     // 10 minutes for DVD
                : 300_000;                                // 5 minutes for CD

            // DAO mode always finalizes the disc regardless of the CloseDisc setting.
            // Per MMC-5 §4.9.3, DAO writes the entire disc atomically and the disc
            // must be finalized. cdrecord/ImgBurn also enforce this behavior.
            bool shouldFinalize = (job.CloseDisc || daoModeRequested) && !isMultiSessionStart && !isMultiSessionContinue;

            if (shouldFinalize)
            {
                // Single session: finalize disc
                progress.Report(new BurnProgress
                {
                    StatusMessage = "Finalizing disc...",
                    LogLine = "[Info] Closing session and finalizing disc"
                });

                // Skip CLOSE TRACK when:
                // 1. SAO/DAO mode on CD: tracks are defined by the CUE sheet and the
                //    drive closes them internally as part of the SAO write process.
                //    Per MMC-5 §4.9.3, only CLOSE SESSION should be sent after SAO.
                //    Sending CLOSE TRACK in SAO mode causes errors because the track
                //    is already closed from the drive's perspective.
                // 2. DVD-R/-RW Sequential and HD DVD-R: per MMC-5 §6.1, CLOSE SESSION
                //    automatically closes any incomplete tracks. Sending a redundant
                //    CLOSE TRACK can fail on macOS IOKit due to timing sensitivity.
                //    This matches cdrecord and growisofs behavior for DVD-R.
                // 3. BD-R (SRM): per MMC-6, CLOSE SESSION for BD-R automatically
                //    closes any incomplete tracks. The drive firmware manages track
                //    boundaries internally. Sending CLOSE TRACK separately can fail
                //    or is redundant.
                //
                // For CD TAO mode: keep the explicit CLOSE TRACK + CLOSE SESSION FINAL
                // sequence, which is the standard and proven approach.
                if (!saoModeActive && !isDvdMinusRSequential && !isHdDvdR && !isBdR)
                {
                    var closeTrackOk = drive.CloseTrackSession(MmcCommands.CloseTrack, 1);
                    if (!closeTrackOk)
                    {
                        progress.Report(new BurnProgress
                        {
                            LogLine = "[Warning] Close track command failed — disc may not be readable in all players"
                        });
                    }

                    // Wait for drive readiness between close track and finalize.
                    // After close track (with IMM=1), the drive may still be processing
                    // (writing run-out, padding). Sending CLOSE SESSION too early causes
                    // "Command sequence error" (ASC=0x2C) or "Not ready" (SK=0x02).
                    var closeTrackReady = await WaitForDriveReadyAsync(drive, progress, ct,
                        maxWaitMs: 120_000, pollIntervalMs: 1000,
                        statusPrefix: "Waiting for track close to complete");
                    if (!closeTrackReady)
                    {
                        progress.Report(new BurnProgress
                        {
                            LogLine = "[Warning] Drive did not become ready after close track within 120s — attempting finalize anyway"
                        });
                    }
                }

                // For DVD-R/-RW Sequential, HD DVD-R, and BD-R (SRM): send explicit
                // CLOSE SESSION (0x02) before CLOSE SESSION FINAL (0x06).
                // Per MMC-5 §6.22 Table 29, CLOSE SESSION closes incomplete tracks
                // and writes the session lead-out. Some drives (especially on Windows)
                // require the session to be explicitly closed before accepting the
                // finalize command. This matches growisofs and cdrecord behavior for
                // DVD-R which always close the session before finalizing.
                // For CD SAO mode: the CUE sheet defines the session layout and the
                // drive closes it internally — only CLOSE SESSION FINAL is needed.
                if ((isDvdMinusRSequential || isHdDvdR || isBdR) && !saoModeActive)
                {
                    progress.Report(new BurnProgress
                    {
                        LogLine = "[Info] Closing session before finalization " +
                                  $"({(isDvdMinusRSequential ? "DVD-R Sequential" : isHdDvdR ? "HD DVD-R" : "BD-R")})"
                    });

                    var closeSessionOk = drive.CloseTrackSession(MmcCommands.CloseSession);
                    if (!closeSessionOk)
                    {
                        progress.Report(new BurnProgress
                        {
                            LogLine = "[Warning] Close session command failed — attempting finalize anyway"
                        });
                    }

                    // Wait for drive readiness after session close before finalization.
                    // CLOSE SESSION with IMM=1 returns immediately while the drive writes
                    // session lead-out in the background. On Windows, sending CLOSE SESSION
                    // FINAL too soon causes "Not Ready" (SK=0x02) or "Command Sequence Error"
                    // (ASC=0x2C). Use generous timeout for DVD/BD lead-out writing.
                    var sessionCloseReady = await WaitForDriveReadyAsync(drive, progress, ct,
                        maxWaitMs: 120_000, pollIntervalMs: 1000,
                        statusPrefix: "Waiting for session close to complete");
                    if (!sessionCloseReady)
                    {
                        progress.Report(new BurnProgress
                        {
                            LogLine = "[Warning] Drive did not become ready after session close within 120s — attempting finalize anyway"
                        });
                    }
                }

                var closeFinalOk = drive.CloseTrackSession(MmcCommands.CloseSessionFinal);
                if (!closeFinalOk)
                {
                    progress.Report(new BurnProgress
                    {
                        LogLine = "[Warning] Finalize disc command failed — disc may not be finalized"
                    });
                }

                // Wait for drive readiness after finalize. CloseSessionFinal with IMM=1
                // returns immediately while the drive writes lead-out, TOC, and updates
                // internal structures in the background. This can take 1–20 minutes
                // depending on media type (CD-R ~1 min, DVD-R ~2–5 min, BD-R ~5–20 min).
                // Without this wait, the subsequent eject will fail because the drive
                // is still busy finalizing.
                progress.Report(new BurnProgress
                {
                    LogLine = $"[Info] Waiting for disc finalization to complete (timeout: {finalizeTimeoutMs / 60_000} min)..."
                });
                var finalizeReady = await WaitForDriveReadyAsync(drive, progress, ct,
                    maxWaitMs: finalizeTimeoutMs, pollIntervalMs: 2000,
                    statusPrefix: "Finalizing disc");
                if (!finalizeReady)
                {
                    progress.Report(new BurnProgress
                    {
                        LogLine = $"[Warning] Drive did not become ready within {finalizeTimeoutMs / 60_000} minutes after finalize — " +
                                  "disc may still be finalizing in the background"
                    });
                }
            }
            else if (isMultiSessionContinue && job.CloseDisc)
            {
                // Multi-session continue with close: close current session
                // but keep disc appendable (CloseSession ≠ finalize).
                // Skip CLOSE TRACK for SAO mode, DVD-R/-RW Sequential/HD DVD-R, and BD-R
                // (same rationale as the single-session case above).
                if (!saoModeActive && !isDvdMinusRSequential && !isHdDvdR && !isBdR)
                {
                    var closeTrackOk = drive.CloseTrackSession(MmcCommands.CloseTrack, 1);
                    // Wait for drive readiness between close track and close session
                    var trackReady = await WaitForDriveReadyAsync(drive, progress, ct,
                        maxWaitMs: 120_000, pollIntervalMs: 1000,
                        statusPrefix: "Waiting for track close to complete");
                    if (!trackReady)
                    {
                        progress.Report(new BurnProgress
                        {
                            LogLine = "[Warning] Drive did not become ready after close track within 120s — attempting session close"
                        });
                    }
                    if (!closeTrackOk)
                    {
                        progress.Report(new BurnProgress
                        {
                            LogLine = "[Warning] Close track command failed — multi-session state may be inconsistent"
                        });
                    }
                }
                var closeSessionOk = drive.CloseTrackSession(MmcCommands.CloseSession);
                if (!closeSessionOk)
                {
                    progress.Report(new BurnProgress
                    {
                        LogLine = "[Warning] Close session command failed — multi-session state may be inconsistent"
                    });
                }
                progress.Report(new BurnProgress
                {
                    LogLine = "[Info] Track and session closed, disc remains appendable (multi-session continue)"
                });
                // Wait for drive readiness after session close
                var sessionReady = await WaitForDriveReadyAsync(drive, progress, ct,
                    maxWaitMs: 120_000, pollIntervalMs: 2000,
                    statusPrefix: "Waiting for session close to complete");
                if (!sessionReady)
                {
                    progress.Report(new BurnProgress
                    {
                        LogLine = "[Warning] Drive did not become ready after session close within 120s"
                    });
                }
            }
            else
            {
                // Multi-session start or not closing: close track only, keep session open.
                // Skip CLOSE TRACK for SAO mode, DVD-R/-RW Sequential, HD DVD-R, and BD-R:
                // same rationale as the single-session and multi-session continue branches.
                // In SAO mode, the drive closes tracks internally as part of the SAO write.
                // For DVD-R Sequential and HD DVD-R, CLOSE SESSION auto-closes incomplete tracks.
                // For BD-R (SRM), tracks are managed by the drive firmware.
                if (!saoModeActive && !isDvdMinusRSequential && !isHdDvdR && !isBdR)
                {
                    var closeTrackOk = drive.CloseTrackSession(MmcCommands.CloseTrack, 1);
                    if (!closeTrackOk)
                    {
                        progress.Report(new BurnProgress
                        {
                            LogLine = "[Warning] Close track command failed"
                        });
                    }
                    progress.Report(new BurnProgress
                    {
                        LogLine = "[Info] Track closed, session kept open for multi-session"
                    });
                    // Wait for drive readiness after track close
                    var trackReady = await WaitForDriveReadyAsync(drive, progress, ct,
                        maxWaitMs: 60_000, pollIntervalMs: 1000,
                        statusPrefix: "Waiting for track close to complete");
                    if (!trackReady)
                    {
                        progress.Report(new BurnProgress
                        {
                            LogLine = "[Warning] Drive did not become ready after track close within 60s"
                        });
                    }
                }
                else
                {
                    progress.Report(new BurnProgress
                    {
                        LogLine = "[Info] Session kept open (CLOSE TRACK skipped — " +
                                  (saoModeActive ? "SAO mode" :
                                   isDvdMinusRSequential ? "DVD-R Sequential" :
                                   isHdDvdR ? "HD DVD-R" : "BD-R") +
                                  " handles track closure internally)"
                    });
                }
            }
        }
        else if (!job.SimulateOnly)
        {
            progress.Report(new BurnProgress
            {
                LogLine = "[Info] Overwritable media — session close not required"
            });
        }

        // Verify after burn if requested
        if (job.VerifyAfterBurn && !job.SimulateOnly)
        {
            progress.Report(new BurnProgress
            {
                StatusMessage = "Verifying burn...",
                LogLine = "[Info] Starting post-burn verification"
            });

            var verifyOk = await VerifyBurnAsync(drive, job.ImagePath, startLba, totalSectors,
                sectorSize, isDvdOrBd, job.ImageStartOffset, progress, ct);

            if (verifyOk)
            {
                progress.Report(new BurnProgress
                {
                    LogLine = "[Info] Post-burn verification passed — disc matches image"
                });
            }
            else
            {
                progress.Report(new BurnProgress
                {
                    LogLine = "[Warning] Post-burn verification FAILED — disc may not match image"
                });
            }
        }

        // Eject if requested
        if (job.EjectAfterBurn)
        {
            progress.Report(new BurnProgress
            {
                StatusMessage = "Ejecting disc...",
                LogLine = "[Info] Ejecting disc after burn"
            });

            // Unlock the tray before ejecting. Per SPC-5 §6.32, START STOP UNIT
            // with LoEj=1 SHALL fail with CHECK CONDITION (SK=0x05, ASC=0x53/0x02
            // "Medium removal prevented") if PREVENT MEDIUM REMOVAL is active.
            // The tray was locked at the start of the burn to prevent accidental
            // ejection during write operations.
            for (int unlockRetry = 0; unlockRetry < 3; unlockRetry++)
            {
                try
                {
                    if (drive.PreventMediumRemoval(false))
                        break;
                }
                catch { /* best effort */ }
                if (unlockRetry < 2) Thread.Sleep(500);
            }

            // Wait for drive to become ready before eject — the drive may still be
            // processing finalization (writing lead-out, updating TOC). Attempting to
            // eject while the drive is busy causes the eject to fail silently.
            // Use a generous timeout (120s) as finalization can be slow on some media.
            // Async wait keeps the UI responsive and allows cancellation.
            await WaitForDriveReadyAsync(drive, progress, ct,
                maxWaitMs: 120_000, pollIntervalMs: 2000,
                statusPrefix: "Preparing to eject");

            // Retry eject with backoff — some drives need multiple attempts after
            // finalization, especially USB-attached drives that buffer commands.
            // Between retries, wait for drive readiness to avoid hammering a busy drive.
            bool ejected = false;
            for (int ejectRetry = 0; ejectRetry < 5 && !ejected; ejectRetry++)
            {
                try
                {
                    ejected = drive.Eject();
                }
                catch { /* transient error, retry */ }

                if (!ejected && ejectRetry < 4)
                {
                    await Task.Delay(2000 * (ejectRetry + 1), CancellationToken.None);
                    drive.TestUnitReady(); // Clear any pending Unit Attention
                    // Give the drive more time to finish any pending operations
                    drive.WaitForDriveReady(maxWaitMs: 15_000, pollIntervalMs: 1000);
                }
            }

            // If SCSI eject failed, try platform-specific fallback.
            // On macOS, diskutil eject needs the device to NOT be exclusively
            // claimed through IOKit. Release the SCSI transport first so the
            // system can access the device for ejection. The drive object is
            // still in scope but its transport is disposed — only the fallback
            // eject path is used from this point.
            if (!ejected)
            {
                try
                {
                    // Release exclusive SCSI access so platform eject tools
                    // (diskutil, etc.) can access the device
                    drive.Dispose();
                    driveDisposed = true;
                }
                catch { /* best effort */ }

                try
                {
                    ejected = Native.Platform.NativeEject.EjectDisc(job.DevicePath);
                }
                catch { /* best effort */ }
            }

            // After a successful eject, the disc tray is open and the media is removed.
            // On macOS, IOKit may tear down the user client after media removal, which
            // invalidates the SCSITaskDeviceInterface vtable pointers. Any subsequent
            // SCSI commands (SynchronizeCache, PreventMediumRemoval in the finally block)
            // through the invalid vtable would crash with SIGILL (illegal hardware
            // instruction). Mark the drive as disposed to skip those cleanup calls.
            if (ejected)
            {
                try { drive.Dispose(); } catch { /* best effort */ }
                driveDisposed = true;
            }

            if (!ejected)
            {
                progress.Report(new BurnProgress
                {
                    LogLine = "[Warning] Could not eject disc — please eject manually"
                });
            }
        }

        burnSucceeded = true;

        progress.Report(new BurnProgress
        {
            PercentComplete = 100,
            BytesWritten = totalBytes,
            TotalBytes = totalBytes,
            StatusMessage = job.SimulateOnly ? "Simulation complete." : "Burn complete.",
            LogLine = job.SimulateOnly
                ? "[Info] Simulation completed successfully"
                : $"[Info] Successfully burned {FormatHelper.FormatBytes(totalBytes)}"
        });

        }
        finally
        {
            // Skip cleanup if the drive transport was already disposed (e.g. during
            // the platform-fallback eject path) to avoid ObjectDisposedException.
            if (!driveDisposed)
            {
                RecoverDriveAfterOperation(drive, progress, burnSucceeded);
            }
        }
    }

    /// <summary>Blanks/formats a rewritable disc using native SCSI commands.</summary>
    public async Task BlankDiscAsync(
        string devicePath,
        bool quickBlank,
        string? mediaType,
        IProgress<BurnProgress> progress,
        CancellationToken ct)
    {
        // Native SCSI passthrough is the primary path on all platforms including macOS.
        // No automatic fallback to drutil erase — SCSI must work natively.

        // Report progress immediately so the UI activates before any blocking operations.
        progress.Report(new BurnProgress
        {
            PercentComplete = 0,
            StatusMessage = "Initializing drive...",
            LogLine = $"[Info] Opening device: {devicePath}"
        });

        using var drive = new OpticalDrive(devicePath);

        // Wait for drive to become ready — drives need time to spin up after tray
        // close/load. A single TestUnitReady check fails if media was just inserted.
        progress.Report(new BurnProgress { StatusMessage = "Waiting for drive to become ready..." });
        if (!drive.WaitForDriveReady())
            throw new InvalidOperationException("Drive is not ready. Please ensure a disc is inserted and the tray is closed.");

        // Detect media profile first to determine whether FORMAT UNIT or BLANK is needed.
        // This must happen before the Erasable check because BD-R (write-once) needs FORMAT
        // UNIT for initial formatting but is NOT marked as erasable in the disc info.
        var profile = drive.GetCurrentProfile();
        bool needsFormatUnit = profile is 0x001A or 0x002A or 0x0013 or 0x0043
            or 0x0012 or 0x0052 or 0x0053 or 0x005A or 0x0041 or 0x0042
            or 0x0045 or 0x0046 or 0x004A or 0x004B or 0x004C or 0x004D; // BD-R/RE DL, BDXL, HD DVD-RW/RW DL

        // Only check Erasable for BLANK (CD-RW, DVD-RW sequential) — FORMAT UNIT media
        // like BD-R are write-once but still require FORMAT UNIT for initial formatting.
        if (!needsFormatUnit)
        {
            var discInfo = drive.ReadDiscInfo();
            if (discInfo == null)
            {
                // READ DISC INFO failed — this can happen when the drive has stale state
                // or the media doesn't support READ DISC INFO. For safety, check the profile:
                // only CD-RW (0x000A) and DVD-RW Sequential (0x0014) support BLANK.
                if (profile is not (0x000A or 0x0014))
                    throw new InvalidOperationException(
                        "Could not read disc information and media profile (0x" + profile.ToString("X4") +
                        ") is not a known erasable type. Please check the disc.");
            }
            else if (!discInfo.Erasable)
            {
                throw new InvalidOperationException("Disc is not rewritable/erasable.");
            }
        }

        // Lock the tray during blanking/formatting to prevent accidental ejection
        drive.PreventMediumRemoval(true);

        bool blankSucceeded = false;
        try
        {

        var opName = needsFormatUnit ? "format" : "blank";
        progress.Report(new BurnProgress
        {
            StatusMessage = $"{(needsFormatUnit ? "Formatting" : "Blanking")} disc ({(quickBlank ? "quick" : "full")})...",
            LogLine = $"[Info] Starting {(quickBlank ? "quick" : "full")} {opName} on {devicePath} (profile 0x{profile:X4})"
        });

        ScsiResult result;
        if (needsFormatUnit)
        {
            // Log the format type being attempted for diagnostics
            var formatMethodName = profile switch
            {
                0x0012 or 0x0052 => "FORMAT UNIT (DVD-RAM)",
                0x0043 or 0x0046 or 0x004B or 0x004D => "FORMAT UNIT (BD-RE)",
                0x0041 or 0x0042 or 0x0045 or 0x004A or 0x004C => "FORMAT UNIT (BD-R)",
                0x0053 or 0x005A => "FORMAT UNIT (HD DVD-RW)",
                0x0013 => "FORMAT UNIT (DVD-RW Restricted Overwrite)",
                _ => "FORMAT UNIT (DVD+RW)"
            };
            progress.Report(new BurnProgress
            {
                LogLine = $"[Info] Using {formatMethodName} command"
            });

            // Dispatch to the correct format method based on media profile
            result = profile switch
            {
                0x0012 => drive.FormatDvdRam(quickBlank),
                0x0052 => drive.FormatDvdRam(quickBlank), // HD DVD-RAM uses same FORMAT UNIT structure as DVD-RAM
                0x0043 or 0x0046 or 0x004B or 0x004D => drive.FormatBdRe(quickBlank), // BD-RE, BD-RE DL, BD-RE TL/QL (BDXL)
                0x0041 or 0x0042 or 0x0045 or 0x004A or 0x004C => drive.FormatBdR(quickBlank), // BD-R SRM/RRM, BD-R DL, BD-R TL/QL (BDXL)
                0x0053 or 0x005A => drive.FormatHdDvdRw(quickBlank), // HD DVD-RW, HD DVD-RW DL
                0x0013 => drive.FormatDvdMinusRwRestrictedOverwrite(quickBlank), // DVD-RW Restricted Overwrite
                _ => drive.FormatDvdPlusRw(quickBlank)  // DVD+RW, DVD+RW DL
            };
        }
        else
        {
            result = drive.BlankDisc(quickBlank);

            // If BLANK fails with Illegal Request on DVD-RW Sequential (0x0014),
            // the disc may have been formatted in Restricted Overwrite mode previously.
            // Fall back to FORMAT UNIT which works for both modes.
            if (!result.Success && result.SenseKey == 0x05 && profile == 0x0014)
            {
                progress.Report(new BurnProgress
                {
                    LogLine = "[Info] BLANK rejected — trying FORMAT UNIT (DVD-RW may be in Restricted Overwrite mode)"
                });
                result = drive.FormatDvdMinusRwRestrictedOverwrite(quickBlank);
            }
        }

        if (!result.Success)
            throw new InvalidOperationException($"Blank/format failed: {result.ErrorDescription}");

        // Wait for the operation to complete (it runs in the background with IMM=1)
        await WaitForDriveReadyAsync(drive, progress, ct);

        blankSucceeded = true;

        progress.Report(new BurnProgress
        {
            PercentComplete = 100,
            StatusMessage = "Blank/format complete.",
            LogLine = "[Info] Disc is now blank and ready for writing."
        });
        }
        finally
        {
            RecoverDriveAfterOperation(drive, progress, blankSucceeded);
        }
    }

    /// <summary>
    /// Performs post-operation drive recovery and cleanup.
    /// Ensures the drive is in a usable state after any operation (burn, blank, format),
    /// especially after errors. This eliminates the need for users to manually re-attach
    /// external USB drives after transport errors (e.g., semaphore timeout 121).
    ///
    /// Recovery follows cdrecord/growisofs conventions and the ReadEngine recovery pattern:
    ///   1. TUR draining — clear all pending UNIT ATTENTION conditions (3× per SPC-5)
    ///   2. REQUEST SENSE — log the drive's current sense state for diagnostics
    ///   3. MECHANISM STATUS — detect hardware faults (motor, tray, laser)
    ///   4. SYNCHRONIZE CACHE — flush any partially buffered write data
    ///   5. WaitForDriveReady — ensure drive reaches a stable state (longer timeout on error)
    ///   6. Unlock tray — allow disc removal via PREVENT/ALLOW MEDIUM REMOVAL
    ///
    /// On success, steps 2-5 use abbreviated recovery (no diagnostics, shorter timeout).
    /// On error, full recovery is performed with detailed diagnostics logging.
    /// </summary>
    /// <param name="drive">The optical drive to recover.</param>
    /// <param name="progress">Progress reporter for diagnostic log messages.</param>
    /// <param name="operationSucceeded">Whether the preceding operation completed successfully.</param>
    private static void RecoverDriveAfterOperation(
        OpticalDrive drive,
        IProgress<BurnProgress> progress,
        bool operationSucceeded)
    {
        // Step 1: TUR draining — clear all pending UNIT ATTENTION conditions.
        // After a failed write, drives may queue multiple UA events (e.g., "medium
        // may have changed", "power on/reset occurred"). Each TUR clears one UA.
        // Three TURs covers even aggressive queuing per SPC-5.
        for (int i = 0; i < 3; i++)
        {
            try { drive.TestUnitReady(); } catch { /* best effort */ }
        }

        if (!operationSucceeded)
        {
            // Step 2: REQUEST SENSE diagnostics — log the drive's current error state.
            // This helps diagnose persistent issues (e.g., failing laser, bad media)
            // and matches cdrecord/ImgBurn's detailed error reporting behavior.
            try
            {
                var (senseKey, asc, ascq) = drive.RequestSense();
                if (senseKey != 0x00)
                {
                    var errorDesc = new ScsiResult
                    {
                        Status = 0x02,
                        SenseData = BuildFixedSenseData(senseKey, asc, ascq)
                    }.ErrorDescription;
                    progress.Report(new BurnProgress
                    {
                        LogLine = $"[Info] Post-operation drive sense: {errorDesc}"
                    });
                }
            }
            catch { /* REQUEST SENSE may fail if drive is completely unresponsive */ }

            // Step 3: MECHANISM STATUS diagnostics — detect hardware faults.
            // The Fault bit in MECHANISM STATUS indicates a mechanical failure
            // (jammed tray, motor fault, etc.) that requires user intervention.
            try
            {
                var mechStatus = drive.GetMechanismStatus();
                if (mechStatus != null && mechStatus.Fault)
                {
                    progress.Report(new BurnProgress
                    {
                        LogLine = "[Warning] Drive reports mechanical fault — check drive hardware"
                    });
                }
            }
            catch { /* best effort */ }
        }

        // Step 4: SYNCHRONIZE CACHE — flush any pending writes in the drive buffer.
        // When the burn is cancelled or fails, partially written data may still be
        // in the drive's write buffer. Flushing prevents the buffer from holding
        // stale data that could interfere with subsequent operations.
        try { drive.SynchronizeCache(); } catch { /* best effort */ }

        // Step 5: Wait for drive to reach a ready state.
        // After errors (especially transport/IOCTL failures on USB drives),
        // the drive may need time to reset its firmware state, re-establish
        // communication with the host, or finalize internal error recovery.
        // Without this, the next operation (e.g., opening the drive for a retry)
        // may fail with "Device not ready" or "Semaphore timeout".
        // On success: quick 5s check (drive should already be ready).
        // On error: longer 30s wait to handle USB re-enumeration and firmware recovery.
        try
        {
            var waitMs = operationSucceeded ? 5_000 : 30_000;
            drive.WaitForDriveReady(maxWaitMs: waitMs, pollIntervalMs: 1000);
        }
        catch { /* best effort */ }

        // Step 6: Retry tray unlock — the first attempt may fail if the drive is still busy
        for (int retry = 0; retry < 3; retry++)
        {
            try
            {
                if (drive.PreventMediumRemoval(false))
                    break;
            }
            catch { /* best effort */ }
            if (retry < 2) Thread.Sleep(1000);
        }
    }

    /// <summary>Polls the drive until it becomes ready (TEST UNIT READY succeeds).</summary>
    private static async Task WaitForDriveReadyAsync(
        OpticalDrive drive,
        IProgress<BurnProgress> progress,
        CancellationToken ct)
    {
        await WaitForDriveReadyAsync(drive, progress, ct,
            maxWaitMs: 3_600_000, statusPrefix: "Waiting for drive");
    }

    /// <summary>
    /// Async polling loop that waits for the drive to become ready while reporting
    /// periodic progress updates. Uses Task.Delay instead of Thread.Sleep so the
    /// async context is not blocked and cancellation remains responsive.
    /// </summary>
    /// <param name="drive">The optical drive to poll.</param>
    /// <param name="progress">Progress reporter for UI updates.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="maxWaitMs">Maximum wait time in milliseconds before returning false.</param>
    /// <param name="pollIntervalMs">Interval between TEST UNIT READY polls.</param>
    /// <param name="statusPrefix">Prefix for the status message shown in the UI.</param>
    /// <returns>True if the drive became ready, false if the timeout elapsed.</returns>
    private static async Task<bool> WaitForDriveReadyAsync(
        OpticalDrive drive,
        IProgress<BurnProgress> progress,
        CancellationToken ct,
        int maxWaitMs,
        int pollIntervalMs = 2000,
        string statusPrefix = "Waiting for drive")
    {
        // Quick check — drive may already be ready
        if (drive.TestUnitReady())
            return true;

        var startTime = DateTime.Now;
        var deadline = Environment.TickCount64 + maxWaitMs;
        int pollCount = 0;
        // Calculate how many polls correspond to ~30 seconds for log throttling.
        // Ensure at least 1 to avoid division by zero if pollIntervalMs is very large.
        int logEveryNPolls = Math.Max(1, 30_000 / pollIntervalMs);

        while (Environment.TickCount64 < deadline)
        {
            // Check cancellation once per iteration to produce a single, clean throw
            // instead of letting it bubble out of Task.Delay (which throws
            // TaskCanceledException from System.Private.CoreLib).
            if (ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);

            if (drive.TestUnitReady())
                return true;

            pollCount++;
            var elapsed = DateTime.Now - startTime;

            // Report progress every other poll so the UI stays responsive
            // and the user knows the operation hasn't stalled.
            if (pollCount % 2 == 0)
            {
                progress.Report(new BurnProgress
                {
                    StatusMessage = $"{statusPrefix}... ({elapsed.TotalSeconds:F0}s)",
                    // Add a log line every ~30 seconds to avoid flooding the log
                    LogLine = pollCount % logEveryNPolls == 0
                        ? $"[Info] {statusPrefix}... ({elapsed.TotalSeconds:F0}s elapsed)"
                        : null
                });
            }

            // Use try-catch around Task.Delay to prevent TaskCanceledException
            // from propagating out of System.Private.CoreLib. The cancellation
            // check at the top of the loop will produce a clean single throw.
            try
            {
                await Task.Delay(pollIntervalMs, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Re-throw as a clean OperationCanceledException from our code
                // rather than letting multiple TaskCanceledExceptions escape.
                throw new OperationCanceledException(ct);
            }
        }

        return false;
    }

    /// <summary>Parses write speed string to KB/s value.</summary>
    private static ushort ParseWriteSpeed(string speed, bool isDvd, bool isBd, bool isHdDvd = false)
    {
        if (string.Equals(speed, FormatHelper.SpeedAuto, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(speed, FormatHelper.SpeedMax, StringComparison.OrdinalIgnoreCase))
            return 0xFFFF; // Maximum speed

        var numStr = speed.TrimEnd('x', 'X');
        if (!double.TryParse(numStr, System.Globalization.CultureInfo.InvariantCulture, out var mult))
            return 0xFFFF;

        // Reject negative or zero multipliers
        if (mult <= 0) return 0xFFFF;

        // 1x CD = 150 KB/s, 1x DVD = 1385 KB/s, 1x BD = 4495 KB/s, 1x HD DVD = 4568 KB/s
        var baseSpeed = isBd ? FormatHelper.BdBaseSpeedKBs
            : isHdDvd ? FormatHelper.HdDvdBaseSpeedKBs
            : isDvd ? FormatHelper.DvdBaseSpeedKBs
            : FormatHelper.CdBaseSpeedKBs;
        var speedValue = mult * baseSpeed;
        // Clamp to ushort range to prevent silent truncation
        if (speedValue > ushort.MaxValue) return 0xFFFF; // Max speed
        return (ushort)speedValue;
    }

    /// <summary>
    /// Detects the first track's mode from a CUE file to set matching write parameters.
    /// Returns the track mode, block type, and session format that match the CUE sheet's first track,
    /// or null if the CUE file can't be parsed.
    /// </summary>
    private static (byte TrackMode, byte BlockType, byte SessionFormat)? DetectCueFirstTrackMode(string cuePath)
    {
        if (!File.Exists(cuePath)) return null;

        try
        {
            var lines = File.ReadAllLines(cuePath);
            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                var trackMatch = CueTrackRx.Match(line);
                if (trackMatch.Success)
                {
                    var mode = trackMatch.Groups[2].Value.ToUpperInvariant();
                    // Session format for Mode Page 05h byte 8:
                    // 0x00 = CD-DA / CD-ROM (Audio or Mode 1)
                    // 0x20 = CD-ROM XA (Mode 2, used by PS1/PS2/Sega CD etc.)
                    // 0x10 = CDI
                    return mode switch
                    {
                        "AUDIO"      => (MmcCommands.TrackModeAudio, MmcCommands.BlockTypeRaw2352, (byte)0x00),
                        "MODE1/2048" => (MmcCommands.TrackModeData, MmcCommands.BlockTypeMode1_2048, (byte)0x00),
                        "MODE1/2352" => (MmcCommands.TrackModeData, MmcCommands.BlockTypeRaw2352, (byte)0x00),
                        "MODE2/2048" => (MmcCommands.TrackModeData, MmcCommands.BlockTypeMode2XA_2048, (byte)0x20),
                        "MODE2/2328" => (MmcCommands.TrackModeData, MmcCommands.BlockTypeMode2XA_2328, (byte)0x20),
                        "MODE2/2336" => (MmcCommands.TrackModeData, MmcCommands.BlockTypeMode2_2336, (byte)0x20),
                        "MODE2/2352" => (MmcCommands.TrackModeData, MmcCommands.BlockTypeRaw2352, (byte)0x20),
                        "CDI/2336"   => (MmcCommands.TrackModeData, MmcCommands.BlockTypeMode2_2336, (byte)0x10),
                        "CDI/2352"   => (MmcCommands.TrackModeData, MmcCommands.BlockTypeRaw2352, (byte)0x10),
                        _            => (MmcCommands.TrackModeData, MmcCommands.BlockTypeMode1_2048, (byte)0x00)
                    };
                }
            }
        }
        catch { /* best-effort */ }
        return null;
    }

    /// <summary>
    /// Writes sectors to disc with retry logic for transient SCSI errors.
    /// Retries on recoverable conditions:
    ///   - "Not Ready" (SenseKey 0x02, ASC 0x04): drive busy / long write in progress
    ///   - "Unit Attention" (SenseKey 0x06): transient condition cleared by re-issuing
    ///   - Transport errors (Status 0xFF): IOCTL failures like semaphore timeout (error 121),
    ///     device not ready (error 21), or I/O device error (error 1117). These are common
    ///     on USB-connected drives when the USB bridge firmware stalls or the OS times out.
    ///     Recovery follows the same pattern as ReadEngine: TUR + delay + TUR.
    ///   - Medium Error (SenseKey 0x03) with write error ASC 0x0C: may be transient on
    ///     marginal media. Retried with drive recovery (TUR draining + WaitForDriveReady).
    ///   - Hardware Error (SenseKey 0x04): may be transient on USB drives where the
    ///     bridge temporarily loses communication. Retried with full drive recovery.
    /// </summary>
    private static ScsiResult WriteSectorsWithRetry(
        OpticalDrive drive, uint lba, byte[] data, int sectorSize, int sectorCount,
        int dataLength, bool useDvdBdMode, bool simulate = false)
    {
        ScsiResult? result = null;

        for (int retry = 0; retry <= WriteRetryMax; retry++)
        {
            result = drive.WriteSectors(lba, data, sectorSize, sectorCount, dataLength, useDvdBdMode, simulate);
            if (result.Success)
                return result;

            // Classify the error for recovery strategy
            bool isNotReady = result.SenseKey == 0x02 && result.Asc == 0x04;
            bool isUnitAttention = result.SenseKey == 0x06;
            bool isTransportError = result.Status == 0xFF;
            bool isMediumError = result.SenseKey == 0x03 && result.Asc == 0x0C;
            bool isHardwareError = result.SenseKey == 0x04;

            bool isRecoverable = isNotReady || isUnitAttention || isTransportError ||
                                 isMediumError || isHardwareError;

            if (!isRecoverable || retry == WriteRetryMax)
                return result;

            if (isTransportError || isHardwareError)
            {
                // Transport/hardware errors need aggressive recovery matching ReadEngine:
                // TUR to clear pending state, longer delay for USB bridge/firmware recovery,
                // then TUR again to verify the drive is responsive.
                // Semaphore timeout (error 121) and I/O device error (error 1117) indicate
                // the OS lost communication with the drive — the delay gives the USB bridge
                // or SATA controller time to re-establish the link.
                drive.TestUnitReady();
                Thread.Sleep(Math.Min(2000 * (retry + 1), WriteRetryMaxDelayMs));
                drive.TestUnitReady();
                // WaitForDriveReady polls TUR with fallbacks (GET EVENT STATUS, GET CONFIGURATION)
                // to handle drives that need extra time to recover from transport errors.
                drive.WaitForDriveReady(maxWaitMs: 15_000, pollIntervalMs: 1000);
            }
            else if (isMediumError)
            {
                // Medium errors (write error on marginal media) may be transient if the
                // drive's internal error correction can handle the spot on a re-attempt.
                // Per MMC-5, drives perform OPC and write strategy adjustments internally.
                // Give the drive time to recalibrate before retrying.
                drive.TestUnitReady();
                Thread.Sleep(Math.Min(1000 * (retry + 1), WriteRetryMaxDelayMs));
                drive.TestUnitReady();
            }
            else
            {
                // Not Ready / Unit Attention: standard backoff + TUR to clear the condition
                Thread.Sleep(Math.Min(WriteRetryBaseDelayMs * (retry + 1), WriteRetryMaxDelayMs));
                drive.TestUnitReady();
            }
        }

        return result!;
    }

    /// <summary>Parses write mode string to SCSI write parameters.</summary>
    private static (byte WriteType, byte TrackMode, byte BlockType) ParseWriteMode(string mode) => mode switch
    {
        "TAO (Track At Once)" => (MmcCommands.WriteTypeTao, MmcCommands.TrackModeData, MmcCommands.BlockTypeMode1_2048),
        "DAO/SAO (Disc At Once)" => (MmcCommands.WriteTypeSao, MmcCommands.TrackModeData, MmcCommands.BlockTypeMode1_2048),
        // Alternate display strings used by Video/Audio/Game wizard combo boxes
        "DAO (Disc At Once)" => (MmcCommands.WriteTypeSao, MmcCommands.TrackModeData, MmcCommands.BlockTypeMode1_2048),
        "SAO (Session At Once)" => (MmcCommands.WriteTypeSao, MmcCommands.TrackModeData, MmcCommands.BlockTypeMode1_2048),
        "RAW96R" => (MmcCommands.WriteTypeRaw, MmcCommands.TrackModeData, MmcCommands.BlockTypeRawPW2448_R),
        "RAW16" => (MmcCommands.WriteTypeRaw, MmcCommands.TrackModeData, MmcCommands.BlockTypeRawPQ2368),
        "RAW96P" => (MmcCommands.WriteTypeRaw, MmcCommands.TrackModeData, MmcCommands.BlockTypeRawPW2448),
        "Incremental" => (MmcCommands.WriteTypePacket, MmcCommands.TrackModeDataInc, MmcCommands.BlockTypeMode1_2048),
        _ => (MmcCommands.WriteTypeTao, MmcCommands.TrackModeData, MmcCommands.BlockTypeMode1_2048)
    };

    /// <summary>
    /// Builds a minimal CUE sheet for SAO/DAO writing of a single data track.
    /// CUE sheet entries are 8 bytes each per MMC-3 specification (Table 480).
    /// Format: [CTL/ADR, Track#, Index#, DataForm, SCMS, Min, Sec, Frame]
    /// The CUE sheet describes the complete session layout including lead-in,
    /// track data, and lead-out.
    /// </summary>
    private static byte[] BuildSaoCueSheet(byte blockType, byte trackMode, int sectorSize, long imageLength)
    {
        // Determine CUE sheet Data Form from block type and track mode.
        // Per MMC-5 Table 564, Data Form field values:
        //   0x00 = CD-DA (audio, no pre-emphasis)
        //   0x01 = CD-DA (audio, with pre-emphasis)
        //   0x10 = CD-ROM Mode 1 (user data only, 2048 bytes)
        //   0x11 = CD-ROM Mode 1 (all 2352 bytes, raw)
        //   0x12 = CD-ROM Mode 1 (raw + P-Q subchannel)
        //   0x13 = CD-ROM Mode 1 (raw + P-W subchannel)
        //   0x20 = CD-ROM XA Mode 2 (user data, formless 2336 bytes)
        //   0x21 = CD-ROM XA Mode 2 (all 2352 bytes, raw)
        bool isAudio = (trackMode & 0x04) == 0;
        byte dataForm = blockType switch
        {
            MmcCommands.BlockTypeMode1_2048 => 0x10,    // Mode 1 cooked (2048)
            MmcCommands.BlockTypeRaw2352 when isAudio => 0x00,  // CD-DA audio (2352)
            MmcCommands.BlockTypeRaw2352 => 0x11,       // Mode 1 raw (2352)
            MmcCommands.BlockTypeRawPQ2368 when isAudio => 0x02,  // CD-DA + P-Q sub
            MmcCommands.BlockTypeRawPQ2368 => 0x12,     // Mode 1 raw + P-Q sub
            MmcCommands.BlockTypeRawPW2448 when isAudio => 0x03,  // CD-DA + P-W sub
            MmcCommands.BlockTypeRawPW2448 => 0x13,     // Mode 1 raw + P-W sub
            MmcCommands.BlockTypeRawPW2448_R when isAudio => 0x03, // CD-DA + P-W sub (raw)
            MmcCommands.BlockTypeRawPW2448_R => 0x13,   // Mode 1 raw + P-W sub (raw)
            MmcCommands.BlockTypeMode2_2336 => 0x20,    // Mode 2 formless (2336)
            MmcCommands.BlockTypeMode2XA_2048 => 0x20,  // Mode 2 XA (2048)
            MmcCommands.BlockTypeMode2XA_2328 => 0x20,  // Mode 2 XA Form 1 (2328)
            MmcCommands.BlockTypeMode2XA_2332 => 0x20,  // Mode 2 XA Form 2 (2332)
            _ => 0x10                                    // Default: Mode 1
        };

        // CTL nibble for the track (bits 7-4 of byte 0)
        // Per MMC-3 Table 480, CTL field uses Q-channel control bits:
        //   bit 6: 0 = audio (2-channel), 1 = data track
        //   bit 5: digital copy permitted
        //   bit 4: pre-emphasis (audio) or incremental (data)
        // Track mode bit 2 (0x04) indicates data vs audio per write parameters page
        byte ctl = (trackMode & 0x04) != 0 ? (byte)0x40 : (byte)0x00;

        // Calculate total sectors for lead-out position
        var totalSectors = (int)((imageLength + sectorSize - 1) / sectorSize);
        // Standard pregap is 150 frames (2 seconds)
        const int pregapFrames = 150;

        // Lead-out position = pregap + data sectors
        var leadOutLba = pregapFrames + totalSectors;
        // Validate lead-out fits in MSF range (max 99:59:74 = 449,999 frames).
        // CDs longer than ~99 minutes cannot be represented in MSF format.
        if (leadOutLba > 449_999)
        {
            throw new InvalidOperationException(
                $"Disc content exceeds maximum MSF address (99:59:74). " +
                $"Lead-out at LBA {leadOutLba} ({leadOutLba / 75 / 60} minutes). " +
                $"Reduce content size or use TAO mode instead of SAO.");
        }
        var loMin = leadOutLba / 75 / 60;
        var loSec = (leadOutLba / 75) % 60;
        var loFrm = leadOutLba % 75;

        // Build CUE sheet: lead-in, pregap, data start, lead-out = 4 entries × 8 bytes
        var cue = new byte[4 * 8];

        // Entry 0: Lead-in (track 0, index 0)
        cue[0] = (byte)(ctl | 0x01);   // CTL/ADR (ADR=1 for position)
        cue[1] = 0x00;                 // Track 0 (lead-in)
        cue[2] = 0x00;                 // Index 0
        cue[3] = dataForm;             // Data form
        cue[4] = 0x00;                 // SCMS
        cue[5] = 0x00;                 // Min
        cue[6] = 0x00;                 // Sec
        cue[7] = 0x00;                 // Frame

        // Entry 1: Track 1, Index 0 (pregap)
        cue[8]  = (byte)(ctl | 0x01);
        cue[9]  = 0x01;                // Track 1
        cue[10] = 0x00;                // Index 0 (pregap)
        cue[11] = dataForm;
        cue[12] = 0x00;
        cue[13] = 0x00;                // 00:00:00
        cue[14] = 0x00;
        cue[15] = 0x00;

        // Entry 2: Track 1, Index 1 (data start at 00:02:00 = 150 frames)
        cue[16] = (byte)(ctl | 0x01);
        cue[17] = 0x01;                // Track 1
        cue[18] = 0x01;                // Index 1 (data start)
        cue[19] = dataForm;
        cue[20] = 0x00;
        cue[21] = 0x00;                // Min = 0
        cue[22] = 0x02;                // Sec = 2 (150 frames = 2 seconds)
        cue[23] = 0x00;                // Frame = 0

        // Entry 3: Lead-out (track 0xAA)
        cue[24] = (byte)(ctl | 0x01);
        cue[25] = 0xAA;                // Lead-out marker
        cue[26] = 0x01;                // Index 1
        cue[27] = dataForm;
        cue[28] = 0x00;
        cue[29] = (byte)loMin;
        cue[30] = (byte)loSec;
        cue[31] = (byte)loFrm;

        return cue;
    }

    /// <summary>
    /// Builds a CUE sheet for SAO/DAO writing of audio/data tracks from a BIN/CUE image.
    /// Parses the text CUE sheet file to generate MMC CUE sheet entries.
    /// Supports both single-file CUE sheets (one BIN for all tracks) and multi-file
    /// CUE sheets (one BIN per track, as used in PS1/Sega CD images).
    /// For multi-file CUEs, per-file relative MSF addresses are converted to
    /// session-absolute MSF addresses required by the MMC SEND CUE SHEET command.
    /// </summary>
    /// <param name="cuePath">Path to the .cue text file.</param>
    /// <returns>MMC-format CUE sheet byte array, or null if parsing fails.</returns>
    private static byte[]? BuildSaoAudioCueSheet(string cuePath)
    {
        if (!File.Exists(cuePath)) return null;

        try
        {
            var lines = File.ReadAllLines(cuePath);
            var entries = new System.Collections.Generic.List<byte[]>();
            var cueDir = Path.GetDirectoryName(cuePath) ?? ".";

            byte currentTrackNumber = 0;
            byte currentDataForm = 0x00; // 0x00 = audio
            byte currentCtl = 0x00;      // 0x00 = audio

            // Multi-file CUE support: track all BIN files and their sizes.
            // For multi-file CUE sheets, each FILE directive starts a new file with
            // MSF addresses relative to that file. We need to convert to session-absolute MSF.
            var allBinFiles = new System.Collections.Generic.List<string>();
            long totalBinBytes = 0;
            // Cumulative sector offset for the current file (used to convert file-relative
            // MSF to session-absolute MSF in multi-file CUE sheets)
            long currentFileOffsetFrames = 0;
            // Whether this is a multi-file CUE (more than one FILE directive)
            bool isMultiFile = false;

            // Pre-scan: count FILE directives, collect BIN file paths,
            // and determine per-file sector sizes for accurate offset calculation.
            int fileScanIndex = -1;
            var fileSectorSizes = new System.Collections.Generic.List<int>();
            var fileSectorSizeSet = new System.Collections.Generic.List<bool>();
            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                var fileMatch = CueFileRx.Match(line);
                if (fileMatch.Success)
                {
                    fileScanIndex++;
                    var binName = fileMatch.Groups[1].Value;
                    var candidate = Path.Combine(cueDir, binName);
                    // Always add the path — for multi-file CUEs the BurnService
                    // will have already concatenated the files, so individual existence
                    // is only needed for size calculation. Use 0 size for missing files.
                    allBinFiles.Add(candidate);
                    if (File.Exists(candidate))
                        totalBinBytes += new FileInfo(candidate).Length;
                    fileSectorSizes.Add(2352); // default, updated from first TRACK in this FILE
                    fileSectorSizeSet.Add(false);
                }
                else
                {
                    // Determine per-file sector size from the first TRACK in each FILE block
                    var tm = CueTrackRx.Match(line);
                    if (tm.Success && fileScanIndex >= 0 && !fileSectorSizeSet[fileScanIndex])
                    {
                        var mode = tm.Groups[2].Value.ToUpperInvariant();
                        fileSectorSizes[fileScanIndex] = GetSectorSizeForCueMode(mode);
                        fileSectorSizeSet[fileScanIndex] = true;
                    }
                }
            }
            isMultiFile = allBinFiles.Count > 1;

            // Determine sector size from the first track mode encountered.
            // Default to 2352 (raw/audio), refined when we see the first TRACK directive.
            int sectorSize = 2352;
            bool sectorSizeDetermined = false;

            int currentFileIndex = -1;

            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();

                // Parse FILE directive
                var fileMatch = CueFileRx.Match(line);
                if (fileMatch.Success)
                {
                    currentFileIndex++;

                    // For multi-file CUE: calculate the cumulative frame offset
                    // based on the sizes of all previous files, using each file's
                    // own sector size for accurate frame count calculation.
                    if (isMultiFile && currentFileIndex > 0)
                    {
                        long cumulativeFrames = 0;
                        for (int i = 0; i < currentFileIndex; i++)
                        {
                            if (i < allBinFiles.Count && File.Exists(allBinFiles[i]))
                            {
                                var fileSectorSize = i < fileSectorSizes.Count
                                    ? fileSectorSizes[i] : 2352;
                                cumulativeFrames += new FileInfo(allBinFiles[i]).Length
                                    / fileSectorSize;
                            }
                        }
                        currentFileOffsetFrames = cumulativeFrames;
                    }
                    continue;
                }

                // Parse TRACK
                var trackMatch = CueTrackRx.Match(line);
                if (trackMatch.Success)
                {
                    currentTrackNumber = byte.Parse(trackMatch.Groups[1].Value);
                    var mode = trackMatch.Groups[2].Value.ToUpperInvariant();
                    (currentDataForm, currentCtl) = mode switch
                    {
                        "AUDIO" => ((byte)0x00, (byte)0x00),
                        "MODE1/2048" => ((byte)0x10, (byte)0x40),
                        "MODE1/2352" => ((byte)0x11, (byte)0x40),
                        "MODE2/2048" => ((byte)0x20, (byte)0x40),
                        "MODE2/2328" => ((byte)0x20, (byte)0x40),
                        "MODE2/2336" => ((byte)0x20, (byte)0x40),
                        "MODE2/2352" => ((byte)0x21, (byte)0x40),
                        "CDI/2336" => ((byte)0x20, (byte)0x40),
                        "CDI/2352" => ((byte)0x21, (byte)0x40),
                        _ => ((byte)0x10, (byte)0x40)
                    };

                    // Determine sector size from the first track's data form
                    if (!sectorSizeDetermined)
                    {
                        sectorSize = currentDataForm switch
                        {
                            0x00 => 2352,  // Audio
                            0x10 => 2048,  // Mode 1 cooked
                            0x11 => 2352,  // Mode 1 raw
                            0x20 => 2336,  // Mode 2
                            0x21 => 2352,  // Mode 2 raw
                            _ => 2352
                        };
                        sectorSizeDetermined = true;
                    }
                    continue;
                }

                // Parse INDEX
                var indexMatch = CueIndexRx.Match(line);
                if (indexMatch.Success && currentTrackNumber > 0)
                {
                    var indexNum = byte.Parse(indexMatch.Groups[1].Value);
                    var min = (int)byte.Parse(indexMatch.Groups[2].Value);
                    var sec = (int)byte.Parse(indexMatch.Groups[3].Value);
                    var frm = (int)byte.Parse(indexMatch.Groups[4].Value);

                    // Convert CUE-file MSF to session-absolute MSF for the MMC CUE sheet.
                    // CUE file MSF addresses are relative to the BIN data start.
                    // MMC CUE sheet MSF addresses are absolute within the session,
                    // which includes the standard 150-frame (2-second) pregap before data.
                    // Therefore, all CUE file MSF addresses must be offset by 150 frames.
                    long relativeFrames = (long)min * 60 * 75 + (long)sec * 75 + frm;

                    // For multi-file CUE sheets, also add cumulative file size offsets.
                    // Each FILE directive resets MSF to 00:00:00 in the CUE text file,
                    // but the MMC CUE sheet needs monotonically increasing absolute MSF.
                    if (isMultiFile)
                    {
                        relativeFrames += currentFileOffsetFrames;
                    }

                    // Add the standard 150-frame pregap offset.
                    // Per Red Book / MMC, the first data sector of a CD session starts
                    // at MSF 00:02:00 (150 frames), so all data addresses shift by 150.
                    // INDEX 00 entries (pregap) for track 1 at CUE position 00:00:00
                    // remain at 00:00:00 in absolute MSF (before the pregap offset).
                    // Only INDEX 01+ (actual data positions) need the pregap offset.
                    // However, INDEX 00 for tracks > 1 should also be offset since they
                    // are within the data area.
                    bool isFirstTrackPregapIndex = (currentTrackNumber == 1 && indexNum == 0);
                    if (!isFirstTrackPregapIndex)
                    {
                        relativeFrames += 150;
                    }

                    min = (int)(relativeFrames / 75 / 60);
                    sec = (int)((relativeFrames / 75) % 60);
                    frm = (int)(relativeFrames % 75);

                    var entry = new byte[8];
                    entry[0] = (byte)(currentCtl | 0x01); // CTL/ADR
                    entry[1] = currentTrackNumber;
                    entry[2] = indexNum;
                    entry[3] = currentDataForm;
                    entry[4] = 0x00; // SCMS
                    entry[5] = (byte)min;
                    entry[6] = (byte)sec;
                    entry[7] = (byte)frm;
                    entries.Add(entry);
                }
            }

            if (entries.Count == 0) return null;

            // Ensure track 1 has a pregap (INDEX 00) entry before INDEX 01.
            // Per MMC-3, SAO/DAO mode requires a pregap entry for the first track.
            // If the CUE file only has INDEX 01 for track 1 (which is common),
            // insert an INDEX 00 at 00:00:00 to provide the standard 2-second pregap.
            if (entries.Count > 0 && entries[0][1] == 0x01 && entries[0][2] == 0x01)
            {
                // First entry is track 1 INDEX 01 — insert INDEX 00 before it
                var pregap = new byte[8];
                pregap[0] = entries[0][0]; // Same CTL/ADR
                pregap[1] = 0x01;          // Track 1
                pregap[2] = 0x00;          // Index 0 (pregap)
                pregap[3] = entries[0][3]; // Same data form
                pregap[4] = 0x00;          // SCMS
                pregap[5] = 0x00;          // Min = 0
                pregap[6] = 0x00;          // Sec = 0
                pregap[7] = 0x00;          // Frame = 0
                entries.Insert(0, pregap);
            }

            // Prepend lead-in entry
            var leadIn = new byte[8];
            leadIn[0] = (byte)(entries[0][0]); // Same CTL/ADR as first track
            leadIn[1] = 0x00; // Track 0
            leadIn[2] = 0x00; // Index 0
            leadIn[3] = entries[0][3]; // Same data form
            entries.Insert(0, leadIn);

            // Append lead-out entry using the last track's data form.
            // Calculate lead-out position from total BIN data size + pregap for accurate MSF.
            // For multi-file CUEs, use per-file sector sizes for correct frame calculation.
            // For single-file CUEs, use the single BIN file size with the first track's sector size.
            // Many drives reject CUE sheets where the lead-out MSF is 00:00:00
            // or does not match the actual data length.
            var lastEntry = entries[^1];
            const int pregapFrames = 150;
            long totalDataSectors;
            if (isMultiFile && fileSectorSizes.Count > 0)
            {
                // Multi-file: sum sectors using each file's own sector size
                totalDataSectors = 0;
                for (int i = 0; i < allBinFiles.Count; i++)
                {
                    if (File.Exists(allBinFiles[i]))
                    {
                        var fs = i < fileSectorSizes.Count ? fileSectorSizes[i] : 2352;
                        totalDataSectors += new FileInfo(allBinFiles[i]).Length / fs;
                    }
                }
            }
            else
            {
                // Single-file: use total bytes / first track sector size
                long totalDataBytes = allBinFiles.Count > 0 && File.Exists(allBinFiles[0])
                    ? new FileInfo(allBinFiles[0]).Length : 0;
                totalDataSectors = totalDataBytes / sectorSize;
            }
            // Lead-out = pregap (150 frames) + total data frames.
            // This matches the single-track CUE builder which uses pregapFrames + totalSectors.
            long leadOutLba = pregapFrames + totalDataSectors;

            var loMin = (int)(leadOutLba / 75 / 60);
            var loSec = (int)((leadOutLba / 75) % 60);
            var loFrm = (int)(leadOutLba % 75);

            if (loMin > 99)
            {
                throw new InvalidOperationException(
                    $"Disc content exceeds maximum MSF address (99:59:74). " +
                    $"Lead-out at {loMin}:{loSec:D2}:{loFrm:D2}. Reduce content size.");
            }

            var leadOut = new byte[8];
            leadOut[0] = (byte)(lastEntry[0]); // Same CTL/ADR
            leadOut[1] = 0xAA; // Lead-out marker
            leadOut[2] = 0x01; // Index 1
            leadOut[3] = lastEntry[3]; // Same data form
            leadOut[4] = 0x00; // SCMS
            leadOut[5] = (byte)loMin;
            leadOut[6] = (byte)loSec;
            leadOut[7] = (byte)loFrm;
            entries.Add(leadOut);

            // Flatten to byte array
            var result = new byte[entries.Count * 8];
            for (int i = 0; i < entries.Count; i++)
                entries[i].CopyTo(result, i * 8);
            return result;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Determines if a CUE sheet references multiple BIN files (multi-file CUE).
    /// </summary>
    /// <param name="cuePath">Path to the .cue file.</param>
    /// <returns>True if the CUE sheet has more than one FILE directive.</returns>
    internal static bool IsMultiFileCue(string cuePath)
    {
        if (!File.Exists(cuePath)) return false;
        try
        {
            int fileCount = 0;
            foreach (var rawLine in File.ReadLines(cuePath))
            {
                var line = rawLine.Trim();

                // Skip REM comments (consistent with GetCueBinFiles / ParseCueTrackInfos)
                if (Services.VerifyService.IsCueRemComment(line))
                    continue;

                if (CueFileRx.IsMatch(line))
                {
                    fileCount++;
                    if (fileCount > 1) return true;
                }
            }
        }
        catch { /* best effort */ }
        return false;
    }

    /// <summary>
    /// Gets the ordered list of BIN file paths referenced by a CUE sheet.
    /// Resolves relative paths against the CUE file's directory.
    /// </summary>
    /// <param name="cuePath">Path to the .cue file.</param>
    /// <returns>List of resolved BIN file paths in order.</returns>
    internal static System.Collections.Generic.List<string> GetCueBinFiles(string cuePath)
    {
        var binFiles = new System.Collections.Generic.List<string>();
        if (!File.Exists(cuePath)) return binFiles;

        var cueDir = Path.GetDirectoryName(cuePath) ?? ".";
        try
        {
            foreach (var rawLine in File.ReadLines(cuePath))
            {
                var line = rawLine.Trim();

                // Skip REM comments — they may contain FILE keywords that would
                // cause false matches (e.g., "REM ORIGINAL FILE ...").
                // Must be consistent with ParseCueTrackInfos to keep file
                // indices aligned between the two methods.
                if (Services.VerifyService.IsCueRemComment(line))
                    continue;

                var fileMatch = CueFileRx.Match(line);
                if (fileMatch.Success)
                {
                    var binName = fileMatch.Groups[1].Value;
                    var candidate = Path.Combine(cueDir, binName);
                    binFiles.Add(candidate);
                }
            }
        }
        catch { /* best effort */ }
        return binFiles;
    }

    /// <summary>
    /// Gets the sector size in bytes for a CUE track mode string.
    /// Delegates to <see cref="Toc.TocCueConverter.GetSectorSizeForCueMode"/> to avoid duplication.
    /// </summary>
    private static int GetSectorSizeForCueMode(string cueMode)
        => Toc.TocCueConverter.GetSectorSizeForCueMode(cueMode);

    /// <summary>
    /// Verifies burned data by reading sectors back from the disc and comparing with the source image.
    /// Handles both cooked (2048-byte) and raw (2352+ byte) sector modes by comparing only
    /// the user data portion of each sector.
    /// </summary>
    private static async Task<bool> VerifyBurnAsync(
        OpticalDrive drive, string imagePath, uint startLba, uint totalSectors,
        int sectorSize, bool useDvdBdMode, long imageStartOffset,
        IProgress<BurnProgress> progress, CancellationToken ct)
    {
        const int sectorsPerRead = 32;
        // For verification, we always read back as 2048-byte cooked sectors via READ(10).
        // If the image was burned with raw sectors (2352+), we extract the user data
        // portion from each raw sector in the image file for comparison.
        const int cookedSectorSize = 2048;
        bool isRawImage = sectorSize > cookedSectorSize;

        await using var imageStream = new FileStream(
            imagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);

        // When burning CDI/MDF/IMG files directly, sector data starts at a non-zero
        // offset within the file (set by ConvertToBurnableFormatAsync). The verify
        // must read from the same offset to compare against the correct data.
        if (imageStartOffset > 0)
        {
            if (imageStartOffset >= imageStream.Length)
            {
                progress.Report(new BurnProgress
                {
                    LogLine = $"[Warning] Verify: image start offset ({imageStartOffset}) exceeds " +
                              $"file size ({imageStream.Length}) — cannot verify"
                });
                return false;
            }
            imageStream.Seek(imageStartOffset, SeekOrigin.Begin);
        }

        var imageBuffer = new byte[sectorsPerRead * sectorSize];
        var imageUserData = isRawImage ? new byte[sectorsPerRead * cookedSectorSize] : null;
        uint currentLba = startLba;
        long bytesVerified = 0;
        var totalBytes = imageStream.Length - imageStartOffset;
        // Use long arithmetic to prevent uint overflow when startLba + totalSectors > uint.MaxValue
        long endLba = (long)startLba + totalSectors;

        while (currentLba < endLba)
        {
            if (ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);

            var remaining = endLba - currentLba;
            var count = (ushort)Math.Min(sectorsPerRead, remaining);

            // Read from image file
            int imageBytes;
            try
            {
                imageBytes = await imageStream.ReadAsync(imageBuffer.AsMemory(0, count * sectorSize), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }
            if (imageBytes <= 0)
            {
                // Image file ended before all sectors were verified.
                // This happens when the image was padded to sector boundary during burn.
                // Remaining sectors are padding — verification is complete.
                break;
            }

            // For raw images, extract user data (2048 bytes) from each raw sector.
            // Mode 1: user data at offset 16 (12 sync + 4 header)
            // Mode 2 Form 1: user data at offset 24 (12 sync + 4 header + 8 subheader)
            // Audio sectors: no sync pattern, skip for user-data comparison
            var compareBuffer = imageBuffer;
            int compareBytes = imageBytes;
            int compareSectorSize = sectorSize;
            if (isRawImage && imageUserData != null)
            {
                int sectorsInRead = imageBytes / sectorSize;
                int userDataWritten = 0;
                for (int s = 0; s < sectorsInRead; s++)
                {
                    int rawOffset = s * sectorSize;
                    // Detect sync pattern to determine sector type
                    bool hasSyncPattern = imageBuffer[rawOffset] == 0x00 && imageBuffer[rawOffset + 11] == 0x00;
                    if (hasSyncPattern)
                    {
                        for (int si = 1; si <= 10 && hasSyncPattern; si++)
                            hasSyncPattern = imageBuffer[rawOffset + si] == 0xFF;
                    }

                    int userDataOffset;
                    if (hasSyncPattern)
                    {
                        byte mode = imageBuffer[rawOffset + 15];
                        userDataOffset = mode == 2 ? 24 : 16;
                    }
                    else
                    {
                        // Audio sector — no user data to compare against cooked read
                        // Fill with zeros so comparison passes (audio sectors aren't
                        // returned by READ(10))
                        Array.Clear(imageUserData, userDataWritten, cookedSectorSize);
                        userDataWritten += cookedSectorSize;
                        continue;
                    }

                    Array.Copy(imageBuffer, rawOffset + userDataOffset, imageUserData, userDataWritten, cookedSectorSize);
                    userDataWritten += cookedSectorSize;
                }
                compareBuffer = imageUserData;
                compareBytes = userDataWritten;
                compareSectorSize = cookedSectorSize;
            }

            // Read from disc (always as cooked 2048-byte sectors)
            // Use READ(12) for DVD/BD media for compatibility with drives that
            // prefer 32-bit transfer length commands for non-CD media.
            var (result, data) = drive.ReadSectors(currentLba, count, cookedSectorSize, useDvdBdMode);
            long expectedDiscBytes = isRawImage ? compareBytes : imageBytes;
            if (!result.Success || result.DataTransferred < expectedDiscBytes || data.Length < expectedDiscBytes)
            {
                progress.Report(new BurnProgress
                {
                    LogLine = $"[Warning] Verify: read failed at LBA {currentLba}"
                });
                return false;
            }

            // Compare
            for (int i = 0; i < expectedDiscBytes; i++)
            {
                if (compareBuffer[i] != data[i])
                {
                    var sector = currentLba + (uint)(i / compareSectorSize);
                    var byteInSector = i % compareSectorSize;
                    progress.Report(new BurnProgress
                    {
                        LogLine = $"[Warning] Verify: mismatch at LBA {sector}, byte {byteInSector} in sector (buffer offset {i})"
                    });
                    return false;
                }
            }

            currentLba += count;
            bytesVerified += imageBytes;

            // Report progress every ~1000 sectors or at end
            if (currentLba % 1000 < (uint)sectorsPerRead || currentLba >= endLba)
            {
                var pct = totalBytes > 0
                    ? (int)(bytesVerified * 100 / totalBytes)
                    : 0;
                progress.Report(new BurnProgress
                {
                    PercentComplete = Math.Min(pct, 99),
                    StatusMessage = $"Verifying: {bytesVerified / 1_048_576} MB...",
                    LogLine = $"[Info] Verify: {bytesVerified / 1_048_576} MB verified"
                });
            }
        }

        return true;
    }

    /// <summary>
    /// Builds a minimal fixed-format (0x70) sense data buffer from individual sense fields.
    /// Used to format REQUEST SENSE results into a ScsiResult for error description decoding.
    /// Fixed-format layout per SPC-5 §4.5.3:
    ///   Byte 0: Response Code (0x70 = current, fixed-format)
    ///   Byte 2: Sense Key (bits 3:0)
    ///   Byte 12: ASC
    ///   Byte 13: ASCQ
    /// </summary>
    private static byte[] BuildFixedSenseData(byte senseKey, byte asc, byte ascq)
    {
        var sense = new byte[14];
        sense[0] = 0x70; // Fixed-format, current errors
        sense[2] = senseKey;
        sense[12] = asc;
        sense[13] = ascq;
        return sense;
    }

}
