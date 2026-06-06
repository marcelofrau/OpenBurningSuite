using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace OpenBurningSuite.Views;

public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();

        var version = Assembly.GetEntryAssembly()?.GetName().Version;
        var verStr = version is not null
            ? $"v{version.Major}.{version.Minor}.{version.Build}"
            : "v1.0.0";

        VersionText.Text = verStr;

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
}
