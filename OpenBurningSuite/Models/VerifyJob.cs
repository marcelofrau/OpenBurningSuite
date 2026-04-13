// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;

namespace OpenBurningSuite.Models;

public class VerifyJob
{
    public string VerifyMode { get; set; } = "Verify Disc";   // Verify Disc, Compare Disc to Image, Check Image Integrity
    public string DevicePath { get; set; } = string.Empty;
    public string ImagePath { get; set; } = string.Empty;
    public string ChecksumType { get; set; } = "SHA256";    // MD5, SHA1, SHA256, SHA512, CRC32
    public bool SectorBySector { get; set; } = true;
    public bool VerifySubchannel { get; set; }
    public bool AudioVerification { get; set; }
    public VerifyStatus Status { get; set; } = VerifyStatus.Idle;
    public VerifyResult? Result { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime FinishedAt { get; set; }
}
