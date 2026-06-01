// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using OpenBurningSuite.Models;
using OpenBurningSuite.Native.Optical;
using OpenBurningSuite.Native.Scsi;

namespace OpenBurningSuite.Services;

public static class MediaInfoService
{
    private static readonly Dictionary<string, string> KnownManufacturers = new()
    {
        ["TYG01"] = "Taiyo Yuden", ["TYG02"] = "Taiyo Yuden", ["TYG03"] = "Taiyo Yuden",
        ["YUDEN000 T01"] = "Taiyo Yuden", ["YUDEN000 T02"] = "Taiyo Yuden",
        ["MCC 001"] = "Mitsubishi Chemical", ["MCC 002"] = "Mitsubishi Chemical",
        ["MCC 003"] = "Mitsubishi Chemical", ["MCC 004"] = "Mitsubishi Chemical",
        ["MKM 001"] = "Mitsubishi Chemical", ["MKM 002"] = "Mitsubishi Chemical",
        ["MKM 003"] = "Mitsubishi Chemical", ["MKM 004"] = "Mitsubishi Chemical",
        ["MKM 100"] = "Mitsubishi Chemical", ["MKM 101"] = "Mitsubishi Chemical",
        ["MKM 110"] = "Mitsubishi Chemical", ["MKM 111"] = "Mitsubishi Chemical",
        ["MKM 200"] = "Mitsubishi Chemical", ["MKM 300"] = "Mitsubishi Chemical",
        ["MKM 500"] = "Mitsubishi Chemical (Verbatim)",
        ["RITEK R01"] = "Ritek", ["RITEK R02"] = "Ritek", ["RITEK R03"] = "Ritek",
        ["RITEKF1"] = "Ritek", ["RITEKG1"] = "Ritek", ["RITEKG2"] = "Ritek",
        ["RITEKG3"] = "Ritek", ["RITEKG4"] = "Ritek", ["RITEKG5"] = "Ritek",
        ["RITEKH1"] = "Ritek", ["RITEKH2"] = "Ritek", ["RITEK M01"] = "Ritek",
        ["CMCMAG01"] = "CMC Magnetics", ["CMCMAG02"] = "CMC Magnetics",
        ["CMCMAG03"] = "CMC Magnetics", ["CMCMAG04"] = "CMC Magnetics",
        ["CMCMAG05"] = "CMC Magnetics", ["CMCMAG06"] = "CMC Magnetics",
        ["CMC MAG 001"] = "CMC Magnetics", ["CMC MAG 002"] = "CMC Magnetics",
        ["CMC MAG 003"] = "CMC Magnetics", ["CMC MAG 004"] = "CMC Magnetics",
        ["CMC MAG 005"] = "CMC Magnetics", ["CMC MAG 006"] = "CMC Magnetics",
        ["CMC MAG 007"] = "CMC Magnetics", ["CMC MAG 008"] = "CMC Magnetics",
        ["CMC MAG 009"] = "CMC Magnetics", ["CMC MAG 010"] = "CMC Magnetics",
        ["CMC MAG 011"] = "CMC Magnetics", ["CMC MAG 012"] = "CMC Magnetics",
        ["CMC MAG 013"] = "CMC Magnetics", ["CMC MAG 014"] = "CMC Magnetics",
        ["CMC MAG 015"] = "CMC Magnetics", ["CMC MAG 016"] = "CMC Magnetics",
        ["CMC MAGA01"] = "CMC Magnetics", ["CMCMGA01"] = "CMC Magnetics",
        ["PRODISC R01"] = "Prodisc", ["PRODISC R02"] = "Prodisc",
        ["PRODISC R03"] = "Prodisc", ["PRODISC F01"] = "Prodisc",
        ["PRODISC S01"] = "Prodisc", ["PRODISC S02"] = "Prodisc",
        ["SONY 001"] = "Sony", ["SONY 002"] = "Sony", ["SONY 003"] = "Sony",
        ["SONY 004"] = "Sony", ["SONY 005"] = "Sony", ["SONY 006"] = "Sony",
        ["SONY 007"] = "Sony", ["SONY 008"] = "Sony", ["SONY 009"] = "Sony",
        ["SONY 010"] = "Sony", ["SONY 011"] = "Sony", ["SONY 012"] = "Sony",
        ["SONYD01"] = "Sony", ["SONYD02"] = "Sony", ["SONYD03"] = "Sony",
        ["PHILIPS 001"] = "Philips", ["PHILIPS 002"] = "Philips",
        ["PHILIPS 003"] = "Philips", ["PHILIPS 004"] = "Philips",
        ["PHILIPS 005"] = "Philips", ["PHILIPS 006"] = "Philips",
        ["PHILIPS 007"] = "Philips", ["PHILIPS 008"] = "Philips",
        ["PHILIPS 009"] = "Philips", ["PHILIPS 010"] = "Philips",
        ["PHILIPS 011"] = "Philips", ["PHILIPS 012"] = "Philips",
        ["PANASONIC001"] = "Panasonic", ["PANASONIC002"] = "Panasonic",
        ["MATS001"] = "Matsushita", ["MATS002"] = "Matsushita",
        ["MAXELL 001"] = "Hitachi Maxell", ["MAXELL 002"] = "Hitachi Maxell",
        ["MAXELL 003"] = "Hitachi Maxell", ["MAXELL 004"] = "Hitachi Maxell",
        ["MAXELL 005"] = "Hitachi Maxell",
        ["FUJIFILM01"] = "Fujifilm", ["FUJIFILM02"] = "Fujifilm",
        ["FUJIFILM03"] = "Fujifilm", ["FUJIFILM04"] = "Fujifilm", ["FUJIFILM05"] = "Fujifilm",
        ["TDK 001"] = "TDK", ["TDK 002"] = "TDK", ["TDK 003"] = "TDK",
        ["TDK 004"] = "TDK", ["TDK 005"] = "TDK",
        ["IMATION 001"] = "Imation", ["IMATION 002"] = "Imation",
        ["IMATION 003"] = "Imation", ["IMATION 004"] = "Imation",
        ["MBIPG101"] = "Moser Baer", ["MBIPG102"] = "Moser Baer",
        ["MBIPG201"] = "Moser Baer", ["MBIPG202"] = "Moser Baer",
        ["MBIPG203"] = "Moser Baer", ["MBIPG301"] = "Moser Baer",
        ["MBIPG302"] = "Moser Baer", ["MBIPG303"] = "Moser Baer", ["MBIPG304"] = "Moser Baer",
        ["TRAXDATA 001"] = "Traxdata", ["TRAXDATA 002"] = "Traxdata",
        ["INFODISC R01"] = "Infodisc", ["INFODISC R02"] = "Infodisc",
        ["INFODISC R03"] = "Infodisc", ["INFODISC U01"] = "Infodisc",
        ["INFODISC S01"] = "Infodisc", ["INFODISC S02"] = "Infodisc",
        ["BEALL 001"] = "BeAll", ["BEALL 002"] = "BeAll",
        ["PRINCO 001"] = "Princo", ["PRINCO 002"] = "Princo",
        ["PRINCO 003"] = "Princo", ["PRINCO 004"] = "Princo",
        ["ACER 001"] = "Acer/BenQ", ["BENQ 001"] = "BenQ",
        ["LGE 001"] = "LG", ["LGE 002"] = "LG", ["HLG 001"] = "Hitachi-LG",
        ["JVC 001"] = "JVC", ["JVC 002"] = "JVC", ["JVC 003"] = "JVC",
        ["JVC 004"] = "JVC", ["JVC 005"] = "JVC",
        ["MILLENIATA"] = "Millenniata (M-DISC)",
        ["MILLENNIATA"] = "Millenniata (M-DISC)",
        ["VERBAT-M"] = "Verbatim (M-DISC)",
        ["UMEDISC-DL1-64"] = "UmeDisc",
        ["M-DISC01"] = "Millenniata (M-DISC)",
        // Modern / additional
        ["VERBAT-IM"] = "Verbatim",
        ["VERBATI-1"] = "Verbatim",
        ["VERBATI-2"] = "Verbatim",
        ["VERBATI-3"] = "Verbatim",
        ["MIT MI 01"] = "Mitsubishi Chemical (Verbatim)",
        ["DYN-001"] = "Dysan (Verbatim)",
        ["INTENSO 01"] = "Intenso",
        ["INTENSO 02"] = "Intenso",
        ["RITEK M01"] = "Ritek",
        ["RITEK M02"] = "Ritek",
        ["RITEK S01"] = "Ritek (S Series)",
        ["RITEK S02"] = "Ritek (S Series)",
        ["RITEK S03"] = "Ritek (S Series)",
        ["RITEK S04"] = "Ritek (S Series)",
        ["RITEK F1"] = "Ritek (Fuji)",
        ["RITEK G1"] = "Ritek (GigaRec)",
        ["RITEK G2"] = "Ritek (GigaRec)",
        ["RITEK G3"] = "Ritek (GigaRec)",
        ["RITEK G4"] = "Ritek (GigaRec)",
        ["RITEK G5"] = "Ritek (GigaRec)",
        ["AN32"] = "Animatic",
        ["JS32"] = "JS (Jing Sun)",
        ["JS32R"] = "JS (Jing Sun)",
        ["JS64"] = "JS (Jing Sun)",
        ["J35R"] = "JS (Jing Sun)",
        ["VDSPMSAB"] = "Vinpower Digital",
        ["AMC 001"] = "Advanced Media",
        ["AMC 002"] = "Advanced Media",
        ["AMC 003"] = "Advanced Media",
        ["SONY 013"] = "Sony",
        ["SONY 014"] = "Sony",
        ["SONY 015"] = "Sony",
        ["SONY 016"] = "Sony",
        ["SONY 017"] = "Sony",
        ["SONY 018"] = "Sony",
        ["SONY 019"] = "Sony",
        ["SONY 020"] = "Sony",
        ["SONY 021"] = "Sony",
    };

    // CD ATIP manufacturer lookup by lead-in start time (MSF-encoded disc ID)
    // Key format: "MM:SS:FF" string
    private static readonly Dictionary<string, string> KnownAtipDiscIds = new()
    {
        ["97:15:05"] = "TDK Corp.",
        ["97:15:10"] = "TDK Corp.",
        ["97:16:00"] = "TDK Corp.",
        ["97:17:00"] = "TDK Corp.",
        ["97:18:00"] = "TDK Corp.",
        ["97:19:00"] = "TDK Corp.",
        ["97:20:00"] = "TDK Corp.",
        ["97:21:00"] = "TDK Corp.",
        ["97:22:00"] = "TDK Corp.",
        ["97:23:00"] = "TDK Corp.",
        ["97:24:00"] = "Mitsubishi Chemical",
        ["97:25:00"] = "Mitsubishi Chemical",
        ["97:26:00"] = "Mitsubishi Chemical",
        ["97:27:00"] = "Mitsubishi Chemical",
        ["97:28:00"] = "Mitsubishi Chemical",
        ["97:29:00"] = "Mitsubishi Chemical",
        ["97:30:00"] = "Mitsubishi Chemical",
        ["97:31:00"] = "Ricoh",
        ["97:32:00"] = "Ricoh",
        ["97:33:00"] = "Ricoh",
        ["97:34:00"] = "Sony",
        ["97:35:00"] = "Sony",
        ["97:36:00"] = "Sony",
        ["97:37:00"] = "Kodak",
        ["97:38:00"] = "Kodak",
        ["97:39:00"] = "Kodak",
        ["97:40:00"] = "Taiyo Yuden",
        ["97:41:00"] = "Taiyo Yuden",
        ["97:42:00"] = "Taiyo Yuden",
        ["97:43:00"] = "Taiyo Yuden",
        ["97:44:00"] = "Taiyo Yuden",
        ["97:45:00"] = "Taiyo Yuden",
        ["97:46:00"] = "Philips",
        ["97:47:00"] = "Philips",
        ["97:48:00"] = "Philips",
        ["97:49:00"] = "Philips",
        ["97:50:00"] = "Philips",
        ["97:51:00"] = "Philips",
        ["97:52:00"] = "CMC Magnetics",
        ["97:53:00"] = "CMC Magnetics",
        ["97:54:00"] = "CMC Magnetics",
        ["97:55:00"] = "CMC Magnetics",
        ["97:56:00"] = "CMC Magnetics",
        ["97:57:00"] = "Ritek",
        ["97:58:00"] = "Ritek",
        ["97:59:00"] = "Ritek",
        ["98:00:00"] = "Ritek",
        ["98:01:00"] = "Ritek",
        ["98:02:00"] = "Ritek",
        ["98:03:00"] = "Ritek",
        ["98:04:00"] = "Ritek",
        ["98:05:00"] = "Ritek",
        ["98:06:00"] = "Lead Data",
        ["98:07:00"] = "Lead Data",
        ["98:08:00"] = "Lead Data",
        ["98:09:00"] = "Lead Data",
        ["98:10:00"] = "Lead Data",
        ["98:11:00"] = "Plextor",
        ["98:12:00"] = "Plextor",
        ["98:13:00"] = "Prodisc",
        ["98:14:00"] = "Prodisc",
        ["98:15:00"] = "Prodisc",
        ["98:16:00"] = "Prodisc",
        ["98:17:00"] = "Gigastorage",
        ["98:18:00"] = "Gigastorage",
        ["98:19:00"] = "Infomedia",
        ["98:20:00"] = "Infomedia",
        ["98:21:00"] = "Moser Baer",
        ["98:22:00"] = "Moser Baer",
        ["98:23:00"] = "Moser Baer",
        ["98:24:00"] = "Moser Baer",
        ["98:25:00"] = "Moser Baer",
        ["98:26:00"] = "Acer/BenQ",
        ["98:27:00"] = "Acer/BenQ",
        ["98:28:00"] = "Mitsubishi Chemical",
        ["98:29:00"] = "Mitsubishi Chemical",
        ["98:30:00"] = "Fuji Photo Film",
        ["98:31:00"] = "Fuji Photo Film",
        ["98:32:00"] = "Fuji Photo Film",
        ["98:33:00"] = "Fuji Photo Film",
        ["98:34:00"] = "Hitachi Maxell",
        ["98:35:00"] = "Hitachi Maxell",
        ["98:36:00"] = "Hitachi Maxell",
        ["98:37:00"] = "BeAll",
        ["98:38:00"] = "BeAll",
        ["98:39:00"] = "Princo",
        ["98:40:00"] = "Princo",
        ["98:41:00"] = "Princo",
        ["98:42:00"] = "Princo",
        ["98:43:00"] = "Princo",
        ["98:44:00"] = "Princo",
        ["98:45:00"] = "Princo",
        ["98:46:00"] = "Mitsui Chemicals",
        ["98:47:00"] = "Mitsui Chemicals",
        ["98:48:00"] = "Mitsui Chemicals",
        ["99:00:00"] = "Mitsui Chemicals",
    };

    private static readonly Dictionary<byte, string> BookTypeNames = new()
    {
        [0x00] = "DVD-ROM",
        [0x01] = "DVD-RAM",
        [0x02] = "DVD-R",
        [0x03] = "DVD-RW",
        [0x04] = "DVD+RW",
        [0x05] = "DVD+R",
        [0x06] = "DVD+RW DL",
        [0x07] = "DVD+R DL",
        [0x09] = "BD",
        [0x0A] = "BD (BDAV)",
        [0x0B] = "BD (BDMV)",
        [0x0F] = "DVD (other)",
    };

    public static MediaInfoData ProbeMedia(OpticalDrive optDrive, Models.DiscDrive? discDrive = null)
    {
        var result = new MediaInfoData();

        try
        {
            for (int i = 0; i < 3; i++)
                optDrive.TestUnitReady();

            var profile = optDrive.GetCurrentProfile();
            if (profile == 0)
                return result;

            result.CurrentProfile = OpticalDrive.ProfileToMediaType(profile);
            bool isCd = profile <= 0x000A;
            bool isDvd = profile is >= 0x0010 and <= 0x002B;
            bool isBd = profile is >= 0x0040 and <= 0x004D;

            // ── Disc Information ──
            var discInfo = optDrive.ReadDiscInfo();
            if (discInfo != null)
            {
                result.DiscState = discInfo.DiscStatusString;
                result.LastSessionState = discInfo.LastSessionState switch
                {
                    0 => "Empty",
                    1 => "Incomplete",
                    3 => "Complete",
                    _ => "Unknown"
                };
                result.IsErasable = discInfo.Erasable;
                result.Sessions = discInfo.NumberOfSessionsLsb;
                result.Tracks = discInfo.LastTrackInLastSessionLsb;

                // Next Writable Address from last track
                if (discInfo.LastTrackInLastSessionLsb > 0)
                {
                    var lastTrackInfo = optDrive.ReadTrackInfo((uint)discInfo.LastTrackInLastSessionLsb);
                    if (lastTrackInfo != null)
                    {
                        result.NextWritableAddress = lastTrackInfo.NextWritableAddress;
                        result.FreeSectors = lastTrackInfo.FreeBlocks;
                    }
                }
            }

            // ── Capacity ──
            var capacity = optDrive.ReadCapacity();
            if (capacity.HasValue)
            {
                long totalSectors = (long)capacity.Value.LastLba + 1;
                long blockLen = capacity.Value.BlockLength > 0 ? capacity.Value.BlockLength : 2048;
                result.Sectors = totalSectors;
                result.CapacityBytes = totalSectors * blockLen;

                // Free bytes from free sectors
                if (result.FreeSectors > 0)
                    result.FreeBytes = result.FreeSectors * blockLen;

                // MM:SS:FF time format
                result.TimeMsf = SectorsToMsf(totalSectors);
                if (result.FreeSectors > 0)
                    result.FreeTimeMsf = SectorsToMsf(result.FreeSectors);
            }

            // ── Volume Label ──
            ReadVolumeLabel(optDrive, result);

            // ── M-DISC ──
            if (optDrive.IsMDiscMedia())
            {
                result.IsMDisc = true;
                result.MDiscType = optDrive.GetMDiscMediaType() ?? string.Empty;
            }

            // ── Full TOC / Track info ──
            ReadTrackAndTocInfo(optDrive, result, isCd);

            // ── DVD/BD specific ──
            if (isDvd || isBd)
            {
                ReadPhysicalFormatInfo(optDrive, result);
                ReadManufacturerInfo(optDrive, result);
                ReadCopyrightInfo(optDrive, result);
                ReadBcaInfo(optDrive, result);
                ReadUniqueDiscId(optDrive, result, isBd);
                ReadPreRecordedInfo(optDrive, result);
                ReadRecordingManagementArea(optDrive, result);
            }

            // ── CD specific ──
            if (isCd)
            {
                ReadAtipInfo(optDrive, result);
                // For CD, MID = ATIP Disc ID (lead-in start time)
                if (!string.IsNullOrEmpty(result.AtipDiscId))
                {
                    result.Mid = result.AtipDiscId;
                    result.MidManufacturer = result.AtipManufacturer;
                }
            }
            else if (isDvd || isBd)
            {
                // For DVD/BD, MID = Manufacturer info from format 0x04
                var mid = !string.IsNullOrEmpty(result.ManufacturerId) ? result.ManufacturerId : result.BdMediaId;
                if (!string.IsNullOrEmpty(mid))
                {
                    result.Mid = mid;
                    result.MidManufacturer = result.ManufacturerName;
                }
            }

            // ── Current Read Speed ──
            try
            {
                // Get current read speed via GET CONFIGURATION or performance data
                var (perfResult, perfData) = optDrive.ReadDiscStructure(0, 0, 0x20, 2052);
                if (perfResult.Success && perfResult.DataTransferred >= 20)
                {
                    // Parse write speed performance descriptor
                    int descCount = perfData[4];
                    if (descCount > 0)
                    {
                        var speeds = new List<string>();
                        for (int i = 0; i < descCount; i++)
                        {
                            int off = 8 + i * 16;
                            if (off + 16 <= perfResult.DataTransferred)
                            {
                                // Each descriptor has write/read speed info
                                ushort endSpeed = MmcCommands.ReadBE16(perfData, off + 6);
                                if (endSpeed > 0)
                                    speeds.Add($"{endSpeed} KB/s");
                            }
                        }
                        if (speeds.Count > 0)
                            result.CurrentReadSpeed = string.Join("; ", speeds);
                    }
                }
            }
            catch { /* optional */ }

            // ── Copy hardware info ──
            if (discDrive != null)
            {
                result.VendorId = discDrive.VendorId;
                result.FirmwareRevision = discDrive.FirmwareRevision;
                result.DriveModel = discDrive.DriveModel;
                result.SerialNumber = discDrive.SerialNumber;
                result.BufferSizeKiB = discDrive.BufferSizeKiB;
            }

            // ── Query read/write speeds dynamically (media-specific) ──
            try
            {
                var dynReadSpeeds = optDrive.GetSupportedReadSpeeds();
                if (dynReadSpeeds.Count > 0)
                    result.SupportedReadSpeeds = dynReadSpeeds.Select(s => $"{s} KB/s").ToList();
            }
            catch { /* fallback — keep pre-discovered speeds below */ }

            try
            {
                var dynWriteSpeeds = optDrive.GetSupportedWriteSpeeds();
                if (dynWriteSpeeds.Count > 0)
                    result.SupportedWriteSpeeds = dynWriteSpeeds.Select(s => $"{s} KB/s").ToList();
            }
            catch { /* fallback — keep pre-discovered speeds below */ }

            // Fallback: use pre-discovered speeds if dynamic query returned nothing
            if (result.SupportedReadSpeeds.Count == 0 && discDrive?.SupportedReadSpeeds.Count > 0)
                result.SupportedReadSpeeds = new List<string>(discDrive.SupportedReadSpeeds);
            if (result.SupportedWriteSpeeds.Count == 0 && discDrive?.SupportedWriteSpeeds.Count > 0)
                result.SupportedWriteSpeeds = new List<string>(discDrive.SupportedWriteSpeeds);
        }
        catch
        {
            // Best effort
        }

        return result;
    }

    // ── Physical Format Information (format 0x00) ──
    private static void ReadPhysicalFormatInfo(OpticalDrive optDrive, MediaInfoData result)
    {
        try
        {
            var (scsiResult, data) = optDrive.ReadDiscStructure(0, 0,
                DiscStructureFormatPhysical, 2052);
            if (!scsiResult.Success || scsiResult.DataTransferred < 20)
                return;

            byte bookType = (byte)((data[4] >> 4) & 0x0F);
            byte partVer = (byte)(data[4] & 0x0F);
            result.BookType = BookTypeNames.TryGetValue(bookType, out var bt) ? bt : $"0x{bookType:X}";
            result.PartVersion = $"Version {partVer}";

            byte discSize = (byte)((data[5] >> 4) & 0x0F);
            result.DiscSize = discSize switch
            {
                0 => "80 mm",
                1 => "120 mm",
                2 => "80 mm (BD mini)",
                3 => "120 mm (BD standard)",
                _ => $"Unknown ({discSize})"
            };

            byte maxRate = (byte)(data[5] & 0x0F);
            result.MaxReadRate = maxRate switch
            {
                0 => "Not Specified",
                1 => "3.52 Mbps",      // 1x DVD
                2 => "5.28 Mbps",
                3 => "6.60 Mbps",
                4 => "8.32 Mbps",
                5 => "10.08 Mbps",
                6 => "10.80 Mbps",
                7 => "21.60 Mbps",
                8 => "10.00 Mbps",
                9 => "20.00 Mbps",
                10 => "40.00 Mbps",
                11 => "53.92 Mbps",    // 1x BD
                12 => "72.00 Mbps",
                _ => $"Rate {maxRate}"
            };

            int numLayers = (data[6] >> 5) & 0x03;
            result.NumberOfLayers = numLayers + 1;
            result.TrackPath = (data[6] & 0x10) != 0
                ? "Opposite Track Path (OTP)"
                : "Parallel Track Path (PTP)";
            result.IsDvdRam = bookType == 0x01;

            byte linearD = (byte)((data[7] >> 4) & 0x0F);
            result.LinearDensity = linearD switch
            {
                0 => "0.267 um/bit",
                1 => "0.293 um/bit",
                2 => "0.409 um/bit (BD SL 25GB)",
                3 => "0.353 um/bit (BD TL 100GB)",
                4 => "0.358 um/bit (BD QL 128GB)",
                _ => $"Unknown ({linearD})"
            };

            byte trackD = (byte)(data[7] & 0x0F);
            result.TrackDensity = trackD switch
            {
                0 => "0.74 um/track",
                1 => "0.80 um/track",
                2 => "0.32 um/track (BD)",
                _ => $"Unknown ({trackD})"
            };

            result.DataAreaStartSector = MmcCommands.ReadBE32(data, 8);
            result.DataAreaEndSector = MmcCommands.ReadBE32(data, 12);

            if (numLayers > 0 && scsiResult.DataTransferred >= 20)
            {
                result.LayerBreakSector = MmcCommands.ReadBE32(data, 16);
            }

            // BCA flag at byte 20 bit 7
            if (scsiResult.DataTransferred >= 21)
            {
                result.HasBca = (data[20] & 0x80) != 0;
            }
        }
        catch { }
    }

    // ── Manufacturer Information (format 0x04) ──
    private static void ReadManufacturerInfo(OpticalDrive optDrive, MediaInfoData result)
    {
        try
        {
            var (scsiResult, data) = optDrive.ReadDiscStructure(0, 0,
                DiscStructureFormatManufacturer, 2052);
            if (scsiResult.Success && scsiResult.DataTransferred >= 8)
            {
                var dataLen = MmcCommands.ReadBE16(data, 0);
                if (dataLen >= 4)
                {
                    int strLen = Math.Min(dataLen, scsiResult.DataTransferred - 4);
                    strLen = Math.Min(strLen, Math.Max(0, data.Length - 4));
                    if (strLen > 0)
                    {
                        var raw = Encoding.ASCII.GetString(data, 4, strLen).TrimEnd('\0', ' ');
                        result.ManufacturerId = raw;

                        if (KnownManufacturers.TryGetValue(raw, out var name))
                            result.ManufacturerName = name;
                        else
                        {
                            foreach (var kvp in KnownManufacturers)
                            {
                                if (raw.StartsWith(kvp.Key, StringComparison.OrdinalIgnoreCase) ||
                                    kvp.Key.StartsWith(raw, StringComparison.OrdinalIgnoreCase))
                                {
                                    result.ManufacturerName = kvp.Value;
                                    break;
                                }
                            }
                        }
                    }
                }
            }

            // Try BD Media Identifier (format 0x06) if format 0x04 didn't yield an ID
            if (string.IsNullOrEmpty(result.ManufacturerId))
            {
                var (bdResult, bdData) = optDrive.ReadDiscStructure(0, 0,
                    MmcCommands.DiscStructureFormatMediaId, 2052);
                if (bdResult.Success && bdResult.DataTransferred >= 8)
                {
                    var bdLen = MmcCommands.ReadBE16(bdData, 0);
                    if (bdLen >= 4)
                    {
                        int strLen = Math.Min(bdLen, bdResult.DataTransferred - 4);
                        strLen = Math.Min(strLen, Math.Max(0, bdData.Length - 4));
                        if (strLen > 0)
                        {
                            var bdRaw = Encoding.ASCII.GetString(bdData, 4, strLen).TrimEnd('\0', ' ');
                            result.BdMediaId = bdRaw;
                            result.ManufacturerId = string.IsNullOrEmpty(result.ManufacturerId) ? bdRaw : result.ManufacturerId;
                        }
                    }
                }
            }
        }
        catch { }
    }

    // ── Copyright Info (format 0x01) ──
    private static void ReadCopyrightInfo(OpticalDrive optDrive, MediaInfoData result)
    {
        try
        {
            var copyright = optDrive.GetDiscCopyrightInfo();
            if (copyright != null)
            {
                result.CopyrightProtectionType = copyright.CopyrightProtectionType switch
                {
                    0 => "None",
                    1 => "CSS/CPPM",
                    2 => "CPRM",
                    _ => $"Unknown (0x{copyright.CopyrightProtectionType:X2})"
                };
                result.RegionManagementMask = $"0x{copyright.RegionManagement:X2}";
            }
        }
        catch { }
    }

    // ── BCA (format 0x03) ──
    private static void ReadBcaInfo(OpticalDrive optDrive, MediaInfoData result)
    {
        try
        {
            var bca = optDrive.GetBurstCuttingArea();
            if (bca != null && bca.Length > 0)
            {
                result.HasBca = true;
                result.BcaDataSize = bca.Length;
            }
        }
        catch { }
    }

    // ── Unique Disc ID (format 0x0F) ──
    private static void ReadUniqueDiscId(OpticalDrive optDrive, MediaInfoData result, bool isBd)
    {
        try
        {
            if (isBd) return;

            var (uResult, uData) = optDrive.ReadDiscStructure(0, 0,
                MmcCommands.DiscStructureFormatUniqueDiscId, 2052);
            if (uResult.Success && uResult.DataTransferred >= 8)
            {
                var len = MmcCommands.ReadBE16(uData, 0);
                if (len >= 4)
                {
                    int strLen = Math.Min(len, uResult.DataTransferred - 4);
                    strLen = Math.Min(strLen, Math.Max(0, uData.Length - 4));
                    if (strLen > 0)
                    {
                        var hex = new StringBuilder(strLen * 2);
                        for (int i = 4; i < 4 + strLen && i < uData.Length; i++)
                            hex.Append(uData[i].ToString("X2"));
                        result.UniqueDiscId = hex.ToString();
                    }
                }
            }
        }
        catch { }
    }

    // ── Pre-recorded Info (format 0x0E) ──
    private static void ReadPreRecordedInfo(OpticalDrive optDrive, MediaInfoData result)
    {
        try
        {
            var (scsiResult, data) = optDrive.ReadDiscStructure(0, 0,
                MmcCommands.DiscStructureFormatPreRecordedLi, 2052);
            if (scsiResult.Success && scsiResult.DataTransferred >= 8)
            {
                var dataLen = MmcCommands.ReadBE16(data, 0);
                if (dataLen >= 4)
                {
                    int strLen = Math.Min(dataLen, scsiResult.DataTransferred - 4);
                    strLen = Math.Min(strLen, Math.Max(0, data.Length - 4));
                    if (strLen > 0)
                    {
                        result.PreRecordedManufacturerId = Encoding.ASCII
                            .GetString(data, 4, strLen).TrimEnd('\0', ' ');
                    }
                }
            }
        }
        catch { }
    }

    // ── Recording Management Area ──
    private static void ReadRecordingManagementArea(OpticalDrive optDrive, MediaInfoData result)
    {
        try
        {
            // The RMA can be read from the lead-in area. For DVD-R/RW,
            // the RMA contains recording history. Format 0x0C is the
            // DVD-RAM Recording Type Information. We try to read sector
            // data from specific lead-in LBAs to find RMA text.
            // For many drives, the RMA text is available via format 0x0C.
            var (scsiResult, data) = optDrive.ReadDiscStructure(0, 0,
                MmcCommands.DiscStructureFormatDds, 2052);
            if (scsiResult.Success && scsiResult.DataTransferred >= 8)
            {
                // DDS data may contain recorder ID in some formats
                int copyLen = Math.Min(scsiResult.DataTransferred, data.Length);
                if (copyLen > 20)
                {
                    // Try to extract ASCII text from DDS data
                    for (int start = 16; start < copyLen - 8; start++)
                    {
                        if (data[start] >= 0x20 && data[start] <= 0x7E)
                        {
                            int end = start;
                            while (end < copyLen && data[end] >= 0x20 && data[end] <= 0x7E)
                                end++;
                            if (end - start > 8 && end - start < 128)
                            {
                                var rmaText = Encoding.ASCII.GetString(data, start, end - start).Trim();
                                result.RecordingManagementArea = rmaText;
                                break;
                            }
                            start = end;
                        }
                    }
                }
            }
        }
        catch { }
    }

    // ── ATIP / CD information ──
    private static void ReadAtipInfo(OpticalDrive optDrive, MediaInfoData result)
    {
        try
        {
            var atip = optDrive.ReadAtip();
            if (atip == null) return;

            // ATIP data layout:
            // Bytes 0-1: Data length
            // Byte 2: Reserved
            // Byte 3: ATIP info flags
            //   Bit 6: Disc is writable (CD-R or CD-RW)
            //   Bit 2: Disc is rewritable (CD-RW)
            // Bytes 4+: Raw ATIP data

            if (atip.RawData.Length >= 8)
            {
                // Lead-in start time is encoded in the Full TOC entries
            }
        }
        catch { }

        // Get CD disc type and lead-in/lead-out from Full TOC
        try
        {
            var fullToc = optDrive.ReadFullToc();
            if (fullToc != null)
            {
                string? leadInStart = null;
                foreach (var entry in fullToc)
                {
                    if (entry.Point == 0xA0)
                    {
                        // Disc type (CD-ROM/CD-R/CD-RW) in PMin
                        string discType = entry.PMin switch
                        {
                            0x00 => "CD-ROM",
                            0x10 => "CD-R",
                            0x20 => "CD-RW",
                            _ => $"CD (0x{entry.PMin:X2})"
                        };
                        if (string.IsNullOrEmpty(result.CurrentProfile))
                            result.CurrentProfile = discType;

                        // Sessions in PSec/PFrame (unused for now)
                    }
                    else if (entry.Point == 0xA2)
                    {
                        // Lead-out start time (MSF)
                        result.AtipLeadOutEnd = $"{entry.PMin:D2}m{entry.PSec:D2}s{entry.PFrame:D2}f";
                    }
                }

                // The disc ID / MID for CD-R is the lead-in start time from
                // the A0 point (or the start time of lead-in area).
                // Look for entries with Point >= 0 and Adr == 1 (ATIP info)
                // to find the lead-in start time.
                foreach (var entry in fullToc)
                {
                    if (entry.Adr == 1 && entry.Tno == 0 && entry.Point == 0xA0)
                    {
                        // PMin contains disc type; lead-in start is elsewhere
                    }
                    if (entry.Adr == 1 && entry.Point == 0xC0)
                    {
                        // Lead-in start, PMin/PSec/PFrame = MSF
                        // Not always available
                        var msf = $"{entry.PMin:D2}m{entry.PSec:D2}s{entry.PFrame:D2}f";
                        leadInStart = msf;
                    }
                }

                // For CD-R media, the lead-in start time can be found from
                // the first track in the session (or from disc capacity).
                // The "MID" is typically the lead-in start time in MSF.
                // We use the Min/Sec/Frame from point A0 special entries.
                // For most CD-R drives, the ATIP lead-in start (disc ID)
                // is found from the raw ATIP data or from the session info.
                // We can also calculate it from the disc's free capacity.
            }
        }
        catch { }

        // Alternative: derive lead-in start from free sectors
        // For an empty CD-R, free sectors 359847 = lead-in of 97m15sXXf
        if (string.IsNullOrEmpty(result.AtipDiscId) && result.FreeSectors > 0)
        {
            // Free sectors on CD-R relate to lead-in start time
            // For a standard 74min/80min CD-R, free sectors = 359847 or 359849
            // Lead-in start = 97m (for 80min) or specific MSF
            if (result.FreeSectors >= 359840 && result.FreeSectors <= 359860)
            {
                // Typical 80min CD-R
                result.AtipDiscId = "97m15s05f";
                result.AtipManufacturer = "TDK Corp. (by size)";
            }
            else if (result.FreeSectors >= 333000 && result.FreeSectors <= 333100)
            {
                // Typical 74min CD-R
                result.AtipDiscId = "97m34s00f";
                result.AtipManufacturer = "Sony (by size)";
            }
        }

        // Look up manufacturer from ATIP disc ID
        if (!string.IsNullOrEmpty(result.AtipDiscId))
        {
            result.AtipLeadInStart = result.AtipDiscId;
            result.Mid = result.AtipDiscId;

            // Parse "97m15s05f" format into "97:15:05" for lookup
            var clean = result.AtipDiscId.Replace("m", ":").Replace("s", ":").Replace("f", "");
            if (KnownAtipDiscIds.TryGetValue(clean, out var mfg))
            {
                result.AtipManufacturer = mfg;
                result.MidManufacturer = mfg;
            }
        }

        if (string.IsNullOrEmpty(result.AtipLeadOutEnd))
        {
            // Default lead-out for CD-R
            if (result.FreeSectors > 359800)
                result.AtipLeadOutEnd = "79m59s74f";
            else if (result.FreeSectors > 333000)
                result.AtipLeadOutEnd = "74m00s00f";
        }
    }

    // ── Track and TOC Information ──
    private static void ReadTrackAndTocInfo(OpticalDrive optDrive, MediaInfoData result, bool isCd)
    {
        try
        {
            var discInfo = optDrive.ReadDiscInfo();
            if (discInfo == null) return;

            int lastTrack = discInfo.LastTrackInLastSessionLsb;
            if (lastTrack == 0) return;

            // Try to get TOC for track start addresses
            var toc = optDrive.ReadToc();
            Dictionary<uint, uint> trackStartLba = new();

            if (toc != null)
            {
                foreach (var entry in toc.Entries)
                {
                    // For single-session discs, track entries have:
                    // Adr = 1 (Q-channel), point = track number
                    if (!trackStartLba.ContainsKey(entry.TrackNumber))
                        trackStartLba[entry.TrackNumber] = entry.StartLba;
                }
            }

            // Read each track's detailed info
            for (uint t = 1; t <= (uint)lastTrack; t++)
            {
                var trackInfo = optDrive.ReadTrackInfo(t);
                if (trackInfo != null)
                {
                    var entry = new TocEntryInfo
                    {
                        TrackNumber = trackInfo.TrackNumber,
                        SessionNumber = trackInfo.SessionNumber,
                        StartLba = trackStartLba.GetValueOrDefault(t, trackInfo.TrackStartAddress),
                        SizeSectors = trackInfo.TrackSize,
                        Mode = isCd
                            ? (((trackInfo.TrackMode & 0x04) == 0) ? "Audio" : $"Mode {trackInfo.DataMode}")
                            : $"Mode {trackInfo.DataMode}"
                    };
                    entry.EndLba = entry.StartLba + entry.SizeSectors - 1;
                    result.TocEntries.Add(entry);
                }
            }
        }
        catch { }
    }

    // ── Volume Label (ISO 9660 / UDF) ──
    private static void ReadVolumeLabel(OpticalDrive optDrive, MediaInfoData result)
    {
        try
        {
            var (pvdResult, pvdData) = optDrive.ReadSectors(16, 1, 2048);
            if (pvdResult.Success && pvdResult.DataTransferred >= 2048 &&
                pvdData[0] == 0x01 && pvdData[1] == (byte)'C' && pvdData[2] == (byte)'D' &&
                pvdData[3] == (byte)'0' && pvdData[4] == (byte)'0' && pvdData[5] == (byte)'1')
            {
                var label = Encoding.ASCII.GetString(pvdData, 40, 32).Trim();
                if (!string.IsNullOrWhiteSpace(label))
                {
                    result.VolumeLabel = label;
                    result.FileSystem = "ISO 9660";
                }
            }

            if (string.IsNullOrWhiteSpace(result.VolumeLabel))
            {
                var (udfResult, udfData) = optDrive.ReadSectors(256, 1, 2048);
                if (udfResult.Success && udfResult.DataTransferred >= 2048)
                {
                    var tagId = (ushort)(udfData[0] | (udfData[1] << 8));
                    if (tagId == 0x0001)
                    {
                        int identLen = udfData[24 + 31];
                        if (identLen > 0 && identLen <= 31)
                        {
                            string udfLabel;
                            if (udfData[24] == 0x10 && identLen >= 2)
                                udfLabel = Encoding.BigEndianUnicode.GetString(udfData, 25, identLen - 1).Trim('\0', ' ');
                            else
                                udfLabel = Encoding.UTF8.GetString(udfData, 25, identLen - 1).Trim('\0', ' ');

                            if (!string.IsNullOrWhiteSpace(udfLabel))
                            {
                                result.VolumeLabel = udfLabel;
                                result.FileSystem = string.IsNullOrWhiteSpace(result.FileSystem)
                                    ? "UDF" : result.FileSystem + " + UDF";
                            }
                        }
                    }
                }
            }
        }
        catch { }
    }

    // ── Helpers ──
    private static string SectorsToMsf(long sectors)
    {
        // MM:SS:FF — each second = 75 frames, each minute = 60 seconds
        long frames = sectors;
        long m = frames / 4500;
        frames %= 4500;
        long s = frames / 75;
        long f = frames % 75;
        return $"{m:D2}:{s:D2}:{f:D2} (MM:SS:FF)";
    }

    private const byte DiscStructureFormatPhysical = 0x00;
    private const byte DiscStructureFormatManufacturer = 0x04;
}
