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
using OpenBurningSuite.Native.Video;
using OpenBurningSuite.Services;

namespace OpenBurningSuite.Views;

public partial class VideoWizardView : UserControl
{
    // -----------------------------------------------------------------------
    // Mode enum
    // -----------------------------------------------------------------------
    private enum VideoWizardMode { None, DvdFromFolder, BlurayFromFolder, BdavFromFolder, BluRay3DFromFolder, UhdBlurayFromFolder, Vcd, Svcd, Xsvcd }

    // -----------------------------------------------------------------------
    // State
    // -----------------------------------------------------------------------
    private VideoWizardMode _mode = VideoWizardMode.None;
    private int _currentStep = 1;

    private readonly ObservableCollection<VideoFileItem> _dvdFiles = new();
    private readonly ObservableCollection<VideoFileItem> _bdFiles = new();
    private readonly ObservableCollection<VideoFileItem> _bdavFiles = new();
    private readonly ObservableCollection<VideoFileItem> _bd3dFiles = new();
    private readonly ObservableCollection<VideoFileItem> _vcdFiles = new();
    private readonly ObservableCollection<VideoFileItem> _svcdFiles = new();
    private readonly ObservableCollection<VideoFileItem> _xsvcdFiles = new();
    private readonly ObservableCollection<VideoFileItem> _uhdBdFiles = new();

    private CancellationTokenSource? _cts;
    private bool _isRunning;

    // Audio playback (platform-safe: NAudio WaveOutEvent works on Windows;
    // on Linux/macOS we fall back to a simple status-only preview)
    private IWavePlayer? _player;
    private AudioFileReader? _audioReader;
    private DispatcherTimer? _playbackTimer;

    // Track the active UI elements for the current playback
    private TextBlock? _activeNowPlaying;
    private Button? _activePlayPauseBtn;
    private Slider? _activePositionSlider;
    private TextBlock? _activePlaybackTime;

    // Services
    private DiscDiscoveryService? _discovery;
    private List<DiscDrive> _drives = new();

    public VideoWizardView()
    {
        InitializeComponent();

        LstDvdFiles.ItemsSource = _dvdFiles;
        LstBdFiles.ItemsSource = _bdFiles;
        LstBdavFiles.ItemsSource = _bdavFiles;
        LstBd3dFiles.ItemsSource = _bd3dFiles;
        LstVcdFiles.ItemsSource = _vcdFiles;
        LstSvcdFiles.ItemsSource = _svcdFiles;
        LstXsvcdFiles.ItemsSource = _xsvcdFiles;
        LstUhdBdFiles.ItemsSource = _uhdBdFiles;

        _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _playbackTimer.Tick += OnPlaybackTimerTick;

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

            CmbDvdDrive.ItemsSource = _drives;
            CmbBdDrive.ItemsSource = _drives;
            CmbBdavDrive.ItemsSource = _drives;
            CmbBd3dDrive.ItemsSource = _drives;
            CmbVcdDrive.ItemsSource = _drives;
            CmbSvcdDrive.ItemsSource = _drives;
            CmbXsvcdDrive.ItemsSource = _drives;
            CmbUhdBdDrive.ItemsSource = _drives;

            if (_drives.Count > 0)
            {
                // Prefer a writable drive
                var writable = _drives.FirstOrDefault(d => d.CanWrite) ?? _drives[0];
                CmbDvdDrive.SelectedItem = writable;
                CmbBdDrive.SelectedItem = writable;
                CmbBdavDrive.SelectedItem = writable;
                CmbBd3dDrive.SelectedItem = writable;
                CmbVcdDrive.SelectedItem = writable;
                CmbSvcdDrive.SelectedItem = writable;
                CmbXsvcdDrive.SelectedItem = writable;
                CmbUhdBdDrive.SelectedItem = writable;
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
    private void OnModeDvd(object? sender, RoutedEventArgs e) => SetMode(VideoWizardMode.DvdFromFolder);
    private void OnModeBluray(object? sender, RoutedEventArgs e) => SetMode(VideoWizardMode.BlurayFromFolder);
    private void OnModeBdav(object? sender, RoutedEventArgs e) => SetMode(VideoWizardMode.BdavFromFolder);
    private void OnModeBd3d(object? sender, RoutedEventArgs e) => SetMode(VideoWizardMode.BluRay3DFromFolder);
    private void OnModeVcd(object? sender, RoutedEventArgs e) => SetMode(VideoWizardMode.Vcd);
    private void OnModeSvcd(object? sender, RoutedEventArgs e) => SetMode(VideoWizardMode.Svcd);
    private void OnModeXsvcd(object? sender, RoutedEventArgs e) => SetMode(VideoWizardMode.Xsvcd);
    private void OnModeUhdBluray(object? sender, RoutedEventArgs e) => SetMode(VideoWizardMode.UhdBlurayFromFolder);

    private void SetMode(VideoWizardMode mode)
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
        Step2DvdPanel.IsVisible = false;
        Step2BdPanel.IsVisible = false;
        Step2BdavPanel.IsVisible = false;
        Step2Bd3dPanel.IsVisible = false;
        Step2UhdBdPanel.IsVisible = false;
        Step2VcdPanel.IsVisible = false;
        Step2SvcdPanel.IsVisible = false;
        Step2XsvcdPanel.IsVisible = false;
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
                    case VideoWizardMode.DvdFromFolder:
                        Step2DvdPanel.IsVisible = true;
                        ScanDvdFolder();
                        break;
                    case VideoWizardMode.BlurayFromFolder:
                        Step2BdPanel.IsVisible = true;
                        ScanBdFolder();
                        break;
                    case VideoWizardMode.BdavFromFolder:
                        Step2BdavPanel.IsVisible = true;
                        ScanBdavFolder();
                        break;
                    case VideoWizardMode.BluRay3DFromFolder:
                        Step2Bd3dPanel.IsVisible = true;
                        ScanBd3dFolder();
                        break;
                    case VideoWizardMode.UhdBlurayFromFolder:
                        Step2UhdBdPanel.IsVisible = true;
                        ScanUhdBdFolder();
                        break;
                    case VideoWizardMode.Vcd:
                        Step2VcdPanel.IsVisible = true;
                        break;
                    case VideoWizardMode.Svcd:
                        Step2SvcdPanel.IsVisible = true;
                        break;
                    case VideoWizardMode.Xsvcd:
                        Step2XsvcdPanel.IsVisible = true;
                        break;
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
        SetStepIndicator(VStepInd1, VStepLbl1, _currentStep >= 1, _currentStep == 1);
        SetStepIndicator(VStepInd2, VStepLbl2, _currentStep >= 2, _currentStep == 2);
        SetStepIndicator(VStepInd3, VStepLbl3, _currentStep >= 3, _currentStep == 3);
        SetStepIndicator(VStepInd4, VStepLbl4, _currentStep >= 4, _currentStep == 4);
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
        BtnVideoBack.IsVisible = _currentStep > 1 && !_isRunning;
        BtnVideoNext.IsVisible = _currentStep == 2 && !_isRunning;
        BtnVideoExecute.IsVisible = _currentStep == 3 && !_isRunning;
        BtnVideoCancel.IsVisible = _currentStep == 4 && _isRunning;
        BtnVideoFinish.IsVisible = _currentStep == 4 && !_isRunning;
    }

    private void OnVideoBackClick(object? sender, RoutedEventArgs e)
    {
        StopPlayback();
        GoToStep(_currentStep - 1);
    }

    private void OnVideoNextClick(object? sender, RoutedEventArgs e)
    {
        // Validate step 2 before proceeding
        var error = ValidateStep2();
        if (error != null)
        {
            AppendVideoLog($"[Error] {error}");
            return;
        }
        GoToStep(3);
    }

    private void OnVideoFinishClick(object? sender, RoutedEventArgs e)
    {
        // Reset wizard to step 1
        _mode = VideoWizardMode.None;
        _isRunning = false;
        GoToStep(1);
    }

    private async void OnVideoExecuteClick(object? sender, RoutedEventArgs e)
    {
        GoToStep(4);
        _isRunning = true;
        UpdateNavButtons();

        // Reset step 4 UI for a fresh operation
        PrgVideoExec.Value = 0;
        TxtVideoExecPercent.Text = "0%";
        TxtVideoExecSpeed.Text = string.Empty;
        TxtVideoExecStatus.Text = "Preparing...";
        TxtVideoExecLog.Text = string.Empty;
        TxtVideoStep4Title.Text = "Executing...";

        // Reset disc visualization for a fresh operation
        DiscVizBorder.IsVisible = false;
        DiscViz.IsActive = false;
        DiscViz.IsCompleted = false;
        DiscViz.VerifyPassed = null;
        DiscViz.BadSectors = 0;
        DiscViz.PercentComplete = 0;
        DiscViz.CurrentLba = 0;
        DiscViz.TotalSectors = 0;

        // Configure layer visualization based on the video disc type.
        // Video DVDs (DVD-9) use dual-layer OTP; BD/UHD BD can be DL.
        // We infer from the wizard mode; the drive's actual media type may
        // be set later when burning starts.
        var videoMediaHint = _mode switch
        {
            VideoWizardMode.DvdFromFolder => "DVD",      // Could be DVD-5 or DVD-9
            VideoWizardMode.BlurayFromFolder => "BD",
            VideoWizardMode.BdavFromFolder => "BD",
            VideoWizardMode.BluRay3DFromFolder => "BD",
            VideoWizardMode.UhdBlurayFromFolder => "BD",
            _ => "CD"  // VCD/SVCD/XSVCD are CD
        };
        DiscViz.ConfigureForMedia(videoMediaHint);

        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        try
        {
            StopPlayback();

            switch (_mode)
            {
                case VideoWizardMode.DvdFromFolder:
                    await ExecuteDvdFromFolderAsync(_cts.Token);
                    break;
                case VideoWizardMode.BlurayFromFolder:
                    await ExecuteBlurayFromFolderAsync(_cts.Token);
                    break;
                case VideoWizardMode.BdavFromFolder:
                    await ExecuteBdavFromFolderAsync(_cts.Token);
                    break;
                case VideoWizardMode.BluRay3DFromFolder:
                    await ExecuteBluRay3DFromFolderAsync(_cts.Token);
                    break;
                case VideoWizardMode.UhdBlurayFromFolder:
                    await ExecuteUhdBlurayFromFolderAsync(_cts.Token);
                    break;
                case VideoWizardMode.Vcd:
                    await ExecuteVcdAsync(_cts.Token);
                    break;
                case VideoWizardMode.Svcd:
                    await ExecuteSvcdAsync(_cts.Token);
                    break;
                case VideoWizardMode.Xsvcd:
                    await ExecuteXsvcdAsync(_cts.Token);
                    break;
            }

            TxtVideoStep4Title.Text = "✅ Complete!";
            TxtVideoExecStatus.Text = "Operation completed successfully.";
        }
        catch (OperationCanceledException)
        {
            TxtVideoStep4Title.Text = "⏹ Cancelled";
            TxtVideoExecStatus.Text = "Operation was cancelled by user.";
            AppendVideoLog("[Info] Operation cancelled.");
            DiscViz.IsActive = false;
            DiscViz.IsCompleted = false;
            DiscViz.UpdateVisualization();
        }
        catch (Exception ex)
        {
            TxtVideoStep4Title.Text = "❌ Failed";
            TxtVideoExecStatus.Text = $"Error: {ex.Message}";
            AppendVideoLog($"[Error] {ex.Message}");
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

    private void OnVideoCancelClick(object? sender, RoutedEventArgs e)
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
            case VideoWizardMode.DvdFromFolder:
                if (string.IsNullOrWhiteSpace(TxtDvdSourceFolder.Text))
                    return "Please specify the VIDEO_TS source folder.";
                var dvdFolder = TxtDvdSourceFolder.Text!.Trim();
                if (!IsValidDvdFolder(dvdFolder))
                    return "The specified folder does not contain a valid VIDEO_TS structure.";
                if (string.IsNullOrWhiteSpace(TxtDvdOutputPath.Text))
                    return "Please specify an output image path.";
                {
                    var outDir = Path.GetDirectoryName(TxtDvdOutputPath.Text.Trim());
                    if (!string.IsNullOrWhiteSpace(outDir) && !Directory.Exists(outDir))
                        return $"Output directory does not exist: {outDir}";
                }
                if (ChkDvdBurnDisc.IsChecked == true && CmbDvdDrive.SelectedItem == null)
                    return "Please select a target drive for burning.";
                return null;

            case VideoWizardMode.BlurayFromFolder:
                if (string.IsNullOrWhiteSpace(TxtBdSourceFolder.Text))
                    return "Please specify the BDMV source folder.";
                var bdFolder = TxtBdSourceFolder.Text!.Trim();
                if (!IsValidBdFolder(bdFolder))
                    return "The specified folder does not contain a valid BDMV structure.";
                if (string.IsNullOrWhiteSpace(TxtBdOutputPath.Text))
                    return "Please specify an output image path.";
                {
                    var outDir = Path.GetDirectoryName(TxtBdOutputPath.Text.Trim());
                    if (!string.IsNullOrWhiteSpace(outDir) && !Directory.Exists(outDir))
                        return $"Output directory does not exist: {outDir}";
                }
                if (ChkBdBurnDisc.IsChecked == true && CmbBdDrive.SelectedItem == null)
                    return "Please select a target drive for burning.";
                return null;

            case VideoWizardMode.BdavFromFolder:
                if (string.IsNullOrWhiteSpace(TxtBdavSourceFolder.Text))
                    return "Please specify the BDAV source folder.";
                var bdavFolder = TxtBdavSourceFolder.Text!.Trim();
                if (!IsValidBdavFolder(bdavFolder))
                    return "The specified folder does not contain a valid BDAV structure (requires STREAM and CLIPINF or PLAYLIST directories under BDAV/).";
                if (string.IsNullOrWhiteSpace(TxtBdavOutputPath.Text))
                    return "Please specify an output image path.";
                {
                    var outDir = Path.GetDirectoryName(TxtBdavOutputPath.Text.Trim());
                    if (!string.IsNullOrWhiteSpace(outDir) && !Directory.Exists(outDir))
                        return $"Output directory does not exist: {outDir}";
                }
                if (ChkBdavBurnDisc.IsChecked == true && CmbBdavDrive.SelectedItem == null)
                    return "Please select a target drive for burning.";
                return null;

            case VideoWizardMode.BluRay3DFromFolder:
                if (string.IsNullOrWhiteSpace(TxtBd3dSourceFolder.Text))
                    return "Please specify the BDMV 3D source folder.";
                var bd3dFolder = TxtBd3dSourceFolder.Text!.Trim();
                if (!IsValidBdFolder(bd3dFolder))
                    return "The specified folder does not contain a valid BDMV structure.";
                if (string.IsNullOrWhiteSpace(TxtBd3dOutputPath.Text))
                    return "Please specify an output image path.";
                {
                    var outDir = Path.GetDirectoryName(TxtBd3dOutputPath.Text.Trim());
                    if (!string.IsNullOrWhiteSpace(outDir) && !Directory.Exists(outDir))
                        return $"Output directory does not exist: {outDir}";
                }
                // Validate dependent view for Frame Packed mode
                var mode3d = GetBd3dMode();
                if (mode3d == "FramePacked" && string.IsNullOrWhiteSpace(TxtBd3dDependentViewPath.Text))
                    return "Frame Packed (MVC) mode requires a dependent view (right-eye) video file.";
                if (mode3d == "FramePacked" && !string.IsNullOrWhiteSpace(TxtBd3dDependentViewPath.Text)
                    && !File.Exists(TxtBd3dDependentViewPath.Text.Trim()))
                    return "The dependent view (right-eye) video file was not found.";
                if (ChkBd3dBurnDisc.IsChecked == true && CmbBd3dDrive.SelectedItem == null)
                    return "Please select a target drive for burning.";
                return null;

            case VideoWizardMode.Vcd:
                if (_vcdFiles.Count == 0)
                    return "Please add at least one video file.";
                if (string.IsNullOrWhiteSpace(TxtVcdOutputPath.Text))
                    return "Please specify an output image path.";
                {
                    var outDir = Path.GetDirectoryName(TxtVcdOutputPath.Text.Trim());
                    if (!string.IsNullOrWhiteSpace(outDir) && !Directory.Exists(outDir))
                        return $"Output directory does not exist: {outDir}";
                }
                if (ChkVcdBurnDisc.IsChecked == true && CmbVcdDrive.SelectedItem == null)
                    return "Please select a target drive for burning.";
                return null;

            case VideoWizardMode.Svcd:
                if (_svcdFiles.Count == 0)
                    return "Please add at least one video file.";
                if (string.IsNullOrWhiteSpace(TxtSvcdOutputPath.Text))
                    return "Please specify an output image path.";
                {
                    var outDir = Path.GetDirectoryName(TxtSvcdOutputPath.Text.Trim());
                    if (!string.IsNullOrWhiteSpace(outDir) && !Directory.Exists(outDir))
                        return $"Output directory does not exist: {outDir}";
                }
                if (ChkSvcdBurnDisc.IsChecked == true && CmbSvcdDrive.SelectedItem == null)
                    return "Please select a target drive for burning.";
                return null;

            case VideoWizardMode.Xsvcd:
                if (_xsvcdFiles.Count == 0)
                    return "Please add at least one video file.";
                if (string.IsNullOrWhiteSpace(TxtXsvcdOutputPath.Text))
                    return "Please specify an output image path.";
                {
                    var outDir = Path.GetDirectoryName(TxtXsvcdOutputPath.Text.Trim());
                    if (!string.IsNullOrWhiteSpace(outDir) && !Directory.Exists(outDir))
                        return $"Output directory does not exist: {outDir}";
                }
                if (ChkXsvcdBurnDisc.IsChecked == true && CmbXsvcdDrive.SelectedItem == null)
                    return "Please select a target drive for burning.";
                return null;

            case VideoWizardMode.UhdBlurayFromFolder:
                if (string.IsNullOrWhiteSpace(TxtUhdBdSourceFolder.Text))
                    return "Please specify the BDMV source folder.";
                var uhdFolder = TxtUhdBdSourceFolder.Text!.Trim();
                if (!IsValidBdFolder(uhdFolder))
                    return "The specified folder does not contain a valid BDMV structure.";
                if (string.IsNullOrWhiteSpace(TxtUhdBdOutputPath.Text))
                    return "Please specify an output image path.";
                {
                    var outDir = Path.GetDirectoryName(TxtUhdBdOutputPath.Text.Trim());
                    if (!string.IsNullOrWhiteSpace(outDir) && !Directory.Exists(outDir))
                        return $"Output directory does not exist: {outDir}";
                }
                if (ChkUhdBdBurnDisc.IsChecked == true && CmbUhdBdDrive.SelectedItem == null)
                    return "Please select a target drive for burning.";
                return null;

            default:
                return "Please choose a task first.";
        }
    }

    private static bool IsValidDvdFolder(string path)
    {
        if (!Directory.Exists(path)) return false;
        // Check if this IS the VIDEO_TS folder
        if (Path.GetFileName(path).Equals("VIDEO_TS", StringComparison.OrdinalIgnoreCase))
            return true;
        // Check if it contains a VIDEO_TS subfolder
        return Directory.Exists(Path.Combine(path, "VIDEO_TS"));
    }

    private static bool IsValidBdFolder(string path)
    {
        if (!Directory.Exists(path)) return false;
        if (Path.GetFileName(path).Equals("BDMV", StringComparison.OrdinalIgnoreCase))
            return true;
        return Directory.Exists(Path.Combine(path, "BDMV"));
    }

    private static bool IsValidBdavFolder(string path)
    {
        if (!Directory.Exists(path)) return false;
        // Check if this IS the BDAV folder
        if (Path.GetFileName(path).Equals("BDAV", StringComparison.OrdinalIgnoreCase))
        {
            return Directory.Exists(Path.Combine(path, "STREAM"))
                || Directory.Exists(Path.Combine(path, "CLIPINF"))
                || Directory.Exists(Path.Combine(path, "PLAYLIST"));
        }
        // Check if it contains a BDAV subfolder
        var bdavDir = Path.Combine(path, "BDAV");
        if (!Directory.Exists(bdavDir)) return false;
        return Directory.Exists(Path.Combine(bdavDir, "STREAM"))
            || Directory.Exists(Path.Combine(bdavDir, "CLIPINF"))
            || Directory.Exists(Path.Combine(bdavDir, "PLAYLIST"));
    }

    // -----------------------------------------------------------------------
    // Summary (Step 3)
    // -----------------------------------------------------------------------
    private void PopulateSummary()
    {
        VideoSummaryContent.Children.Clear();

        switch (_mode)
        {
            case VideoWizardMode.DvdFromFolder:
                AddVideoSummaryLine("📀 Task", "DVD-Video from Folder");
                AddVideoSummaryLine("📂 Source Folder", TxtDvdSourceFolder.Text ?? "(not set)");
                AddVideoSummaryLine("🎬 Video Standard", GetSelectedText(CmbDvdVideoStandard, "NTSC"));
                AddVideoSummaryLine("📐 Aspect Ratio", GetSelectedText(CmbDvdAspectRatio, "16:9"));
                AddVideoSummaryLine("🏷 Volume Label",
                    string.IsNullOrWhiteSpace(TxtDvdVolumeLabel.Text) ? "DVD_VIDEO" : TxtDvdVolumeLabel.Text!);
                AddVideoSummaryLine("🌍 Region", GetSelectedText(CmbDvdRegion, "Region Free"));
                AddVideoSummaryLine("💾 Output Image", TxtDvdOutputPath.Text ?? "(not set)");
                if (ChkDvdBurnDisc.IsChecked == true)
                {
                    var drive = CmbDvdDrive.SelectedItem as DiscDrive;
                    AddVideoSummaryLine("🔥 Burn to Drive", drive?.DisplayName ?? "(none)");
                    AddVideoSummaryLine("⚡ Write Speed", GetSelectedText(CmbDvdSpeed, "Auto"));
                    AddVideoSummaryLine("📝 Write Mode", GetSelectedText(CmbDvdWriteMode, "DAO"));
                    AddVideoSummaryLine("📦 Copies", ((int)(NumDvdCopies.Value ?? 1)).ToString());
                }
                else
                {
                    AddVideoSummaryLine("🔥 Burn to Disc", "No (image only)");
                }
                break;

            case VideoWizardMode.BlurayFromFolder:
                AddVideoSummaryLine("💿 Task", "Video Blu-ray from Folder");
                AddVideoSummaryLine("📂 Source Folder", TxtBdSourceFolder.Text ?? "(not set)");
                AddVideoSummaryLine("🏷 Volume Label",
                    string.IsNullOrWhiteSpace(TxtBdVolumeLabel.Text) ? "BLURAY_DISC" : TxtBdVolumeLabel.Text!);
                AddVideoSummaryLine("💾 Output Image", TxtBdOutputPath.Text ?? "(not set)");
                if (ChkBdBurnDisc.IsChecked == true)
                {
                    var drive = CmbBdDrive.SelectedItem as DiscDrive;
                    AddVideoSummaryLine("🔥 Burn to Drive", drive?.DisplayName ?? "(none)");
                    AddVideoSummaryLine("⚡ Write Speed", GetSelectedText(CmbBdSpeed, "Auto"));
                    AddVideoSummaryLine("📦 Copies", ((int)(NumBdCopies.Value ?? 1)).ToString());
                }
                else
                {
                    AddVideoSummaryLine("🔥 Burn to Disc", "No (image only)");
                }
                break;

            case VideoWizardMode.BdavFromFolder:
                AddVideoSummaryLine("📹 Task", "BDAV Recording from Folder");
                AddVideoSummaryLine("📂 Source Folder", TxtBdavSourceFolder.Text ?? "(not set)");
                AddVideoSummaryLine("🏷 Volume Label",
                    string.IsNullOrWhiteSpace(TxtBdavVolumeLabel.Text) ? "BDAV_DISC" : TxtBdavVolumeLabel.Text!);
                AddVideoSummaryLine("💿 Disc Size", GetSelectedText(CmbBdavDiscSize, "BD-25 (25 GB)"));
                if (DpBdavRecordingDate.SelectedDate.HasValue)
                    AddVideoSummaryLine("📅 Recording Date", DpBdavRecordingDate.SelectedDate.Value.ToString("yyyy-MM-dd"));
                AddVideoSummaryLine("🌍 TZ Offset", $"{(int)(NumBdavTimezoneOffset.Value ?? 0)} min");
                AddVideoSummaryLine("💾 Output Image", TxtBdavOutputPath.Text ?? "(not set)");
                if (ChkBdavBurnDisc.IsChecked == true)
                {
                    var drive = CmbBdavDrive.SelectedItem as DiscDrive;
                    AddVideoSummaryLine("🔥 Burn to Drive", drive?.DisplayName ?? "(none)");
                    AddVideoSummaryLine("⚡ Write Speed", GetSelectedText(CmbBdavSpeed, "Auto"));
                    AddVideoSummaryLine("📦 Copies", ((int)(NumBdavCopies.Value ?? 1)).ToString());
                }
                else
                {
                    AddVideoSummaryLine("🔥 Burn to Disc", "No (image only)");
                }
                break;

            case VideoWizardMode.BluRay3DFromFolder:
                AddVideoSummaryLine("🎥 Task", "3D Blu-ray from Folder");
                AddVideoSummaryLine("📂 Source Folder", TxtBd3dSourceFolder.Text ?? "(not set)");
                AddVideoSummaryLine("🏷 Volume Label",
                    string.IsNullOrWhiteSpace(TxtBd3dVolumeLabel.Text) ? "BLURAY3D_DISC" : TxtBd3dVolumeLabel.Text!);
                AddVideoSummaryLine("💿 Disc Size", GetSelectedText(CmbBd3dDiscSize, "BD-50 (50 GB)"));
                AddVideoSummaryLine("🎥 3D Mode", GetSelectedText(CmbBd3dMode, "Frame Packed (MVC)"));
                AddVideoSummaryLine("🔧 Depth Offset", ((int)(NumBd3dDepthOffset.Value ?? 0)).ToString());
                if (!string.IsNullOrWhiteSpace(TxtBd3dDependentViewPath.Text))
                    AddVideoSummaryLine("👁 Dependent View", TxtBd3dDependentViewPath.Text!);
                AddVideoSummaryLine("💾 Output Image", TxtBd3dOutputPath.Text ?? "(not set)");
                if (ChkBd3dBurnDisc.IsChecked == true)
                {
                    var drive = CmbBd3dDrive.SelectedItem as DiscDrive;
                    AddVideoSummaryLine("🔥 Burn to Drive", drive?.DisplayName ?? "(none)");
                    AddVideoSummaryLine("⚡ Write Speed", GetSelectedText(CmbBd3dSpeed, "Auto"));
                    AddVideoSummaryLine("📦 Copies", ((int)(NumBd3dCopies.Value ?? 1)).ToString());
                }
                else
                {
                    AddVideoSummaryLine("🔥 Burn to Disc", "No (image only)");
                }
                break;

            case VideoWizardMode.Vcd:
                AddVideoSummaryLine("📼 Task", "Video CD (VCD)");
                AddVideoSummaryLine("🎬 Video Files", $"{_vcdFiles.Count} file(s)");
                AddVideoSummaryLine("📺 Video Standard", GetSelectedText(CmbVcdVideoStandard, "NTSC"));
                AddVideoSummaryLine("📀 VCD Version", GetSelectedText(CmbVcdVersion, "2.0"));
                AddVideoSummaryLine("🏷 Disc Label",
                    string.IsNullOrWhiteSpace(TxtVcdDiscLabel.Text) ? "VIDEOCD" : TxtVcdDiscLabel.Text!);
                AddVideoSummaryLine("💾 Output Image", TxtVcdOutputPath.Text ?? "(not set)");
                if (ChkVcdBurnDisc.IsChecked == true)
                {
                    var drive = CmbVcdDrive.SelectedItem as DiscDrive;
                    AddVideoSummaryLine("🔥 Burn to Drive", drive?.DisplayName ?? "(none)");
                    AddVideoSummaryLine("⚡ Write Speed", GetSelectedText(CmbVcdSpeed, "Auto"));
                    AddVideoSummaryLine("📝 Write Mode", GetSelectedText(CmbVcdWriteMode, "DAO"));
                    AddVideoSummaryLine("📦 Copies", ((int)(NumVcdCopies.Value ?? 1)).ToString());
                }
                else
                {
                    AddVideoSummaryLine("🔥 Burn to Disc", "No (image only)");
                }
                break;

            case VideoWizardMode.Svcd:
                AddVideoSummaryLine("📼 Task", "Super Video CD (SVCD)");
                AddVideoSummaryLine("🎬 Video Files", $"{_svcdFiles.Count} file(s)");
                AddVideoSummaryLine("📺 Video Standard", GetSelectedText(CmbSvcdVideoStandard, "NTSC"));
                AddVideoSummaryLine("🏷 Disc Label",
                    string.IsNullOrWhiteSpace(TxtSvcdDiscLabel.Text) ? "SUPERVCD" : TxtSvcdDiscLabel.Text!);
                AddVideoSummaryLine("💾 Output Image", TxtSvcdOutputPath.Text ?? "(not set)");
                if (ChkSvcdBurnDisc.IsChecked == true)
                {
                    var drive = CmbSvcdDrive.SelectedItem as DiscDrive;
                    AddVideoSummaryLine("🔥 Burn to Drive", drive?.DisplayName ?? "(none)");
                    AddVideoSummaryLine("⚡ Write Speed", GetSelectedText(CmbSvcdSpeed, "Auto"));
                    AddVideoSummaryLine("📝 Write Mode", GetSelectedText(CmbSvcdWriteMode, "DAO"));
                    AddVideoSummaryLine("📦 Copies", ((int)(NumSvcdCopies.Value ?? 1)).ToString());
                }
                else
                {
                    AddVideoSummaryLine("🔥 Burn to Disc", "No (image only)");
                }
                break;

            case VideoWizardMode.Xsvcd:
                AddVideoSummaryLine("📼 Task", "eXtended Super Video CD (XSVCD)");
                AddVideoSummaryLine("🎬 Video Files", $"{_xsvcdFiles.Count} file(s)");
                AddVideoSummaryLine("📺 Video Standard", GetSelectedText(CmbXsvcdVideoStandard, "NTSC"));
                AddVideoSummaryLine("🏷 Disc Label",
                    string.IsNullOrWhiteSpace(TxtXsvcdDiscLabel.Text) ? "XSVCD" : TxtXsvcdDiscLabel.Text!);
                AddVideoSummaryLine("💾 Output Image", TxtXsvcdOutputPath.Text ?? "(not set)");
                if (ChkXsvcdBurnDisc.IsChecked == true)
                {
                    var drive = CmbXsvcdDrive.SelectedItem as DiscDrive;
                    AddVideoSummaryLine("🔥 Burn to Drive", drive?.DisplayName ?? "(none)");
                    AddVideoSummaryLine("⚡ Write Speed", GetSelectedText(CmbXsvcdSpeed, "Auto"));
                    AddVideoSummaryLine("📝 Write Mode", GetSelectedText(CmbXsvcdWriteMode, "DAO"));
                    AddVideoSummaryLine("📦 Copies", ((int)(NumXsvcdCopies.Value ?? 1)).ToString());
                }
                else
                {
                    AddVideoSummaryLine("🔥 Burn to Disc", "No (image only)");
                }
                break;

            case VideoWizardMode.UhdBlurayFromFolder:
                AddVideoSummaryLine("🎬 Task", "UHD Blu-ray from Folder");
                AddVideoSummaryLine("📂 Source Folder", TxtUhdBdSourceFolder.Text ?? "(not set)");
                AddVideoSummaryLine("🏷 Volume Label",
                    string.IsNullOrWhiteSpace(TxtUhdBdVolumeLabel.Text) ? "UHD_BLURAY" : TxtUhdBdVolumeLabel.Text!);
                AddVideoSummaryLine("💿 Disc Size", GetSelectedText(CmbUhdBdDiscSize, "UHD BD-66 (66 GB)"));
                AddVideoSummaryLine("🎨 HDR Mode", GetSelectedText(CmbUhdBdHdrMode, "HDR10"));
                AddVideoSummaryLine("💾 Output Image", TxtUhdBdOutputPath.Text ?? "(not set)");
                if (ChkUhdBdBurnDisc.IsChecked == true)
                {
                    var drive = CmbUhdBdDrive.SelectedItem as DiscDrive;
                    AddVideoSummaryLine("🔥 Burn to Drive", drive?.DisplayName ?? "(none)");
                    AddVideoSummaryLine("⚡ Write Speed", GetSelectedText(CmbUhdBdSpeed, "Auto"));
                    AddVideoSummaryLine("📦 Copies", ((int)(NumUhdBdCopies.Value ?? 1)).ToString());
                }
                else
                {
                    AddVideoSummaryLine("🔥 Burn to Disc", "No (image only)");
                }
                break;
        }
    }

    private void AddVideoSummaryLine(string label, string value)
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
        VideoSummaryContent.Children.Add(sp);
    }

    // -----------------------------------------------------------------------
    // EXECUTE: DVD-Video from Folder
    // -----------------------------------------------------------------------
    private async Task ExecuteDvdFromFolderAsync(CancellationToken ct)
    {
        TxtVideoStep4Title.Text = "📀 Creating DVD-Video Image...";

        var sourceFolder = TxtDvdSourceFolder.Text!.Trim();
        // If the user pointed at VIDEO_TS itself, use the parent folder
        if (Path.GetFileName(sourceFolder).Equals("VIDEO_TS", StringComparison.OrdinalIgnoreCase))
            sourceFolder = Path.GetDirectoryName(sourceFolder) ?? sourceFolder;

        var outputPath = TxtDvdOutputPath.Text?.Trim() ?? "dvd_video.iso";
        var volumeLabel = string.IsNullOrWhiteSpace(TxtDvdVolumeLabel.Text)
            ? "DVD_VIDEO"
            : TxtDvdVolumeLabel.Text!.Trim();

        var buildJob = new BuildJob
        {
            DiscType = "DVD-5",
            SourceFolder = sourceFolder,
            OutputImagePath = outputPath,
            FileSystem = "ISO 9660 + UDF",
            UdfVersion = "1.02",
            VolumeLabel = volumeLabel,
            JolietLongFilenames = false
        };

        AppendVideoLog("[Info] Building DVD-Video image (ISO)...");
        var buildService = new BuildService();
        bool burnRequested = ChkDvdBurnDisc.IsChecked == true;

        var buildProgress = new Progress<BuildProgress>(p =>
        {
            PrgVideoExec.Value = burnRequested ? p.PercentComplete * 0.5 : p.PercentComplete;
            TxtVideoExecPercent.Text = $"{(int)PrgVideoExec.Value}%";
            if (!string.IsNullOrEmpty(p.StatusMessage))
                TxtVideoExecStatus.Text = p.StatusMessage;
            if (p.Remaining.TotalSeconds > 0)
                TxtVideoExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)} | ETA: {FormatHelper.FormatTimeSpan(p.Remaining)}";
            else if (p.Elapsed.TotalSeconds > 0)
                TxtVideoExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)}";
            if (!string.IsNullOrEmpty(p.LogLine))
                AppendVideoLog(p.LogLine);
        });

        await Task.Run(() => buildService.BuildImageAsync(buildJob, buildProgress, ct));
        AppendVideoLog($"[Info] DVD-Video image created: {outputPath}");

        // Phase 2: Burn to disc (optional)
        if (burnRequested)
        {
            var drive = CmbDvdDrive.SelectedItem as DiscDrive;
            if (drive == null)
            {
                AppendVideoLog("[Warning] No drive selected — skipping burn.");
                return;
            }

            var burnJob = new BurnJob
            {
                ImagePath = outputPath,
                DevicePath = drive.DevicePath,
                WriteMode = GetSelectedText(CmbDvdWriteMode, "DAO (Disc At Once)"),
                WriteSpeed = GetSelectedText(CmbDvdSpeed, "Auto"),
                Copies = (int)(NumDvdCopies.Value ?? 1),
                EjectAfterBurn = ChkDvdEject.IsChecked == true,
                VerifyAfterBurn = ChkDvdVerify.IsChecked == true,
                SimulateOnly = ChkDvdSimulate.IsChecked == true,
                BufferUnderrunProtection = ChkDvdBup.IsChecked == true,
                CloseDisc = true
            };

            AppendVideoLog($"[Info] Burning to {drive.DisplayName}...");
            TxtVideoExecStatus.Text = "Burning DVD-Video...";

            var burnService = new BurnService();
            var burnProgress = new Progress<BurnProgress>(p =>
            {
                PrgVideoExec.Value = 50 + p.PercentComplete * 0.5;
                TxtVideoExecPercent.Text = $"{(int)PrgVideoExec.Value}%";
                if (p.CurrentSpeedX > 0)
                    TxtVideoExecSpeed.Text = $"{p.CurrentSpeedX:F1}x";
                if (p.Remaining.TotalSeconds > 0)
                    TxtVideoExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)} | ETA: {FormatHelper.FormatTimeSpan(p.Remaining)}";
                else if (p.Elapsed.TotalSeconds > 0)
                    TxtVideoExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)}";
                if (!string.IsNullOrEmpty(p.StatusMessage))
                    TxtVideoExecStatus.Text = p.StatusMessage;
                if (!string.IsNullOrEmpty(p.LogLine))
                    AppendVideoLog(p.LogLine);

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
            AppendVideoLog("[Info] Burn complete!");

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
            PrgVideoExec.Value = 100;
            TxtVideoExecPercent.Text = "100%";
        });
    }

    // -----------------------------------------------------------------------
    // EXECUTE: Blu-ray from Folder
    // -----------------------------------------------------------------------
    private async Task ExecuteBlurayFromFolderAsync(CancellationToken ct)
    {
        TxtVideoStep4Title.Text = "💿 Creating Blu-ray Image...";

        var sourceFolder = TxtBdSourceFolder.Text!.Trim();
        // If the user pointed at BDMV itself, use the parent folder
        if (Path.GetFileName(sourceFolder).Equals("BDMV", StringComparison.OrdinalIgnoreCase))
            sourceFolder = Path.GetDirectoryName(sourceFolder) ?? sourceFolder;

        var outputPath = TxtBdOutputPath.Text?.Trim() ?? "bluray.iso";
        var volumeLabel = string.IsNullOrWhiteSpace(TxtBdVolumeLabel.Text)
            ? "BLURAY_DISC"
            : TxtBdVolumeLabel.Text!.Trim();

        var buildJob = new BuildJob
        {
            DiscType = "BD-25",
            SourceFolder = sourceFolder,
            OutputImagePath = outputPath,
            FileSystem = "UDF 2.50",
            UdfVersion = "2.50",
            VolumeLabel = volumeLabel,
            JolietLongFilenames = false
        };

        AppendVideoLog("[Info] Building Blu-ray image (ISO)...");
        var buildService = new BuildService();
        bool burnRequested = ChkBdBurnDisc.IsChecked == true;

        var buildProgress = new Progress<BuildProgress>(p =>
        {
            PrgVideoExec.Value = burnRequested ? p.PercentComplete * 0.5 : p.PercentComplete;
            TxtVideoExecPercent.Text = $"{(int)PrgVideoExec.Value}%";
            if (!string.IsNullOrEmpty(p.StatusMessage))
                TxtVideoExecStatus.Text = p.StatusMessage;
            if (p.Remaining.TotalSeconds > 0)
                TxtVideoExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)} | ETA: {FormatHelper.FormatTimeSpan(p.Remaining)}";
            else if (p.Elapsed.TotalSeconds > 0)
                TxtVideoExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)}";
            if (!string.IsNullOrEmpty(p.LogLine))
                AppendVideoLog(p.LogLine);
        });

        await Task.Run(() => buildService.BuildImageAsync(buildJob, buildProgress, ct));
        AppendVideoLog($"[Info] Blu-ray image created: {outputPath}");

        if (burnRequested)
        {
            var drive = CmbBdDrive.SelectedItem as DiscDrive;
            if (drive == null)
            {
                AppendVideoLog("[Warning] No drive selected — skipping burn.");
                return;
            }

            var burnJob = new BurnJob
            {
                ImagePath = outputPath,
                DevicePath = drive.DevicePath,
                WriteMode = "DAO (Disc At Once)",
                WriteSpeed = GetSelectedText(CmbBdSpeed, "Auto"),
                Copies = (int)(NumBdCopies.Value ?? 1),
                EjectAfterBurn = ChkBdEject.IsChecked == true,
                VerifyAfterBurn = ChkBdVerify.IsChecked == true,
                SimulateOnly = ChkBdSimulate.IsChecked == true,
                BufferUnderrunProtection = ChkBdBup.IsChecked == true,
                CloseDisc = true
            };

            AppendVideoLog($"[Info] Burning to {drive.DisplayName}...");
            TxtVideoExecStatus.Text = "Burning Blu-ray...";

            var burnService = new BurnService();
            var burnProgress = new Progress<BurnProgress>(p =>
            {
                PrgVideoExec.Value = 50 + p.PercentComplete * 0.5;
                TxtVideoExecPercent.Text = $"{(int)PrgVideoExec.Value}%";
                if (p.CurrentSpeedX > 0)
                    TxtVideoExecSpeed.Text = $"{p.CurrentSpeedX:F1}x";
                if (p.Remaining.TotalSeconds > 0)
                    TxtVideoExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)} | ETA: {FormatHelper.FormatTimeSpan(p.Remaining)}";
                else if (p.Elapsed.TotalSeconds > 0)
                    TxtVideoExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)}";
                if (!string.IsNullOrEmpty(p.StatusMessage))
                    TxtVideoExecStatus.Text = p.StatusMessage;
                if (!string.IsNullOrEmpty(p.LogLine))
                    AppendVideoLog(p.LogLine);

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
            AppendVideoLog("[Info] Burn complete!");

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
            PrgVideoExec.Value = 100;
            TxtVideoExecPercent.Text = "100%";
        });
    }

    // -----------------------------------------------------------------------
    // EXECUTE: BDAV from Folder
    // -----------------------------------------------------------------------
    private async Task ExecuteBdavFromFolderAsync(CancellationToken ct)
    {
        TxtVideoStep4Title.Text = "📹 Creating BDAV Image...";

        var sourceFolder = TxtBdavSourceFolder.Text!.Trim();
        // If the user pointed at BDAV itself, use the parent folder
        if (Path.GetFileName(sourceFolder).Equals("BDAV", StringComparison.OrdinalIgnoreCase))
            sourceFolder = Path.GetDirectoryName(sourceFolder) ?? sourceFolder;

        var outputPath = TxtBdavOutputPath.Text?.Trim() ?? "bdav.iso";
        var volumeLabel = string.IsNullOrWhiteSpace(TxtBdavVolumeLabel.Text)
            ? "BDAV_DISC"
            : TxtBdavVolumeLabel.Text!.Trim();

        var discSizeText = GetSelectedText(CmbBdavDiscSize, "BD-25 (25 GB)");
        var discType = discSizeText.Contains("BD-128") ? "BD-128"
            : discSizeText.Contains("BD-100") ? "BD-100"
            : discSizeText.Contains("BD-50") ? "BD-50"
            : "BD-25";

        // Build the BDAV authoring job
        var bdavAuthoring = new BluRayAuthoringJob
        {
            IsBdav = true,
            VolumeLabel = volumeLabel,
            TranscodeVideo = false
        };

        // Set recording metadata
        if (DpBdavRecordingDate.SelectedDate.HasValue)
            bdavAuthoring.RecordingTimestamp = DpBdavRecordingDate.SelectedDate.Value.DateTime;
        bdavAuthoring.TimeZoneOffsetMinutes = (int)(NumBdavTimezoneOffset.Value ?? 0);

        // Create a default playlist with play items from the BDAV folder's M2TS files
        var bdavDir = Path.Combine(sourceFolder, "BDAV");
        if (!Directory.Exists(bdavDir))
            bdavDir = sourceFolder;

        var streamDir = Path.Combine(bdavDir, "STREAM");
        var playlist = new BluRayPlaylist { Name = "Main Recording" };
        if (Directory.Exists(streamDir))
        {
            foreach (var m2ts in Directory.EnumerateFiles(streamDir, "*.m2ts", SearchOption.TopDirectoryOnly).OrderBy(f => f))
            {
                playlist.PlayItems.Add(new BluRayPlayItem { VideoPath = m2ts });
            }
        }

        if (playlist.PlayItems.Count == 0)
        {
            AppendVideoLog("[Warning] No M2TS files found in BDAV/STREAM — building from folder structure.");
            // Fallback: build from source folder directly using a simple data image approach
            playlist.PlayItems.Add(new BluRayPlayItem { VideoPath = string.Empty });
        }

        bdavAuthoring.Playlists.Add(playlist);

        var buildJob = new BuildJob
        {
            DiscType = discType,
            SourceFolder = sourceFolder,
            OutputImagePath = outputPath,
            FileSystem = "UDF 2.50",
            UdfVersion = "2.50",
            VolumeLabel = volumeLabel,
            JolietLongFilenames = false,
            BdavAuthoring = bdavAuthoring
        };

        AppendVideoLog("[Info] Building BDAV image (ISO)...");
        var buildService = new BuildService();
        bool burnRequested = ChkBdavBurnDisc.IsChecked == true;

        var buildProgress = new Progress<BuildProgress>(p =>
        {
            PrgVideoExec.Value = burnRequested ? p.PercentComplete * 0.5 : p.PercentComplete;
            TxtVideoExecPercent.Text = $"{(int)PrgVideoExec.Value}%";
            if (!string.IsNullOrEmpty(p.StatusMessage))
                TxtVideoExecStatus.Text = p.StatusMessage;
            if (p.Remaining.TotalSeconds > 0)
                TxtVideoExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)} | ETA: {FormatHelper.FormatTimeSpan(p.Remaining)}";
            else if (p.Elapsed.TotalSeconds > 0)
                TxtVideoExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)}";
            if (!string.IsNullOrEmpty(p.LogLine))
                AppendVideoLog(p.LogLine);
        });

        await Task.Run(() => buildService.BuildImageAsync(buildJob, buildProgress, ct));
        AppendVideoLog($"[Info] BDAV image created: {outputPath}");

        if (burnRequested)
        {
            var drive = CmbBdavDrive.SelectedItem as DiscDrive;
            if (drive == null)
            {
                AppendVideoLog("[Warning] No drive selected — skipping burn.");
                return;
            }

            var burnJob = new BurnJob
            {
                ImagePath = outputPath,
                DevicePath = drive.DevicePath,
                WriteMode = "DAO (Disc At Once)",
                WriteSpeed = GetSelectedText(CmbBdavSpeed, "Auto"),
                Copies = (int)(NumBdavCopies.Value ?? 1),
                EjectAfterBurn = ChkBdavEject.IsChecked == true,
                VerifyAfterBurn = ChkBdavVerify.IsChecked == true,
                SimulateOnly = ChkBdavSimulate.IsChecked == true,
                BufferUnderrunProtection = ChkBdavBup.IsChecked == true,
                CloseDisc = true
            };

            AppendVideoLog($"[Info] Burning to {drive.DisplayName}...");
            TxtVideoExecStatus.Text = "Burning BDAV...";

            var burnService = new BurnService();
            var burnProgress = new Progress<BurnProgress>(p =>
            {
                PrgVideoExec.Value = 50 + p.PercentComplete * 0.5;
                TxtVideoExecPercent.Text = $"{(int)PrgVideoExec.Value}%";
                if (p.CurrentSpeedX > 0)
                    TxtVideoExecSpeed.Text = $"{p.CurrentSpeedX:F1}x";
                if (p.Remaining.TotalSeconds > 0)
                    TxtVideoExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)} | ETA: {FormatHelper.FormatTimeSpan(p.Remaining)}";
                else if (p.Elapsed.TotalSeconds > 0)
                    TxtVideoExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)}";
                if (!string.IsNullOrEmpty(p.StatusMessage))
                    TxtVideoExecStatus.Text = p.StatusMessage;
                if (!string.IsNullOrEmpty(p.LogLine))
                    AppendVideoLog(p.LogLine);

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
            AppendVideoLog("[Info] Burn complete!");

            // Note: Post-burn verification is handled internally by BurnEngine when
            // VerifyAfterBurn is set. Do NOT add a second verify here.

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
            PrgVideoExec.Value = 100;
            TxtVideoExecPercent.Text = "100%";
        });
    }

    // -----------------------------------------------------------------------
    // EXECUTE: 3D Blu-ray from Folder
    // -----------------------------------------------------------------------
    private async Task ExecuteBluRay3DFromFolderAsync(CancellationToken ct)
    {
        TxtVideoStep4Title.Text = "🎥 Creating 3D Blu-ray Image...";

        var sourceFolder = TxtBd3dSourceFolder.Text!.Trim();
        // If the user pointed at BDMV itself, use the parent folder
        if (Path.GetFileName(sourceFolder).Equals("BDMV", StringComparison.OrdinalIgnoreCase))
            sourceFolder = Path.GetDirectoryName(sourceFolder) ?? sourceFolder;

        var outputPath = TxtBd3dOutputPath.Text?.Trim() ?? "bluray3d.iso";
        var volumeLabel = string.IsNullOrWhiteSpace(TxtBd3dVolumeLabel.Text)
            ? "BLURAY3D_DISC"
            : TxtBd3dVolumeLabel.Text!.Trim();

        var discSizeText = GetSelectedText(CmbBd3dDiscSize, "BD-50 (50 GB)");
        var discType = discSizeText.Contains("BD-128") ? "BD-128"
            : discSizeText.Contains("BD-100") ? "BD-100"
            : discSizeText.Contains("BD-25") ? "BD-25"
            : "BD-50";

        var mode3d = GetBd3dMode();
        var depthOffset = (int)(NumBd3dDepthOffset.Value ?? 0);

        var bdAuthoring = new BluRayAuthoringJob
        {
            Stereoscopic3D = true,
            Stereoscopic3DMode = mode3d,
            Stereoscopic3DDepthOffset = depthOffset,
            DependentViewPath = TxtBd3dDependentViewPath.Text?.Trim() ?? string.Empty,
            VolumeLabel = volumeLabel,
            TranscodeVideo = false
        };

        // Create a default playlist from M2TS files in the BDMV/STREAM folder
        var bdmvDir = Path.Combine(sourceFolder, "BDMV");
        var streamDir = Path.Combine(bdmvDir, "STREAM");
        var playlist = new BluRayPlaylist { Name = "Main Feature" };
        if (Directory.Exists(streamDir))
        {
            foreach (var m2ts in Directory.EnumerateFiles(streamDir, "*.m2ts", SearchOption.TopDirectoryOnly).OrderBy(f => f))
            {
                playlist.PlayItems.Add(new BluRayPlayItem { VideoPath = m2ts });
            }
        }

        if (playlist.PlayItems.Count == 0)
        {
            AppendVideoLog("[Warning] No M2TS files found in BDMV/STREAM — building from folder structure.");
            playlist.PlayItems.Add(new BluRayPlayItem { VideoPath = string.Empty });
        }

        bdAuthoring.Playlists.Add(playlist);

        var buildJob = new BuildJob
        {
            DiscType = discType,
            SourceFolder = sourceFolder,
            OutputImagePath = outputPath,
            FileSystem = "UDF 2.50",
            UdfVersion = "2.50",
            VolumeLabel = volumeLabel,
            JolietLongFilenames = false,
            BluRayAuthoring = bdAuthoring
        };

        AppendVideoLog($"[Info] Building 3D Blu-ray image ({mode3d} mode)...");
        var buildService = new BuildService();
        bool burnRequested = ChkBd3dBurnDisc.IsChecked == true;

        var buildProgress = new Progress<BuildProgress>(p =>
        {
            PrgVideoExec.Value = burnRequested ? p.PercentComplete * 0.5 : p.PercentComplete;
            TxtVideoExecPercent.Text = $"{(int)PrgVideoExec.Value}%";
            if (!string.IsNullOrEmpty(p.StatusMessage))
                TxtVideoExecStatus.Text = p.StatusMessage;
            if (p.Remaining.TotalSeconds > 0)
                TxtVideoExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)} | ETA: {FormatHelper.FormatTimeSpan(p.Remaining)}";
            else if (p.Elapsed.TotalSeconds > 0)
                TxtVideoExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)}";
            if (!string.IsNullOrEmpty(p.LogLine))
                AppendVideoLog(p.LogLine);
        });

        await Task.Run(() => buildService.BuildImageAsync(buildJob, buildProgress, ct));
        AppendVideoLog($"[Info] 3D Blu-ray image created: {outputPath}");

        if (burnRequested)
        {
            var drive = CmbBd3dDrive.SelectedItem as DiscDrive;
            if (drive == null)
            {
                AppendVideoLog("[Warning] No drive selected — skipping burn.");
                return;
            }

            var burnJob = new BurnJob
            {
                ImagePath = outputPath,
                DevicePath = drive.DevicePath,
                WriteMode = "DAO (Disc At Once)",
                WriteSpeed = GetSelectedText(CmbBd3dSpeed, "Auto"),
                Copies = (int)(NumBd3dCopies.Value ?? 1),
                EjectAfterBurn = ChkBd3dEject.IsChecked == true,
                VerifyAfterBurn = ChkBd3dVerify.IsChecked == true,
                SimulateOnly = ChkBd3dSimulate.IsChecked == true,
                BufferUnderrunProtection = ChkBd3dBup.IsChecked == true,
                CloseDisc = true
            };

            AppendVideoLog($"[Info] Burning to {drive.DisplayName}...");
            TxtVideoExecStatus.Text = "Burning 3D Blu-ray...";

            var burnService = new BurnService();
            var burnProgress = new Progress<BurnProgress>(p =>
            {
                PrgVideoExec.Value = 50 + p.PercentComplete * 0.5;
                TxtVideoExecPercent.Text = $"{(int)PrgVideoExec.Value}%";
                if (p.CurrentSpeedX > 0)
                    TxtVideoExecSpeed.Text = $"{p.CurrentSpeedX:F1}x";
                if (p.Remaining.TotalSeconds > 0)
                    TxtVideoExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)} | ETA: {FormatHelper.FormatTimeSpan(p.Remaining)}";
                else if (p.Elapsed.TotalSeconds > 0)
                    TxtVideoExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)}";
                if (!string.IsNullOrEmpty(p.StatusMessage))
                    TxtVideoExecStatus.Text = p.StatusMessage;
                if (!string.IsNullOrEmpty(p.LogLine))
                    AppendVideoLog(p.LogLine);

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
            AppendVideoLog("[Info] Burn complete!");

            // Note: Post-burn verification is handled internally by BurnEngine when
            // VerifyAfterBurn is set. Do NOT add a second verify here.

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
            PrgVideoExec.Value = 100;
            TxtVideoExecPercent.Text = "100%";
        });
    }

    // -----------------------------------------------------------------------
    // EXECUTE: VCD
    // -----------------------------------------------------------------------
    private async Task ExecuteVcdAsync(CancellationToken ct)
    {
        TxtVideoStep4Title.Text = "📼 Creating Video CD Image...";

        var outputPath = TxtVcdOutputPath.Text?.Trim() ?? "vcd.bin";
        var videoStandard = GetSelectedText(CmbVcdVideoStandard, "NTSC");
        var vcdVersionText = GetSelectedText(CmbVcdVersion, "2.0 (with PBC)");
        var vcdVersion = vcdVersionText.Contains("1.1") ? "1.1" : "2.0";
        var discLabel = string.IsNullOrWhiteSpace(TxtVcdDiscLabel.Text)
            ? "VIDEOCD"
            : TxtVcdDiscLabel.Text!.Trim();

        // Transcode non-MPEG container files (TS, MP4, MKV, WebM, AVI, MOV/QT, OGG, DivX, etc.) to MPEG-PS
        // VCD requires MPEG-1 Program Stream; SVCD requires MPEG-2 Program Stream.
        // The VcdBuilder validates MPEG headers, so we must convert container formats first.
        var videoFilePaths = new List<string>();
        var tempTranscodedPaths = new List<string>();
        var transcodingProgress = new Progress<BuildProgress>(p =>
        {
            if (!string.IsNullOrEmpty(p.StatusMessage))
                TxtVideoExecStatus.Text = p.StatusMessage;
            if (!string.IsNullOrEmpty(p.LogLine))
                AppendVideoLog(p.LogLine);
            if (p.PercentComplete > 0)
            {
                PrgVideoExec.Value = p.PercentComplete * 0.3; // Transcoding takes ~30% of total progress
                TxtVideoExecPercent.Text = $"{(int)PrgVideoExec.Value}%";
            }
        });

        try
        {
            for (int i = 0; i < _vcdFiles.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var filePath = _vcdFiles[i].FilePath;
                var ext = Path.GetExtension(filePath);

                if (FormatHelper.RequiresTranscodeToMpegPs(ext))
                {
                    // Non-MPEG container — transcode to VCD-compliant MPEG-1 PS
                    AppendVideoLog($"[Info] Transcoding container file: {Path.GetFileName(filePath)} ({FormatHelper.GetContainerType(ext)})");
                    var tempPath = Path.Combine(Path.GetTempPath(), $"obs_vcd_{Guid.NewGuid():N}.mpg");
                    tempTranscodedPaths.Add(tempPath);

                    // VCD uses MPEG-1 at 1,150 kbps video, 224 kbps audio per White Book specification
                    bool isNtsc = videoStandard.Equals("NTSC", StringComparison.OrdinalIgnoreCase);
                    string resolution = isNtsc ? "352:240" : "352:288";
                    string frameRate = isNtsc ? "29.97" : "25";

                    await VideoTranscoder.TranscodeContainerToDvdAsync(
                        filePath, tempPath,
                        videoStandard, "4:3",
                        1150, "MP2", 224,
                        transcodingProgress, ct);

                    videoFilePaths.Add(tempPath);
                    AppendVideoLog($"[Info] Transcoded ({i + 1}/{_vcdFiles.Count}): {Path.GetFileName(filePath)} → MPEG-1 PS");
                }
                else
                {
                    videoFilePaths.Add(filePath);
                }
            }

            var buildJob = new BuildJob
            {
                DiscType = "VCD",
                VideoFiles = videoFilePaths,
                VideoStandard = videoStandard,
                VcdVersion = vcdVersion,
                VcdDiscLabel = discLabel,
                PlaybackControl = ChkVcdPbc.IsChecked == true,
                CreateScanData = ChkVcdScanData.IsChecked == true,
                CreateSearchData = ChkVcdSearchData.IsChecked == true,
                IncludeCdiApplication = ChkVcdCdi.IsChecked == true,
                OutputImagePath = outputPath,
                CdXaMode = true
            };

            AppendVideoLog("[Info] Building VCD image (BIN/CUE)...");
            var buildService = new BuildService();
            bool burnRequested = ChkVcdBurnDisc.IsChecked == true;
            bool didTranscode = tempTranscodedPaths.Count > 0;

            // When transcoding occurred, build phase starts at 30%.
            // With burn: transcode 0-30%, build 30-50%, burn 50-100%
            // Without burn: transcode 0-30%, build 30-100%
            double buildBase = didTranscode ? 30 : 0;
            double buildRange = burnRequested ? 20 : (100 - buildBase);

            var buildProgress = new Progress<BuildProgress>(p =>
            {
                PrgVideoExec.Value = buildBase + p.PercentComplete / 100.0 * buildRange;
                TxtVideoExecPercent.Text = $"{(int)PrgVideoExec.Value}%";
                if (!string.IsNullOrEmpty(p.StatusMessage))
                    TxtVideoExecStatus.Text = p.StatusMessage;
                if (p.Remaining.TotalSeconds > 0)
                    TxtVideoExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)} | ETA: {FormatHelper.FormatTimeSpan(p.Remaining)}";
                else if (p.Elapsed.TotalSeconds > 0)
                    TxtVideoExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)}";
                if (!string.IsNullOrEmpty(p.LogLine))
                    AppendVideoLog(p.LogLine);
            });

            await Task.Run(() => buildService.BuildImageAsync(buildJob, buildProgress, ct));
            AppendVideoLog($"[Info] VCD image created: {outputPath}");

            if (burnRequested)
            {
                var drive = CmbVcdDrive.SelectedItem as DiscDrive;
                if (drive == null)
                {
                    AppendVideoLog("[Warning] No drive selected — skipping burn.");
                    return;
                }

                var cuePath = Path.ChangeExtension(outputPath, ".cue");
                var burnJob = new BurnJob
                {
                    ImagePath = outputPath,
                    CuePath = cuePath,
                    DevicePath = drive.DevicePath,
                    WriteMode = GetSelectedText(CmbVcdWriteMode, "DAO (Disc At Once)"),
                    WriteSpeed = GetSelectedText(CmbVcdSpeed, "Auto"),
                    Copies = (int)(NumVcdCopies.Value ?? 1),
                    EjectAfterBurn = ChkVcdEject.IsChecked == true,
                    VerifyAfterBurn = ChkVcdVerify.IsChecked == true,
                    SimulateOnly = ChkVcdSimulate.IsChecked == true,
                    BufferUnderrunProtection = ChkVcdBup.IsChecked == true,
                    CloseDisc = true
                };

                AppendVideoLog($"[Info] Burning to {drive.DisplayName}...");
                TxtVideoExecStatus.Text = "Burning VCD...";

                var burnService = new BurnService();
                var burnProgress = new Progress<BurnProgress>(p =>
                {
                    PrgVideoExec.Value = 50 + p.PercentComplete * 0.5;
                    TxtVideoExecPercent.Text = $"{(int)PrgVideoExec.Value}%";
                    if (p.CurrentSpeedX > 0)
                        TxtVideoExecSpeed.Text = $"{p.CurrentSpeedX:F1}x";
                    if (p.Remaining.TotalSeconds > 0)
                        TxtVideoExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)} | ETA: {FormatHelper.FormatTimeSpan(p.Remaining)}";
                    else if (p.Elapsed.TotalSeconds > 0)
                        TxtVideoExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)}";
                    if (!string.IsNullOrEmpty(p.StatusMessage))
                        TxtVideoExecStatus.Text = p.StatusMessage;
                    if (!string.IsNullOrEmpty(p.LogLine))
                        AppendVideoLog(p.LogLine);

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
                AppendVideoLog("[Info] Burn complete!");

                // Note: Post-burn verification is handled internally by BurnEngine when
                // VerifyAfterBurn is set. Do NOT add a second verify here.

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
                PrgVideoExec.Value = 100;
                TxtVideoExecPercent.Text = "100%";
            });
        }
        finally
        {
            // Clean up temporary transcoded files
            foreach (var tempPath in tempTranscodedPaths)
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }
    }
    // -----------------------------------------------------------------------
    // EXECUTE: SVCD
    // -----------------------------------------------------------------------
    private async Task ExecuteSvcdAsync(CancellationToken ct)
    {
        TxtVideoStep4Title.Text = "📼 Creating Super Video CD Image...";

        var outputPath = TxtSvcdOutputPath.Text?.Trim() ?? "svcd.bin";
        var videoStandard = GetSelectedText(CmbSvcdVideoStandard, "NTSC");
        var discLabel = string.IsNullOrWhiteSpace(TxtSvcdDiscLabel.Text)
            ? "SUPERVCD"
            : TxtSvcdDiscLabel.Text!.Trim();

        // Transcode non-MPEG container files (TS, MP4, MKV, WebM, AVI, MOV/QT, OGG, DivX, etc.) to MPEG-PS
        // SVCD requires MPEG-2 Program Stream per IEC 62107 specification.
        var videoFilePaths = new List<string>();
        var tempTranscodedPaths = new List<string>();
        var transcodingProgress = new Progress<BuildProgress>(p =>
        {
            if (!string.IsNullOrEmpty(p.StatusMessage))
                TxtVideoExecStatus.Text = p.StatusMessage;
            if (!string.IsNullOrEmpty(p.LogLine))
                AppendVideoLog(p.LogLine);
            if (p.PercentComplete > 0)
            {
                PrgVideoExec.Value = p.PercentComplete * 0.3;
                TxtVideoExecPercent.Text = $"{(int)PrgVideoExec.Value}%";
            }
        });

        try
        {
            for (int i = 0; i < _svcdFiles.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var filePath = _svcdFiles[i].FilePath;
                var ext = Path.GetExtension(filePath);

                if (FormatHelper.RequiresTranscodeToMpegPs(ext))
                {
                    // Non-MPEG container — transcode to SVCD-compliant MPEG-2 PS
                    AppendVideoLog($"[Info] Transcoding container file: {Path.GetFileName(filePath)} ({FormatHelper.GetContainerType(ext)})");
                    var tempPath = Path.Combine(Path.GetTempPath(), $"obs_svcd_{Guid.NewGuid():N}.mpg");
                    tempTranscodedPaths.Add(tempPath);

                    // SVCD uses MPEG-2 at 2,600 kbps video, 224 kbps audio per IEC 62107
                    await VideoTranscoder.TranscodeContainerToDvdAsync(
                        filePath, tempPath,
                        videoStandard, "4:3",
                        2600, "MP2", 224,
                        transcodingProgress, ct);

                    videoFilePaths.Add(tempPath);
                    AppendVideoLog($"[Info] Transcoded ({i + 1}/{_svcdFiles.Count}): {Path.GetFileName(filePath)} → MPEG-2 PS");
                }
                else
                {
                    videoFilePaths.Add(filePath);
                }
            }

            var buildJob = new BuildJob
            {
                DiscType = "SVCD",
                VideoFiles = videoFilePaths,
                VideoStandard = videoStandard,
                VcdVersion = "2.0",
                VcdDiscLabel = discLabel,
                PlaybackControl = ChkSvcdPbc.IsChecked == true,
                CreateScanData = ChkSvcdScanData.IsChecked == true,
                CreateSearchData = ChkSvcdSearchData.IsChecked == true,
                OutputImagePath = outputPath,
                CdXaMode = true
            };

            AppendVideoLog("[Info] Building SVCD image (BIN/CUE)...");
            var buildService = new BuildService();
            bool burnRequested = ChkSvcdBurnDisc.IsChecked == true;
            bool didTranscode = tempTranscodedPaths.Count > 0;

            // When transcoding occurred, build phase starts at 30%.
            double buildBase = didTranscode ? 30 : 0;
            double buildRange = burnRequested ? 20 : (100 - buildBase);

            var buildProgress = new Progress<BuildProgress>(p =>
            {
                PrgVideoExec.Value = buildBase + p.PercentComplete / 100.0 * buildRange;
                TxtVideoExecPercent.Text = $"{(int)PrgVideoExec.Value}%";
                if (!string.IsNullOrEmpty(p.StatusMessage))
                    TxtVideoExecStatus.Text = p.StatusMessage;
                if (p.Remaining.TotalSeconds > 0)
                    TxtVideoExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)} | ETA: {FormatHelper.FormatTimeSpan(p.Remaining)}";
                else if (p.Elapsed.TotalSeconds > 0)
                    TxtVideoExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)}";
                if (!string.IsNullOrEmpty(p.LogLine))
                    AppendVideoLog(p.LogLine);

            });

            await Task.Run(() => buildService.BuildImageAsync(buildJob, buildProgress, ct));
            AppendVideoLog($"[Info] SVCD image created: {outputPath}");

            if (burnRequested)
            {
                var drive = CmbSvcdDrive.SelectedItem as DiscDrive;
                if (drive == null)
                {
                    AppendVideoLog("[Warning] No drive selected — skipping burn.");
                    return;
                }

                var cuePath = Path.ChangeExtension(outputPath, ".cue");
                var burnJob = new BurnJob
                {
                    ImagePath = outputPath,
                    CuePath = cuePath,
                    DevicePath = drive.DevicePath,
                    WriteMode = GetSelectedText(CmbSvcdWriteMode, "DAO (Disc At Once)"),
                    WriteSpeed = GetSelectedText(CmbSvcdSpeed, "Auto"),
                    Copies = (int)(NumSvcdCopies.Value ?? 1),
                    EjectAfterBurn = ChkSvcdEject.IsChecked == true,
                    VerifyAfterBurn = ChkSvcdVerify.IsChecked == true,
                    SimulateOnly = ChkSvcdSimulate.IsChecked == true,
                    BufferUnderrunProtection = ChkSvcdBup.IsChecked == true,
                    CloseDisc = true
                };

                AppendVideoLog($"[Info] Burning to {drive.DisplayName}...");
                TxtVideoExecStatus.Text = "Burning SVCD...";

                var burnService = new BurnService();
                var burnProgress = new Progress<BurnProgress>(p =>
                {
                    PrgVideoExec.Value = 50 + p.PercentComplete * 0.5;
                    TxtVideoExecPercent.Text = $"{(int)PrgVideoExec.Value}%";
                    if (p.CurrentSpeedX > 0)
                        TxtVideoExecSpeed.Text = $"{p.CurrentSpeedX:F1}x";
                    if (p.Remaining.TotalSeconds > 0)
                        TxtVideoExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)} | ETA: {FormatHelper.FormatTimeSpan(p.Remaining)}";
                    else if (p.Elapsed.TotalSeconds > 0)
                        TxtVideoExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)}";
                    if (!string.IsNullOrEmpty(p.StatusMessage))
                        TxtVideoExecStatus.Text = p.StatusMessage;
                    if (!string.IsNullOrEmpty(p.LogLine))
                        AppendVideoLog(p.LogLine);

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
                AppendVideoLog("[Info] Burn complete!");

                // Note: Post-burn verification is handled internally by BurnEngine when
                // VerifyAfterBurn is set. Do NOT add a second verify here.

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
                PrgVideoExec.Value = 100;
                TxtVideoExecPercent.Text = "100%";
            });
        }
        finally
        {
            // Clean up temporary transcoded files
            foreach (var tempPath in tempTranscodedPaths)
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }
    }

    // -----------------------------------------------------------------------
    // EXECUTE: XSVCD (eXtended Super Video CD)
    // -----------------------------------------------------------------------
    private async Task ExecuteXsvcdAsync(CancellationToken ct)
    {
        TxtVideoStep4Title.Text = "📼 Creating eXtended Super Video CD Image...";

        var outputPath = TxtXsvcdOutputPath.Text?.Trim() ?? "xsvcd.bin";
        var videoStandard = GetSelectedText(CmbXsvcdVideoStandard, "NTSC");
        var discLabel = string.IsNullOrWhiteSpace(TxtXsvcdDiscLabel.Text)
            ? "XSVCD"
            : TxtXsvcdDiscLabel.Text!.Trim();

        // Transcode non-MPEG container files to MPEG-PS
        // XSVCD uses MPEG-2 like SVCD but with extended bitrate parameters
        var videoFilePaths = new List<string>();
        var tempTranscodedPaths = new List<string>();
        var transcodingProgress = new Progress<BuildProgress>(p =>
        {
            if (!string.IsNullOrEmpty(p.StatusMessage))
                TxtVideoExecStatus.Text = p.StatusMessage;
            if (!string.IsNullOrEmpty(p.LogLine))
                AppendVideoLog(p.LogLine);
            if (p.PercentComplete > 0)
            {
                PrgVideoExec.Value = p.PercentComplete * 0.3;
                TxtVideoExecPercent.Text = $"{(int)PrgVideoExec.Value}%";
            }
        });

        try
        {
            for (int i = 0; i < _xsvcdFiles.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var filePath = _xsvcdFiles[i].FilePath;
                var ext = Path.GetExtension(filePath);

                if (FormatHelper.RequiresTranscodeToMpegPs(ext))
                {
                    // Non-MPEG container — transcode to XSVCD-compliant MPEG-2 PS with extended bitrate
                    AppendVideoLog($"[Info] Transcoding container file: {Path.GetFileName(filePath)} ({FormatHelper.GetContainerType(ext)})");
                    var tempPath = Path.Combine(Path.GetTempPath(), $"obs_xsvcd_{Guid.NewGuid():N}.mpg");
                    tempTranscodedPaths.Add(tempPath);

                    // XSVCD uses MPEG-2 at up to 3,500 kbps video, 224 kbps audio
                    await VideoTranscoder.TranscodeContainerToDvdAsync(
                        filePath, tempPath,
                        videoStandard, "4:3",
                        FormatHelper.XsvcdMaxVideoBitrateKbps, "MP2", FormatHelper.XsvcdDefaultAudioBitrateKbps,
                        transcodingProgress, ct);

                    videoFilePaths.Add(tempPath);
                    AppendVideoLog($"[Info] Transcoded ({i + 1}/{_xsvcdFiles.Count}): {Path.GetFileName(filePath)} → MPEG-2 PS");
                }
                else
                {
                    videoFilePaths.Add(filePath);
                }
            }

            var buildJob = new BuildJob
            {
                DiscType = "XSVCD",
                VideoFiles = videoFilePaths,
                VideoStandard = videoStandard,
                VcdVersion = "2.0",
                VcdDiscLabel = discLabel,
                PlaybackControl = ChkXsvcdPbc.IsChecked == true,
                CreateScanData = ChkXsvcdScanData.IsChecked == true,
                CreateSearchData = ChkXsvcdSearchData.IsChecked == true,
                OutputImagePath = outputPath,
                CdXaMode = true
            };

            AppendVideoLog("[Info] Building XSVCD image (BIN/CUE)...");
            var buildService = new BuildService();
            bool burnRequested = ChkXsvcdBurnDisc.IsChecked == true;
            bool didTranscode = tempTranscodedPaths.Count > 0;

            // When transcoding occurred, build phase starts at 30%.
            double buildBase = didTranscode ? 30 : 0;
            double buildRange = burnRequested ? 20 : (100 - buildBase);

            var buildProgress = new Progress<BuildProgress>(p =>
            {
                PrgVideoExec.Value = buildBase + p.PercentComplete / 100.0 * buildRange;
                TxtVideoExecPercent.Text = $"{(int)PrgVideoExec.Value}%";
                if (!string.IsNullOrEmpty(p.StatusMessage))
                    TxtVideoExecStatus.Text = p.StatusMessage;
                if (p.Remaining.TotalSeconds > 0)
                    TxtVideoExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)} | ETA: {FormatHelper.FormatTimeSpan(p.Remaining)}";
                else if (p.Elapsed.TotalSeconds > 0)
                    TxtVideoExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)}";
                if (!string.IsNullOrEmpty(p.LogLine))
                    AppendVideoLog(p.LogLine);

            });

            await Task.Run(() => buildService.BuildImageAsync(buildJob, buildProgress, ct));
            AppendVideoLog($"[Info] XSVCD image created: {outputPath}");

            if (burnRequested)
            {
                var drive = CmbXsvcdDrive.SelectedItem as DiscDrive;
                if (drive == null)
                {
                    AppendVideoLog("[Warning] No drive selected — skipping burn.");
                    return;
                }

                var cuePath = Path.ChangeExtension(outputPath, ".cue");
                var burnJob = new BurnJob
                {
                    ImagePath = outputPath,
                    CuePath = cuePath,
                    DevicePath = drive.DevicePath,
                    WriteMode = GetSelectedText(CmbXsvcdWriteMode, "DAO (Disc At Once)"),
                    WriteSpeed = GetSelectedText(CmbXsvcdSpeed, "Auto"),
                    Copies = (int)(NumXsvcdCopies.Value ?? 1),
                    EjectAfterBurn = ChkXsvcdEject.IsChecked == true,
                    VerifyAfterBurn = ChkXsvcdVerify.IsChecked == true,
                    SimulateOnly = ChkXsvcdSimulate.IsChecked == true,
                    BufferUnderrunProtection = ChkXsvcdBup.IsChecked == true,
                    CloseDisc = true
                };

                AppendVideoLog($"[Info] Burning XSVCD to {drive.DisplayName}...");
                TxtVideoExecStatus.Text = "Burning XSVCD...";

                var burnService = new BurnService();
                var burnProgress = new Progress<BurnProgress>(p =>
                {
                    PrgVideoExec.Value = 50 + p.PercentComplete * 0.5;
                    TxtVideoExecPercent.Text = $"{(int)PrgVideoExec.Value}%";
                    if (p.CurrentSpeedX > 0)
                        TxtVideoExecSpeed.Text = $"{p.CurrentSpeedX:F1}x";
                    if (p.Remaining.TotalSeconds > 0)
                        TxtVideoExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)} | ETA: {FormatHelper.FormatTimeSpan(p.Remaining)}";
                    else if (p.Elapsed.TotalSeconds > 0)
                        TxtVideoExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)}";
                    if (!string.IsNullOrEmpty(p.StatusMessage))
                        TxtVideoExecStatus.Text = p.StatusMessage;
                    if (!string.IsNullOrEmpty(p.LogLine))
                        AppendVideoLog(p.LogLine);

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
                AppendVideoLog("[Info] Burn complete!");

                // Note: Post-burn verification is handled internally by BurnEngine when
                // VerifyAfterBurn is set. Do NOT add a second verify here.

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
                PrgVideoExec.Value = 100;
                TxtVideoExecPercent.Text = "100%";
            });
        }
        finally
        {
            foreach (var tempPath in tempTranscodedPaths)
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }
    }

    // -----------------------------------------------------------------------
    // EXECUTE: UHD Blu-ray from Folder
    // -----------------------------------------------------------------------
    private async Task ExecuteUhdBlurayFromFolderAsync(CancellationToken ct)
    {
        TxtVideoStep4Title.Text = "🎬 Creating UHD Blu-ray Image...";

        var sourceFolder = TxtUhdBdSourceFolder.Text?.Trim() ?? "";
        var outputPath = TxtUhdBdOutputPath.Text?.Trim() ?? "uhd_bluray.iso";
        var volumeLabel = string.IsNullOrWhiteSpace(TxtUhdBdVolumeLabel.Text)
            ? "UHD_BLURAY" : TxtUhdBdVolumeLabel.Text!;

        // Resolve BDMV directory
        string bdmvParent;
        if (Path.GetFileName(sourceFolder).Equals("BDMV", StringComparison.OrdinalIgnoreCase))
            bdmvParent = Path.GetDirectoryName(sourceFolder) ?? sourceFolder;
        else
            bdmvParent = sourceFolder;

        AppendVideoLog($"[Info] Building UHD Blu-ray ISO from: {bdmvParent}");
        AppendVideoLog($"[Info] Output: {outputPath}");

        var discSizeText = GetSelectedText(CmbUhdBdDiscSize, "UHD BD-66 (66 GB)");
        var uhdDiscType = discSizeText.Contains("UHD BD-100") ? "UHD BD-100" : "UHD BD-66";

        // Build UDF 2.50 ISO image from the BDMV directory structure
        var buildJob = new BuildJob
        {
            SourceFolder = bdmvParent,
            OutputImagePath = outputPath,
            VolumeLabel = volumeLabel,
            FileSystem = "UDF 2.50",
            UdfVersion = "2.50",
            DiscType = uhdDiscType
        };

        bool burnRequested = ChkUhdBdBurnDisc.IsChecked == true;
        var buildService = new BuildService();
        var buildProgress = new Progress<BuildProgress>(p =>
        {
            PrgVideoExec.Value = burnRequested ? p.PercentComplete * 0.5 : p.PercentComplete;
            TxtVideoExecPercent.Text = $"{(int)PrgVideoExec.Value}%";
            if (!string.IsNullOrEmpty(p.StatusMessage))
                TxtVideoExecStatus.Text = p.StatusMessage;
            if (p.Remaining.TotalSeconds > 0)
                TxtVideoExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)} | ETA: {FormatHelper.FormatTimeSpan(p.Remaining)}";
            else if (p.Elapsed.TotalSeconds > 0)
                TxtVideoExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)}";
            if (!string.IsNullOrEmpty(p.LogLine))
                AppendVideoLog(p.LogLine);
        });

        await Task.Run(() => buildService.BuildImageAsync(buildJob, buildProgress, ct));
        AppendVideoLog($"[Info] UHD Blu-ray ISO image created: {outputPath}");

        if (burnRequested)
        {
            var drive = CmbUhdBdDrive.SelectedItem as DiscDrive;
            if (drive == null)
            {
                AppendVideoLog("[Warning] No drive selected — skipping burn.");
                return;
            }

            var burnJob = new BurnJob
            {
                ImagePath = outputPath,
                DevicePath = drive.DevicePath,
                WriteMode = "DAO (Disc At Once)",
                WriteSpeed = GetSelectedText(CmbUhdBdSpeed, "Auto"),
                Copies = (int)(NumUhdBdCopies.Value ?? 1),
                EjectAfterBurn = ChkUhdBdEject.IsChecked == true,
                VerifyAfterBurn = ChkUhdBdVerify.IsChecked == true,
                SimulateOnly = ChkUhdBdSimulate.IsChecked == true,
                BufferUnderrunProtection = ChkUhdBdBup.IsChecked == true,
                CloseDisc = true
            };

            AppendVideoLog($"[Info] Burning UHD Blu-ray to {drive.DisplayName}...");
            TxtVideoExecStatus.Text = "Burning UHD Blu-ray...";

            var burnService = new BurnService();
            var burnProgress = new Progress<BurnProgress>(p =>
            {
                PrgVideoExec.Value = 50 + p.PercentComplete * 0.5;
                TxtVideoExecPercent.Text = $"{(int)PrgVideoExec.Value}%";
                if (p.CurrentSpeedX > 0)
                    TxtVideoExecSpeed.Text = $"{p.CurrentSpeedX:F1}x";
                if (p.Remaining.TotalSeconds > 0)
                    TxtVideoExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)} | ETA: {FormatHelper.FormatTimeSpan(p.Remaining)}";
                else if (p.Elapsed.TotalSeconds > 0)
                    TxtVideoExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)}";
                if (!string.IsNullOrEmpty(p.StatusMessage))
                    TxtVideoExecStatus.Text = p.StatusMessage;
                if (!string.IsNullOrEmpty(p.LogLine))
                    AppendVideoLog(p.LogLine);

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
            AppendVideoLog("[Info] UHD Blu-ray burn complete!");

            // Note: Post-burn verification is handled internally by BurnEngine when
            // VerifyAfterBurn is set. Do NOT add a second verify here.

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
            PrgVideoExec.Value = 100;
            TxtVideoExecPercent.Text = "100%";
        });
    }

    // -----------------------------------------------------------------------
    // DVD/BD Folder Scanning
    // -----------------------------------------------------------------------
    private void ScanDvdFolder()
    {
        _dvdFiles.Clear();
        var folder = TxtDvdSourceFolder.Text?.Trim();
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;

        // Find the VIDEO_TS directory
        string videoTsDir;
        if (Path.GetFileName(folder).Equals("VIDEO_TS", StringComparison.OrdinalIgnoreCase))
            videoTsDir = folder;
        else
            videoTsDir = Path.Combine(folder, "VIDEO_TS");

        if (!Directory.Exists(videoTsDir)) return;

        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".VOB", ".IFO", ".BUP" };
        foreach (var file in Directory.EnumerateFiles(videoTsDir, "*.*", SearchOption.TopDirectoryOnly)
                     .Where(f => extensions.Contains(Path.GetExtension(f)))
                     .OrderBy(f => f))
        {
            var fi = new FileInfo(file);
            _dvdFiles.Add(new VideoFileItem
            {
                FileName = fi.Name,
                FilePath = fi.FullName,
                FileSize = fi.Length
            });
        }
    }

    private void ScanBdFolder()
    {
        _bdFiles.Clear();
        var folder = TxtBdSourceFolder.Text?.Trim();
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;

        string bdmvDir;
        if (Path.GetFileName(folder).Equals("BDMV", StringComparison.OrdinalIgnoreCase))
            bdmvDir = folder;
        else
            bdmvDir = Path.Combine(folder, "BDMV");

        if (!Directory.Exists(bdmvDir)) return;

        // Native BD structure files + supported container formats for transcoding workflows
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".m2ts", ".mpls", ".clpi",
            // Additional container formats (TS, MP4, MKV, WebM) that can be transcoded
            ".ts", ".mts", ".mp4", ".m4v", ".mkv", ".mk3d", ".webm"
        };
        foreach (var file in Directory.EnumerateFiles(bdmvDir, "*.*", SearchOption.AllDirectories)
                     .Where(f => extensions.Contains(Path.GetExtension(f)))
                     .OrderBy(f => f))
        {
            var fi = new FileInfo(file);
            _bdFiles.Add(new VideoFileItem
            {
                FileName = fi.Name,
                FilePath = fi.FullName,
                FileSize = fi.Length
            });
        }
    }

    private void ScanBdavFolder()
    {
        _bdavFiles.Clear();
        var folder = TxtBdavSourceFolder.Text?.Trim();
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;

        string bdavDir;
        if (Path.GetFileName(folder).Equals("BDAV", StringComparison.OrdinalIgnoreCase))
            bdavDir = folder;
        else
            bdavDir = Path.Combine(folder, "BDAV");

        if (!Directory.Exists(bdavDir)) return;

        // Native BDAV structure files + supported container formats for transcoding workflows
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".m2ts", ".mpls", ".clpi",
            // Additional container formats (TS, MP4, MKV, WebM) that can be transcoded
            ".ts", ".mts", ".mp4", ".m4v", ".mkv", ".mk3d", ".webm"
        };
        foreach (var file in Directory.EnumerateFiles(bdavDir, "*.*", SearchOption.AllDirectories)
                     .Where(f => extensions.Contains(Path.GetExtension(f)))
                     .OrderBy(f => f))
        {
            var fi = new FileInfo(file);
            _bdavFiles.Add(new VideoFileItem
            {
                FileName = fi.Name,
                FilePath = fi.FullName,
                FileSize = fi.Length
            });
        }
    }

    private void ScanBd3dFolder()
    {
        _bd3dFiles.Clear();
        var folder = TxtBd3dSourceFolder.Text?.Trim();
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;

        string bdmvDir;
        if (Path.GetFileName(folder).Equals("BDMV", StringComparison.OrdinalIgnoreCase))
            bdmvDir = folder;
        else
            bdmvDir = Path.Combine(folder, "BDMV");

        if (!Directory.Exists(bdmvDir)) return;

        // Native BD3D structure files + supported container formats for transcoding workflows
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".m2ts", ".mpls", ".clpi", ".ssif",
            // Additional container formats (TS, MP4, MKV, WebM) that can be transcoded
            ".ts", ".mts", ".mp4", ".m4v", ".mkv", ".mk3d", ".webm"
        };
        foreach (var file in Directory.EnumerateFiles(bdmvDir, "*.*", SearchOption.AllDirectories)
                     .Where(f => extensions.Contains(Path.GetExtension(f)))
                     .OrderBy(f => f))
        {
            var fi = new FileInfo(file);
            _bd3dFiles.Add(new VideoFileItem
            {
                FileName = fi.Name,
                FilePath = fi.FullName,
                FileSize = fi.Length
            });
        }
    }

    private void ScanUhdBdFolder()
    {
        _uhdBdFiles.Clear();
        var folder = TxtUhdBdSourceFolder.Text?.Trim();
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;

        string bdmvDir;
        if (Path.GetFileName(folder).Equals("BDMV", StringComparison.OrdinalIgnoreCase))
            bdmvDir = folder;
        else
            bdmvDir = Path.Combine(folder, "BDMV");

        if (!Directory.Exists(bdmvDir)) return;

        // UHD BD structure files: .m2ts (HEVC streams), .mpls (playlists), .clpi (clip info)
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".m2ts", ".mpls", ".clpi",
            // Supported containers for UHD transcoding workflows
            ".ts", ".mts", ".mp4", ".m4v", ".mkv", ".webm"
        };
        foreach (var file in Directory.EnumerateFiles(bdmvDir, "*.*", SearchOption.AllDirectories)
                     .Where(f => extensions.Contains(Path.GetExtension(f)))
                     .OrderBy(f => f))
        {
            var fi = new FileInfo(file);
            _uhdBdFiles.Add(new VideoFileItem
            {
                FileName = fi.Name,
                FilePath = fi.FullName,
                FileSize = fi.Length
            });
        }
    }

    // -----------------------------------------------------------------------
    // VCD File Management
    // -----------------------------------------------------------------------
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // MPEG Program Stream (DVD native)
        ".mpg", ".mpeg", ".mp2", ".m1v", ".m2v", ".dat",
        // MPEG Transport Stream (Blu-ray native)
        ".ts", ".mts", ".m2ts", ".m2t",
        // MPEG-4 Part 14 (ISO 14496-14)
        ".mp4", ".m4v",
        // Matroska (RFC 8794)
        ".mkv", ".mk3d",
        // WebM (Matroska subset — VP8/VP9/AV1 + Vorbis/Opus)
        ".webm",
        // AVI (Microsoft RIFF — AVI 1.0 / OpenDML 2.0)
        ".avi",
        // QuickTime / MOV (Apple QuickTime File Format)
        ".mov", ".qt",
        // Ogg Video (Xiph.Org — RFC 3533 / RFC 5334)
        ".ogv", ".ogx",
        // DivX Media Format (AVI-compatible with DivX extensions)
        ".divx",
        // Other common containers
        ".wmv", ".flv"
    };

    private async void OnAddVcdFilesClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add VCD Video Files",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("MPEG Video Files") { Patterns = new[] { "*.mpg", "*.mpeg", "*.mp2", "*.m1v", "*.m2v", "*.dat" } },
                new FilePickerFileType("Transport Stream") { Patterns = new[] { "*.ts", "*.mts", "*.m2ts" } },
                new FilePickerFileType("MP4 Video") { Patterns = new[] { "*.mp4", "*.m4v" } },
                new FilePickerFileType("Matroska Video") { Patterns = new[] { "*.mkv", "*.mk3d" } },
                new FilePickerFileType("WebM Video") { Patterns = new[] { "*.webm" } },
                new FilePickerFileType("AVI Video") { Patterns = new[] { "*.avi" } },
                new FilePickerFileType("QuickTime Video") { Patterns = new[] { "*.mov", "*.qt" } },
                new FilePickerFileType("Ogg Video") { Patterns = new[] { "*.ogv", "*.ogx" } },
                new FilePickerFileType("DivX Video") { Patterns = new[] { "*.divx" } },
                new FilePickerFileType("Other Video") { Patterns = new[] { "*.wmv", "*.flv" } },
                new FilePickerFileType("All Files") { Patterns = new[] { "*.*" } }
            }
        });

        foreach (var file in files)
            AddVideoFile(file.Path.LocalPath, _vcdFiles);

        RenumberVcdFiles();
        UpdateVcdFileInfo();
    }

    private void OnRemoveVcdFileClick(object? sender, RoutedEventArgs e)
    {
        if (LstVcdFiles.SelectedIndex >= 0 && LstVcdFiles.SelectedIndex < _vcdFiles.Count)
        {
            _vcdFiles.RemoveAt(LstVcdFiles.SelectedIndex);
            RenumberVcdFiles();
            UpdateVcdFileInfo();
        }
    }

    private void OnMoveVcdUpClick(object? sender, RoutedEventArgs e)
    {
        var idx = LstVcdFiles.SelectedIndex;
        if (idx > 0)
        {
            _vcdFiles.Move(idx, idx - 1);
            LstVcdFiles.SelectedIndex = idx - 1;
            RenumberVcdFiles();
        }
    }

    private void OnMoveVcdDownClick(object? sender, RoutedEventArgs e)
    {
        var idx = LstVcdFiles.SelectedIndex;
        if (idx >= 0 && idx < _vcdFiles.Count - 1)
        {
            _vcdFiles.Move(idx, idx + 1);
            LstVcdFiles.SelectedIndex = idx + 1;
            RenumberVcdFiles();
        }
    }

    private void OnVcdDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DragDropEffects.Copy;
    }

    private void OnVcdDrop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer?.TryGetFiles();
        if (files != null)
        {
            foreach (var file in files)
            {
                if (file is IStorageFile storageFile)
                {
                    var ext = Path.GetExtension(storageFile.Name).ToLowerInvariant();
                    if (VideoExtensions.Contains(ext))
                        AddVideoFile(storageFile.Path.LocalPath, _vcdFiles);
                }
            }
            RenumberVcdFiles();
            UpdateVcdFileInfo();
        }
    }

    private void RenumberVcdFiles()
    {
        for (int i = 0; i < _vcdFiles.Count; i++)
            _vcdFiles[i].TrackNumber = i + 1;
    }

    private void UpdateVcdFileInfo()
    {
        TxtVcdFileCount.Text = $"({_vcdFiles.Count} file{(_vcdFiles.Count != 1 ? "s" : "")})";
    }

    // -----------------------------------------------------------------------
    // SVCD File Management
    // -----------------------------------------------------------------------
    private async void OnAddSvcdFilesClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add SVCD Video Files",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("MPEG Video Files") { Patterns = new[] { "*.mpg", "*.mpeg", "*.mp2", "*.m1v", "*.m2v", "*.dat" } },
                new FilePickerFileType("Transport Stream") { Patterns = new[] { "*.ts", "*.mts", "*.m2ts" } },
                new FilePickerFileType("MP4 Video") { Patterns = new[] { "*.mp4", "*.m4v" } },
                new FilePickerFileType("Matroska Video") { Patterns = new[] { "*.mkv", "*.mk3d" } },
                new FilePickerFileType("WebM Video") { Patterns = new[] { "*.webm" } },
                new FilePickerFileType("AVI Video") { Patterns = new[] { "*.avi" } },
                new FilePickerFileType("QuickTime Video") { Patterns = new[] { "*.mov", "*.qt" } },
                new FilePickerFileType("Ogg Video") { Patterns = new[] { "*.ogv", "*.ogx" } },
                new FilePickerFileType("DivX Video") { Patterns = new[] { "*.divx" } },
                new FilePickerFileType("Other Video") { Patterns = new[] { "*.wmv", "*.flv" } },
                new FilePickerFileType("All Files") { Patterns = new[] { "*.*" } }
            }
        });

        foreach (var file in files)
            AddVideoFile(file.Path.LocalPath, _svcdFiles);

        RenumberSvcdFiles();
        UpdateSvcdFileInfo();
    }

    private void OnRemoveSvcdFileClick(object? sender, RoutedEventArgs e)
    {
        if (LstSvcdFiles.SelectedIndex >= 0 && LstSvcdFiles.SelectedIndex < _svcdFiles.Count)
        {
            _svcdFiles.RemoveAt(LstSvcdFiles.SelectedIndex);
            RenumberSvcdFiles();
            UpdateSvcdFileInfo();
        }
    }

    private void OnMoveSvcdUpClick(object? sender, RoutedEventArgs e)
    {
        var idx = LstSvcdFiles.SelectedIndex;
        if (idx > 0)
        {
            _svcdFiles.Move(idx, idx - 1);
            LstSvcdFiles.SelectedIndex = idx - 1;
            RenumberSvcdFiles();
        }
    }

    private void OnMoveSvcdDownClick(object? sender, RoutedEventArgs e)
    {
        var idx = LstSvcdFiles.SelectedIndex;
        if (idx >= 0 && idx < _svcdFiles.Count - 1)
        {
            _svcdFiles.Move(idx, idx + 1);
            LstSvcdFiles.SelectedIndex = idx + 1;
            RenumberSvcdFiles();
        }
    }

    private void OnSvcdDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DragDropEffects.Copy;
    }

    private void OnSvcdDrop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer?.TryGetFiles();
        if (files != null)
        {
            foreach (var file in files)
            {
                if (file is IStorageFile storageFile)
                {
                    var ext = Path.GetExtension(storageFile.Name).ToLowerInvariant();
                    if (VideoExtensions.Contains(ext))
                        AddVideoFile(storageFile.Path.LocalPath, _svcdFiles);
                }
            }
            RenumberSvcdFiles();
            UpdateSvcdFileInfo();
        }
    }

    private void RenumberSvcdFiles()
    {
        for (int i = 0; i < _svcdFiles.Count; i++)
            _svcdFiles[i].TrackNumber = i + 1;
    }

    private void UpdateSvcdFileInfo()
    {
        TxtSvcdFileCount.Text = $"({_svcdFiles.Count} file{(_svcdFiles.Count != 1 ? "s" : "")})";
    }

    // -----------------------------------------------------------------------
    // XSVCD File Management
    // -----------------------------------------------------------------------
    private async void OnAddXsvcdFilesClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add XSVCD Video Files",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("MPEG Video Files") { Patterns = new[] { "*.mpg", "*.mpeg", "*.mp2", "*.m1v", "*.m2v", "*.dat" } },
                new FilePickerFileType("Transport Stream") { Patterns = new[] { "*.ts", "*.mts", "*.m2ts" } },
                new FilePickerFileType("MP4 Video") { Patterns = new[] { "*.mp4", "*.m4v" } },
                new FilePickerFileType("Matroska Video") { Patterns = new[] { "*.mkv", "*.mk3d" } },
                new FilePickerFileType("WebM Video") { Patterns = new[] { "*.webm" } },
                new FilePickerFileType("AVI Video") { Patterns = new[] { "*.avi" } },
                new FilePickerFileType("QuickTime Video") { Patterns = new[] { "*.mov", "*.qt" } },
                new FilePickerFileType("All Files") { Patterns = new[] { "*.*" } }
            }
        });

        foreach (var file in files)
            AddVideoFile(file.Path.LocalPath, _xsvcdFiles);

        RenumberXsvcdFiles();
        UpdateXsvcdFileInfo();
    }

    private void OnRemoveXsvcdFileClick(object? sender, RoutedEventArgs e)
    {
        if (LstXsvcdFiles.SelectedIndex >= 0 && LstXsvcdFiles.SelectedIndex < _xsvcdFiles.Count)
        {
            _xsvcdFiles.RemoveAt(LstXsvcdFiles.SelectedIndex);
            RenumberXsvcdFiles();
            UpdateXsvcdFileInfo();
        }
    }

    private void OnMoveXsvcdUpClick(object? sender, RoutedEventArgs e)
    {
        var idx = LstXsvcdFiles.SelectedIndex;
        if (idx > 0)
        {
            _xsvcdFiles.Move(idx, idx - 1);
            LstXsvcdFiles.SelectedIndex = idx - 1;
            RenumberXsvcdFiles();
        }
    }

    private void OnMoveXsvcdDownClick(object? sender, RoutedEventArgs e)
    {
        var idx = LstXsvcdFiles.SelectedIndex;
        if (idx >= 0 && idx < _xsvcdFiles.Count - 1)
        {
            _xsvcdFiles.Move(idx, idx + 1);
            LstXsvcdFiles.SelectedIndex = idx + 1;
            RenumberXsvcdFiles();
        }
    }

    private void OnXsvcdDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DragDropEffects.Copy;
    }

    private void OnXsvcdDrop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer?.TryGetFiles();
        if (files != null)
        {
            foreach (var file in files)
            {
                if (file is IStorageFile storageFile)
                {
                    var ext = Path.GetExtension(storageFile.Name).ToLowerInvariant();
                    if (VideoExtensions.Contains(ext))
                        AddVideoFile(storageFile.Path.LocalPath, _xsvcdFiles);
                }
            }
            RenumberXsvcdFiles();
            UpdateXsvcdFileInfo();
        }
    }

    private void RenumberXsvcdFiles()
    {
        for (int i = 0; i < _xsvcdFiles.Count; i++)
            _xsvcdFiles[i].TrackNumber = i + 1;
    }

    private void UpdateXsvcdFileInfo()
    {
        TxtXsvcdFileCount.Text = $"({_xsvcdFiles.Count} file{(_xsvcdFiles.Count != 1 ? "s" : "")})";
    }

    private void AddVideoFile(string filePath, ObservableCollection<VideoFileItem> collection)
    {
        try
        {
            var fi = new FileInfo(filePath);
            collection.Add(new VideoFileItem
            {
                TrackNumber = collection.Count + 1,
                FileName = fi.Name,
                FilePath = fi.FullName,
                FileSize = fi.Length
            });
        }
        catch (Exception ex)
        {
            AppendVideoLog($"[Error] Could not add '{Path.GetFileName(filePath)}': {ex.Message}");
        }
    }

    // -----------------------------------------------------------------------
    // Video Preview (audio track via NAudio)
    // -----------------------------------------------------------------------
    private void OnPreviewDvdFileClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string filePath)
            StartPlayback(filePath, TxtDvdNowPlaying, BtnDvdPlayPause, SliderDvdPosition, TxtDvdPlaybackTime);
    }

    private void OnDvdPlayPauseClick(object? sender, RoutedEventArgs e)
    {
        TogglePlayPause(BtnDvdPlayPause);
    }

    private void OnDvdStopClick(object? sender, RoutedEventArgs e)
    {
        StopPlayback();
        UpdatePlayerUi(TxtDvdNowPlaying, BtnDvdPlayPause, SliderDvdPosition, TxtDvdPlaybackTime, stopped: true);
    }

    private void OnPreviewBdFileClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string filePath)
            StartPlayback(filePath, TxtBdNowPlaying, BtnBdPlayPause, SliderBdPosition, TxtBdPlaybackTime);
    }

    private void OnBdPlayPauseClick(object? sender, RoutedEventArgs e)
    {
        TogglePlayPause(BtnBdPlayPause);
    }

    private void OnBdStopClick(object? sender, RoutedEventArgs e)
    {
        StopPlayback();
        UpdatePlayerUi(TxtBdNowPlaying, BtnBdPlayPause, SliderBdPosition, TxtBdPlaybackTime, stopped: true);
    }

    private void OnPreviewBdavFileClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string filePath)
            StartPlayback(filePath, TxtBdavNowPlaying, BtnBdavPlayPause, SliderBdavPosition, TxtBdavPlaybackTime);
    }

    private void OnBdavPlayPauseClick(object? sender, RoutedEventArgs e)
    {
        TogglePlayPause(BtnBdavPlayPause);
    }

    private void OnBdavStopClick(object? sender, RoutedEventArgs e)
    {
        StopPlayback();
        UpdatePlayerUi(TxtBdavNowPlaying, BtnBdavPlayPause, SliderBdavPosition, TxtBdavPlaybackTime, stopped: true);
    }

    private void OnPreviewBd3dFileClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string filePath)
            StartPlayback(filePath, TxtBd3dNowPlaying, BtnBd3dPlayPause, SliderBd3dPosition, TxtBd3dPlaybackTime);
    }

    private void OnBd3dPlayPauseClick(object? sender, RoutedEventArgs e)
    {
        TogglePlayPause(BtnBd3dPlayPause);
    }

    private void OnBd3dStopClick(object? sender, RoutedEventArgs e)
    {
        StopPlayback();
        UpdatePlayerUi(TxtBd3dNowPlaying, BtnBd3dPlayPause, SliderBd3dPosition, TxtBd3dPlaybackTime, stopped: true);
    }

    private void OnPreviewVcdFileClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string filePath)
            StartPlayback(filePath, TxtVcdNowPlaying, BtnVcdPlayPause, SliderVcdPosition, TxtVcdPlaybackTime);
    }

    private void OnVcdPlayPauseClick(object? sender, RoutedEventArgs e)
    {
        TogglePlayPause(BtnVcdPlayPause);
    }

    private void OnVcdStopClick(object? sender, RoutedEventArgs e)
    {
        StopPlayback();
        UpdatePlayerUi(TxtVcdNowPlaying, BtnVcdPlayPause, SliderVcdPosition, TxtVcdPlaybackTime, stopped: true);
    }

    private void OnPreviewSvcdFileClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string filePath)
            StartPlayback(filePath, TxtSvcdNowPlaying, BtnSvcdPlayPause, SliderSvcdPosition, TxtSvcdPlaybackTime);
    }

    private void OnSvcdPlayPauseClick(object? sender, RoutedEventArgs e)
    {
        TogglePlayPause(BtnSvcdPlayPause);
    }

    private void OnSvcdStopClick(object? sender, RoutedEventArgs e)
    {
        StopPlayback();
        UpdatePlayerUi(TxtSvcdNowPlaying, BtnSvcdPlayPause, SliderSvcdPosition, TxtSvcdPlaybackTime, stopped: true);
    }

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
            _audioReader = new AudioFileReader(filePath);
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
            nowPlaying.Text = "No file selected";
            playPauseBtn.Content = "▶ Play";
            positionSlider.Value = 0;
            positionSlider.IsEnabled = false;
            playbackTime.Text = "00:00 / 00:00";
        }
    }

    // -----------------------------------------------------------------------
    // Browse dialogs
    // -----------------------------------------------------------------------
    private async void OnBrowseDvdSourceClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select VIDEO_TS Folder",
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            TxtDvdSourceFolder.Text = folders[0].Path.LocalPath;
            ScanDvdFolder();
        }
    }

    private async void OnBrowseDvdOutputClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save DVD-Video Image",
            SuggestedFileName = "DVD_VIDEO",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("ISO Image") { Patterns = new[] { "*.iso" } }
            }
        });

        if (file != null)
            TxtDvdOutputPath.Text = file.Path.LocalPath;
    }

    private async void OnBrowseBdSourceClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select BDMV Folder",
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            TxtBdSourceFolder.Text = folders[0].Path.LocalPath;
            ScanBdFolder();
        }
    }

    private async void OnBrowseBdOutputClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Blu-ray Image",
            SuggestedFileName = "BLURAY_DISC",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("ISO Image") { Patterns = new[] { "*.iso" } }
            }
        });

        if (file != null)
            TxtBdOutputPath.Text = file.Path.LocalPath;
    }

    private async void OnBrowseBdavSourceClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select BDAV Folder",
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            TxtBdavSourceFolder.Text = folders[0].Path.LocalPath;
            ScanBdavFolder();
        }
    }

    private async void OnBrowseBdavOutputClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save BDAV Image",
            SuggestedFileName = "BDAV_DISC",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("ISO Image") { Patterns = new[] { "*.iso" } }
            }
        });

        if (file != null)
            TxtBdavOutputPath.Text = file.Path.LocalPath;
    }

    private async void OnBrowseBd3dSourceClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select BDMV 3D Folder",
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            TxtBd3dSourceFolder.Text = folders[0].Path.LocalPath;
            ScanBd3dFolder();
        }
    }

    private async void OnBrowseBd3dOutputClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save 3D Blu-ray Image",
            SuggestedFileName = "BLURAY3D_DISC",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("ISO Image") { Patterns = new[] { "*.iso" } }
            }
        });

        if (file != null)
            TxtBd3dOutputPath.Text = file.Path.LocalPath;
    }

    private async void OnBrowseBd3dDependentViewClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Dependent View (Right-Eye) Video File",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Video Files") { Patterns = new[] { "*.m2ts", "*.ts", "*.h264", "*.264", "*.mvc", "*.mp4", "*.mkv", "*.webm", "*.m4v", "*.mk3d", "*.avi", "*.mov", "*.qt", "*.ogv", "*.ogx", "*.divx" } },
                new FilePickerFileType("All Files") { Patterns = new[] { "*.*" } }
            }
        });

        if (files.Count > 0)
            TxtBd3dDependentViewPath.Text = files[0].Path.LocalPath;
    }

    private async void OnBrowseVcdOutputClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save VCD Image",
            SuggestedFileName = "VideoCD",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("BIN/CUE Image") { Patterns = new[] { "*.bin" } }
            }
        });

        if (file != null)
            TxtVcdOutputPath.Text = file.Path.LocalPath;
    }

    private async void OnBrowseSvcdOutputClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save SVCD Image",
            SuggestedFileName = "SuperVideoCD",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("BIN/CUE Image") { Patterns = new[] { "*.bin" } }
            }
        });

        if (file != null)
            TxtSvcdOutputPath.Text = file.Path.LocalPath;
    }

    private async void OnBrowseUhdBdSourceClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select UHD BDMV Folder",
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            TxtUhdBdSourceFolder.Text = folders[0].Path.LocalPath;
            ScanUhdBdFolder();
        }
    }

    private async void OnBrowseUhdBdOutputClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save UHD Blu-ray Image",
            SuggestedFileName = "UHD_BLURAY",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("ISO Image") { Patterns = new[] { "*.iso" } }
            }
        });

        if (file != null)
            TxtUhdBdOutputPath.Text = file.Path.LocalPath;
    }

    private async void OnBrowseXsvcdOutputClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save XSVCD Image",
            SuggestedFileName = "XSVCD",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("BIN/CUE Image") { Patterns = new[] { "*.bin" } }
            }
        });

        if (file != null)
            TxtXsvcdOutputPath.Text = file.Path.LocalPath;
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------
    private string GetBd3dMode()
    {
        var modeText = GetSelectedText(CmbBd3dMode, "Frame Packed (MVC, full resolution)");
        if (modeText.Contains("Side by Side", StringComparison.OrdinalIgnoreCase))
            return "SideBySide";
        if (modeText.Contains("Top and Bottom", StringComparison.OrdinalIgnoreCase))
            return "TopBottom";
        return "FramePacked";
    }

    private static string GetSelectedText(ComboBox cmb, string fallback)
    {
        if (cmb.SelectedItem is ComboBoxItem item)
            return item.Content?.ToString() ?? fallback;
        return cmb.SelectedItem?.ToString() ?? fallback;
    }

    private void AppendVideoLog(string line)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            AppendVideoLogCore(line);
        }
        else
        {
            Dispatcher.UIThread.Post(() => AppendVideoLogCore(line));
        }
    }

    private void AppendVideoLogCore(string line)
    {
        TxtVideoExecLog.Text = string.IsNullOrEmpty(TxtVideoExecLog.Text)
            ? line
            : TxtVideoExecLog.Text + Environment.NewLine + line;

        // Trim log to prevent unbounded memory growth (keep last ~20 KB)
        var text = TxtVideoExecLog.Text;
        if (text != null && text.Length > 30_000)
        {
            var trimIdx = text.IndexOf('\n', text.Length - 20_000);
            if (trimIdx > 0)
                TxtVideoExecLog.Text = "...\n" + text[(trimIdx + 1)..];
            else
                TxtVideoExecLog.Text = "...\n" + text[(text.Length - 20_000)..];
        }

        // Auto-scroll to bottom
        TxtVideoExecLog.CaretIndex = TxtVideoExecLog.Text?.Length ?? 0;
    }
}

// -----------------------------------------------------------------------
// Data model for video file list items
// -----------------------------------------------------------------------

/// <summary>Represents a video file in the Video Disc Wizard.</summary>
public class VideoFileItem
{
    public int TrackNumber { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public TimeSpan Duration { get; set; }
    public string SizeText => FormatHelper.FormatBytes(FileSize);
    public string DurationText => Duration.TotalHours >= 1
        ? Duration.ToString(@"h\:mm\:ss")
        : Duration.ToString(@"mm\:ss");
}
