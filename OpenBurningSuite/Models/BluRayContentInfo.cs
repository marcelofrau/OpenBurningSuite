// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System.Collections.Generic;

namespace OpenBurningSuite.Models;

/// <summary>Type of content detected on a Blu-ray disc or in a Blu-ray ISO image.</summary>
public enum BluRayContentType
{
    /// <summary>Unknown or unreadable content.</summary>
    Unknown,
    /// <summary>Standard data disc (no specific structure detected).</summary>
    Data,
    /// <summary>Blu-ray movie (BDMV structure).</summary>
    Movie,
    /// <summary>PlayStation 3 game disc.</summary>
    Ps3Game,
    /// <summary>PlayStation 4 game disc.</summary>
    Ps4Game,
    /// <summary>PlayStation 5 game disc.</summary>
    Ps5Game,
    /// <summary>Blu-ray Audio disc (high-fidelity audio in BDMV structure).</summary>
    Audio,
    /// <summary>BDAV recording (Blu-ray Disc Audio/Visual recorded content).</summary>
    Bdav,
    /// <summary>BD-J Interactive (Blu-ray Disc Java application).</summary>
    BdJava,
    /// <summary>Blu-ray 3D movie (stereoscopic content with SSIF streams).</summary>
    Movie3D,
    /// <summary>UHD Blu-ray (4K Ultra HD, BDMV version 0300).</summary>
    UhdMovie,
    /// <summary>Xbox One game disc (Blu-ray based).</summary>
    XboxOneGame,
    /// <summary>Xbox Series X|S game disc (Blu-ray based).</summary>
    XboxSeriesGame,
    /// <summary>Photo disc (DCIM or gallery structure on Blu-ray).</summary>
    Photo
}

/// <summary>
/// Information about Blu-ray disc or ISO image content, including
/// PS3 game metadata when applicable.
/// </summary>
public class BluRayContentInfo
{
    /// <summary>Detected content type.</summary>
    public BluRayContentType ContentType { get; set; } = BluRayContentType.Unknown;

    /// <summary>Volume label from the ISO 9660 or UDF filesystem.</summary>
    public string VolumeLabel { get; set; } = string.Empty;

    /// <summary>PS3 title ID (e.g. "BCUS98114"), empty for non-PS3 discs.</summary>
    public string TitleId { get; set; } = string.Empty;

    /// <summary>PS3 game title from PARAM.SFO, empty for non-PS3 discs.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>PS3 disc version from PARAM.SFO (e.g. "01.00").</summary>
    public string DiscVersion { get; set; } = string.Empty;

    /// <summary>PS3 firmware version required from PARAM.SFO (e.g. "1.20").</summary>
    public string FirmwareVersion { get; set; } = string.Empty;

    /// <summary>PARAM.SFO CATEGORY field (e.g. "DG" for disc game).</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>Total number of encryption regions on the disc (PS3 only).</summary>
    public int RegionCount { get; set; }

    /// <summary>Whether the disc contains encrypted content that requires a disc key.</summary>
    public bool IsEncrypted { get; set; }

    /// <summary>Human-readable summary of the detected content.</summary>
    public string Summary =>
        ContentType switch
        {
            BluRayContentType.Ps3Game => string.IsNullOrEmpty(Title)
                ? $"PS3 Game Disc [{TitleId}]"
                : $"PS3 Game: {Title} [{TitleId}]",
            BluRayContentType.Ps4Game => "PS4 Game Disc",
            BluRayContentType.Ps5Game => "PS5 Game Disc",
            BluRayContentType.Movie => "Blu-ray Movie",
            BluRayContentType.Audio => "Blu-ray Audio",
            BluRayContentType.Bdav => "BDAV Recording",
            BluRayContentType.BdJava => "BD-J Interactive",
            BluRayContentType.Movie3D => "Blu-ray 3D Movie",
            BluRayContentType.UhdMovie => "UHD Blu-ray (4K)",
            BluRayContentType.XboxOneGame => "Xbox One Game Disc",
            BluRayContentType.XboxSeriesGame => "Xbox Series X|S Game Disc",
            BluRayContentType.Photo => "Blu-ray Photo Disc",
            BluRayContentType.Data => "Data Disc",
            _ => "Unknown Blu-ray Content"
        };
}
