// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

namespace OpenBurningSuite.Models;

/// <summary>
/// Application-wide settings and preferences for Open Burning Suite.
/// Persisted as JSON in the user's application data directory.
/// </summary>
public class AppSettings
{
    // -----------------------------------------------------------------------
    // General
    // -----------------------------------------------------------------------

    /// <summary>Show a confirmation dialog before starting burn operations.</summary>
    public bool ConfirmBeforeBurn { get; set; } = true;

    /// <summary>Show a confirmation dialog before erasing/blanking a disc.</summary>
    public bool ConfirmBeforeErase { get; set; } = true;

    /// <summary>Minimize the application to the system tray instead of the taskbar.</summary>
    public bool MinimizeToSystemTray { get; set; }

    /// <summary>Prevent the system from going to sleep/standby during burn operations.</summary>
    public bool PreventSleepDuringBurn { get; set; } = true;

    /// <summary>Prevent the screensaver from activating during burn operations.</summary>
    public bool PreventScreensaverDuringBurn { get; set; } = true;

    /// <summary>Automatically check for application updates at startup.</summary>
    public bool CheckForUpdatesAtStartup { get; set; } = true;

    /// <summary>Remember and restore window size and position on startup.</summary>
    public bool RememberWindowPosition { get; set; } = true;

    /// <summary>Show tooltips on UI controls.</summary>
    public bool ShowTooltips { get; set; } = true;

    // -----------------------------------------------------------------------
    // Default Burn/Write Settings
    // -----------------------------------------------------------------------

    /// <summary>Default write speed (e.g. "Auto", "4x", "8x", "16x", "Max").</summary>
    public string DefaultWriteSpeed { get; set; } = "Auto";

    /// <summary>Default write mode (e.g. "TAO (Track At Once)", "SAO/DAO (Session/Disc At Once)").</summary>
    public string DefaultWriteMode { get; set; } = "TAO (Track At Once)";

    /// <summary>Enable buffer underrun protection by default.</summary>
    public bool DefaultBufferUnderrunProtection { get; set; } = true;

    /// <summary>Verify data after burning by default.</summary>
    public bool DefaultVerifyAfterBurn { get; set; }

    /// <summary>Eject disc after burn completes by default.</summary>
    public bool DefaultEjectAfterBurn { get; set; } = true;

    /// <summary>Close/finalize the disc after writing by default.</summary>
    public bool DefaultCloseDisc { get; set; } = true;

    /// <summary>Number of burn copies to make by default.</summary>
    public int DefaultCopies { get; set; } = 1;

    /// <summary>Enable overburning (writing beyond rated capacity) by default.</summary>
    public bool DefaultOverburn { get; set; }

    /// <summary>Simulate burn before actually writing by default.</summary>
    public bool DefaultSimulateBurn { get; set; }

    /// <summary>Default multi-session mode.</summary>
    public string DefaultMultiSessionMode { get; set; } = "Close (Single Session)";

    // -----------------------------------------------------------------------
    // Default Read/Copy Settings
    // -----------------------------------------------------------------------

    /// <summary>Default read speed (e.g. "Max", "4x", "8x", "16x").</summary>
    public string DefaultReadSpeed { get; set; } = "Max";

    /// <summary>Default output format for disc copies (e.g. "ISO", "BIN/CUE", "MDF/MDS").</summary>
    public string DefaultOutputFormat { get; set; } = "ISO";

    /// <summary>Default number of read retries on errors.</summary>
    public int DefaultReadRetryCount { get; set; } = 3;

    /// <summary>Default error recovery mode (e.g. "Yes", "No", "Abort").</summary>
    public string DefaultErrorRecovery { get; set; } = "Yes";

    /// <summary>Enable audio paranoia mode for audio CD extraction by default.</summary>
    public bool DefaultAudioParanoia { get; set; }

    /// <summary>Enable read jitter correction by default.</summary>
    public bool DefaultJitterCorrection { get; set; } = true;

    /// <summary>Default subchannel extraction mode.</summary>
    public string DefaultSubchannelMode { get; set; } = "None";

    /// <summary>Default sector size for reading (2048 or 2352).</summary>
    public int DefaultSectorSize { get; set; } = 2048;

    // -----------------------------------------------------------------------
    // Verification Settings
    // -----------------------------------------------------------------------

    /// <summary>Default checksum algorithm for verification (e.g. "SHA256", "SHA1", "MD5", "CRC32").</summary>
    public string DefaultChecksumAlgorithm { get; set; } = "SHA256";

    /// <summary>Automatically verify disc after read/copy operations.</summary>
    public bool AutoVerifyAfterRead { get; set; }

    // -----------------------------------------------------------------------
    // File System Defaults
    // -----------------------------------------------------------------------

    /// <summary>Default file system for data discs (e.g. "ISO 9660", "ISO 9660 + Joliet", "UDF", "ISO 9660 + UDF").</summary>
    public string DefaultFileSystem { get; set; } = "ISO 9660 + Joliet";

    /// <summary>Default UDF revision (e.g. "1.02", "1.50", "2.01", "2.50", "2.60").</summary>
    public string DefaultUdfRevision { get; set; } = "2.01";

    /// <summary>Allow long file names beyond ISO 9660 8.3 limit using Joliet extensions.</summary>
    public bool AllowLongFileNames { get; set; } = true;

    /// <summary>Allow deep directory nesting beyond 8 levels.</summary>
    public bool AllowDeepDirectories { get; set; } = true;

    /// <summary>Default volume label for new disc projects.</summary>
    public string DefaultVolumeLabel { get; set; } = string.Empty;

    // -----------------------------------------------------------------------
    // Audio CD Settings
    // -----------------------------------------------------------------------

    /// <summary>Default gap (in seconds) between audio CD tracks.</summary>
    public int DefaultAudioTrackGapSeconds { get; set; } = 2;

    /// <summary>Enable audio track normalization by default.</summary>
    public bool DefaultAudioNormalization { get; set; }

    /// <summary>Enable CD-TEXT by default for audio CDs.</summary>
    public bool DefaultCdText { get; set; }

    /// <summary>Apply gapless audio playback by default.</summary>
    public bool DefaultGaplessPlayback { get; set; }

    // -----------------------------------------------------------------------
    // I/O & Performance
    // -----------------------------------------------------------------------

    /// <summary>I/O buffer size in kilobytes for read/write operations.</summary>
    public int IoBufferSizeKB { get; set; } = 64;

    /// <summary>Number of SCSI write retries on transient errors.</summary>
    public int ScsiWriteRetries { get; set; } = 20;

    /// <summary>Number of SCSI read retries on transient errors.</summary>
    public int ScsiReadRetries { get; set; } = 20;

    /// <summary>I/O transfer length in kilobytes per SCSI command.</summary>
    public int TransferLengthKB { get; set; } = 64;

    /// <summary>Enable direct (unbuffered) I/O for better performance on some systems.</summary>
    public bool DirectIo { get; set; }

    /// <summary>Enable exclusive drive access to prevent other applications from interfering.</summary>
    public bool ExclusiveDriveAccess { get; set; } = true;

    // -----------------------------------------------------------------------
    // Post-Operation Actions
    // -----------------------------------------------------------------------

    /// <summary>Action after successful burn ("None", "Eject", "Shutdown", "Sleep", "Hibernate", "Close App").</summary>
    public string PostBurnAction { get; set; } = "Eject";

    /// <summary>Action after successful read/copy ("None", "Eject", "Shutdown", "Sleep", "Hibernate", "Close App").</summary>
    public string PostReadAction { get; set; } = "None";

    /// <summary>Action after successful erase/blank ("None", "Eject").</summary>
    public string PostEraseAction { get; set; } = "None";

    /// <summary>Play a notification sound when operations complete.</summary>
    public bool PlaySoundOnComplete { get; set; } = true;

    /// <summary>Play a notification sound on operation failure/error.</summary>
    public bool PlaySoundOnError { get; set; } = true;

    /// <summary>Show a desktop notification when operations complete.</summary>
    public bool ShowNotificationOnComplete { get; set; } = true;

    // -----------------------------------------------------------------------
    // Logging & Diagnostics
    // -----------------------------------------------------------------------

    /// <summary>Enable detailed operation logging to file.</summary>
    public bool EnableLogging { get; set; } = true;

    /// <summary>Logging level (e.g. "Normal", "Verbose", "Debug").</summary>
    public string LogLevel { get; set; } = "Normal";

    /// <summary>Maximum log file size in megabytes before rotation.</summary>
    public int MaxLogFileSizeMB { get; set; } = 10;

    /// <summary>Number of log files to keep.</summary>
    public int LogFileRetentionCount { get; set; } = 5;

    /// <summary>Include SCSI/MMC command details in the log.</summary>
    public bool LogScsiCommands { get; set; }

    // -----------------------------------------------------------------------
    // Paths & Locations
    // -----------------------------------------------------------------------

    /// <summary>Default directory for saving disc images.</summary>
    public string DefaultImageOutputDirectory { get; set; } = string.Empty;

    /// <summary>Temporary file directory for build operations.</summary>
    public string TempDirectory { get; set; } = string.Empty;

    /// <summary>Custom log file directory (empty = default app data).</summary>
    public string LogDirectory { get; set; } = string.Empty;

    /// <summary>Path to chdman executable (optional). If set, overrides automatic detection.</summary>
    public string ChdmanPath { get; set; } = string.Empty;

    // -----------------------------------------------------------------------
    // Advanced / Safety
    // -----------------------------------------------------------------------

    /// <summary>Allow burning to the same drive being used as the source (not recommended).</summary>
    public bool AllowSameSourceAndTarget { get; set; }

    /// <summary>Enable advanced/expert mode showing additional low-level options.</summary>
    public bool ExpertMode { get; set; }

    /// <summary>Automatically detect media type and suggest optimal write speed.</summary>
    public bool AutoDetectOptimalSpeed { get; set; } = true;

    /// <summary>Force the layer break position for dual-layer media (0 = auto).</summary>
    public int DefaultLayerBreakPosition { get; set; }

    /// <summary>Always perform a media compatibility check before burning.</summary>
    public bool MediaCompatibilityCheck { get; set; } = true;

    /// <summary>Enable automatic drive firmware capability detection.</summary>
    public bool AutoDetectDriveCapabilities { get; set; } = true;

    // -----------------------------------------------------------------------
    // Gaming Disc Defaults
    // -----------------------------------------------------------------------

    /// <summary>Automatically apply ESR patching for PS2 DVD images.</summary>
    public bool AutoEsrPatch { get; set; }

    /// <summary>Automatically apply region-free patching.</summary>
    public bool AutoRegionFreePatch { get; set; }

    /// <summary>Default gaming disc preset (e.g. "None", "PS1", "PS2", "Dreamcast", "Xbox").</summary>
    public string DefaultGamingPreset { get; set; } = "None";
}
