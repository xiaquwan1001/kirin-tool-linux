// Licensed under the Business Source License 1.1. See LICENSE.
using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Styling;

namespace Kirin_Tool.Services;

/// <summary>
/// Persisted user preferences. Every member maps 1:1 to a snake_case key in
/// `settings.json` so the file stays readable and hand-editable.
/// </summary>
public sealed class UserSettingsData
{
    [JsonPropertyName("cpu_model")] public string? CpuModel { get; set; }
    [JsonPropertyName("theme")] public string? Theme { get; set; }
    [JsonPropertyName("last_directory")] public string? LastDirectory { get; set; }

    // Full OTA / Software Testpoint / USB update file picks.
    [JsonPropertyName("base_ptable_path")] public string? BasePtablePath { get; set; }
    [JsonPropertyName("base_update_path")] public string? BaseUpdatePath { get; set; }
    [JsonPropertyName("cust_ptable_path")] public string? CustPtablePath { get; set; }
    [JsonPropertyName("cust_update_path")] public string? CustUpdatePath { get; set; }
    [JsonPropertyName("preload_ptable_path")] public string? PreloadPtablePath { get; set; }
    [JsonPropertyName("preload_update_path")] public string? PreloadUpdatePath { get; set; }
    [JsonPropertyName("sw_tp_update_path")] public string? SwTpUpdatePath { get; set; }
    [JsonPropertyName("usb_update_path")] public string? UsbUpdatePath { get; set; }

    // Fastboot flashing XML ("BD"/board XML) picked by "Flash from XML".
    [JsonPropertyName("fastboot_config_xml")] public string? FastbootConfigXml { get; set; }

    // Offline extractor.
    [JsonPropertyName("extractor_source_path")] public string? ExtractorSourcePath { get; set; }
    [JsonPropertyName("extractor_destination_path")] public string? ExtractorDestinationPath { get; set; }

    // OEMInfo tool inputs.
    [JsonPropertyName("oem_info_file")] public string? OemInfoFile { get; set; }
    [JsonPropertyName("device_model")] public string? DeviceModel { get; set; }
    [JsonPropertyName("vendor")] public string? Vendor { get; set; }
}

/// <summary>
/// Static, throw-free settings service backed by
/// <c>$XDG_CONFIG_HOME/kirin-tool/settings.json</c> (falling back to
/// <c>~/.config/kirin-tool/settings.json</c>).
///
/// Reads and writes never throw: a corrupt file is renamed to
/// <c>settings.json.bad</c> and defaults are used instead, and writes go
/// through a temporary file so a crash cannot truncate the real one.
/// </summary>
public static class UserSettings
{
    /// <summary>File name inside the configuration directory.</summary>
    public const string FileName = "settings.json";

    /// <summary>Where an unreadable settings file is moved to.</summary>
    public const string CorruptFileName = "settings.json.bad";

    public const string ThemeLight = "light";
    public const string ThemeDark = "dark";
    public const string ThemeSystem = "system";

    private static readonly object Gate = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static UserSettingsData _data = new();
    private static string? _configDirectory;
    private static bool _loaded;

    /// <summary>
    /// Points the service at <paramref name="configDir"/> (defaults to the XDG
    /// location) and reloads from disk. Passing a directory lets tests and
    /// packaged checks run against a temporary configuration.
    /// </summary>
    public static void Initialize(string? configDir = null)
    {
        lock (Gate)
        {
            _configDirectory = string.IsNullOrWhiteSpace(configDir)
                ? ResolveDefaultConfigDirectory()
                : Path.GetFullPath(configDir!);
            _data = new UserSettingsData();
            _loaded = false;
        }

        EnsureLoaded();
    }

    /// <summary>
    /// Loads the default XDG location unless <see cref="Initialize"/> already
    /// chose a directory. Used by the application startup path so an injected
    /// configuration directory (tests, packaged checks) is never replaced.
    /// </summary>
    public static void EnsureInitialized()
    {
        lock (Gate)
        {
            _configDirectory ??= ResolveDefaultConfigDirectory();
        }

        EnsureLoaded();
    }

    /// <summary>Directory holding <c>settings.json</c>.</summary>
    public static string ConfigDirectory
    {
        get
        {
            lock (Gate)
            {
                return _configDirectory ??= ResolveDefaultConfigDirectory();
            }
        }
    }

    /// <summary>Full path of <c>settings.json</c>.</summary>
    public static string SettingsFilePath => Path.Combine(ConfigDirectory, FileName);

    /// <summary>Reads the file again, discarding anything held in memory.</summary>
    public static void Reload()
    {
        lock (Gate)
        {
            _data = new UserSettingsData();
            _loaded = false;
        }

        EnsureLoaded();
    }

    /// <summary>Snapshot of every persisted value.</summary>
    public static UserSettingsData Data
    {
        get
        {
            EnsureLoaded();
            lock (Gate)
            {
                return _data;
            }
        }
    }

    /// <summary>Batch edit: mutate several keys with a single atomic write.</summary>
    public static void Update(Action<UserSettingsData> mutate)
    {
        if (mutate is null) return;
        lock (Gate)
        {
            EnsureLoadedLocked();
            mutate(_data);
            WriteToDiskLocked();
        }
    }

    public static string? CpuModel
    {
        get => Data.CpuModel;
        set => Update(d => d.CpuModel = Normalize(value));
    }

    /// <summary><c>light</c>, <c>dark</c> or <c>system</c>.</summary>
    public static string Theme
    {
        get => NormalizeTheme(Data.Theme);
        set => Update(d => d.Theme = NormalizeTheme(value));
    }

    public static string? LastDirectory => ExistingDirectory(Data.LastDirectory);

    public static string? BasePtablePath { get => Data.BasePtablePath; set => Update(d => d.BasePtablePath = Normalize(value)); }
    public static string? BaseUpdatePath { get => Data.BaseUpdatePath; set => Update(d => d.BaseUpdatePath = Normalize(value)); }
    public static string? CustPtablePath { get => Data.CustPtablePath; set => Update(d => d.CustPtablePath = Normalize(value)); }
    public static string? CustUpdatePath { get => Data.CustUpdatePath; set => Update(d => d.CustUpdatePath = Normalize(value)); }
    public static string? PreloadPtablePath { get => Data.PreloadPtablePath; set => Update(d => d.PreloadPtablePath = Normalize(value)); }
    public static string? PreloadUpdatePath { get => Data.PreloadUpdatePath; set => Update(d => d.PreloadUpdatePath = Normalize(value)); }
    public static string? SwTpUpdatePath { get => Data.SwTpUpdatePath; set => Update(d => d.SwTpUpdatePath = Normalize(value)); }
    public static string? UsbUpdatePath { get => Data.UsbUpdatePath; set => Update(d => d.UsbUpdatePath = Normalize(value)); }
    public static string? FastbootConfigXml { get => Data.FastbootConfigXml; set => Update(d => d.FastbootConfigXml = Normalize(value)); }
    public static string? ExtractorSourcePath { get => Data.ExtractorSourcePath; set => Update(d => d.ExtractorSourcePath = Normalize(value)); }
    public static string? ExtractorDestinationPath { get => Data.ExtractorDestinationPath; set => Update(d => d.ExtractorDestinationPath = Normalize(value)); }
    public static string? OemInfoFile { get => Data.OemInfoFile; set => Update(d => d.OemInfoFile = Normalize(value)); }
    public static string? DeviceModel { get => Data.DeviceModel; set => Update(d => d.DeviceModel = Normalize(value)); }
    public static string? Vendor { get => Data.Vendor; set => Update(d => d.Vendor = Normalize(value)); }

    /// <summary>Maps the stored theme key to an Avalonia theme variant.</summary>
    public static ThemeVariant ResolveThemeVariant(string? theme) => NormalizeTheme(theme) switch
    {
        ThemeDark => ThemeVariant.Dark,
        ThemeSystem => ThemeVariant.Default,
        _ => ThemeVariant.Light
    };

    /// <summary>Theme variant of the persisted <c>theme</c> key.</summary>
    public static ThemeVariant CurrentThemeVariant => ResolveThemeVariant(Data.Theme);

    /// <summary>Applies the persisted theme to the running application.</summary>
    public static void ApplyTheme()
    {
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = CurrentThemeVariant;
        }
    }

    /// <summary>Persists <paramref name="theme"/> and applies it immediately.</summary>
    public static void SetTheme(string? theme)
    {
        Theme = NormalizeTheme(theme);
        ApplyTheme();
    }

    // ---------------------------------------------------------------- paths

    /// <summary>Returns the path when it names an existing file, otherwise null.</summary>
    public static string? ExistingFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            return File.Exists(path) ? path : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Returns the path when it names an existing directory, otherwise null.</summary>
    public static string? ExistingDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            return Directory.Exists(path) ? path : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Directory a picker should start in for <paramref name="path"/>: the path
    /// itself when it is a directory, otherwise its parent. Null when neither
    /// exists.
    /// </summary>
    public static string? DirectoryFor(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            if (Directory.Exists(path)) return Path.GetFullPath(path!);
            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent)) return parent;
        }
        catch (Exception)
        {
            // Malformed paths simply do not get remembered.
        }

        return null;
    }

    /// <summary>Remembers the directory of a picked file (or the picked folder).</summary>
    public static void RememberDirectory(string? path)
    {
        var directory = DirectoryFor(path);
        if (directory is null) return;
        if (string.Equals(Data.LastDirectory, directory, StringComparison.Ordinal)) return;
        Update(d => d.LastDirectory = directory);
    }

    // ------------------------------------------------------------- internals

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string NormalizeTheme(string? theme)
    {
        var value = theme?.Trim().ToLowerInvariant();
        return value switch
        {
            ThemeDark => ThemeDark,
            ThemeSystem or "auto" => ThemeSystem,
            _ => ThemeLight
        };
    }

    private static string ResolveDefaultConfigDirectory()
    {
        // Explicit override for headless test harnesses / packaged checks: a test run must
        // never overwrite the real user configuration.
        var overrideDir = Environment.GetEnvironmentVariable("KIRIN_TOOL_SETTINGS_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDir)) return Path.GetFullPath(overrideDir!);

        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrWhiteSpace(xdg)) return Path.Combine(xdg!, "kirin-tool");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
        {
            home = Environment.GetEnvironmentVariable("HOME") ?? ".";
        }

        return Path.Combine(home, ".config", "kirin-tool");
    }

    private static void EnsureLoaded()
    {
        lock (Gate)
        {
            EnsureLoadedLocked();
        }
    }

    private static void EnsureLoadedLocked()
    {
        if (_loaded) return;
        _loaded = true;
        _data = ReadFromDiskLocked();
    }

    private static UserSettingsData ReadFromDiskLocked()
    {
        var path = Path.Combine(_configDirectory ??= ResolveDefaultConfigDirectory(), FileName);
        try
        {
            if (!File.Exists(path)) return new UserSettingsData();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<UserSettingsData>(json) ?? new UserSettingsData();
        }
        catch (Exception)
        {
            QuarantineLocked(path);
            return new UserSettingsData();
        }
    }

    private static void QuarantineLocked(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            File.Move(path, Path.Combine(Path.GetDirectoryName(path) ?? ".", CorruptFileName), overwrite: true);
        }
        catch (Exception)
        {
            // A file we cannot even move must not take the application down.
        }
    }

    private static void WriteToDiskLocked()
    {
        try
        {
            var directory = _configDirectory ??= ResolveDefaultConfigDirectory();
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, FileName);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_data, JsonOptions));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception)
        {
            // Read-only home or a full disk: keep running with in-memory values.
        }
    }
}
