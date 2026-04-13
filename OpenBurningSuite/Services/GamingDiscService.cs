// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenBurningSuite.Helpers;
using OpenBurningSuite.Models;
using OpenBurningSuite.Native.Iso;

namespace OpenBurningSuite.Services;

/// <summary>
/// Service for gaming disc operations: applying gaming presets to read/burn jobs,
/// disc image patching, region detection, and format validation.
/// </summary>
public class GamingDiscService
{
    /// <summary>Applies a gaming preset to a ReadJob for accurate disc reading.</summary>
    public static void ApplyReadPreset(ReadJob job, string presetName)
    {
        if (!FormatHelper.GamingPresets.TryGetValue(presetName, out var preset))
            return;

        job.GamingPreset = presetName;
        job.SectorSize = preset.SectorSize;
        job.SubchannelMode = preset.SubchannelMode;
        job.ReadSpeed = preset.ReadSpeed;
        job.ErrorRecovery = preset.ErrorRecovery;
        job.RetryCount = preset.Retries;
        job.AudioParanoia = preset.AudioParanoia;
        job.ReadBadSectors = preset.ErrorRecovery == "Yes"; // Continue past bad sectors for gaming discs

        // Set appropriate output format based on system requirements
        if (preset.SectorSize == 2352)
        {
            job.OutputFormat = "BIN/CUE";
            job.JitterCorrection = true;
        }
    }

    /// <summary>Applies a gaming preset to a BurnJob for accurate disc burning.
    /// Only sets WriteSpeed, WriteMode and CloseDisc from the preset.
    /// Other burn options (EjectAfterBurn, BufferUnderrunProtection, etc.)
    /// are left unchanged so the caller can set them from user preferences.</summary>
    public static void ApplyBurnPreset(BurnJob job, string presetName)
    {
        if (!FormatHelper.GamingPresets.TryGetValue(presetName, out var preset))
            return;

        job.WriteSpeed = preset.WriteSpeed;
        job.WriteMode = preset.WriteMode;
        job.CloseDisc = preset.CloseDisc;

        // Auto-detect CUE file for BIN/CUE image burning in SAO/DAO mode.
        // Gaming disc presets that use raw sectors typically come as BIN/CUE pairs.
        if (string.IsNullOrWhiteSpace(job.CuePath) && !string.IsNullOrWhiteSpace(job.ImagePath))
        {
            var cuePath = Path.ChangeExtension(job.ImagePath, ".cue");
            if (File.Exists(cuePath))
                job.CuePath = cuePath;
        }
    }

    /// <summary>
    /// Maps a raw detection result to the canonical preset name in
    /// <see cref="FormatHelper.GamingPresets"/>. Some detection results (e.g.
    /// "Sega Dreamcast (CDI)") are more specific than the preset key.
    /// </summary>
    public static string NormalizeToPresetName(string detectedSystem)
    {
        if (string.IsNullOrEmpty(detectedSystem))
            return detectedSystem ?? string.Empty;

        // CDI-format Dreamcast images map to the standard Dreamcast preset
        if (detectedSystem.StartsWith("Sega Dreamcast", StringComparison.OrdinalIgnoreCase))
            return "Sega Dreamcast";

        return detectedSystem;
    }

    /// <summary>
    /// Detects the gaming system from an image file by examining its structure.
    /// Returns the preset name if detected, null otherwise.
    /// </summary>
    public static string? DetectGamingSystem(string imagePath)
    {
        if (!File.Exists(imagePath)) return null;

        try
        {
            // CUE files are text metadata — resolve to the actual BIN data file for detection.
            if (Path.GetExtension(imagePath).Equals(".cue", StringComparison.OrdinalIgnoreCase))
            {
                var binFiles = Native.Optical.BurnEngine.GetCueBinFiles(imagePath);
                if (binFiles.Count > 0 && File.Exists(binFiles[0]))
                    return DetectGamingSystem(binFiles[0]);

                // Fallback: try companion BIN file with same base name
                var companionBin = Path.ChangeExtension(imagePath, ".bin");
                if (File.Exists(companionBin))
                    return DetectGamingSystem(companionBin);

                return null;
            }

            using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, false);

            if (stream.Length == 0) return null;

            // Check ISO PVD at sector 16 (offset 32768) for system identifiers
            if (stream.Length > 32768 + 2048)
            {
                stream.Seek(32768, SeekOrigin.Begin);
                var pvd = new byte[2048];
                if (stream.Read(pvd, 0, 2048) == 2048 &&
                    pvd[0] == 0x01 && pvd[1] == (byte)'C' && pvd[2] == (byte)'D' &&
                    pvd[3] == (byte)'0' && pvd[4] == (byte)'0' && pvd[5] == (byte)'1')
                {
                    var pvdResult = DetectFromPvd(pvd, stream.Length);
                    if (pvdResult != null) return pvdResult;
                }
            }

            // Check PVD at raw 2352-byte sector offset for BIN/IMG files.
            // In raw sector images, sector 16 is at offset 16 * 2352 = 37632,
            // and the user data (containing the PVD) starts 16 bytes into the sector
            // (after 12-byte sync pattern + 4-byte header).
            const int rawSectorSize = 2352;
            const int rawUserDataOffset = 16; // 12 sync + 4 header
            long rawPvdOffset = 16L * rawSectorSize + rawUserDataOffset;
            if (stream.Length > rawPvdOffset + 2048)
            {
                stream.Seek(rawPvdOffset, SeekOrigin.Begin);
                var rawPvd = new byte[2048];
                if (stream.Read(rawPvd, 0, 2048) == 2048 &&
                    rawPvd[0] == 0x01 && rawPvd[1] == (byte)'C' && rawPvd[2] == (byte)'D' &&
                    rawPvd[3] == (byte)'0' && rawPvd[4] == (byte)'0' && rawPvd[5] == (byte)'1')
                {
                    var pvdResult = DetectFromPvd(rawPvd, stream.Length);
                    if (pvdResult != null) return pvdResult;
                }
            }

            // Check for PlayStation 1 via license string in raw BIN images.
            // PS1 discs have "Sony Computer Entertainment" or "PLAYSTATION" at the
            // beginning of the system area. For raw 2352-byte sectors, the data
            // starts at offset 16 (sync+header) in sector 0.
            if (stream.Length > rawSectorSize)
            {
                // Check raw sector 4 (observed license string location in PS1 dumps)
                // at byte offset 4*2352+16
                long ps1LicenseOffset = 4L * rawSectorSize + rawUserDataOffset;
                if (stream.Length > ps1LicenseOffset + 128)
                {
                    stream.Seek(ps1LicenseOffset, SeekOrigin.Begin);
                    var ps1Check = new byte[128];
                    if (stream.Read(ps1Check, 0, 128) == 128)
                    {
                        var ps1Str = Encoding.ASCII.GetString(ps1Check);
                        if (ps1Str.Contains("Sony Computer Entertainment", StringComparison.OrdinalIgnoreCase) ||
                            ps1Str.Contains("PLAYSTATION", StringComparison.OrdinalIgnoreCase))
                            return "PlayStation 1";
                    }
                }

                // Also check cooked sector 4 offset (4*2048 = 8192)
                if (stream.Length > 8192 + 128)
                {
                    stream.Seek(8192, SeekOrigin.Begin);
                    var ps1Cooked = new byte[128];
                    if (stream.Read(ps1Cooked, 0, 128) == 128)
                    {
                        var ps1CookedStr = Encoding.ASCII.GetString(ps1Cooked);
                        if (ps1CookedStr.Contains("Sony Computer Entertainment", StringComparison.OrdinalIgnoreCase) ||
                            ps1CookedStr.Contains("PLAYSTATION", StringComparison.OrdinalIgnoreCase))
                            return "PlayStation 1";
                    }
                }
            }

            // Check for specific magic bytes at the start of the image
            stream.Seek(0, SeekOrigin.Begin);
            var header = new byte[16];
            if (stream.Read(header, 0, 16) < 16) return null;

            // Sega Saturn: "SEGA SEGASATURN" at offset 0 in IP.BIN (sector 0 of data track)
            // For raw 2352-byte images, the data starts at offset 16 (after sync+header)
            if (stream.Length > 2352 + 16)
            {
                stream.Seek(16, SeekOrigin.Begin);
                var saturnCheck = new byte[16];
                if (stream.Read(saturnCheck, 0, 16) == 16)
                {
                    var saturnStr = Encoding.ASCII.GetString(saturnCheck, 0, 16);
                    if (saturnStr.Contains("SEGA SEGASATURN", StringComparison.OrdinalIgnoreCase))
                        return "Sega Saturn";
                    if (saturnStr.Contains("SEGA SEGAKATANA", StringComparison.OrdinalIgnoreCase))
                        return "Sega Dreamcast";
                    if (saturnStr.Contains("SEGA MEGA DRIVE", StringComparison.OrdinalIgnoreCase) ||
                        saturnStr.Contains("SEGADISCSYSTEM", StringComparison.OrdinalIgnoreCase))
                        return "Sega Mega CD";
                }
            }

            // Also check at offset 0 for 2048-byte (cooked) images
            if (stream.Length > 32)
            {
                stream.Seek(0, SeekOrigin.Begin);
                var cookedCheck = new byte[32];
                if (stream.Read(cookedCheck, 0, 32) == 32)
                {
                    var cookedStr = Encoding.ASCII.GetString(cookedCheck, 0, 32);
                    if (cookedStr.Contains("SEGA SEGASATURN", StringComparison.OrdinalIgnoreCase))
                        return "Sega Saturn";
                    if (cookedStr.Contains("SEGA SEGAKATANA", StringComparison.OrdinalIgnoreCase))
                        return "Sega Dreamcast";
                    if (cookedStr.Contains("SEGA MEGA DRIVE", StringComparison.OrdinalIgnoreCase) ||
                        cookedStr.Contains("SEGADISCSYSTEM", StringComparison.OrdinalIgnoreCase))
                        return "Sega Mega CD";
                }
            }

            // GameCube/Wii: check magic words in disc header.
            // Per Nintendo disc format:
            //   Offset 0x18: Wii magic word (0x5D1C9EA3)
            //   Offset 0x1C: GameCube magic word (0xC2339F3D)
            // For raw 2352-byte sector images, user data starts at offset 16 (after
            // 12-byte sync + 4-byte header).
            if (stream.Length > 0x20)
            {
                var magic = new byte[4];

                // Check Wii magic at 0x18 (cooked offset)
                stream.Seek(0x18, SeekOrigin.Begin);
                if (stream.Read(magic, 0, 4) == 4)
                {
                    uint discMagic = ((uint)magic[0] << 24) | ((uint)magic[1] << 16) | ((uint)magic[2] << 8) | magic[3];
                    if (discMagic == 0x5D1C9EA3) return "Wii";
                }

                // Check GameCube magic at 0x1C (cooked offset)
                stream.Seek(0x1C, SeekOrigin.Begin);
                if (stream.Read(magic, 0, 4) == 4)
                {
                    uint discMagic = ((uint)magic[0] << 24) | ((uint)magic[1] << 16) | ((uint)magic[2] << 8) | magic[3];
                    if (discMagic == 0xC2339F3D) return "GameCube";
                }

                // Try raw (2352-byte sector) offsets: user data at +16
                // Wii magic at 16 + 0x18 = 0x28
                if (stream.Length > 0x30)
                {
                    stream.Seek(0x28, SeekOrigin.Begin);
                    if (stream.Read(magic, 0, 4) == 4)
                    {
                        uint discMagic = ((uint)magic[0] << 24) | ((uint)magic[1] << 16) | ((uint)magic[2] << 8) | magic[3];
                        if (discMagic == 0x5D1C9EA3) return "Wii";
                    }

                    // GameCube magic at 16 + 0x1C = 0x2C
                    stream.Seek(0x2C, SeekOrigin.Begin);
                    if (stream.Read(magic, 0, 4) == 4)
                    {
                        uint discMagic = ((uint)magic[0] << 24) | ((uint)magic[1] << 16) | ((uint)magic[2] << 8) | magic[3];
                        if (discMagic == 0xC2339F3D) return "GameCube";
                    }
                }
            }

            // Wii U: check for WUP magic at offset 0
            if (stream.Length > 4)
            {
                stream.Seek(0, SeekOrigin.Begin);
                var wiiUHeader = new byte[4];
                if (stream.Read(wiiUHeader, 0, 4) == 4)
                {
                    uint wiiUMagic = ((uint)wiiUHeader[0] << 24) | ((uint)wiiUHeader[1] << 16) | ((uint)wiiUHeader[2] << 8) | wiiUHeader[3];
                    if (wiiUMagic == 0x57555030) return "Wii U"; // "WUP0"
                }
            }

            // 3DO: check for "REAL" at the start of the disc
            if (header[0] == 0x01 && header[1] == 0x5A)
                return "3DO";

            // Neo Geo CD: check system area
            if (stream.Length > 0x110)
            {
                stream.Seek(0x100, SeekOrigin.Begin);
                var neoGeo = new byte[10];
                if (stream.Read(neoGeo, 0, 10) >= 7 &&
                    Encoding.ASCII.GetString(neoGeo).Contains("NEO-GEO"))
                    return "Neo Geo CD";
            }

            // PC Engine / TurboGrafx-CD: check for "PC Engine CD-ROM SYSTEM" at sector 1
            if (stream.Length > 2048 + 32)
            {
                // For cooked images, system area starts at sector 0
                stream.Seek(0, SeekOrigin.Begin);
                var pceCheck = new byte[32];
                if (stream.Read(pceCheck, 0, 32) == 32)
                {
                    var pceStr = Encoding.ASCII.GetString(pceCheck);
                    if (pceStr.Contains("PC Engine") || pceStr.Contains("PCECD"))
                        return "PC Engine / TurboGrafx-CD";
                }

                // For raw (2352-byte) images, data starts at offset 16 (sync+header) in the first sector
                if (stream.Length > 2352 + 32)
                {
                    stream.Seek(16, SeekOrigin.Begin); // Seek from beginning to offset 16
                    var pceRawCheck = new byte[32];
                    if (stream.Read(pceRawCheck, 0, 32) == 32)
                    {
                        var pceRawStr = Encoding.ASCII.GetString(pceRawCheck);
                        if (pceRawStr.Contains("PC Engine") || pceRawStr.Contains("PCECD"))
                            return "PC Engine / TurboGrafx-CD";
                    }
                }
            }

            // Amiga CD32 / CDTV: check boot block and PVD system identifier.
            // CD32 boot block: 0x11 0x00 "AMIGABOOT" at offset 0 (cooked) or at offset 16 (raw).
            // Alternate: "AMIGA" in PVD system identifier (checked above).
            // CDTV: "CDTV" identifier at offset 0 or PVD system identifier.
            if (stream.Length > 0x60)
            {
                stream.Seek(0, SeekOrigin.Begin);
                var amigaCheck = new byte[32];
                var amigaBytesRead = stream.Read(amigaCheck, 0, Math.Min(32, (int)Math.Min(stream.Length, 32)));
                if (amigaBytesRead >= 16)
                {
                    var amigaStr = Encoding.ASCII.GetString(amigaCheck, 0, amigaBytesRead);
                    // CDTV identifier at offset 0
                    if (amigaStr.Contains("CDTV"))
                        return "Amiga CDTV";
                    // CD32 specific boot block: "AMIGABOOT" or the classic 0x11 0x00 signature
                    if (amigaStr.Contains("AMIGABOOT"))
                        return "Amiga CD32";
                    if (amigaStr.Contains("AMIGA"))
                        return "Amiga CD32";
                }

                // Also check raw sector offset (16 bytes in for sync+header)
                if (stream.Length > 2352 + 32)
                {
                    stream.Seek(16, SeekOrigin.Begin);
                    var amigaRawCheck = new byte[32];
                    var rawRead = stream.Read(amigaRawCheck, 0, 32);
                    if (rawRead >= 16)
                    {
                        var amigaRawStr = Encoding.ASCII.GetString(amigaRawCheck, 0, rawRead);
                        if (amigaRawStr.Contains("CDTV"))
                            return "Amiga CDTV";
                        if (amigaRawStr.Contains("AMIGABOOT"))
                            return "Amiga CD32";
                        if (amigaRawStr.Contains("AMIGA"))
                            return "Amiga CD32";
                    }
                }
            }

            // Atari Jaguar CD: check for "ATRI" magic at offset 0 or "TARA" at specific location
            if (stream.Length > 0x40)
            {
                stream.Seek(0, SeekOrigin.Begin);
                var jagCheck = new byte[32];
                if (stream.Read(jagCheck, 0, 32) == 32)
                {
                    var jagStr = Encoding.ASCII.GetString(jagCheck);
                    if (jagStr.Contains("ATRI") || jagStr.Contains("TARA"))
                        return "Atari Jaguar CD";
                }

                // Also check at raw sector offset (16 bytes in for sync+header)
                if (stream.Length > 2352 + 32)
                {
                    stream.Seek(16, SeekOrigin.Begin);
                    if (stream.Read(jagCheck, 0, 32) == 32)
                    {
                        var jagStr = Encoding.ASCII.GetString(jagCheck);
                        if (jagStr.Contains("ATRI") || jagStr.Contains("TARA"))
                            return "Atari Jaguar CD";
                    }
                }
            }

            // CDI format detection: parse CDI trailer and examine tracks for Dreamcast signatures.
            // Must be checked BEFORE VCD/SVCD detection — some CDI images contain CD-XA tracks
            // that could be misidentified as VCD by the VCD detector, preventing Dreamcast detection.
            if (Path.GetExtension(imagePath).Equals(".cdi", StringComparison.OrdinalIgnoreCase))
            {
                var cdiImage = CdiParser.Parse(imagePath);
                if (cdiImage.IsValid)
                {
                    // Dreamcast GD-ROM images in CDI format typically have multiple sessions,
                    // and the data track contains "SEGA SEGAKATANA" or "SEGA DREAMCAST" headers.
                    foreach (var track in cdiImage.DataTracks)
                    {
                        if (track.TotalLength <= 0 || track.FileOffset < 0) continue;
                        try
                        {
                            using var cdiStream = new FileStream(imagePath, FileMode.Open,
                                FileAccess.Read, FileShare.Read);
                            // Read first sector of data track to check for Sega header
                            long dataStart = track.FileOffset + (long)track.Pregap * track.SectorSize;
                            if (dataStart + track.SectorSize > cdiStream.Length) continue;
                            cdiStream.Seek(dataStart + track.UserDataOffset, SeekOrigin.Begin);
                            var sectorBuf = new byte[Math.Min(256, track.UserDataSize)];
                            if (cdiStream.Read(sectorBuf, 0, sectorBuf.Length) >= 16)
                            {
                                var headerStr = Encoding.ASCII.GetString(sectorBuf, 0, Math.Min(32, sectorBuf.Length));
                                if (headerStr.Contains("SEGA SEGAKATANA", StringComparison.OrdinalIgnoreCase) ||
                                    headerStr.Contains("SEGA DREAMCAST", StringComparison.OrdinalIgnoreCase))
                                    return "Sega Dreamcast (CDI)";
                            }
                        }
                        catch { /* best-effort */ }
                    }

                    // Size-based heuristic: GD-ROM images are typically around 1.2 GB
                    if (stream.Length >= 500_000_000 && stream.Length <= FormatHelper.GdRomCapacity + 200_000_000)
                        return "Sega Dreamcast (CDI)";
                }
            }

            // VCD / SVCD detection: check for CD-XA + VCD/SVCD directory structure
            var vcdType = Native.Iso.VcdBuilder.DetectVcdSvcd(imagePath);
            if (vcdType != null) return null; // Not a gaming disc — return null to let callers handle

            // Blu-ray directory-based detection for PS3/PS4/PS5/Wii U ISOs.
            // PS3 ISOs may not have "PLAYSTATION" in the PVD system identifier,
            // so fall back to scanning the root directory for known structures.
            // This mirrors the approach used in Ps3DiscService.DetectContentFromStream.
            var bdResult = TryDetectBluRayGameFromDirectories(stream);
            if (bdResult != null) return bdResult;
        }
        catch { /* detection is best-effort */ }

        return null;
    }

    /// <summary>
    /// Scans the ISO 9660 root directory for known Blu-ray game structures
    /// (PS3_GAME, PS4, PS5, Wii U). This is a fallback when PVD-based detection
    /// fails — PS3 ISOs in particular often lack a "PLAYSTATION" system identifier.
    /// </summary>
    private static string? TryDetectBluRayGameFromDirectories(Stream stream)
    {
        if (stream.Length < 17 * 2048) return null;

        try
        {
            // Read PVD at sector 16
            stream.Seek(16L * 2048, SeekOrigin.Begin);
            var pvd = new byte[2048];
            if (stream.Read(pvd, 0, 2048) < 2048) return null;

            // Validate PVD signature: type 1 + "CD001"
            if (pvd[0] != 0x01 || pvd[1] != (byte)'C' || pvd[2] != (byte)'D' ||
                pvd[3] != (byte)'0' || pvd[4] != (byte)'0' || pvd[5] != (byte)'1')
                return null;

            // Extract root directory record from PVD offset 156
            if (pvd[156] < 34) return null;

            int rootLba = pvd[156 + 2] | (pvd[156 + 3] << 8) |
                          (pvd[156 + 4] << 16) | (pvd[156 + 5] << 24);
            int rootLen = pvd[156 + 10] | (pvd[156 + 11] << 8) |
                          (pvd[156 + 12] << 16) | (pvd[156 + 13] << 24);

            if (rootLba <= 0 || rootLen <= 0) return null;

            // Read root directory entries
            int sectorsToRead = (rootLen + 2047) / 2048;
            var dirData = new byte[sectorsToRead * 2048];
            stream.Seek((long)rootLba * 2048, SeekOrigin.Begin);
            int read = stream.Read(dirData, 0, dirData.Length);
            if (read == 0) return null;

            var dirs = new List<string>();
            int offset = 0;
            while (offset < rootLen && offset < read)
            {
                int recordLen = dirData[offset];
                if (recordLen == 0)
                {
                    offset = ((offset / 2048) + 1) * 2048;
                    continue;
                }
                if (offset + recordLen > read) break;

                int nameLen = dirData[offset + 32];
                if (nameLen > 0 && nameLen <= recordLen - 33)
                {
                    var name = Encoding.ASCII.GetString(dirData, offset + 33, nameLen)
                        .TrimEnd(';', '1', '\0', ' ');
                    if (name.Length > 1)
                        dirs.Add(name);
                }
                offset += recordLen;
            }

            // Check for PS3 game disc structure
            bool hasPs3Game = dirs.Any(d => d.Equals("PS3_GAME", StringComparison.OrdinalIgnoreCase));
            bool hasPs3Disc = dirs.Any(d => d.Equals("PS3_DISC.SFB", StringComparison.OrdinalIgnoreCase));
            if (hasPs3Game || hasPs3Disc)
                return "PlayStation 3";

            // Check for PS4/PS5 disc structure
            bool hasApp = dirs.Any(d => d.Equals("app", StringComparison.OrdinalIgnoreCase));
            bool hasBd = dirs.Any(d => d.Equals("bd", StringComparison.OrdinalIgnoreCase));
            bool hasUds = dirs.Any(d => d.Equals("uds", StringComparison.OrdinalIgnoreCase));
            if (hasUds || (hasApp && hasBd))
                return "PlayStation 4/5";

            // Check for Wii U disc structure (WUP content)
            bool hasContent = dirs.Any(d => d.Equals("content", StringComparison.OrdinalIgnoreCase));
            bool hasMeta = dirs.Any(d => d.Equals("meta", StringComparison.OrdinalIgnoreCase));
            bool hasCode = dirs.Any(d => d.Equals("code", StringComparison.OrdinalIgnoreCase));
            if (hasContent && hasMeta && hasCode && stream.Length > Helpers.FormatHelper.DVD9Capacity)
                return "Wii U";
        }
        catch { /* best-effort */ }

        return null;
    }

    /// <summary>
    /// Detects the content type of a Blu-ray disc image.
    /// Examines the filesystem for BDMV (movie), PS3_GAME (PS3), PS4, PS5, or generic data.
    /// For PS3 game discs, also extracts PARAM.SFO metadata.
    /// </summary>
    /// <param name="imagePath">Path to the Blu-ray ISO image.</param>
    /// <returns>Detailed content information about the Blu-ray disc.</returns>
    public static BluRayContentInfo DetectBluRayContent(string imagePath)
    {
        return Ps3DiscService.DetectContent(imagePath);
    }

    /// <summary>
    /// Detects gaming system from a validated ISO 9660 PVD (Primary Volume Descriptor).
    /// Returns the preset name if detected, null otherwise.
    /// </summary>
    private static string? DetectFromPvd(byte[] pvd, long imageLength)
    {
        // System identifier at offset 8, 32 bytes
        var systemId = Encoding.ASCII.GetString(pvd, 8, 32).Trim();
        // Application identifier at offset 702, 128 bytes (ECMA-119 §8.4.1)
        var appId = Encoding.ASCII.GetString(pvd, 702, 128).Trim();

        if (systemId.Contains("PLAYSTATION", StringComparison.OrdinalIgnoreCase))
        {
            // Detect PlayStation generation using both PVD identifier and image size.
            var appIdUpper = appId.ToUpperInvariant();
            if (appIdUpper.Contains("PS5") || appIdUpper.Contains("PS4") ||
                imageLength > FormatHelper.BD25Capacity)
                return "PlayStation 4/5";
            if (appIdUpper.Contains("PS3") ||
                imageLength > FormatHelper.DVD9Capacity)
                return "PlayStation 3";
            if (appIdUpper.Contains("PS2") || appIdUpper.Contains("PLAYSTATION 2") ||
                imageLength >= FormatHelper.CdImageSizeThresholdBytes)
                return "PlayStation 2";
            return "PlayStation 1";
        }

        if (systemId.Contains("PSP GAME", StringComparison.OrdinalIgnoreCase))
            return "PSP (UMD)";

        if (systemId.Contains("Xbox", StringComparison.OrdinalIgnoreCase) ||
            appId.Contains("Xbox", StringComparison.OrdinalIgnoreCase))
        {
            if (imageLength > FormatHelper.BD25Capacity)
                return "Xbox One/Series";
            if (imageLength > FormatHelper.DVD5Capacity)
                return "Xbox 360";
            return "Xbox";
        }

        if (systemId.Contains("CD-RTOS", StringComparison.OrdinalIgnoreCase) ||
            systemId.Contains("CD-I", StringComparison.OrdinalIgnoreCase))
            return "CD-i (Philips)";

        // Amiga CD32/CDTV: check PVD system identifier for "AMIGA" or "CDTV"
        if (systemId.Contains("CDTV", StringComparison.OrdinalIgnoreCase))
            return "Amiga CDTV";
        if (systemId.Contains("AMIGA", StringComparison.OrdinalIgnoreCase))
            return "Amiga CD32";

        return null;
    }

    /// <summary>
    /// Validates a disc image against gaming system specifications.
    /// Returns validation messages (empty list = valid).
    /// </summary>
    public static System.Collections.Generic.List<string> ValidateImage(
        string imagePath, string gamingSystem)
    {
        var messages = new System.Collections.Generic.List<string>();

        if (!File.Exists(imagePath))
        {
            messages.Add($"Image file not found: {imagePath}");
            return messages;
        }

        var fi = new FileInfo(imagePath);

        if (!FormatHelper.GamingPresets.TryGetValue(gamingSystem, out var preset))
        {
            messages.Add($"Unknown gaming system: {gamingSystem}");
            return messages;
        }

        // Validate sector size alignment
        if (preset.SectorSize > 0 && fi.Length % preset.SectorSize != 0)
        {
            messages.Add($"Warning: Image size ({fi.Length} bytes) is not aligned to " +
                $"{preset.SectorSize}-byte sectors. Expected alignment for {gamingSystem}.");
        }

        // Validate capacity
        switch (gamingSystem)
        {
            case "PlayStation 1":
                if (fi.Length > FormatHelper.CD80Capacity)
                    messages.Add("Warning: Image exceeds standard CD capacity (80 min).");
                break;
            case "PlayStation 2":
            case "Xbox":
                if (fi.Length > FormatHelper.DVD9Capacity)
                    messages.Add("Warning: Image exceeds DVD-9 capacity.");
                break;
            case "GameCube":
                if (fi.Length > FormatHelper.GameCubeMiniDVDCapacity)
                    messages.Add("Warning: Image exceeds GameCube miniDVD capacity (1.46 GB).");
                break;
            case "Wii":
                if (fi.Length > FormatHelper.WiiDLCapacity)
                    messages.Add("Warning: Image exceeds Wii dual-layer disc capacity.");
                break;
            case "PSP (UMD)":
                if (fi.Length > FormatHelper.UMDDualLayerCapacity)
                    messages.Add("Warning: Image exceeds UMD dual-layer capacity (1.8 GB).");
                break;
            case "PlayStation 3":
                if (fi.Length > FormatHelper.BD50Capacity)
                    messages.Add("Warning: Image exceeds BD-50 capacity.");
                // Validate PS3 disc structure and encryption status
                try
                {
                    var bdInfo = Ps3DiscService.DetectContent(imagePath);
                    if (bdInfo.ContentType == BluRayContentType.Ps3Game)
                    {
                        if (!string.IsNullOrEmpty(bdInfo.Title))
                            messages.Add($"Info: PS3 game detected — {bdInfo.Title}" +
                                (!string.IsNullOrEmpty(bdInfo.TitleId) ? $" [{bdInfo.TitleId}]" : ""));
                        if (bdInfo.IsEncrypted)
                            messages.Add("Warning: PS3 ISO is encrypted. Encrypted PS3 discs " +
                                "will not work on consoles. Please decrypt the ISO using an " +
                                "IRD file before burning.");
                        else if (bdInfo.RegionCount > 0)
                            messages.Add("Info: PS3 ISO appears to be decrypted (ready for burning).");
                    }
                    else
                    {
                        messages.Add("Warning: ISO does not appear to contain a valid PS3 game " +
                            "structure (PS3_GAME directory not found).");
                    }
                }
                catch { /* PS3 detection is best-effort */ }
                break;
            case "PlayStation 4/5":
            case "Xbox One/Series":
                if (fi.Length > FormatHelper.BD100Capacity)
                    messages.Add("Warning: Image exceeds BD-100 (BDXL) capacity.");
                break;
            case "Xbox 360":
                if (fi.Length > FormatHelper.DVD9Capacity)
                    messages.Add("Warning: Image exceeds DVD-9 dual-layer capacity.");
                break;
            case "Wii U":
                if (fi.Length > FormatHelper.BD25Capacity)
                    messages.Add("Warning: Image exceeds Wii U disc capacity (25 GB).");
                break;
            case "Sega Dreamcast":
                // GD-ROM max usable is ~1.2 GB
                if (fi.Length > FormatHelper.GdRomCapacity)
                    messages.Add("Warning: Image exceeds GD-ROM usable capacity (~1.2 GB).");
                // Validate GD-ROM header structure
                try
                {
                    using var dcStream = File.OpenRead(imagePath);
                    // Check for SEGA SEGAKATANA header in IP.BIN
                    bool foundHeader = false;
                    // Try raw (2352-byte sectors) first
                    if (dcStream.Length > 2352 + 32)
                    {
                        dcStream.Seek(16, SeekOrigin.Begin);
                        var dcHeader = new byte[32];
                        if (dcStream.Read(dcHeader, 0, 32) == 32)
                        {
                            var dcStr = Encoding.ASCII.GetString(dcHeader, 0, 16);
                            if (dcStr.Contains("SEGA SEGAKATANA")) foundHeader = true;
                        }
                    }
                    // Try cooked (2048-byte sectors)
                    if (!foundHeader && dcStream.Length > 32)
                    {
                        dcStream.Seek(0, SeekOrigin.Begin);
                        var dcHeader = new byte[32];
                        if (dcStream.Read(dcHeader, 0, 32) == 32)
                        {
                            var dcStr = Encoding.ASCII.GetString(dcHeader, 0, 16);
                            if (dcStr.Contains("SEGA SEGAKATANA")) foundHeader = true;
                        }
                    }
                    if (!foundHeader)
                        messages.Add("Warning: Missing SEGA SEGAKATANA header in IP.BIN — " +
                                     "image may not be a valid GD-ROM dump.");
                }
                catch { /* validation is best-effort */ }
                break;
            case "Sega Saturn":
                if (fi.Length > FormatHelper.CD80Capacity)
                    messages.Add("Warning: Image exceeds standard CD capacity (80 min).");
                // Validate Sega Saturn IP.BIN header
                try
                {
                    using var ssStream = File.OpenRead(imagePath);
                    bool foundSsHeader = false;
                    if (ssStream.Length > 2352 + 16)
                    {
                        ssStream.Seek(16, SeekOrigin.Begin);
                        var ssHeader = new byte[16];
                        if (ssStream.Read(ssHeader, 0, 16) == 16)
                        {
                            var ssStr = Encoding.ASCII.GetString(ssHeader, 0, 16);
                            if (ssStr.Contains("SEGA SEGASATURN")) foundSsHeader = true;
                        }
                    }
                    if (!foundSsHeader && ssStream.Length > 16)
                    {
                        ssStream.Seek(0, SeekOrigin.Begin);
                        var ssHeader = new byte[16];
                        if (ssStream.Read(ssHeader, 0, 16) == 16)
                        {
                            var ssStr = Encoding.ASCII.GetString(ssHeader, 0, 16);
                            if (ssStr.Contains("SEGA SEGASATURN")) foundSsHeader = true;
                        }
                    }
                    if (!foundSsHeader)
                        messages.Add("Warning: Missing SEGA SEGASATURN header in IP.BIN — " +
                                     "image may not be a valid Saturn disc dump.");
                }
                catch { /* validation is best-effort */ }
                break;
            case "Sega Mega CD":
            case "3DO":
            case "Neo Geo CD":
            case "PC Engine / TurboGrafx-CD":
            case "Atari Jaguar CD":
            case "CD-i (Philips)":
                if (fi.Length > FormatHelper.CD80Capacity)
                    messages.Add("Warning: Image exceeds standard CD capacity (80 min).");
                break;
            case "Amiga CD32":
                if (fi.Length > FormatHelper.CD80Capacity)
                    messages.Add("Warning: Image exceeds standard CD capacity (80 min).");
                // Validate Amiga CD32 boot block
                try
                {
                    using var cd32Stream = File.OpenRead(imagePath);
                    bool foundAmigaHeader = false;
                    // Check cooked image (offset 0)
                    if (cd32Stream.Length > 32)
                    {
                        cd32Stream.Seek(0, SeekOrigin.Begin);
                        var cd32Header = new byte[32];
                        if (cd32Stream.Read(cd32Header, 0, 32) >= 16)
                        {
                            var cd32Str = Encoding.ASCII.GetString(cd32Header, 0, 16);
                            if (cd32Str.Contains("AMIGA") || cd32Str.Contains("AMIGABOOT"))
                                foundAmigaHeader = true;
                        }
                    }
                    // Check raw image (offset 16)
                    if (!foundAmigaHeader && cd32Stream.Length > 2352 + 32)
                    {
                        cd32Stream.Seek(16, SeekOrigin.Begin);
                        var cd32Header = new byte[32];
                        if (cd32Stream.Read(cd32Header, 0, 32) >= 16)
                        {
                            var cd32Str = Encoding.ASCII.GetString(cd32Header, 0, 16);
                            if (cd32Str.Contains("AMIGA") || cd32Str.Contains("AMIGABOOT"))
                                foundAmigaHeader = true;
                        }
                    }
                    if (!foundAmigaHeader)
                        messages.Add("Warning: Missing Amiga boot block header — " +
                                     "image may not be a valid CD32 disc dump.");
                }
                catch { /* validation is best-effort */ }
                break;
            case "Amiga CDTV":
                if (fi.Length > FormatHelper.CD80Capacity)
                    messages.Add("Warning: Image exceeds standard CD capacity (80 min).");
                // Validate CDTV boot block
                try
                {
                    using var cdtvStream = File.OpenRead(imagePath);
                    bool foundCdtvHeader = false;
                    if (cdtvStream.Length > 32)
                    {
                        cdtvStream.Seek(0, SeekOrigin.Begin);
                        var cdtvHeader = new byte[32];
                        if (cdtvStream.Read(cdtvHeader, 0, 32) >= 16)
                        {
                            var cdtvStr = Encoding.ASCII.GetString(cdtvHeader, 0, 16);
                            if (cdtvStr.Contains("CDTV") || cdtvStr.Contains("AMIGA"))
                                foundCdtvHeader = true;
                        }
                    }
                    if (!foundCdtvHeader && cdtvStream.Length > 2352 + 32)
                    {
                        cdtvStream.Seek(16, SeekOrigin.Begin);
                        var cdtvHeader = new byte[32];
                        if (cdtvStream.Read(cdtvHeader, 0, 32) >= 16)
                        {
                            var cdtvStr = Encoding.ASCII.GetString(cdtvHeader, 0, 16);
                            if (cdtvStr.Contains("CDTV") || cdtvStr.Contains("AMIGA"))
                                foundCdtvHeader = true;
                        }
                    }
                    if (!foundCdtvHeader)
                        messages.Add("Warning: Missing CDTV boot block header — " +
                                     "image may not be a valid CDTV disc dump.");
                }
                catch { /* validation is best-effort */ }
                break;

        }

        return messages;
    }

    /// <summary>
    /// Detects the disc region from an image file header.
    /// Common regions: NTSC-U, NTSC-J, PAL, Region Free.
    /// </summary>
    public static string DetectRegion(string imagePath)
    {
        if (!File.Exists(imagePath)) return "Unknown";

        try
        {
            using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, false);

            // Check ISO PVD system identifier and volume identifier
            if (stream.Length > 32768 + 2048)
            {
                stream.Seek(32768, SeekOrigin.Begin);
                var pvd = new byte[2048];
                if (stream.Read(pvd, 0, 2048) == 2048)
                {
                    var sysId = Encoding.ASCII.GetString(pvd, 8, 32).Trim().ToUpperInvariant();
                    // Volume Identifier at PVD offset 40, 32 bytes (ECMA-119 §8.4, offset within PVD sector)
                    var volId = Encoding.ASCII.GetString(pvd, 40, 32).Trim().ToUpperInvariant();
                    // Combined search across both identifiers
                    var combined = sysId + " " + volId;

                    // PlayStation region codes in system or volume identifier
                    if (combined.Contains("SCUS") || combined.Contains("SLUS")) return "NTSC-U";
                    if (combined.Contains("SCPS") || combined.Contains("SLPS") || combined.Contains("SCPJ")) return "NTSC-J";
                    if (combined.Contains("SCES") || combined.Contains("SLES")) return "PAL";
                }
            }

            // Check Sega Saturn/Dreamcast header for region
            // In raw (2352-byte) images, IP.BIN data starts at offset 16 (sync+header)
            // In cooked (2048-byte) images, IP.BIN data starts at offset 0
            // Region field is 16 bytes at IP.BIN offset 0x40, containing space-separated
            // region codes: "J" (Japan/NTSC-J), "U" (USA/NTSC-U), "E" (Europe/PAL)
            foreach (var regionOffset in new long[] { 16 + 0x40, 0x40 })
            {
                if (stream.Length > regionOffset + 16)
                {
                    stream.Seek(regionOffset, SeekOrigin.Begin);
                    var region = new byte[16];
                    if (stream.Read(region, 0, 16) == 16)
                    {
                        var regionStr = Encoding.ASCII.GetString(region).Trim();
                        // Sega region codes are space-separated single characters
                        // Multi-region discs have multiple codes, e.g. "J U E"
                        // Only match exact single-char tokens, not substrings
                        var regionParts = regionStr.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        bool hasU = false, hasJ = false, hasE = false;
                        foreach (var part in regionParts)
                        {
                            if (part == "U") hasU = true;
                            else if (part == "J") hasJ = true;
                            else if (part == "E") hasE = true;
                        }
                        if (hasU && hasJ && hasE) return "Region Free";
                        if (hasU) return "NTSC-U";
                        if (hasJ) return "NTSC-J";
                        if (hasE) return "PAL";
                    }
                }
            }

            // Check GameCube/Wii region byte at offset 3
            // Only applies if the disc has GC/Wii magic at offset 0x18
            if (stream.Length > 0x20)
            {
                stream.Seek(0x18, SeekOrigin.Begin);
                var magic = new byte[4];
                if (stream.Read(magic, 0, 4) == 4)
                {
                    uint gcWiiMagic = ((uint)magic[0] << 24) | ((uint)magic[1] << 16) | ((uint)magic[2] << 8) | magic[3];
                    if (gcWiiMagic is 0xC2339F3D or 0x5D1C9EA3)
                    {
                        stream.Seek(3, SeekOrigin.Begin);
                        var regionByte = (byte)stream.ReadByte();
                        return regionByte switch
                        {
                            (byte)'E' => "NTSC-U",
                            (byte)'J' => "NTSC-J",
                            (byte)'P' => "PAL",
                            (byte)'K' => "NTSC-K",
                            (byte)'W' => "NTSC-T",
                            _ => "Unknown"
                        };
                    }
                }
            }
        }
        catch { /* detection is best-effort */ }

        // VCD/SVCD region detection: check video standard flag in INFO.VCD/INFO.SVD
        try
        {
            var vcdType = Native.Iso.VcdBuilder.DetectVcdSvcd(imagePath);
            if (vcdType != null)
            {
                using var vcdStream = new FileStream(imagePath, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 4096, false);
                // Scan sectors 17-50 for INFO.VCD/INFO.SVD
                string signature = vcdType == "SVCD" ? "SUPERVCD" : "VIDEO_CD";
                for (int sector = 17; sector < 50 && (sector * 2048L + 2048) <= vcdStream.Length; sector++)
                {
                    vcdStream.Seek(sector * 2048L, SeekOrigin.Begin);
                    var sectorData = new byte[64];
                    if (vcdStream.Read(sectorData, 0, 64) < 37) continue;
                    var sig = Encoding.ASCII.GetString(sectorData, 0, 8).TrimEnd();
                    if (sig == signature && sectorData[36] != 0)
                    {
                        return sectorData[36] switch
                        {
                            0x01 => "NTSC",
                            0x02 => "PAL",
                            _ => "Unknown"
                        };
                    }
                }
            }
        }
        catch { /* detection is best-effort */ }

        return "Unknown";
    }

    /// <summary>
    /// Patches a disc image to modify the region byte (for supported systems only).
    /// Returns true if the patch was applied successfully.
    /// </summary>
    public static bool PatchRegion(string imagePath, string targetRegion, string gamingSystem)
    {
        if (!File.Exists(imagePath)) return false;

        // GameCube, Wii, Sega Saturn, Sega Dreamcast, and PlayStation 1/2 support region patching
        if (gamingSystem is not ("GameCube" or "Wii" or "Sega Saturn" or "Sega Dreamcast"
            or "PlayStation 1" or "PlayStation 2"))
            return false;

        if (gamingSystem is "PlayStation 1" or "PlayStation 2")
        {
            // PlayStation region patching: modify the system.cnf or PVD system identifier
            // PS1/PS2 region is encoded in the disc serial number in the PVD:
            //   SCUS/SLUS = NTSC-U, SCPS/SLPS/SCPJ = NTSC-J, SCES/SLES = PAL
            // We patch the system identifier and volume identifier fields in the PVD.
            string regionPrefix = targetRegion.ToUpperInvariant() switch
            {
                "NTSC-U" or "USA" => "SLUS",
                "NTSC-J" or "JAPAN" => "SLPS",
                "PAL" or "EUROPE" => "SLES",
                _ => ""
            };
            if (string.IsNullOrEmpty(regionPrefix)) return false;

            try
            {
                using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.ReadWrite,
                    FileShare.None, 4096, false);

                // Check ISO PVD at sector 16 (offset 32768 for 2048-byte sectors)
                // For raw 2352-byte images, PVD is at sector 16 × 2352 + 16 (sync+header offset)
                long pvdOffset = -1;
                if (stream.Length > 32768 + 2048)
                {
                    // Try cooked (2048-byte) first
                    stream.Seek(32768, SeekOrigin.Begin);
                    var pvdCheck = new byte[6];
                    if (stream.Read(pvdCheck, 0, 6) == 6 &&
                        pvdCheck[0] == 0x01 && pvdCheck[1] == (byte)'C' && pvdCheck[2] == (byte)'D' &&
                        pvdCheck[3] == (byte)'0' && pvdCheck[4] == (byte)'0' && pvdCheck[5] == (byte)'1')
                    {
                        pvdOffset = 32768;
                    }
                }

                if (pvdOffset < 0 && stream.Length > 16 * 2352 + 16 + 2048)
                {
                    // Try raw (2352-byte sectors): PVD at sector 16 × 2352 + 16
                    long rawPvdOffset = 16L * 2352 + 16;
                    stream.Seek(rawPvdOffset, SeekOrigin.Begin);
                    var pvdCheck = new byte[6];
                    if (stream.Read(pvdCheck, 0, 6) == 6 &&
                        pvdCheck[0] == 0x01 && pvdCheck[1] == (byte)'C' && pvdCheck[2] == (byte)'D' &&
                        pvdCheck[3] == (byte)'0' && pvdCheck[4] == (byte)'0' && pvdCheck[5] == (byte)'1')
                    {
                        pvdOffset = rawPvdOffset;
                    }
                }

                if (pvdOffset < 0) return false;

                // Read the current system identifier at PVD + 8 (32 bytes)
                stream.Seek(pvdOffset + 8, SeekOrigin.Begin);
                var sysId = new byte[32];
                if (stream.Read(sysId, 0, 32) != 32) return false;
                var sysIdStr = Encoding.ASCII.GetString(sysId).Trim();

                // Only patch if it looks like a PlayStation identifier
                if (!sysIdStr.Contains("PLAYSTATION", StringComparison.OrdinalIgnoreCase))
                    return false;

                // Read the volume identifier at PVD + 40 (32 bytes) and patch the serial prefix
                stream.Seek(pvdOffset + 40, SeekOrigin.Begin);
                var volId = new byte[32];
                if (stream.Read(volId, 0, 32) != 32) return false;
                var volIdStr = Encoding.ASCII.GetString(volId).TrimEnd();

                // Replace the region prefix in the volume identifier (e.g., SLUS -> SLES)
                string[] knownPrefixes = { "SCUS", "SLUS", "SCPS", "SLPS", "SCPJ", "SCES", "SLES" };
                bool patched = false;
                foreach (var prefix in knownPrefixes)
                {
                    if (volIdStr.Contains(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        volIdStr = volIdStr.Replace(prefix, regionPrefix, StringComparison.OrdinalIgnoreCase);
                        patched = true;
                        break;
                    }
                }
                if (!patched) return false;

                // Write the patched volume identifier back (truncate to 32 characters per ECMA-119)
                var truncatedVolId = volIdStr.Length > 32 ? volIdStr[..32] : volIdStr;
                var newVolId = Encoding.ASCII.GetBytes(truncatedVolId.PadRight(32));
                stream.Seek(pvdOffset + 40, SeekOrigin.Begin);
                stream.Write(newVolId, 0, 32);
                return true;
            }
            catch { return false; }
        }

        if (gamingSystem is "GameCube" or "Wii")
        {
            byte regionByte = targetRegion.ToUpperInvariant() switch
            {
                "NTSC-U" or "USA" => (byte)'E',
                "NTSC-J" or "JAPAN" => (byte)'J',
                "PAL" or "EUROPE" => (byte)'P',
                "NTSC-K" or "KOREA" => (byte)'K',
                _ => 0
            };
            if (regionByte == 0) return false;

            try
            {
                using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.ReadWrite,
                    FileShare.None, 4096, false);
                if (stream.Length < 4) return false;

                stream.Seek(3, SeekOrigin.Begin);
                stream.WriteByte(regionByte);
                return true;
            }
            catch { return false; }
        }

        if (gamingSystem is "Sega Saturn" or "Sega Dreamcast")
        {
            // Sega region string at offset 0x40 in IP.BIN (cooked) or 16 + 0x40 (raw)
            string regionChar = targetRegion.ToUpperInvariant() switch
            {
                "NTSC-U" or "USA" => "U",
                "NTSC-J" or "JAPAN" => "J",
                "PAL" or "EUROPE" => "E",
                _ => ""
            };
            if (string.IsNullOrEmpty(regionChar)) return false;

            try
            {
                using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.ReadWrite,
                    FileShare.None, 4096, false);
                if (stream.Length < 0x50) return false;

                // Determine offset and validate IP.BIN header before patching
                long offset = 0x40;
                bool isRaw = false;
                if (stream.Length > 2352 + 0x50)
                {
                    // Check if this is a raw image by looking for sync pattern
                    stream.Seek(0, SeekOrigin.Begin);
                    var syncCheck = new byte[4];
                    if (stream.Read(syncCheck, 0, 4) == 4 &&
                        syncCheck[0] == 0x00 && syncCheck[1] == 0xFF &&
                        syncCheck[2] == 0xFF && syncCheck[3] == 0xFF)
                    {
                        offset = 16 + 0x40; // Raw image
                        isRaw = true;
                    }
                }

                // Validate IP.BIN header: check for "SEGA" identifier at the start
                long headerOffset = isRaw ? 16 : 0;
                stream.Seek(headerOffset, SeekOrigin.Begin);
                var headerCheck = new byte[16];
                if (stream.Read(headerCheck, 0, 16) < 16) return false;
                var headerStr = Encoding.ASCII.GetString(headerCheck, 0, 16);
                bool isValidHeader = gamingSystem == "Sega Saturn"
                    ? headerStr.Contains("SEGA SEGASATURN")
                    : headerStr.Contains("SEGA SEGAKATANA");
                if (!isValidHeader) return false;

                stream.Seek(offset, SeekOrigin.Begin);
                var regionBytes = Encoding.ASCII.GetBytes(regionChar.PadRight(16));
                stream.Write(regionBytes, 0, Math.Min(regionBytes.Length, 16));
                return true;
            }
            catch { return false; }
        }

        return false;
    }

    // =====================================================================
    // ESR Patch (PS2 DVD-R)
    // =====================================================================

    /// <summary>
    /// CRC-ITU-T lookup table used for UDF descriptor tag checksums.
    /// Per ITU-T Recommendation V.41, polynomial 0x1021.
    /// </summary>
    private static readonly ushort[] CrcItuTTable = {
        0x0000, 0x1021, 0x2042, 0x3063, 0x4084, 0x50a5, 0x60c6, 0x70e7,
        0x8108, 0x9129, 0xa14a, 0xb16b, 0xc18c, 0xd1ad, 0xe1ce, 0xf1ef,
        0x1231, 0x0210, 0x3273, 0x2252, 0x52b5, 0x4294, 0x72f7, 0x62d6,
        0x9339, 0x8318, 0xb37b, 0xa35a, 0xd3bd, 0xc39c, 0xf3ff, 0xe3de,
        0x2462, 0x3443, 0x0420, 0x1401, 0x64e6, 0x74c7, 0x44a4, 0x5485,
        0xa56a, 0xb54b, 0x8528, 0x9509, 0xe5ee, 0xf5cf, 0xc5ac, 0xd58d,
        0x3653, 0x2672, 0x1611, 0x0630, 0x76d7, 0x66f6, 0x5695, 0x46b4,
        0xb75b, 0xa77a, 0x9719, 0x8738, 0xf7df, 0xe7fe, 0xd79d, 0xc7bc,
        0x48c4, 0x58e5, 0x6886, 0x78a7, 0x0840, 0x1861, 0x2802, 0x3823,
        0xc9cc, 0xd9ed, 0xe98e, 0xf9af, 0x8948, 0x9969, 0xa90a, 0xb92b,
        0x5af5, 0x4ad4, 0x7ab7, 0x6a96, 0x1a71, 0x0a50, 0x3a33, 0x2a12,
        0xdbfd, 0xcbdc, 0xfbbf, 0xeb9e, 0x9b79, 0x8b58, 0xbb3b, 0xab1a,
        0x6ca6, 0x7c87, 0x4ce4, 0x5cc5, 0x2c22, 0x3c03, 0x0c60, 0x1c41,
        0xedae, 0xfd8f, 0xcdec, 0xddcd, 0xad2a, 0xbd0b, 0x8d68, 0x9d49,
        0x7e97, 0x6eb6, 0x5ed5, 0x4ef4, 0x3e13, 0x2e32, 0x1e51, 0x0e70,
        0xff9f, 0xefbe, 0xdfdd, 0xcffc, 0xbf1b, 0xaf3a, 0x9f59, 0x8f78,
        0x9188, 0x81a9, 0xb1ca, 0xa1eb, 0xd10c, 0xc12d, 0xf14e, 0xe16f,
        0x1080, 0x00a1, 0x30c2, 0x20e3, 0x5004, 0x4025, 0x7046, 0x6067,
        0x83b9, 0x9398, 0xa3fb, 0xb3da, 0xc33d, 0xd31c, 0xe37f, 0xf35e,
        0x02b1, 0x1290, 0x22f3, 0x32d2, 0x4235, 0x5214, 0x6277, 0x7256,
        0xb5ea, 0xa5cb, 0x95a8, 0x8589, 0xf56e, 0xe54f, 0xd52c, 0xc50d,
        0x34e2, 0x24c3, 0x14a0, 0x0481, 0x7466, 0x6447, 0x5424, 0x4405,
        0xa7db, 0xb7fa, 0x8799, 0x97b8, 0xe75f, 0xf77e, 0xc71d, 0xd73c,
        0x26d3, 0x36f2, 0x0691, 0x16b0, 0x6657, 0x7676, 0x4615, 0x5634,
        0xd94c, 0xc96d, 0xf90e, 0xe92f, 0x99c8, 0x89e9, 0xb98a, 0xa9ab,
        0x5844, 0x4865, 0x7806, 0x6827, 0x18c0, 0x08e1, 0x3882, 0x28a3,
        0xcb7d, 0xdb5c, 0xeb3f, 0xfb1e, 0x8bf9, 0x9bd8, 0xabbb, 0xbb9a,
        0x4a75, 0x5a54, 0x6a37, 0x7a16, 0x0af1, 0x1ad0, 0x2ab3, 0x3a92,
        0xfd2e, 0xed0f, 0xdd6c, 0xcd4d, 0xbdaa, 0xad8b, 0x9de8, 0x8dc9,
        0x7c26, 0x6c07, 0x5c64, 0x4c45, 0x3ca2, 0x2c83, 0x1ce0, 0x0cc1,
        0xef1f, 0xff3e, 0xcf5d, 0xdf7c, 0xaf9b, 0xbfba, 0x8fd9, 0x9ff8,
        0x6e17, 0x7e36, 0x4e55, 0x5e74, 0x2e93, 0x3eb2, 0x0ed1, 0x1ef0
    };

    /// <summary>Computes CRC-ITU-T over a span of bytes.</summary>
    private static ushort ComputeCrcItuT(byte[] buffer, int offset, int length)
    {
        ushort crc = 0;
        for (int i = offset; i < offset + length; i++)
            crc = (ushort)((crc << 8) ^ CrcItuTTable[((crc >> 8) ^ buffer[i]) & 0xFF]);
        return crc;
    }

    /// <summary>Recomputes the UDF descriptor tag checksum (byte 4) and CRC (bytes 8-9).</summary>
    private static void RecalculateUdfTagChecksum(byte[] sector)
    {
        // CRC length at bytes 10-11 (LE16)
        ushort crcLength = (ushort)(sector[10] | (sector[11] << 8));
        ushort crc = ComputeCrcItuT(sector, 16, crcLength);
        sector[8] = (byte)(crc & 0xFF);
        sector[9] = (byte)((crc >> 8) & 0xFF);

        // Tag checksum: sum of bytes 0-15 excluding byte 4
        byte tagChecksum = 0;
        for (int i = 0; i < 16; i++)
        {
            if (i == 4) continue;
            tagChecksum += sector[i];
        }
        sector[4] = tagChecksum;
    }

    /// <summary>
    /// Minimal DVD-Video data (12 sectors = 24576 bytes) containing VIDEO_TS and AUDIO_TS
    /// UDF file entries. This tricks the PS2's MechaCon into recognizing the disc as DVD-Video.
    /// The data consists of minimal UDF File Identifier Descriptors (FIDs) for VIDEO_TS.IFO,
    /// VIDEO_TS.VOB, VIDEO_TS.BUP and AUDIO_TS directories.
    /// </summary>
    private static byte[] GenerateDvdVideoData()
    {
        // 12 sectors of 2048 bytes = 24576 bytes total
        var data = new byte[12 * 2048];

        // Sector 0: UDF File Identifier Descriptor for VIDEO_TS directory
        // Tag identifier 0x0101 (File Identifier Descriptor)
        data[0] = 0x01; data[1] = 0x01;
        // Descriptor version 2
        data[2] = 0x02; data[3] = 0x00;
        // Tag checksum placeholder (byte 4) - recalculated below
        // Descriptor Tag Serial Number (bytes 6-7)
        data[6] = 0x01; data[7] = 0x00;
        // CRC length (bytes 10-11): 38 bytes
        data[10] = 0x26; data[11] = 0x00;
        // File version number (byte 16-17)
        data[16] = 0x01; data[17] = 0x00;
        // File characteristics: directory (bit 1)
        data[18] = 0x02;
        // Identifier length (byte 19): 9 ("VIDEO_TS" + separator)
        data[19] = 0x09;
        // ICB location (bytes 20-35): point to sector 128
        data[20] = 0x80; data[21] = 0x00; data[22] = 0x00; data[23] = 0x00;
        // Partition reference (bytes 24-25)
        // ICB length
        data[28] = 0x00; data[29] = 0x08; // 2048 bytes
        // Implementation use length (bytes 36-37): 0
        // Identifier: "VIDEO_TS"
        byte[] videoTs = Encoding.ASCII.GetBytes("VIDEO_TS");
        // File identifier starts at offset 38 (after header), compression ID 8
        data[38] = 0x08;
        Array.Copy(videoTs, 0, data, 39, videoTs.Length);

        // Compute tag checksum for sector 0
        byte tc = 0;
        for (int i = 0; i < 16; i++) { if (i != 4) tc += data[i]; }
        data[4] = tc;

        // Sector 1: UDF File Identifier Descriptor for AUDIO_TS directory
        int s1 = 2048;
        data[s1] = 0x01; data[s1 + 1] = 0x01;
        data[s1 + 2] = 0x02;
        data[s1 + 6] = 0x01; data[s1 + 7] = 0x00;
        data[s1 + 10] = 0x26; data[s1 + 11] = 0x00;
        data[s1 + 16] = 0x01; data[s1 + 17] = 0x00;
        data[s1 + 18] = 0x02; // directory
        data[s1 + 19] = 0x09;
        data[s1 + 20] = 0x81; // sector 129
        data[s1 + 29] = 0x08;
        byte[] audioTs = Encoding.ASCII.GetBytes("AUDIO_TS");
        data[s1 + 38] = 0x08;
        Array.Copy(audioTs, 0, data, s1 + 39, audioTs.Length);
        tc = 0;
        for (int i = 0; i < 16; i++) { if (i != 4) tc += data[s1 + i]; }
        data[s1 + 4] = tc;

        // Sectors 2-5: VIDEO_TS.IFO minimal (empty but valid) content
        int s2 = 2 * 2048;
        // DVDVIDEO-VMG magic "DVDVIDEO-VMG"
        byte[] vmg = Encoding.ASCII.GetBytes("DVDVIDEO-VMG");
        Array.Copy(vmg, 0, data, s2, vmg.Length);

        return data;
    }

    /// <summary>
    /// Checks whether a PS2 DVD ISO image has a UDF descriptor.
    /// Returns true if "NSR" (UDF) is found at the expected offset in clusters 1-63.
    /// </summary>
    public static bool HasUdfDescriptor(string isoPath)
    {
        if (!File.Exists(isoPath)) return false;
        try
        {
            using var stream = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            const int sectorSize = 2048;
            var buf = new byte[3];
            for (int i = 1; i < 64; i++)
            {
                long offset = (long)i * sectorSize + 0x8001; // sector offset + UDF Volume Recognition Sequence offset (0x8001)
                if (offset + 3 > stream.Length) break;
                stream.Seek(offset, SeekOrigin.Begin);
                if (stream.Read(buf, 0, 3) == 3 &&
                    buf[0] == (byte)'N' && buf[1] == (byte)'S' && buf[2] == (byte)'R')
                    return true;
            }
        }
        catch { /* best-effort */ }
        return false;
    }

    /// <summary>
    /// Checks whether a PS2 DVD ISO image already has the ESR patch applied.
    /// The patch is detected by the presence of "+NSR" at sector 14 offset +25.
    /// </summary>
    public static bool IsEsrPatched(string isoPath)
    {
        if (!File.Exists(isoPath)) return false;
        try
        {
            using var stream = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            const int sectorSize = 2048;
            long checkOffset = 14L * sectorSize + 25;
            if (checkOffset + 4 > stream.Length) return false;
            stream.Seek(checkOffset, SeekOrigin.Begin);
            var buf = new byte[4];
            if (stream.Read(buf, 0, 4) != 4) return false;
            return buf[0] == (byte)'+' && buf[1] == (byte)'N' &&
                   buf[2] == (byte)'S' && buf[3] == (byte)'R';
        }
        catch { return false; }
    }

    /// <summary>
    /// Applies the ESR (Entertainment System Region) patch to a PS2 DVD ISO image.
    /// This patches the UDF descriptors to add a DVD-Video structure that tricks
    /// the PS2's MechaCon chip into allowing the disc to boot.
    /// The ISO must be a 2048-byte sector DVD image with UDF filesystem.
    /// </summary>
    /// <returns>Null on success, or an error message string.</returns>
    public static string? ApplyEsrPatch(string isoPath)
    {
        if (!File.Exists(isoPath))
            return "ISO file not found.";

        const int sectorSize = 2048;

        if (!HasUdfDescriptor(isoPath))
            return "No UDF descriptor found. The ISO must be a PS2 DVD image with UDF filesystem.";

        if (IsEsrPatched(isoPath))
            return "Cannot apply ESR patch: ISO is already patched.";

        try
        {
            using var stream = new FileStream(isoPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            // Backup sector 34 into sector 14
            var sector = new byte[sectorSize];
            stream.Seek(34L * sectorSize, SeekOrigin.Begin);
            if (stream.Read(sector, 0, sectorSize) != sectorSize)
                return "Failed to read sector 34.";
            stream.Seek(14L * sectorSize, SeekOrigin.Begin);
            stream.Write(sector, 0, sectorSize);

            // Backup sector 50 into sector 15
            stream.Seek(50L * sectorSize, SeekOrigin.Begin);
            if (stream.Read(sector, 0, sectorSize) != sectorSize)
                return "Failed to read sector 50.";
            stream.Seek(15L * sectorSize, SeekOrigin.Begin);
            stream.Write(sector, 0, sectorSize);

            // Modify sector 34: set ICB allocation descriptor to point to DVD_VIDEO at sector 128
            stream.Seek(34L * sectorSize, SeekOrigin.Begin);
            if (stream.Read(sector, 0, sectorSize) != sectorSize)
                return "Failed to re-read sector 34.";
            sector[0xBC] = 0x80; // sector 128 (LE)
            sector[0xBD] = 0x00;
            RecalculateUdfTagChecksum(sector);
            stream.Seek(34L * sectorSize, SeekOrigin.Begin);
            stream.Write(sector, 0, sectorSize);

            // Modify sector 50: same modification
            stream.Seek(50L * sectorSize, SeekOrigin.Begin);
            if (stream.Read(sector, 0, sectorSize) != sectorSize)
                return "Failed to re-read sector 50.";
            sector[0xBC] = 0x80;
            sector[0xBD] = 0x00;
            RecalculateUdfTagChecksum(sector);
            stream.Seek(50L * sectorSize, SeekOrigin.Begin);
            stream.Write(sector, 0, sectorSize);

            // Write DVD-Video data at sector 128
            byte[] dvdVideoData = GenerateDvdVideoData();
            stream.Seek(128L * sectorSize, SeekOrigin.Begin);
            stream.Write(dvdVideoData, 0, dvdVideoData.Length);

            return null; // Success
        }
        catch (Exception ex) { return $"ESR patch failed: {ex.Message}"; }
    }

    /// <summary>
    /// Removes the ESR patch from a previously patched PS2 DVD ISO image.
    /// Restores the original UDF descriptors from the backup sectors.
    /// </summary>
    /// <returns>Null on success, or an error message string.</returns>
    public static string? RemoveEsrPatch(string isoPath)
    {
        if (!File.Exists(isoPath))
            return "ISO file not found.";

        const int sectorSize = 2048;

        if (!IsEsrPatched(isoPath))
            return "ISO is not ESR-patched.";

        try
        {
            using var stream = new FileStream(isoPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var sector = new byte[sectorSize];

            // Restore sector 14 backup into sector 34
            stream.Seek(14L * sectorSize, SeekOrigin.Begin);
            if (stream.Read(sector, 0, sectorSize) != sectorSize)
                return "Failed to read backup sector 14.";
            stream.Seek(34L * sectorSize, SeekOrigin.Begin);
            stream.Write(sector, 0, sectorSize);

            // Restore sector 15 backup into sector 50
            stream.Seek(15L * sectorSize, SeekOrigin.Begin);
            if (stream.Read(sector, 0, sectorSize) != sectorSize)
                return "Failed to read backup sector 15.";
            stream.Seek(50L * sectorSize, SeekOrigin.Begin);
            stream.Write(sector, 0, sectorSize);

            // Clear backup sectors (14, 15) and DVD-Video data (sector 128+)
            Array.Clear(sector, 0, sectorSize);
            stream.Seek(14L * sectorSize, SeekOrigin.Begin);
            stream.Write(sector, 0, sectorSize);
            stream.Seek(15L * sectorSize, SeekOrigin.Begin);
            stream.Write(sector, 0, sectorSize);

            // Clear 12 sectors of DVD-Video data at sector 128
            var clearBuf = new byte[12 * sectorSize];
            stream.Seek(128L * sectorSize, SeekOrigin.Begin);
            stream.Write(clearBuf, 0, clearBuf.Length);

            return null; // Success
        }
        catch (Exception ex) { return $"ESR unpatch failed: {ex.Message}"; }
    }

    // =====================================================================
    // Master Disc Patch (PS2 CD-R / DVD-R)
    // =====================================================================

    /// <summary>PS2 region codes for the Master Disc header.</summary>
    public enum MasterDiscRegion : byte
    {
        Japan = 0x01,
        Usa = 0x02,
        Europe = 0x04,
        World = 0x07
    }

    /// <summary>
    /// EDC (Error Detection Code) lookup table for CD-ROM XA Mode 2 Form 1 sectors.
    /// CRC-32 variant per ECMA-130 §22.3.3 with polynomial 0xD8018001.
    /// </summary>
    private static readonly uint[] EdcTable = InitEdcTable();

    private static uint[] InitEdcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint edc = i;
            for (int j = 0; j < 8; j++)
                edc = (edc >> 1) ^ ((edc & 1) != 0 ? 0xD8018001u : 0);
            table[i] = edc;
        }
        return table;
    }

    /// <summary>
    /// Computes the EDC (32-bit CRC) for a range of bytes using the CD-ROM XA EDC polynomial.
    /// </summary>
    private static uint ComputeEdc(byte[] data, int offset, int length)
    {
        uint edc = 0;
        for (int i = offset; i < offset + length; i++)
            edc = (edc >> 8) ^ EdcTable[(edc ^ data[i]) & 0xFF];
        return edc;
    }

    /// <summary>
    /// Recalculates and writes the EDC field for a CD-ROM XA Mode 2 Form 1 sector.
    /// The sector buffer must be a full 2352-byte raw sector.
    /// EDC covers bytes 16..2071 (subheader + user data), written at bytes 2072..2075.
    /// </summary>
    private static void RecalculateMode2Form1Edc(byte[] sector)
    {
        // Mode 2 Form 1: EDC over subheader (4+4 bytes at offset 16) + user data (2048 bytes at offset 24)
        // = bytes 16..2071 (2056 bytes total)
        // EDC stored at offset 2072 (4 bytes, little-endian)
        uint edc = ComputeEdc(sector, 16, 2056);
        sector[2072] = (byte)(edc & 0xFF);
        sector[2073] = (byte)((edc >> 8) & 0xFF);
        sector[2074] = (byte)((edc >> 16) & 0xFF);
        sector[2075] = (byte)((edc >> 24) & 0xFF);
    }

    /// <summary>
    /// Determines the sector size of a disc image (2048 for cooked DVD/ISO, 2352 for raw CD BIN).
    /// Checks the CD-ROM sync pattern first for reliable detection before falling back
    /// to file-size alignment heuristics.
    /// </summary>
    private static int DetectSectorSize(string imagePath)
    {
        var fi = new FileInfo(imagePath);
        if (fi.Length == 0) return 0;

        // Heuristic: check for CD-ROM sync pattern at offset 0 (raw sectors)
        // This is the most reliable indicator and should be checked first to
        // avoid misdetecting DVD ISOs whose size happens to be divisible by 2352.
        try
        {
            using var stream = File.OpenRead(imagePath);
            var header = new byte[16];
            if (stream.Read(header, 0, 16) == 16)
            {
                // CD-ROM sync pattern: 00 FF FF FF FF FF FF FF FF FF FF 00
                if (header[0] == 0x00 && header[1] == 0xFF && header[2] == 0xFF &&
                    header[10] == 0xFF && header[11] == 0x00)
                    return 2352;
            }
        }
        catch { /* best-effort */ }

        // Fall back to file size alignment checks
        // Check if file size is divisible by 2048 (cooked/ISO) — preferred for DVD/BD
        if (fi.Length % 2048 == 0) return 2048;
        // Check if file size is divisible by 2352 (raw CD)
        if (fi.Length % 2352 == 0) return 2352;
        // Check if it looks like Mode 2 raw with subchannel (2448 bytes)
        if (fi.Length % 2448 == 0) return 2448;

        return 2048; // Default to cooked
    }

    /// <summary>
    /// Builds a Master Disc sector (2048 bytes) with the specified parameters.
    /// Based on the PS2 boot sector format documented by the scene community.
    /// </summary>
    private static byte[] BuildMasterDiscSector(
        string discName, string region, bool isDvd, long imageSize)
    {
        var sector = new byte[2048];

        // Disc name (32 bytes, space-padded)
        PadAsciiString(sector, 0, discName, 32);
        // Producer name (32 bytes)
        PadAsciiString(sector, 32, "OpenBurningSuite", 32);
        // Copyright holder (32 bytes)
        PadAsciiString(sector, 64, "Master Disc", 32);

        // Date in BCD-text format
        var now = DateTime.UtcNow;
        string yearStr = now.Year.ToString("D4");
        string monthStr = now.Month.ToString("D2");
        string dayStr = now.Day.ToString("D2");
        Encoding.ASCII.GetBytes(yearStr, 0, 4, sector, 96);
        Encoding.ASCII.GetBytes(monthStr, 0, 2, sector, 100);
        Encoding.ASCII.GetBytes(dayStr, 0, 2, sector, 102);

        // Master disc identifier: "PlayStation Master Disc " (24 bytes)
        PadAsciiString(sector, 104, "PlayStation Master Disc", 24);

        // PlayStation version (byte 128): 2 for PS2
        sector[128] = 0x02;

        // Region (byte 129)
        sector[129] = region.ToUpperInvariant() switch
        {
            "NTSC-J" or "JAPAN" or "J" => (byte)MasterDiscRegion.Japan,
            "NTSC-U" or "USA" or "U" => (byte)MasterDiscRegion.Usa,
            "PAL" or "EUROPE" or "E" => (byte)MasterDiscRegion.Europe,
            "WORLD" or "W" => (byte)MasterDiscRegion.World,
            _ => (byte)MasterDiscRegion.Usa // Default to USA
        };

        // Disc type (byte 131): 1=CD, 2=DVD
        sector[131] = isDvd ? (byte)0x02 : (byte)0x01;

        if (isDvd)
        {
            // DVD-specific fields at offset 132
            sector[132] = 0x01; // dvd_byte1
            sector[133] = 0x00; // dvd_byte2
            // Sector count adjusted (LE32 at offset 134)
            uint sectorCount = (uint)(imageSize / 2048);
            sector[134] = (byte)(sectorCount & 0xFF);
            sector[135] = (byte)((sectorCount >> 8) & 0xFF);
            sector[136] = (byte)((sectorCount >> 16) & 0xFF);
            sector[137] = (byte)((sectorCount >> 24) & 0xFF);
        }

        // Common fields at offset 256
        sector[256] = 0x01;
        // Fill 8 bytes of 0xFF at offset 257
        for (int i = 257; i < 265; i++) sector[i] = 0xFF;
        // Magic values at 265-269
        sector[265] = 0x4C; sector[266] = 0x49; sector[267] = 0x43; sector[268] = 0x45; // "LICE"
        sector[269] = 0x01;

        // Section bytes at 272
        sector[272] = 0x01;
        // 8 bytes of 0xFF at 273
        for (int i = 273; i < 281; i++) sector[i] = 0xFF;

        // Magic values repeat at 297-301
        sector[297] = 0x4C; sector[298] = 0x49; sector[299] = 0x43; sector[300] = 0x45;
        sector[301] = 0x01;

        // Magic3 at 317
        sector[317] = 0x01;

        // Fill spaces at offset 768
        for (int i = 768; i < 816; i++) sector[i] = 0x20;
        // CDVDGEN version text at offset 816 (16 bytes)
        PadAsciiString(sector, 816, "CDVDGEN 2.00    ", 16);
        // Fill remaining with spaces
        for (int i = 832; i < 2048; i++) sector[i] = 0x20;

        return sector;
    }

    /// <summary>Pads an ASCII string to a fixed length with spaces.</summary>
    private static void PadAsciiString(byte[] buffer, int offset, string text, int length)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        int copyLen = Math.Min(bytes.Length, length);
        Array.Copy(bytes, 0, buffer, offset, copyLen);
        for (int i = copyLen; i < length; i++)
            buffer[offset + i] = 0x20; // Space
    }

    /// <summary>
    /// Checks whether a PS2 image already has the Master Disc patch applied.
    /// Detected by "PlayStation Master Disc" at the expected offset in sectors 12-13.
    /// </summary>
    public static bool IsMasterDiscPatched(string imagePath)
    {
        if (!File.Exists(imagePath)) return false;
        try
        {
            int sectorSize = DetectSectorSize(imagePath);
            using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);

            for (int sectorNum = 12; sectorNum <= 13; sectorNum++)
            {
                int userDataOffset = sectorSize == 2352 ? 24 : 0; // Mode 2 Form 1: 24-byte header
                long offset = (long)sectorNum * sectorSize + userDataOffset + 104;
                if (offset + 23 > stream.Length) continue;

                stream.Seek(offset, SeekOrigin.Begin);
                var buf = new byte[23];
                if (stream.Read(buf, 0, 23) == 23)
                {
                    string text = Encoding.ASCII.GetString(buf);
                    if (text.Contains("PlayStation Master Disc"))
                        return true;
                }
            }
        }
        catch { /* best-effort */ }
        return false;
    }

    /// <summary>
    /// Applies the Master Disc patch to a PS2 disc image for use with MechaPwn DEX consoles.
    /// Writes the master disc header data to sectors 12 and 13 of the image.
    /// For raw CD images (2352-byte sectors), EDC is recalculated after patching.
    /// Supports both DVD (2048-byte ISO) and CD (2352-byte BIN) images.
    /// </summary>
    /// <param name="imagePath">Path to the PS2 disc image (ISO for DVD, BIN for CD).</param>
    /// <param name="region">Target region: "NTSC-J", "NTSC-U", "PAL", or "WORLD".</param>
    /// <returns>Null on success, or an error message string.</returns>
    public static string? ApplyMasterDiscPatch(string imagePath, string region = "NTSC-U")
    {
        if (!File.Exists(imagePath))
            return "Image file not found.";

        if (IsMasterDiscPatched(imagePath))
            return "Cannot apply Master Disc patch: image is already patched.";

        try
        {
            int sectorSize = DetectSectorSize(imagePath);
            bool isRaw = sectorSize == 2352;
            bool isDvd = !isRaw; // Raw = CD, Cooked = DVD

            using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            // Build two master disc sectors
            byte[] masterSector1 = BuildMasterDiscSector(
                Path.GetFileNameWithoutExtension(imagePath), region, isDvd, stream.Length);
            byte[] masterSector2 = BuildMasterDiscSector(
                Path.GetFileNameWithoutExtension(imagePath), region, isDvd, stream.Length);

            // Sectors 12 and 13 receive the master disc data
            for (int sectorNum = 12; sectorNum <= 13; sectorNum++)
            {
                byte[] masterData = sectorNum == 12 ? masterSector1 : masterSector2;

                if (isRaw)
                {
                    // Read the full raw sector
                    var rawSector = new byte[2352];
                    long rawOffset = (long)sectorNum * 2352;
                    if (rawOffset + 2352 > stream.Length)
                        return $"Image too small to contain sector {sectorNum}.";

                    stream.Seek(rawOffset, SeekOrigin.Begin);
                    if (stream.Read(rawSector, 0, 2352) != 2352)
                        return $"Failed to read sector {sectorNum}.";

                    // Write user data at offset 24 (after 12-byte sync + 4-byte header + 8-byte subheader)
                    Array.Copy(masterData, 0, rawSector, 24, 2048);

                    // Recalculate EDC for Mode 2 Form 1
                    RecalculateMode2Form1Edc(rawSector);

                    stream.Seek(rawOffset, SeekOrigin.Begin);
                    stream.Write(rawSector, 0, 2352);
                }
                else
                {
                    // Cooked 2048-byte sectors (DVD)
                    long offset = (long)sectorNum * 2048;
                    if (offset + 2048 > stream.Length)
                        return $"Image too small to contain sector {sectorNum}.";

                    stream.Seek(offset, SeekOrigin.Begin);
                    stream.Write(masterData, 0, 2048);
                }
            }

            return null; // Success
        }
        catch (Exception ex) { return $"Master Disc patch failed: {ex.Message}"; }
    }

    /// <summary>
    /// Removes the Master Disc patch by clearing sectors 12 and 13.
    /// </summary>
    public static string? RemoveMasterDiscPatch(string imagePath)
    {
        if (!File.Exists(imagePath))
            return "Image file not found.";

        if (!IsMasterDiscPatched(imagePath))
            return "Image does not have Master Disc patch applied.";

        try
        {
            int sectorSize = DetectSectorSize(imagePath);
            bool isRaw = sectorSize == 2352;

            using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            for (int sectorNum = 12; sectorNum <= 13; sectorNum++)
            {
                if (isRaw)
                {
                    var rawSector = new byte[2352];
                    long rawOffset = (long)sectorNum * 2352;
                    stream.Seek(rawOffset, SeekOrigin.Begin);
                    if (stream.Read(rawSector, 0, 2352) != 2352) continue;

                    // Clear user data (offset 24, 2048 bytes)
                    Array.Clear(rawSector, 24, 2048);
                    RecalculateMode2Form1Edc(rawSector);

                    stream.Seek(rawOffset, SeekOrigin.Begin);
                    stream.Write(rawSector, 0, 2352);
                }
                else
                {
                    long offset = (long)sectorNum * 2048;
                    stream.Seek(offset, SeekOrigin.Begin);
                    stream.Write(new byte[2048], 0, 2048);
                }
            }

            return null; // Success
        }
        catch (Exception ex) { return $"Master Disc unpatch failed: {ex.Message}"; }
    }

    // =====================================================================
    // PSX 80 Minute Patch (PS1/PS2 CD-R)
    // =====================================================================

    /// <summary>Number of dummy silence sectors to append (6 minutes at 75 sectors/second).</summary>
    private const int Psx80MinDummySectors = 27000;

    /// <summary>Maximum sector count for an unpatched PSX CD image (71 minutes).</summary>
    private const int PsxMaxSectorCount = 319500;

    /// <summary>
    /// Checks whether a BIN file has already been patched with the PSX 80-minute fix.
    /// A patched file exceeds the PSX maximum sector count.
    /// </summary>
    public static bool IsPsx80MinutePatched(string binPath)
    {
        if (!File.Exists(binPath)) return false;
        var fi = new FileInfo(binPath);
        return fi.Length / 2352 > PsxMaxSectorCount;
    }

    /// <summary>
    /// Applies the PSX 80 Minute patch to a PS1 or PS2 CD game's data track BIN file.
    /// Appends 27,000 dummy sectors (6 minutes of CDDA silence) to the end of the
    /// BIN file, ensuring compatibility with 80-minute/700MB CD-R media on early
    /// PS2 models (SCPH-10000 to SCPH-39004) that have a hardware seek bug.
    /// </summary>
    /// <param name="binPath">Path to the data track BIN file (2352-byte raw sectors).</param>
    /// <returns>Null on success, or an error message string.</returns>
    public static string? ApplyPsx80MinutePatch(string binPath)
    {
        if (!File.Exists(binPath))
            return "BIN file not found.";

        try
        {
            var fi = new FileInfo(binPath);
            if (fi.Length % 2352 != 0)
                return "File size is not aligned to 2352-byte sectors. Expected raw CD image.";

            long sectorCount = fi.Length / 2352;
            if (sectorCount > PsxMaxSectorCount)
                return "File already exceeds PSX maximum sector count — may already be patched.";

            using var stream = new FileStream(binPath, FileMode.Open, FileAccess.Write, FileShare.None);
            stream.Seek(0, SeekOrigin.End);

            // Write 27,000 zero-filled sectors (silence)
            var dummySector = new byte[2352]; // All zeros = CDDA silence
            for (int i = 0; i < Psx80MinDummySectors; i++)
                stream.Write(dummySector, 0, 2352);

            return null; // Success
        }
        catch (Exception ex) { return $"PSX 80-minute patch failed: {ex.Message}"; }
    }

    // =====================================================================
    // PSX Undither Patch (PS1 CD-R)
    // =====================================================================

    /// <summary>
    /// GPU Draw Mode dither pattern to search for in PS1 disc images.
    /// Pattern: 0x00, 0xE1, [wildcard], 0x3C, 0x00, 0x02
    /// The 3rd byte (index 2) is a wildcard — any value matches.
    /// The 6th byte (0x02) contains the dither enable flag that gets cleared to 0x00.
    /// This corresponds to the PS1 GPU command GP0(E1h) "Draw Mode Setting"
    /// where bit 9 controls dithering.
    /// </summary>
    private static readonly byte[] DitherPattern = { 0x00, 0xE1, 0x00 /* wildcard */, 0x3C, 0x00, 0x02 };

    /// <summary>
    /// Applies the PSX Undither patch to a PS1 game's data track BIN file.
    /// Scans all sectors for the GPU dither pattern and clears the dither enable bit.
    /// Recalculates EDC/ECC after modifying each sector.
    /// Works on raw 2352-byte sector BIN images only.
    /// </summary>
    /// <param name="binPath">Path to the PS1 data track BIN file (2352-byte raw sectors).</param>
    /// <returns>Patch result with success status and the number of matches found.</returns>
    public static (string? Error, int MatchCount) ApplyPsxUndither(string binPath)
    {
        if (!File.Exists(binPath))
            return ("BIN file not found.", 0);

        try
        {
            var fi = new FileInfo(binPath);
            if (fi.Length < 2352)
                return ("File too small to be a valid CD image.", 0);

            int sectorSize = 0x930; // 2352
            int headerSize = 0x18; // 24 bytes (12 sync + 4 header + 8 subheader)
            int userDataSize = 0x800; // 2048 bytes
            int totalSectors = (int)(fi.Length / sectorSize);
            int matchCount = 0;

            using var stream = new FileStream(binPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            // Read two sectors at a time to catch patterns that cross sector boundaries
            var twoSectorsBuf = new byte[sectorSize * 2];
            var userData = new byte[userDataSize * 2];

            for (int s = 0; s < totalSectors; s++)
            {
                long filePos = (long)s * sectorSize;
                stream.Seek(filePos, SeekOrigin.Begin);

                bool isLastSector = (s == totalSectors - 1);
                int readSize = isLastSector ? sectorSize : sectorSize * 2;

                if (filePos + readSize > fi.Length)
                {
                    readSize = (int)(fi.Length - filePos);
                    isLastSector = true;
                }

                int bytesRead = stream.Read(twoSectorsBuf, 0, readSize);
                if (bytesRead < sectorSize) break;

                // Extract user data from sector 1
                int searchSize;
                Array.Copy(twoSectorsBuf, headerSize, userData, 0, userDataSize);

                if (!isLastSector && bytesRead >= sectorSize * 2)
                {
                    // Extract user data from sector 2
                    Array.Copy(twoSectorsBuf, sectorSize + headerSize, userData, userDataSize, userDataSize);
                    searchSize = userDataSize * 2;
                }
                else
                {
                    searchSize = userDataSize;
                }

                bool modified = false;
                for (int i = 0; i <= searchSize - 6; i++)
                {
                    bool match = true;
                    for (int p = 0; p < 6; p++)
                    {
                        if (p == 2) continue; // 3rd byte is wildcard
                        if (DitherPattern[p] != userData[i + p])
                        {
                            match = false;
                            break;
                        }
                    }

                    if (match)
                    {
                        userData[i + 5] = 0x00; // Clear dither enable
                        matchCount++;
                        modified = true;
                    }
                }

                if (modified)
                {
                    // Write back user data to sector buffers
                    Array.Copy(userData, 0, twoSectorsBuf, headerSize, userDataSize);
                    if (!isLastSector && bytesRead >= sectorSize * 2)
                        Array.Copy(userData, userDataSize, twoSectorsBuf, sectorSize + headerSize, userDataSize);

                    // Recalculate EDC for modified sectors
                    RecalculateMode2Form1Edc(twoSectorsBuf);
                    if (!isLastSector && bytesRead >= sectorSize * 2)
                    {
                        var sector2 = new byte[sectorSize];
                        Array.Copy(twoSectorsBuf, sectorSize, sector2, 0, sectorSize);
                        RecalculateMode2Form1Edc(sector2);
                        Array.Copy(sector2, 0, twoSectorsBuf, sectorSize, sectorSize);
                    }

                    // Write back
                    stream.Seek(filePos, SeekOrigin.Begin);
                    stream.Write(twoSectorsBuf, 0, isLastSector ? sectorSize : Math.Min(bytesRead, sectorSize * 2));
                }
            }

            return (null, matchCount);
        }
        catch (Exception ex) { return ($"PSX Undither patch failed: {ex.Message}", 0); }
    }
}
