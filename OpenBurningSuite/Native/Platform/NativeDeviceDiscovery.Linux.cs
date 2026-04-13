// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using OpenBurningSuite.Models;
using OpenBurningSuite.Native.Optical;
using OpenBurningSuite.Native.Scsi;

namespace OpenBurningSuite.Native.Platform;

public static partial class NativeDeviceDiscovery
{
    private static List<DiscDrive> DiscoverLinux()
    {
        var drives = new List<DiscDrive>();

        // Method 1: Parse /proc/sys/dev/cdrom/info
        try
        {
            var infoPath = "/proc/sys/dev/cdrom/info";
            if (File.Exists(infoPath))
            {
                var lines = File.ReadAllLines(infoPath);
                string[]? deviceNames = null;
                bool[]? canWrite = null, canWriteDvd = null, canReadDvd = null;
                bool[]? canReadBd = null, canWriteBd = null;

                foreach (var line in lines)
                {
                    if (line.StartsWith("drive name:"))
                    {
                        var parts = line.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        deviceNames = parts.Length > 2 ? parts[2..] : Array.Empty<string>();
                    }
                    else if (line.StartsWith("Can write CD-R:"))
                        canWrite = ParseBoolArray(line);
                    else if (line.StartsWith("Can read DVD:"))
                        canReadDvd = ParseBoolArray(line);
                    else if (line.StartsWith("Can write DVD-R:"))
                        canWriteDvd = ParseBoolArray(line);
                    else if (line.StartsWith("Can read Blu-ray:") || line.StartsWith("Can read BD:"))
                        canReadBd = ParseBoolArray(line);
                    else if (line.StartsWith("Can write Blu-ray:") || line.StartsWith("Can write BD:"))
                        canWriteBd = ParseBoolArray(line);
                }

                if (deviceNames != null)
                {
                    for (int i = 0; i < deviceNames.Length; i++)
                    {
                        var devName = deviceNames[i].Trim();
                        if (string.IsNullOrWhiteSpace(devName)) continue;

                        var devPath = $"/dev/{devName}";
                        var drive = new DiscDrive
                        {
                            DevicePath = devPath,
                            DriveLetter = string.Empty,
                            CanRead = true,
                            CanWrite = canWrite != null && i < canWrite.Length && canWrite[i],
                            CanReadDVD = canReadDvd != null && i < canReadDvd.Length && canReadDvd[i],
                            CanWriteDVD = canWriteDvd != null && i < canWriteDvd.Length && canWriteDvd[i],
                            CanReadBluRay = canReadBd != null && i < canReadBd.Length && canReadBd[i],
                            CanWriteBluRay = canWriteBd != null && i < canWriteBd.Length && canWriteBd[i],
                        };
                        drive.DriveModel = GetLinuxDriveModel(devPath);
                        drive.DriveType = DetermineLinuxDriveType(drive);
                        drive.Status = GetLinuxDriveStatus(devPath);

                        // Probe write speeds, additional capabilities, and media via SCSI.
                        // Use a single OpticalDrive connection for both capabilities and media
                        // probing to avoid redundant device opens.
                        // /proc/sys/dev/cdrom/info doesn't expose HD DVD capabilities,
                        // so we use GET CONFIGURATION to detect the full feature set.
                        try
                        {
                            using var optDrive = new OpticalDrive(devPath);
                            ProbeCapabilities(optDrive, drive);

                            var speeds = optDrive.GetSupportedWriteSpeeds();
                            foreach (var s in speeds)
                                drive.SupportedWriteSpeeds.Add($"{s} KB/s");

                            var readSpeeds = optDrive.GetSupportedReadSpeeds();
                            foreach (var s in readSpeeds)
                                drive.SupportedReadSpeeds.Add($"{s} KB/s");

                            // Probe media using the same connection
                            drive.CurrentMedia = ProbeLinuxMedia(devPath, optDrive);
                        }
                        catch
                        {
                            // SCSI probe failed — try media probing separately
                            drive.CurrentMedia = ProbeLinuxMedia(devPath);
                        }

                        // Re-evaluate drive type after SCSI probe (may have detected HD DVD)
                        drive.DriveType = DetermineLinuxDriveType(drive);
                        drives.Add(drive);
                    }
                }
            }
        }
        catch { /* fall through */ }

        // Method 2: Scan /dev/sr0..sr15 and common symlinks as fallback
        if (drives.Count == 0)
        {
            // Check /dev/sr0..sr15 (supports systems with more than 10 optical drives)
            // and common symlinks like /dev/cdrom and /dev/dvd
            var devicePaths = new List<string>();
            for (int i = 0; i <= 15; i++)
                devicePaths.Add($"/dev/sr{i}");
            devicePaths.Add("/dev/cdrom");
            devicePaths.Add("/dev/dvd");

            var seenPaths = new HashSet<string>();
            foreach (var path in devicePaths)
            {
                // Block/character device files may not be detected by File.Exists on all systems.
                // Use a try-open approach: if we can stat/open it, it exists.
                bool deviceExists;
                try
                {
                    // FileInfo.Exists works for device files on most Linux systems
                    deviceExists = new FileInfo(path).Exists;
                }
                catch
                {
                    deviceExists = false;
                }
                if (!deviceExists) continue;

                // Resolve symlinks to avoid adding the same physical device twice
                // (e.g., /dev/cdrom → /dev/sr0)
                string resolvedPath = path;
                try
                {
                    var fi = new FileInfo(path);
                    if (fi.LinkTarget != null)
                    {
                        // LinkTarget may return a relative path (e.g., "sr0" instead of "/dev/sr0").
                        // Resolve it relative to the directory containing the symlink.
                        resolvedPath = Path.IsPathRooted(fi.LinkTarget)
                            ? fi.LinkTarget
                            : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path) ?? "/dev", fi.LinkTarget));
                    }
                }
                catch { /* use original path */ }
                if (!seenPaths.Add(resolvedPath)) continue;

                var drive = new DiscDrive
                {
                    DevicePath = path,
                    CanRead = true,
                    CanWrite = false,
                    CanReadDVD = false,
                    CanWriteDVD = false,
                    CanReadBluRay = false,
                    CanWriteBluRay = false,
                };
                drive.DriveModel = GetLinuxDriveModel(path);
                drive.Status = GetLinuxDriveStatus(path);

                // Try to probe actual capabilities via SCSI
                try
                {
                    using var optDrive = new OpticalDrive(path);
                    ProbeCapabilities(optDrive, drive);

                    // Probe write speeds
                    var speeds = optDrive.GetSupportedWriteSpeeds();
                    foreach (var s in speeds)
                        drive.SupportedWriteSpeeds.Add($"{s} KB/s");

                    var readSpeeds = optDrive.GetSupportedReadSpeeds();
                    foreach (var s in readSpeeds)
                        drive.SupportedReadSpeeds.Add($"{s} KB/s");
                }
                catch { /* keep default capabilities */ }

                drive.DriveType = DetermineLinuxDriveType(drive);
                drive.CurrentMedia = ProbeLinuxMedia(path);
                drives.Add(drive);
            }
        }

        return drives;
    }

    /// <summary>Gets drive model from /sys filesystem.</summary>
    private static string GetLinuxDriveModel(string dev)
    {
        try
        {
            var devName = Path.GetFileName(dev);
            var modelPath = $"/sys/block/{devName}/device/model";
            if (File.Exists(modelPath))
                return File.ReadAllText(modelPath).Trim();

            // Try vendor + model (vendor may exist without model on some systems)
            var vendorPath = $"/sys/block/{devName}/device/vendor";
            if (File.Exists(vendorPath))
            {
                var vendor = File.ReadAllText(vendorPath).Trim();
                return vendor;
            }
        }
        catch { }

        // Try SCSI INQUIRY as last resort
        try
        {
            using var drive = new OpticalDrive(dev);
            var (vendor, product, _) = drive.Inquiry();
            if (vendor != "Unknown")
                return $"{vendor} {product}".Trim();
        }
        catch { }

        return "Unknown Optical Drive";
    }

    /// <summary>Gets volume label and filesystem from /sys/block.</summary>
    private static DiscMedia? ProbeLinuxMedia(string dev)
    {
        return ProbeLinuxMedia(dev, null);
    }

    /// <summary>
    /// Probes Linux media using SCSI commands. Accepts an optional already-open OpticalDrive
    /// to avoid redundant device opens during discovery.
    /// </summary>
    private static DiscMedia? ProbeLinuxMedia(string dev, OpticalDrive? existingDrive)
    {
        var media = new DiscMedia();
        bool hasInfo = false;

        // Try SCSI commands for media type detection and used space calculation.
        try
        {
            // Use existing drive connection if available, otherwise open a new one
            OpticalDrive? ownedDrive = null;
            OpticalDrive drive;
            if (existingDrive != null)
            {
                drive = existingDrive;
            }
            else
            {
                ownedDrive = new OpticalDrive(dev);
                drive = ownedDrive;
            }
            try
            {
                // Use GET CONFIGURATION to detect media instead of TEST UNIT READY.
                // GET CONFIGURATION returns the current profile which indicates media type,
                // and works reliably even when TEST UNIT READY fails (e.g., UNIT ATTENTION
                // after handle open, or drives that don't properly respond to TUR).
                var profile = drive.GetCurrentProfile();
                if (profile != 0)
                {
                    media.MediaType = OpticalDrive.ProfileToMediaType(profile);
                    hasInfo = true;

                    // Check for M-DISC media (uses standard profiles but inorganic recording layer)
                    if (drive.IsMDiscMedia())
                    {
                        var mDiscType = drive.GetMDiscMediaType();
                        if (mDiscType != null)
                            media.MediaType = mDiscType;
                    }

                    // Get disc info
                    var discInfo = drive.ReadDiscInfo();
                    if (discInfo != null)
                    {
                        media.DiscState = discInfo.DiscStatusString;
                        media.Sessions = discInfo.NumberOfSessionsLsb;
                        media.Tracks = discInfo.LastTrackInLastSessionLsb;
                        hasInfo = true;

                        // Determine used bytes from track info
                        var lastTrack = discInfo.LastTrackInLastSessionLsb;
                        if (lastTrack > 0)
                        {
                            var trackInfo = drive.ReadTrackInfo((uint)lastTrack);
                            if (trackInfo != null)
                            {
                                var usedSectors = trackInfo.TrackStartAddress + trackInfo.TrackSize;
                                // Query actual block length from READ CAPACITY for accuracy;
                                // fall back to 2048 (standard data sector size).
                                var cap = drive.ReadCapacity();
                                int blockLen = (cap.HasValue && cap.Value.BlockLength > 0)
                                    ? (int)cap.Value.BlockLength : 2048;
                                media.UsedBytes = (long)usedSectors * blockLen;

                                // For blank/empty discs where READ CAPACITY may fail or
                                // /sys/block/*/size is unavailable, use FreeBlocks as fallback
                                if (media.CapacityBytes <= 0 && trackInfo.FreeBlocks > 0)
                                {
                                    media.CapacityBytes = (long)trackInfo.FreeBlocks * blockLen;
                                    hasInfo = true;
                                }
                            }
                        }
                    }
                }
            }
            finally
            {
                // Only dispose if we created the drive ourselves
                ownedDrive?.Dispose();
            }
        }
        catch { }

        // Try reading volume label from superblock (ISO 9660 then UDF)
        try
        {
            var label = ReadIsoVolumeLabel(dev);
            if (!string.IsNullOrWhiteSpace(label))
            {
                media.VolumeLabel = label;
                media.FileSystem = "ISO 9660";
                hasInfo = true;
            }
            else
            {
                // Try UDF volume label (common on DVD/BD media)
                var udfLabel = ReadUdfVolumeLabel(dev);
                if (!string.IsNullOrWhiteSpace(udfLabel))
                {
                    media.VolumeLabel = udfLabel;
                    media.FileSystem = "UDF";
                    hasInfo = true;
                }
            }
        }
        catch { }

        // Try getting capacity from /sys/block
        try
        {
            var devName = Path.GetFileName(dev);
            var sizePath = $"/sys/block/{devName}/size";
            if (File.Exists(sizePath))
            {
                var sizeStr = File.ReadAllText(sizePath).Trim();
                if (long.TryParse(sizeStr, out var sectors) && sectors > 0)
                {
                    // /sys/block/*/size reports in 512-byte sectors.
                    // Capacity in bytes is sectors × 512 — this is correct for all device types.
                    // Note: optical media internally uses 2048-byte sectors, but the kernel
                    // always reports /sys/block/*/size in 512-byte units.
                    media.CapacityBytes = sectors * 512;
                    hasInfo = true;
                }
            }
        }
        catch { }

        if (hasInfo)
        {
            if (string.IsNullOrWhiteSpace(media.MediaType))
                media.MediaType = media.CapacityBytes > Helpers.FormatHelper.CdImageSizeThresholdBytes ? "DVD/BD" : "CD";
            if (string.IsNullOrWhiteSpace(media.DiscState))
                media.DiscState = "Closed";
            return media;
        }

        return null;
    }

    /// <summary>Reads the ISO 9660 volume label directly from the device.</summary>
    private static string? ReadIsoVolumeLabel(string dev)
    {
        try
        {
            using var stream = new FileStream(dev, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite, 4096, false);

            // ISO 9660 PVD is at sector 16 (offset 32768)
            stream.Seek(32768, SeekOrigin.Begin);
            var pvd = new byte[2048];
            if (stream.Read(pvd, 0, 2048) < 2048) return null;

            // Verify PVD signature: type=1, id="CD001"
            if (pvd[0] != 0x01 || pvd[1] != (byte)'C' || pvd[2] != (byte)'D' ||
                pvd[3] != (byte)'0' || pvd[4] != (byte)'0' || pvd[5] != (byte)'1')
                return null;

            // Volume identifier is at offset 40, 32 bytes
            var label = Encoding.ASCII.GetString(pvd, 40, 32).Trim();
            return string.IsNullOrWhiteSpace(label) ? null : label;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Reads the UDF volume label directly from the device.</summary>
    /// <remarks>
    /// UDF Primary Volume Descriptor is at sector 256 (offset 524288).
    /// Tag identifier 0x0001 identifies it as a PVD. Volume identifier
    /// is a Dstring at offset 24, 32 bytes.
    /// </remarks>
    private static string? ReadUdfVolumeLabel(string dev)
    {
        try
        {
            using var stream = new FileStream(dev, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite, 4096, false);

            // UDF PVD is at sector 256 (offset UdfPvdSector * 2048 = 524288)
            stream.Seek(UdfPvdSector * 2048, SeekOrigin.Begin);
            var sector = new byte[2048];
            if (stream.Read(sector, 0, 2048) < 2048) return null;

            // Check UDF tag identifier (bytes 0-1, little-endian) = 0x0001 (Primary Volume Descriptor)
            var tagId = (ushort)(sector[0] | (sector[1] << 8));
            if (tagId != 0x0001) return null;

            // Volume identifier is a Dstring at offset 24, 32 bytes.
            // Last byte stores the length of the used portion (including the encoding byte),
            // so actual character data is identLen - 1 bytes starting at offset 25.
            // Per ECMA-167 §7.2.12, identLen can be 0 (empty) to 31 (all content bytes used).
            int identLen = sector[24 + 31];
            if (identLen <= 0 || identLen > 31) return null;

            string label;
            if (sector[24] == 0x10 && identLen >= 2)
            {
                // UTF-16 (big-endian per OSTA spec)
                label = Encoding.BigEndianUnicode.GetString(sector, 25, identLen - 1).Trim('\0', ' ');
            }
            else
            {
                // OSTA Compressed Unicode (essentially UTF-8/ASCII)
                label = Encoding.UTF8.GetString(sector, 25, identLen - 1).Trim('\0', ' ');
            }

            return string.IsNullOrWhiteSpace(label) ? null : label;
        }
        catch
        {
            return null;
        }
    }


    private static bool[] ParseBoolArray(string line)
    {
        var parts = line.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var values = new List<bool>();
        bool pastColon = false;
        foreach (var p in parts)
        {
            if (!pastColon) { if (p.EndsWith(':')) pastColon = true; continue; }
            values.Add(p == "1");
        }
        return values.ToArray();
    }

    private static string DetermineLinuxDriveType(DiscDrive d)
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

    private static string GetLinuxDriveStatus(string dev)
    {
        try
        {
            if (!File.Exists(dev)) return "Not Found";
            using var fs = new FileStream(dev, FileMode.Open,
                FileAccess.Read, FileShare.ReadWrite);
            return "Ready";
        }
        catch (IOException ex)
        {
            // ENOMEDIUM = 123 on Linux x86_64 and arm64
            const int ErrnoNoMedium = 123;
            var errno = ex.HResult & 0xFFFF;
            if (errno == ErrnoNoMedium ||
                ex.Message.Contains("no medium", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("no media",  StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("not ready",  StringComparison.OrdinalIgnoreCase))
                return "No Media";
            // EACCES = 13 (permission denied)
            if (errno == 13 || ex.Message.Contains("permission", StringComparison.OrdinalIgnoreCase))
                return "Permission Denied";
            return "Not Ready";
        }
        catch
        {
            return "Not Ready";
        }
    }
}
