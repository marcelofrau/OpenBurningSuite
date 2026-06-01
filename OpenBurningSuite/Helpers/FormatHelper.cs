// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;

namespace OpenBurningSuite.Helpers;

/// <summary>Constants and helpers for disc formats, capacities, and descriptions.</summary>
public static class FormatHelper
{
    // ----- Disc capacity constants (in bytes) -----
    // CD capacities are derived from sector counts:  minutes × 60 × 75 frames/sec = sectors
    // Data capacity = sectors × 2048 bytes/sector (Mode 1)
    public const long CD74Capacity    = 333_000L * 2048;       // 74-min: 333,000 sectors = 681,984,000 bytes
    public const long CD80Capacity    = 360_000L * 2048;       // 80-min: 360,000 sectors = 737,280,000 bytes
    public const long CD90Capacity    = 405_000L * 2048;       // 90-min: 405,000 sectors = 829,440,000 bytes
    public const long CD99Capacity    = 445_500L * 2048;       // 99-min: 445,500 sectors = 912,384,000 bytes

    /// <summary>
    /// DVD-5 usable data capacity: 4,707,319,808 bytes (≈4.37 GiB).
    /// This is the actual usable payload reported by the UDF/ISO filesystem after
    /// accounting for error-correction overhead on a single-layer DVD.
    /// Raw sector count: 2,295,104 × 2,048 bytes/sector.
    /// </summary>
    public const long DVD5Capacity    = 4_707_319_808L;

    /// <summary>
    /// DVD-9 usable data capacity: 8,543,666,176 bytes (≈7.95 GiB).
    /// Dual-layer DVD; each layer holds ~4.37 GiB.
    /// Raw sector count: 4,171,712 × 2,048 bytes/sector.
    /// </summary>
    public const long DVD9Capacity    = 8_543_666_176L;

    public const long HDDVD15Capacity = 14_959_017_984L;    // HD DVD single-layer: 7,305,576 sectors × 2,048 (ECMA-378)
    public const long HDDVD30Capacity = 29_918_035_968L;    // HD DVD dual-layer (ECMA-378)
    /// <summary>HD DVD-RAM capacity: 4,700,372,992 bytes per ECMA-378.</summary>
    public const long HDDVDRAMCapacity = 4_700_372_992L;
    /// <summary>HD DVD-RW capacity: 14,959,017,984 bytes (same as single-layer HD DVD).</summary>
    public const long HDDVDRWCapacity = 14_959_017_984L;
    public const long BD25Capacity    = 25_025_314_816L;   // BD-25 single-layer
    public const long BD50Capacity    = 50_050_629_632L;   // BD-50 dual-layer
    public const long BD100Capacity   = 100_103_356_416L;  // BD-100 triple-layer BDXL
    public const long BD128Capacity   = 128_001_769_472L;  // BD-128 quad-layer BDXL

    // ----- Ultra HD Blu-ray (UHD BD) capacities -----
    // UHD BD uses higher density pits and tighter tracks than standard Blu-ray.
    // Per Blu-ray Disc Association UHD specification:
    //   BD-66 (dual-layer UHD): 66,000,000,000 bytes (≈61.5 GiB)
    //   BD-100 UHD (triple-layer UHD): same physical capacity as BDXL BD-100

    /// <summary>
    /// UHD Blu-ray dual-layer (BD-66) capacity: 66,000,000,000 bytes (≈61.5 GiB).
    /// Per Blu-ray Disc Association Ultra HD specification.
    /// 2 layers with higher data density than standard BD layers.
    /// </summary>
    public const long UHDBD66Capacity = 66_000_000_000L;

    /// <summary>
    /// UHD Blu-ray triple-layer (BD-100 UHD) capacity: 100,000,000,000 bytes (≈93.1 GiB).
    /// Per Blu-ray Disc Association Ultra HD specification.
    /// Same physical structure as BDXL triple-layer but marketed for UHD content.
    /// </summary>
    public const long UHDBD100Capacity = 100_000_000_000L;

    /// <summary>
    /// DVD-RAM usable data capacity: 4,700,372,992 bytes (≈4.37 GiB).
    /// Single-sided 12cm DVD-RAM per ECMA-330/ECMA-337.
    /// Raw sector count: 2,294,912 × 2,048 bytes/sector.
    /// </summary>
    public const long DVDRAMCapacity  = 4_700_372_992L;

    /// <summary>
    /// DVD+R DL usable data capacity: 8,547,991,552 bytes (≈7.96 GiB).
    /// Same as DVD+R DL physical capacity minus overhead.
    /// </summary>
    public const long DVDPlusRDLCapacity = 8_547_991_552L;

    /// <summary>
    /// DVD-R usable data capacity: 4,707,319,808 bytes (≈4.37 GiB).
    /// Same physical structure as DVD-5 (DVD-ROM single-layer).
    /// </summary>
    public const long DVDMinusRCapacity = 4_707_319_808L;

    /// <summary>
    /// DVD+R usable data capacity: 4,700,372,992 bytes (≈4.37 GiB).
    /// </summary>
    public const long DVDPlusRCapacity = 4_700_372_992L;

    /// <summary>
    /// DVD-RW usable data capacity: 4,707,319,808 bytes (≈4.37 GiB).
    /// Same as DVD-R single-layer (DVD-5).
    /// </summary>
    public const long DVDMinusRWCapacity = 4_707_319_808L;

    /// <summary>
    /// DVD+RW DL usable data capacity: 8,547,991,552 bytes (≈7.96 GiB).
    /// </summary>
    public const long DVDPlusRWDLCapacity = 8_547_991_552L;

    /// <summary>
    /// DVD-R DL (Sequential, profile 0x0015) usable data capacity: 8,543,666,176 bytes (≈7.95 GiB).
    /// Same physical structure as DVD-9.
    /// </summary>
    public const long DVDMinusRDLCapacity = 8_543_666_176L;

    /// <summary>
    /// DVD+RW usable data capacity: 4,700,372,992 bytes (≈4.37 GiB).
    /// Same as DVD-RAM single-sided.
    /// </summary>
    public const long DVDPlusRWCapacity = 4_700_372_992L;

    /// <summary>
    /// GameCube miniDVD capacity: 1,459,978,240 bytes (≈1.36 GiB).
    /// </summary>
    public const long GameCubeMiniDVDCapacity = 1_459_978_240L;

    /// <summary>
    /// Wii single-layer disc capacity: 4,699,979,776 bytes (≈4.37 GiB).
    /// </summary>
    public const long WiiSLCapacity = 4_699_979_776L;

    /// <summary>
    /// Wii dual-layer disc capacity: 8,511,160,320 bytes (≈7.93 GiB).
    /// </summary>
    public const long WiiDLCapacity = 8_511_160_320L;

    /// <summary>
    /// UMD (Universal Media Disc) single-layer capacity: 900,000,000 bytes (≈838 MiB).
    /// </summary>
    public const long UMDSingleLayerCapacity = 900_000_000L;

    /// <summary>
    /// UMD (Universal Media Disc) capacity for PSP: 1,800,000,000 bytes (≈1.68 GiB).
    /// Single-layer: 900 MB, dual-layer: 1.8 GB.
    /// </summary>
    public const long UMDDualLayerCapacity = 1_800_000_000L;

    /// <summary>
    /// GD-ROM (Gigabyte Disc) usable capacity: ~1,200,000,000 bytes (≈1.12 GiB).
    /// Used by Sega Dreamcast. The high-density area holds approximately 1 GiB of data.
    /// </summary>
    public const long GdRomCapacity = 1_200_000_000L;

    /// <summary>
    /// Mini CD (8cm disc) data capacity: approximately 210 MB.
    /// 8cm CD-R discs hold about 21 minutes of audio or ~210 MB of data.
    /// Sector count: 21 × 60 × 75 = 94,500 sectors × 2,048 = 193,536,000 bytes.
    /// </summary>
    public const long MiniCDCapacity = 94_500L * 2048;

    /// <summary>
    /// DVD-RAM double-sided capacity: 9,400,745,984 bytes (≈8.75 GiB).
    /// Two single-sided 4.7 GB surfaces on one disc per ECMA-330.
    /// </summary>
    public const long DVDRAMDoubleSidedCapacity = 9_400_745_984L;

    // ----- M-DISC capacities -----
    // M-DISC uses the same physical format as standard recordable media but with
    // an inorganic recording layer for archival longevity (estimated 1,000+ years).
    // Capacities match their standard counterparts.

    /// <summary>
    /// M-DISC DVD capacity: 4,700,372,992 bytes (≈4.37 GiB).
    /// Same physical format as DVD+R (single-layer).
    /// </summary>
    public const long MDiscDvdCapacity = 4_700_372_992L;

    /// <summary>
    /// M-DISC BD-R single-layer capacity: 25,025,314,816 bytes (≈25 GB).
    /// Same physical format as BD-R SL.
    /// </summary>
    public const long MDiscBd25Capacity = 25_025_314_816L;

    /// <summary>
    /// M-DISC BD-R dual-layer capacity: 50,050,629,632 bytes (≈50 GB).
    /// Same physical format as BD-R DL.
    /// </summary>
    public const long MDiscBd50Capacity = 50_050_629_632L;

    /// <summary>
    /// M-DISC BD-R XL triple-layer capacity: 100,103,356,416 bytes (≈100 GB).
    /// Same physical format as BD-R TL (BDXL).
    /// </summary>
    public const long MDiscBd100Capacity = 100_103_356_416L;

    // ----- VCD / SVCD capacities -----
    // Video CD (VCD) per IEC 62107 / White Book uses CD-ROM XA Mode 2 sectors.
    // Data capacity uses Mode 2 Form 2 (2328 bytes/sector) for MPEG-1 video,
    // with Mode 2 Form 1 (2048 bytes/sector) for filesystem structures.
    // A standard 74-min CD holds ~333,000 sectors; ~740 MB of VCD video data.
    // An 80-min CD holds ~360,000 sectors; ~800 MB of VCD video data.

    /// <summary>
    /// VCD data capacity on a 74-minute CD: 333,000 sectors × 2,328 bytes/sector (Mode 2 Form 2).
    /// Approximately 775 MB of MPEG-1 video data.
    /// </summary>
    public const long VCD74Capacity = 333_000L * 2328;

    /// <summary>
    /// VCD data capacity on an 80-minute CD: 360,000 sectors × 2,328 bytes/sector (Mode 2 Form 2).
    /// Approximately 838 MB of MPEG-1 video data.
    /// </summary>
    public const long VCD80Capacity = 360_000L * 2328;

    /// <summary>
    /// SVCD data capacity on a 74-minute CD: 333,000 sectors × 2,328 bytes/sector (Mode 2 Form 2).
    /// SVCD uses higher MPEG-2 bitrate, so playback time is shorter (~35-60 min on 74-min disc).
    /// </summary>
    public const long SVCD74Capacity = 333_000L * 2328;

    /// <summary>
    /// SVCD data capacity on an 80-minute CD: 360,000 sectors × 2,328 bytes/sector (Mode 2 Form 2).
    /// SVCD uses higher MPEG-2 bitrate, so playback time is shorter (~40-70 min on 80-min disc).
    /// </summary>
    public const long SVCD80Capacity = 360_000L * 2328;

    // ----- XSVCD (eXtended Super Video CD) capacities -----
    // XSVCD is an informal extension of the SVCD specification that allows
    // non-standard parameters: higher bitrates (up to 3500+ kbps), higher resolutions
    // (720×480/576), and extended GOP structures beyond IEC 62107 limits.
    // Uses the same CD-ROM XA Mode 2 physical format as SVCD.

    /// <summary>
    /// XSVCD data capacity on a 74-minute CD: 333,000 sectors × 2,328 bytes/sector (Mode 2 Form 2).
    /// Same physical capacity as SVCD; extended parameters affect quality vs. duration trade-off.
    /// </summary>
    public const long XSVCD74Capacity = 333_000L * 2328;

    /// <summary>
    /// XSVCD data capacity on an 80-minute CD: 360,000 sectors × 2,328 bytes/sector (Mode 2 Form 2).
    /// Same physical capacity as SVCD; higher bitrates reduce effective playback time.
    /// </summary>
    public const long XSVCD80Capacity = 360_000L * 2328;

    /// <summary>
    /// Size threshold (in bytes) used to distinguish CD images from DVD/Blu-ray images.
    /// Must be above the maximum possible CD capacity (99-min CD ≈ 912 MB) so that
    /// oversize CD images are correctly identified as CD-size rather than DVD-size.
    /// </summary>
    public const long CdImageSizeThresholdBytes = 950_000_000L;

    // ----- Gaming disc format constants -----

    /// <summary>
    /// PSX raw CD sector size: 2352 bytes (Mode 1 or Mode 2 with full header/EDC/ECC).
    /// </summary>
    public const int PsxRawSectorSize = 2352;

    /// <summary>
    /// PSX CD sector user data offset: 24 bytes into raw sector
    /// (12-byte sync + 4-byte header + 8-byte subheader for Mode 2).
    /// </summary>
    public const int PsxSectorUserDataOffset = 24;

    /// <summary>
    /// Maximum sector count for a standard PSX CD-ROM (71 minutes × 60 seconds × 75 sectors/second).
    /// </summary>
    public const int PsxMaxSectorCount = 319500;

    /// <summary>
    /// Number of dummy sectors appended by the PSX 80 Minute patch (6 minutes of CDDA silence).
    /// </summary>
    public const int Psx80MinDummySectors = 27000;

    /// <summary>
    /// XDVDFS (Xbox DVD File System) volume descriptor sector number.
    /// </summary>
    public const int XdvdfVolumeDescriptorSector = 32;

    /// <summary>
    /// Xbox 360 XGD2 game partition byte offset.
    /// </summary>
    public const long Xgd2GamePartitionOffset = 0x0FD90000L;

    /// <summary>
    /// Xbox 360 XGD3 game partition byte offset.
    /// </summary>
    public const long Xgd3GamePartitionOffset = 0x02080000L;

    // ----- Supported output formats for reading -----
    public static readonly string[] ReadOutputFormats =
        { "ISO", "BIN/CUE", "BIN/CUE/SBI", "NRG", "MDF/MDS", "IMG", "TOC/BIN", "CDI", "CCD/IMG/SUB", "VCD", "SVCD", "XSVCD", "XISO (Xbox)" };

    // ----- Supported disc types for building -----
    public static readonly string[] BuildDiscTypes =
        { "CD", "Mini CD", "VCD", "SVCD", "XSVCD", "DVD-5", "DVD-9", "DVD-R", "DVD-R DL", "DVD+R", "DVD+R DL",
          "DVD-RW", "DVD+RW", "DVD+RW DL", "DVD-RAM",
          "HD DVD-15", "HD DVD-30", "HD DVD-RAM", "HD DVD-RW",
          "BD-25", "BD-50", "BD-100", "BD-128",
          "UHD BD-66", "UHD BD-100",
          "M-DISC DVD", "M-DISC BD-25", "M-DISC BD-50", "M-DISC BD-100",
          "Xbox DVD", "Xbox 360 DVD" };

    // ----- Supported filesystems for building -----
    public static readonly string[] FileSystems =
        { "ISO 9660", "Joliet", "UDF 1.02", "UDF 1.5", "UDF 2.01", "UDF 2.50", "UDF 2.60", "Rock Ridge", "HFS+", "ISO 9660 + UDF", "ISO 9660 + Joliet" };

    /// <summary>
    /// Filesystem options available for on-the-fly (Build &amp; Burn) mode.
    /// These are a subset of <see cref="FileSystems"/> — the most commonly used
    /// and broadly compatible options for direct-to-disc burning.
    /// </summary>
    public static readonly string[] OnTheFlyFileSystems =
        { "ISO 9660 + Joliet", "ISO 9660", "Joliet", "UDF 1.02", "UDF 2.01", "UDF 2.50", "ISO 9660 + UDF", "Rock Ridge" };

    // ----- Speed sentinel values -----
    /// <summary>Speed value meaning "let the drive choose automatically".</summary>
    public const string SpeedAuto = "Auto";
    /// <summary>Speed value meaning "use maximum available speed".</summary>
    public const string SpeedMax  = "Max";

    // ----- 1x base speed constants (KB/s) -----
    /// <summary>1x CD speed: 150 KB/s (153,600 bytes/sec).</summary>
    public const double CdBaseSpeedKBs = 150.0;
    /// <summary>1x DVD speed: 1,385 KB/s (1,418,240 bytes/sec).</summary>
    public const double DvdBaseSpeedKBs = 1385.0;
    /// <summary>1x Blu-ray speed: 4,495 KB/s (4,602,880 bytes/sec).</summary>
    public const double BdBaseSpeedKBs = 4495.0;
    /// <summary>1x HD DVD speed: 4,568 KB/s (4,677,632 bytes/sec).</summary>
    public const double HdDvdBaseSpeedKBs = 4568.0;

    // ----- Read speeds -----
    /// <summary>Read speeds for optical drives (1x CD = 150 KB/s).</summary>
    public static readonly string[] ReadSpeeds =
        { SpeedMax, "1", "2", "4", "8", "12", "16", "24", "32", "40", "48", "52" };

    // ----- Write speeds -----
    public static readonly string[] WriteSpeeds =
        { SpeedAuto, "1x", "2x", "4x", "6x", "8x", "12x", "16x", "20x", "24x", "32x", "40x", "48x", "52x", SpeedMax };

    /// <summary>Write speeds for VCD/SVCD media (CD-based, lower speeds recommended for compatibility).</summary>
    public static readonly string[] VcdWriteSpeeds =
        { SpeedAuto, "1x", "2x", "4x", "8x", "12x", "16x", "24x", SpeedMax };

    /// <summary>Write speeds applicable to DVD media (1x DVD = 1385 KB/s).</summary>
    public static readonly string[] DvdWriteSpeeds =
        { SpeedAuto, "1x", "2x", "2.4x", "4x", "6x", "8x", "12x", "16x", "20x", "24x", SpeedMax };

    /// <summary>Write speeds applicable to Blu-ray media (1x BD = 4495 KB/s).</summary>
    public static readonly string[] BdWriteSpeeds =
        { SpeedAuto, "1x", "2x", "4x", "6x", "8x", "12x", "16x", SpeedMax };

    /// <summary>
    /// Write speeds applicable to UHD Blu-ray media.
    /// UHD BD uses the same 1x base speed as standard Blu-ray (4495 KB/s).
    /// Higher speeds are limited due to the increased data density and precision required.
    /// </summary>
    public static readonly string[] UhdBdWriteSpeeds =
        { SpeedAuto, "1x", "2x", "4x", "6x", "8x", SpeedMax };

    /// <summary>Write speeds applicable to HD DVD media (1x HD DVD = 4568 KB/s).</summary>
    public static readonly string[] HdDvdWriteSpeeds =
        { SpeedAuto, "1x", "2x", "4x", "6x", "8x", SpeedMax };

    /// <summary>
    /// Write speeds for M-DISC DVD media (limited to 4x maximum).
    /// M-DISC uses a higher-power laser and inorganic recording layer;
    /// exceeding rated speed risks incomplete engraving and data loss.
    /// </summary>
    public static readonly string[] MDiscDvdWriteSpeeds =
        { SpeedAuto, "1x", "2x", "2.4x", "4x" };

    /// <summary>
    /// Write speeds for M-DISC BD-R media (limited to 6x maximum).
    /// Lower speeds are recommended for maximum archival reliability.
    /// </summary>
    public static readonly string[] MDiscBdWriteSpeeds =
        { SpeedAuto, "1x", "2x", "4x", "6x" };

    /// <summary>Returns the appropriate write speed list for the given media type string.</summary>
    public static string[] GetWriteSpeedsForMedia(string mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType)) return WriteSpeeds;
        var upper = mediaType.ToUpperInvariant();
        // M-DISC check must come before BD/DVD checks since M-DISC names contain those strings
        if (upper.Contains("M-DISC"))
        {
            if (upper.Contains("BD")) return MDiscBdWriteSpeeds;
            return MDiscDvdWriteSpeeds;
        }
        if (upper.Contains("UHD") && (upper.Contains("BD") || upper.Contains("BLU"))) return UhdBdWriteSpeeds;
        if (upper.Contains("BD") || upper.Contains("BLU")) return BdWriteSpeeds;
        // HD DVD check must come before DVD check since "HD DVD" contains "DVD"
        if (upper.Contains("HD DVD") || upper.Contains("HD-DVD") || upper.Contains("HDDVD")) return HdDvdWriteSpeeds;
        if (upper.Contains("DVD")) return DvdWriteSpeeds;
        if (upper.Contains("VCD") || upper.Contains("SVCD") || upper.Contains("XSVCD")) return VcdWriteSpeeds;
        return WriteSpeeds; // default: CD speeds
    }

    // ----- Write modes -----
    // SAO (Session At Once) and DAO (Disc At Once) are separate modes per MMC-5 §4.9.3.
    // Both use SCSI Write Type 0x02 (Mode Page 05h byte 2), but differ in finalization:
    //   SAO: writes one session at a time; multi-session IS possible (close session, disc stays appendable).
    //        CD media only. The drive writes lead-in + tracks + lead-out for the session.
    //   DAO: writes the entire disc in one operation; no multi-session; disc always finalized.
    //        Supported by CD and DVD-R media. For DVD-R, uses Write Type 0x02 (DAO) in Mode Page 05h.
    //
    // cdrecord/cdrtools, InfraRecorder, and ImgBurn all treat these as distinct modes.
    // Reference: MMC-5 §4.9.3, MMC-5 §7.5.4.8, cdrecord -sao/-dao documentation.
    public static readonly string[] WriteModes =
        { "TAO (Track At Once)", "SAO (Session At Once)", "DAO (Disc At Once)", "RAW96R", "RAW16", "RAW96P", "Incremental" };

    // ----- Sector sizes -----
    public static readonly string[] SectorSizes =
        { "2048", "2352", "2336", "2056", "2332", "2340", "2448" };

    // ----- Subchannel modes -----
    public static readonly string[] SubchannelModes =
        { "None", "RW", "RW_RAW", "P-W" };

    // ----- Checksum types -----
    public static readonly string[] ChecksumTypes =
        { "MD5", "SHA1", "SHA256", "SHA512", "CRC32" };

    // ----- Multi-session modes -----
    public static readonly string[] MultiSessionModes =
        { "Close (Single Session)", "Start (Multi-session)", "Continue (Append)" };

    // ----- Verify modes -----
    public static readonly string[] VerifyModes =
        { "Verify Disc", "Compare Disc to Image", "Check Image Integrity" };

    // ----- Error recovery modes -----
    public static readonly string[] ErrorRecoveryModes =
        { "Yes", "No", "Abort" };

    // ----- Blank types -----
    public static readonly string[] BlankTypes =
        { "Full (Entire Disc)", "Quick (Minimal)", "Track", "Unreserve Track",
          "Track Tail", "Unclose Session", "Last Session" };

    // ----- BD-R modes -----
    public static readonly string[] BdRModes =
        { "SRM (Sequential Recording)", "SRM+POW", "RRM (Random Recording)" };

    // ----- VCD / SVCD format parameters -----

    /// <summary>Supported video standards for VCD/SVCD.</summary>
    public static readonly string[] VideoStandards = { "NTSC", "PAL", "SECAM", "Film (24fps)" };

    /// <summary>VCD resolution options per White Book (IEC 62107) specification.</summary>
    public static readonly string[] VcdResolutions = { "352x240 (NTSC)", "352x288 (PAL)" };

    /// <summary>SVCD resolution options per IEC 62107 specification.</summary>
    public static readonly string[] SvcdResolutions =
        { "480x480 (NTSC)", "480x576 (PAL)", "352x480 (NTSC Half-D1)", "352x576 (PAL Half-D1)" };

    /// <summary>
    /// VCD MPEG-1 constant bitrate: 1,150 kbit/s per White Book specification.
    /// This is the mandatory video elementary stream bitrate for all VCDs.
    /// </summary>
    public const int VcdVideoBitrateKbps = 1150;

    /// <summary>
    /// VCD MPEG-1 audio bitrate: 224 kbit/s (Layer II) per White Book specification.
    /// Mandatory stereo MPEG-1 Layer II audio at 44.1 kHz.
    /// </summary>
    public const int VcdAudioBitrateKbps = 224;

    /// <summary>
    /// VCD total mux rate: 1,394.4 kbit/s (174,300 bytes/sec) per White Book.
    /// This is the MPEG-1 system stream multiplexed rate including all overhead.
    /// </summary>
    public const int VcdMuxRateKbps = 1394;

    /// <summary>
    /// SVCD maximum video bitrate: 2,600 kbit/s per IEC 62107 specification.
    /// SVCD supports variable bitrate (VBR) MPEG-2 video up to this maximum.
    /// </summary>
    public const int SvcdMaxVideoBitrateKbps = 2600;

    /// <summary>
    /// SVCD MPEG-2 audio bitrate: typically 128-384 kbit/s (Layer II) per IEC 62107.
    /// Supports MPEG-1 Layer II or MPEG-2 multichannel audio at 44.1 kHz.
    /// </summary>
    public const int SvcdDefaultAudioBitrateKbps = 224;

    /// <summary>
    /// SVCD total mux rate: up to 2,778 kbit/s (347,250 bytes/sec) per IEC 62107.
    /// </summary>
    public const int SvcdMaxMuxRateKbps = 2778;

    // ----- XSVCD (eXtended Super Video CD) format parameters -----

    /// <summary>
    /// XSVCD maximum video bitrate: 3,500 kbit/s.
    /// XSVCD extends the SVCD maximum (2,600 kbps) to allow higher quality encoding.
    /// Compatibility varies by player; exceeding ~2,700 kbps may cause playback issues
    /// on some standalone DVD/SVCD players.
    /// </summary>
    public const int XsvcdMaxVideoBitrateKbps = 3500;

    /// <summary>
    /// XSVCD default audio bitrate: 224 kbit/s (MPEG-1 Layer II, same as SVCD).
    /// XSVCD supports up to 384 kbps audio for higher quality.
    /// </summary>
    public const int XsvcdDefaultAudioBitrateKbps = 224;

    /// <summary>
    /// XSVCD maximum audio bitrate: 384 kbit/s (MPEG-1 Layer II).
    /// Higher than standard SVCD for improved audio quality.
    /// </summary>
    public const int XsvcdMaxAudioBitrateKbps = 384;

    /// <summary>
    /// XSVCD maximum total mux rate: 3,500 kbit/s (437,500 bytes/sec).
    /// Extended from SVCD's 2,778 kbps limit.
    /// </summary>
    public const int XsvcdMaxMuxRateKbps = 3500;

    /// <summary>XSVCD resolution options (extended beyond standard SVCD).</summary>
    public static readonly string[] XsvcdResolutions =
        { "480x480 (NTSC)", "480x576 (PAL)", "352x480 (NTSC Half-D1)", "352x576 (PAL Half-D1)",
          "528x480 (NTSC Extended)", "528x576 (PAL Extended)",
          "720x480 (NTSC Full-D1)", "720x576 (PAL Full-D1)" };

    /// <summary>
    /// Estimates XSVCD playback duration in seconds for a given data size in bytes.
    /// Based on a typical XSVCD mux rate average (~85% of max for VBR average).
    /// </summary>
    public static double EstimateXsvcdPlaybackSeconds(long dataBytes)
    {
        const double avgMuxBytesPerSec = XsvcdMaxMuxRateKbps * 1000.0 / 8.0 * 0.85;
        return avgMuxBytesPerSec > 0 ? dataBytes / avgMuxBytesPerSec : 0;
    }

    /// <summary>
    /// VCD sector size: 2,352 bytes (raw CD-ROM XA Mode 2 Form 2 sector).
    /// VCD video data uses Mode 2 Form 2 sectors (2,328 bytes user data per sector).
    /// </summary>
    public const int VcdSectorSize = 2352;

    /// <summary>
    /// VCD/SVCD user data per Mode 2 Form 2 sector: 2,328 bytes.
    /// Mode 2 Form 2 sacrifices error correction for capacity (no L2 ECC).
    /// </summary>
    public const int VcdMode2Form2UserDataSize = 2328;

    /// <summary>
    /// VCD/SVCD user data per Mode 2 Form 1 sector: 2,048 bytes.
    /// Mode 2 Form 1 sectors are used for filesystem structures (ISO 9660 directory records).
    /// </summary>
    public const int VcdMode2Form1UserDataSize = 2048;

    /// <summary>Maximum number of VCD/SVCD playback items per disc (tracks 2-99).</summary>
    public const int VcdMaxPlaybackItems = 98;

    /// <summary>
    /// Estimates VCD playback duration in seconds for a given data size in bytes.
    /// Based on the fixed VCD mux rate of 174,300 bytes/sec.
    /// </summary>
    public static double EstimateVcdPlaybackSeconds(long dataBytes)
    {
        const double muxBytesPerSec = VcdMuxRateKbps * 1000.0 / 8.0;
        return muxBytesPerSec > 0 ? dataBytes / muxBytesPerSec : 0;
    }

    /// <summary>
    /// Estimates SVCD playback duration in seconds for a given data size in bytes.
    /// Based on the average SVCD mux rate of ~260,000 bytes/sec (typical VBR average).
    /// </summary>
    public static double EstimateSvcdPlaybackSeconds(long dataBytes)
    {
        const double avgMuxBytesPerSec = SvcdMaxMuxRateKbps * 1000.0 / 8.0 * 0.75; // ~75% of max for VBR average
        return avgMuxBytesPerSec > 0 ? dataBytes / avgMuxBytesPerSec : 0;
    }

    /// <summary>Returns true if the disc type is VCD, SVCD, or XSVCD.</summary>
    public static bool IsVcdOrSvcd(string discType)
    {
        if (string.IsNullOrWhiteSpace(discType)) return false;
        var upper = discType.ToUpperInvariant();
        return upper == "VCD" || upper == "SVCD" || upper == "XSVCD";
    }

    /// <summary>Returns true if the disc type is SVCD specifically.</summary>
    public static bool IsSvcd(string discType)
    {
        if (string.IsNullOrWhiteSpace(discType)) return false;
        return discType.Equals("SVCD", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Returns true if the disc type is XSVCD (eXtended Super Video CD).</summary>
    public static bool IsXsvcd(string discType)
    {
        if (string.IsNullOrWhiteSpace(discType)) return false;
        return discType.Equals("XSVCD", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Returns true if the disc type is a CD variant (CD, Mini CD, VCD, SVCD, XSVCD).</summary>
    public static bool IsCd(string discType)
    {
        if (string.IsNullOrWhiteSpace(discType)) return false;
        var upper = discType.ToUpperInvariant();
        return upper == "CD" || upper == "MINI CD" || upper == "VCD" || upper == "SVCD" || upper == "XSVCD";
    }

    /// <summary>Returns true if the disc type is a DVD variant (includes HD DVD).</summary>
    public static bool IsDvd(string discType)
    {
        if (string.IsNullOrWhiteSpace(discType)) return false;
        var upper = discType.ToUpperInvariant();
        return upper.Contains("DVD");
    }

    /// <summary>Returns true if the disc type is an HD DVD variant.</summary>
    public static bool IsHdDvd(string discType)
    {
        if (string.IsNullOrWhiteSpace(discType)) return false;
        var upper = discType.ToUpperInvariant();
        return upper.Contains("HD DVD") || upper.Contains("HD-DVD") || upper.Contains("HDDVD");
    }

    /// <summary>Returns true if the disc type is a Blu-ray variant (includes UHD BD).</summary>
    public static bool IsBluRay(string discType)
    {
        if (string.IsNullOrWhiteSpace(discType)) return false;
        var upper = discType.ToUpperInvariant();
        return upper.StartsWith("BD") || upper.Contains("BLU") || upper.StartsWith("UHD BD") || upper.StartsWith("UHD");
    }

    /// <summary>Returns true if the disc type is an Ultra HD Blu-ray variant.</summary>
    public static bool IsUhdBluRay(string discType)
    {
        if (string.IsNullOrWhiteSpace(discType)) return false;
        var upper = discType.ToUpperInvariant();
        return upper.StartsWith("UHD BD") || upper.StartsWith("UHD BLU") || upper.Contains("ULTRA HD");
    }

    /// <summary>Returns true if the disc type or media type string represents M-DISC media.</summary>
    public static bool IsMDisc(string discType)
    {
        if (string.IsNullOrWhiteSpace(discType)) return false;
        return discType.Contains("M-DISC", StringComparison.OrdinalIgnoreCase);
    }

    // ----- DVD-Video authoring constants -----

    /// <summary>Maximum DVD-Video video bitrate: 9,800 kbps per DVD Forum specification.</summary>
    public const int DvdVideoMaxBitrateKbps = 9800;

    /// <summary>Maximum DVD-Video total mux rate: 10,080 kbps per DVD Forum specification.</summary>
    public const int DvdVideoMaxMuxRateKbps = 10080;

    /// <summary>Maximum VOB file size: 1 GiB (1,073,741,824 bytes) per DVD-Video specification.</summary>
    public const long DvdVideoMaxVobSize = 1_073_741_824L;

    /// <summary>NTSC DVD-Video resolution: 720×480 pixels.</summary>
    public const string DvdNtscResolution = "720x480";

    /// <summary>PAL DVD-Video resolution: 720×576 pixels.</summary>
    public const string DvdPalResolution = "720x576";

    /// <summary>NTSC frame rate: 29.97 fps.</summary>
    public const double DvdNtscFrameRate = 29.97;

    /// <summary>PAL frame rate: 25 fps.</summary>
    public const double DvdPalFrameRate = 25.0;

    /// <summary>Maximum titles per DVD-Video disc.</summary>
    public const int DvdMaxTitles = 99;

    /// <summary>Maximum chapters per DVD title.</summary>
    public const int DvdMaxChapters = 999;

    /// <summary>Maximum audio streams per DVD title set.</summary>
    public const int DvdMaxAudioStreams = 8;

    /// <summary>Maximum subtitle streams per DVD title set.</summary>
    public const int DvdMaxSubtitleStreams = 32;

    /// <summary>Supported DVD audio codecs.</summary>
    public static readonly string[] DvdAudioCodecs = { "AC3", "MP2", "LPCM", "DTS" };

    /// <summary>DVD video aspect ratios.</summary>
    public static readonly string[] DvdAspectRatios = { "4:3", "16:9" };

    // ----- Blu-ray authoring constants -----

    /// <summary>Maximum Blu-ray H.264 video bitrate: 40,000 kbps.</summary>
    public const int BluRayMaxH264BitrateKbps = 40000;

    /// <summary>Maximum Blu-ray total mux rate: 48,000 kbps (108 Mbps for BD-ROM).</summary>
    public const int BluRayMaxMuxRateKbps = 48000;

    /// <summary>Supported Blu-ray video codecs.</summary>
    public static readonly string[] BluRayVideoCodecs = { "H264", "MPEG2", "VC1" };

    /// <summary>Supported Blu-ray audio codecs.</summary>
    public static readonly string[] BluRayAudioCodecs =
        { "LPCM", "AC3", "EAC3", "TrueHD", "DTS", "DTSHD_MA", "DTSHD_HR" };

    /// <summary>Supported Blu-ray video formats.</summary>
    public static readonly string[] BluRayVideoFormats =
        { "1080p", "1080i", "720p", "576p", "576i", "480p", "480i" };

    /// <summary>Supported Blu-ray frame rates.</summary>
    public static readonly string[] BluRayFrameRates =
        { "23.976", "24", "25", "29.97", "50", "59.94" };

    /// <summary>Maximum audio streams per Blu-ray play item.</summary>
    public const int BluRayMaxAudioStreams = 32;

    /// <summary>Maximum subtitle streams per Blu-ray play item.</summary>
    public const int BluRayMaxSubtitleStreams = 255;

    /// <summary>Blu-ray TS packet size (4-byte header + 188-byte TS packet).</summary>
    public const int BluRayTsPacketSize = 192;

    // ----- BDAV (Blu-ray Audio/Visual recording) constants -----

    /// <summary>
    /// BDAV MPEG-2 TS packet size: identical to BDMV (192 bytes).
    /// 4-byte arrival time stamp header + 188-byte MPEG-2 TS packet.
    /// Per Blu-ray Disc Association specification Part 3-2.
    /// </summary>
    public const int BdavTsPacketSize = 192;

    /// <summary>
    /// BDAV supported video codecs. BDAV supports the same codecs as BDMV
    /// (H.264/AVC, MPEG-2, VC-1) per BDA spec Part 3-2, section 5.3.
    /// </summary>
    public static readonly string[] BdavVideoCodecs = { "H264", "MPEG2", "VC1" };

    /// <summary>
    /// BDAV supported audio codecs. BDAV supports a subset of BDMV audio:
    /// LPCM, AC-3, and DTS are mandatory; E-AC-3 is optional.
    /// Per BDA spec Part 3-2, section 5.4.
    /// </summary>
    public static readonly string[] BdavAudioCodecs = { "LPCM", "AC3", "EAC3", "DTS" };

    /// <summary>BDAV supported video formats (same as BDMV subset for recording).</summary>
    public static readonly string[] BdavVideoFormats =
        { "1080i", "720p", "576i", "480i", "1080p", "576p", "480p" };

    /// <summary>BDAV supported frame rates for recording.</summary>
    public static readonly string[] BdavFrameRates =
        { "23.976", "24", "25", "29.97", "50", "59.94" };

    /// <summary>Maximum BDAV recording bitrate: 28 Mbps (limited vs BDMV's 48 Mbps).</summary>
    public const int BdavMaxBitrateKbps = 28000;

    /// <summary>Returns true if the disc type string indicates BDAV recording format.</summary>
    public static bool IsBdav(string discType)
    {
        if (string.IsNullOrWhiteSpace(discType)) return false;
        return discType.Contains("BDAV", StringComparison.OrdinalIgnoreCase);
    }

    // ----- Blu-ray 3D (Stereoscopic) constants — Profile 5.0 -----

    /// <summary>
    /// Blu-ray 3D (Profile 5.0) supported video codecs.
    /// MVC (Multiview Video Coding) is the primary codec for frame-packed 3D.
    /// H.264 base view + MVC dependent view.
    /// Frame-compatible modes (SideBySide, TopBottom) use standard H.264.
    /// </summary>
    public static readonly string[] BluRay3DVideoCodecs = { "MVC", "H264" };

    /// <summary>
    /// Blu-ray 3D content types per specification.
    /// </summary>
    public static readonly string[] BluRay3DModes =
        { "FramePacked", "SideBySide", "TopBottom" };

    /// <summary>MVC stream type identifier (0x20) per MPEG-2 Systems specification for H.264 MVC.</summary>
    public const byte MvcStreamType = 0x20;

    /// <summary>MVC base view stream PID (0x1011) — same as primary video.</summary>
    public const ushort MvcBaseViewPid = 0x1011;

    /// <summary>MVC dependent view stream PID (0x1012) — secondary video for stereoscopic.</summary>
    public const ushort MvcDependentViewPid = 0x1012;

    // ----- Ultra HD Blu-ray (UHD BD) authoring constants -----

    /// <summary>
    /// UHD Blu-ray mandatory video codec: HEVC (H.265) Main10 Profile.
    /// Per Blu-ray Disc Association UHD specification, all UHD BD content
    /// must use HEVC/H.265 with 10-bit color depth.
    /// </summary>
    public static readonly string[] UhdBdVideoCodecs = { "HEVC" };

    /// <summary>
    /// UHD Blu-ray supported audio codecs.
    /// Mandatory (player must decode): LPCM, AC3 (Dolby Digital), DTS.
    /// Optional: EAC3 (Dolby Digital Plus), TrueHD (Dolby TrueHD with Atmos),
    ///           DTS-HD Master Audio (with DTS:X), DTS-HD High Resolution.
    /// </summary>
    public static readonly string[] UhdBdAudioCodecs =
        { "LPCM", "AC3", "EAC3", "TrueHD", "DTS", "DTSHD_MA", "DTSHD_HR" };

    /// <summary>
    /// UHD Blu-ray supported video formats (resolutions).
    /// Primary: 2160p (3840×2160) at various frame rates.
    /// Also supports 1080p for SDR content compatibility.
    /// </summary>
    public static readonly string[] UhdBdVideoFormats =
        { "2160p", "1080p" };

    /// <summary>
    /// UHD Blu-ray supported frame rates.
    /// 2160p supports 23.976, 24, 25, 29.97, 50, 59.94 fps.
    /// </summary>
    public static readonly string[] UhdBdFrameRates =
        { "23.976", "24", "25", "29.97", "50", "59.94" };

    /// <summary>
    /// UHD Blu-ray HDR (High Dynamic Range) modes.
    /// HDR10 is mandatory; Dolby Vision and HDR10+ are optional.
    /// </summary>
    public static readonly string[] UhdBdHdrModes =
        { "HDR10", "DolbyVision", "HDR10Plus", "HLG" };

    /// <summary>
    /// UHD Blu-ray maximum HEVC video bitrate: 100,000 kbps (100 Mbps).
    /// Maximum mux rate varies by disc capacity:
    ///   BD-66 (dual-layer UHD): ~108 Mbps
    ///   BD-100 (triple-layer UHD): ~128 Mbps
    /// </summary>
    public const int UhdBdMaxH265BitrateKbps = 100000;

    /// <summary>
    /// UHD Blu-ray maximum total mux rate: 128,000 kbps (128 Mbps) for BD-100 UHD.
    /// For BD-66 UHD, the practical maximum is approximately 108 Mbps.
    /// </summary>
    public const int UhdBdMaxMuxRateKbps = 128000;

    /// <summary>
    /// UHD Blu-ray color space: BT.2020 (Rec. 2020) wide color gamut.
    /// </summary>
    public static readonly string[] UhdBdColorSpaces = { "BT.2020", "BT.709" };

    /// <summary>
    /// UHD Blu-ray color depth: 10-bit (mandatory for HEVC Main10 Profile).
    /// </summary>
    public const int UhdBdColorDepthBits = 10;

    /// <summary>
    /// UHD Blu-ray chroma subsampling: 4:2:0 (mandatory for HEVC Main10).
    /// </summary>
    public const string UhdBdChromaSubsampling = "4:2:0";

    // ----- Video container format support -----
    // These extensions represent video container formats that can be used as input
    // for video disc workflows (DVD, Blu-ray, BDAV, BD3D, VCD, SVCD).
    // FFmpeg handles demuxing/remuxing from these containers transparently.

    /// <summary>
    /// MPEG Transport Stream (.ts, .mts, .m2ts) file extensions.
    /// MPEG-2 TS is the native container for Blu-ray and broadcast video.
    /// Per ISO/IEC 13818-1 (MPEG-2 Systems), TS packets are 188 bytes
    /// (or 192 bytes with arrival timestamp header for Blu-ray).
    /// Contains multiplexed video (H.264/MPEG-2/VC-1), audio (AC-3/DTS/LPCM),
    /// and subtitle (PGS) elementary streams with Program-specific information (PAT/PMT).
    /// </summary>
    public static readonly string[] TsExtensions = { ".ts", ".mts", ".m2ts", ".m2t" };

    /// <summary>
    /// MPEG-4 Part 14 (.mp4, .m4v, .m4a) file extensions.
    /// Per ISO/IEC 14496-14, MP4 is based on the ISO Base Media File Format (ISO/IEC 14496-12).
    /// Supports H.264/AVC, H.265/HEVC, MPEG-4 ASP video and AAC, AC-3, E-AC-3 audio.
    /// Uses a hierarchical box (atom) structure: ftyp, moov (movie metadata),
    /// mdat (media data), with optional moof for fragmented MP4.
    /// </summary>
    public static readonly string[] Mp4Extensions = { ".mp4", ".m4v", ".m4a" };

    /// <summary>
    /// Matroska (.mkv, .mka, .mk3d) file extensions.
    /// Per Matroska specification (IETF RFC 8794 for WebM subset, matroska.org for full spec).
    /// EBML-based container supporting virtually any video/audio codec combination.
    /// Supports H.264, H.265, VP8/VP9, AV1, MPEG-2, VC-1 video and
    /// AAC, AC-3, DTS, FLAC, Vorbis, Opus, LPCM audio.
    /// Features: multiple audio/subtitle tracks, chapters, tags, attachments.
    /// </summary>
    public static readonly string[] MkvExtensions = { ".mkv", ".mka", ".mk3d" };

    /// <summary>
    /// WebM (.webm) file extensions.
    /// WebM is a subset of the Matroska container restricted to VP8/VP9/AV1 video
    /// and Vorbis/Opus audio per the WebM specification (webmproject.org).
    /// Uses the same EBML structure as Matroska but with restricted codec set.
    /// </summary>
    public static readonly string[] WebmExtensions = { ".webm" };

    /// <summary>
    /// AVI (.avi) file extensions.
    /// Audio Video Interleave per Microsoft RIFF specification (1992).
    /// Uses a RIFF (Resource Interchange File Format) container with interleaved
    /// audio and video chunks. Supports virtually any video codec (DivX, Xvid,
    /// H.264, MPEG-4 ASP, MJPEG, etc.) and audio codec (MP3, AC-3, PCM, etc.)
    /// via installed VfW (Video for Windows) or DirectShow codecs.
    /// AVI 2.0 (OpenDML) extends the original 1.0 spec to support files &gt; 2 GiB
    /// via the 'odml' super-index extension.
    /// Maximum interleave granularity is per-frame; no native B-frame reordering support.
    /// </summary>
    public static readonly string[] AviExtensions = { ".avi" };

    /// <summary>
    /// QuickTime File Format (.mov, .qt) file extensions.
    /// Apple QuickTime File Format per Apple's QuickTime File Format Specification (2001).
    /// Uses a hierarchical atom (box) structure: ftyp, moov (movie resource), mdat (media data).
    /// MP4 (ISO 14496-14) is derived from this format via ISO Base Media File Format (ISO 14496-12).
    /// Supports H.264/AVC, H.265/HEVC, ProRes, MPEG-4 ASP, Apple Animation, Sorenson video
    /// and AAC, AC-3, ALAC, LPCM, MP3 audio.
    /// Features: multiple tracks, edit lists, reference movies, timecode tracks, chapters.
    /// The .qt extension is a legacy alias used by older QuickTime versions.
    /// </summary>
    public static readonly string[] MovExtensions = { ".mov", ".qt" };

    /// <summary>
    /// Ogg container (.ogv, .ogx) video file extensions.
    /// Per Xiph.Org Ogg specification (RFC 3533, RFC 5334).
    /// Ogg is a free, open bitstream container using logical bitstream multiplexing.
    /// Video codecs: Theora (lossy, based on VP3), Dirac/Schroedinger, VP8 (via WebM mapping).
    /// Audio codecs: Vorbis (lossy), FLAC (lossless), Opus (low-latency lossy/lossless).
    /// .ogv — Ogg Video (contains at least one Theora, Dirac, or other video stream).
    /// .ogx — Ogg Multiplex (generic extension for multiplexed application streams).
    /// Ogg uses framing via granule positions for seeking; each logical stream has a serial number.
    /// </summary>
    public static readonly string[] OggVideoExtensions = { ".ogv", ".ogx" };

    /// <summary>
    /// DivX Media Format (.divx) file extension.
    /// DivX Media Format (DMF) is an AVI-compatible container with DivX-specific extensions.
    /// Based on the RIFF/AVI structure but adds support for DivX-specific metadata,
    /// interactive menus (DivX Media Format Profile), multiple subtitle tracks, and
    /// chapter information via the DivX Plus HD profile.
    /// Video: MPEG-4 ASP (DivX 4/5/6), H.264/AVC (DivX Plus HD), HEVC (DivX HEVC Ultra HD).
    /// Audio: MP3, AC-3, AAC, DTS.
    /// FFmpeg demuxes .divx files identically to AVI since the container structure is compatible.
    /// </summary>
    public static readonly string[] DivxExtensions = { ".divx" };

    /// <summary>
    /// All supported video input container extensions for disc workflows.
    /// Includes MPEG-PS (DVD native), MPEG-TS (Blu-ray native), MP4, MKV, WebM,
    /// AVI, QuickTime/MOV, Ogg, DivX, and common WMV/FLV containers supported by FFmpeg.
    /// </summary>
    public static readonly HashSet<string> SupportedVideoInputExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // MPEG Program Stream (DVD native format)
            ".mpg", ".mpeg", ".vob", ".m2v", ".m1v", ".mp2", ".dat",
            // DVD-Video structure files (per DVD Forum ECMA-267)
            ".ifo", ".bup",
            // MPEG Transport Stream (Blu-ray native format)
            ".ts", ".mts", ".m2ts", ".m2t",
            // Blu-ray structure files (per BDA specification)
            ".mpls", ".clpi",
            // MPEG-4 Part 14 (ISO 14496-14)
            ".mp4", ".m4v",
            // Matroska (RFC 8794 / matroska.org)
            ".mkv", ".mk3d",
            // WebM (webmproject.org — Matroska subset)
            ".webm",
            // AVI (Microsoft RIFF-based — AVI 1.0 / OpenDML 2.0)
            ".avi",
            // QuickTime / MOV (Apple QuickTime File Format)
            ".mov", ".qt",
            // Ogg Video (Xiph.Org — RFC 3533 / RFC 5334)
            ".ogv", ".ogx",
            // DivX Media Format (AVI-compatible with DivX extensions)
            ".divx",
            // Windows Media Video
            ".wmv", ".asf",
            // Flash Video
            ".flv", ".f4v",
            // 3GPP / 3GPP2 mobile video
            ".3gp", ".3g2"
        };

    /// <summary>
    /// Returns true if the given file extension is a recognized video container format
    /// supported as input for video disc workflows.
    /// </summary>
    /// <param name="extension">File extension including the dot (e.g., ".mp4").</param>
    public static bool IsSupportedVideoInput(string extension)
    {
        return SupportedVideoInputExtensions.Contains(extension);
    }

    /// <summary>
    /// Identifies the container type from a file extension.
    /// Returns a normalized container name: "MPEG-PS", "MPEG-TS", "MP4", "MKV", "WebM",
    /// "AVI", "MOV", "OGG", "DIVX", "WMV", "FLV", "3GP", or "Unknown".
    /// </summary>
    /// <param name="extension">File extension including the dot (e.g., ".mp4").</param>
    public static string GetContainerType(string extension)
    {
        if (string.IsNullOrEmpty(extension)) return "Unknown";
        var ext = extension.ToUpperInvariant();
        return ext switch
        {
            ".MPG" or ".MPEG" or ".VOB" or ".M2V" or ".M1V" or ".MP2" or ".DAT" => "MPEG-PS",
            ".IFO" or ".BUP" => "DVD-IFO",
            ".TS" or ".MTS" or ".M2TS" or ".M2T" => "MPEG-TS",
            ".MPLS" => "BD-MPLS",
            ".CLPI" => "BD-CLPI",
            ".MP4" or ".M4V" => "MP4",
            ".MKV" or ".MK3D" => "MKV",
            ".WEBM" => "WebM",
            ".AVI" => "AVI",
            ".MOV" or ".QT" => "MOV",
            ".OGV" or ".OGX" => "OGG",
            ".DIVX" => "DIVX",
            ".WMV" or ".ASF" => "WMV",
            ".FLV" or ".F4V" => "FLV",
            ".3GP" or ".3G2" => "3GP",
            _ => "Unknown"
        };
    }

    /// <summary>
    /// Returns true if the given file extension represents a container format
    /// that requires transcoding to MPEG-2 Program Stream output.
    /// MPEG-PS (.mpg, .mpeg, .vob) files are already in the correct container;
    /// all other containers (TS, MP4, MKV, WebM, AVI, MOV, etc.) require transcoding.
    /// Used by DVD, VCD, and SVCD workflows that require MPEG-PS input.
    /// </summary>
    public static bool RequiresTranscodeToMpegPs(string extension)
    {
        var containerType = GetContainerType(extension);
        return containerType != "MPEG-PS";
    }

    /// <summary>
    /// Returns true if the given file extension represents a container format
    /// that requires transcoding to MPEG-2 Transport Stream output.
    /// MPEG-TS (.ts, .m2ts) files are already in the correct container;
    /// all other containers (MP4, MKV, WebM, AVI, MOV, etc.) require transcoding.
    /// Used by Blu-ray and BDAV workflows that require MPEG-TS input.
    /// </summary>
    public static bool RequiresTranscodeToMpegTs(string extension)
    {
        var containerType = GetContainerType(extension);
        return containerType != "MPEG-TS";
    }

    // ----- Gaming system presets -----
    public static readonly IReadOnlyDictionary<string, GamingPreset> GamingPresets =
        new Dictionary<string, GamingPreset>
        {
            ["PlayStation 1"] = new GamingPreset
            {
                Name = "PlayStation 1",
                SectorSize = 2352,
                SubchannelMode = "RW_RAW",
                ReadSpeed = "4",
                WriteSpeed = "4x",
                WriteMode = "DAO (Disc At Once)",
                ErrorRecovery = "Yes",
                Retries = 5,
                AudioParanoia = true,
                Description = "2352-byte raw sectors with RW_RAW subchannel for LibCrypt & copy-protection support."
            },
            ["PlayStation 2"] = new GamingPreset
            {
                Name = "PlayStation 2",
                SectorSize = 2048,
                SubchannelMode = "None",
                ReadSpeed = "4",
                WriteSpeed = "4x",
                WriteMode = "DAO (Disc At Once)",
                ErrorRecovery = "Yes",
                Retries = 3,
                OutputFormat = "ISO",
                Description = "2048-byte sectors. DVD titles may require protection bypass at drive level."
            },
            ["PlayStation 3"] = new GamingPreset
            {
                Name = "PlayStation 3",
                SectorSize = 2048,
                SubchannelMode = "None",
                ReadSpeed = "2",
                ErrorRecovery = "Yes",
                Retries = 3,
                OutputFormat = "ISO",
                Description = "Blu-ray. Encrypted titles cannot be backed up with standard tools."
            },
            ["PlayStation 4/5"] = new GamingPreset
            {
                Name = "PlayStation 4/5",
                SectorSize = 2048,
                SubchannelMode = "None",
                ReadSpeed = "2",
                ErrorRecovery = "Yes",
                Retries = 3,
                OutputFormat = "ISO",
                Description = "Blu-ray encrypted discs. For preservation use only."
            },
            ["Sega Saturn"] = new GamingPreset
            {
                Name = "Sega Saturn",
                SectorSize = 2352,
                SubchannelMode = "RW",
                ReadSpeed = "4",
                WriteSpeed = "4x",
                WriteMode = "DAO (Disc At Once)",
                ErrorRecovery = "Yes",
                Retries = 5,
                Description = "Mixed-mode CD. First track is data (Mode 1), rest are audio. " +
                    "Raw 2352-byte sectors preserve complete disc structure."
            },
            ["Sega Dreamcast"] = new GamingPreset
            {
                Name = "Sega Dreamcast",
                SectorSize = 2352,
                SubchannelMode = "RW",
                ReadSpeed = "2",
                WriteSpeed = "2x",
                WriteMode = "DAO (Disc At Once)",
                ErrorRecovery = "Yes",
                Retries = 5,
                Description = "GD-ROM (~1.2 GB usable). Use 2352-byte raw sectors for full preservation."
            },
            ["Sega Mega CD"] = new GamingPreset
            {
                Name = "Sega Mega CD",
                SectorSize = 2352,
                SubchannelMode = "RW",
                ReadSpeed = "4",
                WriteSpeed = "4x",
                WriteMode = "DAO (Disc At Once)",
                ErrorRecovery = "Yes",
                Retries = 3,
                AudioParanoia = true,
                Description = "Mixed-mode audio+data disc requiring 2352-byte sectors."
            },
            ["Xbox"] = new GamingPreset
            {
                Name = "Xbox",
                SectorSize = 2048,
                SubchannelMode = "None",
                ReadSpeed = "2",
                WriteSpeed = "4x",
                WriteMode = "DAO (Disc At Once)",
                ErrorRecovery = "Yes",
                Retries = 3,
                OutputFormat = "XISO (Xbox)",
                Description = "DVD-5/DVD-9. Security Sector (SS) region requires compatible drive."
            },
            ["Xbox 360"] = new GamingPreset
            {
                Name = "Xbox 360",
                SectorSize = 2048,
                SubchannelMode = "None",
                ReadSpeed = "2",
                WriteSpeed = "4x",
                WriteMode = "DAO (Disc At Once)",
                ErrorRecovery = "Yes",
                Retries = 3,
                OutputFormat = "XISO (Xbox)",
                Description = "Dual-layer DVD with security sectors. Requires Kreon-compatible drive " +
                    "or unlocked firmware for full disc access."
            },
            ["GameCube"] = new GamingPreset
            {
                Name = "GameCube",
                SectorSize = 2048,
                SubchannelMode = "None",
                ReadSpeed = "2",
                WriteSpeed = "2x",
                WriteMode = "DAO (Disc At Once)",
                ErrorRecovery = "Yes",
                Retries = 3,
                OutputFormat = "ISO",
                Description = "Nintendo miniDVD (1.46 GB). Requires compatible drive or Wii with GC mode."
            },
            ["Wii"] = new GamingPreset
            {
                Name = "Wii",
                SectorSize = 2048,
                SubchannelMode = "None",
                ReadSpeed = "2",
                WriteSpeed = "2x",
                WriteMode = "DAO (Disc At Once)",
                ErrorRecovery = "Yes",
                Retries = 3,
                OutputFormat = "ISO",
                Description = "Wii optical disc. Use Wii with Homebrew or compatible drive."
            },
            ["3DO"] = new GamingPreset
            {
                Name = "3DO",
                SectorSize = 2352,
                SubchannelMode = "None",
                ReadSpeed = "4",
                WriteSpeed = "4x",
                WriteMode = "DAO (Disc At Once)",
                ErrorRecovery = "Yes",
                Retries = 3,
                Description = "Standard CD-ROM Mode 1 data disc. Use 2352-byte raw sectors for full preservation."
            },
            ["PC Engine / TurboGrafx-CD"] = new GamingPreset
            {
                Name = "PC Engine / TurboGrafx-CD",
                SectorSize = 2352,
                SubchannelMode = "RW",
                ReadSpeed = "4",
                WriteSpeed = "4x",
                WriteMode = "DAO (Disc At Once)",
                ErrorRecovery = "Yes",
                Retries = 3,
                AudioParanoia = true,
                Description = "Mixed-mode CD with data track 1 followed by audio tracks."
            },
            ["Neo Geo CD"] = new GamingPreset
            {
                Name = "Neo Geo CD",
                SectorSize = 2352,
                SubchannelMode = "RW",
                ReadSpeed = "4",
                WriteSpeed = "4x",
                WriteMode = "DAO (Disc At Once)",
                ErrorRecovery = "Yes",
                Retries = 3,
                Description = "ISO 9660 data disc. Use 2352-byte raw sectors with subchannel for full preservation."
            },
            ["Atari Jaguar CD"] = new GamingPreset
            {
                Name = "Atari Jaguar CD",
                SectorSize = 2352,
                SubchannelMode = "RW",
                ReadSpeed = "4",
                WriteSpeed = "4x",
                WriteMode = "DAO (Disc At Once)",
                ErrorRecovery = "Yes",
                Retries = 3,
                AudioParanoia = true,
                Description = "Mixed-mode CD. Boot track is audio. Use 2352-byte raw sectors to preserve audio tracks."
            },
            ["PSP (UMD)"] = new GamingPreset
            {
                Name = "PSP (UMD)",
                SectorSize = 2048,
                SubchannelMode = "None",
                ReadSpeed = "2",
                ErrorRecovery = "Yes",
                Retries = 3,
                OutputFormat = "ISO",
                Description = "Universal Media Disc (UMD). Requires compatible USB drive or dumped via PSP homebrew. " +
                    "ISO 9660 format, up to 1.8 GB dual-layer."
            },
            ["Wii U"] = new GamingPreset
            {
                Name = "Wii U",
                SectorSize = 2048,
                SubchannelMode = "None",
                ReadSpeed = "2",
                ErrorRecovery = "Yes",
                Retries = 3,
                OutputFormat = "ISO",
                Description = "Wii U proprietary 25 GB optical disc. Requires compatible drive for raw dumping. " +
                    "Standard Blu-ray drives cannot read these discs."
            },
            ["CD-i (Philips)"] = new GamingPreset
            {
                Name = "CD-i (Philips)",
                SectorSize = 2352,
                SubchannelMode = "RW",
                ReadSpeed = "4",
                WriteSpeed = "4x",
                WriteMode = "DAO (Disc At Once)",
                ErrorRecovery = "Yes",
                Retries = 3,
                Description = "CD-i uses Mode 2 Form 1/Form 2 sectors. Use 2352-byte raw sectors for full preservation."
            },
            ["Amiga CD32"] = new GamingPreset
            {
                Name = "Amiga CD32",
                SectorSize = 2352,
                SubchannelMode = "RW",
                ReadSpeed = "4",
                WriteSpeed = "4x",
                WriteMode = "DAO (Disc At Once)",
                ErrorRecovery = "Yes",
                Retries = 3,
                Description = "Amiga CD32 console disc. Standard CD-ROM Mode 1 with Amiga filesystem (ISO 9660 + " +
                    "custom boot block). Use 2352-byte raw sectors with RW subchannel for full preservation."
            },
            ["Amiga CDTV"] = new GamingPreset
            {
                Name = "Amiga CDTV",
                SectorSize = 2352,
                SubchannelMode = "RW",
                ReadSpeed = "4",
                WriteSpeed = "4x",
                WriteMode = "DAO (Disc At Once)",
                ErrorRecovery = "Yes",
                Retries = 3,
                Description = "Commodore CDTV (1991) disc. Standard CD-ROM with Amiga filesystem. " +
                    "Use 2352-byte raw sectors with RW subchannel for full preservation."
            },
            ["Xbox One/Series"] = new GamingPreset
            {
                Name = "Xbox One/Series",
                SectorSize = 2048,
                SubchannelMode = "None",
                ReadSpeed = "2",
                ErrorRecovery = "Yes",
                Retries = 3,
                OutputFormat = "ISO",
                Description = "Blu-ray encrypted discs (50 GB). For preservation reference only."
            },
        };

    /// <summary>Returns capacity bytes for a given disc type string.</summary>
    public static long GetCapacity(string discType) => discType switch
    {
        "CD"         => CD80Capacity,
        "Mini CD"    => MiniCDCapacity,
        "VCD"        => VCD80Capacity,
        "SVCD"       => SVCD80Capacity,
        "XSVCD"      => XSVCD80Capacity,
        "DVD-5"      => DVD5Capacity,
        "DVD-9"      => DVD9Capacity,
        "DVD-R"      => DVDMinusRCapacity,
        "DVD+R"      => DVDPlusRCapacity,
        "DVD-RW"     => DVDMinusRWCapacity,
        "DVD-RAM"    => DVDRAMCapacity,
        "DVD+RW"     => DVDPlusRWCapacity,
        "DVD+RW DL"  => DVDPlusRWDLCapacity,
        "DVD+R DL"   => DVDPlusRDLCapacity,
        "DVD-R DL"   => DVDMinusRDLCapacity,
        "HD DVD-15"  => HDDVD15Capacity,
        "HD DVD-30"  => HDDVD30Capacity,
        "HD DVD-RAM" => HDDVDRAMCapacity,
        "HD DVD-RW"  => HDDVDRWCapacity,
        "BD-25"      => BD25Capacity,
        "BD-50"      => BD50Capacity,
        "BD-100"     => BD100Capacity,
        "BD-128"     => BD128Capacity,
        "UHD BD-66"  => UHDBD66Capacity,
        "UHD BD-100" => UHDBD100Capacity,
        "M-DISC DVD"  => MDiscDvdCapacity,
        "M-DISC BD-25" => MDiscBd25Capacity,
        "M-DISC BD-50" => MDiscBd50Capacity,
        "M-DISC BD-100" => MDiscBd100Capacity,
        "Xbox DVD"     => DVD5Capacity,
        "Xbox 360 DVD" => DVD9Capacity,
        _            => CD80Capacity
    };

    /// <summary>Returns a human-readable size string.</summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes < 0) return bytes == long.MinValue ? "-8.00 EB" : $"-{FormatBytes(-bytes)}";
        if (bytes >= 1_073_741_824L) return $"{bytes / 1_073_741_824.0:F2} GB";
        if (bytes >= 1_048_576L)     return $"{bytes / 1_048_576.0:F0} MB";
        if (bytes >= 1024L)          return $"{bytes / 1024.0:F0} KB";
        return $"{bytes} B";
    }

    // ----- Time formatting helpers -----

    /// <summary>
    /// Formats a <see cref="TimeSpan"/> for ETA/Elapsed display.
    /// Returns "h:mm:ss" when the duration is 1 hour or more, otherwise "mm:ss".
    /// Operations on large BD-R/BDXL/UHD BD discs can easily exceed 59 minutes,
    /// so the format must handle hours to avoid truncation.
    /// </summary>
    public static string FormatTimeSpan(TimeSpan ts) =>
        ts.TotalHours >= 1
            ? ts.ToString(@"h\:mm\:ss")
            : ts.ToString(@"mm\:ss");

    /// <summary>
    /// Estimates the remaining time based on elapsed time and progress so far.
    /// Returns <see cref="TimeSpan.Zero"/> if not enough data to produce a reliable estimate
    /// (requires at least 1 second of elapsed time and non-zero progress).
    /// </summary>
    public static TimeSpan EstimateRemaining(TimeSpan elapsed, long completed, long total)
    {
        if (completed <= 0 || total <= 0 || elapsed.TotalSeconds < 1)
            return TimeSpan.Zero;
        var rate = (double)completed / elapsed.TotalSeconds;
        if (rate <= 0) return TimeSpan.Zero;
        var remaining = (total - completed) / rate;
        return TimeSpan.FromSeconds(Math.Max(0, remaining));
    }

    // ----- Disc type / file system / format compatibility helpers -----

    /// <summary>
    /// Returns the file systems that are compatible with the given disc type.
    /// CD-based discs support ISO 9660, Joliet, Rock Ridge, HFS+ and limited UDF.
    /// DVD discs support ISO 9660, Joliet, UDF up to 2.01, Rock Ridge, HFS+ and bridge formats.
    /// Blu-ray discs require UDF 2.50 or 2.60 (BD spec mandates UDF 2.50+).
    /// HD DVD discs use UDF 2.50.
    /// M-DISC uses the same filesystem as its base format.
    /// Xbox discs use ISO 9660 (XDVDFS is handled internally).
    /// </summary>
    public static string[] GetCompatibleFileSystems(string discType)
    {
        if (string.IsNullOrWhiteSpace(discType)) return FileSystems;

        // VCD/SVCD/XSVCD: fixed format per White Book / IEC 62107 — filesystem is implicit
        if (IsVcdOrSvcd(discType))
            return new[] { "ISO 9660" };

        // Xbox discs use ISO 9660 (XDVDFS is applied internally by build/burn engine)
        if (discType.Contains("Xbox", StringComparison.OrdinalIgnoreCase))
            return new[] { "ISO 9660" };

        // M-DISC BD → same as BD
        if (discType.Contains("M-DISC BD", StringComparison.OrdinalIgnoreCase))
            return new[] { "UDF 2.50", "UDF 2.60" };

        // M-DISC DVD → same as DVD
        if (IsMDisc(discType))
            return new[] { "ISO 9660", "Joliet", "UDF 1.02", "UDF 1.5", "UDF 2.01", "ISO 9660 + UDF", "ISO 9660 + Joliet", "Rock Ridge", "HFS+" };

        // UHD Blu-ray (BD-66, BD-100 UHD) — requires UDF 2.50 or 2.60
        if (IsUhdBluRay(discType))
            return new[] { "UDF 2.50", "UDF 2.60" };

        // Blu-ray (BD-25, BD-50, BD-100, BD-128)
        if (IsBluRay(discType))
            return new[] { "UDF 2.50", "UDF 2.60" };

        // HD DVD
        if (IsHdDvd(discType))
            return new[] { "UDF 2.50", "UDF 2.60", "UDF 2.01" };

        // DVD variants (DVD-5, DVD-9, DVD-R, DVD+R, DVD-RW, DVD+RW, DVD-RAM, etc.)
        if (IsDvd(discType))
            return new[] { "ISO 9660", "Joliet", "UDF 1.02", "UDF 1.5", "UDF 2.01", "ISO 9660 + UDF", "ISO 9660 + Joliet", "Rock Ridge", "HFS+" };

        // CD, Mini CD and default
        return new[] { "ISO 9660", "Joliet", "UDF 1.02", "UDF 1.5", "ISO 9660 + UDF", "ISO 9660 + Joliet", "Rock Ridge", "HFS+" };
    }

    /// <summary>
    /// Returns the disc types that are compatible with the given file system.
    /// </summary>
    public static string[] GetCompatibleDiscTypes(string fileSystem)
    {
        if (string.IsNullOrWhiteSpace(fileSystem)) return BuildDiscTypes;

        var result = new List<string>();
        foreach (var dt in BuildDiscTypes)
        {
            var compatible = GetCompatibleFileSystems(dt);
            if (Array.Exists(compatible, fs => string.Equals(fs, fileSystem, StringComparison.OrdinalIgnoreCase)))
                result.Add(dt);
        }
        return result.ToArray();
    }

    /// <summary>
    /// Returns the default file system for a given disc type.
    /// Blu-ray → UDF 2.50, HD DVD → UDF 2.50, DVD → ISO 9660 + Joliet, CD → ISO 9660 + Joliet.
    /// </summary>
    public static string GetDefaultFileSystem(string discType)
    {
        if (IsUhdBluRay(discType))
            return "UDF 2.50";
        if (IsBluRay(discType) || (IsMDisc(discType) && discType.Contains("BD", StringComparison.OrdinalIgnoreCase)))
            return "UDF 2.50";
        if (IsHdDvd(discType))
            return "UDF 2.50";
        if (IsVcdOrSvcd(discType))
            return "ISO 9660";
        if (discType.Contains("Xbox", StringComparison.OrdinalIgnoreCase))
            return "ISO 9660";
        return "ISO 9660 + Joliet";
    }

}
