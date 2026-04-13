// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;

namespace OpenBurningSuite.Models;

public class ReadJob
{
    public string DevicePath { get; set; } = string.Empty;
    public string OutputPath { get; set; } = string.Empty;
    public string OutputFormat { get; set; } = "ISO";   // ISO, BIN/CUE, BIN/CUE/SBI, NRG, MDF/MDS, IMG, TOC/BIN, CDI, CCD/IMG/SUB, VCD, SVCD, XSVCD, XISO (Xbox)
    public string ReadSpeed { get; set; } = "Max";
    public string ErrorRecovery { get; set; } = "Yes";  // Yes, No, Abort
    public int RetryCount { get; set; } = 3;
    public int SectorSize { get; set; } = 2048;
    public string SubchannelMode { get; set; } = "None"; // None, RW, RW_RAW, P-W
    public bool AudioParanoia { get; set; }
    public bool JitterCorrection { get; set; } = true;
    public bool ReadBadSectors { get; set; }
    public string GamingPreset { get; set; } = string.Empty;
    /// <summary>When true, attempt to decrypt PS3 disc content during copy.</summary>
    public bool DecryptPs3 { get; set; }
    /// <summary>Path to the IRD file for PS3 disc decryption (contains disc encryption keys).</summary>
    public string IrdFilePath { get; set; } = string.Empty;

    /// <summary>
    /// Enable C2 error flag reporting during raw CD reads.
    /// When enabled, the drive returns a 294-byte bitmap per sector identifying
    /// which individual bytes had uncorrectable C2-level errors. This is essential
    /// for byte-perfect disc dumping (as used by redumper and other preservation tools).
    /// Requires drive support for C2 Error Pointers (Feature 0x001E, CDRead, C2ErrorData bit).
    /// </summary>
    public bool C2ErrorFlags { get; set; }

    /// <summary>
    /// Drive read offset in samples for audio CD accuracy.
    /// Each audio CD drive has a fixed read offset (positive or negative) that
    /// shifts the audio data relative to sector boundaries. Applying the correct
    /// offset produces byte-perfect audio rips. Values are per-drive and can be
    /// found in the AccurateRip drive offset database.
    /// Default: 0 (no offset correction).
    /// </summary>
    public int DriveReadOffset { get; set; }

    /// <summary>
    /// When true, generates MD5 and SHA-1 hashes of the read data for dump verification.
    /// Hash values are logged on completion and can be used to verify dump integrity
    /// against known-good databases (e.g., Redump.org).
    /// </summary>
    public bool GenerateHashes { get; set; }

    /// <summary>
    /// When true, attempts to read sectors beyond the lead-out area for complete disc
    /// preservation. Some drives support reading a small number of sectors past the
    /// lead-out start position. This captures data that may exist in the run-out area
    /// and is used by disc preservation tools like redumper. Stops gracefully when the
    /// drive returns persistent read errors.
    /// Default: false.
    /// </summary>
    public bool LeadOutOverread { get; set; }

    public ReadStatus Status { get; set; } = ReadStatus.Idle;
    public DateTime StartedAt { get; set; }
    public DateTime FinishedAt { get; set; }
}
