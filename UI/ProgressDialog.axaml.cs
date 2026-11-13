// Licensed to Kethily Daniel & NDXCode under one or more agreements.
// Kethily Daniel & NDXCode licenses this file to you under the Business Source License 1.1.
// See the LICENSE file in the project root for more information.

using Avalonia.Controls;
using Avalonia.Threading;
using Kirin_Tool.Models;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace Kirin_Tool.UI
{
    public partial class ProgressDialog : Window
    {
        public ObservableCollection<ProgressItemViewModel> ProgressItems { get; }

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

        public void ShowCloseButton(bool isSuccess = true, string title = null)
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
