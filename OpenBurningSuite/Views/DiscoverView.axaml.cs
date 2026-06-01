// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using OpenBurningSuite.Models;
using OpenBurningSuite.Services;

namespace OpenBurningSuite.Views;

public partial class DiscoverView : UserControl
{
    private readonly DiscDiscoveryService _discovery;
    private List<DiscDrive> _drives = new();

    public DiscoverView()
    {
        InitializeComponent();
        _discovery = new DiscDiscoveryService();

        // Show platform-specific hint text for the "no drives" panel
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            TxtNoDrivesHint.Text = "On macOS, the drive may take a few seconds to appear after connection.";
            TxtNoDrivesHint.IsVisible = true;
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            TxtNoDrivesHint.Text = "On Linux, ensure you have read/write access to /dev/sr* devices.";
            TxtNoDrivesHint.IsVisible = true;
        }

        RefreshDrives();
    }

    // -----------------------------------------------------------------------
    // Event handlers
    // -----------------------------------------------------------------------
    private void OnRefreshDrivesClick(object? sender, RoutedEventArgs e) => RefreshDrives();

    private void OnClearLogClick(object? sender, RoutedEventArgs e) =>
        TxtLog.Text = string.Empty;

    private void OnDriveSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var idx = CmbDriveSel.SelectedIndex;
        if (idx >= 0 && idx < _drives.Count)
            ShowDriveCapabilities(_drives[idx]);
    }

    private async void OnProbeMediaClick(object? sender, RoutedEventArgs e)
    {
        var idx = CmbDriveSel.SelectedIndex;
        if (idx < 0 || idx >= _drives.Count)
        {
            Log("⚠ Select a drive first.");
            return;
        }
        var drive = _drives[idx];

        // Re-probe media freshly via SCSI instead of using cached data.
        // This ensures accurate results after disc insert/eject operations.
        Log("🔍 Probing media...");
        BtnProbeMedia.IsEnabled = false;
        try
        {
            await Task.Run(() =>
            {
                try
                {
                    using var optDrive = new Native.Optical.OpticalDrive(drive.DevicePath);

                    // Clear any stale cached media info before probing.
                    // After a burn or other operation, the drive may have UNIT ATTENTION
                    // conditions queued that make the first few commands return stale data.
                    drive.CurrentMedia = null;

                    // Send multiple TEST UNIT READY commands to clear all pending
                    // UNIT ATTENTION conditions. Drives may queue multiple UA events
                    // (e.g., "medium may have changed" + "mode parameters changed")
                    // and each TUR clears only one. Three TURs covers even aggressive queuing.
                    optDrive.TestUnitReady();
                    optDrive.TestUnitReady();
                    optDrive.TestUnitReady();

                    // Force the drive to re-read the medium by issuing START STOP UNIT
                    // (stop + start). After a burn, the drive's internal cache may still hold
                    // pre-burn data, causing stale reads for volume label/filesystem.
                    // This spin-down/spin-up cycle forces a fresh medium read.
                    optDrive.StartStopUnit(start: false);
                    System.Threading.Thread.Sleep(500);
                    optDrive.StartStopUnit(start: true);

                    // Try WaitForDriveReady (includes TEST UNIT READY + fallbacks).
                    // Use a shorter timeout for drives that should already be ready.
                    bool driveReady = optDrive.WaitForDriveReady(maxWaitMs: 15_000);

                    if (!driveReady)
                    {
                        // Even if WaitForDriveReady returns false, still attempt to detect
                        // media via GET CONFIGURATION — some drives respond to profile queries
                        // even when TEST UNIT READY fails (e.g., USB drives, virtual drives).
                        var checkProfile = optDrive.GetCurrentProfile();
                        if (checkProfile == 0)
                        {
                            drive.CurrentMedia = null;
                            drive.Status = "No Media";
                            return;
                        }
                        // Profile detected — drive has media despite TUR failure
                    }

                    drive.Status = "Ready";
                    var profile = optDrive.GetCurrentProfile();
                    if (profile == 0)
                    {
                        drive.CurrentMedia = null;
                        return;
                    }

                    drive.CurrentMedia ??= new DiscMedia();
                    drive.CurrentMedia.MediaType = Native.Optical.OpticalDrive.ProfileToMediaType(profile);

                    // Check for M-DISC media
                    if (optDrive.IsMDiscMedia())
                    {
                        var mDiscType = optDrive.GetMDiscMediaType();
                        if (mDiscType != null)
                            drive.CurrentMedia.MediaType = mDiscType;
                    }

                    var discInfo = optDrive.ReadDiscInfo();

                    // Get media capacity via READ CAPACITY (used for both total capacity
                    // and block length in used bytes calculation below)
                    var capacity = optDrive.ReadCapacity();
                    if (capacity.HasValue)
                        drive.CurrentMedia.CapacityBytes = ((long)capacity.Value.LastLba + 1) * capacity.Value.BlockLength;

                    if (discInfo != null)
                    {
                        drive.CurrentMedia.DiscState = discInfo.DiscStatusString;
                        drive.CurrentMedia.Sessions = discInfo.NumberOfSessionsLsb;
                        drive.CurrentMedia.Tracks = discInfo.LastTrackInLastSessionLsb;

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

                    // Read volume label — try ISO 9660 PVD first, then UDF
                    try
                    {
                        // ISO 9660 Primary Volume Descriptor is at LBA 16
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

                        // If no ISO 9660 label found, try UDF (common on DVD/BD media).
                        // UDF Primary Volume Descriptor is at LBA 256, tag ID 0x0001.
                        // Volume identifier is at offset 24, 32 bytes (Dstring, first byte = length).
                        if (string.IsNullOrWhiteSpace(drive.CurrentMedia.VolumeLabel))
                        {
                            var (udfResult, udfData) = optDrive.ReadSectors(256, 1, 2048);
                            if (udfResult.Success && udfResult.DataTransferred >= 2048)
                            {
                                // Check UDF tag identifier (bytes 0-1, little-endian) = 0x0001 (Primary Volume Descriptor)
                                var tagId = (ushort)(udfData[0] | (udfData[1] << 8));
                                if (tagId == 0x0001)
                                {
                                    // Volume identifier is a Dstring at offset 24, 32 bytes.
                                    // First byte of Dstring indicates character encoding:
                                    //   0x08 = OSTA Compressed Unicode (UTF-8), 0x10 = UTF-16
                                    // Last byte stores the length of the used portion (including
                                    // the encoding byte), so actual character data is identLen - 1 bytes.
                                    int identLen = udfData[24 + 31];
                                    if (identLen > 0 && identLen <= 31)
                                    {
                                        string udfLabel;
                                        if (udfData[24] == 0x10 && identLen >= 2)
                                        {
                                            // UTF-16 (big-endian per OSTA spec)
                                            udfLabel = System.Text.Encoding.BigEndianUnicode
                                                .GetString(udfData, 25, identLen - 1).Trim('\0', ' ');
                                        }
                                        else
                                        {
                                            // OSTA Compressed Unicode (essentially UTF-8/ASCII)
                                            udfLabel = System.Text.Encoding.UTF8
                                                .GetString(udfData, 25, identLen - 1).Trim('\0', ' ');
                                        }

                                        if (!string.IsNullOrWhiteSpace(udfLabel))
                                        {
                                            drive.CurrentMedia.VolumeLabel = udfLabel;
                                            drive.CurrentMedia.FileSystem = string.IsNullOrWhiteSpace(drive.CurrentMedia.FileSystem)
                                                ? "UDF"
                                                : drive.CurrentMedia.FileSystem + " + UDF";
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[DiscoverView] Volume label read failed: {ex.Message}"); }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[DiscoverView] SCSI probe failed: {ex.Message}");
                }
            });

            ShowMediaInfo(drive);
        }
        finally
        {
            BtnProbeMedia.IsEnabled = true;
        }
    }

    // -----------------------------------------------------------------------
    // Drive discovery
    // -----------------------------------------------------------------------
    private async void RefreshDrives()
    {
        Log("Scanning for optical drives...");
        BtnRefreshDrives.IsEnabled = false;

        try
        {
            _drives = await Task.Run(() => _discovery.DiscoverDrives());

            DrivesGrid.ItemsSource = _drives;

            // Toggle visibility of drives grid vs "no drives found" panel
            DrivesGrid.IsVisible = _drives.Count > 0;
            NoDrivesPanel.IsVisible = _drives.Count == 0;

            // Populate drive selector
            CmbDriveSel.ItemsSource = _drives.Select(d => d.DisplayName).ToList();
            if (_drives.Count > 0)
            {
                CmbDriveSel.SelectedIndex = 0;
                ShowDriveCapabilities(_drives[0]);
            }

            Log(_drives.Count == 0
                ? "⚠ No optical drives found. Connect an optical drive and click Refresh."
                : $"✅ Found {_drives.Count} drive(s).");
        }
        catch (Exception ex)
        {
            Log($"❌ Error scanning drives: {ex.Message}");
        }
        finally
        {
            BtnRefreshDrives.IsEnabled = true;
        }
    }

    // -----------------------------------------------------------------------
    // Media info display
    // -----------------------------------------------------------------------
    private void ShowMediaInfo(DiscDrive drive)
    {
        var m = drive.CurrentMedia;
        if (m == null)
        {
            ClearMediaInfo("No media detected");
            Log($"No media in {drive.DevicePath}");
            return;
        }

        TxtMediaType.Text   = string.IsNullOrWhiteSpace(m.MediaType) ? "Unknown" : m.MediaType;
        TxtDiscState.Text   = string.IsNullOrWhiteSpace(m.DiscState) ? "Unknown" : m.DiscState;
        TxtVolumeLabel.Text = string.IsNullOrWhiteSpace(m.VolumeLabel) ? "(none)" : m.VolumeLabel;
        TxtCapacity.Text    = m.CapacityBytes > 0 ? m.CapacityFormatted : "Unknown";
        TxtUsedFree.Text    = m.CapacityBytes > 0
            ? $"{m.UsedFormatted} / {m.FreeFormatted}"
            : "Unknown";
        TxtFileSystem.Text  = string.IsNullOrWhiteSpace(m.FileSystem) ? "Unknown" : m.FileSystem;
        TxtSessions.Text    = m.Sessions > 0 ? m.Sessions.ToString() : "1";
        TxtTracks.Text      = m.Tracks > 0 ? m.Tracks.ToString() : "Unknown";

        Log($"✅ Media probed: {m.MediaType} — {m.VolumeLabel}");
    }

    private void ClearMediaInfo(string msg = "—")
    {
        TxtMediaType.Text   = msg;
        TxtDiscState.Text   = "—";
        TxtVolumeLabel.Text = "—";
        TxtCapacity.Text    = "—";
        TxtUsedFree.Text    = "—";
        TxtFileSystem.Text  = "—";
        TxtSessions.Text    = "—";
        TxtTracks.Text      = "—";
    }

    // -----------------------------------------------------------------------
    // Drive capabilities display
    // -----------------------------------------------------------------------
    private void ShowDriveCapabilities(DiscDrive drive)
    {
        var p = drive.Profiles;
        var f = drive.Features;

        // Read capabilities
        ChkReadCdR.IsChecked = p.ReadCdR;
        ChkReadCdRw.IsChecked = p.ReadCdRw;
        ChkReadDvdRom.IsChecked = p.ReadDvdRom;
        ChkReadDvdR.IsChecked = p.ReadDvdR;
        ChkReadDvdRw.IsChecked = p.ReadDvdRw;
        ChkReadDvdPlusR.IsChecked = p.ReadDvdPlusR;
        ChkReadDvdPlusRw.IsChecked = p.ReadDvdPlusRw;
        ChkReadDvdRDl.IsChecked = p.ReadDvdRDl;
        ChkReadDvdRwDl.IsChecked = p.ReadDvdRwDl;
        ChkReadDvdPlusRDl.IsChecked = p.ReadDvdPlusRDl;
        ChkReadDvdPlusRwDl.IsChecked = p.ReadDvdPlusRwDl;
        ChkReadDvdRam.IsChecked = p.ReadDvdRam;
        ChkReadHdDvdRom.IsChecked = p.ReadHdDvdRom;
        ChkReadHdDvdR.IsChecked = p.ReadHdDvdR;
        ChkReadHdDvdRw.IsChecked = p.ReadHdDvdRw;
        ChkReadHdDvdRam.IsChecked = p.ReadHdDvdRam;
        ChkReadHdDvdRDl.IsChecked = p.ReadHdDvdRDl;
        ChkReadHdDvdRwDl.IsChecked = p.ReadHdDvdRwDl;
        ChkReadBdRom.IsChecked = p.ReadBdRom;
        ChkReadBdR.IsChecked = p.ReadBdR;
        ChkReadBdRe.IsChecked = p.ReadBdRe;
        ChkReadBdRXl.IsChecked = p.ReadBdRXl;
        ChkReadBdReXl.IsChecked = p.ReadBdReXl;

        // Write capabilities
        ChkWriteCdR.IsChecked = p.WriteCdR;
        ChkWriteCdRw.IsChecked = p.WriteCdRw;
        ChkWriteDvdRom.IsChecked = false; // DVD-ROM is read-only by definition
        ChkWriteDvdR.IsChecked = p.WriteDvdR;
        ChkWriteDvdRw.IsChecked = p.WriteDvdRw;
        ChkWriteDvdPlusR.IsChecked = p.WriteDvdPlusR;
        ChkWriteDvdPlusRw.IsChecked = p.WriteDvdPlusRw;
        ChkWriteDvdRDl.IsChecked = p.WriteDvdRDl;
        ChkWriteDvdRwDl.IsChecked = false; // DVD-RW DL has no standard MMC profile — no drives support this format
        ChkWriteDvdPlusRDl.IsChecked = p.WriteDvdPlusRDl;
        ChkWriteDvdPlusRwDl.IsChecked = p.WriteDvdPlusRwDl;
        ChkWriteDvdRam.IsChecked = p.WriteDvdRam;
        ChkWriteHdDvdRom.IsChecked = false; // HD DVD-ROM is read-only
        ChkWriteHdDvdR.IsChecked = p.WriteHdDvdR;
        ChkWriteHdDvdRw.IsChecked = p.WriteHdDvdRw;
        ChkWriteHdDvdRam.IsChecked = p.WriteHdDvdRam;
        ChkWriteHdDvdRDl.IsChecked = p.WriteHdDvdRDl;
        ChkWriteHdDvdRwDl.IsChecked = p.WriteHdDvdRwDl;
        ChkWriteBdRom.IsChecked = false; // BD-ROM is read-only
        ChkWriteBdR.IsChecked = p.WriteBdR;
        ChkWriteBdRe.IsChecked = p.WriteBdRe;
        ChkWriteBdRXl.IsChecked = p.WriteBdRXl;
        ChkWriteBdReXl.IsChecked = p.WriteBdReXl;

        // Additional features
        ChkC2Errors.IsChecked = f.C2ErrorPointers;
        ChkCdText.IsChecked = f.CdText;
        ChkLayerJumpRec.IsChecked = f.LayerJumpRecording;
        ChkLabelflash.IsChecked = f.Labelflash;
        ChkLightScribe.IsChecked = f.LightScribe;
        ChkBindingNonce.IsChecked = f.BindingNonceGeneration;
        ChkBusEncryption.IsChecked = f.BusEncryption;

        TxtWriteSpeeds.Text = drive.SupportedWriteSpeeds.Count > 0
            ? string.Join(", ", drive.SupportedWriteSpeeds)
            : "Unknown";

        TxtReadSpeeds.Text = drive.SupportedReadSpeeds.Count > 0
            ? string.Join(", ", drive.SupportedReadSpeeds)
            : "Unknown";

        // Populate firmware/hardware info
        PopulateFirmwareInfo(drive);
    }

    private void PopulateFirmwareInfo(DiscDrive drive)
    {
        try
        {
            // Use probed hardware info if available
            TxtDriveVendor.Text = !string.IsNullOrWhiteSpace(drive.VendorId)
                ? drive.VendorId : "—";
            TxtDriveFirmware.Text = !string.IsNullOrWhiteSpace(drive.FirmwareRevision)
                ? drive.FirmwareRevision : "—";
            TxtDriveSerial.Text = !string.IsNullOrWhiteSpace(drive.SerialNumber)
                ? drive.SerialNumber : "—";
            TxtBufferSize.Text = drive.BufferSizeKiB > 0
                ? $"{drive.BufferSizeKiB} KiB" : "—";

            // Fallback: extract vendor from model string if not set by hardware probe
            if (TxtDriveVendor.Text == "—")
            {
                var modelParts = drive.DriveModel.Trim().Split(' ', 2);
                if (modelParts.Length > 0 && !string.IsNullOrWhiteSpace(modelParts[0]))
                    TxtDriveVendor.Text = modelParts[0];
            }

            // If hardware info wasn't populated during drive discovery, try SCSI INQUIRY
            if (TxtDriveFirmware.Text == "—" || TxtDriveSerial.Text == "—")
            {
                Task.Run(() =>
                {
                    try
                    {
                        using var optDrive = new Native.Optical.OpticalDrive(drive.DevicePath);
                        var (vendor, product, revision) = optDrive.Inquiry();
                        var serial = optDrive.InquirySerialNumber();
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (!string.IsNullOrWhiteSpace(vendor) && vendor != "Unknown"
                                && TxtDriveVendor.Text == "—")
                                TxtDriveVendor.Text = vendor;
                            if (!string.IsNullOrWhiteSpace(revision) && TxtDriveFirmware.Text == "—")
                                TxtDriveFirmware.Text = revision;
                            if (!string.IsNullOrWhiteSpace(serial) && TxtDriveSerial.Text == "—")
                                TxtDriveSerial.Text = serial;
                        });
                    }
                    catch { /* best effort */ }
                });
            }
        }
        catch { /* best effort */ }
    }

    // -----------------------------------------------------------------------
    // Content analysis
    // -----------------------------------------------------------------------
    private async void OnAnalyzeContentClick(object? sender, RoutedEventArgs e)
    {
        var idx = CmbDriveSel.SelectedIndex;
        if (idx < 0 || idx >= _drives.Count)
        {
            Log("⚠ Select a drive first.");
            return;
        }
        var drive = _drives[idx];

        Log("🔬 Analyzing disc content...");
        BtnAnalyzeContent.IsEnabled = false;

        try
        {
            await Task.Run(() =>
            {
                try
                {
                    using var optDrive = new Native.Optical.OpticalDrive(drive.DevicePath);
                    if (!optDrive.WaitForDriveReady(maxWaitMs: 10_000))
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            TxtContentType.Text = "No media";
                            TxtMediaId.Text = "—";
                            TxtLayerInfo.Text = "—";
                            TxtTrackListing.Text = "No media in drive";
                        });
                        return;
                    }

                    var profile = optDrive.GetCurrentProfile();
                    bool isDvdOrBd = profile >= 0x0010;

                    // ---- Content type detection using DiscContentDetectionService ----
                    var discContentInfo = DiscContentDetectionService.DetectFromDrive(optDrive);
                    string contentType = discContentInfo.ContentLabel;

                    // ---- Media manufacturer ID ----
                    string mediaId = "—";
                    // Media ID is encoded in ATIP (CD) or ADIP (DVD/BD) data
                    // We use the profile to give a general classification
                    mediaId = Native.Optical.OpticalDrive.ProfileToMediaType(profile);

                    // ---- Layer info ----
                    string layerInfo = "—";
                    try
                    {
                        if (isDvdOrBd)
                        {
                            var capacity = optDrive.ReadCapacity();
                            if (capacity.HasValue)
                            {
                                var sizeGb = ((long)capacity.Value.LastLba + 1) * capacity.Value.BlockLength / (1024.0 * 1024.0 * 1024.0);

                                // Try to get actual layer count from Physical Format Info
                                // (READ DISC STRUCTURE format 0x00, byte 6 bits 6:5).
                                // For DVD, GetLayerBreakPosition uses the Number of Layers
                                // field which is 0=single, 1=dual per ECMA-267 §23.3.
                                // For BD, layer break detection may not work (different
                                // disc structure), so we fall back to size-based estimation.
                                int layers = 1;
                                var layerBreak = optDrive.GetLayerBreakPosition();
                                if (layerBreak.HasValue)
                                    layers = 2;
                                else if (profile >= 0x0040) // BD
                                {
                                    // BD layer estimation from capacity when physical info unavailable:
                                    // SL=25GB, DL=50GB, TL=100GB, QL=128GB
                                    if (sizeGb > 100.0) layers = 4;
                                    else if (sizeGb > 50.0) layers = 3;
                                    else if (sizeGb > 25.0) layers = 2;
                                }
                                else
                                {
                                    // DVD layer estimation: SL=4.7GB, DL=8.5GB
                                    if (sizeGb > 8.0) layers = 2;
                                }

                                layerInfo = $"{layers} layer{(layers > 1 ? "s" : "")} ({sizeGb:F1} GB)";
                            }
                        }
                        else
                        {
                            var capacity = optDrive.ReadCapacity();
                            if (capacity.HasValue)
                            {
                                var sizeMb = ((long)capacity.Value.LastLba + 1) * capacity.Value.BlockLength / (1024.0 * 1024.0);
                                layerInfo = $"Single layer ({sizeMb:F0} MB)";
                            }
                            else
                            {
                                layerInfo = "Single layer (CD)";
                            }
                        }
                    }
                    catch { /* layer info is optional */ }

                    // ---- Track listing ----
                    var trackLines = new System.Text.StringBuilder();
                    try
                    {
                        var discInfo = optDrive.ReadDiscInfo();
                        if (discInfo != null)
                        {
                            trackLines.AppendLine($"{"#",-4} {"Type",-10} {"Start LBA",-12} {"Length",-12} {"Size",-10}");
                            trackLines.AppendLine(new string('─', 52));

                            var lastTrack = discInfo.LastTrackInLastSessionLsb;
                            for (uint t = 1; t <= (uint)lastTrack; t++)
                            {
                                var trackInfo = optDrive.ReadTrackInfo(t);
                                if (trackInfo != null)
                                {
                                    bool isAudio = (trackInfo.TrackMode & 0x04) == 0;
                                    var typeStr = isAudio ? "Audio" : "Data";
                                    var sizeMb = (long)trackInfo.TrackSize * 2048 / (1024.0 * 1024.0);
                                    trackLines.AppendLine(
                                        $"{t,-4} {typeStr,-10} {trackInfo.TrackStartAddress,-12} " +
                                        $"{trackInfo.TrackSize,-12} {sizeMb:F1} MB");
                                }
                            }
                        }
                    }
                    catch { trackLines.AppendLine("Could not read track info"); }

                    Dispatcher.UIThread.Post(() =>
                    {
                        TxtContentType.Text = contentType;
                        TxtMediaId.Text = mediaId;
                        TxtLayerInfo.Text = layerInfo;
                        TxtTrackListing.Text = trackLines.Length > 0
                            ? trackLines.ToString()
                            : "No track information available";

                        // Show extended content details when available
                        var detailParts = new System.Collections.Generic.List<string>();
                        if (!string.IsNullOrWhiteSpace(discContentInfo.VolumeLabel))
                            detailParts.Add($"Volume: {discContentInfo.VolumeLabel}");
                        if (!string.IsNullOrWhiteSpace(discContentInfo.GamingSystem))
                            detailParts.Add($"System: {discContentInfo.GamingSystem}");
                        if (!string.IsNullOrWhiteSpace(discContentInfo.GameTitle))
                            detailParts.Add($"Title: {discContentInfo.GameTitle}");
                        if (!string.IsNullOrWhiteSpace(discContentInfo.GameTitleId))
                            detailParts.Add($"ID: {discContentInfo.GameTitleId}");
                        if (!string.IsNullOrWhiteSpace(discContentInfo.SystemIdentifier))
                            detailParts.Add($"System ID: {discContentInfo.SystemIdentifier}");
                        if (discContentInfo.HasCssProtection)
                            detailParts.Add("🔒 CSS protected");
                        if (discContentInfo.IsEncrypted)
                            detailParts.Add("🔒 Encrypted");
                        if (discContentInfo.DvdRegionMask != 0)
                            detailParts.Add($"Region: {discContentInfo.DvdRegionMask:X2}");
                        if (discContentInfo.AudioTrackCount > 0 && discContentInfo.DataTrackCount > 0)
                            detailParts.Add($"{discContentInfo.AudioTrackCount} audio + {discContentInfo.DataTrackCount} data tracks");
                        else if (discContentInfo.AudioTrackCount > 0)
                            detailParts.Add($"{discContentInfo.AudioTrackCount} audio tracks");
                        if (discContentInfo.SessionCount > 1)
                            detailParts.Add($"{discContentInfo.SessionCount} sessions");

                        if (detailParts.Count > 0)
                        {
                            PnlContentDetails.IsVisible = true;
                            TxtContentDetails.Text = string.Join("\n", detailParts);
                        }
                        else
                        {
                            PnlContentDetails.IsVisible = false;
                        }
                    });
                }
                catch (Exception ex)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        TxtContentType.Text = "Error";
                        Log($"❌ Content analysis failed: {ex.Message}");
                    });
                }
            });

            Log("✅ Content analysis complete.");
        }
        finally
        {
            BtnAnalyzeContent.IsEnabled = true;
        }
    }

    // -----------------------------------------------------------------------
    // Log helper
    // -----------------------------------------------------------------------
    private void Log(string message)
    {
        if (Dispatcher.UIThread.CheckAccess())
            LogCore(message);
        else
            Dispatcher.UIThread.Post(() => LogCore(message));
    }

    private void LogCore(string message)
    {
        var existing = TxtLog.Text ?? string.Empty;
        TxtLog.Text = existing + $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n";
        var text = TxtLog.Text ?? string.Empty;
        if (text.Length > 30_000)
        {
            var trimIdx = text.IndexOf('\n', text.Length - 20_000);
            if (trimIdx > 0)
                TxtLog.Text = "...\n" + text[(trimIdx + 1)..];
            else
                TxtLog.Text = "...\n" + text[(text.Length - 20_000)..];
        }
        TxtLog.CaretIndex = TxtLog.Text?.Length ?? 0;
    }
}
