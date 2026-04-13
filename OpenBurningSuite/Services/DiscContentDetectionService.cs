// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using OpenBurningSuite.Helpers;
using OpenBurningSuite.Models;
using OpenBurningSuite.Native.Iso;
using OpenBurningSuite.Native.Optical;

namespace OpenBurningSuite.Services;

/// <summary>
/// Unified disc content detection service for CD, DVD, and Blu-ray media.
/// Detects and differentiates between video, audio, game, photo, and data content
/// from both physical optical drives and disc image files.
/// </summary>
/// <remarks>
/// Detection is based on:
/// - MMC profile codes (media type classification)
/// - TOC/track analysis (audio vs data tracks, session layout)
/// - ISO 9660 PVD fields (system identifier, volume label, application identifier)
/// - Filesystem directory structure scanning (VIDEO_TS, BDMV, PS3_GAME, etc.)
/// - Magic bytes and signature detection (Sega headers, Nintendo magic words, etc.)
/// - VCD/SVCD White Book structure detection (CD-XA, CD-BRIDGE markers)
/// - Blu-ray structure analysis (BDMV/BDAV, BD-J, 3D, UHD markers)
///
/// References:
/// - ECMA-119 (ISO 9660 filesystem)
/// - IEC 60908 (CD-DA / Red Book)
/// - ECMA-130 (CD-ROM / Yellow Book)
/// - IEC 61104 (Video CD / White Book)
/// - IEC 62107 (Super Video CD)
/// - ECMA-359 / DVD Forum specs (DVD-Video, DVD-Audio)
/// - Blu-ray Disc Association specifications (BDMV, BDAV, BD-J, UHD BD)
/// - MMC-5 / SFF-8090 (SCSI multimedia command set, profile codes)
/// </remarks>
public static class DiscContentDetectionService
{
    // -----------------------------------------------------------------------
    //  Constants
    // -----------------------------------------------------------------------
    private const int SectorSize = 2048;
    private const int PvdSector = 16;
    private const long PvdOffset = PvdSector * SectorSize;

    /// <summary>
    /// Minimum size margin above BD-50 capacity to consider a disc as UHD Blu-ray.
    /// Standard BD-50 is ~50 GB; UHD BD starts at 66 GB, so a ~16 GB margin
    /// avoids false positives from slightly oversized BD-50 images.
    /// </summary>
    private const long UhdBdSizeMarginBytes = 16_000_000_000L;

    /// <summary>Minimum expected size of a Dreamcast GD-ROM image (~500 MB).</summary>
    private const long GdRomMinSizeBytes = 500_000_000L;

    /// <summary>Tolerance above GD-ROM capacity for size-based Dreamcast detection (~200 MB).</summary>
    private const long GdRomSizeToleranceBytes = 200_000_000L;

    // -----------------------------------------------------------------------
    //  Physical drive content detection
    // -----------------------------------------------------------------------

    /// <summary>
    /// Detects disc content from a physical optical drive.
    /// Uses SCSI/MMC commands to read TOC, track info, sector data, and disc structures.
    /// </summary>
    /// <param name="drive">An open <see cref="OpticalDrive"/> instance.</param>
    /// <returns>Comprehensive content information.</returns>
    public static DiscContentInfo DetectFromDrive(OpticalDrive drive)
    {
        var info = new DiscContentInfo();

        try
        {
            var profile = drive.GetCurrentProfile();
            info.ProfileCode = profile;
            info.MediaType = OpticalDrive.ProfileToMediaType(profile);

            if (profile == 0)
            {
                info.ContentLabel = "No media";
                return info;
            }

            // Read disc structure information
            ReadDiscAndTrackInfo(drive, info);

            // Determine media category and apply appropriate detection
            if (profile is >= 0x0008 and <= 0x000A) // CD profiles
            {
                DetectCdContentFromDrive(drive, info);
            }
            else if (profile is >= 0x0010 and <= 0x002B) // DVD profiles
            {
                DetectDvdContentFromDrive(drive, info);
            }
            else if (profile is >= 0x0040 and <= 0x004D) // BD profiles
            {
                DetectBluRayContentFromDrive(drive, info);
            }

            // If nothing specific detected, classify as generic data
            if (info.ContentType == DiscContentType.Unknown && info.TrackCount > 0)
            {
                info.ContentType = DiscContentType.Data;
                info.ContentLabel = "💿 Data Disc";
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[DiscContentDetection] Drive detection error: {ex.Message}");
        }

        return info;
    }

    // -----------------------------------------------------------------------
    //  Image file content detection
    // -----------------------------------------------------------------------

    /// <summary>
    /// Detects disc content from a disc image file (ISO, BIN, IMG, etc.).
    /// </summary>
    /// <param name="imagePath">Path to the disc image file.</param>
    /// <returns>Comprehensive content information.</returns>
    public static DiscContentInfo DetectFromImage(string imagePath)
    {
        var info = new DiscContentInfo();
        if (!File.Exists(imagePath)) return info;

        try
        {
            var ext = Path.GetExtension(imagePath).ToLowerInvariant();
            var fileSize = new FileInfo(imagePath).Length;

            // Resolve CUE to BIN
            if (ext == ".cue")
            {
                var binFiles = Native.Optical.BurnEngine.GetCueBinFiles(imagePath);
                if (binFiles.Count > 0 && File.Exists(binFiles[0]))
                    return DetectFromImage(binFiles[0]);
                var companionBin = Path.ChangeExtension(imagePath, ".bin");
                if (File.Exists(companionBin))
                    return DetectFromImage(companionBin);
                return info;
            }

            using var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, false);
            if (stream.Length == 0) return info;

            info.CapacityBytes = stream.Length;

            // Classify by size heuristic to determine likely media type
            if (stream.Length > FormatHelper.BD25Capacity)
                info.ProfileCode = 0x0040; // BD-ROM
            else if (stream.Length > FormatHelper.CdImageSizeThresholdBytes)
                info.ProfileCode = 0x0010; // DVD-ROM
            else
                info.ProfileCode = 0x0008; // CD-ROM

            // Try ISO 9660 PVD at cooked offset first
            var pvd = TryReadPvd(stream, PvdOffset);
            bool isRaw = false;

            // Try raw 2352-byte sector offset if cooked didn't work
            if (pvd == null)
            {
                const int rawSectorSize = 2352;
                const int rawUserDataOffset = 16;
                long rawPvdOffset = (long)PvdSector * rawSectorSize + rawUserDataOffset;
                pvd = TryReadPvd(stream, rawPvdOffset);
                if (pvd != null) isRaw = true;
            }

            if (pvd != null)
            {
                ParsePvdFields(pvd, info);
                ReadRootDirectoryFromPvd(stream, pvd, info, isRaw);

                // Try image-based classification from PVD and directories
                ClassifyFromPvdAndDirectories(info, stream.Length, isRaw);
            }

            // If still unknown, try non-PVD detection methods
            if (info.ContentType == DiscContentType.Unknown)
            {
                DetectFromMagicBytes(stream, info);
            }

            // PS3 encryption detection: read the region map from sector 0
            // to determine if the ISO contains encrypted regions.
            if (info.ContentType == DiscContentType.PlayStation3Game)
            {
                try
                {
                    var bdInfo = Ps3DiscService.DetectContentFromStream(stream);
                    info.IsEncrypted = bdInfo.IsEncrypted;
                    info.BluRayInfo = bdInfo;
                    if (!string.IsNullOrEmpty(bdInfo.Title))
                        info.GameTitle = bdInfo.Title;
                    if (!string.IsNullOrEmpty(bdInfo.TitleId))
                        info.GameTitleId = bdInfo.TitleId;
                }
                catch { /* PS3 encryption detection is best-effort */ }
            }

            // CDI format: check for Dreamcast
            if (info.ContentType == DiscContentType.Unknown &&
                ext == ".cdi")
            {
                DetectDreamcastFromCdi(imagePath, stream, info);
            }

            // VCD/SVCD detection for image files
            if (info.ContentType == DiscContentType.Unknown)
            {
                var vcdType = VcdBuilder.DetectVcdSvcd(imagePath);
                if (vcdType != null)
                {
                    if (vcdType == "SVCD")
                    {
                        info.ContentType = DiscContentType.SuperVideoCd;
                        info.ContentLabel = "📀 Super Video CD (SVCD)";
                    }
                    else
                    {
                        info.ContentType = DiscContentType.VideoCd;
                        info.ContentLabel = "📀 Video CD (VCD)";
                    }
                }
            }

            // Fallback
            if (info.ContentType == DiscContentType.Unknown && stream.Length > 0)
            {
                info.ContentType = DiscContentType.Data;
                info.ContentLabel = "💿 Data Disc";
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[DiscContentDetection] Image detection error: {ex.Message}");
        }

        return info;
    }

    // -----------------------------------------------------------------------
    //  CD Content Detection (from physical drive)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Detects CD content type from a physical drive using TOC analysis,
    /// track classification, PVD inspection, and directory structure scanning.
    /// </summary>
    private static void DetectCdContentFromDrive(OpticalDrive drive, DiscContentInfo info)
    {
        // Step 1: Analyze TOC for track types
        var toc = drive.ReadToc();
        if (toc != null)
        {
            AnalyzeTocForCdType(toc, info);

            // If pure audio CD detected from TOC, we're done
            if (info.ContentType == DiscContentType.AudioCd)
                return;

            // Enhanced CD (CD Extra) already classified — no further analysis needed
            if (info.ContentType == DiscContentType.EnhancedCd)
                return;

            // For Mixed Mode CDs, continue to PVD analysis — the data track may
            // contain a gaming system identifier (PS1, Saturn, Dreamcast, etc.)
            // that overrides the generic "Mixed Mode CD" classification.
        }

        // Step 2: If data tracks exist, read PVD from sector 16
        if (info.DataTrackCount > 0 || info.ContentType == DiscContentType.Unknown)
        {
            var (pvdResult, pvdData) = drive.ReadSectors(PvdSector, 1, SectorSize);
            if (pvdResult.Success && pvdResult.DataTransferred >= SectorSize &&
                IsValidPvd(pvdData))
            {
                ParsePvdFields(pvdData, info);
                ReadRootDirectoryFromDrive(drive, pvdData, info);

                // Classify from PVD + directories
                DetectCdContentFromPvdAndDirs(info);
            }
        }

        // Step 3: If still unknown data CD, just label it
        if (info.ContentType == DiscContentType.Unknown && info.DataTrackCount > 0)
        {
            info.ContentType = DiscContentType.DataCd;
            info.ContentLabel = "💿 Data CD";
        }
    }

    /// <summary>
    /// Analyzes TOC entries to classify CD content based on track layout.
    /// </summary>
    private static void AnalyzeTocForCdType(TocData toc, DiscContentInfo info)
    {
        int audioTracks = 0;
        int dataTracks = 0;
        bool firstTrackIsData = false;
        var sessionTracks = new Dictionary<byte, List<TocEntry>>();

        foreach (var entry in toc.Entries)
        {
            // Skip lead-out entries (track 0xAA)
            if (entry.TrackNumber == 0xAA) continue;

            if (entry.IsAudio)
                audioTracks++;
            else
                dataTracks++;

            if (entry.TrackNumber == toc.FirstTrack)
                firstTrackIsData = entry.IsData;

            if (!sessionTracks.ContainsKey(entry.SessionNumber))
                sessionTracks[entry.SessionNumber] = new List<TocEntry>();
            sessionTracks[entry.SessionNumber].Add(entry);
        }

        info.AudioTrackCount = audioTracks;
        info.DataTrackCount = dataTracks;
        info.TrackCount = audioTracks + dataTracks;
        info.SessionCount = sessionTracks.Count > 0 ? sessionTracks.Count : 1;

        // Classification based on track layout:
        if (audioTracks > 0 && dataTracks == 0)
        {
            // Pure audio CD (Red Book / CD-DA per IEC 60908)
            info.ContentType = DiscContentType.AudioCd;
            info.ContentLabel = "🎵 Audio CD";
        }
        else if (dataTracks > 0 && audioTracks > 0)
        {
            // Check for Enhanced CD (Blue Book / CD Extra):
            // Session 1 = audio tracks, Session 2 = data track
            if (sessionTracks.Count >= 2)
            {
                var session1 = sessionTracks.Values.First();
                bool session1AllAudio = session1.All(t => t.IsAudio || t.TrackNumber == 0xAA);
                if (session1AllAudio)
                {
                    info.ContentType = DiscContentType.EnhancedCd;
                    info.ContentLabel = "🎵 Enhanced CD (CD Extra)";
                    return;
                }
            }

            // Mixed Mode CD: first track is data, remaining are audio
            if (firstTrackIsData)
            {
                info.ContentType = DiscContentType.MixedModeCd;
                info.ContentLabel = "🎵 Mixed Mode CD";
            }
            else
            {
                // Audio tracks first, then data — unusual but classify as enhanced
                info.ContentType = DiscContentType.EnhancedCd;
                info.ContentLabel = "🎵 Enhanced CD (CD Extra)";
            }
        }
        // dataTracks > 0 && audioTracks == 0: data-only CD, needs further analysis
    }

    /// <summary>
    /// Classifies CD content from PVD and directory structure on a data CD.
    /// </summary>
    private static void DetectCdContentFromPvdAndDirs(DiscContentInfo info)
    {
        var dirs = info.RootDirectories;
        var files = info.RootFiles;
        var sysId = info.SystemIdentifier.ToUpperInvariant();
        var volId = info.VolumeLabel.ToUpperInvariant();

        // ---- Game disc detection (CD) ----
        if (sysId.Contains("PLAYSTATION"))
        {
            info.ContentType = DiscContentType.PlayStation1Game;
            info.ContentLabel = "🎮 PlayStation 1 Game";
            info.GamingSystem = "PlayStation 1";
            return;
        }

        if (sysId.Contains("CD-RTOS") || sysId.Contains("CD-I"))
        {
            // Check if it's a game or generic CD-i
            // CD-i games typically have specific structures
            if (HasAnyDirectory(dirs, "VIDEO", "GAME", "APP"))
            {
                info.ContentType = DiscContentType.CdInteractiveGame;
                info.ContentLabel = "🎮 CD-i Game";
            }
            else
            {
                info.ContentType = DiscContentType.CdInteractive;
                info.ContentLabel = "📀 CD-i Disc";
            }
            return;
        }

        if (sysId.Contains("SEGA"))
        {
            if (sysId.Contains("SEGASATURN"))
            {
                info.ContentType = DiscContentType.SegaSaturnGame;
                info.ContentLabel = "🎮 Sega Saturn Game";
                info.GamingSystem = "Sega Saturn";
            }
            else if (sysId.Contains("SEGAKATANA") || sysId.Contains("DREAMCAST"))
            {
                info.ContentType = DiscContentType.SegaDreamcastGame;
                info.ContentLabel = "🎮 Sega Dreamcast Game";
                info.GamingSystem = "Sega Dreamcast";
            }
            else if (sysId.Contains("MEGA DRIVE") || sysId.Contains("SEGADISCSYSTEM"))
            {
                info.ContentType = DiscContentType.SegaMegaCdGame;
                info.ContentLabel = "🎮 Sega Mega CD Game";
                info.GamingSystem = "Sega Mega CD";
            }
            else
            {
                info.ContentType = DiscContentType.SegaSaturnGame;
                info.ContentLabel = "🎮 Sega Game";
                info.GamingSystem = "Sega Saturn";
            }
            return;
        }

        if (sysId.Contains("CDTV"))
        {
            info.ContentType = DiscContentType.AmigaCdtvGame;
            info.ContentLabel = "🎮 Amiga CDTV Game";
            info.GamingSystem = "Amiga CDTV";
            return;
        }

        if (sysId.Contains("AMIGA"))
        {
            info.ContentType = DiscContentType.AmigaCd32Game;
            info.ContentLabel = "🎮 Amiga CD32 Game";
            info.GamingSystem = "Amiga CD32";
            return;
        }

        // ---- VCD / SVCD detection ----
        // Per White Book: VCD uses CD-ROM XA (system id "CD-RTOS CD-BRIDGE")
        // with VCD/SVCD directory structures
        if (HasAnyDirectory(dirs, "VCD") || HasAnyFile(files, "INFO.VCD"))
        {
            info.ContentType = DiscContentType.VideoCd;
            info.ContentLabel = "📀 Video CD (VCD)";
            return;
        }

        if (HasAnyDirectory(dirs, "SVCD") || HasAnyFile(files, "INFO.SVD"))
        {
            info.ContentType = DiscContentType.SuperVideoCd;
            info.ContentLabel = "📀 Super Video CD (SVCD)";
            return;
        }

        // ---- Photo CD detection ----
        // Kodak Photo CD uses "PHOTO_CD" directory and INFO.PCD file
        if (HasAnyDirectory(dirs, "PHOTO_CD") || HasAnyFile(files, "INFO.PCD"))
        {
            info.ContentType = DiscContentType.PhotoCd;
            info.ContentLabel = "📷 Photo CD";
            return;
        }

        // DCIM-based photo disc (digital camera photos on CD)
        if (HasAnyDirectory(dirs, "DCIM"))
        {
            info.ContentType = DiscContentType.PhotoCd;
            info.ContentLabel = "📷 Photo CD";
            return;
        }

        // ---- Video structures on CD ----
        // Some CDs contain VIDEO_TS (mini DVD-Video on CD)
        if (HasAnyDirectory(dirs, "VIDEO_TS"))
        {
            info.ContentType = DiscContentType.DvdVideo;
            info.ContentLabel = "🎬 DVD-Video (on CD)";
            return;
        }

        // ---- Default: Data CD ----
        if (info.ContentType == DiscContentType.Unknown)
        {
            info.ContentType = DiscContentType.DataCd;
            info.ContentLabel = "💿 Data CD";
        }
    }

    // -----------------------------------------------------------------------
    //  DVD Content Detection (from physical drive)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Detects DVD content type from a physical drive.
    /// </summary>
    private static void DetectDvdContentFromDrive(OpticalDrive drive, DiscContentInfo info)
    {
        // Read PVD from sector 16
        var (pvdResult, pvdData) = drive.ReadSectors(PvdSector, 1, SectorSize);
        if (pvdResult.Success && pvdResult.DataTransferred >= SectorSize &&
            IsValidPvd(pvdData))
        {
            ParsePvdFields(pvdData, info);
            ReadRootDirectoryFromDrive(drive, pvdData, info);

            // Classify from PVD + directories
            DetectDvdContentFromPvdAndDirs(info, drive);
        }

        // Read layer information
        ReadDvdLayerInfo(drive, info);

        // Check for CSS content protection
        DetectDvdCss(drive, info);

        // Fallback
        if (info.ContentType == DiscContentType.Unknown)
        {
            info.ContentType = DiscContentType.DataDvd;
            info.ContentLabel = "💿 Data DVD";
        }
    }

    /// <summary>
    /// Classifies DVD content from PVD and directory structure.
    /// </summary>
    private static void DetectDvdContentFromPvdAndDirs(DiscContentInfo info, OpticalDrive? drive = null)
    {
        var dirs = info.RootDirectories;
        var files = info.RootFiles;
        var sysId = info.SystemIdentifier.ToUpperInvariant();
        var appId = info.ApplicationIdentifier.ToUpperInvariant();
        var volId = info.VolumeLabel.ToUpperInvariant();

        // ---- Game disc detection (DVD) ----
        if (sysId.Contains("PLAYSTATION"))
        {
            if (appId.Contains("PS2") || appId.Contains("PLAYSTATION 2") ||
                sysId.Contains("PLAYSTATION 2"))
            {
                info.ContentType = DiscContentType.PlayStation2Game;
                info.ContentLabel = "🎮 PlayStation 2 Game";
                info.GamingSystem = "PlayStation 2";
                return;
            }

            // PS3 on DVD is rare but possible (some PS3 discs used DVD format)
            if (appId.Contains("PS3"))
            {
                info.ContentType = DiscContentType.PlayStation3Game;
                info.ContentLabel = "🎮 PlayStation 3 Game";
                info.GamingSystem = "PlayStation 3";
                return;
            }

            // Default PlayStation DVD to PS2
            info.ContentType = DiscContentType.PlayStation2Game;
            info.ContentLabel = "🎮 PlayStation 2 Game";
            info.GamingSystem = "PlayStation 2";
            return;
        }

        if (sysId.Contains("PSP GAME"))
        {
            info.ContentType = DiscContentType.PspUmdGame;
            info.ContentLabel = "🎮 PSP UMD Game";
            info.GamingSystem = "PSP (UMD)";
            return;
        }

        if (sysId.Contains("XBOX") || appId.Contains("XBOX"))
        {
            if (info.CapacityBytes > FormatHelper.DVD5Capacity)
            {
                info.ContentType = DiscContentType.Xbox360Game;
                info.ContentLabel = "🎮 Xbox 360 Game";
                info.GamingSystem = "Xbox 360";
            }
            else
            {
                info.ContentType = DiscContentType.XboxGame;
                info.ContentLabel = "🎮 Xbox Game";
                info.GamingSystem = "Xbox";
            }
            return;
        }

        // Nintendo Wii/GameCube are detected via magic bytes, but also check PVD
        if (HasAnyDirectory(dirs, "DATA", "UPDATE") && volId.Contains("WII"))
        {
            info.ContentType = DiscContentType.WiiGame;
            info.ContentLabel = "🎮 Nintendo Wii Game";
            info.GamingSystem = "Wii";
            return;
        }

        // ---- DVD-Video detection ----
        // Per DVD Forum spec: DVD-Video discs contain VIDEO_TS directory with
        // VIDEO_TS.IFO, VTS_xx_x.IFO, VTS_xx_x.VOB files
        if (HasAnyDirectory(dirs, "VIDEO_TS"))
        {
            info.ContentType = DiscContentType.DvdVideo;
            info.ContentLabel = "🎬 DVD-Video";

            // Check for DVD-Audio combo disc (has both VIDEO_TS and AUDIO_TS)
            if (HasAnyDirectory(dirs, "AUDIO_TS"))
            {
                // Universal disc with both audio and video — classify as DVD-Video (primary)
                info.ContentLabel = "🎬 DVD-Video + DVD-Audio";
            }
            return;
        }

        // ---- DVD-Audio detection ----
        // Per DVD Forum spec: DVD-Audio discs contain AUDIO_TS directory with
        // AUDIO_TS.IFO, ATS_xx_x.IFO, ATS_xx_x.AOB files
        if (HasAnyDirectory(dirs, "AUDIO_TS"))
        {
            info.ContentType = DiscContentType.DvdAudio;
            info.ContentLabel = "🎵 DVD-Audio";
            return;
        }

        // ---- DVD-VR (Video Recording) detection ----
        // DVD-VR uses DVD_RTAV directory with VR_MANGR.IFO and VR_MOVIE.VRO
        if (HasAnyDirectory(dirs, "DVD_RTAV"))
        {
            info.ContentType = DiscContentType.DvdVideoRecording;
            info.ContentLabel = "🎬 DVD-VR (Video Recording)";
            return;
        }

        // ---- Photo DVD detection ----
        // DCIM directory standard (Digital Camera Images) is the reliable indicator
        if (HasAnyDirectory(dirs, "DCIM"))
        {
            info.ContentType = DiscContentType.PhotoDvd;
            info.ContentLabel = "📷 Photo DVD";
            return;
        }

        // ---- Default: Data DVD ----
        if (info.ContentType == DiscContentType.Unknown)
        {
            info.ContentType = DiscContentType.DataDvd;
            info.ContentLabel = "💿 Data DVD";
        }
    }

    /// <summary>
    /// Reads DVD layer information (number of layers, total capacity).
    /// </summary>
    private static void ReadDvdLayerInfo(OpticalDrive drive, DiscContentInfo info)
    {
        try
        {
            var capacity = drive.ReadCapacity();
            if (capacity.HasValue)
            {
                info.CapacityBytes = ((long)capacity.Value.LastLba + 1) * capacity.Value.BlockLength;
                var sizeGb = info.CapacityBytes / (1024.0 * 1024.0 * 1024.0);
                info.LayerCount = sizeGb > 8.0 ? 2 : 1;
            }
        }
        catch { /* layer info is optional */ }
    }

    /// <summary>
    /// Detects CSS (Content Scramble System) protection on DVD media.
    /// Uses READ DISC STRUCTURE with format 0x01 (Copyright Information).
    /// </summary>
    private static void DetectDvdCss(OpticalDrive drive, DiscContentInfo info)
    {
        try
        {
            // READ DISC STRUCTURE format 0x01 = DVD Copyright Information
            // Per MMC-5: returns copyright protection type and region management info
            var (result, data) = drive.ReadDiscStructure(0, 0, 0x01, 8);
            if (result.Success && result.DataTransferred >= 8)
            {
                // Byte 4: Copyright Protection System Type
                //   0x00 = No protection
                //   0x01 = CSS/CPPM
                //   0x02 = CPRM
                byte protectionType = data[4];
                info.HasCssProtection = protectionType != 0;

                // Byte 5: Region Management Information (bitmask)
                //   Bits 0-7 represent regions 1-8
                //   A set bit means the region is ALLOWED
                //   0xFF (all bits set) = region-free
                //   0x00 = region-free (some drives report this)
                info.DvdRegionMask = data[5];
            }
        }
        catch { /* CSS detection is best-effort */ }
    }

    // -----------------------------------------------------------------------
    //  Blu-ray Content Detection (from physical drive)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Detects Blu-ray content type from a physical drive.
    /// Reads PVD and directory structures to classify as movie, audio, game, data, etc.
    /// </summary>
    private static void DetectBluRayContentFromDrive(OpticalDrive drive, DiscContentInfo info)
    {
        // Read PVD from sector 16
        var (pvdResult, pvdData) = drive.ReadSectors(PvdSector, 1, SectorSize);
        if (pvdResult.Success && pvdResult.DataTransferred >= SectorSize &&
            IsValidPvd(pvdData))
        {
            ParsePvdFields(pvdData, info);
            ReadRootDirectoryFromDrive(drive, pvdData, info);
            DetectBluRayContentFromPvdAndDirs(info, drive);
        }

        // Read BD layer information
        ReadBluRayLayerInfo(drive, info);

        // Fallback
        if (info.ContentType == DiscContentType.Unknown)
        {
            info.ContentType = DiscContentType.DataBluRay;
            info.ContentLabel = "💿 Data Blu-ray";
        }
    }

    /// <summary>
    /// Classifies Blu-ray content from PVD and directory structure.
    /// Provides comprehensive differentiation between all BD content types.
    /// </summary>
    private static void DetectBluRayContentFromPvdAndDirs(DiscContentInfo info, OpticalDrive? drive = null)
    {
        var dirs = info.RootDirectories;
        var files = info.RootFiles;
        var sysId = info.SystemIdentifier.ToUpperInvariant();
        var appId = info.ApplicationIdentifier.ToUpperInvariant();

        // ---- PlayStation game detection ----
        if (HasAnyDirectory(dirs, "PS3_GAME") ||
            HasAnyFile(files, "PS3_DISC.SFB"))
        {
            info.ContentType = DiscContentType.PlayStation3Game;
            info.ContentLabel = "🎮 PlayStation 3 Game";
            info.GamingSystem = "PlayStation 3";

            // Try to read PS3 metadata
            if (drive != null) TryReadPs3Metadata(drive, info);
            return;
        }

        // PS4 detection: app0/app1 directories + bd directory
        if ((HasAnyDirectory(dirs, "app0", "app1") || HasAnyDirectory(dirs, "app")) &&
            HasAnyDirectory(dirs, "bd"))
        {
            // Differentiate PS4 vs PS5 by size and directory structure
            if (info.CapacityBytes > FormatHelper.BD50Capacity ||
                HasAnyDirectory(dirs, "uds"))
            {
                info.ContentType = DiscContentType.PlayStation5Game;
                info.ContentLabel = "🎮 PlayStation 5 Game";
                info.GamingSystem = "PlayStation 5";
            }
            else
            {
                info.ContentType = DiscContentType.PlayStation4Game;
                info.ContentLabel = "🎮 PlayStation 4 Game";
                info.GamingSystem = "PlayStation 4";
            }
            return;
        }

        // PlayStation detection via system identifier fallback
        if (sysId.Contains("PLAYSTATION"))
        {
            if (appId.Contains("PS5") || info.CapacityBytes > FormatHelper.BD100Capacity)
            {
                info.ContentType = DiscContentType.PlayStation5Game;
                info.ContentLabel = "🎮 PlayStation 5 Game";
                info.GamingSystem = "PlayStation 5";
            }
            else if (appId.Contains("PS4") || info.CapacityBytes > FormatHelper.BD50Capacity)
            {
                info.ContentType = DiscContentType.PlayStation4Game;
                info.ContentLabel = "🎮 PlayStation 4 Game";
                info.GamingSystem = "PlayStation 4";
            }
            else
            {
                info.ContentType = DiscContentType.PlayStation3Game;
                info.ContentLabel = "🎮 PlayStation 3 Game";
                info.GamingSystem = "PlayStation 3";
            }
            return;
        }

        // ---- Xbox game detection (Blu-ray based) ----
        if (sysId.Contains("XBOX") || appId.Contains("XBOX"))
        {
            if (appId.Contains("SERIES") || info.CapacityBytes > FormatHelper.BD50Capacity)
            {
                info.ContentType = DiscContentType.XboxSeriesGame;
                info.ContentLabel = "🎮 Xbox Series X|S Game";
                info.GamingSystem = "Xbox Series X|S";
            }
            else
            {
                info.ContentType = DiscContentType.XboxOneGame;
                info.ContentLabel = "🎮 Xbox One Game";
                info.GamingSystem = "Xbox One";
            }
            return;
        }

        // ---- BDMV (Blu-ray Disc Movie) detection ----
        // Per BDA specification: BDMV directory contains index.bdmv, MovieObject.bdmv,
        // PLAYLIST/*.mpls, CLIPINF/*.clpi, STREAM/*.m2ts
        if (HasAnyDirectory(dirs, "BDMV"))
        {
            // Determine BD sub-type by examining BDMV structure
            ClassifyBdmvContent(info, drive);
            return;
        }

        // ---- BDAV (Blu-ray Disc Audio/Visual Recording) detection ----
        // BDAV uses the same BDMV-like structure but is authored differently
        // Key indicator: BDAV directory instead of or alongside BDMV
        if (HasAnyDirectory(dirs, "BDAV"))
        {
            info.ContentType = DiscContentType.BluRayAudioVisual;
            info.ContentLabel = "🎬 Blu-ray BDAV Recording";
            return;
        }

        // ---- AVCHD detection (consumer camcorder format on BD) ----
        // AVCHD uses a similar structure to BDMV but in a different layout
        if (HasAnyDirectory(dirs, "AVCHD") || HasAnyDirectory(dirs, "PRIVATE"))
        {
            info.ContentType = DiscContentType.BluRayAudioVisual;
            info.ContentLabel = "🎬 AVCHD Recording";
            return;
        }

        // ---- Photo Blu-ray detection ----
        // DCIM is the reliable standard photo directory indicator
        if (HasAnyDirectory(dirs, "DCIM"))
        {
            info.ContentType = DiscContentType.PhotoBluRay;
            info.ContentLabel = "📷 Photo Blu-ray";
            return;
        }

        // ---- Default: Data Blu-ray ----
        if (info.ContentType == DiscContentType.Unknown)
        {
            info.ContentType = DiscContentType.DataBluRay;
            info.ContentLabel = "💿 Data Blu-ray";
        }
    }

    /// <summary>
    /// Classifies a BDMV disc into its specific sub-type:
    /// BD Movie, BD Audio, BD-J (Java), BD 3D, or UHD BD.
    /// </summary>
    /// <remarks>
    /// BDMV sub-type detection strategy:
    /// 1. Read index.bdmv to determine version (0100/0200=HD, 0300=UHD)
    /// 2. Check for BD-J content (BDJO directory with .bdjo files, JAR directory)
    /// 3. Check for 3D content (SSIF directory for stereoscopic streams)
    /// 4. Check for BD-Audio (audio-primary playlists)
    /// 5. Default to standard BD Movie
    ///
    /// References:
    /// - Blu-ray Disc Association: System Description Blu-ray Disc Read-Only Format
    /// - Blu-ray Disc Association: System Description Blu-ray Disc Audio Visual Basic Format
    /// </remarks>
    private static void ClassifyBdmvContent(DiscContentInfo info, OpticalDrive? drive)
    {
        var dirs = info.RootDirectories;
        var files = info.RootFiles;

        // Default to BD Movie
        info.ContentType = DiscContentType.BluRayMovie;
        info.ContentLabel = "🎬 Blu-ray Movie";

        // ---- UHD Blu-ray detection ----
        // UHD BD uses MPLS version "0300" and typically has very large capacity (66/100 GB)
        // Detection requires either BDXL profile, size exceeding BD-50 + margin, or
        // confirmation from the index.bdmv version field
        if (info.CapacityBytes > FormatHelper.BD50Capacity ||
            info.ProfileCode >= 0x004A) // BDXL profiles
        {
            // Try to confirm via index.bdmv version if accessible
            if (drive != null)
            {
                if (TryDetectUhdFromBdmv(drive))
                {
                    info.ContentType = DiscContentType.UhdBluRay;
                    info.ContentLabel = "🎬 UHD Blu-ray (4K)";
                    return;
                }
            }

            // Size-based UHD detection: standard BD is max 50GB, UHD starts at 66GB
            if (info.CapacityBytes > FormatHelper.BD50Capacity + UhdBdSizeMarginBytes)
            {
                info.ContentType = DiscContentType.UhdBluRay;
                info.ContentLabel = "🎬 UHD Blu-ray (4K)";
                return;
            }
        }

        // ---- BD 3D detection ----
        // Stereoscopic 3D Blu-ray discs contain an SSIF directory (Stereoscopic Interleaved File)
        // within the BDMV/STREAM directory, containing .ssif files
        if (HasAnyDirectory(dirs, "SSIF"))
        {
            info.ContentType = DiscContentType.BluRay3D;
            info.ContentLabel = "🎬 Blu-ray 3D";
            return;
        }

        // ---- BD-J (Blu-ray Disc Java) detection ----
        // BD-J discs contain a BDJO directory with .bdjo files and optionally a JAR directory
        // BD-J can coexist with movie content; classify as BD-J only if it's the primary content
        if (HasAnyDirectory(dirs, "BDJO") || HasAnyDirectory(dirs, "JAR"))
        {
            // BD-J with movie is still a BD Movie (BD-J provides interactivity)
            // Only classify as pure BD-J if there are no playlists/streams
            info.ContentLabel = "🎬 Blu-ray Movie (BD-J Interactive)";
            return;
        }

        // ---- BD-Audio detection ----
        // BD-Audio discs use BDMV structure but contain primarily high-resolution audio
        // Indicator: presence of audio-only streams without video, or AUDIO directory
        if (HasAnyDirectory(dirs, "AUDIO_TS"))
        {
            info.ContentType = DiscContentType.BluRayAudio;
            info.ContentLabel = "🎵 Blu-ray Audio";
            return;
        }

        // Standard BD Movie (already set as default)
    }

    /// <summary>
    /// Attempts to detect UHD Blu-ray by reading the BDMV index.bdmv file
    /// and checking the version field.
    /// </summary>
    /// <remarks>
    /// index.bdmv format (first 8 bytes):
    /// - Bytes 0-3: "INDX" magic
    /// - Bytes 4-7: Version string "0200" (HD) or "0300" (UHD)
    /// The file resides in the BDMV directory.
    /// </remarks>
    private static bool TryDetectUhdFromBdmv(OpticalDrive drive)
    {
        try
        {
            // Read sector 16 (PVD) to find BDMV directory, then look for index.bdmv
            // This is a simplified approach — scan a range of sectors for the INDX marker
            // BDMV/index.bdmv is typically found within the first few hundred sectors
            // We look for the "INDX" signature followed by version
            for (uint sector = 17; sector < 500; sector++)
            {
                var (result, data) = drive.ReadSectors(sector, 1, SectorSize);
                if (!result.Success || result.DataTransferred < 8) continue;

                // Check for "INDX" magic
                if (data[0] == (byte)'I' && data[1] == (byte)'N' &&
                    data[2] == (byte)'D' && data[3] == (byte)'X')
                {
                    var version = Encoding.ASCII.GetString(data, 4, 4);
                    // Version "0300" indicates UHD BD
                    if (version == "0300")
                        return true;
                    break; // Found index.bdmv but it's not UHD
                }
            }
        }
        catch { /* best-effort */ }

        return false;
    }

    /// <summary>
    /// Reads Blu-ray layer information (number of layers, total capacity).
    /// </summary>
    private static void ReadBluRayLayerInfo(OpticalDrive drive, DiscContentInfo info)
    {
        try
        {
            var capacity = drive.ReadCapacity();
            if (capacity.HasValue)
            {
                info.CapacityBytes = ((long)capacity.Value.LastLba + 1) * capacity.Value.BlockLength;
                var sizeGb = info.CapacityBytes / (1024.0 * 1024.0 * 1024.0);
                // BD layer estimation:
                // Single-layer: up to ~25 GB
                // Dual-layer: up to ~50 GB
                // Triple-layer (BDXL): up to ~100 GB
                // Quad-layer (BDXL): up to ~128 GB
                if (sizeGb > 100.0)
                    info.LayerCount = 4;
                else if (sizeGb > 50.0)
                    info.LayerCount = 3;
                else if (sizeGb > 25.0)
                    info.LayerCount = 2;
                else
                    info.LayerCount = 1;
            }
        }
        catch { /* layer info is optional */ }
    }

    /// <summary>
    /// Attempts to read PS3 metadata (PARAM.SFO title, ID) from the disc
    /// using the existing Ps3DiscService infrastructure.
    /// </summary>
    private static void TryReadPs3Metadata(OpticalDrive drive, DiscContentInfo info)
    {
        try
        {
            // Try to create a temporary stream from the drive for PS3 detection
            // Read enough sectors to cover the root directory and PS3_GAME/PARAM.SFO
            var (pvdResult, pvdData) = drive.ReadSectors(PvdSector, 1, SectorSize);
            if (!pvdResult.Success || pvdResult.DataTransferred < SectorSize) return;
            if (!IsValidPvd(pvdData)) return;

            // The Ps3DiscService.DetectContent works with file streams
            // We set basic metadata from PVD
            var volLabel = Encoding.ASCII.GetString(pvdData, 40, 32).Trim();
            info.VolumeLabel = volLabel;
        }
        catch { /* PS3 metadata is optional */ }
    }

    // -----------------------------------------------------------------------
    //  Image-file classification (shared by DetectFromImage)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Classifies content from PVD fields and root directory listing (image files).
    /// </summary>
    private static void ClassifyFromPvdAndDirectories(DiscContentInfo info, long imageSize, bool isRaw)
    {
        var dirs = info.RootDirectories;
        var files = info.RootFiles;
        var sysId = info.SystemIdentifier.ToUpperInvariant();
        var appId = info.ApplicationIdentifier.ToUpperInvariant();
        var volId = info.VolumeLabel.ToUpperInvariant();

        // ---- Blu-ray content (large images) ----
        if (imageSize > FormatHelper.BD25Capacity ||
            HasAnyDirectory(dirs, "BDMV", "PS3_GAME"))
        {
            DetectBluRayContentFromPvdAndDirs(info, null);
            if (info.ContentType != DiscContentType.Unknown) return;
        }

        // ---- DVD content ----
        if (imageSize > FormatHelper.CdImageSizeThresholdBytes ||
            HasAnyDirectory(dirs, "VIDEO_TS", "AUDIO_TS", "DVD_RTAV"))
        {
            DetectDvdContentFromPvdAndDirs(info, null);
            if (info.ContentType != DiscContentType.Unknown) return;
        }

        // ---- CD content ----
        if (imageSize <= FormatHelper.CdImageSizeThresholdBytes || info.ContentType == DiscContentType.Unknown)
        {
            DetectCdContentFromPvdAndDirs(info);
        }
    }

    /// <summary>
    /// Detects gaming systems and other content from magic bytes at various offsets
    /// in the image stream (non-PVD detection).
    /// </summary>
    private static void DetectFromMagicBytes(Stream stream, DiscContentInfo info)
    {
        // ---- PlayStation 1 license string detection ----
        // PS1 discs have "Sony Computer Entertainment" at specific offsets
        if (TryDetectPs1(stream, info)) return;

        // ---- Sega systems detection ----
        if (TryDetectSegaSystem(stream, info)) return;

        // ---- Nintendo systems detection ----
        if (TryDetectNintendoSystem(stream, info)) return;

        // ---- Wii U detection ----
        if (TryDetectWiiU(stream, info)) return;

        // ---- 3DO detection ----
        if (TryDetect3DO(stream, info)) return;

        // ---- Neo Geo CD detection ----
        if (TryDetectNeoGeo(stream, info)) return;

        // ---- PC Engine / TurboGrafx-CD detection ----
        if (TryDetectPcEngine(stream, info)) return;

        // ---- Amiga detection ----
        if (TryDetectAmiga(stream, info)) return;

        // ---- Atari Jaguar CD detection ----
        if (TryDetectAtariJaguar(stream, info)) return;
    }

    // -----------------------------------------------------------------------
    //  Magic byte detection helpers
    // -----------------------------------------------------------------------

    private static bool TryDetectPs1(Stream stream, DiscContentInfo info)
    {
        const int rawSectorSize = 2352;
        const int rawUserDataOffset = 16;

        // Check raw sector 4
        long ps1LicenseOffset = 4L * rawSectorSize + rawUserDataOffset;
        if (stream.Length > ps1LicenseOffset + 128)
        {
            if (CheckForString(stream, ps1LicenseOffset, 128,
                "Sony Computer Entertainment", "PLAYSTATION"))
            {
                info.ContentType = DiscContentType.PlayStation1Game;
                info.ContentLabel = "🎮 PlayStation 1 Game";
                info.GamingSystem = "PlayStation 1";
                return true;
            }
        }

        // Check cooked sector 4
        if (stream.Length > 8192 + 128)
        {
            if (CheckForString(stream, 8192, 128,
                "Sony Computer Entertainment", "PLAYSTATION"))
            {
                info.ContentType = DiscContentType.PlayStation1Game;
                info.ContentLabel = "🎮 PlayStation 1 Game";
                info.GamingSystem = "PlayStation 1";
                return true;
            }
        }

        return false;
    }

    private static bool TryDetectSegaSystem(Stream stream, DiscContentInfo info)
    {
        // Sega disc-based system identification strings per official documentation:
        //   "SEGA SEGASATURN"  — Sega Saturn (sector 0 of data track)
        //   "SEGA SEGAKATANA"  — Sega Dreamcast (sector 0 of data track)
        //   "SEGADISCSYSTEM"   — Sega Mega CD / Sega CD (IP area, sector 0)
        //   "SEGA MEGA DRIVE"  — Some Sega Mega CD discs use this variant
        //   "SEGA 32X"         — Sega 32X CD (32X add-on for Mega CD)
        //   "SEGADATADISC"     — Sega CD data disc (non-bootable)
        //
        // For raw (2352-byte) BIN files, the CD sync pattern occupies bytes 0-11,
        // the header bytes 12-15, and user data starts at offset 16 within each sector.
        // For cooked (2048-byte) ISO files, user data starts at offset 0.
        //
        // Per Sega Retro documentation and RetroAchievements game identification:
        //   - Sector 0 user data bytes 0-15 contain the system identifier
        //   - Read at least 32 bytes to cover all variants reliably
        //
        // Reference: Mega-CD Disc Format Specifications (segaretro.org),
        //            RetroAchievements game-identification.html

        const int rawSectorSize = 2352;
        const int rawUserDataOffset = 16;

        // Check multiple offsets to handle various image formats:
        //   0   — cooked ISO or raw BIN with no sync (some dumpers strip it)
        //   16  — raw BIN (2352 bytes/sector), sector 0 user data at offset 16
        //   24  — raw BIN with subchannels (2448 bytes/sector), user data at 16
        // Also check sector 16 (0x8000 cooked / sector_size * 16 raw) for
        // edge cases where the system identifier is in the PVD area.
        long[] offsets =
        {
            0L,                                      // cooked sector 0
            rawUserDataOffset,                       // raw sector 0 (2352-byte)
            (long)rawSectorSize + rawUserDataOffset,  // raw sector 1 (rarely used)
        };

        foreach (long offset in offsets)
        {
            if (stream.Length <= offset + 32) continue;
            stream.Seek(offset, SeekOrigin.Begin);
            var buf = new byte[32];
            if (stream.Read(buf, 0, 32) < 16) continue;

            // Read as ASCII string, using full 32 bytes for wider variant matching
            var str16 = Encoding.ASCII.GetString(buf, 0, 16);
            var str32 = Encoding.ASCII.GetString(buf, 0, 32);

            // Sega Saturn: "SEGA SEGASATURN" at offset 0 of sector 0
            if (str16.Contains("SEGA SEGASATURN", StringComparison.OrdinalIgnoreCase))
            {
                info.ContentType = DiscContentType.SegaSaturnGame;
                info.ContentLabel = "🎮 Sega Saturn Game";
                info.GamingSystem = "Sega Saturn";
                return true;
            }

            // Sega Dreamcast: "SEGA SEGAKATANA" at offset 0 of sector 0
            if (str16.Contains("SEGA SEGAKATANA", StringComparison.OrdinalIgnoreCase))
            {
                info.ContentType = DiscContentType.SegaDreamcastGame;
                info.ContentLabel = "🎮 Sega Dreamcast Game";
                info.GamingSystem = "Sega Dreamcast";
                return true;
            }

            // Sega 32X CD: "SEGA 32X" at offset 0 of sector 0
            // 32X CD games run on the Mega CD + 32X combo hardware.
            // Check this BEFORE "SEGA MEGA DRIVE" since 32X headers may also
            // contain the Mega Drive identifier further into the header.
            if (str32.Contains("SEGA 32X", StringComparison.OrdinalIgnoreCase))
            {
                info.ContentType = DiscContentType.SegaMegaCdGame;
                info.ContentLabel = "🎮 Sega 32X CD Game";
                info.GamingSystem = "Sega 32X CD";
                return true;
            }

            // Sega Mega CD / Sega CD: multiple identifier variants
            //   "SEGADISCSYSTEM"  — standard bootable game disc
            //   "SEGA MEGA DRIVE" — some Mega CD discs use this identifier
            //   "SEGADATADISC"    — data disc (non-bootable, used by some titles)
            //   "SEGA DISC"       — abbreviated variant found in some dumps
            if (str16.Contains("SEGADISCSYSTEM", StringComparison.OrdinalIgnoreCase) ||
                str16.Contains("SEGA MEGA DRIVE", StringComparison.OrdinalIgnoreCase) ||
                str16.Contains("SEGADATADISC", StringComparison.OrdinalIgnoreCase) ||
                str32.Contains("SEGA DISC SYSTEM", StringComparison.OrdinalIgnoreCase) ||
                str32.Contains("SEGA_DISC_SYSTEM", StringComparison.OrdinalIgnoreCase))
            {
                info.ContentType = DiscContentType.SegaMegaCdGame;
                info.ContentLabel = "🎮 Sega Mega CD Game";
                info.GamingSystem = "Sega Mega CD";
                return true;
            }
        }
        return false;
    }

    private static bool TryDetectNintendoSystem(Stream stream, DiscContentInfo info)
    {
        if (stream.Length <= 0x20) return false;

        var magic = new byte[4];

        // Check Wii magic at 0x18 (cooked) and 0x28 (raw)
        foreach (long offset in new[] { 0x18L, 0x28L })
        {
            if (stream.Length <= offset + 4) continue;
            stream.Seek(offset, SeekOrigin.Begin);
            if (stream.Read(magic, 0, 4) == 4)
            {
                uint discMagic = ((uint)magic[0] << 24) | ((uint)magic[1] << 16) |
                                 ((uint)magic[2] << 8) | magic[3];
                if (discMagic == 0x5D1C9EA3)
                {
                    info.ContentType = DiscContentType.WiiGame;
                    info.ContentLabel = "🎮 Nintendo Wii Game";
                    info.GamingSystem = "Wii";
                    return true;
                }
            }
        }

        // Check GameCube magic at 0x1C (cooked) and 0x2C (raw)
        foreach (long offset in new[] { 0x1CL, 0x2CL })
        {
            if (stream.Length <= offset + 4) continue;
            stream.Seek(offset, SeekOrigin.Begin);
            if (stream.Read(magic, 0, 4) == 4)
            {
                uint discMagic = ((uint)magic[0] << 24) | ((uint)magic[1] << 16) |
                                 ((uint)magic[2] << 8) | magic[3];
                if (discMagic == 0xC2339F3D)
                {
                    info.ContentType = DiscContentType.GameCubeGame;
                    info.ContentLabel = "🎮 Nintendo GameCube Game";
                    info.GamingSystem = "GameCube";
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryDetectWiiU(Stream stream, DiscContentInfo info)
    {
        if (stream.Length <= 4) return false;
        stream.Seek(0, SeekOrigin.Begin);
        var header = new byte[4];
        if (stream.Read(header, 0, 4) != 4) return false;

        uint magic = ((uint)header[0] << 24) | ((uint)header[1] << 16) |
                     ((uint)header[2] << 8) | header[3];
        if (magic == 0x57555030) // "WUP0"
        {
            info.ContentType = DiscContentType.WiiUGame;
            info.ContentLabel = "🎮 Nintendo Wii U Game";
            info.GamingSystem = "Wii U";
            return true;
        }
        return false;
    }

    private static bool TryDetect3DO(Stream stream, DiscContentInfo info)
    {
        // 3DO disc identification: check volume header at sector 0.
        // Per RetroAchievements documentation, the first 132 bytes of sector 0
        // contain the 3DO volume header. The header starts with 0x01 0x5A.
        // Also check at raw offset 16 for 2352-byte sector images.
        foreach (long offset in new[] { 0L, 16L })
        {
            if (stream.Length <= offset + 16) continue;
            stream.Seek(offset, SeekOrigin.Begin);
            var header = new byte[16];
            if (stream.Read(header, 0, 16) < 16) continue;

            if (header[0] == 0x01 && header[1] == 0x5A)
            {
                info.ContentType = DiscContentType.ThreeDOGame;
                info.ContentLabel = "🎮 3DO Game";
                info.GamingSystem = "3DO";
                return true;
            }
        }
        return false;
    }

    private static bool TryDetectNeoGeo(Stream stream, DiscContentInfo info)
    {
        if (stream.Length <= 0x110) return false;
        stream.Seek(0x100, SeekOrigin.Begin);
        var buf = new byte[10];
        if (stream.Read(buf, 0, 10) < 7) return false;
        if (Encoding.ASCII.GetString(buf).Contains("NEO-GEO"))
        {
            info.ContentType = DiscContentType.NeoGeoCdGame;
            info.ContentLabel = "🎮 Neo Geo CD Game";
            info.GamingSystem = "Neo Geo CD";
            return true;
        }
        return false;
    }

    private static bool TryDetectPcEngine(Stream stream, DiscContentInfo info)
    {
        // Check both cooked (offset 0) and raw (offset 16) sectors
        foreach (long offset in new[] { 0L, 16L })
        {
            if (stream.Length <= offset + 32) continue;
            stream.Seek(offset, SeekOrigin.Begin);
            var buf = new byte[32];
            if (stream.Read(buf, 0, 32) < 32) continue;
            var str = Encoding.ASCII.GetString(buf);
            if (str.Contains("PC Engine") || str.Contains("PCECD"))
            {
                info.ContentType = DiscContentType.PcEngineGame;
                info.ContentLabel = "🎮 PC Engine / TurboGrafx-CD Game";
                info.GamingSystem = "PC Engine / TurboGrafx-CD";
                return true;
            }
        }
        return false;
    }

    private static bool TryDetectAmiga(Stream stream, DiscContentInfo info)
    {
        // Check both cooked (offset 0) and raw (offset 16) sectors
        foreach (long offset in new[] { 0L, 16L })
        {
            if (stream.Length <= offset + 32) continue;
            stream.Seek(offset, SeekOrigin.Begin);
            var buf = new byte[32];
            var bytesRead = stream.Read(buf, 0, 32);
            if (bytesRead < 16) continue;
            var str = Encoding.ASCII.GetString(buf, 0, bytesRead);

            if (str.Contains("CDTV"))
            {
                info.ContentType = DiscContentType.AmigaCdtvGame;
                info.ContentLabel = "🎮 Amiga CDTV Game";
                info.GamingSystem = "Amiga CDTV";
                return true;
            }
            if (str.Contains("AMIGABOOT") || str.Contains("AMIGA"))
            {
                info.ContentType = DiscContentType.AmigaCd32Game;
                info.ContentLabel = "🎮 Amiga CD32 Game";
                info.GamingSystem = "Amiga CD32";
                return true;
            }
        }
        return false;
    }

    private static bool TryDetectAtariJaguar(Stream stream, DiscContentInfo info)
    {
        foreach (long offset in new[] { 0L, 16L })
        {
            if (stream.Length <= offset + 32) continue;
            stream.Seek(offset, SeekOrigin.Begin);
            var buf = new byte[32];
            if (stream.Read(buf, 0, 32) < 32) continue;
            var str = Encoding.ASCII.GetString(buf);
            if (str.Contains("ATRI") || str.Contains("TARA"))
            {
                info.ContentType = DiscContentType.AtariJaguarCdGame;
                info.ContentLabel = "🎮 Atari Jaguar CD Game";
                info.GamingSystem = "Atari Jaguar CD";
                return true;
            }
        }
        return false;
    }

    private static void DetectDreamcastFromCdi(string imagePath, Stream stream, DiscContentInfo info)
    {
        try
        {
            var cdiImage = CdiParser.Parse(imagePath);
            if (!cdiImage.IsValid) return;

            foreach (var track in cdiImage.DataTracks)
            {
                if (track.TotalLength <= 0 || track.FileOffset < 0) continue;
                try
                {
                    using var cdiStream = new FileStream(imagePath, FileMode.Open,
                        FileAccess.Read, FileShare.Read);
                    long dataStart = track.FileOffset + (long)track.Pregap * track.SectorSize;
                    if (dataStart + track.SectorSize > cdiStream.Length) continue;
                    cdiStream.Seek(dataStart + track.UserDataOffset, SeekOrigin.Begin);
                    var sectorBuf = new byte[Math.Min(256, track.UserDataSize)];
                    if (cdiStream.Read(sectorBuf, 0, sectorBuf.Length) >= 16)
                    {
                        var headerStr = Encoding.ASCII.GetString(sectorBuf, 0,
                            Math.Min(32, sectorBuf.Length));
                        if (headerStr.Contains("SEGA SEGAKATANA", StringComparison.OrdinalIgnoreCase) ||
                            headerStr.Contains("SEGA DREAMCAST", StringComparison.OrdinalIgnoreCase))
                        {
                            info.ContentType = DiscContentType.SegaDreamcastGame;
                            info.ContentLabel = "🎮 Sega Dreamcast Game (CDI)";
                            info.GamingSystem = "Sega Dreamcast";
                            return;
                        }
                    }
                }
                catch { /* best-effort */ }
            }

            // Size heuristic for GD-ROM
            if (stream.Length >= GdRomMinSizeBytes &&
                stream.Length <= FormatHelper.GdRomCapacity + GdRomSizeToleranceBytes)
            {
                info.ContentType = DiscContentType.SegaDreamcastGame;
                info.ContentLabel = "🎮 Sega Dreamcast Game (CDI)";
                info.GamingSystem = "Sega Dreamcast";
            }
        }
        catch { /* best-effort */ }
    }

    // -----------------------------------------------------------------------
    //  ISO 9660 parsing helpers
    // -----------------------------------------------------------------------

    /// <summary>
    /// Tries to read a PVD from the given offset in the stream.
    /// Returns the PVD data if valid, null otherwise.
    /// </summary>
    private static byte[]? TryReadPvd(Stream stream, long offset)
    {
        if (stream.Length < offset + SectorSize) return null;
        stream.Seek(offset, SeekOrigin.Begin);
        var pvd = new byte[SectorSize];
        if (stream.Read(pvd, 0, SectorSize) < SectorSize) return null;
        return IsValidPvd(pvd) ? pvd : null;
    }

    /// <summary>
    /// Checks if a buffer contains a valid ISO 9660 Primary Volume Descriptor.
    /// PVD starts with type 0x01 followed by "CD001" per ECMA-119 §8.4.
    /// </summary>
    private static bool IsValidPvd(byte[] data)
    {
        return data.Length >= 6 &&
               data[0] == 0x01 &&
               data[1] == (byte)'C' && data[2] == (byte)'D' &&
               data[3] == (byte)'0' && data[4] == (byte)'0' && data[5] == (byte)'1';
    }

    /// <summary>
    /// Extracts key fields from the PVD into the content info.
    /// </summary>
    private static void ParsePvdFields(byte[] pvd, DiscContentInfo info)
    {
        info.SystemIdentifier = Encoding.ASCII.GetString(pvd, 8, 32).Trim();
        info.VolumeLabel = Encoding.ASCII.GetString(pvd, 40, 32).Trim();

        // Application identifier at offset 574, 128 bytes (ECMA-119 §8.4.1)
        if (pvd.Length >= 702 + 128)
            info.ApplicationIdentifier = Encoding.ASCII.GetString(pvd, 574, 128).Trim();
    }

    /// <summary>
    /// Reads root directory entries from an ISO 9660 filesystem using a physical drive.
    /// </summary>
    private static void ReadRootDirectoryFromDrive(OpticalDrive drive, byte[] pvd, DiscContentInfo info)
    {
        if (pvd[156] < 34) return;

        int rootLba = pvd[156 + 2] | (pvd[156 + 3] << 8) |
                      (pvd[156 + 4] << 16) | (pvd[156 + 5] << 24);
        int rootLen = pvd[156 + 10] | (pvd[156 + 11] << 8) |
                      (pvd[156 + 12] << 16) | (pvd[156 + 13] << 24);

        if (rootLba <= 0 || rootLen <= 0) return;

        int sectorsToRead = (rootLen + SectorSize - 1) / SectorSize;
        sectorsToRead = Math.Min(sectorsToRead, 64); // Limit reads

        var (result, dirData) = drive.ReadSectors((uint)rootLba, (ushort)sectorsToRead, SectorSize);
        if (!result.Success || result.DataTransferred == 0) return;

        ParseDirectoryEntries(dirData, rootLen, result.DataTransferred, info);
    }

    /// <summary>
    /// Reads root directory entries from an ISO 9660 filesystem in an image stream.
    /// </summary>
    private static void ReadRootDirectoryFromPvd(Stream stream, byte[] pvd, DiscContentInfo info, bool isRaw)
    {
        if (pvd[156] < 34) return;

        int rootLba = pvd[156 + 2] | (pvd[156 + 3] << 8) |
                      (pvd[156 + 4] << 16) | (pvd[156 + 5] << 24);
        int rootLen = pvd[156 + 10] | (pvd[156 + 11] << 8) |
                      (pvd[156 + 12] << 16) | (pvd[156 + 13] << 24);

        if (rootLba <= 0 || rootLen <= 0) return;

        int sectorsToRead = Math.Min((rootLen + SectorSize - 1) / SectorSize, 64);
        var dirData = new byte[sectorsToRead * SectorSize];

        if (isRaw)
        {
            // For raw images, each sector is 2352 bytes with 16-byte header
            const int rawSectorSize = 2352;
            const int rawUserDataOffset = 16;
            int bytesRead = 0;
            for (int i = 0; i < sectorsToRead; i++)
            {
                long sectorOffset = ((long)rootLba + i) * rawSectorSize + rawUserDataOffset;
                if (sectorOffset + SectorSize > stream.Length) break;
                stream.Seek(sectorOffset, SeekOrigin.Begin);
                int read = stream.Read(dirData, bytesRead, SectorSize);
                if (read == 0) break;
                bytesRead += read;
            }
            ParseDirectoryEntries(dirData, rootLen, bytesRead, info);
        }
        else
        {
            long offset = (long)rootLba * SectorSize;
            if (offset + rootLen > stream.Length) return;
            stream.Seek(offset, SeekOrigin.Begin);
            int read = stream.Read(dirData, 0, dirData.Length);
            ParseDirectoryEntries(dirData, rootLen, read, info);
        }
    }

    /// <summary>
    /// Parses ISO 9660 directory record entries and populates directory/file names.
    /// Per ECMA-119 §9.1, each directory record contains:
    /// - Byte 0: Length of Directory Record
    /// - Byte 25: File Flags (bit 1 = directory)
    /// - Byte 32: Length of File Identifier
    /// - Byte 33+: File Identifier
    /// </summary>
    private static void ParseDirectoryEntries(byte[] data, int dirLength, int dataRead, DiscContentInfo info)
    {
        int offset = 0;
        int limit = Math.Min(dirLength, dataRead);

        while (offset < limit)
        {
            int recordLen = data[offset];
            if (recordLen == 0)
            {
                // Jump to next sector boundary
                offset = ((offset / SectorSize) + 1) * SectorSize;
                continue;
            }

            if (offset + recordLen > dataRead) break;

            int nameLen = data[offset + 32];
            if (nameLen > 0 && nameLen <= recordLen - 33)
            {
                var name = Encoding.ASCII.GetString(data, offset + 33, nameLen)
                    .TrimEnd(';', '1', '\0', ' ');

                if (name.Length > 1) // Skip "." and ".." entries
                {
                    // Check file flags: bit 1 = directory
                    bool isDirectory = (data[offset + 25] & 0x02) != 0;
                    if (isDirectory)
                        info.RootDirectories.Add(name);
                    else
                        info.RootFiles.Add(name);
                }
            }

            offset += recordLen;
        }
    }

    /// <summary>
    /// Reads disc and track information from a physical drive.
    /// </summary>
    private static void ReadDiscAndTrackInfo(OpticalDrive drive, DiscContentInfo info)
    {
        try
        {
            var discInfo = drive.ReadDiscInfo();
            if (discInfo != null)
            {
                info.SessionCount = discInfo.NumberOfSessionsLsb;
                info.TrackCount = discInfo.LastTrackInLastSessionLsb;

                // Count audio vs data tracks
                for (uint t = 1; t <= (uint)discInfo.LastTrackInLastSessionLsb; t++)
                {
                    var trackInfo = drive.ReadTrackInfo(t);
                    if (trackInfo != null)
                    {
                        if ((trackInfo.TrackMode & 0x04) == 0)
                            info.AudioTrackCount++;
                        else
                            info.DataTrackCount++;
                    }
                }
            }

            // Get capacity
            var capacity = drive.ReadCapacity();
            if (capacity.HasValue)
            {
                info.CapacityBytes = ((long)capacity.Value.LastLba + 1) * capacity.Value.BlockLength;
            }
        }
        catch { /* disc/track info is optional */ }
    }

    // -----------------------------------------------------------------------
    //  Utility helpers
    // -----------------------------------------------------------------------

    /// <summary>Checks if any of the specified directory names exist in the list (case-insensitive).</summary>
    private static bool HasAnyDirectory(List<string> dirs, params string[] names)
    {
        return names.Any(name =>
            dirs.Any(d => d.Equals(name, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Checks if any of the specified file names exist in the list (case-insensitive).</summary>
    private static bool HasAnyFile(List<string> files, params string[] names)
    {
        return names.Any(name =>
            files.Any(f => f.Equals(name, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Checks if a region of the stream contains any of the specified strings.</summary>
    private static bool CheckForString(Stream stream, long offset, int length, params string[] patterns)
    {
        if (stream.Length <= offset + length) return false;
        stream.Seek(offset, SeekOrigin.Begin);
        var buf = new byte[length];
        if (stream.Read(buf, 0, length) < length) return false;
        var str = Encoding.ASCII.GetString(buf);
        return patterns.Any(p => str.Contains(p, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets the content label emoji prefix for a content type category.
    /// </summary>
    public static string GetContentTypeLabel(DiscContentType contentType)
    {
        return contentType switch
        {
            DiscContentType.Unknown => "Unknown",
            DiscContentType.Data => "💿 Data Disc",
            DiscContentType.AudioCd => "🎵 Audio CD",
            DiscContentType.MixedModeCd => "🎵 Mixed Mode CD",
            DiscContentType.EnhancedCd => "🎵 Enhanced CD (CD Extra)",
            DiscContentType.VideoCd => "📀 Video CD (VCD)",
            DiscContentType.SuperVideoCd => "📀 Super Video CD (SVCD)",
            DiscContentType.PhotoCd => "📷 Photo CD",
            DiscContentType.CdInteractive => "📀 CD-i Disc",
            DiscContentType.DataCd => "💿 Data CD",
            DiscContentType.PlayStation1Game => "🎮 PlayStation 1 Game",
            DiscContentType.SegaSaturnGame => "🎮 Sega Saturn Game",
            DiscContentType.SegaDreamcastGame => "🎮 Sega Dreamcast Game",
            DiscContentType.SegaMegaCdGame => "🎮 Sega Mega CD Game",
            DiscContentType.PcEngineGame => "🎮 PC Engine / TurboGrafx-CD Game",
            DiscContentType.NeoGeoCdGame => "🎮 Neo Geo CD Game",
            DiscContentType.AmigaCd32Game => "🎮 Amiga CD32 Game",
            DiscContentType.AmigaCdtvGame => "🎮 Amiga CDTV Game",
            DiscContentType.AtariJaguarCdGame => "🎮 Atari Jaguar CD Game",
            DiscContentType.ThreeDOGame => "🎮 3DO Game",
            DiscContentType.CdInteractiveGame => "🎮 CD-i Game",
            DiscContentType.DvdVideo => "🎬 DVD-Video",
            DiscContentType.DvdAudio => "🎵 DVD-Audio",
            DiscContentType.DvdVideoRecording => "🎬 DVD-VR (Video Recording)",
            DiscContentType.DataDvd => "💿 Data DVD",
            DiscContentType.PhotoDvd => "📷 Photo DVD",
            DiscContentType.PlayStation2Game => "🎮 PlayStation 2 Game",
            DiscContentType.XboxGame => "🎮 Xbox Game",
            DiscContentType.Xbox360Game => "🎮 Xbox 360 Game",
            DiscContentType.WiiGame => "🎮 Nintendo Wii Game",
            DiscContentType.GameCubeGame => "🎮 Nintendo GameCube Game",
            DiscContentType.WiiUGame => "🎮 Nintendo Wii U Game",
            DiscContentType.PspUmdGame => "🎮 PSP UMD Game",
            DiscContentType.BluRayMovie => "🎬 Blu-ray Movie",
            DiscContentType.BluRayAudio => "🎵 Blu-ray Audio",
            DiscContentType.BluRayAudioVisual => "🎬 Blu-ray BDAV Recording",
            DiscContentType.BluRayJava => "🎬 Blu-ray BD-J Interactive",
            DiscContentType.BluRay3D => "🎬 Blu-ray 3D",
            DiscContentType.UhdBluRay => "🎬 UHD Blu-ray (4K)",
            DiscContentType.DataBluRay => "💿 Data Blu-ray",
            DiscContentType.PhotoBluRay => "📷 Photo Blu-ray",
            DiscContentType.PlayStation3Game => "🎮 PlayStation 3 Game",
            DiscContentType.PlayStation4Game => "🎮 PlayStation 4 Game",
            DiscContentType.PlayStation5Game => "🎮 PlayStation 5 Game",
            DiscContentType.XboxOneGame => "🎮 Xbox One Game",
            DiscContentType.XboxSeriesGame => "🎮 Xbox Series X|S Game",
            _ => "Unknown"
        };
    }

    /// <summary>
    /// Maps a <see cref="DiscContentType"/> to the corresponding <see cref="BluRayContentType"/>
    /// for backward compatibility with existing Blu-ray workflows.
    /// </summary>
    public static BluRayContentType ToBluRayContentType(DiscContentType contentType)
    {
        return contentType switch
        {
            DiscContentType.PlayStation3Game => BluRayContentType.Ps3Game,
            DiscContentType.PlayStation4Game => BluRayContentType.Ps4Game,
            DiscContentType.PlayStation5Game => BluRayContentType.Ps5Game,
            DiscContentType.XboxOneGame => BluRayContentType.XboxOneGame,
            DiscContentType.XboxSeriesGame => BluRayContentType.XboxSeriesGame,
            DiscContentType.BluRayMovie => BluRayContentType.Movie,
            DiscContentType.BluRay3D => BluRayContentType.Movie3D,
            DiscContentType.UhdBluRay => BluRayContentType.UhdMovie,
            DiscContentType.BluRayAudio => BluRayContentType.Audio,
            DiscContentType.BluRayAudioVisual => BluRayContentType.Bdav,
            DiscContentType.BluRayJava => BluRayContentType.BdJava,
            DiscContentType.PhotoBluRay => BluRayContentType.Photo,
            DiscContentType.DataBluRay => BluRayContentType.Data,
            _ => BluRayContentType.Unknown
        };
    }

    /// <summary>
    /// Maps a <see cref="BluRayContentType"/> to the corresponding <see cref="DiscContentType"/>
    /// for integration with existing Blu-ray detection results.
    /// </summary>
    public static DiscContentType FromBluRayContentType(BluRayContentType bdType)
    {
        return bdType switch
        {
            BluRayContentType.Ps3Game => DiscContentType.PlayStation3Game,
            BluRayContentType.Ps4Game => DiscContentType.PlayStation4Game,
            BluRayContentType.Ps5Game => DiscContentType.PlayStation5Game,
            BluRayContentType.XboxOneGame => DiscContentType.XboxOneGame,
            BluRayContentType.XboxSeriesGame => DiscContentType.XboxSeriesGame,
            BluRayContentType.Movie => DiscContentType.BluRayMovie,
            BluRayContentType.Movie3D => DiscContentType.BluRay3D,
            BluRayContentType.UhdMovie => DiscContentType.UhdBluRay,
            BluRayContentType.Audio => DiscContentType.BluRayAudio,
            BluRayContentType.Bdav => DiscContentType.BluRayAudioVisual,
            BluRayContentType.BdJava => DiscContentType.BluRayJava,
            BluRayContentType.Photo => DiscContentType.PhotoBluRay,
            BluRayContentType.Data => DiscContentType.DataBluRay,
            _ => DiscContentType.Unknown
        };
    }

    // -----------------------------------------------------------------------
    //  Content-aware copy configuration recommendations
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns the recommended output format for faithfully copying the detected content type.
    /// Ensures content is preserved in its native format without unnecessary conversion.
    /// </summary>
    /// <remarks>
    /// Format selection logic:
    /// <list type="bullet">
    /// <item>Audio CDs, Mixed Mode, Enhanced CDs → BIN/CUE (preserves audio tracks and raw sectors)</item>
    /// <item>VCD/SVCD → BIN/CUE (preserves Mode 2 XA sector structure)</item>
    /// <item>CD game discs (PS1, Saturn, Dreamcast, etc.) → BIN/CUE (preserves raw sectors + subchannel)</item>
    /// <item>DVD-Video, DVD-Audio, DVD-VR → ISO (standard cooked sectors)</item>
    /// <item>DVD game discs (PS2, GameCube, Wii) → ISO</item>
    /// <item>Xbox/Xbox 360 game discs → XISO (Xbox) (preserves XDVDFS filesystem)</item>
    /// <item>Blu-ray content (movie, audio, game) → ISO</item>
    /// <item>Photo/data discs → ISO</item>
    /// </list>
    /// </remarks>
    public static string GetRecommendedOutputFormat(DiscContentType contentType)
    {
        return contentType switch
        {
            // CD audio formats — BIN/CUE preserves all audio track data and raw sectors
            DiscContentType.AudioCd => "BIN/CUE",
            DiscContentType.MixedModeCd => "BIN/CUE",
            DiscContentType.EnhancedCd => "BIN/CUE",

            // VCD/SVCD — Mode 2 XA sectors require raw reading to preserve structure
            DiscContentType.VideoCd => "BIN/CUE",
            DiscContentType.SuperVideoCd => "BIN/CUE",

            // CD-i — Mode 2 Form 1/Form 2 sectors
            DiscContentType.CdInteractive => "BIN/CUE",

            // Photo CD — multi-session CD-ROM XA, BIN/CUE preserves session structure
            DiscContentType.PhotoCd => "BIN/CUE",

            // CD game discs — raw sectors + subchannel for copy protection
            DiscContentType.PlayStation1Game => "BIN/CUE",
            DiscContentType.SegaSaturnGame => "BIN/CUE",
            DiscContentType.SegaDreamcastGame => "BIN/CUE",
            DiscContentType.SegaMegaCdGame => "BIN/CUE",
            DiscContentType.PcEngineGame => "BIN/CUE",
            DiscContentType.NeoGeoCdGame => "BIN/CUE",
            DiscContentType.AmigaCd32Game => "BIN/CUE",
            DiscContentType.AmigaCdtvGame => "BIN/CUE",
            DiscContentType.AtariJaguarCdGame => "BIN/CUE",
            DiscContentType.ThreeDOGame => "BIN/CUE",
            DiscContentType.CdInteractiveGame => "BIN/CUE",

            // Data CD — ISO is sufficient for standard cooked sectors
            DiscContentType.DataCd => "ISO",

            // DVD content — standard ISO for cooked 2048-byte sectors
            DiscContentType.DvdVideo => "ISO",
            DiscContentType.DvdAudio => "ISO",
            DiscContentType.DvdVideoRecording => "ISO",
            DiscContentType.DataDvd => "ISO",
            DiscContentType.PhotoDvd => "ISO",

            // DVD game discs — ISO except Xbox which uses XISO
            DiscContentType.PlayStation2Game => "ISO",
            DiscContentType.XboxGame => "XISO (Xbox)",
            DiscContentType.Xbox360Game => "XISO (Xbox)",
            DiscContentType.WiiGame => "ISO",
            DiscContentType.GameCubeGame => "ISO",
            DiscContentType.WiiUGame => "ISO",
            DiscContentType.PspUmdGame => "ISO",

            // Blu-ray content — ISO
            DiscContentType.BluRayMovie => "ISO",
            DiscContentType.BluRayAudio => "ISO",
            DiscContentType.BluRayAudioVisual => "ISO",
            DiscContentType.BluRayJava => "ISO",
            DiscContentType.BluRay3D => "ISO",
            DiscContentType.UhdBluRay => "ISO",
            DiscContentType.DataBluRay => "ISO",
            DiscContentType.PhotoBluRay => "ISO",

            // BD game discs — ISO
            DiscContentType.PlayStation3Game => "ISO",
            DiscContentType.PlayStation4Game => "ISO",
            DiscContentType.PlayStation5Game => "ISO",
            DiscContentType.XboxOneGame => "ISO",
            DiscContentType.XboxSeriesGame => "ISO",

            // Fallback
            DiscContentType.Data => "ISO",
            _ => "ISO"
        };
    }

    /// <summary>
    /// Returns the recommended sector size for faithfully copying the detected content type.
    /// </summary>
    public static int GetRecommendedSectorSize(DiscContentType contentType)
    {
        return contentType switch
        {
            // Raw sector reads for CD audio and game discs
            DiscContentType.AudioCd => 2352,
            DiscContentType.MixedModeCd => 2352,
            DiscContentType.EnhancedCd => 2352,
            DiscContentType.VideoCd => 2352,
            DiscContentType.SuperVideoCd => 2352,
            DiscContentType.CdInteractive => 2352,
            DiscContentType.PhotoCd => 2352,
            DiscContentType.PlayStation1Game => 2352,
            DiscContentType.SegaSaturnGame => 2352,
            DiscContentType.SegaDreamcastGame => 2352,
            DiscContentType.SegaMegaCdGame => 2352,
            DiscContentType.PcEngineGame => 2352,
            DiscContentType.NeoGeoCdGame => 2352,
            DiscContentType.AmigaCd32Game => 2352,
            DiscContentType.AmigaCdtvGame => 2352,
            DiscContentType.AtariJaguarCdGame => 2352,
            DiscContentType.ThreeDOGame => 2352,
            DiscContentType.CdInteractiveGame => 2352,

            // Standard cooked sectors for DVD/BD
            _ => 2048
        };
    }

    /// <summary>
    /// Returns the recommended subchannel mode for faithfully copying the detected content type.
    /// </summary>
    public static string GetRecommendedSubchannelMode(DiscContentType contentType)
    {
        return contentType switch
        {
            // PlayStation 1 needs RW_RAW for LibCrypt protection
            DiscContentType.PlayStation1Game => "RW_RAW",
            // Other CD game systems benefit from subchannel for copy protection
            DiscContentType.SegaSaturnGame => "RW",
            DiscContentType.SegaDreamcastGame => "RW",
            DiscContentType.SegaMegaCdGame => "RW",
            DiscContentType.PcEngineGame => "RW",
            DiscContentType.NeoGeoCdGame => "RW",
            DiscContentType.AtariJaguarCdGame => "RW",
            DiscContentType.AmigaCd32Game => "RW",
            DiscContentType.AmigaCdtvGame => "RW",
            DiscContentType.CdInteractive => "RW",
            DiscContentType.CdInteractiveGame => "RW",
            _ => "None"
        };
    }

    /// <summary>
    /// Returns the name of the matching <see cref="GamingPreset"/> for the detected content type,
    /// or <c>null</c> if no gaming preset applies.
    /// </summary>
    public static string? GetMatchingPresetName(DiscContentType contentType)
    {
        return contentType switch
        {
            DiscContentType.PlayStation1Game => "PlayStation 1",
            DiscContentType.PlayStation2Game => "PlayStation 2",
            DiscContentType.PlayStation3Game => "PlayStation 3",
            DiscContentType.PlayStation4Game or
            DiscContentType.PlayStation5Game => "PlayStation 4/5",
            DiscContentType.SegaSaturnGame => "Sega Saturn",
            DiscContentType.SegaDreamcastGame => "Sega Dreamcast",
            DiscContentType.SegaMegaCdGame => "Sega Mega CD",
            DiscContentType.XboxGame => "Xbox",
            DiscContentType.Xbox360Game => "Xbox 360",
            DiscContentType.XboxOneGame or
            DiscContentType.XboxSeriesGame => "Xbox One/Series",
            DiscContentType.GameCubeGame => "GameCube",
            DiscContentType.WiiGame => "Wii",
            DiscContentType.WiiUGame => "Wii U",
            DiscContentType.PspUmdGame => "PSP (UMD)",
            DiscContentType.PcEngineGame => "PC Engine / TurboGrafx-CD",
            DiscContentType.NeoGeoCdGame => "Neo Geo CD",
            DiscContentType.AtariJaguarCdGame => "Atari Jaguar CD",
            DiscContentType.ThreeDOGame => "3DO",
            DiscContentType.CdInteractive or
            DiscContentType.CdInteractiveGame => "CD-i (Philips)",
            DiscContentType.AmigaCd32Game => "Amiga CD32",
            DiscContentType.AmigaCdtvGame => "Amiga CDTV",
            _ => null
        };
    }

    /// <summary>
    /// Returns the recommended write mode for burning the detected content type.
    /// Returns <c>null</c> when no specific recommendation applies (use user default).
    /// </summary>
    public static string? GetRecommendedWriteMode(DiscContentType contentType)
    {
        return contentType switch
        {
            // CD audio and game discs — DAO (Disc At Once) for gap-less playback
            // and faithful copy protection replication
            DiscContentType.AudioCd => "DAO",
            DiscContentType.MixedModeCd => "SAO",
            DiscContentType.EnhancedCd => "SAO",
            DiscContentType.PlayStation1Game => "DAO",
            DiscContentType.SegaSaturnGame => "DAO",
            DiscContentType.SegaDreamcastGame => "DAO",
            DiscContentType.SegaMegaCdGame => "DAO",
            DiscContentType.PcEngineGame => "DAO",
            DiscContentType.NeoGeoCdGame => "DAO",
            DiscContentType.AmigaCd32Game => "DAO",
            DiscContentType.AmigaCdtvGame => "DAO",
            DiscContentType.AtariJaguarCdGame => "DAO",
            DiscContentType.ThreeDOGame => "DAO",
            DiscContentType.CdInteractive => "DAO",
            DiscContentType.CdInteractiveGame => "DAO",

            // VCD/SVCD — DAO for proper Mode 2 XA layout
            DiscContentType.VideoCd => "DAO",
            DiscContentType.SuperVideoCd => "DAO",

            // DVD-Video — DAO for player compatibility
            DiscContentType.DvdVideo => "DAO",
            DiscContentType.DvdAudio => "DAO",
            DiscContentType.DvdVideoRecording => "DAO",

            // DVD game discs — DAO for faithful copy
            DiscContentType.PlayStation2Game => "DAO",

            // Blu-ray — no specific mode preference (drive handles internally)
            _ => null
        };
    }

    /// <summary>
    /// Returns whether the disc should be finalized (closed) after burning
    /// for the detected content type. Most standalone players require finalization.
    /// </summary>
    public static bool GetRecommendedFinalize(DiscContentType contentType)
    {
        return contentType switch
        {
            // Video discs MUST be finalized for standalone player compatibility
            DiscContentType.DvdVideo => true,
            DiscContentType.DvdAudio => true,
            DiscContentType.DvdVideoRecording => true,
            DiscContentType.BluRayMovie => true,
            DiscContentType.BluRay3D => true,
            DiscContentType.UhdBluRay => true,
            DiscContentType.BluRayAudio => true,
            DiscContentType.BluRayAudioVisual => true,
            DiscContentType.BluRayJava => true,
            DiscContentType.VideoCd => true,
            DiscContentType.SuperVideoCd => true,

            // Audio CDs must be finalized for CD player compatibility
            DiscContentType.AudioCd => true,
            DiscContentType.EnhancedCd => true,
            DiscContentType.MixedModeCd => true,

            // Game discs must be finalized for console compatibility
            DiscContentType.PlayStation1Game => true,
            DiscContentType.PlayStation2Game => true,
            DiscContentType.PlayStation3Game => true,
            DiscContentType.PlayStation4Game => true,
            DiscContentType.PlayStation5Game => true,
            DiscContentType.SegaSaturnGame => true,
            DiscContentType.SegaDreamcastGame => true,
            DiscContentType.SegaMegaCdGame => true,
            DiscContentType.PcEngineGame => true,
            DiscContentType.NeoGeoCdGame => true,
            DiscContentType.AmigaCd32Game => true,
            DiscContentType.AmigaCdtvGame => true,
            DiscContentType.AtariJaguarCdGame => true,
            DiscContentType.ThreeDOGame => true,
            DiscContentType.CdInteractiveGame => true,
            DiscContentType.XboxGame => true,
            DiscContentType.Xbox360Game => true,
            DiscContentType.XboxOneGame => true,
            DiscContentType.XboxSeriesGame => true,
            DiscContentType.WiiGame => true,
            DiscContentType.GameCubeGame => true,
            DiscContentType.WiiUGame => true,
            DiscContentType.PspUmdGame => true,

            // Data and photo discs — finalize by default for broad compatibility
            _ => true
        };
    }

    /// <summary>
    /// Returns a human-readable preset description for the detected content type
    /// suitable for display in the Burn/Write view.
    /// </summary>
    public static string? GetWritePresetDescription(DiscContentType contentType)
    {
        return contentType switch
        {
            DiscContentType.AudioCd =>
                "✅ Audio CD preset: DAO write mode, finalize disc (required for CD player compatibility)",
            DiscContentType.MixedModeCd =>
                "✅ Mixed Mode CD preset: SAO write mode, finalize disc",
            DiscContentType.EnhancedCd =>
                "✅ Enhanced CD preset: SAO write mode, finalize disc",
            DiscContentType.VideoCd =>
                "✅ Video CD preset: DAO write mode, finalize disc (required for VCD player compatibility)",
            DiscContentType.SuperVideoCd =>
                "✅ Super Video CD preset: DAO write mode, finalize disc",

            DiscContentType.PlayStation1Game =>
                "✅ PlayStation 1 preset: DAO write mode, finalize disc (recommended for LibCrypt protection)",
            DiscContentType.PlayStation2Game =>
                "✅ PlayStation 2 preset: DAO write mode, finalize disc",
            DiscContentType.PlayStation3Game =>
                "✅ PlayStation 3 preset: Finalize disc",
            DiscContentType.PlayStation4Game or DiscContentType.PlayStation5Game =>
                "✅ PlayStation preset: Finalize disc",
            DiscContentType.SegaSaturnGame =>
                "✅ Sega Saturn preset: DAO write mode, finalize disc",
            DiscContentType.SegaDreamcastGame =>
                "✅ Sega Dreamcast preset: DAO write mode, finalize disc",
            DiscContentType.SegaMegaCdGame =>
                "✅ Sega Mega CD preset: DAO write mode, finalize disc",
            DiscContentType.PcEngineGame =>
                "✅ PC Engine preset: DAO write mode, finalize disc",
            DiscContentType.NeoGeoCdGame =>
                "✅ Neo Geo CD preset: DAO write mode, finalize disc",
            DiscContentType.AmigaCd32Game =>
                "✅ Amiga CD32 preset: DAO write mode, finalize disc",
            DiscContentType.AmigaCdtvGame =>
                "✅ Amiga CDTV preset: DAO write mode, finalize disc",
            DiscContentType.AtariJaguarCdGame =>
                "✅ Atari Jaguar CD preset: DAO write mode, finalize disc",
            DiscContentType.ThreeDOGame =>
                "✅ 3DO preset: DAO write mode, finalize disc",
            DiscContentType.CdInteractive or DiscContentType.CdInteractiveGame =>
                "✅ CD-i preset: DAO write mode, finalize disc",
            DiscContentType.XboxGame or DiscContentType.Xbox360Game =>
                "✅ Xbox preset: Finalize disc",
            DiscContentType.XboxOneGame or DiscContentType.XboxSeriesGame =>
                "✅ Xbox preset: Finalize disc",
            DiscContentType.WiiGame or DiscContentType.GameCubeGame or DiscContentType.WiiUGame =>
                "✅ Nintendo preset: Finalize disc",
            DiscContentType.PspUmdGame =>
                "✅ PSP UMD preset: Finalize disc",

            DiscContentType.DvdVideo =>
                "✅ DVD-Video preset: DAO write mode, finalize disc (required for standalone DVD player compatibility)",
            DiscContentType.DvdAudio =>
                "✅ DVD-Audio preset: DAO write mode, finalize disc",
            DiscContentType.DvdVideoRecording =>
                "✅ DVD-VR preset: DAO write mode, finalize disc",

            DiscContentType.BluRayMovie =>
                "✅ Blu-ray Movie preset: Finalize disc (required for standalone player compatibility)",
            DiscContentType.BluRay3D =>
                "✅ Blu-ray 3D preset: Finalize disc",
            DiscContentType.UhdBluRay =>
                "✅ UHD Blu-ray preset: Finalize disc",
            DiscContentType.BluRayAudio =>
                "✅ Blu-ray Audio preset: Finalize disc",
            DiscContentType.BluRayAudioVisual =>
                "✅ Blu-ray BDAV preset: Finalize disc",
            DiscContentType.BluRayJava =>
                "✅ BD-J Interactive preset: Finalize disc",

            DiscContentType.PhotoCd =>
                "✅ Photo CD preset: Finalize disc",
            DiscContentType.PhotoDvd =>
                "✅ Photo DVD preset: Finalize disc",
            DiscContentType.PhotoBluRay =>
                "✅ Photo Blu-ray preset: Finalize disc",

            DiscContentType.DataCd or DiscContentType.DataDvd or DiscContentType.DataBluRay =>
                "✅ Data disc preset: Finalize disc for maximum compatibility",

            _ => null
        };
    }

    /// <summary>
    /// Returns a human-readable preset description for the detected content type
    /// suitable for display in the Verify view.
    /// </summary>
    public static string? GetVerifyPresetDescription(DiscContentType contentType)
    {
        return contentType switch
        {
            DiscContentType.PlayStation1Game =>
                "✅ PlayStation 1 preset: Audio + subchannel verification (recommended for LibCrypt protection)",
            DiscContentType.PlayStation2Game =>
                "✅ PlayStation 2 preset: Sector-by-sector data verification",
            DiscContentType.PlayStation3Game or DiscContentType.PlayStation4Game or DiscContentType.PlayStation5Game =>
                "✅ PlayStation preset: Data sector verification",
            DiscContentType.SegaSaturnGame or DiscContentType.SegaDreamcastGame or DiscContentType.SegaMegaCdGame =>
                "✅ Sega preset: Audio verification + sector-by-sector comparison",
            DiscContentType.GameCubeGame or DiscContentType.WiiGame or DiscContentType.WiiUGame =>
                "✅ Nintendo preset: Data-only sector verification",
            DiscContentType.XboxGame or DiscContentType.Xbox360Game =>
                "✅ Xbox preset: Data-only sector verification",
            DiscContentType.XboxOneGame or DiscContentType.XboxSeriesGame =>
                "✅ Xbox preset: Data sector verification",
            DiscContentType.PcEngineGame or DiscContentType.NeoGeoCdGame or
            DiscContentType.AmigaCd32Game or DiscContentType.AmigaCdtvGame or
            DiscContentType.AtariJaguarCdGame or DiscContentType.ThreeDOGame =>
                "✅ Retro game preset: Audio verification + sector-by-sector comparison",
            DiscContentType.CdInteractive or DiscContentType.CdInteractiveGame =>
                "✅ CD-i preset: Audio verification + sector-by-sector comparison",
            DiscContentType.PspUmdGame =>
                "✅ PSP UMD preset: Data sector verification",

            DiscContentType.AudioCd =>
                "✅ Audio CD preset: Full audio track verification",
            DiscContentType.MixedModeCd or DiscContentType.EnhancedCd =>
                "✅ Mixed/Enhanced CD preset: Audio + data verification",

            DiscContentType.VideoCd or DiscContentType.SuperVideoCd =>
                "✅ Video CD preset: Sector-by-sector verification",

            DiscContentType.DvdVideo or DiscContentType.DvdAudio or DiscContentType.DvdVideoRecording =>
                "✅ DVD Video/Audio preset: Data sector verification",
            DiscContentType.BluRayMovie or DiscContentType.BluRay3D or DiscContentType.UhdBluRay =>
                "✅ Blu-ray Movie preset: Data sector verification",
            DiscContentType.BluRayAudio or DiscContentType.BluRayAudioVisual or DiscContentType.BluRayJava =>
                "✅ Blu-ray preset: Data sector verification",

            DiscContentType.DataCd or DiscContentType.DataDvd or DiscContentType.DataBluRay =>
                "✅ Data disc preset: Sector-by-sector verification",
            DiscContentType.PhotoCd or DiscContentType.PhotoDvd or DiscContentType.PhotoBluRay =>
                "✅ Photo disc preset: Data sector verification",

            _ => null
        };
    }

    /// <summary>
    /// Returns a human-readable preset description for the Copy workflow.
    /// </summary>
    public static string? GetCopyPresetDescription(DiscContentType contentType)
    {
        var format = GetRecommendedOutputFormat(contentType);
        var sectorSize = GetRecommendedSectorSize(contentType);
        var subchannel = GetRecommendedSubchannelMode(contentType);
        var presetName = GetMatchingPresetName(contentType);

        if (presetName != null)
        {
            var desc = $"✅ {presetName} preset: {format} format, {sectorSize}-byte sectors";
            if (subchannel != "None")
                desc += $", {subchannel} subchannel";
            return desc;
        }

        // Non-game presets
        return contentType switch
        {
            DiscContentType.AudioCd =>
                $"✅ Audio CD preset: {format} format, {sectorSize}-byte raw sectors for lossless audio capture",
            DiscContentType.MixedModeCd or DiscContentType.EnhancedCd =>
                $"✅ CD preset: {format} format, {sectorSize}-byte raw sectors",
            DiscContentType.VideoCd or DiscContentType.SuperVideoCd =>
                $"✅ Video CD preset: {format} format, {sectorSize}-byte raw sectors",
            DiscContentType.PhotoCd =>
                $"✅ Photo CD preset: {format} format, {sectorSize}-byte raw sectors",
            DiscContentType.CdInteractive =>
                $"✅ CD-i preset: {format} format, {sectorSize}-byte raw sectors",

            DiscContentType.DvdVideo =>
                $"✅ DVD-Video preset: {format} format, {sectorSize}-byte sectors",
            DiscContentType.DvdAudio =>
                $"✅ DVD-Audio preset: {format} format, {sectorSize}-byte sectors",

            DiscContentType.BluRayMovie or DiscContentType.BluRay3D or DiscContentType.UhdBluRay =>
                $"✅ Blu-ray preset: {format} format, {sectorSize}-byte sectors",

            DiscContentType.DataCd or DiscContentType.DataDvd or DiscContentType.DataBluRay or DiscContentType.Data =>
                $"✅ Data preset: {format} format, {sectorSize}-byte sectors",

            _ => null
        };
    }
}
