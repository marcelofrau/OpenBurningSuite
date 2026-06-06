using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using OpenBurningSuite.Helpers;

namespace OpenBurningSuite.Controls;

public class IconButton : Button
{
    public static readonly StyledProperty<string> IconKeyProperty =
        AvaloniaProperty.Register<IconButton, string>(nameof(IconKey), "");

    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<IconButton, string>(nameof(Text), "");

    public static readonly StyledProperty<double> IconSizeProperty =
        AvaloniaProperty.Register<IconButton, double>(nameof(IconSize), 50.0);

    public static readonly StyledProperty<bool> IconOnlyProperty =
        AvaloniaProperty.Register<IconButton, bool>(nameof(IconOnly), false);

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

    public bool IconOnly
    {
        get => GetValue(IconOnlyProperty);
        set => SetValue(IconOnlyProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IconKeyProperty ||
            change.Property == TextProperty ||
            change.Property == IconSizeProperty ||
            change.Property == IconOnlyProperty)
        {
            RebuildContent();
        }
    }

    private void RebuildContent()
    {
        if (IconOnly)
        {
            var uri = IconHelper.GetUri(IconKey, (int)IconSize);
            if (uri != null)
            {
                try
                {
                    var bitmap = new Bitmap(AssetLoader.Open(new Uri(uri)));
                    Content = new Image
                    {
                        Source = bitmap,
                        Width = IconSize,
                        Height = IconSize,
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center
                    };
                }
                catch
                {
                    Content = Text;
                }
            }
            return;
        }

        var sp = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
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
                        VerticalAlignment = VerticalAlignment.Center
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
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }

        Content = sp;
    }
}
