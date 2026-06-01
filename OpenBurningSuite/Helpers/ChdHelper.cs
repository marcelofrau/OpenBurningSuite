// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using OpenBurningSuite.Services;

namespace OpenBurningSuite.Helpers;

/// <summary>Disc type detected from a CHD file.</summary>
public enum ChdDiscType
{
    /// <summary>CD-ROM media (e.g., PS1, Saturn, Mega CD). Use extractcd → BIN/CUE.</summary>
    Cd,
    /// <summary>DVD/Blu-ray media (e.g., PS2, Xbox, Wii). Use extractdvd → ISO.</summary>
    Dvd,
    /// <summary>Hard disk image. Use extracthd → raw IMG.</summary>
    Hdd,
    /// <summary>Laserdisc. Use extractld → AVI.</summary>
    Laserdisc,
    /// <summary>Raw data CHD. Use extractraw → raw file.</summary>
    Raw,
    /// <summary>Could not determine the type.</summary>
    Unknown
}

/// <summary>Helper for detecting and using chdman (MAME CHD tool).</summary>
public static class ChdHelper
{
    private static string? _chdmanPath;
    private static string? _cachedVersionString;
    private static Version? _cachedVersion;

    /// <summary>Minimum chdman version tested: 0.287.</summary>
    public static readonly Version MinimumVersion = new(0, 287);

    /// <summary>
    /// Detects and returns the path to chdman, or null if not found.
    /// Searches PATH and common installation directories per platform.
    /// </summary>
    public static string? FindChdman()
    {
        if (_chdmanPath != null)
            return _chdmanPath.Length > 0 ? _chdmanPath : null;

        var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "chdman.exe" : "chdman";

        // Honor user-configured path from settings if present
        try
        {
            var userPath = SettingsService.Current.ChdmanPath;
            if (!string.IsNullOrWhiteSpace(userPath) && File.Exists(userPath))
            {
                _chdmanPath = userPath;
                return _chdmanPath;
            }
        }
        catch { /* ignore settings access errors and fall back to auto-detection */ }

        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        foreach (var dir in pathDirs)
        {
            var candidate = Path.Combine(dir, exeName);
            if (File.Exists(candidate))
            {
                _chdmanPath = candidate;
                return _chdmanPath;
            }
        }

        string[] extraPaths;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            extraPaths =
            [
                @"C:\mame\chdman.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "mame", "chdman.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mame", "chdman.exe")
            ];
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            extraPaths =
            [
                "/opt/homebrew/bin/chdman",
                "/usr/local/bin/chdman",
                "/usr/bin/chdman"
            ];
        }
        else
        {
            extraPaths =
            [
                "/usr/bin/chdman",
                "/usr/local/bin/chdman",
                "/snap/bin/chdman",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "mame", "chdman")
            ];
        }

        foreach (var path in extraPaths)
        {
            if (File.Exists(path))
            {
                _chdmanPath = path;
                return _chdmanPath;
            }
        }

        _chdmanPath = string.Empty;
        return null;
    }

    /// <summary>Returns true if chdman is available on this system.</summary>
    public static bool IsAvailable() => FindChdman() != null;

    /// <summary>
    /// Returns the chdman version string (e.g. "0.287"), or null if not detected.
    /// </summary>
    public static string? GetVersionString()
    {
        if (_cachedVersionString != null)
            return _cachedVersionString.Length > 0 ? _cachedVersionString : null;

        var chdman = FindChdman();
        if (chdman == null)
            return null;

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = chdman,
                    Arguments = "",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = false
                }
            };

            process.Start();
            // chdman outputs the banner to stdout, then shows usage
            var stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);

            // Parse version from banner: "chdman - ... manager 0.287 (mame0287)"
            var idx = stdout.IndexOf("manager ");
            if (idx >= 0)
            {
                var from = idx + "manager ".Length;
                var space = stdout.IndexOf(' ', from);
                _cachedVersionString = space >= 0 ? stdout[from..space] : stdout[from..];
                return _cachedVersionString;
            }
        }
        catch { /* ignore parse errors */ }

        _cachedVersionString = string.Empty;
        return null;
    }

    /// <summary>
    /// Returns the parsed chdman <see cref="Version"/>, or null if not detected.
    /// </summary>
    public static Version? GetVersion()
    {
        if (_cachedVersion != null)
            return _cachedVersion;

        var verStr = GetVersionString();
        if (verStr == null)
            return null;

        try
        {
            _cachedVersion = Version.Parse(verStr);
            return _cachedVersion;
        }
        catch { /* ignore parse errors */ }

        return null;
    }

    /// <summary>
    /// Detects the disc type of a CHD file by running chdman info and
    /// parsing the unit size and compression fields.
    /// </summary>
    /// <param name="chdPath">Path to the .chd file.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The detected <see cref="ChdDiscType"/>.</returns>
    public static async Task<ChdDiscType> GetDiscTypeAsync(
        string chdPath,
        CancellationToken ct = default)
    {
        var chdman = FindChdman() ?? throw new InvalidOperationException(
            "chdman is not installed. Please install MAME tools (chdman) to identify CHD files.");

        if (!File.Exists(chdPath))
            throw new FileNotFoundException($"CHD file not found: {chdPath}");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = chdman,
                Arguments = $"info -i \"{chdPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        process.Start();
        var lines = new List<string>();
        while (!process.StandardOutput.EndOfStream)
        {
            var line = await process.StandardOutput.ReadLineAsync(ct);
            if (line != null) lines.Add(line);
        }
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
            return ChdDiscType.Unknown;

        return ParseChdDiscType(lines);
    }

    /// <summary>
    /// Parses chdman info output to determine the CHD disc type.
    /// </summary>
    private static ChdDiscType ParseChdDiscType(List<string> lines)
    {
        int unitSize = 0;
        string? compression = null;

        // Attempt 1: look for "CHD type: DVD" or similar in metadata lines
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            // Check metadata field: "CHT2" tag → CD
            if (trimmed.StartsWith("Tag='CHT", StringComparison.OrdinalIgnoreCase))
                return ChdDiscType.Cd;
            if (trimmed.StartsWith("Tag='CHD2", StringComparison.OrdinalIgnoreCase))
                return ChdDiscType.Dvd;
        }

        // Attempt 2: parse Unit Size and Compression for heuristic
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Unit Size:", StringComparison.OrdinalIgnoreCase))
            {
                var val = trimmed["Unit Size:".Length..].Trim();
                var space = val.IndexOf(' ');
                if (space > 0) val = val[..space];
                int.TryParse(val, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out unitSize);
            }
            else if (trimmed.StartsWith("Compression:", StringComparison.OrdinalIgnoreCase))
            {
                compression = trimmed["Compression:".Length..].Trim();
            }
        }

        if (unitSize == 2448 || unitSize == 2352)
            return ChdDiscType.Cd;

        // DVD CHDs use zlib/zstd (not cd* compression) and 2048 unit size
        if (unitSize == 2048)
            return ChdDiscType.Dvd;

        if (unitSize == 512)
            return ChdDiscType.Hdd;

        // Unit size unknown — fall back to checking compression name prefixes
        if (compression != null)
        {
            if (compression.StartsWith("cd", StringComparison.OrdinalIgnoreCase))
                return ChdDiscType.Cd;
            if (compression.StartsWith("dvd", StringComparison.OrdinalIgnoreCase))
                return ChdDiscType.Dvd;
            if (compression.StartsWith("hd", StringComparison.OrdinalIgnoreCase))
                return ChdDiscType.Hdd;
            if (compression.StartsWith("ld", StringComparison.OrdinalIgnoreCase))
                return ChdDiscType.Laserdisc;
        }

        return ChdDiscType.Unknown;
    }

    /// <summary>
    /// Determines the chdman extract command and output file extension to use
    /// for the given disc type.
    /// </summary>
    private static (string command, string outputExt) GetExtractCommand(ChdDiscType type)
    {
        return type switch
        {
            ChdDiscType.Cd => ("extractcd", ".bin"),
            ChdDiscType.Dvd => ("extractdvd", ".iso"),
            ChdDiscType.Hdd => ("extracthd", ".img"),
            ChdDiscType.Laserdisc => ("extractld", ".avi"),
            ChdDiscType.Raw => ("extractraw", ".raw"),
            _ => throw new NotSupportedException($"Cannot extract CHD of type {type}. Only CD, DVD, HDD, Laserdisc, and Raw are supported.")
        };
    }

    /// <summary>
    /// Extracts a CHD file to a burnable format (BIN/CUE for CD, ISO for DVD).
    /// Auto-detects the disc type when <paramref name="chdType"/> is null.
    /// Reports progress via <paramref name="progress"/> when provided.
    /// Progress is parsed from chdman's own progress lines (stderr).
    /// </summary>
    /// <param name="chdPath">Path to the .chd file.</param>
    /// <param name="outputBinPath">Path for the output .bin (CD) or .iso (DVD) file.</param>
    /// <param name="outputCuePath">Path for the output .cue file (CD only, pass null for DVD/HDD).</param>
    /// <param name="chdType">Optional pre-detected disc type. When null, auto-detects.</param>
    /// <param name="progress">Optional progress reporter (0-100).</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task ExtractAsync(
        string chdPath,
        string outputBinPath,
        string? outputCuePath,
        ChdDiscType? chdType = null,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        var chdman = FindChdman() ?? throw new InvalidOperationException(
            "chdman is not installed. Please install MAME tools (chdman) to burn CHD files.");

        if (!File.Exists(chdPath))
            throw new FileNotFoundException($"CHD file not found: {chdPath}");

        // Auto-detect type if not provided
        chdType ??= await GetDiscTypeAsync(chdPath, ct);
        if (chdType == ChdDiscType.Unknown)
            throw new InvalidOperationException(
                $"Could not determine the disc type of CHD file: {chdPath}");

        if (chdType == ChdDiscType.Cd && outputCuePath == null)
            throw new ArgumentException(
                "outputCuePath is required for CD CHD extraction.", nameof(outputCuePath));

        var cmd = chdType.Value == ChdDiscType.Cd ? "extractcd"
            : chdType.Value == ChdDiscType.Dvd ? "extractdvd"
            : GetExtractCommand(chdType.Value).command;

        var args = cmd == "extractcd"
            ? $"{cmd} -i \"{chdPath}\" -o \"{outputCuePath}\" -ob \"{outputBinPath}\""
            : $"{cmd} -i \"{chdPath}\" -o \"{outputBinPath}\"";

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = chdman,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        process.Start();

        // Read stderr line-by-line to parse chdman's own progress output
        // chdman prints lines like: "Output so far: 12345678 bytes, 45% complete"
        var errorLines = new System.Text.StringBuilder();
        while (!process.StandardError.EndOfStream)
        {
            var line = await process.StandardError.ReadLineAsync(ct);
            if (line == null) break;
            errorLines.AppendLine(line);

            if (progress != null)
            {
                var pct = ParseProgressPercent(line);
                if (pct >= 0)
                    progress.Report(pct);
            }
        }

        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"chdman {cmd} failed (exit code {process.ExitCode}): {errorLines}");
        }
    }

    /// <summary>
    /// Parses a chdman progress line like "Extracting, 45.6% complete...".
    /// Returns the percentage (0-100), or -1 if the line is not a progress line.
    /// </summary>
    private static int ParseProgressPercent(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return -1;

        // Expected format: "Extracting, XX.X% complete..." (chdman 0.287)
        var pctIdx = line.IndexOf('%');
        if (pctIdx < 0) return -1;

        // Walk backwards from '%' past decimal and digits: "XX.X" or "XX"
        var end = pctIdx - 1;
        while (end >= 0 && (char.IsDigit(line[end]) || line[end] == '.'))
            end--;

        var numStr = line[(end + 1)..pctIdx];
        if (double.TryParse(numStr, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var pct)
            && pct >= 0 && pct <= 100)
            return (int)Math.Round(pct);

        return -1;
    }
}
