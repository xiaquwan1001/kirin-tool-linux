// Licensed to Kethily Daniel & NDXCode under one or more agreements.
// Kethily Daniel & NDXCode licenses this file to you under the Business Source License 1.1.
// See the LICENSE file in the project root for more information.

using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Kirin_Tool.Models;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace Kirin_Tool.UI
{
    public partial class ProgressDialog : Window
    {
        // The parameterless ctor (designer path) is never used at runtime; every caller uses the
  // items ctor. An empty collection keeps the null-state definite with no behaviour change.
        public ObservableCollection<ProgressItemViewModel> ProgressItems { get; } = new();

        public ProgressDialog()
        {
            InitializeComponent();
        }

        public ProgressDialog(ObservableCollection<ProgressItemViewModel> items)
        {
            InitializeComponent();
            ProgressItems = items;
            DataContext = this;
        }

        public void UpdateOverallStatus(string status)
        {
            Dispatcher.UIThread.InvokeAsync(() => OverallStatusTextBlock.Text = status);
        }

        /// <summary>
        /// Shows the VCOM audit log path in the dialog. The file stays on disk after the run, so
        /// the path is left visible (and selectable) once the operation finishes.
        /// </summary>
        public void ShowLogPath(string logPath)
        {
            if (string.IsNullOrWhiteSpace(logPath)) return;
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                LogPathTextBlock.Text = "log: " + logPath;
                LogPathTextBlock.IsVisible = true;
            });
        }

        /// <summary>
        /// Shows the operation *session directory* (v2.4.15 log merge), next to the log path:
        /// one directory holds session.jsonl (the machine timeline), the human transcript and
        /// summary.json, so after a failed attempt everything about it is in one place.
        /// </summary>
        public void ShowSessionDirectory(string sessionDirectory)
        {
            if (string.IsNullOrWhiteSpace(sessionDirectory)) return;
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                SessionPathTextBlock.Text = "session: " + sessionDirectory;
                SessionPathTextBlock.IsVisible = true;
            });
        }

        /// <summary>
        /// Honest close state. Green only when the device confirmed the operation; amber
        /// "Uploaded — Unverified" when the bytes went out but no device-side evidence came back
        /// (audit F01/F02: the dialog used to say "Unlock Complete" for a bare upload).
        /// The counts that decided it are shown right under the title, so the user can see
        /// whether the device answered at all.
        /// </summary>
        public void ShowVerdict(bool verified, string title, string detail)
        {
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                CloseButton.IsVisible = true;
                Title = title ?? (verified ? "Operation Complete" : "Operation Complete — Unverified");
                if (!string.IsNullOrWhiteSpace(detail))
                {
                    OverallStatusTextBlock.Text = detail;
                    OverallStatusTextBlock.Foreground = new SolidColorBrush(
                        Color.Parse(verified ? "#2E7D32" : "#B26A00"));
                }
            });
        }

        public void ShowCloseButton(bool isSuccess = true, string? title = null)
        {
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                CloseButton.IsVisible = true;
                Title = title ?? (isSuccess ? "Operation Complete" : "Operation Failed");
            });
        }

        private void CloseButton_Click(object sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            Close();
        }

        public void EnableOKButton(bool isSuccess = true)
        {
            ShowCloseButton(isSuccess);
        }
    }
}
