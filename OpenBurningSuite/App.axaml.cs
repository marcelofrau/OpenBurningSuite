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
        var fileMenu = new NativeMenuItem("文件") { Menu = new NativeMenu() };
        var exitItem = new NativeMenuItem("退出");
        exitItem.Click += (_, _) => ShutdownApp();
        fileMenu.Menu!.Items.Add(exitItem);
        nativeMenu.Items.Add(fileMenu);

        // ── Main ──
        AddNavSubmenu(nativeMenu, "主菜单",
            ("home", "主页"),
            ("discover", "发现光驱与介质"),
            ("read", "将光盘复制为镜像"),
            ("build", "构建光盘镜像"),
            ("write", "写入 / 刻录光盘"),
            ("verify", "校验 / 检查"),
            ("advanced", "高级工具"));

        // ── Wizards ──
        AddNavSubmenu(nativeMenu, "向导",
            ("audiowizard", "音频与音乐向导"),
            ("videowizard", "视频光盘向导"),
            ("datawizard", "数据光盘向导"),
            ("gamewizard", "游戏光盘向导"),
            ("copywizard", "复制光盘向导"),
            ("blankwizard", "擦除光盘向导"));

        // ── Help ──
        var helpMenu = new NativeMenuItem("帮助") { Menu = new NativeMenu() };
        var settingsItem = new NativeMenuItem("设置");
        settingsItem.Click += (_, _) => _mainWindow?.NavigateToView("settings");
        helpMenu.Menu!.Items.Add(settingsItem);

        helpMenu.Menu!.Items.Add(new NativeMenuItemSeparator());

        var helpItem = new NativeMenuItem("帮助与文档");
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