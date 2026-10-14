using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace PrayerBar;

/// <summary>How the bar is currently placed.</summary>
public enum BarMode
{
    /// <summary>Child window of Shell_TrayWnd (SetParent): moves, hides and z-orders with the taskbar.</summary>
    Embedded,
    /// <summary>TopMost popup drawn over the taskbar at the same spot (SetParent refused).</summary>
    Overlay,
    /// <summary>TopMost pill just above the taskbar, used when even a TopMost window ends up
    /// underneath the taskbar (seen on some Windows builds after monitor changes).</summary>
    Floating,
}

/// <summary>
/// Borderless, transparent label that sits immediately left of the taskbar notification area
/// (the system tray + clock). Placement strategy, in order of preference:
/// <list type="number">
/// <item><see cref="BarMode.Embedded"/>: SetParent into the taskbar. Best result: no z-order
///   fights, follows auto-hide automatically.</item>
/// <item><see cref="BarMode.Overlay"/>: if Explorer refuses SetParent, a TopMost popup that
///   re-asserts its position and z-order every tick (and retries embedding every 15 s).</item>
/// <item><see cref="BarMode.Floating"/>: if the taskbar still covers the TopMost popup, float
///   just above the taskbar with a small background so the text stays readable.</item>
/// </list>
/// </summary>
public partial class BarWindow : Window
{
    private const int GapPx = 4;
    private static readonly TimeSpan AttachRetry = TimeSpan.FromSeconds(15);

    private IntPtr _hwnd;
    private IntPtr _taskbar;
    private readonly bool _allowAttach;
    private DateTime _lastAttachAttempt;
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

    public BarMode Mode { get; private set; } = BarMode.Overlay;

    private bool Attached => Mode == BarMode.Embedded;

    /// <summary>True when already embedded in the given (current) taskbar window.</summary>
    public bool IsAttachedTo(IntPtr taskbar) => Attached && _taskbar == taskbar && Native.IsWindow(taskbar);

    /// <summary>True when the taskbar this window was embedded in has been destroyed (Explorer
    /// restart). The window itself is usually destroyed with it; this catches the rest.</summary>
    public bool IsOrphaned => Attached && !Native.IsWindow(_taskbar);

    /// <summary>
    /// True when the taskbar's DPI differs from the DPI WPF renders this window at. Embedded child
    /// windows do not receive WM_DPICHANGED, so the app recreates the bar when this happens
    /// (scaling changed in Settings, or the taskbar moved to a monitor with another scale).
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

    // ------------------------------------------------------------------ content

    /// <summary>Updates the label. <paramref name="warning"/> switches to amber (&lt; 10 min left).
    /// Direction follows the UI language so "الفجر ⏳ 01:02:03" and "Fajr ⏳ 01:02:03" both read naturally.</summary>
    public void SetText(string text, bool warning, FlowDirection flow)
    {
        if (Label.FlowDirection != flow) Label.FlowDirection = flow;
        if (Label.Text != text) Label.Text = text;
        _warning = warning;
        ApplyForeground();
    }

    public void SetToolTip(string? text) =>
        Root.ToolTip = text == null ? null : new System.Windows.Controls.ToolTip { Content = text, FlowDirection = Loc.Flow };

    private static readonly Brush WarningBrush = Frozen(Color.FromRgb(0xFF, 0xB3, 0x00));
    private static readonly Brush HitTestBrush = Frozen(Color.FromArgb(0x01, 0, 0, 0));
    private Brush _normalBrush = Brushes.White;
    private Brush? _warningBrush;
    private bool _warning;

    private void ApplyForeground() =>
        Label.Foreground = _warning ? (_warningBrush ?? WarningBrush) : _normalBrush;

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

    /// <summary>Match the clock: dark text on a light taskbar, white text on a dark one.
    /// (The taskbar follows Windows "system mode", which can differ from the app mode.)</summary>
    private void ApplyTheme()
    {
        bool light = Theme.IsTaskbarLight;
        _normalBrush = light ? Brushes.Black : Brushes.White;
        // Bright amber is hard to read on a light taskbar; use a darker amber there.
        _warningBrush = light ? Frozen(Color.FromRgb(0xB2, 0x6A, 0x00)) : null;
        ApplyForeground();
        ApplyModeVisuals();
    }

    /// <summary>On the taskbar the background is (almost) transparent; when floating over the
    /// desktop it gets a taskbar-coloured pill so the text is readable on any wallpaper.</summary>
    private void ApplyModeVisuals()
    {
        if (Mode == BarMode.Floating)
        {
            Root.Background = Frozen(Theme.IsTaskbarLight ? Color.FromArgb(0xF0, 0xF3, 0xF3, 0xF3) : Color.FromArgb(0xF0, 0x20, 0x20, 0x20));
            Root.CornerRadius = new CornerRadius(6);
            Root.Padding = new Thickness(10, 3, 10, 3);
        }
        else
        {
            Root.Background = HitTestBrush; // alpha 1: invisible, but still receives mouse input
            Root.Padding = new Thickness(8, 0, 8, 0);
        }
    }

    // ------------------------------------------------------------------ attach

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

    /// <summary>
    /// Embeds the window in the taskbar: switch WS_POPUP to WS_CHILD, then SetParent.
    /// Explorer can refuse (e.g. ERROR_INVALID_PARAMETER in some shell states, or access denied
    /// when it runs elevated); then the original style is restored and the popup modes are used.
    /// </summary>
    private void TryAttach()
    {
        _lastAttachAttempt = DateTime.UtcNow;
        var taskbar = Native.FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero) return;

        long style = (long)Native.GetWindowLongPtr(_hwnd, Native.GWL_STYLE);
        Native.SetWindowLongPtr(_hwnd, Native.GWL_STYLE, (IntPtr)((style | Native.WS_CHILD) & ~(long)Native.WS_POPUP));
        if (Native.SetParent(_hwnd, taskbar) != IntPtr.Zero || Native.GetParent(_hwnd) == taskbar)
        {
            SetMode(BarMode.Embedded);
            _taskbar = taskbar;
            _lastPlacement = default; // force a move into child coordinates
            PrayerService.Log("Embedded in taskbar");
        }
        else
        {
            int error = Marshal.GetLastWin32Error();
            Native.SetWindowLongPtr(_hwnd, Native.GWL_STYLE, (IntPtr)style);
            if (Mode == BarMode.Embedded) SetMode(BarMode.Overlay);
            PrayerService.Log($"SetParent refused (error {error}); using {Mode} mode");
        }
    }

    private void SetMode(BarMode mode)
    {
        if (Mode == mode) return;
        Mode = mode;
        ApplyModeVisuals();
    }

    // ------------------------------------------------------------------ placement

    /// <summary>Called every second: follow the taskbar (moves, DPI, resolution, auto-hide).</summary>
    public void Reposition()
    {
        if (_hwnd == IntPtr.Zero) return;

        // Not embedded but allowed to be: retry now and then (the shell may accept later).
        if (!Attached && _allowAttach && DateTime.UtcNow - _lastAttachAttempt > AttachRetry)
            TryAttach();

        var taskbar = Attached ? _taskbar : Native.FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero || !Native.IsWindow(taskbar))
        {
            HideBar();
            return;
        }
        _taskbar = taskbar;

        Native.GetWindowRect(taskbar, out var tb);
        // TrayNotifyWnd spans the tray icons + clock; we sit just to its left. On Windows 11 the
        // clock itself is drawn by XAML and has no window of its own to measure.
        var tray = Native.FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        Native.RECT anchor;
        if (tray == IntPtr.Zero || !Native.GetWindowRect(tray, out anchor))
            anchor = new Native.RECT { Left = tb.Right, Top = tb.Top, Right = tb.Right, Bottom = tb.Bottom };

        bool horizontal = tb.Width >= tb.Height;
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;

        // On the taskbar the window is as tall as the taskbar (bigger hit area, text centred);
        // floating, it hugs the text. Vertical taskbars (not on Windows 11) also hug the text.
        double h = horizontal && Mode != BarMode.Floating
            ? tb.Height / scale
            : Math.Max(20, Math.Ceiling(Label.DesiredSize.Height) + Root.Padding.Top + Root.Padding.Bottom);
        if (Math.Abs(Height - h) > 0.5) Height = h;
        UpdateLayout();

        int w = (int)Math.Ceiling(ActualWidth * scale);
        int hgt = (int)Math.Ceiling(ActualHeight * scale);

        int x, y;
        if (!horizontal)
        {
            x = tb.Left + (tb.Width - w) / 2;
            y = anchor.Top - hgt - GapPx;
        }
        else if (Mode == BarMode.Floating)
        {
            x = anchor.Left - w - GapPx;
            y = tb.Top - hgt - GapPx * 2;
        }
        else
        {
            x = anchor.Left - w - GapPx;
            y = tb.Top + (tb.Height - hgt) / 2;
        }

        if (!Attached && ShouldHideOverTaskbar(taskbar, tb))
        {
            HideBar();
            return;
        }

        var target = new Native.RECT { Left = x, Top = y, Right = x + w, Bottom = y + hgt };
        if (Attached)
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
            if (horizontal) UpdateOverlayOrFloating(taskbar, tb, x, y, w, hgt);
        }
        _lastPlacement = target;
        _hiddenByUs = false;
        if (!IsVisible) Show();
    }

    private DateTime _lastOverlayProbe;

    /// <summary>
    /// Overlay vs Floating. Hit-testing (WindowFromPoint) reflects real z-order, so if the point
    /// at our centre belongs to the taskbar rather than to us, the taskbar is drawn on top of
    /// our TopMost window and the user cannot see the bar: switch to Floating. While floating,
    /// every 15 s probe whether the overlay spot is usable again (an instant move-and-check;
    /// DWM does not present a frame in between).
    /// </summary>
    private void UpdateOverlayOrFloating(IntPtr taskbar, Native.RECT tb, int x, int y, int w, int h)
    {
        if (Mode == BarMode.Overlay)
        {
            if (!IsOnTopAt(x + w / 2, y + h / 2)) SetMode(BarMode.Floating);
            return;
        }

        if (DateTime.UtcNow - _lastOverlayProbe < AttachRetry) return;
        _lastOverlayProbe = DateTime.UtcNow;
        int oy = tb.Top + (tb.Height - h) / 2;
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, oy, w, h, Native.SWP_NOACTIVATE);
        bool visible = IsOnTopAt(x + w / 2, oy + h / 2);
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, w, h, Native.SWP_NOACTIVATE);
        if (visible) SetMode(BarMode.Overlay);
    }

    private bool IsOnTopAt(int x, int y)
    {
        var hit = Native.WindowFromPoint(new Native.POINT { X = x, Y = y });
        return hit == _hwnd || Native.GetAncestor(hit, Native.GA_ROOT) == _hwnd;
    }

    private void HideBar()
    {
        if (_hiddenByUs) return;
        _hiddenByUs = true;
        Native.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_HIDEWINDOW);
    }

    /// <summary>
    /// In popup modes we must hide ourselves when the taskbar is not showing:
    /// auto-hide has slid it off-screen, or a full-screen app covers the monitor.
    /// (Embedded, the taskbar hides us automatically.)
    /// </summary>
    private static bool ShouldHideOverTaskbar(IntPtr taskbar, Native.RECT tb)
    {
        var mon = Native.MonitorFromWindow(taskbar, Native.MONITOR_DEFAULTTONEAREST);
        var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfo(mon, ref mi)) return false;
        var m = mi.rcMonitor;

        int visW = Math.Min(tb.Right, m.Right) - Math.Max(tb.Left, m.Left);
        int visH = Math.Min(tb.Bottom, m.Bottom) - Math.Max(tb.Top, m.Top);
        bool horizontal = tb.Width >= tb.Height;
        int thickness = horizontal ? tb.Height : tb.Width;
        int visible = horizontal ? visH : visW;
        if (visible < thickness - 2) return true; // auto-hidden (fully or mid-slide)

        if (!Native.IsWindowVisible(taskbar)) return true;

        // A full-screen foreground window (video, game, presentation) covers the whole monitor.
        var fg = Native.GetForegroundWindow();
        if (fg != IntPtr.Zero && fg != taskbar && Native.GetWindowRect(fg, out var fr)
            && fr.Left <= m.Left && fr.Top <= m.Top && fr.Right >= m.Right && fr.Bottom >= m.Bottom)
        {
            var cls = Native.ClassName(fg);
            if (cls is not ("Progman" or "WorkerW" or "Shell_TrayWnd")) return true;
        }
        return false;
    }
}
