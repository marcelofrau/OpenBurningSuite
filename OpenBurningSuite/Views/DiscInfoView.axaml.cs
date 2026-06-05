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

        bool isRomMedia = r.MediaType.Contains("ROM");

        var writeSpeeds = (!isRomMedia && r.SupportedWriteSpeeds.Count > 0)
            ? string.Join("; ", r.SupportedWriteSpeeds.OrderBy(x => x).Select(s => FormatSpeedWithX(s, r.MediaType))) : null;

        var sb = new StringBuilder();

        // Header: drive model + firmware + interface type
        var interfaceTag = !string.IsNullOrWhiteSpace(r.InterfaceType) ? $" ({r.InterfaceType})" : "";
        if (!string.IsNullOrWhiteSpace(r.DriveModel))
            sb.AppendLine($"{r.DriveModel} {r.Firmware}{interfaceTag}");
        else if (!string.IsNullOrWhiteSpace(r.Vendor))
            sb.AppendLine($"{r.Vendor} {r.Firmware}{interfaceTag}");

        sb.AppendLine($"Disc Type: {r.MediaType}");
        sb.AppendLine();
        sb.AppendLine("Disc Information:");
        Indent(sb, $"Status: {r.DiscStatus}");

        if (r.LastSessionState != "Empty")
            Indent(sb, $"State of Last Session: {r.LastSessionState}");

        Indent(sb, $"Erasable: {(r.IsErasable ? "Yes" : "No")}");

        if (r.DiscStatus != "Empty")
            Indent(sb, $"Finalized: {finalizedStatus}");

        if (r.Sessions > 0)
            Indent(sb, $"Sessions: {r.Sessions}");

        // Sectors / Size / Time (for finalized or complete discs)
        if (r.TotalSectors > 0)
        {
            Indent(sb, $"Sectors: {r.TotalSectors:N0}");
            Indent(sb, $"Size: {FormatHelper.FormatBytes(r.DiscSizeBytes)} ({r.DiscSizeBytes:N0} bytes)");
            Indent(sb, $"Time: {r.TotalTimeFormatted}");
        }

        if (!string.IsNullOrWhiteSpace(r.Mid))
            Indent(sb, $"MID: {r.Mid}{(string.IsNullOrWhiteSpace(r.ManufacturerName) ? "" : $" ({r.ManufacturerName})")}");

        if (!string.IsNullOrWhiteSpace(r.ManufacturerName) && r.ManufacturerName != r.Mid)
            Indent(sb, $"Manufacturer: {r.ManufacturerName}");

        if (r.FreeBytes > 0)
            Indent(sb, $"Free Space: {FormatHelper.FormatBytes(r.FreeBytes)}");

        if (!string.IsNullOrWhiteSpace(r.FreeTimeFormatted) && r.FreeSectors > 0)
            Indent(sb, $"Free Time: {r.FreeTimeFormatted}");

        if (r.NextWritableAddress > 0)
            Indent(sb, $"Next Writable Address: {r.NextWritableAddress}");

        // ATIP fields merged inline
        if (r.Atip != null)
        {
            if (!string.IsNullOrWhiteSpace(r.Atip.LeadInStart))
                Indent(sb, $"Start Time of LeadIn: {r.Atip.LeadInStart}");
            if (!string.IsNullOrWhiteSpace(r.Atip.LeadOutLastPossible))
                Indent(sb, $"Last Possible Start Time of LeadOut: {r.Atip.LeadOutLastPossible}");
        }

        if (readSpeeds != null)
            Indent(sb, $"Supported Read Speeds: {readSpeeds}");

        if (!string.IsNullOrWhiteSpace(r.CurrentReadSpeed))
            Indent(sb, $"Current Read Speed: {r.CurrentReadSpeed}");

        if (writeSpeeds != null)
            Indent(sb, $"Supported Write Speeds: {writeSpeeds}");

        // TOC Information (ImgBurn-style)
        if (r.Toc != null && r.Toc.Entries.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("TOC Information:");

            var firstTrack = r.Toc.Entries.FirstOrDefault(e => e.TrackNumber <= 0x99);
            var firstLba = firstTrack?.StartLba ?? 0;
            Indent(sb, $"Session 1... (LBA: {firstLba})");

            var entries = r.Toc.Entries;
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];

                if (entry.TrackNumber == 0xAA)
                {
                    Indent(sb, $"-> LeadOut  (LBA: {entry.StartLba})");
                }
                else
                {
                    // Use READ HEADER DataMode first, then TrackInfo DataMode, then fallback
                    byte dataMode = 0;
                    if (r.TrackDataModes.TryGetValue(entry.TrackNumber, out var dm))
                        dataMode = dm;
                    else
                        dataMode = r.TrackInfos.FirstOrDefault(ti => ti.TrackNumber == entry.TrackNumber)?.DataMode ?? 0;

                    string mode = entry.IsData
                        ? (dataMode switch
                        {
                            1 => "Mode 1",
                            2 => "Mode 2, Form 1",
                            _ => (entry.Adr switch
                            {
                                1 => "Mode 1",
                                4 => "Mode 2, Form 1",
                                _ => ((entry.Control & 0x0F) switch
                                {
                                    0x04 => "Mode 1",
                                    0x05 => "Mode 2, Form 1",
                                    0x06 => "Mode 2, Form 2",
                                    _ => $"Data (Control=0x{entry.Control:X})"
                                })
                            })
                        })
                        : ((entry.Control & 0x01) switch
                        {
                            0 => "Audio",
                            1 => "Audio (pre-emphasis)",
                            _ => "Audio"
                        });

                    // Compute end LBA: next entry's LBA - 1, or LeadOut LBA - 1 for last track
                    uint endLba = 0;
                    for (int j = i + 1; j < entries.Count; j++)
                    {
                        if (entries[j].TrackNumber == 0xAA)
                        {
                            endLba = entries[j].StartLba - 1;
                            break;
                        }
                        if (entries[j].TrackNumber != entry.TrackNumber)
                        {
                            endLba = entries[j].StartLba - 1;
                            break;
                        }
                    }

                    if (endLba > 0)
                        Indent(sb, $"-> Track {entry.TrackNumber:D2}  ({mode}, LBA: {entry.StartLba} - {endLba})");
                    else
                        Indent(sb, $"-> Track {entry.TrackNumber:D2}  ({mode}, LBA: {entry.StartLba})");
                }
            }
        }

        // Track Information
        if (r.TrackInfos.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Track Information:");
            Indent(sb, "Session 1...");
            foreach (var ti in r.TrackInfos)
            {
                Indent(sb, $"-> Track {ti.TrackNumber:D2} (LTSA: {ti.TrackStartAddress}, LTS: {ti.TrackSize}, LRA: {ti.LastRecordedAddress})");
            }
        }

        // Physical Format Information (DVD/BD only)
        if (r.PhysicalFormats.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Physical Format Information:");

            PhysicalFormatInfo? prev = null;
            foreach (var f in r.PhysicalFormats)
            {
                // Skip layers whose data is identical to the previous (e.g. L0 and L1 often match)
                if (prev != null && f.BookType == prev.BookType
                    && f.FirstPhysicalSector == prev.FirstPhysicalSector
                    && f.LastPhysicalSector == prev.LastPhysicalSector
                    && f.LastSectorLayer0 == prev.LastSectorLayer0
                    && f.LinearDensity == prev.LinearDensity
                    && f.TrackDensity == prev.TrackDensity)
                {
                    prev = f;
                    continue;
                }

                Indent(sb, $"Book Type: {f.BookType}");
                Indent(sb, $"Part Version: {f.PartVersion}");
                Indent(sb, $"Disc Size: {f.DiscSize}");
                if (f.MaxReadRateMbps > 0)
                    Indent(sb, $"Maximum Read Rate: {f.MaxReadRateMbps:F2} Mbps");
                Indent(sb, $"Number of Layers: {f.LayerCount}");
                Indent(sb, $"Track Path: {f.TrackPath}");
                Indent(sb, $"Linear Density: {f.LinearDensity}");
                Indent(sb, $"Track Density: {f.TrackDensity}");
                Indent(sb, $"First Physical Sector of Data Area: {f.FirstPhysicalSector:N0}");
                Indent(sb, $"Last Physical Sector of Data Area: {f.LastPhysicalSector:N0}");
                if (f.LastSectorLayer0 > 0 && f.LayerCount > 1)
                    Indent(sb, $"Last Physical Sector in Layer 0: {f.LastSectorLayer0:N0}");
                prev = f;
            }
        }

        // Drive Info
        sb.AppendLine();
        sb.AppendLine("Drive Info:");

        var vendorModel = "";
        if (!string.IsNullOrWhiteSpace(r.Vendor))
            vendorModel = $"Vendor: {r.Vendor}";
        if (!string.IsNullOrWhiteSpace(r.DriveModel))
            vendorModel += (vendorModel.Length > 0 ? "; " : "") + $"Model: {r.DriveModel}";
        if (vendorModel.Length > 0)
            Indent(sb, vendorModel);

        var fwBuffer = "";
        if (!string.IsNullOrWhiteSpace(r.Firmware))
            fwBuffer = $"Firmware: {r.Firmware}";
        if (r.BufferSizeKiB > 0)
            fwBuffer += (fwBuffer.Length > 0 ? "; " : "") + $"Buffer Size: {r.BufferSizeKiB} KiB";
        if (fwBuffer.Length > 0)
            Indent(sb, fwBuffer);

        if (!string.IsNullOrWhiteSpace(r.Serial))
            Indent(sb, $"Serial: {r.Serial}");

        TxtDiscInfo.Text = sb.ToString();
        Log($"Disc info loaded: {r.MediaType} — {r.ManufacturerName}");
    }

    private static void Indent(StringBuilder sb, string line) =>
        sb.AppendLine($"  {line}");

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

    private static readonly (int X, double Kbps)[] CdDaStandardSpeeds =
    {
        (1, 176.4), (2, 352.8), (4, 705.6), (8, 1411.2),
        (10, 1764), (12, 2116.8), (16, 2822.4), (20, 3528),
        (24, 4233.6), (32, 5644.8), (40, 7056), (48, 8467.2),
        (52, 9172.8)
    };

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

        // CD drives report speeds using CD-DA reference (176.4 KB/s = 1x),
        // not CD-ROM data (150 KB/s = 1x). The SCSI GET PERFORMANCE command
        // returns physical data rate, matching the CD-DA base.
        if (mediaType.Contains("CD"))
        {
            var cdBest = CdDaStandardSpeeds[0];
            var cdBestDiff = Math.Abs(kb - cdBest.Kbps);
            foreach (var s in CdDaStandardSpeeds)
            {
                var diff = Math.Abs(kb - s.Kbps);
                if (diff < cdBestDiff)
                {
                    cdBestDiff = diff;
                    cdBest = s;
                }
            }
            return $"{cdBest.X}x ({speed})";
        }

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
