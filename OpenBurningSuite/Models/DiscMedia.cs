// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;

namespace OpenBurningSuite.Models;

public class DiscMedia
{
    public string MediaType { get; set; } = string.Empty;  // CD-R, DVD+R, BD-R, etc.
    public long CapacityBytes { get; set; }
    public long UsedBytes { get; set; }
    public string DiscState { get; set; } = string.Empty;  // Empty, Appendable, Closed
    public string VolumeLabel { get; set; } = string.Empty;
    public string FileSystem { get; set; } = string.Empty;
    public int Sessions { get; set; }
    public int Tracks { get; set; }

    // --- Disc Info extended fields ---
    public string LastSessionState { get; set; } = string.Empty;
    public bool IsErasable { get; set; }
    public long FreeSectors { get; set; }
    public long NextWritableAddress { get; set; }
    public string ManufacturerId { get; set; } = string.Empty;
    public string MediaId { get; set; } = string.Empty;
    public string ManufacturerName { get; set; } = string.Empty;
    public List<string> SupportedReadSpeeds { get; set; } = new();
    public string? AtipDiscId { get; set; }
    public string? AtipManufacturer { get; set; }
    public string? AtipLeadInStart { get; set; }
    public string? AtipLeadOutLastPossible { get; set; }

    public long FreeBytes => Math.Max(0, CapacityBytes - UsedBytes);

    public string CapacityFormatted => Helpers.FormatHelper.FormatBytes(CapacityBytes);

    public string UsedFormatted => Helpers.FormatHelper.FormatBytes(UsedBytes);

    public string FreeFormatted => Helpers.FormatHelper.FormatBytes(FreeBytes);

    public int UsedPercent =>
        CapacityBytes > 0 ? Math.Min(100, (int)Math.Round(UsedBytes * 100.0 / CapacityBytes)) : 0;
}
