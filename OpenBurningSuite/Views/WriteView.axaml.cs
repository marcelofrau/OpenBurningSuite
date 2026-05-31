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
using OpenBurningSuite.Native.Optical;
using OpenBurningSuite.Services;

namespace OpenBurningSuite.Views;

public partial class WriteView : UserControl
{
    private readonly BurnService _burnService;
    private readonly DiscDiscoveryService _discovery;
    private CancellationTokenSource? _cts;
    private List<DiscDrive> _drives = new();
    /// <summary>Tracks the list of individual source files/folders for on-the-fly mode.</summary>
    private readonly List<string> _otfSourceFiles = new();

    public WriteView()
    {
        InitializeComponent();
        _burnService = new BurnService();
        _discovery = new DiscDiscoveryService();

        PopulateComboBoxes();
        ApplySettingsDefaults();
        CmbDrive.SelectionChanged += OnDriveSelectionChanged;
        LoadDrives();

        // Drag-and-drop events are wired up in AXAML via DragDrop.DragOver/DragDrop.Drop attributes

        ChkLibCrypt.PropertyChanged += (_, e) =>
        {
            if (e.Property.Name == nameof(CheckBox.IsChecked))
            {
                TxtSbiPath.IsEnabled  = ChkLibCrypt.IsChecked == true;
                BtnBrowseSbi.IsEnabled = ChkLibCrypt.IsChecked == true;
            }
        };

        // Wire overburn checkbox toggle in code-behind
        ChkOverburn.IsCheckedChanged += OnOverburnChanged;

        // Wire write mode change to update multi-session/close disc constraints
        CmbWriteMode.SelectionChanged += OnWriteModeChanged;
    }

    // -----------------------------------------------------------------------
    // Initialisation
    // -----------------------------------------------------------------------
    private void PopulateComboBoxes()
    {
        foreach (var s in FormatHelper.WriteSpeeds)
            CmbSpeed.Items.Add(s);
        CmbSpeed.SelectedIndex = 0;

        foreach (var m in FormatHelper.WriteModes)
            CmbWriteMode.Items.Add(m);
        CmbWriteMode.SelectedIndex = 0;

        CmbMultiSession.SelectedIndex = 0;
        CmbBdRMode.SelectedIndex = 0;

        // Populate on-the-fly filesystem options
        foreach (var fs in FormatHelper.OnTheFlyFileSystems)
            CmbOtfFileSystem.Items.Add(fs);
        CmbOtfFileSystem.SelectedIndex = 0; // First item in OnTheFlyFileSystems
    }

    /// <summary>Applies user's saved default settings to the burn UI controls.</summary>
    private void ApplySettingsDefaults()
    {
        var s = SettingsService.Current;

        // Write speed — find matching item
        SelectComboBoxItem(CmbSpeed, s.DefaultWriteSpeed);

        // Write mode
        SelectComboBoxItem(CmbWriteMode, s.DefaultWriteMode);

        // Multi-session — uses ComboBoxItem Content
        SelectComboBoxItemByContent(CmbMultiSession, s.DefaultMultiSessionMode);

        // Checkboxes
        ChkEject.IsChecked     = s.DefaultEjectAfterBurn;
        ChkVerify.IsChecked    = s.DefaultVerifyAfterBurn;
        ChkSimulate.IsChecked  = s.DefaultSimulateBurn;
        ChkBurnProof.IsChecked = s.DefaultBufferUnderrunProtection;
        ChkClose.IsChecked     = s.DefaultCloseDisc;
        ChkOverburn.IsChecked  = s.DefaultOverburn;

        // Copies
        NumCopies.Value = s.DefaultCopies;

        // On-the-fly defaults
        if (!string.IsNullOrWhiteSpace(s.DefaultVolumeLabel))
            TxtOtfVolumeLabel.Text = s.DefaultVolumeLabel;
        SelectComboBoxItem(CmbOtfFileSystem, s.DefaultFileSystem);
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

    /// <summary>Selects the combo box item whose Content matches the value (for ComboBoxItem items).</summary>
    private static void SelectComboBoxItemByContent(ComboBox cmb, string value)
    {
        for (int i = 0; i < cmb.Items.Count; i++)
        {
            if (cmb.Items[i] is ComboBoxItem item &&
                string.Equals(item.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                cmb.SelectedIndex = i;
                return;
            }
        }
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
    // Event handlers
    // -----------------------------------------------------------------------
    private void OnDriveSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var driveIdx = CmbDrive.SelectedIndex;
        if (driveIdx < 0 || driveIdx >= _drives.Count) return;

        var drive = _drives[driveIdx];
        var mediaType = drive.CurrentMedia?.MediaType ?? string.Empty;
        var speeds = FormatHelper.GetWriteSpeedsForMedia(mediaType);

        CmbSpeed.Items.Clear();
        foreach (var s in speeds)
            CmbSpeed.Items.Add(s);
        CmbSpeed.SelectedIndex = 0;

        // Update write options based on the selected drive's media type
        UpdateWriteOptionsForMedia(mediaType);
    }

    /// <summary>
    /// Updates multi-session and close/finalize option availability based on the
    /// selected write mode. SAO/DAO mode is inherently single-session and always
    /// finalizes the disc, so multi-session is disabled. RAW modes are also
    /// single-session. DVD/BD overwritable media (DVD+RW, BD-RE, DVD-RAM) don't
    /// support multi-session at all.
    /// </summary>
    private void OnWriteModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        var writeMode = CmbWriteMode.SelectedItem?.ToString() ?? string.Empty;
        UpdateMultiSessionConstraints(writeMode);
    }

    /// <summary>
    /// Updates multi-session and close disc controls based on write mode constraints.
    /// </summary>
    private void UpdateMultiSessionConstraints(string writeMode)
    {
        bool isDao = writeMode.Contains("DAO", StringComparison.OrdinalIgnoreCase)
                  && !writeMode.Contains("SAO", StringComparison.OrdinalIgnoreCase);
        bool isSao = writeMode.Contains("SAO", StringComparison.OrdinalIgnoreCase)
                  && !writeMode.Contains("DAO", StringComparison.OrdinalIgnoreCase);
        bool isRaw = writeMode.Contains("RAW", StringComparison.OrdinalIgnoreCase);

        if (isDao || isRaw)
        {
            // DAO and RAW modes are single-session only — disable multi-session
            // and force close/finalize to be checked
            CmbMultiSession.SelectedIndex = 0; // "Close (Single Session)"
            CmbMultiSession.IsEnabled = false;
            ChkClose.IsChecked = true;
            ChkClose.IsEnabled = false;

            ToolTip.SetTip(CmbMultiSession, isDao
                ? "Multi-session is not available in DAO (Disc At Once) mode — the entire disc is finalized"
                : "Multi-session is not available in RAW write mode");
            ToolTip.SetTip(ChkClose, isDao
                ? "DAO mode always finalizes the disc"
                : "RAW write mode always finalizes the disc");
        }
        else if (isSao)
        {
            // SAO (Session At Once) supports multi-session:
            // After writing one session, the disc can remain appendable.
            // Per MMC-5 §4.9.3, SAO writes one session at a time.
            CmbMultiSession.IsEnabled = true;
            ChkClose.IsEnabled = true;

            ToolTip.SetTip(CmbMultiSession, "SAO mode supports multi-session — choose 'Start' to leave disc appendable");
            ToolTip.SetTip(ChkClose, "Finalize the disc after this session (prevents adding more sessions)");
        }
        else
        {
            // TAO and Incremental modes support multi-session
            CmbMultiSession.IsEnabled = true;
            ChkClose.IsEnabled = true;

            ToolTip.SetTip(CmbMultiSession, "Single session closes the disc; multi-session allows adding more data later");
            ToolTip.SetTip(ChkClose, "Finalize the disc (required for playback in standalone players)");
        }
    }

    /// <summary>
    /// Updates write mode, multi-session, and close disc constraints based on the
    /// inserted media type. Overwritable media (DVD+RW, BD-RE, DVD-RAM, HD DVD-RW)
    /// don't support CLOSE TRACK/SESSION or multi-session.
    /// </summary>
    private void UpdateWriteOptionsForMedia(string mediaType)
    {
        var mt = mediaType.ToUpperInvariant();

        // Overwritable media types don't support multi-session or close/finalize
        bool isOverwritable = mt.Contains("DVD+RW") || mt.Contains("DVD-RAM") ||
                              mt.Contains("BD-RE") || mt.Contains("HD DVD-RW");

        if (isOverwritable)
        {
            CmbMultiSession.SelectedIndex = 0;
            CmbMultiSession.IsEnabled = false;
            ToolTip.SetTip(CmbMultiSession, $"Multi-session is not supported on {mediaType} — overwritable media uses direct overwrite");

            // Close/Finalize is not applicable to overwritable media
            ChkClose.IsChecked = false;
            ChkClose.IsEnabled = false;
            ToolTip.SetTip(ChkClose, $"Close/Finalize is not applicable to {mediaType} — overwritable media doesn't need finalization");
        }
        else
        {
            // Re-apply write mode constraints (may re-enable or keep disabled)
            var writeMode = CmbWriteMode.SelectedItem?.ToString() ?? string.Empty;
            UpdateMultiSessionConstraints(writeMode);
        }
    }

    private void OnSourceModeChanged(object? sender, RoutedEventArgs e)
    {
        // IsCheckedChanged fires for both check and uncheck. Only act when a
        // button is being checked to avoid processing the toggle twice.
        if (sender is RadioButton rb && rb.IsChecked == true)
        {
            bool isImageMode = ReferenceEquals(rb, RdoImageFile);
            PnlImageFile.IsVisible = isImageMode;
            PnlOnTheFly.IsVisible = !isImageMode;
        }
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
                    Patterns = new[] { "*.iso", "*.bin", "*.cue", "*.toc", "*.img", "*.nrg", "*.mdf", "*.mds", "*.cdi", "*.ccd", "*.obse", "*.chd" }
                },
                new FilePickerFileType("All Files") { Patterns = new[] { "*.*" } }
            }
        });
        if (files.Count > 0)
        {
            TxtImagePath.Text = files[0].Path.LocalPath;
            UpdateImageInfo();
        }
    }

    private async void OnBrowseSbiClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select SBI File",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("SBI Files") { Patterns = new[] { "*.sbi" } }
            }
        });
        if (files.Count > 0) TxtSbiPath.Text = files[0].Path.LocalPath;
    }

    private void OnOverburnChanged(object? sender, RoutedEventArgs e)
    {
        NumOverburnMB.IsEnabled = ChkOverburn.IsChecked == true;
    }

    // -----------------------------------------------------------------------
    // On-the-fly event handlers
    // -----------------------------------------------------------------------

    private async void OnOtfBrowseFolderClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Source Folder",
            AllowMultiple = false
        });
        if (folders.Count > 0)
        {
            TxtOtfSourceFolder.Text = folders[0].Path.LocalPath;
            UpdateOnTheFlyInfo();
        }
    }

    private async void OnOtfAddFilesClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add Files to Disc",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("All Files") { Patterns = new[] { "*.*" } }
            }
        });
        foreach (var f in files)
            AddOtfItem(f.Path.LocalPath);
    }

    private async void OnOtfAddFolderClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Add Folder to Disc",
            AllowMultiple = true
        });
        foreach (var f in folders)
            AddOtfItem(f.Path.LocalPath);
    }

    private void OnOtfRemoveClick(object? sender, RoutedEventArgs e)
    {
        var idx = LstOtfFiles.SelectedIndex;
        if (idx < 0 || idx >= _otfSourceFiles.Count) return;
        _otfSourceFiles.RemoveAt(idx);
        LstOtfFiles.Items.RemoveAt(idx);
        UpdateOnTheFlyInfo();
    }

    private void OnOtfClearAllClick(object? sender, RoutedEventArgs e)
    {
        _otfSourceFiles.Clear();
        LstOtfFiles.Items.Clear();
        UpdateOnTheFlyInfo();
    }

    private void OnFolderDragOver(object? sender, DragEventArgs e)
    {
        // Accept folders or files (for the source folder field, we'll take the first directory)
        e.DragEffects = e.DataTransfer.TryGetFiles() is { } files && files.Any()
            ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnFolderDrop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles()?.ToList();
        if (files?.Count > 0)
        {
            var path = files[0].Path.LocalPath;
            // Prefer directories, but accept file's parent directory
            if (Directory.Exists(path))
                TxtOtfSourceFolder.Text = path;
            else if (File.Exists(path))
                TxtOtfSourceFolder.Text = Path.GetDirectoryName(path) ?? path;
            UpdateOnTheFlyInfo();
        }
    }

    private void OnFilesDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.TryGetFiles() is { } files && files.Any()
            ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnFilesDrop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles()?.ToList();
        if (files == null) return;
        foreach (var f in files)
            AddOtfItem(f.Path.LocalPath);
    }

    /// <summary>Adds a file or folder to the on-the-fly source list.</summary>
    private void AddOtfItem(string path)
    {
        if (_otfSourceFiles.Contains(path)) return; // prevent duplicates

        _otfSourceFiles.Add(path);

        string displayName;
        if (Directory.Exists(path))
            displayName = $"📁 {Path.GetFileName(path)}/";
        else if (File.Exists(path))
        {
            var info = new FileInfo(path);
            var sizeMb = info.Length / (1024.0 * 1024.0);
            displayName = $"📄 {info.Name} ({sizeMb:F1} MB)";
        }
        else
            displayName = $"❓ {Path.GetFileName(path)}";

        LstOtfFiles.Items.Add(displayName);
        TxtOtfFilesHint.IsVisible = false;
        UpdateOnTheFlyInfo();
    }

    /// <summary>Updates the on-the-fly info text showing total size and item count.</summary>
    private void UpdateOnTheFlyInfo()
    {
        long totalSize = 0;
        int fileCount = 0;
        int folderCount = 0;

        // Count from source folder if specified
        var sourceFolder = TxtOtfSourceFolder.Text;
        if (!string.IsNullOrWhiteSpace(sourceFolder) && Directory.Exists(sourceFolder))
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(sourceFolder, "*", SearchOption.AllDirectories))
                {
                    try { totalSize += new FileInfo(f).Length; fileCount++; }
                    catch (UnauthorizedAccessException) { /* skip inaccessible files */ }
                    catch (IOException) { /* skip files with I/O issues */ }
                }
                folderCount = 1;
            }
            catch (UnauthorizedAccessException) { /* ignore access errors */ }
            catch (IOException) { /* ignore I/O errors */ }
        }

        // Count from individual files/folders
        foreach (var path in _otfSourceFiles)
        {
            if (Directory.Exists(path))
            {
                folderCount++;
                try
                {
                    foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    {
                        try { totalSize += new FileInfo(f).Length; fileCount++; }
                        catch (UnauthorizedAccessException) { /* skip */ }
                        catch (IOException) { /* skip */ }
                    }
                }
                catch (UnauthorizedAccessException) { /* ignore */ }
                catch (IOException) { /* ignore */ }
            }
            else if (File.Exists(path))
            {
                fileCount++;
                try { totalSize += new FileInfo(path).Length; }
                catch (UnauthorizedAccessException) { /* skip */ }
                catch (IOException) { /* skip */ }
            }
        }

        if (fileCount > 0 || folderCount > 0)
        {
            var parts = new List<string>();
            if (fileCount > 0) parts.Add($"{fileCount} file(s)");
            if (folderCount > 0) parts.Add($"{folderCount} folder(s)");
            TxtOtfInfo.Text = $"📊 {string.Join(", ", parts)} — Total: {FormatHelper.FormatBytes(totalSize)}";
        }
        else
        {
            TxtOtfInfo.Text = string.Empty;
        }
    }

    // -----------------------------------------------------------------------
    // Burn
    // -----------------------------------------------------------------------

    private async void OnBurnClick(object? sender, RoutedEventArgs e)
    {
        var isOnTheFly = RdoBuildOnTheFly.IsChecked == true;

        if (isOnTheFly)
        {
            // Validate on-the-fly source
            var hasFolder = !string.IsNullOrWhiteSpace(TxtOtfSourceFolder.Text) &&
                            Directory.Exists(TxtOtfSourceFolder.Text);
            var hasFiles = _otfSourceFiles.Count > 0;

            if (!hasFolder && !hasFiles)
            {
                Log("❌ Please select a source folder or add files/folders to burn."); return;
            }
        }
        else
        {
            // Validate image file source
            if (string.IsNullOrWhiteSpace(TxtImagePath.Text))
            {
                Log("❌ Please select a source image file."); return;
            }

            if (!File.Exists(TxtImagePath.Text))
            {
                Log($"❌ Image file not found: {TxtImagePath.Text}"); return;
            }
        }

        var driveIdx = CmbDrive.SelectedIndex;
        if (driveIdx < 0 || driveIdx >= _drives.Count)
        {
            Log("❌ Please select a drive."); return;
        }

        var drive = _drives[driveIdx];
        var job = BuildJobFromUI(drive.DevicePath);

        // Handle encrypted images: decrypt to a temp file before burning
        string? decryptedTempPath = null;
        if (!isOnTheFly && DiscEncryptionService.IsEncryptedImage(job.ImagePath))
        {
            var password = TxtDecryptPassword.Text;
            if (string.IsNullOrEmpty(password))
            {
                Log("❌ Encrypted image detected but no password provided."); return;
            }

            decryptedTempPath = Path.Combine(Path.GetTempPath(),
                "obs_decrypt_" + Guid.NewGuid().ToString("N")[..8] +
                Path.GetExtension(Path.GetFileNameWithoutExtension(job.ImagePath)));

            Log($"🔓 Decrypting image before burning...");
            SetRunning(true);
            _cts?.Dispose();
            _cts = new CancellationTokenSource();

            var decProgress = new Progress<DiscEncryptionService.EncryptionProgress>(ep =>
            {
                // Progress<T> already marshals to the UI thread
                BurnProgressBar.Value = ep.PercentComplete / 2; // 0-50% for decryption
                TxtBurnPct.Text = $"{ep.PercentComplete / 2}%";
                TxtBurnStatus.Text = ep.StatusMessage;
            });

            string? decResult;
            try
            {
                decResult = await DiscEncryptionService.DecryptAsync(
                    job.ImagePath, decryptedTempPath, password, decProgress, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                Log("⏹ Decryption cancelled.");
                try { if (File.Exists(decryptedTempPath)) File.Delete(decryptedTempPath); } catch { }
                SetRunning(false);
                return;
            }

            if (decResult != null)
            {
                Log($"❌ Decryption failed: {decResult}");
                try { if (File.Exists(decryptedTempPath)) File.Delete(decryptedTempPath); } catch { }
                SetRunning(false);
                return;
            }

            Log("✅ Decryption complete. Starting burn...");
            job = new BurnJob
            {
                ImagePath = decryptedTempPath,
                DevicePath = job.DevicePath,
                WriteMode = job.WriteMode,
                WriteSpeed = job.WriteSpeed,
                Copies = job.Copies,
                VerifyAfterBurn = job.VerifyAfterBurn,
                EjectAfterBurn = job.EjectAfterBurn,
                ShowDiscContentAfterBurn = job.ShowDiscContentAfterBurn,
                SimulateOnly = job.SimulateOnly,
                BufferUnderrunProtection = job.BufferUnderrunProtection,
                Overburn = job.Overburn,
                OverburnSizeMb = job.OverburnSizeMb,
                CloseDisc = job.CloseDisc,
                MultiSessionMode = job.MultiSessionMode,
                ForceMediaType = job.ForceMediaType,
                LayerBreakPosition = job.LayerBreakPosition,
                BdRMode = job.BdRMode,
                // Gaming patches
                LibCryptPatching = job.LibCryptPatching,
                SbiFilePath = job.SbiFilePath,
                DreamcastGdiPatching = job.DreamcastGdiPatching,
                RegionFreePatching = job.RegionFreePatching,
                BootSectorPatching = job.BootSectorPatching,
                EsrPatching = job.EsrPatching,
                MasterDiscPatching = job.MasterDiscPatching,
                MasterDiscRegion = job.MasterDiscRegion,
                Psx80MinutePatching = job.Psx80MinutePatching,
                PsxUndither = job.PsxUndither,
                // On-the-fly properties
                OnTheFly = job.OnTheFly,
                SourceFolder = job.SourceFolder,
                SourceFiles = job.SourceFiles,
                VolumeLabel = job.VolumeLabel,
                FileSystem = job.FileSystem
            };
        }

        // Handle PS3 encrypted ISO: decrypt with IRD/dkey/hex key before burning
        string? ps3DecryptedTempPath = null;
        if (!isOnTheFly && ChkWritePs3Decrypt.IsChecked == true &&
            PnlWritePs3Decrypt.IsVisible)
        {
            try
            {
                var bdInfo = Ps3DiscService.DetectContent(job.ImagePath);
                if (bdInfo.ContentType == BluRayContentType.Ps3Game && bdInfo.IsEncrypted)
                {
                    var discKey = GetWritePs3DiscKey();
                    if (discKey == null || discKey.Length != 16)
                    {
                        Log("❌ PS3 ISO is encrypted but no valid disc key was provided.");
                        Log("   Please provide an IRD file, .dkey file, or hex key.");
                        // Clean up OBS-decrypted temp file if it was created in a previous step
                        if (decryptedTempPath != null)
                        {
                            try { if (File.Exists(decryptedTempPath)) File.Delete(decryptedTempPath); } catch { }
                        }
                        SetRunning(false);
                        return;
                    }

                    Log("🔓 Decrypting PS3 ISO before burning...");
                    SetRunning(true);
                    _cts?.Dispose();
                    _cts = new CancellationTokenSource();

                    ps3DecryptedTempPath = Path.Combine(Path.GetTempPath(),
                        "obs_ps3dec_" + Guid.NewGuid().ToString("N")[..8] + ".iso");

                    var ps3Progress = new Progress<Ps3DiscService.Ps3DecryptionProgress>(dp =>
                    {
                        // Progress<T> already marshals to UI thread
                        BurnProgressBar.Value = dp.PercentComplete;
                        TxtBurnPct.Text = $"{dp.PercentComplete}%";
                        TxtBurnStatus.Text = dp.StatusMessage;
                    });

                    var ps3Result = await Ps3DiscService.DecryptIsoAsync(
                        job.ImagePath, ps3DecryptedTempPath, discKey, ps3Progress, _cts.Token);

                    if (ps3Result != null)
                    {
                        Log($"❌ PS3 decryption failed: {ps3Result}");
                        try { if (File.Exists(ps3DecryptedTempPath)) File.Delete(ps3DecryptedTempPath); } catch { }
                        // Clean up OBS-decrypted temp file if it was created in a previous step
                        if (decryptedTempPath != null)
                        {
                            try { if (File.Exists(decryptedTempPath)) File.Delete(decryptedTempPath); } catch { }
                        }
                        SetRunning(false);
                        return;
                    }

                    Log("✅ PS3 decryption complete. Starting burn...");
                    job.ImagePath = ps3DecryptedTempPath;
                }
            }
            catch (OperationCanceledException)
            {
                // PS3 decryption was cancelled — clean up and exit without burning
                if (ps3DecryptedTempPath != null)
                {
                    try { if (File.Exists(ps3DecryptedTempPath)) File.Delete(ps3DecryptedTempPath); } catch { }
                }
                if (decryptedTempPath != null)
                {
                    try { if (File.Exists(decryptedTempPath)) File.Delete(decryptedTempPath); } catch { }
                }
                Log("⏹ PS3 decryption cancelled.");
                SetRunning(false);
                return;
            }
            catch (Exception ex)
            {
                Log($"[Warning] PS3 encryption check failed: {ex.Message}");
            }
        }

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        SetRunning(true);

        // Reset progress UI for a fresh operation
        BurnProgressBar.Value = 0;
        TxtBurnPct.Text = "0%";
        TxtBurnStatus.Text = "Starting...";
        TxtCurrentSpeed.Text = string.Empty;
        TxtETA.Text = string.Empty;
        DiscVizBorder.IsVisible = false;
        DiscViz.IsActive = false;
        DiscViz.IsCompleted = false;
        DiscViz.VerifyPassed = null;
        DiscViz.BadSectors = 0;
        DiscViz.PercentComplete = 0;
        DiscViz.CurrentLba = 0;
        DiscViz.TotalSectors = 0;

        // Configure layer visualization from the inserted media type
        var mediaType = drive.CurrentMedia?.MediaType ?? string.Empty;
        DiscViz.ConfigureForMedia(mediaType, job.LayerBreakPosition);

        if (isOnTheFly)
        {
            var sourceDesc = !string.IsNullOrWhiteSpace(job.SourceFolder)
                ? job.SourceFolder
                : $"{job.SourceFiles.Count} item(s)";
            Log($"▶ Starting build & burn: {sourceDesc} → {drive.DevicePath}");
        }
        else
        {
            Log($"▶ Starting burn: {job.ImagePath} → {drive.DevicePath}");
        }
        if (job.SimulateOnly) Log("⚠ Simulate mode — no actual data will be written.");

        var progress = new Progress<BurnProgress>(p =>
        {
            // Progress<T> already marshals callbacks to the UI thread via
            // the captured SynchronizationContext, so update controls directly
            // without an additional Dispatcher.UIThread.Post() call.  This avoids
            // double-dispatching which delays UI updates and can cause the
            // dispatcher queue to back up during high-frequency progress reports.
            BurnProgressBar.Value = p.PercentComplete;
            TxtBurnPct.Text = $"{p.PercentComplete}%";
            if (!string.IsNullOrWhiteSpace(p.StatusMessage))
                TxtBurnStatus.Text = p.StatusMessage;
            if (p.CurrentSpeedX > 0)
                TxtCurrentSpeed.Text = $"{p.CurrentSpeedX:F1}x";
            if (p.Remaining.TotalSeconds > 0)
                TxtETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)} | ETA: {FormatHelper.FormatTimeSpan(p.Remaining)}";
            else if (p.Elapsed.TotalSeconds > 0)
                TxtETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)}";
            if (!string.IsNullOrWhiteSpace(p.LogLine))
                Log(p.LogLine);

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

        try
        {
            await Task.Run(() => _burnService.BurnAsync(job, progress, _cts.Token));
            BurnProgressBar.Value = 100;
            TxtBurnPct.Text = "100%";
            TxtBurnStatus.Text = isOnTheFly ? "Build & burn complete! 🎉" : "Burn complete! 🎉";
            Log($"✅ {(isOnTheFly ? "Build & burn" : "Burn")} complete: {drive.DevicePath}");

            // Mark disc visualization as completed
            DiscViz.PercentComplete = 100;
            DiscViz.IsCompleted = true;
            DiscViz.IsActive = false;
            DiscViz.UpdateVisualization();

            // Note: Post-burn verification (when enabled via VerifyAfterBurn) is
            // performed inside BurnEngine.BurnImageAsync before the disc is ejected.
            // This ensures the drive is still available for sector-by-sector comparison.
            // Do NOT run a second verify here — it would be redundant and would fail
            // if the disc was already ejected by the burn engine.

            // Open file explorer to show disc content if requested
            if (job.ShowDiscContentAfterBurn && !job.EjectAfterBurn)
            {
                Log("📂 Opening file explorer to show disc content...");
                if (!Helpers.PlatformHelper.OpenFileExplorer(drive.DevicePath))
                    Log("⚠ Could not open file explorer — please browse to the disc manually.");
            }
        }
        catch (OperationCanceledException)
        {
            TxtBurnStatus.Text = "Cancelled.";
            Log("⏹ Burn cancelled.");
            DiscViz.IsActive = false;
            DiscViz.IsCompleted = false;
            DiscViz.UpdateVisualization();
        }
        catch (Exception ex)
        {
            TxtBurnStatus.Text = isOnTheFly ? "Build & burn failed." : "Burn failed.";
            Log($"❌ {(isOnTheFly ? "Build & burn" : "Burn")} failed: {ex.Message}");
            DiscViz.IsActive = false;
            DiscViz.IsCompleted = false;
            DiscViz.UpdateVisualization();
            // Show modal dialog with error details so the user understands what happened
            try
            {
                var dlgMsg = ex.ToString();
                _ = ShowModalMessageAsync(isOnTheFly ? "Build & Burn Failed" : "Burn Failed", dlgMsg);
            }
            catch { /* best effort */ }
        }
        finally
        {
            // Clean up decrypted temp file if used
            if (decryptedTempPath != null)
            {
                try { if (File.Exists(decryptedTempPath)) File.Delete(decryptedTempPath); } catch { }
            }
            // Clean up PS3 decrypted temp file if used
            if (ps3DecryptedTempPath != null)
            {
                try { if (File.Exists(ps3DecryptedTempPath)) File.Delete(ps3DecryptedTempPath); } catch { }
            }
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
    private BurnJob BuildJobFromUI(string devicePath)
    {
        var modeStr = CmbWriteMode.SelectedItem?.ToString() ?? "TAO (Track At Once)";
        var msStr   = (CmbMultiSession.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Close";
        var isOnTheFly = RdoBuildOnTheFly.IsChecked == true;

        var job = new BurnJob
        {
            DevicePath              = devicePath,
            WriteMode               = modeStr,
            WriteSpeed              = CmbSpeed.SelectedItem?.ToString() ?? "Auto",
            Copies                  = (int)(NumCopies.Value ?? 1),
            EjectAfterBurn          = ChkEject.IsChecked == true,
            ShowDiscContentAfterBurn = ChkShowDiscContent.IsChecked == true,
            VerifyAfterBurn         = ChkVerify.IsChecked == true,
            SimulateOnly            = ChkSimulate.IsChecked == true,
            BufferUnderrunProtection = ChkBurnProof.IsChecked == true,
            CloseDisc               = ChkClose.IsChecked == true,
            Overburn                = ChkOverburn.IsChecked == true,
            OverburnSizeMb          = (long)(NumOverburnMB.Value ?? 0),
            MultiSessionMode        = msStr.Contains("Start") ? "Start (Multi-session)"
                                    : msStr.Contains("Continue") ? "Continue (Append)"
                                    : "Close (Single Session)",
            ForceMediaType          = TxtForceMediaType.Text ?? string.Empty,
            LayerBreakPosition      = (int)(NumLayerBreak.Value ?? 0),
            BdRMode                 = (CmbBdRMode.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "SRM",
            // Gaming patches
            LibCryptPatching        = ChkLibCrypt.IsChecked == true,
            SbiFilePath             = TxtSbiPath.Text ?? string.Empty,
            DreamcastGdiPatching    = ChkDcGdi.IsChecked == true,
            RegionFreePatching      = ChkRegionFree.IsChecked == true,
            BootSectorPatching      = ChkBootPatch.IsChecked == true,
            EsrPatching             = ChkEsrPatch.IsChecked == true,
            MasterDiscPatching      = ChkMasterDiscPatch.IsChecked == true,
            MasterDiscRegion        = (CmbMasterDiscRegion.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "NTSC-U",
            Psx80MinutePatching     = ChkPsx80Min.IsChecked == true,
            PsxUndither             = ChkPsxUndither.IsChecked == true,
        };

        if (isOnTheFly)
        {
            job.OnTheFly = true;
            job.SourceFolder = TxtOtfSourceFolder.Text ?? string.Empty;
            job.SourceFiles = new List<string>(_otfSourceFiles);
            job.VolumeLabel = TxtOtfVolumeLabel.Text ?? string.Empty;
            job.FileSystem = CmbOtfFileSystem.SelectedItem?.ToString() ?? "ISO 9660 + Joliet";
        }
        else
        {
            var imagePath = TxtImagePath.Text ?? string.Empty;
            job.ImagePath = imagePath;

            // If the user selected a .cue file, set CuePath explicitly so the
            // burn engine can use the CUE sheet for multi-track SAO/DAO writing.
            if (imagePath.EndsWith(".cue", StringComparison.OrdinalIgnoreCase))
                job.CuePath = imagePath;
        }

        return job;
    }

    private void SetRunning(bool running)
    {
        BtnBurn.IsEnabled   = !running;
        BtnCancel.IsEnabled = running;

        // Disable source controls while burning
        RdoImageFile.IsEnabled = !running;
        RdoBuildOnTheFly.IsEnabled = !running;
    }

    private async System.Threading.Tasks.Task ShowModalMessageAsync(string title, string message)
    {
        var topLevel = TopLevel.GetTopLevel(this) as Window;
        var dialog = new Window
        {
            Title = title,
            Width = 700,
            Height = 420,
            CanResize = true,
            Content = new ScrollViewer
            {
                Content = new TextBox
                {
                    Text = message,
                    AcceptsReturn = true,
                    IsReadOnly = true,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                }
            }
        };

        try
        {
            if (topLevel != null)
                await dialog.ShowDialog<object?>(topLevel);
        }
        catch { /* ignore dialog failures */ }
    }

    private void OnImageDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = e.DataTransfer.TryGetFiles() is { } files && files.Any()
            ? DragDropEffects.Copy : DragDropEffects.None;

    private void OnImageDrop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles()?.ToList();
        if (files?.Count > 0)
        {
            TxtImagePath.Text = files[0].Path.LocalPath;
            UpdateImageInfo();
        }
    }

    private void OnSbiDrop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles()?.ToList();
        if (files?.Count > 0)
            TxtSbiPath.Text = files[0].Path.LocalPath;
    }

    private void OnClearLogClick(object? sender, RoutedEventArgs e) =>
        TxtLog.Text = string.Empty;

    private void OnRefreshDrivesClick(object? sender, RoutedEventArgs e) => LoadDrives();

    private void UpdateImageInfo()
    {
        var path = TxtImagePath.Text;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            var info = new FileInfo(path);
            var ext = Path.GetExtension(path).ToUpperInvariant();

            // Check if the file is an OBS encrypted image
            bool isEncrypted = DiscEncryptionService.IsEncryptedImage(path);
            PnlDecryptImage.IsVisible = isEncrypted;

            if (isEncrypted)
            {
                long originalSize = DiscEncryptionService.GetOriginalSize(path);
                var origMb = originalSize > 0 ? originalSize / (1024.0 * 1024.0) : 0;
                TxtImageInfo.Text = $"🔒 {info.Name} — Encrypted ({info.Length / (1024.0 * 1024.0):F1} MB, original: {origMb:F1} MB)";
                PnlWritePs3Decrypt.IsVisible = false;
                return;
            }

            // Check if the file is an encrypted PS3 ISO
            try
            {
                BluRayContentInfo bdInfo;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    bdInfo = Ps3DiscService.DetectContentFromStream(fs);
                }
                if (bdInfo.ContentType == BluRayContentType.Ps3Game)
                {
                    PnlWritePs3Decrypt.IsVisible = true;
                    TxtWritePs3Info.Text = bdInfo.IsEncrypted
                        ? $"🔒 PS3 game ISO detected (encrypted). Title: {bdInfo.Title ?? "Unknown"}" +
                          (!string.IsNullOrEmpty(bdInfo.TitleId) ? $" [{bdInfo.TitleId}]" : "") +
                          "\nProvide an IRD file or disc key to decrypt before burning."
                        : $"🔓 PS3 game ISO detected (already decrypted). Title: {bdInfo.Title ?? "Unknown"}" +
                          (!string.IsNullOrEmpty(bdInfo.TitleId) ? $" [{bdInfo.TitleId}]" : "");
                }
                else
                {
                    PnlWritePs3Decrypt.IsVisible = false;
                }
            }
            catch
            {
                PnlWritePs3Decrypt.IsVisible = false;
            }

            // For CUE files, show the total BIN data size rather than the tiny CUE text file size
            if (ext == ".CUE")
            {
                var binFiles = BurnEngine.GetCueBinFiles(path);
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
                    TxtImageInfo.Text = $"📄 {info.Name} — {sizeMb:F1} MB ({totalBinSize:N0} bytes{trackInfo})";
                }
                else
                {
                    TxtImageInfo.Text = $"📄 {info.Name} — ⚠ No BIN files found for CUE sheet";
                }
            }
            else
            {
                var sizeMb = info.Length / (1024.0 * 1024.0);
                TxtImageInfo.Text = $"📄 {info.Name} — {sizeMb:F1} MB ({info.Length:N0} bytes)";
            }

            // Detect content type and apply write presets
            DetectAndApplyWritePresets(path);
        }
        else
        {
            TxtImageInfo.Text = string.Empty;
            PnlDecryptImage.IsVisible = false;
            PnlWritePs3Decrypt.IsVisible = false;
            HideWriteDetectedContent();
        }
    }

    /// <summary>
    /// Detects content from the selected image file and applies write presets.
    /// Runs detection asynchronously to avoid blocking the UI thread.
    /// </summary>
    private void DetectAndApplyWritePresets(string imagePath)
    {
        Task.Run(() =>
        {
            try
            {
                return DiscContentDetectionService.DetectFromImage(imagePath);
            }
            catch { return new DiscContentInfo(); }
        }).ContinueWith(t =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (t.IsCompletedSuccessfully && t.Result.ContentType != DiscContentType.Unknown)
                    ShowWriteDetectedContent(t.Result);
                else
                    HideWriteDetectedContent();
            });
        });
    }

    /// <summary>
    /// Shows detected content and applies write presets based on content type.
    /// Presets automatically configure write mode, finalization, and other settings
    /// so users don't have to manually select optimal options for each content type.
    /// </summary>
    private void ShowWriteDetectedContent(DiscContentInfo content)
    {
        PnlWriteDetectedContent.IsVisible = true;
        TxtWriteDetectedContent.Text = content.ContentLabel;

        // Apply write mode preset
        var recommendedMode = DiscContentDetectionService.GetRecommendedWriteMode(content.ContentType);
        if (recommendedMode != null)
            SelectComboBoxItem(CmbWriteMode, recommendedMode);

        // Apply finalization preset
        var shouldFinalize = DiscContentDetectionService.GetRecommendedFinalize(content.ContentType);
        ChkClose.IsChecked = shouldFinalize;

        // Apply LibCrypt preset for PlayStation 1
        if (content.ContentType == DiscContentType.PlayStation1Game)
        {
            ChkLibCrypt.IsChecked = true;
            // Try to auto-find SBI file next to the image
            var sbiPath = System.IO.Path.ChangeExtension(
                TxtImagePath.Text ?? string.Empty, ".sbi");
            if (System.IO.File.Exists(sbiPath))
                TxtSbiPath.Text = sbiPath;
        }

        // Set preset description
        var presetDesc = DiscContentDetectionService.GetWritePresetDescription(content.ContentType);
        TxtWriteDetectedPreset.Text = presetDesc ?? "";
    }

    /// <summary>Hides the write detected content panel.</summary>
    private void HideWriteDetectedContent()
    {
        PnlWriteDetectedContent.IsVisible = false;
        TxtWriteDetectedContent.Text = "—";
        TxtWriteDetectedPreset.Text = "";
    }

    private void Log(string msg)
    {
        // When called from a Progress<T> callback, we're already on the UI
        // thread. Avoid queuing another dispatcher callback, which adds
        // latency and contributes to dispatcher queue buildup.
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

    // -----------------------------------------------------------------------
    // PS3 Decryption UI Handlers (WriteView)
    // -----------------------------------------------------------------------

    private void OnWritePs3KeySourceChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.IsChecked != true) return;

        bool isHexKey = ReferenceEquals(rb, RdoWritePs3HexKey);
        PnlWritePs3FileBrowse.IsVisible = !isHexKey;
        TxtWritePs3HexKey.IsVisible = isHexKey;
        TxtWritePs3KeyStatus.Text = string.Empty;
    }

    private async void OnBrowseWritePs3KeyFileClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        bool isDkey = RdoWritePs3DkeyFile.IsChecked == true;
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
            TxtWritePs3KeyFilePath.Text = files[0].Path.LocalPath;
            ValidateWritePs3Key();
        }
    }

    private void ValidateWritePs3Key()
    {
        byte[]? key = GetWritePs3DiscKey();
        if (key != null && key.Length == 16)
        {
            TxtWritePs3KeyStatus.Text = "✅ Valid disc key loaded.";
            TxtWritePs3KeyStatus.Foreground = Avalonia.Media.Brush.Parse("#4CAF50");
        }
        else
        {
            TxtWritePs3KeyStatus.Text = "❌ Invalid or missing disc key.";
            TxtWritePs3KeyStatus.Foreground = Avalonia.Media.Brush.Parse("#FF6B6B");
        }
    }

    private byte[]? GetWritePs3DiscKey()
    {
        if (RdoWritePs3Ird.IsChecked == true)
        {
            var path = TxtWritePs3KeyFilePath.Text?.Trim();
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                return Ps3DiscService.LoadDiscKeyFromIrd(path);
        }
        else if (RdoWritePs3DkeyFile.IsChecked == true)
        {
            var path = TxtWritePs3KeyFilePath.Text?.Trim();
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                return Ps3DiscService.LoadDiscKeyFromDkeyFile(path);
        }
        else if (RdoWritePs3HexKey.IsChecked == true)
        {
            return Ps3DiscService.LoadDiscKeyFromHex(TxtWritePs3HexKey.Text ?? string.Empty);
        }

        return null;
    }
}