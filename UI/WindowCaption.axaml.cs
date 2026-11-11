// Licensed under the Business Source License 1.1. See LICENSE.
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Kirin_Tool.Services;

namespace Kirin_Tool.UI;

public partial class WindowCaption : UserControl
{
    public static string ApplicationTitle => $"Kirin Tool for Linux v{typeof(WindowCaption).Assembly.GetName().Version?.ToString(3)} · Linux port by Redminote11tech";

    /// <summary>Guards the ComboBox write-back while the saved theme is loaded.</summary>
    private bool _loadingTheme;

    public WindowCaption()
    {
        InitializeComponent();
        this.FindControl<TextBlock>("CaptionTitle")!.Text = ApplicationTitle;
        LoadThemeSelection();
    }

    private Window? OwnerWindow => TopLevel.GetTopLevel(this) as Window;

    private void LoadThemeSelection()
    {
        _loadingTheme = true;
        try
        {
            var theme = UserSettings.Theme;
            foreach (var item in ThemeSelector.Items)
            {
                if (item is ComboBoxItem combo && string.Equals(combo.Tag as string, theme, StringComparison.OrdinalIgnoreCase))
                {
                    ThemeSelector.SelectedItem = combo;
                    break;
                }
            }
        }
        finally
        {
            _loadingTheme = false;
        }
    }

    private void ThemeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingTheme) return;
        if (ThemeSelector.SelectedItem is not ComboBoxItem { Tag: string theme }) return;
        UserSettings.SetTheme(theme);
    }

    private void MinimizeWindow(object? sender, RoutedEventArgs e)
    { if (OwnerWindow is { } window) window.WindowState = WindowState.Minimized; }
    private void ToggleMaximize(object? sender, RoutedEventArgs e)
    { if (OwnerWindow is { } window) window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; }
    private void CloseWindow(object? sender, RoutedEventArgs e) => OwnerWindow?.Close();
    private void DragTitleBar(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || OwnerWindow is not { } window) return;
        if (e.ClickCount == 2) ToggleMaximize(sender, new RoutedEventArgs());
        else window.BeginMoveDrag(e);
    }
}
