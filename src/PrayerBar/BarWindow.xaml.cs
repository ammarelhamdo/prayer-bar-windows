using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace PrayerBar;

/// <summary>
/// Borderless, transparent label that sits immediately left of the taskbar notification area.
/// It first tries to embed itself in the taskbar (SetParent); if that is refused or the taskbar
/// goes away, it falls back to a TopMost popup that re-positions itself on every tick.
/// </summary>
public partial class BarWindow : Window
{
    private const int GapPx = 4;

    private IntPtr _hwnd;
    private IntPtr _taskbar;
    private bool _attached;
    private bool _allowAttach;
    private Native.RECT _lastPlacement;
    private bool _hiddenByUs;

    public BarWindow(bool allowAttach)
    {
        _allowAttach = allowAttach;
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        Closed += (_, _) => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        ApplyTheme();
    }

    public bool IsAttached => _attached;

    /// <summary>True when already embedded in the given (current) taskbar window.</summary>
    public bool IsAttachedTo(IntPtr taskbar) => _attached && _taskbar == taskbar && Native.IsWindow(taskbar);

    /// <summary>True when the taskbar this window was attached to has been destroyed (Explorer restart).</summary>
    public bool IsOrphaned => _taskbar != IntPtr.Zero && !Native.IsWindow(_taskbar);

    /// <summary>
    /// True when the taskbar's DPI differs from the DPI WPF renders this window at. Embedded child
    /// windows do not get WM_DPICHANGED, so the app recreates the bar when this happens.
    /// </summary>
    public bool DpiMismatch
    {
        get
        {
            if (_hwnd == IntPtr.Zero || _taskbar == IntPtr.Zero || !Native.IsWindow(_taskbar)) return false;
            uint taskbarDpi = Native.GetDpiForWindow(_taskbar);
            return taskbarDpi != 0 && Math.Abs(VisualTreeHelper.GetDpi(this).PixelsPerInchX - taskbarDpi) > 0.5;
        }
    }

    public void SetText(string text, bool warning)
    {
        if (Label.Text != text) Label.Text = text;
        _warning = warning;
        ApplyForeground();
    }

    private void ApplyForeground() =>
        Label.Foreground = _warning ? (_warningBrush ?? WarningBrush) : _normalBrush;

    public void SetToolTip(string? text) =>
        Root.ToolTip = text == null ? null : new System.Windows.Controls.ToolTip
        {
            Content = text,
            FlowDirection = FlowDirection.RightToLeft,
        };

    private static readonly Brush WarningBrush = Frozen(Color.FromRgb(0xFF, 0xB3, 0x00));
    private Brush _normalBrush = Brushes.White;
    private Brush? _warningBrush;
    private bool _warning;

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle or UserPreferenceCategory.Color)
            Dispatcher.BeginInvoke(ApplyTheme);
    }

    /// <summary>Match the clock: dark text on a light taskbar, white text on a dark one.</summary>
    private void ApplyTheme()
    {
        bool light = false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            light = key?.GetValue("SystemUsesLightTheme") is int v && v != 0;
        }
        catch { }
        _normalBrush = light ? Brushes.Black : Brushes.White;
        // Bright amber is hard to read on a light taskbar; darken it there.
        _warningBrush = light ? Frozen(Color.FromRgb(0xB2, 0x6A, 0x00)) : null;
        ApplyForeground();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;

        // Tool window + no-activate: no taskbar button, no Alt-Tab entry, never steals focus.
        long ex = (long)Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE);
        ex = (ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE) & ~Native.WS_EX_APPWINDOW;
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, (IntPtr)ex);

        if (_allowAttach) TryAttach();
        Reposition();
    }

    private void TryAttach()
    {
        var taskbar = Native.FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero) return;

        long style = (long)Native.GetWindowLongPtr(_hwnd, Native.GWL_STYLE);
        Native.SetWindowLongPtr(_hwnd, Native.GWL_STYLE, (IntPtr)((style | Native.WS_CHILD) & ~(long)Native.WS_POPUP));
        if (Native.SetParent(_hwnd, taskbar) != IntPtr.Zero || Native.GetParent(_hwnd) == taskbar)
        {
            _attached = true;
            _taskbar = taskbar;
        }
        else
        {
            Native.SetWindowLongPtr(_hwnd, Native.GWL_STYLE, (IntPtr)style);
        }
    }

    /// <summary>Called every second: follow the taskbar (moves, DPI, resolution, auto-hide).</summary>
    public void Reposition()
    {
        if (_hwnd == IntPtr.Zero) return;

        var taskbar = _attached ? _taskbar : Native.FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero || !Native.IsWindow(taskbar))
        {
            HideBar();
            return;
        }
        if (!_attached) _taskbar = taskbar;

        Native.GetWindowRect(taskbar, out var tb);
        var tray = Native.FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        Native.RECT anchor;
        if (tray == IntPtr.Zero || !Native.GetWindowRect(tray, out anchor))
            anchor = new Native.RECT { Left = tb.Right, Top = tb.Top, Right = tb.Right, Bottom = tb.Bottom };

        bool horizontal = tb.Width >= tb.Height;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;

        // Height tracks the taskbar so the hit area covers it and the text is vertically centred.
        // (Vertical taskbars are not supported on Windows 11; fall back to the text's own height.)
        double h = horizontal ? tb.Height / scale : Math.Max(24, Label.DesiredSize.Height + 8);
        if (Math.Abs(Height - h) > 0.5) Height = h;
        UpdateLayout();

        int w = (int)Math.Ceiling(ActualWidth * scale);
        int hgt = (int)Math.Ceiling(ActualHeight * scale);

        int x, y;
        if (horizontal)
        {
            x = anchor.Left - w - GapPx;
            y = tb.Top + (tb.Height - hgt) / 2;
        }
        else
        {
            x = tb.Left + (tb.Width - w) / 2;
            y = anchor.Top - hgt - GapPx;
        }

        if (!_attached && ShouldHideOverTaskbar(taskbar, tb))
        {
            HideBar();
            return;
        }

        var target = new Native.RECT { Left = x, Top = y, Right = x + w, Bottom = y + hgt };
        if (_attached)
        {
            // Child coordinates are relative to the taskbar's client area.
            Native.MapWindowPoints(IntPtr.Zero, taskbar, ref target, 2);
            if (!_lastPlacement.Equals(target) || _hiddenByUs)
            {
                Native.SetWindowPos(_hwnd, Native.HWND_TOP, target.Left, target.Top, w, hgt,
                    Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            }
            else
            {
                // Keep above the taskbar's XAML content bridge, which can be re-ordered on top.
                Native.SetWindowPos(_hwnd, Native.HWND_TOP, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }
        }
        else
        {
            // Re-assert TopMost every tick: clicking the taskbar raises it above us otherwise.
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, w, hgt,
                Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        }
        _lastPlacement = target;
        _hiddenByUs = false;
        if (!IsVisible) Show();
    }

    private void HideBar()
    {
        if (_hiddenByUs) return;
        _hiddenByUs = true;
        const uint SWP_HIDEWINDOW = 0x0080;
        Native.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | SWP_HIDEWINDOW);
    }

    /// <summary>
    /// In popup mode we must hide ourselves when the taskbar is not showing:
    /// auto-hide has slid it off-screen, or a full-screen app covers the monitor.
    /// </summary>
    private static bool ShouldHideOverTaskbar(IntPtr taskbar, Native.RECT tb)
    {
        var mon = Native.MonitorFromWindow(taskbar, Native.MONITOR_DEFAULTTONEAREST);
        var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfo(mon, ref mi)) return false;
        var m = mi.rcMonitor;

        int visW = Math.Min(tb.Right, m.Right) - Math.Max(tb.Left, m.Left);
        int visH = Math.Min(tb.Bottom, m.Bottom) - Math.Max(tb.Top, m.Top);
        bool horizontal = tb.Width >= tb.Height;
        int thickness = horizontal ? tb.Height : tb.Width;
        int visible = horizontal ? visH : visW;
        if (visible < thickness - 2) return true; // auto-hidden (fully or mid-slide)

        if (!Native.IsWindowVisible(taskbar)) return true;

        var fg = Native.GetForegroundWindow();
        if (fg != IntPtr.Zero && fg != taskbar && Native.GetWindowRect(fg, out var fr)
            && fr.Left <= m.Left && fr.Top <= m.Top && fr.Right >= m.Right && fr.Bottom >= m.Bottom)
        {
            var cls = new System.Text.StringBuilder(64);
            GetClassName(fg, cls, cls.Capacity);
            var c = cls.ToString();
            if (c is not ("Progman" or "WorkerW" or "Shell_TrayWnd")) return true;
        }
        return false;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int max);
}
