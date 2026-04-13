// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;

namespace OpenBurningSuite.Models;

public enum BuildStatus { Idle, Running, Completed, Failed, Cancelled }

public class BuildProgress
{
    public int PercentComplete { get; set; }
    public long BytesProcessed { get; set; }
    public long TotalBytes { get; set; }
    public string CurrentFile { get; set; } = string.Empty;
    public TimeSpan Elapsed { get; set; }
    public TimeSpan Remaining { get; set; }
    public string StatusMessage { get; set; } = string.Empty;
    public string? LogLine { get; set; }
}

public class AudioTrack
{
    public string FilePath { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    /// <summary>CD-TEXT songwriter for this track.</summary>
    public string Songwriter { get; set; } = string.Empty;
    public int PreGapSeconds { get; set; } = 2;
    /// <summary>
    /// Additional pre-gap frames (0-74) added to the pre-gap beyond whole seconds.
    /// Total pre-gap = PreGapSeconds × 75 + PreGapFrames. One frame = 1/75 second.
    /// </summary>
    public int PreGapFrames { get; set; }
    public string Isrc { get; set; } = string.Empty;
    public bool CopyPermitted { get; set; }
    public bool FourChannel { get; set; }
    public bool PreEmphasis { get; set; }
    public TimeSpan Duration { get; set; }
}
