// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System.Collections.Generic;

namespace OpenBurningSuite.Models;

/// <summary>
/// Unified content type classification for all optical disc media (CD, DVD, Blu-ray).
/// Provides granular differentiation between video, audio, game, photo, and data content.
/// </summary>
public enum DiscContentType
{
    // ---- Unknown / Generic ----
    /// <summary>Content type could not be determined.</summary>
    Unknown,
    /// <summary>Generic data disc with no recognized structure.</summary>
    Data,

    // ---- CD Content Types ----
    /// <summary>Audio CD (CD-DA) — all tracks are audio, per IEC 60908 / Red Book.</summary>
    AudioCd,
    /// <summary>Mixed Mode CD — first track is data (Mode 1/2), remaining tracks are audio (Yellow+Red Book).</summary>
    MixedModeCd,
    /// <summary>CD Extra / Enhanced CD (Blue Book) — first session is audio, second session is data.</summary>
    EnhancedCd,
    /// <summary>Video CD 1.x/2.0 (White Book) — MPEG-1 video on CD-ROM XA.</summary>
    VideoCd,
    /// <summary>Super Video CD (SVCD) — MPEG-2 video on CD-ROM XA (IEC 62107).</summary>
    SuperVideoCd,
    /// <summary>Photo CD (Kodak) — multi-resolution image portfolio on CD-ROM XA.</summary>
    PhotoCd,
    /// <summary>CD-i (Philips Compact Disc Interactive) — Green Book.</summary>
    CdInteractive,
    /// <summary>CD-ROM data disc (Yellow Book) with standard ISO 9660 / Joliet / UDF filesystem.</summary>
    DataCd,

    // ---- CD Game Disc Types ----
    /// <summary>PlayStation 1 game disc.</summary>
    PlayStation1Game,
    /// <summary>Sega Saturn game disc.</summary>
    SegaSaturnGame,
    /// <summary>Sega Dreamcast GD-ROM game disc.</summary>
    SegaDreamcastGame,
    /// <summary>Sega Mega CD / Sega CD game disc.</summary>
    SegaMegaCdGame,
    /// <summary>PC Engine / TurboGrafx-CD game disc.</summary>
    PcEngineGame,
    /// <summary>Neo Geo CD game disc.</summary>
    NeoGeoCdGame,
    /// <summary>Amiga CD32 game disc.</summary>
    AmigaCd32Game,
    /// <summary>Amiga CDTV game disc.</summary>
    AmigaCdtvGame,
    /// <summary>Atari Jaguar CD game disc.</summary>
    AtariJaguarCdGame,
    /// <summary>3DO Interactive Multiplayer game disc.</summary>
    ThreeDOGame,
    /// <summary>Philips CD-i game disc (gaming content on CD-i platform).</summary>
    CdInteractiveGame,

    // ---- DVD Content Types ----
    /// <summary>DVD-Video (VIDEO_TS structure per DVD Forum specification).</summary>
    DvdVideo,
    /// <summary>DVD-Audio (AUDIO_TS structure per DVD Forum specification).</summary>
    DvdAudio,
    /// <summary>DVD-VR (Video Recording) — real-time recorded content with DVD_RTAV structure.</summary>
    DvdVideoRecording,
    /// <summary>DVD data disc (UDF/ISO 9660 filesystem with generic data).</summary>
    DataDvd,
    /// <summary>Photo DVD — DVD with DCIM/photo gallery structure.</summary>
    PhotoDvd,

    // ---- DVD Game Disc Types ----
    /// <summary>PlayStation 2 game disc.</summary>
    PlayStation2Game,
    /// <summary>Xbox original game disc.</summary>
    XboxGame,
    /// <summary>Xbox 360 game disc.</summary>
    Xbox360Game,
    /// <summary>Nintendo Wii game disc.</summary>
    WiiGame,
    /// <summary>Nintendo GameCube game disc (miniDVD format).</summary>
    GameCubeGame,
    /// <summary>Nintendo Wii U game disc.</summary>
    WiiUGame,
    /// <summary>PSP UMD game disc.</summary>
    PspUmdGame,

    // ---- Blu-ray Content Types ----
    /// <summary>Blu-ray Disc Movie (BDMV structure — commercial video).</summary>
    BluRayMovie,
    /// <summary>Blu-ray Disc Audio (BD-Audio) — high-fidelity audio in BDMV structure.</summary>
    BluRayAudio,
    /// <summary>BDAV (Blu-ray Disc Audio/Visual recording) — recorded content with BDAV structure.</summary>
    BluRayAudioVisual,
    /// <summary>BD-J (Blu-ray Disc Java) — interactive Java application disc.</summary>
    BluRayJava,
    /// <summary>Blu-ray 3D movie (BDMV with stereoscopic content).</summary>
    BluRay3D,
    /// <summary>UHD Blu-ray (4K Ultra HD Blu-ray).</summary>
    UhdBluRay,
    /// <summary>Blu-ray data disc (UDF filesystem with generic data, no BDMV/BDAV).</summary>
    DataBluRay,
    /// <summary>Blu-ray photo disc — BD with DCIM/photo gallery structure.</summary>
    PhotoBluRay,

    // ---- Blu-ray Game Disc Types ----
    /// <summary>PlayStation 3 game disc.</summary>
    PlayStation3Game,
    /// <summary>PlayStation 4 game disc.</summary>
    PlayStation4Game,
    /// <summary>PlayStation 5 game disc.</summary>
    PlayStation5Game,
    /// <summary>Xbox One game disc (Blu-ray based).</summary>
    XboxOneGame,
    /// <summary>Xbox Series X|S game disc (Blu-ray based).</summary>
    XboxSeriesGame
}

/// <summary>
/// Comprehensive information about the content detected on an optical disc or disc image.
/// Used by <see cref="OpenBurningSuite.Services.DiscContentDetectionService"/>.
/// </summary>
public class DiscContentInfo
{
    /// <summary>Primary detected content type.</summary>
    public DiscContentType ContentType { get; set; } = DiscContentType.Unknown;

    /// <summary>Human-readable content type label (e.g. "🎬 DVD-Video", "🎵 Audio CD").</summary>
    public string ContentLabel { get; set; } = "Unknown";

    /// <summary>Volume label from the disc filesystem (ISO 9660 / Joliet / UDF).</summary>
    public string VolumeLabel { get; set; } = string.Empty;

    /// <summary>ISO 9660 PVD System Identifier field.</summary>
    public string SystemIdentifier { get; set; } = string.Empty;

    /// <summary>ISO 9660 PVD Application Identifier field.</summary>
    public string ApplicationIdentifier { get; set; } = string.Empty;

    /// <summary>Media type string (e.g. "CD-ROM", "DVD-R", "BD-RE").</summary>
    public string MediaType { get; set; } = string.Empty;

    /// <summary>MMC profile code for the current media.</summary>
    public ushort ProfileCode { get; set; }

    /// <summary>Number of tracks on the disc.</summary>
    public int TrackCount { get; set; }

    /// <summary>Number of sessions on the disc.</summary>
    public int SessionCount { get; set; }

    /// <summary>Number of audio tracks detected.</summary>
    public int AudioTrackCount { get; set; }

    /// <summary>Number of data tracks detected.</summary>
    public int DataTrackCount { get; set; }

    /// <summary>Total disc capacity in bytes (if known).</summary>
    public long CapacityBytes { get; set; }

    /// <summary>Estimated number of layers.</summary>
    public int LayerCount { get; set; } = 1;

    /// <summary>Root directory entries found on the disc.</summary>
    public List<string> RootDirectories { get; set; } = new();

    /// <summary>Root file entries found on the disc.</summary>
    public List<string> RootFiles { get; set; } = new();

    // ---- Game-specific metadata ----

    /// <summary>Detected gaming system name (null if not a game disc).</summary>
    public string? GamingSystem { get; set; }

    /// <summary>Game title (if extractable from disc metadata, e.g. PARAM.SFO).</summary>
    public string? GameTitle { get; set; }

    /// <summary>Game title ID (e.g. PS3 title ID "BCUS98114").</summary>
    public string? GameTitleId { get; set; }

    // ---- Blu-ray-specific metadata ----

    /// <summary>Whether the disc has AACS or other content encryption.</summary>
    public bool IsEncrypted { get; set; }

    /// <summary>Blu-ray content info from PS3/BD detection (when applicable).</summary>
    public BluRayContentInfo? BluRayInfo { get; set; }

    // ---- DVD-specific metadata ----

    /// <summary>Whether the disc has CSS content protection (DVD).</summary>
    public bool HasCssProtection { get; set; }

    /// <summary>DVD region mask (bitmask of allowed regions, 0 = region-free).</summary>
    public byte DvdRegionMask { get; set; }

    /// <summary>
    /// Generates a human-readable summary of the disc content.
    /// </summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            parts.Add(ContentLabel);
            if (!string.IsNullOrEmpty(VolumeLabel))
                parts.Add($"Volume: {VolumeLabel}");
            if (!string.IsNullOrEmpty(GameTitle))
                parts.Add($"Title: {GameTitle}");
            if (!string.IsNullOrEmpty(GameTitleId))
                parts.Add($"ID: {GameTitleId}");
            if (AudioTrackCount > 0 && DataTrackCount > 0)
                parts.Add($"{AudioTrackCount} audio + {DataTrackCount} data tracks");
            else if (AudioTrackCount > 0)
                parts.Add($"{AudioTrackCount} audio tracks");
            return string.Join(" | ", parts);
        }
    }

    /// <summary>Whether this is any kind of CD media (profile 0x0008-0x000A).</summary>
    public bool IsCdMedia => ProfileCode is >= 0x0008 and <= 0x000A;

    /// <summary>Whether this is any kind of DVD media (profile 0x0010-0x002B).</summary>
    public bool IsDvdMedia => ProfileCode is >= 0x0010 and <= 0x002B;

    /// <summary>Whether this is any kind of Blu-ray media (profile 0x0040-0x004D).</summary>
    public bool IsBluRayMedia => ProfileCode is >= 0x0040 and <= 0x004D;

    /// <summary>Whether this is any kind of game disc.</summary>
    public bool IsGameDisc => ContentType is
        DiscContentType.PlayStation1Game or DiscContentType.PlayStation2Game or
        DiscContentType.PlayStation3Game or DiscContentType.PlayStation4Game or
        DiscContentType.PlayStation5Game or
        DiscContentType.XboxGame or DiscContentType.Xbox360Game or
        DiscContentType.XboxOneGame or DiscContentType.XboxSeriesGame or
        DiscContentType.WiiGame or DiscContentType.GameCubeGame or DiscContentType.WiiUGame or
        DiscContentType.PspUmdGame or
        DiscContentType.SegaSaturnGame or DiscContentType.SegaDreamcastGame or
        DiscContentType.SegaMegaCdGame or
        DiscContentType.PcEngineGame or DiscContentType.NeoGeoCdGame or
        DiscContentType.AmigaCd32Game or DiscContentType.AmigaCdtvGame or
        DiscContentType.AtariJaguarCdGame or DiscContentType.ThreeDOGame or
        DiscContentType.CdInteractiveGame;

    /// <summary>Whether this is any kind of video disc.</summary>
    public bool IsVideoDisc => ContentType is
        DiscContentType.DvdVideo or DiscContentType.DvdVideoRecording or
        DiscContentType.BluRayMovie or DiscContentType.BluRay3D or
        DiscContentType.BluRayAudioVisual or DiscContentType.UhdBluRay or
        DiscContentType.VideoCd or DiscContentType.SuperVideoCd;

    /// <summary>Whether this is any kind of audio disc.</summary>
    public bool IsAudioDisc => ContentType is
        DiscContentType.AudioCd or DiscContentType.EnhancedCd or
        DiscContentType.DvdAudio or DiscContentType.BluRayAudio;
}
