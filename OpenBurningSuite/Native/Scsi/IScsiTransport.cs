// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;

namespace OpenBurningSuite.Native.Scsi;

/// <summary>
/// Abstraction for sending SCSI commands to a device.
/// Platform-specific implementations use SG_IO on Linux and
/// IOCTL_SCSI_PASS_THROUGH_DIRECT on Windows.
/// </summary>
public interface IScsiTransport : IDisposable
{
    /// <summary>Opens the device for SCSI I/O.</summary>
    void Open(string devicePath);

    /// <summary>Sends a SCSI command and returns the result.</summary>
    ScsiResult Execute(ScsiCommand command);

    /// <summary>Whether the transport is currently open.</summary>
    bool IsOpen { get; }

    /// <summary>
    /// Prepares the device for a destructive operation (FORMAT UNIT, BLANK, etc.).
    /// On Windows, this locks and dismounts the volume to prevent the storage filter
    /// driver from blocking SCSI passthrough commands on mounted volumes.
    /// Other platforms typically don't need this (Linux/macOS expect unmount before access).
    /// Default implementation is a no-op.
    /// </summary>
    /// <returns>True if preparation succeeded or was not needed.</returns>
    bool PrepareForWrite() => true;
}

/// <summary>Direction of SCSI data transfer.</summary>
public enum ScsiDataDirection
{
    None = 0,
    In = 1,   // Device → Host (read)
    Out = 2,  // Host → Device (write)
}
