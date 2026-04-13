// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;

namespace OpenBurningSuite.Native.Scsi;

/// <summary>Result of a SCSI command execution.</summary>
public sealed class ScsiResult
{
    /// <summary>SCSI status byte (0x00 = GOOD, 0x02 = CHECK CONDITION, etc.).</summary>
    public byte Status { get; init; }

    /// <summary>Sense data returned by the device (if any).</summary>
    public byte[] SenseData { get; init; } = Array.Empty<byte>();

    /// <summary>Number of data bytes actually transferred.</summary>
    public int DataTransferred { get; init; }

    /// <summary>Whether the command succeeded (SCSI status GOOD).</summary>
    public bool Success => Status == 0x00;

    /// <summary>
    /// Sense key from sense data (if available).
    /// Supports both fixed-format (0x70/0x71) and descriptor-format (0x72/0x73) sense data.
    /// </summary>
    public byte SenseKey
    {
        get
        {
            if (SenseData.Length < 2) return 0;
            var responseCode = (byte)(SenseData[0] & 0x7F);
            return responseCode switch
            {
                // Fixed-format sense data (0x70/0x71): sense key is in byte 2 bits 3:0
                0x70 or 0x71 => SenseData.Length > 2 ? (byte)(SenseData[2] & 0x0F) : (byte)0,
                // Descriptor-format sense data (0x72/0x73): sense key is in byte 1 bits 3:0
                0x72 or 0x73 => (byte)(SenseData[1] & 0x0F),
                _ => SenseData.Length > 2 ? (byte)(SenseData[2] & 0x0F) : (byte)0
            };
        }
    }

    /// <summary>
    /// Additional Sense Code from sense data.
    /// Supports both fixed-format and descriptor-format sense data.
    /// </summary>
    public byte Asc
    {
        get
        {
            if (SenseData.Length < 2) return 0;
            var responseCode = (byte)(SenseData[0] & 0x7F);
            return responseCode switch
            {
                // Fixed-format: ASC is at byte 12
                0x70 or 0x71 => SenseData.Length > 12 ? SenseData[12] : (byte)0,
                // Descriptor-format: ASC is at byte 2.
                // Per SPC-5, descriptor-format minimum header is 8 bytes,
                // but ASC at byte 2 is valid if the data extends that far.
                0x72 or 0x73 => SenseData.Length >= 3 ? SenseData[2] : (byte)0,
                _ => SenseData.Length > 12 ? SenseData[12] : (byte)0
            };
        }
    }

    /// <summary>
    /// Additional Sense Code Qualifier from sense data.
    /// Supports both fixed-format and descriptor-format sense data.
    /// </summary>
    public byte Ascq
    {
        get
        {
            if (SenseData.Length < 4) return 0;
            var responseCode = (byte)(SenseData[0] & 0x7F);
            return responseCode switch
            {
                // Fixed-format: ASCQ is at byte 13
                0x70 or 0x71 => SenseData.Length > 13 ? SenseData[13] : (byte)0,
                // Descriptor-format: ASCQ is at byte 3
                0x72 or 0x73 => SenseData.Length > 3 ? SenseData[3] : (byte)0,
                _ => SenseData.Length > 13 ? SenseData[13] : (byte)0
            };
        }
    }

    /// <summary>Returns a human-readable error description based on sense data.</summary>
    public string ErrorDescription
    {
        get
        {
            if (Success) return "OK";

            // Status=0xFF indicates an IOCTL/transport failure, not a SCSI error from the drive.
            // The platform transport stores platform-specific error info in the sense buffer:
            //   Windows: Win32 error code in first 4 bytes (little-endian)
            //   Linux: HostStatus(2) + DriverStatus(2) in first 4 bytes
            if (Status == 0xFF && SenseData.Length >= 4)
            {
                var errorCode = BitConverter.ToInt32(SenseData, 0);
                var errorDesc = DecodeTransportError(errorCode);
                return $"Transport Error: IOCTL failed (code={errorCode}" +
                       (string.IsNullOrEmpty(errorDesc) ? ")" : $", {errorDesc})");
            }

            var senseDesc = DecodeSenseKey(SenseKey);
            var ascDesc = DecodeAsc(Asc, Ascq);
            return $"SCSI Error: Status=0x{Status:X2}, SenseKey=0x{SenseKey:X2} ({senseDesc}), " +
                   $"ASC=0x{Asc:X2}, ASCQ=0x{Ascq:X2}" +
                   (string.IsNullOrEmpty(ascDesc) ? "" : $" ({ascDesc})");
        }
    }

    /// <summary>Decodes common Win32/transport error codes to human-readable descriptions.</summary>
    private static string DecodeTransportError(int code) => code switch
    {
        1 => "Invalid function",
        2 => "File not found",
        5 => "Access denied",
        6 => "Invalid handle",
        21 => "Device not ready",
        22 => "Bad command",
        23 => "CRC error",
        87 => "Invalid parameter — transfer size may exceed adapter limit",
        121 => "Semaphore timeout",
        1117 => "I/O device error",
        1167 => "Device not connected",
        _ => ""
    };

    /// <summary>Decodes a SCSI sense key to a human-readable string.</summary>
    private static string DecodeSenseKey(byte key) => key switch
    {
        0x00 => "No Sense",
        0x01 => "Recovered Error",
        0x02 => "Not Ready",
        0x03 => "Medium Error",
        0x04 => "Hardware Error",
        0x05 => "Illegal Request",
        0x06 => "Unit Attention",
        0x07 => "Data Protect",
        0x08 => "Blank Check",
        0x09 => "Vendor Specific",
        0x0A => "Copy Aborted",
        0x0B => "Aborted Command",
        0x0C => "Equal (Obsolete)",
        0x0D => "Volume Overflow",
        0x0E => "Miscompare",
        _ => "Unknown"
    };

    /// <summary>Decodes common ASC/ASCQ combinations for optical drives.</summary>
    private static string DecodeAsc(byte asc, byte ascq) => (asc, ascq) switch
    {
        (0x00, 0x00) => "",
        (0x02, 0x00) => "Not Ready, cause not reportable",
        (0x02, 0x06) => "Logical unit not ready, format in progress",
        (0x04, 0x00) => "Logical unit not ready, cause not reportable",
        (0x04, 0x01) => "Logical unit is in process of becoming ready",
        (0x04, 0x02) => "Logical unit not ready, initializing command required",
        (0x04, 0x03) => "Logical unit not ready, manual intervention required",
        (0x04, 0x04) => "Logical unit not ready, format in progress",
        (0x04, 0x07) => "Logical unit not ready, operation in progress",
        (0x04, 0x08) => "Logical unit not ready, long write in progress",
        (0x04, 0x09) => "Logical unit not ready, self-test in progress",
        (0x05, 0x00) => "Logical unit does not respond to selection",
        (0x06, 0x00) => "No reference position found",
        (0x09, 0x00) => "Track following error",
        (0x09, 0x01) => "Tracking servo failure",
        (0x09, 0x02) => "Focus servo failure",
        (0x09, 0x03) => "Spindle servo failure",
        (0x0C, 0x00) => "Write error",
        (0x0C, 0x02) => "Write error, auto reallocation failed",
        (0x0C, 0x07) => "Write error, recovery needed",
        (0x0C, 0x08) => "Write error, recovery failed",
        (0x0C, 0x09) => "Write error, loss of streaming",
        (0x0C, 0x0A) => "Write error, padding blocks added",
        (0x0C, 0x0F) => "Defects in error window",
        (0x11, 0x00) => "Unrecovered read error",
        (0x11, 0x01) => "Read retries exhausted",
        (0x11, 0x02) => "Error too long to correct",
        (0x11, 0x05) => "L-EC uncorrectable error",
        (0x11, 0x06) => "CIRC unrecovered error",
        (0x11, 0x0F) => "Error reading UPC/EAN number",
        (0x11, 0x10) => "Error reading ISRC number",
        (0x14, 0x00) => "Recorded entity not found",
        (0x14, 0x01) => "Record not found",
        (0x15, 0x00) => "Random positioning error",
        (0x15, 0x01) => "Mechanical positioning error",
        (0x15, 0x02) => "Positioning error detected by read of medium",
        (0x17, 0x00) => "Recovered data with no error correction applied",
        (0x17, 0x01) => "Recovered data with retries",
        (0x18, 0x00) => "Recovered data with error correction applied",
        (0x1A, 0x00) => "Parameter list length error",
        (0x20, 0x00) => "Invalid command operation code",
        (0x21, 0x00) => "Logical block address out of range",
        (0x24, 0x00) => "Invalid field in CDB",
        (0x25, 0x00) => "Logical unit not supported",
        (0x26, 0x00) => "Invalid field in parameter list",
        (0x26, 0x01) => "Parameter not supported",
        (0x26, 0x02) => "Parameter value invalid",
        (0x27, 0x00) => "Write protected",
        (0x28, 0x00) => "Not ready to ready change, medium may have changed",
        (0x29, 0x00) => "Power on, reset, or bus device reset occurred",
        (0x29, 0x01) => "Power on occurred",
        (0x29, 0x02) => "SCSI bus reset occurred",
        (0x29, 0x03) => "Bus device reset function occurred",
        (0x2A, 0x00) => "Parameters changed",
        (0x2A, 0x01) => "Mode parameters changed",
        (0x2C, 0x00) => "Command sequence error",
        (0x30, 0x00) => "Incompatible medium installed",
        (0x30, 0x01) => "Cannot read medium, unknown format",
        (0x30, 0x02) => "Cannot read medium, incompatible format",
        (0x30, 0x04) => "Cannot write medium, unknown format",
        (0x30, 0x05) => "Cannot write medium, incompatible format",
        (0x30, 0x06) => "Cannot format medium, incompatible medium",
        (0x30, 0x07) => "Cleaning failure",
        (0x30, 0x08) => "Cannot write, application code mismatch",
        (0x30, 0x0C) => "WORM medium, overwrite attempted",
        (0x30, 0x10) => "Medium not formatted",
        (0x30, 0x11) => "Medium formatted with defect list errors",
        (0x31, 0x00) => "Medium format corrupted",
        (0x31, 0x01) => "Format command failed",
        (0x31, 0x02) => "Zoned formatting failed due to spare linking",
        (0x32, 0x00) => "No defect spare location available",
        (0x32, 0x01) => "Defect list update failure",
        (0x3A, 0x00) => "Medium not present",
        (0x3A, 0x01) => "Medium not present, tray closed",
        (0x3A, 0x02) => "Medium not present, tray open",
        (0x3B, 0x0E) => "Too many target descriptors",
        (0x3D, 0x00) => "Invalid bits in identify message",
        (0x3E, 0x00) => "Logical unit has not self-configured yet",
        (0x3F, 0x00) => "Target operating conditions have changed",
        (0x3F, 0x01) => "Microcode has been changed",
        (0x3F, 0x02) => "Changed operating definition",
        (0x44, 0x00) => "Internal target failure",
        (0x47, 0x00) => "SCSI parity error",
        (0x4E, 0x00) => "Overlapped commands attempted",
        (0x51, 0x00) => "Erase failure",
        (0x51, 0x01) => "Erase failure, incomplete erase operation detected",
        (0x52, 0x00) => "Cartridge fault",
        (0x53, 0x00) => "Media load or eject failed",
        (0x53, 0x02) => "Medium removal prevented",
        (0x55, 0x00) => "System resource failure",
        (0x55, 0x04) => "Insufficient registration resources",
        (0x57, 0x00) => "Unable to recover table of contents",
        (0x5A, 0x00) => "Operator request or state change input",
        (0x5A, 0x01) => "Operator medium removal request",
        (0x5D, 0x00) => "Failure prediction threshold exceeded",
        (0x63, 0x00) => "End of user area encountered on this track",
        (0x63, 0x01) => "Packet does not fit in available space",
        (0x64, 0x00) => "Illegal mode for this track",
        (0x64, 0x01) => "Invalid packet size",
        (0x6F, 0x00) => "Copy protection key exchange failure, authentication failure",
        (0x6F, 0x01) => "Copy protection key exchange failure, key not present",
        (0x6F, 0x02) => "Copy protection key exchange failure, key not established",
        (0x6F, 0x03) => "Read of scrambled sector without authentication",
        (0x6F, 0x04) => "Media region code is mismatched to logical unit region",
        (0x6F, 0x05) => "Drive region counter exhausted",
        (0x72, 0x00) => "Session fixation error",
        (0x72, 0x03) => "Session fixation error, incomplete track in session",
        (0x72, 0x04) => "Empty or partially written reserved track",
        (0x73, 0x00) => "CD control error",
        (0x73, 0x02) => "Power calibration area almost full",
        (0x73, 0x03) => "Power calibration area is full",
        (0x73, 0x04) => "Power calibration area error",
        (0x73, 0x05) => "Power calibration area almost full (write protect imminent)",
        _ => ""
    };
}
