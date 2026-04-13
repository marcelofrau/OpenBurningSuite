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

namespace OpenBurningSuite.Native.Iso;

/// <summary>
/// Parser for DiscJuggler CDI disc image files.
/// Supports CDI versions 2.0, 3.0, 3.5, and 4.0.
/// The CDI format is reverse-engineered from DiscJuggler software; this
/// implementation uses fault-tolerant heuristics and marker scanning to
/// handle version-specific layout variations.
/// </summary>
public static class CdiParser
{
    // CDI version magic constants (stored in the file trailer).
    private const uint MagicVersion2 = 0x80000004;
    private const uint MagicVersion3 = 0x80000005;
    private const uint MagicVersion35 = 0x80000006;
    private const uint MagicVersion4 = 0x80000007;

    // Marker written before the file-position field in every track descriptor.
    private const uint StartMark = 0x80000000;

    // Minimum file size: at least a 12-byte trailer + some content.
    private const int MinFileSize = 16;

    // Known valid sector sizes (includes subchannel variants).
    private static readonly HashSet<int> ValidSectorSizes = new() { 2048, 2336, 2352, 2368, 2448 };

    // CD-DA constant: 75 frames per second.
    private const int FramesPerSecond = 75;

    // Standard CD-ROM multi-session gap: lead-out (6750) + lead-in (4650) = 11400 sectors.
    private const int MultiSessionGap = 11400;

    // -------------------------------------------------------------------
    //  Public API — Synchronous
    // -------------------------------------------------------------------

    /// <summary>
    /// Parses a CDI image file from disk.
    /// </summary>
    /// <param name="filePath">Full path to the .cdi file.</param>
    /// <returns>
    /// A <see cref="CdiImage"/> instance. Check <see cref="CdiImage.IsValid"/>
    /// and <see cref="CdiImage.ErrorMessage"/> if parsing fails.
    /// </returns>
    public static CdiImage Parse(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return ErrorImage(string.Empty, 0, "File path is null or empty.");

        if (!File.Exists(filePath))
            return ErrorImage(filePath, 0, $"File not found: {filePath}");

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Parse(stream, filePath);
        }
        catch (Exception ex)
        {
            return ErrorImage(filePath, 0, $"Failed to open file: {ex.Message}");
        }
    }

    /// <summary>
    /// Parses a CDI image from an existing seekable stream.
    /// </summary>
    /// <param name="stream">A readable, seekable stream containing CDI data.</param>
    /// <param name="filePath">Logical file path stored in the result for reference.</param>
    /// <returns>
    /// A <see cref="CdiImage"/> instance. Check <see cref="CdiImage.IsValid"/>
    /// and <see cref="CdiImage.ErrorMessage"/> if parsing fails.
    /// </returns>
    public static CdiImage Parse(Stream stream, string filePath)
    {
        if (stream == null)
            return ErrorImage(filePath, 0, "Stream is null.");
        if (!stream.CanRead || !stream.CanSeek)
            return ErrorImage(filePath, 0, "Stream must be readable and seekable.");

        long fileSize = stream.Length;
        if (fileSize < MinFileSize)
            return ErrorImage(filePath, fileSize, "File is too small to be a valid CDI image.");

        try
        {
            var (version, headerOffset) = ReadTrailer(stream);

            if (version == CdiVersion.Unknown)
                return ErrorImage(filePath, fileSize, "Unrecognized CDI version magic in trailer.");

            if (headerOffset == 0 || headerOffset >= (uint)fileSize)
                return ErrorImage(filePath, fileSize,
                    $"Invalid header offset {headerOffset} (file size {fileSize}).");

            stream.Seek(headerOffset, SeekOrigin.Begin);
            using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

            var sessions = ParseSessions(reader, version, fileSize);

            if (sessions.Count == 0)
                return ErrorImage(filePath, fileSize, "No sessions found in CDI header.");

            CalculateStartLbas(sessions);

            return new CdiImage
            {
                Version = version,
                FilePath = filePath,
                FileSize = fileSize,
                Sessions = sessions,
                IsValid = true
            };
        }
        catch (Exception ex)
        {
            return ErrorImage(filePath, fileSize, $"Parse error: {ex.Message}");
        }
    }

    /// <summary>
    /// Extracts the raw sector data for a track to the given output stream.
    /// Copies <see cref="CdiTrack.TotalLength"/> bytes starting at
    /// <see cref="CdiTrack.FileOffset"/>.
    /// </summary>
    /// <param name="cdiFilePath">Path to the CDI file.</param>
    /// <param name="track">Track descriptor obtained from a parsed <see cref="CdiImage"/>.</param>
    /// <param name="outputStream">Writable destination stream.</param>
    /// <exception cref="ArgumentNullException"/>
    /// <exception cref="IOException"/>
    public static void ExtractTrackData(string cdiFilePath, CdiTrack track, Stream outputStream)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(outputStream);

        using var fs = new FileStream(cdiFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (track.FileOffset + track.TotalLength > fs.Length)
            throw new IOException(
                $"Track data extends beyond file (offset {track.FileOffset}, length {track.TotalLength}, file size {fs.Length}).");

        fs.Seek(track.FileOffset, SeekOrigin.Begin);
        CopyBytes(fs, outputStream, track.TotalLength);
    }

    /// <summary>
    /// Extracts only the user data portion of each sector for a track,
    /// stripping sync bytes, headers, and ECC/EDC from raw sectors.
    /// For cooked (non-raw) sectors the entire sector is copied.
    /// </summary>
    /// <param name="cdiFilePath">Path to the CDI file.</param>
    /// <param name="track">Track descriptor.</param>
    /// <param name="outputStream">Writable destination stream.</param>
    public static void ExtractUserData(string cdiFilePath, CdiTrack track, Stream outputStream)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(outputStream);

        using var fs = new FileStream(cdiFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (track.FileOffset + track.TotalLength > fs.Length)
            throw new IOException("Track data extends beyond file bounds.");

        fs.Seek(track.FileOffset, SeekOrigin.Begin);

        if (!track.IsRawSector)
        {
            // Cooked sectors — copy everything.
            CopyBytes(fs, outputStream, track.TotalLength);
            return;
        }

        // Raw sectors — strip per-sector overhead.
        int sectorSize = track.SectorSize;
        int userOffset = track.UserDataOffset;
        int userData = track.UserDataSize;
        byte[] sectorBuf = new byte[sectorSize];

        long remaining = track.TotalLength;
        while (remaining >= sectorSize)
        {
            int read = ReadFull(fs, sectorBuf, 0, sectorSize);
            if (read < sectorSize) break;
            outputStream.Write(sectorBuf, userOffset, userData);
            remaining -= sectorSize;
        }
    }

    /// <summary>
    /// Extracts the raw sector data for a track with progress reporting.
    /// </summary>
    /// <param name="cdiFilePath">Path to the CDI file.</param>
    /// <param name="track">Track descriptor.</param>
    /// <param name="outputStream">Writable destination stream.</param>
    /// <param name="progress">Optional progress reporter (0–100).</param>
    public static void ExtractTrackData(
        string cdiFilePath, CdiTrack track, Stream outputStream, IProgress<int>? progress)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(outputStream);

        using var fs = new FileStream(cdiFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (track.FileOffset + track.TotalLength > fs.Length)
            throw new IOException(
                $"Track data extends beyond file (offset {track.FileOffset}, length {track.TotalLength}, file size {fs.Length}).");

        fs.Seek(track.FileOffset, SeekOrigin.Begin);
        CopyBytesWithProgress(fs, outputStream, track.TotalLength, progress);
    }

    /// <summary>
    /// Extracts user data for a track with progress reporting.
    /// </summary>
    /// <param name="cdiFilePath">Path to the CDI file.</param>
    /// <param name="track">Track descriptor.</param>
    /// <param name="outputStream">Writable destination stream.</param>
    /// <param name="progress">Optional progress reporter (0–100).</param>
    public static void ExtractUserData(
        string cdiFilePath, CdiTrack track, Stream outputStream, IProgress<int>? progress)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(outputStream);

        using var fs = new FileStream(cdiFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (track.FileOffset + track.TotalLength > fs.Length)
            throw new IOException("Track data extends beyond file bounds.");

        fs.Seek(track.FileOffset, SeekOrigin.Begin);

        if (!track.IsRawSector)
        {
            CopyBytesWithProgress(fs, outputStream, track.TotalLength, progress);
            return;
        }

        int sectorSize = track.SectorSize;
        int userOffset = track.UserDataOffset;
        int userData = track.UserDataSize;
        byte[] sectorBuf = new byte[sectorSize];
        long totalSectors = track.TotalLength / sectorSize;
        long sectorsWritten = 0;

        long remaining = track.TotalLength;
        while (remaining >= sectorSize)
        {
            int read = ReadFull(fs, sectorBuf, 0, sectorSize);
            if (read < sectorSize) break;
            outputStream.Write(sectorBuf, userOffset, userData);
            remaining -= sectorSize;
            sectorsWritten++;
            if (totalSectors > 0)
                progress?.Report((int)(sectorsWritten * 100 / totalSectors));
        }
        progress?.Report(100);
    }

    /// <summary>
    /// Extracts the largest data track's user data to an ISO file.
    /// </summary>
    /// <param name="cdiFilePath">Path to the CDI file.</param>
    /// <param name="image">A successfully parsed <see cref="CdiImage"/>.</param>
    /// <param name="outputIsoPath">Destination path for the ISO file.</param>
    /// <param name="progress">Optional progress reporter (0–100).</param>
    /// <exception cref="InvalidOperationException">If no data tracks exist.</exception>
    public static void ExtractToIso(
        string cdiFilePath, CdiImage image, string outputIsoPath, IProgress<int>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(image);

        var dataTrack = image.DataTracks
            .OrderByDescending(t => t.TotalLength)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("CDI image contains no data tracks.");

        using var inStream = new FileStream(cdiFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var outStream = new FileStream(outputIsoPath, FileMode.Create, FileAccess.Write);

        if (dataTrack.FileOffset + dataTrack.TotalLength > inStream.Length)
            throw new IOException("Data track extends beyond file bounds.");

        inStream.Seek(dataTrack.FileOffset, SeekOrigin.Begin);

        if (!dataTrack.IsRawSector)
        {
            CopyBytesWithProgress(inStream, outStream, dataTrack.TotalLength, progress);
            return;
        }

        int sectorSize = dataTrack.SectorSize;
        int userOffset = dataTrack.UserDataOffset;
        int userData = dataTrack.UserDataSize;
        byte[] sectorBuf = new byte[sectorSize];

        long totalSectors = dataTrack.TotalLength / sectorSize;
        long sectorsWritten = 0;
        long remaining = dataTrack.TotalLength;

        while (remaining >= sectorSize)
        {
            int read = ReadFull(inStream, sectorBuf, 0, sectorSize);
            if (read < sectorSize) break;
            outStream.Write(sectorBuf, userOffset, userData);
            remaining -= sectorSize;
            sectorsWritten++;

            if (totalSectors > 0)
                progress?.Report((int)(sectorsWritten * 100 / totalSectors));
        }

        progress?.Report(100);
    }

    /// <summary>
    /// Converts a CDI image to BIN/CUE format.
    /// The BIN file is the concatenation of all track data (including subchannel
    /// data when present). The CUE file includes CATALOG, ISRC, and FLAGS metadata.
    /// </summary>
    /// <param name="cdiFilePath">Path to the CDI file.</param>
    /// <param name="image">A successfully parsed <see cref="CdiImage"/>.</param>
    /// <param name="outputBinPath">Destination BIN file path.</param>
    /// <param name="outputCuePath">Destination CUE file path.</param>
    public static void ConvertToBinCue(
        string cdiFilePath, CdiImage image, string outputBinPath, string outputCuePath)
    {
        ArgumentNullException.ThrowIfNull(image);

        using var inStream = new FileStream(cdiFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var binStream = new FileStream(outputBinPath, FileMode.Create, FileAccess.Write);

        var cueLines = new List<string>();
        string binFileName = Path.GetFileName(outputBinPath);
        cueLines.Add($"FILE \"{binFileName}\" BINARY");

        // Write MCN if available.
        if (!string.IsNullOrEmpty(image.Mcn))
            cueLines.Add($"CATALOG {image.Mcn}");

        long binOffset = 0;
        int globalTrackNum = 0;

        foreach (var session in image.Sessions)
        {
            foreach (var track in session.Tracks)
            {
                globalTrackNum++;

                string modeStr = BuildCueTrackMode(track);
                cueLines.Add($"  TRACK {globalTrackNum:D2} {modeStr}");

                // Write ISRC if present.
                if (!string.IsNullOrEmpty(track.Isrc))
                    cueLines.Add($"    ISRC {track.Isrc}");

                // Write FLAGS from control field.
                var flags = new List<string>();
                if ((track.Control & 0x01) != 0) flags.Add("PRE");
                if ((track.Control & 0x02) != 0) flags.Add("DCP");
                if ((track.Control & 0x08) != 0) flags.Add("4CH");
                if (flags.Count > 0)
                    cueLines.Add($"    FLAGS {string.Join(" ", flags)}");

                if (track.Pregap > 0)
                {
                    cueLines.Add($"    INDEX 00 {LbaToMsf(binOffset, track.SectorSize)}");
                    long pregapBytes = (long)track.Pregap * track.SectorSize;

                    if (track.FileOffset + track.TotalLength <= inStream.Length)
                    {
                        inStream.Seek(track.FileOffset, SeekOrigin.Begin);
                        CopyBytes(inStream, binStream, track.TotalLength);
                    }

                    string index01Msf = LbaToMsf(binOffset + pregapBytes, track.SectorSize);
                    cueLines.Add($"    INDEX 01 {index01Msf}");
                    binOffset += track.TotalLength;
                }
                else
                {
                    cueLines.Add($"    INDEX 01 {LbaToMsf(binOffset, track.SectorSize)}");

                    if (track.FileOffset + track.TotalLength <= inStream.Length)
                    {
                        inStream.Seek(track.FileOffset, SeekOrigin.Begin);
                        CopyBytes(inStream, binStream, track.TotalLength);
                    }

                    binOffset += track.TotalLength;
                }
            }
        }

        File.WriteAllText(outputCuePath, string.Join(Environment.NewLine, cueLines) + Environment.NewLine);
    }

    /// <summary>
    /// Generates a CUE sheet string for direct burning of a CDI file.
    /// Used when all tracks are contiguous in the CDI file, allowing the CDI
    /// to be used directly as the burn source with an offset.
    /// Track offsets are relative to the first track's file offset.
    /// </summary>
    /// <param name="image">Parsed CDI image structure.</param>
    /// <param name="dataFileName">CDI file name to reference in the CUE FILE directive.</param>
    /// <returns>CUE sheet content as a string.</returns>
    public static string GenerateCueSheetForDirectBurn(CdiImage image, string dataFileName)
    {
        ArgumentNullException.ThrowIfNull(image);

        var cueLines = new List<string>();
        cueLines.Add($"REM Generated by OpenBurningSuite for direct CDI burning");
        cueLines.Add($"FILE \"{dataFileName}\" BINARY");

        if (!string.IsNullOrEmpty(image.Mcn))
            cueLines.Add($"CATALOG {image.Mcn}");

        var allTracks = image.Sessions.SelectMany(s => s.Tracks).ToList();
        if (allTracks.Count == 0) return string.Join(Environment.NewLine, cueLines);

        long baseOffset = allTracks[0].FileOffset;
        int globalTrackNum = 0;

        foreach (var session in image.Sessions)
        {
            foreach (var track in session.Tracks)
            {
                globalTrackNum++;
                string modeStr = BuildCueTrackMode(track);
                cueLines.Add($"  TRACK {globalTrackNum:D2} {modeStr}");

                if (!string.IsNullOrEmpty(track.Isrc))
                    cueLines.Add($"    ISRC {track.Isrc}");

                var flags = new List<string>();
                if ((track.Control & 0x01) != 0) flags.Add("PRE");
                if ((track.Control & 0x02) != 0) flags.Add("DCP");
                if ((track.Control & 0x08) != 0) flags.Add("4CH");
                if (flags.Count > 0)
                    cueLines.Add($"    FLAGS {string.Join(" ", flags)}");

                long trackRelativeOffset = track.FileOffset - baseOffset;

                if (track.Pregap > 0)
                {
                    cueLines.Add($"    INDEX 00 {LbaToMsf(trackRelativeOffset, track.SectorSize)}");
                    long pregapBytes = (long)track.Pregap * track.SectorSize;
                    cueLines.Add($"    INDEX 01 {LbaToMsf(trackRelativeOffset + pregapBytes, track.SectorSize)}");
                }
                else
                {
                    cueLines.Add($"    INDEX 01 {LbaToMsf(trackRelativeOffset, track.SectorSize)}");
                }
            }
        }

        return string.Join(Environment.NewLine, cueLines) + Environment.NewLine;
    }

    /// <summary>
    /// Extracts all data tracks to a single output stream, concatenating user data.
    /// </summary>
    /// <param name="cdiFilePath">Path to the CDI file.</param>
    /// <param name="image">A successfully parsed <see cref="CdiImage"/>.</param>
    /// <param name="outputStream">Writable destination stream.</param>
    /// <param name="progress">Optional progress reporter (0–100).</param>
    public static void ExtractAllDataTracks(
        string cdiFilePath, CdiImage image, Stream outputStream, IProgress<int>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(outputStream);

        var dataTracks = image.DataTracks.ToList();
        if (dataTracks.Count == 0)
            throw new InvalidOperationException("CDI image contains no data tracks.");

        long totalBytes = dataTracks.Sum(t => (long)t.SectorCount * t.UserDataSize);
        long bytesWritten = 0;

        using var fs = new FileStream(cdiFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        foreach (var track in dataTracks)
        {
            if (track.FileOffset + track.TotalLength > fs.Length)
                throw new IOException($"Track {track.TrackNumber} data extends beyond file bounds.");

            fs.Seek(track.FileOffset, SeekOrigin.Begin);

            if (!track.IsRawSector)
            {
                byte[] buf = new byte[81920];
                long remaining = track.TotalLength;
                while (remaining > 0)
                {
                    int toRead = (int)Math.Min(remaining, buf.Length);
                    int read = ReadFull(fs, buf, 0, toRead);
                    if (read == 0) break;
                    outputStream.Write(buf, 0, read);
                    remaining -= read;
                    bytesWritten += read;
                    if (totalBytes > 0)
                        progress?.Report((int)(bytesWritten * 100 / totalBytes));
                }
            }
            else
            {
                int sectorSize = track.SectorSize;
                int userOffset = track.UserDataOffset;
                int userData = track.UserDataSize;
                byte[] sectorBuf = new byte[sectorSize];

                long remaining = track.TotalLength;
                while (remaining >= sectorSize)
                {
                    int read = ReadFull(fs, sectorBuf, 0, sectorSize);
                    if (read < sectorSize) break;
                    outputStream.Write(sectorBuf, userOffset, userData);
                    remaining -= sectorSize;
                    bytesWritten += userData;
                    if (totalBytes > 0)
                        progress?.Report((int)(bytesWritten * 100 / totalBytes));
                }
            }
        }

        progress?.Report(100);
    }

    /// <summary>
    /// Extracts all tracks from a specific session to BIN/CUE format.
    /// </summary>
    /// <param name="cdiFilePath">Path to the CDI file.</param>
    /// <param name="image">A successfully parsed <see cref="CdiImage"/>.</param>
    /// <param name="sessionNumber">1-based session number to extract.</param>
    /// <param name="outputBinPath">Destination BIN file path.</param>
    /// <param name="outputCuePath">Destination CUE file path.</param>
    public static void ExtractSession(
        string cdiFilePath, CdiImage image, int sessionNumber,
        string outputBinPath, string outputCuePath)
    {
        ArgumentNullException.ThrowIfNull(image);

        var session = image.Sessions.FirstOrDefault(s => s.SessionNumber == sessionNumber)
            ?? throw new ArgumentException(
                $"Session {sessionNumber} not found in CDI image.", nameof(sessionNumber));

        using var inStream = new FileStream(cdiFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var binStream = new FileStream(outputBinPath, FileMode.Create, FileAccess.Write);

        var cueLines = new List<string>();
        string binFileName = Path.GetFileName(outputBinPath);
        cueLines.Add($"FILE \"{binFileName}\" BINARY");

        if (!string.IsNullOrEmpty(session.Mcn))
            cueLines.Add($"CATALOG {session.Mcn}");

        long binOffset = 0;
        int trackNum = 0;

        foreach (var track in session.Tracks)
        {
            trackNum++;

            string modeStr = BuildCueTrackMode(track);
            cueLines.Add($"  TRACK {trackNum:D2} {modeStr}");

            if (!string.IsNullOrEmpty(track.Isrc))
                cueLines.Add($"    ISRC {track.Isrc}");

            if (track.Pregap > 0)
            {
                cueLines.Add($"    INDEX 00 {LbaToMsf(binOffset, track.SectorSize)}");
                long pregapBytes = (long)track.Pregap * track.SectorSize;

                if (track.FileOffset + track.TotalLength <= inStream.Length)
                {
                    inStream.Seek(track.FileOffset, SeekOrigin.Begin);
                    CopyBytes(inStream, binStream, track.TotalLength);
                }

                string index01Msf = LbaToMsf(binOffset + pregapBytes, track.SectorSize);
                cueLines.Add($"    INDEX 01 {index01Msf}");
                binOffset += track.TotalLength;
            }
            else
            {
                cueLines.Add($"    INDEX 01 {LbaToMsf(binOffset, track.SectorSize)}");

                if (track.FileOffset + track.TotalLength <= inStream.Length)
                {
                    inStream.Seek(track.FileOffset, SeekOrigin.Begin);
                    CopyBytes(inStream, binStream, track.TotalLength);
                }

                binOffset += track.TotalLength;
            }
        }

        File.WriteAllText(outputCuePath, string.Join(Environment.NewLine, cueLines) + Environment.NewLine);
    }

    /// <summary>
    /// Validates the integrity of a parsed CDI image by cross-checking track offsets,
    /// verifying non-overlapping data ranges, and ensuring all sector data is
    /// contained within the file boundaries.
    /// </summary>
    /// <param name="image">A previously parsed <see cref="CdiImage"/>.</param>
    /// <param name="fileSize">The actual size of the CDI file on disk (for bounds checking).</param>
    /// <returns>
    /// A list of validation issue descriptions. An empty list indicates no issues found.
    /// </returns>
    public static List<string> ValidateImage(CdiImage image, long fileSize)
    {
        var issues = new List<string>();

        if (image == null)
        {
            issues.Add("Image is null.");
            return issues;
        }

        if (!image.IsValid)
            issues.Add($"Image marked as invalid: {image.ErrorMessage}");

        if (image.SessionCount == 0)
            issues.Add("Image contains no sessions.");

        // Check for overlapping track data ranges.
        var allTracks = image.AllTracks.ToList();
        for (int i = 0; i < allTracks.Count; i++)
        {
            var ti = allTracks[i];

            // Bounds check: track data must be within file.
            if (ti.FileOffset < 0)
                issues.Add($"Track {ti.TrackNumber} (session {ti.SessionNumber}): negative file offset ({ti.FileOffset}).");
            if (ti.TotalLength < 0)
                issues.Add($"Track {ti.TrackNumber} (session {ti.SessionNumber}): negative total length ({ti.TotalLength}).");
            if (ti.FileOffset + ti.TotalLength > fileSize)
                issues.Add($"Track {ti.TrackNumber} (session {ti.SessionNumber}): data extends beyond file " +
                    $"(offset {ti.FileOffset} + length {ti.TotalLength} > file size {fileSize}).");

            // Sector count consistency check.
            if (ti.SectorSize > 0)
            {
                long expectedSectors = ti.TotalLength / ti.SectorSize;
                if (expectedSectors != ti.SectorCount && ti.SectorCount != 0)
                    issues.Add($"Track {ti.TrackNumber} (session {ti.SessionNumber}): sector count mismatch " +
                        $"(computed {expectedSectors}, stored {ti.SectorCount}).");
            }

            // Overlap check against subsequent tracks.
            long endI = ti.FileOffset + ti.TotalLength;
            for (int j = i + 1; j < allTracks.Count; j++)
            {
                var tj = allTracks[j];
                long endJ = tj.FileOffset + tj.TotalLength;

                // Two ranges [a,b) and [c,d) overlap if a < d && c < b.
                if (ti.FileOffset < endJ && tj.FileOffset < endI)
                {
                    issues.Add($"Track {ti.TrackNumber} (session {ti.SessionNumber}) " +
                        $"overlaps with track {tj.TrackNumber} (session {tj.SessionNumber}).");
                }
            }

            // ISRC format validation (if present).
            if (!string.IsNullOrEmpty(ti.Isrc))
            {
                if (ti.Isrc.Length != 12)
                    issues.Add($"Track {ti.TrackNumber}: ISRC '{ti.Isrc}' has invalid length " +
                        $"({ti.Isrc.Length}, expected 12).");
            }
        }

        // MCN format validation (if present).
        if (!string.IsNullOrEmpty(image.Mcn))
        {
            if (image.Mcn.Length != 13)
                issues.Add($"MCN '{image.Mcn}' has invalid length ({image.Mcn.Length}, expected 13).");
            else
            {
                foreach (char c in image.Mcn)
                {
                    if (c < '0' || c > '9')
                    {
                        issues.Add($"MCN '{image.Mcn}' contains non-digit characters.");
                        break;
                    }
                }
            }
        }

        return issues;
    }

    /// <summary>
    /// Converts a CDI image to BIN/CUE format with progress reporting.
    /// Handles mixed-mode discs where tracks may have different sector sizes
    /// by using separate FILE entries in the CUE sheet when necessary.
    /// </summary>
    /// <param name="cdiFilePath">Path to the CDI file.</param>
    /// <param name="image">A successfully parsed <see cref="CdiImage"/>.</param>
    /// <param name="outputBinPath">Destination BIN file path.</param>
    /// <param name="outputCuePath">Destination CUE file path.</param>
    /// <param name="progress">Optional progress reporter (0–100).</param>
    public static void ConvertToBinCue(
        string cdiFilePath, CdiImage image, string outputBinPath, string outputCuePath,
        IProgress<int>? progress)
    {
        ArgumentNullException.ThrowIfNull(image);

        using var inStream = new FileStream(cdiFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var binStream = new FileStream(outputBinPath, FileMode.Create, FileAccess.Write);

        var cueLines = new List<string>();
        string binFileName = Path.GetFileName(outputBinPath);

        // Write MCN/CATALOG at the top of the CUE sheet if available.
        if (!string.IsNullOrEmpty(image.Mcn))
            cueLines.Add($"CATALOG {image.Mcn}");

        // Detect mixed sector sizes — if all tracks share the same base sector size
        // we use a single FILE entry; otherwise use per-track FILE entries.
        var allTracks = image.AllTracks.ToList();
        bool hasMixedSectorSizes = allTracks.Select(t => t.BaseSectorSize).Distinct().Count() > 1;

        if (!hasMixedSectorSizes)
            cueLines.Add($"FILE \"{binFileName}\" BINARY");

        long totalBytesToCopy = allTracks.Sum(t => t.TotalLength);
        long bytesCopied = 0;
        long binOffset = 0;
        int globalTrackNum = 0;

        foreach (var session in image.Sessions)
        {
            foreach (var track in session.Tracks)
            {
                globalTrackNum++;

                // For mixed-mode discs, each track references the same BIN file
                // but the CUE FILE entry clarifies the sector layout.
                if (hasMixedSectorSizes)
                    cueLines.Add($"FILE \"{binFileName}\" BINARY");

                string modeStr = BuildCueTrackMode(track);
                cueLines.Add($"  TRACK {globalTrackNum:D2} {modeStr}");

                // Write ISRC if present.
                if (!string.IsNullOrEmpty(track.Isrc))
                    cueLines.Add($"    ISRC {track.Isrc}");

                // Write FLAGS from control field.
                var flags = new List<string>();
                if ((track.Control & 0x01) != 0) flags.Add("PRE");
                if ((track.Control & 0x02) != 0) flags.Add("DCP");
                if ((track.Control & 0x08) != 0) flags.Add("4CH");
                if (flags.Count > 0)
                    cueLines.Add($"    FLAGS {string.Join(" ", flags)}");

                if (track.Pregap > 0)
                {
                    cueLines.Add($"    INDEX 00 {LbaToMsf(binOffset, track.SectorSize)}");
                    long pregapBytes = (long)track.Pregap * track.SectorSize;

                    if (track.FileOffset + track.TotalLength <= inStream.Length)
                    {
                        inStream.Seek(track.FileOffset, SeekOrigin.Begin);
                        CopyBytesWithProgress(inStream, binStream, track.TotalLength,
                            null); // Don't report per-track; report overall below
                        bytesCopied += track.TotalLength;
                    }

                    string index01Msf = LbaToMsf(binOffset + pregapBytes, track.SectorSize);
                    cueLines.Add($"    INDEX 01 {index01Msf}");
                    binOffset += track.TotalLength;
                }
                else
                {
                    cueLines.Add($"    INDEX 01 {LbaToMsf(binOffset, track.SectorSize)}");

                    if (track.FileOffset + track.TotalLength <= inStream.Length)
                    {
                        inStream.Seek(track.FileOffset, SeekOrigin.Begin);
                        CopyBytesWithProgress(inStream, binStream, track.TotalLength, null);
                        bytesCopied += track.TotalLength;
                    }

                    binOffset += track.TotalLength;
                }

                // Report overall progress.
                if (totalBytesToCopy > 0)
                    progress?.Report((int)(bytesCopied * 100 / totalBytesToCopy));
            }
        }

        File.WriteAllText(outputCuePath, string.Join(Environment.NewLine, cueLines) + Environment.NewLine);
        progress?.Report(100);
    }

    /// <summary>
    /// Retrieves a specific track by its 1-based track number across all sessions.
    /// </summary>
    /// <param name="image">A successfully parsed <see cref="CdiImage"/>.</param>
    /// <param name="trackNumber">1-based track number.</param>
    /// <returns>The matching <see cref="CdiTrack"/>, or null if not found.</returns>
    public static CdiTrack? GetTrack(CdiImage image, int trackNumber)
    {
        ArgumentNullException.ThrowIfNull(image);
        return image.AllTracks.FirstOrDefault(t => t.TrackNumber == trackNumber);
    }

    /// <summary>
    /// Gets a human-readable description of a track for diagnostic or display purposes.
    /// </summary>
    /// <param name="track">The track to describe.</param>
    /// <returns>A formatted string describing the track's properties.</returns>
    public static string DescribeTrack(CdiTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);

        string modeStr = track.Mode switch
        {
            CdiTrackMode.Audio => "Audio (CD-DA)",
            CdiTrackMode.Mode1 => "Data (Mode 1)",
            CdiTrackMode.Mode2 when track.IsMode2Form1 => "Data (Mode 2 Form 1)",
            CdiTrackMode.Mode2 when track.IsMode2Form2 => "Data (Mode 2 Form 2)",
            CdiTrackMode.Mode2 => "Data (Mode 2)",
            _ => "Unknown"
        };

        string subStr = track.SubchannelMode switch
        {
            CdiSubchannelMode.PQ => " + PQ subchannel",
            CdiSubchannelMode.Raw => " + Raw subchannel (96 bytes)",
            _ => ""
        };

        var sb = new StringBuilder();
        sb.Append($"Track {track.TrackNumber}: {modeStr}");
        sb.Append($", {track.SectorSize} bytes/sector{subStr}");
        sb.Append($", {track.SectorCount} sectors");
        sb.Append($", LBA {track.StartLba}");
        if (track.Pregap > 0)
            sb.Append($", pregap {track.Pregap} sectors");
        if (!string.IsNullOrEmpty(track.Isrc))
            sb.Append($", ISRC {track.Isrc}");

        // Show control flags.
        var flagList = new List<string>();
        if ((track.Control & 0x01) != 0) flagList.Add("pre-emphasis");
        if ((track.Control & 0x02) != 0) flagList.Add("copy-permitted");
        if ((track.Control & 0x04) != 0) flagList.Add("data");
        if ((track.Control & 0x08) != 0) flagList.Add("4-channel");
        if (flagList.Count > 0)
            sb.Append($" [{string.Join(", ", flagList)}]");

        return sb.ToString();
    }

    /// <summary>Asynchronously parses a CDI image file from disk.</summary>
    public static async Task<CdiImage> ParseAsync(string filePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return ErrorImage(string.Empty, 0, "File path is null or empty.");
        if (!File.Exists(filePath))
            return ErrorImage(filePath, 0, $"File not found: {filePath}");

        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            return Parse(filePath);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Asynchronously parses a CDI image from a stream.</summary>
    public static async Task<CdiImage> ParseAsync(
        Stream stream, string filePath, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            return Parse(stream, filePath);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Asynchronously extracts raw track data with progress.</summary>
    public static async Task ExtractTrackDataAsync(
        string cdiFilePath, CdiTrack track, Stream outputStream,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            ExtractTrackData(cdiFilePath, track, outputStream, progress);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Asynchronously extracts user data with progress.</summary>
    public static async Task ExtractUserDataAsync(
        string cdiFilePath, CdiTrack track, Stream outputStream,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            ExtractUserData(cdiFilePath, track, outputStream, progress);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Asynchronously extracts the largest data track to an ISO file.</summary>
    public static async Task ExtractToIsoAsync(
        string cdiFilePath, CdiImage image, string outputIsoPath,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            ExtractToIso(cdiFilePath, image, outputIsoPath, progress);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Asynchronously converts a CDI image to BIN/CUE format with progress.</summary>
    public static async Task ConvertToBinCueAsync(
        string cdiFilePath, CdiImage image, string outputBinPath, string outputCuePath,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            ConvertToBinCue(cdiFilePath, image, outputBinPath, outputCuePath, progress);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Asynchronously validates a CDI image.</summary>
    public static async Task<List<string>> ValidateImageAsync(
        CdiImage image, long fileSize, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            return ValidateImage(image, fileSize);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Asynchronously extracts all data tracks.</summary>
    public static async Task ExtractAllDataTracksAsync(
        string cdiFilePath, CdiImage image, Stream outputStream,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            ExtractAllDataTracks(cdiFilePath, image, outputStream, progress);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Asynchronously extracts a specific session to BIN/CUE format.</summary>
    public static async Task ExtractSessionAsync(
        string cdiFilePath, CdiImage image, int sessionNumber,
        string outputBinPath, string outputCuePath,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            ExtractSession(cdiFilePath, image, sessionNumber, outputBinPath, outputCuePath);
            progress?.Report(100);
        }, ct).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------
    //  Private — trailer & header parsing
    // -------------------------------------------------------------------

    /// <summary>
    /// Reads and validates the CDI trailer to determine format version and header offset.
    /// </summary>
    private static (CdiVersion version, uint headerOffset) ReadTrailer(Stream stream)
    {
        long fileSize = stream.Length;

        // Try CDI 3.5 first (12-byte trailer).
        if (fileSize >= 12)
        {
            stream.Seek(fileSize - 12, SeekOrigin.Begin);
            byte[] trailer12 = new byte[12];
            ReadFull(stream, trailer12, 0, 12);

            uint magic35 = BitConverter.ToUInt32(trailer12, 8);
            if (magic35 == MagicVersion35)
            {
                uint offset = BitConverter.ToUInt32(trailer12, 4);
                return (CdiVersion.Version35, offset);
            }

            if (magic35 == MagicVersion4)
            {
                uint offset = BitConverter.ToUInt32(trailer12, 4);
                return (CdiVersion.Version4, offset);
            }
        }

        // Try CDI 2.0 / 3.0 (8-byte trailer).
        if (fileSize >= 8)
        {
            stream.Seek(fileSize - 8, SeekOrigin.Begin);
            byte[] trailer8 = new byte[8];
            ReadFull(stream, trailer8, 0, 8);

            uint magic = BitConverter.ToUInt32(trailer8, 4);
            uint offset = BitConverter.ToUInt32(trailer8, 0);

            if (magic == MagicVersion2) return (CdiVersion.Version2, offset);
            if (magic == MagicVersion3) return (CdiVersion.Version3, offset);
        }

        return (CdiVersion.Unknown, 0);
    }

    /// <summary>
    /// Parses the session/track descriptor area starting at the current stream position.
    /// </summary>
    private static List<CdiSession> ParseSessions(BinaryReader reader, CdiVersion version, long fileSize)
    {
        var sessions = new List<CdiSession>();

        ushort sessionCount = reader.ReadUInt16();
        if (sessionCount == 0 || sessionCount > 99)
            return sessions;

        for (int s = 0; s < sessionCount; s++)
        {
            var session = new CdiSession { SessionNumber = s + 1 };

            ushort trackCount = reader.ReadUInt16();
            if (trackCount == 0 || trackCount > 99)
                break;

            for (int t = 0; t < trackCount; t++)
            {
                var track = ParseTrackDescriptor(reader, version, s + 1, t + 1, fileSize);
                if (track != null)
                {
                    // Subchannel detection from sector size.
                    if (track.SectorSize == 2368)
                        track.SubchannelMode = CdiSubchannelMode.PQ;
                    else if (track.SectorSize == 2448)
                        track.SubchannelMode = CdiSubchannelMode.Raw;

                    // Mode 2 Form detection from first sector subheader.
                    if (track.Mode == CdiTrackMode.Mode2 && track.SectorSize >= 2352 && track.FileOffset > 0)
                    {
                        long savedPos = reader.BaseStream.Position;
                        try
                        {
                            reader.BaseStream.Seek(track.FileOffset + 18, SeekOrigin.Begin);
                            byte submode = reader.ReadByte();
                            track.IsMode2Form2 = (submode & 0x20) != 0;
                            track.IsMode2Form1 = !track.IsMode2Form2;
                        }
                        catch { /* ignore */ }
                        finally { reader.BaseStream.Seek(savedPos, SeekOrigin.Begin); }
                    }

                    // Also detect Mode 2 Form for cooked sectors (2336 bytes)
                    // by examining the first 8 bytes (subheader) of the sector data.
                    if (track.Mode == CdiTrackMode.Mode2 && track.SectorSize == 2336 &&
                        track.FileOffset > 0 && !track.IsMode2Form1 && !track.IsMode2Form2)
                    {
                        long savedPos = reader.BaseStream.Position;
                        try
                        {
                            reader.BaseStream.Seek(track.FileOffset + 2, SeekOrigin.Begin);
                            byte submode = reader.ReadByte();
                            track.IsMode2Form2 = (submode & 0x20) != 0;
                            track.IsMode2Form1 = !track.IsMode2Form2;
                        }
                        catch { /* ignore */ }
                        finally { reader.BaseStream.Seek(savedPos, SeekOrigin.Begin); }
                    }

                    session.Tracks.Add(track);
                }
            }

            // Attempt to scan for MCN/ISRC metadata in the descriptor area.
            {
                long scanPos = reader.BaseStream.Position;
                try
                {
                    long scanEnd = Math.Min(scanPos + 256, fileSize);
                    while (reader.BaseStream.Position + 13 <= scanEnd)
                    {
                        // Look for a 13-digit MCN string.
                        long mark = reader.BaseStream.Position;
                        byte[] candidate = reader.ReadBytes(13);
                        bool isMcn = candidate.Length == 13;
                        for (int i = 0; i < candidate.Length && isMcn; i++)
                        {
                            if (candidate[i] < 0x30 || candidate[i] > 0x39)
                                isMcn = false;
                        }
                        if (isMcn && string.IsNullOrEmpty(session.Mcn))
                        {
                            session.Mcn = Encoding.ASCII.GetString(candidate);
                            break;
                        }
                        // Rewind to mark + 1 and try next position.
                        reader.BaseStream.Seek(mark + 1, SeekOrigin.Begin);
                    }
                }
                catch { /* ignore scan errors */ }
                finally
                {
                    // Always restore position to ensure subsequent session parsing is unaffected.
                    reader.BaseStream.Seek(scanPos, SeekOrigin.Begin);
                }
            }

            if (session.Tracks.Count > 0)
                sessions.Add(session);
        }

        return sessions;
    }

    /// <summary>
    /// Parses a single track descriptor from the current reader position.
    /// Returns null if the descriptor cannot be reliably parsed.
    /// </summary>
    /// <remarks>
    /// CDI track descriptors contain: extra data block, marker, filename,
    /// padding fields, track mode, sector size hint, total length, pregap,
    /// total sectors, ISRC/control data, and a start-mark + file position.
    /// Version-specific layout differences (3.5/4.0 extra fields) are handled.
    /// </remarks>
    private static CdiTrack? ParseTrackDescriptor(
        BinaryReader reader, CdiVersion version, int sessionNum, int trackNum, long fileSize)
    {
        try
        {
            long descriptorStart = reader.BaseStream.Position;

            // Extra data block — may contain ISRC and control information.
            uint extraLen = reader.ReadUInt32();
            if (extraLen > 1_000_000) return null; // sanity
            byte[] extraData = Array.Empty<byte>();
            if (extraLen > 0)
            {
                long extraStart = reader.BaseStream.Position;
                if (extraLen <= 512) // Only read manageable extra blocks for metadata
                {
                    extraData = reader.ReadBytes((int)extraLen);
                }
                else
                {
                    reader.BaseStream.Seek(extraLen, SeekOrigin.Current);
                }
            }

            // Marker / padding.
            reader.ReadUInt32(); // skip

            // Filename.
            byte fnLen = reader.ReadByte();
            if (fnLen > 0)
                reader.BaseStream.Seek(fnLen, SeekOrigin.Current);

            // Padding uint32 × 4.
            reader.ReadUInt32();
            reader.ReadUInt32();
            reader.ReadUInt32();
            reader.ReadUInt32();

            // CDI ≥ 3.5 has an extra uint32.
            if (version == CdiVersion.Version35 || version == CdiVersion.Version4)
                reader.ReadUInt32();

            // Track mode.
            uint modeVal = reader.ReadUInt32();
            CdiTrackMode mode = modeVal switch
            {
                0 => CdiTrackMode.Audio,
                1 => CdiTrackMode.Mode1,
                2 => CdiTrackMode.Mode2,
                _ => CdiTrackMode.Mode1
            };

            // Session/track attributes — may contain control byte data.
            uint attr1 = reader.ReadUInt32();
            uint attr2 = reader.ReadUInt32();

            // Sector size hint.
            uint sectorSizeHint = reader.ReadUInt32();
            int sectorSize = DetermineSectorSize(modeVal, sectorSizeHint);

            // Skip 4 × uint32.
            reader.ReadUInt32();
            reader.ReadUInt32();
            reader.ReadUInt32();
            reader.ReadUInt32();

            // Total length.
            uint totalLength = reader.ReadUInt32();

            // Skip 2 × uint32.
            reader.ReadUInt32();
            reader.ReadUInt32();

            // Pregap.
            uint pregap = reader.ReadUInt32();

            // Total sectors (including pregap).
            uint totalSectors = reader.ReadUInt32();

            // Scan forward for start mark.
            long filePosition = 0;
            bool foundMark = false;

            // Limit the scan window to prevent runaway reads.
            long scanLimit = Math.Min(reader.BaseStream.Position + 512, fileSize);
            while (reader.BaseStream.Position + 8 <= scanLimit)
            {
                uint val = reader.ReadUInt32();
                if (val == StartMark)
                {
                    filePosition = reader.ReadUInt32();
                    foundMark = true;
                    break;
                }
            }

            if (!foundMark)
            {
                // Fallback: try scanning from descriptor start with a wider window.
                return null;
            }

            long sectorCount = sectorSize > 0 ? totalLength / sectorSize : 0;

            var track = new CdiTrack
            {
                TrackNumber = trackNum,
                Mode = mode,
                SectorSize = sectorSize,
                FileOffset = filePosition,
                TotalLength = totalLength,
                SectorCount = sectorCount > 0 ? sectorCount : totalSectors,
                Pregap = (int)pregap,
                SessionNumber = sessionNum,
                StartLba = 0
            };

            // Extract ISRC from the extra data block if present.
            // ISRC is 12 characters (5 alpha + 7 digits) per ISO 3901.
            if (extraData.Length >= 12)
            {
                string isrc = TryExtractIsrc(extraData);
                if (!string.IsNullOrEmpty(isrc))
                    track.Isrc = isrc;
            }

            // Derive control byte from track mode and extra attribute data.
            // Red Book control field: bit 0=pre-emphasis, bit 1=copy-permitted,
            // bit 2=data track, bit 3=four-channel.
            byte control = 0;
            if (mode != CdiTrackMode.Audio)
                control |= 0x04; // Data track

            // Check for pre-emphasis (common in early audio CDs).
            // attr1 low byte may carry pre-emphasis flag when non-zero for audio tracks.
            if (mode == CdiTrackMode.Audio && (attr1 & 0x01) != 0)
                control |= 0x01; // Pre-emphasis

            // Check for digital copy permitted flag in attribute data.
            if ((attr1 & 0x02) != 0)
                control |= 0x02; // Copy permitted

            // Check for four-channel audio flag.
            if (mode == CdiTrackMode.Audio && (attr1 & 0x08) != 0)
                control |= 0x08; // Four-channel

            track.Control = control;

            if (!ValidateTrack(track, fileSize))
                return null;

            return track;
        }
        catch (EndOfStreamException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Attempts to extract an ISRC (International Standard Recording Code)
    /// from a CDI track descriptor's extra data block.
    /// ISRC format: 5 alphanumeric characters + 7 digits (12 characters total).
    /// </summary>
    private static string TryExtractIsrc(byte[] extraData)
    {
        // Scan through the extra data looking for a valid ISRC pattern.
        // ISRC: CC-XXX-YY-NNNNN where CC=country, XXX=registrant, YY=year, NNNNN=designation
        // In binary form, it's 12 consecutive alphanumeric bytes.
        for (int start = 0; start + 12 <= extraData.Length; start++)
        {
            bool valid = true;
            // First 5 characters: alphanumeric (country code + registrant)
            for (int i = 0; i < 5; i++)
            {
                byte b = extraData[start + i];
                if (!((b >= (byte)'A' && b <= (byte)'Z') ||
                      (b >= (byte)'0' && b <= (byte)'9')))
                {
                    valid = false;
                    break;
                }
            }
            if (!valid) continue;

            // Last 7 characters: digits (year + designation)
            for (int i = 5; i < 12; i++)
            {
                byte b = extraData[start + i];
                if (b < (byte)'0' || b > (byte)'9')
                {
                    valid = false;
                    break;
                }
            }
            if (!valid) continue;

            // Verify the character after ISRC (if present) is not alphanumeric
            // to avoid matching arbitrary data in the middle of a string.
            if (start + 12 < extraData.Length)
            {
                byte next = extraData[start + 12];
                if ((next >= (byte)'A' && next <= (byte)'Z') ||
                    (next >= (byte)'0' && next <= (byte)'9'))
                    continue;
            }

            return Encoding.ASCII.GetString(extraData, start, 12);
        }

        return string.Empty;
    }

    /// <summary>
    /// Determines the final sector size from the raw mode value and the
    /// size hint encoded in the track descriptor.
    /// </summary>
    private static int DetermineSectorSize(uint mode, uint sectorSizeHint)
    {
        // If the hint is a well-known sector size, trust it.
        if (ValidSectorSizes.Contains((int)sectorSizeHint))
            return (int)sectorSizeHint;

        // Otherwise derive from mode.
        return mode switch
        {
            0 => 2352, // Audio
            1 => 2048, // Mode1 cooked (default)
            2 => 2336, // Mode2 form-mix (default)
            _ => 2048
        };
    }

    /// <summary>
    /// Validates that a parsed track's data range lies within the file.
    /// </summary>
    private static bool ValidateTrack(CdiTrack track, long fileSize)
    {
        if (track.SectorSize <= 0 || !ValidSectorSizes.Contains(track.SectorSize))
            return false;
        if (track.FileOffset < 0 || track.TotalLength < 0)
            return false;
        if (track.FileOffset + track.TotalLength > fileSize)
            return false;
        if (track.SectorCount < 0)
            return false;
        return true;
    }

    // -------------------------------------------------------------------
    //  Helpers
    // -------------------------------------------------------------------

    /// <summary>
    /// Calculates and assigns Start LBA values for all tracks across sessions.
    /// Uses standard CD-ROM layout rules:
    /// - First session, first track starts at LBA 0 (or after the pregap).
    /// - Subsequent tracks within a session follow contiguously.
    /// - Multi-session gaps (lead-out + lead-in = 11400 sectors) separate sessions.
    /// </summary>
    private static void CalculateStartLbas(List<CdiSession> sessions)
    {
        long currentLba = 0;
        for (int i = 0; i < sessions.Count; i++)
        {
            if (i > 0)
                currentLba += MultiSessionGap;

            foreach (var track in sessions[i].Tracks)
            {
                track.StartLba = currentLba;
                currentLba += track.SectorCount;
            }
        }
    }

    /// <summary>
    /// Builds the CUE sheet track mode string for a given track.
    /// Maps CDI track mode and sector size to CUE MODE keywords.
    /// </summary>
    private static string BuildCueTrackMode(CdiTrack track)
    {
        if (track.Mode == CdiTrackMode.Audio)
            return "AUDIO";

        int size = track.BaseSectorSize;

        if (track.Mode == CdiTrackMode.Mode1)
        {
            return size switch
            {
                2048 => "MODE1/2048",
                2352 => "MODE1/2352",
                _ => $"MODE1/{size}"
            };
        }

        if (track.Mode == CdiTrackMode.Mode2)
        {
            return size switch
            {
                2048 => "MODE2/2048",
                2336 => "MODE2/2336",
                2352 => "MODE2/2352",
                _ => $"MODE2/{size}"
            };
        }

        return $"MODE1/{size}";
    }

    private static CdiImage ErrorImage(string path, long size, string message) => new()
    {
        FilePath = path,
        FileSize = size,
        IsValid = false,
        ErrorMessage = message
    };

    /// <summary>Reads exactly <paramref name="count"/> bytes or as many as available.</summary>
    private static int ReadFull(Stream stream, byte[] buffer, int offset, int count)
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

    /// <summary>Copies exactly <paramref name="count"/> bytes between streams.</summary>
    private static void CopyBytes(Stream source, Stream dest, long count)
    {
        byte[] buf = new byte[81920];
        long remaining = count;
        while (remaining > 0)
        {
            int toRead = (int)Math.Min(remaining, buf.Length);
            int read = ReadFull(source, buf, 0, toRead);
            if (read == 0) break;
            dest.Write(buf, 0, read);
            remaining -= read;
        }
    }

    /// <summary>Copies bytes with progress reporting (0–100).</summary>
    private static void CopyBytesWithProgress(Stream source, Stream dest, long count, IProgress<int>? progress)
    {
        byte[] buf = new byte[81920];
        long remaining = count;
        long copied = 0;
        while (remaining > 0)
        {
            int toRead = (int)Math.Min(remaining, buf.Length);
            int read = ReadFull(source, buf, 0, toRead);
            if (read == 0) break;
            dest.Write(buf, 0, read);
            remaining -= read;
            copied += read;
            if (count > 0)
                progress?.Report((int)(copied * 100 / count));
        }
        progress?.Report(100);
    }

    /// <summary>
    /// Converts a byte offset in the BIN file to MM:SS:FF format.
    /// </summary>
    private static string LbaToMsf(long byteOffset, int sectorSize)
    {
        if (sectorSize <= 0) sectorSize = 2352;
        long frames = byteOffset / sectorSize;
        return FramesToMsf(frames);
    }

    /// <summary>
    /// Converts a frame (sector) count to MM:SS:FF string.
    /// 75 frames = 1 second.
    /// </summary>
    private static string FramesToMsf(long frames)
    {
        long f = frames % FramesPerSecond;
        long totalSeconds = frames / FramesPerSecond;
        long s = totalSeconds % 60;
        long m = totalSeconds / 60;
        return $"{m:D2}:{s:D2}:{f:D2}";
    }
}
