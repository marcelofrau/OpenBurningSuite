// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OpenBurningSuite.Helpers;
using OpenBurningSuite.Models;
using OpenBurningSuite.Native.Iso;
using OpenBurningSuite.Native.Optical;
using OpenBurningSuite.Native.Toc;

namespace OpenBurningSuite.Services;

/// <summary>Reads optical discs to image files using native SCSI/MMC commands.</summary>
public class ReadService
{
    private readonly ReadEngine _engine = new();

    public ReadService() { }

    public async Task ReadDiscAsync(
        ReadJob job,
        IProgress<ReadProgress> progress,
        CancellationToken cancellationToken)
    {
        job.Status = ReadStatus.Running;
        job.StartedAt = DateTime.Now;

        try
        {
            switch (job.OutputFormat)
            {
                case "BIN/CUE":
                case "BIN/CUE/SBI":
                case "TOC/BIN":
                    // Always use ReadToBinCueAsync for BIN/CUE, BIN/CUE/SBI, and TOC/BIN formats.
                    // These formats require ALL sectors (data + audio) to be read as raw
                    // 2352-byte sectors. ReadAudioParanoiaAsync only reads audio tracks and
                    // outputs WAV — wrong for Mixed Mode CDs (e.g., PS1 discs with data + audio).
                    // When AudioParanoia is enabled, ReadToBinCueAsync applies multi-pass
                    // jitter correction to audio track sectors internally.
                    // For BIN/CUE/SBI: enable raw subchannel reading to capture LibCrypt data,
                    // then post-process the .sub file to extract subchannel-Q into SBI format.
                    if (job.OutputFormat == "BIN/CUE/SBI")
                    {
                        if (job.SubchannelMode == "None")
                            job.SubchannelMode = "RW_RAW";
                    }
                    await _engine.ReadToBinCueAsync(job, progress, cancellationToken);
                    // Post-process: convert raw .sub to .sbi for BIN/CUE/SBI format
                    if (job.OutputFormat == "BIN/CUE/SBI")
                    {
                        var subPath = Path.ChangeExtension(job.OutputPath, ".sub");
                        var sbiPath = Path.ChangeExtension(job.OutputPath, ".sbi");
                        if (File.Exists(subPath))
                        {
                            ConvertSubToSbi(subPath, sbiPath);
                            progress.Report(new ReadProgress
                            {
                                LogLine = $"[Info] SBI file generated: {Path.GetFileName(sbiPath)}"
                            });
                        }
                    }
                    break;
                case "MDF/MDS":
                    // Native MDF/MDS output: read disc to ISO first, then
                    // wrap the sector data in MDF/MDS format.
                    progress.Report(new ReadProgress
                    {
                        StatusMessage = "Reading disc for MDF/MDS output...",
                        LogLine = "[Info] MDF/MDS output — reading disc data."
                    });
                    await ReadToMdfMdsAsync(job, progress, cancellationToken);
                    break;
                case "IMG":
                    // IMG is a raw sector dump — identical to ISO for cooked sectors.
                    // For raw reads, the full 2352-byte sectors are preserved.
                    progress.Report(new ReadProgress
                    {
                        StatusMessage = "Reading disc for IMG output...",
                        LogLine = "[Info] IMG output — reading raw disc image."
                    });
                    await _engine.ReadToIsoAsync(job, progress, cancellationToken);
                    break;
                case "NRG":
                    // NRG (Nero) v2 format: read disc data, then wrap in NRG chunk structure.
                    progress.Report(new ReadProgress
                    {
                        StatusMessage = "Reading disc for NRG output...",
                        LogLine = "[Info] NRG output — reading disc data for Nero NRG v2 format."
                    });
                    await ReadToNrgAsync(job, progress, cancellationToken);
                    break;
                case "CDI":
                    // Read disc to raw BIN/CUE first, then convert to CDI format.
                    progress.Report(new ReadProgress
                    {
                        StatusMessage = "Reading disc for CDI output...",
                        LogLine = "[Info] CDI output — reading raw disc data."
                    });
                    await ReadToCdiAsync(job, progress, cancellationToken);
                    break;
                case "CCD/IMG/SUB":
                    // Read disc to CloneCD format (CCD/IMG/SUB)
                    progress.Report(new ReadProgress
                    {
                        StatusMessage = "Reading disc for CCD/IMG/SUB output...",
                        LogLine = "[Info] CCD output — reading raw disc with subchannel data."
                    });
                    // Ensure raw sector size for CCD format
                    if (job.SectorSize == 2048)
                        job.SectorSize = 2352;
                    // Default to subchannel reading for CCD if not already set
                    if (job.SubchannelMode == "None")
                        job.SubchannelMode = "RW";
                    await _engine.ReadToCcdAsync(job, progress, cancellationToken);
                    break;
                case "VCD":
                case "SVCD":
                case "XSVCD":
                    // VCD/SVCD/XSVCD discs use Mode 2 XA sectors — read as raw BIN/CUE
                    // to preserve the Mode 2 Form 1/Form 2 sector structure.
                    progress.Report(new ReadProgress
                    {
                        StatusMessage = $"Reading {job.OutputFormat} disc (Mode 2 XA raw sectors)...",
                        LogLine = $"[Info] {job.OutputFormat} output — reading raw Mode 2 XA sectors to BIN/CUE."
                    });
                    // Ensure sector size is set for raw reading
                    if (job.SectorSize == 2048)
                        job.SectorSize = 2352;
                    await _engine.ReadToBinCueAsync(job, progress, cancellationToken);
                    progress.Report(new ReadProgress
                    {
                        LogLine = $"[Info] {job.OutputFormat} disc read complete. " +
                                  "Output is a raw BIN/CUE image with Mode 2 XA sector structure preserved."
                    });
                    break;
                case "XISO (Xbox)":
                    // Read disc to ISO first, then wrap in XISO format.
                    // Xbox uses XDVDFS (Xbox DVD File System) which can be extracted from standard ISO reads.
                    progress.Report(new ReadProgress
                    {
                        StatusMessage = "Reading disc for Xbox ISO output...",
                        LogLine = "[Info] XISO (Xbox) output — reading disc data as ISO first."
                    });
                    await _engine.ReadToIsoAsync(job, progress, cancellationToken);
                    progress.Report(new ReadProgress
                    {
                        LogLine = "[Info] Xbox disc read complete. Output saved as ISO with XDVDFS filesystem."
                    });
                    break;
                case "ISO":
                default:
                    // Always use ReadToIsoAsync for ISO format. AudioParanoia (ReadAudioParanoiaAsync)
                    // produces WAV output, which is incompatible with ISO format. For discs with
                    // audio tracks (Mixed Mode CDs), AudioParanoia would skip data tracks entirely.
                    // Audio paranoia jitter correction is only applicable via BIN/CUE format reads.
                    await _engine.ReadToIsoAsync(job, progress, cancellationToken);
                    break;
            }

            // PS3 disc decryption: if the user requested decryption and an IRD file is provided,
            // decrypt the read ISO image after the raw read completes.
            if (job.DecryptPs3 && !string.IsNullOrWhiteSpace(job.IrdFilePath) &&
                File.Exists(job.OutputPath) && File.Exists(job.IrdFilePath))
            {
                progress.Report(new ReadProgress
                {
                    StatusMessage = "Decrypting PS3 disc image...",
                    LogLine = $"[Info] PS3 decryption — using IRD: {Path.GetFileName(job.IrdFilePath)}"
                });

                try
                {
                    var ird = Ps3DiscService.ParseIrdFile(job.IrdFilePath);
                    if (ird?.DiscKey != null)
                    {
                        var decryptedPath = Path.Combine(
                            Path.GetDirectoryName(job.OutputPath) ?? ".",
                            Path.GetFileNameWithoutExtension(job.OutputPath) + "_decrypted" + Path.GetExtension(job.OutputPath));

                        var ps3Progress = new Progress<Ps3DiscService.Ps3DecryptionProgress>(p =>
                        {
                            var pct = p.TotalSectors > 0 ? (int)(p.DecryptedSectors * 100 / p.TotalSectors) : 0;
                            progress.Report(new ReadProgress
                            {
                                PercentComplete = pct,
                                StatusMessage = $"Decrypting PS3: {pct}%",
                                LogLine = pct % 10 == 0 ? $"[Info] PS3 decryption: {pct}% ({p.DecryptedSectors}/{p.TotalSectors} sectors)" : null
                            });
                        });

                        var decryptResult = await Ps3DiscService.DecryptIsoAsync(
                            job.OutputPath, decryptedPath, ird.DiscKey, ps3Progress, cancellationToken);

                        if (string.IsNullOrEmpty(decryptResult))
                        {
                            progress.Report(new ReadProgress
                            {
                                LogLine = $"[Info] PS3 decryption complete — output: {decryptedPath}"
                            });
                        }
                        else
                        {
                            progress.Report(new ReadProgress
                            {
                                LogLine = $"[Warning] PS3 decryption failed: {decryptResult}"
                            });
                        }
                    }
                    else
                    {
                        progress.Report(new ReadProgress
                        {
                            LogLine = "[Warning] Could not derive PS3 disc key from IRD file"
                        });
                    }
                }
                catch (Exception ps3Ex)
                {
                    progress.Report(new ReadProgress
                    {
                        LogLine = $"[Warning] PS3 decryption error: {ps3Ex.Message}"
                    });
                }
            }

            job.Status = ReadStatus.Completed;
        }
        catch (OperationCanceledException)
        {
            job.Status = ReadStatus.Cancelled;
            throw;
        }
        catch
        {
            job.Status = ReadStatus.Failed;
            throw;
        }
        finally
        {
            job.FinishedAt = DateTime.Now;
        }
    }

    // -----------------------------------------------------------------------
    // CDI parsing support
    // -----------------------------------------------------------------------

    /// <summary>
    /// Parses a CDI image file and returns its structure (sessions, tracks, metadata).
    /// </summary>
    /// <param name="cdiFilePath">Path to the .cdi file.</param>
    /// <returns>Parsed CDI image structure.</returns>
    public static CdiImage ParseCdiImage(string cdiFilePath)
    {
        return CdiParser.Parse(cdiFilePath);
    }

    /// <summary>
    /// Asynchronously parses a CDI image file and returns its structure.
    /// </summary>
    /// <param name="cdiFilePath">Path to the .cdi file.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Parsed CDI image structure.</returns>
    public static async Task<CdiImage> ParseCdiImageAsync(string cdiFilePath,
        CancellationToken ct = default)
    {
        return await CdiParser.ParseAsync(cdiFilePath, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Extracts user data from a CDI image to an ISO file.
    /// Uses the largest data track for extraction.
    /// </summary>
    /// <param name="cdiFilePath">Path to the .cdi file.</param>
    /// <param name="outputIsoPath">Path for the output ISO file.</param>
    /// <param name="progress">Optional progress reporter (0-100).</param>
    public static void ExtractCdiToIso(string cdiFilePath, string outputIsoPath,
        IProgress<int>? progress = null)
    {
        var image = CdiParser.Parse(cdiFilePath);
        if (!image.IsValid)
            throw new InvalidOperationException($"CDI parse error: {image.ErrorMessage}");

        CdiParser.ExtractToIso(cdiFilePath, image, outputIsoPath, progress);
    }

    /// <summary>
    /// Asynchronously extracts user data from a CDI image to an ISO file.
    /// Uses the largest data track for extraction.
    /// </summary>
    /// <param name="cdiFilePath">Path to the .cdi file.</param>
    /// <param name="outputIsoPath">Path for the output ISO file.</param>
    /// <param name="progress">Optional progress reporter (0-100).</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task ExtractCdiToIsoAsync(string cdiFilePath, string outputIsoPath,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var image = await CdiParser.ParseAsync(cdiFilePath, ct).ConfigureAwait(false);
        if (!image.IsValid)
            throw new InvalidOperationException($"CDI parse error: {image.ErrorMessage}");

        await CdiParser.ExtractToIsoAsync(cdiFilePath, image, outputIsoPath, progress, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Converts a CDI image to BIN/CUE format.
    /// </summary>
    /// <param name="cdiFilePath">Path to the .cdi file.</param>
    /// <param name="outputBinPath">Path for the output BIN file.</param>
    /// <param name="outputCuePath">Path for the output CUE file.</param>
    public static void ConvertCdiToBinCue(string cdiFilePath, string outputBinPath,
        string outputCuePath)
    {
        var image = CdiParser.Parse(cdiFilePath);
        if (!image.IsValid)
            throw new InvalidOperationException($"CDI parse error: {image.ErrorMessage}");

        CdiParser.ConvertToBinCue(cdiFilePath, image, outputBinPath, outputCuePath);
    }

    /// <summary>
    /// Asynchronously converts a CDI image to BIN/CUE format.
    /// </summary>
    /// <param name="cdiFilePath">Path to the .cdi file.</param>
    /// <param name="outputBinPath">Path for the output BIN file.</param>
    /// <param name="outputCuePath">Path for the output CUE file.</param>
    /// <param name="progress">Optional progress reporter (0-100).</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task ConvertCdiToBinCueAsync(string cdiFilePath, string outputBinPath,
        string outputCuePath, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var image = await CdiParser.ParseAsync(cdiFilePath, ct).ConfigureAwait(false);
        if (!image.IsValid)
            throw new InvalidOperationException($"CDI parse error: {image.ErrorMessage}");

        await CdiParser.ConvertToBinCueAsync(cdiFilePath, image, outputBinPath, outputCuePath,
            progress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Extracts all data tracks from a CDI image to an output stream.
    /// </summary>
    /// <param name="cdiFilePath">Path to the .cdi file.</param>
    /// <param name="outputStream">Writable destination stream.</param>
    /// <param name="progress">Optional progress reporter (0-100).</param>
    public static void ExtractAllCdiDataTracks(string cdiFilePath, Stream outputStream,
        IProgress<int>? progress = null)
    {
        var image = CdiParser.Parse(cdiFilePath);
        if (!image.IsValid)
            throw new InvalidOperationException($"CDI parse error: {image.ErrorMessage}");

        CdiParser.ExtractAllDataTracks(cdiFilePath, image, outputStream, progress);
    }

    /// <summary>
    /// Asynchronously extracts all data tracks from a CDI image.
    /// </summary>
    /// <param name="cdiFilePath">Path to the .cdi file.</param>
    /// <param name="outputStream">Writable destination stream.</param>
    /// <param name="progress">Optional progress reporter (0-100).</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task ExtractAllCdiDataTracksAsync(string cdiFilePath, Stream outputStream,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var image = await CdiParser.ParseAsync(cdiFilePath, ct).ConfigureAwait(false);
        if (!image.IsValid)
            throw new InvalidOperationException($"CDI parse error: {image.ErrorMessage}");

        await CdiParser.ExtractAllDataTracksAsync(cdiFilePath, image, outputStream, progress, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Extracts a specific session from a CDI image to BIN/CUE format.
    /// </summary>
    /// <param name="cdiFilePath">Path to the .cdi file.</param>
    /// <param name="sessionNumber">Session number (1-based).</param>
    /// <param name="outputBinPath">Path for the output BIN file.</param>
    /// <param name="outputCuePath">Path for the output CUE file.</param>
    public static void ExtractCdiSession(string cdiFilePath, int sessionNumber,
        string outputBinPath, string outputCuePath)
    {
        var image = CdiParser.Parse(cdiFilePath);
        if (!image.IsValid)
            throw new InvalidOperationException($"CDI parse error: {image.ErrorMessage}");

        CdiParser.ExtractSession(cdiFilePath, image, sessionNumber, outputBinPath, outputCuePath);
    }

    /// <summary>
    /// Asynchronously extracts a specific session from a CDI image to BIN/CUE format.
    /// </summary>
    /// <param name="cdiFilePath">Path to the .cdi file.</param>
    /// <param name="sessionNumber">Session number (1-based).</param>
    /// <param name="outputBinPath">Path for the output BIN file.</param>
    /// <param name="outputCuePath">Path for the output CUE file.</param>
    /// <param name="progress">Optional progress reporter (0-100).</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task ExtractCdiSessionAsync(string cdiFilePath, int sessionNumber,
        string outputBinPath, string outputCuePath,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var image = await CdiParser.ParseAsync(cdiFilePath, ct).ConfigureAwait(false);
        if (!image.IsValid)
            throw new InvalidOperationException($"CDI parse error: {image.ErrorMessage}");

        await CdiParser.ExtractSessionAsync(cdiFilePath, image, sessionNumber,
            outputBinPath, outputCuePath, progress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads disc to ISO first, then wraps the result in MDF/MDS format.
    /// </summary>
    private async Task ReadToMdfMdsAsync(
        ReadJob job, IProgress<ReadProgress> progress, CancellationToken ct)
    {
        // Determine output paths: user specifies .mdf, we derive .mds
        string mdfPath = job.OutputPath;
        string mdsPath = Path.ChangeExtension(mdfPath, ".mds");

        // Read to a temporary ISO first, then convert
        string tempIsoPath = mdfPath + ".tmp.iso";
        try
        {
            var tempJob = new ReadJob
            {
                DevicePath = job.DevicePath,
                OutputPath = tempIsoPath,
                OutputFormat = "ISO",
                ReadSpeed = job.ReadSpeed,
                ErrorRecovery = job.ErrorRecovery,
                RetryCount = job.RetryCount,
                SectorSize = job.SectorSize,
                SubchannelMode = job.SubchannelMode,
                AudioParanoia = job.AudioParanoia,
                JitterCorrection = job.JitterCorrection,
                ReadBadSectors = job.ReadBadSectors
            };

            await _engine.ReadToIsoAsync(tempJob, progress, ct);

            progress.Report(new ReadProgress
            {
                StatusMessage = "Converting to MDF/MDS format...",
                LogLine = "[Info] Wrapping disc image in MDF/MDS format."
            });

            // Determine medium type from sector size and file size.
            // DVD images are typically much larger than CDs (>900 MB).
            long tempFileSize = new FileInfo(tempIsoPath).Length;
            MdsMediumType mediumType;
            if (tempFileSize > 900_000_000) // Larger than max CD capacity
                mediumType = MdsMediumType.DvdRom;
            else
                mediumType = MdsMediumType.CdRom;

            // Use tempJob.SectorSize because ReadToIsoAsync may have overridden it
            // (e.g., from 2352 to 2048 for DVD/BD media where raw sectors don't apply).
            MdfWriter.WriteFromIso(tempIsoPath, mdfPath, mdsPath, tempJob.SectorSize, mediumType);

            progress.Report(new ReadProgress
            {
                LogLine = $"[Info] MDF/MDS output complete: {mdfPath}"
            });
        }
        finally
        {
            // Clean up temporary ISO file
            try { if (File.Exists(tempIsoPath)) File.Delete(tempIsoPath); }
            catch { /* ignore cleanup errors */ }
        }
    }

    /// <summary>
    /// Reads disc to raw BIN first, then converts to CDI format using CdiWriter.
    /// </summary>
    private async Task ReadToCdiAsync(
        ReadJob job, IProgress<ReadProgress> progress, CancellationToken ct)
    {
        // CDI output: read disc to temp BIN, then wrap in CDI format
        string cdiPath = job.OutputPath;
        string tempBinPath = cdiPath + ".tmp.bin";

        try
        {
            // Ensure raw sector size for CDI
            var tempJob = new ReadJob
            {
                DevicePath = job.DevicePath,
                OutputPath = tempBinPath,
                OutputFormat = "BIN/CUE",
                ReadSpeed = job.ReadSpeed,
                ErrorRecovery = job.ErrorRecovery,
                RetryCount = job.RetryCount,
                SectorSize = 2352,
                SubchannelMode = job.SubchannelMode,
                AudioParanoia = job.AudioParanoia,
                JitterCorrection = job.JitterCorrection,
                ReadBadSectors = job.ReadBadSectors
            };

            await _engine.ReadToBinCueAsync(tempJob, progress, ct);

            progress.Report(new ReadProgress
            {
                StatusMessage = "Converting to CDI format...",
                LogLine = "[Info] Wrapping raw disc data in CDI format."
            });

            // We need the TOC data to build the CDI. Read it from the drive.
            using var drive = new OpticalDrive(job.DevicePath);
            var toc = drive.ReadToc();
            if (toc == null || toc.Entries.Count == 0)
                throw new InvalidOperationException("Could not read TOC for CDI conversion.");

            CdiWriter.WriteFromBin(tempBinPath, toc, cdiPath);

            progress.Report(new ReadProgress
            {
                LogLine = $"[Info] CDI output complete: {cdiPath}"
            });
        }
        finally
        {
            // Clean up temporary BIN and CUE files
            try { if (File.Exists(tempBinPath)) File.Delete(tempBinPath); }
            catch { /* ignore cleanup errors */ }
            var tempCuePath = Path.ChangeExtension(tempBinPath, ".cue");
            try { if (File.Exists(tempCuePath)) File.Delete(tempCuePath); }
            catch { /* ignore cleanup errors */ }
        }
    }

    /// <summary>
    /// Reads disc to ISO first, then wraps the result in Nero NRG v2 format.
    /// </summary>
    private async Task ReadToNrgAsync(
        ReadJob job, IProgress<ReadProgress> progress, CancellationToken ct)
    {
        string nrgPath = job.OutputPath;
        string tempIsoPath = nrgPath + ".tmp.iso";

        try
        {
            var tempJob = new ReadJob
            {
                DevicePath = job.DevicePath,
                OutputPath = tempIsoPath,
                OutputFormat = "ISO",
                ReadSpeed = job.ReadSpeed,
                ErrorRecovery = job.ErrorRecovery,
                RetryCount = job.RetryCount,
                SectorSize = job.SectorSize,
                SubchannelMode = job.SubchannelMode,
                AudioParanoia = job.AudioParanoia,
                JitterCorrection = job.JitterCorrection,
                ReadBadSectors = job.ReadBadSectors
            };

            await _engine.ReadToIsoAsync(tempJob, progress, ct);

            progress.Report(new ReadProgress
            {
                StatusMessage = "Converting to NRG format...",
                LogLine = "[Info] Wrapping disc image in Nero NRG v2 format."
            });

            // Use tempJob.SectorSize because ReadToIsoAsync may have overridden it
            // (e.g., from 2352 to 2048 for DVD/BD media where raw sectors don't apply).
            NrgWriter.WriteFromIso(tempIsoPath, nrgPath, tempJob.SectorSize);

            progress.Report(new ReadProgress
            {
                LogLine = $"[Info] NRG output complete: {nrgPath}"
            });
        }
        finally
        {
            // Clean up temporary ISO file
            try { if (File.Exists(tempIsoPath)) File.Delete(tempIsoPath); }
            catch { /* ignore cleanup errors */ }
        }
    }

    // -----------------------------------------------------------------------
    // SBI conversion support
    // -----------------------------------------------------------------------

    /// <summary>
    /// Converts a raw subchannel (.sub) file to SBI format (.sbi).
    /// SBI files contain only the modified subchannel-Q data, used for LibCrypt
    /// protection on PlayStation 1 discs.
    ///
    /// SBI file format (per redump.org/psxt001z specification):
    ///   Header: "SBI\0" (4 bytes)
    ///   Entries (repeated):
    ///     - MSF (3 bytes): minute, second, frame in BCD
    ///     - Type (1 byte): 1 = complete Q subchannel (10 bytes follow)
    ///     - Q data (10 bytes): subchannel-Q without the 2-byte CRC
    ///
    /// A sector's Q subchannel is "modified" (LibCrypt) if the CRC in the Q
    /// subchannel data does not match the computed CRC of the Q data bytes.
    /// </summary>
    private static void ConvertSubToSbi(string subPath, string sbiPath)
    {
        const int subSectorSize = 96; // 96 bytes per sector (P-W subchannel data interleaved)
        // SBI magic header
        byte[] sbiHeader = { (byte)'S', (byte)'B', (byte)'I', 0x00 };

        using var subStream = new FileStream(subPath, FileMode.Open, FileAccess.Read,
            FileShare.Read, 65536, false);
        using var sbiStream = new FileStream(sbiPath, FileMode.Create, FileAccess.Write,
            FileShare.None, 4096, false);

        sbiStream.Write(sbiHeader, 0, 4);

        var subBuffer = new byte[subSectorSize];
        long sectorIndex = 0;

        while (subStream.Read(subBuffer, 0, subSectorSize) == subSectorSize)
        {
            // De-interleave Q subchannel from the raw 96-byte subchannel data.
            // Raw subchannel data is P-W interleaved: 96 bytes = 12 bytes × 8 channels.
            // Each byte has bits for P(7), Q(6), R(5), S(4), T(3), U(2), V(1), W(0).
            // We need to extract the Q channel (bit 6 of each byte) into 12 bytes (96 bits).
            var qData = new byte[12];
            for (int i = 0; i < 96; i++)
            {
                int byteIndex = i / 8;
                int bitIndex = 7 - (i % 8);
                if ((subBuffer[i] & 0x40) != 0) // Q channel is bit 6
                    qData[byteIndex] |= (byte)(1 << bitIndex);
            }

            // Check if this sector has a modified Q subchannel (LibCrypt).
            // Compute CRC-16/CCITT over Q data bytes 0..9, compare with bytes 10..11.
            ushort computedCrc = ComputeSubchannelQCrc(qData, 0, 10);
            ushort storedCrc = (ushort)((qData[10] << 8) | qData[11]);

            if (computedCrc != storedCrc)
            {
                // This sector has a modified Q subchannel — write SBI entry.
                // Convert sector index to absolute MSF (starting from 00:02:00 = LBA 0)
                long absoluteFrame = sectorIndex + 150; // LBA 0 = MSF 00:02:00 = frame 150
                byte minute = (byte)(absoluteFrame / (60 * 75));
                byte second = (byte)((absoluteFrame / 75) % 60);
                byte frame = (byte)(absoluteFrame % 75);

                // Write MSF in BCD
                sbiStream.WriteByte(ToBcd(minute));
                sbiStream.WriteByte(ToBcd(second));
                sbiStream.WriteByte(ToBcd(frame));
                // Type 1 = complete Q subchannel (10 data bytes follow)
                sbiStream.WriteByte(0x01);
                // Write Q data bytes 0..9 (without CRC)
                sbiStream.Write(qData, 0, 10);
            }

            sectorIndex++;
        }
    }

    /// <summary>Converts a binary value to BCD (Binary-Coded Decimal).</summary>
    private static byte ToBcd(byte value) => (byte)(((value / 10) << 4) | (value % 10));

    /// <summary>
    /// Computes CRC-16/CCITT (polynomial 0x1021, init 0x0000) over subchannel Q data.
    /// This is the standard CRC used in CD subchannel-Q per ECMA-130 Section 22.3.4.
    /// The result is inverted (XOR 0xFFFF) per the subchannel specification, matching
    /// the stored CRC bytes in the Q-subchannel data.
    /// </summary>
    private static ushort ComputeSubchannelQCrc(byte[] data, int offset, int length)
    {
        ushort crc = 0x0000;
        for (int i = offset; i < offset + length; i++)
        {
            crc ^= (ushort)(data[i] << 8);
            for (int j = 0; j < 8; j++)
            {
                if ((crc & 0x8000) != 0)
                    crc = (ushort)((crc << 1) ^ 0x1021);
                else
                    crc <<= 1;
            }
        }
        return (ushort)(crc ^ 0xFFFF);
    }

    // -----------------------------------------------------------------------
    // CCD parsing support
    // -----------------------------------------------------------------------

    /// <summary>
    /// Parses a CCD image file and returns its structure (sessions, tracks, metadata).
    /// </summary>
    public static CcdImage ParseCcdImage(string ccdFilePath)
    {
        return CcdParser.Parse(ccdFilePath);
    }

    /// <summary>
    /// Asynchronously parses a CCD image file.
    /// </summary>
    public static async Task<CcdImage> ParseCcdImageAsync(string ccdFilePath,
        CancellationToken ct = default)
    {
        return await CcdParser.ParseAsync(ccdFilePath, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Extracts user data from a CCD image to an ISO file.
    /// </summary>
    public static void ExtractCcdToIso(string ccdFilePath, string outputIsoPath,
        IProgress<int>? progress = null)
    {
        var image = CcdParser.Parse(ccdFilePath);
        if (!image.IsValid)
            throw new InvalidOperationException($"CCD parse error: {image.ErrorMessage}");

        CcdParser.ExtractToIso(image.ImgFilePath, image, outputIsoPath, progress);
    }

    /// <summary>
    /// Asynchronously extracts user data from a CCD image to an ISO file.
    /// </summary>
    public static async Task ExtractCcdToIsoAsync(string ccdFilePath, string outputIsoPath,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var image = await CcdParser.ParseAsync(ccdFilePath, ct).ConfigureAwait(false);
        if (!image.IsValid)
            throw new InvalidOperationException($"CCD parse error: {image.ErrorMessage}");

        await CcdParser.ExtractToIsoAsync(image.ImgFilePath, image, outputIsoPath, progress, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Converts a CCD image to BIN/CUE format.
    /// </summary>
    public static void ConvertCcdToBinCue(string ccdFilePath, string outputBinPath,
        string outputCuePath)
    {
        var image = CcdParser.Parse(ccdFilePath);
        if (!image.IsValid)
            throw new InvalidOperationException($"CCD parse error: {image.ErrorMessage}");

        CcdParser.ConvertToBinCue(image.ImgFilePath, image, outputBinPath, outputCuePath);
    }

    /// <summary>
    /// Asynchronously converts a CCD image to BIN/CUE format.
    /// </summary>
    public static async Task ConvertCcdToBinCueAsync(string ccdFilePath, string outputBinPath,
        string outputCuePath, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var image = await CcdParser.ParseAsync(ccdFilePath, ct).ConfigureAwait(false);
        if (!image.IsValid)
            throw new InvalidOperationException($"CCD parse error: {image.ErrorMessage}");

        await CcdParser.ConvertToBinCueAsync(image.ImgFilePath, image, outputBinPath, outputCuePath,
            progress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Extracts all data tracks from a CCD image.
    /// </summary>
    public static void ExtractAllCcdDataTracks(string ccdFilePath, Stream outputStream,
        IProgress<int>? progress = null)
    {
        var image = CcdParser.Parse(ccdFilePath);
        if (!image.IsValid)
            throw new InvalidOperationException($"CCD parse error: {image.ErrorMessage}");

        CcdParser.ExtractAllDataTracks(image.ImgFilePath, image, outputStream, progress);
    }

    /// <summary>
    /// Asynchronously extracts all data tracks from a CCD image.
    /// </summary>
    public static async Task ExtractAllCcdDataTracksAsync(string ccdFilePath, Stream outputStream,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var image = await CcdParser.ParseAsync(ccdFilePath, ct).ConfigureAwait(false);
        if (!image.IsValid)
            throw new InvalidOperationException($"CCD parse error: {image.ErrorMessage}");

        await CcdParser.ExtractAllDataTracksAsync(image.ImgFilePath, image, outputStream, progress, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Validates a CCD image for integrity.
    /// </summary>
    public static List<string> ValidateCcdImage(string ccdFilePath)
    {
        var image = CcdParser.Parse(ccdFilePath);
        if (!image.IsValid)
            return new List<string> { $"Parse error: {image.ErrorMessage}" };

        return CcdParser.ValidateImage(image);
    }

    /// <summary>
    /// Asynchronously validates a CCD image.
    /// </summary>
    public static async Task<List<string>> ValidateCcdImageAsync(string ccdFilePath,
        CancellationToken ct = default)
    {
        var image = await CcdParser.ParseAsync(ccdFilePath, ct).ConfigureAwait(false);
        if (!image.IsValid)
            return new List<string> { $"Parse error: {image.ErrorMessage}" };

        return await CcdParser.ValidateImageAsync(image, ct).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------
    // MDF/MDS parsing support
    // -----------------------------------------------------------------------

    /// <summary>
    /// Parses an MDS descriptor file and returns the disc image structure.
    /// </summary>
    /// <param name="mdsFilePath">Path to the .mds file.</param>
    /// <returns>Parsed MDF image structure.</returns>
    public static MdfImage ParseMdfImage(string mdsFilePath)
    {
        return MdfParser.Parse(mdsFilePath);
    }

    /// <summary>
    /// Asynchronously parses an MDS descriptor file.
    /// </summary>
    /// <param name="mdsFilePath">Path to the .mds file.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Parsed MDF image structure.</returns>
    public static async Task<MdfImage> ParseMdfImageAsync(string mdsFilePath,
        CancellationToken ct = default)
    {
        return await MdfParser.ParseAsync(mdsFilePath, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Extracts user data from an MDF image to an ISO file.
    /// Uses the largest data track for extraction.
    /// </summary>
    /// <param name="mdsFilePath">Path to the .mds file.</param>
    /// <param name="outputIsoPath">Path for the output ISO file.</param>
    /// <param name="progress">Optional progress reporter (0-100).</param>
    public static void ExtractMdfToIso(string mdsFilePath, string outputIsoPath,
        IProgress<int>? progress = null)
    {
        var image = MdfParser.Parse(mdsFilePath);
        if (!image.IsValid)
            throw new InvalidOperationException($"MDS parse error: {image.ErrorMessage}");

        MdfParser.ExtractToIso(image.MdfFilePath, image, outputIsoPath, progress);
    }

    /// <summary>
    /// Asynchronously extracts user data from an MDF image to an ISO file.
    /// </summary>
    /// <param name="mdsFilePath">Path to the .mds file.</param>
    /// <param name="outputIsoPath">Path for the output ISO file.</param>
    /// <param name="progress">Optional progress reporter (0-100).</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task ExtractMdfToIsoAsync(string mdsFilePath, string outputIsoPath,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var image = await MdfParser.ParseAsync(mdsFilePath, ct).ConfigureAwait(false);
        if (!image.IsValid)
            throw new InvalidOperationException($"MDS parse error: {image.ErrorMessage}");

        await MdfParser.ExtractToIsoAsync(image.MdfFilePath, image, outputIsoPath, progress, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Extracts an MDF image to a raw IMG file.
    /// </summary>
    /// <param name="mdsFilePath">Path to the .mds file.</param>
    /// <param name="outputImgPath">Path for the output IMG file.</param>
    /// <param name="progress">Optional progress reporter (0-100).</param>
    public static void ExtractMdfToImg(string mdsFilePath, string outputImgPath,
        IProgress<int>? progress = null)
    {
        var image = MdfParser.Parse(mdsFilePath);
        if (!image.IsValid)
            throw new InvalidOperationException($"MDS parse error: {image.ErrorMessage}");

        MdfParser.ExtractToImg(image.MdfFilePath, image, outputImgPath, progress);
    }

    /// <summary>
    /// Asynchronously extracts an MDF image to a raw IMG file.
    /// </summary>
    /// <param name="mdsFilePath">Path to the .mds file.</param>
    /// <param name="outputImgPath">Path for the output IMG file.</param>
    /// <param name="progress">Optional progress reporter (0-100).</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task ExtractMdfToImgAsync(string mdsFilePath, string outputImgPath,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var image = await MdfParser.ParseAsync(mdsFilePath, ct).ConfigureAwait(false);
        if (!image.IsValid)
            throw new InvalidOperationException($"MDS parse error: {image.ErrorMessage}");

        await MdfParser.ExtractToImgAsync(image.MdfFilePath, image, outputImgPath, progress, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Converts an MDF/MDS image to BIN/CUE format.
    /// </summary>
    /// <param name="mdsFilePath">Path to the .mds file.</param>
    /// <param name="outputBinPath">Path for the output BIN file.</param>
    /// <param name="outputCuePath">Path for the output CUE file.</param>
    /// <param name="progress">Optional progress reporter (0-100).</param>
    public static void ConvertMdfToBinCue(string mdsFilePath, string outputBinPath,
        string outputCuePath, IProgress<int>? progress = null)
    {
        var image = MdfParser.Parse(mdsFilePath);
        if (!image.IsValid)
            throw new InvalidOperationException($"MDS parse error: {image.ErrorMessage}");

        MdfParser.ConvertToBinCue(image.MdfFilePath, image, outputBinPath, outputCuePath, progress);
    }

    /// <summary>
    /// Asynchronously converts an MDF/MDS image to BIN/CUE format.
    /// </summary>
    /// <param name="mdsFilePath">Path to the .mds file.</param>
    /// <param name="outputBinPath">Path for the output BIN file.</param>
    /// <param name="outputCuePath">Path for the output CUE file.</param>
    /// <param name="progress">Optional progress reporter (0-100).</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task ConvertMdfToBinCueAsync(string mdsFilePath, string outputBinPath,
        string outputCuePath, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var image = await MdfParser.ParseAsync(mdsFilePath, ct).ConfigureAwait(false);
        if (!image.IsValid)
            throw new InvalidOperationException($"MDS parse error: {image.ErrorMessage}");

        await MdfParser.ConvertToBinCueAsync(image.MdfFilePath, image, outputBinPath, outputCuePath,
            progress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Converts an MDF/MDS image to CDI format (produces BIN/CUE pair).
    /// </summary>
    /// <param name="mdsFilePath">Path to the .mds file.</param>
    /// <param name="outputCdiPath">Path for the output CDI file.</param>
    /// <param name="progress">Optional progress reporter (0-100).</param>
    public static void ConvertMdfToCdi(string mdsFilePath, string outputCdiPath,
        IProgress<int>? progress = null)
    {
        var image = MdfParser.Parse(mdsFilePath);
        if (!image.IsValid)
            throw new InvalidOperationException($"MDS parse error: {image.ErrorMessage}");

        MdfParser.ConvertToCdi(image.MdfFilePath, image, outputCdiPath, progress);
    }

    /// <summary>
    /// Asynchronously converts an MDF/MDS image to CDI format.
    /// </summary>
    /// <param name="mdsFilePath">Path to the .mds file.</param>
    /// <param name="outputCdiPath">Path for the output CDI file.</param>
    /// <param name="progress">Optional progress reporter (0-100).</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task ConvertMdfToCdiAsync(string mdsFilePath, string outputCdiPath,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var image = await MdfParser.ParseAsync(mdsFilePath, ct).ConfigureAwait(false);
        if (!image.IsValid)
            throw new InvalidOperationException($"MDS parse error: {image.ErrorMessage}");

        await MdfParser.ConvertToCdiAsync(image.MdfFilePath, image, outputCdiPath, progress, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Extracts all data tracks from an MDF image to an output stream.
    /// </summary>
    /// <param name="mdsFilePath">Path to the .mds file.</param>
    /// <param name="outputStream">Writable destination stream.</param>
    /// <param name="progress">Optional progress reporter (0-100).</param>
    public static void ExtractAllMdfDataTracks(string mdsFilePath, Stream outputStream,
        IProgress<int>? progress = null)
    {
        var image = MdfParser.Parse(mdsFilePath);
        if (!image.IsValid)
            throw new InvalidOperationException($"MDS parse error: {image.ErrorMessage}");

        MdfParser.ExtractAllDataTracks(image.MdfFilePath, image, outputStream, progress);
    }

    /// <summary>
    /// Asynchronously extracts all data tracks from an MDF image.
    /// </summary>
    /// <param name="mdsFilePath">Path to the .mds file.</param>
    /// <param name="outputStream">Writable destination stream.</param>
    /// <param name="progress">Optional progress reporter (0-100).</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task ExtractAllMdfDataTracksAsync(string mdsFilePath, Stream outputStream,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var image = await MdfParser.ParseAsync(mdsFilePath, ct).ConfigureAwait(false);
        if (!image.IsValid)
            throw new InvalidOperationException($"MDS parse error: {image.ErrorMessage}");

        await MdfParser.ExtractAllDataTracksAsync(image.MdfFilePath, image, outputStream, progress, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Validates an MDF/MDS image for integrity.
    /// </summary>
    /// <param name="mdsFilePath">Path to the .mds file.</param>
    /// <returns>List of validation issues. Empty means no problems found.</returns>
    public static List<string> ValidateMdfImage(string mdsFilePath)
    {
        var image = MdfParser.Parse(mdsFilePath);
        if (!image.IsValid)
            return new List<string> { $"Parse error: {image.ErrorMessage}" };

        return MdfParser.ValidateImage(image);
    }

    /// <summary>
    /// Asynchronously validates an MDF/MDS image.
    /// </summary>
    /// <param name="mdsFilePath">Path to the .mds file.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of validation issues.</returns>
    public static async Task<List<string>> ValidateMdfImageAsync(string mdsFilePath,
        CancellationToken ct = default)
    {
        var image = await MdfParser.ParseAsync(mdsFilePath, ct).ConfigureAwait(false);
        if (!image.IsValid)
            return new List<string> { $"Parse error: {image.ErrorMessage}" };

        return await MdfParser.ValidateImageAsync(image, ct).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------
    // NRG parsing and conversion support
    // -----------------------------------------------------------------------

    /// <summary>
    /// Parses an NRG image file and returns its structure (sessions, tracks, metadata).
    /// </summary>
    /// <param name="nrgFilePath">Path to the .nrg file.</param>
    /// <returns>Parsed NRG image structure.</returns>
    public static NrgImage ParseNrgImage(string nrgFilePath)
    {
        return NrgParser.Parse(nrgFilePath);
    }

    /// <summary>
    /// Asynchronously parses an NRG image file and returns its structure.
    /// </summary>
    public static async Task<NrgImage> ParseNrgImageAsync(string nrgFilePath,
        CancellationToken ct = default)
    {
        return await NrgParser.ParseAsync(nrgFilePath, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Extracts user data from an NRG image to an ISO file.
    /// Uses the largest data track for extraction.
    /// </summary>
    public static void ExtractNrgToIso(string nrgFilePath, string outputIsoPath,
        IProgress<int>? progress = null)
    {
        var image = NrgParser.Parse(nrgFilePath);
        if (!image.IsValid)
            throw new InvalidOperationException($"NRG parse error: {image.ErrorMessage}");

        NrgParser.ExtractToIso(nrgFilePath, image, outputIsoPath, progress);
    }

    /// <summary>
    /// Asynchronously extracts user data from an NRG image to an ISO file.
    /// </summary>
    public static async Task ExtractNrgToIsoAsync(string nrgFilePath, string outputIsoPath,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var image = await NrgParser.ParseAsync(nrgFilePath, ct).ConfigureAwait(false);
        if (!image.IsValid)
            throw new InvalidOperationException($"NRG parse error: {image.ErrorMessage}");

        await NrgParser.ExtractToIsoAsync(nrgFilePath, image, outputIsoPath, progress, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Converts an NRG image to BIN/CUE format.
    /// </summary>
    public static void ConvertNrgToBinCue(string nrgFilePath, string outputBinPath,
        string outputCuePath, IProgress<int>? progress = null)
    {
        var image = NrgParser.Parse(nrgFilePath);
        if (!image.IsValid)
            throw new InvalidOperationException($"NRG parse error: {image.ErrorMessage}");

        NrgParser.ConvertToBinCue(nrgFilePath, image, outputBinPath, outputCuePath, progress);
    }

    /// <summary>
    /// Asynchronously converts an NRG image to BIN/CUE format.
    /// </summary>
    public static async Task ConvertNrgToBinCueAsync(string nrgFilePath, string outputBinPath,
        string outputCuePath, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var image = await NrgParser.ParseAsync(nrgFilePath, ct).ConfigureAwait(false);
        if (!image.IsValid)
            throw new InvalidOperationException($"NRG parse error: {image.ErrorMessage}");

        await NrgParser.ConvertToBinCueAsync(nrgFilePath, image, outputBinPath, outputCuePath,
            progress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Extracts all data tracks from an NRG image to an output stream.
    /// </summary>
    public static void ExtractAllNrgDataTracks(string nrgFilePath, Stream outputStream,
        IProgress<int>? progress = null)
    {
        var image = NrgParser.Parse(nrgFilePath);
        if (!image.IsValid)
            throw new InvalidOperationException($"NRG parse error: {image.ErrorMessage}");

        NrgParser.ExtractAllDataTracks(nrgFilePath, image, outputStream, progress);
    }

    /// <summary>
    /// Asynchronously extracts all data tracks from an NRG image.
    /// </summary>
    public static async Task ExtractAllNrgDataTracksAsync(string nrgFilePath, Stream outputStream,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var image = await NrgParser.ParseAsync(nrgFilePath, ct).ConfigureAwait(false);
        if (!image.IsValid)
            throw new InvalidOperationException($"NRG parse error: {image.ErrorMessage}");

        await NrgParser.ExtractAllDataTracksAsync(nrgFilePath, image, outputStream, progress, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Validates an NRG image for integrity.
    /// </summary>
    public static List<string> ValidateNrgImage(string nrgFilePath)
    {
        var image = NrgParser.Parse(nrgFilePath);
        if (!image.IsValid)
            return new List<string> { $"Parse error: {image.ErrorMessage}" };

        return NrgParser.ValidateImage(image);
    }

    /// <summary>
    /// Asynchronously validates an NRG image.
    /// </summary>
    public static async Task<List<string>> ValidateNrgImageAsync(string nrgFilePath,
        CancellationToken ct = default)
    {
        var image = await NrgParser.ParseAsync(nrgFilePath, ct).ConfigureAwait(false);
        if (!image.IsValid)
            return new List<string> { $"Parse error: {image.ErrorMessage}" };

        return await NrgParser.ValidateImageAsync(image, ct).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------
    // IMG parsing and conversion support
    // -----------------------------------------------------------------------

    /// <summary>
    /// Parses a raw IMG image file and returns its structure.
    /// </summary>
    public static ImgImage ParseImgImage(string imgFilePath)
    {
        return ImgParser.Parse(imgFilePath);
    }

    /// <summary>
    /// Asynchronously parses a raw IMG image file.
    /// </summary>
    public static async Task<ImgImage> ParseImgImageAsync(string imgFilePath,
        CancellationToken ct = default)
    {
        return await ImgParser.ParseAsync(imgFilePath, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Extracts user data from an IMG image to an ISO file.
    /// </summary>
    public static void ExtractImgToIso(string imgFilePath, string outputIsoPath,
        IProgress<int>? progress = null)
    {
        var image = ImgParser.Parse(imgFilePath);
        if (!image.IsValid)
            throw new InvalidOperationException($"IMG parse error: {image.ErrorMessage}");

        ImgParser.ExtractToIso(imgFilePath, image, outputIsoPath, progress);
    }

    /// <summary>
    /// Asynchronously extracts user data from an IMG image to an ISO file.
    /// </summary>
    public static async Task ExtractImgToIsoAsync(string imgFilePath, string outputIsoPath,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var image = await ImgParser.ParseAsync(imgFilePath, ct).ConfigureAwait(false);
        if (!image.IsValid)
            throw new InvalidOperationException($"IMG parse error: {image.ErrorMessage}");

        await ImgParser.ExtractToIsoAsync(imgFilePath, image, outputIsoPath, progress, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Converts an IMG image to BIN/CUE format.
    /// </summary>
    public static void ConvertImgToBinCue(string imgFilePath, string outputBinPath,
        string outputCuePath, IProgress<int>? progress = null)
    {
        var image = ImgParser.Parse(imgFilePath);
        if (!image.IsValid)
            throw new InvalidOperationException($"IMG parse error: {image.ErrorMessage}");

        ImgParser.ConvertToBinCue(imgFilePath, image, outputBinPath, outputCuePath, progress);
    }

    /// <summary>
    /// Asynchronously converts an IMG image to BIN/CUE format.
    /// </summary>
    public static async Task ConvertImgToBinCueAsync(string imgFilePath, string outputBinPath,
        string outputCuePath, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var image = await ImgParser.ParseAsync(imgFilePath, ct).ConfigureAwait(false);
        if (!image.IsValid)
            throw new InvalidOperationException($"IMG parse error: {image.ErrorMessage}");

        await ImgParser.ConvertToBinCueAsync(imgFilePath, image, outputBinPath, outputCuePath,
            progress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Validates an IMG image for integrity.
    /// </summary>
    public static List<string> ValidateImgImage(string imgFilePath)
    {
        var image = ImgParser.Parse(imgFilePath);
        if (!image.IsValid)
            return new List<string> { $"Parse error: {image.ErrorMessage}" };

        return ImgParser.ValidateImage(image);
    }

    /// <summary>
    /// Asynchronously validates an IMG image.
    /// </summary>
    public static async Task<List<string>> ValidateImgImageAsync(string imgFilePath,
        CancellationToken ct = default)
    {
        var image = await ImgParser.ParseAsync(imgFilePath, ct).ConfigureAwait(false);
        if (!image.IsValid)
            return new List<string> { $"Parse error: {image.ErrorMessage}" };

        return await ImgParser.ValidateImageAsync(image, ct).ConfigureAwait(false);
    }
}
