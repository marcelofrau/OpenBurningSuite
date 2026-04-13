// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.IO;
using OpenBurningSuite.Models;
using OpenBurningSuite.Native.Optical;
using OpenBurningSuite.Native.Scsi;

namespace OpenBurningSuite.Native.Platform;

public static partial class NativeDeviceDiscovery
{
    private static List<DiscDrive> DiscoverMacOS()
    {
        var drives = new List<DiscDrive>();

        // Primary method: Enumerate optical drives via IOKit service matching.
        // This directly queries IOSCSIPeripheralDeviceType05 services in the
        // IORegistry, which is the Apple-recommended way to discover SCSI devices.
        //
        // The previous approach of scanning /dev/disk0..disk63 had critical issues:
        //   1. Optical drives without media have no BSD name (no /dev/diskN),
        //      so internal drives with an empty tray were never found.
        //   2. Opening every /dev/diskN as a SCSI device triggered expensive
        //      Disk Arbitration unmount/claim operations on system disks (disk0,
        //      disk1, etc.), causing delays and potential system instability.
        //   3. On Apple Silicon Macs with many synthesized APFS volumes, the
        //      scan wasted 2.5+ seconds per non-optical device on DA operations.
        //
        // IOKit enumeration is instant, targets only optical drives, and works
        // regardless of media state. The kernel always maintains IOKit service
        // entries for connected optical drives (internal SATA/ATAPI and USB).
        try
        {
            var ioKitDrives = MacOsScsiTransport.EnumerateOpticalDriveServices();

            foreach (var (bsdName, vendor, product, ioRegPath) in ioKitDrives)
            {
                var isVendorKnown = !string.IsNullOrWhiteSpace(vendor) && vendor != "Unknown";
                var isProductKnown = !string.IsNullOrWhiteSpace(product) && product != "Unknown";
                string model;
                if (isVendorKnown && isProductKnown)
                    model = $"{vendor} {product}".Trim();
                else if (isVendorKnown)
                    model = vendor.Trim();
                else if (isProductKnown)
                    model = product.Trim();
                else
                    model = "Unknown Optical Drive";

                // If the drive has a BSD name, it has media and we can probe via SCSI.
                // Without a BSD name, the drive exists but has no media — show it
                // with basic info from IOKit properties so the user knows it's there.
                // Use IORegistry path as fallback device identifier when BSD name is unavailable.
                string devPath = !string.IsNullOrEmpty(bsdName)
                    ? $"/dev/{bsdName}"
                    : !string.IsNullOrEmpty(ioRegPath) ? ioRegPath : string.Empty;

                var drive = new DiscDrive
                {
                    DevicePath = devPath,
                    DriveModel = model,
                    DriveType = "CD/DVD",
                    CanRead = true,
                    CanWrite = false,
                    CanReadDVD = false,
                    CanWriteDVD = false,
                    Status = string.IsNullOrEmpty(bsdName) ? "No Media" : "Ready"
                };

                // Full SCSI probe for drives with media (have a BSD name)
                // The SCSI transport requires a BSD device path (/dev/diskN), not
                // an IORegistry path, so we check bsdName rather than devPath.
                if (!string.IsNullOrEmpty(bsdName))
                {
                    var bsdPath = $"/dev/{bsdName}";
                    try
                    {
                        using var optDrive = new OpticalDrive(bsdPath);

                        // Probe drive capabilities via GET CONFIGURATION feature list
                        ProbeCapabilities(optDrive, drive);

                        // Probe write speeds
                        var macSpeeds = optDrive.GetSupportedWriteSpeeds();
                        foreach (var s in macSpeeds)
                            drive.SupportedWriteSpeeds.Add($"{s} KB/s");

                        // Probe read speeds
                        var macReadSpeeds = optDrive.GetSupportedReadSpeeds();
                        foreach (var s in macReadSpeeds)
                            drive.SupportedReadSpeeds.Add($"{s} KB/s");

                        // Media probing via GET CONFIGURATION / READ DISC INFO / READ TRACK INFO
                        try
                        {
                            var profile = optDrive.GetCurrentProfile();
                            if (profile != 0)
                            {
                                drive.CurrentMedia = new DiscMedia
                                {
                                    MediaType = OpticalDrive.ProfileToMediaType(profile)
                                };

                                // Check for M-DISC media
                                if (optDrive.IsMDiscMedia())
                                {
                                    var mDiscType = optDrive.GetMDiscMediaType();
                                    if (mDiscType != null)
                                        drive.CurrentMedia.MediaType = mDiscType;
                                }

                                var discInfo = optDrive.ReadDiscInfo();

                                // Get media capacity via READ CAPACITY
                                var macCapacity = optDrive.ReadCapacity();
                                if (macCapacity.HasValue && drive.CurrentMedia != null)
                                {
                                    drive.CurrentMedia.CapacityBytes =
                                        ((long)macCapacity.Value.LastLba + 1) * macCapacity.Value.BlockLength;
                                }

                                if (discInfo != null && drive.CurrentMedia != null)
                                {
                                    drive.CurrentMedia.DiscState = discInfo.DiscStatusString;
                                    drive.CurrentMedia.Sessions = discInfo.NumberOfSessionsLsb;
                                    drive.CurrentMedia.Tracks = discInfo.LastTrackInLastSessionLsb;

                                    // Calculate used bytes from track info
                                    var lastTrack = discInfo.LastTrackInLastSessionLsb;
                                    if (lastTrack > 0)
                                    {
                                        var trackInfo = optDrive.ReadTrackInfo((uint)lastTrack);
                                        if (trackInfo != null)
                                        {
                                            int blockLen = (macCapacity.HasValue && macCapacity.Value.BlockLength > 0)
                                                ? (int)macCapacity.Value.BlockLength : 2048;
                                            var usedSectors = trackInfo.TrackStartAddress + trackInfo.TrackSize;
                                            drive.CurrentMedia.UsedBytes = (long)usedSectors * blockLen;

                                            if (drive.CurrentMedia.CapacityBytes <= 0 && trackInfo.FreeBlocks > 0)
                                            {
                                                drive.CurrentMedia.CapacityBytes = (long)trackInfo.FreeBlocks * blockLen;
                                            }
                                        }
                                    }
                                }

                                // Read volume label via SCSI READ(10) for sector 16 (ISO PVD)
                                if (drive.CurrentMedia != null)
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

                                        // Try UDF PVD at sector 256
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
                                    catch { /* volume label probe failed — not critical */ }
                                }
                            }
                            else
                            {
                                drive.Status = "No Media";
                            }
                        }
                        catch
                        {
                            drive.Status = "No Media";
                        }
                    }
                    catch
                    {
                        // SCSI probe failed — set status to No Media.
                        // On macOS, the IOKit SCSI passthrough should handle all
                        // media detection via native MMC commands.
                        System.Diagnostics.Debug.WriteLine(
                            $"[NativeDeviceDiscovery] macOS SCSI probe failed for {bsdPath}");
                        drive.Status = "No Media";
                    }
                }
                else
                {
                    // No BSD name — drive has no media visible to IOKit.
                    // The IORegistry path still allows SCSI passthrough via IOKit's
                    // MMC user client on the IOSCSIPeripheralDeviceType05 service.
                    // INQUIRY and GET CONFIGURATION work without media, so we can
                    // probe drive capabilities (CD/DVD/BD read/write support).
                    if (!string.IsNullOrEmpty(ioRegPath))
                    {
                        try
                        {
                            using var optDrive = new OpticalDrive(ioRegPath);

                            // INQUIRY works without media — get drive model if not
                            // already populated from IOKit registry properties.
                            if (string.IsNullOrEmpty(drive.DriveModel))
                            {
                                try
                                {
                                    var (inqVendor, inqProduct, _) = optDrive.Inquiry();
                                    if (inqVendor != "Unknown" || inqProduct != "Unknown")
                                        drive.DriveModel = $"{inqVendor} {inqProduct}".Trim();
                                }
                                catch { /* best effort */ }
                            }

                            // Probe drive capabilities via GET CONFIGURATION feature list.
                            // These are drive capabilities, not media-dependent, so they
                            // work correctly without a disc inserted.
                            ProbeCapabilities(optDrive, drive);

                            // Probe write speeds (may be empty without media but some
                            // drives report max speeds regardless)
                            var macSpeeds = optDrive.GetSupportedWriteSpeeds();
                            foreach (var s in macSpeeds)
                                drive.SupportedWriteSpeeds.Add($"{s} KB/s");

                            var macReadSpeeds = optDrive.GetSupportedReadSpeeds();
                            foreach (var s in macReadSpeeds)
                                drive.SupportedReadSpeeds.Add($"{s} KB/s");

                            drive.Status = "No Media";
                        }
                        catch (Exception ex)
                        {
                            // SCSI probe via IORegistry path failed — this can happen
                            // when the drive's IOKit user client rejects exclusive access
                            // or on newer macOS versions with tighter security.
                            System.Diagnostics.Debug.WriteLine(
                                $"[NativeDeviceDiscovery] macOS SCSI probe via IORegistry path failed: {ex.Message}");
                            drive.Status = "No Media";
                        }
                    }
                    else
                    {
                        drive.Status = "No Media";
                    }
                }

                drive.DriveType = DetermineLinuxDriveType(drive); // reuse same logic
                drives.Add(drive);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[NativeDeviceDiscovery] macOS IOKit enumeration failed: {ex.Message}");
        }

        // Fallback: if IOKit enumeration found no drives (e.g., on unusual system
        // configurations), scan /dev/diskN BSD names as a last resort.
        // IMPORTANT: Before opening each disk (which involves expensive Disk Arbitration
        // unmount/claim operations taking 2.5+ seconds per device), use the fast
        // IOKit-based IsOpticalDriveBsdName check to skip non-optical devices.
        // Without this pre-check, the fallback scan on an Apple Silicon Mac with 10+
        // synthesized APFS volumes would take 25+ seconds on DA operations alone.
        if (drives.Count == 0)
        {
            try
            {
                int consecutiveMisses = 0;
                for (int i = 0; i <= 63; i++)
                {
                    var devPath = $"/dev/disk{i}";
                    if (!File.Exists(devPath))
                    {
                        consecutiveMisses++;
                        if (consecutiveMisses > 10) break;
                        continue;
                    }
                    consecutiveMisses = 0;

                    // Fast IOKit pre-check: skip non-optical devices without
                    // triggering any DA operations or SCSI commands. This reduces
                    // the fallback scan from minutes to under a second.
                    var diskName = $"disk{i}";
                    if (!MacOsScsiTransport.IsOpticalDriveBsdName(diskName))
                        continue;

                    try
                    {
                        using var optDrive = new OpticalDrive(devPath);

                        var (vendor, product, revision) = optDrive.Inquiry();
                        if (vendor == "Unknown") continue;

                        var drive = new DiscDrive
                        {
                            DevicePath = devPath,
                            DriveModel = $"{vendor} {product}".Trim(),
                            DriveType = "CD/DVD",
                            CanRead = true,
                            CanWrite = false,
                            CanReadDVD = false,
                            CanWriteDVD = false,
                            Status = "Ready"
                        };

                        ProbeCapabilities(optDrive, drive);
                        drive.DriveType = DetermineLinuxDriveType(drive);

                        var speeds = optDrive.GetSupportedWriteSpeeds();
                        foreach (var s in speeds)
                            drive.SupportedWriteSpeeds.Add($"{s} KB/s");

                        var readSpeeds = optDrive.GetSupportedReadSpeeds();
                        foreach (var s in readSpeeds)
                            drive.SupportedReadSpeeds.Add($"{s} KB/s");

                        drives.Add(drive);
                    }
                    catch { /* Not an optical drive or access failed — skip */ }
                }
            }
            catch { /* fallback scan failed — return empty list */ }
        }

        return drives;
    }
}
