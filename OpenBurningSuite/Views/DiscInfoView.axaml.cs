using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using OpenBurningSuite.Helpers;
using OpenBurningSuite.Models;
using OpenBurningSuite.Services;

namespace OpenBurningSuite.Views;

public partial class DiscInfoView : UserControl
{
    private readonly DiscDiscoveryService _discovery;
    private readonly DiscInfoService _discInfo;
    private List<DiscDrive> _drives = new();

    public DiscInfoView()
    {
        InitializeComponent();
        _discovery = new DiscDiscoveryService();
        _discInfo = new DiscInfoService();

        RefreshDrives();
    }

    private void OnRefreshClick(object? sender, RoutedEventArgs e) => RefreshDrives();

    private void OnClearLogClick(object? sender, RoutedEventArgs e) =>
        TxtLog.Text = string.Empty;

    private void OnDriveSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        _ = ProbeSelectedDriveAsync();

    private async void OnCopyInfoClick(object? sender, RoutedEventArgs e)
    {
        var text = TxtDiscInfo.Text;
        if (!string.IsNullOrWhiteSpace(text))
        {
            var top = TopLevel.GetTopLevel(this);
            if (top?.Clipboard != null)
                await top.Clipboard.SetTextAsync(text.Trim());
            Log("Info copied to clipboard.");
        }
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
                var idx = AutoSelectDriveWithMedia(_drives);
                CmbDriveSel.SelectedIndex = idx;
                Log($"Found {_drives.Count} drive(s).");
                if (idx >= 0)
                    await ProbeSelectedDriveAsync();
            }
            else
            {
                Log("No optical drives found.");
                HideAllInfoPanels();
                PnlNoMedia.IsVisible = true;
            }
        }
        catch (Exception ex)
        {
            Log($"Error scanning drives: {ex.Message}");
        }
        finally
        {
            BtnRefresh.IsEnabled = true;
        }
    }

    private static int AutoSelectDriveWithMedia(List<DiscDrive> drives)
    {
        for (int i = 0; i < drives.Count; i++)
        {
            var d = drives[i];
            if (!string.IsNullOrWhiteSpace(d.DriveLetter) && d.CurrentMedia != null)
                return i;
        }

        for (int i = 0; i < drives.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(drives[i].DriveLetter))
                return i;
        }

        return 0;
    }

    private async Task ProbeSelectedDriveAsync()
    {
        var idx = CmbDriveSel.SelectedIndex;
        if (idx < 0 || idx >= _drives.Count)
            return;

        var drive = _drives[idx];

        TxtDiscInfo.Text = "";
        HideAllInfoPanels();
        PnlLoading.IsVisible = true;
        PnlNoMedia.IsVisible = false;
        TxtLoading.Text = "Reading disc information...";
        Log($"Probing {drive.DisplayName}...");

        BtnRefresh.IsEnabled = false;

        try
        {
            var result = await Task.Run(() =>
            {
                try
                {
                    using var optDrive = new Native.Optical.OpticalDrive(drive.DevicePath);

                    optDrive.TestUnitReady();
                    optDrive.TestUnitReady();
                    optDrive.TestUnitReady();

                    optDrive.StartStopUnit(start: false);
                    System.Threading.Thread.Sleep(500);
                    optDrive.StartStopUnit(start: true);

                    optDrive.WaitForDriveReady(maxWaitMs: 15_000);

                    return _discInfo.GetDiscInfo(optDrive, drive);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[DiscInfoView] SCSI failed: {ex.Message}");
                    return new DiscInfoResult();
                }
            });

            Dispatcher.UIThread.Post(() => ShowDiscInfo(result));
        }
        finally
        {
            Dispatcher.UIThread.Post(() =>
            {
                PnlLoading.IsVisible = false;
                BtnRefresh.IsEnabled = true;
            });
        }
    }

    private void ShowDiscInfo(DiscInfoResult r)
    {
        if (string.IsNullOrWhiteSpace(r.MediaType) || r.MediaType == "No Media")
        {
            HideAllInfoPanels();
            PnlNoMedia.IsVisible = true;
            Log("No media in drive.");
            return;
        }

        HideAllInfoPanels();
        PnlInfoDisplay.IsVisible = true;

        // Determine finalized status
        string finalizedStatus = r.DiscStatus switch
        {
            "Closed" => "Yes (Disc is finalized)",
            "Empty" when r.IsErasable => "No (Blank rewritable disc)",
            "Empty" => "No (Blank disc)",
            "Incomplete" when r.LastSessionState == "Incomplete" => "No (Appendable — open session)",
            "Incomplete" when r.LastSessionState == "Empty" => "No (Appendable)",
            _ => r.DiscStatus
        };

        var readSpeeds = r.SupportedReadSpeeds.Count > 0
            ? string.Join("; ", r.SupportedReadSpeeds.Select(s => FormatSpeedWithX(s, r.MediaType))) : null;

        var writeSpeeds = r.SupportedWriteSpeeds.Count > 0
            ? string.Join("; ", r.SupportedWriteSpeeds.OrderBy(x => x).Select(s => FormatSpeedWithX(s, r.MediaType))) : null;

        var sb = new StringBuilder();

        // Header: drive model + firmware
        if (!string.IsNullOrWhiteSpace(r.DriveModel))
            sb.AppendLine($"{r.DriveModel} {r.Firmware}");
        else if (!string.IsNullOrWhiteSpace(r.Vendor))
            sb.AppendLine($"{r.Vendor} {r.Firmware}");

        sb.AppendLine($"Disc Type: {r.MediaType}");
        sb.AppendLine();
        sb.AppendLine("Disc Information:");
        sb.AppendLine("-----------------------------------------");
        sb.AppendLine($"Status: {r.DiscStatus}");

        if (r.LastSessionState != "Empty")
            sb.AppendLine($"State of Last Session: {r.LastSessionState}");

        sb.AppendLine($"Erasable: {(r.IsErasable ? "Yes" : "No")}");

        if (r.DiscStatus != "Empty")
            sb.AppendLine($"Finalized: {finalizedStatus}");

        if (r.Sessions > 0)
            sb.AppendLine($"Sessions: {r.Sessions}");

        // Sectors / Size / Time (for finalized or complete discs)
        if (r.TotalSectors > 0)
        {
            sb.AppendLine($"Sectors: {r.TotalSectors:N0}");
            sb.AppendLine($"Size: {r.DiscSizeBytes:N0} bytes");
            sb.AppendLine($"Time: {r.TotalTimeFormatted}");
        }

        if (!string.IsNullOrWhiteSpace(r.Mid))
            sb.AppendLine($"MID: {r.Mid}{(string.IsNullOrWhiteSpace(r.ManufacturerName) ? "" : $" ({r.ManufacturerName})")}");

        if (!string.IsNullOrWhiteSpace(r.ManufacturerName) && r.ManufacturerName != r.Mid)
            sb.AppendLine($"Manufacturer: {r.ManufacturerName}");

        if (r.FreeBytes > 0)
            sb.AppendLine($"Free Space: {FormatHelper.FormatBytes(r.FreeBytes)}");

        if (!string.IsNullOrWhiteSpace(r.FreeTimeFormatted) && r.FreeSectors > 0)
            sb.AppendLine($"Free Time: {r.FreeTimeFormatted}");

        if (r.NextWritableAddress > 0)
            sb.AppendLine($"Next Writable Address: {r.NextWritableAddress}");

        // ATIP fields merged inline
        if (r.Atip != null)
        {
            if (!string.IsNullOrWhiteSpace(r.Atip.LeadInStart))
                sb.AppendLine($"Start Time of LeadIn: {r.Atip.LeadInStart}");
            if (!string.IsNullOrWhiteSpace(r.Atip.LeadOutLastPossible))
                sb.AppendLine($"Last Possible Start Time of LeadOut: {r.Atip.LeadOutLastPossible}");
        }

        if (readSpeeds != null)
            sb.AppendLine($"Supported Read Speeds: {readSpeeds}");

        if (!string.IsNullOrWhiteSpace(r.CurrentReadSpeed))
            sb.AppendLine($"Current Read Speed: {r.CurrentReadSpeed}");

        if (writeSpeeds != null)
            sb.AppendLine($"Supported Write Speeds: {writeSpeeds}");

        // TOC Information
        if (r.Toc != null && r.Toc.Entries.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("TOC Information:");
            sb.AppendLine("-----------------------------------------");

            foreach (var entry in r.Toc.Entries)
            {
                var msf = LbaToMsfStr((int)entry.StartLba);
                if (entry.TrackNumber == 0xAA)
                {
                    sb.AppendLine($"LeadOut  (LBA: {entry.StartLba} / {msf})");
                }
                else
                {
                    string mode = entry.IsData
                        ? ((entry.Control & 0x0F) switch
                        {
                            0x04 => "Mode 1",
                            0x05 => "Mode 2, Form 1",
                            0x06 => "Mode 2, Form 2",
                            _ => $"Data (Control=0x{entry.Control:X})"
                        })
                        : ((entry.Control & 0x01) switch
                        {
                            0 => "Audio",
                            1 => "Audio (pre-emphasis)",
                            _ => "Audio"
                        });

                    sb.AppendLine($"Track {entry.TrackNumber:D2}  ({mode}, LBA: {entry.StartLba} / {msf})");
                }
            }
        }

        // Track Information
        if (r.TrackInfos.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Track Information:");
            sb.AppendLine("-----------------------------------------");
            foreach (var ti in r.TrackInfos)
            {
                sb.AppendLine($"Track {ti.TrackNumber:D2} (LTSA: {ti.TrackStartAddress}, LTS: {ti.TrackSize}, LRA: {ti.LastRecordedAddress})");
            }
        }

        // Physical Format Information (DVD/BD only)
        if (r.PhysicalFormats.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Physical Format Information:");
            sb.AppendLine("-----------------------------------------");
            foreach (var f in r.PhysicalFormats)
            {
                sb.AppendLine($"Book Type: {f.BookType}");
                sb.AppendLine($"Disc Size: {f.DiscSize}");
                sb.AppendLine($"Layers: {f.LayerCount}");
                sb.AppendLine($"Track Path: {f.TrackPath}");
                sb.AppendLine($"Linear Density: {f.LinearDensity}");
                sb.AppendLine($"Track Density: {f.TrackDensity}");
                sb.AppendLine($"First Physical Sector: {f.FirstPhysicalSector}");
                sb.AppendLine($"Last Physical Sector: {f.LastPhysicalSector}");
                if (f.LastSectorLayer0 > 0 && f.LayerCount > 1)
                    sb.AppendLine($"Last Sector of Layer 0: {f.LastSectorLayer0}");
            }
        }

        // Drive Info
        sb.AppendLine();
        sb.AppendLine("Drive Info");
        sb.AppendLine("-----------------------------------------");

        var vendorModel = "";
        if (!string.IsNullOrWhiteSpace(r.Vendor))
            vendorModel = $"Vendor: {r.Vendor}";
        if (!string.IsNullOrWhiteSpace(r.DriveModel))
            vendorModel += (vendorModel.Length > 0 ? "; " : "") + $"Model: {r.DriveModel}";
        if (vendorModel.Length > 0)
            sb.AppendLine(vendorModel);

        var fwBuffer = "";
        if (!string.IsNullOrWhiteSpace(r.Firmware))
            fwBuffer = $"Firmware: {r.Firmware}";
        if (r.BufferSizeKiB > 0)
            fwBuffer += (fwBuffer.Length > 0 ? "; " : "") + $"Buffer Size: {r.BufferSizeKiB} KiB";
        if (fwBuffer.Length > 0)
            sb.AppendLine(fwBuffer);

        if (!string.IsNullOrWhiteSpace(r.Serial))
            sb.AppendLine($"Serial: {r.Serial}");

        TxtDiscInfo.Text = sb.ToString();
        Log($"Disc info loaded: {r.MediaType} — {r.ManufacturerName}");
    }

    private static string LbaToMsfStr(int lba)
    {
        int totalFrames = lba + 150;
        int minutes = totalFrames / (75 * 60);
        int seconds = (totalFrames / 75) % 60;
        int frames = totalFrames % 75;
        return $"{minutes:D2}:{seconds:D2}:{frames:D2} (MM:SS:FF)";
    }

    private void HideAllInfoPanels()
    {
        PnlNoMedia.IsVisible = false;
        PnlInfoDisplay.IsVisible = false;
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
            if (trimIdx > 0)
                TxtLog.Text = "...\n" + text[(trimIdx + 1)..];
            else
                TxtLog.Text = "...\n" + text[(text.Length - 20_000)..];
        }
        TxtLog.CaretIndex = TxtLog.Text?.Length ?? 0;
    }

    private static readonly (int X, int Kbps)[] CdStandardSpeeds =
    {
        (1, 150), (2, 300), (4, 600), (8, 1200), (10, 1500), (12, 1800),
        (16, 2400), (20, 3000), (24, 3600), (32, 4800), (40, 6000), (48, 7200), (52, 7800)
    };

    private static readonly (int X, int Kbps)[] DvdStandardSpeeds =
    {
        (1, 1385), (2, 2770), (4, 5540), (6, 8310), (8, 11080),
        (10, 13850), (12, 16620), (16, 22160), (18, 24930), (20, 27700),
        (22, 30470), (24, 33240)
    };

    private static readonly (int X, int Kbps)[] BdStandardSpeeds =
    {
        (1, 4496), (2, 8992), (4, 17984), (6, 26976), (8, 35968),
        (10, 44960), (12, 53952), (14, 62944), (16, 71936)
    };

    private static string FormatSpeedWithX(string speed, string mediaType)
    {
        if (string.IsNullOrWhiteSpace(speed))
            return speed;

        var match = System.Text.RegularExpressions.Regex.Match(speed, @"(\d+)\s*KB/s");
        if (!match.Success || !double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var kb))
            return speed;

        var table = mediaType switch
        {
            string m when m.Contains("CD") => CdStandardSpeeds,
            string m when m.Contains("DVD") || m.Contains("HD DVD") => DvdStandardSpeeds,
            string m when m.Contains("BD") || m.Contains("Blu-ray") => BdStandardSpeeds,
            _ => CdStandardSpeeds
        };

        var best = table[0];
        var bestDiff = Math.Abs(kb - best.Kbps);
        foreach (var s in table)
        {
            var diff = Math.Abs(kb - s.Kbps);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                best = s;
            }
        }

        return $"{best.X}x ({speed})";
    }
}
