// Licensed under the repository's Business Source License 1.1. See LICENSE.
using Avalonia.Controls;
using Kirin_Tool.Models;
using Kirin_Tool.Services;
using Kirin_Tool.UI;
using Kirin_Tool.Utils;
namespace Kirin_Tool;

public partial class MainWindow
{
    // Non-modal, read-only battery banner. It never gates any write path and
    // never blocks a button: it only informs (decision 2026-11-12, see
    // docs/audit/2026-11-12/fix-F6.md). Temporary loaders commonly answer
    // these getvar queries with "invalid command", so every failure is silent.
    private WindowsInfoBar? _batteryBanner;
    private bool _refreshingBatteryBanner;

    /// <summary>
    /// Best-effort battery percentage via fastboot getvar. Tries
    /// battery-soc, then battery-capacity, then battery-voltage. Only a
    /// plausible 0-100 integer is returned; voltage (or anything
    /// unparseable / rejected by the loader) yields null. Never throws.
    /// </summary>
    private async Task<int?> TryReadBatteryPercentAsync()
    {
        foreach (string variable in new[] { "battery-soc", "battery-capacity", "battery-voltage" })
        {
            try
            {
                ProcessResult result = await _fastbootClient.QueryVariableAsync(variable);
                if (!result.IsSuccess) continue;
                string output = result.Output ?? string.Empty;
                if (output.Contains("FAILED", StringComparison.OrdinalIgnoreCase) ||
                    output.Contains("invalid command", StringComparison.OrdinalIgnoreCase))
                    continue;
                // Parse the first integer token after "battery-soc:" style output.
                foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string trimmed = line.Trim();
                    if (trimmed.StartsWith("(bootloader)", StringComparison.Ordinal))
                        trimmed = trimmed[12..].TrimStart();
                    int colon = trimmed.IndexOf(':');
                    if (colon >= 0) trimmed = trimmed[(colon + 1)..].Trim();
                    if (variable == "battery-voltage") return null; // mV reading is not a percentage
                    if (int.TryParse(trimmed.Split(' ', '%')[0], out int percent) && percent is >= 0 and <= 100)
                        return percent;
                }
            }
            catch
            {
                // Best effort only: an unreachable or temp-loader device must
                // not raise an error dialog from a read-only hint.
            }
        }
        return null;
    }

    private void EnsureBatteryBanner()
    {
        if (_batteryBanner != null) return;
        _batteryBanner = new WindowsInfoBar
        {
            Title = "Battery",
            IsClosable = false,
            IsOpen = false,
            Margin = new Avalonia.Thickness(0, 0, 0, 8),
        };
        if (this.FindControl<StackPanel>("DeviceInfoPage") is { } page)
            page.Children.Insert(2, _batteryBanner); // below the two page headings, above the expander
    }

    /// <summary>Refreshes the read-only battery banner. Low (&lt;50%) shows a
    /// warning; a healthy level hides it; an unreadable level shows one
    /// quiet line. Nothing here can interrupt or refuse a write.</summary>
    private async Task RefreshBatteryBannerAsync()
    {
        if (_refreshingBatteryBanner) return;
        _refreshingBatteryBanner = true;
        try
        {
            EnsureBatteryBanner();
            int? percent = await TryReadBatteryPercentAsync();
            if (_batteryBanner == null) return;
            if (percent is < 50)
            {
                _batteryBanner.Title = "Low Battery";
                _batteryBanner.Message = $"Battery {percent}% — flashing below 50% risks a mid-write power loss. Charge the device before writing.";
                _batteryBanner.IsOpen = true;
            }
            else if (percent is >= 50)
            {
                _batteryBanner.IsOpen = false;
            }
            else
            {
                _batteryBanner.Title = "Battery";
                _batteryBanner.Message = "Battery level unavailable on this loader";
                _batteryBanner.IsOpen = true;
            }
        }
        catch
        {
            // The banner is informational only; never surface errors from it.
            if (_batteryBanner != null) _batteryBanner.IsOpen = false;
        }
        finally { _refreshingBatteryBanner = false; }
    }
    // output is nullable at the call sites (a failed flash may carry no captured text);
    // null simply falls through to the generic "see the log" description.
    private static string FlashFailureDescription(string? output)
    {
        var failed = (output ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.Contains("FAILED", StringComparison.OrdinalIgnoreCase));
        return failed?.Trim() ?? "Flash failed; see the fastboot log.";
    }
    private async Task<bool> AllowChipOperationAsync(string operation)
    {
        if (ChipCapabilities.Allows(GetSelectedCpu(), operation)) return true;
        await _dialogService.ShowMessageBox("Notice", ChipCapabilities.UnsupportedNotice, showHeading: true);
        return false;
    }
    private async void ReadPsid_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!await AllowChipOperationAsync("read-psid")) return;
        try
        {
            if (!await _fastbootClient.IsDeviceConnected())
            {
                await ShowMessageBox("Device Not Found", "No device detected in fastboot mode.");
                return;
            }
            var values = await _fastbootClient.ReadPsidAsync();
            string text = string.Join("\n", values.Select(pair => $"{pair.Key}: {pair.Value}"));
            await ShowMessageBox("Device identifiers (get-psid)", text);
        }
        catch (Exception ex) { await ShowMessageBox("Read device identifiers", ex.Message); }
    }
}
