using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace PrayerBar;

public partial class App : Application
{
    private Mutex? _mutex;
    private BarWindow? _bar;
    private DispatcherTimer? _timer;
    private HwndSource? _messageWindow;
    private uint _taskbarCreatedMsg;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(true, @"Local\PrayerBar.SingleInstance.7F3A2C1E", out bool createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        CreateMessageWindow();
        CreateBar();

        _timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Tick();
    }

    /// <summary>
    /// Hidden top-level window that outlives the bar: "TaskbarCreated" is broadcast only to
    /// top-level windows, and an embedded bar dies together with Explorer's taskbar.
    /// </summary>
    private void CreateMessageWindow()
    {
        _taskbarCreatedMsg = Native.RegisterWindowMessage("TaskbarCreated");
        var p = new HwndSourceParameters("PrayerBar.Messages") { Width = 0, Height = 0, WindowStyle = 0 };
        _messageWindow = new HwndSource(p);
        _messageWindow.AddHook(MessageHook);
    }

    private IntPtr MessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _taskbarCreatedMsg && _taskbarCreatedMsg != 0)
        {
            // Explorer restarted: rebuild the bar against the new taskbar.
            Dispatcher.BeginInvoke(RecreateBar, DispatcherPriority.Background);
        }
        else if (msg is Native.WM_DISPLAYCHANGE or Native.WM_SETTINGCHANGE)
        {
            Dispatcher.BeginInvoke(() => _bar?.Reposition(), DispatcherPriority.Background);
        }
        return IntPtr.Zero;
    }

    private void CreateBar()
    {
        _bar = new BarWindow(allowAttach: true);
        _bar.Closed += OnBarClosed;
        _bar.Show();
    }

    private void OnBarClosed(object? sender, EventArgs e)
    {
        // The embedded window is destroyed along with Explorer; wait for TaskbarCreated / next tick.
        if (ReferenceEquals(sender, _bar)) _bar = null;
    }

    private void RecreateBar()
    {
        if (_bar != null)
        {
            _bar.Closed -= OnBarClosed;
            try { _bar.Close(); } catch { }
            _bar = null;
        }
        if (Native.FindWindow("Shell_TrayWnd", null) != IntPtr.Zero) CreateBar();
    }

    private void Tick()
    {
        if (_bar == null || _bar.IsOrphaned)
        {
            if (Native.FindWindow("Shell_TrayWnd", null) != IntPtr.Zero) RecreateBar();
            if (_bar == null) return;
        }

        var now = DateTime.Now;
        _bar.SetText($"الظهر ⏳ {now:HH:mm:ss}", now.Second % 20 < 10);
        _bar.Reposition();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _timer?.Stop();
        _messageWindow?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
