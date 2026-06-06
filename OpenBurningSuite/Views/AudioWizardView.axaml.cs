// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NAudio.Wave;
using OpenBurningSuite.Helpers;
using OpenBurningSuite.Models;
using OpenBurningSuite.Native.Audio;
using OpenBurningSuite.Services;

namespace OpenBurningSuite.Views;

public partial class AudioWizardView : UserControl
{
    // -----------------------------------------------------------------------
    // Mode enum
    // -----------------------------------------------------------------------
    private enum WizardMode { None, CreateAudioCd, CopyMusic, RipAudioCd }

    // -----------------------------------------------------------------------
    // State
    // -----------------------------------------------------------------------
    private WizardMode _mode = WizardMode.None;
    private int _currentStep = 1;

    private readonly ObservableCollection<AudioTrackItem> _createTracks = new();
    private readonly ObservableCollection<MusicFileItem> _copyFiles = new();

    private CancellationTokenSource? _cts;
    private bool _isRunning;

    // Audio playback (platform-safe: NAudio WaveOutEvent works on Windows;
    // on Linux/macOS we fall back to a simple status-only preview)
    private IWavePlayer? _player;
    private WaveStream? _audioReader;
    private DispatcherTimer? _playbackTimer;

    // Services
    private DiscDiscoveryService? _discovery;
    private List<DiscDrive> _drives = new();

    public AudioWizardView()
    {
        InitializeComponent();

        LstCreateTracks.ItemsSource = _createTracks;
        LstCopyFiles.ItemsSource = _copyFiles;

        _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _playbackTimer.Tick += OnPlaybackTimerTick;

        // Filter file systems based on disc type selection in Copy Music mode
        CmbCopyDiscType.SelectionChanged += OnCopyDiscTypeChanged;

        Loaded += async (_, _) => await LoadDrivesAsync();
        Unloaded += (_, _) => StopPlayback();
    }

    // -----------------------------------------------------------------------
    // Drive Discovery
    // -----------------------------------------------------------------------
    private async Task LoadDrivesAsync()
    {
        try
        {
            _discovery ??= new DiscDiscoveryService();
            _drives = await Task.Run(() => _discovery.DiscoverDrives());

            CmbCreateDrive.ItemsSource = _drives;
            CmbCopyDrive.ItemsSource = _drives;
            CmbRipDrive.ItemsSource = _drives;

            if (_drives.Count > 0)
            {
                // Prefer a writable drive
                var writable = _drives.FirstOrDefault(d => d.CanWrite) ?? _drives[0];
                CmbCreateDrive.SelectedItem = writable;
                CmbCopyDrive.SelectedItem = writable;
                CmbRipDrive.SelectedItem = writable;
            }
        }
        catch
        {
            // Drive discovery may fail without elevated permissions
        }
    }

    // -----------------------------------------------------------------------
    // Step 1 — Choose mode
    // -----------------------------------------------------------------------
    private void OnModeCreateAudioCd(object? sender, RoutedEventArgs e) => SetMode(WizardMode.CreateAudioCd);
    private void OnModeCopyMusic(object? sender, RoutedEventArgs e) => SetMode(WizardMode.CopyMusic);
    private void OnModeRipAudioCd(object? sender, RoutedEventArgs e) => SetMode(WizardMode.RipAudioCd);

    private void SetMode(WizardMode mode)
    {
        _mode = mode;
        GoToStep(2);
    }

    // -----------------------------------------------------------------------
    // Step navigation
    // -----------------------------------------------------------------------
    private void GoToStep(int step)
    {
        _currentStep = step;

        // Hide all step panels
        Step1Panel.IsVisible = false;
        Step2CreatePanel.IsVisible = false;
        Step2CopyPanel.IsVisible = false;
        Step2RipPanel.IsVisible = false;
        Step3Panel.IsVisible = false;
        Step4Panel.IsVisible = false;

        // Show appropriate panel
        switch (step)
        {
            case 1:
                Step1Panel.IsVisible = true;
                break;
            case 2:
                switch (_mode)
                {
                    case WizardMode.CreateAudioCd: Step2CreatePanel.IsVisible = true; break;
                    case WizardMode.CopyMusic: Step2CopyPanel.IsVisible = true; break;
                    case WizardMode.RipAudioCd: Step2RipPanel.IsVisible = true; break;
                }
                break;
            case 3:
                Step3Panel.IsVisible = true;
                PopulateSummary();
                break;
            case 4:
                Step4Panel.IsVisible = true;
                break;
        }

        UpdateStepIndicators();
        UpdateNavButtons();
    }

    private void UpdateStepIndicators()
    {
        SetStepIndicator(StepInd1, StepLbl1, _currentStep >= 1, _currentStep == 1);
        SetStepIndicator(StepInd2, StepLbl2, _currentStep >= 2, _currentStep == 2);
        SetStepIndicator(StepInd3, StepLbl3, _currentStep >= 3, _currentStep == 3);
        SetStepIndicator(StepInd4, StepLbl4, _currentStep >= 4, _currentStep == 4);
    }

    private static void SetStepIndicator(Border indicator, TextBlock label, bool reached, bool current)
    {
        indicator.Background = current
            ? Avalonia.Media.Brush.Parse("#1E90FF")
            : reached
                ? Avalonia.Media.Brush.Parse("#2A5A8A")
                : Avalonia.Media.Brush.Parse("#1E3450");

        var textChild = indicator.Child as TextBlock;
        if (textChild != null)
            textChild.Foreground = reached
                ? Avalonia.Media.Brush.Parse("#FFFFFF")
                : Avalonia.Media.Brush.Parse("#5A7A9A");

        label.Foreground = current
            ? Avalonia.Media.Brush.Parse("#E8F0FE")
            : reached
                ? Avalonia.Media.Brush.Parse("#8EAFC8")
                : Avalonia.Media.Brush.Parse("#5A7A9A");

        label.FontWeight = current
            ? Avalonia.Media.FontWeight.SemiBold
            : Avalonia.Media.FontWeight.Normal;
    }

    private void UpdateNavButtons()
    {
        BtnBack.IsVisible = _currentStep > 1 && !_isRunning;
        BtnNext.IsVisible = _currentStep == 2 && !_isRunning;
        BtnExecute.IsVisible = _currentStep == 3 && !_isRunning;
        BtnCancel.IsVisible = _currentStep == 4 && _isRunning;
        BtnFinish.IsVisible = _currentStep == 4 && !_isRunning;
    }

    private void OnBackClick(object? sender, RoutedEventArgs e)
    {
        StopPlayback();
        GoToStep(_currentStep - 1);
    }

    private void OnNextClick(object? sender, RoutedEventArgs e)
    {
        // Validate step 2 before proceeding
        var error = ValidateStep2();
        if (error != null)
        {
            AppendLog($"[Error] {error}");
            return;
        }
        GoToStep(3);
    }

    private void OnFinishClick(object? sender, RoutedEventArgs e)
    {
        // Reset wizard to step 1
        _mode = WizardMode.None;
        _isRunning = false;
        GoToStep(1);
    }

    private async void OnExecuteClick(object? sender, RoutedEventArgs e)
    {
        GoToStep(4);
        _isRunning = true;
        UpdateNavButtons();

        // Reset step 4 UI for a fresh operation
        PrgExec.Value = 0;
        TxtExecPercent.Text = "0%";
        TxtExecSpeed.Text = string.Empty;
        TxtExecStatus.Text = "Preparing...";
        TxtExecLog.Text = string.Empty;
        TxtStep4Title.Text = "Executing...";

        // Reset disc visualization for a fresh operation
        DiscVizBorder.IsVisible = false;
        DiscViz.IsActive = false;
        DiscViz.IsCompleted = false;
        DiscViz.VerifyPassed = null;
        DiscViz.BadSectors = 0;
        DiscViz.PercentComplete = 0;
        DiscViz.CurrentLba = 0;
        DiscViz.TotalSectors = 0;

        // Audio CDs are always single-layer
        DiscViz.ConfigureForMedia("CD-R");

        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        try
        {
            StopPlayback();

            switch (_mode)
            {
                case WizardMode.CreateAudioCd:
                    await ExecuteCreateAudioCdAsync(_cts.Token);
                    break;
                case WizardMode.CopyMusic:
                    await ExecuteCopyMusicAsync(_cts.Token);
                    break;
                case WizardMode.RipAudioCd:
                    await ExecuteRipAudioCdAsync(_cts.Token);
                    break;
            }

            TxtStep4Title.Text = "✅ Complete!";
            TxtExecStatus.Text = "Operation completed successfully.";
        }
        catch (OperationCanceledException)
        {
            TxtStep4Title.Text = "⏹ Cancelled";
            TxtExecStatus.Text = "Operation was cancelled by user.";
            AppendLog("[Info] Operation cancelled.");
            DiscViz.IsActive = false;
            DiscViz.IsCompleted = false;
            DiscViz.UpdateVisualization();
        }
        catch (Exception ex)
        {
            TxtStep4Title.Text = "❌ Failed";
            TxtExecStatus.Text = $"Error: {ex.Message}";
            AppendLog($"[Error] {ex.Message}");
            DiscViz.IsActive = false;
            DiscViz.IsCompleted = false;
            DiscViz.UpdateVisualization();
        }
        finally
        {
            _isRunning = false;
            UpdateNavButtons();
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
    }

    // -----------------------------------------------------------------------
    // Step 2 validation
    // -----------------------------------------------------------------------
    private string? ValidateStep2()
    {
        switch (_mode)
        {
            case WizardMode.CreateAudioCd:
                if (_createTracks.Count == 0)
                    return "Please add at least one audio track.";
                if (_createTracks.Count > 99)
                    return "Audio CDs support a maximum of 99 tracks.";
                // Red Book audio CD maximum is ~80 minutes (99 min with overburn, but 80 min is safe)
                var totalDuration = TimeSpan.FromTicks(_createTracks.Sum(t => t.Duration.Ticks));
                if (totalDuration.TotalMinutes > 80)
                    return $"Total duration ({totalDuration:mm\\:ss}) exceeds 80-minute CD capacity. Remove some tracks or use shorter files.";
                if (string.IsNullOrWhiteSpace(TxtCreateOutputPath.Text))
                    return "Please specify an output image path.";
                {
                    var outDir = Path.GetDirectoryName(TxtCreateOutputPath.Text.Trim());
                    if (!string.IsNullOrWhiteSpace(outDir) && !Directory.Exists(outDir))
                        return $"Output directory does not exist: {outDir}";
                }
                if (ChkCreateBurnImage.IsChecked == true && CmbCreateDrive.SelectedItem == null)
                    return "Please select a target drive for burning.";
                return null;

            case WizardMode.CopyMusic:
                if (_copyFiles.Count == 0)
                    return "Please add at least one music file.";
                if (CmbCopyDrive.SelectedItem == null)
                    return "Please select a target drive.";
                return null;

            case WizardMode.RipAudioCd:
                if (CmbRipDrive.SelectedItem == null)
                    return "Please select a source drive containing an audio CD.";
                if (string.IsNullOrWhiteSpace(TxtRipOutputFolder.Text))
                    return "Please specify an output folder for the ripped tracks.";
                return null;

            default:
                return "Please choose a task first.";
        }
    }

    // -----------------------------------------------------------------------
    // Summary (Step 3)
    // -----------------------------------------------------------------------
    private void PopulateSummary()
    {
        SummaryContent.Children.Clear();

        switch (_mode)
        {
            case WizardMode.CreateAudioCd:
                AddSummaryLine("🎵 Task", "Create Audio CD (Red Book)");
                AddSummaryLine("📀 Tracks", $"{_createTracks.Count} track(s), total {GetTotalDuration()}");
                if (!string.IsNullOrWhiteSpace(TxtCdAlbum.Text))
                    AddSummaryLine("💿 Album", TxtCdAlbum.Text!);
                if (!string.IsNullOrWhiteSpace(TxtCdArtist.Text))
                    AddSummaryLine("🎤 Artist", TxtCdArtist.Text!);
                AddSummaryLine("💾 Output Image", TxtCreateOutputPath.Text ?? "(not set)");
                if (ChkCreateBurnImage.IsChecked == true)
                {
                    var drive = CmbCreateDrive.SelectedItem as DiscDrive;
                    AddSummaryLine("🔥 Burn to Drive", drive?.DisplayName ?? "(none)");
                    AddSummaryLine("⚡ Write Speed", GetSelectedText(CmbCreateSpeed, "Auto"));
                    AddSummaryLine("📝 Write Mode", GetSelectedText(CmbCreateWriteMode, "DAO"));
                    AddSummaryLine("📦 Copies", ((int)(NumCreateCopies.Value ?? 1)).ToString());
                }
                else
                {
                    AddSummaryLine("🔥 Burn to Disc", "No (image only)");
                }
                break;

            case WizardMode.CopyMusic:
                AddSummaryLine("🎶 Task", "Copy Music Files to Disc");
                AddSummaryLine("📁 Files", $"{_copyFiles.Count} file(s), {GetTotalCopySize()}");
                var copyDrive = CmbCopyDrive.SelectedItem as DiscDrive;
                AddSummaryLine("💿 Target Drive", copyDrive?.DisplayName ?? "(none)");
                AddSummaryLine("📀 Disc Type", GetSelectedText(CmbCopyDiscType, "CD"));
                AddSummaryLine("📂 Filesystem", GetSelectedText(CmbCopyFilesystem, "ISO 9660 + Joliet"));
                AddSummaryLine("⚡ Write Speed", GetSelectedText(CmbCopySpeed, "Auto"));
                AddSummaryLine("🏷 Volume Label", string.IsNullOrWhiteSpace(TxtCopyVolumeLabel.Text) ? "MUSIC_DISC" : TxtCopyVolumeLabel.Text!);
                break;

            case WizardMode.RipAudioCd:
                AddSummaryLine("🎧 Task", "Rip Audio CD");
                var ripDrive = CmbRipDrive.SelectedItem as DiscDrive;
                AddSummaryLine("💿 Source Drive", ripDrive?.DisplayName ?? "(none)");
                AddSummaryLine("🎵 Output Format", GetSelectedText(CmbRipFormat, "WAV"));
                AddSummaryLine("📁 Output Folder", TxtRipOutputFolder.Text ?? "(not set)");
                AddSummaryLine("⚡ Read Speed", GetSelectedText(CmbRipSpeed, "Max"));
                AddSummaryLine("🛡 Audio Paranoia", ChkRipParanoia.IsChecked == true ? "Enabled" : "Disabled");
                AddSummaryLine("🔧 Jitter Correction", ChkRipJitter.IsChecked == true ? "Enabled" : "Disabled");
                break;
        }
    }

    private void AddSummaryLine(string label, string value)
    {
        var sp = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
        sp.Children.Add(new TextBlock
        {
            Text = label + ":",
            Foreground = Avalonia.Media.Brush.Parse("#8EAFC8"),
            FontSize = 12,
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            Width = 160
        });
        sp.Children.Add(new TextBlock
        {
            Text = value,
            Foreground = Avalonia.Media.Brush.Parse("#E8F0FE"),
            FontSize = 12,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });
        SummaryContent.Children.Add(sp);
    }

    private void AddSummaryLine(string iconKey, string label, string value)
    {
        var sp = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };

        if (!string.IsNullOrEmpty(iconKey))
        {
            var uri = Helpers.IconHelper.GetUri(iconKey, 16);
            if (uri != null)
            {
                try
                {
                    var bitmap = new Avalonia.Media.Imaging.Bitmap(Avalonia.Platform.AssetLoader.Open(new Uri(uri)));
                    sp.Children.Add(new Image
                    {
                        Source = bitmap,
                        Width = 16,
                        Height = 16,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                    });
                }
                catch { }
            }
        }

        sp.Children.Add(new TextBlock
        {
            Text = label + ":",
            Foreground = Avalonia.Media.Brush.Parse("#8EAFC8"),
            FontSize = 12,
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            Width = 160
        });
        sp.Children.Add(new TextBlock
        {
            Text = value,
            Foreground = Avalonia.Media.Brush.Parse("#E8F0FE"),
            FontSize = 12,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });
        SummaryContent.Children.Add(sp);
    }

    // -----------------------------------------------------------------------
    // EXECUTE: Create Audio CD
    // -----------------------------------------------------------------------
    private async Task ExecuteCreateAudioCdAsync(CancellationToken ct)
    {
        TxtStep4Title.Text = "🔥 Creating Audio CD...";

        // Build the BuildJob
        var tracks = new List<AudioTrack>();
        foreach (var item in _createTracks)
        {
            tracks.Add(new AudioTrack
            {
                FilePath = item.FilePath,
                Title = item.Title,
                Artist = TxtCdArtist.Text ?? string.Empty,
                Songwriter = TxtCdSongwriter.Text ?? string.Empty,
                PreGapSeconds = 2,
                Duration = item.Duration
            });
        }

        var buildJob = new BuildJob
        {
            DiscType = "CD",
            OutputImagePath = TxtCreateOutputPath.Text?.Trim() ?? Path.Combine(Path.GetTempPath(), "OpenBurningSuite_AudioCD.bin"),
            AudioTracks = tracks,
            CdTextAlbum = TxtCdAlbum.Text ?? string.Empty,
            CdTextArtist = TxtCdArtist.Text ?? string.Empty,
            CdTextSongwriter = TxtCdSongwriter.Text ?? string.Empty,
            CdTextComposer = TxtCdComposer.Text ?? string.Empty,
            CdTextArranger = TxtCdArranger.Text ?? string.Empty,
            CdTextMessage = TxtCdMessage.Text ?? string.Empty,
            CdTextGenre = TxtCdGenre.Text ?? string.Empty,
            MediaCatalogNumber = TxtCdMcn.Text ?? string.Empty
        };

        // Phase 1: Build BIN/CUE image
        AppendLog("[Info] Building audio CD image (BIN/CUE)...");
        var buildService = new BuildService();
        var buildProgress = new Progress<BuildProgress>(p =>
        {
            if (ChkCreateBurnImage.IsChecked == true)
                PrgExec.Value = p.PercentComplete * 0.5; // 0-50% for build
            else
                PrgExec.Value = p.PercentComplete;
            TxtExecPercent.Text = $"{(int)PrgExec.Value}%";
            if (!string.IsNullOrEmpty(p.StatusMessage))
                TxtExecStatus.Text = p.StatusMessage;
            if (p.Remaining.TotalSeconds > 0)
                TxtExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)} | ETA: {FormatHelper.FormatTimeSpan(p.Remaining)}";
            else if (p.Elapsed.TotalSeconds > 0)
                TxtExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)}";
            if (!string.IsNullOrEmpty(p.LogLine))
                AppendLog(p.LogLine);
        });

        await Task.Run(() => buildService.BuildImageAsync(buildJob, buildProgress, ct));
        AppendLog($"[Info] Audio CD image created: {buildJob.OutputImagePath}");

        // Phase 2: Burn to disc (optional)
        if (ChkCreateBurnImage.IsChecked == true)
        {
            var drive = CmbCreateDrive.SelectedItem as DiscDrive;
            if (drive == null)
            {
                AppendLog("[Warning] No drive selected — skipping burn.");
                return;
            }

            var binPath = Path.ChangeExtension(buildJob.OutputImagePath, ".bin");
            var cuePath = Path.ChangeExtension(buildJob.OutputImagePath, ".cue");

            var burnJob = new BurnJob
            {
                ImagePath = binPath,
                CuePath = cuePath,
                DevicePath = drive.DevicePath,
                WriteMode = GetSelectedText(CmbCreateWriteMode, "DAO (Disc At Once)"),
                WriteSpeed = GetSelectedText(CmbCreateSpeed, "Auto"),
                Copies = (int)(NumCreateCopies.Value ?? 1),
                EjectAfterBurn = ChkCreateEject.IsChecked == true,
                VerifyAfterBurn = ChkCreateVerify.IsChecked == true,
                SimulateOnly = ChkCreateSimulate.IsChecked == true,
                BufferUnderrunProtection = ChkCreateBup.IsChecked == true,
                CloseDisc = true
            };

            AppendLog($"[Info] Burning to {drive.DisplayName}...");
            TxtExecStatus.Text = "Burning audio CD...";

            var burnService = new BurnService();
            var burnProgress = new Progress<BurnProgress>(p =>
            {
                PrgExec.Value = 50 + p.PercentComplete * 0.5; // 50-100% for burn
                TxtExecPercent.Text = $"{(int)PrgExec.Value}%";
                if (p.CurrentSpeedX > 0)
                    TxtExecSpeed.Text = $"{p.CurrentSpeedX:F1}x";
                if (p.Remaining.TotalSeconds > 0)
                    TxtExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)} | ETA: {FormatHelper.FormatTimeSpan(p.Remaining)}";
                else if (p.Elapsed.TotalSeconds > 0)
                    TxtExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)}";
                if (!string.IsNullOrEmpty(p.StatusMessage))
                    TxtExecStatus.Text = p.StatusMessage;
                if (!string.IsNullOrEmpty(p.LogLine))
                    AppendLog(p.LogLine);

                // Update disc visualization
                if (p.TotalSectors > 0)
                {
                    DiscVizBorder.IsVisible = true;
                    DiscViz.CurrentLba = p.CurrentLba;
                    DiscViz.TotalSectors = p.TotalSectors;
                    DiscViz.PercentComplete = p.PercentComplete;
                    DiscViz.CurrentSpeedX = p.CurrentSpeedX;
                    DiscViz.OperationMode = DiscOperationMode.Burning;
                    DiscViz.IsActive = true;
                    DiscViz.IsCompleted = false;
                    DiscViz.UpdateVisualization();
                }
            });

            await Task.Run(() => burnService.BurnAsync(burnJob, burnProgress, ct));
            AppendLog("[Info] Burn complete!");

            // Note: Post-burn verification is handled internally by BurnEngine when
            // VerifyAfterBurn is set. Do NOT add a second verify here — the disc may
            // already be ejected, and BurnEngine's sector-comparison verify is more thorough.

            Dispatcher.UIThread.Post(() =>
            {
                DiscViz.PercentComplete = 100;
                DiscViz.IsCompleted = true;
                DiscViz.IsActive = false;
                DiscViz.UpdateVisualization();
            });
        }

        Dispatcher.UIThread.Post(() =>
        {
            PrgExec.Value = 100;
            TxtExecPercent.Text = "100%";
        });
    }

    // -----------------------------------------------------------------------
    // EXECUTE: Copy Music to Disc
    // -----------------------------------------------------------------------
    private async Task ExecuteCopyMusicAsync(CancellationToken ct)
    {
        TxtStep4Title.Text = "🎶 Copying Music to Disc...";

        var drive = CmbCopyDrive.SelectedItem as DiscDrive;
        if (drive == null)
            throw new InvalidOperationException("No target drive selected.");

        // Phase 1: Create a temporary folder with the music files (symlinks or copies)
        var tempDir = Path.Combine(Path.GetTempPath(), $"OpenBurningSuite_MusicDisc_{Guid.NewGuid().ToString("N")[..8]}");
        Directory.CreateDirectory(tempDir);

        try
        {
            AppendLog("[Info] Preparing music files...");
            TxtExecStatus.Text = "Preparing files...";

            // Copy/link files to temp dir
            for (int i = 0; i < _copyFiles.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var src = _copyFiles[i].FilePath;
                var dst = Path.Combine(tempDir, _copyFiles[i].FileName);

                // Avoid filename collisions
                if (File.Exists(dst))
                    dst = Path.Combine(tempDir, $"{i + 1:D2}_{_copyFiles[i].FileName}");

                File.Copy(src, dst);
                Dispatcher.UIThread.Post(() =>
                {
                    PrgExec.Value = (i + 1) * 20.0 / _copyFiles.Count;
                    TxtExecPercent.Text = $"{(int)PrgExec.Value}%";
                });
            }

            // Phase 2: Build ISO image
            AppendLog("[Info] Building data disc image...");
            TxtExecStatus.Text = "Building disc image...";

            var volumeLabel = string.IsNullOrWhiteSpace(TxtCopyVolumeLabel.Text)
                ? "MUSIC_DISC"
                : TxtCopyVolumeLabel.Text!.Trim();

            var fsText = GetSelectedText(CmbCopyFilesystem, "ISO 9660 + Joliet");
            var (fileSystem, udfVersion) = ParseFileSystem(fsText);

            var isoPath = Path.Combine(Path.GetTempPath(), $"OpenBurningSuite_Music_{volumeLabel}_{Guid.NewGuid().ToString("N")[..8]}.iso");

            var buildJob = new BuildJob
            {
                DiscType = GetSelectedText(CmbCopyDiscType, "CD"),
                FileSystem = fileSystem,
                UdfVersion = udfVersion,
                SourceFolder = tempDir,
                OutputImagePath = isoPath,
                VolumeLabel = volumeLabel,
                JolietLongFilenames = fileSystem.Contains("Joliet", StringComparison.OrdinalIgnoreCase)
            };

            var buildService = new BuildService();
            var buildProgress = new Progress<BuildProgress>(p =>
            {
                PrgExec.Value = 20 + p.PercentComplete * 0.3;
                TxtExecPercent.Text = $"{(int)PrgExec.Value}%";
                if (!string.IsNullOrEmpty(p.StatusMessage))
                    TxtExecStatus.Text = p.StatusMessage;
                if (!string.IsNullOrEmpty(p.LogLine))
                    AppendLog(p.LogLine);
            });

            await Task.Run(() => buildService.BuildImageAsync(buildJob, buildProgress, ct));
            AppendLog($"[Info] ISO image created: {isoPath}");

            // Phase 3: Burn to disc
            AppendLog($"[Info] Burning to {drive.DisplayName}...");
            TxtExecStatus.Text = "Burning to disc...";

            var burnJob = new BurnJob
            {
                ImagePath = isoPath,
                DevicePath = drive.DevicePath,
                WriteMode = "DAO (Disc At Once)",
                WriteSpeed = GetSelectedText(CmbCopySpeed, "Auto"),
                Copies = 1,
                EjectAfterBurn = ChkCopyEject.IsChecked == true,
                VerifyAfterBurn = ChkCopyVerify.IsChecked == true,
                BufferUnderrunProtection = true,
                CloseDisc = true
            };

            var burnService = new BurnService();
            var burnProgress = new Progress<BurnProgress>(p =>
            {
                PrgExec.Value = 50 + p.PercentComplete * 0.5;
                TxtExecPercent.Text = $"{(int)PrgExec.Value}%";
                if (p.CurrentSpeedX > 0)
                    TxtExecSpeed.Text = $"{p.CurrentSpeedX:F1}x";
                if (p.Remaining.TotalSeconds > 0)
                    TxtExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)} | ETA: {FormatHelper.FormatTimeSpan(p.Remaining)}";
                else if (p.Elapsed.TotalSeconds > 0)
                    TxtExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)}";
                if (!string.IsNullOrEmpty(p.StatusMessage))
                    TxtExecStatus.Text = p.StatusMessage;
                if (!string.IsNullOrEmpty(p.LogLine))
                    AppendLog(p.LogLine);

                // Update disc visualization
                if (p.TotalSectors > 0)
                {
                    DiscVizBorder.IsVisible = true;
                    DiscViz.CurrentLba = p.CurrentLba;
                    DiscViz.TotalSectors = p.TotalSectors;
                    DiscViz.PercentComplete = p.PercentComplete;
                    DiscViz.CurrentSpeedX = p.CurrentSpeedX;
                    DiscViz.OperationMode = DiscOperationMode.Burning;
                    DiscViz.IsActive = true;
                    DiscViz.IsCompleted = false;
                    DiscViz.UpdateVisualization();
                }
            });

            await Task.Run(() => burnService.BurnAsync(burnJob, burnProgress, ct));
            AppendLog("[Info] Music disc burn complete!");

            // Note: Post-burn verification is handled internally by BurnEngine when
            // VerifyAfterBurn is set. Do NOT add a second verify here — the disc may
            // already be ejected, and BurnEngine's sector-comparison verify is more thorough.

            Dispatcher.UIThread.Post(() =>
            {
                DiscViz.PercentComplete = 100;
                DiscViz.IsCompleted = true;
                DiscViz.IsActive = false;
                DiscViz.UpdateVisualization();
            });

            // Clean up temp ISO
            try { if (File.Exists(isoPath)) File.Delete(isoPath); } catch { /* best effort */ }
        }
        finally
        {
            // Clean up temp directory
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { /* best effort */ }
        }

        Dispatcher.UIThread.Post(() =>
        {
            PrgExec.Value = 100;
            TxtExecPercent.Text = "100%";
        });
    }

    // -----------------------------------------------------------------------
    // EXECUTE: Rip Audio CD
    // -----------------------------------------------------------------------
    private async Task ExecuteRipAudioCdAsync(CancellationToken ct)
    {
        TxtStep4Title.Text = "🎧 Ripping Audio CD...";

        var drive = CmbRipDrive.SelectedItem as DiscDrive;
        if (drive == null)
            throw new InvalidOperationException("No source drive selected.");

        var outputFolder = TxtRipOutputFolder.Text ?? string.Empty;
        if (!Directory.Exists(outputFolder))
            Directory.CreateDirectory(outputFolder);

        var formatText = GetSelectedText(CmbRipFormat, "WAV");
        bool ripToWav = formatText.Contains("WAV", StringComparison.OrdinalIgnoreCase);

        // Map error recovery text to ReadJob values
        var errorRecoveryText = GetSelectedText(CmbRipErrorRecovery, "Yes");
        string errorRecovery;
        if (errorRecoveryText.Contains("Abort"))
            errorRecovery = "Abort";
        else if (errorRecoveryText.Contains("No") || errorRecoveryText.Contains("Ignore"))
            errorRecovery = "No";
        else
            errorRecovery = "Yes";

        if (ripToWav)
        {
            // Rip to BIN/CUE first, then extract individual WAV tracks
            var tempBin = Path.Combine(outputFolder, "temp_rip.bin");
            var tempCue = Path.Combine(outputFolder, "temp_rip.cue");

            var readJob = new ReadJob
            {
                DevicePath = drive.DevicePath,
                OutputPath = tempBin,
                OutputFormat = "BIN/CUE",
                ReadSpeed = GetSelectedText(CmbRipSpeed, "Max"),
                ErrorRecovery = errorRecovery,
                RetryCount = (int)(NumRipRetries.Value ?? 3),
                SectorSize = 2352,
                AudioParanoia = ChkRipParanoia.IsChecked == true,
                JitterCorrection = ChkRipJitter.IsChecked == true
            };

            AppendLog("[Info] Ripping audio CD to BIN/CUE...");
            var readService = new ReadService();
            var readProgress = new Progress<ReadProgress>(p =>
            {
                PrgExec.Value = p.PercentComplete * 0.7;
                TxtExecPercent.Text = $"{(int)PrgExec.Value}%";
                if (p.CurrentSpeedX > 0)
                    TxtExecSpeed.Text = $"{p.CurrentSpeedX:F1}x";
                if (p.Remaining.TotalSeconds > 0)
                    TxtExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)} | ETA: {FormatHelper.FormatTimeSpan(p.Remaining)}";
                else if (p.Elapsed.TotalSeconds > 0)
                    TxtExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)}";
                if (!string.IsNullOrEmpty(p.StatusMessage))
                    TxtExecStatus.Text = p.StatusMessage;
                if (!string.IsNullOrEmpty(p.LogLine))
                    AppendLog(p.LogLine);
            });

            await Task.Run(() => readService.ReadDiscAsync(readJob, readProgress, ct));
            AppendLog("[Info] Rip to BIN/CUE complete.");

            // Extract WAV tracks from the BIN file
            AppendLog("[Info] Extracting individual WAV tracks...");
            TxtExecStatus.Text = "Extracting WAV tracks...";

            await ExtractWavTracksFromBinCueAsync(tempBin, tempCue, outputFolder, ct);

            // Clean up temp files
            try { if (File.Exists(tempBin)) File.Delete(tempBin); } catch { /* best effort */ }
            try { if (File.Exists(tempCue)) File.Delete(tempCue); } catch { /* best effort */ }

            // Also clean up any .toc or .cdt file generated alongside
            var tempToc = Path.ChangeExtension(tempBin, ".toc");
            var tempCdt = Path.ChangeExtension(tempBin, ".cdt");
            try { if (File.Exists(tempToc)) File.Delete(tempToc); } catch { /* best effort */ }
            try { if (File.Exists(tempCdt)) File.Delete(tempCdt); } catch { /* best effort */ }

            AppendLog($"[Info] WAV tracks saved to: {outputFolder}");
        }
        else
        {
            // Rip directly to BIN/CUE
            var outputBin = Path.Combine(outputFolder, "audio_cd.bin");

            var readJob = new ReadJob
            {
                DevicePath = drive.DevicePath,
                OutputPath = outputBin,
                OutputFormat = "BIN/CUE",
                ReadSpeed = GetSelectedText(CmbRipSpeed, "Max"),
                ErrorRecovery = errorRecovery,
                RetryCount = (int)(NumRipRetries.Value ?? 3),
                SectorSize = 2352,
                AudioParanoia = ChkRipParanoia.IsChecked == true,
                JitterCorrection = ChkRipJitter.IsChecked == true
            };

            AppendLog("[Info] Ripping audio CD to BIN/CUE...");
            var readService = new ReadService();
            var readProgress = new Progress<ReadProgress>(p =>
            {
                PrgExec.Value = p.PercentComplete;
                TxtExecPercent.Text = $"{(int)PrgExec.Value}%";
                if (p.CurrentSpeedX > 0)
                    TxtExecSpeed.Text = $"{p.CurrentSpeedX:F1}x";
                if (p.Remaining.TotalSeconds > 0)
                    TxtExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)} | ETA: {FormatHelper.FormatTimeSpan(p.Remaining)}";
                else if (p.Elapsed.TotalSeconds > 0)
                    TxtExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)}";
                if (!string.IsNullOrEmpty(p.StatusMessage))
                    TxtExecStatus.Text = p.StatusMessage;
                if (!string.IsNullOrEmpty(p.LogLine))
                    AppendLog(p.LogLine);
            });

            await Task.Run(() => readService.ReadDiscAsync(readJob, readProgress, ct));
            AppendLog($"[Info] Audio CD ripped to: {outputBin}");
        }

        Dispatcher.UIThread.Post(() =>
        {
            PrgExec.Value = 100;
            TxtExecPercent.Text = "100%";
        });
    }

    /// <summary>
    /// Extracts individual WAV tracks from a BIN/CUE audio CD image.
    /// Parses the CUE sheet to determine track boundaries, then reads raw
    /// 2352-byte audio sectors and writes standard 16-bit stereo WAV files.
    /// </summary>
    private async Task ExtractWavTracksFromBinCueAsync(
        string binPath, string cuePath, string outputFolder, CancellationToken ct)
    {
        if (!File.Exists(binPath))
        {
            AppendLog("[Warning] BIN file not found — cannot extract tracks.");
            return;
        }

        // Parse CUE sheet to find track start positions
        var trackStarts = new List<(int TrackNum, long StartSector)>();
        if (File.Exists(cuePath))
        {
            var cueLines = await File.ReadAllLinesAsync(cuePath, ct);
            int currentTrack = -1;
            foreach (var line in cueLines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("TRACK ", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && int.TryParse(parts[1], out var trackNum))
                        currentTrack = trackNum;
                }
                else if (trimmed.StartsWith("INDEX 01", StringComparison.OrdinalIgnoreCase) && currentTrack > 0)
                {
                    var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 3)
                    {
                        var msf = parts[2].Split(':');
                        if (msf.Length == 3 &&
                            int.TryParse(msf[0], out var mm) &&
                            int.TryParse(msf[1], out var ss) &&
                            int.TryParse(msf[2], out var ff))
                        {
                            long sector = mm * 60 * 75 + ss * 75 + ff;
                            trackStarts.Add((currentTrack, sector));
                        }
                    }
                }
            }
        }

        if (trackStarts.Count == 0)
        {
            // No CUE or couldn't parse — treat entire BIN as a single track
            AppendLog("[Warning] Could not parse CUE — saving entire BIN as a single WAV.");
            trackStarts.Add((1, 0));
        }

        const int sectorSize = 2352;
        const int sampleRate = 44100;
        const int bitsPerSample = 16;
        const int channels = 2;

        await using var binStream = new FileStream(binPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 256 * 1024, useAsync: true);
        long totalSectors = binStream.Length / sectorSize;

        for (int i = 0; i < trackStarts.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var (trackNum, startSector) = trackStarts[i];
            long endSector = i + 1 < trackStarts.Count ? trackStarts[i + 1].StartSector : totalSectors;
            long trackSectors = endSector - startSector;

            if (trackSectors <= 0) continue;

            var wavPath = Path.Combine(outputFolder, $"Track{trackNum:D2}.wav");
            AppendLog($"[Info] Extracting Track {trackNum}: {trackSectors} sectors → {Path.GetFileName(wavPath)}");

            Dispatcher.UIThread.Post(() =>
            {
                TxtExecStatus.Text = $"Extracting Track {trackNum}...";
                PrgExec.Value = 70 + (i * 30.0 / trackStarts.Count);
                TxtExecPercent.Text = $"{(int)PrgExec.Value}%";
            });

            binStream.Seek(startSector * sectorSize, SeekOrigin.Begin);

            await using var wavStream = new FileStream(wavPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 256 * 1024, useAsync: true);

            // Write WAV header
            var dataSize = (int)(trackSectors * sectorSize);
            var header = BuildWavHeader(sampleRate, bitsPerSample, channels, dataSize);
            await wavStream.WriteAsync(header, ct);

            // Copy audio data sector by sector
            var buffer = new byte[sectorSize * 64]; // 64 sectors at a time
            long sectorsRemaining = trackSectors;
            while (sectorsRemaining > 0)
            {
                ct.ThrowIfCancellationRequested();
                int sectorsToRead = (int)Math.Min(64, sectorsRemaining);
                int bytesToRead = sectorsToRead * sectorSize;
                int bytesRead = await binStream.ReadAsync(buffer.AsMemory(0, bytesToRead), ct);
                if (bytesRead == 0) break;
                await wavStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                sectorsRemaining -= sectorsToRead;
            }
        }

        AppendLog($"[Info] Extracted {trackStarts.Count} track(s) to WAV.");
    }

    /// <summary>Builds a standard 44-byte PCM WAV header.</summary>
    private static byte[] BuildWavHeader(int sampleRate, int bitsPerSample, int channels, int dataSize)
    {
        int byteRate = sampleRate * channels * (bitsPerSample / 8);
        short blockAlign = (short)(channels * (bitsPerSample / 8));

        using var ms = new MemoryStream(44);
        using var bw = new BinaryWriter(ms);

        // RIFF header
        bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + dataSize); // file size - 8
        bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));

        // fmt subchunk
        bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);                        // subchunk1 size (PCM)
        bw.Write((short)1);                  // audio format (PCM)
        bw.Write((short)channels);
        bw.Write(sampleRate);
        bw.Write(byteRate);
        bw.Write(blockAlign);
        bw.Write((short)bitsPerSample);

        // data subchunk
        bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        bw.Write(dataSize);

        return ms.ToArray();
    }

    // -----------------------------------------------------------------------
    // Track management (Create Audio CD)
    // -----------------------------------------------------------------------
    private async void OnAddTracksClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add Audio Tracks",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Audio Files") { Patterns = new[] { "*.wav", "*.mp3", "*.wma", "*.flac", "*.ogg", "*.aiff", "*.aif", "*.aac", "*.m4a", "*.ape", "*.opus" } },
                new FilePickerFileType("All Files") { Patterns = new[] { "*.*" } }
            }
        });

        foreach (var file in files)
        {
            await AddAudioTrackAsync(file.Path.LocalPath);
        }
    }

    private async Task AddAudioTrackAsync(string filePath)
    {
        try
        {
            var duration = await Task.Run(() =>
            {
                using var reader = AudioReaderFactory.CreateReader(filePath);
                return reader.TotalTime;
            });

            _createTracks.Add(new AudioTrackItem
            {
                TrackNumber = _createTracks.Count + 1,
                Title = Path.GetFileNameWithoutExtension(filePath),
                FilePath = filePath,
                Duration = duration
            });

            UpdateCreateTrackInfo();
        }
        catch (Exception ex)
        {
            AppendLog($"[Error] Could not add '{Path.GetFileName(filePath)}': {ex.Message}");
        }
    }

    private void OnRemoveTrackClick(object? sender, RoutedEventArgs e)
    {
        if (LstCreateTracks.SelectedIndex >= 0 && LstCreateTracks.SelectedIndex < _createTracks.Count)
        {
            _createTracks.RemoveAt(LstCreateTracks.SelectedIndex);
            RenumberTracks();
            UpdateCreateTrackInfo();
        }
    }

    private void OnMoveTrackUpClick(object? sender, RoutedEventArgs e)
    {
        var idx = LstCreateTracks.SelectedIndex;
        if (idx > 0)
        {
            _createTracks.Move(idx, idx - 1);
            LstCreateTracks.SelectedIndex = idx - 1;
            RenumberTracks();
        }
    }

    private void OnMoveTrackDownClick(object? sender, RoutedEventArgs e)
    {
        var idx = LstCreateTracks.SelectedIndex;
        if (idx >= 0 && idx < _createTracks.Count - 1)
        {
            _createTracks.Move(idx, idx + 1);
            LstCreateTracks.SelectedIndex = idx + 1;
            RenumberTracks();
        }
    }

    private void RenumberTracks()
    {
        for (int i = 0; i < _createTracks.Count; i++)
            _createTracks[i].TrackNumber = i + 1;
    }

    private void UpdateCreateTrackInfo()
    {
        var total = TimeSpan.FromTicks(_createTracks.Sum(t => t.Duration.Ticks));
        var durationStr = total.TotalHours >= 1 ? total.ToString(@"h\:mm\:ss") : total.ToString(@"mm\:ss");
        TxtCreateTrackCount.Text = $"({_createTracks.Count} track{(_createTracks.Count != 1 ? "s" : "")}, {durationStr} total)";
    }

    // Drag-drop for audio tracks (also handles playlist files)
    private void OnTrackDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DragDropEffects.Copy;
    }

    private async void OnTrackDrop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer?.TryGetFiles();
        if (files != null)
        {
            foreach (var file in files)
            {
                if (file is IStorageFile storageFile)
                {
                    var ext = Path.GetExtension(storageFile.Name).ToLowerInvariant();
                    if (PlaylistParser.IsPlaylistFile(ext))
                    {
                        await ImportPlaylistToCreateTracksAsync(storageFile.Path.LocalPath);
                    }
                    else if (ext is ".wav" or ".mp3" or ".wma" or ".aiff" or ".aif" or ".flac" or ".ogg" or ".aac" or ".m4a" or ".ape" or ".opus")
                    {
                        await AddAudioTrackAsync(storageFile.Path.LocalPath);
                    }
                }
            }
        }
    }

    // -----------------------------------------------------------------------
    // Playlist import / export (Create Audio CD)
    // -----------------------------------------------------------------------
    private static readonly FilePickerFileType PlaylistFileFilter = new("Playlist Files")
    {
        Patterns = new[] { "*.m3u", "*.m3u8", "*.pls", "*.wpl", "*.asx" }
    };

    private async void OnImportPlaylistCreateClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Playlist",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                PlaylistFileFilter,
                new FilePickerFileType("M3U Playlist")  { Patterns = new[] { "*.m3u" } },
                new FilePickerFileType("M3U8 Playlist") { Patterns = new[] { "*.m3u8" } },
                new FilePickerFileType("PLS Playlist")  { Patterns = new[] { "*.pls" } },
                new FilePickerFileType("WPL Playlist")  { Patterns = new[] { "*.wpl" } },
                new FilePickerFileType("ASX Playlist")  { Patterns = new[] { "*.asx" } },
                new FilePickerFileType("All Files")     { Patterns = new[] { "*.*" } }
            }
        });

        foreach (var file in files)
            await ImportPlaylistToCreateTracksAsync(file.Path.LocalPath);
    }

    private async Task ImportPlaylistToCreateTracksAsync(string playlistPath)
    {
        try
        {
            var entries = await Task.Run(() => PlaylistParser.Load(playlistPath));
            int added = 0, skipped = 0;

            foreach (var entry in entries)
            {
                if (!File.Exists(entry.Path))
                {
                    skipped++;
                    AppendLog($"[Warning] Skipped missing file: {entry.Path}");
                    continue;
                }

                var ext = Path.GetExtension(entry.Path).ToLowerInvariant();
                if (!AudioReaderFactory.IsSupported(ext))
                {
                    skipped++;
                    AppendLog($"[Warning] Unsupported audio format: {entry.Path}");
                    continue;
                }

                // Use the duration from the playlist when available, else read from file
                var duration = entry.Duration;
                if (duration < TimeSpan.Zero)
                {
                    try
                    {
                        duration = await Task.Run(() =>
                        {
                            using var reader = AudioReaderFactory.CreateReader(entry.Path);
                            return reader.TotalTime;
                        });
                    }
                    catch (Exception ex)
                    {
                        // Fall back to zero rather than keeping the negative sentinel value
                        duration = TimeSpan.Zero;
                        System.Diagnostics.Debug.WriteLine(
                            $"[PlaylistImport] Could not read duration for '{entry.Path}': {ex.Message}");
                    }
                }

                var title = !string.IsNullOrWhiteSpace(entry.Title)
                    ? entry.Title
                    : Path.GetFileNameWithoutExtension(entry.Path);

                _createTracks.Add(new AudioTrackItem
                {
                    TrackNumber = _createTracks.Count + 1,
                    Title = title,
                    FilePath = entry.Path,
                    Duration = duration
                });
                added++;
            }

            UpdateCreateTrackInfo();
            AppendLog($"[Playlist] Imported {added} track{(added != 1 ? "s" : "")} from '{Path.GetFileName(playlistPath)}'"
                      + (skipped > 0 ? $" ({skipped} skipped)" : ""));
        }
        catch (Exception ex)
        {
            AppendLog($"[Error] Could not import playlist '{Path.GetFileName(playlistPath)}': {ex.Message}");
        }
    }

    private async void OnExportPlaylistCreateClick(object? sender, RoutedEventArgs e)
    {
        if (_createTracks.Count == 0)
        {
            AppendLog("[Info] No tracks to export.");
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Playlist",
            SuggestedFileName = "AudioCD",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("M3U8 Playlist (UTF-8)") { Patterns = new[] { "*.m3u8" } },
                new FilePickerFileType("M3U Playlist")          { Patterns = new[] { "*.m3u" } },
                new FilePickerFileType("PLS Playlist")          { Patterns = new[] { "*.pls" } },
                new FilePickerFileType("WPL Playlist")          { Patterns = new[] { "*.wpl" } },
                new FilePickerFileType("ASX Playlist")          { Patterns = new[] { "*.asx" } }
            }
        });

        if (file == null) return;

        try
        {
            var outputPath = file.Path.LocalPath;

            // Ensure the file has a valid playlist extension
            if (PlaylistParser.FormatFromExtension(Path.GetExtension(outputPath)) == null)
            {
                outputPath = Path.ChangeExtension(outputPath, ".m3u8");
                AppendLog($"[Info] Using M3U8 format: '{Path.GetFileName(outputPath)}'");
            }

            var entries = _createTracks.Select(t => new PlaylistEntry
            {
                Path = t.FilePath,
                Title = t.Title,
                Duration = t.Duration
            }).ToList();

            var albumTitle = TxtCdAlbum?.Text;
            var playlistTitle = !string.IsNullOrWhiteSpace(albumTitle)
                ? albumTitle
                : "Audio CD";

            await Task.Run(() => PlaylistParser.Save(outputPath, entries, playlistTitle));
            AppendLog($"[Playlist] Exported {entries.Count} track{(entries.Count != 1 ? "s" : "")} to '{Path.GetFileName(outputPath)}'");
        }
        catch (Exception ex)
        {
            AppendLog($"[Error] Could not export playlist: {ex.Message}");
        }
    }

    // -----------------------------------------------------------------------
    // File management (Copy Music)
    // -----------------------------------------------------------------------
    private static readonly HashSet<string> MusicExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".flac", ".ogg", ".aiff", ".aif", ".aac", ".wma", ".m4a", ".opus", ".ape"
    };

    private async void OnAddCopyFilesClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add Music Files",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Music Files") { Patterns = new[] { "*.mp3", "*.wav", "*.flac", "*.ogg", "*.aiff", "*.aif", "*.aac", "*.wma", "*.m4a", "*.opus", "*.ape" } },
                new FilePickerFileType("All Files") { Patterns = new[] { "*.*" } }
            }
        });

        foreach (var file in files)
            AddCopyFile(file.Path.LocalPath);
    }

    private async void OnAddCopyFolderClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Music Folder",
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            var folderPath = folders[0].Path.LocalPath;
            foreach (var file in Directory.EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories))
            {
                if (MusicExtensions.Contains(Path.GetExtension(file)))
                    AddCopyFile(file);
            }
        }
    }

    private void AddCopyFile(string filePath)
    {
        try
        {
            var fi = new FileInfo(filePath);

            // Skip duplicates (same full path already in list)
            if (_copyFiles.Any(f => string.Equals(f.FilePath, fi.FullName, StringComparison.OrdinalIgnoreCase)))
                return;

            _copyFiles.Add(new MusicFileItem
            {
                FileName = fi.Name,
                FilePath = fi.FullName,
                FileSize = fi.Length
            });
            UpdateCopyFileInfo();
        }
        catch (Exception ex)
        {
            AppendLog($"[Error] Could not add '{Path.GetFileName(filePath)}': {ex.Message}");
        }
    }

    private void OnRemoveCopyFileClick(object? sender, RoutedEventArgs e)
    {
        if (LstCopyFiles.SelectedIndex >= 0 && LstCopyFiles.SelectedIndex < _copyFiles.Count)
        {
            _copyFiles.RemoveAt(LstCopyFiles.SelectedIndex);
            UpdateCopyFileInfo();
        }
    }

    private void UpdateCopyFileInfo()
    {
        var totalSize = _copyFiles.Sum(f => f.FileSize);
        TxtCopyFileCount.Text = $"({_copyFiles.Count} file{(_copyFiles.Count != 1 ? "s" : "")}, {FormatHelper.FormatBytes(totalSize)})";
    }

    // Drag-drop for copy files (also handles playlist files)
    private void OnCopyDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DragDropEffects.Copy;
    }

    private async void OnCopyDrop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer?.TryGetFiles();
        if (files != null)
        {
            foreach (var file in files)
            {
                if (file is IStorageFile storageFile)
                {
                    var ext = Path.GetExtension(storageFile.Name).ToLowerInvariant();
                    if (PlaylistParser.IsPlaylistFile(ext))
                        await ImportPlaylistToCopyFilesAsync(storageFile.Path.LocalPath);
                    else if (MusicExtensions.Contains(ext))
                        AddCopyFile(storageFile.Path.LocalPath);
                }
                else if (file is IStorageFolder storageFolder)
                {
                    var folderPath = storageFolder.Path.LocalPath;
                    foreach (var f in Directory.EnumerateFiles(folderPath, "*.*", SearchOption.AllDirectories))
                    {
                        if (MusicExtensions.Contains(Path.GetExtension(f)))
                            AddCopyFile(f);
                    }
                }
            }
        }
    }

    // -----------------------------------------------------------------------
    // Playlist import / export (Copy Music)
    // -----------------------------------------------------------------------
    private async void OnImportPlaylistCopyClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Playlist",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                PlaylistFileFilter,
                new FilePickerFileType("M3U Playlist")  { Patterns = new[] { "*.m3u" } },
                new FilePickerFileType("M3U8 Playlist") { Patterns = new[] { "*.m3u8" } },
                new FilePickerFileType("PLS Playlist")  { Patterns = new[] { "*.pls" } },
                new FilePickerFileType("WPL Playlist")  { Patterns = new[] { "*.wpl" } },
                new FilePickerFileType("ASX Playlist")  { Patterns = new[] { "*.asx" } },
                new FilePickerFileType("All Files")     { Patterns = new[] { "*.*" } }
            }
        });

        foreach (var file in files)
            await ImportPlaylistToCopyFilesAsync(file.Path.LocalPath);
    }

    private async Task ImportPlaylistToCopyFilesAsync(string playlistPath)
    {
        try
        {
            var entries = await Task.Run(() => PlaylistParser.Load(playlistPath));
            int added = 0, skipped = 0;

            foreach (var entry in entries)
            {
                if (!File.Exists(entry.Path))
                {
                    skipped++;
                    AppendLog($"[Warning] Skipped missing file: {entry.Path}");
                    continue;
                }

                AddCopyFile(entry.Path);
                added++;
            }

            AppendLog($"[Playlist] Imported {added} file{(added != 1 ? "s" : "")} from '{Path.GetFileName(playlistPath)}'"
                      + (skipped > 0 ? $" ({skipped} skipped)" : ""));
        }
        catch (Exception ex)
        {
            AppendLog($"[Error] Could not import playlist '{Path.GetFileName(playlistPath)}': {ex.Message}");
        }
    }

    private async void OnExportPlaylistCopyClick(object? sender, RoutedEventArgs e)
    {
        if (_copyFiles.Count == 0)
        {
            AppendLog("[Info] No files to export.");
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Playlist",
            SuggestedFileName = "MusicDisc",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("M3U8 Playlist (UTF-8)") { Patterns = new[] { "*.m3u8" } },
                new FilePickerFileType("M3U Playlist")          { Patterns = new[] { "*.m3u" } },
                new FilePickerFileType("PLS Playlist")          { Patterns = new[] { "*.pls" } },
                new FilePickerFileType("WPL Playlist")          { Patterns = new[] { "*.wpl" } },
                new FilePickerFileType("ASX Playlist")          { Patterns = new[] { "*.asx" } }
            }
        });

        if (file == null) return;

        try
        {
            var outputPath = file.Path.LocalPath;

            // Ensure the file has a valid playlist extension
            if (PlaylistParser.FormatFromExtension(Path.GetExtension(outputPath)) == null)
            {
                outputPath = Path.ChangeExtension(outputPath, ".m3u8");
                AppendLog($"[Info] Using M3U8 format: '{Path.GetFileName(outputPath)}'");
            }

            var entries = _copyFiles.Select(f => new PlaylistEntry
            {
                Path = f.FilePath,
                Title = Path.GetFileNameWithoutExtension(f.FileName)
            }).ToList();

            await Task.Run(() => PlaylistParser.Save(outputPath, entries, "Music Disc"));
            AppendLog($"[Playlist] Exported {entries.Count} file{(entries.Count != 1 ? "s" : "")} to '{Path.GetFileName(outputPath)}'");
        }
        catch (Exception ex)
        {
            AppendLog($"[Error] Could not export playlist: {ex.Message}");
        }
    }

    // -----------------------------------------------------------------------
    // Audio Player (preview)
    // -----------------------------------------------------------------------
    private void OnPreviewTrackClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string filePath)
            StartPlayback(filePath, TxtNowPlaying, BtnPlayPause, SliderPosition, TxtPlaybackTime);
    }

    private void OnPlayPauseClick(object? sender, RoutedEventArgs e)
    {
        TogglePlayPause(BtnPlayPause);
    }

    private void OnStopClick(object? sender, RoutedEventArgs e)
    {
        StopPlayback();
        UpdatePlayerUi(TxtNowPlaying, BtnPlayPause, SliderPosition, TxtPlaybackTime, stopped: true);
    }

    private void OnPreviewCopyFileClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string filePath)
            StartPlayback(filePath, TxtCopyNowPlaying, BtnCopyPlayPause, SliderCopyPosition, TxtCopyPlaybackTime);
    }

    private void OnCopyPlayPauseClick(object? sender, RoutedEventArgs e)
    {
        TogglePlayPause(BtnCopyPlayPause);
    }

    private void OnCopyStopClick(object? sender, RoutedEventArgs e)
    {
        StopPlayback();
        UpdatePlayerUi(TxtCopyNowPlaying, BtnCopyPlayPause, SliderCopyPosition, TxtCopyPlaybackTime, stopped: true);
    }

    // Track the active UI elements for the current playback
    private TextBlock? _activeNowPlaying;
    private Button? _activePlayPauseBtn;
    private Slider? _activePositionSlider;
    private TextBlock? _activePlaybackTime;

    private void StartPlayback(string filePath, TextBlock nowPlaying, Button playPauseBtn,
        Slider positionSlider, TextBlock playbackTime)
    {
        StopPlayback();

        _activeNowPlaying = nowPlaying;
        _activePlayPauseBtn = playPauseBtn;
        _activePositionSlider = positionSlider;
        _activePlaybackTime = playbackTime;

        try
        {
            // Try AudioFileReader first (handles WAV, MP3, AIFF with volume normalization).
            // Fall back to AudioReaderFactory for WMA and other formats.
            try
            {
                _audioReader = new AudioFileReader(filePath);
            }
            catch
            {
                _audioReader = AudioReaderFactory.CreateReader(filePath);
            }

            _player = new WaveOutEvent();
            _player.Init(_audioReader);
            _player.Play();

            nowPlaying.Text = $"🎵 {Path.GetFileName(filePath)}";
            playPauseBtn.Content = "⏸ Pause";
            positionSlider.Maximum = _audioReader.TotalTime.TotalSeconds;
            positionSlider.IsEnabled = true;

            _playbackTimer?.Start();
        }
        catch (Exception ex)
        {
            nowPlaying.Text = $"⚠ Cannot preview: {ex.Message}";
            playPauseBtn.Content = "▶ Play";
        }
    }

    private void TogglePlayPause(Button playPauseBtn)
    {
        if (_player == null) return;

        if (_player.PlaybackState == PlaybackState.Playing)
        {
            _player.Pause();
            playPauseBtn.Content = "▶ Play";
        }
        else
        {
            _player.Play();
            playPauseBtn.Content = "⏸ Pause";
        }
    }

    private void StopPlayback()
    {
        _playbackTimer?.Stop();

        if (_player != null)
        {
            try { _player.Stop(); } catch { /* best effort — player may already be stopped */ }
            try { _player.Dispose(); } catch { /* best effort */ }
            _player = null;
        }

        if (_audioReader != null)
        {
            try { _audioReader.Dispose(); } catch { /* best effort */ }
            _audioReader = null;
        }
    }

    private void OnPlaybackTimerTick(object? sender, EventArgs e)
    {
        if (_audioReader == null || _player == null) return;

        var current = _audioReader.CurrentTime;
        var total = _audioReader.TotalTime;

        if (_activePositionSlider != null)
            _activePositionSlider.Value = current.TotalSeconds;

        if (_activePlaybackTime != null)
        {
            if (total.TotalHours >= 1)
                _activePlaybackTime.Text = $"{current:h\\:mm\\:ss} / {total:h\\:mm\\:ss}";
            else
                _activePlaybackTime.Text = $"{current:mm\\:ss} / {total:mm\\:ss}";
        }

        if (_player.PlaybackState == PlaybackState.Stopped)
        {
            _playbackTimer?.Stop();
            if (_activePlayPauseBtn != null)
                _activePlayPauseBtn.Content = "▶ Play";
        }
    }

    private static void UpdatePlayerUi(TextBlock nowPlaying, Button playPauseBtn,
        Slider positionSlider, TextBlock playbackTime, bool stopped)
    {
        if (stopped)
        {
            nowPlaying.Text = "No track selected";
            playPauseBtn.Content = "▶ Play";
            positionSlider.Value = 0;
            positionSlider.IsEnabled = false;
            playbackTime.Text = "00:00 / 00:00";
        }
    }

    // -----------------------------------------------------------------------
    // Browse dialogs
    // -----------------------------------------------------------------------
    private async void OnBrowseCreateOutputClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Audio CD Image",
            SuggestedFileName = "AudioCD",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("BIN/CUE Image") { Patterns = new[] { "*.bin" } }
            }
        });

        if (file != null)
            TxtCreateOutputPath.Text = file.Path.LocalPath;
    }

    private async void OnBrowseRipOutputClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Output Folder for Ripped Tracks",
            AllowMultiple = false
        });

        if (folders.Count > 0)
            TxtRipOutputFolder.Text = folders[0].Path.LocalPath;
    }

    private void OnRefreshRipDrivesClick(object? sender, RoutedEventArgs e)
    {
        _ = LoadDrivesAsync();
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>Updates the file system combo when the copy music disc type changes.</summary>
    private void OnCopyDiscTypeChanged(object? sender, SelectionChangedEventArgs e)
    {
        var discType = GetSelectedText(CmbCopyDiscType, "CD");
        var currentFs = GetSelectedText(CmbCopyFilesystem, "");
        var compatibleFs = FormatHelper.GetCompatibleFileSystems(discType);

        CmbCopyFilesystem.Items.Clear();
        foreach (var fs in compatibleFs)
            CmbCopyFilesystem.Items.Add(new ComboBoxItem { Content = fs });

        // Try to re-select the previously selected file system if still compatible
        bool reselected = false;
        for (int i = 0; i < CmbCopyFilesystem.Items.Count; i++)
        {
            if (CmbCopyFilesystem.Items[i] is ComboBoxItem item &&
                string.Equals(item.Content?.ToString(), currentFs, StringComparison.OrdinalIgnoreCase))
            {
                CmbCopyFilesystem.SelectedIndex = i;
                reselected = true;
                break;
            }
        }
        if (!reselected)
        {
            var defaultFs = FormatHelper.GetDefaultFileSystem(discType);
            for (int i = 0; i < CmbCopyFilesystem.Items.Count; i++)
            {
                if (CmbCopyFilesystem.Items[i] is ComboBoxItem item &&
                    string.Equals(item.Content?.ToString(), defaultFs, StringComparison.OrdinalIgnoreCase))
                {
                    CmbCopyFilesystem.SelectedIndex = i;
                    reselected = true;
                    break;
                }
            }
            if (!reselected && CmbCopyFilesystem.Items.Count > 0)
                CmbCopyFilesystem.SelectedIndex = 0;
        }
    }

    private static string GetSelectedText(ComboBox cmb, string fallback)
    {
        if (cmb.SelectedItem is ComboBoxItem item)
            return item.Content?.ToString() ?? fallback;
        return cmb.SelectedItem?.ToString() ?? fallback;
    }

    private static (string fileSystem, string udfVersion) ParseFileSystem(string fsText)
    {
        if (fsText.Contains("UDF 2.60"))
            return ("UDF 2.60", "2.60");
        if (fsText.Contains("UDF 2.50"))
            return ("UDF 2.50", "2.50");
        if (fsText.Contains("UDF 2.01"))
            return ("UDF 2.01", "2.01");
        if (fsText.Contains("UDF 1.5"))
            return ("UDF 1.5", "1.50");
        if (fsText.Contains("UDF 1.02"))
            return ("UDF 1.02", "1.02");
        if (fsText.Contains("UDF") && fsText.Contains("ISO"))
            return ("ISO 9660 + UDF", "1.02");
        // "ISO 9660 + Joliet" must be checked before plain "Joliet"
        if (fsText.Contains("ISO") && fsText.Contains("Joliet"))
            return ("ISO 9660 + Joliet", "");
        if (fsText.Contains("Joliet"))
            return ("Joliet", "");
        if (fsText.Contains("Rock Ridge"))
            return ("Rock Ridge", "");
        if (fsText.Contains("HFS"))
            return ("HFS+", "");
        return ("ISO 9660", "");
    }

    private string GetTotalDuration()
    {
        var total = TimeSpan.FromTicks(_createTracks.Sum(t => t.Duration.Ticks));
        return total.TotalHours >= 1 ? total.ToString(@"h\:mm\:ss") : total.ToString(@"mm\:ss");
    }

    private string GetTotalCopySize()
    {
        return FormatHelper.FormatBytes(_copyFiles.Sum(f => f.FileSize));
    }

    private void AppendLog(string line)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            AppendLogCore(line);
        }
        else
        {
            Dispatcher.UIThread.Post(() => AppendLogCore(line));
        }
    }

    private void AppendLogCore(string line)
    {
        var timestampedLine = $"[{DateTime.Now:HH:mm:ss.fff}] {line}";
        TxtExecLog.Text = string.IsNullOrEmpty(TxtExecLog.Text)
            ? timestampedLine
            : TxtExecLog.Text + Environment.NewLine + timestampedLine;

        // Trim log to prevent unbounded memory growth (keep last ~20 KB)
        var text = TxtExecLog.Text;
        if (text != null && text.Length > 30_000)
        {
            var trimIdx = text.IndexOf('\n', text.Length - 20_000);
            if (trimIdx > 0)
                TxtExecLog.Text = "...\n" + text[(trimIdx + 1)..];
            else
                TxtExecLog.Text = "...\n" + text[(text.Length - 20_000)..];
        }

        // Auto-scroll to bottom
        TxtExecLog.CaretIndex = TxtExecLog.Text?.Length ?? 0;
    }
}

// -----------------------------------------------------------------------
// Data models for the wizard list items
// -----------------------------------------------------------------------

/// <summary>Represents an audio track in the Create Audio CD wizard.</summary>
public class AudioTrackItem
{
    public int TrackNumber { get; set; }
    public string Title { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public TimeSpan Duration { get; set; }
    public string DurationText => Duration.TotalHours >= 1
        ? Duration.ToString(@"h\:mm\:ss")
        : Duration.ToString(@"mm\:ss");
}

/// <summary>Represents a music file in the Copy Music to Disc wizard.</summary>
public class MusicFileItem
{
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string SizeText => FormatHelper.FormatBytes(FileSize);
}
