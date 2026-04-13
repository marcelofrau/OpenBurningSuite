// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OpenBurningSuite.Models;
using OpenBurningSuite.Native.Audio;
using OpenBurningSuite.Native.Iso;
using OpenBurningSuite.Native.Video;
using OpenBurningSuite.Helpers;

namespace OpenBurningSuite.Services;

/// <summary>Builds disc images using native ISO builder, audio CD builder, and video authoring builders.</summary>
public class BuildService
{
    private readonly IsoBuilder _isoBuilder = new();
    private readonly VcdBuilder _vcdBuilder = new();
    private readonly DvdAuthoringBuilder _dvdAuthoringBuilder = new();
    private readonly BluRayAuthoringBuilder _bluRayAuthoringBuilder = new();

    public BuildService() { }

    public async Task BuildImageAsync(
        BuildJob job,
        IProgress<BuildProgress> progress,
        CancellationToken cancellationToken)
    {
        // Validate inputs before starting
        if (job.DvdAuthoring != null)
        {
            // DVD-Video authoring: validate title sets have video files
            if (job.DvdAuthoring.TitleSets.Count == 0)
                throw new ArgumentException("At least one title set is required for DVD-Video authoring.");
            foreach (var ts in job.DvdAuthoring.TitleSets)
                foreach (var title in ts.Titles)
                {
                    if (string.IsNullOrWhiteSpace(title.VideoPath))
                        throw new ArgumentException("Video path must not be empty for DVD titles.");
                    if (!job.DvdAuthoring.TranscodeVideo && !File.Exists(title.VideoPath))
                        throw new FileNotFoundException($"Video file not found: {title.VideoPath}");
                }
        }
        else if (job.BluRayAuthoring != null)
        {
            // Blu-ray authoring: validate playlists have play items
            if (job.BluRayAuthoring.Playlists.Count == 0)
                throw new ArgumentException("At least one playlist is required for Blu-ray authoring.");
            foreach (var pl in job.BluRayAuthoring.Playlists)
                foreach (var item in pl.PlayItems)
                {
                    if (string.IsNullOrWhiteSpace(item.VideoPath))
                        throw new ArgumentException("Video path must not be empty for Blu-ray play items.");
                    if (!job.BluRayAuthoring.TranscodeVideo && !File.Exists(item.VideoPath))
                        throw new FileNotFoundException($"Video file not found: {item.VideoPath}");
                }
        }
        else if (job.BdavAuthoring != null)
        {
            // BDAV authoring: validate playlists have play items
            if (job.BdavAuthoring.Playlists.Count == 0)
                throw new ArgumentException("At least one playlist is required for BDAV authoring.");
            foreach (var pl in job.BdavAuthoring.Playlists)
                foreach (var item in pl.PlayItems)
                {
                    if (string.IsNullOrWhiteSpace(item.VideoPath))
                        throw new ArgumentException("Video path must not be empty for BDAV play items.");
                    if (!job.BdavAuthoring.TranscodeVideo && !File.Exists(item.VideoPath))
                        throw new FileNotFoundException($"Video file not found: {item.VideoPath}");
                }
        }
        else if (FormatHelper.IsVcdOrSvcd(job.DiscType))
        {
            // VCD/SVCD uses VideoFiles instead of SourceFolder
            if (job.VideoFiles.Count == 0)
                throw new ArgumentException($"At least one MPEG video file is required for {job.DiscType} builds.");
            foreach (var videoFile in job.VideoFiles)
            {
                if (string.IsNullOrWhiteSpace(videoFile))
                    throw new ArgumentException("Video file path must not be empty.");
                if (!File.Exists(videoFile))
                    throw new FileNotFoundException($"Video file not found: {videoFile}");
            }
        }
        else if (job.AudioTracks.Count > 0)
        {
            foreach (var track in job.AudioTracks)
            {
                if (string.IsNullOrWhiteSpace(track.FilePath))
                    throw new ArgumentException("Audio track file path must not be empty.");
                if (!File.Exists(track.FilePath))
                    throw new FileNotFoundException($"Audio track file not found: {track.FilePath}");
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(job.SourceFolder))
                throw new ArgumentException("Source folder must not be empty for data disc builds.");
            if (!Directory.Exists(job.SourceFolder))
                throw new DirectoryNotFoundException($"Source folder not found: {job.SourceFolder}");
        }

        if (string.IsNullOrWhiteSpace(job.OutputImagePath))
            throw new ArgumentException("Output image path must not be empty.");

        job.Status = BuildStatus.Running;
        job.StartedAt = DateTime.Now;

        try
        {
            if (job.DvdAuthoring != null)
                await BuildDvdVideoAsync(job, progress, cancellationToken);
            else if (job.BluRayAuthoring != null)
                await BuildBluRayAsync(job, progress, cancellationToken);
            else if (job.BdavAuthoring != null)
                await BuildBdavAsync(job, progress, cancellationToken);
            else if (FormatHelper.IsVcdOrSvcd(job.DiscType))
                await _vcdBuilder.BuildVcdImageAsync(job, progress, cancellationToken);
            else if (job.AudioTracks.Count > 0)
                await BuildAudioCDAsync(job, progress, cancellationToken);
            else if (job.DiscType is "Xbox DVD" or "Xbox 360 DVD")
                await BuildXboxIsoAsync(job, progress, cancellationToken);
            else
                await _isoBuilder.BuildDataImageAsync(job, progress, cancellationToken);

            job.Status = BuildStatus.Completed;
        }
        catch (OperationCanceledException)
        {
            job.Status = BuildStatus.Cancelled;
            throw;
        }
        catch
        {
            job.Status = BuildStatus.Failed;
            throw;
        }
        finally
        {
            job.FinishedAt = DateTime.Now;
        }
    }

    // -----------------------------------------------------------------------
    // Audio CD (Red Book BIN/CUE + TOC generation)
    // -----------------------------------------------------------------------
    private static async Task BuildAudioCDAsync(
        BuildJob job,
        IProgress<BuildProgress> progress,
        CancellationToken ct)
    {
        // Use the full Red Book AudioCdBuilder for BIN/CUE output
        await AudioCdBuilder.BuildAsync(job, progress, ct);
    }

    // -----------------------------------------------------------------------
    // DVD-Video authoring (VIDEO_TS structure + optional ISO image)
    // -----------------------------------------------------------------------
    private async Task BuildDvdVideoAsync(
        BuildJob job,
        IProgress<BuildProgress> progress,
        CancellationToken ct)
    {
        var dvdJob = job.DvdAuthoring!;

        progress.Report(new BuildProgress
        {
            PercentComplete = 1,
            StatusMessage = "Starting DVD-Video authoring...",
            LogLine = $"[Info] DVD-Video authoring: {dvdJob.TitleSets.Count} title set(s), {dvdJob.VideoStandard}"
        });

        // Build the VIDEO_TS directory structure
        var outputDir = Path.GetDirectoryName(job.OutputImagePath) ?? Path.GetTempPath();
        var dvdDir = Path.Combine(outputDir, "dvd_authoring_" + Path.GetFileNameWithoutExtension(job.OutputImagePath));
        Directory.CreateDirectory(dvdDir);

        try
        {
            // Phase 1: Build VIDEO_TS structure
            var videoTsDir = await _dvdAuthoringBuilder.BuildAsync(dvdJob, dvdDir, progress, ct);

            progress.Report(new BuildProgress
            {
                PercentComplete = 80,
                StatusMessage = "Creating DVD ISO image...",
                LogLine = "[Info] Building ISO 9660 + UDF image from VIDEO_TS"
            });

            // Phase 2: Build ISO image from the DVD directory structure
            var isoBuildJob = new BuildJob
            {
                SourceFolder = dvdDir,
                OutputImagePath = job.OutputImagePath,
                VolumeLabel = dvdJob.VolumeLabel,
                FileSystem = "ISO 9660 + UDF",
                UdfVersion = "1.02",
                DiscType = job.DiscType,
                JolietLongFilenames = false, // DVD-Video doesn't use Joliet
                DeepDirectoryNesting = false
            };

            await _isoBuilder.BuildDataImageAsync(isoBuildJob, progress, ct);

            progress.Report(new BuildProgress
            {
                PercentComplete = 100,
                StatusMessage = "DVD-Video image complete.",
                LogLine = $"[Info] DVD-Video ISO written: {job.OutputImagePath}"
            });
        }
        finally
        {
            // Clean up temporary directory
            try
            {
                if (Directory.Exists(dvdDir))
                    Directory.Delete(dvdDir, recursive: true);
            }
            catch (Exception ex)
            {
                // Best effort cleanup — log but don't fail the build
                System.Diagnostics.Debug.WriteLine(
                    $"[BuildService] Failed to clean up temp directory '{dvdDir}': {ex.Message}");
            }
        }
    }

    // -----------------------------------------------------------------------
    // Blu-ray authoring (BDMV structure + optional ISO image)
    // -----------------------------------------------------------------------
    private async Task BuildBluRayAsync(
        BuildJob job,
        IProgress<BuildProgress> progress,
        CancellationToken ct)
    {
        var bdJob = job.BluRayAuthoring!;

        progress.Report(new BuildProgress
        {
            PercentComplete = 1,
            StatusMessage = "Starting Blu-ray authoring...",
            LogLine = $"[Info] Blu-ray authoring: {bdJob.Playlists.Count} playlist(s), {bdJob.VideoFormat} {bdJob.VideoCodec}"
        });

        var outputDir = Path.GetDirectoryName(job.OutputImagePath) ?? Path.GetTempPath();
        var bdDir = Path.Combine(outputDir, "bd_authoring_" + Path.GetFileNameWithoutExtension(job.OutputImagePath));
        Directory.CreateDirectory(bdDir);

        try
        {
            // Phase 1: Build BDMV structure
            var bdmvDir = await _bluRayAuthoringBuilder.BuildAsync(bdJob, bdDir, progress, ct);

            progress.Report(new BuildProgress
            {
                PercentComplete = 80,
                StatusMessage = "Creating Blu-ray ISO image...",
                LogLine = "[Info] Building UDF 2.50 image from BDMV structure"
            });

            // Phase 2: Build ISO image from the Blu-ray directory structure
            var isoBuildJob = new BuildJob
            {
                SourceFolder = bdDir,
                OutputImagePath = job.OutputImagePath,
                VolumeLabel = bdJob.VolumeLabel,
                FileSystem = "UDF 2.50",
                UdfVersion = "2.50",
                DiscType = job.DiscType,
                JolietLongFilenames = false
            };

            await _isoBuilder.BuildDataImageAsync(isoBuildJob, progress, ct);

            progress.Report(new BuildProgress
            {
                PercentComplete = 100,
                StatusMessage = "Blu-ray disc image complete.",
                LogLine = $"[Info] Blu-ray ISO written: {job.OutputImagePath}"
            });
        }
        finally
        {
            try
            {
                if (Directory.Exists(bdDir))
                    Directory.Delete(bdDir, recursive: true);
            }
            catch (Exception ex)
            {
                // Best effort cleanup — log but don't fail the build
                System.Diagnostics.Debug.WriteLine(
                    $"[BuildService] Failed to clean up temp directory '{bdDir}': {ex.Message}");
            }
        }
    }

    // -----------------------------------------------------------------------
    // BDAV (Blu-ray Audio/Visual recording) authoring
    // -----------------------------------------------------------------------
    private async Task BuildBdavAsync(
        BuildJob job,
        IProgress<BuildProgress> progress,
        CancellationToken ct)
    {
        var bdavJob = job.BdavAuthoring!;

        var formatDesc = bdavJob.Stereoscopic3D ? "BDAV 3D" : "BDAV";
        progress.Report(new BuildProgress
        {
            PercentComplete = 1,
            StatusMessage = $"Starting {formatDesc} authoring...",
            LogLine = $"[Info] {formatDesc} authoring: {bdavJob.Playlists.Count} playlist(s), {bdavJob.VideoFormat} {bdavJob.VideoCodec}"
        });

        var outputDir = Path.GetDirectoryName(job.OutputImagePath) ?? Path.GetTempPath();
        var bdavDir = Path.Combine(outputDir, "bdav_authoring_" + Path.GetFileNameWithoutExtension(job.OutputImagePath));
        Directory.CreateDirectory(bdavDir);

        try
        {
            // Phase 1: Build BDAV structure (uses same builder with IsBdav=true)
            var bdavStructDir = await _bluRayAuthoringBuilder.BuildAsync(bdavJob, bdavDir, progress, ct);

            progress.Report(new BuildProgress
            {
                PercentComplete = 80,
                StatusMessage = $"Creating {formatDesc} ISO image...",
                LogLine = $"[Info] Building UDF 2.50 image from {formatDesc} structure"
            });

            // Phase 2: Build UDF 2.50 ISO image from the BDAV directory structure
            var isoBuildJob = new BuildJob
            {
                SourceFolder = bdavDir,
                OutputImagePath = job.OutputImagePath,
                VolumeLabel = bdavJob.VolumeLabel,
                FileSystem = "UDF 2.50",
                UdfVersion = "2.50",
                DiscType = job.DiscType,
                JolietLongFilenames = false
            };

            await _isoBuilder.BuildDataImageAsync(isoBuildJob, progress, ct);

            progress.Report(new BuildProgress
            {
                PercentComplete = 100,
                StatusMessage = $"{formatDesc} disc image complete.",
                LogLine = $"[Info] {formatDesc} ISO written: {job.OutputImagePath}"
            });
        }
        finally
        {
            try
            {
                if (Directory.Exists(bdavDir))
                    Directory.Delete(bdavDir, recursive: true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[BuildService] Failed to clean up temp directory '{bdavDir}': {ex.Message}");
            }
        }
    }

    // -----------------------------------------------------------------------
    // Xbox ISO building (XDVDFS format via XisoService)
    // -----------------------------------------------------------------------
    private static async Task BuildXboxIsoAsync(
        BuildJob job,
        IProgress<BuildProgress> progress,
        CancellationToken ct)
    {
        progress.Report(new BuildProgress
        {
            PercentComplete = 1,
            StatusMessage = $"Building {job.DiscType} ISO (XDVDFS)...",
            LogLine = $"[Info] Creating XDVDFS image from: {job.SourceFolder}"
        });

        ct.ThrowIfCancellationRequested();

        var error = await Task.Run(() => XisoService.Create(job.SourceFolder, job.OutputImagePath), ct);

        if (!string.IsNullOrEmpty(error))
            throw new InvalidOperationException($"Xbox ISO creation failed: {error}");

        progress.Report(new BuildProgress
        {
            PercentComplete = 100,
            StatusMessage = "Xbox ISO build complete.",
            LogLine = $"[Info] XDVDFS image created: {job.OutputImagePath}"
        });
    }
}
