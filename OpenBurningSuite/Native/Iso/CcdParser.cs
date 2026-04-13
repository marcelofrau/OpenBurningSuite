// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenBurningSuite.Models;

namespace OpenBurningSuite.Native.Iso;

/// <summary>
/// Parser for CloneCD Control (.ccd) disc image files.
///
/// The CloneCD format consists of three files:
///   .ccd — INI-style text file describing the disc structure (TOC, tracks, sessions)
///   .img — Raw 2352-byte sector data for the entire disc
///   .sub — (Optional) 96-byte interleaved P-W subchannel data per sector
///
/// This parser reads the .ccd descriptor and provides methods to:
/// - Parse the disc structure (sessions, tracks, TOC entries)
/// - Extract user data from specific tracks
/// - Convert to ISO (extracting Mode 1 user data from raw sectors)
/// - Convert to BIN/CUE format
/// - Validate the image for integrity
/// </summary>
public static class CcdParser
{
    // -----------------------------------------------------------------------
    //  Constants
    // -----------------------------------------------------------------------

    /// <summary>Raw sector size: all CCD images use 2352-byte sectors.</summary>
    private const int RawSectorSize = 2352;

    /// <summary>Subchannel data size per sector.</summary>
    private const int SubchannelSize = 96;

    /// <summary>Mode 1 user data offset within a raw sector (after 12-byte sync + 4-byte header).</summary>
    private const int Mode1UserDataOffset = 16;

    /// <summary>Mode 1 user data size per sector.</summary>
    private const int Mode1UserDataSize = 2048;

    /// <summary>Mode 2 user data offset within a raw sector.</summary>
    private const int Mode2UserDataOffset = 16;

    /// <summary>Mode 2 user data size per sector (without Form distinction).</summary>
    private const int Mode2UserDataSize = 2336;

    /// <summary>Mode 2 Form 1 user data offset (after header + subheader).</summary>
    private const int Mode2Form1UserDataOffset = 24;

    /// <summary>Mode 2 Form 1 user data size.</summary>
    private const int Mode2Form1UserDataSize = 2048;

    /// <summary>Mode 2 Form 2 user data offset (after header + subheader).</summary>
    private const int Mode2Form2UserDataOffset = 24;

    /// <summary>Mode 2 Form 2 user data size.</summary>
    private const int Mode2Form2UserDataSize = 2328;

    /// <summary>Standard copy buffer size.</summary>
    private const int BufferSize = 81920;

    // -----------------------------------------------------------------------
    //  Public API — Parse
    // -----------------------------------------------------------------------

    /// <summary>
    /// Parses a CCD file and returns the disc image structure.
    /// </summary>
    /// <param name="ccdFilePath">Path to the .ccd file.</param>
    /// <returns>Parsed CCD image structure.</returns>
    public static CcdImage Parse(string ccdFilePath)
    {
        var image = new CcdImage { CcdFilePath = ccdFilePath };

        try
        {
            if (!File.Exists(ccdFilePath))
            {
                image.ErrorMessage = $"CCD file not found: {ccdFilePath}";
                return image;
            }

            // Derive companion file paths
            image.ImgFilePath = Path.ChangeExtension(ccdFilePath, ".img");
            image.SubFilePath = Path.ChangeExtension(ccdFilePath, ".sub");
            image.HasSubchannel = File.Exists(image.SubFilePath);

            if (!File.Exists(image.ImgFilePath))
            {
                image.ErrorMessage = $"IMG file not found: {image.ImgFilePath}";
                return image;
            }

            // Parse the INI-style CCD content
            var lines = File.ReadAllLines(ccdFilePath, Encoding.Default);
            ParseCcdContent(lines, image);

            image.IsValid = true;
        }
        catch (Exception ex)
        {
            image.ErrorMessage = $"Parse error: {ex.Message}";
        }

        return image;
    }

    /// <summary>Asynchronously parses a CCD file.</summary>
    public static async Task<CcdImage> ParseAsync(string ccdFilePath, CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            return Parse(ccdFilePath);
        }, ct).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------
    //  Public API — Extract to ISO
    // -----------------------------------------------------------------------

    /// <summary>
    /// Extracts user data from the CCD image to an ISO file.
    /// Uses the largest data track for extraction (Mode 1 = 2048-byte user data).
    /// </summary>
    public static void ExtractToIso(string imgFilePath, CcdImage image, string outputIsoPath,
        IProgress<int>? progress = null)
    {
        var track = image.LargestDataTrack;
        if (track == null)
            throw new InvalidOperationException("No data tracks found in the CCD image.");

        var trackLength = image.GetTrackLength(track.TrackNumber);
        if (trackLength <= 0)
            throw new InvalidOperationException($"Track {track.TrackNumber} has no data.");

        int userDataSize, userDataOffset;
        DetermineUserDataLayout(track.Mode, imgFilePath, track.StartLba, out userDataSize, out userDataOffset);

        using var imgStream = new FileStream(imgFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var isoStream = new FileStream(outputIsoPath, FileMode.Create, FileAccess.Write);

        var sectorBuffer = new byte[RawSectorSize];
        long startOffset = (long)track.StartLba * RawSectorSize;
        imgStream.Seek(startOffset, SeekOrigin.Begin);

        for (int i = 0; i < trackLength; i++)
        {
            int bytesRead = imgStream.Read(sectorBuffer, 0, RawSectorSize);
            if (bytesRead < RawSectorSize) break;

            isoStream.Write(sectorBuffer, userDataOffset, userDataSize);
            progress?.Report((int)((long)(i + 1) * 100 / trackLength));
        }
    }

    /// <summary>Asynchronously extracts user data to ISO.</summary>
    public static async Task ExtractToIsoAsync(string imgFilePath, CcdImage image,
        string outputIsoPath, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            ExtractToIso(imgFilePath, image, outputIsoPath, progress);
        }, ct).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------
    //  Public API — Convert to BIN/CUE
    // -----------------------------------------------------------------------

    /// <summary>
    /// Converts a CCD image to BIN/CUE format.
    /// The BIN file is a raw copy of the IMG sectors; the CUE sheet is generated
    /// from the CCD track layout.
    /// </summary>
    public static void ConvertToBinCue(string imgFilePath, CcdImage image,
        string outputBinPath, string outputCuePath, IProgress<int>? progress = null)
    {
        // Copy IMG to BIN (they are the same format: raw 2352-byte sectors)
        CopyFileWithProgress(imgFilePath, outputBinPath, progress);

        // Generate CUE sheet
        var cueContent = GenerateCueSheet(image, Path.GetFileName(outputBinPath));
        File.WriteAllText(outputCuePath, cueContent, Encoding.ASCII);
    }

    /// <summary>Asynchronously converts to BIN/CUE.</summary>
    public static async Task ConvertToBinCueAsync(string imgFilePath, CcdImage image,
        string outputBinPath, string outputCuePath,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            ConvertToBinCue(imgFilePath, image, outputBinPath, outputCuePath, progress);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Generates a CUE sheet string that references the specified file name.
    /// Used for direct burning: the CCD .img file is already raw 2352-byte sectors
    /// (identical to BIN format), so only a CUE sheet is needed — no data copy.
    /// </summary>
    /// <param name="image">Parsed CCD image structure.</param>
    /// <param name="dataFileName">File name to reference in the CUE sheet FILE directive.</param>
    /// <returns>CUE sheet content as a string.</returns>
    public static string GenerateCueSheetForFile(CcdImage image, string dataFileName)
    {
        return GenerateCueSheet(image, dataFileName);
    }

    // -----------------------------------------------------------------------
    //  Public API — Subchannel data access
    // -----------------------------------------------------------------------

    /// <summary>
    /// Parses the companion .sub file for a CCD image.
    /// Returns the parsed subchannel data, or null if no .sub file exists.
    /// </summary>
    /// <param name="image">Parsed CCD image with HasSubchannel and SubFilePath set.</param>
    /// <returns>Parsed SubImage, or null if .sub file is missing.</returns>
    public static SubImage? ParseSubchannelData(CcdImage image)
    {
        if (!image.HasSubchannel || string.IsNullOrEmpty(image.SubFilePath))
            return null;

        if (!File.Exists(image.SubFilePath))
            return null;

        return SubParser.Parse(image.SubFilePath);
    }

    /// <summary>
    /// Asynchronously parses the companion .sub file for a CCD image.
    /// </summary>
    public static async Task<SubImage?> ParseSubchannelDataAsync(
        CcdImage image, CancellationToken ct = default)
    {
        if (!image.HasSubchannel || string.IsNullOrEmpty(image.SubFilePath))
            return null;

        if (!File.Exists(image.SubFilePath))
            return null;

        return await SubParser.ParseAsync(image.SubFilePath, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the Q subchannel data for a specific sector from the .sub file.
    /// Returns 12 bytes of Q subchannel data, or null if unavailable.
    /// </summary>
    public static byte[]? ReadQSubchannel(CcdImage image, long sectorIndex)
    {
        if (!image.HasSubchannel || string.IsNullOrEmpty(image.SubFilePath))
            return null;

        return SubParser.ReadQSubchannel(image.SubFilePath, sectorIndex);
    }

    // -----------------------------------------------------------------------
    //  Public API — Extract all data tracks
    // -----------------------------------------------------------------------

    /// <summary>
    /// Extracts user data from all data tracks to an output stream.
    /// </summary>
    public static void ExtractAllDataTracks(string imgFilePath, CcdImage image,
        Stream outputStream, IProgress<int>? progress = null)
    {
        var dataTracks = image.Tracks.Where(t => t.IsData).OrderBy(t => t.TrackNumber).ToList();
        if (dataTracks.Count == 0)
            throw new InvalidOperationException("No data tracks found.");

        long totalSectors = dataTracks.Sum(t => (long)image.GetTrackLength(t.TrackNumber));
        long sectorsProcessed = 0;

        using var imgStream = new FileStream(imgFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var sectorBuffer = new byte[RawSectorSize];

        foreach (var track in dataTracks)
        {
            var trackLength = image.GetTrackLength(track.TrackNumber);
            if (trackLength <= 0) continue;

            int userDataSize, userDataOffset;
            DetermineUserDataLayout(track.Mode, imgFilePath, track.StartLba, out userDataSize, out userDataOffset);

            long startOffset = (long)track.StartLba * RawSectorSize;
            imgStream.Seek(startOffset, SeekOrigin.Begin);

            for (int i = 0; i < trackLength; i++)
            {
                int bytesRead = imgStream.Read(sectorBuffer, 0, RawSectorSize);
                if (bytesRead < RawSectorSize) break;

                outputStream.Write(sectorBuffer, userDataOffset, userDataSize);
                sectorsProcessed++;

                if (totalSectors > 0)
                    progress?.Report((int)(sectorsProcessed * 100 / totalSectors));
            }
        }
    }

    /// <summary>Asynchronously extracts all data tracks.</summary>
    public static async Task ExtractAllDataTracksAsync(string imgFilePath, CcdImage image,
        Stream outputStream, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            ExtractAllDataTracks(imgFilePath, image, outputStream, progress);
        }, ct).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------
    //  Public API — Extract track data (raw)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Extracts raw sector data for a specific track.
    /// </summary>
    public static void ExtractTrackData(string imgFilePath, CcdImage image,
        int trackNumber, Stream outputStream, IProgress<int>? progress = null)
    {
        var track = image.GetTrackByNumber(trackNumber);
        if (track == null)
            throw new ArgumentException($"Track {trackNumber} not found.", nameof(trackNumber));

        var trackLength = image.GetTrackLength(trackNumber);
        if (trackLength <= 0)
            throw new InvalidOperationException($"Track {trackNumber} has no data.");

        using var imgStream = new FileStream(imgFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var sectorBuffer = new byte[RawSectorSize];

        long startOffset = (long)track.StartLba * RawSectorSize;
        imgStream.Seek(startOffset, SeekOrigin.Begin);

        for (int i = 0; i < trackLength; i++)
        {
            int bytesRead = imgStream.Read(sectorBuffer, 0, RawSectorSize);
            if (bytesRead < RawSectorSize) break;

            outputStream.Write(sectorBuffer, 0, RawSectorSize);
            progress?.Report((int)((long)(i + 1) * 100 / trackLength));
        }
    }

    /// <summary>Asynchronously extracts raw track data.</summary>
    public static async Task ExtractTrackDataAsync(string imgFilePath, CcdImage image,
        int trackNumber, Stream outputStream,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            ExtractTrackData(imgFilePath, image, trackNumber, outputStream, progress);
        }, ct).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------
    //  Public API — Extract user data
    // -----------------------------------------------------------------------

    /// <summary>
    /// Extracts user data (stripping raw sector headers/ECC) for a specific track.
    /// </summary>
    public static void ExtractUserData(string imgFilePath, CcdImage image,
        int trackNumber, Stream outputStream, IProgress<int>? progress = null)
    {
        var track = image.GetTrackByNumber(trackNumber);
        if (track == null)
            throw new ArgumentException($"Track {trackNumber} not found.", nameof(trackNumber));

        var trackLength = image.GetTrackLength(trackNumber);
        if (trackLength <= 0)
            throw new InvalidOperationException($"Track {trackNumber} has no data.");

        int userDataSize, userDataOffset;
        DetermineUserDataLayout(track.Mode, imgFilePath, track.StartLba, out userDataSize, out userDataOffset);

        using var imgStream = new FileStream(imgFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var sectorBuffer = new byte[RawSectorSize];

        long startOffset = (long)track.StartLba * RawSectorSize;
        imgStream.Seek(startOffset, SeekOrigin.Begin);

        for (int i = 0; i < trackLength; i++)
        {
            int bytesRead = imgStream.Read(sectorBuffer, 0, RawSectorSize);
            if (bytesRead < RawSectorSize) break;

            outputStream.Write(sectorBuffer, userDataOffset, userDataSize);
            progress?.Report((int)((long)(i + 1) * 100 / trackLength));
        }
    }

    /// <summary>Asynchronously extracts user data for a track.</summary>
    public static async Task ExtractUserDataAsync(string imgFilePath, CcdImage image,
        int trackNumber, Stream outputStream,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            ExtractUserData(imgFilePath, image, trackNumber, outputStream, progress);
        }, ct).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------
    //  Public API — Validate
    // -----------------------------------------------------------------------

    /// <summary>
    /// Validates a CCD image for integrity issues.
    /// </summary>
    /// <returns>List of validation issues. Empty list means no problems found.</returns>
    public static List<string> ValidateImage(CcdImage image)
    {
        var issues = new List<string>();

        if (!image.IsValid)
        {
            issues.Add($"Image is not valid: {image.ErrorMessage}");
            return issues;
        }

        // Check IMG file exists and has correct size
        if (!File.Exists(image.ImgFilePath))
        {
            issues.Add($"IMG file not found: {image.ImgFilePath}");
        }
        else
        {
            var imgFileSize = new FileInfo(image.ImgFilePath).Length;
            var expectedSize = image.TotalDiscSize;

            if (expectedSize > 0 && imgFileSize < expectedSize)
            {
                issues.Add($"IMG file is smaller than expected: " +
                    $"{imgFileSize:N0} bytes vs {expectedSize:N0} bytes expected " +
                    $"({image.TotalSectors} sectors × {RawSectorSize} bytes).");
            }

            // Check if IMG file size is aligned to sector size
            if (imgFileSize % RawSectorSize != 0)
            {
                issues.Add($"IMG file size ({imgFileSize:N0}) is not aligned to {RawSectorSize}-byte sectors. " +
                    $"Remainder: {imgFileSize % RawSectorSize} bytes.");
            }
        }

        // Check subchannel file if present
        if (image.HasSubchannel && File.Exists(image.SubFilePath))
        {
            var subFileSize = new FileInfo(image.SubFilePath).Length;
            var imgFileSize = File.Exists(image.ImgFilePath) ? new FileInfo(image.ImgFilePath).Length : 0;
            var expectedSubSize = (imgFileSize / RawSectorSize) * SubchannelSize;

            if (subFileSize != expectedSubSize && expectedSubSize > 0)
            {
                issues.Add($"SUB file size mismatch: {subFileSize:N0} bytes " +
                    $"vs {expectedSubSize:N0} expected.");
            }
        }

        // Validate entries
        if (image.Entries.Count == 0)
            issues.Add("No TOC entries found.");

        if (image.TocEntries != image.Entries.Count)
            issues.Add($"TocEntries count mismatch: header says {image.TocEntries}, " +
                $"found {image.Entries.Count}.");

        // Check for required A0, A1, A2 entries per session
        for (int s = 1; s <= image.SessionCount; s++)
        {
            var sessionEntries = image.Entries.Where(e => e.Session == s).ToList();
            if (!sessionEntries.Any(e => e.IsFirstTrackPointer))
                issues.Add($"Session {s}: missing A0 (first track) entry.");
            if (!sessionEntries.Any(e => e.IsLastTrackPointer))
                issues.Add($"Session {s}: missing A1 (last track) entry.");
            if (!sessionEntries.Any(e => e.IsLeadOutPointer))
                issues.Add($"Session {s}: missing A2 (lead-out) entry.");
        }

        // Validate tracks
        if (image.Tracks.Count == 0)
            issues.Add("No tracks found.");

        foreach (var track in image.Tracks)
        {
            if (!track.Indices.ContainsKey(1) && !track.Indices.ContainsKey(0))
                issues.Add($"Track {track.TrackNumber}: no INDEX 0 or INDEX 1 defined.");

            if (track.StartLba < 0)
                issues.Add($"Track {track.TrackNumber}: negative start LBA ({track.StartLba}).");

            var trackLen = image.GetTrackLength(track.TrackNumber);
            if (trackLen <= 0)
                issues.Add($"Track {track.TrackNumber}: zero or negative length ({trackLen} sectors).");

            // Check track data doesn't extend beyond IMG file
            if (File.Exists(image.ImgFilePath))
            {
                var imgSize = new FileInfo(image.ImgFilePath).Length;
                var trackEnd = ((long)track.StartLba + trackLen) * RawSectorSize;
                if (trackEnd > imgSize)
                    issues.Add($"Track {track.TrackNumber}: data extends beyond IMG file " +
                        $"(end at {trackEnd:N0}, file size {imgSize:N0}).");
            }

            // Validate ISRC format (12 alphanumeric characters)
            if (!string.IsNullOrEmpty(track.Isrc) && track.Isrc.Length != 12)
                issues.Add($"Track {track.TrackNumber}: ISRC '{track.Isrc}' should be 12 characters " +
                    $"(got {track.Isrc.Length}).");
        }

        // Check for overlapping tracks
        var sortedTracks = image.Tracks.OrderBy(t => t.StartLba).ToList();
        for (int i = 0; i < sortedTracks.Count - 1; i++)
        {
            var curr = sortedTracks[i];
            var next = sortedTracks[i + 1];
            var currEnd = curr.StartLba + image.GetTrackLength(curr.TrackNumber);
            if (currEnd > next.StartLba)
                issues.Add($"Track {curr.TrackNumber} (end LBA {currEnd}) overlaps with " +
                    $"track {next.TrackNumber} (start LBA {next.StartLba}).");
        }

        return issues;
    }

    /// <summary>Asynchronously validates the image.</summary>
    public static async Task<List<string>> ValidateImageAsync(CcdImage image,
        CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            return ValidateImage(image);
        }, ct).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------
    //  Public API — Describe track
    // -----------------------------------------------------------------------

    /// <summary>Returns a human-readable description of a track.</summary>
    public static string DescribeTrack(CcdImage image, int trackNumber)
    {
        var track = image.GetTrackByNumber(trackNumber);
        if (track == null) return $"Track {trackNumber}: not found";

        var len = image.GetTrackLength(trackNumber);
        var modeStr = track.Mode switch
        {
            CcdTrackMode.Audio => "Audio (CD-DA)",
            CcdTrackMode.Mode1 => "Data (Mode 1)",
            CcdTrackMode.Mode2 => "Data (Mode 2)",
            _ => $"Unknown ({(int)track.Mode})"
        };

        var sb = new StringBuilder();
        sb.Append($"Track {trackNumber:D2}: {modeStr}");
        sb.Append($", LBA {track.StartLba}");
        sb.Append($", {len} sectors");

        if (track.IsAudio)
            sb.Append($" ({len / 75.0:F1}s)");
        else
            sb.Append($" ({(long)len * track.UserDataSize:N0} bytes)");

        foreach (var idx in track.Indices.OrderBy(kv => kv.Key))
            sb.Append($", INDEX {idx.Key}={idx.Value}");

        if (!string.IsNullOrEmpty(track.Isrc))
            sb.Append($", ISRC: {track.Isrc}");

        return sb.ToString();
    }

    // -----------------------------------------------------------------------
    //  Private — CCD INI parsing
    // -----------------------------------------------------------------------

    /// <summary>
    /// Parses the INI-style CCD content into the image structure.
    /// CCD format uses Windows INI sections like [CloneCD], [Disc], [Session N],
    /// [Entry N], and [TRACK N] with key=value pairs.
    /// </summary>
    private static void ParseCcdContent(string[] lines, CcdImage image)
    {
        string currentSection = string.Empty;
        int currentEntryIndex = -1;
        int currentSessionIndex = -1;
        int currentTrackIndex = -1;

        CcdEntry? currentEntry = null;
        CcdSession? currentSession = null;
        CcdTrack? currentTrack = null;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            // Skip empty lines and comments
            if (string.IsNullOrEmpty(line) || line.StartsWith(';') || line.StartsWith('#'))
                continue;

            // Section header
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                // Save previous pending objects
                FinalizePendingObject(ref currentEntry, image.Entries);
                FinalizePendingObject(ref currentSession, image.Sessions);
                FinalizePendingObject(ref currentTrack, image.Tracks);

                currentSection = line[1..^1].Trim();

                // Parse section type
                if (currentSection.Equals("CloneCD", StringComparison.OrdinalIgnoreCase))
                {
                    // Header section
                }
                else if (currentSection.Equals("Disc", StringComparison.OrdinalIgnoreCase))
                {
                    // Disc info section
                }
                else if (currentSection.StartsWith("Session", StringComparison.OrdinalIgnoreCase))
                {
                    // [Session N]
                    if (TryParseIndex(currentSection, "Session", out int sessNum))
                    {
                        currentSessionIndex = sessNum;
                        currentSession = new CcdSession { SessionNumber = sessNum };
                    }
                }
                else if (currentSection.StartsWith("Entry", StringComparison.OrdinalIgnoreCase))
                {
                    // [Entry N]
                    if (TryParseIndex(currentSection, "Entry", out int entryNum))
                    {
                        currentEntryIndex = entryNum;
                        currentEntry = new CcdEntry { EntryIndex = entryNum };
                    }
                }
                else if (currentSection.StartsWith("TRACK", StringComparison.OrdinalIgnoreCase))
                {
                    // [TRACK N]
                    if (TryParseIndex(currentSection, "TRACK", out int trackNum))
                    {
                        currentTrackIndex = trackNum;
                        currentTrack = new CcdTrack { TrackNumber = trackNum };
                    }
                }

                continue;
            }

            // Key=Value pair
            var eqIndex = line.IndexOf('=');
            if (eqIndex <= 0) continue;

            var key = line[..eqIndex].Trim();
            var value = line[(eqIndex + 1)..].Trim();

            // Dispatch to appropriate handler based on current section
            if (currentSection.Equals("CloneCD", StringComparison.OrdinalIgnoreCase))
            {
                ParseCloneCdSection(key, value, image);
            }
            else if (currentSection.Equals("Disc", StringComparison.OrdinalIgnoreCase))
            {
                ParseDiscSection(key, value, image);
            }
            else if (currentSession != null)
            {
                ParseSessionSection(key, value, currentSession);
            }
            else if (currentEntry != null)
            {
                ParseEntrySection(key, value, currentEntry);
            }
            else if (currentTrack != null)
            {
                ParseTrackSection(key, value, currentTrack);
            }
        }

        // Save final pending objects
        FinalizePendingObject(ref currentEntry, image.Entries);
        FinalizePendingObject(ref currentSession, image.Sessions);
        FinalizePendingObject(ref currentTrack, image.Tracks);

        // Post-processing: if no sessions were explicitly defined, create a default
        if (image.Sessions.Count == 0 && image.SessionCount > 0)
        {
            for (int i = 1; i <= image.SessionCount; i++)
                image.Sessions.Add(new CcdSession { SessionNumber = i });
        }

        // If track count doesn't match entries, try to derive tracks from entries
        if (image.Tracks.Count == 0 && image.Entries.Count > 0)
        {
            DeriveTracksFromEntries(image);
        }
    }

    private static void FinalizePendingObject<T>(ref T? obj, List<T> list) where T : class
    {
        if (obj != null)
        {
            list.Add(obj);
            obj = null;
        }
    }

    private static void ParseCloneCdSection(string key, string value, CcdImage image)
    {
        if (key.Equals("Version", StringComparison.OrdinalIgnoreCase))
            image.Version = ParseIntValue(value);
    }

    private static void ParseDiscSection(string key, string value, CcdImage image)
    {
        switch (key.ToUpperInvariant())
        {
            case "TOCENTRIES":
                image.TocEntries = ParseIntValue(value);
                break;
            case "SESSIONS":
                image.SessionCount = ParseIntValue(value);
                break;
            case "DATATRACKSSCRAMBLED":
                image.DataTracksScrambled = ParseIntValue(value) != 0;
                break;
            case "CDTEXTLENGTH":
                image.CdTextLength = ParseIntValue(value);
                break;
            case "CATALOG":
                image.Catalog = value;
                break;
        }
    }

    private static void ParseSessionSection(string key, string value, CcdSession session)
    {
        switch (key.ToUpperInvariant())
        {
            case "PREGAPMODE":
                session.PreGapMode = ParseIntValue(value);
                break;
            case "PREGAPSUBC":
                session.PreGapSubC = ParseIntValue(value);
                break;
        }
    }

    private static void ParseEntrySection(string key, string value, CcdEntry entry)
    {
        switch (key.ToUpperInvariant())
        {
            case "SESSION":
                entry.Session = ParseIntValue(value);
                break;
            case "POINT":
                entry.Point = ParseIntValue(value);
                break;
            case "ADR":
                entry.Adr = ParseIntValue(value);
                break;
            case "CONTROL":
                entry.Control = ParseIntValue(value);
                break;
            case "TRACKNO":
                entry.TrackNo = ParseIntValue(value);
                break;
            case "AMIN":
                entry.AMin = ParseIntValue(value);
                break;
            case "ASEC":
                entry.ASec = ParseIntValue(value);
                break;
            case "AFRAME":
                entry.AFrame = ParseIntValue(value);
                break;
            case "ALBA":
                entry.Alba = ParseIntValue(value);
                break;
            case "ZERO":
                entry.Zero = ParseIntValue(value);
                break;
            case "PMIN":
                entry.PMin = ParseIntValue(value);
                break;
            case "PSEC":
                entry.PSec = ParseIntValue(value);
                break;
            case "PFRAME":
                entry.PFrame = ParseIntValue(value);
                break;
            case "PLBA":
                entry.Plba = ParseIntValue(value);
                break;
        }
    }

    private static void ParseTrackSection(string key, string value, CcdTrack track)
    {
        if (key.Equals("MODE", StringComparison.OrdinalIgnoreCase))
        {
            track.Mode = ParseIntValue(value) switch
            {
                0 => CcdTrackMode.Audio,
                1 => CcdTrackMode.Mode1,
                2 => CcdTrackMode.Mode2,
                _ => CcdTrackMode.Mode1
            };
        }
        else if (key.Equals("ISRC", StringComparison.OrdinalIgnoreCase))
        {
            track.Isrc = value;
        }
        else if (key.Equals("FLAGS", StringComparison.OrdinalIgnoreCase))
        {
            track.Flags = ParseIntValue(value);
        }
        else if (key.StartsWith("INDEX", StringComparison.OrdinalIgnoreCase))
        {
            // INDEX N=<lba>
            var indexPart = key["INDEX".Length..].Trim();
            if (int.TryParse(indexPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out int indexNum))
            {
                track.Indices[indexNum] = ParseIntValue(value);
            }
        }
    }

    /// <summary>
    /// Parses an integer value that may be decimal or hex (0x prefix).
    /// </summary>
    private static int ParseIntValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;

        value = value.Trim();

        // Handle hex values (0x prefix)
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("0X", StringComparison.OrdinalIgnoreCase))
        {
            if (int.TryParse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int hexVal))
                return hexVal;
        }

        // Handle negative values
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int intVal))
            return intVal;

        return 0;
    }

    /// <summary>
    /// Tries to extract a numeric index from a section name like "Entry 5" or "TRACK 1".
    /// </summary>
    private static bool TryParseIndex(string sectionName, string prefix, out int index)
    {
        index = 0;
        var numPart = sectionName[prefix.Length..].Trim();
        return int.TryParse(numPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out index);
    }

    /// <summary>
    /// When no [TRACK N] sections are present, derives track info from [Entry N] sections.
    /// </summary>
    private static void DeriveTracksFromEntries(CcdImage image)
    {
        foreach (var entry in image.Entries.Where(e => e.IsTrackEntry).OrderBy(e => e.Point))
        {
            var track = new CcdTrack
            {
                TrackNumber = entry.Point,
                Mode = entry.IsData ? CcdTrackMode.Mode1 : CcdTrackMode.Audio,
                Indices = { [1] = Math.Max(entry.Plba, 0) }
            };
            image.Tracks.Add(track);
        }
    }

    // -----------------------------------------------------------------------
    //  Private — CUE sheet generation
    // -----------------------------------------------------------------------

    /// <summary>Generates a CUE sheet from CCD image data.</summary>
    private static string GenerateCueSheet(CcdImage image, string binFileName)
    {
        var sb = new StringBuilder();
        sb.AppendLine("REM Generated by OpenBurningSuite from CloneCD image");
        sb.AppendLine($"FILE \"{binFileName}\" BINARY");

        foreach (var track in image.Tracks.OrderBy(t => t.TrackNumber))
        {
            var modeStr = track.Mode switch
            {
                CcdTrackMode.Audio => "AUDIO",
                CcdTrackMode.Mode1 => "MODE1/2352",
                CcdTrackMode.Mode2 => "MODE2/2352",
                _ => "MODE1/2352"
            };
            sb.AppendLine($"  TRACK {track.TrackNumber:D2} {modeStr}");

            // Add ISRC if present
            if (!string.IsNullOrEmpty(track.Isrc))
                sb.AppendLine($"    ISRC {track.Isrc}");

            // Add indices
            foreach (var idx in track.Indices.OrderBy(kv => kv.Key))
            {
                var lba = Math.Max(idx.Value, 0);
                var frames = lba % 75;
                var seconds = (lba / 75) % 60;
                var minutes = Math.Min(lba / 75 / 60, 99);
                sb.AppendLine($"    INDEX {idx.Key:D2} {minutes:D2}:{seconds:D2}:{frames:D2}");
            }
        }

        return sb.ToString();
    }

    // -----------------------------------------------------------------------
    //  Private — Helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Determines user data layout based on track mode.
    /// For Mode 2, inspects actual sector data to distinguish Form 1 from Form 2.
    /// </summary>
    private static void DetermineUserDataLayout(CcdTrackMode mode, string imgFilePath,
        int startLba, out int userDataSize, out int userDataOffset)
    {
        switch (mode)
        {
            case CcdTrackMode.Audio:
                userDataSize = RawSectorSize;
                userDataOffset = 0;
                return;
            case CcdTrackMode.Mode1:
                userDataSize = Mode1UserDataSize;
                userDataOffset = Mode1UserDataOffset;
                return;
            case CcdTrackMode.Mode2:
                // Mode 2 sectors can be Form 1 (2048 user) or Form 2 (2328 user).
                // Check the subheader to determine the form.
                // Default to Mode 2 Form 1 (2048 bytes) as it's the most common for data.
                if (DetectMode2Form(imgFilePath, startLba) == 2)
                {
                    userDataSize = Mode2Form2UserDataSize;
                    userDataOffset = Mode2Form2UserDataOffset;
                }
                else
                {
                    userDataSize = Mode2Form1UserDataSize;
                    userDataOffset = Mode2Form1UserDataOffset;
                }
                return;
            default:
                userDataSize = Mode1UserDataSize;
                userDataOffset = Mode1UserDataOffset;
                return;
        }
    }

    /// <summary>
    /// Detects Mode 2 Form by examining the subheader of a sector.
    /// Form is determined by bit 5 of the submode byte (offset 18 in the raw sector).
    /// Bit 5 set = Form 2, bit 5 clear = Form 1.
    /// </summary>
    private static int DetectMode2Form(string imgFilePath, int startLba)
    {
        try
        {
            using var stream = new FileStream(imgFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            long offset = (long)startLba * RawSectorSize;
            if (offset < 0 || offset + RawSectorSize > stream.Length) return 1;

            stream.Seek(offset, SeekOrigin.Begin);
            var sector = new byte[RawSectorSize];
            if (stream.Read(sector, 0, RawSectorSize) < RawSectorSize) return 1;

            // Submode byte is at offset 18 (12 sync + 4 header + 2 subheader = 18)
            // Bit 5 (0x20) = Form bit: 0=Form1, 1=Form2
            byte submode = sector[18];
            return (submode & 0x20) != 0 ? 2 : 1;
        }
        catch
        {
            return 1; // Default to Form 1
        }
    }

    /// <summary>Copies a file with progress reporting.</summary>
    private static void CopyFileWithProgress(string source, string dest, IProgress<int>? progress)
    {
        using var srcStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var dstStream = new FileStream(dest, FileMode.Create, FileAccess.Write);

        var buffer = new byte[BufferSize];
        long total = srcStream.Length;
        long copied = 0;
        int read;

        while ((read = srcStream.Read(buffer, 0, buffer.Length)) > 0)
        {
            dstStream.Write(buffer, 0, read);
            copied += read;
            if (total > 0)
                progress?.Report((int)(copied * 100 / total));
        }
    }
}
