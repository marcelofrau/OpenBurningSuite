using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using OpenBurningSuite.Helpers;

namespace OpenBurningSuite.Controls;

public class IconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key)
            return null;

        var size = 50;
        if (parameter is string paramStr && int.TryParse(paramStr, out var parsed))
            size = parsed;

        var uri = IconHelper.GetUri(key, size);
        if (uri == null)
            return null;

        try
        {
            return new Bitmap(AssetLoader.Open(new Uri(uri)));
        }
        catch
        {
            return null;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
