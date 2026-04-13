// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

// ---------------------------------------------------------------------------
// PlaylistParser.cs — Unified playlist import / export for Audio CD workflows
// Supports: M3U, M3U8, PLS, WPL (Windows Playlist) & ASX
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace OpenBurningSuite.Native.Audio;

/// <summary>
/// Cross-platform playlist reader and writer.
/// <para>
/// Handles five widely-used playlist formats with full round-trip fidelity
/// for track paths, titles and durations.  All methods are static and
/// thread-safe.
/// </para>
/// </summary>
public static class PlaylistParser
{
    // -----------------------------------------------------------------------
    //  File-extension helpers
    // -----------------------------------------------------------------------

    /// <summary>All file extensions this parser can handle.</summary>
    public static readonly string[] SupportedExtensions =
        { ".m3u", ".m3u8", ".pls", ".wpl", ".asx" };

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="extension"/> is a
    /// playlist format this class can parse (case-insensitive, with or
    /// without leading dot).
    /// </summary>
    public static bool IsPlaylistFile(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return false;

        if (!extension.StartsWith('.'))
            extension = "." + extension;

        return SupportedExtensions.Any(
            e => e.Equals(extension, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Infers the <see cref="PlaylistFormat"/> from a file extension.
    /// Returns <see langword="null"/> when the extension is not recognised.
    /// </summary>
    public static PlaylistFormat? FormatFromExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return null;

        if (!extension.StartsWith('.'))
            extension = "." + extension;

        return extension.ToLowerInvariant() switch
        {
            ".m3u"  => PlaylistFormat.M3U,
            ".m3u8" => PlaylistFormat.M3U8,
            ".pls"  => PlaylistFormat.PLS,
            ".wpl"  => PlaylistFormat.WPL,
            ".asx"  => PlaylistFormat.ASX,
            _       => null
        };
    }

    /// <summary>Returns the canonical file extension (with dot) for a format.</summary>
    public static string ExtensionForFormat(PlaylistFormat format) => format switch
    {
        PlaylistFormat.M3U  => ".m3u",
        PlaylistFormat.M3U8 => ".m3u8",
        PlaylistFormat.PLS  => ".pls",
        PlaylistFormat.WPL  => ".wpl",
        PlaylistFormat.ASX  => ".asx",
        _                   => ".m3u"
    };

    // -----------------------------------------------------------------------
    //  Unified load / save (auto-detect from extension)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Reads a playlist file and returns the entries it contains.
    /// The format is auto-detected from the file extension.
    /// Relative paths inside the playlist are resolved against the directory
    /// that contains the playlist file.
    /// </summary>
    /// <exception cref="ArgumentException">Unrecognised extension.</exception>
    /// <exception cref="IOException">File could not be read.</exception>
    public static List<PlaylistEntry> Load(string playlistPath)
    {
        var ext = System.IO.Path.GetExtension(playlistPath);
        var fmt = FormatFromExtension(ext)
                  ?? throw new ArgumentException(
                      $"Unsupported playlist extension '{ext}'.", nameof(playlistPath));

        var baseDir = System.IO.Path.GetDirectoryName(
            System.IO.Path.GetFullPath(playlistPath)) ?? string.Empty;

        return fmt switch
        {
            PlaylistFormat.M3U  => ReadM3U(playlistPath, Encoding.Latin1, baseDir),
            PlaylistFormat.M3U8 => ReadM3U(playlistPath, Encoding.UTF8, baseDir),
            PlaylistFormat.PLS  => ReadPLS(playlistPath, baseDir),
            PlaylistFormat.WPL  => ReadWPL(playlistPath, baseDir),
            PlaylistFormat.ASX  => ReadASX(playlistPath, baseDir),
            _                   => new List<PlaylistEntry>()
        };
    }

    /// <summary>
    /// Writes the given entries to a playlist file.  The format is determined
    /// by the file extension.  Paths are stored relative to the playlist
    /// location when possible.
    /// </summary>
    /// <param name="playlistPath">Destination file path.</param>
    /// <param name="entries">Entries to write.</param>
    /// <param name="playlistTitle">
    /// Optional playlist title (used by WPL and ASX).
    /// </param>
    /// <exception cref="ArgumentException">Unrecognised extension.</exception>
    public static void Save(string playlistPath, IReadOnlyList<PlaylistEntry> entries,
                            string? playlistTitle = null)
    {
        var ext = System.IO.Path.GetExtension(playlistPath);
        var fmt = FormatFromExtension(ext)
                  ?? throw new ArgumentException(
                      $"Unsupported playlist extension '{ext}'.", nameof(playlistPath));

        var baseDir = System.IO.Path.GetDirectoryName(
            System.IO.Path.GetFullPath(playlistPath)) ?? string.Empty;

        // Ensure the output directory exists
        if (!string.IsNullOrEmpty(baseDir))
            Directory.CreateDirectory(baseDir);

        switch (fmt)
        {
            case PlaylistFormat.M3U:
                WriteM3U(playlistPath, entries, Encoding.Latin1, baseDir);
                break;
            case PlaylistFormat.M3U8:
                WriteM3U(playlistPath, entries, Encoding.UTF8, baseDir);
                break;
            case PlaylistFormat.PLS:
                WritePLS(playlistPath, entries, baseDir);
                break;
            case PlaylistFormat.WPL:
                WriteWPL(playlistPath, entries, playlistTitle, baseDir);
                break;
            case PlaylistFormat.ASX:
                WriteASX(playlistPath, entries, playlistTitle, baseDir);
                break;
        }
    }

    // ===================================================================
    //  M3U / M3U8 — Extended M3U
    // ===================================================================
    //  Spec references:
    //    • https://en.wikipedia.org/wiki/M3U
    //    • https://datatracker.ietf.org/doc/html/rfc8216 (HLS – superset)
    //
    //  Format overview:
    //    #EXTM3U                           ← header (optional but recommended)
    //    #EXTINF:123,Artist – Title        ← duration (integer seconds), title
    //    /path/to/file.mp3                 ← media path (next non-comment line)
    //
    //  Lines starting with '#' that are NOT recognised directives are
    //  treated as comments and skipped.  Blank lines are also skipped.
    //  M3U uses the system default encoding; M3U8 is always UTF-8.
    // ===================================================================

    private static List<PlaylistEntry> ReadM3U(string path, Encoding encoding, string baseDir)
    {
        var entries = new List<PlaylistEntry>();
        var lines = File.ReadAllLines(path, encoding);

        string pendingTitle = string.Empty;
        var pendingDuration = TimeSpan.Zero;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            if (string.IsNullOrEmpty(line))
                continue;

            // ---- Extended header (just skip it) ----
            if (line.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
                continue;

            // ---- EXTINF directive ----
            if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                ParseExtInf(line, out pendingDuration, out pendingTitle);
                continue;
            }

            // ---- Other comment / directive ----
            if (line.StartsWith('#'))
                continue;

            // ---- This is a media path ----
            var resolvedPath = ResolvePath(line, baseDir);
            entries.Add(new PlaylistEntry
            {
                Path = resolvedPath,
                Title = pendingTitle,
                Duration = pendingDuration
            });

            pendingTitle = string.Empty;
            pendingDuration = TimeSpan.Zero;
        }

        return entries;
    }

    /// <summary>
    /// Parses an <c>#EXTINF</c> line into duration and title.
    /// Accepts both integer and decimal seconds.
    /// </summary>
    private static void ParseExtInf(string line, out TimeSpan duration, out string title)
    {
        duration = TimeSpan.Zero;
        title = string.Empty;

        // Strip "#EXTINF:" prefix
        var payload = line.Substring("#EXTINF:".Length).Trim();

        var commaIndex = payload.IndexOf(',');
        if (commaIndex < 0)
        {
            // No comma — try to parse the whole thing as duration
            if (double.TryParse(payload, NumberStyles.Float, CultureInfo.InvariantCulture, out var sec) && sec >= 0)
                duration = TimeSpan.FromSeconds(sec);
            return;
        }

        var durStr = payload.Substring(0, commaIndex).Trim();
        title = payload.Substring(commaIndex + 1).Trim();

        if (double.TryParse(durStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0)
            duration = TimeSpan.FromSeconds(seconds);
    }

    private static void WriteM3U(string path, IReadOnlyList<PlaylistEntry> entries,
                                  Encoding encoding, string baseDir)
    {
        using var writer = new StreamWriter(path, false, encoding);
        writer.WriteLine("#EXTM3U");

        foreach (var entry in entries)
        {
            var durationSeconds = (int)Math.Round(entry.Duration.TotalSeconds);
            if (durationSeconds <= 0) durationSeconds = -1;

            var displayTitle = !string.IsNullOrWhiteSpace(entry.Title)
                ? entry.Title
                : System.IO.Path.GetFileNameWithoutExtension(entry.Path);

            writer.WriteLine($"#EXTINF:{durationSeconds},{displayTitle}");
            writer.WriteLine(MakeRelativePath(entry.Path, baseDir));
        }
    }

    // ===================================================================
    //  PLS — Winamp / SHOUTcast playlist
    // ===================================================================
    //  Spec references:
    //    • https://en.wikipedia.org/wiki/PLS_(file_format)
    //
    //  Format overview (INI-style):
    //    [playlist]
    //    File1=/path/to/first.mp3
    //    Title1=First Song
    //    Length1=120                       ← seconds, –1 = unknown
    //    File2=/path/to/second.ogg
    //    Title2=Second Song
    //    Length2=240
    //    NumberOfEntries=2
    //    Version=2
    //
    //  Keys are case-insensitive.  Entry indices are 1-based and may have
    //  gaps (rare but allowed).
    // ===================================================================

    private static List<PlaylistEntry> ReadPLS(string path, string baseDir)
    {
        var lines = File.ReadAllLines(path, Encoding.UTF8);

        // Collect keyed values
        var files = new Dictionary<int, string>();
        var titles = new Dictionary<int, string>();
        var lengths = new Dictionary<int, int>();

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('[') || line.StartsWith(';'))
                continue;

            var eq = line.IndexOf('=');
            if (eq < 1) continue;

            var key = line.Substring(0, eq).Trim();
            var val = line.Substring(eq + 1).Trim();

            if (TryExtractIndexedKey(key, "File", out var idx))
                files[idx] = val;
            else if (TryExtractIndexedKey(key, "Title", out idx))
                titles[idx] = val;
            else if (TryExtractIndexedKey(key, "Length", out idx))
            {
                if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sec) && sec >= 0)
                    lengths[idx] = sec;
            }
        }

        // Build entries in index order
        var entries = new List<PlaylistEntry>();
        foreach (var index in files.Keys.OrderBy(k => k))
        {
            var entry = new PlaylistEntry
            {
                Path = ResolvePath(files[index], baseDir)
            };

            if (titles.TryGetValue(index, out var t))
                entry.Title = t;

            if (lengths.TryGetValue(index, out var len) && len > 0)
                entry.Duration = TimeSpan.FromSeconds(len);

            entries.Add(entry);
        }

        return entries;
    }

    /// <summary>
    /// Tries to match a key like "File3" or "Title12" against the given
    /// prefix and extract the 1-based index.
    /// </summary>
    private static bool TryExtractIndexedKey(string key, string prefix, out int index)
    {
        index = 0;
        if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var numPart = key.Substring(prefix.Length);
        return int.TryParse(numPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out index);
    }

    private static void WritePLS(string path, IReadOnlyList<PlaylistEntry> entries,
                                  string baseDir)
    {
        using var writer = new StreamWriter(path, false, Encoding.UTF8);
        writer.WriteLine("[playlist]");
        writer.WriteLine();

        for (int i = 0; i < entries.Count; i++)
        {
            var n = i + 1; // PLS uses 1-based indexing
            var entry = entries[i];

            writer.WriteLine($"File{n}={MakeRelativePath(entry.Path, baseDir)}");

            var title = !string.IsNullOrWhiteSpace(entry.Title)
                ? entry.Title
                : System.IO.Path.GetFileNameWithoutExtension(entry.Path);
            writer.WriteLine($"Title{n}={title}");

            var durationSeconds = (int)Math.Round(entry.Duration.TotalSeconds);
            writer.WriteLine($"Length{n}={(durationSeconds > 0 ? durationSeconds : -1)}");
            writer.WriteLine();
        }

        writer.WriteLine($"NumberOfEntries={entries.Count}");
        writer.WriteLine("Version=2");
    }

    // ===================================================================
    //  WPL — Windows Playlist (SMIL-based XML)
    // ===================================================================
    //  Spec references:
    //    • https://docs.microsoft.com/en-us/windows/win32/wmp/windows-media-playlist-elements-reference
    //
    //  Format overview:
    //    <?wpl version="1.0"?>
    //    <smil>
    //      <head>
    //        <meta name="Generator" content="Open Burning Suite"/>
    //        <meta name="ItemCount" content="3"/>
    //        <title>My Playlist</title>
    //      </head>
    //      <body>
    //        <seq>
    //          <media src="path/to/file1.mp3"/>
    //          <media src="path/to/file2.wav"/>
    //        </seq>
    //      </body>
    //    </smil>
    //
    //  The <media> element carries only a "src" attribute in the minimal
    //  form.  Optional attributes include "albumTitle", "albumArtist",
    //  "trackTitle", "trackArtist", "duration" (ms).
    // ===================================================================

    private static List<PlaylistEntry> ReadWPL(string path, string baseDir)
    {
        var entries = new List<PlaylistEntry>();

        try
        {
            // WPL files start with <?wpl ...?> which is not a valid XML PI
            // for standard parsers.  Read the raw text and replace it.
            var text = File.ReadAllText(path, Encoding.UTF8);
            text = SanitiseWplProcessingInstruction(text);

            var doc = XDocument.Parse(text);

            // Find all <media> elements anywhere in the document
            var mediaElements = doc.Descendants()
                .Where(e => e.Name.LocalName.Equals("media", StringComparison.OrdinalIgnoreCase));

            foreach (var media in mediaElements)
            {
                var src = media.Attribute("src")?.Value;
                if (string.IsNullOrWhiteSpace(src))
                    continue;

                var entry = new PlaylistEntry
                {
                    Path = ResolvePath(src, baseDir)
                };

                // Optional metadata attributes
                var trackTitle = media.Attribute("trackTitle")?.Value;
                if (!string.IsNullOrWhiteSpace(trackTitle))
                    entry.Title = trackTitle;

                var durMs = media.Attribute("duration")?.Value;
                if (!string.IsNullOrWhiteSpace(durMs) &&
                    long.TryParse(durMs, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) &&
                    ms > 0)
                {
                    entry.Duration = TimeSpan.FromMilliseconds(ms);
                }

                entries.Add(entry);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PlaylistParser] Error reading WPL '{path}': {ex.Message}");
        }

        return entries;
    }

    /// <summary>
    /// Replaces the non-standard <c>&lt;?wpl …?&gt;</c> processing
    /// instruction with a proper XML declaration so that
    /// <see cref="XDocument.Parse"/> can handle it.
    /// </summary>
    private static string SanitiseWplProcessingInstruction(string text)
    {
        // The PI can appear as <?wpl version="1.0"?>
        const string wplPi = "<?wpl";
        var idx = text.IndexOf(wplPi, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            var end = text.IndexOf("?>", idx, StringComparison.Ordinal);
            if (end >= 0)
            {
                text = text.Substring(0, idx)
                       + "<?xml version=\"1.0\" encoding=\"UTF-8\"?>"
                       + text.Substring(end + 2);
            }
        }

        return text;
    }

    private static void WriteWPL(string path, IReadOnlyList<PlaylistEntry> entries,
                                  string? title, string baseDir)
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "    ",
            Encoding = Encoding.UTF8,
            OmitXmlDeclaration = true
        };

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = XmlWriter.Create(stream, settings);

        // WPL-specific processing instruction
        writer.WriteProcessingInstruction("wpl", "version=\"1.0\"");
        writer.WriteStartElement("smil");

        // <head>
        writer.WriteStartElement("head");

        writer.WriteStartElement("meta");
        writer.WriteAttributeString("name", "Generator");
        writer.WriteAttributeString("content", "Open Burning Suite");
        writer.WriteEndElement(); // meta

        writer.WriteStartElement("meta");
        writer.WriteAttributeString("name", "ItemCount");
        writer.WriteAttributeString("content", entries.Count.ToString(CultureInfo.InvariantCulture));
        writer.WriteEndElement(); // meta

        writer.WriteElementString("title", title ?? "Playlist");

        writer.WriteEndElement(); // head

        // <body><seq>
        writer.WriteStartElement("body");
        writer.WriteStartElement("seq");

        foreach (var entry in entries)
        {
            writer.WriteStartElement("media");
            writer.WriteAttributeString("src", MakeRelativePath(entry.Path, baseDir));

            if (!string.IsNullOrWhiteSpace(entry.Title))
                writer.WriteAttributeString("trackTitle", entry.Title);

            if (entry.Duration.TotalMilliseconds > 0)
            {
                writer.WriteAttributeString("duration",
                    ((long)entry.Duration.TotalMilliseconds).ToString(CultureInfo.InvariantCulture));
            }

            writer.WriteEndElement(); // media
        }

        writer.WriteEndElement(); // seq
        writer.WriteEndElement(); // body
        writer.WriteEndElement(); // smil
    }

    // ===================================================================
    //  ASX — Advanced Stream Redirector
    // ===================================================================
    //  Spec references:
    //    • https://docs.microsoft.com/en-us/windows/win32/wmp/asx-element
    //    • https://en.wikipedia.org/wiki/Advanced_Stream_Redirector
    //
    //  Format overview:
    //    <asx version="3.0">
    //      <title>My Playlist</title>
    //      <entry>
    //        <title>Track 1</title>
    //        <ref href="path/to/file1.mp3"/>
    //        <duration value="00:03:45"/>
    //      </entry>
    //      <entry>
    //        <title>Track 2</title>
    //        <ref href="path/to/file2.wav"/>
    //      </entry>
    //    </asx>
    //
    //  Element and attribute names are case-insensitive per the spec.
    //  Each <entry> can contain multiple <ref> elements (fallback URLs);
    //  we take the first one.  <duration value="…"/> uses HH:MM:SS or
    //  HH:MM:SS.mmm format.
    // ===================================================================

    private static List<PlaylistEntry> ReadASX(string path, string baseDir)
    {
        var entries = new List<PlaylistEntry>();

        try
        {
            var text = File.ReadAllText(path, Encoding.UTF8);

            // ASX tags are case-insensitive — XDocument expects well-formed
            // XML so we normalise the root and key element names.
            // However, simple lowercase works for most real-world files.
            var doc = XDocument.Parse(text);

            var entryElements = doc.Descendants()
                .Where(e => e.Name.LocalName.Equals("entry", StringComparison.OrdinalIgnoreCase));

            foreach (var entryEl in entryElements)
            {
                var refEl = entryEl.Elements()
                    .FirstOrDefault(e => e.Name.LocalName.Equals("ref", StringComparison.OrdinalIgnoreCase));

                var href = refEl?.Attribute("href")?.Value
                           ?? refEl?.Attribute("HREF")?.Value
                           ?? refEl?.Attribute("Href")?.Value;

                if (string.IsNullOrWhiteSpace(href))
                    continue;

                var entry = new PlaylistEntry
                {
                    Path = ResolvePath(href, baseDir)
                };

                // <title>
                var titleEl = entryEl.Elements()
                    .FirstOrDefault(e => e.Name.LocalName.Equals("title", StringComparison.OrdinalIgnoreCase));
                if (titleEl != null)
                    entry.Title = titleEl.Value.Trim();

                // <duration value="HH:MM:SS"/>
                var durEl = entryEl.Elements()
                    .FirstOrDefault(e => e.Name.LocalName.Equals("duration", StringComparison.OrdinalIgnoreCase));
                var durValue = durEl?.Attribute("value")?.Value
                               ?? durEl?.Attribute("VALUE")?.Value
                               ?? durEl?.Attribute("Value")?.Value;
                if (!string.IsNullOrWhiteSpace(durValue))
                    entry.Duration = ParseAsxDuration(durValue);

                entries.Add(entry);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PlaylistParser] Error reading ASX '{path}': {ex.Message}");
        }

        return entries;
    }

    /// <summary>Parses an ASX duration value (HH:MM:SS or HH:MM:SS.mmm).</summary>
    private static TimeSpan ParseAsxDuration(string value)
    {
        value = value.Trim();

        if (TimeSpan.TryParseExact(value, new[] { @"hh\:mm\:ss\.fff", @"hh\:mm\:ss", @"h\:mm\:ss",
                @"mm\:ss", @"m\:ss" },
                CultureInfo.InvariantCulture, out var ts))
            return ts;

        // Fallback: try general TimeSpan parsing
        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out ts))
            return ts;

        return TimeSpan.Zero;
    }

    private static void WriteASX(string path, IReadOnlyList<PlaylistEntry> entries,
                                  string? title, string baseDir)
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "    ",
            Encoding = Encoding.UTF8,
            OmitXmlDeclaration = false
        };

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = XmlWriter.Create(stream, settings);

        writer.WriteStartElement("asx");
        writer.WriteAttributeString("version", "3.0");

        // Playlist-level title
        writer.WriteElementString("title", title ?? "Playlist");

        foreach (var entry in entries)
        {
            writer.WriteStartElement("entry");

            // Track title
            var trackTitle = !string.IsNullOrWhiteSpace(entry.Title)
                ? entry.Title
                : System.IO.Path.GetFileNameWithoutExtension(entry.Path);
            writer.WriteElementString("title", trackTitle);

            // <ref href="..."/>
            writer.WriteStartElement("ref");
            writer.WriteAttributeString("href", MakeRelativePath(entry.Path, baseDir));
            writer.WriteEndElement(); // ref

            // <duration value="HH:MM:SS"/>
            if (entry.Duration.TotalSeconds > 0)
            {
                writer.WriteStartElement("duration");
                writer.WriteAttributeString("value", entry.Duration.ToString(@"hh\:mm\:ss"));
                writer.WriteEndElement(); // duration
            }

            writer.WriteEndElement(); // entry
        }

        writer.WriteEndElement(); // asx
    }

    // -----------------------------------------------------------------------
    //  Path resolution helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Resolves a path from a playlist entry.  If the path is relative it
    /// is combined with <paramref name="baseDir"/>.  Back-slashes are
    /// normalised to the OS separator.
    /// </summary>
    private static string ResolvePath(string rawPath, string baseDir)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return string.Empty;

        // Normalise directory separators for the current OS
        var normalised = rawPath.Replace('\\', System.IO.Path.DirectorySeparatorChar)
                                .Replace('/', System.IO.Path.DirectorySeparatorChar);

        // If the path is already absolute, return it as-is
        if (System.IO.Path.IsPathRooted(normalised))
            return System.IO.Path.GetFullPath(normalised);

        // Resolve against the playlist's base directory
        if (!string.IsNullOrEmpty(baseDir))
            return System.IO.Path.GetFullPath(
                System.IO.Path.Combine(baseDir, normalised));

        return normalised;
    }

    /// <summary>
    /// Attempts to create a relative path from <paramref name="filePath"/>
    /// to <paramref name="baseDir"/>.  If the file is on a different drive
    /// or the relative path cannot be computed, returns the absolute path.
    /// </summary>
    private static string MakeRelativePath(string filePath, string baseDir)
    {
        if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(baseDir))
            return filePath;

        try
        {
            var fileUri = new Uri(System.IO.Path.GetFullPath(filePath));
            var baseDirWithSlash = baseDir.TrimEnd(
                System.IO.Path.DirectorySeparatorChar,
                System.IO.Path.AltDirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            var baseUri = new Uri(baseDirWithSlash);

            var relativeUri = baseUri.MakeRelativeUri(fileUri);
            var relative = Uri.UnescapeDataString(relativeUri.ToString());

            // Convert URI forward-slashes to OS separators
            return relative.Replace('/', System.IO.Path.DirectorySeparatorChar);
        }
        catch
        {
            // If anything goes wrong (different drives on Windows, etc.)
            // just return the absolute path.
            return filePath;
        }
    }
}
