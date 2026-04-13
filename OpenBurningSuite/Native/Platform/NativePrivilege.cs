// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Runtime.InteropServices;

namespace OpenBurningSuite.Native.Platform;

/// <summary>
/// Native privilege detection checking if the application is running
/// with elevated privileges (root/Administrator).
/// </summary>
public static class NativePrivilege
{
    // P/Invoke for Unix getuid()/geteuid()
    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

    /// <summary>Checks whether we are running with elevated privileges (root/Administrator).</summary>
    public static bool IsElevated
    {
        get
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
#pragma warning disable CA1416
                    using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                    var principal = new System.Security.Principal.WindowsPrincipal(identity);
                    return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
#pragma warning restore CA1416
                }

                // On Unix/macOS: check effective UID (0 = root)
                // First try direct P/Invoke (no external process needed)
                try
                {
                    return GetEffectiveUserId() == 0;
                }
                catch (DllNotFoundException)
                {
                    // Fallback: check UID environment variable or username.
                    // EUID is more reliable than username since a non-root user
                    // could theoretically be named "root".
                    var euid = Environment.GetEnvironmentVariable("EUID");
                    if (euid == "0") return true;
                    return Environment.UserName == "root";
                }
                catch (EntryPointNotFoundException)
                {
                    var euid = Environment.GetEnvironmentVariable("EUID");
                    if (euid == "0") return true;
                    return Environment.UserName == "root";
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
