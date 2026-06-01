// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using OpenBurningSuite.Helpers;
using OpenBurningSuite.Models;
using OpenBurningSuite.Services;

namespace OpenBurningSuite.Views;

public partial class CopyWizardView : UserControl
{
    private readonly ReadService _readService;
    private readonly DiscDiscoveryService _discovery;
    private CancellationTokenSource? _cts;
    private List<DiscDrive> _drives = new();
    private int _currentStep = 1;

    public CopyWizardView()
    {
        InitializeComponent();
        _readService = new ReadService();
        _discovery = new DiscDiscoveryService();

        PopulateComboBoxes();
        LoadDrives();

        CmbCopyDrive.SelectionChanged += (_, _) => UpdateDriveInfo();

        ChkCopyDecryptPs3.IsCheckedChanged += (_, _) =>
            PnlCopyIrd.IsVisible = ChkCopyDecryptPs3.IsChecked == true;
    }

    // -----------------------------------------------------------------------
    // Initialisation
    // -----------------------------------------------------------------------
    private void PopulateComboBoxes()
    {
        foreach (var fmt in FormatHelper.ReadOutputFormats)
            CmbCopyFormat.Items.Add(fmt);
        CmbCopyFormat.SelectedIndex = 0;

        foreach (var s in FormatHelper.ReadSpeeds)
            CmbCopySpeed.Items.Add(s);
        CmbCopySpeed.SelectedIndex = 0;

        CmbCopyGamingPreset.Items.Add("None (standard copy)");
        foreach (var sys in FormatHelper.GamingPresets.Keys)
            CmbCopyGamingPreset.Items.Add(sys);
        CmbCopyGamingPreset.SelectedIndex = 0;

        ApplySettingsDefaults();
    }

    /// <summary>Applies user's saved default settings to the copy wizard UI controls.</summary>
    private void ApplySettingsDefaults()
    {
        var s = SettingsService.Current;

        // Output format — migrate legacy names
        var defaultOutputFormat = s.DefaultOutputFormat;
        if (defaultOutputFormat == "CCD/IMG") defaultOutputFormat = "CCD/IMG/SUB";
        for (int i = 0; i < CmbCopyFormat.Items.Count; i++)
        {
            if (string.Equals(CmbCopyFormat.Items[i]?.ToString(), defaultOutputFormat, StringComparison.OrdinalIgnoreCase))
            {
                CmbCopyFormat.SelectedIndex = i;
                break;
            }
        }

        // Read speed
        for (int i = 0; i < CmbCopySpeed.Items.Count; i++)
        {
            if (string.Equals(CmbCopySpeed.Items[i]?.ToString(), s.DefaultReadSpeed, StringComparison.OrdinalIgnoreCase))
            {
                CmbCopySpeed.SelectedIndex = i;
                break;
            }
        }
    }

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
                CmbCopyDrive.ItemsSource = _drives.Select(d => d.DisplayName).ToList();
                if (_drives.Count > 0)
                {
                    CmbCopyDrive.SelectedIndex = 0;
                    UpdateDriveInfo();
                }
            });
        });
    }

    private void UpdateDriveInfo()
    {
        var idx = CmbCopyDrive.SelectedIndex;
        if (idx < 0 || idx >= _drives.Count) return;
        var drive = _drives[idx];
        TxtCopyDriveInfo.Text = $"📀 {drive.DriveModel} ({drive.DriveType})";
        TxtCopyMediaInfo.Text = drive.CurrentMedia != null
            ? $"Media: {drive.CurrentMedia.MediaType} — {drive.CurrentMedia.CapacityFormatted}"
            : "No media detected — insert a disc and click Refresh";

        // Detect content from the physical disc and apply copy presets
        if (drive.CurrentMedia != null)
        {
            var devicePath = drive.DevicePath ?? string.Empty;
            Task.Run(() =>
            {
                try
                {
                    using var optDrive = new Native.Optical.OpticalDrive(devicePath);
                    return DiscContentDetectionService.DetectFromDrive(optDrive);
                }
                catch { return new DiscContentInfo(); }
            }).ContinueWith(t =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (t.IsCompletedSuccessfully && t.Result.ContentType != DiscContentType.Unknown)
                        ShowCopyDetectedContent(t.Result);
                    else
                        HideCopyDetectedContent();
                });
            });
        }
        else
        {
            HideCopyDetectedContent();
        }
    }

    /// <summary>
    /// Shows detected content and applies copy presets based on content type.
    /// Automatically selects the best output format, gaming preset, and read settings.
    /// </summary>
    private void ShowCopyDetectedContent(DiscContentInfo content)
    {
        PnlCopyDetectedContent.IsVisible = true;
        TxtCopyDetectedContent.Text = $"🔍 {content.ContentLabel}";

        // Apply recommended output format
        var format = DiscContentDetectionService.GetRecommendedOutputFormat(content.ContentType);
        for (int i = 0; i < CmbCopyFormat.Items.Count; i++)
        {
            if (CmbCopyFormat.Items[i]?.ToString()?.Contains(format, StringComparison.OrdinalIgnoreCase) == true)
            {
                CmbCopyFormat.SelectedIndex = i;
                break;
            }
        }

        // Apply gaming preset if detected
        var presetName = DiscContentDetectionService.GetMatchingPresetName(content.ContentType);
        if (presetName != null)
        {
            for (int i = 0; i < CmbCopyGamingPreset.Items.Count; i++)
            {
                if (CmbCopyGamingPreset.Items[i]?.ToString()?.Contains(presetName, StringComparison.OrdinalIgnoreCase) == true)
                {
                    CmbCopyGamingPreset.SelectedIndex = i;
                    break;
                }
            }
        }

        // Show PS3 decrypt option if PS3 game detected
        if (content.ContentType == DiscContentType.PlayStation3Game && content.IsEncrypted)
        {
            ChkCopyDecryptPs3.IsChecked = true;
        }

        // Set preset description
        TxtCopyDetectedPreset.Text = DiscContentDetectionService.GetCopyPresetDescription(content.ContentType) ?? "";
    }

    /// <summary>Hides the copy detected content panel.</summary>
    private void HideCopyDetectedContent()
    {
        PnlCopyDetectedContent.IsVisible = false;
        TxtCopyDetectedContent.Text = "—";
        TxtCopyDetectedPreset.Text = "";
    }

    // -----------------------------------------------------------------------
    // Navigation
    // -----------------------------------------------------------------------
    private void GoToStep(int step)
    {
        _currentStep = step;
        PnlCopyStep1.IsVisible = step == 1;
        PnlCopyStep2.IsVisible = step == 2;
        PnlCopyStep3.IsVisible = step == 3;
        BtnCopyBack.IsVisible = step > 1;
        BtnCopyCancel.IsVisible = step == 3;
        BtnCopyNext.Content = step == 2 ? "Start Copy →" : "Next →";
        BtnCopyNext.IsVisible = step < 3;

        TxtCopyStep1Ind.Foreground = new Avalonia.Media.SolidColorBrush(
            step >= 1 ? Avalonia.Media.Color.Parse("#5BC0FF") : Avalonia.Media.Color.Parse("#5A7A9A"));
        TxtCopyStep2Ind.Foreground = new Avalonia.Media.SolidColorBrush(
            step >= 2 ? Avalonia.Media.Color.Parse("#5BC0FF") : Avalonia.Media.Color.Parse("#5A7A9A"));
        TxtCopyStep3Ind.Foreground = new Avalonia.Media.SolidColorBrush(
            step >= 3 ? Avalonia.Media.Color.Parse("#5BC0FF") : Avalonia.Media.Color.Parse("#5A7A9A"));
    }

    private void OnCopyBackClick(object? sender, RoutedEventArgs e)
    {
        if (_currentStep > 1) GoToStep(_currentStep - 1);
    }

    private async void OnCopyNextClick(object? sender, RoutedEventArgs e)
    {
        if (_currentStep == 1)
        {
            if (CmbCopyDrive.SelectedIndex < 0)
            {
                AppendLog("⚠ Please select a drive first.");
                return;
            }
            GoToStep(2);
        }
        else if (_currentStep == 2)
        {
            if (string.IsNullOrWhiteSpace(TxtCopyOutputPath.Text))
            {
                AppendLog("⚠ Please specify an output file path.");
                return;
            }
            // Validate output directory exists
            var outputDir = Path.GetDirectoryName(TxtCopyOutputPath.Text.Trim());
            if (!string.IsNullOrWhiteSpace(outputDir) && !Directory.Exists(outputDir))
            {
                AppendLog($"⚠ Output directory does not exist: {outputDir}");
                return;
            }
            GoToStep(3);
            await ExecuteCopyAsync();
        }
    }

    private void OnCopyCancelClick(object? sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
    }

    // -----------------------------------------------------------------------
    // Event handlers
    // -----------------------------------------------------------------------
    private void OnCopyRefreshDrivesClick(object? sender, RoutedEventArgs e) => LoadDrives();

    private async void OnCopyBrowseOutputClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var fmt = CmbCopyFormat.SelectedItem?.ToString() ?? "ISO";
        var ext = fmt switch
        {
            "BIN/CUE" => "bin",
            "BIN/CUE/SBI" => "bin",
            "TOC/BIN" => "bin",
            "NRG" => "nrg",
            "MDF/MDS" => "mdf",
            "IMG" => "img",
            "CDI" => "cdi",
            "CCD/IMG/SUB" => "ccd",
            "VCD" => "bin",
            "SVCD" => "bin",
            "XSVCD" => "bin",
            "XISO (Xbox)" => "iso",
            _ => "iso"
        };

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Disc Image As",
            DefaultExtension = ext,
            SuggestedFileName = $"disc_copy.{ext}"
        });
        if (file != null)
            TxtCopyOutputPath.Text = file.Path.LocalPath;
    }

    private async void OnCopyBrowseIrdClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select IRD Key File",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("IRD Files") { Patterns = new[] { "*.ird" } },
                FilePickerFileTypes.All
            }
        });
        if (files.Count > 0)
            TxtCopyIrdPath.Text = files[0].Path.LocalPath;
    }

    // -----------------------------------------------------------------------
    // Execution
    // -----------------------------------------------------------------------
    private async Task ExecuteCopyAsync()
    {
        var driveIdx = CmbCopyDrive.SelectedIndex;
        if (driveIdx < 0 || driveIdx >= _drives.Count) return;
        var drive = _drives[driveIdx];

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        // Reset progress UI for a fresh operation
        PrgCopyExec.Value = 0;
        TxtCopyExecPercent.Text = "0%";
        TxtCopyExecSpeed.Text = string.Empty;
        TxtCopyExecStatus.Text = "Preparing...";
        TxtCopyExecETA.Text = string.Empty;
        TxtCopyExecLog.Text = string.Empty;

        // Reset disc visualization for a fresh operation
        DiscVizBorder.IsVisible = false;
        DiscViz.IsActive = false;
        DiscViz.IsCompleted = false;
        DiscViz.VerifyPassed = null;
        DiscViz.BadSectors = 0;
        DiscViz.PercentComplete = 0;
        DiscViz.CurrentLba = 0;
        DiscViz.TotalSectors = 0;

        // Configure layer visualization from the inserted media type
        DiscViz.ConfigureForMedia(drive.CurrentMedia?.MediaType);

        var gamingPreset = CmbCopyGamingPreset.SelectedIndex > 0
            ? CmbCopyGamingPreset.SelectedItem?.ToString() ?? ""
            : "";

        var job = new ReadJob
        {
            DevicePath = drive.DevicePath,
            OutputPath = TxtCopyOutputPath.Text ?? "",
            OutputFormat = CmbCopyFormat.SelectedItem?.ToString() ?? "ISO",
            ReadSpeed = CmbCopySpeed.SelectedItem?.ToString() ?? "Max",
            ErrorRecovery = ChkCopyRetry.IsChecked == true ? "Yes" : "No",
            RetryCount = SettingsService.Current.DefaultReadRetryCount,
            GamingPreset = gamingPreset,
            DecryptPs3 = ChkCopyDecryptPs3.IsChecked == true,
            IrdFilePath = TxtCopyIrdPath.Text ?? ""
        };

        // Apply gaming preset parameters (sector size, subchannel, audio paranoia, etc.)
        // after creating the job so preset values override the defaults above.
        if (!string.IsNullOrEmpty(gamingPreset))
            GamingDiscService.ApplyReadPreset(job, gamingPreset);

        AppendLog($"[Info] Starting disc copy: {drive.DisplayName} → {job.OutputPath}");
        AppendLog($"[Info] Format: {job.OutputFormat}, Speed: {job.ReadSpeed}");

        var progress = new Progress<ReadProgress>(p =>
        {
            // Progress<T> already marshals callbacks to the UI thread via
            // the captured SynchronizationContext — no need for Dispatcher.UIThread.Post.
            PrgCopyExec.Value = p.PercentComplete;
            TxtCopyExecPercent.Text = $"{p.PercentComplete}%";
            if (p.CurrentSpeedX > 0)
                TxtCopyExecSpeed.Text = $"{p.CurrentSpeedX:F1}x";
            if (!string.IsNullOrEmpty(p.StatusMessage))
                TxtCopyExecStatus.Text = p.StatusMessage;
            if (p.Remaining.TotalSeconds > 0)
                TxtCopyExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)} | ETA: {FormatHelper.FormatTimeSpan(p.Remaining)}";
            else if (p.Elapsed.TotalSeconds > 0)
                TxtCopyExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)}";
            if (!string.IsNullOrEmpty(p.LogLine))
                AppendLog(p.LogLine);

            // Update disc visualization
            if (p.TotalSectors > 0)
            {
                DiscVizBorder.IsVisible = true;
                DiscViz.CurrentLba = (uint)p.SectorsRead;
                DiscViz.TotalSectors = (uint)p.TotalSectors;
                DiscViz.PercentComplete = p.PercentComplete;
                DiscViz.CurrentSpeedX = p.CurrentSpeedX;
                DiscViz.OperationMode = DiscOperationMode.Reading;
                DiscViz.IsActive = true;
                DiscViz.IsCompleted = false;
                DiscViz.UpdateVisualization();
            }
        });

        try
        {
            await Task.Run(() => _readService.ReadDiscAsync(job, progress, ct));
            AppendLog("✅ Disc copy complete!");
            TxtCopyExecStatus.Text = "Copy complete! 🎉";
            PrgCopyExec.Value = 100;
            TxtCopyExecPercent.Text = "100%";

            DiscViz.PercentComplete = 100;
            DiscViz.IsCompleted = true;
            DiscViz.IsActive = false;
            DiscViz.UpdateVisualization();

            // Verify after copy if requested
            if (ChkCopyVerify.IsChecked == true && File.Exists(job.OutputPath))
            {
                AppendLog("[Info] Verifying copied image...");
                TxtCopyExecStatus.Text = "Verifying...";
                var verifyService = new VerifyService();
                var verifyJob = new VerifyJob
                {
                    VerifyMode = "Compare Disc to Image",
                    DevicePath = drive.DevicePath,
                    ImagePath = job.OutputPath,
                    ChecksumType = "SHA256"
                };
                var verifyProgress = new Progress<VerifyProgress>(p =>
                {
                    // Progress<T> already marshals to the UI thread
                    if (!string.IsNullOrEmpty(p.StatusMessage))
                        TxtCopyExecStatus.Text = p.StatusMessage;
                    if (!string.IsNullOrEmpty(p.LogLine))
                        AppendLog(p.LogLine);
                });
                await Task.Run(() => verifyService.VerifyAsync(verifyJob, verifyProgress, ct));
                if (verifyJob.Result is { Passed: true })
                {
                    AppendLog("✅ Verification passed — disc matches image.");
                    TxtCopyExecStatus.Text = "Copy & verify complete! ✅";
                }
                else
                {
                    AppendLog($"❌ Verification FAILED: {verifyJob.Result?.ErrorSummary ?? "Unknown error"}");
                    TxtCopyExecStatus.Text = "Copy done, verification FAILED! ❌";
                }
            }
        }
        catch (OperationCanceledException)
        {
            AppendLog("⏹ Copy cancelled.");
            TxtCopyExecStatus.Text = "Cancelled.";
            DiscViz.IsActive = false;
            DiscViz.IsCompleted = false;
            DiscViz.UpdateVisualization();
        }
        catch (Exception ex)
        {
            AppendLog($"❌ Copy failed: {ex.Message}");
            TxtCopyExecStatus.Text = "Copy failed.";
            DiscViz.IsActive = false;
            DiscViz.IsCompleted = false;
            DiscViz.UpdateVisualization();
        }
        finally
        {
            BtnCopyCancel.IsVisible = false;
            BtnCopyBack.IsVisible = true;
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
        var existing = TxtCopyExecLog.Text ?? string.Empty;
        TxtCopyExecLog.Text = existing + $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n";
        var text = TxtCopyExecLog.Text;
        if (text != null && text.Length > 30_000)
        {
            var trimIdx = text.IndexOf('\n', text.Length - 20_000);
            TxtCopyExecLog.Text = trimIdx > 0
                ? "...\n" + text[(trimIdx + 1)..]
                : "...\n" + text[(text.Length - 20_000)..];
        }
        TxtCopyExecLog.CaretIndex = TxtCopyExecLog.Text?.Length ?? 0;
    }
}
