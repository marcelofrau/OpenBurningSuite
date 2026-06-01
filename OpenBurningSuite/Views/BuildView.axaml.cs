// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
using OpenBurningSuite.Native.Audio;
using OpenBurningSuite.Services;

namespace OpenBurningSuite.Views;

public partial class BuildView : UserControl
{
    private readonly BuildService _buildService;
    private CancellationTokenSource? _cts;
    private readonly ObservableCollection<AudioTrack> _audioTracks = new();

    private static readonly Dictionary<string, string> GamingDescriptions = new()
    {
        ["PlayStation 1"]             = "PS1: ISO 9660 disc with SYSTEM.CNF at root, PSX.EXE or custom executable. Sector size 2352.",
        ["PlayStation 2"]             = "PS2: UDF 1.02 + ISO 9660 bridge. SYSTEM.CNF at root pointing to SLUS_XXXXX.XX.",
        ["PlayStation 3"]             = "PS3: Blu-ray (BD-25/BD-50). Encrypted with AACS+BD+. Standard tools cannot decrypt.",
        ["PlayStation 4/5"]           = "PS4/5: Blu-ray encrypted discs. For preservation reference only.",
        ["PSP (UMD)"]                 = "PSP: Universal Media Disc (UMD). ISO 9660, up to 1.8 GB dual-layer. Dump via PSP homebrew.",
        ["Sega Mega CD"]              = "Mega CD: Mixed-mode audio+data. Data track first, then audio tracks. Requires Sega header.",
        ["Sega Saturn"]               = "Saturn: Mode 1 data first track, then audio. IP.BIN boot header required. Raw 2352-byte sectors.",
        ["Sega Dreamcast"]            = "Dreamcast: GD-ROM (~1.2 GB usable). Boot sector at track 03, 1ST_READ.BIN at root.",
        ["PC Engine / TurboGrafx-CD"] = "PC Engine: Mixed-mode. Data track followed by audio tracks. Boot IPL sector required.",
        ["3DO"]                       = "3DO: Standard ISO 9660 with custom volume label and directory structure.",
        ["Neo Geo CD"]                = "Neo Geo CD: ISO 9660 with SPR/Z80/FIX files in root.",
        ["Xbox"]                      = "Xbox: DVD-5/DVD-9 with security sector (SS) region. Requires compatible drive.",
        ["Xbox 360"]                  = "Xbox 360: Dual-layer DVD with security sectors. Requires Kreon-compatible drive.",
        ["Xbox One/Series"]           = "Xbox One/Series: Blu-ray encrypted discs (50 GB). For preservation reference only.",
        ["GameCube"]                  = "GameCube: Nintendo miniDVD (1.46 GB). Requires compatible drive or Wii with GC mode.",
        ["Wii"]                       = "Wii: Wii optical disc. Use Wii with Homebrew or compatible drive.",
        ["Wii U"]                     = "Wii U: Proprietary 25 GB optical disc. Requires compatible drive for raw dumping.",
        ["Atari Jaguar CD"]           = "Jaguar CD: Mixed-mode CD. Boot track is audio.",
        ["Amiga CD32"]                = "Amiga CD32: Mixed-mode CD with AMIGABOOT boot block. ISO 9660 data track.",
        ["Amiga CDTV"]                = "Amiga CDTV: Mixed-mode CD with CDTV-compatible boot block. ISO 9660 data track.",
        ["CD-i (Philips)"]            = "CD-i: Philips CD-Interactive disc (Green Book). ISO 9660 with CD-i application directory.",
        ["VCD"]                       = "VCD: Video CD per IEC 62107 (White Book). MPEG-1 video at 352×240/288, 1150 kbps. CD-ROM XA Mode 2 sectors.",
        ["SVCD"]                      = "SVCD: Super Video CD per IEC 62107. MPEG-2 video at 480×480/576, up to 2600 kbps. CD-ROM XA Mode 2 sectors.",
    };

    public BuildView()
    {
        InitializeComponent();
        _buildService = new BuildService();

        PopulateComboBoxes();
        AudioTrackList.ItemsSource = _audioTracks;

        ChkBootable.PropertyChanged += (_, e) =>
        {
            if (e.Property.Name == nameof(CheckBox.IsChecked))
            {
                bool enabled = ChkBootable.IsChecked == true;
                TxtBootImage.IsEnabled = enabled;
                BtnBrowseBoot.IsEnabled = enabled;
                CmbBootEmulation.IsEnabled = enabled;
                CmbBootPlatform.IsEnabled = enabled;
                TxtBootLoadSegment.IsEnabled = enabled;
                TxtBootSectorCount.IsEnabled = enabled;
                ChkBootInfoTable.IsEnabled = enabled;
                TxtEfiBootImage.IsEnabled = enabled;
                BtnBrowseEfiBoot.IsEnabled = enabled;
            }
        };

        CmbDiscType.SelectionChanged += OnDiscTypeChanged;
        CmbFileSystem.SelectionChanged += OnFileSystemChanged;
    }

    // -----------------------------------------------------------------------
    // Initialisation
    // -----------------------------------------------------------------------
    private void PopulateComboBoxes()
    {
        foreach (var dt in FormatHelper.BuildDiscTypes)
            CmbDiscType.Items.Add(dt);
        CmbDiscType.SelectedIndex = 0;

        // Populate file system combo with options compatible with the default disc type (CD)
        var defaultDiscType = FormatHelper.BuildDiscTypes.Length > 0 ? FormatHelper.BuildDiscTypes[0] : "CD";
        var compatibleFs = FormatHelper.GetCompatibleFileSystems(defaultDiscType);
        foreach (var fs in compatibleFs)
            CmbFileSystem.Items.Add(fs);
        CmbFileSystem.SelectedIndex = 0;

        CmbCharset.SelectedIndex = 0;

        ApplySettingsDefaults();
    }

    /// <summary>Applies user's saved default settings to the build UI controls.</summary>
    private void ApplySettingsDefaults()
    {
        var s = SettingsService.Current;

        // File system
        for (int i = 0; i < CmbFileSystem.Items.Count; i++)
        {
            if (string.Equals(CmbFileSystem.Items[i]?.ToString(), s.DefaultFileSystem, StringComparison.OrdinalIgnoreCase))
            {
                CmbFileSystem.SelectedIndex = i;
                break;
            }
        }

        // Volume label
        if (!string.IsNullOrWhiteSpace(s.DefaultVolumeLabel))
            TxtVolumeLabel.Text = s.DefaultVolumeLabel;
    }

    // -----------------------------------------------------------------------
    // Event handlers
    // -----------------------------------------------------------------------
    private void OnDiscTypeChanged(object? sender, SelectionChangedEventArgs e)
    {
        var selected = CmbDiscType.SelectedItem?.ToString() ?? "CD";
        var cap = FormatHelper.GetCapacity(selected);
        TxtCapacity.Text = $"{FormatHelper.FormatBytes(cap)} ({selected})";

        // Show VCD/SVCD/gaming description if applicable, otherwise clear it
        if (GamingDescriptions.TryGetValue(selected, out var desc))
            TxtGamingDesc.Text = desc;
        else
            TxtGamingDesc.Text = string.Empty;

        // Update file system combo to show only compatible options
        UpdateFileSystemForDiscType(selected);
    }

    /// <summary>Repopulates the file system ComboBox with only the file systems compatible with the selected disc type.</summary>
    private void UpdateFileSystemForDiscType(string discType)
    {
        var currentFs = CmbFileSystem.SelectedItem?.ToString() ?? "";
        var compatibleFs = FormatHelper.GetCompatibleFileSystems(discType);

        CmbFileSystem.Items.Clear();
        foreach (var fs in compatibleFs)
            CmbFileSystem.Items.Add(fs);

        // Try to re-select the previously selected file system if still compatible
        bool reselected = false;
        for (int i = 0; i < CmbFileSystem.Items.Count; i++)
        {
            if (string.Equals(CmbFileSystem.Items[i]?.ToString(), currentFs, StringComparison.OrdinalIgnoreCase))
            {
                CmbFileSystem.SelectedIndex = i;
                reselected = true;
                break;
            }
        }

        if (!reselected)
        {
            var defaultFs = FormatHelper.GetDefaultFileSystem(discType);
            for (int i = 0; i < CmbFileSystem.Items.Count; i++)
            {
                if (string.Equals(CmbFileSystem.Items[i]?.ToString(), defaultFs, StringComparison.OrdinalIgnoreCase))
                {
                    CmbFileSystem.SelectedIndex = i;
                    reselected = true;
                    break;
                }
            }
            if (!reselected && CmbFileSystem.Items.Count > 0)
                CmbFileSystem.SelectedIndex = 0;
        }
    }

    /// <summary>Enables/disables ISO extension checkboxes based on the selected file system.</summary>
    private void OnFileSystemChanged(object? sender, SelectionChangedEventArgs e)
    {
        var fs = CmbFileSystem.SelectedItem?.ToString() ?? "ISO 9660 + Joliet";

        // Joliet long filenames only applies when Joliet is in use
        bool hasJoliet = fs.Contains("Joliet", StringComparison.OrdinalIgnoreCase);
        ChkJolietLong.IsEnabled = hasJoliet;
        if (!hasJoliet) ChkJolietLong.IsChecked = false;

        // Rock Ridge only applies to ISO 9660-based file systems (not pure UDF)
        bool isIsoBase = fs.Contains("ISO", StringComparison.OrdinalIgnoreCase) || fs == "Rock Ridge";
        ChkRockRidge.IsEnabled = isIsoBase;
        if (!isIsoBase) ChkRockRidge.IsChecked = false;

        // Deep directory, lowercase, special chars are ISO 9660 options
        ChkDeepDir.IsEnabled = isIsoBase;
        ChkLowercase.IsEnabled = isIsoBase;
        ChkSpecialChars.IsEnabled = isIsoBase;

        // CD-XA mode is only for CD disc types
        var discType = CmbDiscType.SelectedItem?.ToString() ?? "CD";
        ChkCdXaMode.IsEnabled = FormatHelper.IsCd(discType);
        if (!FormatHelper.IsCd(discType)) ChkCdXaMode.IsChecked = false;
    }

    private async void OnBrowseSourceClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = "Select Source Folder", AllowMultiple = false });
        if (folders.Count > 0)
            TxtSourceFolder.Text = folders[0].Path.LocalPath;
    }

    private async void OnBrowseOutputClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Image As",
            DefaultExtension = "iso",
            SuggestedFileName = "disc_image.iso"
        });
        if (file != null) TxtOutputPath.Text = file.Path.LocalPath;
    }

    private async void OnBrowseBootClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Boot Image",
            AllowMultiple = false
        });
        if (files.Count > 0) TxtBootImage.Text = files[0].Path.LocalPath;
    }

    private async void OnBrowseEfiBootClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select EFI Boot Image",
            AllowMultiple = false
        });
        if (files.Count > 0) TxtEfiBootImage.Text = files[0].Path.LocalPath;
    }

    private async void OnAddTrackClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add Audio Track(s)",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Audio Files")
                {
                    Patterns = new[] { "*.wav", "*.mp3", "*.wma", "*.flac", "*.ogg", "*.aiff", "*.aif", "*.aac", "*.m4a", "*.ape", "*.opus" }
                }
            }
        });

        foreach (var f in files)
        {
            var track = new AudioTrack
            {
                FilePath = f.Path.LocalPath,
                Title = System.IO.Path.GetFileNameWithoutExtension(f.Name)
            };

            // Try to read audio duration using AudioReaderFactory (supports WAV, MP3, WMA, AIFF)
            try
            {
                using var reader = AudioReaderFactory.CreateReader(f.Path.LocalPath);
                track.Duration = reader.TotalTime;
            }
            catch
            {
                // Duration will remain TimeSpan.Zero if reading fails
            }

            _audioTracks.Add(track);
        }
        UpdateTotalTime();
    }

    private void OnRemoveTrackClick(object? sender, RoutedEventArgs e)
    {
        if (AudioTrackList.SelectedIndex >= 0 &&
            AudioTrackList.SelectedIndex < _audioTracks.Count)
        {
            _audioTracks.RemoveAt(AudioTrackList.SelectedIndex);
            UpdateTotalTime();
        }
    }

    private void OnGamingSystemChanged(object? sender, SelectionChangedEventArgs e)
    {
        var item = (CmbGamingSystem.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? string.Empty;
        TxtGamingDesc.Text = GamingDescriptions.TryGetValue(item, out var desc)
            ? desc
            : "No gaming preset selected.";
    }

    private async void OnBuildClick(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TxtOutputPath.Text))
        {
            Log("❌ Specify output image path."); return;
        }

        if (_audioTracks.Count == 0 && string.IsNullOrWhiteSpace(TxtSourceFolder.Text))
        {
            Log("❌ Specify source folder or add audio tracks."); return;
        }

        if (_audioTracks.Count == 0 && !System.IO.Directory.Exists(TxtSourceFolder.Text))
        {
            Log($"❌ Source folder not found: {TxtSourceFolder.Text}"); return;
        }

        // Validate all audio track files exist
        if (_audioTracks.Count > 0)
        {
            foreach (var track in _audioTracks)
            {
                if (!System.IO.File.Exists(track.FilePath))
                {
                    Log($"❌ Audio track file not found: {track.FilePath}"); return;
                }
            }
        }

        // Validate output directory exists
        var outputDir = System.IO.Path.GetDirectoryName(TxtOutputPath.Text);
        if (!string.IsNullOrWhiteSpace(outputDir) && !System.IO.Directory.Exists(outputDir))
        {
            Log($"❌ Output directory does not exist: {outputDir}"); return;
        }

        var job = BuildJobFromUI();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        SetRunning(true);

        // Reset progress UI for a fresh operation
        BuildProgressBar.Value = 0;
        TxtBuildPct.Text = "0%";
        TxtBuildStatus.Text = "Starting...";
        TxtBuildETA.Text = string.Empty;

        Log($"▶ Building image: {job.OutputImagePath}");

        var progress = new Progress<BuildProgress>(p =>
        {
            BuildProgressBar.Value = p.PercentComplete;
            TxtBuildPct.Text = $"{p.PercentComplete}%";
            if (!string.IsNullOrWhiteSpace(p.StatusMessage))
                TxtBuildStatus.Text = p.StatusMessage;
            if (p.Remaining.TotalSeconds > 0)
                TxtBuildETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)} | ETA: {FormatHelper.FormatTimeSpan(p.Remaining)}";
            else if (p.Elapsed.TotalSeconds > 0)
                TxtBuildETA.Text = $"Elapsed: {FormatHelper.FormatTimeSpan(p.Elapsed)}";
            if (!string.IsNullOrWhiteSpace(p.LogLine))
                Log(p.LogLine);
        });

        try
        {
            await Task.Run(() => _buildService.BuildImageAsync(job, progress, _cts.Token));
            BuildProgressBar.Value = 100;
            TxtBuildPct.Text = "100%";
            TxtBuildStatus.Text = "Build complete.";
            Log($"✅ Image built: {job.OutputImagePath}");
        }
        catch (OperationCanceledException)
        {
            TxtBuildStatus.Text = "Cancelled.";
            Log("⏹ Build cancelled.");
        }
        catch (Exception ex)
        {
            TxtBuildStatus.Text = "Failed.";
            Log($"❌ Build failed: {ex.Message}");
        }
        finally
        {
            SetRunning(false);
        }
    }

    private void OnCancelBuildClick(object? sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        Log("Cancelling...");
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------
    private BuildJob BuildJobFromUI()
    {
        var gaming = (CmbGamingSystem.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? string.Empty;

        // Parse El Torito emulation mode from ComboBox selection
        var bootEmulationText = (CmbBootEmulation.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "No Emulation";
        string bootEmulation = bootEmulationText switch
        {
            "1.2 MB Floppy" => "Floppy1200",
            "1.44 MB Floppy" => "Floppy1440",
            "2.88 MB Floppy" => "Floppy2880",
            "Hard Disk" => "HardDisk",
            _ => "NoEmulation"
        };

        // Parse El Torito platform from ComboBox selection
        var bootPlatformText = (CmbBootPlatform.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "x86 (BIOS)";
        string bootPlatform = bootPlatformText switch
        {
            "PowerPC" => "PowerPC",
            "Mac" => "Mac",
            "EFI (UEFI)" => "EFI",
            _ => "x86"
        };

        // Parse load segment (hex)
        int bootLoadSegment = 0;
        var segText = TxtBootLoadSegment.Text?.Trim() ?? "0";
        if (segText.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            int.TryParse(segText[2..], System.Globalization.NumberStyles.HexNumber, null, out bootLoadSegment);
        else
            int.TryParse(segText, out bootLoadSegment);

        // Parse sector count
        int.TryParse(TxtBootSectorCount.Text?.Trim() ?? "0", out int bootSectorCount);

        return new BuildJob
        {
            DiscType        = CmbDiscType.SelectedItem?.ToString() ?? "CD",
            FileSystem      = CmbFileSystem.SelectedItem?.ToString() ?? "ISO 9660",
            SourceFolder    = TxtSourceFolder.Text ?? string.Empty,
            OutputImagePath = TxtOutputPath.Text ?? string.Empty,
            VolumeLabel     = TxtVolumeLabel.Text ?? string.Empty,
            Publisher       = TxtPublisher.Text ?? string.Empty,
            Preparer        = TxtPreparer.Text ?? string.Empty,
            Application     = TxtApplication.Text ?? string.Empty,
            Bootable        = ChkBootable.IsChecked == true,
            BootImagePath   = TxtBootImage.Text ?? string.Empty,
            BootEmulation   = bootEmulation,
            BootPlatform    = bootPlatform,
            BootLoadSegment = bootLoadSegment,
            BootSectorCount = bootSectorCount,
            BootInfoTable   = ChkBootInfoTable.IsChecked == true,
            EfiBootImagePath = TxtEfiBootImage.Text ?? string.Empty,
            CdXaMode        = ChkCdXaMode.IsChecked == true,
            IncludeCdiApplication = ChkIncludeCdiApp.IsChecked == true,
            JolietLongFilenames = ChkJolietLong.IsChecked == true,
            RockRidgeExtensions = ChkRockRidge.IsChecked == true,
            DeepDirectoryNesting = ChkDeepDir.IsChecked == true,
            AllowLowercase  = ChkLowercase.IsChecked == true,
            AllowSpecialChars = ChkSpecialChars.IsChecked == true,
            PadToCapacity   = ChkPad.IsChecked == true,
            SortFiles       = ChkSortFiles.IsChecked == true,
            InputCharset    = (CmbCharset.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "UTF-8",
            OutputCharset   = (CmbCharset.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "UTF-8",
            AudioTracks     = new List<AudioTrack>(_audioTracks),
            CdTextAlbum     = TxtCdTextAlbum.Text ?? string.Empty,
            CdTextArtist    = TxtCdTextArtist.Text ?? string.Empty,
            CdTextSongwriter = TxtCdTextSongwriter.Text ?? string.Empty,
            CdTextComposer  = TxtCdTextComposer.Text ?? string.Empty,
            CdTextArranger  = TxtCdTextArranger.Text ?? string.Empty,
            CdTextMessage   = TxtCdTextMessage.Text ?? string.Empty,
            CdTextGenre     = TxtCdTextGenre.Text ?? string.Empty,
            MediaCatalogNumber = TxtMediaCatalogNumber.Text ?? string.Empty,
            GamingSystem    = gaming == "(Standard — no gaming preset)" ? string.Empty : gaming,
        };
    }

    private void UpdateTotalTime()
    {
        var total = TimeSpan.Zero;
        foreach (var t in _audioTracks) total += t.Duration;
        TxtTotalTime.Text = $"Total time: {(int)total.TotalMinutes:D2}:{total.Seconds:D2}";
    }

    private void SetRunning(bool running)
    {
        BtnBuild.IsEnabled       = !running;
        BtnCancelBuild.IsEnabled = running;
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

    // -----------------------------------------------------------------------
    // Drag-and-drop handlers
    // -----------------------------------------------------------------------

    private void OnSourceDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = e.DataTransfer.TryGetFiles() is { } files && files.Any()
            ? DragDropEffects.Copy : DragDropEffects.None;

    private void OnSourceDrop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles()?.ToList();
        if (files?.Count > 0)
        {
            var path = files[0].Path.LocalPath;
            TxtSourceFolder.Text = System.IO.Directory.Exists(path) ? path : System.IO.Path.GetDirectoryName(path) ?? path;
        }
    }

    private void OnFilePathDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = e.DataTransfer.TryGetFiles() is { } files && files.Any()
            ? DragDropEffects.Copy : DragDropEffects.None;

    private void OnFilePathDrop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles()?.ToList();
        if (files?.Count > 0)
            TxtOutputPath.Text = files[0].Path.LocalPath;
    }

    private void OnBootImageDrop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles()?.ToList();
        if (files?.Count > 0)
            TxtBootImage.Text = files[0].Path.LocalPath;
    }

    private void OnEfiBootDrop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles()?.ToList();
        if (files?.Count > 0)
            TxtEfiBootImage.Text = files[0].Path.LocalPath;
    }

    private void OnAudioDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = e.DataTransfer.TryGetFiles() is { } files && files.Any()
            ? DragDropEffects.Copy : DragDropEffects.None;

    private void OnAudioDrop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles()?.ToList();
        if (files == null) return;
        foreach (var f in files)
        {
            var ext = System.IO.Path.GetExtension(f.Name)?.ToLowerInvariant();
            if (ext is ".wav" or ".mp3" or ".wma" or ".flac" or ".ogg" or ".aiff" or ".aif" or ".aac" or ".m4a" or ".ape" or ".opus")
            {
                var track = new AudioTrack
                {
                    FilePath = f.Path.LocalPath,
                    Title = System.IO.Path.GetFileNameWithoutExtension(f.Name)
                };
                try
                {
                    using var reader = AudioReaderFactory.CreateReader(f.Path.LocalPath);
                    track.Duration = reader.TotalTime;
                }
                catch { }
                _audioTracks.Add(track);
            }
        }
        UpdateTotalTime();
    }

    private void OnClearLogClick(object? sender, RoutedEventArgs e) =>
        TxtLog.Text = string.Empty;
}
