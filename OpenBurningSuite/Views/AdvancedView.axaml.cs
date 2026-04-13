// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using OpenBurningSuite.Helpers;
using OpenBurningSuite.Models;
using OpenBurningSuite.Services;

namespace OpenBurningSuite.Views;

public partial class AdvancedView : UserControl
{
    private readonly BurnService _burnService;
    private readonly DiscDiscoveryService _discovery;
    private CancellationTokenSource? _cts;
    private List<DiscDrive> _drives = new();

    public AdvancedView()
    {
        InitializeComponent();
        _burnService = new BurnService();
        _discovery = new DiscDiscoveryService();

        LoadDrives();
    }

    // -----------------------------------------------------------------------
    // Initialisation
    // -----------------------------------------------------------------------
    private void LoadDrives()
    {
        Task.Run(() => _discovery.DiscoverDrives()).ContinueWith(t =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (t.IsFaulted)
                {
                    _drives = new List<DiscDrive>();
                    Log($"❌ Error discovering drives: {t.Exception?.InnerException?.Message ?? t.Exception?.Message}");
                }
                else
                {
                    _drives = t.IsCompletedSuccessfully ? t.Result : new List<DiscDrive>();
                }
                CmbDrive.Items.Clear();
                foreach (var d in _drives) CmbDrive.Items.Add(d.DisplayName);
                if (_drives.Count > 0) CmbDrive.SelectedIndex = 0;
                else CmbDrive.Items.Add("No drives found");
            });
        });
    }

    // -----------------------------------------------------------------------
    // Event handlers
    // -----------------------------------------------------------------------
    private void OnEjectClick(object? sender, RoutedEventArgs e)
    {
        var driveIdx = CmbDrive.SelectedIndex;
        if (driveIdx < 0 || driveIdx >= _drives.Count)
        {
            Log("❌ Please select a drive."); return;
        }

        var drive = _drives[driveIdx];
        try
        {
            BurnService.EjectDisc(drive.DevicePath);
            Log($"⏏ Tray ejected: {drive.DevicePath}");
        }
        catch (Exception ex)
        {
            Log($"❌ Eject failed: {ex.Message}");
        }
    }

    private void OnLoadTrayClick(object? sender, RoutedEventArgs e)
    {
        var driveIdx = CmbDrive.SelectedIndex;
        if (driveIdx < 0 || driveIdx >= _drives.Count)
        {
            Log("❌ Please select a drive."); return;
        }

        var drive = _drives[driveIdx];
        try
        {
            BurnService.LoadTray(drive.DevicePath);
            Log($"📥 Tray loaded: {drive.DevicePath}");
        }
        catch (Exception ex)
        {
            Log($"❌ Load tray failed: {ex.Message}");
        }
    }

    private async void OnBlankQuickClick(object? sender, RoutedEventArgs e) =>
        await BlankDiscAsync(quickBlank: true);

    private async void OnBlankFullClick(object? sender, RoutedEventArgs e) =>
        await BlankDiscAsync(quickBlank: false);

    private async void OnFormatQuickClick(object? sender, RoutedEventArgs e) =>
        await FormatDiscAsync(quickFormat: true);

    private async void OnFormatFullClick(object? sender, RoutedEventArgs e) =>
        await FormatDiscAsync(quickFormat: false);

    /// <summary>Checks whether the media type supports blanking/erasing.</summary>
    private static bool IsBlankableMedia(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType)) return false;
        var mt = mediaType.ToUpperInvariant();
        // Rewritable media that can be blanked/erased
        return mt.Contains("CD-RW") || mt.Contains("CDRW")
            || mt.Contains("DVD-RW") || mt.Contains("DVDRW")
            || mt.Contains("DVD+RW") || mt.Contains("DVDPLUSRW")
            || mt.Contains("DVD-RAM") || mt.Contains("DVDRAM")
            || mt.Contains("BD-RE") || mt.Contains("BDRE")
            || mt.Contains("HD DVD-RW") || mt.Contains("HDDVD-RW")
            || mt.Contains("HD DVD-RAM") || mt.Contains("HDDVD-RAM");
    }

    /// <summary>Checks whether the media type supports formatting.</summary>
    private static bool IsFormattableMedia(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType)) return false;
        var mt = mediaType.ToUpperInvariant();
        // Media types that support FORMAT UNIT command.
        return mt.Contains("DVD+RW") || mt.Contains("DVD-RAM") || mt.Contains("DVDRAM")
            || mt.Contains("DVD-RW") || mt.Contains("DVDRW")
            || mt.Contains("BD-RE") || mt.Contains("BDRE")
            || IsBluRayRecordable(mt)
            || mt.Contains("HD DVD-RW") || mt.Contains("HDDVD-RW")
            || mt.Contains("HD DVD-RAM") || mt.Contains("HDDVD-RAM");
    }

    /// <summary>
    /// Returns true if the media type string represents BD-R (write-once Blu-ray),
    /// excluding BD-ROM (read-only) and BD-RE (rewritable, checked separately).
    /// Both "BD-ROM" and "BD-RE" contain "BD-R" as a substring, so naive Contains
    /// checks would produce false positives.
    /// </summary>
    private static bool IsBluRayRecordable(string mtUpper)
    {
        return (mtUpper.Contains("BD-R") || mtUpper.Contains("BDR"))
            && !mtUpper.Contains("BD-ROM") && !mtUpper.Contains("BDROM")
            && !mtUpper.Contains("BD-RE") && !mtUpper.Contains("BDRE");
    }

    private async Task BlankDiscAsync(bool quickBlank)
    {
        var driveIdx = CmbDrive.SelectedIndex;
        if (driveIdx < 0 || driveIdx >= _drives.Count)
        {
            Log("❌ Please select a drive."); return;
        }

        var drive = _drives[driveIdx];

        // Re-probe media type freshly via SCSI before checking blankability.
        // The cached CurrentMedia may be stale after eject/close operations.
        Log("🔍 Probing current media...");
        var mediaType = await Task.Run(() =>
        {
            try
            {
                using var optDrive = new Native.Optical.OpticalDrive(drive.DevicePath);
                if (!optDrive.WaitForDriveReady())
                    return (string?)null;
                var profile = optDrive.GetCurrentProfile();
                if (profile == 0) return null;
                // Update cached drive info for display purposes
                var mt = Native.Optical.OpticalDrive.ProfileToMediaType(profile);
                drive.CurrentMedia ??= new DiscMedia();
                drive.CurrentMedia.MediaType = mt;
                return mt;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AdvancedView] Blank media probe failed: {ex.Message}");
                return drive.CurrentMedia?.MediaType;
            }
        });

        if (!IsBlankableMedia(mediaType))
        {
            Log($"⚠ Blanking is only supported for rewritable media (CD-RW, DVD±RW, DVD-RAM, BD-RE, HD DVD-RW/RAM). Current media: {mediaType ?? "None"}");
            return;
        }

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        SetRunning(true);
        OperationProgressBar.Value = 0;
        TxtOperationPct.Text = "0%";
        TxtOperationStatus.Text = quickBlank ? "Quick blanking..." : "Full blanking...";
        Log($"▶ {(quickBlank ? "Quick" : "Full")} blanking: {drive.DevicePath}");

        var progress = new Progress<BurnProgress>(p =>
        {
            OperationProgressBar.Value = p.PercentComplete;
            TxtOperationPct.Text = $"{p.PercentComplete}%";
            if (p.Remaining.TotalSeconds > 0)
                TxtOperationETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)} | ETA: {FormatHelper.FormatTimeSpan(p.Remaining)}";
            else if (p.Elapsed.TotalSeconds > 0)
                TxtOperationETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)}";
            if (!string.IsNullOrWhiteSpace(p.StatusMessage))
                TxtOperationStatus.Text = p.StatusMessage;
            if (!string.IsNullOrWhiteSpace(p.LogLine))
                Log(p.LogLine);
        });

        try
        {
            await Task.Run(() => _burnService.BlankDiscAsync(drive.DevicePath, quickBlank, mediaType, progress, _cts.Token));
            OperationProgressBar.Value = 100;
            TxtOperationPct.Text = "100%";
            TxtOperationStatus.Text = "Blank complete.";
            Log("✅ Disc blanked successfully.");
        }
        catch (OperationCanceledException)
        {
            TxtOperationStatus.Text = "Cancelled.";
            Log("⏹ Blank cancelled.");
        }
        catch (Exception ex)
        {
            TxtOperationStatus.Text = "Blank failed.";
            Log($"❌ Blank failed: {ex.Message}");
        }
        finally
        {
            SetRunning(false);
        }
    }

    private async Task FormatDiscAsync(bool quickFormat)
    {
        var driveIdx = CmbDrive.SelectedIndex;
        if (driveIdx < 0 || driveIdx >= _drives.Count)
        {
            Log("❌ Please select a drive."); return;
        }

        var drive = _drives[driveIdx];

        // Re-probe media type freshly via SCSI before checking formattability.
        // The cached CurrentMedia may be stale after eject/close operations.
        Log("🔍 Probing current media...");
        var mediaType = await Task.Run(() =>
        {
            try
            {
                using var optDrive = new Native.Optical.OpticalDrive(drive.DevicePath);
                if (!optDrive.WaitForDriveReady())
                    return (string?)null;
                var profile = optDrive.GetCurrentProfile();
                if (profile == 0) return null;
                var mt = Native.Optical.OpticalDrive.ProfileToMediaType(profile);
                drive.CurrentMedia ??= new DiscMedia();
                drive.CurrentMedia.MediaType = mt;
                return mt;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AdvancedView] Format media probe failed: {ex.Message}");
                return drive.CurrentMedia?.MediaType;
            }
        });

        if (!IsFormattableMedia(mediaType))
        {
            Log($"⚠ Formatting is only supported for DVD+RW, DVD-RW, DVD-RAM, BD-R, BD-RE, HD DVD-RW/RAM media. Current media: {mediaType ?? "None"}");
            return;
        }

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        SetRunning(true);
        OperationProgressBar.Value = 0;
        TxtOperationPct.Text = "0%";
        TxtOperationStatus.Text = quickFormat ? "Quick formatting..." : "Full formatting...";
        Log($"▶ {(quickFormat ? "Quick" : "Full")} formatting: {drive.DevicePath}");

        var progress = new Progress<BurnProgress>(p =>
        {
            OperationProgressBar.Value = p.PercentComplete;
            TxtOperationPct.Text = $"{p.PercentComplete}%";
            if (p.Remaining.TotalSeconds > 0)
                TxtOperationETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)} | ETA: {FormatHelper.FormatTimeSpan(p.Remaining)}";
            else if (p.Elapsed.TotalSeconds > 0)
                TxtOperationETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)}";
            if (!string.IsNullOrWhiteSpace(p.StatusMessage))
                TxtOperationStatus.Text = p.StatusMessage;
            if (!string.IsNullOrWhiteSpace(p.LogLine))
                Log(p.LogLine);
        });

        try
        {
            await Task.Run(() => _burnService.FormatDiscAsync(drive.DevicePath, quickFormat, progress, _cts.Token));
            OperationProgressBar.Value = 100;
            TxtOperationPct.Text = "100%";
            TxtOperationStatus.Text = "Format complete.";
            Log("✅ Disc formatted successfully.");
        }
        catch (OperationCanceledException)
        {
            TxtOperationStatus.Text = "Cancelled.";
            Log("⏹ Format cancelled.");
        }
        catch (Exception ex)
        {
            TxtOperationStatus.Text = "Format failed.";
            Log($"❌ Format failed: {ex.Message}");
        }
        finally
        {
            SetRunning(false);
        }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------
    private void SetRunning(bool running)
    {
        BtnEject.IsEnabled = !running;
        BtnLoadTray.IsEnabled = !running;
        BtnBlankQuick.IsEnabled = !running;
        BtnBlankFull.IsEnabled  = !running;
        BtnFormatQuick.IsEnabled = !running;
        BtnFormatFull.IsEnabled = !running;
        BtnCancelOp.IsEnabled = running;
        BtnRefreshDrives.IsEnabled = !running;
        CmbDrive.IsEnabled = !running;
    }

    private void OnCancelOpClick(object? sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        BtnCancelOp.IsEnabled = false;
        Log("Cancelling...");
    }

    private void OnRefreshDrivesClick(object? sender, RoutedEventArgs e) => LoadDrives();

    private void OnClearLogClick(object? sender, RoutedEventArgs e) =>
        TxtLog.Text = string.Empty;

    private void Log(string msg)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            LogCore(msg);
        }
        else
        {
            Dispatcher.UIThread.Post(() => LogCore(msg));
        }
    }

    private void LogCore(string msg)
    {
        TxtLog.Text += $"[{DateTime.Now:HH:mm:ss}] {msg}\n";
        var text = TxtLog.Text;
        if (text != null && text.Length > 30_000)
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
