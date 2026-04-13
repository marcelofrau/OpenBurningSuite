// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenBurningSuite.Models;

namespace OpenBurningSuite.Native.Video;

/// <summary>
/// Builds Blu-ray Disc (BDMV/BDAV) compliant file structures per Blu-ray Disc Association
/// specification (System Description Blu-ray Disc Read-Only Format Part 3 / Part 3-2).
///
/// BDMV (Movie) disc structure:
///   BDMV/
///     index.bdmv           - Top-level index table (titles, first play)
///     MovieObject.bdmv     - Movie object navigation commands
///     PLAYLIST/
///       00000.mpls         - Movie playlist files
///     CLIPINF/
///       00001.clpi         - Clip information files
///     STREAM/
///       00001.m2ts         - MPEG-2 Transport Stream files
///       SSIF/              - Stereoscopic Interleaved Files (3D only)
///     AUXDATA/             - Auxiliary data (sound.bdmv, font files)
///     META/                - Metadata (thumbnail images, disc library)
///     BACKUP/
///       index.bdmv         - Backup of index
///       MovieObject.bdmv   - Backup of movie object
///       PLAYLIST/          - Backup of playlists
///       CLIPINF/           - Backup of clip info
///   CERTIFICATE/           - AACS certificates (optional)
///
/// BDAV (Audio/Visual recording) disc structure:
///   BDAV/
///     index.bdmv           - Simplified index (no movie objects for basic BDAV)
///     MovieObject.bdmv     - Minimal movie object
///     PLAYLIST/
///       00000.mpls         - Recording playlists
///     CLIPINF/
///       00001.clpi         - Clip information with recording metadata
///     STREAM/
///       00001.m2ts         - MPEG-2 Transport Stream recordings
///
/// 3D Blu-ray (Profile 5.0) additions:
///   - SSIF directory: Stereoscopic Interleaved Files (.ssif) combining base+dependent views
///   - CLPI: Extent start points for 3D interleaved access
///   - MPLS: STN_table_SS for stereoscopic stream table
///   - index.bdmv: initial_output_mode_preference = 3D
///   - MVC (Multiview Video Coding) H.264 extension for frame-packed 3D
///
/// index.bdmv structure:
///   - Type indicator "INDX" + version "0200"
///   - AppInfoBDMV: initial output mode, content exist flag
///   - Indexes: First Playback, Top Menu, Title entries
///
/// MovieObject.bdmv structure:
///   - Type indicator "MOBJ" + version "0200"
///   - Movie objects with navigation commands (HDMV instructions)
///
/// MPLS structure:
///   - Type indicator "MPLS" + version "0200"
///   - AppInfoPlayList: playback type, playback count
///   - PlayList: play items, sub-paths
///   - PlayListMark: chapter marks
///   - ExtensionData: STN_table_SS for 3D (when stereoscopic)
///
/// CLPI structure:
///   - Type indicator "HDMV" + version "0200"
///   - ClipInfo: clip type, stream coding info
///   - SequenceInfo: ATC/STC sequences
///   - ProgramInfo: stream PIDs and attributes
///   - CPI: Characteristic Point Information (entry points)
///   - ExtensionData: extent_start_point for 3D SSIF access
///
/// M2TS files use MPEG-2 Transport Stream with 192-byte packets
/// (4-byte Blu-ray header + 188-byte TS packet).
/// </summary>
public sealed class BluRayAuthoringBuilder
{
    /// <summary>Blu-ray TS packet size (4-byte header + 188-byte TS packet).</summary>
    private const int BdTsPacketSize = 192;

    /// <summary>Standard MPEG-2 TS packet size.</summary>
    private const int TsPacketSize = 188;

    /// <summary>TS sync byte.</summary>
    private const byte TsSyncByte = 0x47;

    /// <summary>
    /// Typical Blu-ray packets per second at ~40 Mbps bitrate.
    /// Calculation: 40,000,000 bits/sec ÷ (192 bytes/packet × 8 bits/byte) ≈ 26,042.
    /// Rounded to 26,595 to account for overhead and timing accuracy.
    /// </summary>
    private const int TypicalBdPacketsPerSecond = 26595;

    /// <summary>MVC dependent view stream type (0x20) per MPEG-2 Systems for H.264 MVC.</summary>
    private const byte MvcStreamType = 0x20;

    /// <summary>MVC dependent view PID for stereoscopic 3D.</summary>
    private const ushort MvcDependentViewPid = 0x1012;

    /// <summary>
    /// Builds a complete BDMV or BDAV file structure from the authoring job configuration.
    /// When <see cref="BluRayAuthoringJob.IsBdav"/> is true, builds a BDAV recording structure.
    /// When <see cref="BluRayAuthoringJob.Stereoscopic3D"/> is true, adds 3D SSIF and metadata.
    /// </summary>
    /// <param name="job">Blu-ray authoring job configuration.</param>
    /// <param name="outputDir">Output directory where BDMV/BDAV will be created.</param>
    /// <param name="progress">Progress reporter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Path to the BDMV/BDAV directory.</returns>
    public async Task<string> BuildAsync(
        BluRayAuthoringJob job,
        string outputDir,
        IProgress<BuildProgress> progress,
        CancellationToken ct)
    {
        ValidateJob(job);

        // Use BDAV directory when building a recording format disc
        var rootDirName = job.IsBdav ? "BDAV" : "BDMV";
        var bdmvDir = Path.Combine(outputDir, rootDirName);
        var streamDir = Path.Combine(bdmvDir, "STREAM");
        var clipinfDir = Path.Combine(bdmvDir, "CLIPINF");
        var playlistDir = Path.Combine(bdmvDir, "PLAYLIST");
        var auxdataDir = Path.Combine(bdmvDir, "AUXDATA");
        var metaDir = Path.Combine(bdmvDir, "META");
        var backupDir = Path.Combine(bdmvDir, "BACKUP");
        var certDir = Path.Combine(outputDir, "CERTIFICATE");

        // SSIF directory for 3D Blu-ray stereoscopic interleaved files
        var ssifDir = Path.Combine(streamDir, "SSIF");

        Directory.CreateDirectory(streamDir);
        Directory.CreateDirectory(clipinfDir);
        Directory.CreateDirectory(playlistDir);
        if (!job.IsBdav) // BDAV recording format omits AUXDATA/META
        {
            Directory.CreateDirectory(auxdataDir);
            Directory.CreateDirectory(metaDir);
        }
        if (job.CreateBackup && !job.IsBdav) // BDAV does not require BACKUP
        {
            Directory.CreateDirectory(Path.Combine(backupDir, "PLAYLIST"));
            Directory.CreateDirectory(Path.Combine(backupDir, "CLIPINF"));
        }
        if (!job.IsBdav)
            Directory.CreateDirectory(certDir);

        // Create SSIF directory for 3D stereoscopic content
        if (job.Stereoscopic3D)
            Directory.CreateDirectory(ssifDir);

        var formatLabel = job.IsBdav ? "BDAV" : (job.Stereoscopic3D ? "BDMV 3D" :
            (job.UltraHd ? "UHD BDMV" : "BDMV"));

        progress.Report(new BuildProgress
        {
            PercentComplete = 2,
            StatusMessage = $"Building {formatLabel} disc structure...",
            LogLine = $"[Info] Creating {rootDirName} directory: {bdmvDir}"
        });

        // Phase 1: Transcode/mux video files to M2TS
        var clipInfos = new List<ClipInfo>();
        var transcodedPaths = new List<string>();

        int totalPlayItems = 0;
        foreach (var pl in job.Playlists)
            totalPlayItems += pl.PlayItems.Count;

        int itemsProcessed = 0;
        int clipIndex = 1;

        try
        {
            foreach (var playlist in job.Playlists)
            {
                foreach (var playItem in playlist.PlayItems)
                {
                    ct.ThrowIfCancellationRequested();

                    var clipId = $"{clipIndex:D5}";
                    var m2tsPath = Path.Combine(streamDir, $"{clipId}.m2ts");

                    if (job.TranscodeVideo)
                    {
                        // Transcode to Blu-ray compliant M2TS
                        var tempPath = Path.Combine(Path.GetTempPath(), $"obs_bd_{Guid.NewGuid():N}.m2ts");
                        transcodedPaths.Add(tempPath);

                        string audioCodec = playItem.AudioStreams.Count > 0
                            ? playItem.AudioStreams[0].Codec : "AC3";
                        int audioBitrate = playItem.AudioStreams.Count > 0
                            ? playItem.AudioStreams[0].BitrateKbps : 640;
                        int audioChannels = playItem.AudioStreams.Count > 0
                            ? playItem.AudioStreams[0].Channels : 2;

                        if (job.UltraHd)
                        {
                            // UHD Blu-ray: HEVC Main10 + HDR10 metadata
                            await VideoTranscoder.TranscodeContainerToUhdBluRayAsync(
                                playItem.VideoPath, tempPath,
                                job.VideoFormat, job.FrameRate,
                                job.VideoBitrateKbps, audioCodec, audioBitrate, audioChannels,
                                job.HdrMode, job.ColorSpace,
                                job.HdrMaxContentLightLevel, job.HdrMaxFrameAverageLightLevel,
                                progress, ct);
                        }
                        else
                        {
                            await VideoTranscoder.TranscodeContainerToBluRayAsync(
                                playItem.VideoPath, tempPath,
                                job.VideoCodec, job.VideoFormat, job.FrameRate,
                                job.VideoBitrateKbps, audioCodec, audioBitrate, audioChannels,
                                progress, ct);
                        }

                        // Convert standard TS to Blu-ray TS (add 4-byte headers)
                        await ConvertToBluRayTsAsync(tempPath, m2tsPath, ct);
                    }
                    else
                    {
                        // Input is already compliant — copy or wrap in BD TS
                        if (IsBluRayTs(playItem.VideoPath))
                        {
                            File.Copy(playItem.VideoPath, m2tsPath, overwrite: true);
                        }
                        else
                        {
                            await ConvertToBluRayTsAsync(playItem.VideoPath, m2tsPath, ct);
                        }
                    }

                    // Get clip duration
                    var clipDuration = await GetClipDurationAsync(m2tsPath, ct);

                    // Generate SSIF for 3D stereoscopic content
                    string? ssifPath = null;
                    string? depViewM2tsPath = null;
                    List<SsifExtentStartPoint>? ssifExtentStartPoints = null;
                    if (job.Stereoscopic3D)
                    {
                        var depClipId = $"{clipIndex + 10000:D5}";
                        depViewM2tsPath = Path.Combine(streamDir, $"{depClipId}.m2ts");

                        if (job.Stereoscopic3DMode.Equals("FramePacked", StringComparison.OrdinalIgnoreCase)
                            && !string.IsNullOrWhiteSpace(job.DependentViewPath))
                        {
                            // Frame-packed MVC: transcode or copy dependent view
                            if (job.TranscodeVideo)
                            {
                                var tempDepPath = Path.Combine(Path.GetTempPath(), $"obs_bd3d_dep_{Guid.NewGuid():N}.m2ts");
                                transcodedPaths.Add(tempDepPath);
                                await VideoTranscoder.TranscodeContainerToBluRay3DAsync(
                                    job.DependentViewPath, tempDepPath,
                                    job.VideoFormat, job.FrameRate,
                                    job.VideoBitrateKbps, "MVC",
                                    progress, ct);
                                await ConvertToBluRayTsAsync(tempDepPath, depViewM2tsPath, ct);
                            }
                            else
                            {
                                if (IsBluRayTs(job.DependentViewPath))
                                    File.Copy(job.DependentViewPath, depViewM2tsPath, overwrite: true);
                                else
                                    await ConvertToBluRayTsAsync(job.DependentViewPath, depViewM2tsPath, ct);
                            }
                        }
                        else
                        {
                            // SideBySide or TopBottom: dependent view is derived from same source
                            // Create a copy reference (player handles frame-compatible extraction)
                            File.Copy(m2tsPath, depViewM2tsPath, overwrite: true);
                        }

                        // Build SSIF: interleave base view + dependent view into .ssif file
                        ssifPath = Path.Combine(ssifDir, $"{clipId}.ssif");
                        ssifExtentStartPoints = await BuildSsifAsync(m2tsPath, depViewM2tsPath, ssifPath, ct);

                        progress.Report(new BuildProgress
                        {
                            StatusMessage = $"Generated 3D SSIF for clip {clipId}",
                            LogLine = $"[Info] 3D SSIF: {clipId}.ssif ({job.Stereoscopic3DMode}, {ssifExtentStartPoints.Count} extents)"
                        });
                    }

                    clipInfos.Add(new ClipInfo
                    {
                        ClipId = clipId,
                        M2tsPath = m2tsPath,
                        Duration = clipDuration,
                        PlayItem = playItem,
                        SsifPath = ssifPath,
                        DependentViewM2tsPath = depViewM2tsPath,
                        Is3D = job.Stereoscopic3D,
                        SsifExtentStartPoints = ssifExtentStartPoints
                    });

                    itemsProcessed++;
                    int pct = 2 + itemsProcessed * 50 / totalPlayItems;
                    progress.Report(new BuildProgress
                    {
                        PercentComplete = pct,
                        StatusMessage = $"Processed clip {itemsProcessed}/{totalPlayItems}",
                        LogLine = $"[Info] Clip {clipId}: {Path.GetFileName(playItem.VideoPath)} → {clipId}.m2ts"
                    });

                    clipIndex++;
                }
            }

            // Phase 2: Generate CLPI files
            progress.Report(new BuildProgress
            {
                PercentComplete = 55,
                StatusMessage = "Generating clip information files...",
                LogLine = "[Info] Writing CLPI (Clip Information) files"
            });

            foreach (var clip in clipInfos)
            {
                ct.ThrowIfCancellationRequested();
                var clpiData = BuildClpi(clip, job);
                var clpiPath = Path.Combine(clipinfDir, $"{clip.ClipId}.clpi");
                await File.WriteAllBytesAsync(clpiPath, clpiData, ct);

                if (job.CreateBackup && !job.IsBdav)
                {
                    var backupClpiPath = Path.Combine(backupDir, "CLIPINF", $"{clip.ClipId}.clpi");
                    await File.WriteAllBytesAsync(backupClpiPath, clpiData, ct);
                }
            }

            // Phase 3: Generate MPLS files
            progress.Report(new BuildProgress
            {
                PercentComplete = 65,
                StatusMessage = "Generating playlist files...",
                LogLine = "[Info] Writing MPLS (Movie Playlist) files"
            });

            int playlistIndex = 0;
            foreach (var playlist in job.Playlists)
            {
                ct.ThrowIfCancellationRequested();
                var mplsId = $"{playlistIndex:D5}";

                // Gather clips for this playlist
                var playlistClips = new List<ClipInfo>();
                int clipOffset = 0;
                for (int p = 0; p < playlistIndex; p++)
                    clipOffset += job.Playlists[p].PlayItems.Count;

                for (int i = 0; i < playlist.PlayItems.Count; i++)
                {
                    if (clipOffset + i < clipInfos.Count)
                        playlistClips.Add(clipInfos[clipOffset + i]);
                }

                var mplsData = BuildMpls(playlist, playlistClips, job);
                var mplsPath = Path.Combine(playlistDir, $"{mplsId}.mpls");
                await File.WriteAllBytesAsync(mplsPath, mplsData, ct);

                if (job.CreateBackup && !job.IsBdav)
                {
                    var backupMplsPath = Path.Combine(backupDir, "PLAYLIST", $"{mplsId}.mpls");
                    await File.WriteAllBytesAsync(backupMplsPath, mplsData, ct);
                }

                playlistIndex++;
            }

            // Phase 4: Generate index.bdmv
            progress.Report(new BuildProgress
            {
                PercentComplete = 78,
                StatusMessage = "Generating disc index...",
                LogLine = "[Info] Writing index.bdmv"
            });

            var indexData = BuildIndexBdmv(job);
            await File.WriteAllBytesAsync(Path.Combine(bdmvDir, "index.bdmv"), indexData, ct);

            if (job.CreateBackup && !job.IsBdav)
                await File.WriteAllBytesAsync(Path.Combine(backupDir, "index.bdmv"), indexData, ct);

            // Phase 5: Generate MovieObject.bdmv
            progress.Report(new BuildProgress
            {
                PercentComplete = 85,
                StatusMessage = "Generating movie objects...",
                LogLine = "[Info] Writing MovieObject.bdmv"
            });

            var mobjData = BuildMovieObject(job);
            await File.WriteAllBytesAsync(Path.Combine(bdmvDir, "MovieObject.bdmv"), mobjData, ct);

            if (job.CreateBackup && !job.IsBdav)
                await File.WriteAllBytesAsync(Path.Combine(backupDir, "MovieObject.bdmv"), mobjData, ct);

            // Phase 6: Create auxiliary data (BDMV only; BDAV omits AUXDATA)
            if (!job.IsBdav)
            {
                progress.Report(new BuildProgress
                {
                    PercentComplete = 88,
                    StatusMessage = "Creating auxiliary data...",
                    LogLine = "[Info] Writing AUXDATA files"
                });

                await CreateSoundBdmvAsync(auxdataDir, ct);
            }

            // Phase 7: BD-J (Blu-ray Disc Java) interactive content
            if (job.EnableBdJ && !job.IsBdav && job.BdJApplications.Count > 0)
            {
                ct.ThrowIfCancellationRequested();

                progress.Report(new BuildProgress
                {
                    PercentComplete = 92,
                    StatusMessage = "Creating BD-J interactive content...",
                    LogLine = $"[Info] Building BD-J structure ({job.BdJApplications.Count} application(s), " +
                              $"Profile {job.BdJProfileVersion})"
                });

                var bdjoDir = Path.Combine(bdmvDir, "BDJO");
                var jarDir = Path.Combine(bdmvDir, "JAR");
                Directory.CreateDirectory(bdjoDir);
                Directory.CreateDirectory(jarDir);

                // Create backup directories for BD-J
                if (job.CreateBackup)
                {
                    Directory.CreateDirectory(Path.Combine(backupDir, "BDJO"));
                }

                ushort currentAppId = job.BdJInitialAppId;
                int bdjoIndex = 0;

                foreach (var app in job.BdJApplications)
                {
                    ct.ThrowIfCancellationRequested();

                    // Copy JAR files to BDMV/JAR/
                    var copiedJarNames = new List<string>();
                    foreach (var jarPath in app.JarPaths)
                    {
                        if (!File.Exists(jarPath))
                            throw new FileNotFoundException($"BD-J JAR file not found: {jarPath}");

                        var jarFileName = Path.GetFileName(jarPath);
                        var destJarPath = Path.Combine(jarDir, jarFileName);
                        if (!File.Exists(destJarPath))
                            File.Copy(jarPath, destJarPath, overwrite: true);
                        copiedJarNames.Add(jarFileName);
                    }

                    // Build classpath from JAR names
                    var classpath = app.ClasspathEntries.Count > 0
                        ? app.ClasspathEntries
                        : copiedJarNames;

                    // Generate BDJO file
                    var bdjoId = $"{bdjoIndex:D5}";
                    var bdjoData = BuildBdjo(job, app, currentAppId, classpath);
                    var bdjoPath = Path.Combine(bdjoDir, $"{bdjoId}.bdjo");
                    await File.WriteAllBytesAsync(bdjoPath, bdjoData, ct);

                    // Create backup
                    if (job.CreateBackup)
                    {
                        var backupBdjoPath = Path.Combine(backupDir, "BDJO", $"{bdjoId}.bdjo");
                        await File.WriteAllBytesAsync(backupBdjoPath, bdjoData, ct);
                    }

                    progress.Report(new BuildProgress
                    {
                        LogLine = $"[Info] BD-J app '{app.Name}': {bdjoId}.bdjo, " +
                                  $"class={app.InitialClass}, JARs={string.Join(",", copiedJarNames)}"
                    });

                    currentAppId++;
                    bdjoIndex++;
                }

                progress.Report(new BuildProgress
                {
                    LogLine = $"[Info] BD-J structure complete: {job.BdJApplications.Count} BDJO file(s), " +
                              $"{Directory.GetFiles(jarDir, "*.jar").Length} JAR file(s)"
                });
            }

            progress.Report(new BuildProgress
            {
                PercentComplete = 100,
                StatusMessage = $"{formatLabel} disc structure complete.",
                LogLine = $"[Info] {rootDirName} structure built: {bdmvDir}"
            });

            return bdmvDir;
        }
        finally
        {
            // Clean up transcoded temp files
            foreach (var tempFile in transcodedPaths)
            {
                try { if (File.Exists(tempFile)) File.Delete(tempFile); }
                catch { /* best effort */ }
            }
        }
    }

    /// <summary>Validates the Blu-ray authoring job.</summary>
    private static void ValidateJob(BluRayAuthoringJob job)
    {
        if (job.Playlists.Count == 0)
            throw new ArgumentException("At least one playlist is required for Blu-ray authoring.");

        if (job.Playlists.Count > 999)
            throw new ArgumentException("Blu-ray supports a maximum of 999 playlists.");

        foreach (var playlist in job.Playlists)
        {
            if (playlist.PlayItems.Count == 0)
                throw new ArgumentException("Each playlist must contain at least one play item.");

            if (playlist.PlayItems.Count > 999)
                throw new ArgumentException("Blu-ray supports a maximum of 999 play items per playlist.");

            foreach (var item in playlist.PlayItems)
            {
                if (string.IsNullOrWhiteSpace(item.VideoPath))
                    throw new ArgumentException("Video path must not be empty for Blu-ray play items.");

                if (item.AudioStreams.Count > 32)
                    throw new ArgumentException("Blu-ray supports a maximum of 32 audio streams per play item.");

                if (item.SubtitleStreams.Count > 255)
                    throw new ArgumentException("Blu-ray supports a maximum of 255 subtitle streams per play item.");
            }
        }

        if (job.UltraHd)
        {
            // UHD Blu-ray validation per BDA UHD specification
            var validFormats = new[] { "2160P", "1080P" };
            var format = job.VideoFormat.ToUpperInvariant();
            if (Array.IndexOf(validFormats, format) < 0)
                throw new ArgumentException(
                    $"UHD Blu-ray requires 2160p or 1080p video format, got: {job.VideoFormat}");

            // UHD BD mandates HEVC (H.265) codec
            if (!string.IsNullOrEmpty(job.VideoCodec) &&
                !job.VideoCodec.Equals("HEVC", StringComparison.OrdinalIgnoreCase) &&
                !job.VideoCodec.Equals("H265", StringComparison.OrdinalIgnoreCase) &&
                !job.VideoCodec.Equals("H.265", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(
                    $"UHD Blu-ray requires HEVC (H.265) video codec, got: {job.VideoCodec}");

            // UHD BD does not support 3D stereoscopic content
            if (job.Stereoscopic3D)
                throw new ArgumentException(
                    "UHD Blu-ray does not support 3D stereoscopic content. " +
                    "Disable Stereoscopic3D or use standard Blu-ray 3D instead.");

            // Validate HDR mode
            var validHdrModes = new[] { "HDR10", "DOLBYVISION", "HDR10PLUS", "HLG" };
            var hdrMode = job.HdrMode.ToUpperInvariant().Replace(" ", "").Replace("-", "");
            if (Array.IndexOf(validHdrModes, hdrMode) < 0)
                throw new ArgumentException(
                    $"Unsupported HDR mode: {job.HdrMode}. " +
                    "Supported modes: HDR10, DolbyVision, HDR10Plus, HLG.");

            // Validate UHD disc size (accept both "BD-66" and "UHD BD-66 (66 GB)" style strings)
            var discSizeUpper = job.UhdDiscSize.ToUpperInvariant();
            if (!discSizeUpper.Contains("BD-66") && !discSizeUpper.Contains("BD-100"))
                throw new ArgumentException(
                    $"Unsupported UHD disc size: {job.UhdDiscSize}. " +
                    "Supported sizes: BD-66 (66 GB), BD-100 (100 GB).");
        }
        else
        {
            var validFormats = new[] { "1080P", "1080I", "720P", "576P", "576I", "480P", "480I" };
            var format = job.VideoFormat.ToUpperInvariant();
            if (Array.IndexOf(validFormats, format) < 0)
                throw new ArgumentException($"Unsupported video format: {job.VideoFormat}");
        }

        // BD-J validation
        if (job.EnableBdJ)
        {
            if (job.IsBdav)
                throw new ArgumentException(
                    "BD-J interactive content is not supported with BDAV recording format. " +
                    "Use BDMV (movie) format instead.");

            if (job.BdJApplications.Count == 0)
                throw new ArgumentException(
                    "At least one BD-J application is required when EnableBdJ is true.");

            if (job.BdJApplications.Count > 9999)
                throw new ArgumentException(
                    "BD-J supports a maximum of 9,999 applications per disc.");

            var validProfiles = new[] { "1.0", "1.1", "2.0", "5.0", "6.0" };
            if (Array.IndexOf(validProfiles, job.BdJProfileVersion) < 0)
                throw new ArgumentException(
                    $"Unsupported BD-J profile version: {job.BdJProfileVersion}. " +
                    "Supported versions: 1.0, 1.1, 2.0, 5.0, 6.0.");

            if (job.BdJNetworkAccess && job.BdJProfileVersion == "1.0")
                throw new ArgumentException(
                    "BD-J network access requires Profile 2.0 or higher.");

            foreach (var app in job.BdJApplications)
            {
                if (app.JarPaths.Count == 0)
                    throw new ArgumentException(
                        $"BD-J application '{app.Name}' must have at least one JAR file.");

                if (string.IsNullOrWhiteSpace(app.InitialClass))
                    throw new ArgumentException(
                        $"BD-J application '{app.Name}' must specify an initial Xlet class.");

                if (app.Priority < 1 || app.Priority > 255)
                    throw new ArgumentException(
                        $"BD-J application '{app.Name}' priority must be 1-255, got: {app.Priority}");
            }
        }
    }

    // -----------------------------------------------------------------------
    // index.bdmv builder
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds the index.bdmv file containing the top-level disc index.
    /// Structure: Type Indicator (8) + AppInfoBDMV (34) + Indexes (variable)
    /// </summary>
    private byte[] BuildIndexBdmv(BluRayAuthoringJob job)
    {
        // Count additional BD-J titles not mapped to playlists
        int extraBdJTitles = 0;
        if (job.EnableBdJ)
        {
            extraBdJTitles = job.BdJApplications.Count(app =>
                app.PlaylistIndex < 0 || app.PlaylistIndex >= job.Playlists.Count);
        }

        // Estimate size: header + AppInfo + index table
        int indexTableSize = 12 + (job.Playlists.Count + extraBdJTitles) * 12 + 24; // First play + top menu + titles
        int totalSize = 40 + 34 + indexTableSize;
        totalSize = ((totalSize + 3) / 4) * 4; // Align to 4 bytes

        var data = new byte[totalSize];
        int offset = 0;

        // Type Indicator (8 bytes)
        Encoding.ASCII.GetBytes("INDX").CopyTo(data, offset); offset += 4;
        // Version: "0200" for standard BD, "0300" for UHD BD per BDA specification
        var version = job.UltraHd ? "0300" : "0200";
        Encoding.ASCII.GetBytes(version).CopyTo(data, offset); offset += 4;

        // Offsets (will be filled in)
        int appInfoStartOffset = offset;
        WriteBigEndian32(data, offset, 40); offset += 4; // AppInfoBDMV start address
        int indexStartOffset = offset;
        WriteBigEndian32(data, offset, 40 + 34); offset += 4; // Indexes start address

        // Extension data start address (0 = no extension)
        WriteBigEndian32(data, offset, 0); offset += 4;

        // Padding to 40 bytes
        while (offset < 40) data[offset++] = 0;

        // ----- AppInfoBDMV (34 bytes) -----
        int appInfoBase = offset;
        WriteBigEndian32(data, offset, 30); offset += 4; // Length of AppInfo data (excluding this field)

        // Initial output mode area (4 bytes)
        // Bit 7-6: initial_output_mode_preference (0=2D, 1=3D)
        data[offset] = job.Stereoscopic3D ? (byte)0x40 : (byte)0x00;
        offset += 4;

        // Content exist flag (4 bytes)
        data[offset] = 0x40; // Video content exists
        offset += 4;

        // Video format + frame rate (2 bytes)
        data[offset] = EncodeVideoFormatByte(job.VideoFormat, job.FrameRate);
        offset += 2;

        // Provider data (16 bytes)
        var providerBytes = Encoding.UTF8.GetBytes("OpenBurningSuite");
        Buffer.BlockCopy(providerBytes, 0, data, offset, Math.Min(providerBytes.Length, 16));
        offset += 16;

        // ----- Indexes -----
        int indexBase = offset;
        WriteBigEndian32(data, indexBase, (uint)(indexTableSize - 4)); offset += 4;

        // First Playback (12 bytes)
        data[offset] = 0x01; // First Playback type: Movie Object
        offset += 4;
        WriteBigEndian16(data, offset, 0); // First Play movie object index
        offset += 2;
        offset += 6; // Reserved

        // Top Menu (12 bytes)
        data[offset] = job.TopMenu != null ? (byte)0x01 : (byte)0x00; // Top Menu type
        offset += 4;
        WriteBigEndian16(data, offset, 1); // Top Menu movie object index
        offset += 2;
        offset += 6; // Reserved

        // Number of titles (including extra BD-J titles not mapped to playlists)
        WriteBigEndian16(data, offset, (ushort)(job.Playlists.Count + extraBdJTitles)); offset += 2;

        // Title entries (12 bytes each)
        for (int i = 0; i < job.Playlists.Count; i++)
        {
            // Title type: 0x01 = Movie Object (HDMV), 0x02 = BD-J Object
            // BD-J titles use type 0x02 and reference a BDJO index instead of a movie object.
            // Check if any BD-J application targets this playlist index.
            bool isBdJTitle = job.EnableBdJ && job.BdJApplications.Any(app => app.PlaylistIndex == i);
            data[offset] = isBdJTitle ? (byte)0x02 : (byte)0x01;
            offset += 4;
            WriteBigEndian16(data, offset, (ushort)(i + 2)); // Object index (0=first play, 1=top menu, 2+=titles)
            offset += 2;
            offset += 6; // Reserved
        }

        // BD-J title entries (additional titles for BD-J apps not mapped to playlists)
        if (job.EnableBdJ)
        {
            for (int j = 0; j < job.BdJApplications.Count; j++)
            {
                // If this BD-J app isn't already mapped to a playlist title, add a separate title
                if (job.BdJApplications[j].PlaylistIndex < 0 ||
                    job.BdJApplications[j].PlaylistIndex >= job.Playlists.Count)
                {
                    if (offset + 12 <= data.Length)
                    {
                        data[offset] = 0x02; // BD-J Object type
                        offset += 4;
                        WriteBigEndian16(data, offset, (ushort)j); // BDJO index
                        offset += 2;
                        offset += 6; // Reserved
                    }
                }
            }
        }

        return data;
    }

    // -----------------------------------------------------------------------
    // MovieObject.bdmv builder
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds the MovieObject.bdmv file containing HDMV navigation commands.
    /// Each movie object contains a sequence of navigation commands that control playback.
    /// </summary>
    private byte[] BuildMovieObject(BluRayAuthoringJob job)
    {
        // Movie objects: First Play + Top Menu + one per title
        int numObjects = 2 + job.Playlists.Count;

        // Each object: 10-byte header + commands (12 bytes each)
        // Simple objects have 1-2 commands
        int objectDataSize = numObjects * (10 + 2 * 12); // 2 commands per object max
        int totalSize = 40 + 4 + 4 + objectDataSize;
        totalSize = ((totalSize + 3) / 4) * 4;

        var data = new byte[totalSize];
        int offset = 0;

        // Type Indicator
        Encoding.ASCII.GetBytes("MOBJ").CopyTo(data, offset); offset += 4;
        // Version: "0200" for standard BD, "0300" for UHD BD
        var mobjVersion = job.UltraHd ? "0300" : "0200";
        Encoding.ASCII.GetBytes(mobjVersion).CopyTo(data, offset); offset += 4;

        // Extension data start (0 = none)
        WriteBigEndian32(data, offset, 0); offset += 4;

        // Padding to 40 bytes
        while (offset < 40) data[offset++] = 0;

        // Movie objects table
        int tableBase = offset;
        WriteBigEndian32(data, tableBase, (uint)(objectDataSize + 4)); offset += 4;

        // Reserved
        WriteBigEndian32(data, offset, 0); offset += 4;

        // Number of movie objects
        WriteBigEndian16(data, offset, (ushort)numObjects); offset += 2;

        // ----- Object 0: First Playback -----
        offset = WriteMovieObject(data, offset,
            resumeIntentionFlag: false, menuCallMask: 0,
            commands: new[]
            {
                // PlayPL(playlist_id=0, chapter=0) — play first playlist
                BuildPlayPlCommand((ushort)(job.FirstPlayPlaylistIndex >= 0 ? job.FirstPlayPlaylistIndex : 0), 0)
            });

        // ----- Object 1: Top Menu -----
        offset = WriteMovieObject(data, offset,
            resumeIntentionFlag: false, menuCallMask: 0,
            commands: new[]
            {
                // For now, top menu just plays first playlist
                // A full implementation would display an interactive menu
                BuildPlayPlCommand(0, 0)
            });

        // ----- Objects 2+: Title objects -----
        for (int i = 0; i < job.Playlists.Count; i++)
        {
            offset = WriteMovieObject(data, offset,
                resumeIntentionFlag: true, menuCallMask: 0,
                commands: new[]
                {
                    BuildPlayPlCommand((ushort)i, 0)
                });
        }

        return data;
    }

    private static int WriteMovieObject(byte[] data, int offset,
        bool resumeIntentionFlag, uint menuCallMask, byte[][] commands)
    {
        // Terminal info (2 bytes)
        data[offset] = resumeIntentionFlag ? (byte)0x80 : (byte)0x00;
        data[offset + 1] = 0x00;
        offset += 2;

        // Menu call mask (4 bytes)
        WriteBigEndian32(data, offset, menuCallMask);
        offset += 4;

        // Number of navigation commands (2 bytes)
        WriteBigEndian16(data, offset, (ushort)commands.Length);
        offset += 2;

        // Padding (2 bytes)
        offset += 2;

        // Navigation commands (12 bytes each)
        foreach (var cmd in commands)
        {
            Buffer.BlockCopy(cmd, 0, data, offset, Math.Min(cmd.Length, 12));
            offset += 12;
        }

        return offset;
    }

    /// <summary>
    /// Builds an HDMV PlayPL (Play Playlist) navigation command.
    /// Opcode: 0x21810000 — PlayPL(playlist_number, chapter)
    /// </summary>
    private static byte[] BuildPlayPlCommand(ushort playlistId, ushort chapter)
    {
        var cmd = new byte[12];

        // Instruction word (4 bytes): opcode group + sub-group + operand count
        // PlayPL opcode: 0x21 (Play), sub 0x81 (PlayList)
        cmd[0] = 0x21;
        cmd[1] = 0x81;
        cmd[2] = 0x00;
        cmd[3] = 0x00;

        // Operand 1: playlist number (4 bytes)
        WriteBigEndian32(cmd, 4, playlistId);

        // Operand 2: chapter/mark (4 bytes)
        WriteBigEndian32(cmd, 8, chapter);

        return cmd;
    }

    // -----------------------------------------------------------------------
    // CLPI (Clip Information) builder
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds a Clip Information (CLPI) file for a single clip.
    /// Includes 3D extent_start_point extension data when the clip has stereoscopic content.
    /// Includes BDAV recording metadata when building for BDAV format.
    ///
    /// Per Blu-ray specification (Part 3 Section 5.4), the CLPI file contains:
    ///   - ClipInfo: clip type, stream coding info, recording rate
    ///   - SequenceInfo: ATC/STC sequence descriptors
    ///   - ProgramInfo: PIDs and stream coding attributes
    ///   - CPI: Characteristic Point Information (entry points for seeking)
    ///   - ExtensionData (3D): extent_start_point entries for SSIF access
    /// </summary>
    private byte[] BuildClpi(ClipInfo clip, BluRayAuthoringJob job)
    {
        // Calculate extent_start_point data size for 3D content
        int extentPointCount = 0;
        if (clip.Is3D && clip.SsifExtentStartPoints != null)
            extentPointCount = clip.SsifExtentStartPoints.Count;

        // 3D extension: 12 header + 4 (count) + extentPointCount * 10 (per entry) + ProgramInfo_SS (~40 bytes)
        int extDataPayloadSize = extentPointCount > 0
            ? 12 + 4 + extentPointCount * 10 + 40
            : 0;

        // Size: 512 base + 3D extension data if stereoscopic
        int totalSize = clip.Is3D ? 512 + extDataPayloadSize + 32 : 512;
        var data = new byte[totalSize];
        int offset = 0;

        // Type Indicator
        Encoding.ASCII.GetBytes("HDMV").CopyTo(data, offset); offset += 4;
        // Version: "0200" for standard BD, "0300" for UHD BD
        var clpiVersion = job.UltraHd ? "0300" : "0200";
        Encoding.ASCII.GetBytes(clpiVersion).CopyTo(data, offset); offset += 4;

        // Sequence Info start address
        int seqInfoAddr = 100;
        WriteBigEndian32(data, offset, (uint)seqInfoAddr); offset += 4;

        // Program Info start address
        int progInfoAddr = 200;
        WriteBigEndian32(data, offset, (uint)progInfoAddr); offset += 4;

        // CPI start address
        int cpiAddr = 300;
        WriteBigEndian32(data, offset, (uint)cpiAddr); offset += 4;

        // Clip Mark start address (0 = none)
        WriteBigEndian32(data, offset, 0); offset += 4;

        // Extension data start address — points to 3D extent start point data when stereoscopic
        int extDataAddr = clip.Is3D ? 420 : 0;
        WriteBigEndian32(data, offset, (uint)extDataAddr); offset += 4;

        // Padding to 40 bytes
        while (offset < 40) data[offset++] = 0;

        // ----- ClipInfo -----
        int clipInfoBase = offset;
        WriteBigEndian32(data, clipInfoBase, 56); // Length
        offset += 4;

        // Reserved (2 bytes)
        offset += 2;

        // Clip stream type (1 byte): 1 = AV stream
        data[offset++] = 0x01;

        // Application type (1 byte): 1 = Main TS (BDMV), 6 = BDAV Main TS
        data[offset++] = job.IsBdav ? (byte)0x06 : (byte)0x01;

        // Reserved (4 bytes)
        offset += 4;

        // TS recording rate (4 bytes) — in bytes per second (total mux rate: video + audio)
        const long DefaultAudioBytesPerSecond = 192000 / 8; // 192 kbps default audio rate
        long videoBps = job.VideoBitrateKbps > 0
            ? (long)job.VideoBitrateKbps * 1000 / 8
            : (job.UltraHd ? 50000L * 1000 / 8 : 25000L * 1000 / 8); // UHD default: 50 Mbps
        long audioBps = 0;
        if (clip.PlayItem?.AudioStreams != null)
        {
            foreach (var a in clip.PlayItem.AudioStreams)
                audioBps += a.BitrateKbps > 0 ? (long)a.BitrateKbps * 1000 / 8 : DefaultAudioBytesPerSecond;
        }
        uint recordingRate = (uint)Math.Min(videoBps + audioBps, uint.MaxValue);
        WriteBigEndian32(data, offset, recordingRate);
        offset += 4;

        // Number of source packets (4 bytes)
        long fileSize = File.Exists(clip.M2tsPath) ? new FileInfo(clip.M2tsPath).Length : 0;
        WriteBigEndian32(data, offset, (uint)(fileSize / BdTsPacketSize));
        offset += 4;

        // TS type info (16 bytes total padding)
        while (offset < seqInfoAddr) data[offset++] = 0;

        // ----- BDAV Recording Metadata -----
        // For BDAV recordings, embed timestamp and timezone data in the reserved
        // area between TS type info and SequenceInfo (CLPI MakersPrivateData area).
        // Per Blu-ray Disc Association specification Part 3-2, the clip info may
        // include recording date/time encoded as BCD in the reserved area.
        if (job.IsBdav)
        {
            var ts = job.RecordingTimestamp ?? DateTime.UtcNow;
            int metaOffset = 60; // Use reserved area at offset 60-99
            if (metaOffset + 14 <= seqInfoAddr)
            {
                // Recording timestamp: BCD-encoded YYYY MM DD HH MM SS (7 bytes)
                data[metaOffset++] = ToBcd((byte)(ts.Year / 100));   // Century BCD
                data[metaOffset++] = ToBcd((byte)(ts.Year % 100));   // Year BCD
                data[metaOffset++] = ToBcd((byte)ts.Month);          // Month BCD
                data[metaOffset++] = ToBcd((byte)ts.Day);            // Day BCD
                data[metaOffset++] = ToBcd((byte)ts.Hour);           // Hour BCD
                data[metaOffset++] = ToBcd((byte)ts.Minute);         // Minute BCD
                data[metaOffset++] = ToBcd((byte)ts.Second);         // Second BCD

                // Time zone offset from UTC in minutes, stored as signed 16-bit big-endian
                short tzOffset = (short)Math.Clamp(job.TimeZoneOffsetMinutes, -720, 840);
                WriteBigEndian16(data, metaOffset, (ushort)tzOffset);
                metaOffset += 2;
            }
        }

        // ----- SequenceInfo -----
        offset = seqInfoAddr;
        WriteBigEndian32(data, offset, 28); offset += 4; // Length

        // Reserved (2 bytes)
        offset += 2;

        // Number of ATC sequences (1 byte)
        data[offset++] = 1;

        // ATC sequence entry
        // SPN_ATC_start (4 bytes)
        WriteBigEndian32(data, offset, 0); offset += 4;

        // Number of STC sequences (1 byte)
        data[offset++] = 1;

        // Offset_STC_id (1 byte)
        data[offset++] = 0;

        // STC sequence entry
        // PCR_PID (2 bytes) — typically 0x1001 for BD
        WriteBigEndian16(data, offset, 0x1001); offset += 2;

        // SPN_STC_start (4 bytes)
        WriteBigEndian32(data, offset, 0); offset += 4;

        // Presentation start time (4 bytes) — 45kHz ticks
        WriteBigEndian32(data, offset, 0); offset += 4;

        // Presentation end time (4 bytes) — 45kHz ticks
        uint endTime = (uint)(clip.Duration.TotalSeconds * 45000);
        WriteBigEndian32(data, offset, endTime);
        offset += 4;

        // Pad to progInfoAddr
        while (offset < progInfoAddr) data[offset++] = 0;

        // ----- ProgramInfo -----
        offset = progInfoAddr;
        WriteBigEndian32(data, offset, 60); offset += 4; // Length

        // Reserved (2 bytes)
        offset += 2;

        // Number of program sequences (1 byte)
        data[offset++] = 1;

        // Program sequence entry
        // SPN_program_sequence_start (4 bytes)
        WriteBigEndian32(data, offset, 0); offset += 4;

        // Program_map_PID (2 bytes)
        WriteBigEndian16(data, offset, 0x0100); offset += 2;

        // Number of streams in PS (1 byte)
        int streamCount = 1; // Video
        if (clip.PlayItem != null)
            streamCount += clip.PlayItem.AudioStreams.Count + clip.PlayItem.SubtitleStreams.Count;
        data[offset++] = (byte)streamCount;

        // Reserved (1 byte)
        offset++;

        // Stream entries (video)
        // Stream PID (2 bytes)
        WriteBigEndian16(data, offset, 0x1011); offset += 2;
        // Stream coding info length (1 byte)
        data[offset++] = 5;
        // Stream coding type (1 byte)
        data[offset++] = EncodeStreamCodingType(job.VideoCodec);
        // Video format + frame rate (1 byte)
        data[offset++] = EncodeVideoFormatByte(job.VideoFormat, job.FrameRate);
        // Padding
        offset += 3;

        // Audio stream entries
        if (clip.PlayItem != null)
        {
            ushort audioPid = 0x1100;
            foreach (var audio in clip.PlayItem.AudioStreams)
            {
                if (offset + 10 > cpiAddr) break;

                WriteBigEndian16(data, offset, audioPid++); offset += 2;
                data[offset++] = 5; // coding info length
                data[offset++] = EncodeAudioCodingType(audio.Codec);
                data[offset++] = EncodeAudioFormat(audio.Channels, audio.SampleRate);
                // Language code (3 bytes)
                if (audio.LanguageCode.Length >= 3)
                {
                    data[offset++] = (byte)audio.LanguageCode[0];
                    data[offset++] = (byte)audio.LanguageCode[1];
                    data[offset++] = (byte)audio.LanguageCode[2];
                }
                else
                {
                    data[offset++] = (byte)'e';
                    data[offset++] = (byte)'n';
                    data[offset++] = (byte)'g';
                }
            }

            // Subtitle stream entries
            ushort subPid = 0x1200;
            foreach (var sub in clip.PlayItem.SubtitleStreams)
            {
                if (offset + 10 > cpiAddr) break;

                WriteBigEndian16(data, offset, subPid++); offset += 2;
                data[offset++] = 5; // coding info length
                data[offset++] = 0x90; // PGS subtitle
                data[offset++] = 0x00; // Reserved
                if (sub.LanguageCode.Length >= 3)
                {
                    data[offset++] = (byte)sub.LanguageCode[0];
                    data[offset++] = (byte)sub.LanguageCode[1];
                    data[offset++] = (byte)sub.LanguageCode[2];
                }
                else
                {
                    data[offset++] = (byte)'e';
                    data[offset++] = (byte)'n';
                    data[offset++] = (byte)'g';
                }
            }
        }

        // Pad to CPI address
        while (offset < cpiAddr) data[offset++] = 0;

        // ----- CPI (Characteristic Point Information) -----
        offset = cpiAddr;
        WriteBigEndian32(data, offset, 40); offset += 4; // Length

        // CPI type (2 bytes): 0x01 = EP_map
        WriteBigEndian16(data, offset, 0x0001); offset += 2;

        // EP map: minimal entry
        // Number of stream PID entries (1 byte)
        data[offset++] = 1;

        // Stream PID entry
        WriteBigEndian16(data, offset, 0x1011); offset += 2; // Video PID

        // EP_stream_type + number of EP_coarse + number of EP_fine
        data[offset++] = 0x01; // Stream type 1
        WriteBigEndian16(data, offset, 1); offset += 2; // 1 coarse entry
        WriteBigEndian32(data, offset, 1); offset += 4; // 1 fine entry

        // EP_fine start address (relative to EP map start)
        WriteBigEndian32(data, offset, 20); offset += 4;

        // EP_coarse entry (8 bytes)
        // ref_to_EP_fine_id (2 bytes) + PTS_EP_coarse (18 bits) + SPN_EP_coarse (32 bits)
        WriteBigEndian16(data, offset, 0); offset += 2;
        WriteBigEndian32(data, offset, 0); offset += 4;
        offset += 2; // padding

        // EP_fine entry (4 bytes)
        // is_angle_change_point + I_end_position_offset + PTS_EP_fine + SPN_EP_fine
        WriteBigEndian32(data, offset, 0);
        offset += 4;

        // ----- 3D Extension Data: extent_start_point + ProgramInfo_SS -----
        // Per Blu-ray 3D spec (Part 3 Section 5.4.7), when stereoscopic content is
        // present, the CLPI extension data contains:
        //   1. extent_start_point entries: byte offsets within the SSIF file marking
        //      where each base-view and dependent-view extent begins.
        //   2. ProgramInfo_SS: stereoscopic stream descriptors (MVC dependent view PIDs).
        //
        // These enable the player to:
        //   - Parse the interleaved SSIF file and extract individual view extents
        //   - Identify the MVC dependent view elementary stream
        //   - Apply depth offset metadata for convergence adjustment
        if (clip.Is3D && extDataAddr > 0)
        {
            offset = extDataAddr;

            // Total extension data length (excluding this 4-byte field)
            int totalExtLen = extDataPayloadSize;
            WriteBigEndian32(data, offset, (uint)totalExtLen); offset += 4;

            // Number of extension entries (4 bytes): 2 entries (extent_start_point + ProgramInfo_SS)
            WriteBigEndian32(data, offset, 2); offset += 4;

            // ----- Extension entry 1: extent_start_point (ID 0x0001) -----
            WriteBigEndian16(data, offset, 0x0001); offset += 2; // Extension ID

            // Specifier start address relative to extension data start (4 bytes)
            int espSpecAddr = 24; // After both entry descriptors
            WriteBigEndian32(data, offset, (uint)espSpecAddr); offset += 4;

            // Specifier length (4 bytes)
            int espDataLen = 4 + extentPointCount * 10;
            WriteBigEndian32(data, offset, (uint)espDataLen); offset += 4;

            // ----- Extension entry 2: ProgramInfo_SS (ID 0x0002) -----
            WriteBigEndian16(data, offset, 0x0002); offset += 2; // Extension ID

            int progSsSpecAddr = espSpecAddr + espDataLen;
            WriteBigEndian32(data, offset, (uint)progSsSpecAddr); offset += 4;
            WriteBigEndian32(data, offset, 40); offset += 4; // ProgramInfo_SS length

            // ----- extent_start_point data -----
            // Pad to specifier start
            while (offset < extDataAddr + espSpecAddr) data[offset++] = 0;

            // Number of extent_start_point entries (4 bytes)
            WriteBigEndian32(data, offset, (uint)extentPointCount); offset += 4;

            // Each extent_start_point entry (10 bytes):
            //   - SPN_extent_start (4 bytes): source packet number in the original M2TS
            //   - extent_file_offset (4 bytes): byte offset within the SSIF file
            //   - is_base_view (1 byte): 1 = base view, 0 = dependent view
            //   - reserved (1 byte)
            if (clip.SsifExtentStartPoints != null)
            {
                foreach (var extentPoint in clip.SsifExtentStartPoints)
                {
                    if (offset + 10 > data.Length) break;

                    // SPN_extent_start: source packet number (byte offset / 192)
                    uint spn = (uint)(extentPoint.SourceByteOffset / BdTsPacketSize);
                    WriteBigEndian32(data, offset, spn); offset += 4;

                    // Extent byte offset within the SSIF file (4 bytes, upper 32 bits)
                    // For files > 4 GB, this wraps; per spec, use SPN-based addressing
                    WriteBigEndian32(data, offset, (uint)(extentPoint.SsifByteOffset & 0xFFFFFFFF)); offset += 4;

                    // View flag: 1 = base view, 0 = dependent view
                    data[offset++] = extentPoint.IsBaseView ? (byte)0x01 : (byte)0x00;

                    // Reserved
                    data[offset++] = 0x00;
                }
            }

            // ----- ProgramInfo_SS: stereoscopic program info -----
            while (offset < extDataAddr + progSsSpecAddr) data[offset++] = 0;

            // Length (4 bytes)
            WriteBigEndian32(data, offset, 32); offset += 4;

            // Reserved (1 byte)
            offset++;

            // Number of program sequences SS (1 byte)
            data[offset++] = 1;

            // Program sequence SS entry
            // SPN_program_sequence_start (4 bytes)
            WriteBigEndian32(data, offset, 0); offset += 4;

            // Program_map_PID (2 bytes)
            WriteBigEndian16(data, offset, 0x0100); offset += 2;

            // Number of streams in SS (1 byte): 1 (dependent view video)
            data[offset++] = 1;

            // Reserved (1 byte)
            offset++;

            // Dependent view stream entry
            // Stream PID (2 bytes) — MVC dependent view
            WriteBigEndian16(data, offset, MvcDependentViewPid); offset += 2;

            // Stream coding info length (1 byte)
            data[offset++] = 5;

            // Stream coding type (1 byte): MVC dependent view
            data[offset++] = MvcStreamType;

            // Video format + frame rate (1 byte)
            data[offset++] = EncodeVideoFormatByte(job.VideoFormat, job.FrameRate);

            // 3D content type (1 byte)
            data[offset++] = Encode3DContentType(job.Stereoscopic3DMode);

            // 3D depth offset metadata (2 bytes, signed big-endian)
            // Per Blu-ray 3D spec, convergence distance in 1/10th pixel units.
            // Positive = behind screen, negative = in front. 0 = screen depth.
            short depthOffset = (short)Math.Clamp(job.Stereoscopic3DDepthOffset, -2048, 2047);
            // Write signed 16-bit big-endian: preserve sign bits via unchecked cast
            data[offset] = (byte)((depthOffset >> 8) & 0xFF);
            data[offset + 1] = (byte)(depthOffset & 0xFF);
            offset += 2;
        }

        return data;
    }

    // -----------------------------------------------------------------------
    // MPLS (Movie Playlist) builder
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds a Movie Playlist (MPLS) file.
    /// </summary>
    private byte[] BuildMpls(BluRayPlaylist playlist, List<ClipInfo> clips, BluRayAuthoringJob job)
    {
        int playItemCount = playlist.PlayItems.Count;
        int chapterCount = playlist.Chapters.Count;

        // Calculate sizes
        int playListSize = 10 + playItemCount * 60; // Play items
        int playListMarkSize = 4 + chapterCount * 14; // Chapter marks

        // 3D extension: STN_table_SS with dependent view stream entries
        bool has3D = job.Stereoscopic3D && clips.Exists(c => c.Is3D);
        int stnTableSsSize = has3D ? (16 + playItemCount * 20) : 0;
        int extDataSize = has3D ? (12 + stnTableSsSize) : 0;

        int totalSize = 40 + 8 + playListSize + playListMarkSize + extDataSize + 50;
        totalSize = ((totalSize + 3) / 4) * 4;

        var data = new byte[totalSize];
        int offset = 0;

        // Type Indicator
        Encoding.ASCII.GetBytes("MPLS").CopyTo(data, offset); offset += 4;
        // Version: "0200" for standard BD, "0300" for UHD BD
        var mplsVersion = job.UltraHd ? "0300" : "0200";
        Encoding.ASCII.GetBytes(mplsVersion).CopyTo(data, offset); offset += 4;

        // PlayList start address
        int playListAddr = 58;
        WriteBigEndian32(data, offset, (uint)playListAddr); offset += 4;

        // PlayListMark start address
        int playListMarkAddr = playListAddr + 4 + playListSize;
        WriteBigEndian32(data, offset, (uint)playListMarkAddr); offset += 4;

        // Extension data start address (for 3D STN_table_SS)
        int extAddr = has3D ? (playListMarkAddr + 4 + playListMarkSize) : 0;
        WriteBigEndian32(data, offset, (uint)extAddr); offset += 4;

        // Padding to 40 bytes
        while (offset < 40) data[offset++] = 0;

        // ----- AppInfoPlayList -----
        WriteBigEndian32(data, offset, 14); offset += 4; // Length

        // Reserved (1 byte)
        offset++;

        // Playback type (1 byte): 1 = sequential
        data[offset++] = 0x01;

        // Playback count (2 bytes): 0 = no repeat
        WriteBigEndian16(data, offset, 0); offset += 2;

        // UO mask table (8 bytes)
        offset += 8;

        // Random access flag + audio/subtitle mix flags (2 bytes)
        offset += 2;

        // Pad to playListAddr
        while (offset < playListAddr) data[offset++] = 0;

        // ----- PlayList -----
        WriteBigEndian32(data, offset, (uint)playListSize); offset += 4;

        // Reserved (2 bytes)
        offset += 2;

        // Number of PlayItems (2 bytes)
        WriteBigEndian16(data, offset, (ushort)playItemCount); offset += 2;

        // Number of SubPaths (2 bytes)
        WriteBigEndian16(data, offset, (ushort)playlist.SubPaths.Count); offset += 2;

        // Play items
        for (int i = 0; i < playItemCount; i++)
        {
            var item = playlist.PlayItems[i];
            var clip = i < clips.Count ? clips[i] : null;
            int itemStart = offset;

            // PlayItem length (2 bytes) — filled in after
            offset += 2;

            // Clip Information file name (5 bytes ASCII + 4 bytes ".m2ts" extension code)
            var clipName = clip?.ClipId ?? $"{i + 1:D5}";
            Encoding.ASCII.GetBytes(clipName).CopyTo(data, offset); offset += 5;
            Encoding.ASCII.GetBytes("M2TS").CopyTo(data, offset); offset += 4;

            // Reserved (1 byte)
            offset += 1;

            // Connection condition (1 byte): 1 = non-seamless, 5 = seamless
            data[offset++] = item.ConnectionCondition == "seamless" ? (byte)5 : (byte)1;

            // Ref to STC_id (1 byte)
            data[offset++] = 0;

            // IN_time (4 bytes) — 45kHz ticks
            uint inTime = (uint)(item.InTime.TotalSeconds * 45000);
            WriteBigEndian32(data, offset, inTime); offset += 4;

            // OUT_time (4 bytes) — 45kHz ticks
            uint outTime = item.OutTime > TimeSpan.Zero
                ? (uint)(item.OutTime.TotalSeconds * 45000)
                : (clip != null ? (uint)(clip.Duration.TotalSeconds * 45000) : 0);
            WriteBigEndian32(data, offset, outTime); offset += 4;

            // UO_mask_table (8 bytes)
            offset += 8;

            // random_access_flag + still_mode + still_time
            data[offset++] = 0x00; // Flags
            data[offset++] = 0x00; // Still mode: off
            WriteBigEndian16(data, offset, 0); offset += 2; // Still time

            // Number of primary audio streams (1 byte)
            data[offset++] = (byte)(item.AudioStreams.Count > 0 ? item.AudioStreams.Count : 1);

            // Primary audio stream entries (minimal)
            for (int a = 0; a < Math.Max(1, item.AudioStreams.Count); a++)
            {
                // Stream entry length (1 byte)
                data[offset++] = 0x0A;
                // Stream type (1 byte): 1 = PlayItem
                data[offset++] = 0x01;
                // PID (2 bytes)
                WriteBigEndian16(data, offset, (ushort)(0x1100 + a)); offset += 2;

                // Stream coding info
                if (a < item.AudioStreams.Count)
                {
                    data[offset++] = EncodeAudioCodingType(item.AudioStreams[a].Codec);
                    data[offset++] = EncodeAudioFormat(item.AudioStreams[a].Channels, item.AudioStreams[a].SampleRate);
                    var lang = item.AudioStreams[a].LanguageCode;
                    data[offset++] = lang.Length > 0 ? (byte)lang[0] : (byte)'e';
                    data[offset++] = lang.Length > 1 ? (byte)lang[1] : (byte)'n';
                    data[offset++] = lang.Length > 2 ? (byte)lang[2] : (byte)'g';
                    offset++; // padding
                }
                else
                {
                    // No audio streams defined — write a default silent AC-3 stereo entry
                    // to satisfy Blu-ray player requirements for at least one audio stream.
                    data[offset++] = 0x80; // AC-3 coding type
                    data[offset++] = 0x03; // Stereo (2ch), 48 kHz
                    data[offset++] = (byte)'e'; // Default language: eng
                    data[offset++] = (byte)'n';
                    data[offset++] = (byte)'g';
                    offset++; // padding
                }
            }

            // Write PlayItem length
            int itemLen = offset - itemStart - 2;
            WriteBigEndian16(data, itemStart, (ushort)itemLen);
        }

        // Pad to playListMarkAddr
        while (offset < playListMarkAddr) data[offset++] = 0;

        // ----- PlayListMark -----
        WriteBigEndian32(data, offset, (uint)playListMarkSize); offset += 4;

        // Number of marks (2 bytes)
        WriteBigEndian16(data, offset, (ushort)chapterCount); offset += 2;

        // Mark entries (14 bytes each)
        foreach (var chapter in playlist.Chapters)
        {
            // Reserved (1 byte)
            data[offset++] = 0;

            // Mark type (1 byte): 1 = chapter mark, 2 = bookmark
            data[offset++] = 0x01;

            // Ref to PlayItem id (2 bytes)
            WriteBigEndian16(data, offset, (ushort)chapter.PlayItemIndex); offset += 2;

            // Mark time stamp (4 bytes) — 45kHz ticks
            uint markTime = (uint)(chapter.StartTime.TotalSeconds * 45000);
            WriteBigEndian32(data, offset, markTime); offset += 4;

            // Entry_ES_PID (2 bytes): 0xFFFF = not applicable
            WriteBigEndian16(data, offset, 0xFFFF); offset += 2;

            // Duration (4 bytes): 0 = not specified
            WriteBigEndian32(data, offset, 0); offset += 4;
        }

        // ----- 3D Extension Data: STN_table_SS -----
        // Per Blu-ray 3D spec, the MPLS extension data contains STN_table_SS which
        // describes stereoscopic stream entries (dependent view video) for each play item.
        if (has3D && extAddr > 0)
        {
            // Pad to extension data address
            while (offset < extAddr) data[offset++] = 0;

            // Extension data header
            WriteBigEndian32(data, offset, (uint)(extDataSize - 4)); offset += 4;

            // Number of extension entries (4 bytes)
            WriteBigEndian32(data, offset, 1); offset += 4;

            // Extension entry descriptor
            // Extension data ID (2 bytes): 0x0002 = STN_table_SS (SubPath extension for 3D)
            WriteBigEndian16(data, offset, 0x0002); offset += 2;

            // Entry specifier start address (relative to extension data start, 4 bytes)
            WriteBigEndian32(data, offset, 12); offset += 4;

            // STN_table_SS data for each play item
            // Length (4 bytes)
            WriteBigEndian32(data, offset, (uint)stnTableSsSize); offset += 4;

            // Number of play items with SS info (2 bytes)
            WriteBigEndian16(data, offset, (ushort)playItemCount); offset += 2;

            // Reserved (2 bytes)
            offset += 2;

            for (int i = 0; i < playItemCount; i++)
            {
                var clip = i < clips.Count ? clips[i] : null;

                // Offset to STN_table_SS for this play item (2 bytes) — relative
                WriteBigEndian16(data, offset, (ushort)(8 + i * 12)); offset += 2;

                // Dependent view stream entry
                // Stream entry length (1 byte)
                data[offset++] = 8;

                // Stream type (1 byte): 2 = SubPath (dependent view)
                data[offset++] = 0x02;

                // Dependent view PID (2 bytes)
                WriteBigEndian16(data, offset, MvcDependentViewPid); offset += 2;

                // Stream coding type (1 byte): MVC
                data[offset++] = MvcStreamType;

                // Video format + frame rate (1 byte)
                data[offset++] = EncodeVideoFormatByte(job.VideoFormat, job.FrameRate);

                // 3D content type (1 byte)
                data[offset++] = Encode3DContentType(job.Stereoscopic3DMode);

                // 3D depth offset (2 bytes, signed big-endian) — convergence distance
                short mplsDepth = (short)Math.Clamp(job.Stereoscopic3DDepthOffset, -2048, 2047);
                WriteBigEndian16(data, offset, (ushort)mplsDepth);
                offset += 2;
            }
        }

        return data;
    }

    // -----------------------------------------------------------------------
    // BDJO (BD-J Object) builder
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds a BD-J Object (BDJO) file per Blu-ray Disc Association specification
    /// Part 3-2, Section 12.3.
    ///
    /// BDJO file structure (big-endian, all offsets absolute from file start):
    ///   - Type indicator: "BDJO" (4 bytes)
    ///   - Version: "0200"/"0300" (4 bytes)
    ///   - Terminal info start address (4 bytes)
    ///   - App cache info start address (4 bytes)
    ///   - Accessible playlists start address (4 bytes)
    ///   - Application management table start address (4 bytes)
    ///   - Key interest table start address (4 bytes)
    ///   - File access info start address (4 bytes)
    ///   - Extension data start address (4 bytes)
    ///   - Padding to 40 bytes
    ///
    /// Terminal info:
    ///   - Default font, initial HAVi configuration, menu call mask
    ///   - Title search mask, initial output mode
    ///
    /// Application management table:
    ///   - Application descriptors with lifecycle, classpath, initial class
    ///   - Organization ID, application ID, control code, priority, visibility
    ///
    /// Key interest table:
    ///   - Key event masks for BD remote control button handling
    ///
    /// File access info:
    ///   - Accessible directories and files for sandbox security
    /// </summary>
    private byte[] BuildBdjo(BluRayAuthoringJob job, BdJApplication app,
        ushort appId, List<string> classpath)
    {
        // Calculate sizes for each section
        int terminalInfoSize = 36;
        int appCacheInfoSize = 8; // Minimal: length(4) + count(1) + reserved(3)
        int accessiblePlaylistsSize = 8;

        // Application management table
        // Per app: org_id(4) + app_id(2) + descriptor_length(2) + descriptor data (~128 bytes)
        int appDescriptorSize = 128;
        int appMgmtTableSize = 8 + appDescriptorSize;

        // Key interest table: 4(length) + 4(key_mask) = 8 bytes minimum
        int keyInterestSize = 8;

        // File access info: 4(length) + entries
        int fileAccessSize = 12;

        int totalSize = 40 + terminalInfoSize + appCacheInfoSize + accessiblePlaylistsSize
            + appMgmtTableSize + keyInterestSize + fileAccessSize;
        totalSize = ((totalSize + 3) / 4) * 4; // Align to 4 bytes

        var data = new byte[totalSize];
        int offset = 0;

        // ---- Header ----
        Encoding.ASCII.GetBytes("BDJO").CopyTo(data, offset); offset += 4;
        var version = job.UltraHd ? "0300" : "0200";
        Encoding.ASCII.GetBytes(version).CopyTo(data, offset); offset += 4;

        // Section addresses (filled in below)
        int terminalInfoAddr = 40;
        int appCacheAddr = terminalInfoAddr + terminalInfoSize;
        int accessPlaylistAddr = appCacheAddr + appCacheInfoSize;
        int appMgmtAddr = accessPlaylistAddr + accessiblePlaylistsSize;
        int keyInterestAddr = appMgmtAddr + appMgmtTableSize;
        int fileAccessAddr = keyInterestAddr + keyInterestSize;

        WriteBigEndian32(data, offset, (uint)terminalInfoAddr); offset += 4;
        WriteBigEndian32(data, offset, (uint)appCacheAddr); offset += 4;
        WriteBigEndian32(data, offset, (uint)accessPlaylistAddr); offset += 4;
        WriteBigEndian32(data, offset, (uint)appMgmtAddr); offset += 4;
        WriteBigEndian32(data, offset, (uint)keyInterestAddr); offset += 4;
        WriteBigEndian32(data, offset, (uint)fileAccessAddr); offset += 4;
        WriteBigEndian32(data, offset, 0); offset += 4; // Extension data (0 = none)

        // Pad to 40 bytes
        while (offset < 40) data[offset++] = 0;

        // ---- Terminal Info ----
        offset = terminalInfoAddr;
        WriteBigEndian32(data, offset, (uint)(terminalInfoSize - 4)); offset += 4;

        // Default font file: "*****" = system default (5 bytes)
        Encoding.ASCII.GetBytes("*****").CopyTo(data, offset); offset += 5;

        // Initial HAVi configuration (4 bytes):
        // Bits 7-4: graphics resolution (0x0F = all, 0x01 = HD, 0x02 = QHD)
        // Bits 3-0: menu description (0x0F = all)
        data[offset++] = 0xFF; // Support all configurations
        data[offset++] = 0xFF;
        data[offset++] = 0xFF;
        data[offset++] = 0xFF;

        // Menu call mask (4 bytes): 0 = all menu calls allowed
        WriteBigEndian32(data, offset, 0); offset += 4;

        // Title search mask (4 bytes): 0 = all titles searchable
        WriteBigEndian32(data, offset, 0); offset += 4;

        // Initial output mode (4 bytes):
        // Bit 7: initial_output_mode (0=2D, 1=3D)
        data[offset] = job.Stereoscopic3D ? (byte)0x80 : (byte)0x00;
        offset += 4;

        // Provider data (8 bytes)
        var providerData = Encoding.UTF8.GetBytes("OBS-BDJ");
        Buffer.BlockCopy(providerData, 0, data, offset, Math.Min(providerData.Length, 8));
        offset += 8;

        // ---- App Cache Info ----
        offset = appCacheAddr;
        WriteBigEndian32(data, offset, (uint)(appCacheInfoSize - 4)); offset += 4;
        data[offset++] = 0; // Number of cache entries
        offset += 3; // Reserved/padding

        // ---- Accessible Playlists ----
        offset = accessPlaylistAddr;
        WriteBigEndian32(data, offset, (uint)(accessiblePlaylistsSize - 4)); offset += 4;
        // Access to all playlists flag
        data[offset++] = 0x01; // 1 = access all playlists
        offset += 3; // Reserved/padding

        // ---- Application Management Table ----
        offset = appMgmtAddr;
        WriteBigEndian32(data, offset, (uint)(appMgmtTableSize - 4)); offset += 4;

        // Number of applications (1 byte)
        data[offset++] = 1;
        offset += 3; // Reserved/alignment

        // Application descriptor
        int descStart = offset;

        // Organization ID (4 bytes)
        WriteBigEndian32(data, offset, (uint)job.BdJOrganizationId); offset += 4;

        // Application ID (2 bytes)
        WriteBigEndian16(data, offset, appId); offset += 2;

        // Application descriptor length (2 bytes) — filled in later
        int descLenPos = offset;
        offset += 2;

        // Application control code (1 byte)
        byte controlCode = app.ControlCode.ToUpperInvariant() switch
        {
            "AUTOSTART" => 0x01,
            "PRESENT" => 0x02,
            "DESTROY" => 0x03,
            _ => 0x01
        };
        data[offset++] = controlCode;

        // Application type (1 byte): 1 = BD-J application
        data[offset++] = 0x01;

        // Application version (4 bytes): profile + version
        // Encode BD-J profile version
        var profileParts = job.BdJProfileVersion.Split('.');
        data[offset++] = profileParts.Length > 0 ? byte.Parse(profileParts[0]) : (byte)1;
        data[offset++] = profileParts.Length > 1 ? byte.Parse(profileParts[1]) : (byte)1;
        data[offset++] = 0; // Micro version
        data[offset++] = 0; // Reserved

        // Priority (1 byte)
        data[offset++] = (byte)Math.Clamp(app.Priority, 1, 255);

        // Visibility (1 byte)
        data[offset++] = app.Visibility.Equals("VISIBLE", StringComparison.OrdinalIgnoreCase)
            ? (byte)0x01 : (byte)0x00;

        // Reserved (2 bytes)
        offset += 2;

        // Application name length + name
        var nameBytes = Encoding.UTF8.GetBytes(
            !string.IsNullOrEmpty(app.Name) ? app.Name : "BD-J Application");
        int nameLen = Math.Min(nameBytes.Length, 32);
        data[offset++] = (byte)nameLen;
        Buffer.BlockCopy(nameBytes, 0, data, offset, nameLen);
        offset += nameLen;

        // Icon locator (1 byte length + path)
        var iconBytes = Encoding.ASCII.GetBytes(app.IconPath ?? string.Empty);
        int iconLen = Math.Min(iconBytes.Length, 32);
        data[offset++] = (byte)iconLen;
        if (iconLen > 0)
        {
            Buffer.BlockCopy(iconBytes, 0, data, offset, iconLen);
            offset += iconLen;
        }

        // Initial class name (1 byte length + class name)
        var classBytes = Encoding.UTF8.GetBytes(app.InitialClass);
        int classLen = Math.Min(classBytes.Length, 64);
        data[offset++] = (byte)classLen;
        Buffer.BlockCopy(classBytes, 0, data, offset, classLen);
        offset += classLen;

        // Classpath extension count + entries
        data[offset++] = (byte)classpath.Count;
        foreach (var cpEntry in classpath)
        {
            var cpBytes = Encoding.ASCII.GetBytes(cpEntry);
            int cpLen = Math.Min(cpBytes.Length, 32);
            if (offset + 1 + cpLen >= data.Length) break;
            data[offset++] = (byte)cpLen;
            Buffer.BlockCopy(cpBytes, 0, data, offset, cpLen);
            offset += cpLen;
        }

        // Parameters count + entries
        var paramCount = Math.Min(app.Parameters.Count, 16);
        data[offset++] = (byte)paramCount;
        int paramIdx = 0;
        foreach (var param in app.Parameters)
        {
            if (paramIdx >= paramCount) break;
            var paramBytes = Encoding.UTF8.GetBytes($"{param.Key}={param.Value}");
            int paramLen = Math.Min(paramBytes.Length, 64);
            if (offset + 1 + paramLen >= data.Length) break;
            data[offset++] = (byte)paramLen;
            Buffer.BlockCopy(paramBytes, 0, data, offset, paramLen);
            offset += paramLen;
            paramIdx++;
        }

        // Fill in descriptor length
        int descLen = offset - descStart - 8; // Exclude org_id, app_id, and length field
        WriteBigEndian16(data, descLenPos, (ushort)descLen);

        // ---- Key Interest Table ----
        offset = keyInterestAddr;
        if (offset + keyInterestSize <= data.Length)
        {
            WriteBigEndian32(data, offset, (uint)(keyInterestSize - 4)); offset += 4;
            // Key interest mask: enable common BD remote keys
            // Bit 0: VK_COLORED_KEY_0 (Red), Bit 1: VK_COLORED_KEY_1 (Green)
            // Bit 2: VK_COLORED_KEY_2 (Yellow), Bit 3: VK_COLORED_KEY_3 (Blue)
            // Bit 4-7: Arrow keys, Bit 8: VK_ENTER, Bit 9: VK_POPUP_MENU
            // 0x03FF = all common keys enabled
            WriteBigEndian32(data, offset, 0x03FF); offset += 4;
        }

        // ---- File Access Info ----
        offset = fileAccessAddr;
        if (offset + fileAccessSize <= data.Length)
        {
            WriteBigEndian32(data, offset, (uint)(fileAccessSize - 4)); offset += 4;
            // Number of accessible roots (1 byte)
            data[offset++] = 0x01; // Access to BDMV/ root
            offset += 3; // Reserved/padding
            // Access permission: 0x01 = read access to all files
            WriteBigEndian32(data, offset, 0x01); offset += 4;
        }

        return data;
    }

    // -----------------------------------------------------------------------
    // M2TS conversion
    // -----------------------------------------------------------------------

    /// <summary>
    /// Converts a standard MPEG-2 TS file to Blu-ray TS format by adding
    /// the 4-byte Blu-ray header (arrival timestamp) to each 188-byte TS packet.
    /// </summary>
    private static async Task ConvertToBluRayTsAsync(string inputPath, string outputPath, CancellationToken ct)
    {
        await using var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize: 64 * 1024, useAsync: true);
        await using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write,
            FileShare.None, bufferSize: 64 * 1024, useAsync: true);

        var tsPacket = new byte[TsPacketSize];
        var bdHeader = new byte[4];
        long packetCount = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // Find sync byte
            int b = input.ReadByte();
            if (b < 0) break; // EOF

            if (b != TsSyncByte)
            {
                // Scan for next sync byte
                while (true)
                {
                    b = input.ReadByte();
                    if (b < 0 || b == TsSyncByte) break;
                }
                if (b < 0) break;
            }

            tsPacket[0] = (byte)b;
            int read = await input.ReadAsync(tsPacket.AsMemory(1, TsPacketSize - 1), ct);
            if (read < TsPacketSize - 1) break;

            // Generate arrival timestamp using 27MHz Blu-ray clock
            // Timestamp increments per packet based on typical BD bitrate
            uint arrivalTime = (uint)((packetCount * 27_000_000L / TypicalBdPacketsPerSecond) & 0x3FFFFFFF);

            // 4-byte BD header: 2 bits copy permission + 30 bits arrival timestamp
            bdHeader[0] = (byte)((arrivalTime >> 24) & 0x3F); // Copy permission = 0 (copy free)
            bdHeader[1] = (byte)((arrivalTime >> 16) & 0xFF);
            bdHeader[2] = (byte)((arrivalTime >> 8) & 0xFF);
            bdHeader[3] = (byte)(arrivalTime & 0xFF);

            await output.WriteAsync(bdHeader, ct);
            await output.WriteAsync(tsPacket, ct);

            packetCount++;
        }
    }

    /// <summary>Checks if a file is already in Blu-ray TS format (192-byte packets).</summary>
    private static bool IsBluRayTs(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.Length < BdTsPacketSize * 2) return false;

            var buf = new byte[BdTsPacketSize * 2];
            if (fs.Read(buf, 0, buf.Length) < buf.Length) return false;

            // Check if sync byte appears at position 4 and 196 (4 + 192)
            return buf[4] == TsSyncByte && buf[4 + BdTsPacketSize] == TsSyncByte;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Gets the duration of an M2TS clip.</summary>
    private static async Task<TimeSpan> GetClipDurationAsync(string m2tsPath, CancellationToken ct)
    {
        try
        {
            var info = await VideoTranscoder.ProbeAsync(m2tsPath, ct);
            return info.Duration;
        }
        catch
        {
            // Estimate from file size: assume ~20Mbps average
            long fileSize = new FileInfo(m2tsPath).Length;
            double seconds = fileSize * 8.0 / 20_000_000;
            return TimeSpan.FromSeconds(seconds);
        }
    }

    // -----------------------------------------------------------------------
    // Auxiliary files
    // -----------------------------------------------------------------------

    /// <summary>Creates a minimal sound.bdmv file for the AUXDATA directory.</summary>
    private static async Task CreateSoundBdmvAsync(string auxdataDir, CancellationToken ct)
    {
        // Minimal sound.bdmv — type indicator + empty sound index
        var data = new byte[32];
        Encoding.ASCII.GetBytes("BCLK").CopyTo(data, 0);
        Encoding.ASCII.GetBytes("0200").CopyTo(data, 4);
        await File.WriteAllBytesAsync(Path.Combine(auxdataDir, "sound.bdmv"), data, ct);
    }

    // -----------------------------------------------------------------------
    // Encoding helpers
    // -----------------------------------------------------------------------

    private static byte EncodeVideoFormatByte(string videoFormat, string frameRate)
    {
        byte format = videoFormat.ToUpperInvariant() switch
        {
            "480I" => 0x01,
            "576I" => 0x02,
            "480P" => 0x03,
            "1080I" => 0x04,
            "720P" => 0x05,
            "1080P" => 0x06,
            "576P" => 0x07,
            "2160P" => 0x08, // UHD 4K (3840×2160) — per BDA UHD specification
            _ => 0x06 // Default 1080p
        };

        byte rate = frameRate switch
        {
            "23.976" => 0x01,
            "24" => 0x02,
            "25" => 0x03,
            "29.97" => 0x04,
            "30" => 0x04,    // Mapped to 29.97 (NTSC standard)
            "50" => 0x06,
            "59.94" => 0x07,
            "60" => 0x07,    // Mapped to 59.94 (NTSC standard)
            _ => 0x01
        };

        return (byte)((format << 4) | rate);
    }

    private static byte EncodeStreamCodingType(string codec)
    {
        return codec.ToUpperInvariant() switch
        {
            "MPEG2" => 0x02,
            "H264" or "AVC" => 0x1B,
            "MVC" => MvcStreamType, // 0x20 — H.264 MVC dependent view for 3D
            "VC1" => 0xEA,
            "HEVC" or "H265" or "H.265" => 0x24, // HEVC Main10 for UHD Blu-ray
            _ => 0x1B // Default H.264
        };
    }

    private static byte EncodeAudioCodingType(string codec)
    {
        return codec.ToUpperInvariant() switch
        {
            "LPCM" => 0x80,
            "AC3" => 0x81,
            "DTS" => 0x82,
            "TRUEHD" => 0x83,
            "EAC3" => 0x84,
            "DTSHD_HR" => 0x85,
            "DTSHD_MA" => 0x86,
            _ => 0x81 // Default AC-3
        };
    }

    private static byte EncodeAudioFormat(int channels, int sampleRate)
    {
        byte ch = channels switch
        {
            1 => 0x01,   // Mono
            2 => 0x03,   // Stereo
            6 => 0x06,   // 5.1
            8 => 0x0C,   // 7.1
            _ => 0x03
        };

        byte sr = sampleRate switch
        {
            48000 => 0x01,
            96000 => 0x04,
            192000 => 0x05,
            _ => 0x01
        };

        return (byte)((ch << 4) | sr);
    }

    // -----------------------------------------------------------------------
    // Binary helpers
    // -----------------------------------------------------------------------

    private static void WriteBigEndian32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)(value & 0xFF);
    }

    private static void WriteBigEndian16(byte[] buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)(value >> 8);
        buffer[offset + 1] = (byte)(value & 0xFF);
    }

    // -----------------------------------------------------------------------
    // SSIF (Stereoscopic Interleaved File) builder — Blu-ray 3D
    // -----------------------------------------------------------------------

    /// <summary>
    /// Size of one Aligned Unit (AU) for SSIF: 6144 bytes = 32 BD TS packets.
    /// Per Blu-ray 3D specification, all SSIF extents must be aligned to AU boundaries.
    /// </summary>
    private const int SsifAlignedUnitSize = 6144;

    /// <summary>
    /// Number of Aligned Units per SSIF extent. Per Blu-ray 3D specification, extents
    /// should be large enough for smooth dual-view playback at up to 60 Mbps combined
    /// bitrate. 192 AUs = 192 × 6144 = 1,179,648 bytes (~1.1 MB) per extent.
    /// This provides approximately 0.15 seconds of data at 60 Mbps, sufficient for
    /// the BD-ROM drive read-ahead buffer to maintain seamless playback.
    /// </summary>
    private const int SsifAUsPerExtent = 192;

    /// <summary>
    /// Extent size for SSIF interleaving: 192 AUs × 6144 bytes = 1,179,648 bytes.
    /// Each extent in the SSIF file contains contiguous data from one view.
    /// </summary>
    private const int SsifExtentSize = SsifAUsPerExtent * SsifAlignedUnitSize;

    /// <summary>
    /// Builds a Stereoscopic Interleaved File (.ssif) by interleaving 192-byte
    /// packets from the base view M2TS and the dependent view M2TS.
    ///
    /// Per Blu-ray 3D specification (Part 3 Section 5.8), SSIF files contain
    /// alternating extents of base view and dependent view data:
    ///   [Base Extent 0][Dep Extent 0][Base Extent 1][Dep Extent 1]...
    ///
    /// Each extent is aligned to a 6144-byte Aligned Unit (AU) boundary and
    /// spans 192 AUs (~1.1 MB). The interleaving enables a single-lens BD player
    /// to read both views from one file while maintaining the seek pattern needed
    /// for the BD-ROM optical pickup.
    ///
    /// Players use extent_start_point entries from CLPI ExtensionData to locate
    /// the start of each view's extents within the SSIF file.
    /// </summary>
    /// <returns>
    /// List of <see cref="SsifExtentStartPoint"/> entries recording the byte offset
    /// of each base-view and dependent-view extent. These are written into the CLPI
    /// ExtensionData so the player can parse the interleaved file.
    /// </returns>
    private static async Task<List<SsifExtentStartPoint>> BuildSsifAsync(
        string baseViewPath,
        string dependentViewPath,
        string ssifOutputPath,
        CancellationToken ct)
    {
        var extentStartPoints = new List<SsifExtentStartPoint>();

        // Validate input files exist and have content
        if (!File.Exists(baseViewPath))
            throw new FileNotFoundException($"Base view M2TS not found: {baseViewPath}");
        if (!File.Exists(dependentViewPath))
            throw new FileNotFoundException($"Dependent view M2TS not found: {dependentViewPath}");

        var baseInfo = new FileInfo(baseViewPath);
        var depInfo = new FileInfo(dependentViewPath);
        if (baseInfo.Length == 0)
            throw new InvalidOperationException($"Base view M2TS is empty: {baseViewPath}");
        if (depInfo.Length == 0)
            throw new InvalidOperationException($"Dependent view M2TS is empty: {dependentViewPath}");

        // Validate both files appear to be BD TS (sync byte at position 4)
        ValidateBdTsSyncByte(baseViewPath, "base view");
        ValidateBdTsSyncByte(dependentViewPath, "dependent view");

        await using var baseFs = new FileStream(baseViewPath, FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize: 256 * 1024, useAsync: true);
        await using var depFs = new FileStream(dependentViewPath, FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize: 256 * 1024, useAsync: true);
        await using var outFs = new FileStream(ssifOutputPath, FileMode.Create, FileAccess.Write,
            FileShare.None, bufferSize: 256 * 1024, useAsync: true);

        var buffer = new byte[SsifExtentSize];
        bool baseEof = false, depEof = false;
        long ssifOffset = 0;
        int extentIndex = 0;

        while (!baseEof || !depEof)
        {
            ct.ThrowIfCancellationRequested();

            // Write base view extent
            if (!baseEof)
            {
                long baseSourceOffset = baseFs.Position;
                int totalRead = 0;

                // Read a full extent (or as much as available)
                while (totalRead < SsifExtentSize)
                {
                    int toRead = SsifExtentSize - totalRead;
                    int read = await baseFs.ReadAsync(buffer.AsMemory(totalRead, toRead), ct);
                    if (read == 0) break;
                    totalRead += read;
                }

                if (totalRead > 0)
                {
                    // Record extent start point for CLPI metadata
                    extentStartPoints.Add(new SsifExtentStartPoint
                    {
                        ExtentIndex = extentIndex,
                        IsBaseView = true,
                        SsifByteOffset = ssifOffset,
                        SourceByteOffset = baseSourceOffset,
                        ExtentSizeBytes = AlignToAu(totalRead)
                    });

                    await outFs.WriteAsync(buffer.AsMemory(0, totalRead), ct);
                    ssifOffset += totalRead;

                    // Pad to AU boundary if needed
                    int alignedSize = AlignToAu(totalRead);
                    if (alignedSize > totalRead)
                    {
                        int padSize = alignedSize - totalRead;
                        Array.Clear(buffer, 0, padSize);
                        await outFs.WriteAsync(buffer.AsMemory(0, padSize), ct);
                        ssifOffset += padSize;
                    }
                }

                if (totalRead < SsifExtentSize) baseEof = true;
            }

            // Write dependent view extent
            if (!depEof)
            {
                long depSourceOffset = depFs.Position;
                int totalRead = 0;

                while (totalRead < SsifExtentSize)
                {
                    int toRead = SsifExtentSize - totalRead;
                    int read = await depFs.ReadAsync(buffer.AsMemory(totalRead, toRead), ct);
                    if (read == 0) break;
                    totalRead += read;
                }

                if (totalRead > 0)
                {
                    extentStartPoints.Add(new SsifExtentStartPoint
                    {
                        ExtentIndex = extentIndex,
                        IsBaseView = false,
                        SsifByteOffset = ssifOffset,
                        SourceByteOffset = depSourceOffset,
                        ExtentSizeBytes = AlignToAu(totalRead)
                    });

                    await outFs.WriteAsync(buffer.AsMemory(0, totalRead), ct);
                    ssifOffset += totalRead;

                    int alignedSize = AlignToAu(totalRead);
                    if (alignedSize > totalRead)
                    {
                        int padSize = alignedSize - totalRead;
                        Array.Clear(buffer, 0, padSize);
                        await outFs.WriteAsync(buffer.AsMemory(0, padSize), ct);
                        ssifOffset += padSize;
                    }
                }

                if (totalRead < SsifExtentSize) depEof = true;
            }

            extentIndex++;
        }

        return extentStartPoints;
    }

    /// <summary>
    /// Validates that a file begins with a valid BD TS sync pattern (sync byte 0x47
    /// at offset 4, and again at offset 4 + 192).
    /// </summary>
    private static void ValidateBdTsSyncByte(string path, string label)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.Length < BdTsPacketSize * 2) return; // Too short to validate, allow anyway

            var header = new byte[BdTsPacketSize * 2];
            int read = fs.Read(header, 0, header.Length);
            if (read < header.Length) return;

            if (header[4] != TsSyncByte || header[4 + BdTsPacketSize] != TsSyncByte)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[Warning] SSIF {label} file may not be valid BD TS: " +
                    $"sync byte mismatch at offset 4 (0x{header[4]:X2}) " +
                    $"or {4 + BdTsPacketSize} (0x{header[4 + BdTsPacketSize]:X2})");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[Warning] Could not validate BD TS sync for {label}: {ex.Message}");
        }
    }

    /// <summary>
    /// Aligns a byte size up to the nearest Aligned Unit (6144-byte) boundary.
    /// Per BD 3D spec, all SSIF extents must be AU-aligned.
    /// </summary>
    private static int AlignToAu(int size)
    {
        return ((size + SsifAlignedUnitSize - 1) / SsifAlignedUnitSize) * SsifAlignedUnitSize;
    }

    /// <summary>
    /// Records the position of one extent within an SSIF file.
    /// Used to generate extent_start_point entries in the CLPI ExtensionData.
    /// </summary>
    private sealed class SsifExtentStartPoint
    {
        /// <summary>Sequential extent pair index (0-based, increments per base+dep pair).</summary>
        public int ExtentIndex { get; set; }

        /// <summary>True if this is a base view extent, false for dependent view.</summary>
        public bool IsBaseView { get; set; }

        /// <summary>Byte offset of this extent from the start of the SSIF file.</summary>
        public long SsifByteOffset { get; set; }

        /// <summary>Byte offset in the original source M2TS that this extent was read from.</summary>
        public long SourceByteOffset { get; set; }

        /// <summary>Actual size of this extent in bytes (AU-aligned).</summary>
        public int ExtentSizeBytes { get; set; }
    }

    /// <summary>
    /// Encodes the Blu-ray 3D stereoscopic mode as a CLPI extension byte.
    /// Per Blu-ray 3D spec, this goes into the CLPI ExtensionData.
    /// </summary>
    private static byte Encode3DContentType(string mode)
    {
        return mode.ToUpperInvariant() switch
        {
            "FRAMEPACKED" => 0x01,   // Full-resolution MVC frame packing
            "SIDEBYSIDE" => 0x03,    // Frame-compatible side-by-side (half horizontal)
            "TOPBOTTOM" => 0x04,     // Frame-compatible top-and-bottom (half vertical)
            _ => 0x01                // Default to frame-packed
        };
    }

    /// <summary>Converts a value 0-99 to BCD (Binary Coded Decimal) encoding.</summary>
    private static byte ToBcd(byte value)
    {
        return (byte)(((value / 10) << 4) | (value % 10));
    }

    // -----------------------------------------------------------------------
    // Internal types
    // -----------------------------------------------------------------------

    private sealed class ClipInfo
    {
        public string ClipId { get; set; } = string.Empty;
        public string M2tsPath { get; set; } = string.Empty;
        public TimeSpan Duration { get; set; }
        public BluRayPlayItem? PlayItem { get; set; }
        /// <summary>Path to the SSIF (Stereoscopic Interleaved File) for 3D content.</summary>
        public string? SsifPath { get; set; }
        /// <summary>Path to the dependent-view M2TS for 3D stereoscopic content.</summary>
        public string? DependentViewM2tsPath { get; set; }
        /// <summary>Whether this clip is part of a 3D stereoscopic disc.</summary>
        public bool Is3D { get; set; }
        /// <summary>Extent start point entries recorded during SSIF building, for CLPI metadata.</summary>
        public List<SsifExtentStartPoint>? SsifExtentStartPoints { get; set; }
    }
}
