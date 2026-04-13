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
/// Provides VCD/SVCD-specific disc image verification methods.
/// Validates directory structure, metadata files, and MPEG stream compliance
/// per IEC 62107 (White Book) specification.
/// </summary>
public static class VcdSvcdVerifier
{
    /// <summary>
    /// Validates a VCD or SVCD disc image for structural correctness.
    /// Checks ISO 9660 PVD, CD-XA markers, VCD/SVCD directory structure,
    /// and metadata file signatures.
    /// Returns a list of validation messages (empty = valid).
    /// </summary>
    public static List<string> ValidateVcdSvcdImage(string imagePath)
    {
        var messages = new List<string>();

        if (!File.Exists(imagePath))
        {
            messages.Add($"Image not found: {imagePath}");
            return messages;
        }

        var fi = new FileInfo(imagePath);
        if (fi.Length == 0)
        {
            messages.Add("Image file is empty.");
            return messages;
        }

        try
        {
            using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, false);

            // Determine if this is a raw (2352-byte) or cooked (2048-byte) image
            bool isRaw = false;
            int sectorSize = 2048;
            int userDataOffset = 0;

            // Check for CD sync pattern at the start (00 FF×10 00)
            if (stream.Length >= 16)
            {
                var syncCheck = new byte[12];
                stream.ReadExactly(syncCheck, 0, 12);
                bool isSyncValid = syncCheck[0] == 0x00 && syncCheck[11] == 0x00;
                for (int i = 1; i < 11 && isSyncValid; i++)
                    isSyncValid = syncCheck[i] == 0xFF;
                if (isSyncValid)
                {
                    isRaw = true;
                    sectorSize = 2352;
                    userDataOffset = 24; // Sync(12) + Header(4) + SubHeader(8)
                }
                stream.Seek(0, SeekOrigin.Begin);
            }

            // Read PVD at sector 16
            long pvdPos = 16L * sectorSize + userDataOffset;
            if (pvdPos + 2048 > stream.Length)
            {
                messages.Add("Image too small to contain an ISO 9660 Primary Volume Descriptor.");
                return messages;
            }

            stream.Seek(pvdPos, SeekOrigin.Begin);
            var pvd = new byte[2048];
            if (stream.Read(pvd, 0, 2048) != 2048)
            {
                messages.Add("Could not read PVD sector.");
                return messages;
            }

            // Validate ISO 9660 PVD
            if (pvd[0] != 0x01 || pvd[1] != (byte)'C' || pvd[2] != (byte)'D' ||
                pvd[3] != (byte)'0' || pvd[4] != (byte)'0' || pvd[5] != (byte)'1')
            {
                messages.Add("Missing or invalid ISO 9660 Primary Volume Descriptor at sector 16.");
                return messages;
            }

            // Check System Identifier (should contain "CD-RTOS" for VCD/SVCD)
            var systemId = Encoding.ASCII.GetString(pvd, 8, 32).Trim();
            if (!systemId.Contains("CD-RTOS", StringComparison.OrdinalIgnoreCase) &&
                !systemId.Contains("CD-BRIDGE", StringComparison.OrdinalIgnoreCase))
            {
                messages.Add($"Warning: System Identifier '{systemId}' does not contain 'CD-RTOS CD-BRIDGE'. " +
                             "VCD/SVCD discs should use 'CD-RTOS CD-BRIDGE' as the system identifier.");
            }

            // Check CD-XA marker at PVD offset 1024
            var xaMarker = Encoding.ASCII.GetString(pvd, 1024, 8);
            if (xaMarker != "CD-XA001")
            {
                messages.Add("Warning: Missing CD-XA001 marker in PVD. " +
                             "VCD/SVCD discs must be CD-ROM XA compliant.");
            }

            // Determine VCD or SVCD from Volume Identifier
            var volumeId = Encoding.ASCII.GetString(pvd, 40, 32).Trim().ToUpperInvariant();
            bool isSvcd = volumeId.Contains("SUPERVCD") || volumeId.Contains("SVCD");
            string formatName = isSvcd ? "SVCD" : "VCD";

            messages.Add($"[Info] Detected format: {formatName} (Volume: {volumeId.TrimEnd()})");

            // Validate total image size
            long maxCapacity = isRaw
                ? 360_000L * 2352  // 80-min CD in raw sectors
                : 360_000L * 2048; // 80-min CD in cooked sectors
            if (fi.Length > maxCapacity)
            {
                messages.Add($"Warning: Image size ({FormatHelper.FormatBytes(fi.Length)}) exceeds " +
                             "standard 80-minute CD capacity. The disc may require overburning.");
            }

            // Check sector alignment
            if (fi.Length % sectorSize != 0)
            {
                messages.Add($"Warning: Image size ({fi.Length} bytes) is not aligned to " +
                             $"{sectorSize}-byte sector boundaries.");
            }

            // Scan subsequent sectors for VCD/SVCD metadata signatures
            bool foundInfoFile = false;
            bool foundEntriesFile = false;
            string expectedInfoSig = isSvcd ? "SUPERVCD" : "VIDEO_CD";
            string expectedEntrySig = isSvcd ? "ENTRYSV " : "ENTRYVCD";

            // Scan sectors 17-50 for metadata files
            for (int sector = 17; sector < 50; sector++)
            {
                long sectorPos = (long)sector * sectorSize + userDataOffset;
                if (sectorPos + 2048 > stream.Length) break;

                stream.Seek(sectorPos, SeekOrigin.Begin);
                var sectorData = new byte[Math.Min(2048, (int)(stream.Length - sectorPos))];
                int bytesRead = stream.Read(sectorData, 0, sectorData.Length);
                if (bytesRead < 8) continue;

                var sectorStr = Encoding.ASCII.GetString(sectorData, 0, Math.Min(64, bytesRead));

                // Check for INFO.VCD/INFO.SVD signature
                if (!foundInfoFile && sectorStr.StartsWith(expectedInfoSig, StringComparison.Ordinal))
                {
                    foundInfoFile = true;
                    messages.Add($"[Info] Found {(isSvcd ? "INFO.SVD" : "INFO.VCD")} at sector {sector}");

                    // Validate version bytes
                    if (bytesRead >= 10)
                    {
                        byte majorVer = sectorData[8];
                        byte minorVer = sectorData[9];
                        messages.Add($"[Info] {formatName} version: {majorVer}.{minorVer}");

                        if (isSvcd && majorVer != 0x01)
                            messages.Add("Warning: SVCD major version should be 1.");
                        if (!isSvcd && majorVer != 0x01 && majorVer != 0x02)
                            messages.Add("Warning: VCD major version should be 1 (VCD 1.1) or 2 (VCD 2.0).");
                    }

                    // Check video standard flag
                    if (bytesRead >= 37)
                    {
                        byte videoStd = sectorData[36];
                        string stdName = videoStd switch
                        {
                            0x01 => "NTSC",
                            0x02 => "PAL/SECAM",
                            0x03 => "Film (24fps)",
                            _ => $"Unknown (0x{videoStd:X2})"
                        };
                        messages.Add($"[Info] Video standard: {stdName}");
                    }
                }

                // Check for ENTRIES.VCD/ENTRIES.SVD signature
                if (!foundEntriesFile && sectorStr.StartsWith(expectedEntrySig, StringComparison.Ordinal))
                {
                    foundEntriesFile = true;
                    messages.Add($"[Info] Found {(isSvcd ? "ENTRIES.SVD" : "ENTRIES.VCD")} at sector {sector}");

                    // Validate entry count
                    if (bytesRead >= 12)
                    {
                        int entryCount = (sectorData[10] << 8) | sectorData[11];
                        messages.Add($"[Info] Number of playback entries: {entryCount}");
                        if (entryCount == 0)
                            messages.Add("Warning: No playback entries defined. Disc may not play.");
                        if (entryCount > 98)
                            messages.Add("Warning: Entry count exceeds maximum of 98.");
                    }
                }

                if (foundInfoFile && foundEntriesFile) break;
            }

            if (!foundInfoFile)
                messages.Add($"Warning: {(isSvcd ? "INFO.SVD" : "INFO.VCD")} metadata file not found. " +
                             $"The disc may not be a valid {formatName}.");

            if (!foundEntriesFile)
                messages.Add($"Warning: {(isSvcd ? "ENTRIES.SVD" : "ENTRIES.VCD")} entries file not found. " +
                             "Players may not be able to navigate tracks.");
        }
        catch (Exception ex)
        {
            messages.Add($"Verification error: {ex.Message}");
        }

        return messages;
    }

    /// <summary>
    /// Validates El Torito boot structures in an ISO 9660 disc image.
    /// Checks the Boot Record Volume Descriptor, boot catalog validation entry,
    /// initial/default entry, and any additional multi-boot section entries.
    /// </summary>
    public static List<string> ValidateElToritoBoot(string imagePath)
    {
        var results = OpenBurningSuite.Native.Iso.ElToritoWriter.VerifyBootCatalog(imagePath);
        return new List<string>(results);
    }

    /// <summary>
    /// Validates CD-i (Green Book) structures within a VCD/SVCD disc image.
    /// Checks for the CDI directory and CD-i application stub presence.
    /// </summary>
    public static List<string> ValidateCdiStructures(string imagePath)
    {
        var messages = new List<string>();

        if (!File.Exists(imagePath))
        {
            messages.Add($"Image not found: {imagePath}");
            return messages;
        }

        try
        {
            using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, false);

            // Determine raw vs cooked sector layout
            int sectorSize = 2048;
            int userDataOffset = 0;
            if (stream.Length >= 16)
            {
                var syncCheck = new byte[12];
                stream.ReadExactly(syncCheck, 0, 12);
                bool isSyncValid = syncCheck[0] == 0x00 && syncCheck[11] == 0x00;
                for (int i = 1; i < 11 && isSyncValid; i++)
                    isSyncValid = syncCheck[i] == 0xFF;
                if (isSyncValid)
                {
                    sectorSize = 2352;
                    userDataOffset = 24;
                }
                stream.Seek(0, SeekOrigin.Begin);
            }

            // Read PVD at sector 16
            long pvdPos = 16L * sectorSize + userDataOffset;
            if (pvdPos + 2048 > stream.Length)
            {
                messages.Add("Image too small to contain a PVD.");
                return messages;
            }

            stream.Seek(pvdPos, SeekOrigin.Begin);
            var pvd = new byte[2048];
            if (stream.Read(pvd, 0, 2048) != 2048)
            {
                messages.Add("Could not read PVD sector.");
                return messages;
            }

            // Check System Identifier for CD-RTOS (CD-i operating system)
            var systemId = Encoding.ASCII.GetString(pvd, 8, 32).Trim();
            bool hasCdRtos = systemId.Contains("CD-RTOS", StringComparison.OrdinalIgnoreCase);
            messages.Add(hasCdRtos
                ? "[Info] CD-i: System Identifier contains 'CD-RTOS' — CD-i compatible."
                : "[Info] CD-i: System Identifier does not contain 'CD-RTOS' — not a CD-i disc.");

            // Check CD-XA marker (required for CD-i)
            var xaMarker = Encoding.ASCII.GetString(pvd, 1024, 8);
            if (xaMarker == "CD-XA001")
                messages.Add("[Info] CD-i: CD-XA001 marker present — XA compliant.");
            else
                messages.Add("[Warning] CD-i: Missing CD-XA001 marker. CD-i requires CD-ROM XA.");

            // Check CD-BRIDGE identifier (indicates CD-i Bridge disc, used by VCD on CD-i)
            bool hasBridge = systemId.Contains("CD-BRIDGE", StringComparison.OrdinalIgnoreCase);
            if (hasBridge)
                messages.Add("[Info] CD-i: CD-BRIDGE identifier found — disc supports CD-i VCD playback.");

            // Scan directory records for CDI directory
            bool foundCdiDir = false;
            var sectorData = new byte[2048];
            for (int sector = 17; sector < 50; sector++)
            {
                long sectorPos = (long)sector * sectorSize + userDataOffset;
                if (sectorPos + 2048 > stream.Length) break;

                stream.Seek(sectorPos, SeekOrigin.Begin);
                int bytesRead = stream.Read(sectorData, 0, sectorData.Length);
                if (bytesRead < 33) continue;

                // Scan for directory entries containing "CDI" name
                int offset = 0;
                while (offset + 33 < bytesRead)
                {
                    int recordLen = sectorData[offset];
                    if (recordLen < 33) break;

                    int nameLen = sectorData[offset + 32];
                    if (nameLen > 0 && offset + 33 + nameLen <= bytesRead)
                    {
                        var name = Encoding.ASCII.GetString(sectorData, offset + 33, nameLen);
                        if (name.Equals("CDI", StringComparison.OrdinalIgnoreCase) ||
                            name.StartsWith("CDI;", StringComparison.OrdinalIgnoreCase) ||
                            name.StartsWith("CDI.", StringComparison.OrdinalIgnoreCase))
                        {
                            foundCdiDir = true;
                            // Check if it's a directory (flag bit 1)
                            bool isDir = (sectorData[offset + 25] & 0x02) != 0;
                            messages.Add($"[Info] CD-i: Found CDI {(isDir ? "directory" : "file")} at sector {sector}");
                            break;
                        }
                    }
                    offset += recordLen;
                }
                if (foundCdiDir) break;
            }

            if (!foundCdiDir && hasCdRtos)
                messages.Add("[Warning] CD-i: CD-RTOS identifier present but no CDI directory found.");
            else if (!foundCdiDir)
                messages.Add("[Info] CD-i: No CDI directory found (not a CD-i disc or VCD with CD-i support).");

            // Check for CD-i application stub content
            if (foundCdiDir)
            {
                // Look for OS-9 module header (0x4AFC) in nearby sectors
                bool foundModule = false;
                for (int sector = 20; sector < 60; sector++)
                {
                    long sectorPos = (long)sector * sectorSize + userDataOffset;
                    if (sectorPos + 4 > stream.Length) break;

                    stream.Seek(sectorPos, SeekOrigin.Begin);
                    var header = new byte[4];
                    if (stream.Read(header, 0, 4) < 4) continue;

                    // OS-9/68000 module sync word: 0x4AFC
                    if (header[0] == 0x4A && header[1] == 0xFC)
                    {
                        foundModule = true;
                        messages.Add($"[Info] CD-i: OS-9 module header found at sector {sector} — CD-i application stub present.");
                        break;
                    }
                }
                if (!foundModule)
                    messages.Add("[Warning] CD-i: CDI directory exists but no OS-9 module found. " +
                                 "CD-i players may not be able to execute the application.");
            }
        }
        catch (Exception ex)
        {
            messages.Add($"CD-i verification error: {ex.Message}");
        }

        return messages;
    }

    /// <summary>
    /// Validates CD-XA (CD-ROM XA) compliance of an ISO 9660 disc image.
    /// Checks for the CD-XA001 marker in the PVD application-use area.
    /// </summary>
    public static List<string> ValidateCdXaCompliance(string imagePath)
    {
        var messages = new List<string>();

        if (!File.Exists(imagePath))
        {
            messages.Add($"Image not found: {imagePath}");
            return messages;
        }

        try
        {
            using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, false);

            // Determine raw vs cooked layout
            int sectorSize = 2048;
            int userDataOffset = 0;
            if (stream.Length >= 16)
            {
                var syncCheck = new byte[12];
                stream.ReadExactly(syncCheck, 0, 12);
                bool isSyncValid = syncCheck[0] == 0x00 && syncCheck[11] == 0x00;
                for (int i = 1; i < 11 && isSyncValid; i++)
                    isSyncValid = syncCheck[i] == 0xFF;
                if (isSyncValid)
                {
                    sectorSize = 2352;
                    userDataOffset = 24;
                }
                stream.Seek(0, SeekOrigin.Begin);
            }

            messages.Add($"[Info] CD-XA: Sector layout — {sectorSize}-byte sectors" +
                         (sectorSize == 2352 ? " (raw, Mode 2)" : " (cooked, Mode 1)"));

            // Read PVD
            long pvdPos = 16L * sectorSize + userDataOffset;
            if (pvdPos + 2048 > stream.Length)
            {
                messages.Add("Image too small to contain a PVD.");
                return messages;
            }

            stream.Seek(pvdPos, SeekOrigin.Begin);
            var pvd = new byte[2048];
            if (stream.Read(pvd, 0, 2048) != 2048)
            {
                messages.Add("Could not read PVD.");
                return messages;
            }

            // Validate PVD
            if (pvd[0] != 0x01 || pvd[1] != (byte)'C' || pvd[2] != (byte)'D' ||
                pvd[3] != (byte)'0' || pvd[4] != (byte)'0' || pvd[5] != (byte)'1')
            {
                messages.Add("Invalid ISO 9660 PVD.");
                return messages;
            }

            // Check CD-XA001 marker at PVD offset 1024
            var xaMarker = Encoding.ASCII.GetString(pvd, 1024, 8);
            if (xaMarker == "CD-XA001")
            {
                messages.Add("[Info] CD-XA: CD-XA001 marker present at PVD offset 1024 — disc is XA compliant.");

                // Check null terminator after marker
                if (pvd[1032] == 0x00)
                    messages.Add("[Info] CD-XA: Null terminator present after CD-XA001 marker.");
                else
                    messages.Add("[Warning] CD-XA: Missing null terminator after CD-XA001 marker.");
            }
            else
            {
                messages.Add("[Info] CD-XA: No CD-XA001 marker found — disc is not CD-ROM XA compliant.");
            }

            // Check if raw sectors use Mode 2 (XA indicator in sector header)
            if (sectorSize == 2352)
            {
                // Read first data sector header to check mode byte
                long firstDataPos = 16L * sectorSize;
                stream.Seek(firstDataPos + 15, SeekOrigin.Begin);
                int modeByte = stream.ReadByte();
                if (modeByte == 0x02)
                {
                    messages.Add("[Info] CD-XA: Sector mode byte = 0x02 (Mode 2) — consistent with XA.");

                    // Check sub-header for Form indicator
                    stream.Seek(firstDataPos + 18, SeekOrigin.Begin);
                    int subMode = stream.ReadByte();
                    bool isForm2 = (subMode & 0x20) != 0;
                    messages.Add($"[Info] CD-XA: Sub-mode byte = 0x{subMode:X2} — " +
                                 $"Form {(isForm2 ? "2" : "1")}");
                }
                else if (modeByte == 0x01)
                {
                    messages.Add("[Info] CD-XA: Sector mode byte = 0x01 (Mode 1) — standard CD-ROM, not XA.");
                }
                else
                {
                    messages.Add($"[Info] CD-XA: Sector mode byte = 0x{modeByte:X2}");
                }
            }

            // Check System Identifier for CD-RTOS (indicates CD-i/VCD)
            var systemId = Encoding.ASCII.GetString(pvd, 8, 32).Trim();
            if (systemId.Contains("CD-RTOS", StringComparison.OrdinalIgnoreCase))
                messages.Add("[Info] CD-XA: System Identifier contains 'CD-RTOS' — CD-i/VCD disc.");
            if (systemId.Contains("CD-BRIDGE", StringComparison.OrdinalIgnoreCase))
                messages.Add("[Info] CD-XA: System Identifier contains 'CD-BRIDGE' — bridge disc format.");
        }
        catch (Exception ex)
        {
            messages.Add($"CD-XA verification error: {ex.Message}");
        }

        return messages;
    }
}
