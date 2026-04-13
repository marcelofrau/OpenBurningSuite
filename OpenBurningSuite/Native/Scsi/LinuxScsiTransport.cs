// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Runtime.InteropServices;

namespace OpenBurningSuite.Native.Scsi;

/// <summary>
/// Linux implementation of IScsiTransport using the SG_IO ioctl interface.
/// Sends SCSI commands directly to /dev/srN or /dev/sgN devices.
/// </summary>
public sealed class LinuxScsiTransport : IScsiTransport
{
    // ioctl request code for SG_IO
    private const uint SG_IO = 0x2285;
    private const int SG_INTERFACE_ID = (int)'S';

    // Data transfer directions
    private const int SG_DXFER_NONE      = -1;
    private const int SG_DXFER_TO_DEV    = -2;
    private const int SG_DXFER_FROM_DEV  = -3;

    // open() flags — values are the same on all .NET-supported Linux architectures (x64, arm64)
    private const int O_RDONLY   = 0x0000;
    private const int O_RDWR     = 0x0002;
    // O_NONBLOCK is 0x0800 on x64 and arm64 (differs on mips/sparc, but .NET doesn't target those).
    private const int O_NONBLOCK = 0x0800;

    private int _fd = -1;
    private string? _devicePath;

    public bool IsOpen => _fd >= 0;

    [DllImport("libc", SetLastError = true)]
    private static extern int open([MarshalAs(UnmanagedType.LPStr)] string pathname, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, uint request, ref SgIoHdr hdr);

    /// <summary>
    /// The sg_io_hdr structure for Linux SG_IO ioctl.
    /// Must match the kernel's struct sg_io_hdr layout exactly.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SgIoHdr
    {
        public int InterfaceId;        // [i] 'S'
        public int DxferDirection;     // [i] data transfer direction
        public byte CmdLen;            // [i] SCSI command length
        public byte MxSbLen;           // [i] max sense buffer length
        public ushort IovecCount;      // [i] 0 = no scatter gather
        public uint DxferLen;          // [i] byte count of data transfer
        public IntPtr Dxferp;          // [i] data buffer pointer
        public IntPtr Cmdp;            // [i] CDB pointer
        public IntPtr Sbp;             // [i] sense buffer pointer
        public uint Timeout;           // [i] timeout in ms
        public uint Flags;             // [i] 0 = default
        public int PackId;             // [i->o] unused
        public IntPtr UsrPtr;          // [i->o] unused
        public byte Status;            // [o] SCSI status
        public byte MaskedStatus;      // [o]
        public byte MsgStatus;         // [o]
        public byte SbLenWr;           // [o] sense bytes written
        public ushort HostStatus;      // [o]
        public ushort DriverStatus;    // [o]
        public int Resid;              // [o] residual count
        public uint Duration;          // [o] time taken (ms)
        public uint Info;              // [o]
    }

    [DllImport("libc", EntryPoint = "strerror")]
    private static extern IntPtr strerror_native(int errnum);

    public void Open(string devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath))
            throw new ArgumentException("Device path must not be empty.", nameof(devicePath));

        if (_fd >= 0)
            close(_fd);

        _devicePath = devicePath;

        // Try O_RDWR first (required for write commands like WRITE10, BLANK, FORMAT UNIT).
        // Fall back to O_RDONLY if read/write fails — this allows SCSI passthrough for
        // read-only operations (INQUIRY, TEST UNIT READY, GET CONFIGURATION, READ, etc.)
        // without requiring cdrom group membership or root privileges.
        // This is especially important for USB optical drives where udev rules may
        // not grant write permissions to the current user.
        _fd = open(devicePath, O_RDWR | O_NONBLOCK);
        if (_fd < 0)
        {
            // Retry with read-only access
            _fd = open(devicePath, O_RDONLY | O_NONBLOCK);
        }

        if (_fd < 0)
        {
            // Marshal.GetLastWin32Error() correctly retrieves errno on Linux via .NET's
            // SetLastError=true DllImport interop (it maps to errno, not Win32 error codes).
            var errno = Marshal.GetLastWin32Error();
            var errPtr = strerror_native(errno);
            var errMsg = errPtr != IntPtr.Zero
                ? Marshal.PtrToStringAnsi(errPtr) ?? $"errno={errno}"
                : $"errno={errno}";
            throw new InvalidOperationException(
                $"Failed to open SCSI device '{devicePath}': {errMsg} (errno={errno})");
        }
    }

    /// <summary>
    /// Prepares the device for destructive operations (FORMAT UNIT, BLANK).
    /// On Linux, attempts to unmount any mounted filesystems on the device to
    /// prevent data corruption and stale mount state after the operation.
    /// SCSI passthrough via SG_IO bypasses the mounted filesystem, so FORMAT UNIT
    /// and BLANK succeed even with mounted media — but the mount becomes invalid.
    /// Always returns true because the unmount is best-effort: SCSI commands will
    /// succeed regardless, and failure to unmount is not a blocking condition.
    /// </summary>
    public bool PrepareForWrite()
    {
        if (string.IsNullOrEmpty(_devicePath))
            return true;

        const int umountTimeoutMs = 10_000;

        try
        {
            // Try to unmount all partitions of the device using umount.
            // This is best-effort — if the device isn't mounted, umount exits non-zero
            // which is fine. If we can't unmount (busy), the SCSI command will still work
            // but the user may need to handle the stale mount manually.
            var psi = new System.Diagnostics.ProcessStartInfo("umount")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add(_devicePath);

            using var proc = new System.Diagnostics.Process { StartInfo = psi };
            proc.Start();
            // Drain stdout/stderr before WaitForExit to prevent pipe buffer deadlock
            proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(umountTimeoutMs))
            {
                try { if (!proc.HasExited) proc.Kill(); } catch { /* best effort */ }
            }
        }
        catch
        {
            // umount not available or failed — this is best-effort.
            // SG_IO will still work for the SCSI commands.
        }

        return true;
    }

    public ScsiResult Execute(ScsiCommand command)
    {
        if (_fd < 0)
            throw new InvalidOperationException("SCSI transport is not open.");

        if (command?.Cdb == null || command.Cdb.Length == 0)
            throw new ArgumentException("CDB must not be null or empty.", nameof(command));

        var senseBuffer = new byte[252];
        var hdr = new SgIoHdr();

        // Pin managed arrays to get unmanaged pointers
        var cdbHandle = GCHandle.Alloc(command.Cdb, GCHandleType.Pinned);
        var senseHandle = GCHandle.Alloc(senseBuffer, GCHandleType.Pinned);
        GCHandle dataHandle = default;

        try
        {
            hdr.InterfaceId = SG_INTERFACE_ID;
            hdr.CmdLen = (byte)command.Cdb.Length;
            hdr.Cmdp = cdbHandle.AddrOfPinnedObject();
            hdr.MxSbLen = (byte)senseBuffer.Length;
            hdr.Sbp = senseHandle.AddrOfPinnedObject();
            hdr.Timeout = (uint)command.TimeoutMs;

            hdr.DxferDirection = command.Direction switch
            {
                ScsiDataDirection.In => SG_DXFER_FROM_DEV,
                ScsiDataDirection.Out => SG_DXFER_TO_DEV,
                _ => SG_DXFER_NONE
            };

            if (command.DataBuffer.Length > 0)
            {
                dataHandle = GCHandle.Alloc(command.DataBuffer, GCHandleType.Pinned);
                hdr.Dxferp = dataHandle.AddrOfPinnedObject();
                hdr.DxferLen = (uint)command.DataBuffer.Length;
            }

            var ret = ioctl(_fd, SG_IO, ref hdr);
            if (ret < 0)
            {
                var errno = Marshal.GetLastWin32Error();
                var errPtr = strerror_native(errno);
                var errMsg = errPtr != IntPtr.Zero
                    ? Marshal.PtrToStringAnsi(errPtr) ?? $"errno={errno}"
                    : $"errno={errno}";
                // Include errno in sense data for diagnostics
                var errorSense = new byte[4];
                BitConverter.GetBytes(errno).CopyTo(errorSense, 0);
                return new ScsiResult
                {
                    Status = 0xFF,
                    SenseData = errorSense,
                    DataTransferred = 0
                };
            }

            // Check for host or driver errors reported via SG_IO header fields.
            // Per Linux SG_IO documentation, host_status and driver_status indicate
            // transport-level errors that are not reflected in the ioctl return value.
            // host_status: 0x00 = OK, others indicate host adapter errors
            // driver_status: bits 3:0 = driver byte, bits 7:4 = suggest byte
            //   driver byte: 0x00 = OK, 0x08 = DRIVER_SENSE (sense data available)
            bool hasSenseData = (hdr.DriverStatus & 0x0F) == 0x08;
            if (hdr.HostStatus != 0)
            {
                // Host adapter error — always report transport-level failures.
                // Even if DRIVER_SENSE is set, host adapter errors (timeouts, bus
                // resets, selection failures) take priority because they indicate
                // the command did not complete reliably at the transport level.
                var hostErrSense = new byte[4];
                hostErrSense[0] = (byte)hdr.HostStatus;
                hostErrSense[1] = (byte)(hdr.HostStatus >> 8);
                hostErrSense[2] = (byte)hdr.DriverStatus;
                hostErrSense[3] = (byte)(hdr.DriverStatus >> 8);
                return new ScsiResult
                {
                    Status = 0xFF,
                    SenseData = hostErrSense,
                    DataTransferred = 0
                };
            }

            // Guard against negative residual count which would cause underflow.
            // hdr.Resid is int, so use Math.Max to clamp to non-negative before uint conversion.
            uint residU = (uint)Math.Max(0, hdr.Resid);
            // Ensure transferred bytes is non-negative even if residU > DxferLen
            var transferred = residU <= hdr.DxferLen ? (int)(hdr.DxferLen - residU) : 0;
            // Clamp SbLenWr to allocated sense buffer size
            var senseLen = Math.Min((int)hdr.SbLenWr, senseBuffer.Length);
            return new ScsiResult
            {
                Status = hdr.Status,
                SenseData = senseBuffer[..senseLen],
                DataTransferred = transferred
            };
        }
        finally
        {
            cdbHandle.Free();
            senseHandle.Free();
            if (dataHandle.IsAllocated) dataHandle.Free();
        }
    }

    public void Dispose()
    {
        if (_fd >= 0)
        {
            close(_fd);
            _fd = -1;
        }
    }
}
