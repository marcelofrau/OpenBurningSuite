// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;

namespace OpenBurningSuite.Models;

public enum VerifyStatus { Idle, Running, Completed, Failed, Cancelled }

public class VerifyProgress
{
    public int PercentComplete { get; set; }
    public long SectorsVerified { get; set; }
    public long TotalSectors { get; set; }
    public long GoodSectors { get; set; }
    public long BadSectors { get; set; }
    public long CurrentLba { get; set; }
    public TimeSpan Elapsed { get; set; }
    public TimeSpan Remaining { get; set; }
    public string StatusMessage { get; set; } = string.Empty;
    public string? LogLine { get; set; }
}

public class VerifyResult
{
    public bool Passed { get; set; }
    public long TotalSectors { get; set; }
    public long GoodSectors { get; set; }
    public long BadSectors { get; set; }
    public string ChecksumType { get; set; } = string.Empty;
    public string ExpectedChecksum { get; set; } = string.Empty;
    public string ActualChecksum { get; set; } = string.Empty;
    public bool ChecksumMatch { get; set; }
    public string ErrorSummary { get; set; } = string.Empty;
}
