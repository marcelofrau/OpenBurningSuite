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
                throw new ArgumentException("DVD-Video 制作至少需要一个标题集。");
            foreach (var ts in job.DvdAuthoring.TitleSets)
                foreach (var title in ts.Titles)
                {
                    if (string.IsNullOrWhiteSpace(title.VideoPath))
                        throw new ArgumentException("DVD 标题的视频路径不能为空。");
                    if (!job.DvdAuthoring.TranscodeVideo && !File.Exists(title.VideoPath))
                        throw new FileNotFoundException($"未找到视频文件：{title.VideoPath}");
                }
        }
        else if (job.BluRayAuthoring != null)
        {
            // Blu-ray authoring: validate playlists have play items
            if (job.BluRayAuthoring.Playlists.Count == 0)
                throw new ArgumentException("蓝光制作至少需要一个播放列表。");
            foreach (var pl in job.BluRayAuthoring.Playlists)
                foreach (var item in pl.PlayItems)
                {
                    if (string.IsNullOrWhiteSpace(item.VideoPath))
                        throw new ArgumentException("蓝光播放项的视频路径不能为空。");
                    if (!job.BluRayAuthoring.TranscodeVideo && !File.Exists(item.VideoPath))
                        throw new FileNotFoundException($"未找到视频文件：{item.VideoPath}");
                }
        }
        else if (job.BdavAuthoring != null)
        {
            // BDAV authoring: validate playlists have play items
            if (job.BdavAuthoring.Playlists.Count == 0)
                throw new ArgumentException("BDAV 制作至少需要一个播放列表。");
            foreach (var pl in job.BdavAuthoring.Playlists)
                foreach (var item in pl.PlayItems)
                {
                    if (string.IsNullOrWhiteSpace(item.VideoPath))
                        throw new ArgumentException("BDAV 播放项的视频路径不能为空。");
                    if (!job.BdavAuthoring.TranscodeVideo && !File.Exists(item.VideoPath))
                        throw new FileNotFoundException($"未找到视频文件：{item.VideoPath}");
                }
        }
        else if (FormatHelper.IsVcdOrSvcd(job.DiscType))
        {
            // VCD/SVCD uses VideoFiles instead of SourceFolder
            if (job.VideoFiles.Count == 0)
                throw new ArgumentException($"{job.DiscType} 构建至少需要一个 MPEG 视频文件。");
            foreach (var videoFile in job.VideoFiles)
            {
                if (string.IsNullOrWhiteSpace(videoFile))
                    throw new ArgumentException("视频文件路径不能为空。");
                if (!File.Exists(videoFile))
                    throw new FileNotFoundException($"未找到视频文件：{videoFile}");
            }
        }
        else if (job.AudioTracks.Count > 0)
        {
            foreach (var track in job.AudioTracks)
            {
                if (string.IsNullOrWhiteSpace(track.FilePath))
                    throw new ArgumentException("音轨文件路径不能为空。");
                if (!File.Exists(track.FilePath))
                    throw new FileNotFoundException($"未找到音轨文件：{track.FilePath}");
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(job.SourceFolder))
                throw new ArgumentException("数据光盘构建的源文件夹不能为空。");
            if (!Directory.Exists(job.SourceFolder))
                throw new DirectoryNotFoundException($"未找到源文件夹：{job.SourceFolder}");
        }

        if (string.IsNullOrWhiteSpace(job.OutputImagePath))
            throw new ArgumentException("输出镜像路径不能为空。");

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
            StatusMessage = "正在开始 DVD-Video 制作…",
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
                StatusMessage = "正在创建 DVD ISO 镜像…",
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
                StatusMessage = "DVD-Video 镜像完成。",
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
            StatusMessage = "正在开始蓝光制作…",
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
                StatusMessage = "正在创建蓝光 ISO 镜像…",
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
                StatusMessage = "蓝光光盘镜像完成。",
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
            StatusMessage = $"正在开始 {formatDesc} 制作…",
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
                StatusMessage = $"正在创建 {formatDesc} ISO 镜像…",
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
                StatusMessage = $"{formatDesc} 光盘镜像完成。",
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
            StatusMessage = $"正在构建 {job.DiscType} ISO 镜像（XDVDFS）…",
            LogLine = $"[Info] Creating XDVDFS image from: {job.SourceFolder}"
        });

        ct.ThrowIfCancellationRequested();

        var error = await Task.Run(() => XisoService.Create(job.SourceFolder, job.OutputImagePath), ct);

        if (!string.IsNullOrEmpty(error))
            throw new InvalidOperationException($"Xbox ISO 创建失败：{error}");

        progress.Report(new BuildProgress
        {
            PercentComplete = 100,
            StatusMessage = "Xbox ISO 镜像构建完成。",
            LogLine = $"[Info] XDVDFS image created: {job.OutputImagePath}"
        });
    }
}
