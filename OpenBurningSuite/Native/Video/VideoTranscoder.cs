// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using OpenBurningSuite.Helpers;
using OpenBurningSuite.Models;

namespace OpenBurningSuite.Native.Video;

/// <summary>
/// Cross-platform video transcoder that converts video files to DVD-Video or Blu-ray
/// compliant formats using FFmpeg.
///
/// DVD-Video requirements (MPEG-2 Program Stream):
/// - Video: MPEG-2, 720×480 (NTSC) or 720×576 (PAL)
/// - Frame rate: 29.97fps (NTSC) or 25fps (PAL)
/// - Max bitrate: 9,800 kbps (video), 10,080 kbps (total mux)
/// - Audio: AC-3 (Dolby Digital) 48kHz, 192-448 kbps; or LPCM 48kHz 16-bit
///
/// Blu-ray requirements (MPEG-2 Transport Stream):
/// - Video: H.264/AVC, MPEG-2, or VC-1
/// - Resolution: 1920×1080, 1280×720, 720×480, 720×576
/// - Max bitrate: 40,000 kbps (H.264), 80,000 kbps (total mux)
/// - Audio: AC-3, E-AC-3, DTS, TrueHD, LPCM at 48/96/192 kHz
///
/// FFmpeg is detected automatically on Windows (PATH, common install locations),
/// Linux (PATH, /usr/bin, /usr/local/bin, snap, flatpak),
/// and macOS (PATH, Homebrew /opt/homebrew/bin, /usr/local/bin).
/// </summary>
public static class VideoTranscoder
{
    /// <summary>Cached FFmpeg path (null = not yet searched, empty = not found).</summary>
    private static string? _ffmpegPath;

    /// <summary>Maximum DVD-Video video bitrate in kbps.</summary>
    public const int DvdMaxVideoBitrateKbps = 9800;

    /// <summary>Default DVD-Video video bitrate in kbps.</summary>
    public const int DvdDefaultVideoBitrateKbps = 6000;

    /// <summary>Maximum Blu-ray H.264 video bitrate in kbps.</summary>
    public const int BluRayMaxVideoBitrateKbps = 40000;

    /// <summary>Default Blu-ray H.264 video bitrate in kbps.</summary>
    public const int BluRayDefaultVideoBitrateKbps = 25000;

    private static readonly Regex DurationRx = new(@"Duration:\s*(\d{2}):(\d{2}):(\d{2})\.(\d{2})",
        RegexOptions.Compiled);
    private static readonly Regex TimeRx = new(@"time=(\d{2}):(\d{2}):(\d{2})\.(\d{2})",
        RegexOptions.Compiled);

    /// <summary>
    /// Detects and returns the path to the FFmpeg executable, or null if not found.
    /// Searches PATH and common installation directories per platform.
    /// </summary>
    public static string? FindFfmpeg()
    {
        if (_ffmpegPath != null)
            return _ffmpegPath.Length > 0 ? _ffmpegPath : null;

        var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffmpeg.exe" : "ffmpeg";

        // Search PATH first
        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? Array.Empty<string>();
        foreach (var dir in pathDirs)
        {
            var candidate = Path.Combine(dir, exeName);
            if (File.Exists(candidate))
            {
                _ffmpegPath = candidate;
                return _ffmpegPath;
            }
        }

        // Platform-specific common locations
        string[] extraPaths;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            extraPaths = new[]
            {
                @"C:\ffmpeg\bin\ffmpeg.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "bin", "ffmpeg.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ffmpeg", "bin", "ffmpeg.exe")
            };
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            extraPaths = new[]
            {
                "/opt/homebrew/bin/ffmpeg",
                "/usr/local/bin/ffmpeg",
                "/usr/bin/ffmpeg"
            };
        }
        else // Linux
        {
            extraPaths = new[]
            {
                "/usr/bin/ffmpeg",
                "/usr/local/bin/ffmpeg",
                "/snap/bin/ffmpeg",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "ffmpeg")
            };
        }

        foreach (var path in extraPaths)
        {
            if (File.Exists(path))
            {
                _ffmpegPath = path;
                return _ffmpegPath;
            }
        }

        _ffmpegPath = string.Empty; // Mark as searched but not found
        return null;
    }

    /// <summary>
    /// Returns true if FFmpeg is available on this system.
    /// </summary>
    public static bool IsAvailable() => FindFfmpeg() != null;

    /// <summary>
    /// Gets information about a video file (duration, resolution, codecs).
    /// </summary>
    public static async Task<VideoInfo> ProbeAsync(string videoPath, CancellationToken ct = default)
    {
        var ffmpeg = FindFfmpeg() ?? throw new InvalidOperationException(
            "FFmpeg is not installed. Please install FFmpeg for video transcoding support. " +
            "Visit https://ffmpeg.org/download.html for installation instructions.");

        var ffprobe = Path.Combine(Path.GetDirectoryName(ffmpeg) ?? ".",
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ffprobe.exe" : "ffprobe");

        // Fall back to ffmpeg -i if ffprobe not available
        if (!File.Exists(ffprobe))
            return await ProbeWithFfmpegAsync(ffmpeg, videoPath, ct);

        var args = $"-v quiet -print_format json -show_format -show_streams \"{videoPath}\"";
        var result = await RunProcessAsync(ffprobe, args, ct);

        var info = new VideoInfo { FilePath = videoPath };
        ParseProbeOutput(result.stdout + result.stderr, info);
        return info;
    }

    /// <summary>
    /// Transcodes a video file to DVD-Video compliant MPEG-2 Program Stream.
    /// </summary>
    /// <param name="inputPath">Input video file path.</param>
    /// <param name="outputPath">Output MPEG-2 PS file path (.mpg).</param>
    /// <param name="videoStandard">"NTSC" or "PAL".</param>
    /// <param name="aspectRatio">"4:3" or "16:9".</param>
    /// <param name="videoBitrateKbps">Video bitrate in kbps (0 = default).</param>
    /// <param name="audioCodec">"AC3", "MP2", or "LPCM".</param>
    /// <param name="audioBitrateKbps">Audio bitrate in kbps (0 = default).</param>
    /// <param name="progress">Progress reporter.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task TranscodeToDvdAsync(
        string inputPath,
        string outputPath,
        string videoStandard = "NTSC",
        string aspectRatio = "16:9",
        int videoBitrateKbps = 0,
        string audioCodec = "AC3",
        int audioBitrateKbps = 0,
        IProgress<BuildProgress>? progress = null,
        CancellationToken ct = default)
    {
        var ffmpeg = FindFfmpeg() ?? throw new InvalidOperationException(
            "FFmpeg is required for DVD video transcoding.");

        if (!File.Exists(inputPath))
            throw new FileNotFoundException($"Input video file not found: {inputPath}");

        bool isNtsc = videoStandard.Equals("NTSC", StringComparison.OrdinalIgnoreCase);
        string resolution = isNtsc ? "720:480" : "720:576";
        string frameRate = isNtsc ? "29.97" : "25";
        int vBitrate = videoBitrateKbps > 0
            ? Math.Min(videoBitrateKbps, DvdMaxVideoBitrateKbps)
            : DvdDefaultVideoBitrateKbps;
        int aBitrate = audioBitrateKbps > 0 ? audioBitrateKbps : 192;

        string audioParams = audioCodec.ToUpperInvariant() switch
        {
            "AC3" => $"-c:a ac3 -b:a {aBitrate}k -ar 48000",
            "MP2" => $"-c:a mp2 -b:a {aBitrate}k -ar 48000",
            "LPCM" => "-c:a pcm_s16be -ar 48000",
            _ => $"-c:a ac3 -b:a {aBitrate}k -ar 48000"
        };

        string aspectParam = aspectRatio == "4:3" ? "4/3" : "16/9";

        var sb = new StringBuilder();
        sb.Append($"-i \"{inputPath}\" ");
        sb.Append($"-target {(isNtsc ? "ntsc" : "pal")}-dvd ");
        sb.Append($"-vf \"scale={resolution}:force_original_aspect_ratio=decrease,pad={resolution}:(ow-iw)/2:(oh-ih)/2\" ");
        sb.Append($"-b:v {vBitrate}k -maxrate {DvdMaxVideoBitrateKbps}k -bufsize 1835k ");
        sb.Append($"-aspect {aspectParam} ");
        sb.Append($"-r {frameRate} ");
        sb.Append($"{audioParams} ");
        sb.Append($"-f dvd -y \"{outputPath}\"");

        // Get input duration for progress tracking
        var inputDuration = await GetDurationAsync(ffmpeg, inputPath, ct);

        progress?.Report(new BuildProgress
        {
            StatusMessage = $"Transcoding to DVD format ({videoStandard})...",
            LogLine = $"[Info] Transcoding: {Path.GetFileName(inputPath)} → DVD MPEG-2 PS"
        });

        await RunFfmpegWithProgressAsync(ffmpeg, sb.ToString(), inputDuration, progress, ct);

        progress?.Report(new BuildProgress
        {
            LogLine = $"[Info] DVD transcode complete: {Path.GetFileName(outputPath)}"
        });
    }

    /// <summary>
    /// Transcodes a video file to Blu-ray compliant MPEG-2 Transport Stream.
    /// </summary>
    /// <param name="inputPath">Input video file path.</param>
    /// <param name="outputPath">Output M2TS file path (.m2ts).</param>
    /// <param name="videoCodec">"H264", "MPEG2", or "VC1".</param>
    /// <param name="videoFormat">"1080p", "1080i", "720p", "480p", "480i", "576p", "576i".</param>
    /// <param name="frameRate">"23.976", "24", "25", "29.97", "50", "59.94".</param>
    /// <param name="videoBitrateKbps">Video bitrate in kbps (0 = default).</param>
    /// <param name="audioCodec">"AC3", "EAC3", "DTS", "TrueHD", "LPCM".</param>
    /// <param name="audioBitrateKbps">Audio bitrate in kbps (0 = default).</param>
    /// <param name="audioChannels">Number of audio channels (2, 6, 8).</param>
    /// <param name="progress">Progress reporter.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task TranscodeToBluRayAsync(
        string inputPath,
        string outputPath,
        string videoCodec = "H264",
        string videoFormat = "1080p",
        string frameRate = "23.976",
        int videoBitrateKbps = 0,
        string audioCodec = "AC3",
        int audioBitrateKbps = 0,
        int audioChannels = 2,
        IProgress<BuildProgress>? progress = null,
        CancellationToken ct = default)
    {
        var ffmpeg = FindFfmpeg() ?? throw new InvalidOperationException(
            "FFmpeg is required for Blu-ray video transcoding.");

        if (!File.Exists(inputPath))
            throw new FileNotFoundException($"Input video file not found: {inputPath}");

        // Parse resolution from format
        var (width, height, isInterlaced) = ParseVideoFormat(videoFormat);
        int vBitrate = videoBitrateKbps > 0
            ? Math.Min(videoBitrateKbps, BluRayMaxVideoBitrateKbps)
            : BluRayDefaultVideoBitrateKbps;
        int aBitrate = audioBitrateKbps > 0 ? audioBitrateKbps :
            audioChannels >= 6 ? 640 : 256;

        // Build video encoder parameters
        string videoParams = videoCodec.ToUpperInvariant() switch
        {
            "H264" => BuildH264Params(width, height, isInterlaced, frameRate, vBitrate),
            "MPEG2" => BuildMpeg2Params(width, height, isInterlaced, frameRate, vBitrate),
            "VC1" => BuildVc1Params(width, height, isInterlaced, frameRate, vBitrate),
            _ => BuildH264Params(width, height, isInterlaced, frameRate, vBitrate)
        };

        // Build audio encoder parameters
        string audioParams = audioCodec.ToUpperInvariant() switch
        {
            "AC3" => $"-c:a ac3 -b:a {aBitrate}k -ac {audioChannels} -ar 48000",
            "EAC3" => $"-c:a eac3 -b:a {aBitrate}k -ac {audioChannels} -ar 48000",
            "DTS" => $"-c:a dca -b:a {aBitrate}k -ac {audioChannels} -ar 48000 -strict -2",
            "TRUEHD" => $"-c:a truehd -ac {audioChannels} -ar 48000",
            "LPCM" => $"-c:a pcm_bluray -ac {audioChannels} -ar 48000",
            _ => $"-c:a ac3 -b:a {aBitrate}k -ac {audioChannels} -ar 48000"
        };

        var sb = new StringBuilder();
        sb.Append($"-i \"{inputPath}\" ");
        sb.Append($"{videoParams} ");
        sb.Append($"{audioParams} ");
        sb.Append($"-f mpegts -y \"{outputPath}\"");

        var inputDuration = await GetDurationAsync(ffmpeg, inputPath, ct);

        progress?.Report(new BuildProgress
        {
            StatusMessage = $"Transcoding to Blu-ray format ({videoFormat} {videoCodec})...",
            LogLine = $"[Info] Transcoding: {Path.GetFileName(inputPath)} → Blu-ray M2TS ({videoCodec} {videoFormat})"
        });

        await RunFfmpegWithProgressAsync(ffmpeg, sb.ToString(), inputDuration, progress, ct);

        progress?.Report(new BuildProgress
        {
            LogLine = $"[Info] Blu-ray transcode complete: {Path.GetFileName(outputPath)}"
        });
    }

    /// <summary>
    /// Transcodes audio to DVD-compliant format (AC-3 or LPCM).
    /// </summary>
    public static async Task TranscodeAudioForDvdAsync(
        string inputPath,
        string outputPath,
        string codec = "AC3",
        int bitrateKbps = 192,
        int channels = 2,
        IProgress<BuildProgress>? progress = null,
        CancellationToken ct = default)
    {
        var ffmpeg = FindFfmpeg() ?? throw new InvalidOperationException(
            "FFmpeg is required for audio transcoding.");

        string audioParams = codec.ToUpperInvariant() switch
        {
            "AC3" => $"-c:a ac3 -b:a {bitrateKbps}k -ac {channels} -ar 48000",
            "MP2" => $"-c:a mp2 -b:a {bitrateKbps}k -ac {channels} -ar 48000",
            "LPCM" => $"-c:a pcm_s16be -ac {channels} -ar 48000",
            _ => $"-c:a ac3 -b:a {bitrateKbps}k -ac {channels} -ar 48000"
        };

        var args = $"-i \"{inputPath}\" -vn {audioParams} -y \"{outputPath}\"";
        await RunFfmpegWithProgressAsync(ffmpeg, args, TimeSpan.Zero, progress, ct);
    }

    /// <summary>
    /// Transcodes audio to Blu-ray compliant format.
    /// </summary>
    public static async Task TranscodeAudioForBluRayAsync(
        string inputPath,
        string outputPath,
        string codec = "AC3",
        int bitrateKbps = 640,
        int channels = 6,
        int sampleRate = 48000,
        IProgress<BuildProgress>? progress = null,
        CancellationToken ct = default)
    {
        var ffmpeg = FindFfmpeg() ?? throw new InvalidOperationException(
            "FFmpeg is required for audio transcoding.");

        string audioParams = codec.ToUpperInvariant() switch
        {
            "AC3" => $"-c:a ac3 -b:a {bitrateKbps}k -ac {channels} -ar {sampleRate}",
            "EAC3" => $"-c:a eac3 -b:a {bitrateKbps}k -ac {channels} -ar {sampleRate}",
            "DTS" => $"-c:a dca -b:a {bitrateKbps}k -ac {channels} -ar {sampleRate} -strict -2",
            "LPCM" => $"-c:a pcm_bluray -ac {channels} -ar {sampleRate}",
            _ => $"-c:a ac3 -b:a {bitrateKbps}k -ac {channels} -ar {sampleRate}"
        };

        var args = $"-i \"{inputPath}\" -vn {audioParams} -y \"{outputPath}\"";
        await RunFfmpegWithProgressAsync(ffmpeg, args, TimeSpan.Zero, progress, ct);
    }

    /// <summary>
    /// Converts a text subtitle file (.srt, .ass) to DVD VobSub bitmap format.
    /// </summary>
    public static async Task ConvertSubtitleForDvdAsync(
        string inputPath,
        string outputPath,
        string videoStandard = "NTSC",
        int fontSize = 24,
        string fontColor = "#FFFFFF",
        IProgress<BuildProgress>? progress = null,
        CancellationToken ct = default)
    {
        var ffmpeg = FindFfmpeg() ?? throw new InvalidOperationException(
            "FFmpeg is required for subtitle conversion.");

        bool isNtsc = videoStandard.Equals("NTSC", StringComparison.OrdinalIgnoreCase);
        string resolution = isNtsc ? "720x480" : "720x576";

        // Use FFmpeg to render text subtitles onto a transparent video, then extract as dvdsub
        var args = $"-i \"{inputPath}\" -c:s dvd_subtitle -s {resolution} -y \"{outputPath}\"";
        await RunFfmpegWithProgressAsync(ffmpeg, args, TimeSpan.Zero, progress, ct);
    }

    /// <summary>
    /// Converts a text subtitle file to Blu-ray PGS (.sup) format.
    /// </summary>
    public static async Task ConvertSubtitleForBluRayAsync(
        string inputPath,
        string outputPath,
        string videoFormat = "1080p",
        int fontSize = 36,
        string fontColor = "#FFFFFF",
        IProgress<BuildProgress>? progress = null,
        CancellationToken ct = default)
    {
        var ffmpeg = FindFfmpeg() ?? throw new InvalidOperationException(
            "FFmpeg is required for subtitle conversion.");

        var (width, height, _) = ParseVideoFormat(videoFormat);

        var args = $"-i \"{inputPath}\" -c:s hdmv_pgs_subtitle -s {width}x{height} -y \"{outputPath}\"";
        await RunFfmpegWithProgressAsync(ffmpeg, args, TimeSpan.Zero, progress, ct);
    }

    // -----------------------------------------------------------------------
    // Blu-ray 3D (Stereoscopic) transcoding
    // -----------------------------------------------------------------------

    /// <summary>
    /// Transcodes a video file to Blu-ray 3D compliant format.
    ///
    /// For MVC (frame-packed) mode, the dependent view is encoded as an H.264 stream
    /// that will be used alongside the base view in SSIF interleaving.
    ///
    /// For frame-compatible modes (SideBySide, TopBottom), the source file already
    /// contains both views and is transcoded with appropriate metadata.
    ///
    /// Per Blu-ray 3D specification (Profile 5.0):
    /// - MVC: H.264 Stereo High Profile, Level 4.1
    /// - SideBySide: H.264 High Profile with SEI frame_packing_arrangement (type 3)
    /// - TopBottom: H.264 High Profile with SEI frame_packing_arrangement (type 4)
    /// </summary>
    /// <param name="inputPath">Input video file path.</param>
    /// <param name="outputPath">Output M2TS file path.</param>
    /// <param name="videoFormat">Video format: "1080p", "1080i", "720p", etc.</param>
    /// <param name="frameRate">Frame rate: "23.976", "24", "25", "29.97".</param>
    /// <param name="videoBitrateKbps">Video bitrate in kbps (0 = default).</param>
    /// <param name="mode3D">3D mode: "MVC", "SideBySide", "TopBottom".</param>
    /// <param name="progress">Progress reporter.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task TranscodeToBluRay3DAsync(
        string inputPath,
        string outputPath,
        string videoFormat = "1080p",
        string frameRate = "23.976",
        int videoBitrateKbps = 0,
        string mode3D = "MVC",
        IProgress<BuildProgress>? progress = null,
        CancellationToken ct = default)
    {
        var ffmpeg = FindFfmpeg() ?? throw new InvalidOperationException(
            "FFmpeg is required for Blu-ray 3D video transcoding.");

        if (!File.Exists(inputPath))
            throw new FileNotFoundException($"Input video file not found: {inputPath}");

        var (width, height, isInterlaced) = ParseVideoFormat(videoFormat);
        int vBitrate = videoBitrateKbps > 0
            ? Math.Min(videoBitrateKbps, BluRayMaxVideoBitrateKbps)
            : BluRayDefaultVideoBitrateKbps;

        var sb = new StringBuilder();
        sb.Append($"-i \"{inputPath}\" ");

        var modeUpper = mode3D.ToUpperInvariant();
        switch (modeUpper)
        {
            case "MVC":
                // MVC dependent view: encode as H.264 that will be muxed as MVC dependent
                // FFmpeg doesn't natively encode MVC, so we encode as standard H.264
                // with Stereo High profile constraints; the SSIF interleaver handles the rest
                sb.Append($"-c:v libx264 ");
                sb.Append($"-b:v {vBitrate}k -maxrate {vBitrate}k -bufsize {vBitrate * 2}k ");
                sb.Append($"-vf \"scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2\" ");
                sb.Append($"-r {frameRate} ");
                sb.Append("-profile:v high -level 4.1 ");
                sb.Append("-refs 4 -bf 3 -b_strategy 1 ");
                sb.Append("-flags +cgop -g 12 ");
                sb.Append("-pix_fmt yuv420p ");
                break;

            case "SIDEBYSIDE":
                // Frame-compatible side-by-side: half horizontal resolution per eye
                // Input is already SBS; scale to full BD resolution with x264 metadata
                sb.Append($"-c:v libx264 ");
                sb.Append($"-b:v {vBitrate}k -maxrate {vBitrate}k -bufsize {vBitrate * 2}k ");
                sb.Append($"-vf \"scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2\" ");
                sb.Append($"-r {frameRate} ");
                sb.Append("-profile:v high -level 4.1 ");
                sb.Append("-x264-params frame-packing=3 ");
                sb.Append("-refs 4 -bf 3 -b_strategy 1 ");
                sb.Append("-flags +cgop -g 12 ");
                sb.Append("-pix_fmt yuv420p ");
                break;

            case "TOPBOTTOM":
                // Frame-compatible top-and-bottom: half vertical resolution per eye
                sb.Append($"-c:v libx264 ");
                sb.Append($"-b:v {vBitrate}k -maxrate {vBitrate}k -bufsize {vBitrate * 2}k ");
                sb.Append($"-vf \"scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2\" ");
                sb.Append($"-r {frameRate} ");
                sb.Append("-profile:v high -level 4.1 ");
                sb.Append("-x264-params frame-packing=4 ");
                sb.Append("-refs 4 -bf 3 -b_strategy 1 ");
                sb.Append("-flags +cgop -g 12 ");
                sb.Append("-pix_fmt yuv420p ");
                break;

            default:
                throw new ArgumentException($"Unsupported 3D mode: '{mode3D}'. Supported: MVC, SideBySide, TopBottom.");
        }

        sb.Append($"-an -f mpegts -y \"{outputPath}\"");

        var inputDuration = await GetDurationAsync(ffmpeg, inputPath, ct);

        progress?.Report(new BuildProgress
        {
            StatusMessage = $"Transcoding 3D {modeUpper} to Blu-ray format ({videoFormat})...",
            LogLine = $"[Info] 3D Transcoding: {Path.GetFileName(inputPath)} → Blu-ray 3D M2TS ({modeUpper})"
        });

        await RunFfmpegWithProgressAsync(ffmpeg, sb.ToString(), inputDuration, progress, ct);

        progress?.Report(new BuildProgress
        {
            LogLine = $"[Info] 3D transcode complete: {Path.GetFileName(outputPath)}"
        });
    }

    /// <summary>
    /// Transcodes a video file to BDAV (Blu-ray recording) compliant MPEG-2 Transport Stream.
    ///
    /// BDAV has similar requirements to BDMV but with a reduced maximum bitrate (28 Mbps)
    /// and is designed for real-time recording on BD-R/BD-RE media.
    /// Per BDA specification Part 3-2, section 5.3.
    /// </summary>
    /// <param name="inputPath">Input video file path.</param>
    /// <param name="outputPath">Output M2TS file path.</param>
    /// <param name="videoCodec">"H264", "MPEG2", or "VC1".</param>
    /// <param name="videoFormat">"1080i", "720p", "480i", "576i", "1080p", etc.</param>
    /// <param name="frameRate">"23.976", "24", "25", "29.97", "50", "59.94".</param>
    /// <param name="videoBitrateKbps">Video bitrate in kbps (0 = default). Max 28,000 for BDAV.</param>
    /// <param name="audioCodec">"AC3", "EAC3", "DTS", "LPCM".</param>
    /// <param name="audioBitrateKbps">Audio bitrate in kbps (0 = default).</param>
    /// <param name="audioChannels">Number of audio channels (2, 6, 8).</param>
    /// <param name="progress">Progress reporter.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task TranscodeToBdavAsync(
        string inputPath,
        string outputPath,
        string videoCodec = "H264",
        string videoFormat = "1080i",
        string frameRate = "29.97",
        int videoBitrateKbps = 0,
        string audioCodec = "AC3",
        int audioBitrateKbps = 0,
        int audioChannels = 2,
        IProgress<BuildProgress>? progress = null,
        CancellationToken ct = default)
    {
        var ffmpeg = FindFfmpeg() ?? throw new InvalidOperationException(
            "FFmpeg is required for BDAV video transcoding.");

        if (!File.Exists(inputPath))
            throw new FileNotFoundException($"Input video file not found: {inputPath}");

        // Validate BDAV-allowed audio codecs per BDA specification Part 3-2
        var allowedAudioCodecs = new[] { "AC3", "EAC3", "DTS", "LPCM" };
        var normalizedAudioCodec = audioCodec.ToUpperInvariant();
        if (Array.IndexOf(allowedAudioCodecs, normalizedAudioCodec) < 0)
            throw new ArgumentException(
                $"Audio codec '{audioCodec}' is not supported for BDAV. Allowed: AC3, EAC3, DTS, LPCM.");

        // Validate BDAV-allowed video codecs
        var allowedVideoCodecs = new[] { "H264", "MPEG2", "VC1" };
        var normalizedVideoCodec = videoCodec.ToUpperInvariant();
        if (Array.IndexOf(allowedVideoCodecs, normalizedVideoCodec) < 0)
            throw new ArgumentException(
                $"Video codec '{videoCodec}' is not supported for BDAV. Allowed: H264, MPEG2, VC1.");

        const int bdavMaxBitrateKbps = 28000;
        var (width, height, isInterlaced) = ParseVideoFormat(videoFormat);
        int vBitrate = videoBitrateKbps > 0
            ? Math.Min(videoBitrateKbps, bdavMaxBitrateKbps)
            : Math.Min(BluRayDefaultVideoBitrateKbps, bdavMaxBitrateKbps);
        int aBitrate = audioBitrateKbps > 0 ? audioBitrateKbps :
            audioChannels >= 6 ? 448 : 192;

        // Build video encoder parameters (same codecs as BDMV, lower bitrate cap)
        string videoParams = videoCodec.ToUpperInvariant() switch
        {
            "H264" => BuildH264Params(width, height, isInterlaced, frameRate, vBitrate),
            "MPEG2" => BuildMpeg2Params(width, height, isInterlaced, frameRate, vBitrate),
            "VC1" => BuildVc1Params(width, height, isInterlaced, frameRate, vBitrate),
            _ => BuildH264Params(width, height, isInterlaced, frameRate, vBitrate)
        };

        // Build audio encoder parameters (BDAV supports AC-3, E-AC-3, DTS, LPCM)
        string audioParams = audioCodec.ToUpperInvariant() switch
        {
            "AC3" => $"-c:a ac3 -b:a {aBitrate}k -ac {audioChannels} -ar 48000",
            "EAC3" => $"-c:a eac3 -b:a {aBitrate}k -ac {audioChannels} -ar 48000",
            "DTS" => $"-c:a dca -b:a {aBitrate}k -ac {audioChannels} -ar 48000 -strict -2",
            "LPCM" => $"-c:a pcm_bluray -ac {audioChannels} -ar 48000",
            _ => $"-c:a ac3 -b:a {aBitrate}k -ac {audioChannels} -ar 48000"
        };

        var args = new StringBuilder();
        args.Append($"-i \"{inputPath}\" ");
        args.Append($"{videoParams} ");
        args.Append($"{audioParams} ");
        args.Append($"-f mpegts -y \"{outputPath}\"");

        var inputDuration = await GetDurationAsync(ffmpeg, inputPath, ct);

        progress?.Report(new BuildProgress
        {
            StatusMessage = $"Transcoding to BDAV format ({videoFormat} {videoCodec})...",
            LogLine = $"[Info] Transcoding: {Path.GetFileName(inputPath)} → BDAV M2TS ({videoCodec} {videoFormat})"
        });

        await RunFfmpegWithProgressAsync(ffmpeg, args.ToString(), inputDuration, progress, ct);

        progress?.Report(new BuildProgress
        {
            LogLine = $"[Info] BDAV transcode complete: {Path.GetFileName(outputPath)}"
        });
    }

    // -----------------------------------------------------------------------
    // Container-aware transcoding for TS, MP4, MKV, WebM inputs
    // -----------------------------------------------------------------------

    /// <summary>
    /// Supported video input extensions for disc workflows. Includes native disc
    /// formats (MPEG-PS/TS) and container formats (MP4, MKV, WebM, AVI, MOV, etc.)
    /// that FFmpeg can demux and transcode.
    /// </summary>
    public static readonly HashSet<string> SupportedVideoInputExtensions =
        FormatHelper.SupportedVideoInputExtensions;

    /// <summary>
    /// Returns true if the input file is a supported video container for disc workflows.
    /// </summary>
    public static bool IsSupportedVideoInput(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return FormatHelper.IsSupportedVideoInput(ext);
    }

    /// <summary>
    /// Detects whether the input file requires transcoding for DVD output.
    /// Non-MPEG-PS containers (TS, MP4, MKV, WebM, AVI, MOV, etc.) always require transcoding.
    /// MPEG-PS files may also need transcoding if not DVD-compliant (wrong resolution, etc.).
    /// </summary>
    public static bool RequiresTranscodeForDvd(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return FormatHelper.RequiresTranscodeToMpegPs(ext);
    }

    /// <summary>
    /// Detects whether the input file requires transcoding for Blu-ray output.
    /// Non-MPEG-TS containers (MP4, MKV, WebM, AVI, MOV, etc.) always require transcoding.
    /// </summary>
    public static bool RequiresTranscodeForBluRay(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return FormatHelper.RequiresTranscodeToMpegTs(ext);
    }

    /// <summary>
    /// Transcodes any supported video container (TS, MP4, MKV, WebM, AVI, MOV, etc.)
    /// to DVD-Video compliant MPEG-2 Program Stream.
    ///
    /// This method handles container detection and applies the correct FFmpeg demuxing
    /// for each container format:
    /// - MPEG-TS (.ts, .mts, .m2ts): demux transport stream, re-encode to DVD MPEG-2 PS
    /// - MP4 (.mp4, .m4v): demux ISO BMFF, re-encode to DVD MPEG-2 PS
    /// - MKV (.mkv, .mk3d): demux EBML/Matroska, re-encode to DVD MPEG-2 PS
    /// - WebM (.webm): demux VP8/VP9+Vorbis/Opus, re-encode to DVD MPEG-2 PS
    /// - Other containers (AVI, MOV, WMV, FLV): generic FFmpeg demux + re-encode
    ///
    /// For MPEG-PS inputs that are already DVD-compliant, this method still works
    /// but TranscodeToDvdAsync may be more efficient.
    /// </summary>
    /// <param name="inputPath">Input video file in any supported container format.</param>
    /// <param name="outputPath">Output MPEG-2 PS file path (.mpg).</param>
    /// <param name="videoStandard">"NTSC" or "PAL".</param>
    /// <param name="aspectRatio">"4:3" or "16:9".</param>
    /// <param name="videoBitrateKbps">Video bitrate in kbps (0 = default).</param>
    /// <param name="audioCodec">"AC3", "MP2", or "LPCM".</param>
    /// <param name="audioBitrateKbps">Audio bitrate in kbps (0 = default).</param>
    /// <param name="progress">Progress reporter.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task TranscodeContainerToDvdAsync(
        string inputPath,
        string outputPath,
        string videoStandard = "NTSC",
        string aspectRatio = "16:9",
        int videoBitrateKbps = 0,
        string audioCodec = "AC3",
        int audioBitrateKbps = 0,
        IProgress<BuildProgress>? progress = null,
        CancellationToken ct = default)
    {
        var ffmpeg = FindFfmpeg() ?? throw new InvalidOperationException(
            "FFmpeg is required for video transcoding.");

        if (!File.Exists(inputPath))
            throw new FileNotFoundException($"Input video file not found: {inputPath}");

        var containerType = FormatHelper.GetContainerType(Path.GetExtension(inputPath));
        progress?.Report(new BuildProgress
        {
            StatusMessage = $"Transcoding {containerType} to DVD format ({videoStandard})...",
            LogLine = $"[Info] Container: {containerType} → DVD MPEG-2 PS ({Path.GetFileName(inputPath)})"
        });

        bool isNtsc = videoStandard.Equals("NTSC", StringComparison.OrdinalIgnoreCase);
        string resolution = isNtsc ? "720:480" : "720:576";
        string frameRate = isNtsc ? "29.97" : "25";
        int vBitrate = videoBitrateKbps > 0
            ? Math.Min(videoBitrateKbps, DvdMaxVideoBitrateKbps)
            : DvdDefaultVideoBitrateKbps;
        int aBitrate = audioBitrateKbps > 0 ? audioBitrateKbps : 192;

        string audioParams = audioCodec.ToUpperInvariant() switch
        {
            "AC3" => $"-c:a ac3 -b:a {aBitrate}k -ar 48000",
            "MP2" => $"-c:a mp2 -b:a {aBitrate}k -ar 48000",
            "LPCM" => "-c:a pcm_s16be -ar 48000",
            _ => $"-c:a ac3 -b:a {aBitrate}k -ar 48000"
        };

        string aspectParam = aspectRatio == "4:3" ? "4/3" : "16/9";

        var sb = new StringBuilder();
        // Container-specific input options
        sb.Append(BuildContainerInputArgs(containerType));
        sb.Append($"-i \"{inputPath}\" ");
        sb.Append($"-target {(isNtsc ? "ntsc" : "pal")}-dvd ");
        sb.Append($"-vf \"scale={resolution}:force_original_aspect_ratio=decrease,pad={resolution}:(ow-iw)/2:(oh-ih)/2\" ");
        sb.Append($"-b:v {vBitrate}k -maxrate {DvdMaxVideoBitrateKbps}k -bufsize 1835k ");
        sb.Append($"-aspect {aspectParam} ");
        sb.Append($"-r {frameRate} ");
        sb.Append($"{audioParams} ");
        // Map all streams explicitly to avoid container-specific stream selection issues.
        // The "?" suffix (FFmpeg 4.1+) makes the audio stream optional — if the input
        // has no audio, FFmpeg proceeds without error instead of failing.
        sb.Append("-map 0:v:0 -map 0:a:0? ");
        sb.Append($"-f dvd -y \"{outputPath}\"");

        var inputDuration = await GetDurationAsync(ffmpeg, inputPath, ct);
        await RunFfmpegWithProgressAsync(ffmpeg, sb.ToString(), inputDuration, progress, ct);

        progress?.Report(new BuildProgress
        {
            LogLine = $"[Info] DVD transcode complete: {Path.GetFileName(outputPath)}"
        });
    }

    /// <summary>
    /// Transcodes any supported video container (TS, MP4, MKV, WebM, AVI, MOV, etc.)
    /// to Blu-ray compliant MPEG-2 Transport Stream (.m2ts).
    ///
    /// Container-specific handling:
    /// - MPEG-TS: may remux if streams are already Blu-ray compliant (H.264+AC-3)
    /// - MP4: extracts H.264/AAC and re-encodes audio to Blu-ray codecs
    /// - MKV: extracts any codec combination and transcodes as needed
    /// - WebM: extracts VP8/VP9+Vorbis/Opus and transcodes to H.264+AC-3
    ///
    /// Per Blu-ray specification: output is MPEG-2 TS with 192-byte packets
    /// containing H.264/MPEG-2/VC-1 video and AC-3/EAC-3/DTS/LPCM audio.
    /// </summary>
    public static async Task TranscodeContainerToBluRayAsync(
        string inputPath,
        string outputPath,
        string videoCodec = "H264",
        string videoFormat = "1080p",
        string frameRate = "23.976",
        int videoBitrateKbps = 0,
        string audioCodec = "AC3",
        int audioBitrateKbps = 0,
        int audioChannels = 2,
        IProgress<BuildProgress>? progress = null,
        CancellationToken ct = default)
    {
        var ffmpeg = FindFfmpeg() ?? throw new InvalidOperationException(
            "FFmpeg is required for Blu-ray video transcoding.");

        if (!File.Exists(inputPath))
            throw new FileNotFoundException($"Input video file not found: {inputPath}");

        var containerType = FormatHelper.GetContainerType(Path.GetExtension(inputPath));
        progress?.Report(new BuildProgress
        {
            StatusMessage = $"Transcoding {containerType} to Blu-ray format ({videoFormat} {videoCodec})...",
            LogLine = $"[Info] Container: {containerType} → Blu-ray M2TS ({Path.GetFileName(inputPath)})"
        });

        var (width, height, isInterlaced) = ParseVideoFormat(videoFormat);
        int vBitrate = videoBitrateKbps > 0
            ? Math.Min(videoBitrateKbps, BluRayMaxVideoBitrateKbps)
            : BluRayDefaultVideoBitrateKbps;
        int aBitrate = audioBitrateKbps > 0 ? audioBitrateKbps :
            audioChannels >= 6 ? 640 : 256;

        string videoParams = videoCodec.ToUpperInvariant() switch
        {
            "H264" => BuildH264Params(width, height, isInterlaced, frameRate, vBitrate),
            "MPEG2" => BuildMpeg2Params(width, height, isInterlaced, frameRate, vBitrate),
            "VC1" => BuildVc1Params(width, height, isInterlaced, frameRate, vBitrate),
            _ => BuildH264Params(width, height, isInterlaced, frameRate, vBitrate)
        };

        string audioParams = audioCodec.ToUpperInvariant() switch
        {
            "AC3" => $"-c:a ac3 -b:a {aBitrate}k -ac {audioChannels} -ar 48000",
            "EAC3" => $"-c:a eac3 -b:a {aBitrate}k -ac {audioChannels} -ar 48000",
            "DTS" => $"-c:a dca -b:a {aBitrate}k -ac {audioChannels} -ar 48000 -strict -2",
            "TRUEHD" => $"-c:a truehd -ac {audioChannels} -ar 48000",
            "LPCM" => $"-c:a pcm_bluray -ac {audioChannels} -ar 48000",
            _ => $"-c:a ac3 -b:a {aBitrate}k -ac {audioChannels} -ar 48000"
        };

        var sb = new StringBuilder();
        sb.Append(BuildContainerInputArgs(containerType));
        sb.Append($"-i \"{inputPath}\" ");
        sb.Append($"{videoParams} ");
        sb.Append($"{audioParams} ");
        sb.Append("-map 0:v:0 -map 0:a:0? ");
        sb.Append($"-f mpegts -y \"{outputPath}\"");

        var inputDuration = await GetDurationAsync(ffmpeg, inputPath, ct);
        await RunFfmpegWithProgressAsync(ffmpeg, sb.ToString(), inputDuration, progress, ct);

        progress?.Report(new BuildProgress
        {
            LogLine = $"[Info] Blu-ray transcode complete: {Path.GetFileName(outputPath)}"
        });
    }

    /// <summary>
    /// Transcodes any supported video container to BDAV (Blu-ray recording) compliant
    /// MPEG-2 Transport Stream with a 28 Mbps maximum bitrate cap.
    ///
    /// Per BDA specification Part 3-2:
    /// - Video: H.264/AVC, MPEG-2, VC-1 (max 28 Mbps total mux rate)
    /// - Audio: AC-3, E-AC-3, DTS, LPCM
    /// </summary>
    public static async Task TranscodeContainerToBdavAsync(
        string inputPath,
        string outputPath,
        string videoCodec = "H264",
        string videoFormat = "1080i",
        string frameRate = "29.97",
        int videoBitrateKbps = 0,
        string audioCodec = "AC3",
        int audioBitrateKbps = 0,
        int audioChannels = 2,
        IProgress<BuildProgress>? progress = null,
        CancellationToken ct = default)
    {
        var ffmpeg = FindFfmpeg() ?? throw new InvalidOperationException(
            "FFmpeg is required for BDAV video transcoding.");

        if (!File.Exists(inputPath))
            throw new FileNotFoundException($"Input video file not found: {inputPath}");

        // Validate BDAV-allowed codecs
        var allowedAudioCodecs = new[] { "AC3", "EAC3", "DTS", "LPCM" };
        var normalizedAudioCodec = audioCodec.ToUpperInvariant();
        if (Array.IndexOf(allowedAudioCodecs, normalizedAudioCodec) < 0)
            throw new ArgumentException(
                $"Audio codec '{audioCodec}' is not supported for BDAV. Allowed: AC3, EAC3, DTS, LPCM.");

        var allowedVideoCodecs = new[] { "H264", "MPEG2", "VC1" };
        var normalizedVideoCodec = videoCodec.ToUpperInvariant();
        if (Array.IndexOf(allowedVideoCodecs, normalizedVideoCodec) < 0)
            throw new ArgumentException(
                $"Video codec '{videoCodec}' is not supported for BDAV. Allowed: H264, MPEG2, VC1.");

        var containerType = FormatHelper.GetContainerType(Path.GetExtension(inputPath));
        progress?.Report(new BuildProgress
        {
            StatusMessage = $"Transcoding {containerType} to BDAV format ({videoFormat} {videoCodec})...",
            LogLine = $"[Info] Container: {containerType} → BDAV M2TS ({Path.GetFileName(inputPath)})"
        });

        const int bdavMaxBitrateKbps = 28000;
        var (width, height, isInterlaced) = ParseVideoFormat(videoFormat);
        int vBitrate = videoBitrateKbps > 0
            ? Math.Min(videoBitrateKbps, bdavMaxBitrateKbps)
            : Math.Min(BluRayDefaultVideoBitrateKbps, bdavMaxBitrateKbps);
        int aBitrate = audioBitrateKbps > 0 ? audioBitrateKbps :
            audioChannels >= 6 ? 448 : 192;

        string videoParams = videoCodec.ToUpperInvariant() switch
        {
            "H264" => BuildH264Params(width, height, isInterlaced, frameRate, vBitrate),
            "MPEG2" => BuildMpeg2Params(width, height, isInterlaced, frameRate, vBitrate),
            "VC1" => BuildVc1Params(width, height, isInterlaced, frameRate, vBitrate),
            _ => BuildH264Params(width, height, isInterlaced, frameRate, vBitrate)
        };

        string audioParams = audioCodec.ToUpperInvariant() switch
        {
            "AC3" => $"-c:a ac3 -b:a {aBitrate}k -ac {audioChannels} -ar 48000",
            "EAC3" => $"-c:a eac3 -b:a {aBitrate}k -ac {audioChannels} -ar 48000",
            "DTS" => $"-c:a dca -b:a {aBitrate}k -ac {audioChannels} -ar 48000 -strict -2",
            "LPCM" => $"-c:a pcm_bluray -ac {audioChannels} -ar 48000",
            _ => $"-c:a ac3 -b:a {aBitrate}k -ac {audioChannels} -ar 48000"
        };

        var sb = new StringBuilder();
        sb.Append(BuildContainerInputArgs(containerType));
        sb.Append($"-i \"{inputPath}\" ");
        sb.Append($"{videoParams} ");
        sb.Append($"{audioParams} ");
        sb.Append("-map 0:v:0 -map 0:a:0? ");
        sb.Append($"-f mpegts -y \"{outputPath}\"");

        var inputDuration = await GetDurationAsync(ffmpeg, inputPath, ct);
        await RunFfmpegWithProgressAsync(ffmpeg, sb.ToString(), inputDuration, progress, ct);

        progress?.Report(new BuildProgress
        {
            LogLine = $"[Info] BDAV transcode complete: {Path.GetFileName(outputPath)}"
        });
    }

    /// <summary>
    /// Transcodes any supported video container to Blu-ray 3D compliant format.
    ///
    /// Supports MVC (frame-packed), SideBySide, and TopBottom 3D modes.
    /// Container-specific handling is the same as TranscodeContainerToBluRayAsync
    /// but with 3D metadata (SEI frame_packing_arrangement) for frame-compatible modes.
    /// </summary>
    public static async Task TranscodeContainerToBluRay3DAsync(
        string inputPath,
        string outputPath,
        string videoFormat = "1080p",
        string frameRate = "23.976",
        int videoBitrateKbps = 0,
        string mode3D = "MVC",
        IProgress<BuildProgress>? progress = null,
        CancellationToken ct = default)
    {
        var ffmpeg = FindFfmpeg() ?? throw new InvalidOperationException(
            "FFmpeg is required for Blu-ray 3D video transcoding.");

        if (!File.Exists(inputPath))
            throw new FileNotFoundException($"Input video file not found: {inputPath}");

        var containerType = FormatHelper.GetContainerType(Path.GetExtension(inputPath));
        progress?.Report(new BuildProgress
        {
            StatusMessage = $"Transcoding {containerType} to Blu-ray 3D ({mode3D} {videoFormat})...",
            LogLine = $"[Info] Container: {containerType} → Blu-ray 3D M2TS ({mode3D}) ({Path.GetFileName(inputPath)})"
        });

        var (width, height, isInterlaced) = ParseVideoFormat(videoFormat);
        int vBitrate = videoBitrateKbps > 0
            ? Math.Min(videoBitrateKbps, BluRayMaxVideoBitrateKbps)
            : BluRayDefaultVideoBitrateKbps;

        var sb = new StringBuilder();
        sb.Append(BuildContainerInputArgs(containerType));
        sb.Append($"-i \"{inputPath}\" ");

        var modeUpper = mode3D.ToUpperInvariant();
        switch (modeUpper)
        {
            case "MVC":
                sb.Append($"-c:v libx264 ");
                sb.Append($"-b:v {vBitrate}k -maxrate {vBitrate}k -bufsize {vBitrate * 2}k ");
                sb.Append($"-vf \"scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2\" ");
                sb.Append($"-r {frameRate} ");
                sb.Append("-profile:v high -level 4.1 ");
                sb.Append("-refs 4 -bf 3 -b_strategy 1 ");
                sb.Append("-flags +cgop -g 12 ");
                sb.Append("-pix_fmt yuv420p ");
                break;

            case "SIDEBYSIDE":
                sb.Append($"-c:v libx264 ");
                sb.Append($"-b:v {vBitrate}k -maxrate {vBitrate}k -bufsize {vBitrate * 2}k ");
                sb.Append($"-vf \"scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2\" ");
                sb.Append($"-r {frameRate} ");
                sb.Append("-profile:v high -level 4.1 ");
                sb.Append("-x264-params frame-packing=3 ");
                sb.Append("-refs 4 -bf 3 -b_strategy 1 ");
                sb.Append("-flags +cgop -g 12 ");
                sb.Append("-pix_fmt yuv420p ");
                break;

            case "TOPBOTTOM":
                sb.Append($"-c:v libx264 ");
                sb.Append($"-b:v {vBitrate}k -maxrate {vBitrate}k -bufsize {vBitrate * 2}k ");
                sb.Append($"-vf \"scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2\" ");
                sb.Append($"-r {frameRate} ");
                sb.Append("-profile:v high -level 4.1 ");
                sb.Append("-x264-params frame-packing=4 ");
                sb.Append("-refs 4 -bf 3 -b_strategy 1 ");
                sb.Append("-flags +cgop -g 12 ");
                sb.Append("-pix_fmt yuv420p ");
                break;

            default:
                throw new ArgumentException($"Unsupported 3D mode: '{mode3D}'. Supported: MVC, SideBySide, TopBottom.");
        }

        sb.Append("-map 0:v:0 ");
        sb.Append($"-an -f mpegts -y \"{outputPath}\"");

        var inputDuration = await GetDurationAsync(ffmpeg, inputPath, ct);
        await RunFfmpegWithProgressAsync(ffmpeg, sb.ToString(), inputDuration, progress, ct);

        progress?.Report(new BuildProgress
        {
            LogLine = $"[Info] 3D transcode complete: {Path.GetFileName(outputPath)}"
        });
    }

    /// <summary>
    /// Transcodes any supported video container to UHD Blu-ray (4K HDR) compliant
    /// MPEG-2 Transport Stream with HEVC/H.265 Main10 Profile.
    ///
    /// Per Blu-ray Disc Association UHD specification:
    /// - Video: HEVC Main10 (mandatory), 3840×2160, 10-bit, BT.2020
    /// - Audio: same as standard Blu-ray (AC-3, DTS, TrueHD, etc.)
    /// - HDR: HDR10 (mandatory), Dolby Vision and HDR10+ (optional)
    /// - Max bitrate: 100 Mbps video, 128 Mbps total mux (BD-100)
    /// </summary>
    public static async Task TranscodeContainerToUhdBluRayAsync(
        string inputPath,
        string outputPath,
        string videoFormat = "2160p",
        string frameRate = "23.976",
        int videoBitrateKbps = 0,
        string audioCodec = "AC3",
        int audioBitrateKbps = 0,
        int audioChannels = 2,
        string hdrMode = "HDR10",
        string colorSpace = "BT.2020",
        int maxCll = 0,
        int maxFall = 0,
        IProgress<BuildProgress>? progress = null,
        CancellationToken ct = default)
    {
        var ffmpeg = FindFfmpeg() ?? throw new InvalidOperationException(
            "FFmpeg is required for UHD Blu-ray video transcoding.");

        if (!File.Exists(inputPath))
            throw new FileNotFoundException($"Input video file not found: {inputPath}");

        var containerType = FormatHelper.GetContainerType(Path.GetExtension(inputPath));
        progress?.Report(new BuildProgress
        {
            StatusMessage = $"Transcoding {containerType} to UHD Blu-ray ({videoFormat} HEVC {hdrMode})...",
            LogLine = $"[Info] Container: {containerType} → UHD BD M2TS (HEVC Main10, {hdrMode}) ({Path.GetFileName(inputPath)})"
        });

        var (width, height, _) = ParseVideoFormat(videoFormat);
        // UHD BD: default to 3840×2160 if not specified
        if (width < 3840)
        {
            width = 3840;
            height = 2160;
        }

        // UHD BD max: 100 Mbps for video
        const int UhdMaxVideoBitrateKbps = 100000;
        const int UhdDefaultVideoBitrateKbps = 50000;
        int vBitrate = videoBitrateKbps > 0
            ? Math.Min(videoBitrateKbps, UhdMaxVideoBitrateKbps)
            : UhdDefaultVideoBitrateKbps;
        int aBitrate = audioBitrateKbps > 0 ? audioBitrateKbps :
            audioChannels >= 6 ? 640 : 256;

        // Build HEVC parameters with HDR10/BT.2020 metadata
        var sb = new StringBuilder();
        sb.Append(BuildContainerInputArgs(containerType));
        sb.Append($"-i \"{inputPath}\" ");

        // HEVC Main10 encoding
        sb.Append("-c:v libx265 ");
        sb.Append($"-b:v {vBitrate}k -maxrate {vBitrate}k -bufsize {vBitrate * 2}k ");
        sb.Append($"-vf \"scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2\" ");
        sb.Append($"-r {frameRate} ");
        sb.Append("-profile:v main10 -pix_fmt yuv420p10le ");

        // HDR metadata via x265 params
        var x265Params = new List<string>
        {
            "repeat-headers=1",
            "aud=1",
            "hrd=1",
            "chromaloc=2",
            "open-gop=0",
            "keyint=48",
            "min-keyint=24"
        };

        // Color space metadata
        if (colorSpace.Contains("2020", StringComparison.OrdinalIgnoreCase))
        {
            x265Params.Add("colorprim=bt2020");
            x265Params.Add("transfer=smpte2084");
            x265Params.Add("colormatrix=bt2020nc");
        }
        else
        {
            x265Params.Add("colorprim=bt709");
            x265Params.Add("transfer=bt709");
            x265Params.Add("colormatrix=bt709");
        }

        // HDR10 static metadata (SMPTE ST 2086 + CTA-861.3)
        if (hdrMode.Equals("HDR10", StringComparison.OrdinalIgnoreCase) ||
            hdrMode.Equals("HDR10Plus", StringComparison.OrdinalIgnoreCase))
        {
            if (maxCll > 0 || maxFall > 0)
                x265Params.Add($"max-cll={maxCll},{maxFall}");

            // Default mastering display metadata (DCI-P3 D65 mastering environment)
            x265Params.Add("master-display=G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,50)");
        }

        sb.Append($"-x265-params \"{string.Join(":", x265Params)}\" ");

        // Audio encoding
        string audioParams = audioCodec.ToUpperInvariant() switch
        {
            "AC3" => $"-c:a ac3 -b:a {aBitrate}k -ac {audioChannels} -ar 48000",
            "EAC3" => $"-c:a eac3 -b:a {aBitrate}k -ac {audioChannels} -ar 48000",
            "DTS" => $"-c:a dca -b:a {aBitrate}k -ac {audioChannels} -ar 48000 -strict -2",
            "TRUEHD" => $"-c:a truehd -ac {audioChannels} -ar 48000",
            "LPCM" => $"-c:a pcm_bluray -ac {audioChannels} -ar 48000",
            "DTSHD_MA" => $"-c:a dca -b:a {aBitrate}k -ac {audioChannels} -ar 48000 -strict -2",
            _ => $"-c:a ac3 -b:a {aBitrate}k -ac {audioChannels} -ar 48000"
        };

        sb.Append($"{audioParams} ");
        sb.Append("-map 0:v:0 -map 0:a:0? ");
        sb.Append($"-f mpegts -y \"{outputPath}\"");

        var inputDuration = await GetDurationAsync(ffmpeg, inputPath, ct);
        await RunFfmpegWithProgressAsync(ffmpeg, sb.ToString(), inputDuration, progress, ct);

        progress?.Report(new BuildProgress
        {
            LogLine = $"[Info] UHD BD transcode complete: {Path.GetFileName(outputPath)}"
        });
    }

    /// <summary>
    /// Builds container-specific FFmpeg input arguments for optimal demuxing.
    /// Returns a string that should be prepended before the -i argument.
    ///
    /// Container-specific optimizations:
    /// - MPEG-TS: -analyzeduration/probesize for transport stream PID detection
    /// - MKV: standard defaults (EBML parsing is fast)
    /// - MP4/MOV: standard defaults (moov atom provides full index)
    /// - WebM: standard defaults (same EBML as MKV)
    /// - AVI/DIVX: standard defaults (simple RIFF header)
    /// - OGG: increased analyzeduration for Ogg logical bitstream detection
    /// - Other: increased analyzeduration for container probe accuracy
    /// </summary>
    private static string BuildContainerInputArgs(string containerType)
    {
        return containerType switch
        {
            // MPEG-TS may need more analysis time for accurate stream detection
            // especially with multiple programs or encrypted content.
            // probesize 50M and analyzeduration 10s cover most broadcast TS files.
            "MPEG-TS" => "-analyzeduration 10000000 -probesize 50000000 ",

            // MP4/MOV rely on the moov atom for stream info; defaults are sufficient.
            // However, for fragmented MP4 (fMP4), FFmpeg handles moof atoms automatically.
            // QuickTime (.mov/.qt) uses the same atom-based structure as MP4.
            "MP4" or "MOV" => "",

            // MKV/WebM use EBML with a SeekHead for fast codec detection; defaults suffice.
            "MKV" or "WebM" => "",

            // AVI has a simple RIFF header; defaults are fine.
            // DivX Media Format uses the same RIFF/AVI structure with DivX extensions;
            // FFmpeg demuxes .divx identically to .avi.
            "AVI" or "DIVX" => "",

            // Ogg uses logical bitstream multiplexing with granule-position-based seeking.
            // Multiple chained/grouped logical streams (Theora video + Vorbis/Opus audio)
            // may require extended analysis for accurate stream enumeration.
            // probesize 30M and analyzeduration 8s ensure all logical streams are detected.
            "OGG" => "-analyzeduration 8000000 -probesize 30000000 ",

            // WMV/ASF containers need standard analysis.
            "WMV" => "",

            // For unknown/other containers, use a slightly longer probe.
            _ => "-analyzeduration 5000000 -probesize 20000000 "
        };
    }

    // -----------------------------------------------------------------------
    // H.264 / MPEG-2 / VC-1 parameter builders
    // -----------------------------------------------------------------------

    private static string BuildH264Params(int width, int height, bool interlaced, string frameRate, int bitrateKbps)
    {
        var sb = new StringBuilder();
        sb.Append($"-c:v libx264 ");
        sb.Append($"-b:v {bitrateKbps}k -maxrate {bitrateKbps}k -bufsize {bitrateKbps * 2}k ");
        sb.Append($"-vf \"scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2");
        if (interlaced)
            sb.Append(",interlace=scan=tff");
        sb.Append("\" ");
        sb.Append($"-r {frameRate} ");
        // Blu-ray H.264 profile constraints
        sb.Append(height > 576
            ? "-profile:v high -level 4.1 "
            : "-profile:v main -level 3.2 ");
        sb.Append("-refs 4 -bf 3 -b_strategy 1 ");
        sb.Append("-flags +cgop -g 12 ");
        sb.Append("-pix_fmt yuv420p ");
        return sb.ToString();
    }

    private static string BuildMpeg2Params(int width, int height, bool interlaced, string frameRate, int bitrateKbps)
    {
        var sb = new StringBuilder();
        sb.Append($"-c:v mpeg2video ");
        sb.Append($"-b:v {bitrateKbps}k -maxrate {bitrateKbps}k -bufsize {bitrateKbps}k ");
        sb.Append($"-vf \"scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2\" ");
        sb.Append($"-r {frameRate} ");
        sb.Append("-profile:v high ");
        sb.Append("-flags +ildct+ilme -top 1 ");
        sb.Append("-pix_fmt yuv420p ");
        return sb.ToString();
    }

    private static string BuildVc1Params(int width, int height, bool interlaced, string frameRate, int bitrateKbps)
    {
        // VC-1 encoding: FFmpeg does not have a native VC-1/SMPTE 421M encoder.
        // WMV3 (Windows Media Video 9 / VC-1 Simple/Main Profile) is the closest
        // available encoder and is accepted by Blu-ray authoring for VC-1 streams.
        var sb = new StringBuilder();
        sb.Append("-c:v wmv3 -profile:v main ");
        sb.Append($"-b:v {bitrateKbps}k -maxrate {bitrateKbps}k ");
        sb.Append($"-vf \"scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2\" ");
        sb.Append($"-r {frameRate} ");
        sb.Append("-pix_fmt yuv420p ");
        return sb.ToString();
    }

    // -----------------------------------------------------------------------
    // Format parsing helpers
    // -----------------------------------------------------------------------

    private static (int width, int height, bool interlaced) ParseVideoFormat(string format)
    {
        return format.ToUpperInvariant() switch
        {
            "2160P" => (3840, 2160, false), // UHD 4K
            "1080P" => (1920, 1080, false),
            "1080I" => (1920, 1080, true),
            "720P" => (1280, 720, false),
            "576P" => (720, 576, false),
            "576I" => (720, 576, true),
            "480P" => (720, 480, false),
            "480I" => (720, 480, true),
            _ => throw new ArgumentException($"Unsupported video format: '{format}'. " +
                "Supported formats: 2160p, 1080p, 1080i, 720p, 576p, 576i, 480p, 480i.")
        };
    }

    // -----------------------------------------------------------------------
    // FFmpeg process management
    // -----------------------------------------------------------------------

    private static async Task<TimeSpan> GetDurationAsync(string ffmpegPath, string inputPath, CancellationToken ct)
    {
        try
        {
            var result = await RunProcessAsync(ffmpegPath, $"-i \"{inputPath}\" -f null -", ct, timeoutMs: 10000);
            var match = DurationRx.Match(result.stderr);
            if (match.Success)
            {
                return new TimeSpan(0,
                    int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                    int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
                    int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture),
                    int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture) * 10);
            }
        }
        catch { /* duration probe is best-effort */ }
        return TimeSpan.Zero;
    }

    private static async Task RunFfmpegWithProgressAsync(
        string ffmpegPath,
        string args,
        TimeSpan totalDuration,
        IProgress<BuildProgress>? progress,
        CancellationToken ct)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        var stderrBuilder = new StringBuilder();
        process.Start();

        // Read stderr asynchronously for progress and error messages
        var stderrTask = Task.Run(async () =>
        {
            var buffer = new char[4096];
            while (true)
            {
                int read = await process.StandardError.ReadAsync(buffer, ct);
                if (read == 0) break;

                var chunk = new string(buffer, 0, read);
                stderrBuilder.Append(chunk);

                if (progress != null && totalDuration > TimeSpan.Zero)
                {
                    var timeMatch = TimeRx.Match(chunk);
                    if (timeMatch.Success)
                    {
                        var currentTime = new TimeSpan(0,
                            int.Parse(timeMatch.Groups[1].Value, CultureInfo.InvariantCulture),
                            int.Parse(timeMatch.Groups[2].Value, CultureInfo.InvariantCulture),
                            int.Parse(timeMatch.Groups[3].Value, CultureInfo.InvariantCulture),
                            int.Parse(timeMatch.Groups[4].Value, CultureInfo.InvariantCulture) * 10);

                        int pct = (int)(currentTime.TotalSeconds / totalDuration.TotalSeconds * 100);
                        progress.Report(new BuildProgress
                        {
                            PercentComplete = Math.Clamp(pct, 0, 99),
                            StatusMessage = $"Transcoding: {currentTime:hh\\:mm\\:ss} / {totalDuration:hh\\:mm\\:ss}"
                        });
                    }
                }
            }
        }, ct);

        // Also read stdout (some FFmpeg output goes there)
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);

        // Register cancellation
        using var registration = ct.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        });

        await Task.WhenAll(stderrTask, stdoutTask);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            var errorOutput = stderrBuilder.ToString();
            // Extract last few lines for the error message
            var lines = errorOutput.Split('\n');
            var lastLines = lines.Length > 5
                ? string.Join("\n", lines[^5..])
                : errorOutput;
            throw new InvalidOperationException(
                $"FFmpeg exited with code {process.ExitCode}:\n{lastLines}");
        }
    }

    private static async Task<(string stdout, string stderr)> RunProcessAsync(
        string path, string args, CancellationToken ct, int timeoutMs = 30000)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = path,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        process.Start();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);

        var stdout = await process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderr = await process.StandardError.ReadToEndAsync(cts.Token);

        try { await process.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        }

        return (stdout, stderr);
    }

    private static async Task<VideoInfo> ProbeWithFfmpegAsync(string ffmpegPath, string videoPath, CancellationToken ct)
    {
        var result = await RunProcessAsync(ffmpegPath, $"-i \"{videoPath}\" -f null -", ct, timeoutMs: 10000);
        var info = new VideoInfo { FilePath = videoPath };
        ParseProbeOutput(result.stderr, info);
        return info;
    }

    private static void ParseProbeOutput(string output, VideoInfo info)
    {
        var durationMatch = DurationRx.Match(output);
        if (durationMatch.Success)
        {
            // Group 4 captures centiseconds (e.g., "01:23:45.67" → 67 centiseconds = 670 ms)
            info.Duration = new TimeSpan(0,
                int.Parse(durationMatch.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(durationMatch.Groups[2].Value, CultureInfo.InvariantCulture),
                int.Parse(durationMatch.Groups[3].Value, CultureInfo.InvariantCulture),
                int.Parse(durationMatch.Groups[4].Value, CultureInfo.InvariantCulture) * 10);
        }

        // Parse video stream info
        var videoRx = new Regex(@"Video:\s*(\S+).*?,\s*(\d+)x(\d+)", RegexOptions.IgnoreCase);
        var videoMatch = videoRx.Match(output);
        if (videoMatch.Success)
        {
            info.VideoCodec = videoMatch.Groups[1].Value;
            info.Width = int.Parse(videoMatch.Groups[2].Value, CultureInfo.InvariantCulture);
            info.Height = int.Parse(videoMatch.Groups[3].Value, CultureInfo.InvariantCulture);
        }

        // Parse audio stream info
        var audioRx = new Regex(@"Audio:\s*(\S+)", RegexOptions.IgnoreCase);
        var audioMatch = audioRx.Match(output);
        if (audioMatch.Success)
        {
            info.AudioCodec = audioMatch.Groups[1].Value;
        }
    }
}
