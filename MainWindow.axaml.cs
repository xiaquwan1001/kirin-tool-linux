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

using Kirin_Tool.Models;
using Kirin_Tool.Services;
using Kirin_Tool.Services.USBUpdate;
using Kirin_Tool.UI;
using Kirin_Tool.Utils;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Policy;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Interactivity;

namespace Kirin_Tool
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private readonly FastbootClient _fastbootClient;
        private readonly FastbootClient _xmlFastbootClient;
        private readonly FirmwareUnlocker _firmwareUnlocker;
        private readonly OemInfoService _oemInfoService;
        private readonly Services.DialogService _dialogService;
        private readonly FastbootFlasherService _fastbootFlasherService;
        private readonly USBUpdateFlasherService _usbUpdateFlasherService;
        private readonly SoftwareTestpointService _swTpService;
        private TaskCompletionSource<bool>? _globalInteractionTcs;

        public FullOtaFile BasePtableFile { get; set; } = new FullOtaFile { FileType = "BasePtable", DisplayName = "Base PTABLE" };
        public FullOtaFile BaseUpdateFile { get; set; } = new FullOtaFile { FileType = "BaseUpdate", DisplayName = "Base UPDATE" };
        public FullOtaFile CustPtableFile { get; set; } = new FullOtaFile { FileType = "CustPtable", DisplayName = "Cust PTABLE" };
        public FullOtaFile CustUpdateFile { get; set; } = new FullOtaFile { FileType = "CustUpdate", DisplayName = "Cust UPDATE" };
        public FullOtaFile PreloadPtableFile { get; set; } = new FullOtaFile { FileType = "PreloadPtable", DisplayName = "Preload PTABLE" };
        public FullOtaFile PreloadUpdateFile { get; set; } = new FullOtaFile { FileType = "PreloadUpdate", DisplayName = "Preload UPDATE" };
        public FullOtaFile SwTpUpdateFile { get; set; } = new FullOtaFile { FileType = "SwTpUpdate", DisplayName = "SW TP Base UPDATE" };
        public FullOtaFile UsbUpdateFile { get; set; } = new FullOtaFile { FileType = "UsbUpdate", DisplayName = "USB Update APP" };

        private bool _isUsbUpdateSelected;
        public bool IsUsbUpdateSelected
        {
            get => _isUsbUpdateSelected;
            set
            {
                if (_isUsbUpdateSelected == value) return;
                _isUsbUpdateSelected = value;
                OnPropertyChanged(nameof(IsUsbUpdateSelected));
                OnPropertyChanged(nameof(IsFastbootUpdateSelected));
                UpdateEditButtonsState();
                OnPropertyChanged(nameof(FullOtaStatusMessage));
                OnPropertyChanged(nameof(ShowFullOtaWarning));
                OnPropertyChanged(nameof(FullOtaWarningMessage));
                RefreshAllPartitionSelections();
            }
        }

        private void UpdateEditButtonsState()
        {
            bool isFastboot = IsFastbootUpdateSelected;
            BasePtableFile.IsEditEnabled = isFastboot && BasePtableFile.HasFile;
            BaseUpdateFile.IsEditEnabled = isFastboot && BaseUpdateFile.HasFile;
            CustPtableFile.IsEditEnabled = false;
            CustUpdateFile.IsEditEnabled = isFastboot && CustUpdateFile.HasFile;
            PreloadPtableFile.IsEditEnabled = false;
            PreloadUpdateFile.IsEditEnabled = isFastboot && PreloadUpdateFile.HasFile;
            SwTpUpdateFile.IsEditEnabled = isFastboot && SwTpUpdateFile.HasFile;
            UsbUpdateFile.IsEditEnabled = isFastboot && UsbUpdateFile.HasFile;
        }

        public bool IsFastbootUpdateSelected
        {
            get => !IsUsbUpdateSelected;
            set => IsUsbUpdateSelected = !value;
        }

        public bool CanStartFullOta => BasePtableFile.HasFile || BaseUpdateFile.HasFile ||
                               CustUpdateFile.HasFile || PreloadUpdateFile.HasFile ||
                               CustPtableFile.HasFile || PreloadPtableFile.HasFile;

        public string FullOtaStatusMessage
        {
            get
            {
                if (IsUsbUpdateSelected)
                {
                    var selectedFiles = new[] { BasePtableFile, CustPtableFile, PreloadPtableFile, BaseUpdateFile, CustUpdateFile, PreloadUpdateFile }
                                       .Count(f => f.HasFile);

                    if (selectedFiles == 0)
                        return "0/6 files selected. Please select at least one file to continue.";
                    else if (selectedFiles == 6)
                        return "6/6 files selected. Ready for complete USB update flash.";
                    else
                        return $"{selectedFiles}/6 files selected. Warning: Incomplete USB update flash";
                }
                else
                {
                    var selectedFiles = new[] { BasePtableFile, BaseUpdateFile, CustUpdateFile, PreloadUpdateFile }
                                       .Count(f => f.HasFile);

                    if (selectedFiles == 0)
                        return "0/4 files selected. Please select at least one file to continue.";
                    else if (selectedFiles == 4)
                        return "4/4 files selected. Ready for complete OTA flash.";
                    else
                        return $"{selectedFiles}/4 files selected. Warning: Incomplete Full OTA flash";
                }
            }
        }

        public MainWindow()
        {
            InitializeComponent();

            _dialogService = new Services.DialogService(this);

            _fastbootClient = new FastbootClient();
            _xmlFastbootClient = new FastbootClient();
            _firmwareUnlocker = new FirmwareUnlocker();
            _oemInfoService = new OemInfoService(_fastbootClient);
            _fastbootFlasherService = new FastbootFlasherService(_xmlFastbootClient);

            _usbUpdateFlasherService = new USBUpdateFlasherService(
                msg => ProcessRunner.WriteDiagnostic("USB Update", msg),
                "dload", 
                async (title, message) => await ShowGlobalInteractionPromptAsync($"{title}: {message}")
            );

            _swTpService = new SoftwareTestpointService(
                msg => ProcessRunner.WriteDiagnostic("Software Testpoint", msg),
                "dload",
                _fastbootClient,
                async (title, message) => await ShowGlobalInteractionPromptAsync($"{title}: {message}")
            );

            Closed += (_, _) => _extractionCancellation?.Cancel();
            Opened += async (_, _) => await RefreshUdevNoticeAsync();
            Activated += async (_, _) => await RefreshUdevNoticeAsync();
            SetupNavigationItemHandlers();
            SetupEventHandlers();
            InitializeFullOta();
            HookUserSettingsPersistence();
            RestoreUserSettings();

            ShowWelcomePage();
        }

        private void InitializeFullOta()
        {
            this.DataContext = this;

            BasePtableFile.PropertyChanged += (s, e) =>
            {
                OnPropertyChanged(nameof(CanStartFullOta));
                OnPropertyChanged(nameof(ShowFullOtaWarning));
            };
            BaseUpdateFile.PropertyChanged += (s, e) =>
            {
                OnPropertyChanged(nameof(CanStartFullOta));
                OnPropertyChanged(nameof(ShowFullOtaWarning));
            };
            CustUpdateFile.PropertyChanged += (s, e) =>
            {
                OnPropertyChanged(nameof(CanStartFullOta));
                OnPropertyChanged(nameof(ShowFullOtaWarning));
            };
            PreloadUpdateFile.PropertyChanged += (s, e) =>
            {
                OnPropertyChanged(nameof(CanStartFullOta));
                OnPropertyChanged(nameof(ShowFullOtaWarning));
            };
            UsbUpdateFile.PropertyChanged += (s, e) =>
            {
            };

            BasePtableFile.PropertyChanged += (s, e) => OnPropertyChanged(nameof(FullOtaStatusMessage));
            BaseUpdateFile.PropertyChanged += (s, e) => OnPropertyChanged(nameof(FullOtaStatusMessage));
            CustPtableFile.PropertyChanged += (s, e) => OnPropertyChanged(nameof(FullOtaStatusMessage));
            CustUpdateFile.PropertyChanged += (s, e) => OnPropertyChanged(nameof(FullOtaStatusMessage));
            PreloadPtableFile.PropertyChanged += (s, e) => OnPropertyChanged(nameof(FullOtaStatusMessage));
            PreloadUpdateFile.PropertyChanged += (s, e) => OnPropertyChanged(nameof(FullOtaStatusMessage));

            BasePtableFile.PropertyChanged += FullOtaFile_PropertyChanged;
            BaseUpdateFile.PropertyChanged += FullOtaFile_PropertyChanged;
            CustPtableFile.PropertyChanged += FullOtaFile_PropertyChanged;
            CustUpdateFile.PropertyChanged += FullOtaFile_PropertyChanged;
            PreloadPtableFile.PropertyChanged += FullOtaFile_PropertyChanged;
            PreloadUpdateFile.PropertyChanged += FullOtaFile_PropertyChanged;
            SwTpUpdateFile.PropertyChanged += FullOtaFile_PropertyChanged;
            UsbUpdateFile.PropertyChanged += FullOtaFile_PropertyChanged;
        }

        private void RefreshAllPartitionSelections()
        {
            var files = new[] { BasePtableFile, BaseUpdateFile, CustPtableFile, CustUpdateFile, PreloadPtableFile, PreloadUpdateFile, SwTpUpdateFile, UsbUpdateFile };
            foreach (var file in files)
            {
                if (file.HasFile && File.Exists(file.FilePath))
                {
                    using (var updateApp = new UpdateApp(file.FilePath, IsUsbUpdateSelected))
                    {
                        file.AvailablePartitions = new List<PartitionInfo>(updateApp.Partitions);
                    }
                }

                if (file.AvailablePartitions != null)
                {
                    file.SelectedPartitions = file.AvailablePartitions
                        .Where(p => !IsSkipPartition(p.Name, IsUsbUpdateSelected))
                        .ToList();
                }
            }
        }

        private async void DumpPartitions_Click(object sender, RoutedEventArgs e)
        {
            if (!await AllowChipOperationAsync("dump-partitions")) return;
            if (!await _xmlFastbootClient.IsDeviceConnected())
            {
                await ShowMessageBox("Device Not Found", "No device detected in fastboot mode. Please connect your device and try again.");
                return;
            }

            try
            {

                var partitions = await _fastbootFlasherService.GetPartitionTableAsync();

                if (partitions.Count == 0)
                {
                    await ShowMessageBox("No Partitions Found", "No partitions found on the device.");
                    return;
                }

                var partitionSelector = new FastbootPartitionSelector(partitions);
                partitionSelector.SkipSecureCheckBox.IsVisible = false;
                var dialogResult = await _dialogService.ShowDialog(partitionSelector);

                if (dialogResult != true && !partitionSelector.DialogResult)
                {
                    return;
                }

                if (partitionSelector.SelectedPartitions == null)
                {
                    partitionSelector.SetSelectedPartitions();
                }

                var selectedPartitions = partitionSelector.SelectedPartitions;

                if (selectedPartitions == null || selectedPartitions.Count == 0)
                {
                    await ShowMessageBox("No Partitions Selected", "Please select at least one partition to dump.");
                    return;
                }

                var folderDialog = new OpenFolderDialog
                {
                    Title = "Select Directory to Save Partitions"
                };

                var folderResult = await folderDialog.ShowAsync(this);
                if (string.IsNullOrWhiteSpace(folderResult))
                    return;

                var saveDirectory = folderResult;
                Directory.CreateDirectory(saveDirectory);

                await DumpPartitionsProcess(selectedPartitions, saveDirectory);
            }
            catch (Exception ex)
            {
                await ShowMessageBox("Dump Error", $"Failed to dump partitions: {ex.Message}");
            }
        }

        private List<PartitionInfo> ConvertToPartitionInfo(List<FastbootPartition> fastbootPartitions)
        {
            return fastbootPartitions.Select(fp => new PartitionInfo
            {
                Name = fp.Name,
                Size = 0,
                FormattedSize = "Unknown",
                DataOffset = 0,
                EntryOffset = 0,
                UpdateAppFilePath = fp.DumpPath ?? string.Empty
            }).ToList();
        }

        private async void FlashFromXml_Click(object sender, RoutedEventArgs e)
        {
            if (!await AllowChipOperationAsync("flash-xml")) return;
            if (!await _xmlFastbootClient.IsDeviceConnected())
            {
                await ShowMessageBox("Device Not Found", "No device detected in fastboot mode. Please connect your device and try again.");
                return;
            }

            try
            {
                var selectedFile = await _dialogService.ShowOpenFileDialog(
                    "Select Fastboot Configuration XML",
                    new List<string> { "xml", "*" },
                    "XML files");

                if (string.IsNullOrWhiteSpace(selectedFile))
                    return;

                UserSettings.FastbootConfigXml = selectedFile;
                var partitions = _fastbootFlasherService.ParseFlashingXml(selectedFile);

                if (partitions.Count == 0)
                {
                    await ShowMessageBox("Invalid XML", "No valid partition configurations found in the XML file.");
                    return;
                }

                var missingFiles = partitions.Where(p => !File.Exists(p.DumpPath)).ToList();
                if (missingFiles.Count > 0)
                {
                    var missingList = string.Join("\n", missingFiles.Select(p => $"- {p.DumpPath}"));
                    await ShowMessageBox("Missing Files", $"The following image files were not found:\n\n{missingList}\n\nPlease ensure all image files are accessible from the XML's directory.");
                    return;
                }

                var partitionSelector = new FastbootPartitionSelector(partitions);
                var dialogResult = await _dialogService.ShowDialog(partitionSelector);

                if (dialogResult != true && !partitionSelector.DialogResult)
                {

                    return;
                }

                if (partitionSelector.SelectedPartitions == null)
                {
                    partitionSelector.SetSelectedPartitions();
                }

                var selectedPartitions = partitionSelector.SelectedPartitions;

                if (selectedPartitions == null || selectedPartitions.Count == 0)
                {
                    await ShowMessageBox("No Partitions Selected", "Please select at least one partition to flash.");
                    return;
                }

                var skipSecure = partitionSelector.SkipSecurePartitions;
                bool allowRedline = await ConfirmRedlineWriteAsync(
                    selectedPartitions
                        .Where(p => !(skipSecure && IsSecurePartition(p.Name, p.Identifier)))
                        .SelectMany(p => new[] { p.Name, p.Identifier }),
                    "Flashing the selected partitions of the flashing XML");
                if (!allowRedline)
                {
                    return;
                }

                await FlashPartitionsProcess(selectedPartitions, skipSecure, allowRedline);
            }
            catch (Exception ex)
            {
                await ShowMessageBox("Flash Error", $"Failed to flash partitions: {ex.Message}");
            }
        }

        private async Task DumpPartitionsProcess(List<FastbootPartition> selectedPartitions, string dumpDirectory)
        {
            using var cancellationTokenSource = new CancellationTokenSource();

            var partitionInfos = ConvertToPartitionInfo(selectedPartitions);

            var progressDialog = new ProcessDialogUapp(partitionInfos, cancellationTokenSource);

            bool dialogClosed = false;
            progressDialog.RequestClose += (s, e) =>
            {
                dialogClosed = true;
                cancellationTokenSource.Cancel();
            };

            try
            {
                var dialogTask = _dialogService.ShowDialog(progressDialog);

                var dumpResult = await Task.Run(async () =>
                {
                    return await DumpPartitionsWithProgress(selectedPartitions, dumpDirectory, progressDialog, cancellationTokenSource.Token);
                }, cancellationTokenSource.Token);

                progressDialog.SetOverallComplete(dumpResult.IsSuccess, dumpResult.Message);

                if (dumpResult.IsSuccess)
                {
                    Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        progressDialog.OverallStatusText = $"Successfully dumped all {dumpResult.SuccessCount} partitions!";
                        progressDialog.CurrentOperationText = $"Dumping completed successfully.";
                    });
                }
                else
                {
                    Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        progressDialog.OverallStatusText = $"Completed with {dumpResult.SuccessCount}/{dumpResult.TotalCount} successful";
                        progressDialog.CurrentOperationText = $"Dumping completed with errors.";
                    });
                }

                while (!dialogClosed)
                {
                    await Task.Delay(100);
                    if (dialogTask.IsCompleted) break;
                }

                if (dialogClosed)
                {
                    cancellationTokenSource.Cancel();
                }

                try
                {
                    await Task.WhenAny(dialogTask, Task.Delay(1000));
                }
                catch (OperationCanceledException) { }
            }
            catch (OperationCanceledException)
            {
                progressDialog.SetOverallComplete(false, "Dump operation was cancelled by user");
            }
            catch (Exception ex)
            {
                progressDialog.SetOverallComplete(false, $"Dump failed: {ex.Message}");
            }
        }

        private async Task<(bool IsSuccess, string Message, int SuccessCount, int TotalCount)> DumpPartitionsWithProgress(
        List<FastbootPartition> selectedPartitions,
        string dumpDirectory,
        ProcessDialogUapp progressDialog,
        CancellationToken cancellationToken)
        {
            int successCount = 0;
            int totalCount = selectedPartitions.Count;
            string lastError = string.Empty;
            var dumpedPartitions = new List<FastbootPartition>();

            try
            {
                var fastbootImageDir = Path.Combine(dumpDirectory, "fastbootimage");
                if (!Directory.Exists(fastbootImageDir))
                {
                    Directory.CreateDirectory(fastbootImageDir);
                }

                for (int i = 0; i < selectedPartitions.Count; i++)
                {
                    var partition = selectedPartitions[i];

                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        progressDialog.UpdateCurrentPartition(partition.Name, "Dumping...", 50);

                        Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            progressDialog.CurrentOperationText = $"Dumping {partition.Name}...";
                        });

                        var dumpPath = Path.Combine(fastbootImageDir, partition.ImageFileName);
                        var result = await _fastbootFlasherService.DumpPartitionAsync(partition.Identifier, dumpPath);

                        if (result.IsSuccess && File.Exists(dumpPath))
                        {
                            progressDialog.CompletePartition(partition.Name, true);
                            partition.DumpPath = dumpPath;
                            dumpedPartitions.Add(partition);
                            successCount++;
                        }
                        else
                        {
                            progressDialog.CompletePartition(partition.Name, false, "Dump failed");
                            lastError = result.Message;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        progressDialog.CompletePartition(partition.Name, false, "Cancelled");
                        throw;
                    }
                    catch (Exception ex)
                    {
                        progressDialog.CompletePartition(partition.Name, false, ex.Message);
                        lastError = ex.Message;
                    }
                }

                if (dumpedPartitions.Count > 0)
                {
                    var xmlPath = Path.Combine(dumpDirectory, "flash.xml");
                    _fastbootFlasherService.GenerateFlashingXml(dumpedPartitions, xmlPath);
                }
            }
            catch (OperationCanceledException)
            {
                return (false, "Dump operation was cancelled by user", successCount, totalCount);
            }

            bool allSuccessful = successCount == totalCount;
            string message = allSuccessful
                ? $"Successfully dumped all {totalCount} partitions to fastbootimage directory!"
                : $"Dump completed with {successCount}/{totalCount} successful.";

            return (allSuccessful, message, successCount, totalCount);
        }

        private async Task FlashPartitionsProcess(List<FastbootPartition> selectedPartitions, bool skipSecure = false, bool allowRedline = false)
        {

            var partitionInfos = ConvertToPartitionInfo(selectedPartitions);

            using var cancellationTokenSource = new CancellationTokenSource();
            var progressDialog = new ProcessDialogUapp(partitionInfos, cancellationTokenSource);

            bool dialogClosed = false;
            progressDialog.RequestClose += (s, e) =>
            {
                dialogClosed = true;
                cancellationTokenSource.Cancel();
            };

            try
            {
                var dialogTask = _dialogService.ShowDialog(progressDialog);

                var flashResult = await Task.Run(async () =>
                {
                    return await FlashPartitionsWithProgress(selectedPartitions, progressDialog, cancellationTokenSource.Token, skipSecure, allowRedline);
                }, cancellationTokenSource.Token);

                progressDialog.SetOverallComplete(flashResult.IsSuccess, flashResult.Message);

                while (!dialogClosed)
                {
                    await Task.Delay(100);
                    if (dialogTask.IsCompleted) break;
                }

                if (dialogClosed)
                {
                    cancellationTokenSource.Cancel();
                }

                try
                {
                    await Task.WhenAny(dialogTask, Task.Delay(1000));
                }
                catch (OperationCanceledException) { }
            }
            catch (OperationCanceledException)
            {
                progressDialog.SetOverallComplete(false, "Flash operation was cancelled by user");
            }
            catch (Exception ex)
            {
                progressDialog.SetOverallComplete(false, $"Flash failed: {ex.Message}");
            }
        }

        // TODO(battery-gate): project convention is to refuse writes below 50% battery;
        // the Huawei loader getvar field name for battery level is not yet confirmed on
        // a real device, so no gate is implemented here.
        private async Task<(bool IsSuccess, string Message)> FlashPartitionsWithProgress(
        List<FastbootPartition> selectedPartitions,
        ProcessDialogUapp progressDialog,
        CancellationToken cancellationToken,
        bool skipSecure = false,
        bool allowRedline = false)
        {
            int successCount = 0;
            int totalCount = selectedPartitions.Count;
            string lastError = string.Empty;

            try
            {

                for (int i = 0; i < selectedPartitions.Count; i++)
                {
                    var partition = selectedPartitions[i];

                    if (IsSecurePartition(partition.Name, partition.Identifier))
                    {
                        if (skipSecure)
                        {
                            progressDialog.CompletePartition(partition.Name, true, "Skipped");
                            successCount++;
                            continue;
                        }

                        // Defensive second gate: the entry point already asked the user, but a
                        // redline partition is never written without that explicit opt-in.
                        if (!allowRedline)
                        {
                            progressDialog.CompletePartition(partition.Name, false, "Blocked: redline partition");
                            lastError = $"Blocked write to redline partition {partition.Name}";
                            continue;
                        }
                    }

                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        progressDialog.UpdateCurrentPartition(partition.Name, "Flashing...", 50);

                        Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            progressDialog.CurrentOperationText = $"Flashing {partition.Name}...";
                        });

                        var result = await _fastbootFlasherService.FlashPartitionAsync(partition.Identifier, partition.DumpPath);

                        // Exit code 0 alone is not proof: a write only counts when the
                        // fastboot client printed OKAY for it.
                        bool flashConfirmed = result.IsSuccess &&
                            (result.Message ?? string.Empty).Contains("OKAY", StringComparison.OrdinalIgnoreCase);

                        if (flashConfirmed)
                        {
                            progressDialog.CompletePartition(partition.Name, true);
                            successCount++;
                        }
                        else
                        {
                            progressDialog.CompletePartition(partition.Name, false, FlashFailureDescription(result.Message));
                            lastError = result.Message;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        progressDialog.CompletePartition(partition.Name, false, "Cancelled");
                        throw;
                    }
                    catch (Exception ex)
                    {
                        progressDialog.CompletePartition(partition.Name, false, ex.Message);
                        lastError = ex.Message;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return (false, "Flash operation was cancelled by user");
            }

            bool allSuccessful = successCount == totalCount;
            string message = allSuccessful
                ? $"Successfully flashed all {totalCount} partitions!"
                : $"Flash completed with {successCount}/{totalCount} successful.";

            return (allSuccessful, message);
        }

        public bool ShowFullOtaWarning
        {
            get
            {
                var selectedFiles = IsUsbUpdateSelected
                    ? new[] { BasePtableFile, CustPtableFile, PreloadPtableFile, BaseUpdateFile, CustUpdateFile, PreloadUpdateFile }.Count(f => f.HasFile)
                    : new[] { BasePtableFile, BaseUpdateFile, CustUpdateFile, PreloadUpdateFile }.Count(f => f.HasFile);

                int maxFiles = IsUsbUpdateSelected ? 6 : 4;
                return selectedFiles > 0 && selectedFiles < maxFiles;
            }
        }

        public string FullOtaWarningMessage
        {
            get
            {
                int maxFiles = IsUsbUpdateSelected ? 6 : 4;
                return $"Not all {maxFiles} files are selected. This may result in incomplete firmware flashing.";
            }
        }

        private async void FullOtaFile_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // Entry-time snapshot: settings restore starts the parse without
            // letting a bad cached file interrupt startup with a dialog.
            bool restoringSettings = _restoringUserSettings;
            if (e.PropertyName == nameof(FullOtaFile.FilePath) && sender is FullOtaFile otaFile && !string.IsNullOrEmpty(otaFile.FilePath))
            {
                try
                {
                    await Task.Run(() =>
                    {
                        using var updateApp = new UpdateApp(otaFile.FilePath, IsUsbUpdateSelected);
                        otaFile.AvailablePartitions = new List<PartitionInfo>(updateApp.Partitions);

                        otaFile.SelectedPartitions = updateApp.Partitions
                            .Where(p => !IsSkipPartition(p.Name, IsUsbUpdateSelected))
                            .ToList();
                    });
                }
                catch (Exception ex)
                {
                    if (!restoringSettings)
                        await ShowMessageBox("Parse Error", $"Failed to parse {otaFile.DisplayName}: {ex.Message}");
                    otaFile.FilePath = null;
                }
            }
        }

        private void SetupNavigationItemHandlers()
        {
            NavigationListBox.SelectionChanged += (s, e) =>
            {
                if (NavigationListBox.SelectedItem is ListBoxItem item)
                {
                    HandleNavigation(item.Tag?.ToString() ?? "Welcome");
                }
            };
        }

        private void SetupEventHandlers()
        {
            TogLivePull.Checked += TogLivePull_Toggled;
            TogLivePull.Unchecked += TogLivePull_Toggled;
            CpuComboBox.SelectionChanged += CpuComboBox_SelectionChanged;

            
        }

        private void HandleNavigation(string pageName)
        {
            HideAllPages();
            switch (pageName)
            {
                case "Welcome": ShowWelcomePage(); break;
                case "DeviceInfo": ShowDeviceInfoPage(); break;
                case "Vcom": ShowVcomPage(); break;
                case "Flash": ShowFlashPage(); break;
                case "Security": ShowSecurityPage(); break;
                case "OEMInfo": ShowOEMInfoPage(); break;
                case "NVMe": ShowNVMePage(); break;
                case "Extractor": ExtractorPage.IsVisible = true; break;
                case "About": ShowAboutPage(); break;
            }
        }
        private void ShowWelcomePage() => WelcomePage.IsVisible = true;
        private void ShowDeviceInfoPage() => DeviceInfoPage.IsVisible = true;
        private void ShowVcomPage() => VcomPage.IsVisible = true;
        private void ShowFlashPage() => FlashPage.IsVisible = true;
        private void ShowSecurityPage()
        {
            SecurityPage.IsVisible = true;
            UpdateSecurityUI();
        }
        private void ShowOEMInfoPage()
        {
            OEMInfoPage.IsVisible = true;
            UpdateOemInfoUI();
        }
        private void ShowNVMePage() => NVMePage.IsVisible = true;
        private void ShowAboutPage() => AboutPage.IsVisible = true;
        private void HideAllPages()
        {
            WelcomePage.IsVisible = false;
            DeviceInfoPage.IsVisible = false;
            VcomPage.IsVisible = false;
            FlashPage.IsVisible = false;
            SecurityPage.IsVisible = false;
            OEMInfoPage.IsVisible = false;
            NVMePage.IsVisible = false;
            AboutPage.IsVisible = false;
            ExtractorPage.IsVisible = false;
        }

        private async void BrowseOtaFile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not string fileType)
                return;

            var selected = await _dialogService.ShowOpenFileDialog(
                $"Select {GetFileDisplayName(fileType)} File",
                new List<string> { "APP", "app", "*" },
                "UPDATE.APP files");

            if (!string.IsNullOrWhiteSpace(selected))
            {
                GetOtaFileByType(fileType).FilePath = selected;
                UpdateEditButtonsState();
            }
        }

        private async void EditOtaFile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not string fileType)
                return;

            var otaFile = GetOtaFileByType(fileType);
            if (!otaFile.HasFile) return;

            try
            {
                var partitionDialog = new PartitionSelectorDialog(otaFile.AvailablePartitions);

                foreach (var partition in partitionDialog.Partitions)
                {
                    partition.IsSelected = otaFile.SelectedPartitions.Any(sp => sp.Name == partition.Name);
                }

                await _dialogService.ShowDialog(partitionDialog);

                if (partitionDialog.DialogResult)
                {
                    partitionDialog.SetSelectedPartitions();
                    otaFile.SelectedPartitions = partitionDialog.SelectedPartitions;
                }
            }
            catch (Exception ex)
            {
                await ShowMessageBox("Edit Error", $"Failed to edit partitions for {otaFile.DisplayName}: {ex.Message}");
            }
        }

        private void ClearOtaFile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not string fileType)
                return;

            var otaFile = GetOtaFileByType(fileType);
            otaFile.FilePath = null;
            otaFile.AvailablePartitions = new List<PartitionInfo>();
            otaFile.SelectedPartitions = new List<PartitionInfo>();
            UpdateEditButtonsState();
        }

        private async void StartFullOtaButton_Click(object sender, RoutedEventArgs e)
        {
            if (!await AllowChipOperationAsync("full-ota")) return;
            var otaFileSet = IsUsbUpdateSelected
                ? new[] { BasePtableFile, CustPtableFile, PreloadPtableFile, BaseUpdateFile, CustUpdateFile, PreloadUpdateFile }
                : new[] { BasePtableFile, BaseUpdateFile, CustUpdateFile, PreloadUpdateFile };
            var selectedFiles = otaFileSet.Count(f => f.HasFile);

            if (selectedFiles == 0)
            {
                await ShowMessageBox("No Files Selected", "Please select at least one UPDATE.APP file before starting the flash.");
                return;
            }

            if (selectedFiles < otaFileSet.Length)
            {
                var missingFiles = new List<string>();
                foreach (var f in otaFileSet.Where(f => !f.HasFile))
                {
                    missingFiles.Add(f.DisplayName);
                }

                var warningMessage = $"Warning: You have only selected {selectedFiles}/{otaFileSet.Length} files for the Full OTA flash.\n\n" +
                                   $"Missing file(s): {string.Join(", ", missingFiles)}\n\n" +
                                   "Do you want to continue anyway?";

                var warningResult = await _dialogService.ShowConfirmDialog("Incomplete Full OTA Flash Warning", warningMessage, "Continue Anyway", "Cancel");

                if (warningResult != true)
                {
                    return;
                }
            }

            if (!IsUsbUpdateSelected && !await _fastbootClient.IsDeviceConnected())
            {
                await ShowMessageBox("Device Not Found", "No device detected in fastboot mode. Please connect your device and try again.");
                return;
            }

            if (IsUsbUpdateSelected)
            {
                await StartUsbUpdateFlash();
            }
            else
            {
                await StartFullOtaFlash();
            }
        }

        // TODO(battery-gate): project convention is to refuse writes below 50% battery;
        // the Huawei loader getvar field name for battery level is not yet confirmed on
        // a real device, so no gate is implemented here.
        private async Task StartUsbUpdateFlash()
        {
            using var cancellationTokenSource = new CancellationTokenSource();
            var filesData = new List<(string FilePath, string Label, List<string> SelectedPartitions)>();

            var sourceFiles = new[]
            {
                (BasePtableFile, "Base PTABLE"),
                (CustPtableFile, "Cust PTABLE"),
                (PreloadPtableFile, "Preload PTABLE"),
                (BaseUpdateFile, "Base UPDATE"),
                (CustUpdateFile, "Cust UPDATE"),
                (PreloadUpdateFile, "Preload UPDATE")
            };

            var allPartitions = new List<(PartitionInfo Partition, string Source)>();

            foreach (var (file, label) in sourceFiles)
            {
                if (file.HasFile)
                {
                    var selectedNames = file.SelectedPartitions?.Select(p => p.Name).ToList() ?? new List<string>();
                    filesData.Add((file.FilePath, label, selectedNames));

                    if (file.SelectedPartitions != null)
                    {
                        foreach (var partition in file.SelectedPartitions)
                        {
                            allPartitions.Add((partition, label));
                        }
                    }
                }
            }

            if (filesData.Count == 0 || allPartitions.Count == 0)
            {
                await ShowMessageBox("No Files Selected", "Please select at least one APP file to flash.");
                return;
            }

            // UX pre-confirmation only: this scan sees just the partitions the selected files
            // announce. The service re-checks the partitions it actually discovers after
            // unpacking and stays the final gate, so the flag is only set when the user
            // confirmed THIS operation for the names known up front.
            bool allowRedline = false;
            var redlinePreScan = FlashPolicy.RedlineHits(allPartitions.Select(p => p.Partition.Name));
            if (redlinePreScan.Count > 0)
            {
                allowRedline = await ConfirmRedlineWriteAsync(redlinePreScan, "The USB Update flash");
                if (!allowRedline) return;
            }

            var flashDialog = new ProcessDialogUapp(allPartitions, cancellationTokenSource);
            flashDialog.IsUsbUpdateMode = true;
            flashDialog.Title = "USB Update Flash Progress";
            bool dialogClosed = false;
            flashDialog.RequestClose += (s, e) =>
            {
                dialogClosed = true;
                cancellationTokenSource.Cancel();
            };

            var flashDialogTask = _dialogService.ShowDialog(flashDialog);

            try
            {
                _usbUpdateFlasherService.OnExtractionStarted += (label) =>
                {
                    Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        flashDialog.CurrentOperationText = $"Extracting {label}...";
                        flashDialog.OverallStatusText = $"Preparing files for USB Update flash...";
                    });
                };

                _usbUpdateFlasherService.OnExtractionProgress += (progress) =>
                {
                    Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        flashDialog.OverallProgress = progress;
                    });
                };

                _usbUpdateFlasherService.OnPartitionsDiscovered += (partitions) =>
                {
                    Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        var partitionItems = partitions.Select(p => (new PartitionInfo { Name = p.PartitionName }, p.SourceLabel)).ToList();
                        flashDialog.ReplacePartitions(partitionItems);
                    });

                    // F2 redline diff: partitions that only become visible after unpacking
                    // were never covered by the pre-scan confirmation. Ask once more for
                    // exactly that delta, before the service's post-discovery write gate.
                    var newRedlineHits = RedlinePartitionHits(partitions.Select(p => p.PartitionName))
                        .Where(name => !redlinePreScan.Contains(name, StringComparer.OrdinalIgnoreCase))
                        .ToList();
                    if (newRedlineHits.Count > 0 &&
                        !ConfirmRedlineDeltaOnBackground(newRedlineHits, "The USB Update flash (partitions discovered after unpacking)"))
                    {
                        // Declined (or timed out): cancel before any write happens; the
                        // service throws on the token right after its redline gate.
                        cancellationTokenSource.Cancel();
                        Dispatcher.UIThread.InvokeAsync(() =>
                            flashDialog.SetOverallComplete(false,
                                "USB Update flash aborted: newly discovered redline partition(s) were not confirmed: " +
                                string.Join(", ", newRedlineHits)));
                    }
                };

                _usbUpdateFlasherService.OnPartitionProgress += (index, name, progress) =>
                {
                    flashDialog.UpdateCurrentPartitionByIndex(index, progress == 100 ? "Finalizing..." : "Processing...", progress);
                };

                _usbUpdateFlasherService.OnPartitionCompleted += (index, name, success, message) =>
                {
                    flashDialog.CompletePartitionByIndex(index, success, message);
                };

                await Task.Run(() => _usbUpdateFlasherService.FlashPartitions(filesData, cancellationTokenSource.Token, allowRedline: allowRedline));

                flashDialog.SetOverallComplete(true, "USB Update flash completed successfully!");
            }
            catch (OperationCanceledException)
            {
                flashDialog.SetOverallComplete(false, "USB Update flash was cancelled by user");
            }
            catch (Exception ex)
            {
                string errorMsg = $"USB Update flash failed: {ex.Message}";
                flashDialog.SetOverallComplete(false, errorMsg);
            }
            finally
            {
                _usbUpdateFlasherService.OnExtractionStarted = null;
                _usbUpdateFlasherService.OnExtractionProgress = null;
                _usbUpdateFlasherService.OnPartitionsDiscovered = null;
                _usbUpdateFlasherService.OnPartitionProgress = null;
                _usbUpdateFlasherService.OnPartitionCompleted = null;

                while (!dialogClosed)
                {
                    await Task.Delay(100);
                    if (flashDialogTask.IsCompleted) break;
                }
            }
        }

        private async Task StartFullOtaFlash()
        {
            var filesToCheck = new[] { BasePtableFile, BaseUpdateFile, CustUpdateFile, PreloadUpdateFile };
            foreach (var file in filesToCheck)
            {
                if (file.HasFile && !string.IsNullOrEmpty(file.FilePath))
                {
                    string dir = Path.GetDirectoryName(file.FilePath) ?? string.Empty;
                    if (dir.Length + 60 >= 255)
                    {
                        var warningResult = await _dialogService.ShowConfirmDialog(
                            "Path Length Warning",
                            $"The folder path for '{file.DisplayName}' is too long\n\n" +
                            "The flash/extraction process is highly likely to fail.\n\n" +
                            "Do you want to continue anyway?",
                            "Continue Anyway", "Cancel");

                        if (warningResult != true)
                        {
                            return;
                        }
                        break;
                    }
                }
            }

            using var cancellationTokenSource = new CancellationTokenSource();

            var allPartitions = new List<(PartitionInfo Partition, string Source)>();

            if (BasePtableFile.HasFile && BasePtableFile.SelectedPartitions != null && BasePtableFile.SelectedPartitions.Count > 0)
            {
                foreach (var partition in BasePtableFile.SelectedPartitions)
                {
                    if (IsSkipPartition(partition.Name, false)) continue;
                    allPartitions.Add((partition, "Base PTABLE"));
                }
            }

            if (BaseUpdateFile.HasFile && BaseUpdateFile.SelectedPartitions != null && BaseUpdateFile.SelectedPartitions.Count > 0)
            {
                foreach (var partition in BaseUpdateFile.SelectedPartitions)
                {
                    if (IsSkipPartition(partition.Name, false)) continue;
                    allPartitions.Add((partition, "Base UPDATE"));
                }
            }

            if (CustUpdateFile.HasFile && CustUpdateFile.SelectedPartitions != null && CustUpdateFile.SelectedPartitions.Count > 0)
            {
                foreach (var partition in CustUpdateFile.SelectedPartitions)
                {
                    if (IsSkipPartition(partition.Name, false)) continue;
                    allPartitions.Add((partition, "Cust UPDATE"));
                }
            }

            if (PreloadUpdateFile.HasFile && PreloadUpdateFile.SelectedPartitions != null && PreloadUpdateFile.SelectedPartitions.Count > 0)
            {
                foreach (var partition in PreloadUpdateFile.SelectedPartitions)
                {
                    if (IsSkipPartition(partition.Name, false)) continue;
                    allPartitions.Add((partition, "Preload UPDATE"));
                }
            }

            if (allPartitions.Count == 0)
            {
                await ShowMessageBox("No Partitions", "No partitions selected for flashing. Please edit the file selections.");
                return;
            }

            bool allowRedline = await ConfirmRedlineWriteAsync(
                allPartitions.Select(p => p.Partition.Name),
                "The Full OTA flash");
            if (!allowRedline)
            {
                return;
            }

            var selectedFileTypes = new List<string>();
            if (BasePtableFile.HasFile) selectedFileTypes.Add("Base PTABLE");
            if (BaseUpdateFile.HasFile) selectedFileTypes.Add("Base UPDATE");
            if (CustUpdateFile.HasFile) selectedFileTypes.Add("Cust UPDATE");
            if (PreloadUpdateFile.HasFile) selectedFileTypes.Add("Preload UPDATE");

            var flashDialog = new ProcessDialogUapp(allPartitions, cancellationTokenSource);

            bool isComplete = selectedFileTypes.Count == 4;
            flashDialog.Title = isComplete ? "Full OTA Flash Progress" : "Partial OTA Flash Progress";

            bool dialogClosed = false;
            flashDialog.RequestClose += (s, e) =>
            {
                dialogClosed = true;
                cancellationTokenSource.Cancel();
            };

            try
            {
                var flashDialogTask = _dialogService.ShowDialog(flashDialog);

                var flashResult = await Task.Run(async () =>
                {
                    return await FlashFullOtaPartitions(allPartitions, flashDialog, cancellationTokenSource.Token, allowRedline);
                }, cancellationTokenSource.Token);

                flashDialog.SetOverallComplete(flashResult.IsSuccess, flashResult.Message);

                while (!dialogClosed)
                {
                    await Task.Delay(100);
                    if (flashDialogTask.IsCompleted) break;
                }

                if (dialogClosed)
                {
                    cancellationTokenSource.Cancel();
                }

                try
                {
                    await Task.WhenAny(flashDialogTask, Task.Delay(1000));
                }
                catch (OperationCanceledException) { }
            }
            catch (OperationCanceledException)
            {
                flashDialog.SetOverallComplete(false, "OTA flash was cancelled by user");
            }
            catch (Exception ex)
            {
                flashDialog.SetOverallComplete(false, $"OTA flash failed: {ex.Message}");
            }
        }

        // TODO(battery-gate): project convention is to refuse writes below 50% battery;
        // the Huawei loader getvar field name for battery level is not yet confirmed on
        // a real device, so no gate is implemented here.
        private async Task<(bool IsSuccess, string Message)> FlashFullOtaPartitions(
        List<(PartitionInfo Partition, string Source)> partitions,
        ProcessDialogUapp progressDialog,
        CancellationToken cancellationToken,
        bool allowRedline = false)
        {
            int successCount = 0;
            int totalCount = partitions.Count;
            string lastError = string.Empty;
            string tempDirectory = null;

            try
            {
                var updateAppPath = partitions.First().Partition.UpdateAppFilePath;
                var updateAppDirectory = Path.GetDirectoryName(updateAppPath);
                tempDirectory = Path.Combine(updateAppDirectory, $"temp_full_ota_{DateTime.Now:yyyyMMdd_HHmmss}");

                if (!Directory.Exists(tempDirectory))
                {
                    Directory.CreateDirectory(tempDirectory);
                }

                var superPartitions = partitions.Where(p => p.Partition.Name.Equals("super", StringComparison.OrdinalIgnoreCase)).ToList();
                if (superPartitions.Count > 1)
                {
                    progressDialog.SetOverallStatus("Merging super partitions...");

                    var mergePaths = new List<string>();
                    for (int i = 0; i < superPartitions.Count; i++)
                    {
                        var (partition, source) = superPartitions[i];
                        string partTempPath = Path.Combine(tempDirectory, $"super_part_{i}.img");
                        progressDialog.UpdateCurrentOperation($"Extracting super part {i+1} from {source}...");
                        await ExtractPartitionToTempDirectoryWithCancellation(partition, partTempPath, cancellationToken);
                        mergePaths.Add(partTempPath);
                    }

                    string mergedSuperPath = Path.Combine(tempDirectory, "super_merged.img");
                    progressDialog.UpdateCurrentOperation("Merging super partitions...");
                    string currentInput = mergePaths[0];
                    for (int i = 1; i < mergePaths.Count; i++)
                    {
                        progressDialog.UpdateCurrentOperation("Merging super partitions...");
                        string nextOutput = i == mergePaths.Count - 1 ? mergedSuperPath : Path.Combine(tempDirectory, $"super_intermediate_{i}.img");
                        await SuperMerger.MergeSuperImages(currentInput, mergePaths[i], nextOutput, p => {
                        });
                        currentInput = nextOutput;
                    }

                    var mergedInfo = new PartitionInfo
                    {
                        Name = "super",
                        Size = new FileInfo(mergedSuperPath).Length,
                        UpdateAppFilePath = mergedSuperPath,
                    };

                    mergedInfo.DataOffset = 0;
                    mergedInfo.UpdateAppFilePath = mergedSuperPath;

                    var firstSuperIndex = partitions.IndexOf(superPartitions[0]);
                    partitions.RemoveAll(p => p.Partition.Name.Equals("super", StringComparison.OrdinalIgnoreCase));
                    partitions.Insert(firstSuperIndex, (mergedInfo, "Merged Super"));

                    progressDialog.ReplacePartitions(partitions);
                    progressDialog.SetOverallStatus("Ready to flash");
                }

                totalCount = partitions.Count;

                for (int i = 0; i < partitions.Count; i++)
                {
                    var (partition, source) = partitions[i];
                    string tempFilePath = null;
                    string uniquePartitionId = $"{partition.Name} ({source})";

                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (!allowRedline && IsRedlinePartition(partition.Name))
                        {
                            progressDialog.CompletePartitionByIndex(i, false, "Blocked: redline partition");
                            lastError = $"Blocked write to redline partition {partition.Name}";
                            continue;
                        }

                        progressDialog.UpdateCurrentPartitionByIndex(i, source == "Merged Super" ? "Preparing merged image..." : $"Extracting from {source}...", 25);

                        tempFilePath = Path.Combine(tempDirectory, $"{partition.Name}_{source.Replace(" ", "_")}_{i}.img");

                        if (source == "Merged Super" && File.Exists(partition.UpdateAppFilePath))
                        {
                            tempFilePath = partition.UpdateAppFilePath;
                        }
                        else
                        {
                            await ExtractPartitionToTempDirectoryWithCancellation(partition, tempFilePath, cancellationToken);
                        }

                        cancellationToken.ThrowIfCancellationRequested();

                        progressDialog.UpdateCurrentPartitionByIndex(i, $"Flashing from {source}...", 75);

                        string flashPartitionName = partition.Name;
                        var flashResult = await _fastbootClient.FlashPartition(flashPartitionName, tempFilePath);

                        // Exit code 0 alone is not proof: a write only counts when the
                        // fastboot client printed OKAY for it.
                        bool flashConfirmed = flashResult.IsSuccess &&
                            (flashResult.Output ?? string.Empty).Contains("OKAY", StringComparison.OrdinalIgnoreCase);

                        if (flashConfirmed)
                        {
                            progressDialog.CompletePartitionByIndex(i, true);
                            successCount++;
                        }
                        else
                        {
                            progressDialog.CompletePartitionByIndex(i, false, FlashFailureDescription(flashResult.Output));
                            lastError = $"Command: fastboot {flashResult.Arguments}\nOutput: {flashResult.Output}";
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        progressDialog.CompletePartitionByIndex(i, false, "Cancelled");

                        if (tempFilePath != null && source != "Merged Super" && File.Exists(tempFilePath))
                        {
                            try { File.Delete(tempFilePath); } catch { }
                        }

                        throw;
                    }
                    catch (Exception ex)
                    {
                        progressDialog.CompletePartitionByIndex(i, false, ex.Message);
                        lastError = ex.Message;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                return (false, "Full OTA flash was cancelled by user");
            }
            finally
            {
                if (tempDirectory != null && Directory.Exists(tempDirectory))
                {
                    try
                    {
                        Directory.Delete(tempDirectory, true);
                    }
                    catch (Exception)
                    {
                    }
                }
            }

            bool allSuccessful = successCount == totalCount;
            string message = allSuccessful
                ? $"Successfully completed Full OTA flash! ({totalCount} partitions)"
                : $"Full OTA completed with {successCount}/{totalCount} successful.";

            return (allSuccessful, message);
        }

        private async void OperationButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not string command)
                return;

            await ExecuteCommand(command);
        }

        private async Task ExecuteCommand(string command)
        {
            if (!await AllowChipOperationAsync(command)) return;
            if (command != "unlock-fastboot" && command != "sw-testpoint-enter")
            {
                if (!await _fastbootClient.IsDeviceConnected())
                {
                    await ShowMessageBox("Device Not Found", "No device detected in fastboot mode. Please connect your device and try again.");
                    return;
                }
            }

            try
            {
                switch (command)
                {
                    case "unlock-fastboot":
                        await HandleUnlockFastboot();
                        break;

                    case "frp-remove":
                        await HandleFrpRemove();
                        break;

                    case "read-sn":
                        await HandleReadSerialNumber();
                        break;

                    case "write-sn":
                        await HandleWriteSerialNumber();
                        break;

                    case "unlock-bootloader":
                        await HandleUnlockBootloader();
                        break;

                    case "enable-downgrade":
                        await HandleEnableDowngrade();
                        break;

                    case "reboot-usb-update":
                        await HandleRebootToUsbUpdate();
                        break;

                    case "sw-testpoint-enter":
                        await HandleSoftwareTestpoint(true);
                        break;

                    case "sw-testpoint-exit":
                        await HandleSoftwareTestpoint(false);
                        break;
                }
            }
            catch (Exception ex)
            {
                await ShowMessageBox("Unhandled Exception", $"A critical error occurred: {ex.Message}");
            }
        }

        private async Task HandleUnlockFastboot()
        {
            string cpu = GetSelectedCpu();
            if (cpu == null || !FirmwareUnlocker.CpuAddresses.ContainsKey(cpu))
            {
                await ShowMessageBox("CPU Not Supported", "Please select a supported CPU model for the VCOM unlock operation.");
                return;
            }

            if (cpu == "hisi820" || cpu == "hisi985")
            {
                var warningResult = await _dialogService.ShowConfirmDialog(
                    "Important Warning",
                    "By loading unlocked fastboot on this device, you will permanently increment ARB, \nmeaning you cannot install any operating system older than HarmonyOS 2. \n\nAre you absolutely sure you want to continue?",
                    "Continue", "Cancel");

                if (warningResult != true)
                {
                    return;
                }
            }

            var progressItems = new ObservableCollection<ProgressItemViewModel>(
                FirmwareUnlocker.CpuAddresses[cpu].Select(p => new ProgressItemViewModel { FileName = p.Name, StatusText = "Pending" })
            );
            var dialog = new ProgressDialog(progressItems);
            var overallProgress = new Progress<string>(status => dialog.UpdateOverallStatus(status));

            var interactionHandler = new Func<string, Task<bool>>(async message =>
            {
                return await ShowGlobalInteractionPromptAsync(message, dialog);
            });

            bool useFastFlashLoader = cpu == "hisi980" && UseFastFlashLoaderSwitch.IsChecked == true;

            var dialogShowTask = _dialogService.ShowDialog(dialog);
            var unlockResult = await _firmwareUnlocker.UnlockFastboot(cpu, progressItems, overallProgress, interactionHandler, useFastFlashLoader);

            dialog.ShowCloseButton(unlockResult.IsSuccess, unlockResult.IsSuccess ? "Unlock Complete" : "Unlock Failed");
            await dialogShowTask;
        }

        private async Task HandleSoftwareTestpoint(bool enter)
        {
            if (!SwTpUpdateFile.HasFile)
            {
                await ShowMessageBox("File Required", "Please select a Base UPDATE file first.");
                return;
            }

            // The service returns normally only on its success path and throws on every failure
            // path (patch verification, VCOM transfer result, fastboot flash), so "no exception"
            // is the service's verdict - not a dialog default. It still does not prove that the
            // device reached the requested mode, which is why the texts below say what was and
            // was not confirmed.
            try
            {
                if (enter)
                {
                    await _swTpService.EnterSoftwareTestpoint(SwTpUpdateFile.FilePath);
                    await ShowMessageBox("Transfer Completed - Mode Unknown",
                        "The software-testpoint XLOADER transfer returned without an error report from the service.\n\n" +
                        "Whether the device actually switched to VCOM mode is Unknown from here: while the device sits in VCOM " +
                        "there is no fastboot read-back that could confirm it.\n\n" +
                        "Confirm it on the host before starting the USB Update flash: the device must enumerate as " +
                        "12d1:3609 (USB Update / VCOM port). If it does not, nothing was written and the testpoint entry has to be retried.");
                }
                else
                {
                    if (!await _fastbootClient.IsDeviceConnected())
                    {
                        await ShowMessageBox("Device Not Found", "Device not detected in fastboot mode.");
                        return;
                    }

                    await _swTpService.ExitSoftwareTestpoint(SwTpUpdateFile.FilePath);

                    // The service only checks the flash exit code, so read the device back before
                    // claiming the XLOADER restore worked.
                    var verify = await _fastbootClient.QueryVariableAsync("product");
                    switch (ClassifyCommandOutput(verify.Output))
                    {
                        case CommandOutcome.Succeeded:
                            await ShowMessageBox("Success",
                                "XLOADER was flashed back and the device answers fastboot getvar product again (OKAY).");
                            break;
                        case CommandOutcome.Failed:
                            await ShowMessageBox("Restore Not Confirmed",
                                "The XLOADER flash returned, but the device no longer answers fastboot getvar product.\n\n" +
                                $"Last output:\n{OutputTail(verify.Output)}");
                            break;
                        default:
                            await ShowMessageBox("No Write Confirmed",
                                "The XLOADER flash returned, but the read-back printed neither OKAY nor ERROR, so the restore is not confirmed.\n\n" +
                                $"Last output:\n{OutputTail(verify.Output)}");
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                ProcessRunner.WriteDiagnostic("Software Testpoint", ex.ToString());
                await ShowMessageBox("Error", ex.Message);
            }
        }


        private async Task ExtractPartitionToTempDirectoryWithCancellation(PartitionInfo partition, string outputPath, CancellationToken cancellationToken)
        {
            try
            {
                using var inputStream = new FileStream(partition.UpdateAppFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var outputStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);

                inputStream.Seek(partition.DataOffset, SeekOrigin.Begin);

                const int bufferSize = 65536;
                var buffer = new byte[bufferSize];
                long totalRead = 0;

                while (totalRead < partition.Size)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    int toRead = (int)Math.Min(bufferSize, partition.Size - totalRead);
                    int bytesRead = await inputStream.ReadAsync(buffer, 0, toRead, cancellationToken);

                    if (bytesRead == 0)
                        throw new EndOfStreamException($"Unexpected end of file while extracting {partition.Name}");

                    await outputStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                    totalRead += bytesRead;

                    if (totalRead % (bufferSize * 10) == 0)
                    {
                        double extractionProgress = (double)totalRead / partition.Size * 50;
                    }
                }

                await outputStream.FlushAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                if (File.Exists(outputPath))
                {
                    try
                    {
                        File.Delete(outputPath);
                    }
                    catch (Exception ex)
                    {
                    }
                }

                throw;
            }
            catch (Exception ex)
            {
                if (File.Exists(outputPath))
                {
                    try
                    {
                        File.Delete(outputPath);
                    }
                    catch { }
                }

                throw new InvalidOperationException($"Failed to extract partition data for {partition.Name}: {ex.Message}", ex);
            }
        }

        private async Task ExtractPartitionToTempDirectory(PartitionInfo partition, string outputPath)
        {
            try
            {
                using var inputStream = new FileStream(partition.UpdateAppFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var outputStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);

                inputStream.Seek(partition.DataOffset, SeekOrigin.Begin);

                const int bufferSize = 65536;
                var buffer = new byte[bufferSize];
                long totalRead = 0;

                while (totalRead < partition.Size)
                {
                    int toRead = (int)Math.Min(bufferSize, partition.Size - totalRead);
                    int bytesRead = await inputStream.ReadAsync(buffer, 0, toRead);

                    if (bytesRead == 0)
                        throw new EndOfStreamException($"Unexpected end of file while extracting {partition.Name}");

                    await outputStream.WriteAsync(buffer, 0, bytesRead);
                    totalRead += bytesRead;
                }

                await outputStream.FlushAsync();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to extract partition data for {partition.Name}: {ex.Message}", ex);
            }
        }

        private bool IsSkipPartition(string partitionName, bool isUsbMode = false)
        {
            if (isUsbMode) return false;
            return FlashPolicy.ShouldSkip(partitionName);
        }

        // Device-keyed partitions: a write here is unrecoverable without a verified backup.
        // The token set and the baseband exact-name set now live in Utils/FlashPolicy.cs, which
        // the CLI and the USB-update service share, so the GUI cannot drift away from them.
        internal static bool IsRedlinePartition(string partitionName) => FlashPolicy.IsRedline(partitionName);

        private static List<string> RedlinePartitionHits(IEnumerable<string> partitionNames) =>
            FlashPolicy.RedlineHits(partitionNames).ToList();

        /// <summary>
        /// Hard gate in front of every GUI write path. Returns false before a single write
        /// when the pending set touches a redline partition and the user does not explicitly
        /// confirm THIS operation; the confirmation is never remembered between operations.
        /// </summary>
        private async Task<bool> ConfirmRedlineWriteAsync(IEnumerable<string> partitionNames, string operation)
        {
            var hits = RedlinePartitionHits(partitionNames);
            if (hits.Count == 0) return true;

            string message =
                $"{operation} would write {hits.Count} device-keyed (redline) partition(s):\n\n" +
                string.Join("\n", hits.Select(name => $"- {name}")) +
                "\n\nThese partitions carry the device identity, baseband calibration and key/DRM material. " +
                "A wrong image is unrecoverable without a verified backup and can brick the device or permanently break the modem.\n\n" +
                "Nothing has been written yet. Continue only if you have a verified backup of these partitions.";

            bool confirmed = await _dialogService.ShowConfirmDialog("Redline Partition Write", message, "Write Anyway", "Cancel");
            return confirmed;
        }

        private bool IsSecurePartition(string name, string identifier)
        {
            return IsRedlinePartition(name) || IsRedlinePartition(identifier);
        }

        /// <summary>
        /// Runs the redline confirmation for a diff computed on a worker thread (the USB
        /// Update post-discovery callback) and blocks that worker until the UI answers.
        /// Returns false on decline or timeout so the caller can cancel before any write.
        /// </summary>
        private bool ConfirmRedlineDeltaOnBackground(List<string> newRedlineHits, string operation)
        {
            var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Dispatcher.UIThread.Post(async () =>
            {
                try { answer.TrySetResult(await ConfirmRedlineWriteAsync(newRedlineHits, operation)); }
                catch (Exception ex)
                {
                    ProcessRunner.WriteDiagnostic("ConfirmRedlineDeltaOnBackground", ex.ToString());
                    answer.TrySetResult(false);
                }
            });

            // Bounded wait: if the dialog can never be answered, abort instead of
            // deadlocking the flash worker thread.
            var completed = Task.WhenAny(answer.Task, Task.Delay(TimeSpan.FromMinutes(10))).GetAwaiter().GetResult();
            return completed == answer.Task && answer.Task.Result;
        }

        private async Task HandleFrpRemove()
        {
            // Irreversible erase: ask before the first erase command is sent.
            bool confirmed = await _dialogService.ShowConfirmDialog(
                "FRP Lock Removal",
                "This will PERMANENTLY erase FRP-related data. The following commands run in order:\n\n" +
                "  1. erase frp\n" +
                "  2. erase config\n" +
                "  3. oem frp-erase\n\n" +
                "The erased data cannot be recovered. Continue only if you accept this.",
                "Erase Permanently", "Cancel");
            if (!confirmed)
            {
                await ShowMessageBox("Cancelled", "FRP Lock removal aborted; nothing was erased.");
                return;
            }

            var frpDialog = new FrpStepDialog();
            var dialogTask = _dialogService.ShowDialog(frpDialog);

            frpDialog.UpdateOverallStatus("Starting FRP Lock removal...");

            var frpResult = await _fastbootClient.EraseFrpWithSteps();

            // A step only counts when fastboot exited 0 AND printed OKAY; that check lives in
            // FastbootClient.EraseFrpWithSteps, the dialog never defaults to success.
            await Task.Delay(500);
            frpDialog.UpdateStepStatus(1, frpResult.Step1Success);
            frpDialog.UpdateOverallStatus($"Step 1/3 (erase frp): {(frpResult.Step1Success ? "OKAY" : "no OKAY")}");

            await Task.Delay(500);
            frpDialog.UpdateStepStatus(2, frpResult.Step2Success);
            frpDialog.UpdateOverallStatus($"Step 2/3 (erase config): {(frpResult.Step2Success ? "OKAY" : "no OKAY")}");

            await Task.Delay(500);
            frpDialog.UpdateStepStatus(3, frpResult.Step3Success);
            frpDialog.UpdateOverallStatus($"Step 3/3 (oem frp-erase): {(frpResult.Step3Success ? "OKAY" : "no OKAY")}");

            string frpEvidence = OutputTail(
                !frpResult.Step1Success ? frpResult.Step1Output :
                !frpResult.Step2Success ? frpResult.Step2Output :
                frpResult.Step3Output);

            string finalStatus;
            if (frpResult.AllStepsFailed)
            {
                finalStatus = $"FRP Lock removal failed: none of the 3 erase commands returned OKAY. Last output: {frpEvidence}";
            }
            else if (frpResult.SuccessfulStepsCount == 3)
            {
                finalStatus = "All 3 FRP erase commands returned OKAY - the FRP / Find My Phone state itself is not verified on this path.";
            }
            else
            {
                finalStatus = $"{frpResult.SuccessfulStepsCount}/3 FRP erase commands returned OKAY - FRP and Find My Phone state were not verified, this fastboot path has no reliable read-back for them. Last output: {frpEvidence}";
            }

            frpDialog.UpdateOverallStatus(finalStatus);
            frpDialog.ShowCloseButton(frpResult.SuccessfulStepsCount == 3);

            await dialogTask;
        }

        private async Task HandleEnableDowngrade()
        {
            var downgradeDialog = new EnableDowngradeStepDialog();
            var dialogTask = _dialogService.ShowDialog(downgradeDialog);

            downgradeDialog.UpdateOverallStatus("Starting Enable Downgrade...");

            var result = await _fastbootClient.EnableDowngradeWithSteps();

            // A step only counts when fastboot exited 0 AND printed OKAY
            // (FastbootClient.EnableDowngradeWithSteps). See the final line for what that means.
            await Task.Delay(500);
            downgradeDialog.UpdateStepStatus(1, result.Step1Success);
            downgradeDialog.UpdateOverallStatus($"Step 1/4 (oeminfoerase-amssver): {(result.Step1Success ? "OKAY" : "no OKAY")}");

            await Task.Delay(500);
            downgradeDialog.UpdateStepStatus(2, result.Step2Success);
            downgradeDialog.UpdateOverallStatus($"Step 2/4 (oeminfoerase-basever): {(result.Step2Success ? "OKAY" : "no OKAY")}");

            await Task.Delay(500);
            downgradeDialog.UpdateStepStatus(3, result.Step3Success);
            downgradeDialog.UpdateOverallStatus($"Step 3/4 (oeminfoerase-custver): {(result.Step3Success ? "OKAY" : "no OKAY")}");

            await Task.Delay(500);
            downgradeDialog.UpdateStepStatus(4, result.Step4Success);
            downgradeDialog.UpdateOverallStatus($"Step 4/4 (oeminfoerase-preloadver): {(result.Step4Success ? "OKAY" : "no OKAY")}");

            string downgradeEvidence = OutputTail(
                !result.Step1Success ? result.Step1Output :
                !result.Step2Success ? result.Step2Output :
                !result.Step3Success ? result.Step3Output :
                result.Step4Output);

            // OKAY only proves that the oeminfoerase command was accepted and executed. It does
            // not prove that the version records were cleared or that a downgrade is now
            // possible, and there is no getvar that reads those records back.
            string finalStatus;
            if (result.AllStepsFailed)
            {
                finalStatus = $"Enable Downgrade failed: no version-record erase returned OKAY. Last output: {downgradeEvidence}";
            }
            else if (result.SuccessfulStepsCount == 4)
            {
                finalStatus =
                    "All 4 version-record erases returned OKAY - OKAY only means the commands were executed, " +
                    "the records were not verified as cleared and a downgrade is NOT confirmed to be possible. " +
                    "Verify with 'fastboot oem dump-emmc oeminfo <file>' and compare the version records, or simply attempt the downgrade.";
            }
            else
            {
                finalStatus =
                    $"{result.SuccessfulStepsCount}/4 version-record erases returned OKAY - the downgrade is not confirmed. " +
                    "OKAY only means a command was executed, not that a record was cleared. " +
                    $"Verify with 'fastboot oem dump-emmc oeminfo <file>'. Last output: {downgradeEvidence}";
            }

            downgradeDialog.UpdateOverallStatus(finalStatus);
            downgradeDialog.ShowCloseButton(result.SuccessfulStepsCount == 4);

            await dialogTask;
        }

        // TODO(battery-gate): project convention is to refuse writes below 50% battery;
        // the Huawei loader getvar field name for battery level is not yet confirmed on
        // a real device, so no gate is implemented here.
        private async Task HandleRebootToUsbUpdate()
        {
            using var cancellationTokenSource = new CancellationTokenSource();
            if (!UsbUpdateFile.HasFile)
            {
                await ShowMessageBox("File Required", "Please select a Base UPDATE.APP file first.");
                return;
            }

            if (!await _fastbootClient.IsDeviceConnected())
            {
                await ShowMessageBox("Device Not Found", "No device detected in fastboot mode. Please connect your device and try again.");
                return;
            }

            string lockOutput = await _fastbootClient.OemCommandAsync("lock-state info");
            string fbLockState = ParseOemLine(lockOutput, "FB LockState:", "Unknown");

            var rescuePlan = RescuePartitionPlan.ForChip(GetSelectedCpu());
            var rescuePartitions = rescuePlan.Select(entry => entry.Image).ToList();

            bool allowRedlineRescue = await ConfirmRedlineWriteAsync(rescuePartitions, "The rescue image flash");
            if (!allowRedlineRescue)
            {
                return;
            }
            var partitionInfos = rescuePartitions.Select(p => new PartitionInfo { Name = p, Size = 0, FormattedSize = "" }).ToList();
            var progressDialog = new ProcessDialogUapp(partitionInfos, cancellationTokenSource);
            progressDialog.Title = "Reboot to USB Update Progress";

            bool dialogClosed = false;
            progressDialog.RequestClose += (s, e) =>
            {
                dialogClosed = true;
                cancellationTokenSource.Cancel();
            };

            bool isFbUnlocked = fbLockState.ToUpper().Contains("UNLOCKED");
            Task dialogTask = null;

            if (isFbUnlocked)
            {
                dialogTask = _dialogService.ShowNonModalDialog(progressDialog);
                progressDialog.UpdateCurrentOperation("Rebooting to bootloader...");
                await _fastbootClient.RebootBootloaderAsync();

                bool deviceReappeared = false;
                for (int i = 0; i < 30; i++)
                {
                    await Task.Delay(1000);
                    if (await _fastbootClient.IsDeviceConnected())
                    {
                        deviceReappeared = true;
                        break;
                    }
                }

                if (!deviceReappeared)
                {
                    // Close the progress dialog before showing the message box: leaving it open
                    // leaks the window and both windows fight over z-order.
                    try { progressDialog.Close(); } catch { }
                    await ShowMessageBox("Timeout", "Device did not reappear in fastboot mode after reboot.");
                    return;
                }
            }

            string tempDir = Path.Combine(Path.GetTempPath(), $"kirintool_usb_update_{Guid.NewGuid()}");
            Directory.CreateDirectory(tempDir);

            try
            {
                dialogTask ??= _dialogService.ShowNonModalDialog(progressDialog);

                bool allSuccess = true;
                string baseUpdatePath = UsbUpdateFile.FilePath;

                using (var updateApp = new UpdateApp(baseUpdatePath))
                {
                    if (GetSelectedCpu() == ChipCapabilities.Kirin9000)
                    {
                        var missing = rescuePartitions.Where(name => !updateApp.Partitions.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))).ToHashSet();
                        if (missing.Count > 0)
                        {
                            for (int i = 0; i < rescuePartitions.Count; i++)
                                if (missing.Contains(rescuePartitions[i])) progressDialog.CompletePartitionByIndex(i, false, "Not found in UPDATE.APP");
                            progressDialog.SetOverallComplete(false, "Experimental Kirin 9000: required recovery images missing; no images flashed.");
                            await dialogTask;
                            return;
                        }
                    }
                    for (int i = 0; i < rescuePartitions.Count; i++)
                    {
                        string pName = rescuePartitions[i];
                        var pInfo = updateApp.Partitions.FirstOrDefault(p => p.Name.Equals(pName, StringComparison.OrdinalIgnoreCase));

                        if (pInfo == null)
                        {
                            progressDialog.CompletePartitionByIndex(i, false, "Not found in UPDATE.APP");
                            allSuccess = false;
                            continue;
                        }

                        if (!allowRedlineRescue && IsRedlinePartition(pName))
                        {
                            progressDialog.CompletePartitionByIndex(i, false, "Blocked: redline partition");
                            allSuccess = false;
                            continue;
                        }

                        progressDialog.UpdateCurrentPartitionByIndex(i, "Extracting...", 30);
                        string imgPath = Path.Combine(tempDir, $"{pName}.img");
                        await updateApp.ExtractPartition(pInfo, imgPath);

                        progressDialog.UpdateCurrentPartitionByIndex(i, "Flashing...", 70);
                        string flashCmd = rescuePlan[i].Target;

                        var flashResult = await _fastbootClient.FlashPartition(flashCmd, imgPath);
                        // Exit code 0 alone is not proof: a write only counts when the
                        // fastboot client printed OKAY for it.
                        bool flashConfirmed = flashResult.IsSuccess &&
                            (flashResult.Output ?? string.Empty).Contains("OKAY", StringComparison.OrdinalIgnoreCase);
                        if (flashConfirmed)
                        {
                            progressDialog.CompletePartitionByIndex(i, true);
                        }
                        else
                        {
                            progressDialog.CompletePartitionByIndex(i, false, FlashFailureDescription(flashResult.Output));
                            allSuccess = false;
                        }
                    }
                }

                if (allSuccess)
                {
                    progressDialog.UpdateCurrentOperation("Rebooting to USB Update mode...");
                    await _fastbootClient.GetVarAsync("rescue_ugs_port");
                    var finalResult = await _fastbootClient.GetVarAsync("rescue_enter_recovery");

                    // Rescue partitions were written, but entering USB Update mode is a separate
                    // event. "This is sec phone!!" / "start to hisuite" are banners the loader
                    // happens to print, so they count as corroborating evidence only: an error
                    // token wins, and no OKAY/SUCCESS/banner at all is Unknown, never success.
                    switch (ClassifyUsbUpdateModeEntry(finalResult))
                    {
                        case ModeEntryOutcome.Entered:
                            progressDialog.SetOverallComplete(true,
                                "Rescue partitions flashed and the loader confirmed the USB Update mode entry (OKAY/SUCCESS).");
                            break;
                        case ModeEntryOutcome.Failed:
                            progressDialog.SetOverallComplete(false,
                                "Rescue partitions flashed, but the loader refused the USB Update mode entry. " +
                                $"Last output: {OutputTail(finalResult)}");
                            break;
                        default:
                            progressDialog.SetOverallComplete(false,
                                "Rescue partitions flashed, but the USB Update mode entry is NOT confirmed: the loader printed " +
                                $"neither OKAY/SUCCESS nor an error. Last output: {OutputTail(finalResult)}");
                            break;
                    }
                }
                else
                {
                    progressDialog.SetOverallComplete(false, "Failed to prepare or flash rescue partitions.");
                }

                while (!dialogClosed)
                {
                    await Task.Delay(100);
                    if (dialogTask.IsCompleted) break;
                }
            }
            catch (OperationCanceledException)
            {
                progressDialog.SetOverallComplete(false, "Operation cancelled by user.");
            }
            catch (Exception ex)
            {
                progressDialog.SetOverallComplete(false, $"Error: {ex.Message}");
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            }
        }

        private async Task HandleReadSerialNumber()
        {
            try
            {
                CurrentSerialDisplay.Text = await _fastbootClient.ReadNveVariable("SN");
            }
            catch (Exception ex)
            {
                CurrentSerialDisplay.Text = "Serial number unavailable.";
                await ShowMessageBox("Read Serial Number", ex.Message);
            }
        }

        private async Task HandleWriteSerialNumber()
        {
            var newSn = NewSerialInput.Text.Trim();
            if (string.IsNullOrWhiteSpace(newSn))
            {
                await ShowMessageBox("Input Error", "New Serial Number cannot be empty.");
                return;
            }

            // Show the current value next to the new one; a failed read must not be
            // silently presented as the device's real serial number.
            string currentSn;
            try { currentSn = await _fastbootClient.ReadNveVariable("SN"); }
            catch { currentSn = "(read failed)"; }

            string confirmMessage =
                "This PERMANENTLY rewrites the NVE serial number. This change is NOT recoverable " +
                "without a verified backup of the original value.\n\n" +
                $"Current SN: {currentSn}\n" +
                $"New SN:     {newSn}\n\n" +
                "Write the new serial number now?";
            bool confirmed = await _dialogService.ShowConfirmDialog("Write Serial Number", confirmMessage, "Write Permanently", "Cancel");
            if (!confirmed)
            {
                await ShowMessageBox("Cancelled", "Serial number write aborted; nothing was changed.");
                return;
            }

            var writeResult = await _fastbootClient.WriteNveVariable("SN", newSn);
            await ShowMessageBox(
                writeResult.IsSuccess ? "Success" : "Error",
                writeResult.IsSuccess ? "Successfully rewrote the device's serial number!" : "Failed to rewrite the device's serial number!"
            );
        }

        private enum UnlockOutcome
        {
            Success,
            Failed,
            AlreadyUnlockedNoWrite,
            Unknown
        }

        /// <summary>Three-state verdict for a single fastboot command's output.</summary>
        private enum CommandOutcome
        {
            Succeeded,
            Failed,
            Unknown
        }

        private enum ModeEntryOutcome
        {
            Entered,
            Failed,
            Unknown
        }

        /// <summary>
        /// Classifies one command's output. Failure wins over success (fastboot prints OKAY for
        /// the commands it sent and still appends FAILED when the bootloader refuses), and an
        /// output with neither token is Unknown - never a success.
        /// </summary>
        private static CommandOutcome ClassifyCommandOutput(string output)
        {
            string upper = (output ?? string.Empty).ToUpperInvariant();

            if (upper.Contains("FAIL") || upper.Contains("ERROR") ||
                upper.Contains("NOT ALLOWED") || upper.Contains("NOT SUPPORT"))
                return CommandOutcome.Failed;

            if (upper.Contains("OKAY") || upper.Contains("SUCCESS")) return CommandOutcome.Succeeded;

            return CommandOutcome.Unknown;
        }

        /// <summary>
        /// Classifies the rescue_enter_recovery read-back. Explicit OKAY/SUCCESS is the primary
        /// evidence; the loader banners are only accepted when no error token is present and are
        /// reported as such, and an unparsable reply stays Unknown.
        /// </summary>
        private static ModeEntryOutcome ClassifyUsbUpdateModeEntry(string output)
        {
            var verdict = ClassifyCommandOutput(output);
            if (verdict == CommandOutcome.Failed) return ModeEntryOutcome.Failed;
            if (verdict == CommandOutcome.Succeeded) return ModeEntryOutcome.Entered;

            string text = output ?? string.Empty;
            if (text.Contains("This is sec phone!!") || text.Contains("start to hisuite"))
                return ModeEntryOutcome.Entered;

            return ModeEntryOutcome.Unknown;
        }

        /// <summary>
        /// Classifies "fastboot oem unlock" output. The temporary BootROM-injected loader
        /// answers "already fastboot unlocked" without ever writing the userlock: nothing is
        /// written, ro.boot.flash.locked stays 1 and the device is still locked after a
        /// reboot. That answer must never be reported as a successful unlock.
        /// </summary>
        private static UnlockOutcome ClassifyUnlock(string output)
        {
            string lower = (output ?? string.Empty).ToLowerInvariant();

            bool alreadyUnlocked = lower.Contains("already fastboot unlocked") ||
                                   (lower.Contains("failed") && lower.Contains("already") && lower.Contains("unlocked")) ||
                                   (lower.Contains("already") && lower.Contains("unlocked") && lower.Contains("remote"));
            if (alreadyUnlocked) return UnlockOutcome.AlreadyUnlockedNoWrite;

            // Failure wins over success: fastboot prints "OKAY" for every command it sent and
            // still appends "FAILED" when the bootloader refuses the unlock. Reporting such a
            // run as a success is the exact mis-reporting this classifier exists to prevent.
            if (lower.Contains("error") || lower.Contains("failed")) return UnlockOutcome.Failed;

            if (lower.Contains("the device will reboot and do factory reset") || lower.Contains("okay"))
                return UnlockOutcome.Success;

            return UnlockOutcome.Unknown;
        }

        private static string OutputTail(string output, int maxChars = 400)
        {
            string trimmed = (output ?? string.Empty).Trim();
            if (trimmed.Length == 0) return "(the loader printed nothing)";
            return trimmed.Length <= maxChars ? trimmed : "..." + trimmed.Substring(trimmed.Length - maxChars);
        }

        /// <summary>
        /// Reports the classified unlock outcome. The classification and the fastboot exit
        /// code must agree before anything is called a success.
        /// </summary>
        private async Task ShowUnlockOutcome(UnlockOutcome outcome, bool processSucceeded, string output)
        {
            switch (outcome)
            {
                case UnlockOutcome.AlreadyUnlockedNoWrite:
                    await ShowMessageBox("No Write Performed",
                        "The loader answered \"already fastboot unlocked\" (e.g. FAILED (remote: 'already fastboot unlocked')).\n\n" +
                        "This reply comes from the temporary BootROM-injected loader. This unlock path never writes the userlock, so:\n" +
                        "- no partition was written,\n" +
                        "- nothing on the device changed,\n" +
                        "- after a reboot the device is still locked (ro.boot.flash.locked stays 1).\n\n" +
                        "The 'unlocked' line printed by 'fastboot oem get-bootinfo' is an artefact of that temporary loader, not a real unlock.\n\n" +
                        "A real unlock needs a board-level (BD) bootloader or a patched fastboot build (route E).\n\n" +
                        $"(fastboot exited successfully: {(processSucceeded ? "yes" : "no")})");
                    break;

                case UnlockOutcome.Success when processSucceeded:
                    await ShowMessageBox("Success", "Your device's bootloader has been unlocked successfully!");
                    break;

                case UnlockOutcome.Success:
                    await ShowMessageBox("Failed",
                        "The loader printed an unlock message but fastboot did not exit successfully, so no unlock is confirmed.\n\n" +
                        $"fastboot exit code was non-zero.\n\nLast output:\n{OutputTail(output)}");
                    break;

                case UnlockOutcome.Failed:
                    await ShowMessageBox("Failed",
                        "Failed to unlock the bootloader of your device.\n\n" +
                        $"Last output:\n{OutputTail(output)}");
                    break;

                default:
                    await ShowMessageBox("No Write Confirmed",
                        "The unlock command printed neither OKAY nor ERROR, so this is treated as not written: no unlock is confirmed and " +
                        "the device is expected to stay locked after a reboot.\n\n" +
                        $"Last output:\n{OutputTail(output)}");
                    break;
            }
        }

        private async Task HandleUnlockBootloader()
        {
            if (!await _fastbootClient.IsDeviceConnected())
            {
                await ShowMessageBox("Device Not Found", "No device detected in fastboot mode. Please connect your device and try again.");
                return;
            }

            try
            {
                string fullOutput = string.Empty;
                var outputLock = new object();
                TaskCompletionSource<bool> alreadyUnlockedDetected =
                    new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                var progress = new Progress<string>(output =>
                {
                    lock (outputLock)
                    {
                        fullOutput = output;

                        if (ClassifyUnlock(output) == UnlockOutcome.AlreadyUnlockedNoWrite)
                        {
                            alreadyUnlockedDetected.TrySetResult(true);
                        }
                    }
                });

                var unlockTask = _fastbootClient.UnlockBootloaderAsync(progress);

                var firstCompleted = await Task.WhenAny(unlockTask, alreadyUnlockedDetected.Task, Task.Delay(2000));
                bool outcomeKnown = firstCompleted == unlockTask || firstCompleted == alreadyUnlockedDetected.Task;

                if (!outcomeKnown)
                {
                    var confirmResult = await _dialogService.ShowConfirmDialog("Action Needed", "Please press the POWER button on your device, then click 'I Pressed It'.", "I Pressed It", "Cancel");

                    if (confirmResult != true)
                    {
                        return;
                    }
                }

                (bool IsSuccess, string Output) unlockResult;
                if (unlockTask.IsCompleted)
                {
                    unlockResult = await unlockTask;
                }
                else if (outcomeKnown)
                {
                    // The loader already refused the write; the process exits on its own.
                    // Do not hang the UI if it does not.
                    await Task.WhenAny(unlockTask, Task.Delay(5000));
                    if (unlockTask.IsCompleted)
                    {
                        unlockResult = await unlockTask;
                    }
                    else
                    {
                        lock (outputLock)
                        {
                            unlockResult = (false, fullOutput);
                        }
                    }
                }
                else
                {
                    unlockResult = await unlockTask;
                }

                await ShowUnlockOutcome(ClassifyUnlock(unlockResult.Output), unlockResult.IsSuccess, unlockResult.Output);
            }
            catch (Exception ex)
            {
                await ShowMessageBox("Unlock Error", $"An error occurred during bootloader unlock: {ex.Message}");
            }
        }

        private async void BtnConvertOem_Click(object sender, RoutedEventArgs e)
        {
            if (!await AllowChipOperationAsync("convert-oem")) return;
            string model = TxtDeviceModel.Text?.Trim() ?? string.Empty;
            string vendor = TxtVendor.Text?.Trim() ?? string.Empty;
            string selectedCpu = GetSelectedCpu();
            bool useDirectCommand = selectedCpu == "hisi710" || selectedCpu == "hisi710a" || selectedCpu == "hisi980" || selectedCpu == "hisi970" ||
                                    selectedCpu == "hisi810" || selectedCpu == "hisi820" || selectedCpu == "hisi985" || selectedCpu == "hisi990";

            if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(vendor))
            {
                await ShowMessageBox("Input Error", "Device Model and Vendor/Country cannot be empty.");
                return;
            }

            if (useDirectCommand)
            {
                if (!await _fastbootClient.IsDeviceConnected())
                {
                    await ShowMessageBox("Device Not Connected", "No device detected in fastboot mode. Please connect your device and try again.");
                    return;
                }

                try
                {
                    var progressDialog = new ProgressDialog(new ObservableCollection<ProgressItemViewModel>(new[]
                    {
                        new ProgressItemViewModel { FileName = "Writing Model", StatusText = "Pending...", ProgressValue = 0 },
                        new ProgressItemViewModel { FileName = "Writing Vendor", StatusText = "Pending...", ProgressValue = 0 }
                    }));

                    var dialogTask = _dialogService.ShowDialog(progressDialog);

                    bool shouldDisableWp = (selectedCpu == "hisi980" && UseFastFlashLoaderSwitch.IsChecked == true) ||
                                           selectedCpu == "hisi810" ||
                                           selectedCpu == "hisi820" ||
                                           selectedCpu == "hisi985" ||
                                           selectedCpu == "hisi990";
                    if (shouldDisableWp)
                    {
                        progressDialog.UpdateOverallStatus("Disabling write protection...");
                        await _fastbootClient.OemCommandAsync("oeminfoerase-disablewp");
                    }

                    progressDialog.UpdateOverallStatus($"Writing model: {model}...");
                    progressDialog.ProgressItems[0].StatusText = "Writing...";
                    progressDialog.ProgressItems[0].ProgressValue = 50;

                    var modelResult = await _fastbootClient.OemCommandAsync($"oeminfowrite-KTModel@{model}");
                    var modelOutcome = ClassifyCommandOutput(modelResult);
                    bool modelSuccess = modelOutcome == CommandOutcome.Succeeded;

                    progressDialog.ProgressItems[0].StatusText = modelSuccess ? "Done" : (modelOutcome == CommandOutcome.Unknown ? "Unconfirmed" : "Failed");
                    progressDialog.ProgressItems[0].ProgressValue = modelSuccess ? 100 : 0;

                    if (!modelSuccess)
                    {
                        progressDialog.UpdateOverallStatus(modelOutcome == CommandOutcome.Unknown
                            ? $"Model write not confirmed: the loader printed neither OKAY nor ERROR. Last output: {OutputTail(modelResult)}"
                            : $"Failed to write model. Last output: {OutputTail(modelResult)}");
                        progressDialog.ShowCloseButton(false, "Write Failed");
                        await dialogTask;
                        return;
                    }

                    progressDialog.UpdateOverallStatus($"Writing vendor: {vendor}...");
                    progressDialog.ProgressItems[1].StatusText = "Writing...";
                    progressDialog.ProgressItems[1].ProgressValue = 50;

                    var vendorResult = await _fastbootClient.OemCommandAsync($"oeminfowrite-KTVendor@{vendor}");
                    var vendorOutcome = ClassifyCommandOutput(vendorResult);
                    bool vendorSuccess = vendorOutcome == CommandOutcome.Succeeded;

                    progressDialog.ProgressItems[1].StatusText = vendorSuccess ? "Done" : (vendorOutcome == CommandOutcome.Unknown ? "Unconfirmed" : "Failed");
                    progressDialog.ProgressItems[1].ProgressValue = vendorSuccess ? 100 : 0;

                    if (modelSuccess && vendorSuccess)
                    {
                        progressDialog.UpdateOverallStatus("Both oeminfowrite commands (model, vendor) returned OKAY.");
                        progressDialog.ShowCloseButton(true, "Write Complete");
                    }
                    else
                    {
                        progressDialog.UpdateOverallStatus(vendorOutcome == CommandOutcome.Unknown
                            ? $"Vendor write not confirmed (model write returned OKAY): the loader printed neither OKAY nor ERROR. Last output: {OutputTail(vendorResult)}"
                            : $"Failed to write vendor (model write returned OKAY). Last output: {OutputTail(vendorResult)}");
                        progressDialog.ShowCloseButton(false, "Write Failed");
                    }

                    await dialogTask;
                }
                catch (Exception ex)
                {
                    await ShowMessageBox("Error", $"An error occurred: {ex.Message}");
                }
                return;
            }

            string filePath = TxtOemInfoFile.Text?.Trim() ?? string.Empty;
            bool pullFromDevice = TogLivePull.IsChecked == true;

            if (!pullFromDevice && (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)))
            {
                await ShowMessageBox("Input Error", "Please select a valid OEMInfo file.");
                return;
            }

            if (pullFromDevice && !await _fastbootClient.IsDeviceConnected())
            {
                await ShowMessageBox("Device Not Connected", "No device detected in fastboot mode. Please connect your device and try again.");
                return;
            }

            try
            {
                var progressDialog = new ProgressDialog(new ObservableCollection<ProgressItemViewModel>
                {
                    new ProgressItemViewModel { FileName = "OEMInfo Conversion", StatusText = "Starting...", ProgressValue = 0 }
                });

                var dialogTask = _dialogService.ShowDialog(progressDialog);
                var progress = new Progress<string>(status =>
                {
                    progressDialog.UpdateOverallStatus(status);
                    progressDialog.ProgressItems[0].StatusText = "Processing";
                    progressDialog.ProgressItems[0].ProgressValue = 50;
                });

                var result = await _oemInfoService.ConvertOemInfo(pullFromDevice, filePath, model, vendor, progress);

                if (!result.IsSuccess)
                {
                    progressDialog.UpdateOverallStatus($"Conversion failed: {result.ErrorMessage}");
                    progressDialog.ProgressItems[0].StatusText = "Failed";
                    progressDialog.ProgressItems[0].ProgressValue = 0;
                    progressDialog.ShowCloseButton(false, "Conversion Failed");
                    await dialogTask;
                    return;
                }

                progressDialog.UpdateOverallStatus("OEMInfo conversion finished. Press OK to select a save location (conversion only - nothing has been written to the device).");
                progressDialog.ProgressItems[0].StatusText = "Done";
                progressDialog.ProgressItems[0].ProgressValue = 100;
                progressDialog.ShowCloseButton(true, "Conversion Complete");
                await dialogTask;

                var savePath = await _dialogService.ShowSaveFileDialog("Save Converted OEMInfo File", $"converted-oeminfo-{model}.img");
                if (!string.IsNullOrWhiteSpace(savePath))
                {
                    await File.WriteAllBytesAsync(savePath, result.ConvertedData);

                    // Conversion and device write are two separate outcomes and are reported as
                    // such: a finished conversion says nothing about the oeminfo partition.
                    string conversionReport =
                        $"Conversion: succeeded - the converted image was written to:\n{savePath}\n\n" +
                        "Device write: none performed.";

                    bool writeRequested = result.PulledFromDevice &&
                        await _dialogService.ShowConfirmDialog("Flash Back to Device?", "The OEMInfo conversion finished and the converted image was saved.\n\nDo you want to flash the converted OEMInfo back to the device now?", "Yes, Flash", "Cancel") == true;

                    if (!writeRequested)
                    {
                        await ShowMessageBox("Conversion Complete / No Write", conversionReport);
                    }
                    else
                    {
                        var write = await FlashOemInfoToDevice(savePath);
                        if (!write.WriteSucceeded)
                        {
                            await ShowMessageBox("Conversion Complete / Write Not Confirmed",
                                conversionReport.Replace("Device write: none performed.", "Device write: not confirmed.") +
                                $"\n\n{write.Detail}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                await ShowMessageBox("Conversion Error", $"An error occurred during conversion: {ex.Message}");
            }
            finally
            {
                _oemInfoService.CleanupTempFiles();
            }
        }

        private async Task<(bool WriteSucceeded, string Detail)> FlashOemInfoToDevice(string filePath)
        {
            // OEMInfo is a redline partition: the conversion prompt above is not an opt-in
            // for the write itself.
            if (!await ConfirmRedlineWriteAsync(new[] { "oeminfo" }, "Writing the converted OEMInfo"))
            {
                return (false, "The oeminfo write was cancelled at the redline confirmation, no write was performed.");
            }

            try
            {
                var progressDialog = new ProgressDialog(new ObservableCollection<ProgressItemViewModel>
                {
                    new ProgressItemViewModel { FileName = "Flashing OEMInfo", StatusText = "Starting...", ProgressValue = 0 }
                });

                var dialogTask = _dialogService.ShowDialog(progressDialog);

                progressDialog.UpdateOverallStatus("Flashing OEMInfo to device...");
                progressDialog.ProgressItems[0].StatusText = "Flashing";
                progressDialog.ProgressItems[0].ProgressValue = 50;

                var flashResult = await _oemInfoService.FlashConvertedOemInfo(filePath);

                // Success needs the process exit code AND an explicit OKAY token. Exit code 0
                // without OKAY is Unknown: fastboot accepted the command, the write is unproven.
                if (flashResult.IsSuccess && flashResult.Output.ToUpper().Contains("OKAY"))
                {
                    progressDialog.UpdateOverallStatus("Flash oeminfo returned OKAY (write confirmed by fastboot).");
                    progressDialog.ProgressItems[0].StatusText = "Done";
                    progressDialog.ProgressItems[0].ProgressValue = 100;
                    progressDialog.ShowCloseButton(true, "Write Complete");
                    await dialogTask;
                    return (true, "flash oeminfo returned OKAY (exit code 0).");
                }

                string detail = flashResult.IsSuccess
                    ? $"The flash command exited 0 but printed no OKAY, so the write is not confirmed.\nLast output:\n{OutputTail(flashResult.Output)}"
                    : $"The flash command failed (exit code {flashResult.ExitCode}).\nLast output:\n{OutputTail(flashResult.Output)}";

                progressDialog.UpdateOverallStatus(flashResult.IsSuccess
                    ? "OEMInfo write not confirmed: the loader printed no OKAY."
                    : "Failed to flash OEMInfo to device.");
                progressDialog.ProgressItems[0].StatusText = flashResult.IsSuccess ? "Unconfirmed" : "Failed";
                progressDialog.ProgressItems[0].ProgressValue = 0;
                progressDialog.ShowCloseButton(false, "Write Not Confirmed");

                await dialogTask;
                return (false, detail);
            }
            catch (Exception ex)
            {
                await ShowMessageBox("Flash Error", $"Error flashing OEMInfo: {ex.Message}");
                return (false, $"The flash raised an error: {ex.Message}");
            }
        }

        private async void BrowseOemInfoFile_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var fileResult = await _dialogService.ShowOpenFileDialog("Select OEMInfo File", new List<string> { "img", "*" });
                if (!string.IsNullOrWhiteSpace(fileResult))
                {
                    TxtOemInfoFile.Text = fileResult;
                    UserSettings.OemInfoFile = fileResult;
                }
            }
            catch (Exception ex)
            {
                // async void: an unhandled exception here kills the process.
                ProcessRunner.WriteDiagnostic("BrowseOemInfoFile_Click", ex.ToString());
                await ShowMessageBox("File Picker Error", ex.Message);
            }
        }

        private void TogLivePull_Toggled(object sender, RoutedEventArgs e)
        {
            FileSelectionPanel.IsEnabled = !(TogLivePull.IsChecked == true);
            if (TogLivePull.IsChecked == true) TxtOemInfoFile.Text = "";
        }

        private void CpuComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateOemInfoUI();
            UpdateSecurityUI();
            Kirin9000Notice.IsVisible = GetSelectedCpu() == ChipCapabilities.Kirin9000;
            Kirin9000RescueHint.IsVisible = GetSelectedCpu() == ChipCapabilities.Kirin9000;
            
            if (UseFastFlashLoaderSwitch != null)
            {
                UseFastFlashLoaderSwitch.IsVisible = GetSelectedCpu() == "hisi980" ? true : false;
            }
        }

        private void UpdateOemInfoUI()
        {
            string selectedCpu = GetSelectedCpu();
            bool useDirectCommand = selectedCpu == "hisi710" || selectedCpu == "hisi710a" || selectedCpu == "hisi980" || selectedCpu == "hisi970" ||
                                    selectedCpu == "hisi810" || selectedCpu == "hisi820" || selectedCpu == "hisi985" || selectedCpu == "hisi990";

            if (useDirectCommand)
            {
                TogLivePull.IsVisible = false;
                FileSelectionPanel.IsVisible = false;
                devSwtxt.IsVisible = false;
            }
            else
            {
                TogLivePull.IsVisible = true;
                FileSelectionPanel.IsVisible = true;
                devSwtxt.IsVisible = true;
            }
        }

        private void UpdateSecurityUI()
        {
            string selectedCpu = GetSelectedCpu();
            bool isSupported = selectedCpu == "hisi710" || selectedCpu == "hisi970" || selectedCpu == "hisi980" || selectedCpu == "hisi990" || selectedCpu == ChipCapabilities.Kirin9000;

            if (UnlockBootloaderPanel != null)
            {
                UnlockBootloaderPanel.IsVisible = isSupported ? true : false;
            }
        }

        private string GetSelectedCpu()
        {
            // The Tag carries the canonical chip id (see CpuComboBox items).
            if ((CpuComboBox.SelectedItem as ComboBoxItem)?.Tag is string tag &&
                !string.IsNullOrEmpty(tag) && tag != "none")
            {
                return tag;
            }

            string content = (CpuComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString();
            if (!string.IsNullOrEmpty(content) && content.Contains('('))
            {
                int start = content.IndexOf('(') + 1;
                int end = content.IndexOf(')');
                if (end > start)
                {
                    return content.Substring(start, end - start);
                }
            }
            return "none";
        }

        private async Task ShowMessageBox(string title, string message)
        {
            await _dialogService.ShowMessageBox(title, message);
        }

        private string GetFileDisplayName(string fileType)
        {
            return fileType switch
            {
                "BasePtable" => "Base PTABLE",
                "BaseUpdate" => "Base UPDATE",
                "CustPtable" => "Cust PTABLE",
                "CustUpdate" => "Cust UPDATE",
                "PreloadPtable" => "Preload PTABLE",
                "PreloadUpdate" => "Preload UPDATE",
                "SwTpUpdate" => "SW TP Base UPDATE",
                "UsbUpdate" => "Base UPDATE",
                _ => fileType
            };
        }

        private FullOtaFile GetOtaFileByType(string fileType)
        {
            return fileType switch
            {
                "BasePtable" => BasePtableFile,
                "BaseUpdate" => BaseUpdateFile,
                "CustPtable" => CustPtableFile,
                "CustUpdate" => CustUpdateFile,
                "PreloadPtable" => PreloadPtableFile,
                "PreloadUpdate" => PreloadUpdateFile,
                "SwTpUpdate" => SwTpUpdateFile,
                "UsbUpdate" => UsbUpdateFile,
                _ => null
            };
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private void HelpButton_Click(object sender, RoutedEventArgs e)
        {
            Process.Start(new ProcessStartInfo { FileName = "https://kirintool.cfd", UseShellExecute = true });

        }

        private void TelegramButton_Click(object sender, RoutedEventArgs e)
        {
            Process.Start(new ProcessStartInfo { FileName = "https://t.me/kirintoolsupport", UseShellExecute = true });
        }

        private void GithubButton_Click(object sender, RoutedEventArgs e)
        {
            Process.Start(new ProcessStartInfo { FileName = "https://github.com/Redminote11tech/kirin-tool-linux", UseShellExecute = true });
        }
        private async Task<bool> ShowGlobalInteractionPromptAsync(string message, Window? promptOwner = null)
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                return await Dispatcher.UIThread.InvokeAsync(async () => await ShowGlobalInteractionPromptAsync(message, promptOwner));
            }

            if (promptOwner != null)
                return await new DialogService(promptOwner).ShowConfirmDialog(
                    "Action needed!", message, "Done!", "Cancel");

            InteractionMessageTextBlock.Text = message;
            GlobalInteractionOverlay.IsVisible = true;

            _globalInteractionTcs = new TaskCompletionSource<bool>();
            bool result = await _globalInteractionTcs.Task;

            GlobalInteractionOverlay.IsVisible = false;
            return result;
        }

        private void GlobalInteractionDone_Click(object sender, RoutedEventArgs e)
        {
            _globalInteractionTcs?.TrySetResult(true);
        }

        private void GlobalInteractionCancel_Click(object sender, RoutedEventArgs e)
        {
            _globalInteractionTcs?.TrySetResult(false);
        }

        private async void ReadDeviceInfo_Click(object sender, RoutedEventArgs e)
        {
            if (!await _fastbootClient.IsDeviceConnected())
            {
                await ShowMessageBox("Device Not Found", "No device detected in fastboot mode. Please connect your device and try again.");
                return;
            }

            InfoFbLock.Text = "Reading...";
            InfoUserLock.Text = "Reading...";
            InfoDeviceModel.Text = "Reading...";
            InfoVendorCountry.Text = "Reading...";
            InfoBuildNumber.Text = "Reading...";
            InfoBaseVersion.Text = "Reading...";
            InfoCustomVersion.Text = "Reading...";
            InfoPreloadVersion.Text = "Reading...";

            try
            {
                string lockOutput = await _fastbootClient.OemCommandAsync("lock-state info");
                InfoFbLock.Text = ParseOemLine(lockOutput, "FB LockState:", "Unknown");
                InfoUserLock.Text = ParseOemLine(lockOutput, "USER LockState:", "Unknown");
            }
            catch { InfoFbLock.Text = "Unknown"; InfoUserLock.Text = "Unknown"; }

            await Task.Delay(300);

            try
            {
                InfoDeviceModel.Text = ParseGetVar(await _fastbootClient.GetVarAsync("devicemodel"), "Unknown");
            }
            catch { InfoDeviceModel.Text = "Unknown"; }

            await Task.Delay(300);

            try
            {
                InfoVendorCountry.Text = ParseGetVar(await _fastbootClient.GetVarAsync("vendorcountry"), "Unknown");
            }
            catch { InfoVendorCountry.Text = "Unknown"; }

            await Task.Delay(300);

            try
            {
                InfoBaseVersion.Text = ParseOemVersion(await _fastbootClient.OemCommandAsync("oeminforead-BASE_VERSION"));
            }
            catch { InfoBaseVersion.Text = "Unknown"; }

            await Task.Delay(300);

            try
            {
                InfoCustomVersion.Text = ParseOemVersion(await _fastbootClient.OemCommandAsync("oeminforead-CUSTOM_VERSION"));
            }
            catch { InfoCustomVersion.Text = "Unknown"; }

            await Task.Delay(300);

            try
            {
                InfoPreloadVersion.Text = ParseOemVersion(await _fastbootClient.OemCommandAsync("oeminforead-PRELOAD_VERSION"));
            }
            catch { InfoPreloadVersion.Text = "Unknown"; }

            await Task.Delay(300);

            try
            {
                string buildOutput = await _fastbootClient.OemCommandAsync("get-build-number");
                string rawBuild = ParseOemVersion(buildOutput);
                if (rawBuild != "Unknown" && rawBuild.Contains(" "))
                    InfoBuildNumber.Text = rawBuild.Substring(rawBuild.IndexOf(' ') + 1);
                else
                    InfoBuildNumber.Text = rawBuild;
            }
            catch { InfoBuildNumber.Text = "Unknown"; }

            if (DeviceInfoExpander != null)
            {
                DeviceInfoExpander.IsExpanded = true;
            }
        }

        private string ParseOemLine(string output, string key, string fallback)
        {
            if (string.IsNullOrEmpty(output)) return fallback;
            
            var line = output.Split('\n', '\r')
                             .Select(l => l.Trim())
                             .FirstOrDefault(l => l.Contains(key));
                             
            if (line != null)
            {
                int keyIndex = line.IndexOf(key);
                return line.Substring(keyIndex + key.Length).Trim();
            }
            return fallback;
        }

        private string ParseGetVar(string output, string fallback)
        {
            if (string.IsNullOrEmpty(output)) return fallback;

            var line = output.Split('\n', '\r')
                             .Select(l => l.Trim())
                             .FirstOrDefault(l => l.Contains(":") && !l.StartsWith("finished") && !l.StartsWith("OKAY"));
                             
            if (line != null)
            {
                return line.Substring(line.IndexOf(':') + 1).Trim();
            }
            return fallback;
        }

        private string ParseOemVersion(string output) => FastbootReadResponses.OemVersion(output);

        private async void RebootDevice_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!await AllowChipOperationAsync("reboot")) return;
                if (!await _fastbootClient.IsDeviceConnected())
                {
                    await ShowMessageBox("Device Not Found", "No device detected in fastboot mode. Please connect your device and try again.");
                    return;
                }

                var result = await _fastbootClient.RebootAsync();
                if (result.IsSuccess)
                {
                    await ShowMessageBox("Success", "Rebooted successfully!");
                }
                else
                {
                    await ShowMessageBox("Error", $"Failed to reboot device: {result.Output}");
                }
            }
            catch (Exception ex)
            {
                // async void: an unhandled exception here kills the process.
                ProcessRunner.WriteDiagnostic("RebootDevice_Click", ex.ToString());
                await ShowMessageBox("Error", $"Reboot failed: {ex.Message}");
            }
        }
    }
}
