// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using OpenBurningSuite.Helpers;
using OpenBurningSuite.Models;
using OpenBurningSuite.Native.Optical;
using OpenBurningSuite.Services;

namespace OpenBurningSuite.Views;

public partial class BlankWizardView : UserControl
{
    private readonly DiscDiscoveryService _discovery;
    private readonly BurnEngine _engine = new();
    private CancellationTokenSource? _cts;
    private List<DiscDrive> _drives = new();
    private int _currentStep = 1;

    public BlankWizardView()
    {
        InitializeComponent();
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
                if (t.IsFaulted || t.IsCanceled)
                {
                    _drives = new List<DiscDrive>();
                    return;
                }
                _drives = t.Result;
                CmbBlankDrive.ItemsSource = _drives.Select(d => d.DisplayName).ToList();
                if (_drives.Count > 0)
                {
                    CmbBlankDrive.SelectedIndex = 0;
                    UpdateDriveInfo();
                }
            });
        });
    }

    private void UpdateDriveInfo()
    {
        var idx = CmbBlankDrive.SelectedIndex;
        if (idx < 0 || idx >= _drives.Count) return;
        var drive = _drives[idx];
        TxtBlankDriveInfo.Text = $"📀 {drive.DriveModel} ({drive.DriveType})";

        if (drive.CurrentMedia != null)
        {
            TxtBlankMediaInfo.Text = $"Media: {drive.CurrentMedia.MediaType} — " +
                                     $"State: {drive.CurrentMedia.DiscState} — " +
                                     $"Capacity: {drive.CurrentMedia.CapacityFormatted}";

            // Show warnings for non-erasable media
            var mediaType = drive.CurrentMedia.MediaType?.ToUpperInvariant() ?? "";
            if (mediaType.Contains("CD-R") && !mediaType.Contains("CD-RW"))
                TxtBlankWarning.Text = "⚠ CD-R is write-once media — it cannot be erased.";
            else if (mediaType.Contains("DVD-R") && !mediaType.Contains("DVD-RW") && !mediaType.Contains("DVD-RAM"))
                TxtBlankWarning.Text = "⚠ DVD-R is write-once media — it cannot be erased.";
            else if (mediaType.Contains("BD-R") && !mediaType.Contains("BD-RE"))
                TxtBlankWarning.Text = "ℹ BD-R is write-once but can be formatted for initial use.";
            else
                TxtBlankWarning.Text = "";
        }
        else
        {
            TxtBlankMediaInfo.Text = "No media detected — insert a rewritable disc and click Refresh";
            TxtBlankWarning.Text = "";
        }
    }

    // -----------------------------------------------------------------------
    // Navigation
    // -----------------------------------------------------------------------
    private void GoToStep(int step)
    {
        _currentStep = step;
        PnlBlankStep1.IsVisible = step == 1;
        PnlBlankStep2.IsVisible = step == 2;
        PnlBlankStep3.IsVisible = step == 3;
        BtnBlankBack.IsVisible = step > 1 && step < 3;
        BtnBlankCancel.IsVisible = step == 3;
        BtnBlankNext.Content = step == 2 ? "Start Erase →" : "Next →";
        BtnBlankNext.IsVisible = step < 3;

        TxtBlankStep1Ind.Foreground = new Avalonia.Media.SolidColorBrush(
            step >= 1 ? Avalonia.Media.Color.Parse("#5BC0FF") : Avalonia.Media.Color.Parse("#5A7A9A"));
        TxtBlankStep2Ind.Foreground = new Avalonia.Media.SolidColorBrush(
            step >= 2 ? Avalonia.Media.Color.Parse("#5BC0FF") : Avalonia.Media.Color.Parse("#5A7A9A"));
        TxtBlankStep3Ind.Foreground = new Avalonia.Media.SolidColorBrush(
            step >= 3 ? Avalonia.Media.Color.Parse("#5BC0FF") : Avalonia.Media.Color.Parse("#5A7A9A"));
    }

    private void OnBlankBackClick(object? sender, RoutedEventArgs e)
    {
        if (_currentStep > 1) GoToStep(_currentStep - 1);
    }

    private async void OnBlankNextClick(object? sender, RoutedEventArgs e)
    {
        if (_currentStep == 1)
        {
            if (CmbBlankDrive.SelectedIndex < 0)
            {
                AppendLog("⚠ Please select a drive first.");
                return;
            }
            GoToStep(2);
        }
        else if (_currentStep == 2)
        {
            GoToStep(3);
            await ExecuteBlankAsync();
        }
    }

    private void OnBlankCancelClick(object? sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
    }

    // -----------------------------------------------------------------------
    // Event handlers
    // -----------------------------------------------------------------------
    private void OnBlankRefreshClick(object? sender, RoutedEventArgs e) => LoadDrives();

    private void OnBlankDriveSelChanged(object? sender, SelectionChangedEventArgs e) => UpdateDriveInfo();

    // -----------------------------------------------------------------------
    // Execution
    // -----------------------------------------------------------------------
    private async Task ExecuteBlankAsync()
    {
        var driveIdx = CmbBlankDrive.SelectedIndex;
        if (driveIdx < 0 || driveIdx >= _drives.Count) return;
        var drive = _drives[driveIdx];

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        bool quickBlank = RdoQuickBlank.IsChecked == true;
        var modeStr = quickBlank ? "quick" : "full";

        // Reset progress UI for a fresh operation
        PrgBlankExec.IsIndeterminate = true;
        PrgBlankExec.Value = 0;
        TxtBlankExecPercent.Text = "0%";
        TxtBlankExecLog.Text = string.Empty;

        AppendLog($"[Info] Starting {modeStr} erase on {drive.DisplayName}...");
        TxtBlankExecStatus.Text = $"Erasing ({modeStr})...";

        var progress = new Progress<BurnProgress>(p =>
        {
            if (p.PercentComplete > 0)
            {
                PrgBlankExec.IsIndeterminate = false;
                PrgBlankExec.Value = p.PercentComplete;
                TxtBlankExecPercent.Text = $"{p.PercentComplete}%";
            }
            if (p.Remaining.TotalSeconds > 0)
                TxtBlankExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)} | ETA: {FormatHelper.FormatTimeSpan(p.Remaining)}";
            else if (p.Elapsed.TotalSeconds > 0)
                TxtBlankExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)}";
            if (!string.IsNullOrEmpty(p.StatusMessage))
                TxtBlankExecStatus.Text = p.StatusMessage;
            if (!string.IsNullOrEmpty(p.LogLine))
                AppendLog(p.LogLine);
        });

        try
        {
            await Task.Run(() => _engine.BlankDiscAsync(
                drive.DevicePath, quickBlank, drive.CurrentMedia?.MediaType, progress, ct));

            AppendLog("✅ Erase complete! Disc is now blank.");
            TxtBlankExecStatus.Text = "Erase complete! 🎉";
            PrgBlankExec.IsIndeterminate = false;
            PrgBlankExec.Value = 100;
            TxtBlankExecPercent.Text = "100%";
        }
        catch (OperationCanceledException)
        {
            AppendLog("⏹ Erase cancelled.");
            TxtBlankExecStatus.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            AppendLog($"❌ Erase failed: {ex.Message}");
            TxtBlankExecStatus.Text = "Erase failed.";
        }
        finally
        {
            PrgBlankExec.IsIndeterminate = false;
            BtnBlankCancel.IsVisible = false;
            BtnBlankBack.IsVisible = true;
        }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------
    private void AppendLog(string message)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            AppendLogCore(message);
        }
        else
        {
            Dispatcher.UIThread.Post(() => AppendLogCore(message));
        }
    }

    private void AppendLogCore(string message)
    {
        var existing = TxtBlankExecLog.Text ?? string.Empty;
        TxtBlankExecLog.Text = existing + $"[{DateTime.Now:HH:mm:ss}] {message}\n";
        var text = TxtBlankExecLog.Text;
        if (text != null && text.Length > 30_000)
        {
            var trimIdx = text.IndexOf('\n', text.Length - 20_000);
            TxtBlankExecLog.Text = trimIdx > 0
                ? "...\n" + text[(trimIdx + 1)..]
                : "...\n" + text[(text.Length - 20_000)..];
        }
        TxtBlankExecLog.CaretIndex = TxtBlankExecLog.Text?.Length ?? 0;
    }
}
