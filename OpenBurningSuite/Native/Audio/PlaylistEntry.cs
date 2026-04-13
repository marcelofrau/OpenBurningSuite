// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;

namespace OpenBurningSuite.Native.Audio;

/// <summary>Represents one entry parsed from a playlist file.</summary>
public sealed class PlaylistEntry
{
    /// <summary>Absolute or relative file path / URL.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Display title (may be empty if the format doesn't carry it).</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Duration of the track.  <see cref="TimeSpan.Zero"/> or negative when
    /// the playlist did not specify a duration.
    /// </summary>
    public TimeSpan Duration { get; set; }
}

/// <summary>
/// Supported playlist formats.
/// </summary>
public enum PlaylistFormat
{
    /// <summary>Standard M3U (system default encoding).</summary>
    M3U,

    /// <summary>UTF-8 encoded M3U.</summary>
    M3U8,

    /// <summary>PLS (INI-style, Winamp / SHOUTcast).</summary>
    PLS,

    /// <summary>Windows Playlist (SMIL-based XML).</summary>
    WPL,

    /// <summary>Advanced Stream Redirector (XML).</summary>
    ASX
}
