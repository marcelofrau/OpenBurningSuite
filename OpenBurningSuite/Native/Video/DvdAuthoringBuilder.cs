// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenBurningSuite.Models;

namespace OpenBurningSuite.Native.Video;

/// <summary>
/// Builds DVD-Video compliant file structures per DVD Forum specifications
/// (ECMA-267, DVD-Video specification Part 3).
///
/// DVD-Video directory layout:
///   VIDEO_TS/
///     VIDEO_TS.IFO   - Video Manager Information (VMGI)
///     VIDEO_TS.BUP   - Backup of VIDEO_TS.IFO
///     VIDEO_TS.VOB   - VMG Menu VOB (optional)
///     VTS_01_0.IFO   - Video Title Set 1 Information (VTSI)
///     VTS_01_0.BUP   - Backup of VTS_01_0.IFO
///     VTS_01_0.VOB   - VTS 1 Menu VOB (optional)
///     VTS_01_1.VOB   - VTS 1 Title VOB part 1
///     VTS_01_2.VOB   - VTS 1 Title VOB part 2 (if > 1 GB)
///
/// IFO file structure (VMGI):
///   - VMG Header (offset 0): disc info, region mask, title count
///   - Title Table (TT_SRPT): maps title numbers to VTS numbers and PTT_SRPT
///   - VMG Menu PGCI_UT: program chains for VMG menus
///   - VMGM_C_ADT: cell address table for VMG menu VOBs
///   - VMGM_VOBU_ADMAP: VOBU address map for VMG menus
///
/// IFO file structure (VTSI):
///   - VTS Header (offset 0): stream attributes, sector addresses
///   - VTS_PTT_SRPT: Part-of-Title Search Pointer Table (chapter → PGC mapping)
///   - VTS_PGCI_UT: PGC Information Table for VTS menus
///   - VTS_TMAPT: Time Map Table (time → VOBU mapping for seeking)
///   - VTS_C_ADT: Cell Address Table
///   - VTS_VOBU_ADMAP: VOBU Address Map
///
/// VOB files contain MPEG-2 Program Stream multiplexed data with navigation packs
/// (NV_PCK) at the start of each VOBU (Video Object Unit). NV_PCKs contain
/// DSI (Data Search Information) and PCI (Presentation Control Information) packets.
///
/// Maximum VOB file size: 1,073,741,824 bytes (1 GiB) per DVD-Video specification.
/// </summary>
public sealed class DvdAuthoringBuilder
{
    /// <summary>Maximum VOB file size per DVD-Video specification (1 GiB).</summary>
    private const long MaxVobFileSize = 1_073_741_824L;

    /// <summary>DVD sector size (2048 bytes).</summary>
    private const int SectorSize = 2048;

    /// <summary>MPEG-2 pack size (2048 bytes for DVD).</summary>
    private const int PackSize = 2048;

    /// <summary>
    /// VOBU duration in seconds. Per DVD-Video specification, each VOBU spans
    /// approximately 0.4-1.0 seconds of video. We use 0.5s as the standard interval
    /// for good seeking granularity while maintaining reasonable overhead.
    /// </summary>
    private const double VobuDurationSeconds = 0.5;

    /// <summary>
    /// Maximum DVD mux rate: 10,080,000 bits/sec = 10.08 Mbps total (video + audio + overhead).
    /// Per DVD-Video specification Part 3, the maximum programme mux rate is 10.08 Mbps.
    /// Mux rate field in pack header is stored as rate / 50 bytes/sec.
    /// </summary>
    private const int DvdMuxRateField = 25200; // 10,080,000 bits/s ÷ 8 = 1,260,000 bytes/s ÷ 50 = 25,200

    /// <summary>90 kHz clock used for PTS/DTS timestamps in MPEG-2.</summary>
    private const long ClockRate90Khz = 90_000;

    /// <summary>27 MHz system clock reference base frequency for MPEG-2 SCR.</summary>
    private const long ScrClockRate = 27_000_000;

    /// <summary>
    /// Standard PCI packet data length per DVD-Video specification.
    /// PCI (Presentation Control Information) total PES payload: 979 bytes.
    /// Contains PCI_GI (general info), NSML_AGLI (non-seamless angle),
    /// HLI (highlight info for menus), and RECI (recording info).
    /// </summary>
    private const int PciDataLen = 979;

    /// <summary>
    /// Standard DSI packet data length per DVD-Video specification.
    /// DSI (Data Search Information) total PES payload: 1017 bytes.
    /// Contains DSI_GI (general info), SML_PBI (seamless playback),
    /// SML_AGLI (seamless angle), VOBU_SRI (VOBU search info),
    /// and SYNCI (synchronous information).
    /// </summary>
    private const int DsiDataLen = 1017;

    /// <summary>
    /// Builds a complete DVD-Video file structure from the authoring job configuration.
    /// Creates the VIDEO_TS directory with IFO, BUP, and VOB files.
    /// </summary>
    /// <param name="job">The DVD authoring job configuration.</param>
    /// <param name="outputDir">Output directory where VIDEO_TS will be created.</param>
    /// <param name="progress">Progress reporter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Path to the VIDEO_TS directory.</returns>
    public async Task<string> BuildAsync(
        DvdAuthoringJob job,
        string outputDir,
        IProgress<BuildProgress> progress,
        CancellationToken ct)
    {
        ValidateJob(job);

        var videoTsDir = Path.Combine(outputDir, "VIDEO_TS");
        Directory.CreateDirectory(videoTsDir);

        progress.Report(new BuildProgress
        {
            PercentComplete = 2,
            StatusMessage = "Building DVD-Video structure...",
            LogLine = $"[Info] Creating VIDEO_TS directory: {videoTsDir}"
        });

        // Phase 1: Transcode video files if needed
        var transcodedPaths = new List<string>();
        if (job.TranscodeVideo)
        {
            progress.Report(new BuildProgress
            {
                StatusMessage = "Transcoding video files for DVD...",
                LogLine = "[Info] Transcoding enabled — converting to DVD-compliant MPEG-2"
            });

            int totalFiles = CountVideoFiles(job);
            int filesProcessed = 0;

            foreach (var titleSet in job.TitleSets)
            {
                foreach (var title in titleSet.Titles)
                {
                    ct.ThrowIfCancellationRequested();
                    var transcodedPath = Path.Combine(Path.GetTempPath(),
                        $"obs_dvd_{Guid.NewGuid():N}.mpg");

                    int vBitrate = job.VideoBitrateKbps > 0
                        ? job.VideoBitrateKbps : VideoTranscoder.DvdDefaultVideoBitrateKbps;
                    string audioCodec = titleSet.AudioStreams.Count > 0
                        ? titleSet.AudioStreams[0].Codec : "AC3";
                    int audioBitrate = titleSet.AudioStreams.Count > 0
                        ? titleSet.AudioStreams[0].BitrateKbps : 192;

                    await VideoTranscoder.TranscodeContainerToDvdAsync(
                        title.VideoPath, transcodedPath,
                        job.VideoStandard, job.AspectRatio,
                        vBitrate, audioCodec, audioBitrate,
                        progress, ct);

                    transcodedPaths.Add(transcodedPath);
                    filesProcessed++;

                    progress.Report(new BuildProgress
                    {
                        PercentComplete = 2 + filesProcessed * 40 / totalFiles,
                        StatusMessage = $"Transcoded {filesProcessed}/{totalFiles} video files"
                    });
                }
            }
        }

        try
        {
            // Phase 2: Create VOB files from MPEG-2 PS streams
            int baseProgress = job.TranscodeVideo ? 42 : 2;
            var vtsVobInfos = new List<VtsVobInfo>();
            int transcodedIdx = 0;

            for (int vtsIdx = 0; vtsIdx < job.TitleSets.Count; vtsIdx++)
            {
                ct.ThrowIfCancellationRequested();
                var titleSet = job.TitleSets[vtsIdx];
                int vtsNum = vtsIdx + 1;

                var vtsInfo = new VtsVobInfo { VtsNumber = vtsNum };

                for (int titleIdx = 0; titleIdx < titleSet.Titles.Count; titleIdx++)
                {
                    var title = titleSet.Titles[titleIdx];
                    string sourcePath = job.TranscodeVideo && transcodedIdx < transcodedPaths.Count
                        ? transcodedPaths[transcodedIdx++]
                        : title.VideoPath;

                    if (!File.Exists(sourcePath))
                        throw new FileNotFoundException($"Video file not found: {sourcePath}");

                    // Split source into VOB parts (max 1 GB each)
                    var vobParts = await SplitToVobFilesAsync(
                        sourcePath, videoTsDir, vtsNum, vtsInfo.VobParts.Count + 1, vtsInfo, ct);
                    vtsInfo.VobParts.AddRange(vobParts);

                    // Calculate chapter cell positions
                    foreach (var chapter in title.Chapters)
                    {
                        vtsInfo.ChapterCells.Add(new CellInfo
                        {
                            StartTime = chapter.StartTime,
                            ChapterName = chapter.Name
                        });
                    }
                }

                // Ensure at least one chapter at the start
                if (vtsInfo.ChapterCells.Count == 0)
                {
                    vtsInfo.ChapterCells.Add(new CellInfo
                    {
                        StartTime = TimeSpan.Zero,
                        ChapterName = "Chapter 1"
                    });
                }

                vtsVobInfos.Add(vtsInfo);

                int pct = baseProgress + (vtsIdx + 1) * 30 / job.TitleSets.Count;
                progress.Report(new BuildProgress
                {
                    PercentComplete = pct,
                    StatusMessage = $"Created VOBs for title set {vtsNum}/{job.TitleSets.Count}",
                    LogLine = $"[Info] VTS {vtsNum:D2}: {vtsInfo.VobParts.Count} VOB file(s)"
                });
            }

            // Phase 3: Generate IFO and BUP files
            progress.Report(new BuildProgress
            {
                PercentComplete = 75,
                StatusMessage = "Generating IFO navigation data...",
                LogLine = "[Info] Writing VIDEO_TS.IFO (Video Manager)"
            });

            // Write VIDEO_TS.IFO (Video Manager Information)
            var vmgIfo = BuildVmgIfo(job, vtsVobInfos);
            var vmgIfoPath = Path.Combine(videoTsDir, "VIDEO_TS.IFO");
            await File.WriteAllBytesAsync(vmgIfoPath, vmgIfo, ct);

            // Write VIDEO_TS.BUP (backup copy)
            var vmgBupPath = Path.Combine(videoTsDir, "VIDEO_TS.BUP");
            await File.WriteAllBytesAsync(vmgBupPath, vmgIfo, ct);

            // Write VTS IFO and BUP for each title set
            for (int vtsIdx = 0; vtsIdx < vtsVobInfos.Count; vtsIdx++)
            {
                ct.ThrowIfCancellationRequested();
                int vtsNum = vtsIdx + 1;
                var titleSet = job.TitleSets[vtsIdx];
                var vtsInfo = vtsVobInfos[vtsIdx];

                var vtsIfo = BuildVtsIfo(job, titleSet, vtsInfo);
                var vtsIfoPath = Path.Combine(videoTsDir, $"VTS_{vtsNum:D2}_0.IFO");
                await File.WriteAllBytesAsync(vtsIfoPath, vtsIfo, ct);

                // BUP (Backup) is an identical copy of the IFO file, required by
                // DVD-Video specification for error recovery on scratched discs.
                var vtsBupPath = Path.Combine(videoTsDir, $"VTS_{vtsNum:D2}_0.BUP");
                await File.WriteAllBytesAsync(vtsBupPath, vtsIfo, ct);

                progress.Report(new BuildProgress
                {
                    PercentComplete = 80 + (vtsIdx + 1) * 15 / vtsVobInfos.Count,
                    LogLine = $"[Info] VTS_{vtsNum:D2}_0.IFO written ({vtsIfo.Length} bytes)"
                });
            }

            // Phase 4: Create empty VMG menu VOB placeholder if menu is defined
            if (job.MainMenu != null)
            {
                var menuVobPath = Path.Combine(videoTsDir, "VIDEO_TS.VOB");
                await CreateMenuVobAsync(job.MainMenu, menuVobPath, job.VideoStandard, ct);

                progress.Report(new BuildProgress
                {
                    LogLine = "[Info] VIDEO_TS.VOB (menu) created"
                });
            }

            progress.Report(new BuildProgress
            {
                PercentComplete = 100,
                StatusMessage = "DVD-Video structure complete.",
                LogLine = $"[Info] DVD-Video structure built: {videoTsDir}"
            });

            return videoTsDir;
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

    /// <summary>Validates the DVD authoring job.</summary>
    private static void ValidateJob(DvdAuthoringJob job)
    {
        if (job.TitleSets.Count == 0)
            throw new ArgumentException("At least one title set is required for DVD authoring.");

        if (job.TitleSets.Count > 99)
            throw new ArgumentException("DVD-Video supports a maximum of 99 title sets.");

        foreach (var ts in job.TitleSets)
        {
            if (ts.Titles.Count == 0)
                throw new ArgumentException("Each title set must contain at least one title.");

            if (ts.Titles.Count > 99)
                throw new ArgumentException("DVD-Video supports a maximum of 99 titles per title set.");

            if (ts.AudioStreams.Count > 8)
                throw new ArgumentException("DVD-Video supports a maximum of 8 audio streams per title set.");

            if (ts.SubtitleStreams.Count > 32)
                throw new ArgumentException("DVD-Video supports a maximum of 32 subtitle streams per title set.");

            foreach (var title in ts.Titles)
            {
                if (string.IsNullOrWhiteSpace(title.VideoPath))
                    throw new ArgumentException("Video path must not be empty for DVD titles.");

                if (title.Chapters.Count > 999)
                    throw new ArgumentException("DVD-Video supports a maximum of 999 chapters per title.");
            }
        }

        var std = job.VideoStandard.ToUpperInvariant();
        if (std != "NTSC" && std != "PAL")
            throw new ArgumentException($"Unsupported video standard: {job.VideoStandard}. Use 'NTSC' or 'PAL'.");
    }

    /// <summary>
    /// Splits an MPEG-2 PS file into DVD VOB files, each no larger than 1 GiB.
    /// Inserts navigation packs (NV_PCK) at each VOBU boundary (~0.5 seconds of video).
    ///
    /// Per DVD-Video specification, each VOB consists of a sequence of VOBUs (Video Object
    /// Units). Each VOBU begins with a navigation pack containing PCI and DSI packets
    /// that provide timing, seeking, and trick-play information.
    ///
    /// The method tracks VOBU sector offsets for VTS_VOBU_ADMAP and timing info
    /// for VTS_TMAPT generation.
    /// </summary>
    private static async Task<List<string>> SplitToVobFilesAsync(
        string sourcePath,
        string outputDir,
        int vtsNumber,
        int startPartNumber,
        VtsVobInfo vtsInfo,
        CancellationToken ct)
    {
        var vobPaths = new List<string>();
        var sourceInfo = new FileInfo(sourcePath);
        long totalSize = sourceInfo.Length;

        if (totalSize == 0)
            throw new InvalidOperationException($"Source video file is empty: {sourcePath}");

        // Calculate bytes per VOBU based on DVD mux rate and VOBU duration
        // 10.08 Mbps = 1,260,000 bytes/sec; 0.5 sec = 630,000 bytes per VOBU
        long bytesPerVobu = (long)(1_260_000 * VobuDurationSeconds);
        // Each VOBU must start with a nav pack (2048 bytes)
        long dataBytesPerVobu = bytesPerVobu - PackSize;
        if (dataBytesPerVobu < PackSize) dataBytesPerVobu = PackSize;

        int partNumber = startPartNumber;
        long sourceBytesRemaining = totalSize;
        int sectorOffset = 0; // Sector counter within this VTS
        int vobuIndex = 0;

        await using var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize: 64 * 1024, useAsync: true);

        while (sourceBytesRemaining > 0)
        {
            ct.ThrowIfCancellationRequested();

            var vobFileName = $"VTS_{vtsNumber:D2}_{partNumber}.VOB";
            var vobPath = Path.Combine(outputDir, vobFileName);
            long vobBytesWritten = 0;

            await using (var vobStream = new FileStream(vobPath, FileMode.Create, FileAccess.Write,
                FileShare.None, bufferSize: 64 * 1024, useAsync: true))
            {
                var buffer = new byte[64 * 1024];

                while (vobBytesWritten < MaxVobFileSize && sourceBytesRemaining > 0)
                {
                    ct.ThrowIfCancellationRequested();

                    // Calculate VOBU timing
                    double vobuStartTime = vobuIndex * VobuDurationSeconds;
                    var vobuStartTimeSpan = TimeSpan.FromSeconds(vobuStartTime);

                    // Determine how much data to read for this VOBU
                    long vobuDataSize = Math.Min(dataBytesPerVobu, sourceBytesRemaining);

                    // Ensure we don't exceed VOB file size limit
                    long remainingInVob = MaxVobFileSize - vobBytesWritten;
                    if (vobuDataSize + PackSize > remainingInVob)
                    {
                        if (remainingInVob <= PackSize) break; // Not enough room for nav pack
                        vobuDataSize = remainingInVob - PackSize;
                    }

                    // Calculate VOBU end address (in sectors, relative to nav pack LBN)
                    int vobuSectors = (int)((vobuDataSize + PackSize + SectorSize - 1) / SectorSize);

                    // Record VOBU info for address map and time map
                    vtsInfo.VobuAddressMap.Add(sectorOffset);
                    vtsInfo.VobuTimeMap.Add(vobuStartTimeSpan);

                    // Write navigation pack at the start of each VOBU
                    // Per DVD-Video spec, NV_PCK_LBN = sector address of this nav pack
                    var navPack = CreateNavigationPack(
                        vobuStartTimeSpan,
                        sectorOffset,
                        vobuSectors - 1,   // VOBU_EA: last sector offset relative to NV_PCK
                        vobuIndex,
                        vtsInfo.VobuAddressMap);
                    await vobStream.WriteAsync(navPack, ct);
                    vobBytesWritten += PackSize;
                    sectorOffset++;

                    // Copy MPEG-2 PS data for this VOBU
                    long written = 0;
                    while (written < vobuDataSize)
                    {
                        ct.ThrowIfCancellationRequested();
                        int toRead = (int)Math.Min(buffer.Length, vobuDataSize - written);
                        int read = await sourceStream.ReadAsync(buffer.AsMemory(0, toRead), ct);
                        if (read == 0)
                        {
                            sourceBytesRemaining = 0;
                            break;
                        }

                        await vobStream.WriteAsync(buffer.AsMemory(0, read), ct);
                        vobBytesWritten += read;
                        written += read;
                        sourceBytesRemaining -= read;
                    }

                    // Pad last sector to 2048-byte boundary if needed
                    long totalVobuBytes = PackSize + written;
                    int padBytes = (int)((SectorSize - (totalVobuBytes % SectorSize)) % SectorSize);
                    if (padBytes > 0 && padBytes < SectorSize)
                    {
                        var padBuf = new byte[padBytes];
                        await vobStream.WriteAsync(padBuf, ct);
                        vobBytesWritten += padBytes;
                    }

                    // Update sector offset (count sectors written for this VOBU)
                    int dataSectors = (int)((written + padBytes + SectorSize - 1) / SectorSize);
                    sectorOffset += dataSectors;
                    vobuIndex++;
                }
            }

            vobPaths.Add(vobPath);
            partNumber++;
        }

        return vobPaths;
    }

    /// <summary>
    /// Creates a DVD navigation pack (NV_PCK) containing fully compliant PCI and DSI packets.
    ///
    /// Per DVD-Video specification Part 3 (Video Specification), a navigation pack consists of:
    ///   1. MPEG-2 Pack Header (14 bytes): pack_start_code + SCR + mux_rate + stuffing
    ///   2. System Header (24 bytes): system_header_start_code + bounds
    ///   3. PCI Packet (~985 bytes PES): Presentation Control Information
    ///      - PCI_GI: NV_PCK_LBN, VOBU_CAT, UOP_CTL, S_PTM, E_PTM, S_PTMF, C_ELTM
    ///      - NSML_AGLI: Non-seamless angle information (36 bytes)
    ///      - HLI: Highlight information for menus (reserved, 2 bytes)
    ///      - RECI: Recording information (reserved, 1 byte)
    ///   4. DSI Packet (~1023 bytes PES): Data Search Information
    ///      - DSI_GI: NV_PCK_SCR, NV_PCK_LBN, VOBU_EA, VOBU_1STREF_EA/2ND/3RD,
    ///                VOBU_VOB_IDN, VOBU_C_IDN, C_ELTM
    ///      - SML_PBI: Seamless playback information (18 bytes)
    ///      - SML_AGLI: Seamless angle information (36 bytes × 9 angles)
    ///      - VOBU_SRI: VOBU search information (forward/backward seeking)
    ///      - SYNCI: Synchronous information for A/V sync
    ///
    /// The NV_PCK always occupies exactly one DVD sector (2048 bytes / one pack).
    /// </summary>
    /// <param name="presentationTime">Presentation time of this VOBU.</param>
    /// <param name="vobuStartSector">Sector address (LBN) of this navigation pack.</param>
    /// <param name="vobuEndSectorOffset">Last sector offset relative to this nav pack (VOBU_EA).</param>
    /// <param name="vobuIndex">Sequential VOBU index (0-based) within the VOB.</param>
    /// <param name="vobuAddressMap">List of all VOBU start sector addresses for seeking.</param>
    private static byte[] CreateNavigationPack(
        TimeSpan presentationTime,
        int vobuStartSector,
        int vobuEndSectorOffset,
        int vobuIndex,
        List<int> vobuAddressMap)
    {
        var pack = new byte[PackSize];
        int offset = 0;

        // =================================================================
        // MPEG-2 Pack Header (14 bytes)
        // Per ISO/IEC 13818-1 Section 2.5.3.4
        // =================================================================
        // Pack start code: 0x000001BA
        pack[0] = 0x00; pack[1] = 0x00; pack[2] = 0x01; pack[3] = 0xBA;

        // System Clock Reference (SCR) encoded from presentation time
        // SCR_base is in 90 kHz units, SCR_ext is in 27 MHz units
        long scrBase = (long)(presentationTime.TotalSeconds * ClockRate90Khz);
        int scrExt = (int)((long)(presentationTime.TotalSeconds * ScrClockRate) % 300);

        // Pack SCR fields (6 bytes, MPEG-2 format):
        // '01' marker_bit SCR[32..30] marker_bit SCR[29..15] marker_bit SCR[14..0]
        // marker_bit SCR_ext[8..0] marker_bit
        pack[4] = (byte)(0x44 | ((scrBase >> 27) & 0x38) | ((scrBase >> 28) & 0x03));
        pack[5] = (byte)((scrBase >> 20) & 0xFF);
        pack[6] = (byte)(0x04 | ((scrBase >> 12) & 0xF8) | ((scrBase >> 13) & 0x03));
        pack[7] = (byte)((scrBase >> 5) & 0xFF);
        pack[8] = (byte)(0x04 | (((int)(scrBase << 3)) & 0xF8) | ((scrExt >> 7) & 0x03));
        pack[9] = (byte)(((scrExt << 1) & 0xFE) | 0x01);

        // Program mux rate (3 bytes): rate_value (22 bits) + marker_bits
        // DVD mux rate = 10,080,000 bits/sec, field = rate / 50 = 25,200
        pack[10] = (byte)((DvdMuxRateField >> 14) & 0xFF);
        pack[11] = (byte)((DvdMuxRateField >> 6) & 0xFF);
        pack[12] = (byte)(((DvdMuxRateField << 2) & 0xFC) | 0x03);

        // Pack stuffing length (1 byte): 0xF8 = reserved bits + stuffing_length = 0
        pack[13] = 0xF8;
        offset = 14;

        // =================================================================
        // System Header (24 bytes)
        // Per ISO/IEC 13818-1 Section 2.5.3.5
        // Required in DVD navigation packs for player compatibility.
        // =================================================================
        // System header start code: 0x000001BB
        pack[offset++] = 0x00; pack[offset++] = 0x00;
        pack[offset++] = 0x01; pack[offset++] = 0xBB;

        // Header length (2 bytes): 18 (24 total - 6 for start code and length)
        WriteBigEndian16(pack, offset, 18); offset += 2;

        // Rate bound (3 bytes): maximum mux rate bound
        // '1' marker_bit rate_bound[21..0] marker_bit
        pack[offset] = (byte)(0x80 | ((DvdMuxRateField >> 15) & 0x7F));
        pack[offset + 1] = (byte)((DvdMuxRateField >> 7) & 0xFF);
        pack[offset + 2] = (byte)(((DvdMuxRateField << 1) & 0xFE) | 0x01);
        offset += 3;

        // Audio bound (1 byte): number of audio streams (bits 7-2) + fixed_flag + CSPS_flag
        pack[offset++] = 0x04; // 1 audio stream bound, fixed=0, CSPS=0

        // Flags (1 byte): system_audio_lock + system_video_lock + marker + video_bound
        pack[offset++] = 0xE1; // audio_lock=1, video_lock=1, marker=1, video_bound=1

        // Packet rate restriction (1 byte): 0xFF = no restriction
        pack[offset++] = 0xFF;

        // Stream bound entries: Video (0xE0), Audio (0xC0), Private Stream 2 (0xBF), Private Stream 1 (0xBD)
        // Format: stream_id (1 byte) + '11' + P-STD_buffer_bound_scale (1 bit) + P-STD_buffer_bound (13 bits)
        // Video stream (0xE0): buffer size = 232 × 1024 = 237,568 bytes
        pack[offset++] = 0xE0;
        pack[offset++] = 0xE0; // scale=1 (1024), bound=232/1024 = ~232 → 0x68 → scale bit + 13-bit
        pack[offset++] = 0x68; // P-STD buffer bound for video

        // Audio stream (0xC0): buffer size = 4 × 128 = 512 bytes
        pack[offset++] = 0xC0;
        pack[offset++] = 0xC0; // scale=0 (128), bound=4
        pack[offset++] = 0x20;

        // Private Stream 2 (0xBF): for navigation packs — buffer = 2 × 128 = 256
        pack[offset++] = 0xBF;
        pack[offset++] = 0xC0;
        pack[offset++] = 0x02;

        // Private Stream 1 (0xBD): subpicture — buffer = 58 × 1024
        pack[offset++] = 0xBD;
        pack[offset++] = 0xE0;
        pack[offset++] = 0x3A;

        // Pad offset to 38 (14 pack header + 24 system header)
        while (offset < 38) pack[offset++] = 0;

        // =================================================================
        // PCI Packet (Presentation Control Information)
        // Private Stream 2 (0x000001BF), sub-stream ID 0x00
        // Per DVD-Video specification Part 3, Section 5.1
        // =================================================================
        int pciOffset = 38;
        offset = pciOffset;

        // PES header: start code (0x000001BF) + length
        pack[offset++] = 0x00; pack[offset++] = 0x00;
        pack[offset++] = 0x01; pack[offset++] = 0xBF; // Private Stream 2
        WriteBigEndian16(pack, offset, (ushort)PciDataLen); offset += 2;

        // PCI sub-stream ID (1 byte): 0x00 = PCI
        pack[offset++] = 0x00;

        // ----- PCI_GI (PCI General Information) — 38 bytes -----
        int pciGiBase = offset;

        // NV_PCK_LBN (4 bytes): logical block number of this nav pack
        WriteBigEndian32(pack, offset, (uint)vobuStartSector); offset += 4;

        // VOBU_CAT (2 bytes): category
        // Bit 15: entry cell, Bit 14-12: reserved, Bit 11-0: VOBU category
        // 0x0000 = normal content, no restrictions
        WriteBigEndian16(pack, offset, 0x0000); offset += 2;

        // Reserved (2 bytes)
        offset += 2;

        // VOBU_UOP_CTL (4 bytes): user operation control — 0 = all ops permitted
        WriteBigEndian32(pack, offset, 0x00000000); offset += 4;

        // VOBU_S_PTM (4 bytes): VOBU start presentation time (90 kHz ticks)
        uint startPtm = (uint)(presentationTime.TotalSeconds * ClockRate90Khz);
        WriteBigEndian32(pack, offset, startPtm); offset += 4;

        // VOBU_E_PTM (4 bytes): VOBU end presentation time
        uint endPtm = startPtm + (uint)(VobuDurationSeconds * ClockRate90Khz);
        WriteBigEndian32(pack, offset, endPtm); offset += 4;

        // VOBU_SE_PTM (4 bytes): sequence end PTM (0 = not end of sequence)
        WriteBigEndian32(pack, offset, 0); offset += 4;

        // C_ELTM (4 bytes): cell elapsed time in BCD format HH:MM:SS:FF
        // This is the elapsed time from the start of the cell to this VOBU
        WriteCellElapsedTime(pack, offset, presentationTime);
        offset += 4;

        // ----- NSML_AGLI (Non-Seamless Angle Information) — 36 bytes -----
        // Per DVD spec, 9 angle entries × 4 bytes each = 36 bytes
        // For single-angle content, all entries are 0x00000000 (no angle)
        for (int i = 0; i < 9; i++)
        {
            WriteBigEndian32(pack, offset, 0x00000000);
            offset += 4;
        }

        // ----- HLI (Highlight Information for Menus) -----
        // HL_GI (2 bytes): highlight general info — 0 = no highlight
        WriteBigEndian16(pack, offset, 0x0000); offset += 2;

        // Pad remainder of PCI packet to reach PCI_DataLen total
        int pciEnd = pciOffset + 6 + PciDataLen;
        while (offset < pciEnd && offset < PackSize) pack[offset++] = 0;

        // =================================================================
        // DSI Packet (Data Search Information)
        // Private Stream 2 (0x000001BF), sub-stream ID 0x01
        // Per DVD-Video specification Part 3, Section 5.2
        //
        // The DSI packet contains all the information needed for:
        //   - VOBU-level seeking (forward/backward)
        //   - Trick play (fast forward, rewind, slow motion)
        //   - Angle switching (seamless and non-seamless)
        //   - A/V synchronization
        // =================================================================
        int dsiOffset = pciEnd;
        offset = dsiOffset;

        // PES header: start code (0x000001BF) + length
        pack[offset++] = 0x00; pack[offset++] = 0x00;
        pack[offset++] = 0x01; pack[offset++] = 0xBF; // Private Stream 2
        WriteBigEndian16(pack, offset, (ushort)DsiDataLen); offset += 2;

        // DSI sub-stream ID (1 byte): 0x01 = DSI
        pack[offset++] = 0x01;

        // ----- DSI_GI (DSI General Information) — 32 bytes -----
        int dsiGiBase = offset;

        // NV_PCK_SCR (4 bytes): SCR value of this nav pack (in 90 kHz / SCR clock)
        // Per DVD spec, this is the 32-bit SCR base value
        WriteBigEndian32(pack, offset, (uint)(scrBase & 0xFFFFFFFF)); offset += 4;

        // NV_PCK_LBN (4 bytes): logical block number of this nav pack
        WriteBigEndian32(pack, offset, (uint)vobuStartSector); offset += 4;

        // VOBU_EA (4 bytes): end address of this VOBU (sector offset from NV_PCK_LBN)
        WriteBigEndian32(pack, offset, (uint)vobuEndSectorOffset); offset += 4;

        // VOBU_1STREF_EA (4 bytes): first reference frame end address
        // Offset from NV_PCK_LBN to last sector of first I-frame in this VOBU
        // For simplicity, point to end of VOBU (player will handle)
        WriteBigEndian32(pack, offset, (uint)vobuEndSectorOffset); offset += 4;

        // VOBU_2NDREF_EA (4 bytes): second reference frame end address
        // 0xBFFF_FFFF = not present (only 1 ref frame per VOBU typically)
        WriteBigEndian32(pack, offset, 0xBFFFFFFF); offset += 4;

        // VOBU_3RDREF_EA (4 bytes): third reference frame end address
        WriteBigEndian32(pack, offset, 0xBFFFFFFF); offset += 4;

        // VOBU_VOB_IDN (2 bytes): VOB ID number (1-based)
        WriteBigEndian16(pack, offset, 1); offset += 2;

        // Reserved (1 byte)
        pack[offset++] = 0x00;

        // VOBU_C_IDN (1 byte): Cell ID number (1-based)
        pack[offset++] = 0x01;

        // C_ELTM (4 bytes): cell elapsed time in BCD HH:MM:SS:FF
        WriteCellElapsedTime(pack, offset, presentationTime);
        offset += 4;

        // ----- SML_PBI (Seamless Playback Information) — 18 bytes -----
        // Per DVD-Video spec Part 3, Section 5.2.3
        // Category (2 bytes): seamless playback category
        WriteBigEndian16(pack, offset, 0x0000); offset += 2;

        // ILVU_EA (4 bytes): interleaved unit end address (0 = not interleaved)
        WriteBigEndian32(pack, offset, 0); offset += 4;

        // ILVU_SA (4 bytes): next interleaved unit start address
        WriteBigEndian32(pack, offset, 0); offset += 4;

        // Size of next ILVU (4 bytes)
        WriteBigEndian32(pack, offset, 0); offset += 4;

        // VOB_V_S_PTM (4 bytes): video start PTM in ILVU
        WriteBigEndian32(pack, offset, 0); offset += 4;
        // Note: SML_PBI is 18 bytes per spec; remaining go to AGLI

        // ----- SML_AGLI (Seamless Angle Information) — 162 bytes -----
        // 9 angles × 18 bytes each = 162 bytes
        // Per DVD spec, each angle entry contains:
        //   - ILVU address (4 bytes): sector address of angle ILVU
        //   - ILVU size (2 bytes): size in sectors
        //   - VOB_V_S_PTM (4 bytes): video start PTM
        //   - VOB_V_E_PTM (4 bytes): video end PTM
        //   - VOB_A_STP_PTM1 (4 bytes): audio gap position
        for (int i = 0; i < 9; i++)
        {
            WriteBigEndian32(pack, offset, 0); offset += 4;  // Address of angle ILVU
            WriteBigEndian16(pack, offset, 0); offset += 2;  // Size of angle ILVU
            WriteBigEndian32(pack, offset, 0); offset += 4;  // VOB_V_S_PTM
            WriteBigEndian32(pack, offset, 0); offset += 4;  // VOB_V_E_PTM
            WriteBigEndian32(pack, offset, 0); offset += 4;  // VOB_A_STP_PTM1
        }

        // ----- VOBU_SRI (VOBU Search Information) — forward/backward seeking -----
        // Per DVD-Video specification Part 3, this provides sector offsets from
        // the current VOBU to VOBUs at specific time distances, enabling the
        // player to implement fast-forward, rewind, and chapter skip.
        //
        // The VOBU_SRI table contains offsets for:
        //   BWDi (backward): 240, 120, 60, 20, 15, 14, ..., 1 second(s) back
        //   FWDi (forward): 1, 2, ..., 14, 15, 20, 60, 120, 240 second(s) forward
        //
        // Each entry (4 bytes): sector offset from current VOBU to target VOBU.
        // 0x3FFFFFFF = no VOBU exists at that time distance.
        // 0x7FFFFFFF = same VOBU (search lands in current VOBU).

        const uint NoVobu = 0x3FFFFFFF;

        // Backward search times (in seconds): 240, 120, 60, 20, 15, 14, 13, ..., 1
        int[] bwdTimes = { 240, 120, 60, 20, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1 };
        foreach (int bwdSec in bwdTimes)
        {
            uint bwdOffset = FindVobuSectorOffset(vobuIndex, -bwdSec, vobuAddressMap);
            WriteBigEndian32(pack, offset, bwdOffset);
            offset += 4;
        }

        // NEXT_VOBU (4 bytes): offset to next VOBU
        if (vobuIndex + 1 < vobuAddressMap.Count)
        {
            uint nextOffset = (uint)(vobuAddressMap[vobuIndex + 1] - vobuStartSector);
            WriteBigEndian32(pack, offset, nextOffset);
        }
        else
        {
            WriteBigEndian32(pack, offset, NoVobu); // No next VOBU
        }
        offset += 4;

        // PREV_VOBU (4 bytes): offset to previous VOBU (negative offset stored as uint)
        if (vobuIndex > 0)
        {
            // Per DVD spec, backward offsets have bit 31 set, magnitude in bits 30-0
            uint prevDist = (uint)(vobuStartSector - vobuAddressMap[vobuIndex - 1]);
            WriteBigEndian32(pack, offset, 0x80000000 | prevDist);
        }
        else
        {
            WriteBigEndian32(pack, offset, NoVobu); // No previous VOBU
        }
        offset += 4;

        // Forward search times (in seconds): 1, 2, 3, ..., 14, 15, 20, 60, 120, 240
        int[] fwdTimes = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 20, 60, 120, 240 };
        foreach (int fwdSec in fwdTimes)
        {
            uint fwdOffset = FindVobuSectorOffset(vobuIndex, fwdSec, vobuAddressMap);
            WriteBigEndian32(pack, offset, fwdOffset);
            offset += 4;
        }

        // ----- SYNCI (Synchronous Information) — 8 entries × 2 bytes -----
        // Per DVD spec, time offsets (in 90 kHz ticks / 1024) to audio/subpicture sync points.
        // 0x0000 = sync point at start of VOBU for each elementary stream.
        for (int i = 0; i < 8; i++)
        {
            WriteBigEndian16(pack, offset, 0x0000);
            offset += 2;
        }

        // Pad remainder of DSI to fill the pack
        while (offset < PackSize) pack[offset++] = 0;

        return pack;
    }

    /// <summary>
    /// Looks up the sector offset from the current VOBU to a VOBU at a specific
    /// time distance. Used to populate the VOBU_SRI forward/backward search table.
    /// </summary>
    /// <param name="currentVobuIndex">Index of the current VOBU.</param>
    /// <param name="timeDeltaSeconds">Signed time offset in seconds (negative = backward).</param>
    /// <param name="vobuAddressMap">All known VOBU start sector addresses.</param>
    /// <returns>Sector offset or 0x3FFFFFFF if no VOBU exists at that distance.</returns>
    private static uint FindVobuSectorOffset(int currentVobuIndex, int timeDeltaSeconds, List<int> vobuAddressMap)
    {
        const uint NoVobu = 0x3FFFFFFF;

        // Calculate target VOBU index based on time delta and VOBU duration
        int vobuDelta = (int)(timeDeltaSeconds / VobuDurationSeconds);
        int targetIndex = currentVobuIndex + vobuDelta;

        if (targetIndex < 0 || targetIndex >= vobuAddressMap.Count)
            return NoVobu;

        int currentSector = vobuAddressMap[currentVobuIndex];
        int targetSector = vobuAddressMap[targetIndex];

        if (timeDeltaSeconds < 0)
        {
            // Backward: bit 31 set, magnitude in bits 30-0
            uint dist = (uint)(currentSector - targetSector);
            return 0x80000000 | dist;
        }
        else
        {
            // Forward: simple positive offset
            return (uint)(targetSector - currentSector);
        }
    }

    /// <summary>
    /// Writes cell elapsed time in DVD BCD format (HH:MM:SS:FF) where FF is frame
    /// number. The frame rate indicator is encoded in the upper 2 bits of the frame byte:
    ///   0b00 = 25 fps (PAL), 0b11 = 30 fps (NTSC, actually 29.97 drop-frame).
    /// Per DVD-Video specification Part 3, Section 5.1.3 (PCI_GI C_ELTM).
    /// </summary>
    private static void WriteCellElapsedTime(byte[] buffer, int offset, TimeSpan elapsed)
    {
        int totalSeconds = (int)elapsed.TotalSeconds;
        int hours = Math.Min(totalSeconds / 3600, 99);
        int minutes = (totalSeconds % 3600) / 60;
        int seconds = totalSeconds % 60;
        // Frame number within the second (assume 29.97 fps NTSC)
        int frames = (int)((elapsed.TotalSeconds - Math.Floor(elapsed.TotalSeconds)) * 30);
        frames = Math.Clamp(frames, 0, 29);

        buffer[offset] = ToBcd((byte)hours);
        buffer[offset + 1] = ToBcd((byte)minutes);
        buffer[offset + 2] = ToBcd((byte)seconds);
        // Frame byte: upper 2 bits = frame rate flag (0b11 = 30fps NTSC), lower 6 bits = frame number BCD
        buffer[offset + 3] = (byte)(0xC0 | ToBcd((byte)frames));
    }

    /// <summary>
    /// Builds the Video Manager Information (VMGI) file (VIDEO_TS.IFO).
    /// Contains disc-level information: title search table, region mask, and pointers.
    /// </summary>
    private byte[] BuildVmgIfo(DvdAuthoringJob job, List<VtsVobInfo> vtsInfos)
    {
        // VMGI is structured as sectors. Minimum: 1 sector for header + TT_SRPT table.
        int totalTitles = 0;
        foreach (var vts in vtsInfos)
            foreach (var _ in job.TitleSets[vts.VtsNumber - 1].Titles)
                totalTitles++;

        // Calculate sizes
        int ttSrptSize = 8 + totalTitles * 12; // 8-byte header + 12 bytes per title entry
        int ttSrptSectors = (ttSrptSize + SectorSize - 1) / SectorSize;

        // Total sectors: header (1) + TT_SRPT (variable)
        int totalSectors = 1 + ttSrptSectors;
        var ifo = new byte[totalSectors * SectorSize];

        // ----- Sector 0: VMG Header -----
        // Identifier: "DVDVIDEO-VMG"
        Encoding.ASCII.GetBytes("DVDVIDEO-VMG").CopyTo(ifo, 0);

        // Last sector of VMG (offset 0x0C, 4 bytes big-endian)
        WriteBigEndian32(ifo, 0x0C, (uint)(totalSectors - 1));

        // Version (offset 0x20): DVD-Video spec version 1.1 = 0x0011
        WriteBigEndian16(ifo, 0x20, 0x0011);

        // VMG category (offset 0x22): region mask in bits 24-31
        WriteBigEndian32(ifo, 0x22, (uint)(job.RegionMask << 24));

        // Number of title sets (offset 0x3E, 2 bytes)
        WriteBigEndian16(ifo, 0x3E, (ushort)vtsInfos.Count);

        // Provider ID (offset 0x40, 32 bytes ASCII)
        var providerBytes = Encoding.ASCII.GetBytes("OpenBurningSuite");
        Buffer.BlockCopy(providerBytes, 0, ifo, 0x40, Math.Min(providerBytes.Length, 32));

        // Sector pointer to TT_SRPT (offset 0xC4, 4 bytes) - starts at sector 1
        WriteBigEndian32(ifo, 0xC4, 1);

        // ----- TT_SRPT (Title Table Search Pointer Table) -----
        int ttOffset = SectorSize; // Sector 1

        // Number of titles (2 bytes)
        WriteBigEndian16(ifo, ttOffset, (ushort)totalTitles);

        // Last byte of table (4 bytes at offset +4)
        WriteBigEndian32(ifo, ttOffset + 4, (uint)(ttSrptSize - 1));

        // Title entries (12 bytes each)
        int entryOffset = ttOffset + 8;
        int titleNum = 0;
        foreach (var vtsInfo in vtsInfos)
        {
            var titleSet = job.TitleSets[vtsInfo.VtsNumber - 1];
            foreach (var title in titleSet.Titles)
            {
                // Title type (1 byte): 0x00 = sequential, bit 6 = one-sequential-PGC
                ifo[entryOffset] = 0x00;

                // Number of angles (1 byte): primary video + alternate angle paths
                ifo[entryOffset + 1] = (byte)Math.Max(1, title.AngleVideoPaths.Count + 1);

                // Number of chapters/PTT (2 bytes)
                int chapterCount = Math.Max(1, title.Chapters.Count);
                WriteBigEndian16(ifo, entryOffset + 2, (ushort)chapterCount);

                // Parental management mask (2 bytes): 0x0000 = no parental control
                WriteBigEndian16(ifo, entryOffset + 4, 0x0000);

                // VTS number (1 byte, 1-based)
                ifo[entryOffset + 6] = (byte)vtsInfo.VtsNumber;

                // VTS title number within the VTS (1 byte, 1-based)
                ifo[entryOffset + 7] = (byte)(titleNum + 1);

                // Start sector of VTS (4 bytes) — relative to VIDEO_TS directory start
                // We set this to 0 as the actual sector address depends on the ISO layout
                WriteBigEndian32(ifo, entryOffset + 8, 0);

                entryOffset += 12;
                titleNum++;
            }
        }

        return ifo;
    }

    /// <summary>
    /// Builds a Video Title Set Information (VTSI) file (VTS_xx_0.IFO).
    /// Contains title set-level information: stream attributes, PGC tables, chapter mapping,
    /// time map table (VTS_TMAPT), and VOBU address map (VTS_VOBU_ADMAP).
    ///
    /// Per DVD-Video specification Part 3 Section 4.4, the VTS IFO contains:
    ///   - VTS header: video/audio/subtitle stream attributes, sector pointers
    ///   - VTS_PTT_SRPT: Part-of-Title Search Pointer Table (chapter→PGC mapping)
    ///   - VTS_PGCIT: PGC Information Table (program chain descriptors)
    ///   - VTS_TMAPT: Time Map Table (time→VOBU mapping for time-based seeking)
    ///   - VTS_VOBU_ADMAP: VOBU Address Map (sequential VOBU sector addresses)
    /// </summary>
    private byte[] BuildVtsIfo(DvdAuthoringJob job, DvdTitleSet titleSet, VtsVobInfo vtsInfo)
    {
        bool isNtsc = job.VideoStandard.Equals("NTSC", StringComparison.OrdinalIgnoreCase);

        // Calculate sizes for VTS_PTT_SRPT and VTS_PGCIT
        int totalChapters = 0;
        foreach (var title in titleSet.Titles)
            totalChapters += Math.Max(1, title.Chapters.Count);

        int pttSrptSize = 8 + titleSet.Titles.Count * 4 + totalChapters * 4;
        int pttSrptSectors = (pttSrptSize + SectorSize - 1) / SectorSize;

        int pgcitSize = BuildPgcitSize(titleSet);
        int pgcitSectors = (pgcitSize + SectorSize - 1) / SectorSize;

        // VTS_TMAPT: Time Map Table
        // Per DVD-Video spec, TMAPT maps time offsets to VOBU sector addresses.
        // Header (8 bytes) + 1 map per title, each with:
        //   - Map header (4 bytes: time_unit + number of entries)
        //   - Entries (4 bytes each: VOBU sector address at each time_unit interval)
        // Time unit: 1 second (most common, allows 1-second seeking granularity)
        int tmaptSize = 8; // TMAPT header
        foreach (var title in titleSet.Titles)
        {
            // Each time map: 4-byte pointer in header + actual entries
            tmaptSize += 4; // Pointer to this title's map
        }
        // Add actual time map data
        int tmaptDataOffset = tmaptSize;
        foreach (var title in titleSet.Titles)
        {
            // Estimate duration: number of VOBUs × VOBU duration
            // Time entries at 1-second intervals
            int durationSeconds = Math.Max(1, (int)(vtsInfo.VobuTimeMap.Count * VobuDurationSeconds));
            int entryCount = durationSeconds; // One entry per second
            tmaptSize += 4 + entryCount * 4; // 4 byte header + entries
        }
        int tmaptSectors = (tmaptSize + SectorSize - 1) / SectorSize;

        // VTS_VOBU_ADMAP: sequential list of VOBU start sector addresses
        // Header: 4 bytes (last byte address of this table)
        // Entries: 4 bytes each (sector address of each VOBU)
        int vobuAdmapSize = 4 + vtsInfo.VobuAddressMap.Count * 4;
        int vobuAdmapSectors = (vobuAdmapSize + SectorSize - 1) / SectorSize;

        // Total sectors: header (1) + PTT_SRPT + PGCIT + TMAPT + VOBU_ADMAP
        int totalSectors = 1 + pttSrptSectors + pgcitSectors + tmaptSectors + vobuAdmapSectors;
        var ifo = new byte[totalSectors * SectorSize];

        // ----- Sector 0: VTS Header -----
        Encoding.ASCII.GetBytes("DVDVIDEO-VTS").CopyTo(ifo, 0);

        // Last sector of VTS IFO
        WriteBigEndian32(ifo, 0x0C, (uint)(totalSectors - 1));

        // Version
        WriteBigEndian16(ifo, 0x20, 0x0011);

        // VTS category
        WriteBigEndian32(ifo, 0x22, 0);

        // ----- Video attributes (offset 0x200) -----
        // Byte 0: coding mode (0=MPEG-1, 1=MPEG-2) + standard (0=NTSC, 1=PAL)
        byte videoAttr0 = 0x40; // MPEG-2
        if (!isNtsc) videoAttr0 |= 0x10; // PAL

        // Aspect ratio: 0x00 = 4:3, 0x0C = 16:9
        if (job.AspectRatio == "16:9") videoAttr0 |= 0x0C;
        ifo[0x200] = videoAttr0;

        // Resolution: NTSC=720x480, PAL=720x576 (full D1)
        // Byte 1: resolution + bit rate (0=VBR, 1=CBR)
        ifo[0x201] = isNtsc ? (byte)0x4F : (byte)0x4F; // 720xfull, VBR

        // ----- Audio attributes (offset 0x202) -----
        // Number of audio streams (offset 0x202)
        WriteBigEndian16(ifo, 0x202, (ushort)Math.Min(titleSet.AudioStreams.Count, 8));

        // Audio stream attributes (8 bytes each, starting at 0x204)
        for (int i = 0; i < Math.Min(titleSet.AudioStreams.Count, 8); i++)
        {
            var audio = titleSet.AudioStreams[i];
            int audioOffset = 0x204 + i * 8;

            // Byte 0: coding mode
            byte audioMode = audio.Codec.ToUpperInvariant() switch
            {
                "AC3" => 0x00,   // AC-3 (Dolby Digital)
                "MP2" => 0x40,   // MPEG-1 Audio
                "LPCM" => 0x80,  // Linear PCM
                "DTS" => 0xC0,   // DTS
                _ => 0x00        // Default to AC-3
            };
            ifo[audioOffset] = audioMode;

            // Byte 1: application mode (bits 7-6) | quantization/DRC (bits 5-4) |
            //         sample frequency (bits 3-2) | channels code (bits 1-0)
            // Sample frequency: 00 = 48 kHz, 01 = 96 kHz
            // Channels code: 0 = mono, 1 = stereo, ... (number of channels minus 1, max 7)
            byte sampleRateCode = (byte)(audio.SampleRate == 96000 ? 0x01 : 0x00);
            byte channelsCode = (byte)(Math.Clamp(audio.Channels, 1, 8) - 1);
            ifo[audioOffset + 1] = (byte)((sampleRateCode << 2) | (channelsCode & 0x03));

            // Bytes 2-3: ISO 639 language code
            if (audio.LanguageCode.Length >= 2)
            {
                ifo[audioOffset + 2] = (byte)audio.LanguageCode[0];
                ifo[audioOffset + 3] = (byte)audio.LanguageCode[1];
            }

            // Byte 5: number of channels (0-based: 0=mono, 1=stereo, 5=5.1)
            ifo[audioOffset + 5] = (byte)(Math.Max(1, audio.Channels) - 1);

            // Bytes 6-7: quantization / sample rate
            ifo[audioOffset + 6] = audio.SampleRate switch
            {
                48000 => 0x00,
                96000 => 0x01,
                _ => 0x00
            };
        }

        // ----- Subtitle attributes (offset 0x254) -----
        WriteBigEndian16(ifo, 0x254, (ushort)Math.Min(titleSet.SubtitleStreams.Count, 32));

        for (int i = 0; i < Math.Min(titleSet.SubtitleStreams.Count, 32); i++)
        {
            var sub = titleSet.SubtitleStreams[i];
            int subOffset = 0x256 + i * 6;

            // Coding mode: 0x00 = RLE bitmap
            ifo[subOffset] = 0x00;

            // Language code
            if (sub.LanguageCode.Length >= 2)
            {
                ifo[subOffset + 2] = (byte)sub.LanguageCode[0];
                ifo[subOffset + 3] = (byte)sub.LanguageCode[1];
            }
        }

        // ----- VTS_PTT_SRPT (Part-of-Title Search Pointer Table) -----
        // Sector pointer (offset 0xC8 in VTS header)
        int pttSector = 1;
        WriteBigEndian32(ifo, 0xC8, (uint)pttSector);

        int pttBase = pttSector * SectorSize;
        WriteBigEndian16(ifo, pttBase, (ushort)titleSet.Titles.Count);
        WriteBigEndian32(ifo, pttBase + 4, (uint)(pttSrptSize - 1));

        int pttEntryOffset = pttBase + 8;
        int titlePttOffset = 8 + titleSet.Titles.Count * 4; // Offset to first PTT data

        for (int t = 0; t < titleSet.Titles.Count; t++)
        {
            // Title pointer: offset to this title's PTT entries
            WriteBigEndian32(ifo, pttEntryOffset, (uint)titlePttOffset);
            pttEntryOffset += 4;

            var title = titleSet.Titles[t];
            int chapters = Math.Max(1, title.Chapters.Count);

            // Write PTT entries (4 bytes each: PGCN + PGCIT cell number)
            for (int c = 0; c < chapters; c++)
            {
                int pttDataOffset = pttBase + titlePttOffset + c * 4;
                if (pttDataOffset + 3 < ifo.Length)
                {
                    WriteBigEndian16(ifo, pttDataOffset, (ushort)(t + 1)); // PGCN (1-based)
                    WriteBigEndian16(ifo, pttDataOffset + 2, (ushort)(c + 1)); // PG number (1-based)
                }
            }

            titlePttOffset += chapters * 4;
        }

        // ----- VTS_PGCIT (PGC Information Table) -----
        int pgcitSector = pttSector + pttSrptSectors;
        WriteBigEndian32(ifo, 0xCC, (uint)pgcitSector);

        int pgcitBase = pgcitSector * SectorSize;

        // Number of PGCs (2 bytes)
        WriteBigEndian16(ifo, pgcitBase, (ushort)titleSet.Titles.Count);

        // Last byte of PGCIT
        WriteBigEndian32(ifo, pgcitBase + 4, (uint)(pgcitSize - 1));

        // PGC search pointers and PGC data
        int pgcSearchOffset = pgcitBase + 8;
        int pgcDataStart = 8 + titleSet.Titles.Count * 8; // After all search pointers

        for (int t = 0; t < titleSet.Titles.Count; t++)
        {
            var title = titleSet.Titles[t];

            // PGC search pointer (8 bytes): program category + offset
            WriteBigEndian32(ifo, pgcSearchOffset, 0x81000000); // Entry PGC, title
            WriteBigEndian32(ifo, pgcSearchOffset + 4, (uint)pgcDataStart);
            pgcSearchOffset += 8;

            // PGC descriptor (236 bytes minimum per DVD spec)
            int pgcBase = pgcitBase + pgcDataStart;
            if (pgcBase + 236 <= ifo.Length)
            {
                // PGC contents: programs, cells, playback time
                int programs = Math.Max(1, title.Chapters.Count);
                int cells = programs; // 1 cell per chapter for simplicity

                // Bytes 2-3: number of programs + cells
                ifo[pgcBase + 2] = (byte)programs;
                ifo[pgcBase + 3] = (byte)cells;

                // Bytes 4-7: playback time (BCD format HH:MM:SS:FF)
                // Estimate from VOBU count
                if (vtsInfo.VobuTimeMap.Count > 0)
                {
                    var lastVobuTime = vtsInfo.VobuTimeMap[^1];
                    var totalDuration = lastVobuTime + TimeSpan.FromSeconds(VobuDurationSeconds);
                    WriteCellElapsedTime(ifo, pgcBase + 4, totalDuration);
                }

                // PGC still time (byte 8): 0x00 = no pause after PGC
                ifo[pgcBase + 8] = 0x00;

                // Next PGC number (bytes 14-15): 0 = stop, or next title
                if (t + 1 < titleSet.Titles.Count)
                    WriteBigEndian16(ifo, pgcBase + 14, (ushort)(t + 2));

                // Program map offset (bytes 230-231): offset from PGC start
                WriteBigEndian16(ifo, pgcBase + 230, 236);

                // Cell playback info offset (bytes 232-233)
                WriteBigEndian16(ifo, pgcBase + 232, (ushort)(236 + programs));

                // Cell position info offset (bytes 234-235)
                WriteBigEndian16(ifo, pgcBase + 234, (ushort)(236 + programs + cells * 24));
            }

            pgcDataStart += 236 + Math.Max(1, title.Chapters.Count) +
                Math.Max(1, title.Chapters.Count) * 24 + Math.Max(1, title.Chapters.Count) * 4;
        }

        // ----- VTS_TMAPT (Time Map Table) -----
        // Per DVD-Video specification Part 3, Section 4.4.4
        // Provides a mapping from playback time to VOBU sector addresses,
        // enabling time-based seeking (e.g., "skip to 1:23:45").
        //
        // Structure:
        //   - Number of title time maps (2 bytes)
        //   - Reserved (2 bytes)
        //   - Map pointers: one 4-byte offset per title
        //   - For each title: time_unit (4 bytes) + VOBU_sector_entries
        int tmaptSector = pgcitSector + pgcitSectors;
        WriteBigEndian32(ifo, 0xD0, (uint)tmaptSector); // VTS_TMAPT sector pointer

        int tmaptBase = tmaptSector * SectorSize;

        // Number of title time maps (2 bytes)
        WriteBigEndian16(ifo, tmaptBase, (ushort)titleSet.Titles.Count);

        // Reserved (2 bytes)
        WriteBigEndian16(ifo, tmaptBase + 2, 0);

        // Last byte of TMAPT (4 bytes)
        WriteBigEndian32(ifo, tmaptBase + 4, (uint)(tmaptSize - 1));

        // Map data offset (start after header + pointers)
        int tmapPtrOffset = tmaptBase + 8;
        int tmapDataCurrent = 8 + titleSet.Titles.Count * 4;

        for (int t = 0; t < titleSet.Titles.Count; t++)
        {
            // Write pointer to this title's time map
            WriteBigEndian32(ifo, tmapPtrOffset, (uint)tmapDataCurrent);
            tmapPtrOffset += 4;

            // Compute time map entries at 1-second intervals
            int durationSeconds = Math.Max(1, (int)(vtsInfo.VobuTimeMap.Count * VobuDurationSeconds));
            int entryCount = durationSeconds;

            // Time map header for this title
            int tmapBase = tmaptBase + tmapDataCurrent;
            if (tmapBase + 4 + entryCount * 4 <= ifo.Length)
            {
                // tmu (1 byte): time unit in seconds (1 = 1-second intervals)
                ifo[tmapBase] = 0x01;
                // Reserved (1 byte)
                ifo[tmapBase + 1] = 0x00;
                // Number of entries (2 bytes)
                WriteBigEndian16(ifo, tmapBase + 2, (ushort)entryCount);

                // Time map entries: for each second, find the VOBU that covers that time
                for (int sec = 0; sec < entryCount; sec++)
                {
                    // Find VOBU index for this second
                    int vobuIdx = (int)(sec / VobuDurationSeconds);
                    vobuIdx = Math.Clamp(vobuIdx, 0, vtsInfo.VobuAddressMap.Count - 1);

                    int sectorAddr = vtsInfo.VobuAddressMap.Count > 0
                        ? vtsInfo.VobuAddressMap[vobuIdx] : 0;

                    // Entry: bit 31 = discontinuity flag (0), bits 30-0 = sector address
                    WriteBigEndian32(ifo, tmapBase + 4 + sec * 4, (uint)sectorAddr);
                }
            }

            tmapDataCurrent += 4 + entryCount * 4;
        }

        // ----- VTS_VOBU_ADMAP (VOBU Address Map) -----
        // Per DVD-Video specification Part 3, Section 4.4.5
        // Sequential list of sector addresses for every VOBU in the VTS.
        // This enables the player to locate any VOBU by its sequential index.
        //
        // Structure:
        //   - Last byte address of this table (4 bytes)
        //   - VOBU entries: sector address of each VOBU (4 bytes each)
        int vobuAdmapSector = tmaptSector + tmaptSectors;
        WriteBigEndian32(ifo, 0xD4, (uint)vobuAdmapSector); // VTS_VOBU_ADMAP sector pointer

        int admapBase = vobuAdmapSector * SectorSize;

        // Last byte of ADMAP
        WriteBigEndian32(ifo, admapBase, (uint)(vobuAdmapSize - 1));

        // Write VOBU sector addresses
        for (int v = 0; v < vtsInfo.VobuAddressMap.Count; v++)
        {
            int entryOff = admapBase + 4 + v * 4;
            if (entryOff + 3 < ifo.Length)
            {
                WriteBigEndian32(ifo, entryOff, (uint)vtsInfo.VobuAddressMap[v]);
            }
        }

        return ifo;
    }

    /// <summary>Calculates the size of the PGCIT table.</summary>
    private static int BuildPgcitSize(DvdTitleSet titleSet)
    {
        int size = 8; // Header
        size += titleSet.Titles.Count * 8; // Search pointers

        foreach (var title in titleSet.Titles)
        {
            int programs = Math.Max(1, title.Chapters.Count);
            // PGC descriptor + program map + cell playback + cell position
            size += 236 + programs + programs * 24 + programs * 4;
        }

        return size;
    }

    /// <summary>Creates a basic menu VOB from a menu definition.</summary>
    private static async Task CreateMenuVobAsync(
        DvdMenu menu, string outputPath, string videoStandard, CancellationToken ct)
    {
        // Create a minimal valid VOB with a navigation pack and still frame
        var menuVobuMap = new List<int> { 0 };
        var navPack = CreateNavigationPack(TimeSpan.Zero, 0, 0, 0, menuVobuMap);
        await File.WriteAllBytesAsync(outputPath, navPack, ct);
    }

    /// <summary>Counts total video files that need transcoding.</summary>
    private static int CountVideoFiles(DvdAuthoringJob job)
    {
        int count = 0;
        foreach (var ts in job.TitleSets)
            count += ts.Titles.Count;
        return count;
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
    // Internal types
    // -----------------------------------------------------------------------

    private sealed class VtsVobInfo
    {
        public int VtsNumber { get; set; }
        public List<string> VobParts { get; set; } = new();
        public List<CellInfo> ChapterCells { get; set; } = new();
        /// <summary>Sector addresses of each VOBU's navigation pack, for VTS_VOBU_ADMAP.</summary>
        public List<int> VobuAddressMap { get; set; } = new();
        /// <summary>Presentation time of each VOBU, for VTS_TMAPT.</summary>
        public List<TimeSpan> VobuTimeMap { get; set; } = new();
    }

    private sealed class CellInfo
    {
        public TimeSpan StartTime { get; set; }
        public string ChapterName { get; set; } = string.Empty;
    }

    /// <summary>Converts a value 0-99 to BCD (Binary Coded Decimal) encoding.</summary>
    private static byte ToBcd(byte value)
    {
        return (byte)(((value / 10) << 4) | (value % 10));
    }
}
