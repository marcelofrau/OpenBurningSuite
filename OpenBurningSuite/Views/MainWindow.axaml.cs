// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using Avalonia.Controls;
using Avalonia.Interactivity;
using OpenBurningSuite.Helpers;

namespace OpenBurningSuite.Views;

public partial class MainWindow : Window
{
    private Button? _activeNav;

    public MainWindow()
    {
        InitializeComponent();

        TxtElevation.Text = PlatformHelper.IsElevated ? "✅ Elevated" : "⚠ Not elevated";

        // Start on home screen — no nav button active
        BtnHome.Classes.Add("Active");
        _activeNav = BtnHome;
    }

    // -----------------------------------------------------------------------
    // Public API for NativeMenu navigation
    // -----------------------------------------------------------------------

    /// <summary>
    /// Navigates to the view identified by the given tag string.
    /// Used by the NativeMenu to switch views from the application menu.
    /// </summary>
    public void NavigateToView(string tag)
    {
        switch (tag)
        {
            case "home":
                OnHomeClick(this, new RoutedEventArgs());
                break;
            case "discover":
                SetActiveView(BtnDiscover, ViewDiscover, "Discover Drives & Media");
                break;
            case "read":
                SetActiveView(BtnRead, ViewRead, "Copy Disc to Image");
                break;
            case "build":
                SetActiveView(BtnBuild, ViewBuild, "Build Disc Image");
                break;
            case "write":
                SetActiveView(BtnWrite, ViewWrite, "Burn / Write Disc");
                break;
            case "verify":
                SetActiveView(BtnVerify, ViewVerify, "Verify / Check");
                break;
            case "advanced":
                SetActiveView(BtnAdvanced, ViewAdvanced, "Advanced Utilities");
                break;
            case "audiowizard":
                SetActiveView(BtnAudioWizard, ViewAudioWizard, "Audio & Music Wizard");
                break;
            case "videowizard":
                SetActiveView(BtnVideoWizard, ViewVideoWizard, "Video Disc Wizard");
                break;
            case "datawizard":
                SetActiveView(BtnDataWizard, ViewDataWizard, "Data Disc Wizard");
                break;
            case "gamewizard":
                SetActiveView(BtnGameWizard, ViewGameWizard, "Game Disc Wizard");
                break;
            case "copywizard":
                SetActiveView(BtnCopyWizard, ViewCopyWizard, "Copy Disc Wizard");
                break;
            case "blankwizard":
                SetActiveView(BtnBlankWizard, ViewBlankWizard, "Blank Disc Wizard");
                break;
            case "settings":
                OnSettingsClick(this, new RoutedEventArgs());
                break;
            case "help":
                OnHelpClick(this, new RoutedEventArgs());
                break;
        }
    }

    // -----------------------------------------------------------------------
    // Navigation
    // -----------------------------------------------------------------------
    private void OnHomeClick(object? sender, RoutedEventArgs e)
    {
        HideAllViews();
        HomeScreen.IsVisible = true;

        if (_activeNav != null)
            _activeNav.Classes.Remove("Active");
        BtnHome.Classes.Add("Active");
        _activeNav = BtnHome;

        TxtStatus.Text = "Ready — select an action to get started.";
    }

    private void OnDiscoverClick(object? sender, RoutedEventArgs e) =>
        SetActiveView(BtnDiscover, ViewDiscover, "Discover Drives & Media");

    private void OnReadClick(object? sender, RoutedEventArgs e) =>
        SetActiveView(BtnRead, ViewRead, "Copy Disc to Image");

    private void OnBuildClick(object? sender, RoutedEventArgs e) =>
        SetActiveView(BtnBuild, ViewBuild, "Build Disc Image");

    private void OnWriteClick(object? sender, RoutedEventArgs e) =>
        SetActiveView(BtnWrite, ViewWrite, "Burn / Write Disc");

    private void OnVerifyClick(object? sender, RoutedEventArgs e) =>
        SetActiveView(BtnVerify, ViewVerify, "Verify / Check");

    private void OnAdvancedClick(object? sender, RoutedEventArgs e) =>
        SetActiveView(BtnAdvanced, ViewAdvanced, "Advanced Utilities");

    private void OnAudioWizardClick(object? sender, RoutedEventArgs e) =>
        SetActiveView(BtnAudioWizard, ViewAudioWizard, "Audio & Music Wizard");

    private void OnVideoWizardClick(object? sender, RoutedEventArgs e) =>
        SetActiveView(BtnVideoWizard, ViewVideoWizard, "Video Disc Wizard");

    private void OnDataWizardClick(object? sender, RoutedEventArgs e) =>
        SetActiveView(BtnDataWizard, ViewDataWizard, "Data Disc Wizard");

    private void OnGameWizardClick(object? sender, RoutedEventArgs e) =>
        SetActiveView(BtnGameWizard, ViewGameWizard, "Game Disc Wizard");

    private void OnCopyWizardClick(object? sender, RoutedEventArgs e) =>
        SetActiveView(BtnCopyWizard, ViewCopyWizard, "Copy Disc Wizard");

    private void OnBlankWizardClick(object? sender, RoutedEventArgs e) =>
        SetActiveView(BtnBlankWizard, ViewBlankWizard, "Blank Disc Wizard");

    private void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        HideAllViews();

        if (_activeNav != null)
            _activeNav.Classes.Remove("Active");

        ViewSettings.IsVisible = true;
        BtnSettingsNav.Classes.Add("Active");
        _activeNav = BtnSettingsNav;

        TxtStatus.Text = "Viewing: Application Settings & Preferences";
    }

    private void OnHelpClick(object? sender, RoutedEventArgs e)
    {
        HideAllViews();

        if (_activeNav != null)
            _activeNav.Classes.Remove("Active");

        ViewHelp.IsVisible = true;
        BtnHelpNav.Classes.Add("Active");
        _activeNav = BtnHelpNav;

        TxtStatus.Text = "Viewing: Help & Documentation";
    }

    private void SetActiveView(Button navBtn, Control view, string viewName)
    {
        HideAllViews();

        // Remove Active class from previous nav
        if (_activeNav != null)
            _activeNav.Classes.Remove("Active");

        // Show selected view
        view.IsVisible = true;
        navBtn.Classes.Add("Active");
        _activeNav = navBtn;

        TxtStatus.Text = $"Viewing: {viewName}";
    }

    private void HideAllViews()
    {
        HomeScreen.IsVisible   = false;
        ViewDiscover.IsVisible = false;
        ViewRead.IsVisible     = false;
        ViewBuild.IsVisible    = false;
        ViewWrite.IsVisible    = false;
        ViewVerify.IsVisible   = false;
        ViewAdvanced.IsVisible = false;
        ViewAudioWizard.IsVisible = false;
        ViewVideoWizard.IsVisible = false;
        ViewDataWizard.IsVisible = false;
        ViewGameWizard.IsVisible = false;
        ViewCopyWizard.IsVisible = false;
        ViewBlankWizard.IsVisible = false;
        ViewHelp.IsVisible = false;
        ViewSettings.IsVisible = false;
    }

    // -----------------------------------------------------------------------
    // Status
    // -----------------------------------------------------------------------
    public void SetStatusMessage(string message) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => TxtStatus.Text = message);
}