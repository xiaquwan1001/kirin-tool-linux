// Licensed to Kethily Daniel & NDXCode under one or more agreements.
// Kethily Daniel & NDXCode licenses this file to you under the Business Source License 1.1.
// See the LICENSE file in the project root for more information.

/*
 * Copyright (c) 2026 Kethily Daniel & NDXCode. All rights reserved.
 *
 * Use of this software is governed by the Business Source License included
 * in the LICENSE file and at www.mariadb.com/bsl11.
 *
 * Change Date: Four years from the date each version of the Licensed Work
 * is first publicly distributed.
 *
 * On the Change Date, in accordance with the Business Source License,
 * use of this software will be governed by the GNU General Public License v3.0
 * or later (GPL-3.0-or-later).
 *
 * Contact Information: https://kirintool.cfd
 */

using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Kirin_Tool.Services;
using Kirin_Tool.Utils;

namespace Kirin_Tool;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // The persisted theme (light by default) is applied before the first
        // window is created so nothing is rendered in the wrong variant.
        UserSettings.EnsureInitialized();
        RequestedThemeVariant = UserSettings.CurrentThemeVariant;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Global crash net: log through the existing diagnostic channel and, for
        // UI-thread exceptions, show an error dialog and keep the process alive.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            ProcessRunner.WriteDiagnostic(
                "AppDomain.UnhandledException (terminating=" + args.IsTerminating + ")",
                args.ExceptionObject?.ToString() ?? "null");

        Dispatcher.UIThread.UnhandledException += (_, args) =>
        {
            ProcessRunner.WriteDiagnostic("Dispatcher.UIThread.UnhandledException", args.Exception.ToString());
            args.Handled = true;
            ShowGlobalErrorDialog(args.Exception);
        };

        UserSettings.ApplyTheme();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Best-effort crash dialog for unhandled UI-thread exceptions. Must never
    /// throw: any failure here is swallowed so the process stays alive.
    /// </summary>
    private static void ShowGlobalErrorDialog(Exception exception)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

                var message = new Avalonia.Controls.TextBlock
                {
                    Text = "An unexpected error occurred. Details were written to the diagnostic log.\n\n" +
                           exception.Message,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Margin = new Avalonia.Thickness(16)
                };
                var closeButton = new Avalonia.Controls.Button
                {
                    Content = "OK",
                    Margin = new Avalonia.Thickness(16, 0, 16, 16),
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right
                };
                var dialog = new Avalonia.Controls.Window
                {
                    Title = "Unexpected Error",
                    Width = 460,
                    SizeToContent = Avalonia.Controls.SizeToContent.Height,
                    Content = new Avalonia.Controls.StackPanel { Children = { message, closeButton } }
                };
                closeButton.Click += (_, _) => dialog.Close();

                if (owner != null && owner.IsVisible)
                    await dialog.ShowDialog(owner);
                else
                    dialog.Show();
            }
            catch
            {
                // The crash reporter must not become the crash itself.
            }
        });
    }
}
