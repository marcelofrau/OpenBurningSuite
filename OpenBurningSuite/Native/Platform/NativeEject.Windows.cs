// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Runtime.InteropServices;

namespace OpenBurningSuite.Native.Platform;

public static partial class NativeEject
{
    // Windows IOCTL codes for ejection
    // IOCTL_STORAGE_EJECT_MEDIA = CTL_CODE(IOCTL_STORAGE_BASE=0x2D, 0x0202, METHOD_BUFFERED=0, FILE_READ_ACCESS=1)
    private const uint IOCTL_STORAGE_EJECT_MEDIA = 0x002D4808;
    // IOCTL_STORAGE_LOAD_MEDIA = CTL_CODE(IOCTL_STORAGE_BASE=0x2D, 0x0203, METHOD_BUFFERED=0, FILE_READ_ACCESS=1)
    private const uint IOCTL_STORAGE_LOAD_MEDIA = 0x002D480C;

    // Windows P/Invoke for eject
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        IntPtr hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize,
        IntPtr lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>Windows fallback: eject via IOCTL_STORAGE_EJECT_MEDIA.</summary>
    private static bool EjectWindows(string devicePath)
    {
        // Convert drive letter (e.g. "D:\" or "D:") to device path "\\.\D:"
        var winDevicePath = ToWindowsDevicePath(devicePath);
        if (winDevicePath == null) return false;

        var hDevice = IntPtr.Zero;
        try
        {
            // GENERIC_READ = 0x80000000, FILE_SHARE_READ|WRITE = 3, OPEN_EXISTING = 3
            hDevice = CreateFileW(winDevicePath, 0x80000000u, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (hDevice == new IntPtr(-1)) return false;

            return DeviceIoControl(hDevice, IOCTL_STORAGE_EJECT_MEDIA,
                IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
        }
        catch
        {
            return false;
        }
        finally
        {
            if (hDevice != IntPtr.Zero && hDevice != new IntPtr(-1))
                CloseHandle(hDevice);
        }
    }

    /// <summary>Windows fallback: load tray via IOCTL_STORAGE_LOAD_MEDIA.</summary>
    private static bool LoadTrayWindows(string devicePath)
    {
        var winDevicePath = ToWindowsDevicePath(devicePath);
        if (winDevicePath == null) return false;

        var hDevice = IntPtr.Zero;
        try
        {
            hDevice = CreateFileW(winDevicePath, 0x80000000u, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (hDevice == new IntPtr(-1)) return false;

            return DeviceIoControl(hDevice, IOCTL_STORAGE_LOAD_MEDIA,
                IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
        }
        catch
        {
            return false;
        }
        finally
        {
            if (hDevice != IntPtr.Zero && hDevice != new IntPtr(-1))
                CloseHandle(hDevice);
        }
    }

    /// <summary>Converts a drive letter path (e.g. "D:" or "D:\") to a Windows device path "\\.\D:".</summary>
    private static string? ToWindowsDevicePath(string devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath)) return null;
        // Already a device path
        if (devicePath.StartsWith(@"\\.\", StringComparison.Ordinal))
            return devicePath;
        // Drive letter with colon (e.g. "D:" or "D:\")
        if (devicePath.Length >= 2 && char.IsLetter(devicePath[0]) && devicePath[1] == ':')
            return $@"\\.\{devicePath[0]}:";
        return null;
    }
}
