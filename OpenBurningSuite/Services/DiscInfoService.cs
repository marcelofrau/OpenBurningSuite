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

            try
            {
                // Raw hex dump of READ DISC INFO (34-byte allocation)
                var rawDiscInfoCmd = new ScsiCommand(
                    MmcCommands.BuildReadDiscInfo(34),
                    ScsiDataDirection.In, 34);
                var rawDiscInfoResult = drive.ExecuteRaw(rawDiscInfoCmd);
                string hex = rawDiscInfoResult.Success
                    ? BitConverter.ToString(rawDiscInfoCmd.DataBuffer, 0,
                        Math.Min(rawDiscInfoResult.DataTransferred, 34))
                    : "(SCSI failed)";
                result.DebugLog.Add($"RAW READ DISC INFO (34): Success={rawDiscInfoResult.Success}" +
                    $" dt={rawDiscInfoResult.DataTransferred} [{hex}]");
            }
            catch (Exception ex)
            {
                result.DebugLog.Add($"RAW READ DISC INFO exception: {ex.Message}");
            }

            try
            {
                // Raw hex dump of READ DISC STRUCTURE format 0x04 (Manufacturer / MID)
                // Try mediaType=0 (common) first, then mediaType=1 (DVD-RW/-R)
                for (byte mt = 0; mt <= 1; mt++)
                {
                    var (scsiRes, midData) = drive.ReadDiscStructure(mt, 0,
                        MmcCommands.DiscStructureFormatManufacturer, 256);
                    string midHex = scsiRes.Success
                        ? BitConverter.ToString(midData, 0, Math.Min(scsiRes.DataTransferred, 32))
                        : "(SCSI failed)";
                    result.DebugLog.Add($"RAW MANUFACTURER ID (mt={mt}): Success={scsiRes.Success}" +
                        $" dt={scsiRes.DataTransferred} [{midHex}]");
                }
            }
            catch (Exception ex)
            {
                result.DebugLog.Add($"RAW MANUFACTURER ID exception: {ex.Message}");
            }

            try
            {
                // Raw hex dump of READ DISC STRUCTURE format 0x05 (DVD+RW ADIP / CMI)
                // MMC-5: format 0x05 returns ADIP data for DVD+RW/+R media including Disc ID.
                var (adiRes, adiData) = drive.ReadDiscStructure(0, 0, 0x05, 256);
                string adiHex = adiRes.Success
                    ? BitConverter.ToString(adiData, 0, Math.Min(adiRes.DataTransferred, 64))
                    : "(SCSI failed)";
                result.DebugLog.Add($"RAW ADIP (fmt=0x05): Success={adiRes.Success}" +
                    $" dt={adiRes.DataTransferred} [{adiHex}]");
                if (adiRes.Success && adiRes.DataTransferred >= 24)
                {
                    string discId = System.Text.Encoding.ASCII.GetString(adiData, 8,
                        Math.Min(16, adiRes.DataTransferred - 8)).TrimEnd('\0', ' ');
                    result.DebugLog.Add($"ADIP DiscId (bytes 8-23): '{discId}'");
                }
            }
            catch (Exception ex)
            {
                result.DebugLog.Add($"RAW ADIP (fmt=0x05) exception: {ex.Message}");
            }

            try
            {
                // Raw hex dump of READ DISC STRUCTURE format 0x11 (DVD+R Media Manufacturer Info)
                var (fmt11Res, fmt11Data) = drive.ReadDiscStructure(0, 0, 0x11, 256);
                string fmt11Hex = fmt11Res.Success
                    ? BitConverter.ToString(fmt11Data, 0, Math.Min(fmt11Res.DataTransferred, 64))
                    : "(SCSI failed)";
                result.DebugLog.Add($"RAW DVD+R MEDIA MFG (fmt=0x11): Success={fmt11Res.Success}" +
                    $" dt={fmt11Res.DataTransferred} [{fmt11Hex}]");
                if (fmt11Res.Success && fmt11Res.DataTransferred >= 29)
                {
                    string mfg1926 = System.Text.Encoding.ASCII.GetString(fmt11Data, 19, 8).TrimEnd('\0', ' ');
                    string type2729 = System.Text.Encoding.ASCII.GetString(fmt11Data, 27, 3).TrimEnd('\0', ' ');
                    result.DebugLog.Add($"FMT 0x11: bytes 19-26='{mfg1926}' bytes 27-29='{type2729}'");
                }
            }
            catch (Exception ex)
            {
                result.DebugLog.Add($"RAW DVD+R MEDIA MFG (fmt=0x11) exception: {ex.Message}");
            }

            try
            {
                // Raw hex dump of READ DISC STRUCTURE format 0x0D (RMA)
                var rmaCmd = new ScsiCommand(
                    MmcCommands.BuildReadDiscStructure(0, 0,
                        MmcCommands.DiscStructureFormatRma, 2052),
                    ScsiDataDirection.In, 2052) { TimeoutMs = 10_000 };
                var rmaResult = drive.ExecuteRaw(rmaCmd);
                string rmaHex = rmaResult.Success
                    ? BitConverter.ToString(rmaCmd.DataBuffer, 0,
                        Math.Min(rmaResult.DataTransferred, 64))
                    : "(SCSI failed)";
                result.DebugLog.Add($"RAW RMA (0x0D): Success={rmaResult.Success}" +
                    $" dt={rmaResult.DataTransferred} [{rmaHex}]");
            }
            catch (Exception ex)
            {
                result.DebugLog.Add($"RAW RMA exception: {ex.Message}");
            }

            try
            {
                // Raw hex dump of READ DISC STRUCTURE format 0x0E (Pre-recorded Lead-in)
                var preCmd = new ScsiCommand(
                    MmcCommands.BuildReadDiscStructure(0, 0,
                        MmcCommands.DiscStructureFormatPreRecordedLi, 256),
                    ScsiDataDirection.In, 256) { TimeoutMs = 10_000 };
                var preResult = drive.ExecuteRaw(preCmd);
                string preHex = preResult.Success
                    ? BitConverter.ToString(preCmd.DataBuffer, 0,
                        Math.Min(preResult.DataTransferred, 64))
                    : "(SCSI failed)";
                result.DebugLog.Add($"RAW PRERECORDED LI (0x0E): Success={preResult.Success}" +
                    $" dt={preResult.DataTransferred} [{preHex}]");
            }
            catch (Exception ex)
            {
                result.DebugLog.Add($"RAW PRERECORDED LI exception: {ex.Message}");
            }

            try
            {
                // Raw hex dump of READ TRACK INFO for track 1
                var rawTrk1Cmd = new ScsiCommand(
                    MmcCommands.BuildReadTrackInfo(1),
                    ScsiDataDirection.In, 48);
                var rawTrk1Result = drive.ExecuteRaw(rawTrk1Cmd);
                string trk1Hex = rawTrk1Result.Success
                    ? BitConverter.ToString(rawTrk1Cmd.DataBuffer, 0,
                        Math.Min(rawTrk1Result.DataTransferred, 48))
                    : "(SCSI failed)";
                result.DebugLog.Add($"RAW READ TRACK INFO (1): Success={rawTrk1Result.Success}" +
                    $" dt={rawTrk1Result.DataTransferred} [{trk1Hex}]");
            }
            catch (Exception ex)
            {
                result.DebugLog.Add($"RAW READ TRACK INFO (1) exception: {ex.Message}");
            }

            try
            {
                // Raw hex dump of Physical Format Information (format 0x00) — DiscId at bytes 24-39
                var (pfRes, pfData) = drive.ReadDiscStructure(0, 0,
                    MmcCommands.DiscStructureFormatPhysical, 256);
                string pfHex = pfRes.Success
                    ? BitConverter.ToString(pfData, 0, Math.Min(pfRes.DataTransferred, 48))
                    : "(SCSI failed)";
                result.DebugLog.Add($"RAW PHYSICAL FORMAT (fmt=0x00, L0): Success={pfRes.Success}" +
                    $" dt={pfRes.DataTransferred} [{pfHex}]");
                if (pfRes.Success && pfRes.DataTransferred >= 40)
                {
                    string discId24 = System.Text.Encoding.ASCII.GetString(pfData, 24,
                        Math.Min(16, pfRes.DataTransferred - 24)).TrimEnd('\0', ' ');
                    string discId23 = System.Text.Encoding.ASCII.GetString(pfData, 23,
                        Math.Min(16, pfRes.DataTransferred - 23)).TrimEnd('\0', ' ');
                    result.DebugLog.Add($"PHYSICAL DiscId (bytes 24-39): '{discId24}'");
                    result.DebugLog.Add($"PHYSICAL DiscId (bytes 23-38): '{discId23}'");
                    // Full hex dump of the relevant area
                    if (pfRes.DataTransferred >= 64)
                        result.DebugLog.Add($"PHYSICAL hex 0-63: " +
                            BitConverter.ToString(pfData, 0, 64));
                }
            }
            catch (Exception ex)
            {
                result.DebugLog.Add($"RAW PHYSICAL FORMAT exception: {ex.Message}");
            }

            try
            {
                // Try Physical Format with mediaType=2 (DVD+RW specific)
                for (byte mt = 1; mt <= 3; mt++)
                {
                    var (pf2Res, pf2Data) = drive.ReadDiscStructure(2, 0,
                        MmcCommands.DiscStructureFormatPhysical, 256);
                    string pf2Hex = pf2Res.Success
                        ? BitConverter.ToString(pf2Data, 0, Math.Min(pf2Res.DataTransferred, 64))
                        : "(SCSI failed)";
                    result.DebugLog.Add($"RAW PHYSICAL FORMAT (fmt=0x00, L0, mt=2): Success={pf2Res.Success}" +
                        $" dt={pf2Res.DataTransferred} [{pf2Hex}]");
                }
            }
            catch (Exception ex)
            {
                result.DebugLog.Add($"RAW PHYSICAL FORMAT (mt=2) exception: {ex.Message}");
            }

            // Try layer 1 Physical Format
            try
            {
                var (pf1Res, pf1Data) = drive.ReadDiscStructure(0, 1,
                    MmcCommands.DiscStructureFormatPhysical, 256);
                string pf1Hex = pf1Res.Success
                    ? BitConverter.ToString(pf1Data, 0, Math.Min(pf1Res.DataTransferred, 64))
                    : "(SCSI failed)";
                result.DebugLog.Add($"RAW PHYSICAL FORMAT (fmt=0x00, L1): Success={pf1Res.Success}" +
                    $" dt={pf1Res.DataTransferred} [{pf1Hex}]");
            }
            catch (Exception ex)
            {
                result.DebugLog.Add($"RAW PHYSICAL FORMAT (L1) exception: {ex.Message}");
            }

            // Full binary scan of Physical Format for MID-like ASCII patterns
            try
            {
                // try with full allocation (2052) — some drives return different data
                var (pfAllRes, pfAllData) = drive.ReadDiscStructure(0, 0,
                    MmcCommands.DiscStructureFormatPhysical, 2052);
                if (pfAllRes.Success && pfAllRes.DataTransferred >= 40)
                {
                    int dt = pfAllRes.DataTransferred;
                    // Full hex dump up to 128 bytes
                    result.DebugLog.Add($"PHYSICAL (2052 alloc) hex 0-127: " +
                        BitConverter.ToString(pfAllData, 0, Math.Min(dt, 128)));
                    // Find all printable ASCII runs >= 4 chars
                    var found = new System.Collections.Generic.List<string>();
                    var sb = new System.Text.StringBuilder();
                    for (int i = 4; i < Math.Min(dt, 256); i++)
                    {
                        if (pfAllData[i] >= 0x20 && pfAllData[i] <= 0x7E)
                            sb.Append((char)pfAllData[i]);
                        else
                        {
                            if (sb.Length >= 4)
                                found.Add(sb.ToString());
                            sb.Clear();
                        }
                    }
                    if (sb.Length >= 4)
                        found.Add(sb.ToString());
                    if (found.Count > 0)
                        result.DebugLog.Add($"PHYSICAL ASCII runs (>=4): {string.Join(" | ", found.Take(10))}");
                }
            }
            catch (Exception ex)
            {
                result.DebugLog.Add($"PHYSICAL ASCII scan exception: {ex.Message}");
            }

            // Try additional READ DISC STRUCTURE formats for MID
            try
            {
                var (fmtListRes, fmtListData) = drive.ReadDiscStructure(0, 0, 0xFF, 256);
                string flHex = fmtListRes.Success
                    ? BitConverter.ToString(fmtListData, 0, Math.Min(fmtListRes.DataTransferred, 64))
                    : "(SCSI failed)";
                result.DebugLog.Add($"FORMAT LIST (fmt=0xFF): Success={fmtListRes.Success}" +
                    $" dt={fmtListRes.DataTransferred} [{flHex}]");
            }
            catch (Exception ex)
            {
                result.DebugLog.Add($"FORMAT LIST exception: {ex.Message}");
            }
            try
            {
                var (uidRes, uidData) = drive.ReadDiscStructure(0, 0, 0x0F, 256);
                string uidHex = uidRes.Success
                    ? BitConverter.ToString(uidData, 0, Math.Min(uidRes.DataTransferred, 64))
                    : "(SCSI failed)";
                result.DebugLog.Add($"UNIQUE DISC ID (fmt=0x0F): Success={uidRes.Success}" +
                    $" dt={uidRes.DataTransferred} [{uidHex}]");
                if (uidRes.Success && uidRes.DataTransferred >= 12)
                {
                    string uidStr = System.Text.Encoding.ASCII.GetString(uidData, 4,
                        Math.Min(20, uidRes.DataTransferred - 4)).TrimEnd('\0', ' ');
                    result.DebugLog.Add($"UNIQUE DISC ID (bytes 4-23): '{uidStr}'");
                }
            }
            catch (Exception ex)
            {
                result.DebugLog.Add($"UNIQUE DISC ID exception: {ex.Message}");
            }
            try
            {
                var (bcaRes, bcaData) = drive.ReadDiscStructure(0, 0, 0x03, 256);
                string bcaHex = bcaRes.Success
                    ? BitConverter.ToString(bcaData, 0, Math.Min(bcaRes.DataTransferred, 64))
                    : "(SCSI failed)";
                result.DebugLog.Add($"BCA (fmt=0x03): Success={bcaRes.Success}" +
                    $" dt={bcaRes.DataTransferred} [{bcaHex}]");
                if (bcaRes.Success && bcaRes.DataTransferred >= 20)
                {
                    for (int off = 0; off <= Math.Min(bcaRes.DataTransferred - 4, 40); off++)
                    {
                        string bcaStr = System.Text.Encoding.ASCII.GetString(bcaData, 4 + off,
                            Math.Min(16, bcaRes.DataTransferred - 4 - off)).TrimEnd('\0', ' ');
                        var midCheck = SanitizeMid(bcaStr);
                        if (midCheck.Length >= 6)
                        {
                            result.DebugLog.Add($"BCA MID candidate (off={off}): '{midCheck}'");
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                result.DebugLog.Add($"BCA exception: {ex.Message}");
            }

            var discInfo = drive.ReadDiscInformationEx();
            if (discInfo != null)
            {
                result.DiscStatus = discInfo.DiscStatusString;
                result.LastSessionState = discInfo.LastSessionStateString;
                result.IsErasable = discInfo.Erasable;
                result.Sessions = discInfo.NumberOfSessions;
                result.FreeSectors = discInfo.FreeSectors;
                result.NextWritableAddress = discInfo.NextWritableAddress;
                result.DebugLog.Add($"ReadDiscInformationEx: OK," +
                    $" DiscStatus={discInfo.DiscStatus} ({result.DiscStatus})" +
                    $" LastSessionState={discInfo.LastSessionState} ({result.LastSessionState})" +
                    $" Sessions={discInfo.NumberOfSessions}" +
                    $" FreeSectors={discInfo.FreeSectors} NWA={discInfo.NextWritableAddress}");
            }
            else
            {
                result.DebugLog.Add("ReadDiscInformationEx: returned NULL");
            }

            // Fallback: ReadTrackInfo for FreeSectors/NWA if ReadDiscInformationEx
            // didn't return them (some drives don't report extended fields for DVD/BD).
            if (result.FreeSectors == 0 && (OpticalDrive.IsProfileDvd(profile) || profile >= 0x0040))
            {
                uint[] fallbackTracks = { 0xFF, 1 };
                foreach (var track in fallbackTracks)
                {
                    try
                    {
                        var ti = drive.ReadTrackInfo(track);
                        if (ti != null)
                        {
                            result.DebugLog.Add($"ReadTrackInfo({track}) fallback:" +
                                $" FreeBlocks={ti.FreeBlocks}" +
                                $" NWA={ti.NextWritableAddress} NwaValid={ti.NwaValid}" +
                                $" TrackSize={ti.TrackSize} LRA={ti.LastRecordedAddress}");
                            if (result.FreeSectors == 0 && ti.FreeBlocks > 0)
                            {
                                result.FreeSectors = ti.FreeBlocks;
                                result.FreeBytes = ti.FreeBlocks * 2048L;
                                result.FreeTimeFormatted = SectorsToMsf(ti.FreeBlocks);
                            }
                            if (result.NextWritableAddress == 0 && ti.NwaValid)
                                result.NextWritableAddress = ti.NextWritableAddress;
                            break;
                        }
                        else
                        {
                            result.DebugLog.Add($"ReadTrackInfo({track}) fallback: returned NULL");
                        }
                    }
                    catch (Exception ex)
                    {
                        result.DebugLog.Add($"ReadTrackInfo({track}) fallback exception: {ex.Message}");
                    }
                }
            }

            var toc = drive.ReadToc();
            if (toc != null)
            {
                result.DebugLog.Add($"ReadToc(): {toc.Entries.Count} entries, " +
                    $"firstTrack={toc.FirstTrack} lastTrack={toc.LastTrack}");
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
                else
                {
                    result.DebugLog.Add("ReadToc(): no LeadOut entry found");
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

                result.DebugLog.Add($"TrackInfos from TOC: {result.TrackInfos.Count} entries");
            }
            else
            {
                result.DebugLog.Add("ReadToc(): returned NULL");
            }

            // Fallback for DVD-R blank/appendable: TOC may be null or have no track entries,
            // but ReadTrackInfo(1) usually works.
            if (result.TrackInfos.Count == 0 && OpticalDrive.IsProfileDvd(profile))
            {
                PopulateTrackInfoFallback(drive, result);
            }

            // Fix up DataMode for pressed CD-ROMs where ReadTrackInfo returns 0.
            if (toc != null && profile is >= 0x0008 and <= 0x000B && result.TrackInfos.Count > 0)
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

            result.DebugLog.Add($"PROFILE: 0x{profile:X4} ({result.MediaType})");

            // Log what ReadManufacturerId returns before sanitize (hex to avoid binary in output)
            var rawMfg = drive.ReadManufacturerId(MmcCommands.DiscStructureFormatManufacturer);
            string mfgHex = rawMfg != null
                ? BitConverter.ToString(System.Text.Encoding.ASCII.GetBytes(rawMfg))
                : "(null)";
            result.DebugLog.Add($"READ MANUFACTURER ID (fmt=0x04): [{mfgHex}]");

            result.Mid = SanitizeMid(ReadMidFromDisc(drive, profile) ?? string.Empty);
            string finalMidHex = !string.IsNullOrEmpty(result.Mid)
                ? BitConverter.ToString(System.Text.Encoding.ASCII.GetBytes(result.Mid))
                : "(empty)";
            result.DebugLog.Add($"MID (after sanitize, hex): [{finalMidHex}]");
            result.ManufacturerName = MediaManufacturerLookup.LookupDvdBdManufacturer(result.Mid);
            result.PreRecordedManufacturerId = result.Mid;

            if (OpticalDrive.IsProfileDvd(profile))
            {
                result.RecordingManagementArea = ReadRmaInfo(drive, result);
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

                // MID fallback: log DiscIds from Physical Format entries
                for (int i = 0; i < result.PhysicalFormats.Count && i < 4; i++)
                {
                    string didHex = !string.IsNullOrEmpty(result.PhysicalFormats[i].DiscId)
                        ? BitConverter.ToString(System.Text.Encoding.ASCII.GetBytes(result.PhysicalFormats[i].DiscId))
                        : "(empty)";
                    result.DebugLog.Add($"PHYSICAL FORMAT[{i}] DiscId (hex): [{didHex}]");
                }

                if (string.IsNullOrWhiteSpace(result.Mid) && result.PhysicalFormats.Count > 0
                    && !string.IsNullOrWhiteSpace(result.PhysicalFormats[0].DiscId))
                {
                    result.Mid = result.PhysicalFormats[0].DiscId;
                    result.ManufacturerName = MediaManufacturerLookup.LookupDvdBdManufacturer(result.Mid);
                    result.PreRecordedManufacturerId = result.Mid;
                    result.DebugLog.Add($"MID FALLBACK: used DiscId={result.Mid}");
                }
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
                // DVD+R / DVD+RW / DVD+R DL / DVD+RW DL: MID from ADIP (fmt 0x05) or DiscId.
                // Format 0x00 Physical Format Info uses DVD-ROM book type on empty media
                // (bitsetting), so DiscId at bytes 24-39 may be absent/corrupted.
                bool isDvdPlusR = profile is 0x001A or 0x001B or 0x002A or 0x002B;
                if (isDvdPlusR)
                {
                    // 1) Format 0x11 = DVD+R Media Manufacturer Info (libburn primary method)
                    var (fmt11Res, fmt11Data) = drive.ReadDiscStructure(0, 0, 0x11, 256);
                    if (fmt11Res.Success && fmt11Res.DataTransferred >= 29)
                    {
                        var manuf = SanitizeMid(System.Text.Encoding.ASCII.GetString(fmt11Data, 19, 8)).Trim();
                        if (manuf.Length >= 4)
                        {
                            string typeCode = System.Text.Encoding.ASCII.GetString(fmt11Data, 27, 3).TrimEnd('\0', ' ');
                            if (!string.IsNullOrWhiteSpace(typeCode))
                                manuf = manuf + typeCode;
                            // Look up full MID in database via prefix match
                            var fullMid = MediaManufacturerLookup.LookupDvdBdFullMid(manuf);
                            if (fullMid.Length > manuf.Length)
                                return fullMid;
                            // Also try with bytes 31-33 (DL type e.g. "DL1")
                            if (fmt11Res.DataTransferred >= 34)
                            {
                                string dlType = System.Text.Encoding.ASCII.GetString(fmt11Data, 31, 3).TrimEnd('\0', ' ');
                                if (!string.IsNullOrWhiteSpace(dlType))
                                {
                                    string combo = SanitizeMid(manuf + dlType);
                                    fullMid = MediaManufacturerLookup.LookupDvdBdFullMid(combo);
                                    if (fullMid.Length > manuf.Length)
                                        return fullMid;
                                }
                            }
                            return manuf;
                        }
                    }

                    // 2) Format 0x00 Physical Format: libburn-style offset 19-26
                    var (pfRes, pfData) = drive.ReadDiscStructure(0, 0,
                        MmcCommands.DiscStructureFormatPhysical, 256);
                    if (pfRes.Success && pfRes.DataTransferred >= 29)
                    {
                        var pfManuf = SanitizeMid(System.Text.Encoding.ASCII.GetString(pfData, 19, 8)).Trim();
                        if (pfManuf.Length >= 4)
                        {
                            string pfType = System.Text.Encoding.ASCII.GetString(pfData, 27, 2).TrimEnd('\0', ' ');
                            if (!string.IsNullOrWhiteSpace(pfType))
                                pfManuf = pfManuf + pfType;
                            var fullMid = MediaManufacturerLookup.LookupDvdBdFullMid(pfManuf);
                            if (fullMid.Length > pfManuf.Length)
                                return fullMid;
                            return pfManuf;
                        }
                    }

                    // 3) Format 0x05 ADIP (plain ASCII DiscId starting at byte 8)
                    var adip = ReadMidFromAdip(drive);
                    if (!string.IsNullOrWhiteSpace(adip))
                    {
                        var fullMid = MediaManufacturerLookup.LookupDvdBdFullMid(adip);
                        if (fullMid.Length > adip.Length)
                            return fullMid;
                        return adip;
                    }

                    // 4) Fallback: DiscId from Physical Format Info (bytes 23-38 heuristic)
                    var formats = ReadPhysicalFormatInfo(drive);
                    if (formats.Count > 0 && !string.IsNullOrWhiteSpace(formats[0].DiscId))
                    {
                        var fullMid = MediaManufacturerLookup.LookupDvdBdFullMid(formats[0].DiscId);
                        if (fullMid.Length > formats[0].DiscId.Length)
                            return fullMid;
                        return formats[0].DiscId;
                    }
                }

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

                // Fallback: Pre-recorded Information in Lead-in (format 0x0E)
                var mid = ReadMidFromPreRecordedLi(drive);
                if (!string.IsNullOrWhiteSpace(mid))
                    return mid;
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

    private static string ReadMidFromPreRecordedLi(OpticalDrive drive)
    {
        try
        {
            var (result, data) = drive.ReadDiscStructure(0, 0,
                MmcCommands.DiscStructureFormatPreRecordedLi, 256);
            if (!result.Success || result.DataTransferred < 8)
                return string.Empty;

            int dt = result.DataTransferred;
            // DVD-R ADIP data: 8-byte blocks starting at offset 4
            //   Byte 0: block sequence ID (01, 02, 03...)
            //   Bytes 1-7: data (7 bytes)
            // IDs 3+4 concatenated = manufacturer ID (e.g. "MCC 02" + "RG20  ")
            var midParts = new System.Collections.Generic.List<string>();
            for (int offset = 4; offset + 8 <= dt; offset += 8)
            {
                byte blockId = data[offset];
                if (blockId >= 3 && blockId <= 4)
                {
                    var part = System.Text.Encoding.ASCII.GetString(data, offset + 1, 7)
                        .TrimEnd('\0', ' ');
                    if (!string.IsNullOrWhiteSpace(part))
                        midParts.Add(part);
                }
            }

            if (midParts.Count > 0)
            {
                var mid = string.Concat(midParts);
                return !string.IsNullOrWhiteSpace(mid) ? SanitizeMid(mid.Trim()) : string.Empty;
            }

            // Fallback: scan for ASCII printable run >= 4 chars (any block structure)
            var sb = new System.Text.StringBuilder();
            for (int i = 4; i < Math.Min(dt, 200); i++)
            {
                if (data[i] >= 0x20 && data[i] <= 0x7E)
                    sb.Append((char)data[i]);
                else if (sb.Length >= 4)
                    break;
                else
                    sb.Clear();
            }
            var asciiRun = sb.ToString().Trim();
            if (asciiRun.Length >= 4)
                return SanitizeMid(asciiRun);
        }
        catch
        {
        }

        return string.Empty;
    }

    private static string ReadMidFromAdip(OpticalDrive drive)
    {
        try
        {
            // MMC-5: READ DISC STRUCTURE format 0x05 = DVD+RW ADIP Information.
            // For DVD+R/RW media, the Disc ID is at bytes 8-23 as plain ASCII.
            var (result, data) = drive.ReadDiscStructure(0, 0, 0x05, 256);
            if (!result.Success || result.DataTransferred < 24)
                return string.Empty;

            string discId = System.Text.Encoding.ASCII.GetString(data, 8,
                Math.Min(16, result.DataTransferred - 8)).TrimEnd('\0', ' ');
            return SanitizeMid(discId);
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Strips non-printable/non-ASCII characters from a raw MID string.
    /// DVD-ROM pressed discs return binary lead-in data, not clean ASCII text.
    /// Only allows alphanumeric, space, hyphen, underscore — MID never contains symbols.
    /// </summary>
    private static string SanitizeMid(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return raw;

        var chars = new System.Collections.Generic.List<char>(raw.Length);
        foreach (var c in raw)
        {
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                (c >= '0' && c <= '9') || c == ' ' || c == '-' || c == '_')
                chars.Add(c);
        }

        var result = new string(chars.ToArray());
        // If after sanitizing we have very little, return empty
        return result.Length >= 3 ? result : string.Empty;
    }

    private static void PopulateTrackInfoFallback(OpticalDrive drive, DiscInfoResult result)
    {
        uint[] fallbackTracks = { 1, 0xFF };
        foreach (var track in fallbackTracks)
        {
            try
            {
                var ti = drive.ReadTrackInfo(track);
                if (ti != null)
                {
                    if (track == 0xFF)
                    {
                        result.TrackInfos.Add(new TrackInfoData
                        {
                            TrackNumber = 1,
                            TrackStartAddress = ti.TrackStartAddress,
                            TrackSize = ti.TrackSize,
                            LastRecordedAddress = ti.LastRecordedAddress
                        });
                    }
                    else
                    {
                        result.TrackInfos.Add(ti);
                    }
                    result.DebugLog.Add($"ReadTrackInfo({track}) track fallback:" +
                        $" TrackSize={ti.TrackSize} LRA={ti.LastRecordedAddress}");
                    return;
                }
                else
                {
                    result.DebugLog.Add($"ReadTrackInfo({track}) track fallback: returned NULL");
                }
            }
            catch (Exception ex)
            {
                result.DebugLog.Add($"ReadTrackInfo({track}) track fallback exception: {ex.Message}");
            }
        }
    }

    private static string ReadRmaInfo(OpticalDrive drive, DiscInfoResult discResult)
    {
        try
        {
            const int allocationLength = 2052;
            var (scsiResult, data) = drive.ReadDiscStructure(
                0, 0, MmcCommands.DiscStructureFormatRma, allocationLength);
            if (!scsiResult.Success || scsiResult.DataTransferred < 60)
                return string.Empty;

            int dt = scsiResult.DataTransferred;

            // Try multiple RMD block offsets (header sizes vary by drive/firmware)
            int[] possibleOffsets = { 16, 20, 24, 32, 64, 128, 256 };
            foreach (int offset in possibleOffsets)
            {
                if (offset + 48 > dt)
                    continue;

                var mfg = ExtractAscii(data, offset, 8);
                var model = ExtractAscii(data, offset + 8, 16);
                var serial = ExtractAscii(data, offset + 24, 8);

                if (!string.IsNullOrWhiteSpace(mfg) && mfg.Trim().Length >= 2)
                {
                    var parts = new System.Collections.Generic.List<string>(3);
                    parts.Add(mfg.Trim());
                    if (!string.IsNullOrWhiteSpace(serial)) parts.Add(serial.Trim());
                    if (!string.IsNullOrWhiteSpace(model)) parts.Add(model.Trim());

                    discResult.DebugLog.Add($"RMA parsed at offset {offset}:" +
                        $" mfg=[{mfg}] model=[{model}] serial=[{serial}]");
                    return string.Join(" ", parts);
                }
            }

            // Dump first 128 bytes + last 128 bytes for debugging
            string hex128 = BitConverter.ToString(data, 0, Math.Min(128, dt));
            string hexTail = dt > 128
                ? BitConverter.ToString(data, dt - 128, 128)
                : "(buffer <= 128)";
            discResult.DebugLog.Add($"RMA: no valid RMD block found in {dt} bytes," +
                $" head=[{hex128}], tail=[{hexTail}]");

            // Last resort: scan entire buffer for printable ASCII runs >= 4 chars
            var sb = new System.Text.StringBuilder();
            var asciiHits = new System.Collections.Generic.List<string>();
            for (int i = 0; i < dt; i++)
            {
                if (data[i] >= 0x20 && data[i] <= 0x7E)
                    sb.Append((char)data[i]);
                else
                {
                    if (sb.Length >= 4)
                        asciiHits.Add(sb.ToString().Trim());
                    sb.Clear();
                }
            }
            if (sb.Length >= 4)
                asciiHits.Add(sb.ToString().Trim());
            if (asciiHits.Count > 0)
                discResult.DebugLog.Add($"RMA ASCII runs: [{string.Join("] [", asciiHits)}]");

            return string.Empty;
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
                    DiscId = result.DataTransferred >= 40
                        ? SanitizeMid(System.Text.Encoding.ASCII.GetString(data, 23, 16))
                        : string.Empty,
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
