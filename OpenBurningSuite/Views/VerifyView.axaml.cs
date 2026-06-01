// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using OpenBurningSuite.Helpers;
using OpenBurningSuite.Models;
using OpenBurningSuite.Services;

namespace OpenBurningSuite.Views;

public partial class VerifyView : UserControl
{
    private readonly VerifyService _verifyService;
    private readonly DiscDiscoveryService _discovery;
    private CancellationTokenSource? _cts;
    private List<DiscDrive> _drives = new();

    public VerifyView()
    {
        InitializeComponent();
        _verifyService = new VerifyService();
        _discovery = new DiscDiscoveryService();

        LoadDrives();
        UpdatePanelVisibility("Verify Disc");
        CmbVerifyMode.SelectedIndex = 0;

        // Drag-and-drop events are wired up in AXAML via DragDrop.DragOver/DragDrop.Drop attributes
        CmbChecksumType.SelectedIndex = 0;  // Default: SHA256

        ApplySettingsDefaults();

        // Wire up drive and image selection changes for content detection
        CmbDrive.SelectionChanged += OnDriveSelectionChanged;
        TxtImagePath.PropertyChanged += (_, e) =>
        {
            if (e.Property.Name == nameof(TextBox.Text))
                DetectImageContent();
        };
    }

    /// <summary>Applies user's saved default settings to the verification UI controls.</summary>
    private void ApplySettingsDefaults()
    {
        var s = SettingsService.Current;

        // Checksum algorithm — uses ComboBoxItem Content
        for (int i = 0; i < CmbChecksumType.Items.Count; i++)
        {
            if (CmbChecksumType.Items[i] is ComboBoxItem item &&
                string.Equals(item.Content?.ToString(), s.DefaultChecksumAlgorithm, StringComparison.OrdinalIgnoreCase))
            {
                CmbChecksumType.SelectedIndex = i;
                break;
            }
        }
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
    private void OnVerifyModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        var mode = (CmbVerifyMode.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Verify Disc";
        UpdatePanelVisibility(mode);
    }

    /// <summary>Handles drive selection changes to detect disc content.</summary>
    private void OnDriveSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var driveIdx = CmbDrive.SelectedIndex;
        if (driveIdx < 0 || driveIdx >= _drives.Count) return;

        var drive = _drives[driveIdx];
        var media = drive.CurrentMedia;
        if (media != null)
        {
            var devicePath = drive.DevicePath ?? string.Empty;

            Task.Run(() =>
            {
                try
                {
                    using var optDrive = new OpenBurningSuite.Native.Optical.OpticalDrive(devicePath);
                    return DiscContentDetectionService.DetectFromDrive(optDrive);
                }
                catch { return new DiscContentInfo(); }
            }).ContinueWith(t =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (t.IsCompletedSuccessfully && !string.IsNullOrWhiteSpace(t.Result.ContentLabel))
                        ShowDetectedContent(t.Result);
                    else
                        HideDetectedContent();
                });
            });
        }
        else
        {
            HideDetectedContent();
        }
    }

    /// <summary>Detects content from the image file path for preset application.</summary>
    private void DetectImageContent()
    {
        var path = TxtImagePath.Text;
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
        {
            HideDetectedContent();
            return;
        }

        Task.Run(() =>
        {
            try
            {
                return DiscContentDetectionService.DetectFromImage(path);
            }
            catch { return new DiscContentInfo(); }
        }).ContinueWith(t =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (t.IsCompletedSuccessfully && !string.IsNullOrWhiteSpace(t.Result.ContentLabel))
                    ShowDetectedContent(t.Result);
                else
                    HideDetectedContent();
            });
        });
    }

    /// <summary>
    /// Shows detected content and applies verification presets based on content type.
    /// Presets help users by automatically configuring the best verification settings
    /// for the detected content type, so they don't need to manually select options.
    /// </summary>
    private void ShowDetectedContent(DiscContentInfo content)
    {
        PnlDetectedContent.IsVisible = true;
        TxtDetectedContent.Text = content.ContentLabel;

        // Apply presets based on detected content type
        switch (content.ContentType)
        {
            // ---- PlayStation game discs ----
            case DiscContentType.PlayStation1Game:
                ChkAudioVerify.IsChecked = true;
                ChkVerifySubchannel.IsChecked = true;
                ChkSectorBySector.IsChecked = true;
                break;
            case DiscContentType.PlayStation2Game:
                ChkAudioVerify.IsChecked = false;
                ChkVerifySubchannel.IsChecked = false;
                ChkSectorBySector.IsChecked = true;
                break;
            case DiscContentType.PlayStation3Game:
            case DiscContentType.PlayStation4Game:
            case DiscContentType.PlayStation5Game:
                ChkAudioVerify.IsChecked = false;
                ChkVerifySubchannel.IsChecked = false;
                ChkSectorBySector.IsChecked = true;
                break;

            // ---- Sega game discs ----
            case DiscContentType.SegaSaturnGame:
            case DiscContentType.SegaDreamcastGame:
            case DiscContentType.SegaMegaCdGame:
                ChkAudioVerify.IsChecked = true;
                ChkVerifySubchannel.IsChecked = false;
                ChkSectorBySector.IsChecked = true;
                break;

            // ---- Nintendo & Xbox game discs ----
            case DiscContentType.GameCubeGame:
            case DiscContentType.WiiGame:
            case DiscContentType.WiiUGame:
            case DiscContentType.XboxGame:
            case DiscContentType.Xbox360Game:
            case DiscContentType.XboxOneGame:
            case DiscContentType.XboxSeriesGame:
            case DiscContentType.PspUmdGame:
                ChkAudioVerify.IsChecked = false;
                ChkVerifySubchannel.IsChecked = false;
                ChkSectorBySector.IsChecked = true;
                break;

            // ---- Retro CD-based game discs ----
            case DiscContentType.ThreeDOGame:
            case DiscContentType.NeoGeoCdGame:
            case DiscContentType.PcEngineGame:
            case DiscContentType.AmigaCd32Game:
            case DiscContentType.AmigaCdtvGame:
            case DiscContentType.AtariJaguarCdGame:
            case DiscContentType.CdInteractive:
            case DiscContentType.CdInteractiveGame:
                ChkAudioVerify.IsChecked = true;
                ChkVerifySubchannel.IsChecked = false;
                ChkSectorBySector.IsChecked = true;
                break;

            // ---- Audio discs ----
            case DiscContentType.AudioCd:
                ChkAudioVerify.IsChecked = true;
                ChkVerifySubchannel.IsChecked = false;
                ChkSectorBySector.IsChecked = true;
                break;
            case DiscContentType.MixedModeCd:
            case DiscContentType.EnhancedCd:
                ChkAudioVerify.IsChecked = true;
                ChkVerifySubchannel.IsChecked = false;
                ChkSectorBySector.IsChecked = true;
                break;

            // ---- Video discs ----
            case DiscContentType.VideoCd:
            case DiscContentType.SuperVideoCd:
                ChkAudioVerify.IsChecked = false;
                ChkVerifySubchannel.IsChecked = false;
                ChkSectorBySector.IsChecked = true;
                break;
            case DiscContentType.DvdVideo:
            case DiscContentType.DvdAudio:
            case DiscContentType.DvdVideoRecording:
                ChkAudioVerify.IsChecked = false;
                ChkVerifySubchannel.IsChecked = false;
                ChkSectorBySector.IsChecked = true;
                break;
            case DiscContentType.BluRayMovie:
            case DiscContentType.BluRay3D:
            case DiscContentType.UhdBluRay:
            case DiscContentType.BluRayAudio:
            case DiscContentType.BluRayAudioVisual:
            case DiscContentType.BluRayJava:
                ChkAudioVerify.IsChecked = false;
                ChkVerifySubchannel.IsChecked = false;
                ChkSectorBySector.IsChecked = true;
                break;

            // ---- Photo discs ----
            case DiscContentType.PhotoCd:
                ChkAudioVerify.IsChecked = false;
                ChkVerifySubchannel.IsChecked = false;
                ChkSectorBySector.IsChecked = true;
                break;
            case DiscContentType.PhotoDvd:
            case DiscContentType.PhotoBluRay:
                ChkAudioVerify.IsChecked = false;
                ChkVerifySubchannel.IsChecked = false;
                ChkSectorBySector.IsChecked = true;
                break;

            // ---- Data discs ----
            case DiscContentType.DataCd:
            case DiscContentType.DataDvd:
            case DiscContentType.DataBluRay:
            case DiscContentType.Data:
                ChkAudioVerify.IsChecked = false;
                ChkVerifySubchannel.IsChecked = false;
                ChkSectorBySector.IsChecked = true;
                break;

            default:
                break;
        }

        // Set preset description from the centralized helper
        TxtDetectedPreset.Text = DiscContentDetectionService.GetVerifyPresetDescription(content.ContentType) ?? "";
    }

    /// <summary>Hides the detected content panel.</summary>
    private void HideDetectedContent()
    {
        PnlDetectedContent.IsVisible = false;
        TxtDetectedContent.Text = "—";
        TxtDetectedPreset.Text = "";
    }

    private void UpdatePanelVisibility(string mode)
    {
        PnlDrive.IsVisible  = mode != "Check Image Integrity";
        PnlImage.IsVisible  = mode != "Verify Disc";

        // Audio track verification and subchannel verification only apply when a disc is involved
        ChkAudioVerify.IsVisible = mode != "Check Image Integrity";
        ChkVerifySubchannel.IsVisible = mode != "Check Image Integrity";
    }

    private async void OnBrowseImageClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Disc Image",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Disc Images")
                {
                    Patterns = new[] { "*.iso", "*.bin", "*.img", "*.nrg", "*.mdf", "*.mds", "*.cdi", "*.cue", "*.toc", "*.obse", "*.chd" }
                },
                new FilePickerFileType("All Files") { Patterns = new[] { "*.*" } }
            }
        });
        if (files.Count > 0) TxtImagePath.Text = files[0].Path.LocalPath;
    }

    private async void OnVerifyClick(object? sender, RoutedEventArgs e)
    {
        var mode = (CmbVerifyMode.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Verify Disc";

        var driveIdx = CmbDrive.SelectedIndex;
        string devicePath = (driveIdx >= 0 && driveIdx < _drives.Count)
            ? _drives[driveIdx].DevicePath : string.Empty;

        if (mode != "Check Image Integrity" && string.IsNullOrWhiteSpace(devicePath))
        {
            Log("❌ Select a drive."); return;
        }
        if (mode != "Verify Disc" && string.IsNullOrWhiteSpace(TxtImagePath.Text))
        {
            Log("❌ Select an image file."); return;
        }

        var job = new VerifyJob
        {
            VerifyMode       = mode,
            DevicePath       = devicePath,
            ImagePath        = TxtImagePath.Text ?? string.Empty,
            ChecksumType     = (CmbChecksumType.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "SHA256",
            SectorBySector   = ChkSectorBySector.IsChecked == true,
            VerifySubchannel = ChkVerifySubchannel.IsChecked == true,
            AudioVerification = ChkAudioVerify.IsChecked == true,
        };

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        SetRunning(true);
        ClearResults();

        // Reset progress UI and disc visualization for a fresh operation
        TxtVerifyStatus.Text = "Starting...";
        TxtETA.Text = string.Empty;
        DiscVizBorder.IsVisible = false;
        DiscViz.IsActive = false;
        DiscViz.IsCompleted = false;
        DiscViz.VerifyPassed = null;
        DiscViz.BadSectors = 0;
        DiscViz.PercentComplete = 0;
        DiscViz.CurrentLba = 0;
        DiscViz.TotalSectors = 0;

        // Configure layer visualization from the inserted media type (if a drive is selected)
        if (driveIdx >= 0 && driveIdx < _drives.Count)
            DiscViz.ConfigureForMedia(_drives[driveIdx].CurrentMedia?.MediaType);
        else
            DiscViz.ConfigureForMedia(null);

        Log($"▶ Starting verification: {mode}");

        var progress = new Progress<VerifyProgress>(p =>
        {
            VerifyProgressBar.Value = p.PercentComplete;
            TxtVerifyPct.Text = $"{p.PercentComplete}%";
            if (!string.IsNullOrWhiteSpace(p.StatusMessage))
                TxtVerifyStatus.Text = p.StatusMessage;
            if (p.Remaining.TotalSeconds > 0)
                TxtETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)} | ETA: {FormatHelper.FormatTimeSpan(p.Remaining)}";
            else if (p.Elapsed.TotalSeconds > 0)
                TxtETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)}";
            if (p.TotalSectors > 0)
            {
                TxtTotalSectors.Text = p.TotalSectors.ToString("N0");
                TxtGoodSectors.Text = p.GoodSectors.ToString("N0");
                TxtBadSectors.Text = p.BadSectors.ToString("N0");
            }
            if (!string.IsNullOrWhiteSpace(p.LogLine))
                Log(p.LogLine);

            // Update disc visualization
            DiscVizBorder.IsVisible = true;
            DiscViz.OperationMode = DiscOperationMode.Verifying;
            DiscViz.IsActive = true;
            DiscViz.IsCompleted = false;
            DiscViz.PercentComplete = p.PercentComplete;
            DiscViz.BadSectors = p.BadSectors;
            if (p.TotalSectors > 0)
                DiscViz.TotalSectors = p.TotalSectors;
            if (p.CurrentLba > 0)
                DiscViz.CurrentLba = p.CurrentLba;
            else if (p.SectorsVerified > 0)
                DiscViz.CurrentLba = p.SectorsVerified;
            DiscViz.UpdateVisualization();
        });

        try
        {
            await Task.Run(() => _verifyService.VerifyAsync(job, progress, _cts.Token));
            VerifyProgressBar.Value = 100;
            TxtVerifyPct.Text = "100%";

            var result = job.Result;
            if (result != null)
            {
                TxtTotalSectors.Text = result.TotalSectors.ToString("N0");
                TxtGoodSectors.Text  = result.GoodSectors.ToString("N0");
                TxtBadSectors.Text   = result.BadSectors.ToString("N0");
                TxtChecksum.Text     = $"{result.ChecksumType}: {result.ActualChecksum}";

                if (result.Passed)
                {
                    TxtVerifyResult.Text = "✅  PASSED";
                    TxtVerifyResult.Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse("#4CAF50"));
                    TxtVerifyStatus.Text = "Verification passed.";
                }
                else
                {
                    TxtVerifyResult.Text = "❌  FAILED";
                    TxtVerifyResult.Foreground = new SolidColorBrush(Avalonia.Media.Color.Parse("#F38BA8"));
                    TxtVerifyStatus.Text = result.ErrorSummary;
                }

                // Mark disc visualization as completed
                DiscViz.IsCompleted = true;
                DiscViz.IsActive = false;
                DiscViz.PercentComplete = 100;
                DiscViz.VerifyPassed = result.Passed;
                DiscViz.BadSectors = result.BadSectors;
                DiscViz.UpdateVisualization();

                Log(result.Passed ? $"✅ PASSED — {result.ActualChecksum}" : $"❌ FAILED — {result.ErrorSummary}");
            }
        }
        catch (OperationCanceledException)
        {
            TxtVerifyStatus.Text = "Cancelled.";
            Log("⏹ Verification cancelled.");
            DiscViz.IsActive = false;
            DiscViz.IsCompleted = false;
            DiscViz.UpdateVisualization();
        }
        catch (Exception ex)
        {
            TxtVerifyStatus.Text = "Failed.";
            Log($"❌ Verification failed: {ex.Message}");
            DiscViz.IsActive = false;
            DiscViz.IsCompleted = false;
            DiscViz.UpdateVisualization();
        }
        finally
        {
            SetRunning(false);
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        Log("Cancelling...");
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------
    private void ClearResults()
    {
        TxtTotalSectors.Text  = "—";
        TxtGoodSectors.Text   = "—";
        TxtBadSectors.Text    = "—";
        TxtChecksum.Text      = "—";
        TxtVerifyResult.Text  = string.Empty;
        VerifyProgressBar.Value = 0;
        TxtVerifyPct.Text     = "0%";
    }

    private void SetRunning(bool running)
    {
        BtnVerify.IsEnabled = !running;
        BtnCancel.IsEnabled = running;

        // Disable input controls during verification to prevent user from
        // changing settings mid-run which could cause inconsistent results
        CmbVerifyMode.IsEnabled = !running;
        CmbDrive.IsEnabled = !running;
        CmbChecksumType.IsEnabled = !running;
        TxtImagePath.IsEnabled = !running;
        BtnBrowseImage.IsEnabled = !running;
        ChkSectorBySector.IsEnabled = !running;
        ChkVerifySubchannel.IsEnabled = !running;
        ChkAudioVerify.IsEnabled = !running;
        BtnRefreshDrives.IsEnabled = !running;
    }

    private void OnImageDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = e.DataTransfer.TryGetFiles() is { } files && files.Any()
            ? DragDropEffects.Copy : DragDropEffects.None;

    private void OnImageDrop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles()?.ToList();
        if (files?.Count > 0)
            TxtImagePath.Text = files[0].Path.LocalPath;
    }

    private void OnClearLogClick(object? sender, RoutedEventArgs e) =>
        TxtLog.Text = string.Empty;

    private void OnRefreshDrivesClick(object? sender, RoutedEventArgs e) => LoadDrives();

    private async void OnCopyChecksumClick(object? sender, RoutedEventArgs e)
    {
        var text = TxtChecksum.Text;
        if (!string.IsNullOrWhiteSpace(text) && text != "—")
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(text);
                Log("📋 Checksum copied to clipboard.");
            }
        }
    }

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
        TxtLog.Text += $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n";
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