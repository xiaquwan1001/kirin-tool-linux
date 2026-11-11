using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;

namespace Kirin_Tool.Services;

/// <summary>
/// Cross-platform dialog service replacing WPF-UI's ContentDialogService.
/// Shows Avalonia Windows as dialogs (modal or non-modal).
/// </summary>
public class DialogService
{
    private readonly Window _owner;

    public DialogService(Window owner)
    {
        _owner = owner;
    }

    /// <summary>
    /// Resolves a theme brush so programmatic dialogs follow the light/dark
    /// variant instead of hard-coding white.
    /// </summary>
    private static IBrush ThemeBrush(string key, IBrush fallback)
    {
        // The theme-less overload would resolve against the default (light)
        // palette even when the app is running dark.
        return Application.Current is { } app &&
               app.TryFindResource(key, app.ActualThemeVariant, out var value) && value is IBrush brush
            ? brush
            : fallback;
    }

    private static IBrush DialogBackground => ThemeBrush("AppCardBrush", Brushes.White);

    /// <summary>
    /// Folder a picker should open in: the last directory the user picked from,
    /// when it still exists.
    /// </summary>
    private static async Task<IStorageFolder?> SuggestedStartLocationAsync(TopLevel? topLevel)
    {
        var lastDirectory = UserSettings.LastDirectory;
        if (topLevel?.StorageProvider is null || lastDirectory is null) return null;

        try
        {
            return await topLevel.StorageProvider.TryGetFolderFromPathAsync(new Uri(lastDirectory));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Show a dialog window modally and return its result.
    /// </summary>
    public Task<bool?> ShowDialog(Window dialog)
    {
        return dialog.ShowDialog<bool?>(_owner);
    }

    /// <summary>
    /// Show a dialog window non-modally (for progress dialogs that run alongside background work).
    /// Returns a Task that completes when the dialog is closed.
    /// </summary>
    public Task ShowNonModalDialog(Window dialog)
    {
        var tcs = new TaskCompletionSource();
        dialog.Closed += (_, _) => tcs.TrySetResult();
        dialog.Show(_owner);
        // Non-modal windows are not focused automatically; bring the dialog to the front so it
        // does not end up stacked behind the main window.
        dialog.Activate();
        return tcs.Task;
    }

    /// <summary>
    /// Show a simple message box with an OK button.
    /// </summary>
    public async Task ShowMessageBox(string title, string message, bool showHeading = true)
    {
        var dialog = new Window
        {
            Title = title,
            SystemDecorations = SystemDecorations.BorderOnly,
            Background = DialogBackground,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight,
            MinWidth = 300,
            MaxWidth = 500,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new(24),
                Spacing = 16,
                Children =
                {
                    new TextBlock
                    {
                        Text = message,
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 14
                    },
                    new Button
                    {
                        Content = "OK",
                        HorizontalAlignment = HorizontalAlignment.Right,
                        MinWidth = 100,
                        Height = 32
                    }
                }
            }
        };

        // Wire the OK button
        var stack = (StackPanel)dialog.Content;
        ((Button)stack.Children[1]).Click += (_, _) => dialog.Close();
        if (showHeading) stack.Children.Insert(0, new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeight.SemiBold });

        await dialog.ShowDialog<bool?>(_owner);
    }

    /// <summary>
    /// Show a simple confirmation dialog with Primary and Close buttons.
    /// Returns true if Primary was clicked.
    /// </summary>
    public async Task<bool> ShowConfirmDialog(string title, string message, string primaryText = "Continue", string closeText = "Cancel")
    {
        var result = false;
        var dialog = new Window
        {
            Title = title,
            SystemDecorations = SystemDecorations.BorderOnly,
            Background = DialogBackground,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight,
            MinWidth = 350,
            MaxWidth = 500,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new(24),
                Spacing = 16,
                Children =
                {
                    new TextBlock
                    {
                        Text = message,
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 14
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children =
                        {
                            new Button
                            {
                                Content = closeText,
                                MinWidth = 100,
                                Height = 32
                            },
                            new Button
                            {
                                Content = primaryText,
                                MinWidth = 100,
                                Height = 32
                            }
                        }
                    }
                }
            }
        };

        var outerStack = (StackPanel)dialog.Content;
        var buttonRow = (StackPanel)outerStack.Children[1];
        ((Button)buttonRow.Children[0]).Click += (_, _) => { result = false; dialog.Close(); };
        ((Button)buttonRow.Children[1]).Click += (_, _) => { result = true; dialog.Close(); };

        outerStack.Children.Insert(0, new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeight.SemiBold });
        await dialog.ShowDialog<bool?>(_owner);
        return result;
    }

    /// <summary>
    /// Show a SaveFileDialog and return the selected path, or null if cancelled.
    /// </summary>
    public async Task<string?> ShowSaveFileDialog(string title, string? defaultName = null, string? filterName = null, List<string>? extensions = null)
    {
        var topLevel = TopLevel.GetTopLevel(_owner);
        if (topLevel?.StorageProvider == null) return null;

        var options = new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = defaultName ?? "",
            SuggestedStartLocation = await SuggestedStartLocationAsync(topLevel)
        };

        if (filterName != null && extensions != null && extensions.Count > 0)
        {
            options.FileTypeChoices = new List<FilePickerFileType>
            {
                new FilePickerFileType(filterName)
                {
                    Patterns = extensions.ConvertAll(ext => $"*{ext}")
                }
            };
        }

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(options);
        var path = file?.Path.LocalPath;
        if (!string.IsNullOrWhiteSpace(path)) UserSettings.RememberDirectory(path);
        return path;
    }

    /// <summary>
    /// Show a folder picker and return the selected directory, or null if cancelled.
    /// </summary>
    public async Task<string?> ShowOpenFolderDialog(string title)
    {
        var topLevel = TopLevel.GetTopLevel(_owner);
        if (topLevel?.StorageProvider == null) return null;

        var options = new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = await SuggestedStartLocationAsync(topLevel)
        };

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(options);
        var path = folders?.FirstOrDefault()?.Path.LocalPath;
        if (!string.IsNullOrWhiteSpace(path)) UserSettings.RememberDirectory(path);
        return path;
    }
    /// <summary>
    /// Show an OpenFileDialog and return the selected path, or null if cancelled.
    /// A literal "*" inside <paramref name="extensions"/> adds an "All files"
    /// filter, which is how every caller used the legacy dialog.
    /// </summary>
    public async Task<string?> ShowOpenFileDialog(string title, List<string>? extensions = null, string? filterName = null)
    {
        var topLevel = TopLevel.GetTopLevel(_owner);
        if (topLevel?.StorageProvider == null) return null;

        var options = new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = await SuggestedStartLocationAsync(topLevel)
        };

        var patterns = extensions?.Where(ext => !string.IsNullOrWhiteSpace(ext) && ext != "*")
            .Select(ext => $"*{ext}").Where(pattern => pattern != "**").ToList() ?? new List<string>();
        var filters = new List<FilePickerFileType>();
        if (patterns.Count > 0)
        {
            filters.Add(new FilePickerFileType(filterName ?? "Files") { Patterns = patterns });
        }

        if (filters.Count == 0 || (extensions?.Contains("*") ?? false))
        {
            filters.Add(new FilePickerFileType("All files") { Patterns = new List<string> { "*" } });
        }

        options.FileTypeFilter = filters;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(options);
        var selected = files?.FirstOrDefault()?.Path.LocalPath;
        if (!string.IsNullOrWhiteSpace(selected)) UserSettings.RememberDirectory(selected);
        return selected;
    }
}
