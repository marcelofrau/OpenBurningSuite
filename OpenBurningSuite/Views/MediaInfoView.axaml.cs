// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using OpenBurningSuite.Models;
using OpenBurningSuite.Services;

namespace OpenBurningSuite.Views;

public partial class MediaInfoView : UserControl
{
    private readonly DiscDiscoveryService _discovery;
    private List<DiscDrive> _drives = new();

    public MediaInfoView()
    {
        InitializeComponent();
        _discovery = new DiscDiscoveryService();
        RefreshDrives();
    }

    private void OnRefreshClick(object? sender, RoutedEventArgs e) => RefreshDrives();
    private void OnClearLogClick(object? sender, RoutedEventArgs e) => TxtLog.Text = string.Empty;

    private void OnDriveSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var idx = CmbDriveSel.SelectedIndex;
        if (idx >= 0 && idx < _drives.Count)
            ProbeMedia(_drives[idx]);
    }

    private async void RefreshDrives()
    {
        Log("Scanning for optical drives...");
        BtnRefresh.IsEnabled = false;

        try
        {
            _drives = await Task.Run(() => _discovery.DiscoverDrives());
            CmbDriveSel.ItemsSource = _drives.Select(d => d.DisplayName).ToList();

            if (_drives.Count > 0)
            {
                CmbDriveSel.SelectedIndex = 0;
                ProbeMedia(_drives[0]);
            }
            else
            {
                ClearInfo("No optical drives detected");
                Log("⚠ No optical drives found.");
            }
        }
        catch (Exception ex)
        {
            Log($"❌ Error scanning drives: {ex.Message}");
        }
        finally
        {
            BtnRefresh.IsEnabled = true;
        }
    }

    private async void ProbeMedia(DiscDrive? drive)
    {
        if (drive == null)
        {
            ClearInfo("No drive selected");
            return;
        }

        Log($"🔍 Probing media in {drive.DisplayName}...");
        SetMediaStatus("Probing...", "#FF8C00");
        ClearInfo("Probing...");

        try
        {
            var mediaInfo = await Task.Run(() =>
            {
                try
                {
                    using var optDrive = new Native.Optical.OpticalDrive(drive.DevicePath);

                    for (int i = 0; i < 3; i++)
                        optDrive.TestUnitReady();

                    optDrive.StartStopUnit(start: false);
                    System.Threading.Thread.Sleep(300);
                    optDrive.StartStopUnit(start: true);

                    bool driveReady = optDrive.WaitForDriveReady(maxWaitMs: 15_000);

                    ushort profile;
                    if (!driveReady)
                    {
                        profile = optDrive.GetCurrentProfile();
                        if (profile == 0)
                        {
                            drive.Status = "No Media";
                            return new MediaInfoData();
                        }
                    }

                    profile = optDrive.GetCurrentProfile();
                    drive.Status = "Ready";
                    return MediaInfoService.ProbeMedia(optDrive, drive);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"ProbeMedia failed: {ex.Message}");
                    return new MediaInfoData();
                }
            });

            ShowMediaInfo(mediaInfo, drive);

            if (!string.IsNullOrEmpty(mediaInfo.CurrentProfile))
                Log($"✅ Media probed: {mediaInfo.CurrentProfile}");
            else
                Log("⚠ No media detected or media could not be read.");
        }
        catch (Exception ex)
        {
            Log($"❌ Probe failed: {ex.Message}");
            ClearInfo("Probe failed");
        }
    }

    private static readonly Avalonia.Media.Color ColorBlue  = new(0xFF, 0x5B, 0xC0, 0xFF);
    private static readonly Avalonia.Media.Color ColorRed   = new(0xFF, 0xE0, 0x45, 0x20);
    private static readonly Avalonia.Media.Color ColorOrange = new(0xFF, 0xFF, 0x8C, 0x00);

    private void SetMediaStatus(string status, string colorName)
    {
        TxtMediaStatus.Text = status;
        var fgColor = colorName switch
        {
            "#5BC0FF" => ColorBlue,
            "#E04520" => ColorRed,
            "#FF8C00" => ColorOrange,
            _ => ColorBlue,
        };
        TxtMediaStatus.Foreground = new Avalonia.Media.SolidColorBrush(fgColor);
        var bg = new Avalonia.Media.Color(
            fgColor.A, (byte)(fgColor.R / 4), (byte)(fgColor.G / 4), (byte)(fgColor.B / 4));
        BdrMediaStatus.Background = new Avalonia.Media.SolidColorBrush(bg);
    }

    private void ShowMediaInfo(MediaInfoData info, DiscDrive? drive)
    {
        bool hasMedia = !string.IsNullOrEmpty(info.CurrentProfile);

        if (hasMedia)
        {
            TxtCurrentProfile.Text = info.CurrentProfile;
            SetMediaStatus("Ready", "#5BC0FF");
        }
        else
        {
            TxtCurrentProfile.Text = "";
            SetMediaStatus("No Media", "#E04520");
        }

        // ── Disc Information ──
        TxtStatus.Text = !string.IsNullOrEmpty(info.DiscState) ? info.DiscState : "—";
        TxtLastSession.Text = !string.IsNullOrEmpty(info.LastSessionState) ? info.LastSessionState : "—";
        TxtErasable.Text = info.IsErasable ? "Yes" : "No";

        if (info.Sessions > 0)
        {
            TxtSessions.IsVisible = true;
            TxtSessions.Text = info.Sessions.ToString();
        }

        TxtSectors.Text = info.Sectors > 0 ? $"{info.Sectors:N0}" : "—";
        TxtSize.Text = info.CapacityBytes > 0
            ? $"{info.CapacityBytes:N0} bytes ({Helpers.FormatHelper.FormatBytes(info.CapacityBytes)})"
            : "—";
        TxtTime.Text = !string.IsNullOrEmpty(info.TimeMsf) ? info.TimeMsf : "—";

        // Blank media fields
        bool isBlank = string.Equals(info.DiscState, "Empty", StringComparison.OrdinalIgnoreCase);
        LblFreeSectors.IsVisible = isBlank;
        TxtFreeSectors.IsVisible = isBlank;
        LblFreeSpace.IsVisible = isBlank;
        TxtFreeSpace.IsVisible = isBlank;
        LblFreeTime.IsVisible = isBlank;
        TxtFreeTime.IsVisible = isBlank;
        LblNextWritable.IsVisible = isBlank;
        TxtNextWritable.IsVisible = isBlank;

        if (isBlank)
        {
            TxtFreeSectors.Text = info.FreeSectors > 0 ? $"{info.FreeSectors:N0}" : "—";
            TxtFreeSpace.Text = info.FreeBytes > 0
                ? $"{info.FreeBytes:N0} bytes ({Helpers.FormatHelper.FormatBytes(info.FreeBytes)})"
                : "—";
            TxtFreeTime.Text = !string.IsNullOrEmpty(info.FreeTimeMsf) ? info.FreeTimeMsf : "—";
            TxtNextWritable.Text = info.NextWritableAddress > 0 ? $"{info.NextWritableAddress}" : "0";
        }

        // MID
        bool hasMid = !string.IsNullOrEmpty(info.Mid);
        PnlMid.IsVisible = hasMid;
        if (hasMid)
        {
            var midText = info.Mid;
            if (!string.IsNullOrEmpty(info.MidManufacturer))
                midText += $" ({info.MidManufacturer})";
            TxtMid.Text = midText;
        }

        // Volume / FS
        TxtVolumeLabel.Text = !string.IsNullOrEmpty(info.VolumeLabel) ? info.VolumeLabel : "—";
        TxtFileSystem.Text = !string.IsNullOrEmpty(info.FileSystem) ? info.FileSystem : "—";

        // Speeds
        TxtReadSpeeds.Text = info.SupportedReadSpeeds.Count > 0
            ? string.Join("; ", info.SupportedReadSpeeds) : "—";

        bool hasCurrentRead = !string.IsNullOrEmpty(info.CurrentReadSpeed);
        PnlCurrentReadSpeed.IsVisible = hasCurrentRead;
        if (hasCurrentRead)
            TxtCurrentReadSpeed.Text = info.CurrentReadSpeed;

        TxtWriteSpeeds.Text = info.SupportedWriteSpeeds.Count > 0
            ? string.Join("; ", info.SupportedWriteSpeeds) : "—";

        // ── ATIP (CD) ──
        PnlAtip.IsVisible = info.HasCdInfo;
        if (info.HasCdInfo)
        {
            TxtAtipDiscId.Text = !string.IsNullOrEmpty(info.AtipDiscId) ? info.AtipDiscId : "—";
            TxtAtipMfg.Text = !string.IsNullOrEmpty(info.AtipManufacturer) ? info.AtipManufacturer : "—";
            TxtAtipLeadIn.Text = !string.IsNullOrEmpty(info.AtipLeadInStart) ? info.AtipLeadInStart : "—";
            TxtAtipLeadOut.Text = !string.IsNullOrEmpty(info.AtipLeadOutEnd) ? info.AtipLeadOutEnd : "—";
        }

        // ── TOC ──
        PnlToc.IsVisible = info.HasTocInfo;
        if (info.HasTocInfo)
        {
            var tocLines = new System.Text.StringBuilder();
            foreach (var session in info.TocEntries.GroupBy(e => e.SessionNumber))
            {
                tocLines.AppendLine($"Session {session.Key}... (LBA: 0)");
                foreach (var entry in session)
                {
                    tocLines.AppendLine($"  -> Track {entry.TrackNumber:D2}  ({entry.Mode}, LBA: {entry.StartLba} - {entry.EndLba})");
                }
            }
            TxtTocInfo.Text = tocLines.ToString();
        }

        // ── Pre-recorded Info ──
        bool hasPre = !string.IsNullOrEmpty(info.PreRecordedManufacturerId);
        PnlPreRecorded.IsVisible = hasPre;
        if (hasPre)
            TxtPreRecordedMfg.Text = info.PreRecordedManufacturerId;

        // ── Recording Management Area ──
        bool hasRma = !string.IsNullOrEmpty(info.RecordingManagementArea);
        PnlRma.IsVisible = hasRma;
        if (hasRma)
            TxtRmaInfo.Text = info.RecordingManagementArea;

        // ── Physical Format ──
        PnlPhysicalFormat.IsVisible = info.HasPhysicalFormatInfo;
        if (info.HasPhysicalFormatInfo)
        {
            TxtBookType.Text = info.BookType;
            TxtPartVersion.Text = info.PartVersion;
            TxtDiscSize.Text = info.DiscSize;
            TxtMaxReadRate.Text = info.MaxReadRate;
            TxtNumLayers.Text = info.NumberOfLayers > 0 ? info.NumberOfLayers.ToString() : "—";
            TxtTrackPath.Text = info.TrackPath;
            TxtLinearDensity.Text = info.LinearDensity;
            TxtTrackDensity.Text = info.TrackDensity;
            TxtDataStart.Text = info.DataAreaStartSector > 0 ? $"{info.DataAreaStartSector:N0}" : "0";
            TxtDataEnd.Text = info.DataAreaEndSector > 0 ? $"{info.DataAreaEndSector:N0}" : "0";

            bool hasLayerBreak = info.LayerBreakSector > 0 && info.NumberOfLayers > 1;
            PnlLayerBreak.IsVisible = hasLayerBreak;
            if (hasLayerBreak)
                TxtLayerBreak.Text = $"{info.LayerBreakSector:N0}";
        }

        // ── Manufacturer ──
        PnlManufacturer.IsVisible = info.HasManufacturerInfo;
        if (info.HasManufacturerInfo)
        {
            TxtMfgId.Text = info.ManufacturerId;
            TxtMfgName.Text = !string.IsNullOrEmpty(info.ManufacturerName)
                ? info.ManufacturerName : "Unknown";
        }

        // ── Protection ──
        bool hasProt = !string.IsNullOrEmpty(info.CopyrightProtectionType) ||
                       !string.IsNullOrEmpty(info.RegionManagementMask) ||
                       info.HasBca || info.IsMDisc;
        PnlProtection.IsVisible = hasProt;
        if (hasProt)
        {
            TxtProtectionType.Text = !string.IsNullOrEmpty(info.CopyrightProtectionType)
                ? info.CopyrightProtectionType : "None";
            TxtRegionMask.Text = !string.IsNullOrEmpty(info.RegionManagementMask)
                ? info.RegionManagementMask : "—";
            TxtBca.Text = info.HasBca ? $"Yes ({info.BcaDataSize} bytes)" : "No";
            TxtMDisc.Text = info.IsMDisc ? $"Yes ({info.MDiscType})" : "No";
        }

        // ── Drive Hardware ──
        if (drive != null)
        {
            TxtDriveModel.Text = !string.IsNullOrEmpty(drive.DriveModel) ? drive.DriveModel : "—";
            TxtFirmware.Text = !string.IsNullOrEmpty(drive.FirmwareRevision) ? drive.FirmwareRevision : "—";
            TxtSerial.Text = !string.IsNullOrEmpty(drive.SerialNumber) ? drive.SerialNumber : "—";
            TxtBuffer.Text = drive.BufferSizeKiB > 0 ? $"{drive.BufferSizeKiB} KiB" : "—";
            TxtVendor.Text = !string.IsNullOrEmpty(drive.VendorId) ? drive.VendorId : "—";
        }
        else
        {
            TxtDriveModel.Text = "—";
            TxtFirmware.Text = "—";
            TxtSerial.Text = "—";
            TxtBuffer.Text = "—";
            TxtVendor.Text = "—";
        }
    }

    private void ClearInfo(string msg)
    {
        TxtCurrentProfile.Text = msg;
        TxtStatus.Text = "—";
        TxtLastSession.Text = "—";
        TxtErasable.Text = "—";
        TxtSectors.Text = "—";
        TxtSize.Text = "—";
        TxtTime.Text = "—";
        TxtSessions.IsVisible = false;
        LblFreeSectors.IsVisible = false;
        TxtFreeSectors.IsVisible = false;
        LblFreeSpace.IsVisible = false;
        TxtFreeSpace.IsVisible = false;
        LblFreeTime.IsVisible = false;
        TxtFreeTime.IsVisible = false;
        LblNextWritable.IsVisible = false;
        TxtNextWritable.IsVisible = false;
        PnlMid.IsVisible = false;
        TxtVolumeLabel.Text = "—";
        TxtFileSystem.Text = "—";
        TxtReadSpeeds.Text = "—";
        PnlCurrentReadSpeed.IsVisible = false;
        TxtWriteSpeeds.Text = "—";
        PnlAtip.IsVisible = false;
        PnlToc.IsVisible = false;
        PnlPreRecorded.IsVisible = false;
        PnlRma.IsVisible = false;
        PnlPhysicalFormat.IsVisible = false;
        PnlManufacturer.IsVisible = false;
        PnlProtection.IsVisible = false;
        TxtDriveModel.Text = "—";
        TxtFirmware.Text = "—";
        TxtSerial.Text = "—";
        TxtBuffer.Text = "—";
        TxtVendor.Text = "—";
    }

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
            TxtLog.Text = trimIdx > 0
                ? "...\n" + text[(trimIdx + 1)..]
                : "...\n" + text[(text.Length - 20_000)..];
        }
        TxtLog.CaretIndex = TxtLog.Text?.Length ?? 0;
    }
}
