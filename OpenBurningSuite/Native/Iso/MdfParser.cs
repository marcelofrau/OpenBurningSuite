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
/// Parser for Alcohol 120% MDS/MDF disc image files.
/// Reads the MDS descriptor file to obtain disc structure (sessions, tracks, metadata),
/// then extracts sector data from the accompanying MDF file.
///
/// Supports:
/// - CD-ROM, CD-R, CD-RW, DVD-ROM, DVD-R medium types
/// - Multi-session and multi-track discs
/// - Audio, Mode 1, Mode 2, Mode 2 Form 1, Mode 2 Form 2 track modes
/// - Raw sectors (2352 bytes) and cooked sectors (2048/2336 bytes)
/// - 96-byte interleaved P-W subchannel data
/// - Wide-character (UTF-16LE) and ASCII filenames in footers
/// - Conversion to ISO, BIN/CUE, IMG, and CDI formats
/// </summary>
public static class MdfParser
{
    // ------------------------------------------------------------------
    //  Constants
    // ------------------------------------------------------------------

    /// <summary>MDS file signature: "MEDIA DESCRIPTOR" (16 bytes).</summary>
    private static readonly byte[] MdsSignature =
        Encoding.ASCII.GetBytes("MEDIA DESCRIPTOR");

    /// <summary>MDS header size (88 bytes, 0x58).</summary>
    private const int HeaderSize = 88;

    /// <summary>MDS session block size (24 bytes, 0x18).</summary>
    private const int SessionBlockSize = 24;

    /// <summary>MDS track block size (80 bytes, 0x50).</summary>
    private const int TrackBlockSize = 80;

    /// <summary>MDS track extra block size (8 bytes).</summary>
    private const int TrackExtraBlockSize = 8;

    /// <summary>MDS footer block size (16 bytes, 0x10).</summary>
    private const int FooterBlockSize = 16;

    /// <summary>Known valid sector sizes for validation.</summary>
    private static readonly HashSet<int> ValidSectorSizes = new() { 2048, 2336, 2352, 2448 };

    /// <summary>CD-DA constant: 75 frames per second.</summary>
    private const int FramesPerSecond = 75;

    // ------------------------------------------------------------------
    //  Public API — Parse
    // ------------------------------------------------------------------

    /// <summary>
    /// Parses an MDS descriptor file and resolves the accompanying MDF data file.
    /// </summary>
    /// <param name="mdsFilePath">Full path to the .mds file.</param>
    /// <returns>
    /// A <see cref="MdfImage"/> instance. Check <see cref="MdfImage.IsValid"/>
    /// and <see cref="MdfImage.ErrorMessage"/> if parsing fails.
    /// </returns>
    public static MdfImage Parse(string mdsFilePath)
    {
        if (string.IsNullOrWhiteSpace(mdsFilePath))
            return ErrorImage(string.Empty, "File path is null or empty.");

        if (!File.Exists(mdsFilePath))
            return ErrorImage(mdsFilePath, $"MDS file not found: {mdsFilePath}");

        try
        {
            byte[] mdsData = File.ReadAllBytes(mdsFilePath);
            return Parse(mdsData, mdsFilePath);
        }
        catch (Exception ex)
        {
            return ErrorImage(mdsFilePath, $"Failed to read MDS file: {ex.Message}");
        }
    }

    /// <summary>
    /// Parses MDS data from a byte array.
    /// </summary>
    /// <param name="mdsData">Complete contents of the MDS file.</param>
    /// <param name="mdsFilePath">Logical file path for reference and MDF resolution.</param>
    /// <returns>A parsed <see cref="MdfImage"/>.</returns>
    public static MdfImage Parse(byte[] mdsData, string mdsFilePath)
    {
        if (mdsData == null || mdsData.Length < HeaderSize)
            return ErrorImage(mdsFilePath, "MDS data is null or too small for a valid header.");

        try
        {
            // Validate signature
            for (int i = 0; i < MdsSignature.Length; i++)
            {
                if (mdsData[i] != MdsSignature[i])
                    return ErrorImage(mdsFilePath,
                        "Invalid MDS signature. Expected 'MEDIA DESCRIPTOR'.");
            }

            var image = new MdfImage
            {
                MdsFilePath = mdsFilePath,
                MdsFileSize = mdsData.Length
            };

            // Parse header fields
            image.Version = new[] { mdsData[16], mdsData[17] };
            image.MediumType = (MdsMediumType)BitConverter.ToUInt16(mdsData, 18);
            ushort numSessions = BitConverter.ToUInt16(mdsData, 20);

            // BCA data (DVD)
            ushort bcaLen = BitConverter.ToUInt16(mdsData, 24);
            uint bcaDataOffset = BitConverter.ToUInt32(mdsData, 32);
            if (bcaLen > 0 && bcaDataOffset > 0 && bcaDataOffset + bcaLen <= mdsData.Length)
            {
                image.BcaData = new byte[bcaLen];
                Array.Copy(mdsData, bcaDataOffset, image.BcaData, 0, bcaLen);
            }

            // Session blocks offset
            uint sessionsBlocksOffset = BitConverter.ToUInt32(mdsData, 80);

            if (sessionsBlocksOffset == 0 || sessionsBlocksOffset >= mdsData.Length)
                return ErrorImage(mdsFilePath,
                    $"Invalid sessions blocks offset: 0x{sessionsBlocksOffset:X}");

            if (numSessions == 0 || numSessions > 99)
                return ErrorImage(mdsFilePath,
                    $"Invalid session count: {numSessions}");

            // Parse sessions
            int offset = (int)sessionsBlocksOffset;
            for (int s = 0; s < numSessions; s++)
            {
                if (offset + SessionBlockSize > mdsData.Length)
                    break;

                var session = ParseSessionBlock(mdsData, offset);
                offset += SessionBlockSize;

                // Parse track blocks for this session
                if (session.NumAllBlocks > 0)
                {
                    ParseTrackBlocks(mdsData, session, mdsFilePath);
                }

                image.Sessions.Add(session);
            }

            // Resolve MDF file path
            image.MdfFilePath = ResolveMdfPath(image, mdsFilePath);
            if (File.Exists(image.MdfFilePath))
                image.MdfFileSize = new FileInfo(image.MdfFilePath).Length;

            if (image.Sessions.Count == 0)
                return ErrorImage(mdsFilePath, "No sessions found in MDS descriptor.");

            image.IsValid = true;
            return image;
        }
        catch (Exception ex)
        {
            return ErrorImage(mdsFilePath, $"MDS parse error: {ex.Message}");
        }
    }

    /// <summary>Asynchronously parses an MDS file.</summary>
    public static async Task<MdfImage> ParseAsync(string mdsFilePath,
        CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            return Parse(mdsFilePath);
        }, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    //  Public API — Validation
    // ------------------------------------------------------------------

    /// <summary>
    /// Validates the integrity of a parsed MDF image by checking track offsets,
    /// sector counts, and data file bounds.
    /// </summary>
    /// <param name="image">A previously parsed <see cref="MdfImage"/>.</param>
    /// <returns>A list of validation issues. Empty means no problems found.</returns>
    public static List<string> ValidateImage(MdfImage image)
    {
        var issues = new List<string>();

        if (image == null)
        {
            issues.Add("Image is null.");
            return issues;
        }

        if (!image.IsValid)
            issues.Add($"Image marked as invalid: {image.ErrorMessage}");

        if (!File.Exists(image.MdfFilePath))
        {
            issues.Add($"MDF data file not found: {image.MdfFilePath}");
            return issues;
        }

        long mdfSize = new FileInfo(image.MdfFilePath).Length;

        var allTracks = image.AllRegularTracks.ToList();
        for (int i = 0; i < allTracks.Count; i++)
        {
            var t = allTracks[i];

            // Bounds check
            long endOffset = (long)t.StartOffset + t.TotalLength;
            if (endOffset > mdfSize)
            {
                issues.Add($"Track {t.Point} (session {GetSessionForTrack(image, t)}): " +
                    $"data extends beyond MDF file (offset 0x{t.StartOffset:X} + " +
                    $"length {t.TotalLength} > MDF size {mdfSize}).");
            }

            // Sector size check
            if (t.SectorSize > 0 && !ValidSectorSizes.Contains(t.SectorSize) &&
                !ValidSectorSizes.Contains(t.MainSectorSize))
            {
                issues.Add($"Track {t.Point}: unusual sector size {t.SectorSize}.");
            }

            // Overlap check
            for (int j = i + 1; j < allTracks.Count; j++)
            {
                var u = allTracks[j];
                long endI = (long)t.StartOffset + t.TotalLength;
                long endJ = (long)u.StartOffset + u.TotalLength;

                if ((long)t.StartOffset < endJ && (long)u.StartOffset < endI &&
                    t.TotalLength > 0 && u.TotalLength > 0)
                {
                    issues.Add($"Track {t.Point} overlaps with track {u.Point} in MDF data.");
                }
            }
        }

        return issues;
    }

    /// <summary>Asynchronously validates an MDF image.</summary>
    public static async Task<List<string>> ValidateImageAsync(MdfImage image,
        CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            return ValidateImage(image);
        }, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    //  Public API — Extract to ISO
    // ------------------------------------------------------------------

    /// <summary>
    /// Extracts the largest data track's user data to an ISO file.
    /// Raw sectors are stripped of sync, header, and ECC/EDC.
    /// </summary>
    /// <param name="mdfFilePath">Path to the MDF data file.</param>
    /// <param name="image">A successfully parsed <see cref="MdfImage"/>.</param>
    /// <param name="outputIsoPath">Destination path for the ISO file.</param>
    /// <param name="progress">Optional progress reporter (0–100).</param>
    public static void ExtractToIso(
        string mdfFilePath, MdfImage image, string outputIsoPath, IProgress<int>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(image);

        var dataTrack = image.LargestDataTrack
            ?? throw new InvalidOperationException("MDF image contains no data tracks.");

        ExtractTrackUserDataToFile(mdfFilePath, dataTrack, outputIsoPath, progress);
    }

    /// <summary>Asynchronously extracts to ISO.</summary>
    public static async Task ExtractToIsoAsync(
        string mdfFilePath, MdfImage image, string outputIsoPath,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            ExtractToIso(mdfFilePath, image, outputIsoPath, progress);
        }, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    //  Public API — Extract to IMG (raw sector dump)
    // ------------------------------------------------------------------

    /// <summary>
    /// Extracts the largest data track to a raw IMG file.
    /// Unlike ISO extraction, this preserves the raw sector layout (no header stripping).
    /// For cooked sectors, the output is identical to ISO.
    /// For raw sectors, the full 2352-byte sectors are written.
    /// </summary>
    /// <param name="mdfFilePath">Path to the MDF data file.</param>
    /// <param name="image">A successfully parsed <see cref="MdfImage"/>.</param>
    /// <param name="outputImgPath">Destination path for the IMG file.</param>
    /// <param name="progress">Optional progress reporter (0–100).</param>
    public static void ExtractToImg(
        string mdfFilePath, MdfImage image, string outputImgPath, IProgress<int>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(image);

        var dataTrack = image.LargestDataTrack
            ?? throw new InvalidOperationException("MDF image contains no data tracks.");

        ExtractTrackRawDataToFile(mdfFilePath, dataTrack, outputImgPath, progress);
    }

    /// <summary>Asynchronously extracts to IMG.</summary>
    public static async Task ExtractToImgAsync(
        string mdfFilePath, MdfImage image, string outputImgPath,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            ExtractToImg(mdfFilePath, image, outputImgPath, progress);
        }, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    //  Public API — Convert to BIN/CUE
    // ------------------------------------------------------------------

    /// <summary>
    /// Converts an MDF/MDS image to BIN/CUE format.
    /// The BIN file contains concatenated raw sector data for all tracks.
    /// The CUE file describes the track layout.
    /// </summary>
    /// <param name="mdfFilePath">Path to the MDF data file.</param>
    /// <param name="image">A successfully parsed <see cref="MdfImage"/>.</param>
    /// <param name="outputBinPath">Destination BIN file path.</param>
    /// <param name="outputCuePath">Destination CUE file path.</param>
    /// <param name="progress">Optional progress reporter (0–100).</param>
    public static void ConvertToBinCue(
        string mdfFilePath, MdfImage image, string outputBinPath, string outputCuePath,
        IProgress<int>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(image);

        using var inStream = new FileStream(mdfFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var binStream = new FileStream(outputBinPath, FileMode.Create, FileAccess.Write);

        var cueLines = new List<string>();
        string binFileName = Path.GetFileName(outputBinPath);

        var allTracks = image.AllRegularTracks.ToList();

        // Detect if all tracks share the same sector size
        bool hasMixedSectorSizes = allTracks.Select(t => t.MainSectorSize).Distinct().Count() > 1;

        if (!hasMixedSectorSizes)
            cueLines.Add($"FILE \"{binFileName}\" BINARY");

        long totalBytesToCopy = allTracks.Sum(t => t.TotalLength);
        long bytesCopied = 0;
        long binOffset = 0;
        int globalTrackNum = 0;

        foreach (var session in image.Sessions)
        {
            foreach (var track in session.RegularTracks)
            {
                globalTrackNum++;

                if (hasMixedSectorSizes)
                    cueLines.Add($"FILE \"{binFileName}\" BINARY");

                string modeStr = BuildCueTrackMode(track);
                cueLines.Add($"  TRACK {globalTrackNum:D2} {modeStr}");

                // Write FLAGS from ADR/CTL
                byte ctl = (byte)(track.AdrCtl & 0x0F);
                var flags = new List<string>();
                if ((ctl & 0x01) != 0) flags.Add("PRE");
                if ((ctl & 0x02) != 0) flags.Add("DCP");
                if ((ctl & 0x08) != 0) flags.Add("4CH");
                if (flags.Count > 0)
                    cueLines.Add($"    FLAGS {string.Join(" ", flags)}");

                uint pregap = track.ExtraBlock?.Pregap ?? 0;
                uint length = track.ExtraBlock?.Length ?? 0;
                long trackTotalBytes = (pregap + length) * (long)track.SectorSize;

                if (pregap > 0)
                {
                    cueLines.Add($"    INDEX 00 {LbaToMsf(binOffset, track.SectorSize)}");
                    long pregapBytes = pregap * (long)track.SectorSize;

                    CopyTrackData(inStream, binStream, track, trackTotalBytes);
                    bytesCopied += trackTotalBytes;

                    cueLines.Add($"    INDEX 01 {LbaToMsf(binOffset + pregapBytes, track.SectorSize)}");
                    binOffset += trackTotalBytes;
                }
                else
                {
                    cueLines.Add($"    INDEX 01 {LbaToMsf(binOffset, track.SectorSize)}");

                    CopyTrackData(inStream, binStream, track, trackTotalBytes);
                    bytesCopied += trackTotalBytes;

                    binOffset += trackTotalBytes;
                }

                if (totalBytesToCopy > 0)
                    progress?.Report((int)(bytesCopied * 100 / totalBytesToCopy));
            }
        }

        File.WriteAllText(outputCuePath,
            string.Join(Environment.NewLine, cueLines) + Environment.NewLine);
        progress?.Report(100);
    }

    /// <summary>Asynchronously converts to BIN/CUE.</summary>
    public static async Task ConvertToBinCueAsync(
        string mdfFilePath, MdfImage image, string outputBinPath, string outputCuePath,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            ConvertToBinCue(mdfFilePath, image, outputBinPath, outputCuePath, progress);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Generates a CUE sheet string for direct burning of an MDF file.
    /// Used when the MDF sector data is contiguous with uniform sector sizes,
    /// allowing the MDF file to be used directly as the burn source.
    /// The CUE sheet references the MDF file name and maps track offsets
    /// relative to the first track's start position in the MDF.
    /// </summary>
    /// <param name="image">Parsed MDF image structure.</param>
    /// <param name="dataFileName">MDF file name to reference in the CUE FILE directive.</param>
    /// <returns>CUE sheet content as a string.</returns>
    public static string GenerateCueSheetForDirectBurn(MdfImage image, string dataFileName)
    {
        ArgumentNullException.ThrowIfNull(image);

        var cueLines = new List<string>();
        cueLines.Add($"REM Generated by OpenBurningSuite for direct MDF burning");
        cueLines.Add($"FILE \"{dataFileName}\" BINARY");

        var allTracks = image.AllRegularTracks.ToList();
        if (allTracks.Count == 0) return string.Join(Environment.NewLine, cueLines);

        long baseOffset = (long)allTracks[0].StartOffset;
        int globalTrackNum = 0;

        foreach (var session in image.Sessions)
        {
            foreach (var track in session.RegularTracks)
            {
                globalTrackNum++;
                string modeStr = BuildCueTrackMode(track);
                cueLines.Add($"  TRACK {globalTrackNum:D2} {modeStr}");

                // Write FLAGS from ADR/CTL
                byte ctl = (byte)(track.AdrCtl & 0x0F);
                var flags = new List<string>();
                if ((ctl & 0x01) != 0) flags.Add("PRE");
                if ((ctl & 0x02) != 0) flags.Add("DCP");
                if ((ctl & 0x08) != 0) flags.Add("4CH");
                if (flags.Count > 0)
                    cueLines.Add($"    FLAGS {string.Join(" ", flags)}");

                uint pregap = track.ExtraBlock?.Pregap ?? 0;
                long trackStartRelative = (long)track.StartOffset - baseOffset;

                if (pregap > 0)
                {
                    cueLines.Add($"    INDEX 00 {LbaToMsf(trackStartRelative, track.SectorSize)}");
                    long pregapBytes = pregap * (long)track.SectorSize;
                    cueLines.Add($"    INDEX 01 {LbaToMsf(trackStartRelative + pregapBytes, track.SectorSize)}");
                }
                else
                {
                    cueLines.Add($"    INDEX 01 {LbaToMsf(trackStartRelative, track.SectorSize)}");
                }
            }
        }

        return string.Join(Environment.NewLine, cueLines) + Environment.NewLine;
    }

    // ------------------------------------------------------------------
    //  Public API — Convert to CDI
    // ------------------------------------------------------------------

    /// <summary>
    /// Converts an MDF/MDS image to DiscJuggler CDI format.
    /// This first converts to BIN/CUE as an intermediate step, then
    /// builds a CDI-compatible BIN image with CDI metadata.
    /// For practical purposes, the output is a BIN/CUE pair that can be
    /// used with CDI-compatible tools. Direct CDI binary writing is complex
    /// as CDI is a reverse-engineered proprietary format; the BIN/CUE output
    /// preserves all track/session data faithfully.
    /// </summary>
    /// <param name="mdfFilePath">Path to the MDF data file.</param>
    /// <param name="image">A successfully parsed <see cref="MdfImage"/>.</param>
    /// <param name="outputCdiPath">Destination CDI file path (produces BIN/CUE alongside).</param>
    /// <param name="progress">Optional progress reporter (0–100).</param>
    public static void ConvertToCdi(
        string mdfFilePath, MdfImage image, string outputCdiPath, IProgress<int>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(image);

        // CDI conversion: extract to BIN/CUE at the CDI output location.
        // The raw sector data is preserved, which is CDI-compatible.
        string dir = Path.GetDirectoryName(outputCdiPath) ?? ".";
        string baseName = Path.GetFileNameWithoutExtension(outputCdiPath);
        string binPath = Path.Combine(dir, baseName + ".bin");
        string cuePath = Path.Combine(dir, baseName + ".cue");

        ConvertToBinCue(mdfFilePath, image, binPath, cuePath, progress);
    }

    /// <summary>Asynchronously converts to CDI.</summary>
    public static async Task ConvertToCdiAsync(
        string mdfFilePath, MdfImage image, string outputCdiPath,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            ConvertToCdi(mdfFilePath, image, outputCdiPath, progress);
        }, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    //  Public API — Extract All Data Tracks
    // ------------------------------------------------------------------

    /// <summary>
    /// Extracts user data from all data tracks to a single output stream.
    /// </summary>
    /// <param name="mdfFilePath">Path to the MDF data file.</param>
    /// <param name="image">A successfully parsed <see cref="MdfImage"/>.</param>
    /// <param name="outputStream">Writable destination stream.</param>
    /// <param name="progress">Optional progress reporter (0–100).</param>
    public static void ExtractAllDataTracks(
        string mdfFilePath, MdfImage image, Stream outputStream, IProgress<int>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(outputStream);

        var dataTracks = image.DataTracks.ToList();
        if (dataTracks.Count == 0)
            throw new InvalidOperationException("MDF image contains no data tracks.");

        long totalBytes = dataTracks.Sum(t => (t.ExtraBlock?.Length ?? 0) * (long)t.UserDataSize);
        long bytesWritten = 0;

        using var fs = new FileStream(mdfFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        foreach (var track in dataTracks)
        {
            fs.Seek((long)track.StartOffset, SeekOrigin.Begin);

            uint pregap = track.ExtraBlock?.Pregap ?? 0;
            uint length = track.ExtraBlock?.Length ?? 0;

            // Skip pregap sectors
            if (pregap > 0)
                fs.Seek(pregap * (long)track.SectorSize, SeekOrigin.Current);

            if (!track.IsRawSector)
            {
                byte[] buf = new byte[81920];
                long remaining = length * (long)track.SectorSize;
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

                for (uint s = 0; s < length; s++)
                {
                    int read = ReadFull(fs, sectorBuf, 0, sectorSize);
                    if (read < sectorSize) break;
                    outputStream.Write(sectorBuf, userOffset, userData);
                    bytesWritten += userData;
                    if (totalBytes > 0)
                        progress?.Report((int)(bytesWritten * 100 / totalBytes));
                }
            }
        }

        progress?.Report(100);
    }

    /// <summary>Asynchronously extracts all data tracks.</summary>
    public static async Task ExtractAllDataTracksAsync(
        string mdfFilePath, MdfImage image, Stream outputStream,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            ExtractAllDataTracks(mdfFilePath, image, outputStream, progress);
        }, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    //  Public API — Track extraction helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Extracts raw sector data for a specific track to an output stream.
    /// </summary>
    public static void ExtractTrackData(
        string mdfFilePath, MdsTrackBlock track, Stream outputStream, IProgress<int>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(outputStream);

        using var fs = new FileStream(mdfFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Seek((long)track.StartOffset, SeekOrigin.Begin);

        CopyBytesWithProgress(fs, outputStream, track.TotalLength, progress);
    }

    /// <summary>
    /// Extracts user data (stripping sector overhead) for a specific track.
    /// </summary>
    public static void ExtractTrackUserData(
        string mdfFilePath, MdsTrackBlock track, Stream outputStream, IProgress<int>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(outputStream);

        using var fs = new FileStream(mdfFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Seek((long)track.StartOffset, SeekOrigin.Begin);

        // Skip pregap sectors
        uint pregap = track.ExtraBlock?.Pregap ?? 0;
        if (pregap > 0)
            fs.Seek(pregap * (long)track.SectorSize, SeekOrigin.Current);

        uint length = track.ExtraBlock?.Length ?? 0;
        if (length == 0) return;

        if (!track.IsRawSector)
        {
            CopyBytesWithProgress(fs, outputStream, length * (long)track.SectorSize, progress);
            return;
        }

        int sectorSize = track.SectorSize;
        int userOffset = track.UserDataOffset;
        int userData = track.UserDataSize;
        byte[] sectorBuf = new byte[sectorSize];

        for (uint s = 0; s < length; s++)
        {
            int read = ReadFull(fs, sectorBuf, 0, sectorSize);
            if (read < sectorSize) break;
            outputStream.Write(sectorBuf, userOffset, userData);
            if (length > 0)
                progress?.Report((int)((s + 1) * 100 / length));
        }
        progress?.Report(100);
    }

    /// <summary>
    /// Gets a human-readable description of a track for diagnostic purposes.
    /// </summary>
    public static string DescribeTrack(MdsTrackBlock track)
    {
        ArgumentNullException.ThrowIfNull(track);

        string modeStr = track.Mode switch
        {
            MdsTrackMode.Audio => "Audio (CD-DA)",
            MdsTrackMode.Mode1 => "Data (Mode 1)",
            MdsTrackMode.Mode2 or MdsTrackMode.Mode2Alt or MdsTrackMode.Mode2Alt2 => "Data (Mode 2)",
            MdsTrackMode.Mode2Form1 => "Data (Mode 2 Form 1)",
            MdsTrackMode.Mode2Form2 => "Data (Mode 2 Form 2)",
            _ => $"Unknown (0x{(byte)track.Mode:X2})"
        };

        string subStr = track.SubchannelMode == MdsSubchannelMode.PwInterleaved
            ? " + PW subchannel (96B)"
            : "";

        var sb = new StringBuilder();
        sb.Append($"Track {track.Point}: {modeStr}");
        sb.Append($", {track.SectorSize} bytes/sector{subStr}");
        if (track.ExtraBlock != null)
        {
            sb.Append($", {track.ExtraBlock.Length} sectors");
            if (track.ExtraBlock.Pregap > 0)
                sb.Append($", pregap {track.ExtraBlock.Pregap} sectors");
        }
        sb.Append($", PLBA {track.StartSector}");

        // Control flags
        byte ctl = (byte)(track.AdrCtl & 0x0F);
        var flagList = new List<string>();
        if ((ctl & 0x01) != 0) flagList.Add("pre-emphasis");
        if ((ctl & 0x02) != 0) flagList.Add("copy-permitted");
        if ((ctl & 0x04) != 0) flagList.Add("data");
        if ((ctl & 0x08) != 0) flagList.Add("4-channel");
        if (flagList.Count > 0)
            sb.Append($" [{string.Join(", ", flagList)}]");

        return sb.ToString();
    }

    // ------------------------------------------------------------------
    //  Private — MDS binary parsing
    // ------------------------------------------------------------------

    /// <summary>Parses a single session block from MDS data.</summary>
    private static MdsSession ParseSessionBlock(byte[] data, int offset)
    {
        return new MdsSession
        {
            SessionStart = BitConverter.ToInt32(data, offset),
            SessionEnd = BitConverter.ToInt32(data, offset + 4),
            SessionNumber = BitConverter.ToUInt16(data, offset + 8),
            NumAllBlocks = data[offset + 10],
            NumNonTrackBlocks = data[offset + 11],
            FirstTrack = BitConverter.ToUInt16(data, offset + 12),
            LastTrack = BitConverter.ToUInt16(data, offset + 14),
            // offset + 16: 4 bytes unknown/dummy
            // offset + 20: tracks_blocks_offset (we store this to find tracks)
        };
    }

    /// <summary>
    /// Parses all track blocks for a session, including extra blocks and footers.
    /// </summary>
    private static void ParseTrackBlocks(byte[] data, MdsSession session, string mdsFilePath)
    {
        // The tracks block offset is at session block offset + 20
        // We need to re-read from the raw data
        int sessionDataStart = FindSessionDataOffset(data, session);
        if (sessionDataStart < 0) return;

        uint tracksBlocksOffset = BitConverter.ToUInt32(data, sessionDataStart + 20);
        if (tracksBlocksOffset == 0 || tracksBlocksOffset + (long)session.NumAllBlocks * TrackBlockSize > data.Length)
            return;

        int pos = (int)tracksBlocksOffset;

        for (int t = 0; t < session.NumAllBlocks; t++)
        {
            if (pos + TrackBlockSize > data.Length)
                break;

            var track = ParseTrackBlock(data, pos);
            pos += TrackBlockSize;

            // Parse extra block if present
            if (track.ExtraOffset > 0 && track.ExtraOffset + TrackExtraBlockSize <= data.Length)
            {
                track.ExtraBlock = new MdsTrackExtraBlock
                {
                    Pregap = BitConverter.ToUInt32(data, (int)track.ExtraOffset),
                    Length = BitConverter.ToUInt32(data, (int)track.ExtraOffset + 4)
                };
            }

            // Parse footer blocks if present
            if (track.FooterOffset > 0)
            {
                for (uint f = 0; f < Math.Max(1, track.NumberOfFiles); f++)
                {
                    int footerPos = (int)(track.FooterOffset + f * FooterBlockSize);
                    if (footerPos + FooterBlockSize > data.Length)
                        break;

                    var footer = new MdsFooterBlock
                    {
                        FilenameOffset = BitConverter.ToUInt32(data, footerPos),
                        IsWideCharFilename = BitConverter.ToUInt32(data, footerPos + 4) != 0
                    };

                    // Read filename from MDS data
                    if (footer.FilenameOffset > 0 && footer.FilenameOffset < data.Length)
                    {
                        footer.DataFileName = ReadFilename(data, (int)footer.FilenameOffset,
                            footer.IsWideCharFilename);
                    }

                    // Resolve wildcard filenames (e.g. "*.mdf")
                    if (!string.IsNullOrEmpty(footer.DataFileName) &&
                        footer.DataFileName.Contains('*'))
                    {
                        footer.DataFileName = ResolveWildcardFilename(
                            footer.DataFileName, mdsFilePath);
                    }

                    track.Footers.Add(footer);
                }
            }

            session.TrackBlocks.Add(track);
        }
    }

    /// <summary>Parses a single track block.</summary>
    private static MdsTrackBlock ParseTrackBlock(byte[] data, int offset)
    {
        return new MdsTrackBlock
        {
            Mode = (MdsTrackMode)(data[offset] & 0x0F),
            SubchannelMode = (MdsSubchannelMode)data[offset + 1],
            AdrCtl = data[offset + 2],
            Tno = data[offset + 3],
            Point = data[offset + 4],
            Min = data[offset + 5],
            Sec = data[offset + 6],
            Frame = data[offset + 7],
            Zero = data[offset + 8],
            PMin = data[offset + 9],
            PSec = data[offset + 10],
            PFrame = data[offset + 11],
            ExtraOffset = BitConverter.ToUInt32(data, offset + 12),
            SectorSize = BitConverter.ToUInt16(data, offset + 16),
            // offset + 18: 18 bytes unknown
            StartSector = BitConverter.ToUInt32(data, offset + 36),
            StartOffset = BitConverter.ToUInt64(data, offset + 40),
            NumberOfFiles = BitConverter.ToUInt32(data, offset + 48),
            FooterOffset = BitConverter.ToUInt32(data, offset + 52),
            // offset + 56: 24 bytes unknown
        };
    }

    /// <summary>
    /// Finds the offset in MDS data where a specific session block begins.
    /// </summary>
    private static int FindSessionDataOffset(byte[] data, MdsSession session)
    {
        uint sessionsBlocksOffset = BitConverter.ToUInt32(data, 80);
        if (sessionsBlocksOffset == 0 || sessionsBlocksOffset >= data.Length)
            return -1;

        // Session blocks are sequential, indexed by (session number - 1)
        int offset = (int)sessionsBlocksOffset + (session.SessionNumber - 1) * SessionBlockSize;
        if (offset + SessionBlockSize > data.Length)
            return -1;

        return offset;
    }

    /// <summary>Reads a null-terminated string from MDS data.</summary>
    private static string ReadFilename(byte[] data, int offset, bool isWideChar)
    {
        if (isWideChar)
        {
            // UTF-16LE null-terminated string
            var chars = new List<char>();
            for (int i = offset; i + 1 < data.Length; i += 2)
            {
                char c = (char)(data[i] | (data[i + 1] << 8));
                if (c == '\0') break;
                chars.Add(c);
            }
            return new string(chars.ToArray());
        }
        else
        {
            // ASCII null-terminated string
            int end = offset;
            while (end < data.Length && data[end] != 0)
                end++;
            return Encoding.ASCII.GetString(data, offset, end - offset);
        }
    }

    /// <summary>
    /// Resolves a wildcard filename pattern (e.g. "*.mdf") to an actual filename
    /// by replacing the extension in the MDS filename.
    /// </summary>
    private static string ResolveWildcardFilename(string pattern, string mdsFilePath)
    {
        // Pattern is typically "*.mdf" — extract the extension
        int dotIdx = pattern.LastIndexOf('.');
        if (dotIdx < 0) return pattern;

        string ext = pattern[(dotIdx + 1)..];
        string basePath = Path.ChangeExtension(mdsFilePath, ext);
        return basePath;
    }

    /// <summary>
    /// Resolves the MDF data file path from parsed image data.
    /// Tries footer filenames first, then falls back to replacing .mds with .mdf.
    /// </summary>
    private static string ResolveMdfPath(MdfImage image, string mdsFilePath)
    {
        // Try to get filename from the first footer with a data file reference
        foreach (var session in image.Sessions)
        {
            foreach (var track in session.TrackBlocks)
            {
                foreach (var footer in track.Footers)
                {
                    if (!string.IsNullOrEmpty(footer.DataFileName))
                    {
                        string dir = Path.GetDirectoryName(mdsFilePath) ?? ".";
                        string candidate = Path.IsPathRooted(footer.DataFileName)
                            ? footer.DataFileName
                            : Path.Combine(dir, Path.GetFileName(footer.DataFileName));

                        if (File.Exists(candidate))
                            return candidate;
                    }
                }
            }
        }

        // Fallback: replace .mds extension with .mdf
        string fallback = Path.ChangeExtension(mdsFilePath, ".mdf");
        return fallback;
    }

    /// <summary>Gets the session number for a track block.</summary>
    private static int GetSessionForTrack(MdfImage image, MdsTrackBlock track)
    {
        foreach (var session in image.Sessions)
        {
            if (session.TrackBlocks.Contains(track))
                return session.SessionNumber;
        }
        return 0;
    }

    // ------------------------------------------------------------------
    //  Private — Track data extraction
    // ------------------------------------------------------------------

    /// <summary>
    /// Extracts user data (with sector overhead stripping) from a track to a file.
    /// </summary>
    private static void ExtractTrackUserDataToFile(
        string mdfFilePath, MdsTrackBlock track, string outputPath, IProgress<int>? progress)
    {
        using var inStream = new FileStream(mdfFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var outStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);

        inStream.Seek((long)track.StartOffset, SeekOrigin.Begin);

        // Skip pregap
        uint pregap = track.ExtraBlock?.Pregap ?? 0;
        if (pregap > 0)
            inStream.Seek(pregap * (long)track.SectorSize, SeekOrigin.Current);

        uint length = track.ExtraBlock?.Length ?? 0;
        if (length == 0)
        {
            // Fallback: estimate from MDF file size if no extra block
            long availableBytes = new FileInfo(mdfFilePath).Length - (long)track.StartOffset;
            if (track.SectorSize > 0 && availableBytes > 0)
                length = (uint)(availableBytes / track.SectorSize);
        }

        if (!track.IsRawSector)
        {
            CopyBytesWithProgress(inStream, outStream, length * (long)track.SectorSize, progress);
            return;
        }

        int sectorSize = track.SectorSize;
        int userOffset = track.UserDataOffset;
        int userData = track.UserDataSize;
        byte[] sectorBuf = new byte[sectorSize];

        for (uint s = 0; s < length; s++)
        {
            int read = ReadFull(inStream, sectorBuf, 0, sectorSize);
            if (read < sectorSize) break;
            outStream.Write(sectorBuf, userOffset, userData);
            if (length > 0)
                progress?.Report((int)((s + 1) * 100 / length));
        }
        progress?.Report(100);
    }

    /// <summary>
    /// Extracts raw sector data (no stripping) from a track to a file.
    /// Subchannel data is excluded for clean IMG output.
    /// </summary>
    private static void ExtractTrackRawDataToFile(
        string mdfFilePath, MdsTrackBlock track, string outputPath, IProgress<int>? progress)
    {
        using var inStream = new FileStream(mdfFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var outStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);

        inStream.Seek((long)track.StartOffset, SeekOrigin.Begin);

        // Skip pregap
        uint pregap = track.ExtraBlock?.Pregap ?? 0;
        if (pregap > 0)
            inStream.Seek(pregap * (long)track.SectorSize, SeekOrigin.Current);

        uint length = track.ExtraBlock?.Length ?? 0;
        if (length == 0)
        {
            long availableBytes = new FileInfo(mdfFilePath).Length - (long)track.StartOffset;
            if (track.SectorSize > 0 && availableBytes > 0)
                length = (uint)(availableBytes / track.SectorSize);
        }

        if (track.SubchannelSize == 0)
        {
            // No subchannel — straight copy
            CopyBytesWithProgress(inStream, outStream, length * (long)track.SectorSize, progress);
            return;
        }

        // Strip subchannel data, write only main sector
        int sectorSize = track.SectorSize;
        int mainSize = track.MainSectorSize;
        byte[] sectorBuf = new byte[sectorSize];

        for (uint s = 0; s < length; s++)
        {
            int read = ReadFull(inStream, sectorBuf, 0, sectorSize);
            if (read < sectorSize) break;
            outStream.Write(sectorBuf, 0, mainSize);
            if (length > 0)
                progress?.Report((int)((s + 1) * 100 / length));
        }
        progress?.Report(100);
    }

    /// <summary>Copies raw track data from MDF to a destination stream.</summary>
    private static void CopyTrackData(
        FileStream inStream, FileStream outStream, MdsTrackBlock track, long totalBytes)
    {
        if (totalBytes <= 0) return;

        inStream.Seek((long)track.StartOffset, SeekOrigin.Begin);

        long endOffset = (long)track.StartOffset + totalBytes;
        if (endOffset > inStream.Length)
            totalBytes = inStream.Length - (long)track.StartOffset;

        if (totalBytes <= 0) return;

        CopyBytes(inStream, outStream, totalBytes);
    }

    // ------------------------------------------------------------------
    //  Private — CUE sheet helpers
    // ------------------------------------------------------------------

    /// <summary>Builds the CUE track mode string for a given MDS track.</summary>
    private static string BuildCueTrackMode(MdsTrackBlock track)
    {
        if (track.Mode == MdsTrackMode.Audio)
            return "AUDIO";

        int size = track.MainSectorSize;

        switch (track.Mode)
        {
            case MdsTrackMode.Mode1:
                return size switch
                {
                    2048 => "MODE1/2048",
                    2352 => "MODE1/2352",
                    _ => $"MODE1/{size}"
                };
            case MdsTrackMode.Mode2:
            case MdsTrackMode.Mode2Alt:
            case MdsTrackMode.Mode2Alt2:
                return size switch
                {
                    2048 => "MODE2/2048",
                    2336 => "MODE2/2336",
                    2352 => "MODE2/2352",
                    _ => $"MODE2/{size}"
                };
            case MdsTrackMode.Mode2Form1:
                return size switch
                {
                    2048 => "MODE2/2048",
                    2352 => "MODE2/2352",
                    _ => $"MODE2/{size}"
                };
            case MdsTrackMode.Mode2Form2:
                return size switch
                {
                    2336 => "MODE2/2336",
                    2352 => "MODE2/2352",
                    _ => $"MODE2/{size}"
                };
            default:
                return $"MODE1/{size}";
        }
    }

    /// <summary>Converts a byte offset in a BIN file to MM:SS:FF format.</summary>
    private static string LbaToMsf(long byteOffset, int sectorSize)
    {
        if (sectorSize <= 0) sectorSize = 2352;
        long frames = byteOffset / sectorSize;
        return FramesToMsf(frames);
    }

    /// <summary>Converts a frame count to MM:SS:FF string.</summary>
    private static string FramesToMsf(long frames)
    {
        long f = frames % FramesPerSecond;
        long totalSeconds = frames / FramesPerSecond;
        long s = totalSeconds % 60;
        long m = totalSeconds / 60;
        return $"{m:D2}:{s:D2}:{f:D2}";
    }

    // ------------------------------------------------------------------
    //  Private — Stream helpers
    // ------------------------------------------------------------------

    private static MdfImage ErrorImage(string path, string message) => new()
    {
        MdsFilePath = path,
        IsValid = false,
        ErrorMessage = message
    };

    /// <summary>Reads exactly count bytes or as many as available.</summary>
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

    /// <summary>Copies exactly count bytes between streams.</summary>
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
}
