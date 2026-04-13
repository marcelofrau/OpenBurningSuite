// Copyright (c) 2026 SvenGDK
// SPDX-License-Identifier: BSD-2-Clause

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace OpenBurningSuite.Views;

public partial class HelpView : UserControl
{
    public HelpView()
    {
        InitializeComponent();
    }

    private void OnTocClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string sectionName)
        {
            var target = this.FindControl<Border>(sectionName);
            target?.BringIntoView();
        }
    }
}
