// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;

namespace OpenBurningSuite.Native.Optical;


public sealed class DiscInfoData
{
    /// <summary>0=empty, 1=incomplete, 2=complete(finalized), 3=other.</summary>
    public byte DiscStatus { get; init; }
    /// <summary>0=empty, 1=incomplete, 3=complete.</summary>
    public byte LastSessionState { get; init; }
    public bool Erasable { get; init; }
    public byte NumberOfFirstTrack { get; init; }
    public byte NumberOfSessionsLsb { get; init; }
    public byte FirstTrackInLastSessionLsb { get; init; }
    public byte LastTrackInLastSessionLsb { get; init; }
    public byte DiscType { get; init; }

    // Extended fields from ReadDiscInformationEx
    public uint FreeSectors { get; init; }
    public uint NextWritableAddress { get; init; }
    public byte NumberOfSessionsMsb { get; init; }
    public byte NumberOfTracksMsb { get; init; }

    public ushort NumberOfSessions =>
        (ushort)((NumberOfSessionsMsb << 8) | NumberOfSessionsLsb);

    public string DiscStatusString => DiscStatus switch
    {
        0 => "Empty",
        1 => "Incomplete",
        2 => "Closed",
        _ => "Other"
    };
}

public sealed class TrackInfoData
{
    public ushort TrackNumber { get; init; }
    public ushort SessionNumber { get; init; }
    public byte TrackMode { get; init; }
    public byte DataMode { get; init; }
    public uint TrackStartAddress { get; init; }
    public uint NextWritableAddress { get; init; }
    public uint FreeBlocks { get; init; }
    public uint FixedPacketSize { get; init; }
    public uint TrackSize { get; init; }
    public bool NwaValid { get; init; }

    /// <summary>Whether the Last Recorded Address field is valid (LRA_V bit).</summary>
    public bool LraValid { get; init; }

    /// <summary>
    /// Last Recorded Address — the LBA of the last recorded sector in this track.
    /// Valid only when <see cref="LraValid"/> is true. Crucial for determining the end
    /// of recorded data on sequential BD-R/RE discs.
    /// </summary>
    public uint LastRecordedAddress { get; init; }

    /// <summary>Whether the track has been damaged (Damage bit, byte 5 bit 5).</summary>
    public bool Damage { get; init; }

    /// <summary>Whether the track is a copy (Copy bit, byte 5 bit 4).</summary>
    public bool Copy { get; init; }

    /// <summary>Whether this is a reservation track (RT bit, byte 6 bit 5).</summary>
    public bool ReservationTrack { get; init; }
}

public sealed class TocData
{
    public byte FirstTrack { get; init; }
    public byte LastTrack { get; init; }
    public System.Collections.Generic.List<TocEntry> Entries { get; } = new();
}

public sealed class TocEntry
{
    public byte SessionNumber { get; init; }
    public byte Control { get; init; }
    public byte Adr { get; init; }
    public byte TrackNumber { get; init; }
    public uint StartLba { get; init; }
    public bool IsData => (Control & 0x04) != 0;
    public bool IsAudio => !IsData;
}

public sealed class AtipData
{
    public bool WritableDisc { get; init; }
    public bool IsRewritable { get; init; }
    public byte[] RawData { get; init; } = Array.Empty<byte>();
}

/// <summary>
/// Represents a format capacity descriptor from READ FORMAT CAPACITIES response.
/// Each descriptor describes a supported format type with the corresponding
/// Number of Blocks and Type Dependent Parameter values needed for FORMAT UNIT.
/// </summary>
public sealed class FormatCapacityDescriptor
{
    /// <summary>Number of user-addressable blocks for this format.</summary>
    public uint NumberOfBlocks { get; init; }

    /// <summary>
    /// Byte 4 of the descriptor: bits 7:2 = Format Type (pre-shifted),
    /// bits 1:0 = Format Sub-Type.
    /// For the current/maximum capacity descriptor (first one), bits 7:6
    /// indicate Descriptor Type (01=unformatted, 10=formatted, 11=no media).
    /// </summary>
    public byte FormatTypeByte { get; init; }

    /// <summary>Type-dependent parameter (3 bytes, big-endian).</summary>
    public uint TypeDependentParameter { get; init; }

    /// <summary>Whether this is the first descriptor (current/maximum capacity).</summary>
    public bool IsCurrentCapacity { get; init; }

    /// <summary>Extracts the format type from bits 7:2 (right-shifted to get the value).</summary>
    public byte FormatType => (byte)(FormatTypeByte >> 2);
}

/// <summary>
/// Represents sub-channel data returned by READ SUB-CHANNEL command (42h).
/// Contains audio status, current position, MCN, or ISRC depending on the format requested.
/// </summary>
public sealed class SubChannelData
{
    /// <summary>
    /// Audio Status:
    ///   0x00 = Audio status not supported,
    ///   0x11 = Audio play in progress,
    ///   0x12 = Audio play paused,
    ///   0x13 = Audio play completed successfully,
    ///   0x14 = Audio play stopped due to error,
    ///   0x15 = No current audio status to return.
    /// </summary>
    public byte AudioStatus { get; init; }

    /// <summary>Length of sub-channel data following the 4-byte header.</summary>
    public ushort SubChannelDataLength { get; init; }

    /// <summary>Sub-channel data format code that was requested.</summary>
    public byte Format { get; init; }

    // Current Position fields (format 01h)
    /// <summary>ADR field (sub-channel Q address type).</summary>
    public byte Adr { get; set; }
    /// <summary>Control field (track attributes: data/audio, copy permitted, etc.).</summary>
    public byte Control { get; set; }
    /// <summary>Current track number.</summary>
    public byte TrackNumber { get; set; }
    /// <summary>Current index number within the track.</summary>
    public byte IndexNumber { get; set; }
    /// <summary>Absolute CD address (LBA or MSF depending on MSF flag).</summary>
    public uint AbsoluteAddress { get; set; }
    /// <summary>Track-relative CD address (LBA or MSF).</summary>
    public uint TrackRelativeAddress { get; set; }

    // MCN fields (format 02h)
    /// <summary>Whether the Media Catalogue Number is valid.</summary>
    public bool McnValid { get; set; }
    /// <summary>Media Catalogue Number (UPC/EAN barcode), 13 ASCII characters.</summary>
    public string? MediaCatalogueNumber { get; set; }

    // ISRC fields (format 03h)
    /// <summary>Whether the ISRC is valid.</summary>
    public bool IsrcValid { get; set; }
    /// <summary>International Standard Recording Code, 12 ASCII characters.</summary>
    public string? Isrc { get; set; }
}

/// <summary>
/// Represents key data returned by REPORT KEY command (A4h).
/// Used for CSS/CPPM (DVD) and AACS (Blu-ray) authentication/key exchange.
/// </summary>
public sealed class ReportKeyData
{
    /// <summary>Data length from the response header (bytes following the 2-byte length field).</summary>
    public ushort DataLength { get; init; }

    /// <summary>Raw key data payload (after the 4-byte header).</summary>
    public byte[] KeyData { get; init; } = Array.Empty<byte>();
}

/// <summary>
/// Represents a Full TOC entry from READ TOC/PMA/ATIP format 2 (Full TOC).
/// Each entry corresponds to a descriptor in the Full TOC response.
/// Per MMC-5 §6.26, Full TOC entries contain MSF-encoded addresses.
/// </summary>
public sealed class FullTocEntry
{
    /// <summary>Session number for this descriptor.</summary>
    public byte SessionNumber { get; init; }
    /// <summary>ADR field (bits 7:4 of byte 1).</summary>
    public byte Adr { get; init; }
    /// <summary>Control field (bits 3:0 of byte 1).</summary>
    public byte Control { get; init; }
    /// <summary>TNO (track number reference, typically 0 for lead-in entries).</summary>
    public byte Tno { get; init; }
    /// <summary>POINT value — identifies the meaning of this descriptor.</summary>
    public byte Point { get; init; }
    /// <summary>Minute of the running time in the lead-in (PMIN).</summary>
    public byte Min { get; init; }
    /// <summary>Second of the running time in the lead-in (PSEC).</summary>
    public byte Sec { get; init; }
    /// <summary>Frame of the running time in the lead-in (PFRAME).</summary>
    public byte Frame { get; init; }
    /// <summary>PMIN — minutes component of the POINT-dependent address.</summary>
    public byte PMin { get; init; }
    /// <summary>PSEC — seconds component of the POINT-dependent address.</summary>
    public byte PSec { get; init; }
    /// <summary>PFRAME — frames component of the POINT-dependent address.</summary>
    public byte PFrame { get; init; }
}

/// <summary>
/// Represents PMA (Program Memory Area) data from READ TOC/PMA/ATIP format 3.
/// PMA contains a record of what has been written to CD-R/CD-RW media.
/// </summary>
public sealed class PmaData
{
    /// <summary>Raw PMA data including the 4-byte header.</summary>
    public byte[] RawData { get; init; } = Array.Empty<byte>();
    /// <summary>PMA descriptors parsed from the raw data.</summary>
    public System.Collections.Generic.List<PmaEntry> Entries { get; } = new();
}

/// <summary>
/// Represents a single PMA entry from READ TOC/PMA/ATIP format 3.
/// Layout is identical to a Full TOC entry (11 bytes per descriptor).
/// </summary>
public sealed class PmaEntry
{
    /// <summary>ADR field (bits 7:4 of byte 1).</summary>
    public byte Adr { get; init; }
    /// <summary>Control field (bits 3:0 of byte 1).</summary>
    public byte Control { get; init; }
    /// <summary>TNO (track number).</summary>
    public byte Tno { get; init; }
    /// <summary>POINT value.</summary>
    public byte Point { get; init; }
    /// <summary>Min field.</summary>
    public byte Min { get; init; }
    /// <summary>Sec field.</summary>
    public byte Sec { get; init; }
    /// <summary>Frame field.</summary>
    public byte Frame { get; init; }
    /// <summary>PMIN field.</summary>
    public byte PMin { get; init; }
    /// <summary>PSEC field.</summary>
    public byte PSec { get; init; }
    /// <summary>PFRAME field.</summary>
    public byte PFrame { get; init; }
}

/// <summary>
/// Represents the result of a READ HEADER command (44h).
/// Contains the data mode and the absolute CD-ROM address of the block.
/// </summary>
public sealed class ReadHeaderResult
{
    /// <summary>
    /// CD-ROM Data Mode:
    ///   0x00 = Mode 0 (all bytes zero — blank sector),
    ///   0x01 = Mode 1 (2048-byte user data with ECC),
    ///   0x02 = Mode 2 (2336-byte user data, no ECC).
    /// </summary>
    public byte DataMode { get; init; }

    /// <summary>
    /// Absolute CD-ROM address of the block in LBA or MSF format,
    /// depending on the MSF flag in the CDB.
    /// </summary>
    public uint AbsoluteAddress { get; init; }
}

/// <summary>
/// Represents DVD/BD copyright information from READ DISC STRUCTURE format 0x01.
/// </summary>
public sealed class DiscCopyrightInfo
{
    /// <summary>Copyright Protection System Type: 0=None, 1=CSS/CPPM, 2=CPRM.</summary>
    public byte CopyrightProtectionType { get; init; }
    /// <summary>Region management information (bitmask of prohibited regions).</summary>
    public byte RegionManagement { get; init; }
}

/// <summary>
/// Represents disc structure format list from READ DISC STRUCTURE format 0xFF.
/// Contains the list of format codes recognized by the drive for the specified media type.
/// </summary>
public sealed class DiscStructureFormatListEntry
{
    /// <summary>The format code supported by the drive.</summary>
    public byte FormatCode { get; init; }
    /// <summary>Whether SEND DISC STRUCTURE is available for this format code (SDS bit).</summary>
    public bool SendDiscStructureAvailable { get; init; }
    /// <summary>Whether READ DISC STRUCTURE is available for this format code (RDS bit).</summary>
    public bool ReadDiscStructureAvailable { get; init; }
}

/// <summary>
/// Represents a feature descriptor from GET CONFIGURATION response.
/// </summary>
public sealed class FeatureDescriptor
{
    /// <summary>Feature code (e.g. 0x0000=Profile List, 0x0021=Incremental Streaming Writable).</summary>
    public ushort FeatureCode { get; init; }
    /// <summary>Feature version number.</summary>
    public byte Version { get; init; }
    /// <summary>Whether the feature is persistent across resets.</summary>
    public bool Persistent { get; init; }
    /// <summary>Whether the feature is currently active/valid for the inserted media.</summary>
    public bool Current { get; init; }
    /// <summary>Additional feature-specific data (following the 4-byte feature header).</summary>
    public byte[] AdditionalData { get; init; } = Array.Empty<byte>();
}

/// <summary>
/// Represents event status data from GET EVENT STATUS NOTIFICATION.
/// </summary>
public sealed class EventStatusData
{
    /// <summary>Length of the event descriptor data following the header.</summary>
    public ushort EventDescriptorLength { get; init; }
    /// <summary>No Event Available — true if no pending event for the requested class.</summary>
    public bool NoEventAvailable { get; init; }
    /// <summary>Notification class of the returned event (0-7).</summary>
    public byte NotificationClass { get; init; }
    /// <summary>Bitmask of event classes supported by the drive.</summary>
    public byte SupportedEventClasses { get; init; }
    /// <summary>First byte of class-specific event data.</summary>
    public byte EventByte0 { get; set; }
    /// <summary>Second byte of class-specific event data.</summary>
    public byte EventByte1 { get; set; }
    /// <summary>Third byte of class-specific event data.</summary>
    public byte EventByte2 { get; set; }
    /// <summary>Fourth byte of class-specific event data.</summary>
    public byte EventByte3 { get; set; }
}

/// <summary>
/// Represents media event data from GET EVENT STATUS NOTIFICATION (media class).
/// </summary>
public sealed class MediaEventData
{
    /// <summary>
    /// Event code: 0=NoChange, 1=EjectRequested, 2=NewMedia, 3=MediaRemoval,
    /// 4=MediaChanged, 5=BGFormatCompleted, 6=BGFormatRestarted.
    /// </summary>
    public byte EventCode { get; init; }
    /// <summary>Whether media is present in the drive.</summary>
    public bool MediaPresent { get; init; }
    /// <summary>Whether the door/tray is open.</summary>
    public bool DoorTrayOpen { get; init; }
    /// <summary>Start slot (for changer mechanisms).</summary>
    public byte StartSlot { get; init; }
    /// <summary>End slot (for changer mechanisms).</summary>
    public byte EndSlot { get; init; }
}

/// <summary>
/// Represents mechanism status data from MECHANISM STATUS command.
/// </summary>
public sealed class MechanismStatusData
{
    /// <summary>Current slot number (for changer mechanisms, 0 for single drives).</summary>
    public byte CurrentSlot { get; init; }
    /// <summary>Whether a mechanical fault has been detected.</summary>
    public bool Fault { get; init; }
    /// <summary>Whether the changer state machine has changed since last check.</summary>
    public bool ChangerStateChanged { get; init; }
    /// <summary>Whether the drive door/tray is open.</summary>
    public bool DoorOpen { get; init; }
    /// <summary>
    /// Mechanism state: 0=Idle, 1=Playing, 2=Scanning, 3=Host Selected Active.
    /// </summary>
    public byte MechanismState { get; init; }
    /// <summary>Current LBA position of the optical pickup (24-bit).</summary>
    public uint CurrentLba { get; init; }
    /// <summary>Number of slots available (for changer mechanisms).</summary>
    public byte NumberOfSlots { get; init; }
    /// <summary>Length of the slot table data in bytes.</summary>
    public ushort SlotTableLength { get; init; }
}

/// <summary>
/// Represents a single parameter from a LOG SENSE error counter page.
/// </summary>
public sealed class LogParameter
{
    /// <summary>Parameter code identifying this counter.</summary>
    public ushort ParameterCode { get; init; }
    /// <summary>The counter value.</summary>
    public ulong Value { get; init; }
    /// <summary>Parameter control flags byte.</summary>
    public byte Flags { get; init; }
}
