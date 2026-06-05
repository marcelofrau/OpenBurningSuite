// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using OpenBurningSuite.Services;

namespace OpenBurningSuite.Helpers;

/// <summary>
/// Receives Windows device notifications (media insert/eject, drive add/remove)
/// via RegisterDeviceNotification on a dedicated STA message-pump thread.
/// </summary>
internal sealed class WindowsDeviceNotifier : IDisposable
{
    private const int WM_DEVICECHANGE = 0x0219;
    private const int DBT_DEVICEARRIVAL = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
    private const int DBT_DEVTYP_VOLUME = 0x0002;
    private const int DBT_DEVTYP_DEVICEINTERFACE = 0x0005;
    private const uint DBTF_MEDIA = 0x0001;
    private const uint DEVICE_NOTIFY_WINDOW_HANDLE = 0x0000;

    private static readonly Guid GUID_DEVINTERFACE_CDROM = new("53F56308-B6BF-11D0-94F2-00A0C91EFB8B");

    private Thread? _pumpThread;
    private IntPtr _hwnd;
    private IntPtr _notifyHandleVol;
    private IntPtr _notifyHandleCdrom;
    private WndProcDelegate? _wndProc;
    private string? _className;
    private ushort _atom;
    private volatile bool _running;

    public event Action<DriveChangeType, string>? DriveChanged;

    [System.Runtime.Versioning.SupportedOSPlatformGuard("windows")]
    public bool IsSupported => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public bool Start()
    {
        if (!IsSupported)
            return false;

        _running = true;
        _wndProc = WndProc;
        _className = "OBS_DriveWatcher_" + Guid.NewGuid().ToString("N");

        _pumpThread = new Thread(MessagePump)
        {
            Name = "OBS_DriveWatcher",
            IsBackground = true
        };
        _pumpThread.SetApartmentState(ApartmentState.STA);
        _pumpThread.Start();

        // Wait for the pump thread to initialize
        for (int i = 0; i < 50; i++)
        {
            if (_hwnd != IntPtr.Zero)
                return _notifyHandleVol != IntPtr.Zero || _notifyHandleCdrom != IntPtr.Zero;
            Thread.Sleep(100);
        }

        return false;
    }

    public void Stop()
    {
        _running = false;

        if (_notifyHandleVol != IntPtr.Zero)
        {
            UnregisterDeviceNotification(_notifyHandleVol);
            _notifyHandleVol = IntPtr.Zero;
        }

        if (_notifyHandleCdrom != IntPtr.Zero)
        {
            UnregisterDeviceNotification(_notifyHandleCdrom);
            _notifyHandleCdrom = IntPtr.Zero;
        }

        if (_hwnd != IntPtr.Zero)
        {
            // Post a quit message to break the message pump
            PostMessage(_hwnd, 0x0012, IntPtr.Zero, IntPtr.Zero); // WM_QUIT
            _hwnd = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        Stop();
        _pumpThread?.Join(2000);
        _pumpThread = null;
    }

    private void MessagePump()
    {
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc!),
            cbClsExtra = 0,
            cbWndExtra = 0,
            hInstance = GetModuleHandle(null),
            hIcon = IntPtr.Zero,
            hCursor = IntPtr.Zero,
            hbrBackground = IntPtr.Zero,
            lpszMenuName = null,
            lpszClassName = _className!,
            hIconSm = IntPtr.Zero
        };

        _atom = RegisterClassEx(ref wc);
        if (_atom == 0) return;

        // Message-only window — receives messages without being visible
        _hwnd = CreateWindowEx(0, _atom, null, 0, 0, 0, 0, 0,
            HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            UnregisterClass(_className, GetModuleHandle(null));
            return;
        }

        RegisterVolumeNotification();
        RegisterCdromNotification();

        // Message pump — dispatches WM_DEVICECHANGE to our WndProc
        while (_running && GetMessage(out var msg, IntPtr.Zero, 0, 0))
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        // Cleanup
        if (_notifyHandleVol != IntPtr.Zero)
            UnregisterDeviceNotification(_notifyHandleVol);
        if (_notifyHandleCdrom != IntPtr.Zero)
            UnregisterDeviceNotification(_notifyHandleCdrom);
        if (_hwnd != IntPtr.Zero)
            DestroyWindow(_hwnd);
        if (_className != null)
            UnregisterClass(_className, GetModuleHandle(null));
    }

    private void RegisterVolumeNotification()
    {
        var vol = new DEV_BROADCAST_VOLUME
        {
            dbcv_size = Marshal.SizeOf<DEV_BROADCAST_VOLUME>(),
            dbcv_devicetype = DBT_DEVTYP_VOLUME,
            dbcv_unitmask = 0
        };
        var p = Marshal.AllocHGlobal(vol.dbcv_size);
        try
        {
            Marshal.StructureToPtr(vol, p, false);
            _notifyHandleVol = RegisterDeviceNotification(_hwnd, p, DEVICE_NOTIFY_WINDOW_HANDLE);
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    private void RegisterCdromNotification()
    {
        var dbi = new DEV_BROADCAST_DEVICEINTERFACE
        {
            dbcc_size = Marshal.SizeOf<DEV_BROADCAST_DEVICEINTERFACE>(),
            dbcc_devicetype = DBT_DEVTYP_DEVICEINTERFACE,
            dbcc_classguid = GUID_DEVINTERFACE_CDROM
        };
        var p = Marshal.AllocHGlobal(dbi.dbcc_size);
        try
        {
            Marshal.StructureToPtr(dbi, p, false);
            _notifyHandleCdrom = RegisterDeviceNotification(_hwnd, p, DEVICE_NOTIFY_WINDOW_HANDLE);
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_DEVICECHANGE)
        {
            int evt = wParam.ToInt32();

            if (lParam != IntPtr.Zero)
            {
                try
                {
                    var hdr = Marshal.PtrToStructure<DEV_BROADCAST_HDR>(lParam);

                    switch (hdr.dbch_devicetype)
                    {
                        case DBT_DEVTYP_VOLUME:
                            HandleVolumeEvent(evt, lParam);
                            break;

                        case DBT_DEVTYP_DEVICEINTERFACE:
                            HandleDeviceInterfaceEvent(evt);
                            break;
                    }
                }
                catch { }
            }
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void HandleVolumeEvent(int evt, IntPtr lParam)
    {
        var vol = Marshal.PtrToStructure<DEV_BROADCAST_VOLUME>(lParam);
        bool isMedia = (vol.dbcv_flags & DBTF_MEDIA) != 0;

        if (!isMedia)
            return;

        foreach (char letter in GetDriveLettersFromMask(vol.dbcv_unitmask))
        {
            var type = evt == DBT_DEVICEARRIVAL
                ? DriveChangeType.MediaInserted
                : DriveChangeType.MediaEjected;
            DriveChanged?.Invoke(type, $"{letter}:\\");
        }

        if (vol.dbcv_unitmask == 0)
        {
            DriveChanged?.Invoke(
                evt == DBT_DEVICEARRIVAL ? DriveChangeType.MediaInserted : DriveChangeType.MediaEjected,
                "");
        }
    }

    private void HandleDeviceInterfaceEvent(int evt)
    {
        var type = evt switch
        {
            DBT_DEVICEARRIVAL => DriveChangeType.DriveAdded,
            DBT_DEVICEREMOVECOMPLETE => DriveChangeType.DriveRemoved,
            _ => DriveChangeType.MediaChanged
        };
        DriveChanged?.Invoke(type, "");
    }

    private static IEnumerable<char> GetDriveLettersFromMask(uint unitmask)
    {
        for (char letter = 'A'; letter <= 'Z'; letter++)
        {
            uint mask = 1u << (letter - 'A');
            if ((unitmask & mask) != 0)
                yield return letter;
        }
    }

    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEV_BROADCAST_HDR
    {
        public int dbch_size;
        public int dbch_devicetype;
        public int dbch_reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEV_BROADCAST_VOLUME
    {
        public int dbcv_size;
        public int dbcv_devicetype;
        public int dbcv_reserved;
        public uint dbcv_unitmask;
        public ushort dbcv_flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DEV_BROADCAST_DEVICEINTERFACE
    {
        public int dbcc_size;
        public int dbcc_devicetype;
        public int dbcc_reserved;
        public Guid dbcc_classguid;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint dwExStyle, ushort lpClassName, string? lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
        IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterClass(string? lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetMessage(out MSG msg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr DispatchMessage(ref MSG msg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterDeviceNotification(IntPtr hRecipient, IntPtr notificationFilter, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterDeviceNotification(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }
}
