// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenBurningSuite.Helpers;
using OpenBurningSuite.Models;
using OpenBurningSuite.Native.Iso;
using OpenBurningSuite.Native.Optical;
using OpenBurningSuite.Native.Toc;

namespace OpenBurningSuite.Services;

/// <summary>Executes disc burning operations using native SCSI/MMC commands.</summary>
public class BurnService
{
    private readonly BurnEngine _engine = new();
    private readonly IsoBuilder _isoBuilder = new();

    public BurnService() { }

    public async Task BurnAsync(
        BurnJob job,
        IProgress<BurnProgress> progress,
        CancellationToken cancellationToken)
    {
        job.Status = BurnStatus.Running;
        job.StartedAt = DateTime.Now;

        string? tempBinPath = null;
        string? tempCuePath = null;
        string? originalImagePath = null;
        string? originalCuePath = null;
        string? tempOnTheFlyIso = null;
        string? tempPatchedImage = null;

        try
        {
            // -----------------------------------------------------------------
            // On-the-fly mode: build a temporary ISO from files/folders, then burn
            // -----------------------------------------------------------------
            if (job.OnTheFly)
            {
                tempOnTheFlyIso = await BuildOnTheFlyImageAsync(job, progress, cancellationToken);
                originalImagePath = job.ImagePath;
                job.ImagePath = tempOnTheFlyIso;
            }
            else
            {
                // Detect image format and convert to a burnable format if needed.
                // BurnEngine natively handles ISO (2048-byte sectors) and BIN/CUE
                // (raw 2352-byte sectors). All other formats must be converted first.
                var ext = Path.GetExtension(job.ImagePath).ToUpperInvariant();
                if (ext is ".MDS" or ".MDF" or ".CDI" or ".CCD" or ".NRG")
                {
                    (tempBinPath, tempCuePath) = await ConvertToBurnableFormatAsync(
                        job, ext, progress, cancellationToken);

                    if (tempBinPath != null)
                    {
                        originalImagePath = job.ImagePath;
                        originalCuePath = job.CuePath;
                        job.ImagePath = tempBinPath;
                        if (tempCuePath != null)
                            job.CuePath = tempCuePath;
                    }
                }
                else if (ext == ".BIN")
                {
                    // Auto-detect companion .cue file for BIN/CUE image burning.
                    // BIN/CUE pairs are the standard format for raw CD images.
                    // The CUE sheet is needed for multi-track SAO/DAO writing.
                    if (string.IsNullOrWhiteSpace(job.CuePath))
                    {
                        var binDir = Path.GetDirectoryName(job.ImagePath) ?? ".";
                        var binBaseName = Path.GetFileNameWithoutExtension(job.ImagePath);

                        // Try exact name match first (e.g., game.bin → game.cue)
                        var cuePath = Directory.EnumerateFiles(binDir, binBaseName + ".cue",
                            new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive })
                            .FirstOrDefault();

                        // If no exact match, search for any CUE file in the directory
                        // that references this BIN file. This handles multi-file CUE sheets
                        // where the BIN file name doesn't match the CUE file name
                        // (e.g., "Game (Track 01).bin" referenced by "Game.cue").
                        if (cuePath == null)
                        {
                            var binFileName = Path.GetFileName(job.ImagePath);
                            foreach (var candidateCue in Directory.EnumerateFiles(binDir, "*.cue",
                                new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive }))
                            {
                                try
                                {
                                    // Parse FILE directives from the CUE to avoid false positives
                                    // (e.g., "track1.bin" matching "track10.bin" with Contains).
                                    var cueBinFiles = BurnEngine.GetCueBinFiles(candidateCue);
                                    if (cueBinFiles.Any(f =>
                                        string.Equals(Path.GetFileName(f), binFileName,
                                            StringComparison.OrdinalIgnoreCase)))
                                    {
                                        cuePath = candidateCue;
                                        break;
                                    }
                                }
                                catch { /* skip unreadable files */ }
                            }
                        }

                        if (cuePath != null && File.Exists(cuePath))
                        {
                            job.CuePath = cuePath;
                            progress.Report(new BurnProgress
                            {
                                LogLine = $"[Info] Auto-detected companion CUE sheet: {Path.GetFileName(cuePath)}"
                            });

                            // For multi-file CUE sheets, concatenate all BIN files
                            if (BurnEngine.IsMultiFileCue(cuePath))
                            {
                                var cueBinFiles = BurnEngine.GetCueBinFiles(cuePath);
                                if (cueBinFiles.Count > 1 && cueBinFiles.All(File.Exists))
                                {
                                    tempBinPath = Path.Combine(Path.GetTempPath(),
                                        $"obs_concat_{Guid.NewGuid():N}.bin");
                                    await using (var outStream = new FileStream(tempBinPath,
                                        FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
                                    {
                                        foreach (var binFile in cueBinFiles)
                                        {
                                            await using var inStream = new FileStream(binFile,
                                                FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                                            await inStream.CopyToAsync(outStream, cancellationToken);
                                        }
                                    }
                                    originalImagePath = job.ImagePath;
                                    job.ImagePath = tempBinPath;
                                    progress.Report(new BurnProgress
                                    {
                                        LogLine = $"[Info] Concatenated {cueBinFiles.Count} BIN files for multi-file CUE"
                                    });
                                }
                            }
                        }
                    }
                }
                else if (ext == ".IMG")
                {
                    // Standalone .img may be a CCD companion or a raw sector dump.
                    // Search for a companion .ccd file using case-insensitive enumeration
                    // to handle case-sensitive filesystems like Linux ext4.
                    var imgDir = Path.GetDirectoryName(job.ImagePath) ?? ".";
                    var imgBaseName = Path.GetFileNameWithoutExtension(job.ImagePath);
                    var ccdPath = Directory.EnumerateFiles(imgDir, imgBaseName + ".ccd",
                        new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive })
                        .FirstOrDefault();
                    if (ccdPath != null && File.Exists(ccdPath))
                    {
                        // CCD/IMG pair found — use direct burn (no data copy)
                        (tempBinPath, tempCuePath) = await ConvertToBurnableFormatAsync(
                            job, ".CCD", progress, cancellationToken);

                        if (tempBinPath != null)
                        {
                            originalImagePath = job.ImagePath;
                            originalCuePath = job.CuePath;
                            job.ImagePath = tempBinPath;
                            if (tempCuePath != null)
                                job.CuePath = tempCuePath;
                        }
                    }
                    else
                    {
                        // Standalone .img — try direct burn for raw 2352 format
                        (tempBinPath, tempCuePath) = await ConvertToBurnableFormatAsync(
                            job, ".IMG", progress, cancellationToken);

                        if (tempBinPath != null)
                        {
                            originalImagePath = job.ImagePath;
                            originalCuePath = job.CuePath;
                            job.ImagePath = tempBinPath;
                            if (tempCuePath != null)
                                job.CuePath = tempCuePath;
                        }
                    }
                }
                else if (ext == ".CUE")
                {
                    // User selected a .cue file directly — find the companion .bin file(s).
                    // CUE sheets reference the data file by name; we need to set ImagePath
                    // to the actual data file and CuePath to the CUE sheet.
                    var cuePath = job.ImagePath;
                    job.CuePath = cuePath;

                    // Check if this is a multi-file CUE sheet (one BIN per track)
                    var cueBinFiles = BurnEngine.GetCueBinFiles(cuePath);
                    if (cueBinFiles.Count > 1)
                    {
                        // Multi-file CUE: concatenate all BIN files into a single temp file
                        // for the burn engine, which reads from a single stream.
                        var allExist = cueBinFiles.All(File.Exists);
                        if (!allExist)
                        {
                            var missing = cueBinFiles.Where(f => !File.Exists(f)).Select(Path.GetFileName);
                            throw new FileNotFoundException(
                                $"Multi-file CUE sheet references missing BIN files: {string.Join(", ", missing)}");
                        }

                        progress.Report(new BurnProgress
                        {
                            LogLine = $"[Info] Multi-file CUE sheet detected — {cueBinFiles.Count} BIN files"
                        });

                        // Create concatenated temp BIN file
                        tempBinPath = Path.Combine(Path.GetTempPath(),
                            $"obs_concat_{Guid.NewGuid():N}.bin");
                        await using (var outStream = new FileStream(tempBinPath, FileMode.Create,
                            FileAccess.Write, FileShare.None, 65536, true))
                        {
                            foreach (var binFile in cueBinFiles)
                            {
                                await using var inStream = new FileStream(binFile, FileMode.Open,
                                    FileAccess.Read, FileShare.Read, 65536, true);
                                await inStream.CopyToAsync(outStream, cancellationToken);
                            }
                        }

                        originalImagePath = job.ImagePath;
                        job.ImagePath = tempBinPath;
                        progress.Report(new BurnProgress
                        {
                            LogLine = $"[Info] Concatenated {cueBinFiles.Count} BIN files ({new FileInfo(tempBinPath).Length / 1_048_576} MB)"
                        });
                    }
                    else if (cueBinFiles.Count == 1 && File.Exists(cueBinFiles[0]))
                    {
                        // Single-file CUE: use the referenced BIN directly
                        job.ImagePath = cueBinFiles[0];
                        progress.Report(new BurnProgress
                        {
                            LogLine = $"[Info] CUE sheet selected — using BIN file: {Path.GetFileName(cueBinFiles[0])}"
                        });
                    }
                    else
                    {
                        // Fallback: try matching by base name (legacy behavior)
                        var cueDir = Path.GetDirectoryName(cuePath) ?? ".";
                        var cueBaseName = Path.GetFileNameWithoutExtension(cuePath);
                        var binPath = Directory.EnumerateFiles(cueDir, cueBaseName + ".bin",
                            new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive })
                            .FirstOrDefault();
                        if (binPath != null && File.Exists(binPath))
                        {
                            job.ImagePath = binPath;
                            progress.Report(new BurnProgress
                            {
                                LogLine = $"[Info] CUE sheet selected — using companion BIN file: {Path.GetFileName(binPath)}"
                            });
                        }
                        else
                        {
                            throw new FileNotFoundException(
                                $"Could not find companion BIN file(s) for CUE sheet: {cuePath}");
                        }
                    }
                }
                else if (ext == ".TOC")
                {
                    // User selected a .toc file — convert to CUE, then resolve BIN files.
                    // cdrdao TOC files use DATAFILE directives to reference BIN data files.
                    var tocPath = job.ImagePath;

                    // Convert TOC to a temporary CUE sheet for the burn engine
                    tempCuePath = Path.Combine(Path.GetTempPath(),
                        $"obs_toc2cue_{Guid.NewGuid():N}.cue");
                    TocCueConverter.TocToCue(tocPath, tempCuePath);
                    job.CuePath = tempCuePath;

                    progress.Report(new BurnProgress
                    {
                        LogLine = $"[Info] TOC file converted to CUE for burning: {Path.GetFileName(tocPath)}"
                    });

                    // Resolve BIN files from the generated CUE sheet
                    var cueBinFiles = BurnEngine.GetCueBinFiles(tempCuePath);
                    if (cueBinFiles.Count > 1)
                    {
                        var allExist = cueBinFiles.All(File.Exists);
                        if (!allExist)
                        {
                            var missing = cueBinFiles.Where(f => !File.Exists(f)).Select(Path.GetFileName);
                            throw new FileNotFoundException(
                                $"TOC file references missing BIN files: {string.Join(", ", missing)}");
                        }

                        // Multi-file: concatenate all BIN files into a single temp file
                        tempBinPath = Path.Combine(Path.GetTempPath(),
                            $"obs_concat_{Guid.NewGuid():N}.bin");
                        await using (var outStream = new FileStream(tempBinPath, FileMode.Create,
                            FileAccess.Write, FileShare.None, 65536, true))
                        {
                            foreach (var binFile in cueBinFiles)
                            {
                                await using var inStream = new FileStream(binFile, FileMode.Open,
                                    FileAccess.Read, FileShare.Read, 65536, true);
                                await inStream.CopyToAsync(outStream, cancellationToken);
                            }
                        }
                        originalImagePath = job.ImagePath;
                        job.ImagePath = tempBinPath;
                    }
                    else if (cueBinFiles.Count == 1 && File.Exists(cueBinFiles[0]))
                    {
                        job.ImagePath = cueBinFiles[0];
                    }
                    else
                    {
                        // Fallback: try companion BIN by base name
                        var tocDir = Path.GetDirectoryName(tocPath) ?? ".";
                        var tocBaseName = Path.GetFileNameWithoutExtension(tocPath);
                        var binPath = Directory.EnumerateFiles(tocDir, tocBaseName + ".bin",
                            new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive })
                            .FirstOrDefault();
                        if (binPath != null && File.Exists(binPath))
                        {
                            job.ImagePath = binPath;
                        }
                        else
                        {
                            throw new FileNotFoundException(
                                $"Could not find companion BIN file(s) for TOC file: {tocPath}");
                        }
                    }
                }
            }

            // -----------------------------------------------------------------
            // PS3 ISO encryption detection and warning.
            // PS3 game ISOs must be decrypted before burning for the disc to work.
            // Detect encrypted PS3 ISOs and log a warning.
            // -----------------------------------------------------------------
            if (!string.IsNullOrWhiteSpace(job.ImagePath) && File.Exists(job.ImagePath))
            {
                try
                {
                    var bdInfo = Ps3DiscService.DetectContent(job.ImagePath);
                    if (bdInfo.ContentType == BluRayContentType.Ps3Game && bdInfo.IsEncrypted)
                    {
                        progress.Report(new BurnProgress
                        {
                            StatusMessage = "⚠ Encrypted PS3 ISO detected",
                            LogLine = "[Warning] This PS3 ISO appears to be encrypted. " +
                                      "Encrypted PS3 discs will not work on consoles. " +
                                      "Please decrypt the ISO using an IRD file before burning."
                        });
                    }
                }
                catch { /* PS3 detection is best-effort */ }
            }

            // -----------------------------------------------------------------
            // Apply gaming patches to the image before burning.
            // Patches modify data in-place, so we work on a copy to keep the
            // user's original image intact.
            // -----------------------------------------------------------------
            var anyPatch = job.EsrPatching || job.MasterDiscPatching
                        || job.Psx80MinutePatching || job.PsxUndither;

            if (anyPatch && !string.IsNullOrWhiteSpace(job.ImagePath) && File.Exists(job.ImagePath))
            {
                // Sanitize the file extension to prevent path traversal via malicious filenames.
                var safeExt = Path.GetExtension(job.ImagePath);
                if (string.IsNullOrEmpty(safeExt) || safeExt.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    safeExt = ".bin";

                // Copy the image to a temporary file for patching
                tempPatchedImage = Path.Combine(Path.GetTempPath(),
                    $"obs_patched_{Guid.NewGuid():N}{safeExt}");
                progress.Report(new BurnProgress
                {
                    StatusMessage = "Preparing image for gaming patches...",
                    LogLine = "[Info] Copying image for patching (original file will not be modified)"
                });

                try
                {
                    await Task.Run(() => File.Copy(job.ImagePath, tempPatchedImage, overwrite: true), cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new IOException(
                        $"Failed to copy image for patching: {ex.Message}. " +
                        "Check that there is enough disk space and that the file is not locked.", ex);
                }

                originalImagePath ??= job.ImagePath;
                job.ImagePath = tempPatchedImage;

                if (job.EsrPatching)
                {
                    progress.Report(new BurnProgress
                    {
                        StatusMessage = "Applying ESR patch...",
                        LogLine = "[Info] Applying ESR patch (PS2 DVD-R MechaCon bypass)"
                    });
                    var esrError = await Task.Run(() => GamingDiscService.ApplyEsrPatch(tempPatchedImage), cancellationToken);
                    if (esrError != null)
                        progress.Report(new BurnProgress { LogLine = $"[Warning] ESR patch: {esrError}" });
                    else
                        progress.Report(new BurnProgress { LogLine = "[Info] ESR patch applied successfully." });
                }

                if (job.MasterDiscPatching)
                {
                    progress.Report(new BurnProgress
                    {
                        StatusMessage = "Applying Master Disc patch...",
                        LogLine = $"[Info] Applying Master Disc patch (region: {job.MasterDiscRegion})"
                    });
                    var mdError = await Task.Run(() => GamingDiscService.ApplyMasterDiscPatch(tempPatchedImage, job.MasterDiscRegion), cancellationToken);
                    if (mdError != null)
                        progress.Report(new BurnProgress { LogLine = $"[Warning] Master Disc patch: {mdError}" });
                    else
                        progress.Report(new BurnProgress { LogLine = "[Info] Master Disc patch applied successfully." });
                }

                if (job.Psx80MinutePatching)
                {
                    progress.Report(new BurnProgress
                    {
                        StatusMessage = "Applying PSX 80 Minute patch...",
                        LogLine = "[Info] Applying PSX 80 Minute patch (appending dummy sectors)"
                    });
                    var psx80Error = await Task.Run(() => GamingDiscService.ApplyPsx80MinutePatch(tempPatchedImage), cancellationToken);
                    if (psx80Error != null)
                        progress.Report(new BurnProgress { LogLine = $"[Warning] PSX 80min patch: {psx80Error}" });
                    else
                        progress.Report(new BurnProgress { LogLine = "[Info] PSX 80 Minute patch applied successfully." });
                }

                if (job.PsxUndither)
                {
                    progress.Report(new BurnProgress
                    {
                        StatusMessage = "Applying PSX Undither patch...",
                        LogLine = "[Info] Applying PSX Undither patch (removing GPU dithering)"
                    });
                    var (undError, matchCount) = await Task.Run(() => GamingDiscService.ApplyPsxUndither(tempPatchedImage), cancellationToken);
                    if (undError != null)
                        progress.Report(new BurnProgress { LogLine = $"[Warning] PSX Undither: {undError}" });
                    else
                        progress.Report(new BurnProgress { LogLine = $"[Info] PSX Undither applied: {matchCount} pattern(s) patched." });
                }
            }

            var copies = Math.Max(1, job.Copies);
            for (int copy = 1; copy <= copies; copy++)
            {
                if (copies > 1)
                    progress.Report(new BurnProgress
                    {
                        StatusMessage = $"Burning copy {copy} of {copies}...",
                        LogLine = $"--- Copy {copy}/{copies} ---"
                    });

                await _engine.BurnImageAsync(job, progress, cancellationToken);

                // For multi-copy burns: always eject and wait for new media between copies.
                // Even if EjectAfterBurn is false, we must eject to prompt the user to
                // insert a new disc — burning to the same finalized disc would fail.
                if (copy < copies)
                {
                    // If BurnImageAsync didn't eject (EjectAfterBurn was false), eject now
                    if (!job.EjectAfterBurn)
                    {
                        try
                        {
                            using var drive = new Native.Optical.OpticalDrive(job.DevicePath);
                            drive.Eject();
                        }
                        catch { /* best effort */ }
                    }

                    progress.Report(new BurnProgress
                    {
                        StatusMessage = $"Copy {copy} complete. Please insert a new disc for copy {copy + 1}...",
                        LogLine = $"[Info] Waiting for new disc (copy {copy + 1}/{copies})..."
                    });

                    // Wait for new media to become available
                    await WaitForNewMediaAsync(job.DevicePath, progress, cancellationToken);

                    progress.Report(new BurnProgress
                    {
                        StatusMessage = $"New disc detected. Starting copy {copy + 1}...",
                        LogLine = $"[Info] New disc detected, proceeding with copy {copy + 1}."
                    });
                }
            }

            job.Status = BurnStatus.Completed;
        }
        catch (OperationCanceledException)
        {
            job.Status = BurnStatus.Cancelled;
            throw;
        }
        catch
        {
            job.Status = BurnStatus.Failed;
            throw;
        }
        finally
        {
            job.FinishedAt = DateTime.Now;

            // Restore original paths if we substituted a converted file
            if (originalImagePath != null)
                job.ImagePath = originalImagePath;
            if (originalCuePath != null)
                job.CuePath = originalCuePath;

            // Clean up temporary conversion files
            CleanupTempFile(tempBinPath);
            CleanupTempFile(tempCuePath);
            CleanupTempFile(tempOnTheFlyIso);
            CleanupTempFile(tempPatchedImage);
        }
    }

    // -----------------------------------------------------------------------
    //  Build on-the-fly: create a temporary ISO from source files/folders
    // -----------------------------------------------------------------------

    /// <summary>
    /// Maximum percentage allocated to the build phase of an on-the-fly burn.
    /// The remaining percentage (up to 100%) is used for the actual disc burn.
    /// </summary>
    private const int BuildPhaseMaxPercent = 20;

    /// <summary>
    /// Builds a temporary ISO 9660/Joliet/UDF image from source files and folders
    /// for on-the-fly burning. Returns the path to the temporary ISO file.
    /// </summary>
    /// <remarks>
    /// When <see cref="BurnJob.SourceFolder"/> is set, its entire contents are used.
    /// When <see cref="BurnJob.SourceFiles"/> is set instead, a temporary staging
    /// directory is assembled first, then the ISO is built from it.
    /// </remarks>
    private async Task<string> BuildOnTheFlyImageAsync(
        BurnJob job,
        IProgress<BurnProgress> progress,
        CancellationToken ct)
    {
        // Validate sources
        var hasSourceFolder = !string.IsNullOrWhiteSpace(job.SourceFolder);
        var hasSourceFiles = job.SourceFiles.Count > 0;

        if (!hasSourceFolder && !hasSourceFiles)
            throw new ArgumentException(
                "On-the-fly mode requires a source folder or at least one source file/folder.");

        if (hasSourceFolder && !Directory.Exists(job.SourceFolder))
            throw new DirectoryNotFoundException(
                $"Source folder not found: {job.SourceFolder}");

        if (hasSourceFiles)
        {
            foreach (var path in job.SourceFiles)
            {
                if (!File.Exists(path) && !Directory.Exists(path))
                    throw new FileNotFoundException(
                        $"Source path not found: {path}");
            }
        }

        progress.Report(new BurnProgress
        {
            StatusMessage = "Building disc image from files...",
            LogLine = "[Info] On-the-fly mode — building temporary ISO image from source files/folders"
        });

        // Determine the effective source folder
        string effectiveSourceFolder;
        string? tempStagingDir = null;

        if (hasSourceFolder)
        {
            // Use the source folder directly
            effectiveSourceFolder = job.SourceFolder;
            progress.Report(new BurnProgress
            {
                LogLine = $"[Info] Source folder: {job.SourceFolder}"
            });
        }
        else
        {
            // Create a temporary staging directory and copy/link individual files and folders
            tempStagingDir = Path.Combine(Path.GetTempPath(),
                "OpenBurningSuite_OnTheFly_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(tempStagingDir);
            effectiveSourceFolder = tempStagingDir;

            progress.Report(new BurnProgress
            {
                LogLine = $"[Info] Staging {job.SourceFiles.Count} source item(s) for image building"
            });

            await Task.Run(() => StageSourceFiles(job.SourceFiles, tempStagingDir, progress), ct);
        }

        try
        {
            // Count total source data size for progress reporting
            var totalSize = CalculateDirectorySize(effectiveSourceFolder);
            progress.Report(new BurnProgress
            {
                LogLine = $"[Info] Total source data: {FormatHelper.FormatBytes(totalSize)}"
            });

            // Create the temporary ISO output path
            var tempDir = Path.Combine(Path.GetTempPath(), "OpenBurningSuite_OnTheFly");
            Directory.CreateDirectory(tempDir);

            var volumeLabel = string.IsNullOrWhiteSpace(job.VolumeLabel)
                ? "UNTITLED"
                : job.VolumeLabel;
            var safeLabel = new string(volumeLabel
                .Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-')
                .Take(16).ToArray());
            if (string.IsNullOrWhiteSpace(safeLabel)) safeLabel = "DISC";

            var tempIsoPath = Path.Combine(tempDir, $"{safeLabel}_{DateTime.Now:yyyyMMdd_HHmmss}.iso");

            // Build the ISO image using the existing IsoBuilder infrastructure
            var buildJob = new BuildJob
            {
                SourceFolder = effectiveSourceFolder,
                OutputImagePath = tempIsoPath,
                VolumeLabel = volumeLabel,
                FileSystem = job.FileSystem,
                DiscType = DetermineDiscType(job),
                JolietLongFilenames = IsJolietEnabled(job.FileSystem),
                DeepDirectoryNesting = true
            };

            // Bridge BuildProgress → BurnProgress so the UI gets updates during the build phase
            var buildProgress = new Progress<BuildProgress>(bp =>
            {
                // Scale build progress to 0-BuildPhaseMaxPercent of overall burn progress
                var scaledPct = Math.Min(BuildPhaseMaxPercent,
                    bp.PercentComplete * BuildPhaseMaxPercent / 100);
                progress.Report(new BurnProgress
                {
                    PercentComplete = scaledPct,
                    StatusMessage = bp.StatusMessage,
                    LogLine = bp.LogLine,
                    BytesWritten = bp.BytesProcessed,
                    TotalBytes = bp.TotalBytes,
                    Elapsed = bp.Elapsed,
                    Remaining = bp.Remaining
                });
            });

            await _isoBuilder.BuildDataImageAsync(buildJob, buildProgress, ct);

            if (!File.Exists(tempIsoPath))
                throw new InvalidOperationException(
                    "Failed to build temporary ISO image — output file was not created.");

            var isoSize = new FileInfo(tempIsoPath).Length;
            progress.Report(new BurnProgress
            {
                PercentComplete = BuildPhaseMaxPercent,
                StatusMessage = "Image built — starting burn...",
                LogLine = $"[Info] Temporary ISO built: {FormatHelper.FormatBytes(isoSize)} → {tempIsoPath}"
            });

            return tempIsoPath;
        }
        finally
        {
            // Clean up staging directory if we created one
            if (tempStagingDir != null)
            {
                try
                {
                    if (Directory.Exists(tempStagingDir))
                        Directory.Delete(tempStagingDir, recursive: true);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[BurnService] Failed to clean up staging directory '{tempStagingDir}': {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Stages individual source files and folders into a temporary directory
    /// by copying them, preserving directory structure.
    /// </summary>
    private static void StageSourceFiles(
        System.Collections.Generic.List<string> sourcePaths,
        string stagingDir,
        IProgress<BurnProgress> progress)
    {
        foreach (var sourcePath in sourcePaths)
        {
            if (Directory.Exists(sourcePath))
            {
                // Copy entire directory contents into a subdirectory of staging
                var dirName = Path.GetFileName(sourcePath);
                if (string.IsNullOrWhiteSpace(dirName))
                    dirName = "folder";

                var targetDir = Path.Combine(stagingDir, dirName);
                // Handle name collisions by appending a suffix
                if (Directory.Exists(targetDir) || File.Exists(targetDir))
                    targetDir = Path.Combine(stagingDir, dirName + "_" + Guid.NewGuid().ToString("N")[..4]);

                CopyDirectoryRecursive(sourcePath, targetDir);
                progress.Report(new BurnProgress
                {
                    LogLine = $"[Info] Staged folder: {sourcePath} → {Path.GetFileName(targetDir)}/"
                });
            }
            else if (File.Exists(sourcePath))
            {
                // Copy individual file to staging root
                var fileName = Path.GetFileName(sourcePath);
                var targetFile = Path.Combine(stagingDir, fileName);
                // Handle name collisions
                if (File.Exists(targetFile))
                {
                    var nameOnly = Path.GetFileNameWithoutExtension(fileName);
                    var ext = Path.GetExtension(fileName);
                    targetFile = Path.Combine(stagingDir,
                        $"{nameOnly}_{Guid.NewGuid().ToString("N")[..4]}{ext}");
                }

                File.Copy(sourcePath, targetFile);
                progress.Report(new BurnProgress
                {
                    LogLine = $"[Info] Staged file: {fileName}"
                });
            }
        }
    }

    /// <summary>Recursively copies a directory and all its contents.</summary>
    private static void CopyDirectoryRecursive(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var destFile = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: false);
        }

        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            var destSubDir = Path.Combine(destDir, Path.GetFileName(subDir));
            CopyDirectoryRecursive(subDir, destSubDir);
        }
    }

    /// <summary>Calculates the total size of all files in a directory tree.</summary>
    private static long CalculateDirectorySize(string path)
    {
        long size = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { size += new FileInfo(file).Length; }
                catch (UnauthorizedAccessException) { /* skip files we can't access */ }
                catch (IOException) { /* skip files with I/O issues */ }
            }
        }
        catch (UnauthorizedAccessException) { /* ignore access errors on root enumeration */ }
        catch (IOException) { /* ignore I/O errors on root enumeration */ }
        return size;
    }

    /// <summary>Determines the best disc type string based on the burn job context.</summary>
    private static string DetermineDiscType(BurnJob job)
    {
        // Use forced media type if specified
        if (!string.IsNullOrWhiteSpace(job.ForceMediaType))
        {
            var upper = job.ForceMediaType.ToUpperInvariant();
            if (upper.Contains("BD")) return "BD-25";
            if (upper.Contains("HD DVD") || upper.Contains("HD-DVD")) return "HD DVD-15";
            if (upper.Contains("DVD")) return "DVD-5";
            return "CD";
        }
        return "DVD-5"; // Default to DVD for on-the-fly (most common use case)
    }

    /// <summary>Checks if the filesystem selection includes Joliet extensions.</summary>
    private static bool IsJolietEnabled(string fileSystem)
    {
        if (string.IsNullOrWhiteSpace(fileSystem)) return true;
        var upper = fileSystem.ToUpperInvariant();
        return upper.Contains("JOLIET");
    }

    // -----------------------------------------------------------------------
    //  Pre-burn format conversion
    // -----------------------------------------------------------------------

    /// <summary>
    /// Converts MDF/MDS, CDI, and CCD/IMG disc images to BIN/CUE format
    /// suitable for native SCSI burning. Returns the paths to the temporary
    /// BIN and CUE files, or (null, null) if conversion is not needed.
    /// </summary>
    private static async Task<(string? BinPath, string? CuePath)> ConvertToBurnableFormatAsync(
        BurnJob job,
        string formatExtension,
        IProgress<BurnProgress> progress,
        CancellationToken ct)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "OpenBurningSuite_BurnConvert");
        Directory.CreateDirectory(tempDir);

        var tempBin = Path.Combine(tempDir,
            Path.GetFileNameWithoutExtension(job.ImagePath) + "_burn.bin");
        var tempCue = Path.Combine(tempDir,
            Path.GetFileNameWithoutExtension(job.ImagePath) + "_burn.cue");

        switch (formatExtension)
        {
            case ".MDS" or ".MDF":
                return await PrepareMdfMdsForBurnAsync(job, tempCue, progress, ct);

            case ".CDI":
                return await PrepareCdiForBurnAsync(job, tempBin, tempCue, progress, ct);

            case ".CCD":
                return await PrepareCcdForBurnAsync(job, tempCue, progress, ct);

            case ".NRG":
                return await ConvertNrgToBinCueAsync(job.ImagePath, tempBin, tempCue, progress, ct);

            case ".IMG":
                return await PrepareImgForBurnAsync(job, tempCue, progress, ct);

            case ".CHD":
                return await ConvertChdToBinCueAsync(job.ImagePath, tempBin, tempCue, progress, ct);

            default:
                return (null, null);
        }
    }

    /// <summary>
    /// Prepares an Alcohol 120% MDF/MDS image for direct burning.
    /// When the MDF sector data is contiguous with uniform sector sizes,
    /// the MDF file is used directly as the burn source (no data copy needed).
    /// Only a small CUE sheet text file is generated.
    /// Falls back to full conversion if the layout is incompatible.
    /// </summary>
    private static async Task<(string? BinPath, string? CuePath)> PrepareMdfMdsForBurnAsync(
        BurnJob job,
        string tempCue,
        IProgress<BurnProgress> progress,
        CancellationToken ct)
    {
        // Resolve MDS/MDF companion paths (try both cases for case-sensitive filesystems)
        var ext = Path.GetExtension(job.ImagePath).ToUpperInvariant();
        string mdsPath, mdfPath;
        if (ext == ".MDS")
        {
            mdsPath = job.ImagePath;
            mdfPath = Path.ChangeExtension(job.ImagePath, ".mdf");
            if (!File.Exists(mdfPath))
                mdfPath = Path.ChangeExtension(job.ImagePath, ".MDF");
        }
        else
        {
            mdfPath = job.ImagePath;
            mdsPath = Path.ChangeExtension(job.ImagePath, ".mds");
            if (!File.Exists(mdsPath))
                mdsPath = Path.ChangeExtension(job.ImagePath, ".MDS");
        }

        if (!File.Exists(mdsPath))
            throw new FileNotFoundException(
                $"MDS descriptor file not found: {mdsPath}. Both .mds and .mdf files are required.");
        if (!File.Exists(mdfPath))
            throw new FileNotFoundException(
                $"MDF data file not found: {mdfPath}. Both .mds and .mdf files are required.");

        var image = await MdfParser.ParseAsync(mdsPath, ct);
        if (!image.IsValid)
            throw new InvalidOperationException(
                $"Failed to parse MDS file: {image.ErrorMessage}");

        progress.Report(new BurnProgress
        {
            LogLine = $"[Info] MDS parsed: {image.SessionCount} session(s), " +
                      $"{image.TotalTrackCount} track(s), medium type: {image.MediumType}"
        });

        // Check if MDF data is suitable for direct burning:
        // All tracks must have uniform sector sizes and be contiguous in the MDF
        var allTracks = image.AllRegularTracks.ToList();
        bool canBurnDirectly = allTracks.Count > 0;
        bool hasMixedSectorSizes = allTracks.Select(t => t.MainSectorSize).Distinct().Count() > 1;
        if (hasMixedSectorSizes)
            canBurnDirectly = false;

        // Check contiguity: each track must follow the previous one
        if (canBurnDirectly && allTracks.Count > 1)
        {
            for (int i = 1; i < allTracks.Count; i++)
            {
                var prev = allTracks[i - 1];
                var curr = allTracks[i];
                uint prevPregap = prev.ExtraBlock?.Pregap ?? 0;
                uint prevLength = prev.ExtraBlock?.Length ?? 0;
                long prevEnd = (long)prev.StartOffset + (prevPregap + prevLength) * prev.SectorSize;
                if ((long)curr.StartOffset != prevEnd)
                {
                    canBurnDirectly = false;
                    break;
                }
            }
        }

        if (canBurnDirectly)
        {
            // Direct burning: use MDF file directly, only generate CUE sheet
            var cueContent = MdfParser.GenerateCueSheetForDirectBurn(image, Path.GetFileName(mdfPath));
            await File.WriteAllTextAsync(tempCue, cueContent, Encoding.ASCII, ct);

            long startOffset = (long)allTracks[0].StartOffset;
            job.ImageStartOffset = startOffset;

            progress.Report(new BurnProgress
            {
                StatusMessage = "MDF/MDS ready for direct burning",
                LogLine = $"[Info] MDF/MDS direct burn — using {Path.GetFileName(mdfPath)} directly " +
                          $"(offset: {startOffset}, no data copy needed)"
            });

            return (mdfPath, tempCue);
        }
        else
        {
            // Fallback: full conversion needed due to mixed sector sizes or non-contiguous layout
            progress.Report(new BurnProgress
            {
                StatusMessage = "Converting MDF/MDS image for burning...",
                LogLine = "[Info] MDF/MDS layout requires conversion — extracting to BIN/CUE"
            });

            var tempDir = Path.GetDirectoryName(tempCue)!;
            var tempBin = Path.Combine(tempDir,
                Path.GetFileNameWithoutExtension(job.ImagePath) + "_burn.bin");

            await MdfParser.ConvertToBinCueAsync(mdfPath, image, tempBin, tempCue,
                new Progress<int>(pct => progress.Report(new BurnProgress
                {
                    PercentComplete = pct / 10,
                    StatusMessage = $"Converting MDF/MDS: {pct}%..."
                })), ct);

            progress.Report(new BurnProgress
            {
                LogLine = $"[Info] MDF/MDS converted to BIN/CUE: {FormatHelper.FormatBytes(new FileInfo(tempBin).Length)}"
            });

            return (tempBin, tempCue);
        }
    }

    /// <summary>
    /// Prepares a DiscJuggler CDI image for direct burning.
    /// When all tracks are contiguous in the CDI file, the CDI is used directly
    /// as the burn source with an offset to skip metadata headers.
    /// Falls back to full conversion if the track layout is non-contiguous.
    /// </summary>
    private static async Task<(string? BinPath, string? CuePath)> PrepareCdiForBurnAsync(
        BurnJob job,
        string tempBin,
        string tempCue,
        IProgress<BurnProgress> progress,
        CancellationToken ct)
    {
        var cdiPath = job.ImagePath;

        if (!File.Exists(cdiPath))
            throw new FileNotFoundException($"CDI file not found: {cdiPath}");

        var image = await CdiParser.ParseAsync(cdiPath, ct);
        if (!image.IsValid)
            throw new InvalidOperationException(
                $"Failed to parse CDI file: {image.ErrorMessage}");

        progress.Report(new BurnProgress
        {
            LogLine = $"[Info] CDI parsed: {image.SessionCount} session(s), " +
                      $"{image.TotalTrackCount} track(s), version: {image.Version}"
        });

        // Check if CDI tracks are contiguous (track data follows sequentially in the file)
        var allTracks = image.Sessions.SelectMany(s => s.Tracks).ToList();
        bool canBurnDirectly = allTracks.Count > 0;
        long firstTrackOffset = allTracks.Count > 0 ? allTracks[0].FileOffset : 0;

        if (canBurnDirectly && allTracks.Count > 1)
        {
            for (int i = 1; i < allTracks.Count; i++)
            {
                long prevEnd = allTracks[i - 1].FileOffset + allTracks[i - 1].TotalLength;
                if (allTracks[i].FileOffset != prevEnd)
                {
                    canBurnDirectly = false;
                    break;
                }
            }
        }

        if (canBurnDirectly)
        {
            // Direct burning: use CDI file directly with offset
            var cueContent = CdiParser.GenerateCueSheetForDirectBurn(image, Path.GetFileName(cdiPath));
            await File.WriteAllTextAsync(tempCue, cueContent, Encoding.ASCII, ct);

            job.ImageStartOffset = firstTrackOffset;

            progress.Report(new BurnProgress
            {
                StatusMessage = "CDI ready for direct burning",
                LogLine = $"[Info] CDI direct burn — using {Path.GetFileName(cdiPath)} directly " +
                          $"(offset: {firstTrackOffset}, no data copy needed)"
            });

            return (cdiPath, tempCue);
        }
        else
        {
            // Fallback: full conversion needed due to non-contiguous tracks
            progress.Report(new BurnProgress
            {
                StatusMessage = "Converting CDI image for burning...",
                LogLine = "[Info] CDI layout requires conversion — extracting to BIN/CUE"
            });

            await CdiParser.ConvertToBinCueAsync(cdiPath, image, tempBin, tempCue,
                new Progress<int>(pct => progress.Report(new BurnProgress
                {
                    PercentComplete = pct / 10,
                    StatusMessage = $"Converting CDI: {pct}%..."
                })), ct);

            progress.Report(new BurnProgress
            {
                LogLine = $"[Info] CDI converted to BIN/CUE: {FormatHelper.FormatBytes(new FileInfo(tempBin).Length)}"
            });

            return (tempBin, tempCue);
        }
    }

    /// <summary>
    /// Prepares a CloneCD CCD/IMG image for direct burning.
    /// The .img file already contains raw 2352-byte sectors identical to a .bin file.
    /// Only a small CUE sheet text file is generated — NO data copy is performed.
    /// This eliminates the need to duplicate multi-GB disc image files.
    /// </summary>
    private static async Task<(string? BinPath, string? CuePath)> PrepareCcdForBurnAsync(
        BurnJob job,
        string tempCue,
        IProgress<BurnProgress> progress,
        CancellationToken ct)
    {
        // Resolve CCD and IMG companion paths (try both cases for case-sensitive filesystems)
        var ext = Path.GetExtension(job.ImagePath).ToUpperInvariant();
        string ccdPath, imgPath;
        if (ext == ".CCD")
        {
            ccdPath = job.ImagePath;
            imgPath = Path.ChangeExtension(job.ImagePath, ".img");
            if (!File.Exists(imgPath))
                imgPath = Path.ChangeExtension(job.ImagePath, ".IMG");
        }
        else
        {
            imgPath = job.ImagePath;
            ccdPath = Path.ChangeExtension(job.ImagePath, ".ccd");
            if (!File.Exists(ccdPath))
                ccdPath = Path.ChangeExtension(job.ImagePath, ".CCD");
        }

        if (!File.Exists(ccdPath))
            throw new FileNotFoundException(
                $"CCD descriptor file not found: {ccdPath}. Both .ccd and .img files are required.");
        if (!File.Exists(imgPath))
            throw new FileNotFoundException(
                $"IMG data file not found: {imgPath}. Both .ccd and .img files are required.");

        var image = await CcdParser.ParseAsync(ccdPath, ct);
        if (!image.IsValid)
            throw new InvalidOperationException(
                $"Failed to parse CCD file: {image.ErrorMessage}");

        progress.Report(new BurnProgress
        {
            LogLine = $"[Info] CCD parsed: {image.SessionCount} session(s), " +
                      $"{image.TrackCount} track(s), disc type: {image.DiscType}"
        });

        // Direct burning: CCD .img files are raw 2352-byte sectors, identical to BIN format.
        // No data copy needed — just generate a CUE sheet pointing at the .img file.
        var cueContent = CcdParser.GenerateCueSheetForFile(image, Path.GetFileName(imgPath));
        await File.WriteAllTextAsync(tempCue, cueContent, Encoding.ASCII, ct);

        progress.Report(new BurnProgress
        {
            StatusMessage = "CCD/IMG ready for direct burning",
            LogLine = $"[Info] CCD/IMG direct burn — using {Path.GetFileName(imgPath)} directly " +
                      $"({FormatHelper.FormatBytes(new FileInfo(imgPath).Length)}, no data copy needed)"
        });

        // Return imgPath as the BinPath so BurnService sets job.ImagePath to the .img file
        return (imgPath, tempCue);
    }

    /// <summary>
    /// Converts a Nero NRG image to BIN/CUE for burning.
    /// Parses the NRG chunk chain to determine track layout, then extracts
    /// the raw sector data into a standard BIN/CUE pair.
    /// </summary>
    private static async Task<(string? BinPath, string? CuePath)> ConvertNrgToBinCueAsync(
        string nrgPath,
        string tempBin,
        string tempCue,
        IProgress<BurnProgress> progress,
        CancellationToken ct)
    {
        progress.Report(new BurnProgress
        {
            StatusMessage = "Converting NRG image for burning...",
            LogLine = "[Info] NRG format detected — converting to BIN/CUE for native burning"
        });

        if (!File.Exists(nrgPath))
            throw new FileNotFoundException($"NRG file not found: {nrgPath}");

        var image = await NrgParser.ParseAsync(nrgPath, ct);
        if (!image.IsValid)
            throw new InvalidOperationException(
                $"Failed to parse NRG file: {image.ErrorMessage}");

        progress.Report(new BurnProgress
        {
            LogLine = $"[Info] NRG parsed: {image.VersionString}, {image.SessionCount} session(s), " +
                      $"{image.TotalTrackCount} track(s), recording: {(image.IsDao ? "DAO" : "TAO")}"
        });

        await NrgParser.ConvertToBinCueAsync(nrgPath, image, tempBin, tempCue,
            new Progress<int>(pct => progress.Report(new BurnProgress
            {
                PercentComplete = pct / 10,
                StatusMessage = $"Converting NRG: {pct}%..."
            })), ct);

        progress.Report(new BurnProgress
        {
            LogLine = $"[Info] NRG converted to BIN/CUE: {FormatHelper.FormatBytes(new FileInfo(tempBin).Length)}"
        });

        return (tempBin, tempCue);
    }

    /// <summary>
    /// Extracts a MAME CHD image to BIN/CUE using chdman.
    /// </summary>
    private static async Task<(string? BinPath, string? CuePath)> ConvertChdToBinCueAsync(
        string chdPath,
        string tempBin,
        string tempCue,
        IProgress<BurnProgress> progress,
        CancellationToken ct)
    {
        progress.Report(new BurnProgress
        {
            StatusMessage = "Converting CHD image for burning...",
            LogLine = "[Info] CHD format detected — extracting to BIN/CUE via chdman"
        });

        if (!File.Exists(chdPath))
            throw new FileNotFoundException($"CHD file not found: {chdPath}");

        if (!ChdHelper.IsAvailable())
            throw new InvalidOperationException(
                "chdman is not installed or not found in PATH. Please install MAME tools (chdman).");

        try
        {
            await ChdHelper.ExtractToBinCueAsync(chdPath, tempBin, tempCue, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"chdman extraction failed: {ex.Message}", ex);
        }

        if (!File.Exists(tempBin) || !File.Exists(tempCue))
            throw new InvalidOperationException("chdman did not produce expected BIN/CUE output.");

        progress.Report(new BurnProgress
        {
            LogLine = $"[Info] CHD extracted to BIN/CUE: {FormatHelper.FormatBytes(new FileInfo(tempBin).Length)}"
        });

        return (tempBin, tempCue);
    }

    /// <summary>
    /// Prepares a raw IMG image for direct burning.
    /// For raw 2352-byte sector IMG files, uses the file directly with a generated CUE sheet.
    /// For cooked 2048-byte IMG files, treats as ISO (no conversion needed).
    /// </summary>
    private static async Task<(string? BinPath, string? CuePath)> PrepareImgForBurnAsync(
        BurnJob job,
        string tempCue,
        IProgress<BurnProgress> progress,
        CancellationToken ct)
    {
        var imgPath = job.ImagePath;

        if (!File.Exists(imgPath))
            throw new FileNotFoundException($"IMG file not found: {imgPath}");

        var image = await ImgParser.ParseAsync(imgPath, ct);
        if (!image.IsValid)
            throw new InvalidOperationException(
                $"Failed to parse IMG file: {image.ErrorMessage}");

        progress.Report(new BurnProgress
        {
            LogLine = $"[Info] IMG parsed: {image.SectorFormatString}, " +
                      $"{image.TrackCount} track(s), {image.TotalSectorCount:N0} sectors"
        });

        // If the IMG is cooked 2048-byte sectors, it's essentially an ISO
        // and can be burned directly without conversion.
        if (image.SectorFormat == ImgSectorFormat.Cooked2048 && !image.HasCcdCompanion)
        {
            progress.Report(new BurnProgress
            {
                LogLine = "[Info] IMG is cooked 2048B — treating as ISO (no conversion needed)"
            });
            return (null, null);
        }

        // For raw 2352-byte sector IMG files, use directly with CUE sheet
        if (image.SectorFormat == ImgSectorFormat.Raw2352)
        {
            var cueContent = ImgParser.GenerateCueSheetForFile(image, Path.GetFileName(imgPath));
            await File.WriteAllTextAsync(tempCue, cueContent, Encoding.ASCII, ct);

            progress.Report(new BurnProgress
            {
                StatusMessage = "IMG ready for direct burning",
                LogLine = $"[Info] IMG direct burn — using {Path.GetFileName(imgPath)} directly " +
                          $"({FormatHelper.FormatBytes(new FileInfo(imgPath).Length)}, no data copy needed)"
            });

            return (imgPath, tempCue);
        }

        // Fallback: convert to BIN/CUE for other sector formats
        progress.Report(new BurnProgress
        {
            StatusMessage = "Converting IMG image for burning...",
            LogLine = "[Info] IMG format requires conversion — extracting to BIN/CUE"
        });

        var tempDir = Path.GetDirectoryName(tempCue)!;
        var tempBin = Path.Combine(tempDir,
            Path.GetFileNameWithoutExtension(job.ImagePath) + "_burn.bin");

        await ImgParser.ConvertToBinCueAsync(imgPath, image, tempBin, tempCue,
            new Progress<int>(pct => progress.Report(new BurnProgress
            {
                PercentComplete = pct / 10,
                StatusMessage = $"Converting IMG: {pct}%..."
            })), ct);

        progress.Report(new BurnProgress
        {
            LogLine = $"[Info] IMG converted to BIN/CUE: {FormatHelper.FormatBytes(new FileInfo(tempBin).Length)}"
        });

        return (tempBin, tempCue);
    }

    /// <summary>Deletes a temporary file if it exists, suppressing errors.</summary>
    private static void CleanupTempFile(string? path)
    {
        if (path == null) return;
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[BurnService] Failed to clean up temp file '{path}': {ex.Message}");
        }
    }

    /// <summary>
    /// Waits for the user to insert new media after an eject (for multi-copy burning).
    /// Polls TEST UNIT READY until a writable disc becomes available.
    /// </summary>
    private static async Task WaitForNewMediaAsync(
        string devicePath,
        IProgress<BurnProgress> progress,
        CancellationToken ct)
    {
        var startTime = DateTime.UtcNow;
        var lastReportTime = DateTime.MinValue;
        // Maximum wait time of 30 minutes to prevent indefinite blocking
        const int maxWaitMinutes = 30;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var drive = new Native.Optical.OpticalDrive(devicePath);
                if (drive.TestUnitReady())
                {
                    var discInfo = drive.ReadDiscInfo();
                    // Accept any disc that isn't fully finalized
                    if (discInfo != null && discInfo.DiscStatus != 2)
                        return;
                    // Also accept erasable (rewritable) discs even if finalized
                    if (discInfo is { Erasable: true })
                        return;
                }
            }
            catch (InvalidOperationException) { /* drive may not be accessible yet */ }
            catch (IOException) { /* drive may not be accessible yet */ }

            var elapsed = DateTime.UtcNow - startTime;
            if (elapsed.TotalMinutes >= maxWaitMinutes)
            {
                throw new TimeoutException(
                    $"Timed out after {maxWaitMinutes} minutes waiting for writable media in {devicePath}.");
            }
            if ((DateTime.UtcNow - lastReportTime).TotalSeconds >= 5)
            {
                lastReportTime = DateTime.UtcNow;
                progress.Report(new BurnProgress
                {
                    StatusMessage = $"Insert new disc... ({elapsed.TotalSeconds:F0}s)",
                    LogLine = "[Info] Still waiting for disc..."
                });
            }

            try
            {
                await Task.Delay(2000, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }
        }

        if (ct.IsCancellationRequested)
            throw new OperationCanceledException(ct);
    }

    /// <summary>
    /// Blanks (erases) a rewritable disc (CD-RW, DVD±RW, BD-RE).
    /// Uses native SCSI BLANK command.
    /// </summary>
    public async Task BlankDiscAsync(
        string devicePath,
        bool quickBlank,
        string? mediaType,
        IProgress<BurnProgress> progress,
        CancellationToken ct)
    {
        await _engine.BlankDiscAsync(devicePath, quickBlank, mediaType, progress, ct);
    }

    /// <summary>
    /// Ejects the disc tray on the specified drive.
    /// Uses NativeEject which tries SCSI START STOP UNIT first, then falls back
    /// to platform-specific mechanisms (ioctl on Linux, diskutil on macOS,
    /// IOCTL_STORAGE_EJECT_MEDIA on Windows) for maximum reliability.
    /// </summary>
    public static void EjectDisc(string devicePath)
    {
        if (!Native.Platform.NativeEject.EjectDisc(devicePath))
            throw new InvalidOperationException($"Failed to eject disc in {devicePath}.");
    }

    /// <summary>
    /// Closes (loads) the disc tray on the specified drive.
    /// Uses NativeEject which tries SCSI START STOP UNIT first, then falls back
    /// to platform-specific mechanisms on Windows for maximum reliability.
    /// </summary>
    public static void LoadTray(string devicePath)
    {
        if (!Native.Platform.NativeEject.LoadTray(devicePath))
            throw new InvalidOperationException($"Failed to load tray in {devicePath}.");
    }

    /// <summary>
    /// Formats a disc using FORMAT UNIT command. Used for DVD+RW, DVD-RAM, BD-R, BD-RE,
    /// DVD-RW (Restricted Overwrite), and HD DVD-RW media that require FORMAT UNIT
    /// instead of BLANK.
    /// </summary>
    public async Task FormatDiscAsync(
        string devicePath,
        bool quickFormat,
        IProgress<BurnProgress> progress,
        CancellationToken ct)
    {
        // Delegate to BlankDiscAsync which already handles profile-based dispatch
        // between BLANK and FORMAT UNIT commands.
        await _engine.BlankDiscAsync(devicePath, quickFormat, null, progress, ct);
    }

    // -----------------------------------------------------------------------
    // NRG parsing support
    // -----------------------------------------------------------------------

    /// <summary>
    /// Parses an NRG image file and returns its structure.
    /// </summary>
    public static NrgImage ParseNrgImage(string nrgFilePath)
    {
        return NrgParser.Parse(nrgFilePath);
    }

    /// <summary>
    /// Asynchronously parses an NRG image file.
    /// </summary>
    public static async Task<NrgImage> ParseNrgImageAsync(string nrgFilePath,
        CancellationToken ct = default)
    {
        return await NrgParser.ParseAsync(nrgFilePath, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Extracts user data from an NRG image to an ISO file.
    /// </summary>
    public static void ExtractNrgToIso(string nrgFilePath, string outputIsoPath,
        IProgress<int>? progress = null)
    {
        var image = NrgParser.Parse(nrgFilePath);
        if (!image.IsValid)
            throw new InvalidOperationException($"NRG parse error: {image.ErrorMessage}");

        NrgParser.ExtractToIso(nrgFilePath, image, outputIsoPath, progress);
    }

    /// <summary>
    /// Asynchronously extracts user data from an NRG image to an ISO file.
    /// </summary>
    public static async Task ExtractNrgToIsoAsync(string nrgFilePath, string outputIsoPath,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var image = await NrgParser.ParseAsync(nrgFilePath, ct).ConfigureAwait(false);
        if (!image.IsValid)
            throw new InvalidOperationException($"NRG parse error: {image.ErrorMessage}");

        await NrgParser.ExtractToIsoAsync(nrgFilePath, image, outputIsoPath, progress, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Converts an NRG image to BIN/CUE format.
    /// </summary>
    public static void ConvertNrgToBinCue(string nrgFilePath, string outputBinPath,
        string outputCuePath, IProgress<int>? progress = null)
    {
        var image = NrgParser.Parse(nrgFilePath);
        if (!image.IsValid)
            throw new InvalidOperationException($"NRG parse error: {image.ErrorMessage}");

        NrgParser.ConvertToBinCue(nrgFilePath, image, outputBinPath, outputCuePath, progress);
    }

    /// <summary>
    /// Asynchronously converts an NRG image to BIN/CUE format.
    /// </summary>
    public static async Task ConvertNrgToBinCueAsync(string nrgFilePath, string outputBinPath,
        string outputCuePath, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var image = await NrgParser.ParseAsync(nrgFilePath, ct).ConfigureAwait(false);
        if (!image.IsValid)
            throw new InvalidOperationException($"NRG parse error: {image.ErrorMessage}");

        await NrgParser.ConvertToBinCueAsync(nrgFilePath, image, outputBinPath, outputCuePath,
            progress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Validates an NRG image for integrity.
    /// </summary>
    public static List<string> ValidateNrgImage(string nrgFilePath)
    {
        var image = NrgParser.Parse(nrgFilePath);
        if (!image.IsValid)
            return new List<string> { $"Parse error: {image.ErrorMessage}" };

        return NrgParser.ValidateImage(image);
    }

    /// <summary>
    /// Asynchronously validates an NRG image.
    /// </summary>
    public static async Task<List<string>> ValidateNrgImageAsync(string nrgFilePath,
        CancellationToken ct = default)
    {
        var image = await NrgParser.ParseAsync(nrgFilePath, ct).ConfigureAwait(false);
        if (!image.IsValid)
            return new List<string> { $"Parse error: {image.ErrorMessage}" };

        return await NrgParser.ValidateImageAsync(image, ct).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------
    // IMG parsing support
    // -----------------------------------------------------------------------

    /// <summary>
    /// Parses a raw IMG image file and returns its structure.
    /// </summary>
    public static ImgImage ParseImgImage(string imgFilePath)
    {
        return ImgParser.Parse(imgFilePath);
    }

    /// <summary>
    /// Asynchronously parses a raw IMG image file.
    /// </summary>
    public static async Task<ImgImage> ParseImgImageAsync(string imgFilePath,
        CancellationToken ct = default)
    {
        return await ImgParser.ParseAsync(imgFilePath, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Extracts user data from an IMG image to an ISO file.
    /// </summary>
    public static void ExtractImgToIso(string imgFilePath, string outputIsoPath,
        IProgress<int>? progress = null)
    {
        var image = ImgParser.Parse(imgFilePath);
        if (!image.IsValid)
            throw new InvalidOperationException($"IMG parse error: {image.ErrorMessage}");

        ImgParser.ExtractToIso(imgFilePath, image, outputIsoPath, progress);
    }

    /// <summary>
    /// Asynchronously extracts user data from an IMG image to an ISO file.
    /// </summary>
    public static async Task ExtractImgToIsoAsync(string imgFilePath, string outputIsoPath,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var image = await ImgParser.ParseAsync(imgFilePath, ct).ConfigureAwait(false);
        if (!image.IsValid)
            throw new InvalidOperationException($"IMG parse error: {image.ErrorMessage}");

        await ImgParser.ExtractToIsoAsync(imgFilePath, image, outputIsoPath, progress, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Validates an IMG image for integrity.
    /// </summary>
    public static List<string> ValidateImgImage(string imgFilePath)
    {
        var image = ImgParser.Parse(imgFilePath);
        if (!image.IsValid)
            return new List<string> { $"Parse error: {image.ErrorMessage}" };

        return ImgParser.ValidateImage(image);
    }

    /// <summary>
    /// Asynchronously validates an IMG image.
    /// </summary>
    public static async Task<List<string>> ValidateImgImageAsync(string imgFilePath,
        CancellationToken ct = default)
    {
        var image = await ImgParser.ParseAsync(imgFilePath, ct).ConfigureAwait(false);
        if (!image.IsValid)
            return new List<string> { $"Parse error: {image.ErrorMessage}" };

        return await ImgParser.ValidateImageAsync(image, ct).ConfigureAwait(false);
    }
}
