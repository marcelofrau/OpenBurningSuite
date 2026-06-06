using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace OpenBurningSuite.Views;

public partial class SplashWindow : Window
{
    public SplashWindow() : this(false) { }

    public SplashWindow(bool showCloseButton)
    {
        InitializeComponent();

        var version = Assembly.GetEntryAssembly()?.GetName().Version;
        var verStr = version is not null
            ? $"v{version.Major}.{version.Minor}.{version.Build}"
            : "v1.0.0";

        VersionText.Text = verStr;
        CloseButton.IsVisible = showCloseButton;
        LinkText.Text = "github.com/marcelofrau/OpenBurningSuite";

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close();
        };
    }

    private void OnCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close();
    }

    private void OnLinkClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://github.com/marcelofrau/OpenBurningSuite",
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch
        {
            // Silently ignore if URL can't be opened
        }
    }
}
