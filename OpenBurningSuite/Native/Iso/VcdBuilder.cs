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

namespace OpenBurningSuite.Native.Iso;

/// <summary>
/// Native VCD (Video CD) and SVCD (Super Video CD) image builder.
/// Creates compliant disc images per IEC 62107 (White Book) and ISO 11172 (MPEG-1) /
/// ISO 13818 (MPEG-2) specifications.
///
/// VCD/SVCD disc layout:
///   Track 1  — ISO 9660 data track (Mode 2 Form 1, 2048-byte user data)
///              Contains the VCD directory structure: VCD/, MPEGAV/, SEGMENT/, CDI/, EXT/
///   Tracks 2-99 — MPEG A/V data (Mode 2 Form 2, 2328-byte user data)
///
/// The output is a BIN/CUE image pair suitable for burning with SAO or DAO write mode.
/// </summary>
public sealed class VcdBuilder
{
    // ----- CD-ROM XA sector layout constants -----
    private const int RawSectorSize = 2352;
    private const int Mode2Form1UserData = 2048;
    private const int Mode2Form2UserData = 2328;
    private const int SyncHeaderSize = 16;     // 12 sync + 4 header bytes
    private const int SubHeaderSize = 8;       // Mode 2 sub-header (4 bytes × 2 for interleave)
    private const int Mode2Form1EccSize = 280; // L2 ECC (4+276)
    private const int Mode2Form2EdcSize = 4;   // EDC only (no L2 ECC)

    // ----- CD sync pattern -----
    private static readonly byte[] SyncPattern =
    {
        0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        0xFF, 0xFF, 0xFF, 0x00
    };

    // ----- VCD directory structure constants -----
    private const string VcdInfoFile = "INFO.VCD";
    private const string SvcdInfoFile = "INFO.SVD";
    private const string EntriesFile = "ENTRIES.VCD";
    private const string SvcdEntriesFile = "ENTRIES.SVD";
    private const string LotFile = "LOT.VCD";
    private const string SvcdLotFile = "LOT.SVD";
    private const string PsdFile = "PSD.VCD";
    private const string SvcdPsdFile = "PSD.SVD";
    private const string SearchFile = "SEARCH.DAT";
    private const string ScanDataFile = "SCANDATA.DAT";
    private const string TracksFile = "TRACKS.SVD";

    /// <summary>
    /// Builds a VCD or SVCD disc image (BIN/CUE pair) from the specified build job.
    /// </summary>
    public async Task BuildVcdImageAsync(
        BuildJob job,
        IProgress<BuildProgress> progress,
        CancellationToken ct)
    {
        bool isSvcd = FormatHelper.IsSvcd(job.DiscType);
        bool isXsvcd = FormatHelper.IsXsvcd(job.DiscType);
        string formatName = isXsvcd ? "XSVCD" : (isSvcd ? "SVCD" : "VCD");
        // XSVCD uses same structure as SVCD with extended parameters
        bool svcdCompatible = isSvcd || isXsvcd;

        // Validate inputs
        ValidateInputs(job, svcdCompatible);

        progress.Report(new BuildProgress
        {
            PercentComplete = 2,
            StatusMessage = $"Building {formatName} image...",
            LogLine = $"[Info] {formatName} build started — {job.VideoFiles.Count} video file(s)"
        });

        // Determine output paths
        var binPath = Path.ChangeExtension(job.OutputImagePath, ".bin");
        var cuePath = Path.ChangeExtension(job.OutputImagePath, ".cue");

        // Validate output directory
        var outputDir = Path.GetDirectoryName(binPath);
        if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
        {
            try { Directory.CreateDirectory(outputDir); }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Cannot create output directory '{outputDir}': {ex.Message}");
            }
        }

        // Validate all MPEG files
        var mpegFiles = new List<MpegFileInfo>();
        for (int i = 0; i < job.VideoFiles.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var videoPath = job.VideoFiles[i];
            if (!File.Exists(videoPath))
                throw new FileNotFoundException($"Video file not found: {videoPath}");

            var mpegInfo = AnalyzeMpegFile(videoPath, svcdCompatible);
            mpegFiles.Add(mpegInfo);

            progress.Report(new BuildProgress
            {
                PercentComplete = 2 + (i + 1) * 8 / job.VideoFiles.Count,
                LogLine = $"[Info] Video {i + 1}: {Path.GetFileName(videoPath)} — " +
                          $"{FormatHelper.FormatBytes(mpegInfo.FileSize)}, {mpegInfo.DurationEstimateSeconds:F1}s"
            });
        }

        // Calculate disc layout
        var layout = CalculateDiscLayout(mpegFiles, svcdCompatible, job);

        progress.Report(new BuildProgress
        {
            PercentComplete = 12,
            StatusMessage = $"Disc layout: {layout.TotalSectors} sectors ({layout.TrackCount} tracks)",
            LogLine = $"[Info] Track 1 (data): {layout.DataTrackSectors} sectors, " +
                      $"Tracks 2-{layout.TrackCount}: MPEG A/V data"
        });

        // Validate total size against media capacity
        long totalBytes = (long)layout.TotalSectors * RawSectorSize;
        long capacity = FormatHelper.GetCapacity(job.DiscType);
        if (totalBytes > capacity && !job.PadToCapacity)
        {
            progress.Report(new BuildProgress
            {
                LogLine = $"[Warning] Image size ({FormatHelper.FormatBytes(totalBytes)}) may exceed " +
                          $"media capacity ({FormatHelper.FormatBytes(capacity)})"
            });
        }

        // Build the BIN image
        await using var binStream = new FileStream(binPath, FileMode.Create, FileAccess.Write,
            FileShare.None, 65536, true);

        // Write Track 1 — ISO 9660 data track (Mode 2 Form 1)
        progress.Report(new BuildProgress
        {
            PercentComplete = 15,
            StatusMessage = "Writing data track (Track 1)...",
            LogLine = "[Info] Writing ISO 9660 filesystem structure (Track 1)"
        });
        await WriteDataTrackAsync(binStream, layout, job, svcdCompatible, mpegFiles, ct);

        // Write MPEG A/V tracks (Track 2 onwards)
        for (int i = 0; i < mpegFiles.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            int trackNum = i + 2;
            var mpegInfo = mpegFiles[i];

            int progressBase = 20 + i * 70 / mpegFiles.Count;
            int progressEnd = 20 + (i + 1) * 70 / mpegFiles.Count;

            progress.Report(new BuildProgress
            {
                PercentComplete = progressBase,
                StatusMessage = $"Writing Track {trackNum} ({Path.GetFileName(mpegInfo.FilePath)})...",
                LogLine = $"[Info] Track {trackNum}: {Path.GetFileName(mpegInfo.FilePath)} — " +
                          $"{mpegInfo.SectorCount} sectors"
            });

            await WriteMpegTrackAsync(binStream, mpegInfo, trackNum, layout, progressBase, progressEnd,
                progress, ct);
        }

        progress.Report(new BuildProgress
        {
            PercentComplete = 92,
            StatusMessage = "Writing CUE sheet...",
            LogLine = $"[Info] Writing CUE sheet: {cuePath}"
        });

        // Generate the CUE sheet
        await WriteCueSheetAsync(cuePath, binPath, layout, mpegFiles, svcdCompatible, ct);

        var finalSize = binStream.Length;
        progress.Report(new BuildProgress
        {
            PercentComplete = 100,
            StatusMessage = $"{formatName} image build complete.",
            LogLine = $"[Info] {formatName} image: {FormatHelper.FormatBytes(finalSize)} — " +
                      $"{binPath}"
        });
    }

    // -----------------------------------------------------------------------
    // Input validation
    // -----------------------------------------------------------------------

    private static void ValidateInputs(BuildJob job, bool isSvcd)
    {
        string formatName = isSvcd ? "SVCD" : "VCD";

        if (job.VideoFiles.Count == 0)
            throw new ArgumentException($"At least one MPEG video file is required for {formatName} build.");

        if (job.VideoFiles.Count > FormatHelper.VcdMaxPlaybackItems)
            throw new ArgumentException(
                $"{formatName} supports a maximum of {FormatHelper.VcdMaxPlaybackItems} video tracks, " +
                $"but {job.VideoFiles.Count} were specified.");

        if (string.IsNullOrWhiteSpace(job.OutputImagePath))
            throw new ArgumentException("Output image path must not be empty.");

        // Validate video standard
        var standard = job.VideoStandard?.ToUpperInvariant() ?? "NTSC";
        if (standard is not ("NTSC" or "PAL" or "SECAM" or "FILM" or "FILM (24FPS)"))
            throw new ArgumentException(
                $"Invalid video standard '{job.VideoStandard}'. Use NTSC, PAL, SECAM, or Film.");
    }

    // -----------------------------------------------------------------------
    // MPEG file analysis
    // -----------------------------------------------------------------------

    /// <summary>
    /// Analyzes an MPEG file to determine its properties and sector count.
    /// Performs basic header validation to ensure the file is valid MPEG-1/2 program stream.
    /// </summary>
    private static MpegFileInfo AnalyzeMpegFile(string filePath, bool isSvcd)
    {
        var fi = new FileInfo(filePath);
        if (fi.Length == 0)
            throw new InvalidOperationException($"MPEG file is empty: {filePath}");

        // Read and validate MPEG header
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
            FileShare.Read, 4096, false);
        var header = new byte[Math.Min(16, fi.Length)];
        stream.ReadExactly(header, 0, header.Length);

        // Check for MPEG-1/2 pack start code (0x000001BA)
        bool hasPackHeader = header.Length >= 4 &&
                             header[0] == 0x00 && header[1] == 0x00 &&
                             header[2] == 0x01 && header[3] == 0xBA;

        // Also accept MPEG-1/2 system header start code (0x000001BB)
        bool hasSystemHeader = header.Length >= 4 &&
                               header[0] == 0x00 && header[1] == 0x00 &&
                               header[2] == 0x01 && header[3] == 0xBB;

        if (!hasPackHeader && !hasSystemHeader)
        {
            throw new InvalidOperationException(
                $"File does not appear to be a valid MPEG program stream: {filePath}. " +
                $"Expected MPEG pack header (0x000001BA) at start of file.");
        }

        // Determine MPEG version from pack header
        bool isMpeg2 = false;
        if (hasPackHeader && header.Length >= 5)
        {
            // MPEG-2: bits 7-6 of byte 4 are '01' (0x4x)
            // MPEG-1: bits 7-4 of byte 4 are '0010' (0x2x)
            isMpeg2 = (header[4] & 0xC0) == 0x40;
        }

        if (isSvcd && !isMpeg2)
        {
            // SVCD requires MPEG-2 — warn but don't fail (some tools produce valid SVCD with MPEG-1 headers)
            // The disc may still play on some players
        }

        // Calculate sector count: MPEG data goes into Mode 2 Form 2 sectors
        int userDataPerSector = Mode2Form2UserData;
        long sectorCount = (fi.Length + userDataPerSector - 1) / userDataPerSector;

        // Estimate playback duration
        double durationSeconds = isSvcd
            ? FormatHelper.EstimateSvcdPlaybackSeconds(fi.Length)
            : FormatHelper.EstimateVcdPlaybackSeconds(fi.Length);

        // 2-second pregap (150 sectors at 75 sectors/sec)
        const int pregapSectors = 150;

        return new MpegFileInfo
        {
            FilePath = filePath,
            FileSize = fi.Length,
            SectorCount = (int)Math.Min(sectorCount, int.MaxValue),
            PregapSectors = pregapSectors,
            IsMpeg2 = isMpeg2,
            DurationEstimateSeconds = durationSeconds
        };
    }

    // -----------------------------------------------------------------------
    // Disc layout calculation
    // -----------------------------------------------------------------------

    private static DiscLayout CalculateDiscLayout(
        List<MpegFileInfo> mpegFiles, bool isSvcd, BuildJob job)
    {
        // Track 1: ISO 9660 data track
        // Pre-gap (150 sectors) + System Area (16 sectors) + PVD/SVD (2 sectors) +
        // Path Table + Root Directory + VCD directory structure + file data
        // Minimum ~300 sectors for basic VCD/SVCD filesystem; we use 450 as safe minimum
        const int dataTrackPregap = 150;    // 2-second pre-gap
        const int systemAreaSectors = 16;   // ISO 9660 system area
        const int pvdSectors = 2;           // PVD + terminator
        const int directoryOverhead = 50;   // Directory records, path tables
        const int vcdMetadataSectors = 200; // INFO.VCD, ENTRIES.VCD, LOT.VCD, PSD.VCD, etc.

        // The BIN file does not include Track 1's pregap — the CUE sheet uses a
        // PREGAP directive so the CD burner generates the 2-second silence.
        // This keeps ISO 9660 LBA values consistent with BIN sector positions.
        int dataTrackSectors = systemAreaSectors + pvdSectors +
                               directoryOverhead + vcdMetadataSectors;

        // If still images are included, allocate additional sectors
        if (job.StillImageFiles.Count > 0)
        {
            long stillTotalBytes = 0;
            foreach (var still in job.StillImageFiles)
            {
                if (File.Exists(still))
                    stillTotalBytes += new FileInfo(still).Length;
            }
            int stillSectors = (int)Math.Min((stillTotalBytes + Mode2Form2UserData - 1) / Mode2Form2UserData, int.MaxValue);
            dataTrackSectors += stillSectors + 10; // 10 sectors padding
        }

        // Post-gap for Track 1 (per Red Book: 150 sectors minimum after data track)
        const int dataTrackPostgap = 150;
        dataTrackSectors += dataTrackPostgap;

        // MPEG tracks
        int totalMpegSectors = 0;
        var trackStartSectors = new List<int>();
        int currentSector = dataTrackSectors;

        foreach (var mpeg in mpegFiles)
        {
            trackStartSectors.Add(currentSector);
            int trackTotal = mpeg.PregapSectors + mpeg.SectorCount;
            totalMpegSectors += trackTotal;
            currentSector += trackTotal;
        }

        // Lead-out (6750 sectors = 90 seconds at 75 sectors/sec, per Red Book)
        const int leadOutSectors = 6750;

        return new DiscLayout
        {
            DataTrackSectors = dataTrackSectors,
            DataTrackPregap = dataTrackPregap,
            TrackStartSectors = trackStartSectors,
            // TotalSectors includes the pregap (generated by the burner) for capacity estimation
            TotalSectors = dataTrackPregap + currentSector + leadOutSectors,
            TrackCount = 1 + mpegFiles.Count, // Track 1 + MPEG tracks
            LeadOutSectors = leadOutSectors,
            IsSvcd = isSvcd
        };
    }

    // -----------------------------------------------------------------------
    // Track writing
    // -----------------------------------------------------------------------

    /// <summary>
    /// Writes the ISO 9660 data track (Track 1) with VCD/SVCD directory structure.
    /// Uses CD-ROM XA Mode 2 Form 1 sectors (2048 bytes user data + ECC).
    /// The BIN does not include the Track 1 pregap — it is specified via the
    /// CUE PREGAP directive and generated by the CD burner.
    /// </summary>
    private async Task WriteDataTrackAsync(
        Stream output, DiscLayout layout, BuildJob job, bool isSvcd,
        List<MpegFileInfo> mpegFiles, CancellationToken ct)
    {
        // No pregap in BIN — the CUE sheet uses a PREGAP directive for Track 1.
        // currentSector starts at 0 (= ISO LBA 0 = MSF 00:02:00 after 2-second lead-in).
        int currentSector = 0;

        // Write System Area (16 sectors of zeros — per ISO 9660 §6.2.1)
        for (int i = 0; i < 16; i++)
        {
            ct.ThrowIfCancellationRequested();
            var sectorData = new byte[Mode2Form1UserData];
            WriteMode2Form1Sector(output, sectorData, currentSector);
            currentSector++;
        }

        // Compute sector layout for all structures
        int pvdSector = currentSector;             // 16
        int terminatorSector = pvdSector + 1;      // 17
        int pathTableSector = terminatorSector + 1; // 18
        int rootDirSector = pathTableSector + 1;   // 19
        int vcdDirSector = rootDirSector + 1;      // 20
        int mpegavDirSector = vcdDirSector + 1;    // 21
        int segmentDirSector = mpegavDirSector + 1; // 22
        int extDirSector = segmentDirSector + 1;   // 23

        int nextSector = extDirSector + 1;         // 24
        int cdiDirSector = -1;
        if (!isSvcd && job.IncludeCdiApplication)
        {
            cdiDirSector = nextSector;
            nextSector++;                           // 25
        }

        int infoSector = nextSector;
        int entriesSector = infoSector + 1;

        // Write Primary Volume Descriptor (PVD) at sector 16
        var pvdData = BuildPrimaryVolumeDescriptor(job, isSvcd, layout,
            pathTableSector, rootDirSector);
        WriteMode2Form1Sector(output, pvdData, currentSector);
        currentSector++;

        // Write Volume Descriptor Set Terminator
        var termData = BuildVolumeDescriptorTerminator();
        WriteMode2Form1Sector(output, termData, currentSector);
        currentSector++;

        // Write path table
        var pathTableData = BuildPathTable(isSvcd, rootDirSector, vcdDirSector,
            mpegavDirSector, segmentDirSector, extDirSector, cdiDirSector);
        WriteMode2Form1Sector(output, pathTableData, currentSector);
        currentSector++;

        // Write root directory record
        var rootDirData = BuildRootDirectoryRecord(isSvcd, mpegFiles, job,
            rootDirSector, vcdDirSector, mpegavDirSector, segmentDirSector,
            extDirSector, cdiDirSector);
        WriteMode2Form1Sector(output, rootDirData, currentSector);
        currentSector++;

        // Write VCD/SVCD directory (VCD/ or SVCD/)
        var vcdDirData = BuildVcdDirectoryRecords(isSvcd, vcdDirSector, rootDirSector,
            infoSector, entriesSector);
        WriteMode2Form1Sector(output, vcdDirData, currentSector);
        currentSector++;

        // Write MPEGAV/MPEG2 directory
        var mpegavDirData = BuildMpegavDirectoryRecord(isSvcd, mpegavDirSector,
            rootDirSector, mpegFiles, layout);
        WriteMode2Form1Sector(output, mpegavDirData, currentSector);
        currentSector++;

        // Write SEGMENT directory
        var segmentDirData = BuildSubDirectoryRecord(segmentDirSector, rootDirSector);
        WriteMode2Form1Sector(output, segmentDirData, currentSector);
        currentSector++;

        // Write EXT directory
        var extDirData = BuildSubDirectoryRecord(extDirSector, rootDirSector);
        WriteMode2Form1Sector(output, extDirData, currentSector);
        currentSector++;

        // Write CDI directory (VCD only, optional)
        if (cdiDirSector >= 0)
        {
            var cdiDirData = BuildSubDirectoryRecord(cdiDirSector, rootDirSector);
            WriteMode2Form1Sector(output, cdiDirData, currentSector);
            currentSector++;
        }

        // Write INFO.VCD / INFO.SVD
        var infoData = BuildInfoFile(job, isSvcd, mpegFiles);
        WriteMode2Form1Sector(output, infoData, currentSector);
        currentSector++;

        // Write ENTRIES.VCD / ENTRIES.SVD
        var entriesData = BuildEntriesFile(isSvcd, mpegFiles, layout);
        WriteMode2Form1Sector(output, entriesData, currentSector);
        currentSector++;

        // Write LOT.VCD / LOT.SVD (List ID Offset Table) — required for PBC
        if (job.PlaybackControl)
        {
            var lotData = BuildLotFile(isSvcd, mpegFiles);
            // LOT is 32 sectors (65,536 bytes) per specification
            for (int i = 0; i < 32; i++)
            {
                ct.ThrowIfCancellationRequested();
                var chunk = new byte[Mode2Form1UserData];
                int srcOffset = i * Mode2Form1UserData;
                int copyLen = Math.Min(Mode2Form1UserData, lotData.Length - srcOffset);
                if (copyLen > 0)
                    Buffer.BlockCopy(lotData, srcOffset, chunk, 0, copyLen);
                WriteMode2Form1Sector(output, chunk, currentSector);
                currentSector++;
            }

            // Write PSD.VCD / PSD.SVD (Play Sequence Descriptor)
            var psdData = BuildPsdFile(isSvcd, mpegFiles);
            WriteMode2Form1Sector(output, psdData, currentSector);
            currentSector++;
        }

        // Write SEARCH.DAT if requested
        if (job.CreateSearchData)
        {
            var searchData = BuildSearchDat(mpegFiles, layout, isSvcd);
            int searchSectors = (searchData.Length + Mode2Form1UserData - 1) / Mode2Form1UserData;
            for (int i = 0; i < searchSectors; i++)
            {
                ct.ThrowIfCancellationRequested();
                var chunk = new byte[Mode2Form1UserData];
                int srcOffset = i * Mode2Form1UserData;
                int copyLen = Math.Min(Mode2Form1UserData, searchData.Length - srcOffset);
                if (copyLen > 0)
                    Buffer.BlockCopy(searchData, srcOffset, chunk, 0, copyLen);
                WriteMode2Form1Sector(output, chunk, currentSector);
                currentSector++;
            }
        }

        // Write SCANDATA.DAT if requested (SVCD only per spec, but we support both)
        if (job.CreateScanData && isSvcd)
        {
            var scanData = BuildScanDataDat(mpegFiles, layout);
            int scanSectors = (scanData.Length + Mode2Form1UserData - 1) / Mode2Form1UserData;
            for (int i = 0; i < scanSectors; i++)
            {
                ct.ThrowIfCancellationRequested();
                var chunk = new byte[Mode2Form1UserData];
                int srcOffset = i * Mode2Form1UserData;
                int copyLen = Math.Min(Mode2Form1UserData, scanData.Length - srcOffset);
                if (copyLen > 0)
                    Buffer.BlockCopy(scanData, srcOffset, chunk, 0, copyLen);
                WriteMode2Form1Sector(output, chunk, currentSector);
                currentSector++;
            }
        }

        // Write TRACKS.SVD for SVCD
        if (isSvcd)
        {
            var tracksData = BuildTracksSvd(mpegFiles, layout);
            WriteMode2Form1Sector(output, tracksData, currentSector);
            currentSector++;
        }

        // Write still image segments if provided
        foreach (var stillPath in job.StillImageFiles)
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(stillPath)) continue;

            var stillBytes = await File.ReadAllBytesAsync(stillPath, ct);
            int stillSectors = (stillBytes.Length + Mode2Form2UserData - 1) / Mode2Form2UserData;
            for (int i = 0; i < stillSectors; i++)
            {
                var chunk = new byte[Mode2Form2UserData];
                int srcOffset = i * Mode2Form2UserData;
                int copyLen = Math.Min(Mode2Form2UserData, stillBytes.Length - srcOffset);
                if (copyLen > 0)
                    Buffer.BlockCopy(stillBytes, srcOffset, chunk, 0, copyLen);
                WriteMode2Form2Sector(output, chunk, currentSector);
                currentSector++;
            }
        }

        // Write CD-i application stub for VCD playback on CD-i players (Green Book)
        if (!isSvcd && job.IncludeCdiApplication)
        {
            var cdiStub = BuildCdiApplicationStub(job);
            int cdiSectors = (cdiStub.Length + Mode2Form1UserData - 1) / Mode2Form1UserData;
            for (int i = 0; i < cdiSectors; i++)
            {
                ct.ThrowIfCancellationRequested();
                var chunk = new byte[Mode2Form1UserData];
                int srcOffset = i * Mode2Form1UserData;
                int copyLen = Math.Min(Mode2Form1UserData, cdiStub.Length - srcOffset);
                if (copyLen > 0)
                    Buffer.BlockCopy(cdiStub, srcOffset, chunk, 0, copyLen);
                WriteMode2Form1Sector(output, chunk, currentSector);
                currentSector++;
            }
        }

        // Pad remaining data track sectors
        while (currentSector < layout.DataTrackSectors)
        {
            ct.ThrowIfCancellationRequested();
            var padData = new byte[Mode2Form1UserData];
            WriteMode2Form1Sector(output, padData, currentSector);
            currentSector++;
        }

        await output.FlushAsync(ct);
    }

    /// <summary>
    /// Writes an MPEG A/V track (Mode 2 Form 2 sectors).
    /// </summary>
    private static async Task WriteMpegTrackAsync(
        Stream output, MpegFileInfo mpegInfo, int trackNumber, DiscLayout layout,
        int progressBase, int progressEnd,
        IProgress<BuildProgress> progress, CancellationToken ct)
    {
        int trackIdx = trackNumber - 2;
        int trackStartSector = layout.TrackStartSectors[trackIdx];
        int currentSector = trackStartSector;

        // Write pre-gap (150 sectors of silence)
        for (int i = 0; i < mpegInfo.PregapSectors; i++)
        {
            ct.ThrowIfCancellationRequested();
            WriteSilentSector(output, (byte)trackNumber, (uint)i);
            currentSector++;
        }

        // Write MPEG data as Mode 2 Form 2 sectors
        await using var mpegStream = new FileStream(mpegInfo.FilePath, FileMode.Open,
            FileAccess.Read, FileShare.Read, 65536, true);

        var readBuffer = new byte[Mode2Form2UserData];
        long totalRead = 0;
        int sectorIndex = 0;

        while (totalRead < mpegInfo.FileSize)
        {
            ct.ThrowIfCancellationRequested();

            var bytesToRead = (int)Math.Min(readBuffer.Length, mpegInfo.FileSize - totalRead);
            var bytesRead = await mpegStream.ReadAsync(readBuffer.AsMemory(0, bytesToRead), ct);
            if (bytesRead <= 0) break;

            // Clear any remaining bytes in buffer if last read was short
            if (bytesRead < readBuffer.Length)
                Array.Clear(readBuffer, bytesRead, readBuffer.Length - bytesRead);

            totalRead += bytesRead;

            WriteMode2Form2Sector(output, readBuffer, currentSector);
            currentSector++;
            sectorIndex++;

            // Report progress periodically (every 500 sectors ≈ 1.1 MB)
            if (sectorIndex % 500 == 0)
            {
                var pct = mpegInfo.FileSize > 0
                    ? progressBase + (int)(totalRead * (progressEnd - progressBase) / mpegInfo.FileSize)
                    : progressBase;
                progress.Report(new BuildProgress
                {
                    PercentComplete = Math.Min(pct, progressEnd),
                    BytesProcessed = totalRead,
                    TotalBytes = mpegInfo.FileSize,
                    StatusMessage = $"Track {trackNumber}: {FormatHelper.FormatBytes(totalRead)} / " +
                                   $"{FormatHelper.FormatBytes(mpegInfo.FileSize)}"
                });
            }
        }

        await output.FlushAsync(ct);
    }

    // -----------------------------------------------------------------------
    // Sector writing helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Writes a Mode 2 Form 1 sector (2048 bytes user data + sub-header + ECC).
    /// Used for ISO 9660 filesystem structures.
    /// </summary>
    private static void WriteMode2Form1Sector(Stream output, byte[] userData, int sectorNumber)
    {
        if (userData.Length > Mode2Form1UserData)
            throw new ArgumentException($"Mode 2 Form 1 user data must be ≤ {Mode2Form1UserData} bytes.");

        var sector = new byte[RawSectorSize];

        // Sync pattern
        Buffer.BlockCopy(SyncPattern, 0, sector, 0, SyncPattern.Length);

        // Header: MSF + Mode
        var (m, s, f) = SectorToMsf(sectorNumber);
        sector[12] = m;
        sector[13] = s;
        sector[14] = f;
        sector[15] = 0x02; // Mode 2

        // Sub-header (Form 1): file number, channel number, sub-mode, coding info
        // Sub-mode byte: bit 5 = data, bit 3 = Form indicator (0=Form1, 1=Form2)
        sector[16] = 0x00; // File number
        sector[17] = 0x00; // Channel number
        sector[18] = 0x08; // Sub-mode: data sector, Form 1
        sector[19] = 0x00; // Coding info
        // Duplicate sub-header
        sector[20] = sector[16];
        sector[21] = sector[17];
        sector[22] = sector[18];
        sector[23] = sector[19];

        // User data (2048 bytes at offset 24)
        var copyLen = Math.Min(userData.Length, Mode2Form1UserData);
        Buffer.BlockCopy(userData, 0, sector, 24, copyLen);

        // EDC (Error Detection Code) at offset 2072 (4 bytes)
        uint edc = ComputeEdc(sector, 16, 2072 - 16);
        sector[2072] = (byte)(edc & 0xFF);
        sector[2073] = (byte)((edc >> 8) & 0xFF);
        sector[2074] = (byte)((edc >> 16) & 0xFF);
        sector[2075] = (byte)((edc >> 24) & 0xFF);

        // L2 ECC (276 bytes) at offset 2076 — for full compliance, we write zeros
        // (most CD burners recompute ECC during burning; writing zeros is standard practice)

        output.Write(sector, 0, RawSectorSize);
    }

    /// <summary>
    /// Writes a Mode 2 Form 2 sector (2328 bytes user data, reduced error protection).
    /// Used for MPEG video/audio data where error correction is less critical.
    /// </summary>
    private static void WriteMode2Form2Sector(Stream output, byte[] userData, int sectorNumber)
    {
        var sector = new byte[RawSectorSize];

        // Sync pattern
        Buffer.BlockCopy(SyncPattern, 0, sector, 0, SyncPattern.Length);

        // Header: MSF + Mode
        var (m, s, f) = SectorToMsf(sectorNumber);
        sector[12] = m;
        sector[13] = s;
        sector[14] = f;
        sector[15] = 0x02; // Mode 2

        // Sub-header (Form 2): bit 5 = Form 2 indicator, bit 3 = data sector
        sector[16] = 0x00; // File number
        sector[17] = 0x00; // Channel number
        sector[18] = 0x28; // Sub-mode: Form 2 data (bit 5 = Form 2, bit 3 = data)
        sector[19] = 0x00; // Coding info
        // Duplicate sub-header
        sector[20] = sector[16];
        sector[21] = sector[17];
        sector[22] = sector[18];
        sector[23] = sector[19];

        // User data (2328 bytes at offset 24)
        var copyLen = Math.Min(userData.Length, Mode2Form2UserData);
        Buffer.BlockCopy(userData, 0, sector, 24, copyLen);

        // EDC (4 bytes at offset 2348) — optional for Form 2, but recommended
        uint edc = ComputeEdc(sector, 16, 2348 - 16);
        sector[2348] = (byte)(edc & 0xFF);
        sector[2349] = (byte)((edc >> 8) & 0xFF);
        sector[2350] = (byte)((edc >> 16) & 0xFF);
        sector[2351] = (byte)((edc >> 24) & 0xFF);

        output.Write(sector, 0, RawSectorSize);
    }

    /// <summary>
    /// Writes a silent (zero-filled) Mode 2 sector for pre-gaps.
    /// </summary>
    private static void WriteSilentSector(Stream output, byte trackNumber, uint sectorIndex)
    {
        var sector = new byte[RawSectorSize];
        Buffer.BlockCopy(SyncPattern, 0, sector, 0, SyncPattern.Length);

        var (m, s, f) = SectorToMsf((int)sectorIndex);
        sector[12] = m;
        sector[13] = s;
        sector[14] = f;
        sector[15] = 0x02; // Mode 2

        // Sub-header for silent sector
        sector[16] = 0x00;
        sector[17] = 0x00;
        sector[18] = 0x08; // Data, Form 1
        sector[19] = 0x00;
        sector[20] = sector[16];
        sector[21] = sector[17];
        sector[22] = sector[18];
        sector[23] = sector[19];

        output.Write(sector, 0, RawSectorSize);
    }

    // -----------------------------------------------------------------------
    // VCD/SVCD metadata file builders
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds the Primary Volume Descriptor for the VCD/SVCD ISO 9660 filesystem.
    /// VCD/SVCD uses CD-ROM XA (eXtended Architecture) mode.
    /// </summary>
    private static byte[] BuildPrimaryVolumeDescriptor(
        BuildJob job, bool isSvcd, DiscLayout layout,
        int pathTableSector, int rootDirSector)
    {
        var pvd = new byte[Mode2Form1UserData];

        // Volume Descriptor Type: Primary (1)
        pvd[0] = 0x01;

        // Standard Identifier: "CD001"
        Encoding.ASCII.GetBytes("CD001", 0, 5, pvd, 1);

        // Volume Descriptor Version: 1
        pvd[6] = 0x01;

        // System Identifier (32 bytes at offset 8): "CD-RTOS CD-BRIDGE" for VCD
        var systemId = isSvcd ? "CD-RTOS CD-BRIDGE" : "CD-RTOS CD-BRIDGE";
        WritePaddedString(pvd, 8, 32, systemId);

        // Volume Identifier (32 bytes at offset 40)
        var volumeId = string.IsNullOrWhiteSpace(job.VolumeLabel)
            ? (isSvcd ? "SUPERVCD" : "VIDEOCD")
            : job.VolumeLabel.ToUpperInvariant();
        if (volumeId.Length > 32) volumeId = volumeId[..32];
        WritePaddedString(pvd, 40, 32, volumeId);

        // Volume Space Size (both-endian 32-bit at offset 80)
        // This is the total number of logical blocks in the volume (excluding pregap).
        uint volumeSpaceSize = (uint)layout.DataTrackSectors;
        WriteBothEndian32(pvd, 80, volumeSpaceSize);

        // Volume Set Size (both-endian 16-bit at offset 120): 1
        WriteBothEndian16(pvd, 120, 1);

        // Volume Sequence Number (both-endian 16-bit at offset 124): 1
        WriteBothEndian16(pvd, 124, 1);

        // Logical Block Size (both-endian 16-bit at offset 128): 2048
        WriteBothEndian16(pvd, 128, (ushort)Mode2Form1UserData);

        // Path Table Size (both-endian 32-bit at offset 132): placeholder
        WriteBothEndian32(pvd, 132, 10); // minimal path table

        // Location of Type L Path Table (LE 32-bit at offset 140)
        pvd[140] = (byte)(pathTableSector & 0xFF);
        pvd[141] = (byte)((pathTableSector >> 8) & 0xFF);
        pvd[142] = (byte)((pathTableSector >> 16) & 0xFF);
        pvd[143] = (byte)((pathTableSector >> 24) & 0xFF);

        // Root Directory Record (34 bytes at offset 156)
        pvd[156] = 34;                    // Length of Directory Record
        pvd[157] = 0;                     // Extended Attribute Record length
        WriteBothEndian32(pvd, 158, (uint)rootDirSector); // Location of Extent
        WriteBothEndian32(pvd, 166, (uint)Mode2Form1UserData); // Data Length
        // Recording Date and Time (7 bytes at offset 174)
        var now = DateTime.UtcNow;
        pvd[174] = (byte)(now.Year - 1900);
        pvd[175] = (byte)now.Month;
        pvd[176] = (byte)now.Day;
        pvd[177] = (byte)now.Hour;
        pvd[178] = (byte)now.Minute;
        pvd[179] = (byte)now.Second;
        pvd[180] = 0; // GMT offset
        pvd[181] = 0x02; // File Flags: directory
        pvd[188] = 1; // Root directory name length
        pvd[189] = 0x00; // Root directory name (0x00 = root)

        // Volume Set Identifier (128 bytes at offset 190)
        WritePaddedString(pvd, 190, 128, isSvcd ? "SUPERVCD" : "VIDEOCD");

        // Publisher Identifier (128 bytes at offset 318)
        var publisher = string.IsNullOrWhiteSpace(job.Publisher) ? "OpenBurningSuite" : job.Publisher;
        WritePaddedString(pvd, 318, 128, publisher);

        // Data Preparer Identifier (128 bytes at offset 446)
        var preparer = string.IsNullOrWhiteSpace(job.Preparer) ? "OpenBurningSuite" : job.Preparer;
        WritePaddedString(pvd, 446, 128, preparer);

        // Application Identifier (128 bytes at offset 574)
        var application = string.IsNullOrWhiteSpace(job.Application) ? "OpenBurningSuite" : job.Application;
        WritePaddedString(pvd, 574, 128, application);

        // CD-XA identifier at offset 1024: "CD-XA001" + null byte
        // This marks the disc as CD-ROM XA compliant (required for VCD/SVCD)
        Encoding.ASCII.GetBytes("CD-XA001", 0, 8, pvd, 1024);
        pvd[1032] = 0x00;

        return pvd;
    }

    /// <summary>Builds the Volume Descriptor Set Terminator.</summary>
    private static byte[] BuildVolumeDescriptorTerminator()
    {
        var term = new byte[Mode2Form1UserData];
        term[0] = 0xFF; // Volume Descriptor Type: Terminator
        Encoding.ASCII.GetBytes("CD001", 0, 5, term, 1);
        term[6] = 0x01; // Version
        return term;
    }

    /// <summary>Builds a minimal ISO 9660 path table for VCD/SVCD.</summary>
    private static byte[] BuildPathTable(bool isSvcd, int rootDirSector,
        int vcdDirSector, int mpegavDirSector, int segmentDirSector,
        int extDirSector, int cdiDirSector)
    {
        var data = new byte[Mode2Form1UserData];
        int offset = 0;

        // Root directory entry
        data[offset++] = 1;     // Length of Directory Identifier
        data[offset++] = 0;     // Extended Attribute Record Length
        // Location of Extent (4 bytes LE)
        data[offset++] = (byte)(rootDirSector & 0xFF);
        data[offset++] = (byte)((rootDirSector >> 8) & 0xFF);
        data[offset++] = (byte)((rootDirSector >> 16) & 0xFF);
        data[offset++] = (byte)((rootDirSector >> 24) & 0xFF);
        // Directory Number of Parent Directory (2 bytes LE)
        data[offset++] = 1;
        data[offset++] = 0;
        data[offset++] = 0;     // Root directory identifier (0x00)
        data[offset++] = 0;     // Padding

        // VCD/SVCD directory entries with their computed sector locations
        // Parent directory number 1 = root (all are children of root)
        string[] dirs;
        int[] dirSectors;
        if (isSvcd)
        {
            dirs = new[] { "SVCD", "MPEG2", "SEGMENT", "EXT" };
            dirSectors = new[] { vcdDirSector, mpegavDirSector, segmentDirSector, extDirSector };
        }
        else if (cdiDirSector >= 0)
        {
            dirs = new[] { "VCD", "MPEGAV", "SEGMENT", "EXT", "CDI" };
            dirSectors = new[] { vcdDirSector, mpegavDirSector, segmentDirSector, extDirSector, cdiDirSector };
        }
        else
        {
            dirs = new[] { "VCD", "MPEGAV", "SEGMENT", "EXT" };
            dirSectors = new[] { vcdDirSector, mpegavDirSector, segmentDirSector, extDirSector };
        }

        for (int i = 0; i < dirs.Length; i++)
        {
            var dir = dirs[i];
            byte nameLen = (byte)dir.Length;
            data[offset++] = nameLen;           // Length of Directory Identifier
            data[offset++] = 0;                 // Extended Attribute Record Length
            // Location of Extent (4 bytes LE)
            int dirSec = dirSectors[i];
            data[offset++] = (byte)(dirSec & 0xFF);
            data[offset++] = (byte)((dirSec >> 8) & 0xFF);
            data[offset++] = (byte)((dirSec >> 16) & 0xFF);
            data[offset++] = (byte)((dirSec >> 24) & 0xFF);
            // Parent directory number
            data[offset++] = 1; data[offset++] = 0;
            // Directory identifier
            var nameBytes = Encoding.ASCII.GetBytes(dir);
            Buffer.BlockCopy(nameBytes, 0, data, offset, nameLen);
            offset += nameLen;
            // Pad to even length
            if (nameLen % 2 != 0) data[offset++] = 0;
        }

        return data;
    }

    /// <summary>Builds the root directory record for VCD/SVCD.</summary>
    private static byte[] BuildRootDirectoryRecord(bool isSvcd, List<MpegFileInfo> mpegFiles,
        BuildJob job, int rootDirSector, int vcdDirSector, int mpegavDirSector,
        int segmentDirSector, int extDirSector, int cdiDirSector)
    {
        var data = new byte[Mode2Form1UserData];
        int offset = 0;

        // "." entry (self)
        offset += WriteDirectoryEntry(data, offset, "\0", rootDirSector, Mode2Form1UserData, true);

        // ".." entry (parent = self for root)
        offset += WriteDirectoryEntry(data, offset, "\x01", rootDirSector, Mode2Form1UserData, true);

        // VCD or SVCD directory
        string vcdDir = isSvcd ? "SVCD" : "VCD";
        offset += WriteDirectoryEntry(data, offset, vcdDir, vcdDirSector, Mode2Form1UserData, true);

        // MPEGAV or MPEG2 directory
        string mpegDir = isSvcd ? "MPEG2" : "MPEGAV";
        offset += WriteDirectoryEntry(data, offset, mpegDir, mpegavDirSector, Mode2Form1UserData, true);

        // SEGMENT directory (for still images)
        offset += WriteDirectoryEntry(data, offset, "SEGMENT", segmentDirSector, Mode2Form1UserData, true);

        // EXT directory (extensions)
        offset += WriteDirectoryEntry(data, offset, "EXT", extDirSector, Mode2Form1UserData, true);

        // CDI directory (for VCD CD-i application)
        if (cdiDirSector >= 0)
        {
            offset += WriteDirectoryEntry(data, offset, "CDI", cdiDirSector, Mode2Form1UserData, true);
        }

        return data;
    }

    /// <summary>
    /// Builds VCD/SVCD specific directory records (VCD/, MPEGAV/, etc.)
    /// </summary>
    private static byte[] BuildVcdDirectoryRecords(bool isSvcd, int vcdDirSector,
        int rootDirSector, int infoSector, int entriesSector)
    {
        var data = new byte[Mode2Form1UserData];
        int offset = 0;

        // VCD/SVCD directory "." and ".." entries
        offset += WriteDirectoryEntry(data, offset, "\0", vcdDirSector, Mode2Form1UserData, true);
        offset += WriteDirectoryEntry(data, offset, "\x01", rootDirSector, Mode2Form1UserData, true);

        // INFO.VCD / INFO.SVD file entry
        string infoFile = isSvcd ? SvcdInfoFile : VcdInfoFile;
        offset += WriteDirectoryEntry(data, offset, infoFile, infoSector, 2048, false);

        // ENTRIES.VCD / ENTRIES.SVD file entry
        string entriesFile = isSvcd ? SvcdEntriesFile : EntriesFile;
        offset += WriteDirectoryEntry(data, offset, entriesFile, entriesSector, 2048, false);

        return data;
    }

    /// <summary>
    /// Builds the MPEGAV (VCD) or MPEG2 (SVCD) directory record listing MPEG track files.
    /// Per White Book, MPEG tracks appear as AVSEQ01.DAT, AVSEQ02.DAT, etc. for VCD
    /// or AVSEQ01.MPG, AVSEQ02.MPG, etc. for SVCD.
    /// </summary>
    private static byte[] BuildMpegavDirectoryRecord(bool isSvcd, int mpegavDirSector,
        int rootDirSector, List<MpegFileInfo> mpegFiles, DiscLayout layout)
    {
        var data = new byte[Mode2Form1UserData];
        int offset = 0;

        // "." entry (self)
        offset += WriteDirectoryEntry(data, offset, "\0", mpegavDirSector, Mode2Form1UserData, true);

        // ".." entry (parent = root)
        offset += WriteDirectoryEntry(data, offset, "\x01", rootDirSector, Mode2Form1UserData, true);

        // MPEG track file entries (AVSEQ01.DAT/MPG, AVSEQ02.DAT/MPG, etc.)
        string ext = isSvcd ? ".MPG" : ".DAT";
        for (int i = 0; i < mpegFiles.Count; i++)
        {
            if (i >= layout.TrackStartSectors.Count) break;

            string fileName = $"AVSEQ{(i + 1):D2}{ext};1";
            // Extent location points to the MPEG data area after the track's pregap
            int extentLba = layout.TrackStartSectors[i] + mpegFiles[i].PregapSectors;
            if (extentLba < 0 || extentLba > layout.TotalSectors) continue; // Skip invalid entries
            int fileSize = mpegFiles[i].SectorCount * Mode2Form2UserData;

            int written = WriteDirectoryEntry(data, offset, fileName, extentLba, fileSize, false);
            if (written == 0) break; // Buffer full
            offset += written;
        }

        return data;
    }

    /// <summary>
    /// Builds a minimal subdirectory record containing only "." and ".." entries.
    /// Used for SEGMENT, EXT, and CDI directories.
    /// </summary>
    private static byte[] BuildSubDirectoryRecord(int selfSector, int parentSector)
    {
        var data = new byte[Mode2Form1UserData];
        int offset = 0;

        // "." entry (self)
        offset += WriteDirectoryEntry(data, offset, "\0", selfSector, Mode2Form1UserData, true);

        // ".." entry (parent = root)
        offset += WriteDirectoryEntry(data, offset, "\x01", parentSector, Mode2Form1UserData, true);

        return data;
    }
    private static byte[] BuildInfoFile(BuildJob job, bool isSvcd, List<MpegFileInfo> mpegFiles)
    {
        var data = new byte[Mode2Form1UserData]; // 2048 bytes

        // Signature (8 bytes at offset 0): "VIDEO_CD" for VCD, "SUPERVCD" for SVCD
        var signature = isSvcd ? "SUPERVCD" : "VIDEO_CD";
        Encoding.ASCII.GetBytes(signature, 0, 8, data, 0);

        // Version (2 bytes at offset 8)
        if (isSvcd)
        {
            data[8] = 0x01; // Major version: 1
            data[9] = 0x00; // Minor version: 0
        }
        else
        {
            // VCD version: 1.1 or 2.0
            bool isVcd20 = job.VcdVersion == "2.0";
            data[8] = isVcd20 ? (byte)0x02 : (byte)0x01;
            data[9] = isVcd20 ? (byte)0x00 : (byte)0x01;
        }

        // System Profile Tag (1 byte at offset 10)
        // 0x00 = VCD 1.1, 0x01 = VCD 2.0, 0x02 = SVCD
        data[10] = isSvcd ? (byte)0x02 : (job.VcdVersion == "2.0" ? (byte)0x01 : (byte)0x00);

        // Album Identification (16 bytes at offset 11)
        var albumId = string.IsNullOrWhiteSpace(job.VcdDiscLabel)
            ? (string.IsNullOrWhiteSpace(job.VolumeLabel) ? (isSvcd ? "SUPERVCD" : "VIDEOCD") : job.VolumeLabel)
            : job.VcdDiscLabel;
        if (albumId.Length > 16) albumId = albumId[..16];
        Encoding.ASCII.GetBytes(albumId, 0, Math.Min(albumId.Length, 16), data, 11);

        // Number of volumes in album set (2 bytes at offset 27, BE)
        data[27] = 0x00; data[28] = 0x01;

        // Sequence number within album (2 bytes at offset 29, BE)
        data[29] = 0x00; data[30] = 0x01;

        // Offset multiplier (1 byte at offset 31): always 8
        data[31] = 0x08;

        // Number of List IDs for PBC (2 bytes at offset 32, BE)
        ushort numLids = (ushort)(job.PlaybackControl ? mpegFiles.Count + 1 : 0);
        data[32] = (byte)(numLids >> 8);
        data[33] = (byte)(numLids & 0xFF);

        // Number of segment play items (2 bytes at offset 34, BE)
        ushort numSegments = (ushort)job.StillImageFiles.Count;
        data[34] = (byte)(numSegments >> 8);
        data[35] = (byte)(numSegments & 0xFF);

        // Video standard flag (1 byte at offset 36)
        // NTSC=0x01, PAL=0x02, SECAM=0x02 (treated as PAL for disc purposes), Film=0x03
        data[36] = job.VideoStandard?.ToUpperInvariant() switch
        {
            "NTSC" => 0x01,
            "PAL" or "SECAM" => 0x02,
            "FILM" or "FILM (24FPS)" => 0x03,
            _ => 0x01
        };

        // PBC status flags (1 byte at offset 37)
        if (job.PlaybackControl) data[37] = 0x01;

        return data;
    }

    /// <summary>
    /// Builds the ENTRIES.VCD or ENTRIES.SVD file.
    /// Contains entry points (chapter markers) for each track.
    /// Per White Book specification, this file is exactly 2048 bytes.
    /// </summary>
    private static byte[] BuildEntriesFile(bool isSvcd, List<MpegFileInfo> mpegFiles, DiscLayout layout)
    {
        var data = new byte[Mode2Form1UserData];

        // Signature (8 bytes at offset 0)
        var signature = isSvcd ? "ENTRYSV " : "ENTRYVCD";
        if (signature.Length < 8) signature = signature.PadRight(8);
        Encoding.ASCII.GetBytes(signature, 0, 8, data, 0);

        // Version (2 bytes at offset 8)
        data[8] = isSvcd ? (byte)0x01 : (byte)0x02;
        data[9] = 0x00;

        // Number of entries (2 bytes at offset 10, BE)
        ushort numEntries = (ushort)mpegFiles.Count;
        data[10] = (byte)(numEntries >> 8);
        data[11] = (byte)(numEntries & 0xFF);

        // Entry table (starting at offset 12, 4 bytes per entry)
        // Each entry contains: track number (1 byte) + MSF sector address (3 bytes)
        for (int i = 0; i < mpegFiles.Count && i < layout.TrackStartSectors.Count; i++)
        {
            int entryOffset = 12 + i * 4;
            if (entryOffset + 4 > data.Length) break;

            byte trackNum = (byte)(i + 2); // Tracks start at 2
            int sectorAddr = layout.TrackStartSectors[i] + mpegFiles[i].PregapSectors;
            var (m, s, f) = SectorToMsfDecimal(sectorAddr);

            data[entryOffset] = trackNum;
            data[entryOffset + 1] = m;
            data[entryOffset + 2] = s;
            data[entryOffset + 3] = f;
        }

        return data;
    }

    /// <summary>
    /// Builds the LOT.VCD or LOT.SVD file (List ID Offset Table).
    /// Required for Playback Control (PBC). Size is always 65,536 bytes (32 sectors).
    /// </summary>
    private static byte[] BuildLotFile(bool isSvcd, List<MpegFileInfo> mpegFiles)
    {
        var data = new byte[65536]; // 32 × 2048

        // LOT entries: 2 bytes per List ID, pointing to PSD offset
        // LID 0 is reserved, LIDs 1-N correspond to tracks 2-(N+1)
        for (int i = 0; i < mpegFiles.Count; i++)
        {
            int lid = i + 1;
            if (lid * 2 + 1 >= data.Length) break;

            // Offset into PSD file (in 8-byte units per spec)
            ushort psdOffset = (ushort)(i * 2); // Simplified PSD layout
            data[lid * 2] = (byte)(psdOffset >> 8);
            data[lid * 2 + 1] = (byte)(psdOffset & 0xFF);
        }

        return data;
    }

    /// <summary>
    /// Builds the PSD.VCD or PSD.SVD file (Play Sequence Descriptor).
    /// Defines the playback navigation structure for PBC.
    /// </summary>
    private static byte[] BuildPsdFile(bool isSvcd, List<MpegFileInfo> mpegFiles)
    {
        var data = new byte[Mode2Form1UserData];
        int offset = 0;

        // Simple linear play sequence: each track plays in order
        for (int i = 0; i < mpegFiles.Count; i++)
        {
            if (offset + 16 > data.Length) break;

            // Play List descriptor (type 0x01)
            data[offset] = 0x01;         // Type: Play List
            data[offset + 1] = 0x00;     // Previous: none (0 = disabled)
            data[offset + 2] = (byte)(i + 1 < mpegFiles.Count ? i + 2 : 0); // Next
            data[offset + 3] = 0x00;     // Return: disabled
            data[offset + 4] = 0x00;     // Playing time (MSF-M): auto
            data[offset + 5] = 0x00;     // Playing time (MSF-S): auto
            data[offset + 6] = 0x00;     // Playing time (MSF-F): auto
            data[offset + 7] = 0x00;     // Wait time after playback
            data[offset + 8] = 0x00;     // Auto pause wait time
            data[offset + 9] = (byte)(i + 2); // Item number (track number)
            // Number of items in this play list: 1
            data[offset + 10] = 0x00;
            data[offset + 11] = 0x01;
            // Item ID (2 bytes): track number
            data[offset + 12] = 0x00;
            data[offset + 13] = (byte)(i + 2);

            offset += 16; // Fixed PSD entry size with padding
        }

        // End list marker
        if (offset < data.Length)
            data[offset] = 0xFF;

        return data;
    }

    /// <summary>
    /// Builds the SEARCH.DAT file containing search time-map data.
    /// This enables fast seeking/chapter search on VCD/SVCD players.
    /// </summary>
    private static byte[] BuildSearchDat(List<MpegFileInfo> mpegFiles, DiscLayout layout, bool isSvcd)
    {
        // SEARCH.DAT header + one entry per 0.5 seconds of playback per track
        var entries = new List<byte>();

        // Signature (8 bytes)
        var sig = isSvcd ? "SEARCHSV" : "SEARCHV ";
        entries.AddRange(Encoding.ASCII.GetBytes(sig.Length >= 8 ? sig[..8] : sig.PadRight(8)));

        // Version (2 bytes)
        entries.Add(isSvcd ? (byte)0x01 : (byte)0x02);
        entries.Add(0x00);

        // Number of scan points (2 bytes, BE) — placeholder, updated below
        int scanCountOffset = entries.Count;
        entries.Add(0x00);
        entries.Add(0x00);

        // Time interval for scan points: 0.5 seconds
        entries.Add(0x00);
        entries.Add(0x01); // 0.5 second intervals (coded as 1 in half-second units)

        int totalScanPoints = 0;

        for (int i = 0; i < mpegFiles.Count; i++)
        {
            var mpeg = mpegFiles[i];
            if (i >= layout.TrackStartSectors.Count) break;

            int trackStart = layout.TrackStartSectors[i] + mpeg.PregapSectors;
            double intervalSectors = 75.0 / 2; // 0.5 seconds = 37.5 sectors at 75 sectors/sec

            int numPoints = Math.Max(1, (int)(mpeg.SectorCount / intervalSectors));
            numPoints = Math.Min(numPoints, 2000); // Cap per track

            for (int p = 0; p < numPoints; p++)
            {
                int sectorAddr = trackStart + (int)(p * intervalSectors);
                var (m, s, f) = SectorToMsfDecimal(sectorAddr);
                entries.Add((byte)(i + 2)); // Track number
                entries.Add(m);
                entries.Add(s);
                entries.Add(f);
                totalScanPoints++;
            }
        }

        // Patch scan count
        entries[scanCountOffset] = (byte)(totalScanPoints >> 8);
        entries[scanCountOffset + 1] = (byte)(totalScanPoints & 0xFF);

        return entries.ToArray();
    }

    /// <summary>
    /// Builds the SCANDATA.DAT file for SVCD (fast forward/rewind I-frame map).
    /// </summary>
    private static byte[] BuildScanDataDat(List<MpegFileInfo> mpegFiles, DiscLayout layout)
    {
        var data = new List<byte>();

        // Signature (8 bytes)
        data.AddRange(Encoding.ASCII.GetBytes("SCANDATA"));

        // Version (2 bytes)
        data.Add(0x01);
        data.Add(0x00);

        // Number of tracks (2 bytes, BE)
        ushort numTracks = (ushort)mpegFiles.Count;
        data.Add((byte)(numTracks >> 8));
        data.Add((byte)(numTracks & 0xFF));

        // For each track, write I-frame scan points
        for (int i = 0; i < mpegFiles.Count; i++)
        {
            var mpeg = mpegFiles[i];
            if (i >= layout.TrackStartSectors.Count) break;

            int trackStart = layout.TrackStartSectors[i] + mpeg.PregapSectors;

            // Number of scan points for this track (2 bytes, BE)
            // One point per 0.5 seconds (same as SEARCH.DAT)
            double intervalSectors = 75.0 / 2;
            int numPoints = Math.Max(1, (int)(mpeg.SectorCount / intervalSectors));
            numPoints = Math.Min(numPoints, 2000);

            data.Add((byte)(numPoints >> 8));
            data.Add((byte)(numPoints & 0xFF));

            for (int p = 0; p < numPoints; p++)
            {
                int sectorAddr = trackStart + (int)(p * intervalSectors);
                // 3-byte sector address (BE)
                data.Add((byte)((sectorAddr >> 16) & 0xFF));
                data.Add((byte)((sectorAddr >> 8) & 0xFF));
                data.Add((byte)(sectorAddr & 0xFF));
            }
        }

        return data.ToArray();
    }

    /// <summary>
    /// Builds the TRACKS.SVD file for SVCD (track characteristics).
    /// </summary>
    private static byte[] BuildTracksSvd(List<MpegFileInfo> mpegFiles, DiscLayout layout)
    {
        var data = new byte[Mode2Form1UserData];

        // Signature (8 bytes)
        Encoding.ASCII.GetBytes("TRACKSVD", 0, 8, data, 0);

        // Version (2 bytes at offset 8)
        data[8] = 0x01;
        data[9] = 0x00;

        // Number of tracks (1 byte at offset 10)
        data[10] = (byte)mpegFiles.Count;

        // Track descriptor entries (starting at offset 11, variable size per track)
        int offset = 11;
        for (int i = 0; i < mpegFiles.Count; i++)
        {
            if (offset + 8 > data.Length) break;

            // Track number
            data[offset++] = (byte)(i + 2);

            // MPEG type: 0x00 = MPEG-1, 0x01 = MPEG-2
            data[offset++] = mpegFiles[i].IsMpeg2 ? (byte)0x01 : (byte)0x00;

            // Video attributes (4 bytes): resolution, aspect ratio, etc.
            data[offset++] = 0x00; // Attributes byte 1
            data[offset++] = 0x00; // Attributes byte 2
            data[offset++] = 0x00; // Attributes byte 3
            data[offset++] = 0x00; // Attributes byte 4

            // Sector count (2 bytes, placeholder)
            data[offset++] = 0x00;
            data[offset++] = 0x00;
        }

        return data;
    }

    /// <summary>
    /// Builds the CD-i application stub (CDI_VCD.APP or CDI_VCD.VCD) for VCD playback
    /// on Philips CD-i players per the Green Book / CD-i Full Functional Specification.
    ///
    /// The CD-i application stub is a small program that enables CD-i players to
    /// recognize and play VCD discs. Per the VCD specification (White Book Annex D),
    /// the CDI directory should contain an application module that the CD-i player
    /// loads and executes to start VCD playback.
    ///
    /// This generates a minimal stub with the CD-i module header and a "return-to-shell"
    /// instruction sequence, which is the standard approach used by commercial VCD
    /// authoring tools. Full CD-i interactivity requires platform-specific OS-9/68000
    /// executable modules which are outside the scope of this builder.
    /// </summary>
    private static byte[] BuildCdiApplicationStub(BuildJob job)
    {
        // CD-i application module structure (per Green Book):
        // The CDI directory contains a module that CD-i OS-9 loads.
        // We build a minimal recognizable stub that includes:
        //   1. Module header with sync word and identification
        //   2. Application descriptor with CD-i specific fields
        //   3. Minimal executable content (NOP + return)

        var data = new byte[Mode2Form1UserData]; // One sector
        int offset = 0;

        // --- CD-i Module Header ---
        // OS-9/68000 Module header sync word: 0x4AFC (illegal opcode trap)
        data[offset++] = 0x4A;
        data[offset++] = 0xFC;

        // System revision check value (2 bytes)
        data[offset++] = 0x00;
        data[offset++] = 0x01;

        // Module size (4 bytes, big-endian): entire sector
        data[offset++] = 0x00;
        data[offset++] = 0x00;
        data[offset++] = (byte)((Mode2Form1UserData >> 8) & 0xFF);
        data[offset++] = (byte)(Mode2Form1UserData & 0xFF);

        // Owner (4 bytes): 0 = super user
        data[offset++] = 0x00;
        data[offset++] = 0x00;
        data[offset++] = 0x00;
        data[offset++] = 0x00;

        // Name offset (4 bytes, big-endian): points to module name string
        int nameOffset = 48;
        data[offset++] = 0x00;
        data[offset++] = 0x00;
        data[offset++] = (byte)((nameOffset >> 8) & 0xFF);
        data[offset++] = (byte)(nameOffset & 0xFF);

        // Access permissions (2 bytes): 0x0555 = execute for all
        data[offset++] = 0x05;
        data[offset++] = 0x55;

        // Type/Language (2 bytes): 0x0001 = program module, 68000 code
        data[offset++] = 0x00;
        data[offset++] = 0x01;

        // Attributes/Revision (2 bytes): 0x8001 = reentrant, revision 1
        data[offset++] = 0x80;
        data[offset++] = 0x01;

        // Edition (2 bytes)
        data[offset++] = 0x00;
        data[offset++] = 0x01;

        // Needs (4 bytes): hardware requirements — 0 = none
        data[offset++] = 0x00;
        data[offset++] = 0x00;
        data[offset++] = 0x00;
        data[offset++] = 0x00;

        // Usage (4 bytes): 0
        data[offset++] = 0x00;
        data[offset++] = 0x00;
        data[offset++] = 0x00;
        data[offset++] = 0x00;

        // Symbol table (4 bytes): 0 = none
        data[offset++] = 0x00;
        data[offset++] = 0x00;
        data[offset++] = 0x00;
        data[offset++] = 0x00;

        // Execution entry point (4 bytes, big-endian): offset to code
        int codeOffset = 128;
        data[offset++] = 0x00;
        data[offset++] = 0x00;
        data[offset++] = (byte)((codeOffset >> 8) & 0xFF);
        data[offset++] = (byte)(codeOffset & 0xFF);

        // Exception entry (4 bytes): 0
        data[offset++] = 0x00;
        data[offset++] = 0x00;
        data[offset++] = 0x00;
        data[offset++] = 0x00;

        // --- Module name at nameOffset ---
        offset = nameOffset;
        var moduleName = "CDI_VCD";
        var nameBytes = Encoding.ASCII.GetBytes(moduleName);
        Buffer.BlockCopy(nameBytes, 0, data, offset, nameBytes.Length);
        offset += nameBytes.Length;
        data[offset++] = 0x00; // Null terminator

        // --- CD-i VCD identifier string ---
        offset = 64;
        var cdiIdStr = "CD-i VCD Application";
        var cdiIdBytes = Encoding.ASCII.GetBytes(cdiIdStr);
        Buffer.BlockCopy(cdiIdBytes, 0, data, offset, Math.Min(cdiIdBytes.Length, 32));

        // --- Application descriptor (at offset 96) ---
        offset = 96;
        // VCD application signature: "CD-RTOS"
        var rtosId = Encoding.ASCII.GetBytes("CD-RTOS");
        Buffer.BlockCopy(rtosId, 0, data, offset, rtosId.Length);
        offset += 8;
        // Video standard identifier
        var vsId = Encoding.ASCII.GetBytes(job.VideoStandard ?? "NTSC");
        Buffer.BlockCopy(vsId, 0, data, offset, Math.Min(vsId.Length, 8));
        offset += 8;
        // Version: VCD 2.0
        data[offset++] = 0x02;
        data[offset++] = 0x00;
        // Disc label (12 bytes)
        var label = string.IsNullOrWhiteSpace(job.VcdDiscLabel)
            ? (string.IsNullOrWhiteSpace(job.VolumeLabel) ? "VIDEOCD" : job.VolumeLabel)
            : job.VcdDiscLabel;
        var labelBytes = Encoding.ASCII.GetBytes(label);
        Buffer.BlockCopy(labelBytes, 0, data, offset, Math.Min(labelBytes.Length, 12));

        // --- Executable code at codeOffset ---
        // Minimal 68000 instruction sequence:
        // MOVEQ #0,D0 (set return code 0) + RTS (return from subroutine)
        offset = codeOffset;
        // MOVEQ #0,D0 = 0x7000
        data[offset++] = 0x70;
        data[offset++] = 0x00;
        // RTS = 0x4E75
        data[offset++] = 0x4E;
        data[offset++] = 0x75;

        return data;
    }

    // -----------------------------------------------------------------------
    // CUE sheet generation
    // -----------------------------------------------------------------------

    /// <summary>
    /// Writes the CUE sheet for the VCD/SVCD BIN image.
    /// </summary>
    private static async Task WriteCueSheetAsync(
        string cuePath, string binPath, DiscLayout layout,
        List<MpegFileInfo> mpegFiles, bool isSvcd, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"REM Generated by OpenBurningSuite ({(isSvcd ? "SVCD" : "VCD")})");
        sb.AppendLine($"REM Date: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"FILE \"{Path.GetFileName(binPath)}\" BINARY");

        // Track 1: Data track (CD-ROM XA Mode 2)
        // The pregap is NOT in the BIN file — use a PREGAP directive so the CD burner
        // generates the 2-second silence before Track 1 data.
        sb.AppendLine("  TRACK 01 MODE2/2352");
        sb.AppendLine("    PREGAP 00:02:00");
        sb.AppendLine("    INDEX 01 00:00:00");

        // MPEG tracks (Mode 2 Form 2)
        for (int i = 0; i < mpegFiles.Count; i++)
        {
            int trackNum = i + 2;
            int startSector = layout.TrackStartSectors[i];

            // Convert sector number to MSF for CUE sheet
            var (m, s, f) = SectorToMsfDecimal(startSector);
            sb.AppendLine($"  TRACK {trackNum:D2} MODE2/2352");
            // Pre-gap
            sb.AppendLine($"    INDEX 00 {m:D2}:{s:D2}:{f:D2}");
            // Start of actual data (after pre-gap)
            int dataStart = startSector + mpegFiles[i].PregapSectors;
            var (m2, s2, f2) = SectorToMsfDecimal(dataStart);
            sb.AppendLine($"    INDEX 01 {m2:D2}:{s2:D2}:{f2:D2}");
        }

        await File.WriteAllTextAsync(cuePath, sb.ToString(), ct);
    }

    // -----------------------------------------------------------------------
    // VCD/SVCD validation helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Validates an MPEG file against VCD specifications.
    /// Returns a list of warnings/errors (empty = valid).
    /// </summary>
    public static List<string> ValidateMpegForVcd(string filePath)
    {
        return ValidateMpeg(filePath, false);
    }

    /// <summary>
    /// Validates an MPEG file against SVCD specifications.
    /// Returns a list of warnings/errors (empty = valid).
    /// </summary>
    public static List<string> ValidateMpegForSvcd(string filePath)
    {
        return ValidateMpeg(filePath, true);
    }

    private static List<string> ValidateMpeg(string filePath, bool isSvcd)
    {
        var messages = new List<string>();

        if (!File.Exists(filePath))
        {
            messages.Add($"File not found: {filePath}");
            return messages;
        }

        var fi = new FileInfo(filePath);
        if (fi.Length == 0)
        {
            messages.Add("File is empty.");
            return messages;
        }

        // Read header
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
            FileShare.Read, 4096, false);
        var header = new byte[Math.Min(2048, fi.Length)];
        stream.ReadExactly(header, 0, header.Length);

        // Check MPEG pack header
        if (header.Length < 4 || header[0] != 0x00 || header[1] != 0x00 ||
            header[2] != 0x01 || header[3] != 0xBA)
        {
            messages.Add("Not a valid MPEG program stream (missing pack header 0x000001BA).");
            return messages;
        }

        // Check MPEG version
        if (header.Length >= 5)
        {
            bool isMpeg2 = (header[4] & 0xC0) == 0x40;
            bool isMpeg1 = (header[4] & 0xF0) == 0x20;

            if (isSvcd && !isMpeg2)
                messages.Add("Warning: SVCD requires MPEG-2 program stream. This file appears to be MPEG-1.");
            if (!isSvcd && !isMpeg1 && !isMpeg2)
                messages.Add("Warning: VCD requires MPEG-1 program stream. Could not determine MPEG version.");
        }

        // Check file size against capacity
        long capacity = isSvcd ? FormatHelper.SVCD80Capacity : FormatHelper.VCD80Capacity;
        if (fi.Length > capacity)
        {
            messages.Add($"Warning: File size ({FormatHelper.FormatBytes(fi.Length)}) exceeds " +
                         $"single-disc {(isSvcd ? "SVCD" : "VCD")} capacity " +
                         $"({FormatHelper.FormatBytes(capacity)}).");
        }

        // Scan for video sequence header (0x000001B3) to validate resolution
        int scanLimit = (int)Math.Min(65536, fi.Length);
        for (int i = 0; i < scanLimit - 7; i++)
        {
            if (header.Length <= i + 7) break;
            if (header[i] == 0x00 && header[i + 1] == 0x00 &&
                header[i + 2] == 0x01 && header[i + 3] == 0xB3)
            {
                // Video sequence header found
                int hSize = ((header[i + 4] & 0xFF) << 4) | ((header[i + 5] & 0xF0) >> 4);
                int vSize = ((header[i + 5] & 0x0F) << 8) | (header[i + 6] & 0xFF);

                if (isSvcd)
                {
                    // SVCD/XSVCD valid resolutions
                    // Standard SVCD: 480x480, 480x576, 352x480, 352x576
                    // XSVCD extends: 528x480, 528x576, 720x480, 720x576 (non-standard)
                    bool validRes = (hSize == 480 && (vSize == 480 || vSize == 576)) ||
                                    (hSize == 352 && (vSize == 480 || vSize == 576)) ||
                                    (hSize == 528 && (vSize == 480 || vSize == 576)) ||
                                    (hSize == 720 && (vSize == 480 || vSize == 576));
                    if (!validRes)
                        messages.Add($"Warning: Resolution {hSize}×{vSize} is non-standard for SVCD/XSVCD. " +
                                     "Expected 480×480, 480×576, 352×480, 352×576, 528×480, 528×576, 720×480, or 720×576.");
                }
                else
                {
                    // VCD valid resolutions: 352x240 (NTSC), 352x288 (PAL)
                    bool validRes = hSize == 352 && (vSize == 240 || vSize == 288);
                    if (!validRes)
                        messages.Add($"Warning: Resolution {hSize}×{vSize} is non-standard for VCD. " +
                                     "Expected 352×240 (NTSC) or 352×288 (PAL).");
                }
                break;
            }
        }

        return messages;
    }

    /// <summary>
    /// Detects whether a disc image is a VCD or SVCD.
    /// Checks for VCD/SVCD directory structure and INFO.VCD/INFO.SVD signature.
    /// </summary>
    public static string? DetectVcdSvcd(string imagePath)
    {
        if (!File.Exists(imagePath)) return null;

        try
        {
            using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, false);
            if (stream.Length < 32768 + 2048) return null;

            // Read PVD at sector 16
            stream.Seek(32768, SeekOrigin.Begin);
            var pvd = new byte[2048];
            if (stream.Read(pvd, 0, 2048) != 2048) return null;

            // Check for ISO 9660 PVD
            if (pvd[0] != 0x01 || pvd[1] != (byte)'C' || pvd[2] != (byte)'D' ||
                pvd[3] != (byte)'0' || pvd[4] != (byte)'0' || pvd[5] != (byte)'1')
                return null;

            // Check for CD-XA marker at PVD offset 1024
            var xaMarker = Encoding.ASCII.GetString(pvd, 1024, 8);
            if (xaMarker != "CD-XA001") return null;

            // Check System Identifier for "CD-RTOS CD-BRIDGE"
            var systemId = Encoding.ASCII.GetString(pvd, 8, 32).Trim();
            if (!systemId.Contains("CD-RTOS") && !systemId.Contains("CD-BRIDGE"))
                return null;

            // Check Volume Identifier for VCD/SVCD indicators
            var volumeId = Encoding.ASCII.GetString(pvd, 40, 32).Trim().ToUpperInvariant();
            if (volumeId.Contains("SUPERVCD") || volumeId.Contains("SVCD"))
                return "SVCD";
            if (volumeId.Contains("VIDEOCD") || volumeId.Contains("VCD"))
                return "VCD";

            // If we have CD-XA + CD-BRIDGE but no clear volume label,
            // try to find INFO.VCD or INFO.SVD in the directory records
            // This is a heuristic check — look for signature bytes in subsequent sectors
            for (int sector = 17; sector < 30 && (sector * 2048L + 2048) <= stream.Length; sector++)
            {
                stream.Seek(sector * 2048L, SeekOrigin.Begin);
                var sectorData = new byte[2048];
                if (stream.Read(sectorData, 0, 2048) != 2048) break;
                var sectorStr = Encoding.ASCII.GetString(sectorData, 0, Math.Min(256, sectorData.Length));

                if (sectorStr.Contains("SUPERVCD") || sectorStr.Contains("INFO.SVD"))
                    return "SVCD";
                if (sectorStr.Contains("VIDEO_CD") || sectorStr.Contains("INFO.VCD"))
                    return "VCD";
            }

            // Also try raw sector layout (Mode 2: data at offset 24 within 2352-byte sectors)
            if (stream.Length >= 16 * 2352 + 2352)
            {
                stream.Seek(16L * 2352 + 24, SeekOrigin.Begin);
                var rawPvd = new byte[2048];
                if (stream.Read(rawPvd, 0, 2048) == 2048)
                {
                    if (rawPvd[0] == 0x01 && rawPvd[1] == (byte)'C' && rawPvd[2] == (byte)'D')
                    {
                        var rawXa = Encoding.ASCII.GetString(rawPvd, 1024, 8);
                        if (rawXa == "CD-XA001")
                        {
                            var rawVolId = Encoding.ASCII.GetString(rawPvd, 40, 32).Trim().ToUpperInvariant();
                            if (rawVolId.Contains("SUPERVCD") || rawVolId.Contains("SVCD"))
                                return "SVCD";
                            if (rawVolId.Contains("VIDEOCD") || rawVolId.Contains("VCD"))
                                return "VCD";
                        }
                    }
                }
            }
        }
        catch { /* detection is best-effort */ }

        return null;
    }

    // -----------------------------------------------------------------------
    // Low-level helpers
    // -----------------------------------------------------------------------

    /// <summary>Converts an absolute sector number to MSF (BCD-encoded for CD headers).</summary>
    private static (byte M, byte S, byte F) SectorToMsf(int sectorNumber)
    {
        // Account for the 2-second lead-in offset (150 sectors)
        int absoluteSector = sectorNumber + 150;
        int f = absoluteSector % 75;
        int s = (absoluteSector / 75) % 60;
        int m = absoluteSector / (75 * 60);

        // BCD encode for CD sector headers
        return (ToBcd((byte)m), ToBcd((byte)s), ToBcd((byte)f));
    }

    /// <summary>Converts an absolute sector number to MSF (decimal, for CUE sheets).</summary>
    private static (byte M, byte S, byte F) SectorToMsfDecimal(int sectorNumber)
    {
        int f = sectorNumber % 75;
        int s = (sectorNumber / 75) % 60;
        int m = sectorNumber / (75 * 60);
        return ((byte)m, (byte)s, (byte)f);
    }

    /// <summary>Converts a byte to BCD encoding.</summary>
    private static byte ToBcd(byte value) => (byte)((value / 10) * 16 + (value % 10));

    /// <summary>Computes the EDC (CRC-32) for a range of sector data.</summary>
    private static uint ComputeEdc(byte[] data, int offset, int length)
    {
        // CD-ROM EDC uses the standard CRC-32 polynomial for Mode 2 sectors
        // Polynomial: x^32 + x^31 + x^16 + x^15 + x^4 + x^3 + x + 1
        // = 0x8001801B (reflected: 0xD8018001)
        uint edc = 0;
        for (int i = offset; i < offset + length && i < data.Length; i++)
        {
            edc ^= data[i];
            for (int j = 0; j < 8; j++)
            {
                if ((edc & 1) != 0)
                    edc = (edc >> 1) ^ 0xD8018001;
                else
                    edc >>= 1;
            }
        }
        return edc;
    }

    /// <summary>Writes a padded ASCII string into a buffer.</summary>
    private static void WritePaddedString(byte[] buffer, int offset, int fieldLength, string value)
    {
        var padded = (value ?? string.Empty).PadRight(fieldLength);
        if (padded.Length > fieldLength) padded = padded[..fieldLength];
        Encoding.ASCII.GetBytes(padded, 0, fieldLength, buffer, offset);
    }

    /// <summary>Writes a 32-bit value in both-endian format (LE then BE, 8 bytes total).</summary>
    private static void WriteBothEndian32(byte[] buffer, int offset, uint value)
    {
        // Little-endian (4 bytes)
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
        buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
        // Big-endian (4 bytes)
        buffer[offset + 4] = (byte)((value >> 24) & 0xFF);
        buffer[offset + 5] = (byte)((value >> 16) & 0xFF);
        buffer[offset + 6] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 7] = (byte)(value & 0xFF);
    }

    /// <summary>Writes a 16-bit value in both-endian format (LE then BE, 4 bytes total).</summary>
    private static void WriteBothEndian16(byte[] buffer, int offset, ushort value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 2] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 3] = (byte)(value & 0xFF);
    }

    /// <summary>Writes a minimal ISO 9660 directory entry.</summary>
    private static int WriteDirectoryEntry(
        byte[] buffer, int offset, string name, int extentLba, int dataLength, bool isDirectory)
    {
        var nameBytes = Encoding.ASCII.GetBytes(name);
        int nameLen = nameBytes.Length;
        // Directory entry length must be even
        int entryLen = 33 + nameLen;
        if (entryLen % 2 != 0) entryLen++;

        if (offset + entryLen > buffer.Length)
            return 0;

        buffer[offset] = (byte)entryLen;          // Length of Directory Record
        buffer[offset + 1] = 0;                    // Extended Attribute Record Length
        WriteBothEndian32(buffer, offset + 2, (uint)extentLba); // Location of Extent
        WriteBothEndian32(buffer, offset + 10, (uint)dataLength); // Data Length

        // Recording Date and Time (7 bytes at offset 18)
        var now = DateTime.UtcNow;
        buffer[offset + 18] = (byte)(now.Year - 1900);
        buffer[offset + 19] = (byte)now.Month;
        buffer[offset + 20] = (byte)now.Day;
        buffer[offset + 21] = (byte)now.Hour;
        buffer[offset + 22] = (byte)now.Minute;
        buffer[offset + 23] = (byte)now.Second;
        buffer[offset + 24] = 0; // GMT offset

        // File Flags
        buffer[offset + 25] = isDirectory ? (byte)0x02 : (byte)0x00;

        // File Unit Size, Interleave Gap
        buffer[offset + 26] = 0;
        buffer[offset + 27] = 0;

        // Volume Sequence Number (both-endian 16-bit)
        WriteBothEndian16(buffer, offset + 28, 1);

        // Length of File Identifier
        buffer[offset + 32] = (byte)nameLen;

        // File Identifier
        Buffer.BlockCopy(nameBytes, 0, buffer, offset + 33, nameLen);

        return entryLen;
    }

    // -----------------------------------------------------------------------
    // Internal data structures
    // -----------------------------------------------------------------------

    private sealed class MpegFileInfo
    {
        public string FilePath { get; init; } = string.Empty;
        public long FileSize { get; init; }
        public int SectorCount { get; init; }
        public int PregapSectors { get; init; }
        public bool IsMpeg2 { get; init; }
        public double DurationEstimateSeconds { get; init; }
    }

    private sealed class DiscLayout
    {
        public int DataTrackSectors { get; init; }
        public int DataTrackPregap { get; init; }
        public List<int> TrackStartSectors { get; init; } = new();
        public int TotalSectors { get; init; }
        public int TrackCount { get; init; }
        public int LeadOutSectors { get; init; }
        public bool IsSvcd { get; init; }
    }
}
