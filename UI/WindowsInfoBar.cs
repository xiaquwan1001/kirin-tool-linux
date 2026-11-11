// Licensed under the Business Source License 1.1. See LICENSE.
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Kirin_Tool.UI;

// Native Avalonia warning with the frozen Windows InfoBar's spacing and behavior.
public class WindowsInfoBar : UserControl
{
    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<WindowsInfoBar, string?>(nameof(Title));
    public static readonly StyledProperty<string?> MessageProperty = AvaloniaProperty.Register<WindowsInfoBar, string?>(nameof(Message));
    public static readonly StyledProperty<bool> IsOpenProperty = AvaloniaProperty.Register<WindowsInfoBar, bool>(nameof(IsOpen), true);
    public static readonly StyledProperty<bool> IsClosableProperty = AvaloniaProperty.Register<WindowsInfoBar, bool>(nameof(IsClosable), true);
    public static readonly StyledProperty<string?> ResetKeyProperty = AvaloniaProperty.Register<WindowsInfoBar, string?>(nameof(ResetKey));
    public string? ResetKey { get => GetValue(ResetKeyProperty); set => SetValue(ResetKeyProperty, value); }
    public bool IsDismissed { get; private set; }
    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Message { get => GetValue(MessageProperty); set => SetValue(MessageProperty, value); }
    public bool IsOpen { get => GetValue(IsOpenProperty); set => SetValue(IsOpenProperty, value); }
    public bool IsClosable { get => GetValue(IsClosableProperty); set => SetValue(IsClosableProperty, value); }
    private readonly TextBlock _title = new() { FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 14, 0) };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _close = new() { Classes = { "infobar-close" }, Padding = new Thickness(4), MinHeight = 24, MinWidth = 24, Margin = new Thickness(12, -2, 0, 0), VerticalAlignment = VerticalAlignment.Top };
    private readonly WindowsSymbol _icon = new() { Symbol = "ErrorCircle24", Filled = true, FontSize = 16, Margin = new Thickness(0, 2, 14, 0), VerticalAlignment = VerticalAlignment.Top };
    private readonly Border _surface;
    public WindowsInfoBar()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        grid.Children.Add(_icon);
        var text = new WrapPanel { Orientation = Orientation.Horizontal };
        text.Children.Add(_title); text.Children.Add(_message); Grid.SetColumn(text, 1); grid.Children.Add(text);
        _close.Content = new WindowsSymbol { Symbol = "Dismiss24", FontSize = 16 };
        AutomationProperties.SetName(_close, "Dismiss warning");
        _close.Click += (_, _) => { IsDismissed = true; Update(); };
        Grid.SetColumn(_close, 2); grid.Children.Add(_close);
        _surface = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(16, 12), Child = grid };
        Content = _surface;
        ApplyTheme();
        // The caption bar can switch variants at any time; keep the warning
        // readable instead of staying frozen on the light palette.
        ActualThemeVariantChanged += (_, _) => ApplyTheme();
        Update();
    }

    /// <summary>
    /// Resolves a palette entry for the variant this control is actually
    /// rendered with. The theme-less overload would silently fall back to the
    /// light palette.
    /// </summary>
    private IBrush ThemeBrush(string key, string fallback)
    {
        if (this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush) return brush;
        if (Application.Current is { } app && app.TryFindResource(key, app.ActualThemeVariant, out var appValue) && appValue is IBrush appBrush) return appBrush;
        return Brush.Parse(fallback);
    }

    private void ApplyTheme()
    {
        _surface.Background = ThemeBrush("AppNoticeBrush", "#FFF3CD");
        _surface.BorderBrush = ThemeBrush("AppNoticeBorderBrush", "#FFE69C");
        _icon.Foreground = ThemeBrush("AppWarningTextBrush", "#9D5D00");
        Foreground = ThemeBrush("AppTextBrush", "#1A1A1A");
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ResetKeyProperty || change.Property == IsOpenProperty) { IsDismissed = false; Update(); }
        if (change.Property == TitleProperty || change.Property == MessageProperty || change.Property == IsOpenProperty || change.Property == IsClosableProperty) Update();
    }
    private void Update()
    {
        if (_title == null) return;
        _title.Text = Title; _message.Text = Message; _close.IsVisible = IsClosable; IsVisible = IsOpen && !IsDismissed;
    }
}
