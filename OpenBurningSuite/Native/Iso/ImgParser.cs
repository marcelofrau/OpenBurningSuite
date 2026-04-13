// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenBurningSuite.Models;

namespace OpenBurningSuite.Native.Iso;

/// <summary>
/// Parser for raw disc image files (.img).
///
/// IMG files are headerless raw sector dumps without embedded metadata.
/// The parser detects the sector format by:
/// 1. Checking file size divisibility against known sector sizes
/// 2. Examining the CD sync pattern and mode byte in raw sectors
/// 3. Looking for companion descriptor files (.ccd, .cue, .sub)
///
/// Supported sector formats:
/// - 2048 bytes: Cooked ISO 9660 (Mode 1 user data only)
/// - 2336 bytes: Mode 2 (no sync/header, includes subheader)
/// - 2352 bytes: Raw sectors (full sector with sync, header, ECC/EDC)
/// - 2448 bytes: Raw sectors with 96-byte interleaved subchannel data
/// - 2056 bytes: Mode 2 Form 1 (2048 user data + 8-byte subheader)
/// </summary>
public static class ImgParser
{
    // Standard CD sync pattern (12 bytes at start of each raw sector)
    private static readonly byte[] CdSyncPattern =
    {
        0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        0xFF, 0xFF, 0xFF, 0x00
    };

    // Known sector sizes in order of preference for detection
    private static readonly int[] KnownSectorSizes = { 2352, 2448, 2336, 2048, 2056 };

    /// <summary>Parses a raw IMG image file synchronously.</summary>
    public static ImgImage Parse(string imgFilePath)
    {
        var image = new ImgImage { FilePath = imgFilePath };
        try
        {
            if (!File.Exists(imgFilePath))
            {
                image.ErrorMessage = $"File not found: {imgFilePath}";
                return image;
            }

            image.FileSize = new FileInfo(imgFilePath).Length;
            if (image.FileSize == 0)
            {
                image.ErrorMessage = "File is empty.";
                return image;
            }

            // Detect companion files
            DetectCompanionFiles(imgFilePath, image);

            // Detect sector format
            using var stream = new FileStream(imgFilePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 8192, false);
            DetectSectorFormat(stream, image);

            // Build track list
            BuildTracks(image);

            image.IsValid = image.Tracks.Count > 0;
            if (!image.IsValid && string.IsNullOrEmpty(image.ErrorMessage))
                image.ErrorMessage = "Could not detect sector format.";
        }
        catch (Exception ex)
        {
            image.IsValid = false;
            image.ErrorMessage = $"Parse error: {ex.Message}";
        }
        return image;
    }

    /// <summary>Parses a raw IMG image file asynchronously.</summary>
    public static async Task<ImgImage> ParseAsync(string imgFilePath, CancellationToken ct = default)
    {
        return await Task.Run(() => Parse(imgFilePath), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Extracts user data from the largest data track in an IMG image to an ISO file.
    /// For cooked 2048-byte sectors, this is essentially a file copy.
    /// For raw sectors, user data is extracted from each sector.
    /// </summary>
    public static void ExtractToIso(string imgFilePath, ImgImage image, string outputIsoPath,
        IProgress<int>? progress = null)
    {
        if (!image.IsValid)
            throw new InvalidOperationException($"IMG image is not valid: {image.ErrorMessage}");

        var track = image.LargestDataTrack ?? image.Tracks.FirstOrDefault();
        if (track == null)
            throw new InvalidOperationException("No tracks found in IMG image.");

        using var input = new FileStream(imgFilePath, FileMode.Open, FileAccess.Read,
            FileShare.Read, 65536, false);
        using var output = new FileStream(outputIsoPath, FileMode.Create, FileAccess.Write,
            FileShare.None, 65536, false);

        ExtractTrackUserData(input, output, track, progress);
    }

    /// <summary>Asynchronously extracts user data to an ISO file.</summary>
    public static async Task ExtractToIsoAsync(string imgFilePath, ImgImage image,
        string outputIsoPath, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        await Task.Run(() => ExtractToIso(imgFilePath, image, outputIsoPath, progress), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Converts an IMG image to BIN/CUE format.
    /// For raw (2352/2448) IMG files, the data is copied as-is with a generated CUE sheet.
    /// For cooked (2048) IMG files, the data is wrapped in raw sectors.
    /// </summary>
    public static void ConvertToBinCue(string imgFilePath, ImgImage image,
        string outputBinPath, string outputCuePath, IProgress<int>? progress = null)
    {
        if (!image.IsValid)
            throw new InvalidOperationException($"IMG image is not valid: {image.ErrorMessage}");

        using var input = new FileStream(imgFilePath, FileMode.Open, FileAccess.Read,
            FileShare.Read, 65536, false);
        using var output = new FileStream(outputBinPath, FileMode.Create, FileAccess.Write,
            FileShare.None, 65536, false);

        var cueLines = new List<string>();
        cueLines.Add($"FILE \"{Path.GetFileName(outputBinPath)}\" BINARY");

        long totalSectors = image.TotalSectorCount;
        long processedSectors = 0;

        foreach (var track in image.Tracks)
        {
            int outSectorSize = track.IsRawSector ? track.BaseSectorSize : track.SectorSize;
            string modeStr;
            if (track.IsAudio)
                modeStr = "AUDIO";
            else if (track.IsRawSector)
                modeStr = track.Mode == ImgTrackMode.Mode2Form1 ? "MODE2/2352" : "MODE1/2352";
            else
                modeStr = track.SectorSize == 2336 ? "MODE2/2336" : $"MODE1/{track.SectorSize}";

            cueLines.Add($"  TRACK {track.TrackNumber:D2} {modeStr}");

            long binOffset = output.Position;
            long idx01Frame = outSectorSize > 0 ? binOffset / outSectorSize : 0;
            cueLines.Add($"    INDEX 01 {SectorsToMsf(idx01Frame)}");

            input.Seek(track.FileOffset, SeekOrigin.Begin);
            var buf = new byte[track.SectorSize];
            long sectors = track.SectorCount;

            for (long s = 0; s < sectors; s++)
            {
                int bytesRead = ReadExact(input, buf, 0, track.SectorSize);
                if (bytesRead < track.SectorSize) break;

                // Strip subchannel data if present
                output.Write(buf, 0, track.BaseSectorSize);

                processedSectors++;
                if (progress != null && totalSectors > 0 && processedSectors % 500 == 0)
                    progress.Report((int)(processedSectors * 100 / totalSectors));
            }
        }

        progress?.Report(100);
        File.WriteAllText(outputCuePath,
            string.Join(Environment.NewLine, cueLines) + Environment.NewLine);
    }

    /// <summary>Asynchronously converts to BIN/CUE format.</summary>
    public static async Task ConvertToBinCueAsync(string imgFilePath, ImgImage image,
        string outputBinPath, string outputCuePath,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        await Task.Run(() => ConvertToBinCue(imgFilePath, image, outputBinPath, outputCuePath, progress), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Generates a CUE sheet string for direct burning of a raw IMG file.
    /// Used when the IMG file contains raw 2352-byte sectors (identical to BIN format)
    /// and can be used directly as the burn source without copying.
    /// </summary>
    /// <param name="image">Parsed IMG image structure.</param>
    /// <param name="dataFileName">IMG file name to reference in the CUE FILE directive.</param>
    /// <returns>CUE sheet content as a string.</returns>
    public static string GenerateCueSheetForFile(ImgImage image, string dataFileName)
    {
        ArgumentNullException.ThrowIfNull(image);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("REM Generated by OpenBurningSuite for direct IMG burning");
        sb.AppendLine($"FILE \"{dataFileName}\" BINARY");

        foreach (var track in image.Tracks)
        {
            string modeStr = track.IsData
                ? $"MODE1/{track.SectorSize}"
                : "AUDIO";
            sb.AppendLine($"  TRACK {track.TrackNumber:D2} {modeStr}");
            sb.AppendLine($"    INDEX 01 {FormatMsf(track.StartLba)}");
        }

        return sb.ToString();
    }

    /// <summary>Formats an LBA value as MSF (mm:ss:ff) string.</summary>
    private static string FormatMsf(long lba)
    {
        long absFr = Math.Max(lba, 0);
        int f = (int)(absFr % 75);
        int s = (int)((absFr / 75) % 60);
        int m = (int)Math.Min(absFr / 75 / 60, 99);
        return $"{m:D2}:{s:D2}:{f:D2}";
    }

    /// <summary>Extracts all data tracks to an output stream.</summary>
    public static void ExtractAllDataTracks(string imgFilePath, ImgImage image,
        Stream outputStream, IProgress<int>? progress = null)
    {
        if (!image.IsValid)
            throw new InvalidOperationException($"IMG image is not valid: {image.ErrorMessage}");

        using var input = new FileStream(imgFilePath, FileMode.Open, FileAccess.Read,
            FileShare.Read, 65536, false);

        var dataTracks = image.Tracks.Where(t => t.IsData).ToList();
        for (int i = 0; i < dataTracks.Count; i++)
        {
            var subProgress = progress != null
                ? new Progress<int>(p => progress.Report((i * 100 + p) / dataTracks.Count))
                : null;
            ExtractTrackUserData(input, outputStream, dataTracks[i], subProgress);
        }
    }

    /// <summary>Asynchronously extracts all data tracks.</summary>
    public static async Task ExtractAllDataTracksAsync(string imgFilePath, ImgImage image,
        Stream outputStream, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        await Task.Run(() => ExtractAllDataTracks(imgFilePath, image, outputStream, progress), ct)
            .ConfigureAwait(false);
    }

    /// <summary>Validates an IMG image for integrity.</summary>
    public static List<string> ValidateImage(ImgImage image)
    {
        var issues = new List<string>();
        if (!image.IsValid)
        {
            issues.Add($"Image parse error: {image.ErrorMessage}");
            return issues;
        }

        if (image.TrackCount == 0)
            issues.Add("No tracks detected.");

        foreach (var track in image.Tracks)
        {
            if (track.SectorSize <= 0)
                issues.Add($"Track {track.TrackNumber}: invalid sector size {track.SectorSize}");
            if (track.DataLength <= 0)
                issues.Add($"Track {track.TrackNumber}: empty track");
            if (track.FileOffset + track.DataLength > image.FileSize)
                issues.Add($"Track {track.TrackNumber}: extends beyond file end");
            if (track.DataLength % track.SectorSize != 0)
                issues.Add($"Track {track.TrackNumber}: size {track.DataLength} not a multiple of sector size {track.SectorSize}");
        }

        // Verify ISO 9660 signature for data tracks
        if (image.FirstDataTrack != null && image.FirstDataTrack.SectorSize > 0 &&
            File.Exists(image.FilePath))
        {
            try
            {
                using var fs = new FileStream(image.FilePath, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 4096, false);
                var track = image.FirstDataTrack;
                // ISO 9660 PVD is at sector 16 (LBA 16)
                long pvdOffset = track.FileOffset + (16L * track.SectorSize) + track.UserDataOffset;
                if (pvdOffset + 6 <= image.FileSize)
                {
                    fs.Seek(pvdOffset, SeekOrigin.Begin);
                    var pvdBuf = new byte[6];
                    ReadExact(fs, pvdBuf, 0, 6);
                    // Check for "CD001" at offset 1
                    if (pvdBuf[0] == 1 && pvdBuf[1] == 0x43 && pvdBuf[2] == 0x44 &&
                        pvdBuf[3] == 0x30 && pvdBuf[4] == 0x30 && pvdBuf[5] == 0x31)
                    {
                        // Valid ISO 9660 PVD found
                    }
                    else
                    {
                        issues.Add("ISO 9660 Primary Volume Descriptor not found at sector 16 — " +
                            "image may be a non-ISO format or the sector format detection may be incorrect.");
                    }
                }
            }
            catch { /* Best-effort validation */ }
        }

        return issues;
    }

    /// <summary>Asynchronously validates an IMG image.</summary>
    public static async Task<List<string>> ValidateImageAsync(ImgImage image,
        CancellationToken ct = default)
    {
        return await Task.Run(() => ValidateImage(image), ct).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------
    //  Internal detection logic
    // -----------------------------------------------------------------------

    private static void DetectCompanionFiles(string imgFilePath, ImgImage image)
    {
        var dir = Path.GetDirectoryName(imgFilePath) ?? ".";
        var baseName = Path.GetFileNameWithoutExtension(imgFilePath);

        // Search for companion files with case-insensitive matching
        var opts = new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive };

        // .ccd companion
        var ccdFile = Directory.EnumerateFiles(dir, baseName + ".ccd", opts).FirstOrDefault();
        if (ccdFile != null)
        {
            image.HasCcdCompanion = true;
            image.CcdFilePath = ccdFile;
        }

        // .cue companion
        var cueFile = Directory.EnumerateFiles(dir, baseName + ".cue", opts).FirstOrDefault();
        if (cueFile != null)
        {
            image.HasCueCompanion = true;
            image.CueFilePath = cueFile;
        }

        // .sub companion
        var subFile = Directory.EnumerateFiles(dir, baseName + ".sub", opts).FirstOrDefault();
        if (subFile != null)
        {
            image.HasSubFile = true;
            image.SubFilePath = subFile;
        }
    }

    private static void DetectSectorFormat(FileStream stream, ImgImage image)
    {
        // If there's a CCD companion, it's always 2352-byte raw sectors
        if (image.HasCcdCompanion)
        {
            image.SectorFormat = ImgSectorFormat.Raw2352;
            return;
        }

        // Try to detect by examining sector data for sync pattern
        if (image.FileSize >= 2448)
        {
            var buf = new byte[2448];
            stream.Seek(0, SeekOrigin.Begin);
            ReadExact(stream, buf, 0, Math.Min(2448, (int)Math.Min(image.FileSize, 2448)));

            // Check for CD sync pattern at offset 0
            if (HasSyncPattern(buf, 0))
            {
                // Raw sector detected — determine if subchannel data follows
                if (image.FileSize % 2448 == 0 && image.FileSize % 2352 != 0)
                    image.SectorFormat = ImgSectorFormat.RawSub2448;
                else if (image.FileSize % 2352 == 0)
                    image.SectorFormat = ImgSectorFormat.Raw2352;
                else if (image.FileSize % 2448 == 0)
                    image.SectorFormat = ImgSectorFormat.RawSub2448;
                else
                    image.SectorFormat = ImgSectorFormat.Raw2352; // fallback

                return;
            }
        }

        // No sync pattern — try divisibility
        foreach (int size in KnownSectorSizes)
        {
            if (size > 0 && image.FileSize % size == 0)
            {
                image.SectorFormat = (ImgSectorFormat)size;

                // Verify with a second heuristic: check if sector 16 has ISO PVD
                if (size == 2048)
                {
                    long pvdOffset = 16L * 2048;
                    if (pvdOffset + 6 <= image.FileSize)
                    {
                        stream.Seek(pvdOffset, SeekOrigin.Begin);
                        var pvdBuf = new byte[6];
                        ReadExact(stream, pvdBuf, 0, 6);
                        if (pvdBuf[0] == 1 && pvdBuf[1] == 0x43 && pvdBuf[2] == 0x44 &&
                            pvdBuf[3] == 0x30 && pvdBuf[4] == 0x30 && pvdBuf[5] == 0x31)
                        {
                            // Confirmed ISO 9660
                            image.SectorFormat = ImgSectorFormat.Cooked2048;
                            return;
                        }
                    }
                }
                return;
            }
        }

        // Last resort: default to 2048 (most common)
        image.SectorFormat = ImgSectorFormat.Cooked2048;
    }

    /// <summary>Detects the track mode by examining raw sector header bytes.</summary>
    private static ImgTrackMode DetectTrackMode(FileStream stream, long offset, int sectorSize)
    {
        if (sectorSize < 2352)
        {
            return sectorSize switch
            {
                2048 => ImgTrackMode.Mode1,
                2336 => ImgTrackMode.Mode2,
                2056 => ImgTrackMode.Mode2Form1,
                _ => ImgTrackMode.Mode1
            };
        }

        // Read first raw sector to check mode byte
        var buf = new byte[Math.Min(sectorSize, 2352)];
        stream.Seek(offset, SeekOrigin.Begin);
        int read = ReadExact(stream, buf, 0, buf.Length);
        if (read < 16)
            return ImgTrackMode.Unknown;

        // Check for sync pattern
        if (!HasSyncPattern(buf, 0))
        {
            // No sync = likely audio
            return ImgTrackMode.Audio;
        }

        // Mode byte is at offset 15 in a raw sector (after 12-byte sync + 3-byte address)
        byte modeByte = buf[15];
        return modeByte switch
        {
            1 => ImgTrackMode.Mode1,
            2 => DetectMode2SubType(buf),
            _ => ImgTrackMode.Unknown
        };
    }

    /// <summary>
    /// Detects whether a Mode 2 sector is Form 1 or Form 2 by checking
    /// the subheader flags byte at offset 18.
    /// </summary>
    private static ImgTrackMode DetectMode2SubType(byte[] sectorBuf)
    {
        if (sectorBuf.Length < 24)
            return ImgTrackMode.Mode2;

        // Subheader byte at offset 18 (submode/file number) and 20 (submode)
        // Mode 2 Form 1: bit 5 of submode (byte 18) is clear
        // Mode 2 Form 2: bit 5 of submode (byte 18) is set
        byte submode = sectorBuf[18];
        if ((submode & 0x20) != 0)
            return ImgTrackMode.Mode2Form2;
        return ImgTrackMode.Mode2Form1;
    }

    private static void BuildTracks(ImgImage image)
    {
        if (image.SectorFormat == ImgSectorFormat.Unknown)
            return;

        int sectorSize = (int)image.SectorFormat;
        if (sectorSize <= 0) return;

        // For standalone IMG files, treat the entire file as a single track
        long sectorCount = image.FileSize / sectorSize;
        if (sectorCount <= 0) return;

        // Detect mode from first sector
        ImgTrackMode mode;
        if (File.Exists(image.FilePath))
        {
            using var stream = new FileStream(image.FilePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, false);
            mode = DetectTrackMode(stream, 0, sectorSize);
        }
        else
        {
            mode = sectorSize >= 2352 ? ImgTrackMode.Mode1 : ImgTrackMode.Mode1;
        }

        image.Tracks.Add(new ImgTrack
        {
            TrackNumber = 1,
            SectorFormat = image.SectorFormat,
            Mode = mode,
            FileOffset = 0,
            DataLength = sectorCount * sectorSize,
            StartLba = 0
        });
    }

    // -----------------------------------------------------------------------
    //  Extraction helpers
    // -----------------------------------------------------------------------

    private static void ExtractTrackUserData(Stream input, Stream output,
        ImgTrack track, IProgress<int>? progress)
    {
        input.Seek(track.FileOffset, SeekOrigin.Begin);

        long sectorCount = track.SectorCount;
        int sectorSize = track.SectorSize;
        int userDataSize = track.UserDataSize;
        int userDataOffset = track.UserDataOffset;

        var buf = new byte[sectorSize];

        for (long s = 0; s < sectorCount; s++)
        {
            int bytesRead = ReadExact(input, buf, 0, sectorSize);
            if (bytesRead < sectorSize) break;

            if (track.IsRawSector && !track.IsAudio)
            {
                // Extract user data from raw sector
                output.Write(buf, userDataOffset, userDataSize);
            }
            else
            {
                // Cooked or audio: write as-is
                output.Write(buf, 0, userDataSize);
            }

            if (progress != null && sectorCount > 0 && s % 1000 == 0)
                progress.Report((int)(s * 100 / sectorCount));
        }
        progress?.Report(100);
    }

    // -----------------------------------------------------------------------
    //  Helpers
    // -----------------------------------------------------------------------

    private static bool HasSyncPattern(byte[] buffer, int offset)
    {
        if (buffer.Length < offset + 12) return false;
        for (int i = 0; i < 12; i++)
        {
            if (buffer[offset + i] != CdSyncPattern[i])
                return false;
        }
        return true;
    }

    private static string SectorsToMsf(long sectors)
    {
        if (sectors < 0) sectors = 0;
        int frames = (int)(sectors % 75);
        int seconds = (int)((sectors / 75) % 60);
        int minutes = (int)(sectors / 75 / 60);
        return $"{minutes:D2}:{seconds:D2}:{frames:D2}";
    }

    private static int ReadExact(Stream stream, byte[] buffer, int offset, int count)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = stream.Read(buffer, offset + totalRead, count - totalRead);
            if (read == 0) break;
            totalRead += read;
        }
        return totalRead;
    }
}
