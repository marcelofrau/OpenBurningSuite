using System.Reflection;
using Avalonia;
using Avalonia.Controls;

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
    }
}
