// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OpenBurningSuite.Native.Scsi;

/// <summary>
/// Windows implementation of IScsiTransport using IOCTL_SCSI_PASS_THROUGH_DIRECT.
/// Sends SCSI commands via DeviceIoControl on \\.\X: device handles.
/// </summary>
public sealed class WindowsScsiTransport : IScsiTransport
{
    // IOCTL_SCSI_PASS_THROUGH_DIRECT = CTL_CODE(IOCTL_SCSI_BASE=0x04, 0x0405, METHOD_BUFFERED=0, FILE_READ_ACCESS|FILE_WRITE_ACCESS=3)
    private const uint IOCTL_SCSI_PASS_THROUGH_DIRECT = 0x4D014;
    private const byte SCSI_IOCTL_DATA_OUT = 0;
    private const byte SCSI_IOCTL_DATA_IN = 1;
    private const byte SCSI_IOCTL_DATA_UNSPECIFIED = 2;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_SHARE_READ = 1;
    private const uint FILE_SHARE_WRITE = 2;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    // -----------------------------------------------------------------------
    // IOCTL_CDROM_EXCLUSIVE_ACCESS
    // -----------------------------------------------------------------------
    // Per Windows WDK ntddcdrm.h:
    //   IOCTL_CDROM_EXCLUSIVE_ACCESS = CTL_CODE(IOCTL_CDROM_BASE=0x24, 0x000E, METHOD_BUFFERED=0, FILE_READ_ACCESS|FILE_WRITE_ACCESS=3)
    //   = (0x24 << 16) | (3 << 14) | (0x000E << 2) | 0 = 0x2400C038
    //
    // This IOCTL tells cdrom.sys that the caller is taking exclusive control of
    // the CD-ROM device. While exclusive access is held:
    //   - The OS stops sending periodic "Check Event" (GET EVENT STATUS NOTIFICATION)
    //     commands that poll for media changes, which can interrupt write timing
    //   - Other applications cannot open the device for read/write access
    //   - The storage stack won't interfere with SCSI passthrough commands
    //
    // The IOCTL uses a CDROM_EXCLUSIVE_ACCESS structure:
    //   struct CDROM_EXCLUSIVE_ACCESS {
    //       BOOLEAN RequestType;     // 0 = query, 1 = lock, 2 = unlock
    //       ULONG   CallerName[64];  // UTF-16 caller ID string (zero-terminated, max 64 WCHARs)
    //   }
    //
    // For locking: RequestType = 1 (ExclusiveAccessLockDevice)
    // For unlocking: RequestType = 2 (ExclusiveAccessUnlockDevice)
    // -----------------------------------------------------------------------
    private const uint IOCTL_CDROM_EXCLUSIVE_ACCESS = 0x2400C038;

    // CDROM_EXCLUSIVE_ACCESS RequestType values
    private const byte ExclusiveAccessQueryState = 0;
    private const byte ExclusiveAccessLockDevice = 1;
    private const byte ExclusiveAccessUnlockDevice = 2;

    private bool _hasCdromExclusiveAccess;

    private SafeFileHandle? _handle;

    public bool IsOpen => _handle != null && !_handle.IsInvalid && !_handle.IsClosed;

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice, uint dwIoControlCode,
        byte[] lpInBuffer, uint nInBufferSize,
        byte[] lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    /// <summary>
    /// SCSI_PASS_THROUGH_DIRECT structure layout (offset-based).
    /// We use raw byte arrays to avoid alignment issues across platforms.
    ///
    /// Windows SCSI_PASS_THROUGH_DIRECT layout per WinIoCtl.h:
    ///   USHORT Length;           // +0  (2 bytes)
    ///   UCHAR  ScsiStatus;      // +2  (1 byte)
    ///   UCHAR  PathId;          // +3  (1 byte)
    ///   UCHAR  TargetId;        // +4  (1 byte)
    ///   UCHAR  Lun;             // +5  (1 byte)
    ///   UCHAR  CdbLength;       // +6  (1 byte)
    ///   UCHAR  SenseInfoLength; // +7  (1 byte)
    ///   UCHAR  DataIn;          // +8  (1 byte)
    ///   -- 3 bytes padding to align DataTransferLength on 4-byte boundary --
    ///   ULONG  DataTransferLength; // +12 (4 bytes)
    ///   ULONG  TimeOutValue;       // +16 (4 bytes)
    ///   PVOID  DataBufferOffset;   // +20 on 32-bit, +24 on 64-bit (pointer-size aligned)
    ///   ULONG  SenseInfoOffset;    // follows DataBufferOffset
    ///   UCHAR  Cdb[16];           // follows SenseInfoOffset
    /// </summary>
    private static class SptdOffsets
    {
        // For 64-bit systems, the structure has different alignment
        public static readonly bool Is64Bit = IntPtr.Size == 8;

        public const int Length = 0;          // USHORT (2)
        public const int ScsiStatus = 2;      // UCHAR (1)
        public const int PathId = 3;          // UCHAR (1)
        public const int TargetId = 4;        // UCHAR (1)
        public const int Lun = 5;             // UCHAR (1)
        public const int CdbLength = 6;       // UCHAR (1)
        public const int SenseInfoLength = 7; // UCHAR (1)
        public const int DataIn = 8;          // UCHAR (1)
        // 3 bytes padding to align DataTransferLength on 4-byte boundary
        public const int DataTransferLength = 12; // ULONG (4)
        public const int TimeOutValue = 16;       // ULONG (4)
        // DataBuffer pointer: must be pointer-aligned.
        // On 32-bit: offset 20 (4-byte aligned after TimeOutValue at 16+4=20).
        // On 64-bit: offset 24 (8-byte aligned; 3 padding bytes after TimeOutValue end at 20).
        public static int DataBuffer => Is64Bit ? 24 : 20;
        // SenseInfoOffset: ULONG (4 bytes) immediately after the pointer
        public static int SenseInfoOffset => DataBuffer + IntPtr.Size;
        // CDB starts after SenseInfoOffset (4 bytes)
        public static int CdbStart => SenseInfoOffset + 4;
        // Raw content size is CdbStart + 16 (CDB), but the struct must be padded
        // to a multiple of its alignment (pointer size) to match sizeof() on Windows.
        // On x64: content=52, alignment=8, sizeof=56.
        // On x86: content=44, alignment=4, sizeof=44 (already aligned).
        // Windows validates Length == sizeof(SCSI_PASS_THROUGH_DIRECT), so this
        // must be exact or DeviceIoControl fails with STATUS_REVISION_MISMATCH.
        public static int HeaderSize
        {
            get
            {
                int raw = CdbStart + 16; // 16 bytes for CDB
                int alignment = IntPtr.Size;
                return (raw + alignment - 1) / alignment * alignment;
            }
        }
        // SPC-5 allows up to 252 bytes of sense data. Use the full size to ensure
        // extended sense information (descriptor format) is captured completely.
        public const int SenseBufferSize = 252;
        public static int TotalSize => HeaderSize + SenseBufferSize;
    }

    public void Open(string devicePath)
    {
        _handle?.Dispose();

        // Convert "D:\" or "D:" to "\\.\D:" format
        var winPath = devicePath;
        if (devicePath.Length >= 2 && devicePath[1] == ':')
        {
            winPath = $@"\\.\{devicePath[0]}:";
        }

        // Try GENERIC_READ | GENERIC_WRITE first (required for write commands).
        // Fall back to GENERIC_READ if read/write fails — this allows SCSI passthrough
        // for read-only operations (INQUIRY, TEST UNIT READY, GET CONFIGURATION, etc.)
        // without requiring administrator privileges.
        _handle = CreateFile(
            winPath,
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL,
            IntPtr.Zero);

        if (_handle.IsInvalid)
        {
            // Dispose the invalid handle before retrying with read-only access
            _handle.Dispose();

            // Retry with read-only access
            _handle = CreateFile(
                winPath,
                GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_ATTRIBUTE_NORMAL,
                IntPtr.Zero);
        }

        if (_handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(
                $"Failed to open SCSI device '{devicePath}': Win32 error={error}");
        }
    }

    public ScsiResult Execute(ScsiCommand command)
    {
        if (!IsOpen)
            throw new InvalidOperationException("SCSI transport is not open.");

        var buffer = new byte[SptdOffsets.TotalSize];

        // Fill SCSI_PASS_THROUGH_DIRECT header
        BitConverter.GetBytes((ushort)SptdOffsets.HeaderSize).CopyTo(buffer, SptdOffsets.Length);
        buffer[SptdOffsets.CdbLength] = (byte)command.Cdb.Length;
        buffer[SptdOffsets.SenseInfoLength] = SptdOffsets.SenseBufferSize;
        buffer[SptdOffsets.DataIn] = command.Direction switch
        {
            ScsiDataDirection.In => SCSI_IOCTL_DATA_IN,
            ScsiDataDirection.Out => SCSI_IOCTL_DATA_OUT,
            _ => SCSI_IOCTL_DATA_UNSPECIFIED
        };

        BitConverter.GetBytes((uint)command.DataBuffer.Length).CopyTo(buffer, SptdOffsets.DataTransferLength);
        // Convert timeout from milliseconds to seconds with ceiling rounding;
        // ensure at least 1-second minimum since a 0-second timeout on Windows SPTD
        // means no timeout (infinite wait).
        var timeoutSec = Math.Max(1u, (uint)((command.TimeoutMs + 999) / 1000));
        BitConverter.GetBytes(timeoutSec).CopyTo(buffer, SptdOffsets.TimeOutValue);
        BitConverter.GetBytes((uint)SptdOffsets.HeaderSize).CopyTo(buffer, SptdOffsets.SenseInfoOffset);

        // Copy CDB — SCSI_PASS_THROUGH_DIRECT supports up to 16-byte CDBs
        if (command.Cdb.Length > 16)
            throw new InvalidOperationException(
                $"CDB length {command.Cdb.Length} exceeds the 16-byte limit of Windows SCSI_PASS_THROUGH_DIRECT.");

        Array.Copy(command.Cdb, 0, buffer, SptdOffsets.CdbStart, command.Cdb.Length);

        GCHandle dataHandle = default;
        try
        {
            if (command.DataBuffer.Length > 0)
            {
                dataHandle = GCHandle.Alloc(command.DataBuffer, GCHandleType.Pinned);
                var dataPtr = dataHandle.AddrOfPinnedObject();
                if (SptdOffsets.Is64Bit)
                    BitConverter.GetBytes(dataPtr.ToInt64()).CopyTo(buffer, SptdOffsets.DataBuffer);
                else
                    BitConverter.GetBytes(dataPtr.ToInt32()).CopyTo(buffer, SptdOffsets.DataBuffer);
            }

            var ok = DeviceIoControl(
                _handle!,
                IOCTL_SCSI_PASS_THROUGH_DIRECT,
                buffer, (uint)buffer.Length,
                buffer, (uint)buffer.Length,
                out _,
                IntPtr.Zero);

            if (!ok)
            {
                var error = Marshal.GetLastWin32Error();
                // Provide the Win32 error code in sense data for diagnostics.
                // Store as 4-byte little-endian in a minimal sense buffer so
                // ScsiResult.ErrorDescription can still show meaningful info.
                var errorSense = new byte[4];
                BitConverter.GetBytes(error).CopyTo(errorSense, 0);
                return new ScsiResult
                {
                    Status = 0xFF,
                    SenseData = errorSense,
                    DataTransferred = 0
                };
            }

            // Extract results
            var scsiStatus = buffer[SptdOffsets.ScsiStatus];
            var rawSense = new byte[SptdOffsets.SenseBufferSize];
            Array.Copy(buffer, SptdOffsets.HeaderSize, rawSense, 0, SptdOffsets.SenseBufferSize);

            // Parse actual sense data length from the response format rather than
            // returning the full 252-byte buffer (which wastes memory and includes
            // trailing zeros). Per SPC-5, sense data has a well-defined length field.
            var trimmedSense = TrimSenseData(rawSense);

            // After DeviceIoControl, the DataTransferLength field in the SPTD structure
            // contains the actual number of bytes transferred (Windows updates it in-place).
            // For SCSI_PASS_THROUGH_DIRECT, Windows writes back the actual transferred length.
            var dataTransferred = (int)BitConverter.ToUInt32(buffer, SptdOffsets.DataTransferLength);
            // Clamp to the original buffer size. If the device reports more bytes than our
            // buffer can hold, this may indicate a firmware bug or data corruption — log it.
            if (command.DataBuffer.Length > 0)
            {
                if (dataTransferred > command.DataBuffer.Length)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[SCSI] WARNING: Device reported {dataTransferred} bytes transferred " +
                        $"but buffer size is {command.DataBuffer.Length}. Clamping to buffer size.");
                }
                dataTransferred = Math.Min(dataTransferred, command.DataBuffer.Length);
            }

            return new ScsiResult
            {
                Status = scsiStatus,
                SenseData = trimmedSense,
                DataTransferred = dataTransferred
            };
        }
        finally
        {
            if (dataHandle.IsAllocated) dataHandle.Free();
        }
    }

    // -----------------------------------------------------------------------
    // Volume lock/dismount for destructive operations
    // -----------------------------------------------------------------------

    // FSCTL_LOCK_VOLUME: locks the volume so no other process can access it.
    // Required before FORMAT UNIT / BLANK to prevent Windows' storage filter
    // driver from blocking the SCSI passthrough command on mounted volumes.
    private const uint FSCTL_LOCK_VOLUME = 0x00090018;
    // FSCTL_DISMOUNT_VOLUME: dismounts the file system on the volume.
    // After dismount, the volume is treated as raw — SCSI commands pass through.
    private const uint FSCTL_DISMOUNT_VOLUME = 0x00090020;

    /// <summary>
    /// Prepares the device for destructive SCSI operations (FORMAT UNIT, BLANK, WRITE).
    /// On Windows, performs three operations:
    ///   1. FSCTL_LOCK_VOLUME — locks the volume so no other process can access it
    ///   2. FSCTL_DISMOUNT_VOLUME — dismounts the file system, treating the volume as raw
    ///   3. IOCTL_CDROM_EXCLUSIVE_ACCESS — takes exclusive control of the CD-ROM device,
    ///      preventing cdrom.sys from sending periodic media-change polling commands
    ///      ("Check Event" / GET EVENT STATUS NOTIFICATION) that can interrupt write timing
    ///
    /// Without these, FORMAT UNIT / BLANK on a mounted BD-RE / DVD+RW / etc. returns
    /// "Invalid field in CDB" (ASC=0x24) because Windows intercepts the command, and
    /// periodic polling from cdrom.sys can cause buffer underruns during writing.
    /// </summary>
    public bool PrepareForWrite()
    {
        if (!IsOpen) return false;

        // Lock the volume — tells Windows no other process should access it.
        // This may fail if another process has an open handle (e.g., Explorer).
        // We retry a few times because the OS may take a moment to release handles.
        bool locked = false;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var lockOk = DeviceIoControl(
                _handle!,
                FSCTL_LOCK_VOLUME,
                Array.Empty<byte>(), 0,
                Array.Empty<byte>(), 0,
                out _,
                IntPtr.Zero);

            if (lockOk)
            {
                locked = true;
                break;
            }

            // Brief pause before retry — allow Explorer/antivirus to release handles
            System.Threading.Thread.Sleep(500);
        }

        // Dismount the volume — removes the filesystem from the device.
        // This is critical: even if LOCK failed (e.g., open handles exist),
        // DISMOUNT can still succeed and allows SCSI passthrough to work.
        var dismountOk = DeviceIoControl(
            _handle!,
            FSCTL_DISMOUNT_VOLUME,
            Array.Empty<byte>(), 0,
            Array.Empty<byte>(), 0,
            out _,
            IntPtr.Zero);

        // Acquire IOCTL_CDROM_EXCLUSIVE_ACCESS to prevent cdrom.sys from polling.
        // Per Windows WDK documentation, the CDROM_EXCLUSIVE_ACCESS structure is:
        //   Byte 0:     RequestType (1 = Lock)
        //   Bytes 1-3:  Padding (for ULONG alignment)
        //   Bytes 4+:   CallerName — null-terminated UTF-16LE string identifying the caller
        //               (max 64 WCHARs = 128 bytes). Used by the driver for diagnostics.
        //
        // The total structure size is 4 bytes (header) + 128 bytes (CallerName) = 132 bytes.
        // Some versions of cdrom.sys validate the minimum input buffer size.
        var exclusiveOk = TryCdromExclusiveAccess(ExclusiveAccessLockDevice);
        if (exclusiveOk)
            _hasCdromExclusiveAccess = true;

        // Return true if either volume operation succeeded — some drives work with
        // just dismount even without a full lock. CDROM exclusive access is a bonus.
        return locked || dismountOk;
    }

    /// <summary>
    /// Sends IOCTL_CDROM_EXCLUSIVE_ACCESS to cdrom.sys to lock or unlock exclusive access.
    /// This prevents the OS from sending periodic "Check Event" (GET EVENT STATUS NOTIFICATION)
    /// commands that poll for media changes, which can interrupt burn timing.
    /// </summary>
    /// <param name="requestType">1 = lock, 2 = unlock.</param>
    /// <returns>True if the IOCTL succeeded.</returns>
    private bool TryCdromExclusiveAccess(byte requestType)
    {
        if (!IsOpen) return false;

        try
        {
            // CDROM_EXCLUSIVE_ACCESS layout:
            //   offset 0: BOOLEAN RequestType (1 byte, padded to ULONG alignment = 4 bytes)
            //   offset 4: WCHAR CallerName[64] (128 bytes)
            // Total: 132 bytes
            const int structSize = 132;
            var input = new byte[structSize];
            input[0] = requestType;

            // Write caller name as UTF-16LE at offset 4
            // "OpenBurningSuite" = 16 chars × 2 bytes = 32 bytes
            var callerName = System.Text.Encoding.Unicode.GetBytes("OpenBurningSuite");
            var nameLen = Math.Min(callerName.Length, 126); // Leave room for null terminator
            Array.Copy(callerName, 0, input, 4, nameLen);
            // Null terminator already present since array is zero-initialized

            return DeviceIoControl(
                _handle!,
                IOCTL_CDROM_EXCLUSIVE_ACCESS,
                input, (uint)input.Length,
                input, (uint)input.Length,
                out _,
                IntPtr.Zero);
        }
        catch
        {
            // IOCTL_CDROM_EXCLUSIVE_ACCESS may not be supported on all Windows versions
            // or on USB optical drives. Failure is non-fatal.
            return false;
        }
    }

    public void Dispose()
    {
        // Release CDROM exclusive access before closing the handle.
        // This restores normal cdrom.sys polling behavior so the OS can
        // detect media changes after we're done.
        if (_hasCdromExclusiveAccess)
        {
            TryCdromExclusiveAccess(ExclusiveAccessUnlockDevice);
            _hasCdromExclusiveAccess = false;
        }

        _handle?.Dispose();
        _handle = null;
    }

    /// <summary>
    /// Determines the actual sense data length by parsing the SPC-5 response format,
    /// rather than returning the full 252-byte buffer with trailing zeros.
    /// Matches the sense data parsing logic used in MacOsScsiTransport.CopySenseData.
    /// </summary>
    private static byte[] TrimSenseData(byte[] raw)
    {
        if (raw.Length < 1 || raw[0] == 0)
            return Array.Empty<byte>();

        var responseCode = (byte)(raw[0] & 0x7F);
        int actualLen;

        switch (responseCode)
        {
            case 0x70:
            case 0x71:
            case 0x72:
            case 0x73:
                // Fixed-format (0x70/0x71) and descriptor-format (0x72/0x73) sense data
                // both use the same layout for the length field per SPC-5:
                // Byte 7 = Additional Sense Length (bytes after byte 7)
                // Total length = 8 + additional_sense_length
                actualLen = raw.Length >= 8
                    ? Math.Min(8 + raw[7], raw.Length)
                    : Math.Min(8, raw.Length);
                break;

            default:
                // Unknown format — trim trailing zeros
                actualLen = raw.Length;
                while (actualLen > 0 && raw[actualLen - 1] == 0) actualLen--;
                break;
        }

        if (actualLen <= 0)
            return Array.Empty<byte>();

        return raw[..actualLen];
    }
}
