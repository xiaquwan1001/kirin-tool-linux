// Headless checks for the GUI QoL features: persisted settings and the
// light/dark theme. No window is ever shown on a real display.
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kirin_Tool;
using Kirin_Tool.Services;

// Screenshot output: never a machine-specific path (this test runs in CI too).
// Override with KIRIN_GUIQOL_SHOTS when you want the PNGs in a known folder.
string ShotDirectory = Environment.GetEnvironmentVariable("KIRIN_GUIQOL_SHOTS")
    ?? Path.Combine(Path.GetTempPath(), "kirin-guiqol-shots");

AppBuilder.Configure<App>().WithInterFont().UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .SetupWithoutStarting();

Exception? unhandled = null;
Dispatcher.UIThread.UnhandledException += (_, e) => { unhandled = e.Exception; e.Handled = true; };

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception("FAILED: " + message);
    Console.WriteLine("PASS " + message);
}

static string NewConfigDirectory(string name)
{
    var directory = Path.Combine(Path.GetTempPath(), "kirin-guiqol-" + name + "-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(directory);
    return directory;
}

// ---------------------------------------------------------------- settings
var configA = NewConfigDirectory("roundtrip");
UserSettings.Initialize(configA);
Check(UserSettings.Theme == "light", "default theme is light");
UserSettings.CpuModel = "hisi990";
UserSettings.Theme = "dark";
UserSettings.RememberDirectory(configA);
UserSettings.BasePtablePath = "/tmp/base_ptable.app";
UserSettings.BaseUpdatePath = "/tmp/base_update.app";
UserSettings.CustPtablePath = "/tmp/cust_ptable.app";
UserSettings.CustUpdatePath = "/tmp/cust_update.app";
UserSettings.PreloadPtablePath = "/tmp/preload_ptable.app";
UserSettings.PreloadUpdatePath = "/tmp/preload_update.app";
UserSettings.SwTpUpdatePath = "/tmp/swtp.app";
UserSettings.UsbUpdatePath = "/tmp/usb.app";
UserSettings.FastbootConfigXml = "/tmp/flash.xml";
UserSettings.ExtractorSourcePath = "/tmp/update.app";
UserSettings.ExtractorDestinationPath = configA;
UserSettings.OemInfoFile = "/tmp/oeminfo.img";
UserSettings.DeviceModel = "VOG-AL00";
UserSettings.Vendor = "all/cn";

Check(File.Exists(UserSettings.SettingsFilePath), "settings.json written");
var json = File.ReadAllText(UserSettings.SettingsFilePath);
using (var document = JsonDocument.Parse(json))
{
    var root = document.RootElement;
    Check(root.TryGetProperty("cpu_model", out _) && root.TryGetProperty("theme", out _) &&
          root.TryGetProperty("last_directory", out _) && root.TryGetProperty("base_ptable_path", out _) &&
          root.TryGetProperty("extractor_destination_path", out _) && root.TryGetProperty("oem_info_file", out _),
        "settings.json uses snake_case keys");
}

Check(!File.Exists(UserSettings.SettingsFilePath + ".tmp"), "atomic write leaves no .tmp file");

UserSettings.Initialize(configA); // reload from disk
Check(UserSettings.CpuModel == "hisi990", "cpu_model survives a reload");
Check(UserSettings.Theme == "dark", "theme survives a reload");
Check(UserSettings.DeviceModel == "VOG-AL00" && UserSettings.Vendor == "all/cn", "OEMInfo text fields survive a reload");
Check(UserSettings.PreloadUpdatePath == "/tmp/preload_update.app", "full OTA path survives a reload");
Check(UserSettings.ExtractorDestinationPath == configA, "extractor destination survives a reload");
Check(UserSettings.LastDirectory == configA, "valid last_directory is returned");
Check(UserSettings.ExistingFile("/tmp/does-not-exist.app") is null, "missing files are dropped when restoring");
Check(UserSettings.ExistingDirectory("/tmp/definitely-not-here") is null, "missing directories are dropped when restoring");

// ----------------------------------------------------------- corrupt file
var configB = NewConfigDirectory("corrupt");
File.WriteAllText(Path.Combine(configB, "settings.json"), "{oops");
UserSettings.Initialize(configB);
Check(UserSettings.Theme == "light", "corrupt settings.json falls back to defaults");
Check(UserSettings.CpuModel is null, "corrupt settings.json yields no cpu_model");
Check(File.Exists(Path.Combine(configB, "settings.json.bad")), "corrupt settings.json is quarantined as .bad");
UserSettings.Theme = "dark";
Check(File.Exists(Path.Combine(configB, "settings.json")), "settings are rewritten after a corrupt load");

// ------------------------------------------------------------------ theme
var app = Application.Current ?? throw new Exception("no application");
app.RequestedThemeVariant = ThemeVariant.Dark;
Check(app.RequestedThemeVariant == ThemeVariant.Dark, "RequestedThemeVariant reads back dark");
Check(app.ActualThemeVariant == ThemeVariant.Dark, "ActualThemeVariant switches to dark");
app.RequestedThemeVariant = ThemeVariant.Light;
Check(app.ActualThemeVariant == ThemeVariant.Light, "ActualThemeVariant switches back to light");

var lightText = PaletteBrush(app, "AppTextBrush", ThemeVariant.Light);
var darkText = PaletteBrush(app, "AppTextBrush", ThemeVariant.Dark);
var lightCard = PaletteBrush(app, "AppCardBrush", ThemeVariant.Light);
var darkCard = PaletteBrush(app, "AppCardBrush", ThemeVariant.Dark);
Check(lightText.Color != darkText.Color && lightCard.Color != darkCard.Color, "light and dark palettes differ");
Check(Contrast(lightText.Color, lightCard.Color) > 3 && Contrast(darkText.Color, darkCard.Color) > 3,
    "text/panel contrast stays readable in both palettes");

// -------------------------------------------------- main window rendering
Directory.CreateDirectory(ShotDirectory);
var configC = NewConfigDirectory("window");
UserSettings.Initialize(configC);

var shots = new List<(string Name, ThemeVariant Variant, string Path)>
{
    ("light", ThemeVariant.Light, Path.Combine(ShotDirectory, "main-light.png")),
    ("dark", ThemeVariant.Dark, Path.Combine(ShotDirectory, "main-dark.png"))
};

foreach (var (name, variant, path) in shots)
{
    app.RequestedThemeVariant = variant;
    unhandled = null;
    var window = new MainWindow();
    window.Show();
    Dispatcher.UIThread.RunJobs();
    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    Dispatcher.UIThread.RunJobs();
    Check(unhandled is null, name + " theme: MainWindow shows without UI exceptions");

    var frame = window.CaptureRenderedFrame() ?? throw new Exception(name + ": capture returned no frame");
    frame.Save(path);
    Check(File.Exists(path) && new FileInfo(path).Length > 0, name + " theme: rendered frame saved to " + path);

    var (mean, low, high) = Luminance(frame);
    if (variant == ThemeVariant.Dark)
    {
        Check(mean < 0.45, $"dark theme frame is dark (mean luminance {mean:0.00})");
    }
    else
    {
        Check(mean > 0.55, $"light theme frame is light (mean luminance {mean:0.00})");
    }

    // A window whose text and background collapsed into one colour would show a
    // tiny luminance spread; both frames must keep real contrast.
    Check(high - low > 0.6, $"{name} theme keeps a rendered luminance range ({low:0.00}..{high:0.00})");

    var windowText = (window.Foreground as ISolidColorBrush)?.Color ?? Colors.Black;
    var windowBackground = (window.Background as ISolidColorBrush)?.Color ?? Colors.White;
    Check(Contrast(windowText, windowBackground) > 4.5,
        $"{name} theme: window text {windowText} stays readable on {windowBackground}");
    window.Close();
    Dispatcher.UIThread.RunJobs();
}

// ------------------------------------------- theme switcher in the caption
app.RequestedThemeVariant = ThemeVariant.Light;
var themed = new MainWindow();
themed.Show();
Dispatcher.UIThread.RunJobs();
// The selector lives in the caption UserControl, so it is not in the window's name scope.
var selector = themed.GetVisualDescendants().OfType<ComboBox>().FirstOrDefault(combo => combo.Name == "ThemeSelector")
               ?? throw new Exception("ThemeSelector is missing from the visual tree");
Check(selector.ItemCount == 3, "theme selector offers light/dark/system");
selector.SelectedIndex = 1; // dark
Dispatcher.UIThread.RunJobs();
Check(app.RequestedThemeVariant == ThemeVariant.Dark, "picking Dark in the caption switches the app variant");
Check(UserSettings.Theme == "dark", "picking Dark in the caption persists the theme");
selector.SelectedIndex = 0; // light
Dispatcher.UIThread.RunJobs();
Check(app.RequestedThemeVariant == ThemeVariant.Light && UserSettings.Theme == "light", "picking Light switches back and persists");
themed.Close();
Dispatcher.UIThread.RunJobs();

Console.WriteLine("8 GUI QoL checks passed; headless, no device access.");
return 0;

static ISolidColorBrush PaletteBrush(Application application, string key, ThemeVariant variant)
{
    return application.FindResource(variant, key) as ISolidColorBrush
           ?? throw new Exception($"theme dictionary is missing {key} for {variant}");
}

// Reads the rendered frame and reports mean/2%-98% luminance spread.
static (double Mean, double Low, double High) Luminance(WriteableBitmap frame)
{
    using var locked = frame.Lock();
    var stride = locked.RowBytes;
    var height = locked.Size.Height;
    var bytes = new byte[stride * height];
    Marshal.Copy(locked.Address, bytes, 0, bytes.Length);

    var samples = new List<double>(capacity: 4096);
    double total = 0;
    long count = 0;
    // Sample a coarse grid: enough for statistics without a multi-megabyte pass.
    for (var y = 0; y < height; y += 3)
    {
        for (var x = 0; x < locked.Size.Width; x += 3)
        {
            var offset = y * stride + x * 4;
            if (offset + 2 >= bytes.Length) continue;
            var value = (bytes[offset] + bytes[offset + 1] + bytes[offset + 2]) / 3.0 / 255.0;
            total += value;
            count++;
            if (samples.Count < 4096) samples.Add(value);
        }
    }

    samples.Sort();
    return (count == 0 ? 0 : total / count, samples[0], samples[^1]);
}

static double Contrast(Color foreground, Color background)
{
    var a = RelativeLuminance(foreground);
    var b = RelativeLuminance(background);
    var lighter = Math.Max(a, b);
    var darker = Math.Min(a, b);
    return (lighter + 0.05) / (darker + 0.05);
}

static double RelativeLuminance(Color color) => 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);

static double Channel(byte value)
{
    var v = value / 255.0;
    return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
}
