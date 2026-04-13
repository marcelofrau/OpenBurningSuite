// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using OpenBurningSuite.Helpers;

namespace OpenBurningSuite.Services;

/// <summary>
/// Service for Xbox 360 ISO stealth patching and topology data validation.
/// Handles DMI (Disc Manufacturing Information), PFI (Physical Format Information),
/// SS (Security Sectors), topology data, and video partition verification/patching
/// for Xbox 360 disc images (XGD2 and XGD3 formats).
/// </summary>
public class Xbox360StealthService
{
    // =====================================================================
    // Xbox 360 ISO structure constants
    // =====================================================================

    /// <summary>XGD2 game partition offset: 0xFD90000.</summary>
    public const long Xgd2GamePartitionOffset = 0x0FD90000L;

    /// <summary>XGD3 game partition offset: 0x2080000.</summary>
    public const long Xgd3GamePartitionOffset = 0x02080000L;

    /// <summary>Xbox 360 XDVDFS magic: "MICROSOFT*XBOX*MEDIA".</summary>
    private static readonly byte[] Xbox360Magic =
        Encoding.ASCII.GetBytes("MICROSOFT*XBOX*MEDIA");

    /// <summary>Size of a DVD sector.</summary>
    private const int SectorSize = 2048;

    // DMI is at physical sector 0 of the disc image (first 2048 bytes)
    // PFI is at the next sector
    // SS follows PFI

    /// <summary>DMI offset in Xbox 360 ISO images.</summary>
    public const long DmiOffset = 0;

    /// <summary>DMI size: 2048 bytes.</summary>
    public const int DmiSize = 2048;

    /// <summary>PFI offset in Xbox 360 ISO images.</summary>
    public const long PfiOffset = 2048;

    /// <summary>PFI size: 2048 bytes.</summary>
    public const int PfiSize = 2048;

    /// <summary>SS (Security Sector) offset: sector 2.</summary>
    public const long SsOffset = 4096;

    /// <summary>SS size: 2048 bytes per sector, typically 1-2 sectors.</summary>
    public const int SsSize = 2048;

    /// <summary>Video partition offset in XGD2 images.</summary>
    public const long Xgd2VideoPartitionOffset = 0;

    /// <summary>Video partition size for XGD2 (typically first ~0xFD90000 bytes).</summary>
    public const long Xgd2VideoPartitionSize = Xgd2GamePartitionOffset;

    /// <summary>
    /// Represents the type of Xbox 360 game disc format.
    /// </summary>
    public enum XgdType
    {
        Unknown,
        /// <summary>XGD2: Earlier format, game data at offset 0xFD90000.</summary>
        Xgd2,
        /// <summary>XGD3: Later format, game data at offset 0x2080000.</summary>
        Xgd3
    }

    /// <summary>
    /// Holds the results of an Xbox 360 ISO stealth check.
    /// </summary>
    public class StealthCheckResult
    {
        public bool IsValid { get; set; }
        public XgdType DiscType { get; set; }
        public bool HasValidDmi { get; set; }
        public bool HasValidPfi { get; set; }
        public bool HasValidSs { get; set; }
        public bool HasTopologyData { get; set; }
        public bool HasVideoPartition { get; set; }
        public uint VideoCrc { get; set; }
        public string GameTitle { get; set; } = string.Empty;
        public string MediaId { get; set; } = string.Empty;
        public List<string> Warnings { get; set; } = new();
        public List<string> Errors { get; set; } = new();
    }

    // =====================================================================
    // Detection
    // =====================================================================

    /// <summary>
    /// Detects the XGD type (XGD2 or XGD3) of an Xbox 360 ISO image
    /// by checking for the XDVDFS magic at both known game partition offsets.
    /// </summary>
    public static XgdType DetectXgdType(string imagePath)
    {
        if (!File.Exists(imagePath)) return XgdType.Unknown;

        try
        {
            using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);

            // Check XGD3 first (offset 0x2080000)
            if (CheckMagicAt(stream, Xgd3GamePartitionOffset))
                return XgdType.Xgd3;

            // Check XGD2 (offset 0xFD90000)
            if (CheckMagicAt(stream, Xgd2GamePartitionOffset))
                return XgdType.Xgd2;
        }
        catch { /* detection is best-effort */ }

        return XgdType.Unknown;
    }

    /// <summary>
    /// Performs a comprehensive stealth check on an Xbox 360 ISO image.
    /// Validates DMI, PFI, SS, topology data, and video partition integrity.
    /// </summary>
    public static StealthCheckResult CheckStealth(string imagePath)
    {
        var result = new StealthCheckResult();

        if (!File.Exists(imagePath))
        {
            result.Errors.Add("ISO file not found.");
            return result;
        }

        try
        {
            var fi = new FileInfo(imagePath);
            using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);

            // Detect XGD type
            result.DiscType = DetectXgdType(imagePath);
            if (result.DiscType == XgdType.Unknown)
            {
                result.Errors.Add("Not a recognized Xbox 360 ISO (no XDVDFS magic at XGD2 or XGD3 offset).");
                return result;
            }

            // Validate DMI
            result.HasValidDmi = ValidateDmi(stream);
            if (!result.HasValidDmi)
                result.Warnings.Add("DMI (Disc Manufacturing Information) is missing or invalid.");

            // Validate PFI
            result.HasValidPfi = ValidatePfi(stream);
            if (!result.HasValidPfi)
                result.Warnings.Add("PFI (Physical Format Information) is missing or invalid.");

            // Validate SS
            result.HasValidSs = ValidateSs(stream);
            if (!result.HasValidSs)
                result.Warnings.Add("SS (Security Sector) is missing or invalid.");

            // Check topology data (relevant for AP2.5 protected games)
            result.HasTopologyData = CheckTopologyData(stream, result.DiscType);
            if (!result.HasTopologyData)
                result.Warnings.Add("Topology data not found — may be required for AP2.5 protected games.");

            // Check video partition (XGD2 only — XGD3 has minimal video)
            if (result.DiscType == XgdType.Xgd2)
            {
                result.HasVideoPartition = CheckVideoPartition(stream);
                if (result.HasVideoPartition)
                    result.VideoCrc = ComputeVideoCrc(stream);
                else
                    result.Warnings.Add("Video partition is missing or empty.");
            }
            else
            {
                result.HasVideoPartition = true; // XGD3 has minimal/no video partition
            }

            // Read game title from XDVDFS
            long gameOffset = result.DiscType == XgdType.Xgd2
                ? Xgd2GamePartitionOffset : Xgd3GamePartitionOffset;
            result.GameTitle = ReadGameTitle(stream, gameOffset);

            // Read media ID from DMI
            result.MediaId = ReadMediaId(stream);

            // Overall validity
            result.IsValid = result.HasValidDmi && result.HasValidPfi &&
                             result.HasValidSs && result.HasVideoPartition;

            // Size validation
            if (result.DiscType == XgdType.Xgd3 && fi.Length < 8_547_991_552L)
                result.Warnings.Add("XGD3 image is smaller than expected (~8.7 GB). May be truncated.");
            else if (result.DiscType == XgdType.Xgd2 && fi.Length < 7_305_830_400L)
                result.Warnings.Add("XGD2 image is smaller than expected (~7.3 GB). May be truncated.");
        }
        catch (Exception ex) { result.Errors.Add($"Stealth check failed: {ex.Message}"); }

        return result;
    }

    // =====================================================================
    // Patching
    // =====================================================================

    /// <summary>
    /// Patches the DMI sector of an Xbox 360 ISO with the provided DMI data.
    /// The DMI must be exactly 2048 bytes.
    /// </summary>
    /// <returns>Null on success, or an error message.</returns>
    public static string? PatchDmi(string imagePath, byte[] dmiData)
    {
        if (!File.Exists(imagePath)) return "ISO file not found.";
        if (dmiData == null || dmiData.Length != DmiSize) return "DMI data must be exactly 2048 bytes.";

        try
        {
            using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            if (stream.Length < DmiOffset + DmiSize) return "ISO too small to contain DMI sector.";
            stream.Seek(DmiOffset, SeekOrigin.Begin);
            stream.Write(dmiData, 0, DmiSize);
            return null;
        }
        catch (Exception ex) { return $"DMI patch failed: {ex.Message}"; }
    }

    /// <summary>
    /// Patches the PFI sector of an Xbox 360 ISO with the provided PFI data.
    /// </summary>
    public static string? PatchPfi(string imagePath, byte[] pfiData)
    {
        if (!File.Exists(imagePath)) return "ISO file not found.";
        if (pfiData == null || pfiData.Length != PfiSize) return "PFI data must be exactly 2048 bytes.";

        try
        {
            using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            if (stream.Length < PfiOffset + PfiSize) return "ISO too small to contain PFI sector.";
            stream.Seek(PfiOffset, SeekOrigin.Begin);
            stream.Write(pfiData, 0, PfiSize);
            return null;
        }
        catch (Exception ex) { return $"PFI patch failed: {ex.Message}"; }
    }

    /// <summary>
    /// Patches the SS (Security Sector) of an Xbox 360 ISO with the provided SS data.
    /// </summary>
    public static string? PatchSs(string imagePath, byte[] ssData)
    {
        if (!File.Exists(imagePath)) return "ISO file not found.";
        if (ssData == null || ssData.Length < SsSize) return "SS data must be at least 2048 bytes.";

        try
        {
            using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            if (stream.Length < SsOffset + SsSize) return "ISO too small to contain SS sector.";
            stream.Seek(SsOffset, SeekOrigin.Begin);
            stream.Write(ssData, 0, Math.Min(ssData.Length, SsSize));
            return null;
        }
        catch (Exception ex) { return $"SS patch failed: {ex.Message}"; }
    }

    /// <summary>
    /// Patches stealth files (DMI, PFI, SS) from a directory containing
    /// DMI.bin, PFI.bin, and SS.bin files.
    /// </summary>
    /// <returns>Null on success, or an error message.</returns>
    public static string? PatchStealthFiles(string imagePath, string stealthDir)
    {
        if (!File.Exists(imagePath)) return "ISO file not found.";
        if (!Directory.Exists(stealthDir)) return "Stealth files directory not found.";

        var errors = new List<string>();

        string dmiPath = Path.Combine(stealthDir, "DMI.bin");
        string pfiPath = Path.Combine(stealthDir, "PFI.bin");
        string ssPath = Path.Combine(stealthDir, "SS.bin");

        if (File.Exists(dmiPath))
        {
            var result = PatchDmi(imagePath, File.ReadAllBytes(dmiPath));
            if (result != null) errors.Add(result);
        }
        else
        {
            errors.Add("DMI.bin not found in stealth directory.");
        }

        if (File.Exists(pfiPath))
        {
            var result = PatchPfi(imagePath, File.ReadAllBytes(pfiPath));
            if (result != null) errors.Add(result);
        }
        else
        {
            errors.Add("PFI.bin not found in stealth directory.");
        }

        if (File.Exists(ssPath))
        {
            var result = PatchSs(imagePath, File.ReadAllBytes(ssPath));
            if (result != null) errors.Add(result);
        }
        else
        {
            errors.Add("SS.bin not found in stealth directory.");
        }

        return errors.Count > 0 ? string.Join("; ", errors) : null;
    }

    /// <summary>
    /// Extracts the DMI, PFI, and SS sectors from an Xbox 360 ISO to separate files.
    /// </summary>
    public static string? ExtractStealthFiles(string imagePath, string outputDir)
    {
        if (!File.Exists(imagePath)) return "ISO file not found.";

        try
        {
            Directory.CreateDirectory(outputDir);
            using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);

            // Extract DMI
            if (stream.Length >= DmiOffset + DmiSize)
            {
                var dmi = new byte[DmiSize];
                stream.Seek(DmiOffset, SeekOrigin.Begin);
                stream.ReadExactly(dmi, 0, DmiSize);
                File.WriteAllBytes(Path.Combine(outputDir, "DMI.bin"), dmi);
            }

            // Extract PFI
            if (stream.Length >= PfiOffset + PfiSize)
            {
                var pfi = new byte[PfiSize];
                stream.Seek(PfiOffset, SeekOrigin.Begin);
                stream.ReadExactly(pfi, 0, PfiSize);
                File.WriteAllBytes(Path.Combine(outputDir, "PFI.bin"), pfi);
            }

            // Extract SS
            if (stream.Length >= SsOffset + SsSize)
            {
                var ss = new byte[SsSize];
                stream.Seek(SsOffset, SeekOrigin.Begin);
                stream.ReadExactly(ss, 0, SsSize);
                File.WriteAllBytes(Path.Combine(outputDir, "SS.bin"), ss);
            }

            return null;
        }
        catch (Exception ex) { return $"Stealth file extraction failed: {ex.Message}"; }
    }

    // =====================================================================
    // Private validation helpers
    // =====================================================================

    private static bool CheckMagicAt(FileStream stream, long offset)
    {
        if (stream.Length < offset + 20) return false;
        stream.Seek(offset, SeekOrigin.Begin);
        var buf = new byte[20];
        if (stream.Read(buf, 0, 20) != 20) return false;
        for (int i = 0; i < Xbox360Magic.Length; i++)
            if (buf[i] != Xbox360Magic[i]) return false;
        return true;
    }

    private static bool ValidateDmi(FileStream stream)
    {
        if (stream.Length < DmiOffset + DmiSize) return false;
        stream.Seek(DmiOffset, SeekOrigin.Begin);
        var dmi = new byte[DmiSize];
        stream.ReadExactly(dmi, 0, DmiSize);

        // DMI should not be all zeros
        return !IsAllZeros(dmi);
    }

    private static bool ValidatePfi(FileStream stream)
    {
        if (stream.Length < PfiOffset + PfiSize) return false;
        stream.Seek(PfiOffset, SeekOrigin.Begin);
        var pfi = new byte[PfiSize];
        stream.ReadExactly(pfi, 0, PfiSize);

        // PFI should not be all zeros
        return !IsAllZeros(pfi);
    }

    private static bool ValidateSs(FileStream stream)
    {
        if (stream.Length < SsOffset + SsSize) return false;
        stream.Seek(SsOffset, SeekOrigin.Begin);
        var ss = new byte[SsSize];
        stream.ReadExactly(ss, 0, SsSize);

        // SS should not be all zeros
        return !IsAllZeros(ss);
    }

    private static bool CheckTopologyData(FileStream stream, XgdType xgdType)
    {
        // Topology data is typically stored in the security sector area
        // For AP2.5 protection, it's part of the SS data at known offsets
        if (stream.Length < SsOffset + SsSize) return false;
        stream.Seek(SsOffset, SeekOrigin.Begin);
        var ss = new byte[SsSize];
        stream.ReadExactly(ss, 0, SsSize);

        // Check for non-zero topology markers
        // Topology data has specific challenge/response patterns
        // at offsets within the SS sector
        bool hasNonZeroData = false;
        for (int i = 0; i < Math.Min(256, ss.Length); i++)
        {
            if (ss[i] != 0) { hasNonZeroData = true; break; }
        }

        return hasNonZeroData;
    }

    private static bool CheckVideoPartition(FileStream stream)
    {
        // For XGD2, video partition is at the start of the disc before game data
        // Check for DVD-Video structure markers
        if (stream.Length < 0x20000) return false;

        // Look for "DVDVIDEO" or similar markers in the video partition area
        stream.Seek(0x8000, SeekOrigin.Begin); // Check at sector 16 for PVD
        var pvd = new byte[6];
        if (stream.Read(pvd, 0, 6) != 6) return false;

        // Check for ISO 9660 PVD signature (video partition uses ISO 9660)
        if (pvd[0] == 0x01 && pvd[1] == (byte)'C' && pvd[2] == (byte)'D' &&
            pvd[3] == (byte)'0' && pvd[4] == (byte)'0' && pvd[5] == (byte)'1')
            return true;

        // Also check first few bytes are non-zero (has some data)
        stream.Seek(0, SeekOrigin.Begin);
        var header = new byte[2048];
        stream.ReadExactly(header, 0, 2048);
        return !IsAllZeros(header);
    }

    /// <summary>CRC-32 for video partition verification (standard CRC-32).</summary>
    private static uint ComputeVideoCrc(FileStream stream)
    {
        // Compute CRC-32 over the first portion of the video partition
        long videoSize = Math.Min(Xgd2VideoPartitionSize, stream.Length);
        stream.Seek(0, SeekOrigin.Begin);

        uint crc = 0xFFFFFFFF;
        var buffer = new byte[65536];
        long remaining = videoSize;
        while (remaining > 0)
        {
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int read = stream.Read(buffer, 0, toRead);
            if (read <= 0) break;

            for (int i = 0; i < read; i++)
            {
                crc ^= buffer[i];
                for (int j = 0; j < 8; j++)
                    crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0);
            }
            remaining -= read;
        }

        return ~crc;
    }

    private static string ReadGameTitle(FileStream stream, long gamePartitionOffset)
    {
        // Read the XDVDFS root directory to find default.xex or default.xbe
        // and extract the title. For now, read the first part of the game partition.
        try
        {
            if (stream.Length < gamePartitionOffset + 0x24) return string.Empty;

            stream.Seek(gamePartitionOffset, SeekOrigin.Begin);
            var vd = new byte[SectorSize];
            stream.ReadExactly(vd, 0, SectorSize);

            // Verify magic
            for (int i = 0; i < Xbox360Magic.Length; i++)
                if (vd[i] != Xbox360Magic[i]) return string.Empty;

            // Get root directory sector and size
            uint rootSector = BitConverter.ToUInt32(vd, 0x14);
            uint rootSize = BitConverter.ToUInt32(vd, 0x18);

            long rootOffset = gamePartitionOffset + (long)rootSector * SectorSize;
            if (rootOffset + rootSize > stream.Length || rootSize == 0)
                return string.Empty;

            // Read root directory and look for default.xex
            var dirData = new byte[Math.Min(rootSize, 65536u)];
            stream.Seek(rootOffset, SeekOrigin.Begin);
            stream.ReadExactly(dirData, 0, dirData.Length);

            return ExtractFirstFileName(dirData);
        }
        catch { return string.Empty; }
    }

    private static string ExtractFirstFileName(byte[] dirData)
    {
        // Parse first directory entry for a file name
        if (dirData.Length < 14) return string.Empty;

        byte nameLen = dirData[13];
        if (nameLen == 0 || 14 + nameLen > dirData.Length) return string.Empty;

        return Encoding.ASCII.GetString(dirData, 14, nameLen);
    }

    private static string ReadMediaId(FileStream stream)
    {
        // Media ID is typically in the DMI at a known offset
        if (stream.Length < DmiOffset + DmiSize) return string.Empty;
        stream.Seek(DmiOffset + 8, SeekOrigin.Begin); // Offset 8 in DMI for media ID
        var buf = new byte[16];
        stream.ReadExactly(buf, 0, 16);

        // Build hex string from non-zero bytes
        var sb = new StringBuilder();
        foreach (byte b in buf)
        {
            if (b == 0) break;
            sb.Append(b.ToString("X2"));
        }
        return sb.ToString();
    }

    private static bool IsAllZeros(byte[] data)
    {
        for (int i = 0; i < data.Length; i++)
            if (data[i] != 0) return false;
        return true;
    }
}
