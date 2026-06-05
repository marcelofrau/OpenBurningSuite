using System;
using System.Collections.Generic;
using System.Linq;
using OpenBurningSuite.Helpers;
using OpenBurningSuite.Models;
using OpenBurningSuite.Native.Optical;
using OpenBurningSuite.Native.Scsi;

namespace OpenBurningSuite.Services;

public class DiscInfoService
{
    public DiscInfoResult GetDiscInfo(OpticalDrive drive, DiscDrive discDrive)
    {
        var result = new DiscInfoResult();

        try
        {
            result.Vendor = discDrive.VendorId;
            result.DriveModel = discDrive.DriveModel;
            result.Firmware = discDrive.FirmwareRevision;
            result.Serial = discDrive.SerialNumber;
            result.BufferSizeKiB = discDrive.BufferSizeKiB;
            result.SupportedWriteSpeeds = discDrive.SupportedWriteSpeeds;
            result.InterfaceType = discDrive.BusType;
        }
        catch
        {
        }

        try
        {
            var profile = drive.GetCurrentProfile();
            result.MediaType = profile > 0
                ? OpticalDrive.ProfileToMediaType(profile)
                : "No Media";

            if (profile == 0)
                return result;

            if (drive.IsMDiscMedia())
            {
                var mDiscType = drive.GetMDiscMediaType();
                if (mDiscType != null)
                    result.MediaType = mDiscType;
            }

            var discInfo = drive.ReadDiscInformationEx();
            if (discInfo != null)
            {
                result.DiscStatus = discInfo.DiscStatusString;
                result.LastSessionState = discInfo.LastSessionState switch
                {
                    0 => "Empty",
                    1 => "Incomplete",
                    3 => "Complete",
                    _ => "Other"
                };
                result.IsErasable = discInfo.Erasable;
                result.Sessions = discInfo.NumberOfSessions;
                result.FreeSectors = discInfo.FreeSectors;
                result.NextWritableAddress = discInfo.NextWritableAddress;
                result.FreeBytes = discInfo.FreeSectors * 2048L;
                result.FreeTimeFormatted = SectorsToMsf(discInfo.FreeSectors);
            }

            var toc = drive.ReadToc();
            if (toc != null)
            {
                result.Tracks = toc.LastTrack - toc.FirstTrack + 1;
                result.Toc = toc;

                // Total sectors from LeadOut entry (TrackNumber 0xAA)
                var leadOut = toc.Entries.FirstOrDefault(e => e.TrackNumber == 0xAA);
                if (leadOut != null)
                {
                    result.TotalSectors = leadOut.StartLba;
                    result.DiscSizeBytes = result.TotalSectors * 2048L;
                    result.TotalTimeFormatted = LbaToMsf((int)result.TotalSectors);
                }

                // Track info for each data/audio track
                foreach (var entry in toc.Entries.Where(e => e.TrackNumber <= 0x99))
                {
                    try
                    {
                        var ti = drive.ReadTrackInfo(entry.TrackNumber);
                        if (ti != null)
                            result.TrackInfos.Add(ti);
                    }
                    catch { }
                }

                // Fallback for DVD-R blank: TOC may have no track entries,
                // but ReadTrackInfo(1) usually works.
                if (result.TrackInfos.Count == 0 && OpticalDrive.IsProfileDvd(profile))
                {
                    try
                    {
                        var ti = drive.ReadTrackInfo(1);
                        if (ti != null)
                            result.TrackInfos.Add(ti);
                    }
                    catch { }
                }

                // Fix up DataMode for pressed CD-ROMs where ReadTrackInfo returns 0.
                if (profile is >= 0x0008 and <= 0x000B && result.TrackInfos.Count > 0)
                {
                    // Method 1: READ HEADER (44h) on the track's start LBA
                    foreach (var ti in result.TrackInfos.Where(t => t.TrackNumber <= 0x99 && t.DataMode == 0))
                    {
                        try
                        {
                            var header = drive.ReadHeader(ti.TrackStartAddress);
                            if (header != null && (header.DataMode == 1 || header.DataMode == 2))
                                result.TrackDataModes[ti.TrackNumber] = header.DataMode;
                        }
                        catch { }
                    }

                    // Method 2: Full TOC disc type (PSec of A0h entry, bit 4 = Mode 2 / XA)
                    if (result.TrackDataModes.Count == 0)
                    {
                        try
                        {
                            var fullToc = drive.ReadFullToc();
                            var a0 = fullToc?.FirstOrDefault(e => e.Point == 0xA0);
                            if (a0 != null && (a0.PSec & 0x10) != 0)
                            {
                                foreach (var entry in toc.Entries.Where(e =>
                                    e.TrackNumber <= 0x99 && e.IsData))
                                    result.TrackDataModes[entry.TrackNumber] = 2;
                            }
                        }
                        catch { }
                    }
                }
            }

            try
            {
                var readSpeedValues = drive.GetSupportedReadSpeeds();
                result.SupportedReadSpeeds = readSpeedValues.Select(s => $"{s} KB/s").ToList();
            }
            catch
            {
            }
            finally
            {
                // Fallback to DiscDrive read speeds if SCSI returned nothing
                if (result.SupportedReadSpeeds.Count == 0)
                    result.SupportedReadSpeeds = discDrive.SupportedReadSpeeds;
            }

            result.Mid = ReadMidFromDisc(drive, profile);
            result.ManufacturerName = MediaManufacturerLookup.LookupDvdBdManufacturer(result.Mid);
            result.PreRecordedManufacturerId = result.Mid;

            if (OpticalDrive.IsProfileDvd(profile))
            {
                result.RecordingManagementArea = ReadRmaInfo(drive);
            }

            if (profile is >= 0x0008 and <= 0x000B)
            {
                result.Atip = ReadAtipInfo(drive);

                // For CD-R/RW, use ATIP data as MID/manufacturer fallback
                if (string.IsNullOrWhiteSpace(result.Mid) && result.Atip != null)
                {
                    result.Mid = result.Atip.DiscId;
                    result.ManufacturerName = result.Atip.Manufacturer;
                }
            }

            if (OpticalDrive.IsProfileDvd(profile) || profile >= 0x0040)
            {
                result.PhysicalFormats = ReadPhysicalFormatInfo(drive);
            }

            try
            {
                var perfCmd = new ScsiCommand(
                    MmcCommands.BuildGetPerformance(0x00, 0, 0),
                    ScsiDataDirection.In, 32);
                var perfResult = drive.ExecuteRaw(perfCmd);
                if (perfResult.Success && perfResult.DataTransferred >= 12)
                {
                    var perfData = perfCmd.DataBuffer;
                    uint speedKb = MmcCommands.ReadBE32(perfData, 8);
                    result.CurrentReadSpeed = $"{speedKb} KB/s";
                }
            }
            catch
            {
            }
        }
        catch
        {
        }

        return result;
    }

    private static string ReadMidFromDisc(OpticalDrive drive, ushort profile)
    {
        try
        {
            if (OpticalDrive.IsProfileDvd(profile))
            {
                var mfg = drive.ReadManufacturerId(MmcCommands.DiscStructureFormatManufacturer);
                if (!string.IsNullOrWhiteSpace(mfg))
                    return SanitizeMid(mfg.Trim());

                // Some drives need mediaType=1 (DVD-RW/-R) for format 0x04
                var (result, data) = drive.ReadDiscStructure(1, 0,
                    MmcCommands.DiscStructureFormatManufacturer, 2052);
                if (result.Success && result.DataTransferred >= 8)
                {
                    var dataLen = (data[0] << 8) | data[1];
                    if (dataLen >= 4)
                    {
                        var fallback = ReadManufacturerIdFromRaw(data, dataLen, result.DataTransferred);
                        if (!string.IsNullOrWhiteSpace(fallback))
                            return SanitizeMid(fallback.Trim());
                    }
                }
            }
            else if (profile >= 0x0040)
            {
                var mfg = drive.ReadManufacturerId(MmcCommands.DiscStructureFormatMediaId);
                if (!string.IsNullOrWhiteSpace(mfg))
                    return SanitizeMid(mfg.Trim());
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static string ReadManufacturerIdFromRaw(byte[] data, int dataLen, int dataTransferred)
    {
        int stringLen = Math.Min(dataLen, dataTransferred - 4);
        stringLen = Math.Min(stringLen, data.Length - 4);
        if (stringLen <= 0) return string.Empty;
        return System.Text.Encoding.ASCII.GetString(data, 4, stringLen).TrimEnd('\0', ' ');
    }

    /// <summary>
    /// Strips non-printable/non-ASCII characters from a raw MID string.
    /// DVD-ROM pressed discs return binary lead-in data, not clean ASCII text.
    /// </summary>
    private static string SanitizeMid(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return raw;

        var chars = new System.Collections.Generic.List<char>(raw.Length);
        foreach (var c in raw)
        {
            if (c >= 0x20 && c <= 0x7E)
                chars.Add(c);
        }

        var result = new string(chars.ToArray());
        // If after sanitizing we have very little, return empty
        return result.Length >= 3 ? result : string.Empty;
    }

    private static string ReadRmaInfo(OpticalDrive drive)
    {
        // READ DISC STRUCTURE format 0x0D (RMA Information).
        // Returns Recording Management Data including the last recorder's
        // drive manufacturer, model, and serial number.
        //
        // MMC-5 layout (first RMD block at offset 20+):
        //   0-7   : Drive Manufacturer ID (ASCII)
        //   8-23  : Model ID (ASCII, space-padded)
        //   24-31 : Serial Number (ASCII, space-padded)
        //   32-47 : Date/Time of last recording
        try
        {
            const int allocationLength = 2052;
            var (result, data) = drive.ReadDiscStructure(
                0, 0, MmcCommands.DiscStructureFormatRma, allocationLength);
            if (!result.Success || result.DataTransferred < 60)
                return string.Empty;

            int offset = 20; // skip header (4 len + 4 reserved + 4 update + 4 addr + 4 size)
            if (offset + 48 > data.Length)
                return string.Empty;

            var mfg  = ExtractAscii(data, offset, 8);
            var model = ExtractAscii(data, offset + 8, 16);
            var serial = ExtractAscii(data, offset + 24, 8);

            if (string.IsNullOrWhiteSpace(mfg) && string.IsNullOrWhiteSpace(model))
                return string.Empty;

            var parts = new System.Collections.Generic.List<string>(3);
            if (!string.IsNullOrWhiteSpace(mfg))   parts.Add(mfg.Trim());
            if (!string.IsNullOrWhiteSpace(serial)) parts.Add(serial.Trim());
            if (!string.IsNullOrWhiteSpace(model))  parts.Add(model.Trim());

            return string.Join(" ", parts);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ExtractAscii(byte[] data, int offset, int length)
    {
        var chars = new char[length];
        int count = 0;
        int end = Math.Min(offset + length, data.Length);
        for (int i = offset; i < end; i++)
        {
            var b = data[i];
            if (b >= 0x20 && b <= 0x7E)
                chars[count++] = (char)b;
        }
        return new string(chars, 0, count);
    }

    private static AtipInfo? ReadAtipInfo(OpticalDrive drive)
    {
        try
        {
            var atip = drive.ReadAtip();
            if (atip == null || atip.RawData.Length < 8)
                return null;

            var data = atip.RawData;

            string discId = string.Empty;
            string leadInStart = string.Empty;
            string leadOutLast = string.Empty;

            if (data.Length >= 12)
            {
                int minute = data[8];
                int second = data[9];
                int frame = data[10];
                if (minute > 0 || second > 0 || frame > 0)
                {
                    discId = $"{minute}m{second}s{frame:D2}f";
                    leadInStart = $"{minute}m{second}s{frame:D2}f";
                }
            }

            if (data.Length >= 20)
            {
                int loMin = data[18];
                int loSec = data[19];
                int loFrames = data.Length > 20 ? data[20] : 0;
                if (loMin > 0 && loMin <= 99)
                {
                    leadOutLast = $"{loMin}m{loSec}s{loFrames:D2}f";
                }
            }

            string manufacturer = !string.IsNullOrEmpty(discId)
                ? MediaManufacturerLookup.LookupAtipManufacturer(discId)
                : string.Empty;

            return new AtipInfo
            {
                DiscId = discId,
                Manufacturer = manufacturer,
                LeadInStart = leadInStart,
                LeadOutLastPossible = leadOutLast
            };
        }
        catch
        {
            return null;
        }
    }

    private static List<PhysicalFormatInfo> ReadPhysicalFormatInfo(OpticalDrive drive)
    {
        var formats = new List<PhysicalFormatInfo>();

        try
        {
            for (byte layer = 0; layer < 4; layer++)
            {
                var (result, data) = drive.ReadDiscStructure(0, layer,
                    MmcCommands.DiscStructureFormatPhysical, 2052);
                if (!result.Success || result.DataTransferred < 20)
                    break;

                var info = new PhysicalFormatInfo
                {
                    LayerNumber = layer,
                    BookType = ParseBookType((byte)((data[4] >> 4) & 0x0F)),
                    PartVersion = data[4] & 0x0F,
                    DiscSize = ParseDiscSize((byte)((data[5] >> 4) & 0x0F)),
                    MaxReadRateMbps = (data[5] & 0x0F) * 1.12,
                    LayerCount = ((data[6] >> 5) & 0x03) + 1,
                    TrackPath = (data[6] & 0x10) != 0 ? "Opposite Track Path (OTP)" : "Parallel Track Path (PTP)",
                    LinearDensity = (data[7] >> 4) switch
                    {
                        0 => "0.267 um/bit",
                        1 => "0.293 um/bit",
                        2 => "0.353 um/bit",
                        3 => "0.409 um/bit",
                        4 => "0.480 um/bit",
                        5 => "0.533 um/bit",
                        _ => $"Unknown ({data[7] >> 4})"
                    },
                    TrackDensity = (data[7] & 0x0F) switch
                    {
                        0 => "0.74 um/track",
                        1 => "0.80 um/track",
                        _ => $"Unknown ({data[7] & 0x0F})"
                    },
                    FirstPhysicalSector = MmcCommands.ReadBE32(data, 8),
                    LastPhysicalSector = MmcCommands.ReadBE32(data, 12),
                    LastSectorLayer0 = MmcCommands.ReadBE32(data, 16),
                };

                formats.Add(info);

                if (layer == 0 && info.LayerCount <= 1)
                    break;
            }
        }
        catch
        {
        }

        return formats;
    }

    private static string ParseBookType(byte bookType)
    {
        return bookType switch
        {
            0x00 => "DVD-ROM",
            0x01 => "DVD-RAM",
            0x02 => "DVD-R",
            0x03 => "DVD-RW",
            0x04 => "DVD+RW",
            0x05 => "DVD+R",
            0x06 => "DVD+RW DL",
            0x07 => "DVD+R DL",
            0x08 => "DVD-R DL",
            0x09 => "DVD-RW DL",
            0x0A => "HD DVD-ROM",
            0x0B => "HD DVD-R",
            0x0C => "HD DVD-RAM",
            0x0D => "HD DVD-RW",
            0x0E => "DVD",
            _ => $"Unknown (0x{bookType:X})"
        };
    }

    private static string ParseDiscSize(byte discSize)
    {
        return discSize switch
        {
            0 => "120 mm",
            1 => "80 mm",
            _ => $"Unknown ({discSize})"
        };
    }

    private static string LbaToMsf(int lba)
    {
        int totalFrames = lba + 150; // LBA 0 = audio frame 150 = 00:02:00
        int minutes = totalFrames / (75 * 60);
        int seconds = (totalFrames / 75) % 60;
        int frames = totalFrames % 75;
        return $"{minutes:D2}:{seconds:D2}:{frames:D2} (MM:SS:FF)";
    }

    private static string SectorsToMsf(long sectors)
    {
        if (sectors <= 0) return "00:00:00";

        long totalFrames = sectors;
        long minutes = totalFrames / (75 * 60);
        long seconds = (totalFrames / 75) % 60;
        long frames = totalFrames % 75;

        return $"{minutes:D2}:{seconds:D2}:{frames:D2} (MM:SS:FF)";
    }
}
