using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PrayerBar;

/// <summary>Notification-area icon with the app menu (Arabic, right-to-left).</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _header;
    private readonly ToolStripMenuItem _startup;
    private readonly Icon _image;

    public event Action? SettingsRequested;
    public event Action? RefreshRequested;
    public event Action? ExitRequested;

    public TrayIcon()
    {
        _image = CreateIcon();

        _header = new ToolStripMenuItem("…") { Enabled = false };
        var settings = new ToolStripMenuItem("الإعدادات…", null, (_, _) => SettingsRequested?.Invoke());
        _startup = new ToolStripMenuItem("التشغيل مع بدء ويندوز", null, OnStartupClicked);
        var refresh = new ToolStripMenuItem("تحديث الأوقات", null, (_, _) => RefreshRequested?.Invoke());
        var exit = new ToolStripMenuItem("خروج", null, (_, _) => ExitRequested?.Invoke());

        var menu = new ContextMenuStrip { RightToLeft = RightToLeft.Yes, ShowImageMargin = false };
        menu.Items.AddRange(new ToolStripItem[]
        {
            _header, new ToolStripSeparator(), settings, _startup, refresh, new ToolStripSeparator(), exit,
        });
        menu.Opening += (_, _) => _startup.Checked = SafeIsStartupEnabled();

        _icon = new NotifyIcon
        {
            Icon = _image,
            Text = "PrayerBar",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => SettingsRequested?.Invoke();
    }

    public NotifyIcon NotifyIcon => _icon;

    public void ShowMenu(System.Drawing.Point screenPoint) => _icon.ContextMenuStrip!.Show(screenPoint);

    public void SetStatus(string header, string tooltip)
    {
        _header.Text = header;
        // NotifyIcon.Text is limited to 127 characters.
        _icon.Text = tooltip.Length > 127 ? tooltip[..127] : tooltip;
    }

    private static bool SafeIsStartupEnabled()
    {
        try { return StartupManager.IsEnabled; } catch { return false; }
    }

    private void OnStartupClicked(object? sender, EventArgs e)
    {
        try
        {
            StartupManager.SetEnabled(!StartupManager.IsEnabled);
            _startup.Checked = StartupManager.IsEnabled;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "PrayerBar", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>Gold crescent and star drawn at runtime (no binary resources needed).</summary>
    private static Icon CreateIcon()
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var moon = new GraphicsPath();
            moon.AddEllipse(2, 2, 28, 28);
            using var cut = new GraphicsPath();
            cut.AddEllipse(10, -1, 26, 26);
            using var region = new Region(moon);
            region.Exclude(cut);
            using var gold = new SolidBrush(Color.FromArgb(0xF2, 0xB8, 0x2E));
            g.FillRegion(gold, region);
            g.FillPolygon(gold, Star(new PointF(23, 21), 5.5f, 2.3f));
        }
        var hIcon = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(hIcon).Clone(); }
        finally { DestroyIcon(hIcon); }
    }

    private static PointF[] Star(PointF c, float outer, float inner)
    {
        var pts = new PointF[10];
        for (int i = 0; i < 10; i++)
        {
            double a = -Math.PI / 2 + i * Math.PI / 5;
            float r = i % 2 == 0 ? outer : inner;
            pts[i] = new PointF(c.X + (float)(r * Math.Cos(a)), c.Y + (float)(r * Math.Sin(a)));
        }
        return pts;
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _image.Dispose();
    }
}
