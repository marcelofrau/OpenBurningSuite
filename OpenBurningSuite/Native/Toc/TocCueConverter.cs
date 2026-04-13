// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenBurningSuite.Native.Toc;

/// <summary>
/// Native TOC↔CUE sheet converter.
/// Converts cdrdao-style .toc files to standard .cue sheets and vice versa.
/// </summary>
public static class TocCueConverter
{
    // -----------------------------------------------------------------------
    // Cached regex patterns for TOC parsing (cdrdao format)
    // -----------------------------------------------------------------------
    private static readonly Regex DatafileRx = new(@"(?:DATAFILE|FILE)\s+""([^""]+)""(?:\s+(.+))?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TrackRx = new(@"TRACK\s+(\w+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PregapRx = new(@"PREGAP\s+([\d:]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TitleRx = new(@"TITLE\s+""([^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PerformerRx = new(@"PERFORMER\s+""([^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SongwriterRx = new(@"SONGWRITER\s+""([^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex IsrcRx = new(@"ISRC\s+""([^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TocCatalogRx = new(@"CATALOG\s+""([^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex StartRx = new(@"START\s+([\d:]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SilenceRx = new(@"SILENCE\s+([\d:]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ZeroRx = new(@"ZERO\s+([\d:]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TocIndexRx = new(@"INDEX\s+([\d:]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TocPostgapRx = new(@"POSTGAP\s+([\d:]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // -----------------------------------------------------------------------
    // Cached regex patterns for CUE parsing (CDRWIN format)
    // -----------------------------------------------------------------------
    private static readonly Regex CueFileRx = new(@"FILE\s+""([^""]+)""\s+(\w+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CueTrackRx = new(@"TRACK\s+(\d+)\s+(\S+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CuePregapRx = new(@"PREGAP\s+([\d:]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CuePostgapRx = new(@"POSTGAP\s+([\d:]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CueIndexRx = new(@"INDEX\s+(\d+)\s+(\d+:\d+:\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CueIsrcRx = new(@"ISRC\s+""?([A-Za-z0-9]+)""?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CueFlagsRx = new(@"FLAGS\s+(.+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CueCatalogRx = new(@"CATALOG\s+(\S+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CueSongwriterRx = new(@"SONGWRITER\s+""([^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    /// <summary>
    /// Converts a cdrdao .toc file to a standard .cue sheet.
    /// </summary>
    /// <param name="tocFilePath">Path to the input .toc file.</param>
    /// <param name="cueFilePath">Path for the output .cue file.</param>
    public static void TocToCue(string tocFilePath, string cueFilePath)
    {
        if (!File.Exists(tocFilePath))
            throw new FileNotFoundException($"TOC file not found: {tocFilePath}");

        var tocContent = File.ReadAllText(tocFilePath);
        var cueContent = ConvertTocToCue(tocContent, tocFilePath);
        File.WriteAllText(cueFilePath, cueContent);
    }

    /// <summary>
    /// Generates a basic CUE sheet from a BIN file (when no TOC is available).
    /// </summary>
    /// <param name="binFilePath">Path to the BIN file.</param>
    /// <param name="cueFilePath">Path for the output CUE file.</param>
    /// <param name="sectorMode">Sector mode (e.g., "MODE1/2352", "AUDIO").</param>
    public static void GenerateBasicCue(string binFilePath, string cueFilePath,
        string sectorMode = "MODE1/2352")
    {
        var binFileName = Path.GetFileName(binFilePath);
        var sb = new StringBuilder();
        sb.AppendLine($"REM Generated by OpenBurningSuite");
        sb.AppendLine($"FILE \"{binFileName}\" BINARY");
        sb.AppendLine($"  TRACK 01 {sectorMode}");
        sb.AppendLine($"    INDEX 01 00:00:00");
        File.WriteAllText(cueFilePath, sb.ToString());
    }

    /// <summary>
    /// Converts a .cue sheet to a cdrdao .toc file.
    /// </summary>
    public static void CueToToc(string cueFilePath, string tocFilePath)
    {
        if (!File.Exists(cueFilePath))
            throw new FileNotFoundException($"CUE file not found: {cueFilePath}");

        var cueContent = File.ReadAllText(cueFilePath);
        var tocContent = ConvertCueToToc(cueContent);
        File.WriteAllText(tocFilePath, tocContent);
    }

    // -----------------------------------------------------------------------
    // TOC → CUE conversion
    // -----------------------------------------------------------------------

    private static string ConvertTocToCue(string tocContent, string tocFilePath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("REM Generated by OpenBurningSuite (from TOC)");

        var lines = tocContent.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        string? currentDataFile = null;
        int trackNumber = 0;
        string currentMode = "MODE1/2352";

        // Buffers for track-level metadata.
        // In cdrdao TOC format, metadata (TITLE, PERFORMER, ISRC, PREGAP, SONGWRITER)
        // appears between the TRACK directive and the DATAFILE directive.
        // In CUE format, these must appear AFTER the TRACK line (inside the track block).
        // We buffer them until the DATAFILE is encountered, then flush in correct CUE order.
        string? pendingTitle = null;
        string? pendingPerformer = null;
        string? pendingSongwriter = null;
        string? pendingIsrc = null;
        string? pendingPregap = null;
        bool pendingFlagsCopy = false;
        bool pendingFlagsPre = false;
        bool pendingFlags4ch = false;
        string? pendingStart = null;
        string? pendingSilence = null;
        string? pendingZero = null;
        string? pendingPostgap = null;
        var pendingIndexes = new List<string>();

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            // Skip disc type headers and comment lines
            if (line.StartsWith("CD_DA", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("CD_ROM_XA", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("CD_ROM", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("//", StringComparison.Ordinal))
                continue;

            // Skip CD_TEXT block lines (brace-delimited blocks and LANGUAGE directives).
            // Inline TITLE/PERFORMER within CD_TEXT blocks are still captured by the
            // regex matchers below, which is the desired behavior.
            if (line.Equals("{", StringComparison.Ordinal) ||
                line.Equals("}", StringComparison.Ordinal) ||
                line.StartsWith("CD_TEXT", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("LANGUAGE_MAP", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("LANGUAGE ", StringComparison.OrdinalIgnoreCase))
                continue;

            // Parse CATALOG (disc-level, before any track)
            var catalogMatch = TocCatalogRx.Match(line);
            if (catalogMatch.Success)
            {
                // CUE CATALOG uses unquoted 13-digit MCN
                sb.AppendLine($"CATALOG {catalogMatch.Groups[1].Value}");
                continue;
            }

            // Parse TRACK
            var trackMatch = TrackRx.Match(line);
            if (trackMatch.Success)
            {
                trackNumber++;
                var tocMode = trackMatch.Groups[1].Value.ToUpperInvariant();
                currentMode = TocModeToCueMode(tocMode);

                // Clear metadata buffers for new track
                pendingTitle = null;
                pendingPerformer = null;
                pendingSongwriter = null;
                pendingIsrc = null;
                pendingPregap = null;
                pendingFlagsCopy = false;
                pendingFlagsPre = false;
                pendingFlags4ch = false;
                pendingStart = null;
                pendingSilence = null;
                pendingZero = null;
                pendingPostgap = null;
                pendingIndexes.Clear();
                continue;
            }

            // Parse START directive — marks the playback start within a track
            // In cdrdao, START specifies the pre-gap portion of recorded data.
            // Converted to INDEX 00 (pre-gap start) in CUE format.
            var startMatch = StartRx.Match(line);
            if (startMatch.Success)
            {
                pendingStart = NormalizeMsf(startMatch.Groups[1].Value);
                continue;
            }

            // Parse SILENCE directive — silent pre-gap data (digital silence)
            // In cdrdao, SILENCE inserts zero audio data for the specified duration.
            // Converted to PREGAP in CUE (since CUE PREGAP represents unrecorded silence).
            var silenceMatch = SilenceRx.Match(line);
            if (silenceMatch.Success)
            {
                pendingSilence = NormalizeMsf(silenceMatch.Groups[1].Value);
                continue;
            }

            // Parse ZERO directive — zero data fill for data tracks
            // In cdrdao, ZERO fills the specified duration with zero bytes.
            // Similar to SILENCE but for data tracks. Mapped to PREGAP in CUE.
            var zeroMatch = ZeroRx.Match(line);
            if (zeroMatch.Success)
            {
                pendingZero = NormalizeMsf(zeroMatch.Groups[1].Value);
                continue;
            }

            // Parse INDEX directive (within a track, after data source directives)
            // cdrdao uses: INDEX <MSF> to set additional index points within a track.
            var tocIndexMatch = TocIndexRx.Match(line);
            if (tocIndexMatch.Success && !line.StartsWith("DATAFILE", StringComparison.OrdinalIgnoreCase) &&
                !line.StartsWith("FILE", StringComparison.OrdinalIgnoreCase))
            {
                pendingIndexes.Add(NormalizeMsf(tocIndexMatch.Groups[1].Value));
                continue;
            }

            // Parse POSTGAP directive
            var tocPostgapMatch = TocPostgapRx.Match(line);
            if (tocPostgapMatch.Success)
            {
                pendingPostgap = NormalizeMsf(tocPostgapMatch.Groups[1].Value);
                continue;
            }

            // Parse DATAFILE/FILE reference — this triggers CUE output for the current track
            var dataMatch = DatafileRx.Match(line);
            if (dataMatch.Success)
            {
                var fileName = dataMatch.Groups[1].Value;
                var offsetInfo = dataMatch.Groups[2].Value.Trim();

                // Resolve relative paths relative to TOC file directory
                if (!Path.IsPathRooted(fileName))
                {
                    var tocDir = Path.GetDirectoryName(tocFilePath) ?? ".";
                    var fullTocDir = Path.GetFullPath(tocDir);
                    var resolved = Path.GetFullPath(Path.Combine(tocDir, fileName));
                    // Validate: resolved path must stay within the TOC file's directory
                    // to prevent path traversal attacks via crafted TOC files.
                    var comparisonType = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal;
                    if (!resolved.StartsWith(fullTocDir + Path.DirectorySeparatorChar, comparisonType) &&
                        !resolved.Equals(fullTocDir, comparisonType))
                    {
                        fileName = Path.GetFileName(fileName);
                    }
                    else
                    {
                        fileName = Path.GetFileName(resolved);
                    }
                }

                // Emit FILE directive (only when the file name changes)
                if (currentDataFile != fileName)
                {
                    currentDataFile = fileName;
                    sb.AppendLine($"FILE \"{currentDataFile}\" BINARY");
                }

                // Emit TRACK directive
                sb.AppendLine($"  TRACK {trackNumber:D2} {currentMode}");

                // Flush buffered track metadata in standard CUE order:
                // TITLE, PERFORMER, SONGWRITER, ISRC come after TRACK but before PREGAP/INDEX
                if (pendingTitle != null)
                    sb.AppendLine($"    TITLE \"{pendingTitle}\"");
                if (pendingPerformer != null)
                    sb.AppendLine($"    PERFORMER \"{pendingPerformer}\"");
                if (pendingSongwriter != null)
                    sb.AppendLine($"    SONGWRITER \"{pendingSongwriter}\"");
                if (pendingIsrc != null)
                    sb.AppendLine($"    ISRC {pendingIsrc}");

                // Emit FLAGS if any were buffered
                var flagParts = new List<string>();
                if (pendingFlagsCopy) flagParts.Add("DCP");
                if (pendingFlags4ch) flagParts.Add("4CH");
                if (pendingFlagsPre) flagParts.Add("PRE");
                if (flagParts.Count > 0)
                    sb.AppendLine($"    FLAGS {string.Join(" ", flagParts)}");

                // Handle SILENCE/ZERO → PREGAP (unrecorded gap in CUE format)
                if (pendingSilence != null)
                    sb.AppendLine($"    PREGAP {pendingSilence}");
                else if (pendingZero != null)
                    sb.AppendLine($"    PREGAP {pendingZero}");
                else if (pendingPregap != null)
                    sb.AppendLine($"    PREGAP {pendingPregap}");

                // Clear buffers
                pendingTitle = null;
                pendingPerformer = null;
                pendingSongwriter = null;
                pendingIsrc = null;
                pendingPregap = null;
                pendingSilence = null;
                pendingZero = null;

                // Parse offset from TOC DATAFILE directive (e.g., "DATAFILE "file.bin" #offset")
                // or MSF offset (e.g., "DATAFILE "file.bin" 00:02:00")
                string indexMsf = "00:00:00";
                if (!string.IsNullOrEmpty(offsetInfo))
                {
                    if (offsetInfo.StartsWith('#'))
                    {
                        // Byte offset — convert to MSF
                        if (long.TryParse(offsetInfo[1..].Trim(), out var byteOffset))
                        {
                            int sectorBytes = GetSectorSizeForCueMode(currentMode);
                            var totalFrames = byteOffset / sectorBytes;
                            var minutes = totalFrames / 75 / 60;
                            var seconds = (totalFrames / 75) % 60;
                            var frames = totalFrames % 75;
                            indexMsf = $"{minutes:D2}:{seconds:D2}:{frames:D2}";
                        }
                    }
                    else if (offsetInfo.Contains(':'))
                    {
                        indexMsf = NormalizeMsf(offsetInfo.Split(' ')[0]);
                    }
                }

                // If START was specified, emit INDEX 00 for the pre-gap portion
                // and INDEX 01 at the start + offset position
                if (pendingStart != null)
                {
                    sb.AppendLine($"    INDEX 00 {indexMsf}");
                    // Calculate INDEX 01 = INDEX 00 + START offset
                    var idx01Msf = AddMsf(indexMsf, pendingStart);
                    sb.AppendLine($"    INDEX 01 {idx01Msf}");
                    pendingStart = null;
                }
                else
                {
                    sb.AppendLine($"    INDEX 01 {indexMsf}");
                }

                // Emit any additional INDEX points (INDEX 02, 03, etc.)
                int additionalIndex = 2;
                foreach (var idxMsf in pendingIndexes)
                {
                    sb.AppendLine($"    INDEX {additionalIndex:D2} {idxMsf}");
                    additionalIndex++;
                }
                pendingIndexes.Clear();

                // Emit POSTGAP if present
                if (pendingPostgap != null)
                {
                    sb.AppendLine($"    POSTGAP {pendingPostgap}");
                    pendingPostgap = null;
                }

                continue;
            }

            // Buffer PREGAP — must appear after TRACK line in CUE output
            var pregapMatch = PregapRx.Match(line);
            if (pregapMatch.Success)
            {
                pendingPregap = NormalizeMsf(pregapMatch.Groups[1].Value);
                continue;
            }

            // Buffer or emit TITLE
            var titleMatch = TitleRx.Match(line);
            if (titleMatch.Success)
            {
                if (trackNumber == 0)
                {
                    // Disc-level TITLE (before any TRACK) — emit at disc level (no indent)
                    sb.AppendLine($"TITLE \"{titleMatch.Groups[1].Value}\"");
                }
                else
                {
                    pendingTitle = titleMatch.Groups[1].Value;
                }
                continue;
            }

            // Buffer or emit PERFORMER
            var perfMatch = PerformerRx.Match(line);
            if (perfMatch.Success)
            {
                if (trackNumber == 0)
                {
                    // Disc-level PERFORMER — emit at disc level (no indent)
                    sb.AppendLine($"PERFORMER \"{perfMatch.Groups[1].Value}\"");
                }
                else
                {
                    pendingPerformer = perfMatch.Groups[1].Value;
                }
                continue;
            }

            // Buffer or emit SONGWRITER
            var swMatch = SongwriterRx.Match(line);
            if (swMatch.Success)
            {
                if (trackNumber == 0)
                    sb.AppendLine($"SONGWRITER \"{swMatch.Groups[1].Value}\"");
                else
                    pendingSongwriter = swMatch.Groups[1].Value;
                continue;
            }

            // Buffer ISRC (always track-level)
            var isrcMatch = IsrcRx.Match(line);
            if (isrcMatch.Success)
            {
                pendingIsrc = isrcMatch.Groups[1].Value;
                continue;
            }

            // Handle TOC track flags → CUE FLAGS directive
            // cdrdao uses: COPY, NO COPY, PRE_EMPHASIS, NO PRE_EMPHASIS,
            // FOUR_CHANNEL_AUDIO, TWO_CHANNEL_AUDIO
            if (line.Equals("COPY", StringComparison.OrdinalIgnoreCase))
            {
                pendingFlagsCopy = true;
                continue;
            }
            if (line.Equals("PRE_EMPHASIS", StringComparison.OrdinalIgnoreCase))
            {
                pendingFlagsPre = true;
                continue;
            }
            if (line.Equals("FOUR_CHANNEL_AUDIO", StringComparison.OrdinalIgnoreCase))
            {
                pendingFlags4ch = true;
                continue;
            }
            if (line.Equals("TWO_CHANNEL_AUDIO", StringComparison.OrdinalIgnoreCase) ||
                line.Equals("NO COPY", StringComparison.OrdinalIgnoreCase) ||
                line.Equals("NO PRE_EMPHASIS", StringComparison.OrdinalIgnoreCase))
            {
                // TWO_CHANNEL_AUDIO is the default (no flag needed in CUE).
                // NO COPY and NO PRE_EMPHASIS are also defaults.
                continue;
            }
        }

        // If no tracks were found, generate a minimal single-track CUE
        if (trackNumber == 0)
        {
            var defaultBin = Path.GetFileNameWithoutExtension(tocFilePath) + ".bin";
            sb.Clear();
            sb.AppendLine($"REM Generated by OpenBurningSuite (basic fallback)");
            sb.AppendLine($"FILE \"{defaultBin}\" BINARY");
            sb.AppendLine($"  TRACK 01 MODE1/2352");
            sb.AppendLine($"    INDEX 01 00:00:00");
        }

        return sb.ToString();
    }

    // -----------------------------------------------------------------------
    // CUE → TOC conversion
    // -----------------------------------------------------------------------

    private static string ConvertCueToToc(string cueContent)
    {
        var sb = new StringBuilder();
        var lines = cueContent.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        string? currentFile = null;
        string currentMode = "MODE1_RAW";
        bool firstTrack = true;

        // Track-level metadata buffers
        string? pendingTitle = null;
        string? pendingPerformer = null;
        string? pendingSongwriter = null;
        string? pendingIsrc = null;
        bool pendingFlagsCopy = false;
        bool pendingFlagsPre = false;
        bool pendingFlags4ch = false;

        // Disc-level metadata collected during first pass
        string? discTitle = null;
        string? discPerformer = null;
        string? discSongwriter = null;
        string? discCatalog = null;

        // First pass: determine disc type and collect disc-level metadata
        bool hasAudio = false;
        bool hasData = false;
        bool hasMode2 = false;
        bool beforeFirstTrack = true;
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            var trackMatch = CueTrackRx.Match(line);
            if (trackMatch.Success)
            {
                beforeFirstTrack = false;
                var mode = trackMatch.Groups[2].Value.ToUpperInvariant();
                if (mode == "AUDIO")
                    hasAudio = true;
                else
                {
                    hasData = true;
                    if (mode.StartsWith("MODE2", StringComparison.Ordinal) ||
                        mode.StartsWith("CDI", StringComparison.Ordinal))
                        hasMode2 = true;
                }
                continue;
            }

            if (beforeFirstTrack)
            {
                var titleMatch = TitleRx.Match(line);
                if (titleMatch.Success) { discTitle = titleMatch.Groups[1].Value; continue; }
                var perfMatch = PerformerRx.Match(line);
                if (perfMatch.Success) { discPerformer = perfMatch.Groups[1].Value; continue; }
                var swMatch = CueSongwriterRx.Match(line);
                if (swMatch.Success) { discSongwriter = swMatch.Groups[1].Value; continue; }
                var catMatch = CueCatalogRx.Match(line);
                if (catMatch.Success) { discCatalog = catMatch.Groups[1].Value; continue; }
            }
        }

        // Determine disc type per cdrdao conventions:
        // - Pure audio (all tracks AUDIO) → CD_DA
        // - Any Mode 2 / CDI data → CD_ROM_XA
        // - Data only or mixed data+audio with Mode 1 → CD_ROM
        if (hasAudio && !hasData)
            sb.AppendLine("CD_DA");
        else if (hasMode2)
            sb.AppendLine("CD_ROM_XA");
        else
            sb.AppendLine("CD_ROM");

        sb.AppendLine();
        sb.AppendLine("// Generated by OpenBurningSuite");

        // Emit disc-level metadata
        if (discCatalog != null)
            sb.AppendLine($"CATALOG \"{discCatalog}\"");
        if (discTitle != null || discPerformer != null || discSongwriter != null)
        {
            sb.AppendLine("CD_TEXT {");
            sb.AppendLine("  LANGUAGE 0 {");
            if (discTitle != null) sb.AppendLine($"    TITLE \"{discTitle}\"");
            if (discPerformer != null) sb.AppendLine($"    PERFORMER \"{discPerformer}\"");
            if (discSongwriter != null) sb.AppendLine($"    SONGWRITER \"{discSongwriter}\"");
            sb.AppendLine("  }");
            sb.AppendLine("}");
        }
        sb.AppendLine();

        // Helper to flush pending DATAFILE (deferred until after PREGAP is emitted)
        string? pendingDataFile = null;
        long pendingDataOffset = 0;
        bool dataFileEmitted = false;
        string? pendingIndex00Msf = null;

        void FlushTrackMetadataAndDataFile()
        {
            // Emit track flags (must come before CD_TEXT/ISRC in cdrdao format)
            if (pendingFlagsCopy) sb.AppendLine("COPY");
            if (pendingFlags4ch) sb.AppendLine("FOUR_CHANNEL_AUDIO");
            if (pendingFlagsPre) sb.AppendLine("PRE_EMPHASIS");

            // Emit ISRC (before CD_TEXT)
            if (pendingIsrc != null)
                sb.AppendLine($"ISRC \"{pendingIsrc}\"");

            // Emit per-track CD-TEXT
            if (pendingTitle != null || pendingPerformer != null || pendingSongwriter != null)
            {
                sb.AppendLine("CD_TEXT {");
                sb.AppendLine("  LANGUAGE 0 {");
                if (pendingTitle != null) sb.AppendLine($"    TITLE \"{pendingTitle}\"");
                if (pendingPerformer != null) sb.AppendLine($"    PERFORMER \"{pendingPerformer}\"");
                if (pendingSongwriter != null) sb.AppendLine($"    SONGWRITER \"{pendingSongwriter}\"");
                sb.AppendLine("  }");
                sb.AppendLine("}");
            }

            // Clear metadata buffers
            pendingTitle = null;
            pendingPerformer = null;
            pendingSongwriter = null;
            pendingIsrc = null;
            pendingFlagsCopy = false;
            pendingFlagsPre = false;
            pendingFlags4ch = false;
        }

        void EmitDataFile()
        {
            if (pendingDataFile != null && !dataFileEmitted)
            {
                if (pendingDataOffset > 0)
                    sb.AppendLine($"DATAFILE \"{pendingDataFile}\" #{pendingDataOffset}");
                else
                    sb.AppendLine($"DATAFILE \"{pendingDataFile}\"");
                dataFileEmitted = true;
            }
        }

        // Second pass: convert tracks
        int currentSectorSize = 2352;
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            // Skip REM comments and disc-level directives already handled
            if (line.StartsWith("REM ", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("CATALOG ", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("CDTEXTFILE ", StringComparison.OrdinalIgnoreCase))
                continue;

            // Parse FILE
            var fileMatch = CueFileRx.Match(line);
            if (fileMatch.Success)
            {
                currentFile = fileMatch.Groups[1].Value;
                continue;
            }

            // Parse TRACK — start a new track block
            var trackMatch = CueTrackRx.Match(line);
            if (trackMatch.Success)
            {
                // Flush any pending data from previous track
                if (!firstTrack && !dataFileEmitted)
                    EmitDataFile();

                if (!firstTrack) sb.AppendLine();
                firstTrack = false;

                var cueMode = trackMatch.Groups[2].Value.ToUpperInvariant();
                currentMode = CueModeToTocMode(cueMode);
                currentSectorSize = GetSectorSizeForCueMode(cueMode);
                sb.AppendLine($"TRACK {currentMode}");

                // Reset per-track state
                pendingDataFile = currentFile;
                pendingDataOffset = 0;
                dataFileEmitted = false;
                pendingTitle = null;
                pendingPerformer = null;
                pendingSongwriter = null;
                pendingIsrc = null;
                pendingFlagsCopy = false;
                pendingFlagsPre = false;
                pendingFlags4ch = false;
                pendingIndex00Msf = null;
                continue;
            }

            // Parse FLAGS
            var flagsMatch = CueFlagsRx.Match(line);
            if (flagsMatch.Success)
            {
                var flags = flagsMatch.Groups[1].Value;
                if (flags.Contains("DCP", StringComparison.OrdinalIgnoreCase))
                    pendingFlagsCopy = true;
                if (flags.Contains("4CH", StringComparison.OrdinalIgnoreCase))
                    pendingFlags4ch = true;
                if (flags.Contains("PRE", StringComparison.OrdinalIgnoreCase))
                    pendingFlagsPre = true;
                continue;
            }

            // Parse track-level TITLE (not disc-level, already collected)
            var titleMatch = TitleRx.Match(line);
            if (titleMatch.Success)
            {
                pendingTitle = titleMatch.Groups[1].Value;
                continue;
            }

            // Parse track-level PERFORMER
            var perfMatch = PerformerRx.Match(line);
            if (perfMatch.Success)
            {
                pendingPerformer = perfMatch.Groups[1].Value;
                continue;
            }

            // Parse track-level SONGWRITER
            var swMatch = CueSongwriterRx.Match(line);
            if (swMatch.Success)
            {
                pendingSongwriter = swMatch.Groups[1].Value;
                continue;
            }

            // Parse ISRC (CUE format: unquoted or optionally quoted)
            var isrcMatch = CueIsrcRx.Match(line);
            if (isrcMatch.Success)
            {
                pendingIsrc = isrcMatch.Groups[1].Value;
                continue;
            }

            // Parse PREGAP — emit as SILENCE for audio or ZERO for data in cdrdao format
            var pregapMatch = CuePregapRx.Match(line);
            if (pregapMatch.Success)
            {
                // Flush metadata first, then gap, then DATAFILE
                FlushTrackMetadataAndDataFile();
                // cdrdao uses SILENCE for audio pre-gaps and ZERO for data pre-gaps.
                // Check the current track mode to decide.
                if (currentMode == "AUDIO")
                    sb.AppendLine($"SILENCE {NormalizeMsf(pregapMatch.Groups[1].Value)}");
                else
                    sb.AppendLine($"ZERO {NormalizeMsf(pregapMatch.Groups[1].Value)}");
                continue;
            }

            // Parse POSTGAP — emit as SILENCE/ZERO after DATAFILE in cdrdao format
            var postgapMatch = CuePostgapRx.Match(line);
            if (postgapMatch.Success)
            {
                // In cdrdao, there is no direct POSTGAP keyword.
                // The equivalent is to append a SILENCE (audio) or ZERO (data) block
                // after the DATAFILE entry.
                EmitDataFile();
                if (currentMode == "AUDIO")
                    sb.AppendLine($"SILENCE {NormalizeMsf(postgapMatch.Groups[1].Value)}");
                else
                    sb.AppendLine($"ZERO {NormalizeMsf(postgapMatch.Groups[1].Value)}");
                continue;
            }

            // Parse INDEX — compute byte offsets for DATAFILE
            var indexMatch = CueIndexRx.Match(line);
            if (indexMatch.Success)
            {
                var indexNum = int.Parse(indexMatch.Groups[1].Value);
                var msf = NormalizeMsf(indexMatch.Groups[2].Value);

                if (indexNum == 0)
                {
                    // INDEX 00: pregap start (data in file).
                    // In cdrdao, DATAFILE starts at INDEX 00, and START specifies
                    // the offset from INDEX 00 to INDEX 01 (the playable portion).
                    var msfParts = msf.Split(':');
                    if (msfParts.Length == 3 &&
                        int.TryParse(msfParts[0], out int min) &&
                        int.TryParse(msfParts[1], out int sec) &&
                        int.TryParse(msfParts[2], out int frm))
                    {
                        long frames = (long)min * 60 * 75 + sec * 75 + frm;
                        pendingDataOffset = frames * currentSectorSize;
                    }
                    // Store INDEX 00 MSF to compute START when INDEX 01 arrives
                    pendingIndex00Msf = msf;
                }
                else if (indexNum == 1)
                {
                    // INDEX 01: actual track data start
                    if (pendingIndex00Msf != null)
                    {
                        // We have INDEX 00 + INDEX 01 → emit DATAFILE at INDEX 00 offset
                        // and START = INDEX 01 - INDEX 00
                        FlushTrackMetadataAndDataFile();
                        EmitDataFile();
                        var startMsf = SubtractMsf(msf, pendingIndex00Msf);
                        if (startMsf != "00:00:00")
                            sb.AppendLine($"START {startMsf}");
                        pendingIndex00Msf = null;
                    }
                    else
                    {
                        // No INDEX 00 → use INDEX 01 directly for DATAFILE offset
                        var msfParts = msf.Split(':');
                        if (msfParts.Length == 3 &&
                            int.TryParse(msfParts[0], out int min) &&
                            int.TryParse(msfParts[1], out int sec) &&
                            int.TryParse(msfParts[2], out int frm))
                        {
                            long frames = (long)min * 60 * 75 + sec * 75 + frm;
                            pendingDataOffset = frames * currentSectorSize;
                        }

                        FlushTrackMetadataAndDataFile();
                        EmitDataFile();
                    }
                }
                else if (indexNum >= 2)
                {
                    // INDEX 02+ → emit as INDEX directive in cdrdao format
                    // cdrdao INDEX is relative to the track start, not absolute.
                    EmitDataFile(); // ensure DATAFILE is written first
                    sb.AppendLine($"INDEX {msf}");
                }
                continue;
            }
        }

        // Flush any remaining data for the last track
        if (!dataFileEmitted)
        {
            FlushTrackMetadataAndDataFile();
            EmitDataFile();
        }

        return sb.ToString();
    }

    // -----------------------------------------------------------------------
    // Mode conversion helpers
    // -----------------------------------------------------------------------

    /// <summary>Converts a cdrdao TOC track mode to CUE sheet mode.</summary>
    private static string TocModeToCueMode(string tocMode) => tocMode switch
    {
        "AUDIO" => "AUDIO",
        "MODE1" => "MODE1/2048",
        "MODE1_RAW" => "MODE1/2352",
        "MODE2" => "MODE2/2336",
        "MODE2_RAW" => "MODE2/2352",
        "MODE2_FORM1" => "MODE2/2048",
        "MODE2_FORM2" => "MODE2/2328",
        "MODE2_FORM_MIX" => "MODE2/2336",
        _ => "MODE1/2352"
    };

    /// <summary>Converts a CUE sheet mode to cdrdao TOC track mode.</summary>
    private static string CueModeToTocMode(string cueMode) => cueMode switch
    {
        "AUDIO" => "AUDIO",
        "MODE1/2048" => "MODE1",
        "MODE1/2352" => "MODE1_RAW",
        "MODE2/2048" => "MODE2_FORM1",
        "MODE2/2328" => "MODE2_FORM2",
        "MODE2/2336" => "MODE2_FORM_MIX",
        "MODE2/2352" => "MODE2_RAW",
        "CDI/2336" => "MODE2_FORM_MIX",
        "CDI/2352" => "MODE2_RAW",
        _ => "MODE1_RAW"
    };

    /// <summary>
    /// Gets the sector size in bytes for a CUE track mode string.
    /// </summary>
    internal static int GetSectorSizeForCueMode(string cueMode)
    {
        if (string.Equals(cueMode, "AUDIO", StringComparison.OrdinalIgnoreCase))
            return 2352;
        var slashIdx = cueMode.IndexOf('/');
        if (slashIdx >= 0 && int.TryParse(cueMode.AsSpan(slashIdx + 1), out var size))
            return size;
        return 2352; // default for raw/unknown modes
    }

    /// <summary>
    /// Normalizes an MSF (Minutes:Seconds:Frames) time string to MM:SS:FF format.
    /// Returns "00:00:00" for malformed input to prevent invalid output.
    /// </summary>
    private static string NormalizeMsf(string msf)
    {
        if (string.IsNullOrWhiteSpace(msf))
            return "00:00:00";

        var parts = msf.Split(':');
        if (parts.Length == 3 &&
            int.TryParse(parts[0], out var m) &&
            int.TryParse(parts[1], out var s) &&
            int.TryParse(parts[2], out var f))
        {
            // Clamp values to valid MSF ranges
            m = Math.Max(0, Math.Min(m, 99));
            s = Math.Max(0, Math.Min(s, 59));
            f = Math.Max(0, Math.Min(f, 74));
            return $"{m:D2}:{s:D2}:{f:D2}";
        }
        return "00:00:00"; // Return safe default instead of potentially malformed input
    }

    /// <summary>
    /// Adds two MSF values together, carrying frames → seconds → minutes.
    /// Used to compute INDEX 01 from INDEX 00 + START offset.
    /// </summary>
    private static string AddMsf(string msf1, string msf2)
    {
        var p1 = msf1.Split(':');
        var p2 = msf2.Split(':');
        if (p1.Length != 3 || p2.Length != 3) return msf1;
        if (!int.TryParse(p1[0], out int m1) || !int.TryParse(p1[1], out int s1) || !int.TryParse(p1[2], out int f1))
            return msf1;
        if (!int.TryParse(p2[0], out int m2) || !int.TryParse(p2[1], out int s2) || !int.TryParse(p2[2], out int f2))
            return msf1;

        int totalFrames = (m1 * 60 * 75 + s1 * 75 + f1) + (m2 * 60 * 75 + s2 * 75 + f2);
        int rm = totalFrames / (60 * 75);
        int rs = (totalFrames / 75) % 60;
        int rf = totalFrames % 75;
        rm = Math.Min(rm, 99);
        return $"{rm:D2}:{rs:D2}:{rf:D2}";
    }

    /// <summary>
    /// Subtracts msf2 from msf1 (msf1 - msf2). Returns "00:00:00" if result would be negative.
    /// </summary>
    private static string SubtractMsf(string msf1, string msf2)
    {
        var p1 = msf1.Split(':');
        var p2 = msf2.Split(':');
        if (p1.Length != 3 || p2.Length != 3) return "00:00:00";
        if (!int.TryParse(p1[0], out int m1) || !int.TryParse(p1[1], out int s1) || !int.TryParse(p1[2], out int f1))
            return "00:00:00";
        if (!int.TryParse(p2[0], out int m2) || !int.TryParse(p2[1], out int s2) || !int.TryParse(p2[2], out int f2))
            return "00:00:00";

        int totalFrames = (m1 * 60 * 75 + s1 * 75 + f1) - (m2 * 60 * 75 + s2 * 75 + f2);
        if (totalFrames < 0) return "00:00:00";
        int rm = totalFrames / (60 * 75);
        int rs = (totalFrames / 75) % 60;
        int rf = totalFrames % 75;
        rm = Math.Min(rm, 99);
        return $"{rm:D2}:{rs:D2}:{rf:D2}";
    }
}
