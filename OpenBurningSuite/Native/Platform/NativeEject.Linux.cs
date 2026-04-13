// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System.Runtime.InteropServices;

namespace OpenBurningSuite.Native.Platform;

public static partial class NativeEject
{
    // Linux ioctl for CD-ROM eject
    private const uint CDROMEJECT = 0x5309;

    // Linux ioctl for CD-ROM close tray
    private const uint CDROMCLOSETRAY = 0x5319;

    [DllImport("libc", SetLastError = true)]
    private static extern int open([MarshalAs(UnmanagedType.LPStr)] string pathname, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, uint request);

    private static bool EjectLinux(string devicePath)
    {
        int fd = -1;
        try
        {
            // O_RDONLY = 0x0000, O_NONBLOCK = 0x0800 on x86_64 and arm64 Linux
            fd = open(devicePath, 0x0000 | 0x0800);
            if (fd < 0) return false;
            return ioctl(fd, CDROMEJECT) == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (fd >= 0) close(fd);
        }
    }

    /// <summary>Linux fallback: load tray via CDROMCLOSETRAY ioctl.</summary>
    private static bool LoadTrayLinux(string devicePath)
    {
        int fd = -1;
        try
        {
            // O_RDONLY = 0x0000, O_NONBLOCK = 0x0800 on x86_64 and arm64 Linux
            fd = open(devicePath, 0x0000 | 0x0800);
            if (fd < 0) return false;
            return ioctl(fd, CDROMCLOSETRAY) == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (fd >= 0) close(fd);
        }
    }
}
