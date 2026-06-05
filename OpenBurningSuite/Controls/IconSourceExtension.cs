using System;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using OpenBurningSuite.Helpers;

namespace OpenBurningSuite.Controls;

public class IconSourceExtension : MarkupExtension
{
    public string Key { get; set; }
    public int Size { get; set; } = 50;

    public IconSourceExtension(string key)
    {
        Key = key;
    }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var uri = IconHelper.GetUri(Key, Size);
        if (uri == null)
            return null!;

        return new Bitmap(AssetLoader.Open(new Uri(uri)));
    }
}
