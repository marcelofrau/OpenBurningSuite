// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
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

public partial class GameWizardView : UserControl
{
    // -----------------------------------------------------------------------
    // Mode enum
    // -----------------------------------------------------------------------
    private enum GameWizardMode { None, FromImage, FromFolder }

    // -----------------------------------------------------------------------
    // State
    // -----------------------------------------------------------------------
    private GameWizardMode _mode = GameWizardMode.None;
    private int _currentStep = 1;

    private CancellationTokenSource? _cts;
    private bool _isRunning;

    // Services
    private DiscDiscoveryService? _discovery;
    private List<DiscDrive> _drives = new();

    public GameWizardView()
    {
        InitializeComponent();

        // Populate gaming system combo boxes with all available presets
        var presetNames = FormatHelper.GamingPresets.Keys.OrderBy(k => k).ToList();
        CmbGameSystem.ItemsSource = presetNames;
        CmbGameFolderSystem.ItemsSource = presetNames;

        // Filter file systems based on disc type selection in From Folder mode
        CmbGameFolderDiscType.SelectionChanged += OnGameFolderDiscTypeChanged;

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

            CmbGameDrive.ItemsSource = _drives;
            CmbGameFolderDrive.ItemsSource = _drives;

            if (_drives.Count > 0)
            {
                var writable = _drives.FirstOrDefault(d => d.CanWrite) ?? _drives[0];
                CmbGameDrive.SelectedItem = writable;
                CmbGameFolderDrive.SelectedItem = writable;
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
    private void OnModeFromImage(object? sender, RoutedEventArgs e) => SetMode(GameWizardMode.FromImage);
    private void OnModeFromFolder(object? sender, RoutedEventArgs e) => SetMode(GameWizardMode.FromFolder);

    private void SetMode(GameWizardMode mode)
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
        GStep1Panel.IsVisible = false;
        GStep2ImagePanel.IsVisible = false;
        GStep2FolderPanel.IsVisible = false;
        GStep3Panel.IsVisible = false;
        GStep4Panel.IsVisible = false;

        switch (step)
        {
            case 1:
                GStep1Panel.IsVisible = true;
                break;
            case 2:
                switch (_mode)
                {
                    case GameWizardMode.FromImage: GStep2ImagePanel.IsVisible = true; break;
                    case GameWizardMode.FromFolder: GStep2FolderPanel.IsVisible = true; break;
                }
                break;
            case 3:
                GStep3Panel.IsVisible = true;
                PopulateSummary();
                break;
            case 4:
                GStep4Panel.IsVisible = true;
                break;
        }

        UpdateStepIndicators();
        UpdateNavButtons();
    }

    private void UpdateStepIndicators()
    {
        SetStepIndicator(GStepInd1, GStepLbl1, _currentStep >= 1, _currentStep == 1);
        SetStepIndicator(GStepInd2, GStepLbl2, _currentStep >= 2, _currentStep == 2);
        SetStepIndicator(GStepInd3, GStepLbl3, _currentStep >= 3, _currentStep == 3);
        SetStepIndicator(GStepInd4, GStepLbl4, _currentStep >= 4, _currentStep == 4);
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
        BtnGameBack.IsVisible = _currentStep > 1 && !_isRunning;
        BtnGameNext.IsVisible = _currentStep == 2 && !_isRunning;
        BtnGameExecute.IsVisible = _currentStep == 3 && !_isRunning;
        BtnGameCancel.IsVisible = _currentStep == 4 && _isRunning;
        BtnGameFinish.IsVisible = _currentStep == 4 && !_isRunning;
    }

    private void OnGameBackClick(object? sender, RoutedEventArgs e) => GoToStep(_currentStep - 1);

    private void OnGameNextClick(object? sender, RoutedEventArgs e)
    {
        var error = ValidateStep2();
        if (error != null)
        {
            AppendLog($"[Error] {error}");
            return;
        }
        GoToStep(3);
    }

    private void OnGameFinishClick(object? sender, RoutedEventArgs e)
    {
        _mode = GameWizardMode.None;
        _isRunning = false;
        GoToStep(1);
    }

    private async void OnGameExecuteClick(object? sender, RoutedEventArgs e)
    {
        GoToStep(4);
        _isRunning = true;
        UpdateNavButtons();

        // Reset step 4 UI for a fresh operation
        PrgGameExec.Value = 0;
        TxtGameExecPercent.Text = "0%";
        TxtGameExecSpeed.Text = string.Empty;
        TxtGameExecStatus.Text = "Preparing...";
        TxtGameExecLog.Text = string.Empty;
        TxtGameStep4Title.Text = "Executing...";

        // Reset disc visualization for a fresh operation
        DiscVizBorder.IsVisible = false;
        DiscViz.IsActive = false;
        DiscViz.IsCompleted = false;
        DiscViz.VerifyPassed = null;
        DiscViz.BadSectors = 0;
        DiscViz.PercentComplete = 0;
        DiscViz.CurrentLba = 0;
        DiscViz.TotalSectors = 0;

        // Configure layer visualization from the target drive's media type
        var gameDriveForViz = CmbGameDrive.SelectedItem as DiscDrive
                           ?? CmbGameFolderDrive.SelectedItem as DiscDrive;
        DiscViz.ConfigureForMedia(gameDriveForViz?.CurrentMedia?.MediaType);

        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        try
        {
            switch (_mode)
            {
                case GameWizardMode.FromImage:
                    await ExecuteFromImageAsync(_cts.Token);
                    break;
                case GameWizardMode.FromFolder:
                    await ExecuteFromFolderAsync(_cts.Token);
                    break;
            }

            TxtGameStep4Title.Text = "✅ Complete!";
            TxtGameExecStatus.Text = "Operation completed successfully.";
        }
        catch (OperationCanceledException)
        {
            TxtGameStep4Title.Text = "⏹ Cancelled";
            TxtGameExecStatus.Text = "Operation was cancelled by user.";
            AppendLog("[Info] Operation cancelled.");
            DiscViz.IsActive = false;
            DiscViz.IsCompleted = false;
            DiscViz.UpdateVisualization();
        }
        catch (Exception ex)
        {
            TxtGameStep4Title.Text = "❌ Failed";
            TxtGameExecStatus.Text = $"Error: {ex.Message}";
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

    private void OnGameCancelClick(object? sender, RoutedEventArgs e)
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
            case GameWizardMode.FromImage:
                if (string.IsNullOrWhiteSpace(TxtGameImagePath.Text))
                    return "Please specify a game disc image file.";
                if (!File.Exists(TxtGameImagePath.Text.Trim()))
                    return "The specified image file does not exist.";
                if (CmbGameSystem.SelectedItem == null)
                    return "Please select a gaming system.";
                if (CmbGameDrive.SelectedItem == null)
                    return "Please select a target drive for burning.";
                return null;

            case GameWizardMode.FromFolder:
                if (string.IsNullOrWhiteSpace(TxtGameFolderPath.Text))
                    return "Please specify a game folder.";
                if (!Directory.Exists(TxtGameFolderPath.Text.Trim()))
                    return "The specified folder does not exist.";
                if (CmbGameFolderSystem.SelectedItem == null)
                    return "Please select a gaming system.";
                // Validate output directory exists when a custom output path is specified
                if (!string.IsNullOrWhiteSpace(TxtGameFolderOutputPath.Text))
                {
                    var outputDir = Path.GetDirectoryName(TxtGameFolderOutputPath.Text.Trim());
                    if (!string.IsNullOrWhiteSpace(outputDir) && !Directory.Exists(outputDir))
                        return $"Output directory does not exist: {outputDir}";
                }
                if (ChkGameFolderBurn.IsChecked == true && CmbGameFolderDrive.SelectedItem == null)
                    return "Please select a target drive for burning.";
                if (ChkGameFolderBurn.IsChecked != true && string.IsNullOrWhiteSpace(TxtGameFolderOutputPath.Text))
                    return "Please specify an output image path, or enable 'Burn to Disc'.";
                return null;

            default:
                return "Please choose a source type first.";
        }
    }

    // -----------------------------------------------------------------------
    // Summary (Step 3)
    // -----------------------------------------------------------------------
    private void PopulateSummary()
    {
        GameSummaryContent.Children.Clear();

        switch (_mode)
        {
            case GameWizardMode.FromImage:
                var systemName = CmbGameSystem.SelectedItem?.ToString() ?? "Unknown";
                AddSummaryLine("💿 Task", "Burn Game Disc from Image");
                AddSummaryLine("📄 Image File", TxtGameImagePath.Text ?? "(not set)");
                if (File.Exists(TxtGameImagePath.Text?.Trim()))
                {
                    var fi = new FileInfo(TxtGameImagePath.Text!.Trim());
                    var imgExt = Path.GetExtension(fi.Name).ToUpperInvariant();
                    // For CUE files, show the total BIN data size
                    if (imgExt == ".CUE")
                    {
                        var binFiles = Native.Optical.BurnEngine.GetCueBinFiles(fi.FullName);
                        long totalBinSize = 0;
                        foreach (var binFile in binFiles)
                        {
                            if (File.Exists(binFile))
                                totalBinSize += new FileInfo(binFile).Length;
                        }
                        if (totalBinSize > 0)
                            AddSummaryLine("📏 Image Size", FormatHelper.FormatBytes(totalBinSize));
                        else
                            AddSummaryLine("📏 Image Size", "⚠ No BIN files found");
                    }
                    else
                    {
                        AddSummaryLine("📏 Image Size", FormatHelper.FormatBytes(fi.Length));
                    }
                }
                AddSummaryLine("🎮 Gaming System", systemName);
                // PS3 encryption status check in summary
                var trimmedImagePath = TxtGameImagePath.Text?.Trim();
                if (systemName == "PlayStation 3" && File.Exists(trimmedImagePath))
                {
                    try
                    {
                        var bdInfo = GamingDiscService.DetectBluRayContent(trimmedImagePath!);
                        if (bdInfo.ContentType == BluRayContentType.Ps3Game)
                        {
                            var encStatus = bdInfo.IsEncrypted ? "🔒 Encrypted" : "🔓 Decrypted";
                            AddSummaryLine("🔐 Encryption Status", encStatus);
                            if (bdInfo.IsEncrypted)
                            {
                                // Check if user has provided a disc key for decryption
                                var hasKey = GetPs3DiscKey() != null;
                                AddSummaryLine("⚠ Warning", hasKey
                                    ? "PS3 ISO will be decrypted before burning."
                                    : "Encrypted PS3 ISOs will not work on consoles. Provide an IRD file or disc key.");
                            }
                        }
                    }
                    catch { /* best-effort */ }
                }
                if (FormatHelper.GamingPresets.TryGetValue(systemName, out var preset))
                {
                    AddSummaryLine("📀 Sector Size", $"{preset.SectorSize} bytes");
                    if (!string.IsNullOrEmpty(preset.SubchannelMode) && preset.SubchannelMode != "None")
                        AddSummaryLine("📡 Subchannel", preset.SubchannelMode);
                }
                var gameDrive = CmbGameDrive.SelectedItem as DiscDrive;
                AddSummaryLine("💿 Target Drive", gameDrive?.DisplayName ?? "(none)");
                AddSummaryLine("⚡ Write Speed", GetSelectedText(CmbGameSpeed, "2x"));
                AddSummaryLine("📝 Write Mode", GetSelectedText(CmbGameWriteMode, "DAO"));
                AddSummaryLine("📦 Copies", ((int)(NumGameCopies.Value ?? 1)).ToString());
                if (ChkGameVerify.IsChecked == true)
                    AddSummaryLine("✅ Verify After Burn", "Yes");
                if (ChkGameSimulate.IsChecked == true)
                    AddSummaryLine("🔬 Simulation Mode", "Yes (no actual write)");
                // Gaming patches summary
                if (ChkGameEsr.IsChecked == true)
                    AddSummaryLine("🎮 ESR Patch", "Yes (PS2 DVD-R)");
                if (ChkGameMasterDisc.IsChecked == true)
                {
                    var mdRegion = (CmbGameMasterRegion.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "NTSC-U";
                    AddSummaryLine("🎮 Master Disc Patch", $"Yes ({mdRegion})");
                }
                if (ChkGamePsx80Min.IsChecked == true)
                    AddSummaryLine("🎮 PSX 80min Patch", "Yes");
                if (ChkGamePsxUndither.IsChecked == true)
                    AddSummaryLine("🎮 PSX Undither", "Yes");
                break;

            case GameWizardMode.FromFolder:
                var folderSystem = CmbGameFolderSystem.SelectedItem?.ToString() ?? "Unknown";
                AddSummaryLine("📁 Task", "Build & Burn Game Disc from Folder");
                AddSummaryLine("📂 Source Folder", TxtGameFolderPath.Text ?? "(not set)");
                AddSummaryLine("🎮 Gaming System", folderSystem);
                AddSummaryLine("📀 Disc Type", GetSelectedText(CmbGameFolderDiscType, "CD"));
                AddSummaryLine("📂 File System", GetSelectedText(CmbGameFolderFileSystem, "ISO 9660"));
                AddSummaryLine("🏷 Volume Label", string.IsNullOrWhiteSpace(TxtGameFolderVolumeLabel.Text) ? "GAME_DISC" : TxtGameFolderVolumeLabel.Text!);

                if (!string.IsNullOrWhiteSpace(TxtGameFolderOutputPath.Text))
                    AddSummaryLine("💾 Output Image", TxtGameFolderOutputPath.Text);
                else
                    AddSummaryLine("💾 Output Image", "(temporary file)");

                if (ChkGameFolderBurn.IsChecked == true)
                {
                    var folderDrive = CmbGameFolderDrive.SelectedItem as DiscDrive;
                    AddSummaryLine("💿 Target Drive", folderDrive?.DisplayName ?? "(none)");
                    AddSummaryLine("⚡ Write Speed", GetSelectedText(CmbGameFolderSpeed, "2x"));
                    if (ChkGameFolderVerify.IsChecked == true)
                        AddSummaryLine("✅ Verify After Burn", "Yes");
                    if (ChkGameFolderSimulate.IsChecked == true)
                        AddSummaryLine("🔬 Simulation Mode", "Yes (no actual write)");
                }
                else
                {
                    AddSummaryLine("🔥 Burn to Disc", "No (image only)");
                }
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
        GameSummaryContent.Children.Add(sp);
    }

    // -----------------------------------------------------------------------
    // EXECUTE: Burn from Image File
    // -----------------------------------------------------------------------
    private async Task ExecuteFromImageAsync(CancellationToken ct)
    {
        TxtGameStep4Title.Text = "🔥 Burning Game Disc...";

        var drive = CmbGameDrive.SelectedItem as DiscDrive;
        if (drive == null)
            throw new InvalidOperationException("No target drive selected.");

        var imagePath = TxtGameImagePath.Text!.Trim();
        var systemName = CmbGameSystem.SelectedItem?.ToString() ?? string.Empty;

        // Validate image before burning
        var validationMessages = GamingDiscService.ValidateImage(imagePath, systemName);
        foreach (var msg in validationMessages)
            AppendLog($"[Validation] {msg}");

        // Auto-detect CUE file for BIN/CUE images
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
            // User selected a .cue file directly — set CuePath and resolve the BIN file
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
                // Try using the first BIN that exists
                var firstExisting = cueBinFiles.FirstOrDefault(File.Exists);
                if (firstExisting != null)
                    imagePath = firstExisting;
            }
        }

        // ---- PS3 decryption before burning ----
        string? tempDecryptedPath = null;
        if (systemName == "PlayStation 3" && ChkPs3DecryptBeforeBurn.IsChecked == true)
        {
            try
            {
                var bdInfo = GamingDiscService.DetectBluRayContent(imagePath);
                if (bdInfo.ContentType == BluRayContentType.Ps3Game && bdInfo.IsEncrypted)
                {
                    var discKey = GetPs3DiscKey();
                    if (discKey == null || discKey.Length != 16)
                    {
                        AppendLog("❌ PS3 ISO is encrypted but no valid disc key was provided.");
                        AppendLog("   Please provide an IRD file, .dkey file, or hex key to decrypt.");
                        throw new InvalidOperationException(
                            "PS3 ISO is encrypted but no valid disc key was provided. " +
                            "Please provide an IRD file, .dkey file, or hex key.");
                    }

                    AppendLog("🔓 Decrypting PS3 ISO before burning...");
                    TxtGameStep4Title.Text = "🔓 Decrypting PS3 ISO...";

                    // Create temp file for decrypted ISO
                    tempDecryptedPath = Path.Combine(Path.GetTempPath(),
                        "obs_ps3dec_" + Guid.NewGuid().ToString("N")[..8] + ".iso");

                    var decryptProgress = new Progress<Ps3DiscService.Ps3DecryptionProgress>(dp =>
                    {
                        // Progress<T> already marshals to UI thread
                        PrgGameExec.Value = dp.PercentComplete;
                        TxtGameExecPercent.Text = $"{dp.PercentComplete}%";
                        TxtGameExecStatus.Text = dp.StatusMessage;
                    });

                    var decryptResult = await Ps3DiscService.DecryptIsoAsync(
                        imagePath, tempDecryptedPath, discKey, decryptProgress, ct);

                    if (decryptResult != null)
                    {
                        AppendLog($"❌ PS3 decryption failed: {decryptResult}");
                        try { if (File.Exists(tempDecryptedPath)) File.Delete(tempDecryptedPath); } catch { }
                        throw new InvalidOperationException($"PS3 decryption failed: {decryptResult}");
                    }

                    AppendLog("✅ PS3 ISO decrypted successfully.");
                    imagePath = tempDecryptedPath;
                    TxtGameStep4Title.Text = "🔥 Burning Game Disc...";
                }
                else if (bdInfo.ContentType == BluRayContentType.Ps3Game && !bdInfo.IsEncrypted)
                {
                    AppendLog("[Info] PS3 ISO is already decrypted — no decryption needed.");
                }
            }
            catch (InvalidOperationException)
            {
                // Re-throw decryption/validation failures so the caller shows
                // the error state instead of proceeding with the burn.
                throw;
            }
            catch (Exception ex)
            {
                AppendLog($"[Warning] PS3 encryption check failed: {ex.Message}");
            }
        }

        try
        {

        // Build BurnJob — apply gaming preset first, then override with user settings
        var burnJob = new BurnJob
        {
            ImagePath = imagePath,
            CuePath = cuePath,
            DevicePath = drive.DevicePath,
            // Gaming patches
            EsrPatching = ChkGameEsr.IsChecked == true,
            MasterDiscPatching = ChkGameMasterDisc.IsChecked == true,
            MasterDiscRegion = (CmbGameMasterRegion.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "NTSC-U",
            Psx80MinutePatching = ChkGamePsx80Min.IsChecked == true,
            PsxUndither = ChkGamePsxUndither.IsChecked == true
        };

        // Apply gaming preset for optimal write speed, mode, and close settings
        GamingDiscService.ApplyBurnPreset(burnJob, systemName);

        // Override with user-selected values (these take precedence over preset)
        var userSpeed = GetSelectedText(CmbGameSpeed, "Auto");
        if (userSpeed != "Auto")
            burnJob.WriteSpeed = userSpeed;
        burnJob.WriteMode = GetSelectedText(CmbGameWriteMode, "DAO (Disc At Once)");
        burnJob.Copies = (int)(NumGameCopies.Value ?? 1);
        burnJob.EjectAfterBurn = ChkGameEject.IsChecked == true;
        burnJob.VerifyAfterBurn = ChkGameVerify.IsChecked == true;
        burnJob.SimulateOnly = ChkGameSimulate.IsChecked == true;
        burnJob.BufferUnderrunProtection = ChkGameBup.IsChecked == true;
        burnJob.CloseDisc = true;

        AppendLog($"[Info] Gaming system: {systemName}");
        AppendLog($"[Info] Burning {Path.GetFileName(imagePath)} to {drive.DisplayName}...");
        AppendLog($"[Info] Write mode: {burnJob.WriteMode}, Speed: {burnJob.WriteSpeed}");

        var burnService = new BurnService();
        var burnProgress = new Progress<BurnProgress>(p =>
        {
            PrgGameExec.Value = p.PercentComplete;
            TxtGameExecPercent.Text = $"{p.PercentComplete}%";
            if (p.CurrentSpeedX > 0)
                TxtGameExecSpeed.Text = $"{p.CurrentSpeedX:F1}x";
            if (p.Remaining.TotalSeconds > 0)
                TxtGameExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)} | ETA: {FormatHelper.FormatTimeSpan(p.Remaining)}";
            else if (p.Elapsed.TotalSeconds > 0)
                TxtGameExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)}";
            if (!string.IsNullOrEmpty(p.StatusMessage))
                TxtGameExecStatus.Text = p.StatusMessage;
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
        AppendLog("[Info] Game disc burn complete!");

        // Note: Post-burn verification is handled internally by BurnEngine when
        // VerifyAfterBurn is set. Do NOT add a second verify here — the disc may
        // already be ejected, and BurnEngine's sector-comparison verify is more thorough.

        Dispatcher.UIThread.Post(() =>
        {
            PrgGameExec.Value = 100;
            TxtGameExecPercent.Text = "100%";
            DiscViz.PercentComplete = 100;
            DiscViz.IsCompleted = true;
            DiscViz.IsActive = false;
            DiscViz.UpdateVisualization();
        });

        } // end try
        finally
        {
            // Clean up temporary decrypted PS3 ISO
            if (tempDecryptedPath != null)
            {
                try { if (File.Exists(tempDecryptedPath)) File.Delete(tempDecryptedPath); } catch { }
            }
        }
    }

    // -----------------------------------------------------------------------
    // EXECUTE: Build from Folder
    // -----------------------------------------------------------------------
    private async Task ExecuteFromFolderAsync(CancellationToken ct)
    {
        TxtGameStep4Title.Text = "🏗 Building Game Disc...";

        var sourceFolder = TxtGameFolderPath.Text!.Trim();
        var systemName = CmbGameFolderSystem.SelectedItem?.ToString() ?? string.Empty;

        var volumeLabel = string.IsNullOrWhiteSpace(TxtGameFolderVolumeLabel.Text)
            ? "GAME_DISC"
            : TxtGameFolderVolumeLabel.Text!.Trim();

        var fsText = GetSelectedText(CmbGameFolderFileSystem, "ISO 9660");
        var (fileSystem, udfVersion) = ParseFileSystem(fsText);

        // Determine output path
        var outputPath = string.IsNullOrWhiteSpace(TxtGameFolderOutputPath.Text)
            ? Path.Combine(Path.GetTempPath(),
                $"OpenBurningSuite_Game_{volumeLabel}_{Guid.NewGuid().ToString("N")[..8]}.iso")
            : TxtGameFolderOutputPath.Text!.Trim();

        bool isTempOutput = string.IsNullOrWhiteSpace(TxtGameFolderOutputPath.Text);

        try
        {
            // Phase 1: Build ISO image
            AppendLog($"[Info] Building game disc image from: {sourceFolder}");
            AppendLog($"[Info] Gaming system: {systemName}");
            TxtGameExecStatus.Text = "Building disc image...";

            var buildJob = new BuildJob
            {
                DiscType = GetSelectedText(CmbGameFolderDiscType, "CD"),
                FileSystem = fileSystem,
                UdfVersion = udfVersion,
                SourceFolder = sourceFolder,
                OutputImagePath = outputPath,
                VolumeLabel = volumeLabel,
                GamingSystem = systemName,
                JolietLongFilenames = fileSystem.Contains("Joliet"),
                DeepDirectoryNesting = true
            };

            bool willBurn = ChkGameFolderBurn.IsChecked == true;
            double buildWeight = willBurn ? 0.4 : 1.0;

            var buildService = new BuildService();
            var buildProgress = new Progress<BuildProgress>(p =>
            {
                PrgGameExec.Value = p.PercentComplete * buildWeight;
                TxtGameExecPercent.Text = $"{(int)PrgGameExec.Value}%";
                if (!string.IsNullOrEmpty(p.StatusMessage))
                    TxtGameExecStatus.Text = p.StatusMessage;
                if (p.Remaining.TotalSeconds > 0)
                    TxtGameExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)} | ETA: {FormatHelper.FormatTimeSpan(p.Remaining)}";
                else if (p.Elapsed.TotalSeconds > 0)
                    TxtGameExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)}";
                if (!string.IsNullOrEmpty(p.LogLine))
                    AppendLog(p.LogLine);
            });

            await Task.Run(() => buildService.BuildImageAsync(buildJob, buildProgress, ct));
            AppendLog($"[Info] Game disc image created: {outputPath}");

            // Phase 2: Burn to disc (optional)
            if (willBurn)
            {
                var drive = CmbGameFolderDrive.SelectedItem as DiscDrive;
                if (drive == null)
                {
                    AppendLog("[Warning] No drive selected — skipping burn.");
                    return;
                }

                var burnJob = new BurnJob
                {
                    ImagePath = outputPath,
                    DevicePath = drive.DevicePath,
                };

                // Apply gaming preset for optimal write speed, mode, and close settings
                GamingDiscService.ApplyBurnPreset(burnJob, systemName);

                // Override with user-selected values (these take precedence over preset)
                var userSpeed = GetSelectedText(CmbGameFolderSpeed, "Auto");
                if (userSpeed != "Auto")
                    burnJob.WriteSpeed = userSpeed;
                burnJob.Copies = 1;
                burnJob.EjectAfterBurn = ChkGameFolderEject.IsChecked == true;
                burnJob.VerifyAfterBurn = ChkGameFolderVerify.IsChecked == true;
                burnJob.SimulateOnly = ChkGameFolderSimulate.IsChecked == true;
                burnJob.BufferUnderrunProtection = ChkGameFolderBup.IsChecked == true;
                burnJob.CloseDisc = true;

                AppendLog($"[Info] Burning to {drive.DisplayName}...");
                TxtGameExecStatus.Text = "Burning game disc...";

                var burnService = new BurnService();
                var burnProgress = new Progress<BurnProgress>(p =>
                {
                    PrgGameExec.Value = 40 + p.PercentComplete * 0.6;
                    TxtGameExecPercent.Text = $"{(int)PrgGameExec.Value}%";
                    if (p.CurrentSpeedX > 0)
                        TxtGameExecSpeed.Text = $"{p.CurrentSpeedX:F1}x";
                    if (p.Remaining.TotalSeconds > 0)
                        TxtGameExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)} | ETA: {FormatHelper.FormatTimeSpan(p.Remaining)}";
                    else if (p.Elapsed.TotalSeconds > 0)
                        TxtGameExecETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)}";
                    if (!string.IsNullOrEmpty(p.StatusMessage))
                        TxtGameExecStatus.Text = p.StatusMessage;
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
                AppendLog("[Info] Game disc burn complete!");

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
        }
        finally
        {
            // Clean up temp ISO if we created one
            if (isTempOutput)
            {
                try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
            }
        }

        Dispatcher.UIThread.Post(() =>
        {
            PrgGameExec.Value = 100;
            TxtGameExecPercent.Text = "100%";
        });
    }

    // -----------------------------------------------------------------------
    // Gaming System Selection Changed
    // -----------------------------------------------------------------------
    private void OnGameSystemChanged(object? sender, SelectionChangedEventArgs e)
    {
        var systemName = CmbGameSystem.SelectedItem?.ToString();
        if (string.IsNullOrEmpty(systemName)) return;

        if (FormatHelper.GamingPresets.TryGetValue(systemName, out var preset))
        {
            TxtGamePresetDesc.Text = preset.Description;

            // Auto-set recommended write speed
            if (!string.IsNullOrEmpty(preset.WriteSpeed))
            {
                foreach (var obj in CmbGameSpeed.Items)
                {
                    if (obj is ComboBoxItem item && item.Content?.ToString() == preset.WriteSpeed)
                    {
                        CmbGameSpeed.SelectedItem = item;
                        break;
                    }
                }
            }

            // Auto-set recommended write mode
            if (!string.IsNullOrEmpty(preset.WriteMode))
            {
                foreach (var obj in CmbGameWriteMode.Items)
                {
                    if (obj is ComboBoxItem item && item.Content?.ToString()?.Contains(preset.WriteMode.Split(' ')[0]) == true)
                    {
                        CmbGameWriteMode.SelectedItem = item;
                        break;
                    }
                }
            }
        }

        // Re-run validation on the image if one is already selected
        if (!string.IsNullOrWhiteSpace(TxtGameImagePath.Text) && File.Exists(TxtGameImagePath.Text.Trim()))
        {
            var validationMessages = GamingDiscService.ValidateImage(TxtGameImagePath.Text.Trim(), systemName);
            TxtGameValidation.Text = validationMessages.Count > 0
                ? string.Join(Environment.NewLine, validationMessages)
                : "✅ Image validation passed.";
        }

        // Show/hide PS3 decryption panel based on gaming system
        PnlPs3Decrypt.IsVisible = systemName == "PlayStation 3";
    }

    private void OnGameFolderSystemChanged(object? sender, SelectionChangedEventArgs e)
    {
        var systemName = CmbGameFolderSystem.SelectedItem?.ToString();
        if (string.IsNullOrEmpty(systemName)) return;

        if (FormatHelper.GamingPresets.TryGetValue(systemName, out var preset))
        {
            TxtGameFolderPresetDesc.Text = preset.Description;

            // Auto-set disc type and file system based on gaming system
            AutoSetDiscTypeForSystem(systemName);

            // Auto-set recommended write speed
            if (!string.IsNullOrEmpty(preset.WriteSpeed))
            {
                foreach (var obj in CmbGameFolderSpeed.Items)
                {
                    if (obj is ComboBoxItem item && item.Content?.ToString() == preset.WriteSpeed)
                    {
                        CmbGameFolderSpeed.SelectedItem = item;
                        break;
                    }
                }
            }
        }
    }

    /// <summary>Updates the file system combo when the game folder disc type changes manually.</summary>
    private void OnGameFolderDiscTypeChanged(object? sender, SelectionChangedEventArgs e)
    {
        var discType = GetSelectedText(CmbGameFolderDiscType, "CD");
        var currentFs = GetSelectedText(CmbGameFolderFileSystem, "");
        var compatibleFs = FormatHelper.GetCompatibleFileSystems(discType);

        CmbGameFolderFileSystem.Items.Clear();
        foreach (var fs in compatibleFs)
            CmbGameFolderFileSystem.Items.Add(new ComboBoxItem { Content = fs });

        // Try to re-select the previously selected file system if still compatible
        bool reselected = false;
        for (int i = 0; i < CmbGameFolderFileSystem.Items.Count; i++)
        {
            if (CmbGameFolderFileSystem.Items[i] is ComboBoxItem item &&
                string.Equals(item.Content?.ToString(), currentFs, StringComparison.OrdinalIgnoreCase))
            {
                CmbGameFolderFileSystem.SelectedIndex = i;
                reselected = true;
                break;
            }
        }
        if (!reselected)
        {
            var defaultFs = FormatHelper.GetDefaultFileSystem(discType);
            for (int i = 0; i < CmbGameFolderFileSystem.Items.Count; i++)
            {
                if (CmbGameFolderFileSystem.Items[i] is ComboBoxItem item &&
                    string.Equals(item.Content?.ToString(), defaultFs, StringComparison.OrdinalIgnoreCase))
                {
                    CmbGameFolderFileSystem.SelectedIndex = i;
                    reselected = true;
                    break;
                }
            }
            if (!reselected && CmbGameFolderFileSystem.Items.Count > 0)
                CmbGameFolderFileSystem.SelectedIndex = 0;
        }
    }

    private void AutoSetDiscTypeForSystem(string systemName)
    {
        // Set appropriate disc type and file system based on gaming system
        string discType;
        string fileSystem;

        switch (systemName)
        {
            case "PlayStation 1":
            case "Sega Saturn":
            case "Sega Dreamcast":
            case "Sega Mega CD":
            case "3DO":
            case "Neo Geo CD":
            case "PC Engine / TurboGrafx-CD":
            case "Atari Jaguar CD":
            case "CD-i (Philips)":
            case "Amiga CD32":
            case "Amiga CDTV":
                discType = "CD";
                fileSystem = "ISO 9660";
                break;

            case "PlayStation 2":
            case "Xbox":
            case "GameCube":
            case "Wii":
                discType = "DVD-5";
                fileSystem = "ISO 9660 + UDF";
                break;

            case "Xbox 360":
                discType = "DVD-9";
                fileSystem = "ISO 9660 + UDF";
                break;

            case "PSP (UMD)":
                discType = "DVD-5";
                fileSystem = "ISO 9660";
                break;

            case "PlayStation 3":
            case "Wii U":
                discType = "BD-25";
                fileSystem = "UDF 2.50";
                break;

            case "PlayStation 4/5":
            case "Xbox One/Series":
                discType = "BD-50";
                fileSystem = "UDF 2.50";
                break;

            default:
                discType = "CD";
                fileSystem = "ISO 9660";
                break;
        }

        // Set disc type combo
        foreach (var obj in CmbGameFolderDiscType.Items)
        {
            if (obj is ComboBoxItem item && item.Content?.ToString() == discType)
            {
                CmbGameFolderDiscType.SelectedItem = item;
                break;
            }
        }

        // Set file system combo
        foreach (var obj in CmbGameFolderFileSystem.Items)
        {
            if (obj is ComboBoxItem item && item.Content?.ToString() == fileSystem)
            {
                CmbGameFolderFileSystem.SelectedItem = item;
                break;
            }
        }
    }

    // -----------------------------------------------------------------------
    // Browse & Drag-drop
    // -----------------------------------------------------------------------
    private async void OnBrowseGameImageClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Game Disc Image",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Disc Images") { Patterns = new[] { "*.iso", "*.bin", "*.chd", "*.nrg", "*.mdf", "*.mds", "*.cdi", "*.img", "*.cue", "*.ccd", "*.obse" } },
                new FilePickerFileType("All Files") { Patterns = new[] { "*.*" } }
            }
        });

        if (files.Count > 0)
        {
            TxtGameImagePath.Text = files[0].Path.LocalPath;
            UpdateGameImageInfo(files[0].Path.LocalPath);
        }
    }

    private async void OnBrowseGameFolderClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Game Folder",
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            TxtGameFolderPath.Text = folders[0].Path.LocalPath;
            UpdateGameFolderInfo(folders[0].Path.LocalPath);
        }
    }

    private async void OnBrowseGameFolderOutputClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Game Disc Image",
            SuggestedFileName = "GameDisc",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("ISO Image") { Patterns = new[] { "*.iso" } },
                new FilePickerFileType("All Files") { Patterns = new[] { "*.*" } }
            }
        });

        if (file != null)
            TxtGameFolderOutputPath.Text = file.Path.LocalPath;
    }

    private void OnGameImageDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.TryGetFiles() is { } files && files.Any()
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private void OnGameImageDrop(object? sender, DragEventArgs e)
    {
        var items = e.DataTransfer.TryGetFiles()?.ToList();
        if (items == null) return;

        foreach (var item in items)
        {
            var path = item.Path.LocalPath;
            if (File.Exists(path))
            {
                TxtGameImagePath.Text = path;
                UpdateGameImageInfo(path);
                break;
            }
        }
    }

    private void OnRefreshGameDrivesClick(object? sender, RoutedEventArgs e)
    {
        _ = LoadDrivesAsync();
    }

    // -----------------------------------------------------------------------
    // Info updaters
    // -----------------------------------------------------------------------
    private void UpdateGameImageInfo(string path)
    {
        if (!File.Exists(path))
        {
            TxtGameImageInfo.Text = string.Empty;
            TxtGameDetectedSystem.Text = string.Empty;
            return;
        }

        var fi = new FileInfo(path);
        var ext = Path.GetExtension(path).ToUpperInvariant();

        // For CUE files, show the total BIN data size rather than the tiny CUE text file size
        if (ext == ".CUE")
        {
            var binFiles = Native.Optical.BurnEngine.GetCueBinFiles(path);
            long totalBinSize = 0;
            int foundCount = 0;
            foreach (var binFile in binFiles)
            {
                if (File.Exists(binFile))
                {
                    totalBinSize += new FileInfo(binFile).Length;
                    foundCount++;
                }
            }

            if (foundCount > 0)
            {
                var sizeMb = totalBinSize / (1024.0 * 1024.0);
                var trackInfo = binFiles.Count > 1 ? $", {binFiles.Count} tracks" : "";
                TxtGameImageInfo.Text = $"Size: {sizeMb:F1} MB ({totalBinSize:N0} bytes{trackInfo}) | Format: BIN/CUE";
            }
            else
            {
                TxtGameImageInfo.Text = $"Size: ⚠ No BIN files found for CUE sheet | Format: .CUE";
            }
        }
        else
        {
            TxtGameImageInfo.Text = $"Size: {FormatHelper.FormatBytes(fi.Length)} | Format: {fi.Extension.ToUpperInvariant()}";
        }

        // Auto-detect gaming system
        var detected = GamingDiscService.DetectGamingSystem(path);
        if (detected != null)
        {
            TxtGameDetectedSystem.Text = $"🎮 Detected system: {detected}";

            // For PS3 games, check encryption status and show additional info
            if (detected.Contains("PlayStation 3", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var bdInfo = GamingDiscService.DetectBluRayContent(path);
                    if (bdInfo.ContentType == Models.BluRayContentType.Ps3Game)
                    {
                        var encStatus = bdInfo.IsEncrypted ? "🔒 Encrypted" : "🔓 Decrypted";
                        var titleInfo = !string.IsNullOrWhiteSpace(bdInfo.Title) ? $" — {bdInfo.Title}" : "";
                        var titleId = !string.IsNullOrWhiteSpace(bdInfo.TitleId) ? $" [{bdInfo.TitleId}]" : "";
                        TxtGameDetectedSystem.Text = $"🎮 Detected: PlayStation 3{titleInfo}{titleId} ({encStatus})";
                        if (bdInfo.IsEncrypted)
                        {
                            TxtGameDetectedSystem.Text += "\n⚠ PS3 ISO is encrypted — decryption is recommended before burning";
                        }
                    }
                }
                catch { /* PS3 content detection is best-effort */ }
            }

            // Normalize the detection result to a canonical preset name so
            // variant names like "Sega Dreamcast (CDI)" map to "Sega Dreamcast".
            var presetName = GamingDiscService.NormalizeToPresetName(detected);

            // Auto-select the detected system in the combo box
            var idx = ((List<string>)CmbGameSystem.ItemsSource!).IndexOf(presetName);
            if (idx >= 0)
                CmbGameSystem.SelectedIndex = idx;
        }
        else
        {
            TxtGameDetectedSystem.Text = "⚠ Could not auto-detect gaming system — please select manually.";
        }
    }

    private void UpdateGameFolderInfo(string path)
    {
        if (!Directory.Exists(path))
        {
            TxtGameFolderInfo.Text = string.Empty;
            TxtGameFolderSize.Text = string.Empty;
            return;
        }

        long size = 0;
        int fileCount = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { size += new FileInfo(file).Length; fileCount++; } catch { }
            }
        }
        catch { }

        TxtGameFolderInfo.Text = $"{fileCount} files, {FormatHelper.FormatBytes(size)}";
        TxtGameFolderSize.Text = FormatHelper.FormatBytes(size);
    }

    // -----------------------------------------------------------------------
    // PS3 Decryption UI Handlers
    // -----------------------------------------------------------------------

    private void OnPs3KeySourceChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.IsChecked != true) return;

        bool isHexKey = ReferenceEquals(rb, RdoPs3HexKey);
        PnlPs3FileBrowse.IsVisible = !isHexKey;
        TxtPs3HexKey.IsVisible = isHexKey;
        TxtPs3KeyStatus.Text = string.Empty;
    }

    private async void OnBrowsePs3KeyFileClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        bool isDkey = RdoPs3DkeyFile.IsChecked == true;
        var filter = isDkey
            ? new FilePickerFileType("Disc Key Files") { Patterns = new[] { "*.dkey" } }
            : new FilePickerFileType("IRD Files") { Patterns = new[] { "*.ird" } };

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = isDkey ? "Select .dkey File" : "Select IRD File",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                filter,
                new FilePickerFileType("All Files") { Patterns = new[] { "*.*" } }
            }
        });

        if (files.Count > 0)
        {
            TxtPs3KeyFilePath.Text = files[0].Path.LocalPath;
            ValidatePs3Key();
        }
    }

    /// <summary>
    /// Validates the currently selected PS3 disc key source and updates the status text.
    /// </summary>
    private void ValidatePs3Key()
    {
        byte[]? key = GetPs3DiscKey();
        if (key != null && key.Length == 16)
        {
            TxtPs3KeyStatus.Text = "✅ Valid disc key loaded.";
            TxtPs3KeyStatus.Foreground = Avalonia.Media.Brush.Parse("#4CAF50");
        }
        else
        {
            TxtPs3KeyStatus.Text = "❌ Invalid or missing disc key.";
            TxtPs3KeyStatus.Foreground = Avalonia.Media.Brush.Parse("#FF6B6B");
        }
    }

    /// <summary>
    /// Retrieves the PS3 disc key from the currently selected source (IRD file, .dkey file, or hex input).
    /// </summary>
    private byte[]? GetPs3DiscKey()
    {
        if (RdoPs3Ird.IsChecked == true)
        {
            var path = TxtPs3KeyFilePath.Text?.Trim();
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                return Ps3DiscService.LoadDiscKeyFromIrd(path);
        }
        else if (RdoPs3DkeyFile.IsChecked == true)
        {
            var path = TxtPs3KeyFilePath.Text?.Trim();
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                return Ps3DiscService.LoadDiscKeyFromDkeyFile(path);
        }
        else if (RdoPs3HexKey.IsChecked == true)
        {
            return Ps3DiscService.LoadDiscKeyFromHex(TxtPs3HexKey.Text ?? string.Empty);
        }

        return null;
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
        TxtGameExecLog.Text = string.IsNullOrEmpty(TxtGameExecLog.Text)
            ? timestampedLine
            : TxtGameExecLog.Text + Environment.NewLine + timestampedLine;

        // Trim log to prevent unbounded memory growth (keep last ~20 KB)
        var text = TxtGameExecLog.Text;
        if (text != null && text.Length > 30_000)
        {
            var trimIdx = text.IndexOf('\n', text.Length - 20_000);
            if (trimIdx > 0)
                TxtGameExecLog.Text = "...\n" + text[(trimIdx + 1)..];
            else
                TxtGameExecLog.Text = "...\n" + text[(text.Length - 20_000)..];
        }

        TxtGameExecLog.CaretIndex = TxtGameExecLog.Text?.Length ?? 0;
    }
}
