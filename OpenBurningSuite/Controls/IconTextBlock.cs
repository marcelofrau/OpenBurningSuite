using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using OpenBurningSuite.Helpers;

namespace OpenBurningSuite.Controls;

public class IconTextBlock : ContentControl
{
    public static readonly StyledProperty<string> IconKeyProperty =
        AvaloniaProperty.Register<IconTextBlock, string>(nameof(IconKey), "");

    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<IconTextBlock, string>(nameof(Text), "");

    public static readonly StyledProperty<double> IconSizeProperty =
        AvaloniaProperty.Register<IconTextBlock, double>(nameof(IconSize), 50.0);

    public string IconKey
    {
        get => GetValue(IconKeyProperty);
        set => SetValue(IconKeyProperty, value);
    }

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public double IconSize
    {
        get => GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IconKeyProperty ||
            change.Property == TextProperty ||
            change.Property == IconSizeProperty)
        {
            RebuildContent();
        }
    }

    private void RebuildContent()
    {
        var sp = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };

        if (!string.IsNullOrEmpty(IconKey))
        {
            var uri = IconHelper.GetUri(IconKey, (int)IconSize);
            if (uri != null)
            {
                try
                {
                    var bitmap = new Bitmap(AssetLoader.Open(new Uri(uri)));
                    sp.Children.Add(new Image
                    {
                        Source = bitmap,
                        Width = IconSize,
                        Height = IconSize,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                    });
                }
                catch
                {
                }
            }
        }

        if (!string.IsNullOrEmpty(Text))
        {
            sp.Children.Add(new TextBlock
            {
                Text = Text,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            });
        }

        Content = sp;
    }
}
