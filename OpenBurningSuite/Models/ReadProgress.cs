// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;

namespace OpenBurningSuite.Models;

public enum ReadStatus { Idle, Running, Completed, Failed, Cancelled }

public class ReadProgress
{
    public int PercentComplete { get; set; }
    public long SectorsRead { get; set; }
    public long TotalSectors { get; set; }
    public long ErrorSectors { get; set; }
    public double CurrentSpeedX { get; set; }
    public TimeSpan Elapsed { get; set; }
    public TimeSpan Remaining { get; set; }
    public string StatusMessage { get; set; } = string.Empty;
    public string? LogLine { get; set; }
}
