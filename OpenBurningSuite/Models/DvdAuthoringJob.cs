// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;

namespace OpenBurningSuite.Models;

/// <summary>
/// Configuration for DVD-Video authoring per DVD-Video specification (ECMA-267, DVD Forum).
///
/// DVD-Video structure (VIDEO_TS directory):
/// - VIDEO_TS.IFO  : Video Manager Information (VMGI) — disc-level navigation
/// - VIDEO_TS.BUP  : Backup copy of VIDEO_TS.IFO
/// - VIDEO_TS.VOB  : Video Manager Menu VOB (optional)
/// - VTS_01_0.IFO  : Video Title Set 1 Information (VTSI)
/// - VTS_01_0.BUP  : Backup of VTS_01_0.IFO
/// - VTS_01_0.VOB  : Title Set 1 Menu VOB (optional)
/// - VTS_01_1.VOB  : Title Set 1 Title VOB (video data)
/// - VTS_01_2.VOB  : Title Set 1 continuation (if > 1 GB)
///
/// Each VOB file is limited to 1 GiB (1,073,741,824 bytes).
/// Maximum 99 title sets, 99 titles, 999 chapters per title.
/// </summary>
public class DvdAuthoringJob
{
    /// <summary>Video standard: "NTSC" (720×480, 29.97fps) or "PAL" (720×576, 25fps).</summary>
    public string VideoStandard { get; set; } = "NTSC";

    /// <summary>Video aspect ratio: "4:3" or "16:9".</summary>
    public string AspectRatio { get; set; } = "16:9";

    /// <summary>
    /// MPEG-2 video encoding bitrate in kbps.
    /// DVD-Video max: 9,800 kbps video (10,080 kbps total mux rate).
    /// Recommended: 4000-8000 kbps for good quality. 0 = auto (calculate from disc capacity).
    /// </summary>
    public int VideoBitrateKbps { get; set; }

    /// <summary>Title sets to create on the disc. Each title set can contain multiple titles.</summary>
    public List<DvdTitleSet> TitleSets { get; set; } = new();

    /// <summary>Optional first-play video (autoplay when disc is inserted). Path to MPEG-2 PS file.</summary>
    public string FirstPlayVideoPath { get; set; } = string.Empty;

    /// <summary>Optional top-level menu definition.</summary>
    public DvdMenu? MainMenu { get; set; }

    /// <summary>
    /// DVD region mask (1-8). Bits 0-7 represent regions 1-8.
    /// 0x00 = all regions (region-free), 0xFF = no region allowed.
    /// Default: 0x00 (region-free).
    /// </summary>
    public byte RegionMask { get; set; }

    /// <summary>Whether to copy-protect the disc (Macrovision analog protection flag).</summary>
    public bool CopyProtection { get; set; }

    /// <summary>
    /// Whether to transcode input video files to DVD-compliant MPEG-2 PS format.
    /// When false, input files must already be DVD-compliant MPEG-2 PS streams.
    /// When true, the VideoTranscoder is used to convert input to compliant format.
    /// </summary>
    public bool TranscodeVideo { get; set; }

    /// <summary>Volume label for the DVD.</summary>
    public string VolumeLabel { get; set; } = "DVD_VIDEO";
}

/// <summary>
/// A DVD-Video Title Set (VTS). Each VTS can contain up to 99 titles
/// and shares the same video/audio/subtitle stream format.
/// </summary>
public class DvdTitleSet
{
    /// <summary>Titles in this title set. At least one title is required.</summary>
    public List<DvdTitle> Titles { get; set; } = new();

    /// <summary>Optional title set menu definition.</summary>
    public DvdMenu? Menu { get; set; }

    /// <summary>
    /// Audio streams for this title set (up to 8 per DVD-Video spec).
    /// All titles in the set share the same audio stream configuration.
    /// </summary>
    public List<DvdAudioStream> AudioStreams { get; set; } = new();

    /// <summary>
    /// Subtitle streams for this title set (up to 32 per DVD-Video spec).
    /// All titles in the set share the same subtitle stream configuration.
    /// </summary>
    public List<DvdSubtitleStream> SubtitleStreams { get; set; } = new();
}

/// <summary>
/// A single DVD-Video title (e.g., a movie, episode, or bonus feature).
/// </summary>
public class DvdTitle
{
    /// <summary>Path to the input video file (MPEG-2 PS or any format if transcoding is enabled).</summary>
    public string VideoPath { get; set; } = string.Empty;

    /// <summary>Chapter points within this title.</summary>
    public List<DvdChapter> Chapters { get; set; } = new();

    /// <summary>
    /// Optional angle video paths for multi-angle content.
    /// Each entry is an alternative video stream for the same time span.
    /// DVD-Video supports up to 9 angles.
    /// </summary>
    public List<string> AngleVideoPaths { get; set; } = new();
}

/// <summary>
/// A chapter point within a DVD title.
/// Chapters divide the title into navigable segments (up to 999 per title).
/// </summary>
public class DvdChapter
{
    /// <summary>Chapter name (for display purposes, not stored on disc).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Start time of the chapter relative to the title start.</summary>
    public TimeSpan StartTime { get; set; }
}

/// <summary>
/// DVD-Video audio stream configuration.
/// DVD supports: AC-3 (Dolby Digital), MPEG-1/2 Layer II, LPCM, DTS.
/// </summary>
public class DvdAudioStream
{
    /// <summary>Path to the audio file. If empty, audio is demuxed from the video file.</summary>
    public string AudioPath { get; set; } = string.Empty;

    /// <summary>
    /// Audio codec: "AC3" (Dolby Digital), "MP2" (MPEG Audio), "LPCM", "DTS".
    /// AC3 is the most compatible and commonly used format for DVD-Video.
    /// </summary>
    public string Codec { get; set; } = "AC3";

    /// <summary>ISO 639-2 language code (e.g., "eng", "fra", "deu", "jpn").</summary>
    public string LanguageCode { get; set; } = "eng";

    /// <summary>Audio bitrate in kbps. 0 = auto. AC3 typical: 192-448 kbps.</summary>
    public int BitrateKbps { get; set; }

    /// <summary>Number of channels: 1 (mono), 2 (stereo), 6 (5.1 surround).</summary>
    public int Channels { get; set; } = 2;

    /// <summary>Sample rate in Hz. DVD audio: 48000 Hz (mandatory for AC3/DTS).</summary>
    public int SampleRate { get; set; } = 48000;
}

/// <summary>
/// DVD-Video subtitle stream configuration.
/// DVD subtitles use bitmap-based subpicture format (run-length encoded images).
/// </summary>
public class DvdSubtitleStream
{
    /// <summary>
    /// Path to subtitle file. Supported formats: .srt, .sub, .ssa/.ass, .idx/.sub (VobSub).
    /// </summary>
    public string SubtitlePath { get; set; } = string.Empty;

    /// <summary>ISO 639-2 language code (e.g., "eng", "fra", "deu").</summary>
    public string LanguageCode { get; set; } = "eng";

    /// <summary>Whether this is the default subtitle stream.</summary>
    public bool IsDefault { get; set; }

    /// <summary>Whether this subtitle is for forced narrative elements only.</summary>
    public bool IsForced { get; set; }

    /// <summary>Character set for text-based subtitle files (e.g., "UTF-8", "ISO-8859-1").</summary>
    public string CharacterSet { get; set; } = "UTF-8";

    /// <summary>Font size for rendering text subtitles to DVD bitmap format (12-36 pixels).</summary>
    public int FontSize { get; set; } = 24;

    /// <summary>Font color for rendering text subtitles, in #RRGGBB hex format.</summary>
    public string FontColor { get; set; } = "#FFFFFF";
}

/// <summary>
/// DVD-Video menu definition.
/// Menus consist of a still image or short video loop with selectable buttons.
/// </summary>
public class DvdMenu
{
    /// <summary>
    /// Path to background image (JPEG, PNG, BMP) or short MPEG-2 video for animated menus.
    /// Image will be scaled to the video standard resolution (720×480 or 720×576).
    /// </summary>
    public string BackgroundPath { get; set; } = string.Empty;

    /// <summary>Optional background audio for the menu. Path to audio file.</summary>
    public string AudioPath { get; set; } = string.Empty;

    /// <summary>
    /// Menu buttons. Each button navigates to a title, chapter, or another menu.
    /// DVD-Video supports up to 36 buttons per menu.
    /// </summary>
    public List<DvdMenuButton> Buttons { get; set; } = new();

    /// <summary>Duration of menu loop in seconds. 0 = still image (infinite).</summary>
    public int LoopDurationSeconds { get; set; }
}

/// <summary>
/// A selectable button on a DVD menu.
/// </summary>
public class DvdMenuButton
{
    /// <summary>Button label text (rendered onto the menu image).</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Navigation action when the button is activated.
    /// Format: "title:N" (play title N), "title:N:chapter:M" (play title N from chapter M),
    /// "menu:root" (go to root menu), "menu:title" (go to title menu).
    /// </summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>X position of button highlight area (pixels from left).</summary>
    public int X { get; set; }

    /// <summary>Y position of button highlight area (pixels from top).</summary>
    public int Y { get; set; }

    /// <summary>Width of button highlight area in pixels.</summary>
    public int Width { get; set; } = 200;

    /// <summary>Height of button highlight area in pixels.</summary>
    public int Height { get; set; } = 40;

    /// <summary>Button highlight color in #RRGGBB hex format.</summary>
    public string HighlightColor { get; set; } = "#FFFF00";

    /// <summary>Button selection (activated) color in #RRGGBB hex format.</summary>
    public string SelectColor { get; set; } = "#FF0000";
}
