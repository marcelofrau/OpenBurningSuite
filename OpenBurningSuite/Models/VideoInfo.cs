// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;

namespace OpenBurningSuite.Native.Video;

/// <summary>
/// Information about a video file obtained from probing.
/// </summary>
public class VideoInfo
{
    public string FilePath { get; set; } = string.Empty;
    public TimeSpan Duration { get; set; }
    public string VideoCodec { get; set; } = string.Empty;
    public string AudioCodec { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
}
