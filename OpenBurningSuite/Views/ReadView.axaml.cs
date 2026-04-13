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
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using OpenBurningSuite.Helpers;
using OpenBurningSuite.Models;
using OpenBurningSuite.Services;

namespace OpenBurningSuite.Views;

public partial class ReadView : UserControl
{
    private readonly ReadService _readService;
    private readonly DiscDiscoveryService _discovery;
    private CancellationTokenSource? _cts;
    private List<DiscDrive> _drives = new();

    /// <summary>Last detected content info for the currently selected drive.</summary>
    private DiscContentInfo? _detectedContent;

    public ReadView()
    {
        InitializeComponent();
        _readService = new ReadService();
        _discovery = new DiscDiscoveryService();

        PopulateComboBoxes();
        ApplySettingsDefaults();
        PopulatePresets();
        LoadDrives();

        // Wire up encryption checkbox visibility toggle
        ChkEncryptOutput.IsCheckedChanged += (_, _) =>
            PnlEncryptPassword.IsVisible = ChkEncryptOutput.IsChecked == true;

        // Wire up drive selection change to auto-detect content
        CmbDrive.SelectionChanged += OnDriveSelectionChanged;
    }

    // -----------------------------------------------------------------------
    // Initialisation
    // -----------------------------------------------------------------------
    private void PopulateComboBoxes()
    {
        foreach (var fmt in FormatHelper.ReadOutputFormats)
            CmbOutputFormat.Items.Add(fmt);
        CmbOutputFormat.SelectedIndex = 0;

        foreach (var s in FormatHelper.ReadSpeeds)
            CmbReadSpeed.Items.Add(s);
        CmbReadSpeed.SelectedIndex = 0;

        foreach (var sz in FormatHelper.SectorSizes)
            CmbSectorSize.Items.Add(sz);
        CmbSectorSize.SelectedIndex = 0; // 2048

        foreach (var sc in FormatHelper.SubchannelModes)
            CmbSubchannel.Items.Add(sc);
        CmbSubchannel.SelectedIndex = 0; // None

        CmbErrorRecovery.SelectedIndex = 0;
    }

    /// <summary>Applies user's saved default settings to the read/copy UI controls.</summary>
    private void ApplySettingsDefaults()
    {
        var s = SettingsService.Current;

        // Read speed
        SelectComboBoxItem(CmbReadSpeed, s.DefaultReadSpeed);

        // Output format — migrate legacy names
        var readOutputFormat = s.DefaultOutputFormat;
        if (readOutputFormat == "CCD/IMG") readOutputFormat = "CCD/IMG/SUB";
        SelectComboBoxItem(CmbOutputFormat, readOutputFormat);

        // Error recovery — uses ComboBoxItem with Tag
        SelectComboBoxItemByTag(CmbErrorRecovery, s.DefaultErrorRecovery);

        // Sector size
        SelectComboBoxItem(CmbSectorSize, s.DefaultSectorSize.ToString());

        // Subchannel mode
        SelectComboBoxItem(CmbSubchannel, s.DefaultSubchannelMode);

        // Retry count
        NumRetries.Value = s.DefaultReadRetryCount;

        // Checkboxes
        ChkAudioParanoia.IsChecked    = s.DefaultAudioParanoia;
        ChkJitterCorrection.IsChecked = s.DefaultJitterCorrection;
    }

    /// <summary>Selects the combo box item whose ToString() matches the value.</summary>
    private static void SelectComboBoxItem(ComboBox cmb, string value)
    {
        for (int i = 0; i < cmb.Items.Count; i++)
        {
            if (string.Equals(cmb.Items[i]?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                cmb.SelectedIndex = i;
                return;
            }
        }
    }

    /// <summary>Selects the combo box item whose Tag matches the value (for ComboBoxItem items).</summary>
    private static void SelectComboBoxItemByTag(ComboBox cmb, string value)
    {
        for (int i = 0; i < cmb.Items.Count; i++)
        {
            if (cmb.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                cmb.SelectedIndex = i;
                return;
            }
        }
    }

    private void PopulatePresets()
    {
        var items = new List<Button>();
        foreach (var kv in FormatHelper.GamingPresets)
        {
            var preset = kv.Value;
            var btn = new Button
            {
                Content = preset.Name,
                Classes = { "PresetBtn" },
                Tag = preset
            };
            btn.Click += OnPresetClick;
            items.Add(btn);
        }
        PresetsList.ItemsSource = items;
    }

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
    // Disc content detection
    // -----------------------------------------------------------------------

    /// <summary>
    /// Handles drive selection changes — triggers automatic content detection.
    /// </summary>
    private void OnDriveSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var driveIdx = CmbDrive.SelectedIndex;
        if (driveIdx < 0 || driveIdx >= _drives.Count) return;

        PnlDetectedContent.IsVisible = true;
        RunContentDetection(_drives[driveIdx], logResult: false);
    }

    /// <summary>
    /// Manual detect disc button click — re-runs content detection on the selected drive.
    /// </summary>
    private void OnDetectContentClick(object? sender, RoutedEventArgs e)
    {
        var driveIdx = CmbDrive.SelectedIndex;
        if (driveIdx < 0 || driveIdx >= _drives.Count)
        {
            Log("❌ Please select a drive first.");
            return;
        }

        PnlDetectedContent.IsVisible = true;
        TxtDetectedContentType.Text = "Detecting...";
        TxtDetectedVolumeLabel.Text = string.Empty;
        TxtDetectedMediaType.Text = string.Empty;
        TxtDetectedTracks.Text = string.Empty;
        TxtDetectedConfig.Text = string.Empty;

        Log($"🔍 Detecting content on {_drives[driveIdx].DisplayName}...");
        RunContentDetection(_drives[driveIdx], logResult: true);
    }

    /// <summary>
    /// Shared helper that runs disc content detection in the background and
    /// applies the result on the UI thread.
    /// </summary>
    /// <param name="drive">The drive to detect.</param>
    /// <param name="logResult">Whether to log the detection result.</param>
    private void RunContentDetection(DiscDrive drive, bool logResult)
    {
        Task.Run(() =>
        {
            try
            {
                using var optDrive = new Native.Optical.OpticalDrive(drive.DevicePath);
                return DiscContentDetectionService.DetectFromDrive(optDrive);
            }
            catch (Exception ex)
            {
                // Detection is best-effort — return an info object with the error
                // so the UI can display it without crashing
                System.Diagnostics.Debug.WriteLine(
                    $"[ReadView] Content detection error: {ex.Message}");
                return new DiscContentInfo { ContentLabel = $"Error: {ex.Message}" };
            }
        }).ContinueWith(t =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (t.IsCompletedSuccessfully)
                {
                    ApplyDetectedContent(t.Result);
                    if (logResult)
                        Log($"✅ Detected: {t.Result.ContentLabel}");
                }
                else
                {
                    ClearDetectedContent();
                    if (logResult)
                        Log("⚠ Content detection failed.");
                }
            });
        });
    }

    /// <summary>
    /// Updates the UI with detected content information and auto-configures
    /// the copy settings for faithful content preservation.
    /// </summary>
    private void ApplyDetectedContent(DiscContentInfo info)
    {
        _detectedContent = info;

        // Update the detection info panel
        TxtDetectedContentType.Text = info.ContentLabel;
        TxtDetectedVolumeLabel.Text = !string.IsNullOrEmpty(info.VolumeLabel)
            ? info.VolumeLabel : "(none)";
        TxtDetectedMediaType.Text = !string.IsNullOrEmpty(info.MediaType)
            ? info.MediaType : "(unknown)";

        // Track info
        var trackParts = new List<string>();
        if (info.AudioTrackCount > 0) trackParts.Add($"{info.AudioTrackCount} audio");
        if (info.DataTrackCount > 0) trackParts.Add($"{info.DataTrackCount} data");
        if (trackParts.Count > 0)
            TxtDetectedTracks.Text = $"{info.TrackCount} ({string.Join(" + ", trackParts)})";
        else if (info.TrackCount > 0)
            TxtDetectedTracks.Text = info.TrackCount.ToString();
        else
            TxtDetectedTracks.Text = "(unknown)";

        // Auto-configure optimal copy settings based on content type
        var recFormat = DiscContentDetectionService.GetRecommendedOutputFormat(info.ContentType);
        var recSectorSize = DiscContentDetectionService.GetRecommendedSectorSize(info.ContentType);
        var recSubchannel = DiscContentDetectionService.GetRecommendedSubchannelMode(info.ContentType);
        var presetName = DiscContentDetectionService.GetMatchingPresetName(info.ContentType);

        // Apply the gaming preset if one matches (this sets all read parameters)
        if (presetName != null && FormatHelper.GamingPresets.TryGetValue(presetName, out var preset))
        {
            ApplyPreset(preset);
            TxtDetectedConfig.Text =
                $"Auto-applied preset: {presetName} — Format: {preset.OutputFormat}, " +
                $"Sector: {preset.SectorSize}B, Subchannel: {preset.SubchannelMode}";
        }
        else
        {
            // No gaming preset — apply content-based recommendations directly
            SelectComboBoxItem(CmbOutputFormat, recFormat);
            SelectComboBoxItem(CmbSectorSize, recSectorSize.ToString());
            SelectComboBoxItem(CmbSubchannel, recSubchannel);

            // Audio CDs benefit from audio paranoia
            if (info.ContentType is DiscContentType.AudioCd or DiscContentType.MixedModeCd or
                DiscContentType.EnhancedCd)
            {
                ChkAudioParanoia.IsChecked = true;
            }

            TxtDetectedConfig.Text =
                $"Auto-configured — Format: {recFormat}, Sector: {recSectorSize}B, Subchannel: {recSubchannel}";
        }
    }

    /// <summary>Clears the detected content panel to its default state.</summary>
    private void ClearDetectedContent()
    {
        _detectedContent = null;
        TxtDetectedContentType.Text = "Not detected";
        TxtDetectedVolumeLabel.Text = string.Empty;
        TxtDetectedMediaType.Text = string.Empty;
        TxtDetectedTracks.Text = string.Empty;
        TxtDetectedConfig.Text = string.Empty;
    }

    // -----------------------------------------------------------------------
    // Event handlers
    // -----------------------------------------------------------------------
    private async void OnBrowseOutputClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var fmt = CmbOutputFormat.SelectedItem?.ToString() ?? "ISO";
        var ext = fmt switch
        {
            "BIN/CUE" => "bin",
            "BIN/CUE/SBI" => "bin",
            "TOC/BIN" => "bin",
            "NRG"     => "nrg",
            "MDF/MDS" => "mdf",
            "IMG"     => "img",
            "CDI"     => "cdi",
            "CCD/IMG/SUB" => "ccd",
            "VCD"     => "bin",
            "SVCD"    => "bin",
            "XSVCD"   => "bin",
            "XISO (Xbox)" => "iso",
            _         => "iso"
        };

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Image As",
            DefaultExtension = ext,
            SuggestedFileName = $"disc_image.{ext}"
        });

        if (file != null)
            TxtOutputPath.Text = file.Path.LocalPath;
    }

    private async void OnStartReadClick(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TxtOutputPath.Text))
        {
            Log("❌ Please specify an output path.");
            return;
        }

        var outputDir = System.IO.Path.GetDirectoryName(TxtOutputPath.Text);
        if (!string.IsNullOrWhiteSpace(outputDir) && !System.IO.Directory.Exists(outputDir))
        {
            Log($"❌ Output directory does not exist: {outputDir}");
            return;
        }

        var driveIdx = CmbDrive.SelectedIndex;
        if (driveIdx < 0 || driveIdx >= _drives.Count)
        {
            Log("❌ Please select a drive.");
            return;
        }

        var drive = _drives[driveIdx];
        var job = BuildJob(drive.DevicePath);

        // Validate output format is compatible with the output file extension
        var outputExt = System.IO.Path.GetExtension(TxtOutputPath.Text).ToUpperInvariant();
        if (job.OutputFormat == "ISO" && outputExt is ".BIN" or ".CUE")
        {
            Log("⚠ Output file has .BIN/.CUE extension but format is ISO. Consider using BIN/CUE format for raw sector output.");
        }
        else if (job.OutputFormat == "BIN/CUE" && outputExt == ".ISO")
        {
            Log("⚠ Output file has .ISO extension but format is BIN/CUE. The output will be a raw sector dump, not a standard ISO.");
        }

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        SetRunning(true);

        // Reset progress UI for a fresh operation
        ReadProgress.Value = 0;
        TxtReadPct.Text = "0%";
        TxtReadStatus.Text = "Starting...";
        TxtReadSpeed.Text = string.Empty;
        TxtReadElapsed.Text = string.Empty;
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

        Log($"▶ Starting read: {drive.DevicePath} → {job.OutputPath}");

        // Log detected content info for the read operation
        if (_detectedContent != null && _detectedContent.ContentType != DiscContentType.Unknown)
        {
            Log($"📀 Content: {_detectedContent.ContentLabel}");
            if (!string.IsNullOrEmpty(_detectedContent.VolumeLabel))
                Log($"📝 Volume: {_detectedContent.VolumeLabel}");
            if (_detectedContent.IsGameDisc && _detectedContent.GamingSystem != null)
                Log($"🎮 System: {_detectedContent.GamingSystem}");
            Log($"⚙ Format: {job.OutputFormat}, Sector: {job.SectorSize}B, Subchannel: {job.SubchannelMode}");
        }

        var progressHandler = new Progress<ReadProgress>(p =>
        {
            // Progress<T> already marshals callbacks to the UI thread via
            // the captured SynchronizationContext — no need for Dispatcher.UIThread.Post.
            ReadProgress.Value = p.PercentComplete;
            TxtReadPct.Text = $"{p.PercentComplete}%";
            TxtReadStatus.Text = string.IsNullOrWhiteSpace(p.StatusMessage)
                ? p.ErrorSectors > 0
                    ? $"Sectors: {p.SectorsRead}/{p.TotalSectors} ({p.ErrorSectors} errors)"
                    : $"Sectors: {p.SectorsRead}/{p.TotalSectors}"
                : p.StatusMessage;
            if (p.CurrentSpeedX > 0) TxtReadSpeed.Text = $"{p.CurrentSpeedX:F1}x";
            if (p.Elapsed.TotalSeconds > 0)
                TxtReadElapsed.Text = p.Remaining.TotalSeconds > 0
                    ? $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)}  ETA: {FormatHelper.FormatTimeSpan(p.Remaining)}"
                    : $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)}";
            if (!string.IsNullOrWhiteSpace(p.LogLine)) Log(p.LogLine);

            // Update disc visualization
            if (p.TotalSectors > 0)
            {
                DiscVizBorder.IsVisible = true;
                DiscViz.CurrentLba = p.SectorsRead;
                DiscViz.TotalSectors = p.TotalSectors;
                DiscViz.PercentComplete = p.PercentComplete;
                DiscViz.CurrentSpeedX = p.CurrentSpeedX;
                DiscViz.OperationMode = DiscOperationMode.Reading;
                DiscViz.BadSectors = p.ErrorSectors;
                DiscViz.IsActive = true;
                DiscViz.IsCompleted = false;
                DiscViz.UpdateVisualization();
            }
        });

        try
        {
            await Task.Run(() => _readService.ReadDiscAsync(job, progressHandler, _cts.Token));
            Log($"✅ Read complete: {job.OutputPath}");
            ReadProgress.Value = 100;
            TxtReadPct.Text = "100%";
            TxtReadStatus.Text = "Read complete.";
            DiscViz.PercentComplete = 100;
            DiscViz.IsCompleted = true;
            DiscViz.IsActive = false;
            DiscViz.UpdateVisualization();

            // Post-read encryption if enabled
            if (ChkEncryptOutput.IsChecked == true && !string.IsNullOrWhiteSpace(TxtEncryptPassword.Text))
            {
                var password = TxtEncryptPassword.Text;
                var confirmPassword = TxtEncryptPasswordConfirm.Text ?? string.Empty;

                if (password != confirmPassword)
                {
                    Log("⚠ Encryption skipped: passwords do not match.");
                }
                else
                {
                    var encryptedPath = job.OutputPath + ".obse";
                    Log($"🔒 Encrypting output image: {encryptedPath}");
                    TxtReadStatus.Text = "Encrypting...";
                    ReadProgress.Value = 0;

                    var encProgress = new Progress<DiscEncryptionService.EncryptionProgress>(ep =>
                    {
                        // Progress<T> already marshals to the UI thread
                        ReadProgress.Value = ep.PercentComplete;
                        TxtReadPct.Text = $"{ep.PercentComplete}%";
                        TxtReadStatus.Text = ep.StatusMessage;
                    });

                    var encResult = await DiscEncryptionService.EncryptAsync(
                        job.OutputPath, encryptedPath, password, encProgress, _cts.Token);

                    if (encResult == null)
                    {
                        Log($"✅ Encryption complete: {encryptedPath}");
                        TxtReadStatus.Text = "Read & encryption complete.";
                        ReadProgress.Value = 100;
                        TxtReadPct.Text = "100%";
                    }
                    else
                    {
                        Log($"⚠ Encryption failed: {encResult}");
                        TxtReadStatus.Text = "Read complete, encryption failed.";
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            Log("⏹ Read cancelled.");
            TxtReadStatus.Text = "Cancelled.";
            DiscViz.IsActive = false;
            DiscViz.IsCompleted = false;
            DiscViz.UpdateVisualization();
        }
        catch (Exception ex)
        {
            Log($"❌ Read failed: {ex.Message}");
            TxtReadStatus.Text = "Failed.";
            DiscViz.IsActive = false;
            DiscViz.IsCompleted = false;
            DiscViz.UpdateVisualization();
        }
        finally
        {
            SetRunning(false);
        }
    }

    private void OnCancelReadClick(object? sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        Log("Cancelling...");
    }

    private void OnOutputPathDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.TryGetFiles() is { } files && files.Any()
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private void OnOutputPathDrop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles()?.ToList();
        if (files?.Count > 0)
            TxtOutputPath.Text = files[0].Path.LocalPath;
    }

    private void OnClearLogClick(object? sender, RoutedEventArgs e) =>
        TxtLog.Text = string.Empty;

    private void OnRefreshDrivesClick(object? sender, RoutedEventArgs e) => LoadDrives();

    private void OnPresetClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is GamingPreset preset)
            ApplyPreset(preset);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------
    private ReadJob BuildJob(string devicePath)
    {
        var sectorSizeStr = CmbSectorSize.SelectedItem?.ToString() ?? "2048";
        int.TryParse(sectorSizeStr, out var sectorSize);

        return new ReadJob
        {
            DevicePath     = devicePath,
            OutputPath     = TxtOutputPath.Text ?? string.Empty,
            OutputFormat   = CmbOutputFormat.SelectedItem?.ToString() ?? "ISO",
            ReadSpeed      = CmbReadSpeed.SelectedItem?.ToString() ?? "Max",
            ErrorRecovery  = (CmbErrorRecovery.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Yes",
            RetryCount     = (int)(NumRetries.Value ?? 3),
            SectorSize     = sectorSize > 0 ? sectorSize : 2048,
            SubchannelMode = CmbSubchannel.SelectedItem?.ToString() ?? "None",
            AudioParanoia  = ChkAudioParanoia.IsChecked == true,
            JitterCorrection = ChkJitterCorrection.IsChecked == true,
            ReadBadSectors = ChkReadBadSectors.IsChecked == true,
        };
    }

    private void ApplyPreset(GamingPreset preset)
    {
        // Set output format (e.g., BIN/CUE for CD games, ISO for DVD/BD, XISO for Xbox)
        SelectComboBoxItem(CmbOutputFormat, preset.OutputFormat);

        // Set sector size
        for (int i = 0; i < CmbSectorSize.Items.Count; i++)
        {
            if (CmbSectorSize.Items[i]?.ToString() == preset.SectorSize.ToString())
            {
                CmbSectorSize.SelectedIndex = i;
                break;
            }
        }

        // Set subchannel
        for (int i = 0; i < CmbSubchannel.Items.Count; i++)
        {
            if (CmbSubchannel.Items[i]?.ToString() == preset.SubchannelMode)
            {
                CmbSubchannel.SelectedIndex = i;
                break;
            }
        }

        // Set speed
        for (int i = 0; i < CmbReadSpeed.Items.Count; i++)
        {
            if (CmbReadSpeed.Items[i]?.ToString() == preset.ReadSpeed)
            {
                CmbReadSpeed.SelectedIndex = i;
                break;
            }
        }

        // Set error recovery
        SelectComboBoxItemByTag(CmbErrorRecovery, preset.ErrorRecovery);

        NumRetries.Value = preset.Retries;
        ChkAudioParanoia.IsChecked = preset.AudioParanoia;

        TxtPresetDesc.Text = $"{preset.Name}: {preset.Description}";
        Log($"✅ Applied preset: {preset.Name}");
    }

    private void SetRunning(bool running)
    {
        BtnStartRead.IsEnabled  = !running;
        BtnCancelRead.IsEnabled = running;
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
        TxtLog.Text += $"[{DateTime.Now:HH:mm:ss}] {msg}\n";
        var text = TxtLog.Text;
        // Trim log to prevent unbounded memory growth (keep last ~20 KB)
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
