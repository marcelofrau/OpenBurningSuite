// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;

namespace OpenBurningSuite.Models;

public enum BurnStatus { Idle, Running, Completed, Failed, Cancelled }

public class BurnProgress
{
    public int PercentComplete { get; set; }
    public double CurrentSpeedX { get; set; }
    public long BytesWritten { get; set; }
    public long TotalBytes { get; set; }
    public long CurrentLba { get; set; }
    public long TotalSectors { get; set; }
    public TimeSpan Elapsed { get; set; }
    public TimeSpan Remaining { get; set; }
    public string StatusMessage { get; set; } = string.Empty;
    public string? LogLine { get; set; }
}
