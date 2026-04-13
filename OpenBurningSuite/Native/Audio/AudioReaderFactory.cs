// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace OpenBurningSuite.Native.Audio;

/// <summary>
/// Cross-platform audio file reader factory supporting WAV, MP3, AIFF, WMA, FLAC, OGG,
/// AAC/M4A, APE (Monkey's Audio), and Opus formats.
///
/// Reader selection strategy per format:
///   WAV       → WaveFileReader (cross-platform, managed)
///   MP3       → Mp3FileReader  (cross-platform, managed NLayer decoder)
///   AIFF      → AiffFileReader (cross-platform, managed)
///   WMA       → MediaFoundationReader on Windows; ffmpeg-to-WAV conversion on Linux/macOS
///   FLAC      → MediaFoundationReader on Windows (Win10+); ffmpeg-to-WAV on Linux/macOS
///   AAC / M4A → MediaFoundationReader on Windows; ffmpeg-to-WAV on Linux/macOS
///   OGG       → ffmpeg-to-WAV conversion (all platforms)
///   APE       → ffmpeg-to-WAV conversion (all platforms)
///   Opus      → ffmpeg-to-WAV conversion (all platforms)
///
/// For formats requiring ffmpeg on non-Windows platforms (or as fallback on Windows),
/// ffmpeg must be installed and available in PATH. The factory converts to a temporary
/// 44100 Hz / 16-bit / stereo WAV file, then reads it with WaveFileReader. Temporary
/// files are cleaned up automatically when the returned WaveStream is disposed.
/// </summary>
public static class AudioReaderFactory
{
    /// <summary>
    /// All audio file extensions that this factory can open.
    /// </summary>
    public static readonly string[] SupportedExtensions =
        { ".wav", ".mp3", ".aiff", ".aif", ".wma", ".flac", ".ogg", ".aac", ".m4a", ".ape", ".opus" };

    /// <summary>
    /// Creates a WaveStream reader for the given audio file.
    /// The caller is responsible for disposing the returned stream.
    /// </summary>
    /// <param name="filePath">Path to an audio file.</param>
    /// <returns>A WaveStream positioned at the beginning of the audio data.</returns>
    /// <exception cref="FileNotFoundException">File does not exist.</exception>
    /// <exception cref="InvalidOperationException">Format not supported or cannot be decoded.</exception>
    public static WaveStream CreateReader(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"Audio file not found: {filePath}", filePath);

        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        return ext switch
        {
            ".wav" => new WaveFileReader(filePath),
            ".mp3" => new Mp3FileReader(filePath),
            ".aiff" or ".aif" => new AiffFileReader(filePath),
            ".wma" => CreateWmaReader(filePath),
            ".flac" => CreateMediaFoundationOrFfmpegReader(filePath, "FLAC"),
            ".aac" or ".m4a" => CreateMediaFoundationOrFfmpegReader(filePath, "AAC"),
            ".ogg" => ConvertWithFfmpeg(filePath, "OGG Vorbis"),
            ".ape" => ConvertWithFfmpeg(filePath, "APE (Monkey's Audio)"),
            ".opus" => ConvertWithFfmpeg(filePath, "Opus"),
            _ => TryFallbackReaders(filePath)
                 ?? throw new InvalidOperationException(
                     $"Unsupported audio format: '{ext}'. " +
                     $"Supported formats: {string.Join(", ", SupportedExtensions)}.")
        };
    }

    /// <summary>
    /// Checks whether the given file extension is supported.
    /// </summary>
    public static bool IsSupported(string extension)
    {
        var ext = extension.StartsWith('.') ? extension : "." + extension;
        foreach (var s in SupportedExtensions)
        {
            if (string.Equals(s, ext, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // -----------------------------------------------------------------
    // WMA reader (platform-specific)
    // -----------------------------------------------------------------

    /// <summary>
    /// Creates a WaveStream for a WMA file.
    /// On Windows: uses MediaFoundationReader (native Windows Media Foundation codec).
    /// On Linux/macOS: converts to temporary WAV via ffmpeg subprocess.
    /// </summary>
    private static WaveStream CreateWmaReader(string filePath)
    {
        // Windows: use MediaFoundationReader which leverages Windows Media Foundation
        if (OperatingSystem.IsWindows())
        {
            try
            {
                return new MediaFoundationReader(filePath);
            }
            catch (Exception ex)
            {
                // MediaFoundation may fail if codec is missing; fall through to ffmpeg
                Debug.WriteLine($"MediaFoundationReader failed for WMA, trying ffmpeg: {ex.Message}");
            }
        }

        // All platforms: try ffmpeg-based conversion
        return ConvertWithFfmpeg(filePath, "WMA");
    }

    // -----------------------------------------------------------------
    // MediaFoundation + ffmpeg reader (FLAC, AAC/M4A)
    // -----------------------------------------------------------------

    /// <summary>
    /// Creates a WaveStream for formats supported by Windows Media Foundation natively
    /// (FLAC on Windows 10+, AAC/M4A). Falls back to ffmpeg on all platforms.
    /// </summary>
    /// <param name="filePath">Path to the audio file.</param>
    /// <param name="formatName">Human-readable format name for error messages.</param>
    private static WaveStream CreateMediaFoundationOrFfmpegReader(string filePath, string formatName)
    {
        // Windows: try MediaFoundationReader first (supports FLAC on Win10+, AAC natively)
        if (OperatingSystem.IsWindows())
        {
            try
            {
                return new MediaFoundationReader(filePath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(
                    $"MediaFoundationReader failed for {formatName}, trying ffmpeg: {ex.Message}");
            }
        }

        // All platforms: convert via ffmpeg
        return ConvertWithFfmpeg(filePath, formatName);
    }

    // -----------------------------------------------------------------
    // ffmpeg-based conversion fallback
    // -----------------------------------------------------------------

    /// <summary>
    /// Converts an audio file to a temporary WAV using ffmpeg, then returns a reader
    /// wrapping both the WAV reader and the temp file for automatic cleanup on dispose.
    /// This method performs synchronous I/O and should be called from a background thread.
    /// </summary>
    /// <param name="filePath">Path to the audio file to convert.</param>
    /// <param name="formatName">Human-readable format name for error messages.</param>
    private static WaveStream ConvertWithFfmpeg(string filePath, string formatName)
    {
        var ffmpegPath = FindFfmpegCached();
        if (ffmpegPath == null)
        {
            throw new InvalidOperationException(
                $"{formatName} audio format requires ffmpeg for decoding on this platform. " +
                "Please install ffmpeg and ensure it is available in your system PATH, " +
                "or convert the file to WAV or MP3 first.");
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "OpenBurningSuite");
        Directory.CreateDirectory(tempDir);
        var tempWav = Path.Combine(tempDir, $"audio_{Guid.NewGuid():N}.wav");

        try
        {
            // Use ArgumentList instead of Arguments to safely pass file paths
            // without risk of shell metacharacter injection.
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(filePath);
            psi.ArgumentList.Add("-ar");
            psi.ArgumentList.Add("44100");
            psi.ArgumentList.Add("-ac");
            psi.ArgumentList.Add("2");
            psi.ArgumentList.Add("-sample_fmt");
            psi.ArgumentList.Add("s16");
            psi.ArgumentList.Add(tempWav);

            using var process = Process.Start(psi);
            if (process == null)
                throw new InvalidOperationException("Failed to start ffmpeg process.");

            // Read stderr to prevent deadlock (ffmpeg writes progress to stderr)
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(120_000); // 2-minute timeout

            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw new InvalidOperationException(
                    "ffmpeg conversion timed out (120 seconds). The audio file may be corrupt or very large.");
            }

            if (process.ExitCode != 0)
            {
                // Clean up partial output
                TryDeleteFile(tempWav);
                throw new InvalidOperationException(
                    $"ffmpeg failed to convert '{Path.GetFileName(filePath)}' (exit code {process.ExitCode}). " +
                    $"Error: {TruncateMessage(stderr, 300)}");
            }

            if (!File.Exists(tempWav) || new FileInfo(tempWav).Length == 0)
            {
                TryDeleteFile(tempWav);
                throw new InvalidOperationException(
                    $"ffmpeg produced no output for '{Path.GetFileName(filePath)}'. " +
                    "The file may be corrupt or in an unsupported variant.");
            }

            // Return a wrapper that deletes the temp file on dispose
            var wavReader = new WaveFileReader(tempWav);
            return new TempFileWaveStream(wavReader, tempWav);
        }
        catch (Exception) when (!File.Exists(tempWav))
        {
            throw; // Re-throw if temp file wasn't created
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            TryDeleteFile(tempWav);
            throw new InvalidOperationException(
                $"Failed to convert '{Path.GetFileName(filePath)}' via ffmpeg: {ex.Message}", ex);
        }
    }

    // -----------------------------------------------------------------
    // Fallback: try known readers for unknown extensions
    // -----------------------------------------------------------------

    private static WaveStream? TryFallbackReaders(string filePath)
    {
        // Try WAV (some files may have wrong extensions)
        try { return new WaveFileReader(filePath); } catch { }

        // Try MP3
        try { return new Mp3FileReader(filePath); } catch { }

        // On Windows, try MediaFoundation as last resort (handles many formats)
        if (OperatingSystem.IsWindows())
        {
            try { return new MediaFoundationReader(filePath); } catch { }
        }

        // Try ffmpeg as final fallback (handles virtually any audio format)
        if (FindFfmpegCached() != null)
        {
            try { return ConvertWithFfmpeg(filePath, Path.GetExtension(filePath)); } catch { }
        }

        return null;
    }

    // -----------------------------------------------------------------
    // ffmpeg discovery (cached)
    // -----------------------------------------------------------------

    private static string? _cachedFfmpegPath;
    private static bool _ffmpegSearched;

    /// <summary>
    /// Returns the cached ffmpeg path, searching only on the first call.
    /// </summary>
    private static string? FindFfmpegCached()
    {
        if (_ffmpegSearched) return _cachedFfmpegPath;
        _cachedFfmpegPath = FindFfmpeg();
        _ffmpegSearched = true;
        return _cachedFfmpegPath;
    }

    /// <summary>
    /// Locates the ffmpeg executable on the system.
    /// Checks: bundled location, PATH, common install locations.
    /// </summary>
    internal static string? FindFfmpeg()
    {
        // 1. Check alongside the application
        var appDir = AppContext.BaseDirectory;
        var bundledName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var bundledPath = Path.Combine(appDir, bundledName);
        if (File.Exists(bundledPath))
            return bundledPath;

        // 2. Check PATH by trying to run ffmpeg -version
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = bundledName,
                Arguments = "-version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p != null)
            {
                if (!p.WaitForExit(5000))
                {
                    try { p.Kill(); } catch { }
                }
                if (p.HasExited && p.ExitCode == 0)
                    return bundledName; // Available in PATH
            }
        }
        catch { }

        // 3. Check common install locations
        string[] commonPaths;
        if (OperatingSystem.IsWindows())
        {
            commonPaths = new[]
            {
                @"C:\ffmpeg\bin\ffmpeg.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "bin", "ffmpeg.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ffmpeg", "bin", "ffmpeg.exe")
            };
        }
        else if (OperatingSystem.IsMacOS())
        {
            commonPaths = new[]
            {
                "/usr/local/bin/ffmpeg",
                "/opt/homebrew/bin/ffmpeg",
                "/opt/local/bin/ffmpeg"
            };
        }
        else // Linux
        {
            commonPaths = new[]
            {
                "/usr/bin/ffmpeg",
                "/usr/local/bin/ffmpeg",
                "/snap/bin/ffmpeg"
            };
        }

        foreach (var path in commonPaths)
        {
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static string TruncateMessage(string message, int maxLength)
    {
        if (string.IsNullOrEmpty(message)) return string.Empty;
        message = message.Trim();
        return message.Length <= maxLength ? message : message[..maxLength] + "…";
    }
}

/// <summary>
/// A WaveStream wrapper that owns a temporary file and deletes it upon disposal.
/// Used by AudioReaderFactory when an audio file is converted to a temp WAV via ffmpeg.
/// </summary>
internal sealed class TempFileWaveStream : WaveStream
{
    private readonly WaveStream _inner;
    private readonly string _tempFilePath;
    private bool _disposed;

    public TempFileWaveStream(WaveStream inner, string tempFilePath)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _tempFilePath = tempFilePath ?? throw new ArgumentNullException(nameof(tempFilePath));
    }

    public override WaveFormat WaveFormat => _inner.WaveFormat;
    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        _inner.Read(buffer, offset, count);

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            if (disposing)
            {
                try { _inner.Dispose(); } catch { }
            }
            // Delete temp file (best-effort)
            try { if (File.Exists(_tempFilePath)) File.Delete(_tempFilePath); } catch { }
        }
        base.Dispose(disposing);
    }
}
