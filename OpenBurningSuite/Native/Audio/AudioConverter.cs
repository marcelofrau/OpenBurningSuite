// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using OpenBurningSuite.Models;

namespace OpenBurningSuite.Native.Audio;

/// <summary>
/// Native audio converter for audio-to-WAV conversion.
/// Converts audio tracks to 44100 Hz, 16-bit stereo WAV format for audio CD burning.
/// Supports WAV, MP3, AIFF, WMA, FLAC, OGG, AAC/M4A, APE, and Opus formats
/// via NAudio and AudioReaderFactory.
/// </summary>
public static class AudioConverter
{
    /// <summary>Target sample rate for audio CDs.</summary>
    private const int TargetSampleRate = 44100;
    /// <summary>Target channels for audio CDs (stereo).</summary>
    private const int TargetChannels = 2;
    /// <summary>Target bits per sample for audio CDs.</summary>
    private const int TargetBitsPerSample = 16;

    /// <summary>
    /// Converts an audio file to 44100 Hz, 16-bit stereo WAV format.
    /// </summary>
    /// <param name="inputPath">Path to the input audio file.</param>
    /// <param name="outputPath">Path for the output WAV file.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task ConvertToWavAsync(
        string inputPath,
        string outputPath,
        IProgress<BuildProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(inputPath))
            throw new FileNotFoundException($"Audio file not found: {inputPath}");

        var fi = new FileInfo(inputPath);
        if (fi.Length == 0)
            throw new InvalidOperationException($"Audio file is empty (0 bytes): {inputPath}");

        // Ensure output directory exists
        var outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
        {
            try { Directory.CreateDirectory(outputDir); }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Cannot create output directory '{outputDir}': {ex.Message}");
            }
        }

        var ext = Path.GetExtension(inputPath).ToLowerInvariant();

        // If it's already a conformant WAV, just copy it
        if (ext == ".wav" && IsConformantWav(inputPath))
        {
            File.Copy(inputPath, outputPath, overwrite: true);
            progress?.Report(new BuildProgress
            {
                LogLine = $"[Info] WAV file already conformant, copied: {Path.GetFileName(inputPath)}"
            });
            return;
        }

        progress?.Report(new BuildProgress
        {
            StatusMessage = $"Converting {Path.GetFileName(inputPath)} to WAV...",
            LogLine = $"[Info] Converting: {inputPath} → {outputPath}"
        });

        WaveStream? reader = null;
        try
        {
            reader = AudioReaderFactory.CreateReader(inputPath);

            // Resample to target format if needed
            var targetFormat = new WaveFormat(TargetSampleRate, TargetBitsPerSample, TargetChannels);

            ISampleProvider sampleProvider = reader.ToSampleProvider();

            // Convert channels if needed
            if (reader.WaveFormat.Channels == 1)
                sampleProvider = new NAudio.Wave.SampleProviders.MonoToStereoSampleProvider(sampleProvider);
            else if (reader.WaveFormat.Channels > 2)
                sampleProvider = new StereoDownmixSampleProvider(sampleProvider);

            // Write the output WAV
            await Task.Run(() =>
            {
                var resampled = new SampleToWaveProvider16(sampleProvider);

                // If sample rate doesn't match, we need to resample
                if (reader.WaveFormat.SampleRate != TargetSampleRate)
                {
                    // NAudio's built-in resampling via MediaFoundation is Windows-only.
                    // For cross-platform, we do a simple linear interpolation resample.
                    using var resampledStream = new ResamplingWaveProvider(resampled, targetFormat);
                    WaveFileWriter.CreateWaveFile(outputPath, resampledStream);
                }
                else
                {
                    WaveFileWriter.CreateWaveFile(outputPath, resampled);
                }
            }, ct);

            progress?.Report(new BuildProgress
            {
                LogLine = $"[Info] Conversion complete: {Path.GetFileName(outputPath)}"
            });
        }
        finally
        {
            reader?.Dispose();
        }
    }

    /// <summary>Checks if a WAV file is already 44100Hz/16-bit/stereo.</summary>
    private static bool IsConformantWav(string path)
    {
        try
        {
            using var reader = new WaveFileReader(path);
            return reader.WaveFormat.SampleRate == TargetSampleRate &&
                   reader.WaveFormat.BitsPerSample == TargetBitsPerSample &&
                   reader.WaveFormat.Channels == TargetChannels &&
                   reader.WaveFormat.Encoding == WaveFormatEncoding.Pcm;
        }
        catch
        {
            return false;
        }
    }

}

/// <summary>
/// Downmixes multi-channel audio to stereo by averaging channels.
/// Left output = average of left-side channels, Right output = average of right-side channels.
/// </summary>
internal sealed class StereoDownmixSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _sourceChannels;
    private float[]? _sourceBuffer;

    public WaveFormat WaveFormat { get; }

    public StereoDownmixSampleProvider(ISampleProvider source)
    {
        _source = source;
        _sourceChannels = source.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int framesRequested = count / 2;
        int sourceSamplesNeeded = framesRequested * _sourceChannels;

        if (_sourceBuffer == null || _sourceBuffer.Length < sourceSamplesNeeded)
            _sourceBuffer = new float[sourceSamplesNeeded];

        int sourceSamplesRead = _source.Read(_sourceBuffer, 0, sourceSamplesNeeded);
        int framesRead = sourceSamplesRead / _sourceChannels;

        int outIdx = offset;
        for (int frame = 0; frame < framesRead; frame++)
        {
            int srcIdx = frame * _sourceChannels;
            float left = 0, right = 0;
            int leftCount = 0, rightCount = 0;

            // Distribute channels: even-index channels → left, odd-index → right
            for (int ch = 0; ch < _sourceChannels; ch++)
            {
                if (ch % 2 == 0) { left += _sourceBuffer[srcIdx + ch]; leftCount++; }
                else { right += _sourceBuffer[srcIdx + ch]; rightCount++; }
            }

            buffer[outIdx++] = leftCount > 0 ? left / leftCount : 0;
            buffer[outIdx++] = rightCount > 0 ? right / rightCount : 0;
        }

        return framesRead * 2;
    }
}

/// <summary>
/// Simple cross-platform resampling provider using linear interpolation.
/// </summary>
internal sealed class ResamplingWaveProvider : IWaveProvider, IDisposable
{
    /// <summary>Maximum source buffer size to prevent memory exhaustion from extreme ratios.</summary>
    private const int MaxResamplingBufferSize = 64 * 1024 * 1024; // 64 MB

    private readonly IWaveProvider _source;
    private readonly WaveFormat _targetFormat;
    private readonly double _ratio;
    private byte[] _sourceBuffer;
    private int _sourceBufferValid;
    private double _samplePosition;

    public WaveFormat WaveFormat => _targetFormat;

    public ResamplingWaveProvider(IWaveProvider source, WaveFormat targetFormat)
    {
        _source = source;
        _targetFormat = targetFormat;
        if (targetFormat.SampleRate <= 0)
            throw new ArgumentException("Target sample rate must be positive.", nameof(targetFormat));
        _ratio = (double)source.WaveFormat.SampleRate / targetFormat.SampleRate;
        // Allocate enough for 2 seconds of source audio, capped at 16 MB to prevent memory exhaustion.
        // Minimum 4096 bytes ensures at least one frame of audio data can be buffered.
        var bufferSize = Math.Min(source.WaveFormat.AverageBytesPerSecond * 2, 16 * 1024 * 1024);
        _sourceBuffer = new byte[Math.Max(bufferSize, 4096)];
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        int bytesPerSample = _targetFormat.BitsPerSample / 8;
        int channels = _targetFormat.Channels;
        int frameSize = bytesPerSample * channels;
        int framesNeeded = count / frameSize;
        int sourceBytesPerSample = _source.WaveFormat.BitsPerSample / 8;
        int sourceFrameSize = sourceBytesPerSample * _source.WaveFormat.Channels;
        int bytesWritten = 0;

        // Calculate how much source data we need (with overflow protection)
        long neededSourceBytesL = (long)(framesNeeded * _ratio * sourceFrameSize) + sourceFrameSize * 2;
        var neededSourceBytes = (int)Math.Min(neededSourceBytesL, MaxResamplingBufferSize);

        // Grow the source buffer if necessary
        if (neededSourceBytes > _sourceBuffer.Length)
        {
            long newSize = Math.Min((long)neededSourceBytes + sourceFrameSize * 4, MaxResamplingBufferSize);
            var newBuf = new byte[newSize];
            Array.Copy(_sourceBuffer, 0, newBuf, 0, _sourceBufferValid);
            _sourceBuffer = newBuf;
        }

        // Fill the source buffer
        if (neededSourceBytes > _sourceBufferValid)
        {
            int toRead = Math.Min(neededSourceBytes - _sourceBufferValid,
                _sourceBuffer.Length - _sourceBufferValid);
            if (toRead > 0)
            {
                int read = _source.Read(_sourceBuffer, _sourceBufferValid, toRead);
                _sourceBufferValid += read;
            }
        }

        int sourceFramesAvailable = _sourceBufferValid / sourceFrameSize;

        // Pre-calculate max usable source frame index (need at least 2 adjacent for interpolation)
        // This ensures sourceFrame + 1 is always valid when sourceFrame < maxInterpolableFrame
        int maxInterpolableFrame = sourceFramesAvailable > 0 ? sourceFramesAvailable - 1 : 0;

        for (int i = 0; i < framesNeeded; i++)
        {
            int sourceFrame = (int)_samplePosition;
            if (sourceFrame >= maxInterpolableFrame) break;

            double fraction = _samplePosition - sourceFrame;

            for (int ch = 0; ch < channels; ch++)
            {
                int sourceCh = Math.Min(ch, _source.WaveFormat.Channels - 1);

                // Read two adjacent source samples for interpolation
                int idx0 = sourceFrame * sourceFrameSize + sourceCh * sourceBytesPerSample;
                int idx1 = (sourceFrame + 1) * sourceFrameSize + sourceCh * sourceBytesPerSample;

                // Bounds check: ensure both sample reads are fully within the valid source buffer.
                // BitConverter.ToInt16 reads sourceBytesPerSample bytes starting at the index.
                if (idx0 + sourceBytesPerSample > _sourceBufferValid ||
                    idx1 + sourceBytesPerSample > _sourceBufferValid)
                    break;

                short sample0 = BitConverter.ToInt16(_sourceBuffer, idx0);
                short sample1 = BitConverter.ToInt16(_sourceBuffer, idx1);

                // Linear interpolation with clamping to prevent overflow
                double interpolated = sample0 + (sample1 - sample0) * fraction;
                short result = (short)Math.Clamp(interpolated, short.MinValue, short.MaxValue);

                BitConverter.GetBytes(result).CopyTo(buffer, offset + bytesWritten);
                bytesWritten += bytesPerSample;
            }

            _samplePosition += _ratio;
        }

        // Shift consumed source data to prevent unbounded position growth.
        // Use Math.Floor to avoid truncation-induced drift over long audio tracks.
        int consumedFrames = (int)Math.Floor(_samplePosition);
        int consumedBytes = consumedFrames * sourceFrameSize;
        if (consumedBytes > 0 && consumedBytes <= _sourceBufferValid)
        {
            Array.Copy(_sourceBuffer, consumedBytes, _sourceBuffer, 0,
                _sourceBufferValid - consumedBytes);
            _sourceBufferValid -= consumedBytes;
            _samplePosition -= consumedFrames;
        }

        return bytesWritten;
    }

    public void Dispose() { }
}
