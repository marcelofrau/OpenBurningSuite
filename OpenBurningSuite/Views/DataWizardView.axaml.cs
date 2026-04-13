// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
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

public partial class DataWizardView : UserControl
{
    // -----------------------------------------------------------------------
    // Mode enum
    // -----------------------------------------------------------------------
    private enum DataWizardMode { None, BuildImage, BuildAndBurn, BurnImage }

    // -----------------------------------------------------------------------
    // State
    // -----------------------------------------------------------------------
    private DataWizardMode _mode = DataWizardMode.None;
    private int _currentStep = 1;

    private readonly ObservableCollection<DataFileItem> _buildFiles = new();
    private readonly ObservableCollection<DataFileItem> _bbFiles = new();

    private CancellationTokenSource? _cts;
    private bool _isRunning;

    // Services
    private DiscDiscoveryService? _discovery;
    private List<DiscDrive> _drives = new();

    public DataWizardView()
    {
        InitializeComponent();

        LstBuildFiles.ItemsSource = _buildFiles;
        LstBBFiles.ItemsSource = _bbFiles;

        // Wire disc type changes to update compatible file systems, capacity, and write speeds
        CmbBuildDiscType.SelectionChanged += OnBuildDiscTypeChanged;
        CmbBBDiscType.SelectionChanged += OnBBDiscTypeChanged;

        // Initialize file system combos with options compatible with the default disc type (CD)
        UpdateFileSystemCombo(CmbBuildFileSystem, "CD");
        UpdateFileSystemCombo(CmbBBFileSystem, "CD");

        // Wire file system changes to update ISO extension checkboxes
        CmbBuildFileSystem.SelectionChanged += (_, _) => UpdateBuildOptionsVisibility();

        Loaded += async (_, _) => await LoadDrivesAsync();
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

            CmbBBDrive.ItemsSource = _drives;
            CmbBurnDrive.ItemsSource = _drives;

            if (_drives.Count > 0)
            {
                var writable = _drives.FirstOrDefault(d => d.CanWrite) ?? _drives[0];
                CmbBBDrive.SelectedItem = writable;
                CmbBurnDrive.SelectedItem = writable;
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
    private void OnModeBuildImage(object? sender, RoutedEventArgs e) => SetMode(DataWizardMode.BuildImage);
    private void OnModeBuildAndBurn(object? sender, RoutedEventArgs e) => SetMode(DataWizardMode.BuildAndBurn);
    private void OnModeBurnImage(object? sender, RoutedEventArgs e) => SetMode(DataWizardMode.BurnImage);

    private void SetMode(DataWizardMode mode)
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
        Step2BuildPanel.IsVisible = false;
        Step2BuildBurnPanel.IsVisible = false;
        Step2BurnPanel.IsVisible = false;
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
                    case DataWizardMode.BuildImage: Step2BuildPanel.IsVisible = true; break;
                    case DataWizardMode.BuildAndBurn: Step2BuildBurnPanel.IsVisible = true; break;
                    case DataWizardMode.BurnImage: Step2BurnPanel.IsVisible = true; break;
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
        SetStepIndicator(DStepInd1, DStepLbl1, _currentStep >= 1, _currentStep == 1);
        SetStepIndicator(DStepInd2, DStepLbl2, _currentStep >= 2, _currentStep == 2);
        SetStepIndicator(DStepInd3, DStepLbl3, _currentStep >= 3, _currentStep == 3);
        SetStepIndicator(DStepInd4, DStepLbl4, _currentStep >= 4, _currentStep == 4);
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
        BtnDataBack.IsVisible = _currentStep > 1 && !_isRunning;
        BtnDataNext.IsVisible = _currentStep == 2 && !_isRunning;
        BtnDataExecute.IsVisible = _currentStep == 3 && !_isRunning;
        BtnDataCancel.IsVisible = _currentStep == 4 && _isRunning;
        BtnDataFinish.IsVisible = _currentStep == 4 && !_isRunning;
    }

    private void OnDataBackClick(object? sender, RoutedEventArgs e) => GoToStep(_currentStep - 1);

    private void OnDataNextClick(object? sender, RoutedEventArgs e)
    {
        var error = ValidateStep2();
        if (error != null)
        {
            AppendLog($"[Error] {error}");
            return;
        }
        GoToStep(3);
    }

    private void OnDataFinishClick(object? sender, RoutedEventArgs e)
    {
        _mode = DataWizardMode.None;
        _isRunning = false;
        GoToStep(1);
    }

    private async void OnDataExecuteClick(object? sender, RoutedEventArgs e)
    {
        GoToStep(4);
        _isRunning = true;
        UpdateNavButtons();

        // Reset step 4 UI for a fresh operation
        PrgDataExec.Value = 0;
        TxtDataExecPercent.Text = "0%";
        TxtDataExecSpeed.Text = string.Empty;
        TxtDataExecStatus.Text = "Preparing...";
        TxtDataExecLog.Text = string.Empty;
        TxtDataStep4Title.Text = "Executing...";

        // Reset disc visualization for a fresh operation
        DiscVizBorder.IsVisible = false;
        DiscViz.IsActive = false;
        DiscViz.IsCompleted = false;
        DiscViz.VerifyPassed = null;
        DiscViz.BadSectors = 0;
        DiscViz.PercentComplete = 0;
        DiscViz.CurrentLba = 0;
        DiscViz.TotalSectors = 0;

        // Configure layer visualization based on the target drive's media type
        var selectedDrive = (_mode == DataWizardMode.BuildAndBurn
            ? CmbBBDrive.SelectedItem
            : CmbBurnDrive.SelectedItem) as DiscDrive;
        DiscViz.ConfigureForMedia(selectedDrive?.CurrentMedia?.MediaType);

        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        try
        {
            switch (_mode)
            {
                case DataWizardMode.BuildImage:
                    await ExecuteBuildImageAsync(_cts.Token);
                    break;
                case DataWizardMode.BuildAndBurn:
                    await ExecuteBuildAndBurnAsync(_cts.Token);
                    break;
                case DataWizardMode.BurnImage:
                    await ExecuteBurnImageAsync(_cts.Token);
                    break;
            }

            TxtDataStep4Title.Text = "✅ Complete!";
            TxtDataExecStatus.Text = "Operation completed successfully.";
        }
        catch (OperationCanceledException)
        {
            TxtDataStep4Title.Text = "⏹ Cancelled";
            TxtDataExecStatus.Text = "Operation was cancelled by user.";
            AppendLog("[Info] Operation cancelled.");
            DiscViz.IsActive = false;
            DiscViz.IsCompleted = false;
            DiscViz.UpdateVisualization();
        }
        catch (Exception ex)
        {
            TxtDataStep4Title.Text = "❌ Failed";
            TxtDataExecStatus.Text = $"Error: {ex.Message}";
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

    private void OnDataCancelClick(object? sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
    }

    // -----------------------------------------------------------------------
    // Disc type / file system compatibility handlers
    // -----------------------------------------------------------------------

    /// <summary>Updates the file system combo box and capacity info when the Build Image disc type changes.</summary>
    private void OnBuildDiscTypeChanged(object? sender, SelectionChangedEventArgs e)
    {
        var discType = GetSelectedText(CmbBuildDiscType, "CD");
        UpdateFileSystemCombo(CmbBuildFileSystem, discType);
        UpdateBuildFileCount();
        UpdateBuildOptionsVisibility();
    }

    /// <summary>Updates the file system combo box, capacity info, and write speeds when the Build &amp; Burn disc type changes.</summary>
    private void OnBBDiscTypeChanged(object? sender, SelectionChangedEventArgs e)
    {
        var discType = GetSelectedText(CmbBBDiscType, "CD");
        UpdateFileSystemCombo(CmbBBFileSystem, discType);
        UpdateBBWriteSpeeds(discType);
        UpdateBBFileCount();
    }

    /// <summary>Repopulates a file system ComboBox with only the file systems compatible with the selected disc type.</summary>
    private static void UpdateFileSystemCombo(ComboBox cmbFileSystem, string discType)
    {
        var currentFs = GetSelectedText(cmbFileSystem, "");
        var compatibleFs = FormatHelper.GetCompatibleFileSystems(discType);

        cmbFileSystem.Items.Clear();
        foreach (var fs in compatibleFs)
            cmbFileSystem.Items.Add(new ComboBoxItem { Content = fs });

        // Try to re-select the previously selected file system if it's still compatible
        bool reselected = false;
        for (int i = 0; i < cmbFileSystem.Items.Count; i++)
        {
            if (cmbFileSystem.Items[i] is ComboBoxItem item &&
                string.Equals(item.Content?.ToString(), currentFs, StringComparison.OrdinalIgnoreCase))
            {
                cmbFileSystem.SelectedIndex = i;
                reselected = true;
                break;
            }
        }

        if (!reselected)
        {
            // Select the default file system for this disc type
            var defaultFs = FormatHelper.GetDefaultFileSystem(discType);
            for (int i = 0; i < cmbFileSystem.Items.Count; i++)
            {
                if (cmbFileSystem.Items[i] is ComboBoxItem item &&
                    string.Equals(item.Content?.ToString(), defaultFs, StringComparison.OrdinalIgnoreCase))
                {
                    cmbFileSystem.SelectedIndex = i;
                    reselected = true;
                    break;
                }
            }
            if (!reselected && cmbFileSystem.Items.Count > 0)
                cmbFileSystem.SelectedIndex = 0;
        }
    }

    /// <summary>Updates the Build &amp; Burn write speed combo to show speeds appropriate for the selected disc type.</summary>
    private void UpdateBBWriteSpeeds(string discType)
    {
        var currentSpeed = GetSelectedText(CmbBBSpeed, "Auto");
        var speeds = FormatHelper.GetWriteSpeedsForMedia(discType);

        CmbBBSpeed.Items.Clear();
        foreach (var s in speeds)
            CmbBBSpeed.Items.Add(new ComboBoxItem { Content = s });

        // Try to re-select the previously selected speed
        bool reselected = false;
        for (int i = 0; i < CmbBBSpeed.Items.Count; i++)
        {
            if (CmbBBSpeed.Items[i] is ComboBoxItem item &&
                string.Equals(item.Content?.ToString(), currentSpeed, StringComparison.OrdinalIgnoreCase))
            {
                CmbBBSpeed.SelectedIndex = i;
                reselected = true;
                break;
            }
        }
        if (!reselected && CmbBBSpeed.Items.Count > 0)
            CmbBBSpeed.SelectedIndex = 0;
    }

    /// <summary>Enables/disables ISO extension checkboxes based on the selected file system.</summary>
    private void UpdateBuildOptionsVisibility()
    {
        var fs = GetSelectedText(CmbBuildFileSystem, "ISO 9660 + Joliet");

        // Joliet long filenames only applies when Joliet is in use
        bool hasJoliet = fs.Contains("Joliet", StringComparison.OrdinalIgnoreCase);
        ChkBuildJolietLong.IsEnabled = hasJoliet;
        if (!hasJoliet) ChkBuildJolietLong.IsChecked = false;

        // Rock Ridge only applies to ISO 9660-based file systems (not pure UDF)
        bool isIsoBase = fs.Contains("ISO", StringComparison.OrdinalIgnoreCase) || fs == "Rock Ridge";
        ChkBuildRockRidge.IsEnabled = isIsoBase;
        if (!isIsoBase) ChkBuildRockRidge.IsChecked = false;

        // Deep directory, lowercase, special chars are ISO 9660 options
        ChkBuildDeepDir.IsEnabled = isIsoBase;
        ChkBuildLowercase.IsEnabled = isIsoBase;
        ChkBuildSpecialChars.IsEnabled = isIsoBase;
    }

    // -----------------------------------------------------------------------
    // Step 2 validation
    // -----------------------------------------------------------------------
    private string? ValidateStep2()
    {
        switch (_mode)
        {
            case DataWizardMode.BuildImage:
                if (_buildFiles.Count == 0)
                    return "Please add at least one file or folder.";
                if (string.IsNullOrWhiteSpace(TxtBuildOutputPath.Text))
                    return "Please specify an output image path.";
                // Validate output directory exists
                var buildOutputDir = Path.GetDirectoryName(TxtBuildOutputPath.Text.Trim());
                if (!string.IsNullOrWhiteSpace(buildOutputDir) && !Directory.Exists(buildOutputDir))
                    return $"Output directory does not exist: {buildOutputDir}";
                // Warn if data size exceeds disc capacity
                {
                    var totalSize = _buildFiles.Sum(f => f.Size);
                    var discType = GetSelectedText(CmbBuildDiscType, "CD");
                    var capacity = FormatHelper.GetCapacity(discType);
                    if (totalSize > capacity)
                        return $"Total data size ({FormatHelper.FormatBytes(totalSize)}) exceeds {discType} capacity ({FormatHelper.FormatBytes(capacity)}).";
                }
                return null;

            case DataWizardMode.BuildAndBurn:
                if (_bbFiles.Count == 0)
                    return "Please add at least one file or folder.";
                if (CmbBBDrive.SelectedItem == null)
                    return "Please select a target drive for burning.";
                // Warn if data size exceeds disc capacity
                {
                    var totalSize = _bbFiles.Sum(f => f.Size);
                    var discType = GetSelectedText(CmbBBDiscType, "CD");
                    var capacity = FormatHelper.GetCapacity(discType);
                    if (totalSize > capacity)
                        return $"Total data size ({FormatHelper.FormatBytes(totalSize)}) exceeds {discType} capacity ({FormatHelper.FormatBytes(capacity)}).";
                }
                return null;

            case DataWizardMode.BurnImage:
                if (string.IsNullOrWhiteSpace(TxtBurnImagePath.Text))
                    return "Please specify a disc image file.";
                if (!File.Exists(TxtBurnImagePath.Text.Trim()))
                    return "The specified image file does not exist.";
                if (CmbBurnDrive.SelectedItem == null)
                    return "Please select a target drive for burning.";
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
        DataSummaryContent.Children.Clear();

        switch (_mode)
        {
            case DataWizardMode.BuildImage:
                AddSummaryLine("🏗 Task", "Build Data Disc Image");
                AddSummaryLine("📁 Source Items", $"{_buildFiles.Count} item(s), {GetTotalBuildSize()}");
                AddSummaryLine("📀 Disc Type", GetSelectedText(CmbBuildDiscType, "CD"));
                AddSummaryLine("📂 File System", GetSelectedText(CmbBuildFileSystem, "ISO 9660 + Joliet"));
                AddSummaryLine("🏷 Volume Label", string.IsNullOrWhiteSpace(TxtBuildVolumeLabel.Text) ? "DATA_DISC" : TxtBuildVolumeLabel.Text!);
                AddSummaryLine("💾 Output Image", TxtBuildOutputPath.Text ?? "(not set)");
                if (ChkBuildBootable.IsChecked == true)
                {
                    AddSummaryLine("🖥 Bootable", "Yes (El Torito)");
                    AddSummaryLine("  Boot Image", TxtBuildBootImage.Text ?? "(not set)");
                    AddSummaryLine("  Emulation", GetSelectedText(CmbBuildBootEmulation, "No Emulation"));
                    AddSummaryLine("  Platform", GetSelectedText(CmbBuildBootPlatform, "x86 (BIOS)"));
                }
                break;

            case DataWizardMode.BuildAndBurn:
                AddSummaryLine("🔥 Task", "Build Data Image & Burn to Disc");
                AddSummaryLine("📁 Source Items", $"{_bbFiles.Count} item(s), {GetTotalBBSize()}");
                AddSummaryLine("📀 Disc Type", GetSelectedText(CmbBBDiscType, "CD"));
                AddSummaryLine("📂 File System", GetSelectedText(CmbBBFileSystem, "ISO 9660 + Joliet"));
                AddSummaryLine("🏷 Volume Label", string.IsNullOrWhiteSpace(TxtBBVolumeLabel.Text) ? "DATA_DISC" : TxtBBVolumeLabel.Text!);
                var bbDrive = CmbBBDrive.SelectedItem as DiscDrive;
                AddSummaryLine("💿 Target Drive", bbDrive?.DisplayName ?? "(none)");
                AddSummaryLine("⚡ Write Speed", GetSelectedText(CmbBBSpeed, "Auto"));
                AddSummaryLine("📦 Copies", ((int)(NumBBCopies.Value ?? 1)).ToString());
                if (ChkBBVerify.IsChecked == true)
                    AddSummaryLine("✅ Verify After Burn", "Yes");
                if (ChkBBSimulate.IsChecked == true)
                    AddSummaryLine("🔬 Simulation Mode", "Yes (no actual write)");
                break;

            case DataWizardMode.BurnImage:
                AddSummaryLine("💿 Task", "Burn Existing Disc Image");
                AddSummaryLine("📄 Image File", TxtBurnImagePath.Text ?? "(not set)");
                if (File.Exists(TxtBurnImagePath.Text?.Trim()))
                {
                    var fi = new FileInfo(TxtBurnImagePath.Text!.Trim());
                    AddSummaryLine("📏 Image Size", FormatHelper.FormatBytes(fi.Length));
                }
                var burnDrive = CmbBurnDrive.SelectedItem as DiscDrive;
                AddSummaryLine("💿 Target Drive", burnDrive?.DisplayName ?? "(none)");
                AddSummaryLine("⚡ Write Speed", GetSelectedText(CmbBurnSpeed, "Auto"));
                AddSummaryLine("📝 Write Mode", GetSelectedText(CmbBurnWriteMode, "TAO"));
                AddSummaryLine("📦 Copies", ((int)(NumBurnCopies.Value ?? 1)).ToString());
                if (ChkBurnVerify.IsChecked == true)
                    AddSummaryLine("✅ Verify After Burn", "Yes");
                if (ChkBurnSimulate.IsChecked == true)
                    AddSummaryLine("🔬 Simulation Mode", "Yes (no actual write)");
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
            Width = 180
        });
        sp.Children.Add(new TextBlock
        {
            Text = value,
            Foreground = Avalonia.Media.Brush.Parse("#E8F0FE"),
            FontSize = 12,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });
        DataSummaryContent.Children.Add(sp);
    }

    // -----------------------------------------------------------------------
    // EXECUTE: Build Data Image
    // -----------------------------------------------------------------------
    private async Task ExecuteBuildImageAsync(CancellationToken ct)
    {
        TxtDataStep4Title.Text = "🏗 Building Data Disc Image...";

        // Stage source files to a temporary directory
        var tempDir = Path.Combine(Path.GetTempPath(),
            $"OpenBurningSuite_DataWiz_{Guid.NewGuid().ToString("N")[..8]}");
        Directory.CreateDirectory(tempDir);

        try
        {
            AppendLog("[Info] Staging source files...");
            TxtDataExecStatus.Text = "Staging files...";

            await Task.Run(() => StageFiles(_buildFiles, tempDir), ct);

            var fsText = GetSelectedText(CmbBuildFileSystem, "ISO 9660 + Joliet");
            var (fileSystem, udfVersion) = ParseFileSystem(fsText);

            var buildJob = new BuildJob
            {
                DiscType = GetSelectedText(CmbBuildDiscType, "CD"),
                FileSystem = fileSystem,
                UdfVersion = udfVersion,
                SourceFolder = tempDir,
                OutputImagePath = TxtBuildOutputPath.Text!.Trim(),
                VolumeLabel = string.IsNullOrWhiteSpace(TxtBuildVolumeLabel.Text) ? "DATA_DISC" : TxtBuildVolumeLabel.Text!.Trim(),
                Publisher = TxtBuildPublisher.Text ?? string.Empty,
                Preparer = TxtBuildPreparer.Text ?? string.Empty,
                Application = TxtBuildApplication.Text ?? string.Empty,
                JolietLongFilenames = ChkBuildJolietLong.IsChecked == true,
                RockRidgeExtensions = ChkBuildRockRidge.IsChecked == true,
                DeepDirectoryNesting = ChkBuildDeepDir.IsChecked == true,
                AllowLowercase = ChkBuildLowercase.IsChecked == true,
                AllowSpecialChars = ChkBuildSpecialChars.IsChecked == true,
                SortFiles = ChkBuildSortFiles.IsChecked == true,
                PadToCapacity = ChkBuildPad.IsChecked == true,
                Bootable = ChkBuildBootable.IsChecked == true,
                BootImagePath = TxtBuildBootImage.Text ?? string.Empty,
                BootEmulation = MapBootEmulation(GetSelectedText(CmbBuildBootEmulation, "No Emulation")),
                BootPlatform = MapBootPlatform(GetSelectedText(CmbBuildBootPlatform, "x86 (BIOS)")),
                BootInfoTable = ChkBuildBootInfoTable.IsChecked == true,
                EfiBootImagePath = TxtBuildEfiBootImage.Text ?? string.Empty
            };

            AppendLog($"[Info] Building {fileSystem} image: {buildJob.OutputImagePath}");
            var buildService = new BuildService();
            var buildProgress = new Progress<BuildProgress>(p =>
            {
                PrgDataExec.Value = p.PercentComplete;
                TxtDataExecPercent.Text = $"{p.PercentComplete}%";
                if (!string.IsNullOrEmpty(p.StatusMessage))
                    TxtDataExecStatus.Text = p.StatusMessage;
                if (p.Remaining.TotalSeconds > 0)
                    TxtDataExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)} | ETA: {FormatHelper.FormatTimeSpan(p.Remaining)}";
                else if (p.Elapsed.TotalSeconds > 0)
                    TxtDataExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)}";
                if (!string.IsNullOrEmpty(p.LogLine))
                    AppendLog(p.LogLine);
            });

            await Task.Run(() => buildService.BuildImageAsync(buildJob, buildProgress, ct));
            AppendLog($"[Info] Image created successfully: {buildJob.OutputImagePath}");
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }

        Dispatcher.UIThread.Post(() =>
        {
            PrgDataExec.Value = 100;
            TxtDataExecPercent.Text = "100%";
        });
    }

    // -----------------------------------------------------------------------
    // EXECUTE: Build and Burn
    // -----------------------------------------------------------------------
    private async Task ExecuteBuildAndBurnAsync(CancellationToken ct)
    {
        TxtDataStep4Title.Text = "🔥 Building & Burning Data Disc...";

        var drive = CmbBBDrive.SelectedItem as DiscDrive;
        if (drive == null)
            throw new InvalidOperationException("No target drive selected.");

        // Stage source files
        var tempDir = Path.Combine(Path.GetTempPath(),
            $"OpenBurningSuite_DataWiz_{Guid.NewGuid().ToString("N")[..8]}");
        Directory.CreateDirectory(tempDir);

        string? tempIso = null;

        try
        {
            AppendLog("[Info] Staging source files...");
            TxtDataExecStatus.Text = "Staging files...";

            await Task.Run(() => StageFiles(_bbFiles, tempDir), ct);

            var fsText = GetSelectedText(CmbBBFileSystem, "ISO 9660 + Joliet");
            var (fileSystem, udfVersion) = ParseFileSystem(fsText);

            var volumeLabel = string.IsNullOrWhiteSpace(TxtBBVolumeLabel.Text)
                ? "DATA_DISC"
                : TxtBBVolumeLabel.Text!.Trim();

            tempIso = Path.Combine(Path.GetTempPath(),
                $"OpenBurningSuite_Data_{volumeLabel}_{Guid.NewGuid().ToString("N")[..8]}.iso");

            // Phase 1: Build ISO image
            AppendLog("[Info] Building data disc image...");
            TxtDataExecStatus.Text = "Building disc image...";

            var buildJob = new BuildJob
            {
                DiscType = GetSelectedText(CmbBBDiscType, "CD"),
                FileSystem = fileSystem,
                UdfVersion = udfVersion,
                SourceFolder = tempDir,
                OutputImagePath = tempIso,
                VolumeLabel = volumeLabel,
                JolietLongFilenames = fileSystem.Contains("Joliet", StringComparison.OrdinalIgnoreCase)
            };

            var buildService = new BuildService();
            var buildProgress = new Progress<BuildProgress>(p =>
            {
                PrgDataExec.Value = p.PercentComplete * 0.4; // 0-40% for build
                TxtDataExecPercent.Text = $"{(int)PrgDataExec.Value}%";
                if (!string.IsNullOrEmpty(p.StatusMessage))
                    TxtDataExecStatus.Text = p.StatusMessage;
                if (p.Remaining.TotalSeconds > 0)
                    TxtDataExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)} | ETA: {FormatHelper.FormatTimeSpan(p.Remaining)}";
                else if (p.Elapsed.TotalSeconds > 0)
                    TxtDataExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)}";
                if (!string.IsNullOrEmpty(p.LogLine))
                    AppendLog(p.LogLine);
            });

            await Task.Run(() => buildService.BuildImageAsync(buildJob, buildProgress, ct));
            AppendLog($"[Info] ISO image created: {tempIso}");

            // Phase 2: Burn to disc
            AppendLog($"[Info] Burning to {drive.DisplayName}...");
            TxtDataExecStatus.Text = "Burning to disc...";

            var burnJob = new BurnJob
            {
                ImagePath = tempIso,
                DevicePath = drive.DevicePath,
                WriteMode = "DAO (Disc At Once)",
                WriteSpeed = GetSelectedText(CmbBBSpeed, "Auto"),
                Copies = (int)(NumBBCopies.Value ?? 1),
                EjectAfterBurn = ChkBBEject.IsChecked == true,
                VerifyAfterBurn = ChkBBVerify.IsChecked == true,
                SimulateOnly = ChkBBSimulate.IsChecked == true,
                BufferUnderrunProtection = ChkBBBup.IsChecked == true,
                CloseDisc = true
            };

            var burnService = new BurnService();
            var burnProgress = new Progress<BurnProgress>(p =>
            {
                PrgDataExec.Value = 40 + p.PercentComplete * 0.6; // 40-100% for burn
                TxtDataExecPercent.Text = $"{(int)PrgDataExec.Value}%";
                if (p.CurrentSpeedX > 0)
                    TxtDataExecSpeed.Text = $"{p.CurrentSpeedX:F1}x";
                if (p.Remaining.TotalSeconds > 0)
                    TxtDataExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)} | ETA: {FormatHelper.FormatTimeSpan(p.Remaining)}";
                else if (p.Elapsed.TotalSeconds > 0)
                    TxtDataExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)}";
                if (!string.IsNullOrEmpty(p.StatusMessage))
                    TxtDataExecStatus.Text = p.StatusMessage;
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
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            try { if (tempIso != null && File.Exists(tempIso)) File.Delete(tempIso); } catch { }
        }

        Dispatcher.UIThread.Post(() =>
        {
            PrgDataExec.Value = 100;
            TxtDataExecPercent.Text = "100%";
        });
    }

    // -----------------------------------------------------------------------
    // EXECUTE: Burn Existing Image
    // -----------------------------------------------------------------------
    private async Task ExecuteBurnImageAsync(CancellationToken ct)
    {
        TxtDataStep4Title.Text = "🔥 Burning Disc Image...";

        var drive = CmbBurnDrive.SelectedItem as DiscDrive;
        if (drive == null)
            throw new InvalidOperationException("No target drive selected.");

        var imagePath = TxtBurnImagePath.Text!.Trim();

        // Auto-detect CUE file for BIN/CUE
        string cuePath = string.Empty;
        var ext = Path.GetExtension(imagePath).ToUpperInvariant();
        if (ext == ".BIN")
        {
            var possibleCue = Path.ChangeExtension(imagePath, ".cue");
            if (File.Exists(possibleCue))
                cuePath = possibleCue;
        }
        else if (ext == ".CUE")
        {
            // User selected a .cue file directly — resolve the BIN file(s)
            cuePath = imagePath;
            var cueBinFiles = Native.Optical.BurnEngine.GetCueBinFiles(imagePath);
            if (cueBinFiles.Count == 1 && File.Exists(cueBinFiles[0]))
            {
                imagePath = cueBinFiles[0];
                AppendLog($"[Info] CUE sheet selected — using BIN file: {Path.GetFileName(cueBinFiles[0])}");
            }
            else if (cueBinFiles.Count > 1 && cueBinFiles.All(File.Exists))
            {
                // Multi-file CUE: use first BIN as the main image path;
                // BurnService will handle concatenation via the CUE path
                imagePath = cueBinFiles[0];
                AppendLog($"[Info] Multi-file CUE sheet — {cueBinFiles.Count} BIN files");
            }
            else if (cueBinFiles.Count > 0)
            {
                var firstExisting = cueBinFiles.FirstOrDefault(File.Exists);
                if (firstExisting != null)
                {
                    imagePath = firstExisting;
                    AppendLog($"[Info] CUE sheet selected — using BIN file: {Path.GetFileName(firstExisting)}");
                }
            }
        }

        var burnJob = new BurnJob
        {
            ImagePath = imagePath,
            CuePath = cuePath,
            DevicePath = drive.DevicePath,
            WriteMode = GetSelectedText(CmbBurnWriteMode, "TAO (Track At Once)"),
            WriteSpeed = GetSelectedText(CmbBurnSpeed, "Auto"),
            Copies = (int)(NumBurnCopies.Value ?? 1),
            EjectAfterBurn = ChkBurnEject.IsChecked == true,
            VerifyAfterBurn = ChkBurnVerify.IsChecked == true,
            SimulateOnly = ChkBurnSimulate.IsChecked == true,
            BufferUnderrunProtection = ChkBurnBup.IsChecked == true,
            CloseDisc = ChkBurnClose.IsChecked == true
        };

        AppendLog($"[Info] Burning {Path.GetFileName(imagePath)} to {drive.DisplayName}...");
        var burnService = new BurnService();
        var burnProgress = new Progress<BurnProgress>(p =>
        {
            PrgDataExec.Value = p.PercentComplete;
            TxtDataExecPercent.Text = $"{p.PercentComplete}%";
            if (p.CurrentSpeedX > 0)
                TxtDataExecSpeed.Text = $"{p.CurrentSpeedX:F1}x";
            if (p.Remaining.TotalSeconds > 0)
                TxtDataExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)} | ETA: {FormatHelper.FormatTimeSpan(p.Remaining)}";
            else if (p.Elapsed.TotalSeconds > 0)
                TxtDataExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)}";
            if (!string.IsNullOrEmpty(p.StatusMessage))
                TxtDataExecStatus.Text = p.StatusMessage;
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
            PrgDataExec.Value = 100;
            TxtDataExecPercent.Text = "100%";
            DiscViz.PercentComplete = 100;
            DiscViz.IsCompleted = true;
            DiscViz.IsActive = false;
            DiscViz.UpdateVisualization();
        });
    }

    // -----------------------------------------------------------------------
    // File management — Build Image mode
    // -----------------------------------------------------------------------
    private async void OnAddBuildFilesClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add Files to Disc",
            AllowMultiple = true
        });

        foreach (var file in files)
            AddFileItem(_buildFiles, file.Path.LocalPath);
        UpdateBuildFileCount();
    }

    private async void OnAddBuildFolderClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Add Folder to Disc",
            AllowMultiple = false
        });

        if (folders.Count > 0)
            AddFolderItem(_buildFiles, folders[0].Path.LocalPath);
        UpdateBuildFileCount();
    }

    private void OnRemoveBuildItemClick(object? sender, RoutedEventArgs e)
    {
        if (LstBuildFiles.SelectedItem is DataFileItem item)
            _buildFiles.Remove(item);
        UpdateBuildFileCount();
    }

    private void OnClearBuildItemsClick(object? sender, RoutedEventArgs e)
    {
        _buildFiles.Clear();
        UpdateBuildFileCount();
    }

    private void OnBuildDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.TryGetFiles() is { } files && files.Any()
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private void OnBuildDrop(object? sender, DragEventArgs e)
    {
        var items = e.DataTransfer.TryGetFiles()?.ToList();
        if (items == null) return;

        foreach (var item in items)
        {
            var path = item.Path.LocalPath;
            if (Directory.Exists(path))
                AddFolderItem(_buildFiles, path);
            else if (File.Exists(path))
                AddFileItem(_buildFiles, path);
        }
        UpdateBuildFileCount();
    }

    // -----------------------------------------------------------------------
    // File management — Build & Burn mode
    // -----------------------------------------------------------------------
    private async void OnAddBBFilesClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add Files to Disc",
            AllowMultiple = true
        });

        foreach (var file in files)
            AddFileItem(_bbFiles, file.Path.LocalPath);
        UpdateBBFileCount();
    }

    private async void OnAddBBFolderClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Add Folder to Disc",
            AllowMultiple = false
        });

        if (folders.Count > 0)
            AddFolderItem(_bbFiles, folders[0].Path.LocalPath);
        UpdateBBFileCount();
    }

    private void OnRemoveBBItemClick(object? sender, RoutedEventArgs e)
    {
        if (LstBBFiles.SelectedItem is DataFileItem item)
            _bbFiles.Remove(item);
        UpdateBBFileCount();
    }

    private void OnClearBBItemsClick(object? sender, RoutedEventArgs e)
    {
        _bbFiles.Clear();
        UpdateBBFileCount();
    }

    private void OnBBDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.TryGetFiles() is { } files && files.Any()
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private void OnBBDrop(object? sender, DragEventArgs e)
    {
        var items = e.DataTransfer.TryGetFiles()?.ToList();
        if (items == null) return;

        foreach (var item in items)
        {
            var path = item.Path.LocalPath;
            if (Directory.Exists(path))
                AddFolderItem(_bbFiles, path);
            else if (File.Exists(path))
                AddFileItem(_bbFiles, path);
        }
        UpdateBBFileCount();
    }

    // -----------------------------------------------------------------------
    // Burn Image — drag & drop
    // -----------------------------------------------------------------------
    private void OnBurnImageDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.TryGetFiles() is { } files && files.Any()
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private void OnBurnImageDrop(object? sender, DragEventArgs e)
    {
        var items = e.DataTransfer.TryGetFiles()?.ToList();
        if (items == null) return;

        foreach (var item in items)
        {
            var path = item.Path.LocalPath;
            if (File.Exists(path))
            {
                TxtBurnImagePath.Text = path;
                UpdateBurnImageInfo(path);
                break;
            }
        }
    }

    // -----------------------------------------------------------------------
    // Browse dialogs
    // -----------------------------------------------------------------------
    private async void OnBrowseBuildOutputClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Data Disc Image",
            SuggestedFileName = "DataDisc",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("ISO Image") { Patterns = new[] { "*.iso" } },
                new FilePickerFileType("All Files") { Patterns = new[] { "*.*" } }
            }
        });

        if (file != null)
            TxtBuildOutputPath.Text = file.Path.LocalPath;
    }

    private async void OnBrowseBurnImageClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Disc Image File",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Disc Images") { Patterns = new[] { "*.iso", "*.bin", "*.cue", "*.nrg", "*.mdf", "*.cdi", "*.img", "*.mds", "*.ccd", "*.obse" } },
                new FilePickerFileType("All Files") { Patterns = new[] { "*.*" } }
            }
        });

        if (files.Count > 0)
        {
            TxtBurnImagePath.Text = files[0].Path.LocalPath;
            UpdateBurnImageInfo(files[0].Path.LocalPath);
        }
    }

    private async void OnBrowseBootImageClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Boot Image File",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Boot Images") { Patterns = new[] { "*.bin", "*.img", "*.efi" } },
                new FilePickerFileType("All Files") { Patterns = new[] { "*.*" } }
            }
        });

        if (files.Count > 0)
            TxtBuildBootImage.Text = files[0].Path.LocalPath;
    }

    private async void OnBrowseEfiBootImageClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select EFI Boot Image File",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("EFI Images") { Patterns = new[] { "*.efi", "*.img" } },
                new FilePickerFileType("All Files") { Patterns = new[] { "*.*" } }
            }
        });

        if (files.Count > 0)
            TxtBuildEfiBootImage.Text = files[0].Path.LocalPath;
    }

    private void OnRefreshDrivesClick(object? sender, RoutedEventArgs e)
    {
        _ = LoadDrivesAsync();
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------
    private static void AddFileItem(ObservableCollection<DataFileItem> collection, string path)
    {
        if (!File.Exists(path)) return;
        if (collection.Any(f => f.FullPath == path)) return;

        var fi = new FileInfo(path);
        collection.Add(new DataFileItem
        {
            Name = fi.Name,
            FullPath = fi.FullName,
            IsDirectory = false,
            Size = fi.Length
        });
    }

    private static void AddFolderItem(ObservableCollection<DataFileItem> collection, string path)
    {
        if (!Directory.Exists(path)) return;
        if (collection.Any(f => f.FullPath == path)) return;

        var di = new DirectoryInfo(path);
        long size = 0;
        try { size = CalculateDirectorySize(path); } catch { }

        collection.Add(new DataFileItem
        {
            Name = di.Name,
            FullPath = di.FullName,
            IsDirectory = true,
            Size = size
        });
    }

    private static long CalculateDirectorySize(string path)
    {
        long size = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { size += new FileInfo(file).Length; } catch { }
            }
        }
        catch { }
        return size;
    }

    private void UpdateBuildFileCount()
    {
        var totalSize = _buildFiles.Sum(f => f.Size);
        TxtBuildFileCount.Text = $"({_buildFiles.Count} item(s), {FormatHelper.FormatBytes(totalSize)})";

        var discType = GetSelectedText(CmbBuildDiscType, "CD");
        var capacity = FormatHelper.GetCapacity(discType);
        TxtBuildCapacityInfo.Text = $"{FormatHelper.FormatBytes(totalSize)} / {FormatHelper.FormatBytes(capacity)} ({discType})";
    }

    private void UpdateBBFileCount()
    {
        var totalSize = _bbFiles.Sum(f => f.Size);
        TxtBBFileCount.Text = $"({_bbFiles.Count} item(s), {FormatHelper.FormatBytes(totalSize)})";

        var discType = GetSelectedText(CmbBBDiscType, "CD");
        var capacity = FormatHelper.GetCapacity(discType);
        TxtBBCapacityInfo.Text = $"{FormatHelper.FormatBytes(totalSize)} / {FormatHelper.FormatBytes(capacity)} ({discType})";
    }

    private void UpdateBurnImageInfo(string path)
    {
        if (File.Exists(path))
        {
            var fi = new FileInfo(path);
            var ext = fi.Extension.ToUpperInvariant();

            // For CUE files, show the total BIN data size instead of the CUE file size
            if (ext == ".CUE")
            {
                var binFiles = Native.Optical.BurnEngine.GetCueBinFiles(path);
                long totalBinSize = 0;
                foreach (var binFile in binFiles)
                {
                    if (File.Exists(binFile))
                        totalBinSize += new FileInfo(binFile).Length;
                }
                if (totalBinSize > 0)
                    TxtBurnImageInfo.Text = $"Size: {FormatHelper.FormatBytes(totalBinSize)} | Format: BIN/CUE ({binFiles.Count} file(s))";
                else
                    TxtBurnImageInfo.Text = $"Size: {FormatHelper.FormatBytes(fi.Length)} | Format: {ext} (⚠ no BIN files found)";
            }
            else
            {
                TxtBurnImageInfo.Text = $"Size: {FormatHelper.FormatBytes(fi.Length)} | Format: {ext}";
            }
        }
        else
        {
            TxtBurnImageInfo.Text = string.Empty;
        }
    }

    private string GetTotalBuildSize() => FormatHelper.FormatBytes(_buildFiles.Sum(f => f.Size));
    private string GetTotalBBSize() => FormatHelper.FormatBytes(_bbFiles.Sum(f => f.Size));

    private static string GetSelectedText(ComboBox cmb, string fallback)
    {
        if (cmb.SelectedItem is ComboBoxItem item)
            return item.Content?.ToString() ?? fallback;
        return cmb.SelectedItem?.ToString() ?? fallback;
    }

    private static void StageFiles(ObservableCollection<DataFileItem> items, string targetDir)
    {
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            if (item.IsDirectory)
            {
                var destDir = Path.Combine(targetDir, item.Name);
                CopyDirectory(item.FullPath, destDir);
                usedNames.Add(item.Name);
            }
            else
            {
                var destFile = Path.Combine(targetDir, item.Name);
                // Avoid collisions against both filesystem and other items
                if (usedNames.Contains(item.Name) || File.Exists(destFile))
                    destFile = Path.Combine(targetDir, $"{Path.GetFileNameWithoutExtension(item.Name)}_{Guid.NewGuid().ToString("N")[..4]}{Path.GetExtension(item.Name)}");
                File.Copy(item.FullPath, destFile);
                usedNames.Add(Path.GetFileName(destFile));
            }
        }
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var destFile = Path.Combine(destinationDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: true);
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var destSubDir = Path.Combine(destinationDir, new DirectoryInfo(dir).Name);
            CopyDirectory(dir, destSubDir);
        }
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

    private static string MapBootEmulation(string text) => text switch
    {
        "1.2 MB Floppy" => "Floppy1200",
        "1.44 MB Floppy" => "Floppy1440",
        "2.88 MB Floppy" => "Floppy2880",
        "Hard Disk" => "HardDisk",
        _ => "NoEmulation"
    };

    private static string MapBootPlatform(string text) => text switch
    {
        "EFI" => "EFI",
        "PowerPC" => "PowerPC",
        "Mac" => "Mac",
        _ => "x86"
    };

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
        TxtDataExecLog.Text = string.IsNullOrEmpty(TxtDataExecLog.Text)
            ? line
            : TxtDataExecLog.Text + Environment.NewLine + line;

        // Trim log to prevent unbounded memory growth (keep last ~20 KB)
        var text = TxtDataExecLog.Text;
        if (text != null && text.Length > 30_000)
        {
            var trimIdx = text.IndexOf('\n', text.Length - 20_000);
            if (trimIdx > 0)
                TxtDataExecLog.Text = "...\n" + text[(trimIdx + 1)..];
            else
                TxtDataExecLog.Text = "...\n" + text[(text.Length - 20_000)..];
        }

        TxtDataExecLog.CaretIndex = TxtDataExecLog.Text?.Length ?? 0;
    }
}

// -----------------------------------------------------------------------
// Data model for the wizard list items
// -----------------------------------------------------------------------

/// <summary>Represents a file or folder in the Data Disc Wizard.</summary>
public class DataFileItem
{
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public long Size { get; set; }
    public string Icon => IsDirectory ? "📁" : "📄";
    public string SizeText => FormatHelper.FormatBytes(Size);
}
