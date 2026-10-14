using Microsoft.Win32;

namespace PrayerBar;

/// <summary>Run-at-login via HKCU\Software\Microsoft\Windows\CurrentVersion\Run.</summary>
public static class StartupManager
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PrayerBar";

    private static string Command => $"\"{Environment.ProcessPath}\"";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        if (enabled) key.SetValue(ValueName, Command);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>If enabled but the exe has moved, point the entry at the current location.</summary>
    public static void RefreshPathIfEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            if (key?.GetValue(ValueName) is string current &&
                !string.Equals(current, Command, StringComparison.OrdinalIgnoreCase))
                key.SetValue(ValueName, Command);
        }
        catch { }
    }
}
