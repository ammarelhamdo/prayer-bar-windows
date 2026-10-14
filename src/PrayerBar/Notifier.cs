using Microsoft.Toolkit.Uwp.Notifications;

namespace PrayerBar;

public static class Notifier
{
    /// <summary>Shows a Windows toast; falls back to a tray balloon (also rendered as a toast on Windows 11).</summary>
    public static void Show(string title, string body, System.Windows.Forms.NotifyIcon? fallback)
    {
        try
        {
            new ToastContentBuilder()
                .AddText(title)
                .AddText(body)
                .Show();
        }
        catch (Exception ex)
        {
            PrayerService.Log($"Toast failed: {ex.Message}");
            fallback?.ShowBalloonTip(10_000, title, body, System.Windows.Forms.ToolTipIcon.Info);
        }
    }
}
