// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;

namespace OpenBurningSuite.Models;

/// <summary>
/// Configuration for Blu-ray Disc (BDMV) authoring per Blu-ray Disc Association specification.
///
/// Blu-ray disc structure (BDMV directory):
/// - BDMV/index.bdmv       : Top-level index (titles, first playback)
/// - BDMV/MovieObject.bdmv : Navigation command programs
/// - BDMV/PLAYLIST/*.mpls  : Movie playlists (play order, chapters, stream selection)
/// - BDMV/CLIPINF/*.clpi   : Clip information (stream properties, entry points)
/// - BDMV/STREAM/*.m2ts    : MPEG-2 Transport Stream files (video/audio/subtitle data)
/// - BDMV/AUXDATA/          : Auxiliary data (sound effects, fonts)
/// - BDMV/META/             : Metadata (thumbnails, disc library)
/// - BDMV/BACKUP/           : Backup copies of index, MovieObject, playlists, clip info
/// - CERTIFICATE/           : AACS content certificate (optional)
///
/// BD-25: single layer, 25 GB capacity
/// BD-50: dual layer, 50 GB capacity
/// BD-100/128: BDXL triple/quad layer
///
/// Supported video codecs: MPEG-2, H.264/AVC, VC-1
/// Supported audio codecs: LPCM, Dolby Digital (AC-3), Dolby Digital Plus, Dolby TrueHD,
///                          DTS, DTS-HD Master Audio, DTS-HD High Resolution
/// Supported subtitle: PGS (Presentation Graphics Stream), Text Subtitle
/// </summary>
public class BluRayAuthoringJob
{
    /// <summary>Video standard: "1080p" (1920×1080 progressive), "1080i" (interlaced),
    /// "720p" (1280×720 progressive), "576p" (PAL), "576i", "480p" (NTSC), "480i".</summary>
    public string VideoFormat { get; set; } = "1080p";

    /// <summary>Frame rate: "23.976", "24", "25", "29.97", "50", "59.94".</summary>
    public string FrameRate { get; set; } = "23.976";

    /// <summary>
    /// Video codec: "H264" (AVC), "MPEG2", "VC1".
    /// H.264/AVC is most commonly used for Blu-ray.
    /// </summary>
    public string VideoCodec { get; set; } = "H264";

    /// <summary>
    /// Video encoding bitrate in kbps.
    /// Blu-ray max: 40,000 kbps (40 Mbps) for H.264.
    /// Recommended: 15000-35000 kbps for 1080p. 0 = auto.
    /// </summary>
    public int VideoBitrateKbps { get; set; }

    /// <summary>Playlists to create on the disc. At least one is required.</summary>
    public List<BluRayPlaylist> Playlists { get; set; } = new();

    /// <summary>Optional first-play playlist index (0-based). -1 = none.</summary>
    public int FirstPlayPlaylistIndex { get; set; }

    /// <summary>Optional top-level menu definition.</summary>
    public BluRayMenu? TopMenu { get; set; }

    /// <summary>
    /// Whether to transcode input video files to Blu-ray compliant format.
    /// When false, input must already be Blu-ray compliant elementary streams.
    /// </summary>
    public bool TranscodeVideo { get; set; }

    /// <summary>Volume label for the Blu-ray disc.</summary>
    public string VolumeLabel { get; set; } = "BLURAY_DISC";

    /// <summary>Disc organization (BD Organization ID, 32 bytes max).</summary>
    public string OrganizationId { get; set; } = string.Empty;

    /// <summary>Whether to create BACKUP directory with copies of essential files.</summary>
    public bool CreateBackup { get; set; } = true;

    // -----------------------------------------------------------------------
    // BDAV (Blu-ray Disc Audio/Visual recording format) properties
    // -----------------------------------------------------------------------

    /// <summary>
    /// When true, builds a BDAV (recording) disc structure instead of BDMV (movie).
    /// BDAV is used for Blu-ray recorders (BD-R/BD-RE) and uses a simplified
    /// directory layout under BDAV/ with real-time recording support.
    /// Per Blu-ray Disc Association specification Part 3-2 (Audio Visual Basic Format).
    /// </summary>
    public bool IsBdav { get; set; }

    /// <summary>
    /// BDAV recording timestamp (used in CLPI for recording date metadata).
    /// Defaults to current date/time if not set.
    /// </summary>
    public DateTime? RecordingTimestamp { get; set; }

    /// <summary>
    /// BDAV time zone offset in minutes from UTC (-720 to +840).
    /// Used in CLPI MakersPrivateData for recording location info.
    /// </summary>
    public int TimeZoneOffsetMinutes { get; set; }

    // -----------------------------------------------------------------------
    // 3D (Stereoscopic) Blu-ray properties — Profile 5.0
    // -----------------------------------------------------------------------

    /// <summary>
    /// Enables Blu-ray 3D (stereoscopic) disc authoring per Blu-ray 3D specification.
    /// When enabled, the builder creates:
    /// - SSIF (Stereoscopic Interleaved File) directory with interleaved base+dependent views
    /// - 3D-aware CLPI files with extent_start_point entries
    /// - 3D-aware MPLS files with STN_table_SS (stereoscopic stream table)
    /// - index.bdmv with initial_output_mode_preference set to 3D
    /// </summary>
    public bool Stereoscopic3D { get; set; }

    /// <summary>
    /// 3D content type: "FramePacked" (full resolution per eye, MVC),
    /// "SideBySide" (half horizontal resolution, frame-compatible),
    /// "TopBottom" (half vertical resolution, frame-compatible).
    /// Frame-packed MVC is the standard Blu-ray 3D format.
    /// </summary>
    public string Stereoscopic3DMode { get; set; } = "FramePacked";

    /// <summary>
    /// Path to the dependent-view (right eye) video file for 3D content.
    /// Only used when Stereoscopic3D is true and Stereoscopic3DMode is "FramePacked".
    /// For SideBySide/TopBottom, the 3D content is encoded within the base view file.
    /// </summary>
    public string DependentViewPath { get; set; } = string.Empty;

    /// <summary>
    /// 3D depth metadata: convergence distance in 1/10th pixel units.
    /// Positive values push content behind the screen, negative in front.
    /// 0 = at screen depth. Range: -2048 to 2047.
    /// </summary>
    public int Stereoscopic3DDepthOffset { get; set; }

    // -----------------------------------------------------------------------
    // Ultra HD Blu-ray (UHD BD) properties — 4K HDR authoring
    // -----------------------------------------------------------------------

    /// <summary>
    /// Enables Ultra HD Blu-ray authoring mode per BDA UHD specification.
    /// When enabled, the builder creates UHD-compliant BDMV structure with:
    /// - HEVC (H.265) Main10 Profile video (mandatory for UHD BD)
    /// - HDR metadata (HDR10 mandatory, Dolby Vision/HDR10+ optional)
    /// - BT.2020 wide color gamut
    /// - 10-bit color depth
    /// - UDF 2.50/2.60 filesystem
    /// UHD BD discs do not support 3D stereoscopic content.
    /// </summary>
    public bool UltraHd { get; set; }

    /// <summary>
    /// HDR (High Dynamic Range) mode for UHD Blu-ray.
    /// "HDR10" (mandatory, static metadata, SMPTE ST 2084 + ST 2086),
    /// "DolbyVision" (optional, dynamic metadata per scene),
    /// "HDR10Plus" (optional, dynamic metadata using SMPTE ST 2094-40),
    /// "HLG" (Hybrid Log-Gamma for broadcast compatibility).
    /// Default: "HDR10" as it is mandatory for all UHD BD players.
    /// </summary>
    public string HdrMode { get; set; } = "HDR10";

    /// <summary>
    /// Maximum Content Light Level (MaxCLL) in cd/m² (nits) for HDR10 metadata.
    /// Specifies the maximum luminance of the brightest pixel in the content.
    /// Range: 0 - 10,000 nits. 0 = unknown/not specified.
    /// Per SMPTE ST 2086 / CTA-861.3 specification.
    /// </summary>
    public int HdrMaxContentLightLevel { get; set; }

    /// <summary>
    /// Maximum Frame-Average Light Level (MaxFALL) in cd/m² (nits) for HDR10 metadata.
    /// Specifies the maximum average luminance across any single frame.
    /// Range: 0 - 10,000 nits. 0 = unknown/not specified.
    /// Per SMPTE ST 2086 / CTA-861.3 specification.
    /// </summary>
    public int HdrMaxFrameAverageLightLevel { get; set; }

    /// <summary>
    /// Display mastering luminance minimum (in 0.0001 cd/m² units) for HDR10 metadata.
    /// Example: 50 = 0.005 cd/m² (typical for mastering monitors).
    /// Per SMPTE ST 2086 specification.
    /// </summary>
    public int HdrMasteringDisplayMinLuminance { get; set; } = 50;

    /// <summary>
    /// Display mastering luminance maximum (in cd/m²) for HDR10 metadata.
    /// Example: 1000 = 1,000 cd/m² (typical consumer HDR target).
    /// Per SMPTE ST 2086 specification.
    /// </summary>
    public int HdrMasteringDisplayMaxLuminance { get; set; } = 1000;

    /// <summary>
    /// Display mastering color primaries as CIE 1931 chromaticity coordinates.
    /// Format: "R(x,y),G(x,y),B(x,y),WP(x,y)" where each value is 0.0-1.0.
    /// Default is DCI-P3 (common mastering gamut for UHD content):
    ///   R(0.680,0.320),G(0.265,0.690),B(0.150,0.060),WP(0.3127,0.3290)
    /// Per SMPTE ST 2086 specification.
    /// </summary>
    public string HdrMasteringDisplayPrimaries { get; set; } =
        "R(0.680,0.320),G(0.265,0.690),B(0.150,0.060),WP(0.3127,0.3290)";

    /// <summary>
    /// Color space for UHD Blu-ray content.
    /// "BT.2020" (Rec. 2020 wide color gamut — standard for UHD BD),
    /// "BT.709" (Rec. 709 — for SDR content on UHD BD).
    /// Default: "BT.2020" for full UHD experience.
    /// </summary>
    public string ColorSpace { get; set; } = "BT.2020";

    /// <summary>
    /// Transfer function (EOTF) for UHD Blu-ray.
    /// "PQ" (Perceptual Quantizer, SMPTE ST 2084 — standard for HDR10/DolbyVision),
    /// "HLG" (Hybrid Log-Gamma — ITU-R BT.2100 for broadcast HDR).
    /// Default: "PQ" for standard UHD BD HDR.
    /// </summary>
    public string TransferFunction { get; set; } = "PQ";

    /// <summary>
    /// UHD Blu-ray disc size target: "BD-66" (66 GB dual-layer) or "BD-100" (100 GB triple-layer).
    /// Determines maximum bitrate and mux rate limits.
    /// BD-66: up to ~108 Mbps max mux rate.
    /// BD-100: up to ~128 Mbps max mux rate.
    /// </summary>
    public string UhdDiscSize { get; set; } = "BD-66";

    // -----------------------------------------------------------------------
    // BD-J (Blu-ray Disc Java) properties — Interactive content
    // -----------------------------------------------------------------------

    /// <summary>
    /// Enables BD-J (Blu-ray Disc Java) interactive content per BD-J specification
    /// (Blu-ray Disc Association Part 3-2, Section 12). When enabled, the builder creates:
    /// - BDMV/BDJO/ directory containing BD-J Object descriptor files (.bdjo)
    /// - BDMV/JAR/ directory containing Java archive files (.jar) with BD-J Xlets
    /// - BD-J title entries in index.bdmv (title type 0x02 = BD-J)
    /// - MovieObject.bdmv with BD-J object references
    ///
    /// BD-J uses the GEM (Globally Executable MHP) platform based on Java ME
    /// (Personal Basis Profile + Java TV API). Xlets implement the
    /// javax.tv.xlet.Xlet interface (initXlet, startXlet, pauseXlet, destroyXlet).
    ///
    /// BD-J profiles:
    /// - Profile 1.0: Basic interactive features (menus, PiP)
    /// - Profile 1.1 (BonusView): Secondary video/audio, virtual package
    /// - Profile 2.0 (BD-Live): Network access, persistent storage
    /// - Profile 5.0 (3D): Stereoscopic content with BD-J interaction
    /// - Profile 6.0 (UHD): 4K HDR content with BD-J interaction
    /// </summary>
    public bool EnableBdJ { get; set; }

    /// <summary>
    /// BD-J application entries to include on the disc.
    /// Each entry defines a Java Xlet application and its BDJO descriptor.
    /// At least one application is required when <see cref="EnableBdJ"/> is true.
    /// </summary>
    public List<BdJApplication> BdJApplications { get; set; } = new();

    /// <summary>
    /// BD-J profile version: "1.0" (basic), "1.1" (BonusView), "2.0" (BD-Live),
    /// "5.0" (3D), "6.0" (UHD). Determines the minimum player profile requirement.
    /// Default: "1.1" (BonusView is the most common profile for BD-J content).
    /// </summary>
    public string BdJProfileVersion { get; set; } = "1.1";

    /// <summary>
    /// BD-J security mode: "Signed" (requires AACS content certificate) or "Unsigned"
    /// (for development/testing). Signed applications can access protected APIs.
    /// Default: "Unsigned" for development compatibility.
    /// </summary>
    public string BdJSecurityMode { get; set; } = "Unsigned";

    /// <summary>
    /// BD-J organization ID (32-bit). Identifies the content provider.
    /// Used in the BDJO file and for application file access permissions.
    /// Range: 0x00000001 - 0x7FFFFFFF (0 is reserved).
    /// Default: 0x7FFF0001 (generic development ID).
    /// </summary>
    public int BdJOrganizationId { get; set; } = 0x7FFF0001;

    /// <summary>
    /// BD-J initial application ID. Each BD-J Xlet has a unique 16-bit application ID.
    /// Range: 0x0001 - 0x3FFF (0x4000-0x7FFF reserved for manufacturer use).
    /// The first application in <see cref="BdJApplications"/> uses this ID;
    /// subsequent applications increment from this value.
    /// </summary>
    public ushort BdJInitialAppId { get; set; } = 0x0001;

    /// <summary>
    /// Whether the BD-J title should auto-start the first application.
    /// When true, the BDJO sets the application lifecycle to AUTOSTART.
    /// When false, the application uses PRESENT (waits for user activation).
    /// </summary>
    public bool BdJAutoStart { get; set; } = true;

    /// <summary>
    /// BD-J persistent storage requirement in bytes.
    /// BD-Live (Profile 2.0) mandates at least 64 KB of persistent storage.
    /// 0 = no persistent storage requirement.
    /// </summary>
    public int BdJPersistentStorageBytes { get; set; }

    /// <summary>
    /// BD-J network access requirement.
    /// When true, the BDJO enables network permission for BD-Live features.
    /// Requires Profile 2.0 or higher player.
    /// </summary>
    public bool BdJNetworkAccess { get; set; }
}

/// <summary>
/// Defines a BD-J (Blu-ray Disc Java) application entry.
/// Each BD-J application corresponds to a BDJO file in BDMV/BDJO/ and
/// one or more JAR files in BDMV/JAR/.
/// </summary>
public class BdJApplication
{
    /// <summary>
    /// Display name of the BD-J application (for disc authoring reference).
    /// This is not stored in the BDJO file itself.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Paths to Java archive (.jar) files containing the BD-J Xlet classes.
    /// At least one JAR file is required per application.
    /// JAR files are copied to BDMV/JAR/ on the disc.
    /// The main Xlet class must implement javax.tv.xlet.Xlet interface.
    /// </summary>
    public List<string> JarPaths { get; set; } = new();

    /// <summary>
    /// Fully qualified name of the initial Xlet class (e.g., "com.example.MainXlet").
    /// This class must implement javax.tv.xlet.Xlet with the standard lifecycle
    /// methods: initXlet(), startXlet(), pauseXlet(), destroyXlet().
    /// </summary>
    public string InitialClass { get; set; } = string.Empty;

    /// <summary>
    /// Application classpath entries (JAR file names without path).
    /// If empty, all JARs in <see cref="JarPaths"/> are added to the classpath.
    /// Example: ["main.jar", "lib.jar"]
    /// </summary>
    public List<string> ClasspathEntries { get; set; } = new();

    /// <summary>
    /// Application control code per BD-J specification:
    /// "AUTOSTART" (0x01) — Application starts automatically when title is entered.
    /// "PRESENT" (0x02) — Application is available but not auto-started.
    /// Default: "AUTOSTART"
    /// </summary>
    public string ControlCode { get; set; } = "AUTOSTART";

    /// <summary>
    /// Application priority (1-255). Higher priority applications are started first
    /// and receive resources before lower priority applications.
    /// Default: 200 (typical for main application).
    /// </summary>
    public int Priority { get; set; } = 200;

    /// <summary>
    /// Application visibility: "VISIBLE" (0x01) or "INVISIBLE" (0x00).
    /// Visible applications have a graphics plane for rendering.
    /// Default: "VISIBLE"
    /// </summary>
    public string Visibility { get; set; } = "VISIBLE";

    /// <summary>
    /// Associated playlist index (0-based). The BD-J title plays this playlist
    /// while the Xlet application runs. -1 = no associated playlist.
    /// </summary>
    public int PlaylistIndex { get; set; } = -1;

    /// <summary>
    /// Application icon locator path relative to BDMV/ (e.g., "META/icon.png").
    /// Optional. Used by BD-J application manager for display.
    /// </summary>
    public string IconPath { get; set; } = string.Empty;

    /// <summary>
    /// Application parameters passed to the Xlet via XletContext.
    /// These are key-value pairs accessible through XletContext.getXletProperty().
    /// </summary>
    public Dictionary<string, string> Parameters { get; set; } = new();
}

/// <summary>
/// A Blu-ray movie playlist (MPLS). Playlists define the play order of clips,
/// chapter marks, and stream selection defaults.
/// </summary>
public class BluRayPlaylist
{
    /// <summary>Playlist name (for display/reference purposes).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Play items (clips) in this playlist in play order.</summary>
    public List<BluRayPlayItem> PlayItems { get; set; } = new();

    /// <summary>Chapter marks within this playlist.</summary>
    public List<BluRayChapter> Chapters { get; set; } = new();

    /// <summary>Sub-path entries for secondary video/audio (e.g., picture-in-picture, commentary).</summary>
    public List<BluRaySubPath> SubPaths { get; set; } = new();
}

/// <summary>
/// A play item (clip reference) within a Blu-ray playlist.
/// Each play item references an M2TS clip with in/out points.
/// </summary>
public class BluRayPlayItem
{
    /// <summary>Path to the input video file for this clip.</summary>
    public string VideoPath { get; set; } = string.Empty;

    /// <summary>In-point (start time) within the clip. Default: beginning.</summary>
    public TimeSpan InTime { get; set; }

    /// <summary>Out-point (end time) within the clip. Default: end of clip. TimeSpan.Zero = end.</summary>
    public TimeSpan OutTime { get; set; }

    /// <summary>
    /// Audio streams for this play item (up to 32 per Blu-ray spec).
    /// Primary audio is index 0.
    /// </summary>
    public List<BluRayAudioStream> AudioStreams { get; set; } = new();

    /// <summary>
    /// PGS subtitle streams for this play item (up to 255 per Blu-ray spec).
    /// </summary>
    public List<BluRaySubtitleStream> SubtitleStreams { get; set; } = new();

    /// <summary>Connection condition to previous play item: "seamless" or "nonseamless".</summary>
    public string ConnectionCondition { get; set; } = "nonseamless";
}

/// <summary>
/// A chapter mark within a Blu-ray playlist.
/// Chapters are associated with specific play items by index.
/// </summary>
public class BluRayChapter
{
    /// <summary>Chapter name for metadata.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Index of the play item this chapter belongs to (0-based).</summary>
    public int PlayItemIndex { get; set; }

    /// <summary>Start time of the chapter relative to the play item's in-time.</summary>
    public TimeSpan StartTime { get; set; }
}

/// <summary>
/// Blu-ray audio stream configuration.
/// </summary>
public class BluRayAudioStream
{
    /// <summary>Path to audio file. Empty = demux from video file.</summary>
    public string AudioPath { get; set; } = string.Empty;

    /// <summary>
    /// Audio codec: "LPCM", "AC3" (Dolby Digital), "EAC3" (Dolby Digital Plus),
    /// "TrueHD" (Dolby TrueHD), "DTS", "DTSHD_MA" (DTS-HD Master Audio),
    /// "DTSHD_HR" (DTS-HD High Resolution).
    /// Primary audio must be one of: LPCM, AC3, DTS.
    /// </summary>
    public string Codec { get; set; } = "AC3";

    /// <summary>ISO 639-2 language code (e.g., "eng", "fra", "deu", "jpn").</summary>
    public string LanguageCode { get; set; } = "eng";

    /// <summary>Audio bitrate in kbps. 0 = auto. AC3 typical: 640 kbps for 5.1.</summary>
    public int BitrateKbps { get; set; }

    /// <summary>Number of channels: 2 (stereo), 6 (5.1), 8 (7.1).</summary>
    public int Channels { get; set; } = 2;

    /// <summary>Sample rate in Hz. Blu-ray: 48000, 96000, or 192000 Hz.</summary>
    public int SampleRate { get; set; } = 48000;

    /// <summary>Bits per sample for LPCM: 16, 20, or 24.</summary>
    public int BitsPerSample { get; set; } = 16;
}

/// <summary>
/// Blu-ray PGS (Presentation Graphics Stream) subtitle configuration.
/// PGS subtitles are bitmap-based for high quality rendering.
/// </summary>
public class BluRaySubtitleStream
{
    /// <summary>
    /// Path to subtitle file. Supported: .srt, .ssa/.ass, .sup (PGS binary).
    /// Text formats are rendered to PGS bitmap format.
    /// </summary>
    public string SubtitlePath { get; set; } = string.Empty;

    /// <summary>ISO 639-2 language code.</summary>
    public string LanguageCode { get; set; } = "eng";

    /// <summary>Whether this is the default subtitle stream.</summary>
    public bool IsDefault { get; set; }

    /// <summary>Whether this subtitle is for forced narrative only.</summary>
    public bool IsForced { get; set; }

    /// <summary>Font size for text-to-PGS rendering (16-72 pixels).</summary>
    public int FontSize { get; set; } = 36;

    /// <summary>Font color in #RRGGBB hex format.</summary>
    public string FontColor { get; set; } = "#FFFFFF";

    /// <summary>Whether to add a shadow/outline for readability.</summary>
    public bool FontShadow { get; set; } = true;
}

/// <summary>
/// Sub-path entry for secondary streams (picture-in-picture, audio commentary, 3D dependent view).
/// </summary>
public class BluRaySubPath
{
    /// <summary>
    /// Sub-path type: "PiP" (picture-in-picture), "SecondaryAudio",
    /// "DependentView3D" (stereoscopic dependent view for Blu-ray 3D).
    /// </summary>
    public string Type { get; set; } = "SecondaryAudio";

    /// <summary>Path to the secondary stream file.</summary>
    public string StreamPath { get; set; } = string.Empty;

    /// <summary>ISO 639-2 language code for the secondary stream.</summary>
    public string LanguageCode { get; set; } = "eng";

    /// <summary>Synchronization base: time offset from main path start.</summary>
    public TimeSpan SyncOffset { get; set; }
}

/// <summary>
/// Blu-ray menu definition using Interactive Graphics (IG) streams.
/// </summary>
public class BluRayMenu
{
    /// <summary>Path to background image (1920×1080 or 1280×720 PNG/JPEG).</summary>
    public string BackgroundPath { get; set; } = string.Empty;

    /// <summary>Optional background audio path.</summary>
    public string AudioPath { get; set; } = string.Empty;

    /// <summary>Menu buttons (up to 255 per Blu-ray spec).</summary>
    public List<BluRayMenuButton> Buttons { get; set; } = new();

    /// <summary>Menu duration in seconds. 0 = still (infinite loop).</summary>
    public int DurationSeconds { get; set; }

    /// <summary>Index of the default selected button (0-based).</summary>
    public int DefaultButtonIndex { get; set; }
}

/// <summary>
/// A selectable button on a Blu-ray menu.
/// </summary>
public class BluRayMenuButton
{
    /// <summary>Button label text.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Navigation action: "playlist:N" (play playlist N),
    /// "playlist:N:chapter:M" (play playlist N from chapter M),
    /// "menu:top" (go to top menu), "popup" (show popup menu).
    /// </summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>Normal state button image path (optional, for custom graphics).</summary>
    public string NormalImagePath { get; set; } = string.Empty;

    /// <summary>Selected state button image path (optional).</summary>
    public string SelectedImagePath { get; set; } = string.Empty;

    /// <summary>Activated state button image path (optional).</summary>
    public string ActivatedImagePath { get; set; } = string.Empty;

    /// <summary>X position of button (pixels from left).</summary>
    public int X { get; set; }

    /// <summary>Y position of button (pixels from top).</summary>
    public int Y { get; set; }

    /// <summary>Width of button area in pixels.</summary>
    public int Width { get; set; } = 300;

    /// <summary>Height of button area in pixels.</summary>
    public int Height { get; set; } = 60;

    /// <summary>Navigation: button index to go to when UP is pressed. -1 = none.</summary>
    public int UpButtonIndex { get; set; } = -1;

    /// <summary>Navigation: button index for DOWN. -1 = none.</summary>
    public int DownButtonIndex { get; set; } = -1;

    /// <summary>Navigation: button index for LEFT. -1 = none.</summary>
    public int LeftButtonIndex { get; set; } = -1;

    /// <summary>Navigation: button index for RIGHT. -1 = none.</summary>
    public int RightButtonIndex { get; set; } = -1;
}
