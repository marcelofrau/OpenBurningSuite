// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using OpenBurningSuite.Models;
using OpenBurningSuite.Native.Optical;
using OpenBurningSuite.Native.Scsi;

namespace OpenBurningSuite.Native.Platform;

public static partial class NativeDeviceDiscovery
{
    private static List<DiscDrive> DiscoverWindows()
    {
        var drives = new List<DiscDrive>();

        // Use .NET DriveInfo API (no external tools needed)
        try
        {
            foreach (var di in DriveInfo.GetDrives())
            {
                if (di.DriveType != DriveType.CDRom) continue;

                // Cache IsReady to avoid repeated calls (each triggers OS I/O).
                // DriveInfo.IsReady can throw IOException when the drive is in a transient
                // state (e.g., firmware reset, tray moving), so catch only IO-related exceptions.
                bool isReady;
                try { isReady = di.IsReady; }
                catch (IOException) { isReady = false; }
                catch (UnauthorizedAccessException) { isReady = false; }

                var drive = new DiscDrive
                {
                    DriveLetter = di.Name,
                    DevicePath = di.Name,
                    DriveModel = "Optical Drive",
                    DriveType = "CD/DVD",
                    CanRead = true,
                    CanWrite = false,
                    CanReadDVD = false,
                    CanWriteDVD = false,
                    Status = isReady ? "Ready" : "No Media"
                };

                // Try SCSI INQUIRY for model name and GET CONFIGURATION for capabilities
                try
                {
                    using var optDrive = new OpticalDrive(di.Name);
                    var (vendor, product, _) = optDrive.Inquiry();
                    if (vendor != "Unknown")
                        drive.DriveModel = $"{vendor} {product}".Trim();

                    // Probe drive capabilities via GET CONFIGURATION feature list
                    ProbeCapabilities(optDrive, drive);

                    // Probe write speeds
                    var speeds = optDrive.GetSupportedWriteSpeeds();
                    foreach (var s in speeds)
                        drive.SupportedWriteSpeeds.Add($"{s} KB/s");

                    // Probe read speeds
                    var readSpeeds = optDrive.GetSupportedReadSpeeds();
                    foreach (var s in readSpeeds)
                        drive.SupportedReadSpeeds.Add($"{s} KB/s");

                    // Probe media via SCSI — works even when DriveInfo.IsReady is false.
                    // This catches cases where the OS hasn't registered the media yet
                    // but the drive has already recognized it (common during spin-up or
                    // with drives that the OS doesn't fully enumerate).
                    var profile = optDrive.GetCurrentProfile();
                    if (profile != 0)
                    {
                        drive.Status = "Ready";
                        if (drive.CurrentMedia == null)
                            drive.CurrentMedia = new DiscMedia();
                        drive.CurrentMedia.MediaType = OpticalDrive.ProfileToMediaType(profile);

                        // Check for M-DISC media
                        if (optDrive.IsMDiscMedia())
                        {
                            var mDiscType = optDrive.GetMDiscMediaType();
                            if (mDiscType != null)
                                drive.CurrentMedia.MediaType = mDiscType;
                        }

                        // Read disc info for session count and disc state
                        var discInfo = optDrive.ReadDiscInfo();
                        if (discInfo != null)
                        {
                            if (drive.CurrentMedia == null)
                                drive.CurrentMedia = new DiscMedia();
                            drive.CurrentMedia.DiscState = discInfo.DiscStatusString;
                            drive.CurrentMedia.Sessions = discInfo.NumberOfSessionsLsb;
                            drive.CurrentMedia.Tracks = discInfo.LastTrackInLastSessionLsb;
                        }

                        // Get media capacity via READ CAPACITY (used for both total capacity
                        // and block length in used bytes calculation below)
                        var capacity = optDrive.ReadCapacity();
                        if (capacity.HasValue && drive.CurrentMedia != null)
                        {
                            drive.CurrentMedia.CapacityBytes =
                                ((long)capacity.Value.LastLba + 1) * capacity.Value.BlockLength;
                        }

                        // Calculate used bytes from track info
                        if (discInfo != null && drive.CurrentMedia != null)
                        {
                            var lastTrack = discInfo.LastTrackInLastSessionLsb;
                            if (lastTrack > 0)
                            {
                                var trackInfo = optDrive.ReadTrackInfo((uint)lastTrack);
                                if (trackInfo != null)
                                {
                                    int blockLen = (capacity.HasValue && capacity.Value.BlockLength > 0)
                                        ? (int)capacity.Value.BlockLength : 2048;
                                    var usedSectors = trackInfo.TrackStartAddress + trackInfo.TrackSize;
                                    drive.CurrentMedia.UsedBytes = (long)usedSectors * blockLen;

                                    // For blank/empty discs where READ CAPACITY may fail,
                                    // use FreeBlocks from track info as capacity fallback
                                    if (drive.CurrentMedia.CapacityBytes <= 0 && trackInfo.FreeBlocks > 0)
                                    {
                                        drive.CurrentMedia.CapacityBytes = (long)trackInfo.FreeBlocks * blockLen;
                                    }
                                }
                            }
                        }

                        // Try SCSI-based volume label probing when DriveInfo may not report it
                        // (disc not ready via OS, or not yet mounted). Read ISO 9660 PVD at
                        // sector 16, then UDF PVD at sector 256 as fallback.
                        if (drive.CurrentMedia != null && string.IsNullOrWhiteSpace(drive.CurrentMedia.VolumeLabel))
                        {
                            try
                            {
                                var (pvdResult, pvdData) = optDrive.ReadSectors(16, 1, 2048);
                                if (pvdResult.Success && pvdResult.DataTransferred >= 2048 &&
                                    pvdData[0] == 0x01 && pvdData[1] == (byte)'C' && pvdData[2] == (byte)'D' &&
                                    pvdData[3] == (byte)'0' && pvdData[4] == (byte)'0' && pvdData[5] == (byte)'1')
                                {
                                    var label = System.Text.Encoding.ASCII.GetString(pvdData, 40, 32).Trim();
                                    if (!string.IsNullOrWhiteSpace(label))
                                    {
                                        drive.CurrentMedia.VolumeLabel = label;
                                        drive.CurrentMedia.FileSystem = "ISO 9660";
                                    }
                                }

                                // Try UDF PVD at sector 256 if ISO label not found
                                if (string.IsNullOrWhiteSpace(drive.CurrentMedia.VolumeLabel))
                                {
                                    var (udfResult, udfData) = optDrive.ReadSectors(UdfPvdSector, 1, 2048);
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
                                                    udfLabel = System.Text.Encoding.BigEndianUnicode
                                                        .GetString(udfData, 25, identLen - 1).Trim('\0', ' ');
                                                else
                                                    udfLabel = System.Text.Encoding.UTF8
                                                        .GetString(udfData, 25, identLen - 1).Trim('\0', ' ');
                                                if (!string.IsNullOrWhiteSpace(udfLabel))
                                                {
                                                    drive.CurrentMedia.VolumeLabel = udfLabel;
                                                    drive.CurrentMedia.FileSystem = "UDF";
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                            catch { /* volume label probe is best-effort */ }
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[NativeDeviceDiscovery] SCSI probe failed for {di.Name}: {ex.Message}");
                }

                // Fallback: if SCSI INQUIRY failed, try Windows-native storage query
                // for drive model. Works for USB drives where SCSI passthrough may fail.
                if (drive.DriveModel == "Optical Drive")
                {
                    var nativeModel = GetWindowsStorageModel(di.Name);
                    if (nativeModel != null)
                        drive.DriveModel = nativeModel;
                }

                // Query bus type (USB, SATA, etc.)
                if (string.IsNullOrEmpty(drive.BusType))
                    drive.BusType = QueryBusType(di.Name);

                // Determine drive type string from capabilities
                if (string.IsNullOrEmpty(drive.DriveType) || drive.DriveType == "CD/DVD")
                    drive.DriveType = DetermineWindowsDriveType(drive);

                // Get media info from .NET DriveInfo if drive is ready — update the existing
                // CurrentMedia instead of replacing it, to preserve SCSI-probed properties
                // (MediaType, DiscState, Sessions) set by the SCSI probe above.
                if (isReady)
                {
                    try
                    {
                        if (drive.CurrentMedia == null)
                            drive.CurrentMedia = new DiscMedia();

                        drive.CurrentMedia.VolumeLabel = di.VolumeLabel;
                        drive.CurrentMedia.FileSystem = di.DriveFormat;
                        // Only override CapacityBytes if SCSI didn't already provide it
                        if (drive.CurrentMedia.CapacityBytes <= 0)
                            drive.CurrentMedia.CapacityBytes = di.TotalSize;
                        // Only override UsedBytes if SCSI track info didn't already provide it
                        if (drive.CurrentMedia.UsedBytes <= 0)
                            drive.CurrentMedia.UsedBytes = di.TotalSize - di.AvailableFreeSpace;

                        // Only override DiscState if SCSI didn't provide it
                        if (string.IsNullOrEmpty(drive.CurrentMedia.DiscState))
                            drive.CurrentMedia.DiscState = "Closed";
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[NativeDeviceDiscovery] DriveInfo media probe failed for {di.Name}: {ex.Message}");
                    }
                }

                drives.Add(drive);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[NativeDeviceDiscovery] Windows drive enumeration error: {ex.Message}");
        }

        return drives;
    }

    /// <summary>
    /// Probes drive capabilities via GET CONFIGURATION feature list.
    /// Feature 0x001E = CD Read, 0x001F = DVD Read, 0x0040 = BD Read
    /// Feature 0x002D = CD-R/RW Write, 0x002F = DVD-R Write, 0x0041 = BD-R Write
    /// </summary>
    private static void ProbeCapabilities(OpticalDrive optDrive, DiscDrive drive)
    {
        try
        {
            // Two-phase approach: first query with a small buffer to get the total
            // data length, then re-query with a properly sized buffer.
            // A 1024-byte buffer is too small for BD writers with many features,
            // causing Blu-ray and HD DVD capabilities to be truncated.
            var probeCmd = new ScsiCommand(
                MmcCommands.BuildGetConfiguration(0x0000, 8),
                ScsiDataDirection.In, 8);
            var probeResult = optDrive.ExecuteRaw(probeCmd);

            // Default to 4096 which covers most drives; use actual length if available
            int bufferSize = 4096;
            if (probeResult.Success && probeResult.DataTransferred >= 4)
            {
                // Bytes 0-3: Data Length (total bytes following this field)
                var totalLength = (int)MmcCommands.ReadBE32(probeCmd.DataBuffer, 0) + 4;
                // Clamp to a reasonable maximum to prevent excessive allocation
                bufferSize = Math.Clamp(totalLength, 512, 65534);
            }

            var cmd = new ScsiCommand(
                MmcCommands.BuildGetConfiguration(0x0000, (ushort)bufferSize),
                ScsiDataDirection.In, bufferSize);
            var result = optDrive.ExecuteRaw(cmd);
            if (!result.Success || result.DataTransferred < 12) return;

            var data = cmd.DataBuffer;
            int featureStart = 8;

            while (featureStart + 4 <= result.DataTransferred)
            {
                var featureCode = MmcCommands.ReadBE16(data, featureStart);
                var additionalLength = data[featureStart + 3];

                // Validate: feature record must fit within the response data
                if (featureStart + 4 + additionalLength > result.DataTransferred)
                    break; // Malformed response — stop parsing

                switch (featureCode)
                {
                    // Feature 0x0000: Profile List — enumerates all supported media profiles.
                    // Each profile descriptor is 4 bytes: 2-byte profile number, 1 byte flags
                    // (bit 0 = currentP), 1 reserved byte.
                    case 0x0000:
                        ParseProfileList(data, featureStart + 4, additionalLength, drive);
                        break;

                    // Feature-based capability flags for backward compatibility with
                    // the simple CanRead/CanWrite/CanReadDVD/etc. model.
                    // Feature codes verified against ntddmmc.h FEATURE_NUMBER enum
                    // from the ReactOS/Windows DDK (MMC-4 compliant).
                    //
                    // IMPORTANT: Feature PRESENCE indicates READ support.
                    // WRITE support requires checking the Write bit in feature data,
                    // which is done in ParseFeatureCapabilities below.
                    case 0x001E: drive.CanRead = true; break;       // FeatureCdRead
                    case 0x001F: drive.CanReadDVD = true; break;    // FeatureDvdRead

                    case 0x002D: drive.CanWrite = true; break;      // FeatureCdTrackAtOnce (presence = CD write)
                    case 0x002E: drive.CanWrite = true; break;      // FeatureCdMastering (SAO/DAO, presence = CD write)
                    case 0x002F: drive.CanWriteDVD = true; break;   // FeatureDvdRecordableWrite (presence = DVD-R write)
                    case 0x0033: break;                              // FeatureLayerJumpRecording (parsed in ParseFeatureCapabilities)
                    case 0x0037: drive.CanWrite = true; break;      // FeatureCDRWMediaWriteSupport (presence = CD-RW write)
                    case 0x002A: drive.CanReadDVD = true; break;    // FeatureDvdPlusRW (presence = read; write via Write bit)
                    case 0x002B: drive.CanReadDVD = true; break;    // FeatureDvdPlusR (presence = read; write via Write bit)
                    case 0x003A: drive.CanReadDVD = true; break;    // FeatureDvdPlusRWDualLayer (presence = read; write via Write bit)
                    case 0x003B: drive.CanReadDVD = true; break;    // FeatureDvdPlusRDualLayer (presence = read; write via Write bit)
                    case 0x0040: drive.CanReadBluRay = true; break; // FeatureBDRead
                    case 0x0041: drive.CanWriteBluRay = true; break;// FeatureBDWrite (BD-R SRM/SRM+POW)
                    case 0x0042: drive.CanWriteBluRay = true; break;// FeatureTSR (BD timely safe recording)
                    case 0x0050: drive.CanReadHDDVD = true; break;  // FeatureHDDVDRead
                    case 0x0051: drive.CanWriteHDDVD = true; break; // FeatureHDDVDWrite
                }

                // Parse additional feature-specific capability bits
                ParseFeatureCapabilities(featureCode, data, featureStart, additionalLength, drive);

                featureStart += 4 + additionalLength;
            }

            // Ensure base capabilities are set from higher-level capabilities
            // and derive high-level Can* flags from per-format profile capabilities.
            var p = drive.Profiles;

            // Derive CanWriteDVD from per-format DVD write capabilities
            if (p.WriteDvdR || p.WriteDvdRw || p.WriteDvdPlusR || p.WriteDvdPlusRw ||
                p.WriteDvdRDl || p.WriteDvdPlusRDl || p.WriteDvdPlusRwDl || p.WriteDvdRam)
                drive.CanWriteDVD = true;

            // Derive CanReadDVD from per-format DVD read capabilities
            if (p.ReadDvdRom || p.ReadDvdR || p.ReadDvdRw || p.ReadDvdPlusR || p.ReadDvdPlusRw ||
                p.ReadDvdRDl || p.ReadDvdPlusRDl || p.ReadDvdPlusRwDl || p.ReadDvdRam)
                drive.CanReadDVD = true;

            // Derive CanWrite/CanRead for CD from profiles
            if (p.WriteCdR || p.WriteCdRw) drive.CanWrite = true;
            if (p.ReadCdR || p.ReadCdRw) drive.CanRead = true;

            // Derive CanWriteHDDVD from per-format HD DVD write capabilities
            if (p.WriteHdDvdR || p.WriteHdDvdRw || p.WriteHdDvdRam || p.WriteHdDvdRDl || p.WriteHdDvdRwDl)
                drive.CanWriteHDDVD = true;

            // Derive CanReadHDDVD from per-format HD DVD read capabilities
            if (p.ReadHdDvdRom || p.ReadHdDvdR || p.ReadHdDvdRw || p.ReadHdDvdRam || p.ReadHdDvdRDl || p.ReadHdDvdRwDl)
                drive.CanReadHDDVD = true;

            // Derive CanWriteBluRay/CanReadBluRay from per-format BD capabilities
            if (p.WriteBdR || p.WriteBdRe || p.WriteBdRXl || p.WriteBdReXl)
                drive.CanWriteBluRay = true;
            if (p.ReadBdRom || p.ReadBdR || p.ReadBdRe || p.ReadBdRXl || p.ReadBdReXl)
                drive.CanReadBluRay = true;

            // Hierarchy: BD/HD DVD → DVD → CD
            if (drive.CanReadDVD) drive.CanRead = true;
            if (drive.CanReadBluRay) { drive.CanRead = true; drive.CanReadDVD = true; }
            if (drive.CanReadHDDVD) { drive.CanRead = true; drive.CanReadDVD = true; }
            if (drive.CanWriteDVD) drive.CanWrite = true;
            if (drive.CanWriteBluRay) { drive.CanWrite = true; drive.CanWriteDVD = true; }
            if (drive.CanWriteHDDVD) { drive.CanWrite = true; drive.CanWriteDVD = true; }

            // Probe hardware info (buffer size, vendor-specific info)
            ProbeHardwareInfo(optDrive, drive);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[NativeDeviceDiscovery] ProbeCapabilities failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Parses the Profile List feature (0x0000) from GET CONFIGURATION response.
    /// Each profile descriptor is 4 bytes: 2-byte profile number (BE), 1 byte flags, 1 reserved.
    /// A profile in the list means the drive supports that media type.
    /// Read-only profiles (ROM, read-only media) indicate read support.
    /// Writable profiles indicate both read and write support.
    /// </summary>
    private static void ParseProfileList(byte[] data, int offset, int length, DiscDrive drive)
    {
        var p = drive.Profiles;
        for (int i = 0; i + 3 < length; i += 4)
        {
            int pos = offset + i;
            if (pos + 1 >= data.Length) break;
            ushort profileNum = MmcCommands.ReadBE16(data, pos);

            switch (profileNum)
            {
                // CD profiles
                case 0x0008: // CD-ROM — read-only
                    p.ReadCdR = true; p.ReadCdRw = true;
                    break;
                case 0x0009: // CD-R
                    p.ReadCdR = true; p.WriteCdR = true;
                    break;
                case 0x000A: // CD-RW
                    p.ReadCdRw = true; p.WriteCdRw = true;
                    break;

                // DVD profiles
                case 0x0010: // DVD-ROM
                    p.ReadDvdRom = true;
                    break;
                case 0x0011: // DVD-R (Sequential Recording)
                    p.ReadDvdR = true; p.WriteDvdR = true;
                    break;
                case 0x0012: // DVD-RAM
                    p.ReadDvdRam = true; p.WriteDvdRam = true;
                    break;
                case 0x0013: // DVD-RW (Restricted Overwrite)
                    p.ReadDvdRw = true; p.WriteDvdRw = true;
                    break;
                case 0x0014: // DVD-RW (Sequential Recording)
                    p.ReadDvdRw = true; p.WriteDvdRw = true;
                    break;
                case 0x0015: // DVD-R DL (Sequential Recording)
                    p.ReadDvdRDl = true; p.WriteDvdRDl = true;
                    break;
                case 0x0016: // DVD-R DL (Layer Jump Recording)
                    p.ReadDvdRDl = true; p.WriteDvdRDl = true;
                    break;
                case 0x001A: // DVD+RW
                    p.ReadDvdPlusRw = true; p.WriteDvdPlusRw = true;
                    break;
                case 0x001B: // DVD+R
                    p.ReadDvdPlusR = true; p.WriteDvdPlusR = true;
                    break;
                case 0x002A: // DVD+RW DL
                    p.ReadDvdPlusRwDl = true; p.WriteDvdPlusRwDl = true;
                    break;
                case 0x002B: // DVD+R DL
                    p.ReadDvdPlusRDl = true; p.WriteDvdPlusRDl = true;
                    break;

                // HD DVD profiles
                // NOTE: HD DVD profiles only set READ capability here.
                // Some drives (e.g. LG BD-RE BU40N) list HD DVD writable profiles
                // to indicate read support, even though they cannot write HD DVD.
                // Write capability is determined by Feature 0x0051 (HD DVD Write)
                // in ParseFeatureCapabilities.
                case 0x0050: // HD DVD-ROM
                    p.ReadHdDvdRom = true;
                    break;
                case 0x0051: // HD DVD-R
                    p.ReadHdDvdR = true;
                    break;
                case 0x0052: // HD DVD-RAM
                    p.ReadHdDvdRam = true;
                    break;
                case 0x0053: // HD DVD-RW
                    p.ReadHdDvdRw = true;
                    break;
                case 0x0058: // HD DVD-R DL
                    p.ReadHdDvdRDl = true;
                    break;
                case 0x005A: // HD DVD-RW DL
                    p.ReadHdDvdRwDl = true;
                    break;

                // Blu-ray profiles
                case 0x0040: // BD-ROM
                    p.ReadBdRom = true;
                    break;
                case 0x0041: // BD-R SRM (Sequential Recording Mode)
                case 0x0042: // BD-R RRM (Random Recording Mode)
                    p.ReadBdR = true; p.WriteBdR = true;
                    break;
                case 0x0043: // BD-RE
                    p.ReadBdRe = true; p.WriteBdRe = true;
                    break;
                case 0x0044: // BD-ROM DL (counted as BD-ROM)
                    p.ReadBdRom = true;
                    break;
                case 0x0045: // BD-R DL (counted as BD-R)
                    p.ReadBdR = true; p.WriteBdR = true;
                    break;
                case 0x0046: // BD-RE DL (counted as BD-RE)
                    p.ReadBdRe = true; p.WriteBdRe = true;
                    break;
                // BDXL profiles (triple/quad layer)
                case 0x004A: // BD-R TL (BDXL)
                case 0x004C: // BD-R QL (BDXL)
                    p.ReadBdRXl = true; p.WriteBdRXl = true;
                    break;
                case 0x004B: // BD-RE TL (BDXL)
                case 0x004D: // BD-RE QL (BDXL)
                    p.ReadBdReXl = true; p.WriteBdReXl = true;
                    break;
            }
        }

        // Imply read support for writable formats:
        // A drive that can write a format can always read it.
        if (p.WriteCdR) p.ReadCdR = true;
        if (p.WriteCdRw) p.ReadCdRw = true;
        if (p.WriteDvdR) { p.ReadDvdR = true; p.ReadDvdRom = true; }
        if (p.WriteDvdRw) { p.ReadDvdRw = true; p.ReadDvdRom = true; }
        if (p.WriteDvdPlusR) { p.ReadDvdPlusR = true; p.ReadDvdRom = true; }
        if (p.WriteDvdPlusRw) { p.ReadDvdPlusRw = true; p.ReadDvdRom = true; }
        if (p.WriteDvdRDl) { p.ReadDvdRDl = true; p.ReadDvdRom = true; }
        if (p.WriteDvdPlusRDl) { p.ReadDvdPlusRDl = true; p.ReadDvdRom = true; }
        if (p.WriteDvdPlusRwDl) { p.ReadDvdPlusRwDl = true; p.ReadDvdRom = true; }
        if (p.WriteDvdRam) { p.ReadDvdRam = true; p.ReadDvdRom = true; }
        if (p.WriteHdDvdR) { p.ReadHdDvdR = true; p.ReadHdDvdRom = true; }
        if (p.WriteHdDvdRw) { p.ReadHdDvdRw = true; p.ReadHdDvdRom = true; }
        if (p.WriteHdDvdRam) { p.ReadHdDvdRam = true; p.ReadHdDvdRom = true; }
        if (p.WriteHdDvdRDl) { p.ReadHdDvdRDl = true; p.ReadHdDvdRom = true; }
        if (p.WriteHdDvdRwDl) { p.ReadHdDvdRwDl = true; p.ReadHdDvdRom = true; }
        if (p.WriteBdR) { p.ReadBdR = true; p.ReadBdRom = true; }
        if (p.WriteBdRe) { p.ReadBdRe = true; p.ReadBdRom = true; }
        if (p.WriteBdRXl) { p.ReadBdRXl = true; p.ReadBdRom = true; }
        if (p.WriteBdReXl) { p.ReadBdReXl = true; p.ReadBdRom = true; }

        // Imply DVD-ROM read from any DVD support
        if (p.ReadDvdR || p.ReadDvdRw || p.ReadDvdPlusR || p.ReadDvdPlusRw ||
            p.ReadDvdRDl || p.ReadDvdPlusRDl || p.ReadDvdPlusRwDl || p.ReadDvdRam)
            p.ReadDvdRom = true;

        // Imply HD DVD-ROM read from any HD DVD support
        if (p.ReadHdDvdR || p.ReadHdDvdRw || p.ReadHdDvdRam || p.ReadHdDvdRDl || p.ReadHdDvdRwDl)
            p.ReadHdDvdRom = true;

        // Imply BD-ROM read from any BD support
        if (p.ReadBdR || p.ReadBdRe || p.ReadBdRXl || p.ReadBdReXl)
            p.ReadBdRom = true;
    }

    /// <summary>
    /// Parses additional feature-specific capability bits from the GET CONFIGURATION response.
    /// Feature codes and bit layouts verified against ntddmmc.h (ReactOS/Windows DDK).
    /// This supplements the Profile List with per-format read/write capability data
    /// from individual feature descriptors.
    /// </summary>
    private static void ParseFeatureCapabilities(ushort featureCode, byte[] data,
        int featureStart, int additionalLength, DiscDrive drive)
    {
        var f = drive.Features;
        var p = drive.Profiles;

        switch (featureCode)
        {
            // Feature 0x001E: FeatureCdRead
            // Per FEATURE_DATA_CD_READ (ntddmmc.h):
            //   byte 4 bit 0: CDText
            //   byte 4 bit 1: C2ErrorData
            //   byte 4 bit 7: DigitalAudioPlay
            case 0x001E when additionalLength >= 1:
                f.CdText = (data[featureStart + 4] & 0x01) != 0;
                f.C2ErrorPointers = (data[featureStart + 4] & 0x02) != 0;
                // FeatureCdRead presence implies drive can read CDs
                p.ReadCdR = true;
                p.ReadCdRw = true;
                break;

            // Feature 0x001F: FeatureDvdRead
            // Per FEATURE_DATA_DVD_READ (ntddmmc.h):
            //   byte 4 bit 0: Multi110
            //   byte 6 bit 0: DualDashR (can read DVD-R DL)
            case 0x001F when additionalLength >= 4:
                p.ReadDvdRom = true;
                p.ReadDvdR = true;
                p.ReadDvdRw = true;
                if ((data[featureStart + 4 + 2] & 0x01) != 0) // DualDashR at byte 6 bit 0
                    p.ReadDvdRDl = true;
                break;
            case 0x001F:
                p.ReadDvdRom = true;
                p.ReadDvdR = true;
                p.ReadDvdRw = true;
                break;

            // Feature 0x002A: FeatureDvdPlusRW
            // Per FEATURE_DATA_DVD_PLUS_RW (ntddmmc.h):
            //   Presence → can READ DVD+RW
            //   byte 4 bit 0: Write (can WRITE DVD+RW)
            case 0x002A when additionalLength >= 1:
                p.ReadDvdPlusRw = true;
                if ((data[featureStart + 4] & 0x01) != 0)
                    p.WriteDvdPlusRw = true;
                break;
            case 0x002A:
                p.ReadDvdPlusRw = true;
                break;

            // Feature 0x002B: FeatureDvdPlusR
            // Per FEATURE_DATA_DVD_PLUS_R (ntddmmc.h):
            //   Presence → can READ DVD+R
            //   byte 4 bit 0: Write (can WRITE DVD+R)
            case 0x002B when additionalLength >= 1:
                p.ReadDvdPlusR = true;
                if ((data[featureStart + 4] & 0x01) != 0)
                    p.WriteDvdPlusR = true;
                break;
            case 0x002B:
                p.ReadDvdPlusR = true;
                break;

            // Feature 0x002D: FeatureCdTrackAtOnce
            // Per FEATURE_DATA_CD_TRACK_AT_ONCE (ntddmmc.h):
            //   Presence → can WRITE CD-R (TAO mode)
            //   byte 4 bit 1: CdRewritable (can WRITE CD-RW in TAO)
            case 0x002D when additionalLength >= 1:
                p.WriteCdR = true;
                if ((data[featureStart + 4] & 0x02) != 0) // CdRewritable bit
                    p.WriteCdRw = true;
                break;
            case 0x002D:
                p.WriteCdR = true;
                break;

            // Feature 0x002E: FeatureCdMastering (SAO/DAO)
            // Per FEATURE_DATA_CD_MASTERING (ntddmmc.h):
            //   Presence → can WRITE CD-R (SAO/DAO mode)
            //   byte 4 bit 1: CdRewritable (can WRITE CD-RW in SAO)
            case 0x002E when additionalLength >= 1:
                p.WriteCdR = true;
                if ((data[featureStart + 4] & 0x02) != 0) // CdRewritable bit
                    p.WriteCdRw = true;
                break;
            case 0x002E:
                p.WriteCdR = true;
                break;

            // Feature 0x002F: FeatureDvdRecordableWrite
            // Per FEATURE_DATA_DVD_RECORDABLE_WRITE (ntddmmc.h):
            //   Presence → can WRITE DVD-R
            //   byte 4 bit 1: DVD_RW (can write DVD-RW)
            //   byte 4 bit 2: TestWrite
            //   byte 4 bit 3: RDualLayer (can write DVD-R DL)
            case 0x002F when additionalLength >= 1:
                p.WriteDvdR = true;
                if ((data[featureStart + 4] & 0x02) != 0) // DVD_RW bit
                    p.WriteDvdRw = true;
                if ((data[featureStart + 4] & 0x08) != 0) // RDualLayer bit
                    p.WriteDvdRDl = true;
                break;
            case 0x002F:
                p.WriteDvdR = true;
                break;

            // Feature 0x0033: FeatureLayerJumpRecording
            // Per FEATURE_DATA_LAYER_JUMP_RECORDING (ntddmmc.h):
            //   byte 4-6: Reserved
            //   byte 7: NumberOfLinkSizes
            //   Presence of this feature means the drive supports Layer Jump Recording.
            case 0x0033:
                f.LayerJumpRecording = true;
                break;

            // Feature 0x0037: FeatureCDRWMediaWriteSupport
            // Per FEATURE_CD_RW_MEDIA_WRITE_SUPPORT (ntddmmc.h):
            //   Presence → can WRITE CD-RW media
            case 0x0037:
                p.WriteCdRw = true;
                break;

            // Feature 0x003A: FeatureDvdPlusRWDualLayer
            // Per FEATURE_DATA_DVD_PLUS_RW_DUAL_LAYER (ntddmmc.h):
            //   Presence → can READ DVD+RW DL
            //   byte 4 bit 0: Write (can WRITE DVD+RW DL)
            case 0x003A when additionalLength >= 1:
                p.ReadDvdPlusRwDl = true;
                if ((data[featureStart + 4] & 0x01) != 0)
                    p.WriteDvdPlusRwDl = true;
                break;
            case 0x003A:
                p.ReadDvdPlusRwDl = true;
                break;

            // Feature 0x003B: FeatureDvdPlusRDualLayer
            // Per FEATURE_DATA_DVD_PLUS_R_DUAL_LAYER (ntddmmc.h):
            //   Presence → can READ DVD+R DL
            //   byte 4 bit 0: Write (can WRITE DVD+R DL)
            case 0x003B when additionalLength >= 1:
                p.ReadDvdPlusRDl = true;
                if ((data[featureStart + 4] & 0x01) != 0)
                    p.WriteDvdPlusRDl = true;
                break;
            case 0x003B:
                p.ReadDvdPlusRDl = true;
                break;

            // Feature 0x0040: FeatureBDRead
            // Per FEATURE_BD_READ (ntddmmc.h):
            //   bytes 4-7: Reserved
            //   bytes 8-15: BD-RE Read Support (4 class bitmaps × 2 bytes each)
            //   bytes 16-23: BD-R Read Support (4 class bitmaps × 2 bytes each)
            //   bytes 24-31: BD-ROM Read Support (4 class bitmaps × 2 bytes each)
            case 0x0040 when additionalLength >= 28:
            {
                // Check if any BD-RE class bitmap has any version bit set (bytes 8-15)
                bool canReadBdRe = false;
                for (int b = 8; b < 16 && featureStart + 4 + b < data.Length; b++)
                    if (data[featureStart + 4 + b] != 0) { canReadBdRe = true; break; }
                if (canReadBdRe) p.ReadBdRe = true;

                // Check BD-R class bitmaps (bytes 16-23)
                bool canReadBdR = false;
                for (int b = 16; b < 24 && featureStart + 4 + b < data.Length; b++)
                    if (data[featureStart + 4 + b] != 0) { canReadBdR = true; break; }
                if (canReadBdR) p.ReadBdR = true;

                // Check BD-ROM class bitmaps (bytes 24-31)
                bool canReadBdRom = false;
                for (int b = 24; b < 32 && featureStart + 4 + b < data.Length; b++)
                    if (data[featureStart + 4 + b] != 0) { canReadBdRom = true; break; }
                if (canReadBdRom) p.ReadBdRom = true;
                break;
            }
            case 0x0040:
                // Feature present but insufficient data to parse bitmaps
                // Just set general BD read capability
                p.ReadBdRom = true;
                break;

            // Feature 0x0041: FeatureBDWrite
            // Per FEATURE_BD_WRITE (ntddmmc.h):
            //   byte 4 bit 0: SupportsVerifyNotRequired
            //   bytes 5-7: Reserved
            //   bytes 8-15: BD-RE Write Support (4 class bitmaps × 2 bytes each)
            //   bytes 16-23: BD-R Write Support (4 class bitmaps × 2 bytes each)
            case 0x0041 when additionalLength >= 20:
            {
                // Check if any BD-RE write class bitmap has any version bit set (bytes 8-15)
                bool canWriteBdRe = false;
                for (int b = 8; b < 16 && featureStart + 4 + b < data.Length; b++)
                    if (data[featureStart + 4 + b] != 0) { canWriteBdRe = true; break; }
                if (canWriteBdRe) { p.WriteBdRe = true; p.ReadBdRe = true; }

                // Check BD-R write class bitmaps (bytes 16-23)
                bool canWriteBdR = false;
                for (int b = 16; b < 24 && featureStart + 4 + b < data.Length; b++)
                    if (data[featureStart + 4 + b] != 0) { canWriteBdR = true; break; }
                if (canWriteBdR) { p.WriteBdR = true; p.ReadBdR = true; }
                break;
            }
            case 0x0041:
                // Feature present but insufficient data — set general BD write
                p.WriteBdR = true; p.ReadBdR = true;
                p.WriteBdRe = true; p.ReadBdRe = true;
                break;

            // Feature 0x0050: FeatureHDDVDRead
            // Per FEATURE_DATA_HDDVD_READ (ntddmmc.h):
            //   byte 4 bit 0: Recordable (can read HD DVD-R)
            //   byte 5: Reserved
            //   byte 6 bit 0: Rewritable (can read HD DVD-RW)
            //   byte 7: Reserved
            case 0x0050 when additionalLength >= 4:
                p.ReadHdDvdRom = true;
                if ((data[featureStart + 4] & 0x01) != 0) // Recordable
                    p.ReadHdDvdR = true;
                if ((data[featureStart + 4 + 2] & 0x01) != 0) // Rewritable
                    p.ReadHdDvdRw = true;
                break;
            case 0x0050:
                p.ReadHdDvdRom = true;
                break;

            // Feature 0x0051: FeatureHDDVDWrite
            // Per FEATURE_DATA_HDDVD_WRITE (ntddmmc.h):
            //   byte 4 bit 0: Recordable (can write HD DVD-R)
            //   byte 5: Reserved
            //   byte 6 bit 0: Rewritable (can write HD DVD-RW)
            //   byte 7: Reserved
            case 0x0051 when additionalLength >= 4:
                if ((data[featureStart + 4] & 0x01) != 0) // Recordable
                {
                    p.WriteHdDvdR = true;
                    p.ReadHdDvdR = true;
                }
                if ((data[featureStart + 4 + 2] & 0x01) != 0) // Rewritable
                {
                    p.WriteHdDvdRw = true;
                    p.ReadHdDvdRw = true;
                }
                break;
            case 0x0051:
                // Feature present but insufficient data — set general HD DVD write
                p.WriteHdDvdR = true; p.ReadHdDvdR = true;
                break;

            // Feature 0x0106: FeatureDvdCSS (Content Scramble System)
            // Per FEATURE_DATA_DVD_CSS (ntddmmc.h):
            //   byte 4-6: Reserved
            //   byte 7: CssVersion
            case 0x0106:
                f.DvdCss = true;
                break;

            // Feature 0x010D: FeatureAACS (Advanced Access Content System)
            // Per FEATURE_DATA_AACS (ntddmmc.h):
            //   byte 4 bit 0: BindingNonceGeneration
            //   byte 4 bits 1-7: Reserved
            //   byte 5: BindingNonceBlockCount
            //   byte 6 bits 0-3: NumberOfAGIDs
            //   byte 6 bits 4-7: Reserved
            //   byte 7: AACSVersion
            // Bus Encryption is implied by the presence of the AACS feature itself —
            // there is no separate "BEC" bit in the standard FEATURE_DATA_AACS struct.
            case 0x010D when additionalLength >= 4:
                f.BindingNonceGeneration = (data[featureStart + 4] & 0x01) != 0;
                // Bus encryption capability is implied when AACS feature is present
                f.BusEncryption = true;
                break;
        }
    }

    /// <summary>
    /// Probes hardware information (buffer size, vendor-specific info) via SCSI commands.
    /// </summary>
    private static void ProbeHardwareInfo(OpticalDrive optDrive, DiscDrive drive)
    {
        try
        {
            // INQUIRY for vendor, product, revision (duplicates PopulateFirmwareInfo
            // but stores in DiscDrive for Drive Capabilities display)
            var (vendor, product, revision) = optDrive.Inquiry();
            if (!string.IsNullOrWhiteSpace(vendor))
                drive.VendorId = vendor;
            if (!string.IsNullOrWhiteSpace(revision))
                drive.FirmwareRevision = revision;

            var serial = optDrive.InquirySerialNumber();
            if (!string.IsNullOrWhiteSpace(serial))
                drive.SerialNumber = serial;

            // MODE SENSE (10) page 0x2A: CD/DVD Capabilities and Mechanical Status Page
            // This page contains buffer size at offsets 12-13 (2 bytes, big-endian, in KiB).
            try
            {
                var modeSenseCmd = new ScsiCommand(
                    MmcCommands.BuildModeSense10(0x2A, 256),
                    ScsiDataDirection.In, 256)
                {
                    TimeoutMs = 5_000
                };
                var msResult = optDrive.ExecuteRaw(modeSenseCmd);
                if (msResult.Success && msResult.DataTransferred >= 8)
                {
                    var msData = modeSenseCmd.DataBuffer;
                    // Mode parameter header (10-byte): 8 bytes
                    // Mode page follows after header + block descriptor length
                    int modeDataLength = MmcCommands.ReadBE16(msData, 0) + 2;
                    int bdLength = MmcCommands.ReadBE16(msData, 6);
                    int pageOffset = 8 + bdLength;

                    if (pageOffset + 14 <= msResult.DataTransferred &&
                        (msData[pageOffset] & 0x3F) == 0x2A) // Page code 0x2A
                    {
                        int pageLen = msData[pageOffset + 1];
                        // Buffer size is at page bytes 12-13 (big-endian, in KiB).
                        // Page byte 0 = page code, byte 1 = page length (bytes following byte 1).
                        // So pageLen >= 12 ensures bytes 12-13 exist (byte 1 + 12 = 13).
                        if (pageLen >= 12 && pageOffset + 14 <= msResult.DataTransferred)
                        {
                            drive.BufferSizeKiB = MmcCommands.ReadBE16(msData, pageOffset + 12);
                        }

                        // Parse capability bits from bytes 2-3 of page 2Ah.
                        // These supplement the profile/feature-based detection.
                        // Per MMC-3/Mt.Fuji spec, Mode Page 2Ah byte 2 (read capabilities):
                        //   Bit 5: DVD-RAM Read, Bit 4: DVD-R Read, Bit 3: DVD-ROM Read
                        //   Bit 2: Method 2, Bit 1: CD-RW Read, Bit 0: CD-R Read
                        // Byte 3 (write capabilities):
                        //   Bit 5: DVD-RAM Write, Bit 4: DVD-R Write
                        //   Bit 3: Test Write, Bit 1: CD-RW Write, Bit 0: CD-R Write
                        if (pageLen >= 2 && pageOffset + 4 <= msResult.DataTransferred)
                        {
                            var readBits = msData[pageOffset + 2];
                            var writeBits = msData[pageOffset + 3];
                            var p = drive.Profiles;

                            // Supplementary read capabilities (only set, never clear)
                            if ((readBits & 0x01) != 0) p.ReadCdR = true;
                            if ((readBits & 0x02) != 0) p.ReadCdRw = true;
                            if ((readBits & 0x08) != 0) p.ReadDvdRom = true;
                            if ((readBits & 0x10) != 0) p.ReadDvdR = true;
                            if ((readBits & 0x20) != 0) p.ReadDvdRam = true;

                            // Supplementary write capabilities (only set, never clear)
                            if ((writeBits & 0x01) != 0) p.WriteCdR = true;
                            if ((writeBits & 0x02) != 0) p.WriteCdRw = true;
                            if ((writeBits & 0x10) != 0) p.WriteDvdR = true;
                            if ((writeBits & 0x20) != 0) p.WriteDvdRam = true;
                        }

                        // Parse C2 Pointers Supported from byte 5 bit 3
                        // This supplements Feature 0x001E C2ErrorData detection
                        if (pageLen >= 4 && pageOffset + 6 <= msResult.DataTransferred)
                        {
                            if ((msData[pageOffset + 5] & 0x08) != 0)
                                drive.Features.C2ErrorPointers = true;
                        }

                        // LightScribe/Labelflash are vendor-specific and cannot be reliably
                        // detected from standard SCSI/MMC commands. They require vendor-specific
                        // INQUIRY VPD pages or SCSI commands:
                        // - LightScribe: Vendor-specific INQUIRY page (HP/LiteOn)
                        // - Labelflash: NEC/Optiarc vendor-specific command
                        // We detect them via INQUIRY vendor string heuristics instead.
                    }
                }
            }
            catch { /* MODE SENSE is optional */ }

            // Detect LightScribe/Labelflash from vendor-specific identification.
            // These are proprietary technologies detectable via vendor strings or
            // vendor-specific SCSI commands.
            try
            {
                // INQUIRY VPD page 0x00 to check supported pages, then check
                // vendor-specific pages. This is a best-effort heuristic.
                var productStr = (drive.DriveModel ?? "").ToUpperInvariant();
                // LightScribe drives typically have "LS" in their model or are HP/LiteOn drives
                // Labelflash drives are NEC/Optiarc with "LF" designation
                if (productStr.Contains("LIGHTSCRIBE") || productStr.Contains(" LS"))
                    drive.Features.LightScribe = true;
                if (productStr.Contains("LABELFLASH") || productStr.Contains(" LF"))
                    drive.Features.Labelflash = true;
            }
            catch { /* vendor detection is best-effort */ }
        }
        catch { /* hardware info probing is best-effort */ }
    }

    private static string DetermineWindowsDriveType(DiscDrive d)
    {
        if (d.CanWriteBluRay)  return "Blu-ray Writer";
        if (d.CanReadBluRay)   return "Blu-ray Reader";
        if (d.CanWriteHDDVD)   return "HD DVD Writer";
        if (d.CanReadHDDVD)    return "HD DVD Reader";
        if (d.CanWriteDVD)     return "DVD Writer";
        if (d.CanReadDVD)      return "DVD-ROM";
        if (d.CanWrite)        return "CD-RW";
        return "CD-ROM";
    }

    // -----------------------------------------------------------------------
    // Windows-native drive model fallback (IOCTL_STORAGE_QUERY_PROPERTY)
    // -----------------------------------------------------------------------
    // Used when SCSI passthrough fails (e.g. USB bridges that don't support SPTI).
    // IOCTL_STORAGE_QUERY_PROPERTY works through the standard storage stack and
    // returns vendor/product info from the device descriptor.

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true, EntryPoint = "CreateFile")]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle StorageCreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "DeviceIoControl")]
    private static extern bool StorageDeviceIoControl(
        Microsoft.Win32.SafeHandles.SafeFileHandle hDevice, uint dwIoControlCode,
        byte[] lpInBuffer, uint nInBufferSize,
        byte[] lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    // IOCTL_STORAGE_QUERY_PROPERTY = CTL_CODE(IOCTL_STORAGE_BASE=0x002D, 0x0500, METHOD_BUFFERED, FILE_ANY_ACCESS)
    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;

    /// <summary>
    /// Gets the drive model using Windows IOCTL_STORAGE_QUERY_PROPERTY.
    /// Works for USB optical drives that may not support SCSI passthrough.
    /// </summary>
    private static string? GetWindowsStorageModel(string drivePath)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return null;

        try
        {
            var winPath = drivePath;
            if (drivePath.Length >= 2 && drivePath[1] == ':')
                winPath = $@"\\.\{drivePath[0]}:";

            using var handle = StorageCreateFile(
                winPath, 0, // No access required for STORAGE_QUERY_PROPERTY
                1 | 2, // FILE_SHARE_READ | FILE_SHARE_WRITE
                IntPtr.Zero, 3, // OPEN_EXISTING
                0x80, // FILE_ATTRIBUTE_NORMAL
                IntPtr.Zero);

            if (handle.IsInvalid)
                return null;

            // STORAGE_PROPERTY_QUERY: PropertyId=0 (StorageDeviceProperty),
            // QueryType=0 (PropertyStandardQuery)
            var query = new byte[12];
            query[0] = 0; // StorageDeviceProperty
            query[4] = 0; // PropertyStandardQuery

            var output = new byte[1024];
            if (!StorageDeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY,
                    query, (uint)query.Length,
                    output, (uint)output.Length,
                    out var bytesReturned, IntPtr.Zero))
                return null;

            if (bytesReturned < 40)
                return null;

            // STORAGE_DEVICE_DESCRIPTOR offsets:
            //   +4: VendorIdOffset (ULONG)
            //   +8: ProductIdOffset (ULONG)
            var vendorOffset = BitConverter.ToInt32(output, 4);
            var productOffset = BitConverter.ToInt32(output, 8);

            string vendor = string.Empty, product = string.Empty;
            if (vendorOffset > 0 && vendorOffset < bytesReturned)
            {
                int end = Array.IndexOf(output, (byte)0, vendorOffset);
                if (end < 0) end = (int)bytesReturned;
                vendor = Encoding.ASCII.GetString(output, vendorOffset, end - vendorOffset).Trim();
            }
            if (productOffset > 0 && productOffset < bytesReturned)
            {
                int end = Array.IndexOf(output, (byte)0, productOffset);
                if (end < 0) end = (int)bytesReturned;
                product = Encoding.ASCII.GetString(output, productOffset, end - productOffset).Trim();
            }

            var model = $"{vendor} {product}".Trim();
            return string.IsNullOrWhiteSpace(model) ? null : model;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[NativeDeviceDiscovery] IOCTL_STORAGE_QUERY_PROPERTY failed for {drivePath}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Queries the storage bus type (USB, SATA, ATAPI, etc.) for a drive letter.
    /// Uses IOCTL_STORAGE_QUERY_PROPERTY with StorageDeviceDescriptor.
    /// Bus type is at offset 24 (STORAGE_BUS_TYPE enum).
    /// Returns empty string on failure.
    /// </summary>
    public static string QueryBusType(string drivePath)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || string.IsNullOrWhiteSpace(drivePath))
            return string.Empty;

        try
        {
            var winPath = drivePath;
            if (drivePath.Length >= 2 && drivePath[1] == ':')
                winPath = $@"\\.\{drivePath[0]}:";

            using var handle = StorageCreateFile(
                winPath, 0,
                1 | 2,
                IntPtr.Zero, 3,
                0x80,
                IntPtr.Zero);

            if (handle.IsInvalid)
                return string.Empty;

            var query = new byte[12];
            query[0] = 0; // StorageDeviceProperty
            query[4] = 0; // PropertyStandardQuery

            var output = new byte[1024];
            if (!StorageDeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY,
                    query, (uint)query.Length,
                    output, (uint)output.Length,
                    out var bytesReturned, IntPtr.Zero))
                return string.Empty;

            if (bytesReturned < 28)
                return string.Empty;

            return (output[24]) switch
            {
                5 => "USB",
                9 => "SATA",
                2 => "ATAPI",
                1 => "SCSI",
                7 => "iSCSI",
                8 => "SAS",
                4 => "IEEE 1394",
                10 => "NVMe",
                _ => string.Empty
            };
        }
        catch
        {
            return string.Empty;
        }
    }
}
