// Licensed under the Business Source License 1.1. See LICENSE.
using System;
using Avalonia.Controls;
using Kirin_Tool.Models;
using Kirin_Tool.Services;

namespace Kirin_Tool;

/// <summary>
/// Startup restore and change-time persistence of the values kept in
/// <see cref="UserSettings"/>: CPU model, last picked files, OEMInfo inputs and
/// the extractor paths. Paths are only restored when they still exist so the UI
/// never shows a stale selection.
/// </summary>
public partial class MainWindow
{
    private bool _restoringUserSettings;

    /// <summary>Restores the last session's choices. Never throws.</summary>
    private void RestoreUserSettings()
    {
        _restoringUserSettings = true;
        try
        {
            RestoreCpuModel();
            RestoreFullOtaFiles();
            RestoreOemInfoInputs();
            RestoreExtractorPaths();
        }
        catch (Exception)
        {
            // Unreadable or half-valid settings must never block startup.
        }
        finally
        {
            _restoringUserSettings = false;
        }
    }

    /// <summary>
    /// Writes a value only when the user actually changes it: CPU selection,
    /// the file pickers' callbacks and TextBox focus loss.
    /// </summary>
    private void HookUserSettingsPersistence()
    {
        CpuComboBox.SelectionChanged += (_, _) =>
        {
            if (_restoringUserSettings) return;
            var cpu = GetSelectedCpu();
            if (cpu != "none") UserSettings.CpuModel = cpu;
        };

        // FilePath changes come from the Browse/Clear buttons, never from typing.
        foreach (var file in new[]
                 {
                     BasePtableFile, BaseUpdateFile, CustPtableFile, CustUpdateFile,
                     PreloadPtableFile, PreloadUpdateFile, SwTpUpdateFile, UsbUpdateFile
                 })
        {
            var tracked = file;
            tracked.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(FullOtaFile.FilePath)) PersistFullOtaFile(tracked);
            };
        }

        TxtOemInfoFile.LostFocus += (_, _) => UserSettings.OemInfoFile = TxtOemInfoFile.Text;
        TxtDeviceModel.LostFocus += (_, _) => UserSettings.DeviceModel = TxtDeviceModel.Text;
        TxtVendor.LostFocus += (_, _) => UserSettings.Vendor = TxtVendor.Text;
    }

    private void PersistFullOtaFile(FullOtaFile file)
    {
        if (_restoringUserSettings) return;

        var path = file.FilePath;
        switch (file.FileType)
        {
            case "BasePtable": UserSettings.BasePtablePath = path; break;
            case "BaseUpdate": UserSettings.BaseUpdatePath = path; break;
            case "CustPtable": UserSettings.CustPtablePath = path; break;
            case "CustUpdate": UserSettings.CustUpdatePath = path; break;
            case "PreloadPtable": UserSettings.PreloadPtablePath = path; break;
            case "PreloadUpdate": UserSettings.PreloadUpdatePath = path; break;
            case "SwTpUpdate": UserSettings.SwTpUpdatePath = path; break;
            case "UsbUpdate": UserSettings.UsbUpdatePath = path; break;
        }
    }

    private void RestoreCpuModel()
    {
        var saved = UserSettings.CpuModel;
        if (string.IsNullOrWhiteSpace(saved)) return;

        foreach (var item in CpuComboBox.Items)
        {
            if (item is ComboBoxItem combo && string.Equals(combo.Tag as string, saved, StringComparison.OrdinalIgnoreCase))
            {
                CpuComboBox.SelectedItem = combo;
                return;
            }
        }
    }

    private void RestoreFullOtaFiles()
    {
        var restored = new[]
        {
            (BasePtableFile, UserSettings.BasePtablePath),
            (BaseUpdateFile, UserSettings.BaseUpdatePath),
            (CustPtableFile, UserSettings.CustPtablePath),
            (CustUpdateFile, UserSettings.CustUpdatePath),
            (PreloadPtableFile, UserSettings.PreloadPtablePath),
            (PreloadUpdateFile, UserSettings.PreloadUpdatePath),
            (SwTpUpdateFile, UserSettings.SwTpUpdatePath),
            (UsbUpdateFile, UserSettings.UsbUpdatePath)
        };

        foreach (var (file, saved) in restored)
        {
            var path = UserSettings.ExistingFile(saved);
            if (path is null || string.Equals(file.FilePath, path, StringComparison.Ordinal)) continue;
            file.FilePath = path;
        }

        UpdateEditButtonsState();
    }

    private void RestoreOemInfoInputs()
    {
        if (UserSettings.ExistingFile(UserSettings.OemInfoFile) is { } oemFile) TxtOemInfoFile.Text = oemFile;
        if (UserSettings.DeviceModel is { Length: > 0 } model) TxtDeviceModel.Text = model;
        if (UserSettings.Vendor is { Length: > 0 } vendor) TxtVendor.Text = vendor;
    }

    private void RestoreExtractorPaths()
    {
        if (UserSettings.ExistingDirectory(UserSettings.ExtractorDestinationPath) is { } destination)
        {
            _extractionDestination = destination;
            ExtractorDestinationPath.Text = destination;
            UpdateExtractionSelection();
        }

        // Reading the archive is asynchronous and swallows its own errors, so the
        // partition list is ready without the user re-picking the file.
        if (UserSettings.ExistingFile(UserSettings.ExtractorSourcePath) is { } source)
        {
            _ = LoadExtractionSourceAsync(source);
        }
    }
}
