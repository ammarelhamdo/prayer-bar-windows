using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace PrayerBar;

/// <summary>
/// Follows Windows' app light/dark mode. Colors live in one ResourceDictionary (merged into
/// App.Resources) whose brushes are swapped when the user changes the mode; Themes/Controls.xaml
/// references them with DynamicResource, so open windows restyle themselves live.
/// </summary>
public static class Theme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static ResourceDictionary? _palette;

    /// <summary>Windows "app mode" (Settings > Personalization > Colors): dark if AppsUseLightTheme = 0.</summary>
    public static bool IsDark => ReadDword("AppsUseLightTheme") == 0;

    /// <summary>Taskbar ("Windows mode") can differ from app mode; the bar text follows this one.</summary>
    public static bool IsTaskbarLight => ReadDword("SystemUsesLightTheme") == 1;

    public static event Action? Changed;

    /// <summary>Installs the palette and starts listening for mode/accent changes.</summary>
    public static void Initialize(Application app)
    {
        _palette = new ResourceDictionary();
        app.Resources.MergedDictionaries.Insert(0, _palette);
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Themes/Controls.xaml"),
        });
        Apply();
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
                app.Dispatcher.BeginInvoke(() => { Apply(); Changed?.Invoke(); });
        };
    }

    private static void Apply()
    {
        if (_palette == null) return;
        bool dark = IsDark;
        var accent = SystemAccent(dark);

        // Windows 11 Fluent-like neutrals.
        Set("Brush.WindowBg", dark ? "#202020" : "#F3F3F3");
        Set("Brush.Card", dark ? "#2B2B2B" : "#FFFFFF");
        Set("Brush.CardBorder", dark ? "#1F1F1F" : "#E5E5E5");
        Set("Brush.Text", dark ? "#FFFFFF" : "#1B1B1B");
        Set("Brush.SubText", dark ? "#A8A8A8" : "#616161");
        Set("Brush.Control", dark ? "#2D2D2D" : "#FDFDFD");
        Set("Brush.ControlHover", dark ? "#323232" : "#F5F5F5");
        Set("Brush.ControlBorder", dark ? "#3A3A3A" : "#D6D6D6");
        Set("Brush.Popup", dark ? "#2C2C2C" : "#FFFFFF");
        Set("Brush.ItemHover", dark ? "#383838" : "#F0F0F0");
        Set("Brush.Danger", dark ? "#FF99A4" : "#C42B1C");
        _palette["Brush.Accent"] = Frozen(accent);
        _palette["Brush.AccentHover"] = Frozen(Blend(accent, dark ? Colors.Black : Colors.White, 0.12));
        _palette["Brush.AccentText"] = Frozen(dark ? Colors.Black : Colors.White);
        _palette["Brush.Selected"] = Frozen(Color.FromArgb(dark ? (byte)0x40 : (byte)0x26, accent.R, accent.G, accent.B));
    }

    private static void Set(string key, string hex) =>
        _palette![key] = Frozen((Color)ColorConverter.ConvertFromString(hex));

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Color Blend(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    /// <summary>The user's accent color, in the shade Windows itself uses for the current mode
    /// (lighter on dark backgrounds, darker on light ones).</summary>
    private static Color SystemAccent(bool dark)
    {
        try
        {
            var ui = new Windows.UI.ViewManagement.UISettings();
            var c = ui.GetColorValue(dark
                ? Windows.UI.ViewManagement.UIColorType.AccentLight2
                : Windows.UI.ViewManagement.UIColorType.AccentDark1);
            return Color.FromRgb(c.R, c.G, c.B);
        }
        catch
        {
            return dark ? Color.FromRgb(0x4C, 0xC2, 0xFF) : Color.FromRgb(0x00, 0x5F, 0xB8);
        }
    }

    private static int ReadDword(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue(name) is int v ? v : 1;
        }
        catch { return 1; }
    }

    /// <summary>Dark or light title bar to match (DWMWA_USE_IMMERSIVE_DARK_MODE, Windows 10 2004+).</summary>
    public static void ApplyTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        int on = IsDark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
