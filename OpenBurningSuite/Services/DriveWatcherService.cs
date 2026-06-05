// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using OpenBurningSuite.Helpers;
using OpenBurningSuite.Models;

namespace OpenBurningSuite.Services;

public enum DriveChangeType
{
    DriveAdded,
    DriveRemoved,
    MediaInserted,
    MediaEjected,
    MediaChanged
}

public sealed class DriveWatcherService : IDisposable
{
    private readonly DiscDiscoveryService _discovery = new();
    private List<DiscDrive> _cachedDrives = new();
    private Dictionary<string, string> _cachedMediaStates = new(StringComparer.OrdinalIgnoreCase);
    private WindowsDeviceNotifier? _winNotifier;
    private Timer? _fallbackTimer;
    private bool _disposed;

    public event Action<DriveChangeType, DiscDrive>? DriveChanged;
    public event Action<List<DiscDrive>>? DrivesUpdated;

    public IReadOnlyList<DiscDrive> CachedDrives => _cachedDrives.AsReadOnly();

    public void Start()
    {
        RefreshDrives();

        // Dedicated STA thread with RegisterDeviceNotification for instant events
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            _winNotifier = new WindowsDeviceNotifier();
            _winNotifier.DriveChanged += OnWindowsDriveChanged;
            _winNotifier.Start();
        }

        // Timer-based safety net (catches anything the native notifier misses)
        _fallbackTimer = new Timer(OnFallbackTimer, null, 1000, 1000);
    }

    public void Stop()
    {
        _winNotifier?.Dispose();
        _winNotifier = null;
        _fallbackTimer?.Dispose();
        _fallbackTimer = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    public List<DiscDrive> RefreshDrives()
    {
        var newDrives = _discovery.DiscoverDrives();
        DetectChanges(_cachedDrives, newDrives);
        _cachedDrives = newDrives;
        _cachedMediaStates = newDrives
            .Where(d => d.CurrentMedia != null)
            .ToDictionary(d => d.DevicePath, d => d.Status ?? "", StringComparer.OrdinalIgnoreCase);
        DrivesUpdated?.Invoke(newDrives);
        return newDrives;
    }

    private void OnWindowsDriveChanged(DriveChangeType type, string driveLetter)
    {
        if (string.IsNullOrEmpty(driveLetter))
        {
            RefreshDrives();
            return;
        }

        var normalized = driveLetter.TrimEnd('\\');
        var drive = _cachedDrives.FirstOrDefault(d =>
            d.DriveLetter != null &&
            d.DriveLetter.TrimEnd('\\').Equals(normalized, StringComparison.OrdinalIgnoreCase));

        if (drive == null)
        {
            RefreshDrives();
            return;
        }

        DriveChanged?.Invoke(type, drive);
    }

    private void OnFallbackTimer(object? state)
    {
        try
        {
            var newDrives = _discovery.DiscoverDrives();
            var oldDrives = _cachedDrives;

            if (!HaveDrivesChanged(oldDrives, newDrives))
                return;

            DetectChanges(oldDrives, newDrives);
            _cachedDrives = newDrives;
            _cachedMediaStates = newDrives
                .Where(d => d.CurrentMedia != null)
                .ToDictionary(d => d.DevicePath, d => d.Status ?? "", StringComparer.OrdinalIgnoreCase);
            DrivesUpdated?.Invoke(newDrives);
        }
        catch { }
    }

    private static bool HaveDrivesChanged(List<DiscDrive> oldDrives, List<DiscDrive> newDrives)
    {
        if (oldDrives.Count != newDrives.Count) return true;

        for (int i = 0; i < oldDrives.Count; i++)
        {
            var o = oldDrives[i];
            var n = newDrives[i];
            if (!string.Equals(o.DevicePath, n.DevicePath, StringComparison.OrdinalIgnoreCase))
                return true;
            if (!string.Equals(o.Status, n.Status, StringComparison.OrdinalIgnoreCase))
                return true;
            if ((o.CurrentMedia == null) != (n.CurrentMedia == null))
                return true;
        }
        return false;
    }

    private void DetectChanges(List<DiscDrive> oldDrives, List<DiscDrive> newDrives)
    {
        var oldByPath = oldDrives.ToDictionary(d => d.DevicePath, StringComparer.OrdinalIgnoreCase);
        var newByPath = newDrives.ToDictionary(d => d.DevicePath, StringComparer.OrdinalIgnoreCase);

        foreach (var (path, oldDrive) in oldByPath)
        {
            if (!newByPath.ContainsKey(path))
                DriveChanged?.Invoke(DriveChangeType.DriveRemoved, oldDrive);
        }

        foreach (var (path, newDrive) in newByPath)
        {
            if (!oldByPath.ContainsKey(path))
            {
                DriveChanged?.Invoke(newDrive.Status == "Ready"
                    ? DriveChangeType.MediaInserted
                    : DriveChangeType.DriveAdded, newDrive);
            }
            else
            {
                var oldDrive = oldByPath[path];
                if (!string.Equals(oldDrive.Status, newDrive.Status, StringComparison.OrdinalIgnoreCase))
                {
                    DriveChanged?.Invoke(
                        newDrive.Status == "Ready"
                            ? DriveChangeType.MediaInserted
                            : DriveChangeType.MediaEjected,
                        newDrive);
                }
            }
        }
    }
}
