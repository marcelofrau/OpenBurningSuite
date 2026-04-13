// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using OpenBurningSuite.Models;

namespace OpenBurningSuite.Native.Audio;

/// <summary>
/// Builds Red Book (IEC 60908) compliant audio CD images in BIN/CUE format.
///
/// Red Book Audio CD specifications:
/// - Sample rate: 44,100 Hz
/// - Bit depth: 16-bit signed PCM
/// - Channels: 2 (stereo)
/// - Byte order: Little-endian (Intel) in sectors
/// - Sector size: 2,352 bytes (588 stereo samples × 4 bytes/sample)
/// - 75 sectors per second
/// - Pre-gap: minimum 2 seconds (150 frames) for track 1, configurable for others
/// - Maximum 99 audio tracks per disc
/// - Maximum disc duration: 79:59:74 (MSF) = 74/80/90/99 minute media
///
/// Output:
/// - .bin file: Raw 2,352-byte audio sectors in Red Book format
/// - .cue file: CUE sheet with track layout, indexes, CD-Text, ISRC, and flags
/// - .cdt file (optional): Binary CD-Text data for CDTEXTFILE directive
/// </summary>
public static class AudioCdBuilder
{
    /// <summary>Red Book audio sector size in bytes.</summary>
    public const int SectorSize = 2352;

    /// <summary>Number of stereo samples per sector (2352 / 4 = 588).</summary>
    public const int SamplesPerSector = 588;

    /// <summary>Sectors per second (75 frames/sec per Red Book).</summary>
    public const int SectorsPerSecond = 75;

    /// <summary>Bytes per sample frame (16-bit stereo = 4 bytes).</summary>
    public const int BytesPerSampleFrame = 4;

    /// <summary>Red Book required sample rate.</summary>
    public const int RequiredSampleRate = 44100;

    /// <summary>Red Book required bits per sample.</summary>
    public const int RequiredBitsPerSample = 16;

    /// <summary>Red Book required channel count.</summary>
    public const int RequiredChannels = 2;

    /// <summary>Maximum tracks per Red Book audio CD.</summary>
    public const int MaxTracks = 99;

    /// <summary>Standard pre-gap for track 1 (2 seconds = 150 frames).</summary>
    public const int Track1PreGapFrames = 150;

    /// <summary>
    /// Builds a Red Book audio CD image as BIN/CUE files from the specified audio tracks.
    /// </summary>
    /// <param name="job">Build job with audio track definitions and CD-Text metadata.</param>
    /// <param name="progress">Progress reporter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Path to the generated CUE file.</returns>
    public static async Task<string> BuildAsync(
        BuildJob job,
        IProgress<BuildProgress> progress,
        CancellationToken ct)
    {
        ValidateJob(job);

        var binPath = Path.ChangeExtension(job.OutputImagePath, ".bin");
        var cuePath = Path.ChangeExtension(job.OutputImagePath, ".cue");

        // Ensure output directory exists
        var outputDir = Path.GetDirectoryName(binPath);
        if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
            Directory.CreateDirectory(outputDir);

        var trackInfos = new List<TrackLayoutInfo>();
        long currentSector = 0;

        progress.Report(new BuildProgress
        {
            PercentComplete = 2,
            StatusMessage = "Analyzing audio tracks...",
            LogLine = "[Info] Starting Red Book audio CD image build"
        });

        // Phase 1: Analyze tracks and calculate layout
        for (int i = 0; i < job.AudioTracks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var track = job.AudioTracks[i];

            // Calculate pre-gap
            int preGapFrames;
            if (i == 0)
            {
                // Track 1 must have a minimum 2-second pre-gap per Red Book
                preGapFrames = Math.Max(Track1PreGapFrames, track.PreGapSeconds * SectorsPerSecond + track.PreGapFrames);
            }
            else
            {
                preGapFrames = track.PreGapSeconds * SectorsPerSecond + track.PreGapFrames;
            }

            // Get audio duration from file
            long audioFrames = await Task.Run(() => GetAudioFrameCount(track.FilePath), ct);

            if (audioFrames <= 0)
            {
                throw new InvalidOperationException(
                    $"Audio track {i + 1} ('{Path.GetFileName(track.FilePath)}') has no audio data or is corrupt.");
            }

            var info = new TrackLayoutInfo
            {
                TrackNumber = i + 1,
                PreGapFrames = preGapFrames,
                PreGapStartSector = currentSector,
                Index01Sector = currentSector + preGapFrames,
                AudioFrames = audioFrames,
                Track = track
            };

            trackInfos.Add(info);
            currentSector = info.Index01Sector + audioFrames;

            // Update track duration
            track.Duration = TimeSpan.FromSeconds((double)audioFrames / SectorsPerSecond);

            progress.Report(new BuildProgress
            {
                PercentComplete = 2 + (i + 1) * 8 / job.AudioTracks.Count,
                StatusMessage = $"Analyzed track {i + 1}/{job.AudioTracks.Count}: {track.Duration:mm\\:ss}",
                LogLine = $"[Info] Track {i + 1}: {audioFrames} frames ({track.Duration:mm\\:ss\\.ff}), pre-gap {preGapFrames} frames"
            });
        }

        long totalSectors = currentSector;

        // Check total duration against media capacity
        ValidateDiscCapacity(totalSectors, job.DiscType);

        progress.Report(new BuildProgress
        {
            PercentComplete = 10,
            StatusMessage = $"Writing {totalSectors} sectors ({FormatMsf(totalSectors)})...",
            LogLine = $"[Info] Total disc: {totalSectors} sectors = {totalSectors * SectorSize:N0} bytes"
        });

        // Phase 2: Write BIN file with raw audio sectors
        await WriteBinFileAsync(binPath, trackInfos, totalSectors, progress, ct);

        // Phase 3: Generate CUE sheet
        bool hasCdText = HasAnyCdText(job);
        string? cdtPath = null;

        if (hasCdText)
        {
            cdtPath = Path.ChangeExtension(binPath, ".cdt");
            var cdtData = BuildCdTextData(job);
            await File.WriteAllBytesAsync(cdtPath, cdtData, ct);

            progress.Report(new BuildProgress
            {
                LogLine = $"[Info] CD-Text binary written: {Path.GetFileName(cdtPath)}"
            });
        }

        await WriteCueFileAsync(cuePath, binPath, cdtPath, job, trackInfos, ct);

        // Also generate a TOC file for cdrdao compatibility
        var tocPath = Path.ChangeExtension(binPath, ".toc");
        await WriteTocFileAsync(tocPath, binPath, cdtPath, job, trackInfos, ct);

        progress.Report(new BuildProgress
        {
            PercentComplete = 100,
            StatusMessage = "Audio CD image build complete.",
            LogLine = $"[Info] Output: {cuePath} + {binPath} ({totalSectors * SectorSize:N0} bytes)"
        });

        return cuePath;
    }

    /// <summary>Validates the build job for Red Book compliance.</summary>
    private static void ValidateJob(BuildJob job)
    {
        if (job.AudioTracks.Count == 0)
            throw new InvalidOperationException("No audio tracks specified for Audio CD build.");

        if (job.AudioTracks.Count > MaxTracks)
            throw new InvalidOperationException(
                $"Audio CD supports a maximum of {MaxTracks} tracks, but {job.AudioTracks.Count} were specified.");

        foreach (var track in job.AudioTracks)
        {
            if (string.IsNullOrWhiteSpace(track.FilePath))
                throw new ArgumentException("Audio track file path must not be empty.");
            if (!File.Exists(track.FilePath))
                throw new FileNotFoundException($"Audio file not found: {track.FilePath}");

            // Validate ISRC format if provided: 5 alphanumeric + 7 digits = 12 chars
            if (!string.IsNullOrEmpty(track.Isrc))
            {
                if (track.Isrc.Length != 12)
                    throw new ArgumentException(
                        $"ISRC code must be exactly 12 characters, got {track.Isrc.Length}: '{track.Isrc}'");
                // First 5 characters must be alphanumeric (country + registrant)
                for (int v = 0; v < 5; v++)
                {
                    if (!char.IsLetterOrDigit(track.Isrc[v]))
                        throw new ArgumentException(
                            $"ISRC characters 1-5 must be alphanumeric, found '{track.Isrc[v]}' at position {v + 1}");
                }
                // Last 7 characters must be digits (year + serial)
                for (int v = 5; v < 12; v++)
                {
                    if (!char.IsDigit(track.Isrc[v]))
                        throw new ArgumentException(
                            $"ISRC characters 6-12 must be digits, found '{track.Isrc[v]}' at position {v + 1}");
                }
            }
        }

        // Validate MCN if provided: 13 digits (UPC/EAN)
        if (!string.IsNullOrEmpty(job.MediaCatalogNumber))
        {
            if (job.MediaCatalogNumber.Length != 13)
                throw new ArgumentException(
                    $"Media Catalog Number (MCN/UPC) must be exactly 13 digits, got {job.MediaCatalogNumber.Length}");
            foreach (char c in job.MediaCatalogNumber)
            {
                if (!char.IsDigit(c))
                    throw new ArgumentException(
                        $"Media Catalog Number must contain only digits, found '{c}'");
            }
        }
    }

    /// <summary>Checks disc capacity against total audio data.</summary>
    private static void ValidateDiscCapacity(long totalSectors, string discType)
    {
        // Calculate maximum sectors for the disc type
        long maxSectors = discType?.ToUpperInvariant() switch
        {
            "CD" or "CD74" => 333_000,   // Standard 74-min Red Book CD
            "CD80" => 360_000,            // Extended 80-min CD
            "CD90" => 405_000,            // Non-standard 90-min overburn
            "CD99" => 445_500,            // Non-standard 99-min overburn
            "MINI CD" => 94_500,          // 8cm Mini CD (~21 min)
            _ => 360_000 // Default to 80-min CD
        };

        if (totalSectors > maxSectors)
        {
            var totalMsf = FormatMsf(totalSectors);
            var maxMsf = FormatMsf(maxSectors);
            throw new InvalidOperationException(
                $"Audio CD content ({totalMsf}, {totalSectors} sectors) exceeds " +
                $"{discType ?? "CD"} capacity ({maxMsf}, {maxSectors} sectors).");
        }
    }

    /// <summary>Gets the number of Red Book audio frames (sectors) from an audio file.</summary>
    private static long GetAudioFrameCount(string filePath)
    {
        WaveStream? reader = null;
        try
        {
            reader = AudioReaderFactory.CreateReader(filePath);

            // Calculate total samples, then convert to Red Book sectors
            long totalSamples = reader.Length / (reader.WaveFormat.BitsPerSample / 8) / reader.WaveFormat.Channels;

            // Account for sample rate conversion if needed
            if (reader.WaveFormat.SampleRate != RequiredSampleRate)
            {
                totalSamples = (long)((double)totalSamples * RequiredSampleRate / reader.WaveFormat.SampleRate);
            }

            // Convert samples to sectors (588 stereo samples per sector)
            long sectors = (totalSamples + SamplesPerSector - 1) / SamplesPerSector;
            return sectors;
        }
        finally
        {
            reader?.Dispose();
        }
    }

    /// <summary>
    /// Writes the BIN file containing raw 2,352-byte Red Book audio sectors.
    /// Each sector contains 588 interleaved stereo 16-bit PCM samples in little-endian byte order.
    /// Pre-gaps are written as digital silence (all zeros).
    /// </summary>
    private static async Task WriteBinFileAsync(
        string binPath,
        List<TrackLayoutInfo> tracks,
        long totalSectors,
        IProgress<BuildProgress> progress,
        CancellationToken ct)
    {
        var silentSector = new byte[SectorSize]; // All zeros = digital silence

        await using var output = new FileStream(binPath, FileMode.Create, FileAccess.Write,
            FileShare.None, bufferSize: 64 * 1024, useAsync: true);

        long sectorsWritten = 0;

        for (int t = 0; t < tracks.Count; t++)
        {
            ct.ThrowIfCancellationRequested();
            var trackInfo = tracks[t];

            // Write pre-gap (digital silence)
            for (int g = 0; g < trackInfo.PreGapFrames; g++)
            {
                await output.WriteAsync(silentSector, ct);
                sectorsWritten++;

                if (sectorsWritten % 1000 == 0)
                {
                    int pct = (int)(10 + sectorsWritten * 85 / totalSectors);
                    progress.Report(new BuildProgress
                    {
                        PercentComplete = pct,
                        BytesProcessed = sectorsWritten * SectorSize,
                        TotalBytes = totalSectors * SectorSize,
                        StatusMessage = $"Writing track {t + 1}/{tracks.Count} pre-gap..."
                    });
                }
            }

            // Write audio data
            sectorsWritten = await WriteTrackAudioAsync(output, trackInfo, sectorsWritten,
                totalSectors, t + 1, tracks.Count, progress, ct);
        }

        await output.FlushAsync(ct);
    }

    /// <summary>
    /// Writes audio data for a single track, converting to Red Book format as needed.
    /// Source audio is read, converted to 44100Hz/16-bit/stereo PCM, and written
    /// as 2352-byte sectors.
    /// </summary>
    private static async Task<long> WriteTrackAudioAsync(
        FileStream output,
        TrackLayoutInfo trackInfo,
        long sectorsWritten,
        long totalSectors,
        int trackNum,
        int totalTracks,
        IProgress<BuildProgress> progress,
        CancellationToken ct)
    {
        WaveStream? reader = null;
        try
        {
            reader = AudioReaderFactory.CreateReader(trackInfo.Track.FilePath);

            // Build conversion pipeline to 44100Hz/16-bit/stereo
            var sampleProvider = reader.ToSampleProvider();

            // Channel conversion
            if (reader.WaveFormat.Channels == 1)
                sampleProvider = new NAudio.Wave.SampleProviders.MonoToStereoSampleProvider(sampleProvider);
            else if (reader.WaveFormat.Channels > 2)
                sampleProvider = new StereoDownmixSampleProvider(sampleProvider);

            // Convert to 16-bit PCM
            var pcmProvider = new NAudio.Wave.SampleProviders.SampleToWaveProvider16(sampleProvider);

            // Apply resampling if needed
            IWaveProvider finalProvider;
            if (reader.WaveFormat.SampleRate != RequiredSampleRate)
            {
                var targetFormat = new WaveFormat(RequiredSampleRate, RequiredBitsPerSample, RequiredChannels);
                finalProvider = new ResamplingWaveProvider(pcmProvider, targetFormat);
            }
            else
            {
                finalProvider = pcmProvider;
            }

            // Read audio and write sectors
            var sectorBuffer = new byte[SectorSize];
            long audioFramesWritten = 0;

            await Task.Run(() =>
            {
                while (audioFramesWritten < trackInfo.AudioFrames)
                {
                    ct.ThrowIfCancellationRequested();

                    // Read exactly one sector worth of audio
                    int totalRead = 0;
                    while (totalRead < SectorSize)
                    {
                        int read = finalProvider.Read(sectorBuffer, totalRead, SectorSize - totalRead);
                        if (read == 0)
                        {
                            // End of audio - pad remainder with silence
                            Array.Clear(sectorBuffer, totalRead, SectorSize - totalRead);
                            break;
                        }
                        totalRead += read;
                    }

                    output.Write(sectorBuffer, 0, SectorSize);
                    sectorsWritten++;
                    audioFramesWritten++;

                    if (sectorsWritten % 2000 == 0)
                    {
                        int pct = (int)(10 + sectorsWritten * 85 / totalSectors);
                        progress.Report(new BuildProgress
                        {
                            PercentComplete = Math.Min(pct, 95),
                            BytesProcessed = sectorsWritten * SectorSize,
                            TotalBytes = totalSectors * SectorSize,
                            CurrentFile = Path.GetFileName(trackInfo.Track.FilePath),
                            StatusMessage = $"Writing track {trackNum}/{totalTracks}..."
                        });
                    }
                }
            }, ct);

            return sectorsWritten;
        }
        finally
        {
            reader?.Dispose();
        }
    }

    /// <summary>
    /// Writes the CUE sheet file describing the track layout of the audio CD image.
    /// Includes CD-Text, ISRC, MCN, and track flags per CUE sheet specification.
    /// </summary>
    private static async Task WriteCueFileAsync(
        string cuePath,
        string binPath,
        string? cdtPath,
        BuildJob job,
        List<TrackLayoutInfo> tracks,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("REM Generated by OpenBurningSuite");
        sb.AppendLine($"REM Date: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss UTC}");

        // CD-Text file reference
        if (cdtPath != null)
            sb.AppendLine($"CDTEXTFILE \"{Path.GetFileName(cdtPath)}\"");

        // MCN (Media Catalog Number / UPC-EAN)
        if (!string.IsNullOrEmpty(job.MediaCatalogNumber))
            sb.AppendLine($"CATALOG {job.MediaCatalogNumber}");

        // Disc-level CD-Text
        if (!string.IsNullOrEmpty(job.CdTextAlbum))
            sb.AppendLine($"TITLE \"{EscapeCueString(job.CdTextAlbum)}\"");
        if (!string.IsNullOrEmpty(job.CdTextArtist))
            sb.AppendLine($"PERFORMER \"{EscapeCueString(job.CdTextArtist)}\"");
        if (!string.IsNullOrEmpty(job.CdTextSongwriter))
            sb.AppendLine($"SONGWRITER \"{EscapeCueString(job.CdTextSongwriter)}\"");

        // Single BIN file for the entire disc
        sb.AppendLine($"FILE \"{Path.GetFileName(binPath)}\" BINARY");

        foreach (var trackInfo in tracks)
        {
            var track = trackInfo.Track;
            sb.AppendLine($"  TRACK {trackInfo.TrackNumber:D2} AUDIO");

            // Track flags
            if (track.FourChannel)
                sb.AppendLine("    FLAGS 4CH");
            else if (track.PreEmphasis && track.CopyPermitted)
                sb.AppendLine("    FLAGS DCP PRE");
            else if (track.PreEmphasis)
                sb.AppendLine("    FLAGS PRE");
            else if (track.CopyPermitted)
                sb.AppendLine("    FLAGS DCP");

            // ISRC code
            if (!string.IsNullOrEmpty(track.Isrc))
                sb.AppendLine($"    ISRC {track.Isrc}");

            // Per-track CD-Text
            if (!string.IsNullOrEmpty(track.Title))
                sb.AppendLine($"    TITLE \"{EscapeCueString(track.Title)}\"");
            if (!string.IsNullOrEmpty(track.Artist))
                sb.AppendLine($"    PERFORMER \"{EscapeCueString(track.Artist)}\"");
            if (!string.IsNullOrEmpty(track.Songwriter))
                sb.AppendLine($"    SONGWRITER \"{EscapeCueString(track.Songwriter)}\"");

            // Pre-gap (INDEX 00) and audio start (INDEX 01)
            if (trackInfo.PreGapFrames > 0)
            {
                if (trackInfo.TrackNumber == 1)
                {
                    // Track 1 pre-gap is typically a PREGAP directive (not in the BIN file)
                    // But since we write it into the BIN, we use INDEX 00
                    sb.AppendLine($"    INDEX 00 {FormatMsf(trackInfo.PreGapStartSector)}");
                }
                else
                {
                    sb.AppendLine($"    INDEX 00 {FormatMsf(trackInfo.PreGapStartSector)}");
                }
            }

            sb.AppendLine($"    INDEX 01 {FormatMsf(trackInfo.Index01Sector)}");
        }

        await File.WriteAllTextAsync(cuePath, sb.ToString(), ct);
    }

    /// <summary>
    /// Writes a cdrdao-compatible TOC file for the audio CD image.
    /// </summary>
    private static async Task WriteTocFileAsync(
        string tocPath,
        string binPath,
        string? cdtPath,
        BuildJob job,
        List<TrackLayoutInfo> tracks,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("CD_DA");
        sb.AppendLine();

        // MCN
        if (!string.IsNullOrEmpty(job.MediaCatalogNumber))
            sb.AppendLine($"CATALOG \"{job.MediaCatalogNumber}\"");

        // Disc-level CD-Text
        bool hasCdText = HasAnyCdText(job);
        if (hasCdText)
        {
            sb.AppendLine("CD_TEXT {");
            sb.AppendLine("  LANGUAGE_MAP { 0: EN }");
            sb.AppendLine("  LANGUAGE 0 {");
            if (!string.IsNullOrEmpty(job.CdTextAlbum))
                sb.AppendLine($"    TITLE \"{EscapeTocString(job.CdTextAlbum)}\"");
            if (!string.IsNullOrEmpty(job.CdTextArtist))
                sb.AppendLine($"    PERFORMER \"{EscapeTocString(job.CdTextArtist)}\"");
            if (!string.IsNullOrEmpty(job.CdTextSongwriter))
                sb.AppendLine($"    SONGWRITER \"{EscapeTocString(job.CdTextSongwriter)}\"");
            if (!string.IsNullOrEmpty(job.CdTextComposer))
                sb.AppendLine($"    COMPOSER \"{EscapeTocString(job.CdTextComposer)}\"");
            if (!string.IsNullOrEmpty(job.CdTextArranger))
                sb.AppendLine($"    ARRANGER \"{EscapeTocString(job.CdTextArranger)}\"");
            if (!string.IsNullOrEmpty(job.CdTextMessage))
                sb.AppendLine($"    MESSAGE \"{EscapeTocString(job.CdTextMessage)}\"");
            sb.AppendLine("  }");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        foreach (var trackInfo in tracks)
        {
            var track = trackInfo.Track;
            sb.AppendLine("TRACK AUDIO");

            // Track flags
            if (track.CopyPermitted) sb.AppendLine("  COPY");
            if (track.FourChannel) sb.AppendLine("  FOUR_CHANNEL_AUDIO");
            if (track.PreEmphasis) sb.AppendLine("  PRE_EMPHASIS");

            // ISRC
            if (!string.IsNullOrEmpty(track.Isrc))
                sb.AppendLine($"  ISRC \"{track.Isrc}\"");

            // Per-track CD-Text
            if (!string.IsNullOrEmpty(track.Title) || !string.IsNullOrEmpty(track.Artist))
            {
                sb.AppendLine("  CD_TEXT {");
                sb.AppendLine("    LANGUAGE 0 {");
                if (!string.IsNullOrEmpty(track.Title))
                    sb.AppendLine($"      TITLE \"{EscapeTocString(track.Title)}\"");
                if (!string.IsNullOrEmpty(track.Artist))
                    sb.AppendLine($"      PERFORMER \"{EscapeTocString(track.Artist)}\"");
                if (!string.IsNullOrEmpty(track.Songwriter))
                    sb.AppendLine($"      SONGWRITER \"{EscapeTocString(track.Songwriter)}\"");
                sb.AppendLine("    }");
                sb.AppendLine("  }");
            }

            // Pre-gap
            if (trackInfo.PreGapFrames > 0)
                sb.AppendLine($"  PREGAP {FormatMsf(trackInfo.PreGapFrames)}");

            // Audio data reference
            long byteOffset = trackInfo.Index01Sector * SectorSize;
            long byteLength = trackInfo.AudioFrames * SectorSize;
            sb.AppendLine($"  FILE \"{Path.GetFileName(binPath)}\" #{byteOffset} {FormatMsf(trackInfo.AudioFrames)}");
            sb.AppendLine();
        }

        await File.WriteAllTextAsync(tocPath, sb.ToString(), ct);
    }

    /// <summary>Builds the CD-Text binary data for the disc.</summary>
    private static byte[] BuildCdTextData(BuildJob job)
    {
        var trackTitles = new string[job.AudioTracks.Count];
        var trackPerformers = new string[job.AudioTracks.Count];
        var isrcCodes = new string[job.AudioTracks.Count];

        for (int i = 0; i < job.AudioTracks.Count; i++)
        {
            trackTitles[i] = job.AudioTracks[i].Title ?? string.Empty;
            trackPerformers[i] = job.AudioTracks[i].Artist ?? string.Empty;
            isrcCodes[i] = job.AudioTracks[i].Isrc ?? string.Empty;
        }

        return CdTextEncoder.EncodeToCdtFile(
            albumTitle: job.CdTextAlbum ?? string.Empty,
            albumPerformer: job.CdTextArtist ?? string.Empty,
            trackTitles: trackTitles,
            trackPerformers: trackPerformers,
            songwriter: job.CdTextSongwriter,
            composer: job.CdTextComposer,
            arranger: job.CdTextArranger,
            message: job.CdTextMessage,
            genre: job.CdTextGenre,
            upcEan: job.MediaCatalogNumber,
            isrcCodes: isrcCodes,
            firstTrackNumber: 1,
            lastTrackNumber: job.AudioTracks.Count);
    }

    /// <summary>Checks if the build job has any CD-Text metadata.</summary>
    private static bool HasAnyCdText(BuildJob job)
    {
        if (!string.IsNullOrEmpty(job.CdTextAlbum) || !string.IsNullOrEmpty(job.CdTextArtist) ||
            !string.IsNullOrEmpty(job.CdTextSongwriter) || !string.IsNullOrEmpty(job.CdTextComposer) ||
            !string.IsNullOrEmpty(job.CdTextArranger) || !string.IsNullOrEmpty(job.CdTextMessage) ||
            !string.IsNullOrEmpty(job.CdTextGenre))
            return true;

        foreach (var track in job.AudioTracks)
        {
            if (!string.IsNullOrEmpty(track.Title) || !string.IsNullOrEmpty(track.Artist) ||
                !string.IsNullOrEmpty(track.Songwriter))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Formats a sector count as MSF (Minutes:Seconds:Frames) string.
    /// 75 frames per second per Red Book specification.
    /// </summary>
    public static string FormatMsf(long sectors)
    {
        long totalFrames = sectors;
        long minutes = totalFrames / (SectorsPerSecond * 60);
        long seconds = (totalFrames / SectorsPerSecond) % 60;
        long frames = totalFrames % SectorsPerSecond;
        return $"{minutes:D2}:{seconds:D2}:{frames:D2}";
    }

    /// <summary>Parses an MSF string to sector count.</summary>
    public static long ParseMsf(string msf)
    {
        var parts = msf.Split(':');
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], out int m) ||
            !int.TryParse(parts[1], out int s) ||
            !int.TryParse(parts[2], out int f))
            return 0;

        return m * 60 * SectorsPerSecond + s * SectorsPerSecond + f;
    }

    /// <summary>Escapes a string for use in CUE sheet quoted values.</summary>
    private static string EscapeCueString(string value)
        => value.Replace("\"", "'"); // CUE spec doesn't support escaped quotes

    /// <summary>Escapes a string for use in TOC file quoted values.</summary>
    private static string EscapeTocString(string value)
        => value.Replace("\"", "\\\"");

    /// <summary>Track layout information calculated during the analysis phase.</summary>
    private sealed class TrackLayoutInfo
    {
        public int TrackNumber { get; set; }
        public int PreGapFrames { get; set; }
        public long PreGapStartSector { get; set; }
        public long Index01Sector { get; set; }
        public long AudioFrames { get; set; }
        public AudioTrack Track { get; set; } = null!;
    }
}
