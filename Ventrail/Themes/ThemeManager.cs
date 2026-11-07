using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace Ventrail.Themes;

public enum AppTheme { System, Light, Dark }

/// <summary>
/// Light / dark themes. The colours live in Colors.Light.xaml and Colors.Dark.xaml (same keys);
/// this swaps the one merged into the app's resources, so every {DynamicResource} colour repaints
/// at once. "System" follows the Windows setting (Settings > Personalisation > Colours) and keeps
/// following it while Ventrail is open. The choice is remembered in %AppData%\Ventrail\settings.json.
/// </summary>
public static class ThemeManager
{
    private static readonly string SettingsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ventrail", "settings.json");

    private static AppTheme _chosen = AppTheme.System;
    private static bool _listening;

    public static AppTheme Chosen => _chosen;

    /// <summary>True when the colours on screen are the dark ones.</summary>
    public static bool IsDark { get; private set; } = true;

    public static AppTheme Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsFile));
                if (settings != null && Enum.TryParse(settings.Theme, out AppTheme theme)) return theme;
            }
        }
        catch (Exception) { }   // unreadable settings: use the default
        return AppTheme.System;
    }

    public static void Apply(AppTheme theme, bool save = true)
    {
        _chosen = theme;
        var app = Application.Current;
        if (app is null) return;   // tests: no WPF application

        bool dark = theme switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => WindowsUsesDarkApps()
        };
        IsDark = dark;

        var colours = new ResourceDictionary
        {
            Source = new Uri(dark ? "Themes/Colors.Dark.xaml" : "Themes/Colors.Light.xaml", UriKind.Relative)
        };
        var merged = app.Resources.MergedDictionaries;
        var old = merged.FirstOrDefault(d => d.Source?.OriginalString.Contains("Themes/Colors.") == true);
        if (old != null) merged[merged.IndexOf(old)] = colours;
        else merged.Insert(0, colours);

        foreach (Window window in app.Windows) SetTitleBar(window, dark);

        if (!_listening)
        {
            // Follow Windows when it switches between light and dark (only matters for "System").
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category == UserPreferenceCategory.General && _chosen == AppTheme.System)
                    app.Dispatcher.BeginInvoke(() => Apply(AppTheme.System, save: false));
            };
            _listening = true;
        }

        if (save) Save(theme);
    }

    /// <summary>Dark or light window title bar to match the theme (Windows 10 2004+ / 11).</summary>
    public static void SetTitleBar(Window window, bool dark)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            window.SourceInitialized += (_, _) => SetTitleBar(window, IsDark);
            return;
        }
        int on = dark ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, 20, ref on, sizeof(int));   // DWMWA_USE_IMMERSIVE_DARK_MODE
    }

    private static bool WindowsUsesDarkApps()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception) { return true; }
    }

    private static void Save(AppTheme theme)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(new Settings { Theme = theme.ToString() }));
        }
        catch (Exception) { }   // can't save: the theme still applies for this session
    }

    private class Settings
    {
        public string Theme { get; set; } = nameof(AppTheme.System);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
