// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Runtime.InteropServices;

namespace OpenBurningSuite.Native.Scsi;

/// <summary>
/// Factory that creates the platform-appropriate SCSI transport.
/// </summary>
public static class ScsiTransportFactory
{
    /// <summary>Creates and opens a SCSI transport for the given device path.</summary>
    public static IScsiTransport Create(string devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath))
            throw new ArgumentException("Device path must not be empty.", nameof(devicePath));

        IScsiTransport transport;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            transport = new LinuxScsiTransport();
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            transport = new WindowsScsiTransport();
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            transport = new MacOsScsiTransport();
        else
            throw new PlatformNotSupportedException(
                "SCSI passthrough is not supported on this platform.");

        try
        {
            transport.Open(devicePath);
            return transport;
        }
        catch
        {
            transport.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Creates and opens a transport in read-only probe mode. On macOS this opens
    /// the MMC device interface WITHOUT Disk Arbitration unmount/claim and WITHOUT
    /// exclusive access — only the read-only MMC commands MMCDeviceInterface
    /// exposes are available. On Linux/Windows there is no such distinction, so
    /// this is identical to <see cref="Create"/>.
    /// </summary>
    public static IScsiTransport CreateReadOnly(string devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath))
            throw new ArgumentException("Device path must not be empty.", nameof(devicePath));

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var transport = new MacOsScsiTransport();
            try
            {
                transport.OpenReadOnly(devicePath);
                return transport;
            }
            catch
            {
                transport.Dispose();
                throw;
            }
        }

        return Create(devicePath);
    }

    /// <summary>Creates a transport without opening it.</summary>
    public static IScsiTransport CreateUnopened()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return new LinuxScsiTransport();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return new WindowsScsiTransport();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return new MacOsScsiTransport();
        throw new PlatformNotSupportedException(
            "SCSI passthrough is not supported on this platform.");
    }
}
