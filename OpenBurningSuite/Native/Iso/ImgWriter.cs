// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace OpenBurningSuite.Native.Iso;

/// <summary>
/// Writes raw disc image files (.img).
///
/// IMG files are headerless raw sector dumps — the file contains only
/// sector data with no metadata, headers, or footers.
///
/// Supports writing from:
/// - ISO files (2048-byte cooked sectors → copied as-is or wrapped in raw sectors)
/// - Raw sector streams (2352-byte or 2448-byte sectors)
/// - Any source stream with known sector size
/// </summary>
public static class ImgWriter
{
    /// <summary>
    /// Creates a raw IMG file from an ISO file.
    /// For cooked (2048-byte) ISOs, the data is copied directly (IMG = ISO in this case).
    /// </summary>
    /// <param name="isoPath">Path to the source ISO file.</param>
    /// <param name="imgPath">Path for the output IMG file.</param>
    /// <param name="sectorSize">Sector size of the source (default: 2048).</param>
    /// <param name="progress">Optional progress reporter (0-100).</param>
    public static void WriteFromIso(string isoPath, string imgPath,
        int sectorSize = 2048, IProgress<int>? progress = null)
    {
        if (!File.Exists(isoPath))
            throw new FileNotFoundException($"Source ISO file not found: {isoPath}");

        long fileSize = new FileInfo(isoPath).Length;
        long sectorCount = fileSize / sectorSize;
        if (sectorCount <= 0)
            throw new InvalidOperationException("Source file is empty or smaller than one sector.");

        using var input = new FileStream(isoPath, FileMode.Open, FileAccess.Read,
            FileShare.Read, 65536, false);
        using var output = new FileStream(imgPath, FileMode.Create, FileAccess.Write,
            FileShare.None, 65536, false);

        CopyWithProgress(input, output, fileSize, progress);
    }

    /// <summary>
    /// Asynchronously creates a raw IMG file from an ISO file.
    /// </summary>
    public static async Task WriteFromIsoAsync(string isoPath, string imgPath,
        int sectorSize = 2048, IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        await Task.Run(() => WriteFromIso(isoPath, imgPath, sectorSize, progress), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a raw IMG file from a data stream.
    /// The stream is read sequentially and written directly to the IMG file.
    /// </summary>
    /// <param name="dataStream">Source data stream (positioned at start).</param>
    /// <param name="imgPath">Path for the output IMG file.</param>
    /// <param name="totalBytes">Total number of bytes to write.</param>
    /// <param name="progress">Optional progress reporter (0-100).</param>
    public static void WriteFromStream(Stream dataStream, string imgPath,
        long totalBytes, IProgress<int>? progress = null)
    {
        using var output = new FileStream(imgPath, FileMode.Create, FileAccess.Write,
            FileShare.None, 65536, false);

        CopyWithProgress(dataStream, output, totalBytes, progress);
    }

    /// <summary>
    /// Asynchronously creates a raw IMG file from a data stream.
    /// </summary>
    public static async Task WriteFromStreamAsync(Stream dataStream, string imgPath,
        long totalBytes, IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        await Task.Run(() => WriteFromStream(dataStream, imgPath, totalBytes, progress), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a raw 2352-byte sector IMG file from a cooked 2048-byte ISO,
    /// wrapping each sector in a raw Mode 1 sector structure.
    /// </summary>
    /// <param name="isoPath">Path to the source 2048-byte ISO file.</param>
    /// <param name="imgPath">Path for the output raw 2352-byte IMG file.</param>
    /// <param name="progress">Optional progress reporter (0-100).</param>
    public static void WriteRawFromCooked(string isoPath, string imgPath,
        IProgress<int>? progress = null)
    {
        if (!File.Exists(isoPath))
            throw new FileNotFoundException($"Source ISO file not found: {isoPath}");

        long fileSize = new FileInfo(isoPath).Length;
        long sectorCount = fileSize / 2048;
        if (sectorCount <= 0)
            throw new InvalidOperationException("Source file is empty.");

        using var input = new FileStream(isoPath, FileMode.Open, FileAccess.Read,
            FileShare.Read, 65536, false);
        using var output = new FileStream(imgPath, FileMode.Create, FileAccess.Write,
            FileShare.None, 65536, false);

        var cookedBuf = new byte[2048];
        var rawBuf = new byte[2352];

        // CD sync pattern
        rawBuf[0] = 0x00;
        for (int i = 1; i <= 10; i++) rawBuf[i] = 0xFF;
        rawBuf[11] = 0x00;

        for (long s = 0; s < sectorCount; s++)
        {
            int bytesRead = ReadExact(input, cookedBuf, 0, 2048);
            if (bytesRead < 2048) break;

            // Build raw Mode 1 sector:
            // [0..11]  = sync pattern (already set)
            // [12..14] = address (MSF of sector)
            // [15]     = mode (1)
            // [16..2063] = user data (2048 bytes)
            // [2064..2351] = EDC/ECC (zero-filled here)

            long absoluteAddr = s + 150; // Add 150-sector lead-in offset
            int frames = (int)(absoluteAddr % 75);
            int seconds = (int)((absoluteAddr / 75) % 60);
            int minutes = (int)(absoluteAddr / 75 / 60);

            rawBuf[12] = ToBcd(minutes);
            rawBuf[13] = ToBcd(seconds);
            rawBuf[14] = ToBcd(frames);
            rawBuf[15] = 1; // Mode 1

            Buffer.BlockCopy(cookedBuf, 0, rawBuf, 16, 2048);

            // Zero EDC/ECC area
            Array.Clear(rawBuf, 2064, 288);

            // Compute EDC for the raw sector (optional but improves compatibility)
            uint edc = ComputeEdc(rawBuf, 0, 2064);
            rawBuf[2064] = (byte)(edc & 0xFF);
            rawBuf[2065] = (byte)((edc >> 8) & 0xFF);
            rawBuf[2066] = (byte)((edc >> 16) & 0xFF);
            rawBuf[2067] = (byte)((edc >> 24) & 0xFF);

            output.Write(rawBuf, 0, 2352);

            if (progress != null && sectorCount > 0 && s % 1000 == 0)
                progress.Report((int)(s * 100 / sectorCount));
        }
        progress?.Report(100);
    }

    /// <summary>
    /// Asynchronously creates a raw IMG from a cooked ISO.
    /// </summary>
    public static async Task WriteRawFromCookedAsync(string isoPath, string imgPath,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        await Task.Run(() => WriteRawFromCooked(isoPath, imgPath, progress), ct)
            .ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------
    //  Helpers
    // -----------------------------------------------------------------------

    private static void CopyWithProgress(Stream input, Stream output,
        long totalBytes, IProgress<int>? progress)
    {
        var buffer = new byte[65536];
        long bytesWritten = 0;

        while (bytesWritten < totalBytes)
        {
            int toRead = (int)Math.Min(buffer.Length, totalBytes - bytesWritten);
            int read = input.Read(buffer, 0, toRead);
            if (read <= 0) break;

            output.Write(buffer, 0, read);
            bytesWritten += read;

            if (progress != null && totalBytes > 0 && bytesWritten % (65536 * 16) < 65536)
                progress.Report((int)(bytesWritten * 100 / totalBytes));
        }
        progress?.Report(100);
    }

    /// <summary>Converts an integer to BCD encoding.</summary>
    private static byte ToBcd(int value)
    {
        if (value < 0 || value > 99) return 0;
        return (byte)(((value / 10) << 4) | (value % 10));
    }

    /// <summary>
    /// Computes EDC (Error Detection Code) for a sector using CRC-32/CD-ROM-EDC.
    /// Uses the polynomial 0xD8018001.
    /// </summary>
    private static uint ComputeEdc(byte[] data, int offset, int length)
    {
        uint edc = 0;
        for (int i = offset; i < offset + length; i++)
        {
            edc ^= data[i];
            for (int j = 0; j < 8; j++)
            {
                if ((edc & 1) != 0)
                    edc = (edc >> 1) ^ 0xD8018001;
                else
                    edc >>= 1;
            }
        }
        return edc;
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
