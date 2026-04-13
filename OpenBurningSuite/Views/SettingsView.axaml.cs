// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using OpenBurningSuite.Models;
using OpenBurningSuite.Services;

namespace OpenBurningSuite.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        PopulateComboBoxes();
        LoadSettingsIntoUi();
    }

    // -----------------------------------------------------------------------
    // Combo-box population
    // -----------------------------------------------------------------------

    private void PopulateComboBoxes()
    {
        // Write speed
        CmbDefaultWriteSpeed.ItemsSource = new[]
            { "Auto", "1x", "2x", "4x", "8x", "12x", "16x", "24x", "32x", "48x", "52x", "Max" };

        // Write mode
        CmbDefaultWriteMode.ItemsSource = new[]
            { "TAO (Track At Once)", "SAO (Session At Once)", "DAO (Disc At Once)" };

        // Multi-session
        CmbDefaultMultiSession.ItemsSource = new[]
            { "Close (Single Session)", "Start (Multi-session)", "Continue (Append)" };

        // Read speed
        CmbDefaultReadSpeed.ItemsSource = new[]
            { "Max", "1x", "2x", "4x", "8x", "12x", "16x", "24x", "32x", "48x", "52x" };

        // Output format
        CmbDefaultOutputFormat.ItemsSource = new[]
            { "ISO", "BIN/CUE", "BIN/CUE/SBI", "MDF/MDS", "NRG", "CDI", "CCD/IMG/SUB", "TOC/BIN", "IMG" };

        // Error recovery
        CmbDefaultErrorRecovery.ItemsSource = new[]
            { "Yes", "No", "Abort" };

        // Subchannel mode
        CmbDefaultSubchannel.ItemsSource = new[]
            { "None", "RW", "RW_RAW", "P-W" };

        // Sector size
        CmbDefaultSectorSize.ItemsSource = new[]
            { "2048", "2352" };

        // Checksum algorithm
        CmbDefaultChecksum.ItemsSource = new[]
            { "SHA256", "SHA1", "MD5", "SHA512", "CRC32" };

        // File system
        CmbDefaultFileSystem.ItemsSource = new[]
            { "ISO 9660", "ISO 9660 + Joliet", "ISO 9660 + UDF", "UDF 1.02", "UDF 1.5", "UDF 2.01", "UDF 2.50", "UDF 2.60", "Rock Ridge", "HFS+" };

        // UDF revision
        CmbDefaultUdfRevision.ItemsSource = new[]
            { "1.02", "1.50", "2.01", "2.50", "2.60" };

        // Post-burn action
        CmbPostBurnAction.ItemsSource = new[]
            { "None", "Eject", "Shutdown", "Sleep", "Hibernate", "Close App" };

        // Post-read action
        CmbPostReadAction.ItemsSource = new[]
            { "None", "Eject", "Shutdown", "Sleep", "Hibernate", "Close App" };

        // Post-erase action
        CmbPostEraseAction.ItemsSource = new[]
            { "None", "Eject" };

        // Log level
        CmbLogLevel.ItemsSource = new[]
            { "Normal", "Verbose", "Debug" };

        // Gaming preset
        CmbDefaultGamingPreset.ItemsSource = new[]
        {
            "None", "PS1", "PS2", "PS3", "PS4", "PS5",
            "Sega Saturn", "Sega Dreamcast", "Sega Mega CD",
            "Xbox", "Xbox 360",
            "GameCube", "Wii",
            "Neo Geo CD", "3DO", "PC Engine", "Atari Jaguar",
            "Amiga CD32", "Amiga CDTV"
        };
    }

    // -----------------------------------------------------------------------
    // Load / Save / Reset
    // -----------------------------------------------------------------------

    private void LoadSettingsIntoUi()
    {
        var s = SettingsService.Current;

        // General
        ChkConfirmBeforeBurn.IsChecked      = s.ConfirmBeforeBurn;
        ChkConfirmBeforeErase.IsChecked     = s.ConfirmBeforeErase;
        ChkMinimizeToTray.IsChecked         = s.MinimizeToSystemTray;
        ChkPreventSleep.IsChecked           = s.PreventSleepDuringBurn;
        ChkPreventScreensaver.IsChecked     = s.PreventScreensaverDuringBurn;
        ChkCheckForUpdates.IsChecked        = s.CheckForUpdatesAtStartup;
        ChkRememberWindowPos.IsChecked      = s.RememberWindowPosition;
        ChkShowTooltips.IsChecked           = s.ShowTooltips;

        // Burn defaults
        SetComboBoxValue(CmbDefaultWriteSpeed, s.DefaultWriteSpeed);
        SetComboBoxValue(CmbDefaultWriteMode, s.DefaultWriteMode);
        SetComboBoxValue(CmbDefaultMultiSession, s.DefaultMultiSessionMode);
        ChkDefaultBUP.IsChecked             = s.DefaultBufferUnderrunProtection;
        ChkDefaultVerifyAfterBurn.IsChecked = s.DefaultVerifyAfterBurn;
        ChkDefaultEjectAfterBurn.IsChecked  = s.DefaultEjectAfterBurn;
        ChkDefaultCloseDisc.IsChecked       = s.DefaultCloseDisc;
        ChkDefaultOverburn.IsChecked        = s.DefaultOverburn;
        ChkDefaultSimulate.IsChecked        = s.DefaultSimulateBurn;
        NudDefaultCopies.Value              = s.DefaultCopies;

        // Read defaults
        SetComboBoxValue(CmbDefaultReadSpeed, s.DefaultReadSpeed);
        // Migrate legacy output format names
        var outputFormat = s.DefaultOutputFormat;
        if (outputFormat == "CCD/IMG") outputFormat = "CCD/IMG/SUB";
        SetComboBoxValue(CmbDefaultOutputFormat, outputFormat);
        SetComboBoxValue(CmbDefaultErrorRecovery, s.DefaultErrorRecovery);
        SetComboBoxValue(CmbDefaultSubchannel, s.DefaultSubchannelMode);
        SetComboBoxValue(CmbDefaultSectorSize, s.DefaultSectorSize.ToString());
        NudDefaultReadRetries.Value         = s.DefaultReadRetryCount;
        ChkDefaultAudioParanoia.IsChecked   = s.DefaultAudioParanoia;
        ChkDefaultJitterCorrection.IsChecked = s.DefaultJitterCorrection;

        // Verification
        SetComboBoxValue(CmbDefaultChecksum, s.DefaultChecksumAlgorithm);
        ChkAutoVerifyAfterRead.IsChecked    = s.AutoVerifyAfterRead;

        // File system
        SetComboBoxValue(CmbDefaultFileSystem, s.DefaultFileSystem);
        SetComboBoxValue(CmbDefaultUdfRevision, s.DefaultUdfRevision);
        TxtDefaultVolumeLabel.Text          = s.DefaultVolumeLabel;
        ChkAllowLongFileNames.IsChecked     = s.AllowLongFileNames;
        ChkAllowDeepDirs.IsChecked          = s.AllowDeepDirectories;

        // Audio CD
        NudAudioTrackGap.Value              = s.DefaultAudioTrackGapSeconds;
        ChkAudioNormalization.IsChecked     = s.DefaultAudioNormalization;
        ChkCdText.IsChecked                 = s.DefaultCdText;
        ChkGaplessPlayback.IsChecked        = s.DefaultGaplessPlayback;

        // I/O & Performance
        NudIoBufferSize.Value               = s.IoBufferSizeKB;
        NudTransferLength.Value             = s.TransferLengthKB;
        NudScsiWriteRetries.Value           = s.ScsiWriteRetries;
        NudScsiReadRetries.Value            = s.ScsiReadRetries;
        ChkDirectIo.IsChecked               = s.DirectIo;
        ChkExclusiveDrive.IsChecked         = s.ExclusiveDriveAccess;

        // Post-operation actions
        SetComboBoxValue(CmbPostBurnAction, s.PostBurnAction);
        SetComboBoxValue(CmbPostReadAction, s.PostReadAction);
        SetComboBoxValue(CmbPostEraseAction, s.PostEraseAction);
        ChkSoundOnComplete.IsChecked        = s.PlaySoundOnComplete;
        ChkSoundOnError.IsChecked           = s.PlaySoundOnError;
        ChkNotificationOnComplete.IsChecked = s.ShowNotificationOnComplete;

        // Logging
        ChkEnableLogging.IsChecked          = s.EnableLogging;
        SetComboBoxValue(CmbLogLevel, s.LogLevel);
        NudMaxLogSize.Value                 = s.MaxLogFileSizeMB;
        NudLogRetention.Value               = s.LogFileRetentionCount;
        ChkLogScsiCommands.IsChecked        = s.LogScsiCommands;

        // Paths
        TxtDefaultImageDir.Text             = s.DefaultImageOutputDirectory;
        TxtTempDir.Text                     = s.TempDirectory;
        TxtLogDir.Text                      = s.LogDirectory;

        // Advanced
        ChkExpertMode.IsChecked             = s.ExpertMode;
        ChkAutoDetectSpeed.IsChecked        = s.AutoDetectOptimalSpeed;
        ChkMediaCompat.IsChecked            = s.MediaCompatibilityCheck;
        ChkAutoDetectCaps.IsChecked         = s.AutoDetectDriveCapabilities;
        ChkAllowSameSourceTarget.IsChecked  = s.AllowSameSourceAndTarget;
        NudLayerBreak.Value                 = s.DefaultLayerBreakPosition;

        // Gaming
        SetComboBoxValue(CmbDefaultGamingPreset, s.DefaultGamingPreset);
        ChkAutoEsrPatch.IsChecked           = s.AutoEsrPatch;
        ChkAutoRegionFree.IsChecked         = s.AutoRegionFreePatch;
    }

    private AppSettings CollectSettingsFromUi()
    {
        return new AppSettings
        {
            // General
            ConfirmBeforeBurn              = ChkConfirmBeforeBurn.IsChecked == true,
            ConfirmBeforeErase             = ChkConfirmBeforeErase.IsChecked == true,
            MinimizeToSystemTray           = ChkMinimizeToTray.IsChecked == true,
            PreventSleepDuringBurn         = ChkPreventSleep.IsChecked == true,
            PreventScreensaverDuringBurn   = ChkPreventScreensaver.IsChecked == true,
            CheckForUpdatesAtStartup       = ChkCheckForUpdates.IsChecked == true,
            RememberWindowPosition         = ChkRememberWindowPos.IsChecked == true,
            ShowTooltips                   = ChkShowTooltips.IsChecked == true,

            // Burn defaults
            DefaultWriteSpeed              = CmbDefaultWriteSpeed.SelectedItem?.ToString() ?? "Auto",
            DefaultWriteMode               = CmbDefaultWriteMode.SelectedItem?.ToString() ?? "TAO (Track At Once)",
            DefaultMultiSessionMode        = CmbDefaultMultiSession.SelectedItem?.ToString() ?? "Close (Single Session)",
            DefaultBufferUnderrunProtection = ChkDefaultBUP.IsChecked == true,
            DefaultVerifyAfterBurn         = ChkDefaultVerifyAfterBurn.IsChecked == true,
            DefaultEjectAfterBurn          = ChkDefaultEjectAfterBurn.IsChecked == true,
            DefaultCloseDisc               = ChkDefaultCloseDisc.IsChecked == true,
            DefaultOverburn                = ChkDefaultOverburn.IsChecked == true,
            DefaultSimulateBurn            = ChkDefaultSimulate.IsChecked == true,
            DefaultCopies                  = (int)(NudDefaultCopies.Value ?? 1),

            // Read defaults
            DefaultReadSpeed               = CmbDefaultReadSpeed.SelectedItem?.ToString() ?? "Max",
            DefaultOutputFormat            = CmbDefaultOutputFormat.SelectedItem?.ToString() ?? "ISO",
            DefaultErrorRecovery           = CmbDefaultErrorRecovery.SelectedItem?.ToString() ?? "Yes",
            DefaultSubchannelMode          = CmbDefaultSubchannel.SelectedItem?.ToString() ?? "None",
            DefaultSectorSize              = int.TryParse(CmbDefaultSectorSize.SelectedItem?.ToString(), out var ss) ? ss : 2048,
            DefaultReadRetryCount          = (int)(NudDefaultReadRetries.Value ?? 3),
            DefaultAudioParanoia           = ChkDefaultAudioParanoia.IsChecked == true,
            DefaultJitterCorrection        = ChkDefaultJitterCorrection.IsChecked == true,

            // Verification
            DefaultChecksumAlgorithm       = CmbDefaultChecksum.SelectedItem?.ToString() ?? "SHA256",
            AutoVerifyAfterRead            = ChkAutoVerifyAfterRead.IsChecked == true,

            // File system
            DefaultFileSystem              = CmbDefaultFileSystem.SelectedItem?.ToString() ?? "ISO 9660 + Joliet",
            DefaultUdfRevision             = CmbDefaultUdfRevision.SelectedItem?.ToString() ?? "2.01",
            DefaultVolumeLabel             = TxtDefaultVolumeLabel.Text ?? string.Empty,
            AllowLongFileNames             = ChkAllowLongFileNames.IsChecked == true,
            AllowDeepDirectories           = ChkAllowDeepDirs.IsChecked == true,

            // Audio CD
            DefaultAudioTrackGapSeconds    = (int)(NudAudioTrackGap.Value ?? 2),
            DefaultAudioNormalization      = ChkAudioNormalization.IsChecked == true,
            DefaultCdText                  = ChkCdText.IsChecked == true,
            DefaultGaplessPlayback         = ChkGaplessPlayback.IsChecked == true,

            // I/O & Performance
            IoBufferSizeKB                 = (int)(NudIoBufferSize.Value ?? 64),
            TransferLengthKB               = (int)(NudTransferLength.Value ?? 64),
            ScsiWriteRetries               = (int)(NudScsiWriteRetries.Value ?? 20),
            ScsiReadRetries                = (int)(NudScsiReadRetries.Value ?? 20),
            DirectIo                       = ChkDirectIo.IsChecked == true,
            ExclusiveDriveAccess           = ChkExclusiveDrive.IsChecked == true,

            // Post-operation actions
            PostBurnAction                 = CmbPostBurnAction.SelectedItem?.ToString() ?? "Eject",
            PostReadAction                 = CmbPostReadAction.SelectedItem?.ToString() ?? "None",
            PostEraseAction                = CmbPostEraseAction.SelectedItem?.ToString() ?? "None",
            PlaySoundOnComplete            = ChkSoundOnComplete.IsChecked == true,
            PlaySoundOnError               = ChkSoundOnError.IsChecked == true,
            ShowNotificationOnComplete     = ChkNotificationOnComplete.IsChecked == true,

            // Logging
            EnableLogging                  = ChkEnableLogging.IsChecked == true,
            LogLevel                       = CmbLogLevel.SelectedItem?.ToString() ?? "Normal",
            MaxLogFileSizeMB               = (int)(NudMaxLogSize.Value ?? 10),
            LogFileRetentionCount          = (int)(NudLogRetention.Value ?? 5),
            LogScsiCommands                = ChkLogScsiCommands.IsChecked == true,

            // Paths
            DefaultImageOutputDirectory    = TxtDefaultImageDir.Text ?? string.Empty,
            TempDirectory                  = TxtTempDir.Text ?? string.Empty,
            LogDirectory                   = TxtLogDir.Text ?? string.Empty,

            // Advanced
            ExpertMode                     = ChkExpertMode.IsChecked == true,
            AutoDetectOptimalSpeed         = ChkAutoDetectSpeed.IsChecked == true,
            MediaCompatibilityCheck        = ChkMediaCompat.IsChecked == true,
            AutoDetectDriveCapabilities    = ChkAutoDetectCaps.IsChecked == true,
            AllowSameSourceAndTarget       = ChkAllowSameSourceTarget.IsChecked == true,
            DefaultLayerBreakPosition      = (int)(NudLayerBreak.Value ?? 0),

            // Gaming
            DefaultGamingPreset            = CmbDefaultGamingPreset.SelectedItem?.ToString() ?? "None",
            AutoEsrPatch                   = ChkAutoEsrPatch.IsChecked == true,
            AutoRegionFreePatch            = ChkAutoRegionFree.IsChecked == true,
        };
    }

    // -----------------------------------------------------------------------
    // Event handlers
    // -----------------------------------------------------------------------

    private void OnCategoryClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string sectionName)
        {
            var target = this.FindControl<Border>(sectionName);
            target?.BringIntoView();
        }
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        var settings = CollectSettingsFromUi();
        if (SettingsService.Save(settings))
            TxtSettingsStatus.Text = "✅ Settings saved successfully.";
        else
            TxtSettingsStatus.Text = $"⚠ Failed to save settings: {SettingsService.LastError}";
    }

    private void OnResetClick(object? sender, RoutedEventArgs e)
    {
        SettingsService.ResetToDefaults();
        LoadSettingsIntoUi();
        TxtSettingsStatus.Text = "↩ Settings reset to defaults.";
    }

    private void OnOpenSettingsFileClick(object? sender, RoutedEventArgs e)
    {
        var path = SettingsService.GetSettingsFilePath();
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            TxtSettingsStatus.Text = $"⚠ Could not open: {path} — {ex.Message}";
        }
    }

    private async void OnBrowseImageDirClick(object? sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync("Select Default Image Output Directory");
        if (folder != null)
            TxtDefaultImageDir.Text = folder;
    }

    private async void OnBrowseTempDirClick(object? sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync("Select Temporary File Directory");
        if (folder != null)
            TxtTempDir.Text = folder;
    }

    private async void OnBrowseLogDirClick(object? sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync("Select Log File Directory");
        if (folder != null)
            TxtLogDir.Text = folder;
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private async System.Threading.Tasks.Task<string?> PickFolderAsync(string title)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return null;

        var result = await topLevel.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = title, AllowMultiple = false });

        if (result.Count > 0)
        {
            var path = result[0].TryGetLocalPath();
            return path;
        }

        return null;
    }

    private static void SetComboBoxValue(ComboBox comboBox, string value)
    {
        if (comboBox.ItemsSource is string[] items)
        {
            for (int i = 0; i < items.Length; i++)
            {
                if (string.Equals(items[i], value, StringComparison.OrdinalIgnoreCase))
                {
                    comboBox.SelectedIndex = i;
                    return;
                }
            }
        }

        // Fallback: select first item
        if (comboBox.ItemCount > 0)
            comboBox.SelectedIndex = 0;
    }
}
