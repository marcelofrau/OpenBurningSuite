// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using Avalonia;
using System;

namespace OpenBurningSuite;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Helpers.Logger.Error(ex, "Unhandled exception in Main");
            throw;
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    static Program()
    {
        Helpers.Logger.Info("Program starting");
    }
}
