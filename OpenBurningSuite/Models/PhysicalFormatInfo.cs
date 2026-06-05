// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

namespace OpenBurningSuite.Models;

public class PhysicalFormatInfo
{
    public int LayerNumber { get; set; }
    public string BookType { get; set; } = string.Empty;
    public int PartVersion { get; set; }
    public string DiscSize { get; set; } = string.Empty;
    public int LayerCount { get; set; }
    public string TrackPath { get; set; } = string.Empty;
    public string LinearDensity { get; set; } = string.Empty;
    public string TrackDensity { get; set; } = string.Empty;
    public long FirstPhysicalSector { get; set; }
    public long LastPhysicalSector { get; set; }
    public long LastSectorLayer0 { get; set; }
    public double MaxReadRateMbps { get; set; }
}
