// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using OpenBurningSuite.Services;

namespace OpenBurningSuite.Helpers;

/// <summary>Helper for detecting and using chdman (MAME CHD tool).</summary>
public static class ChdHelper
{
    private static string? _chdmanPath;

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
    /// Extracts a CHD file to BIN/CUE using chdman extractcd.
    /// </summary>
    /// <param name="chdPath">Path to the .chd file.</param>
    /// <param name="outputBinPath">Path for the output .bin file.</param>
    /// <param name="outputCuePath">Path for the output .cue file.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task ExtractToBinCueAsync(
        string chdPath,
        string outputBinPath,
        string outputCuePath,
        CancellationToken ct = default)
    {
        var chdman = FindChdman() ?? throw new InvalidOperationException(
            "chdman is not installed. Please install MAME tools (chdman) to burn CHD files.");

        if (!File.Exists(chdPath))
            throw new FileNotFoundException($"CHD file not found: {chdPath}");

        var args = $"extractcd \"{chdPath}\" \"{outputBinPath}\" \"{outputCuePath}\"";

        var process = new Process
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
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync(ct);
            throw new InvalidOperationException(
                $"chdman extractcd failed (exit code {process.ExitCode}): {error}");
        }
    }
}
