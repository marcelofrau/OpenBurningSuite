// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

namespace OpenBurningSuite.Models;

/// <summary>
/// Per-format read/write profile capabilities as reported by the drive's
/// GET CONFIGURATION Profile List feature (0x0000).
/// Each property indicates whether the drive supports the given media profile.
/// </summary>
public class DriveProfileCapabilities
{
    // CD profiles
    public bool ReadCdR { get; set; }
    public bool ReadCdRw { get; set; }
    public bool WriteCdR { get; set; }
    public bool WriteCdRw { get; set; }

    // DVD profiles
    public bool ReadDvdRom { get; set; }
    public bool ReadDvdR { get; set; }
    public bool ReadDvdRw { get; set; }
    public bool ReadDvdPlusR { get; set; }
    public bool ReadDvdPlusRw { get; set; }
    public bool ReadDvdRDl { get; set; }
    public bool ReadDvdRwDl { get; set; }   // DVD-RW DL: no standard MMC profile exists for this format; included for UI completeness
    public bool ReadDvdPlusRDl { get; set; }
    public bool ReadDvdPlusRwDl { get; set; }
    public bool ReadDvdRam { get; set; }

    public bool WriteDvdR { get; set; }
    public bool WriteDvdRw { get; set; }
    public bool WriteDvdPlusR { get; set; }
    public bool WriteDvdPlusRw { get; set; }
    public bool WriteDvdRDl { get; set; }
    public bool WriteDvdPlusRDl { get; set; }
    public bool WriteDvdPlusRwDl { get; set; }
    public bool WriteDvdRam { get; set; }

    // HD DVD profiles
    public bool ReadHdDvdRom { get; set; }
    public bool ReadHdDvdR { get; set; }
    public bool ReadHdDvdRw { get; set; }
    public bool ReadHdDvdRam { get; set; }
    public bool ReadHdDvdRDl { get; set; }
    public bool ReadHdDvdRwDl { get; set; }

    public bool WriteHdDvdR { get; set; }
    public bool WriteHdDvdRw { get; set; }
    public bool WriteHdDvdRam { get; set; }
    public bool WriteHdDvdRDl { get; set; }
    public bool WriteHdDvdRwDl { get; set; }

    // Blu-ray profiles
    public bool ReadBdRom { get; set; }
    public bool ReadBdR { get; set; }
    public bool ReadBdRe { get; set; }
    public bool ReadBdRXl { get; set; }
    public bool ReadBdReXl { get; set; }

    public bool WriteBdR { get; set; }
    public bool WriteBdRe { get; set; }
    public bool WriteBdRXl { get; set; }
    public bool WriteBdReXl { get; set; }
}

/// <summary>
/// Additional drive feature capabilities detected via GET CONFIGURATION
/// feature descriptors and MODE SENSE pages.
/// </summary>
public class DriveFeatureCapabilities
{
    /// <summary>Drive supports C2 error pointers (accurate error reporting for audio ripping).</summary>
    public bool C2ErrorPointers { get; set; }
    /// <summary>Drive supports reading/writing CD-Text.</summary>
    public bool CdText { get; set; }
    /// <summary>Drive supports Layer Jump Recording for DVD-R DL.</summary>
    public bool LayerJumpRecording { get; set; }
    /// <summary>Drive supports Labelflash (disc label printing).</summary>
    public bool Labelflash { get; set; }
    /// <summary>Drive supports LightScribe (disc label printing).</summary>
    public bool LightScribe { get; set; }
    /// <summary>Drive supports AACS Binding Nonce Generation (Blu-ray content protection).</summary>
    public bool BindingNonceGeneration { get; set; }
    /// <summary>Drive supports Bus Encryption (AACS bus encryption for Blu-ray).</summary>
    public bool BusEncryption { get; set; }
    /// <summary>Drive supports DVD-CSS/CPPM (DVD content protection).</summary>
    public bool DvdCss { get; set; }
}
