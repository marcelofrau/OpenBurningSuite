// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using OpenBurningSuite.Views;

namespace OpenBurningSuite;

public partial class App : Application
{
    private MainWindow? _mainWindow;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _mainWindow = new MainWindow();
            desktop.MainWindow = _mainWindow;

            // Set up the native menu (macOS menu bar & supported Linux desktops)
            BuildNativeMenu();
        }

        base.OnFrameworkInitializationCompleted();
    }

    // -----------------------------------------------------------------------
    // Native Menu
    // -----------------------------------------------------------------------

    private void BuildNativeMenu()
    {
        var nativeMenu = new NativeMenu();

        // ── File ──
        var fileMenu = new NativeMenuItem("File") { Menu = new NativeMenu() };
        var exitItem = new NativeMenuItem("Exit");
        exitItem.Click += (_, _) => ShutdownApp();
        fileMenu.Menu!.Items.Add(exitItem);
        nativeMenu.Items.Add(fileMenu);

        // ── Main ──
        AddNavSubmenu(nativeMenu, "Main",
            ("home", "Home"),
            ("discover", "Discover Drives & Media"),
            ("read", "Copy Disc to Image"),
            ("build", "Build Disc Image"),
            ("write", "Burn / Write Disc"),
            ("verify", "Verify / Check"),
            ("advanced", "Advanced Utilities"));

        // ── Wizards ──
        AddNavSubmenu(nativeMenu, "Wizards",
            ("audiowizard", "Audio & Music Wizard"),
            ("videowizard", "Video Disc Wizard"),
            ("datawizard", "Data Disc Wizard"),
            ("gamewizard", "Game Disc Wizard"),
            ("copywizard", "Copy Disc Wizard"),
            ("blankwizard", "Blank / Erase Disc Wizard"));

        // ── Help ──
        var helpMenu = new NativeMenuItem("Help") { Menu = new NativeMenu() };
        var settingsItem = new NativeMenuItem("Settings");
        settingsItem.Click += (_, _) => _mainWindow?.NavigateToView("settings");
        helpMenu.Menu!.Items.Add(settingsItem);

        helpMenu.Menu!.Items.Add(new NativeMenuItemSeparator());

        var helpItem = new NativeMenuItem("Help & Documentation");
        helpItem.Click += (_, _) => _mainWindow?.NavigateToView("help");
        helpMenu.Menu!.Items.Add(helpItem);

        nativeMenu.Items.Add(helpMenu);

        NativeMenu.SetMenu(this, nativeMenu);

        // On macOS the menu bar belongs to the frontmost window.
        // Setting the menu on the Window in addition to the Application
        // ensures the native menu bar is visible on macOS.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && _mainWindow != null)
        {
            NativeMenu.SetMenu(_mainWindow, nativeMenu);
        }
    }

    private void AddNavSubmenu(NativeMenu parentMenu, string header, params (string tag, string label)[] items)
    {
        var submenu = new NativeMenuItem(header) { Menu = new NativeMenu() };

        foreach (var (tag, label) in items)
        {
            var menuItem = new NativeMenuItem(label);
            menuItem.Click += (_, _) => _mainWindow?.NavigateToView(tag);
            submenu.Menu!.Items.Add(menuItem);
        }

        parentMenu.Items.Add(submenu);
    }

    private void ShutdownApp()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }
}