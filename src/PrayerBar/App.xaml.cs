using System.Globalization;
using System.Net.NetworkInformation;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace PrayerBar;

public partial class App : Application
{
    private static readonly Prayer[] CountdownPrayers =
        { Prayer.Fajr, Prayer.Dhuhr, Prayer.Asr, Prayer.Maghrib, Prayer.Isha };

    private static readonly TimeSpan WarningThreshold = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(15);

    private Mutex? _mutex;
    private BarWindow? _bar;
    private TrayIcon? _tray;
    private SettingsWindow? _settingsWindow;
    private DispatcherTimer? _timer;
    private HwndSource? _messageWindow;
    private uint _taskbarCreatedMsg;

    private AppSettings _settings = new();
    private PrayerService _service = null!;

    private DateOnly _scheduleDate;
    private List<PrayerTime> _todayAll = new();
    private bool _todayFromApi;
    private List<PrayerTime> _upcoming = new();
    private readonly HashSet<DateTimeOffset> _notified = new();
    private bool _refreshing;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(true, @"Local\PrayerBar.SingleInstance.7F3A2C1E", out bool createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        // A tiny transparent label: software rendering avoids the per-frame GPU read-back of layered
        // windows (which costs CPU and, on some drivers, leaks native memory).
        System.Windows.Media.RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

        DispatcherUnhandledException += (_, args) =>
        {
            PrayerService.Log("Unhandled: " + args.Exception);
            args.Handled = true;
        };

        _settings = AppSettings.Load();
        _service = new PrayerService(_settings);
        StartupManager.RefreshPathIfEnabled();

        _tray = new TrayIcon();
        _tray.SettingsRequested += ShowSettings;
        _tray.RefreshRequested += () => _ = RefreshAsync(force: true, announce: true);
        _tray.ExitRequested += () => Shutdown();

        CreateMessageWindow();
        CreateBar();

        NetworkChange.NetworkAvailabilityChanged += (_, args) =>
        {
            if (args.IsAvailable) Dispatcher.BeginInvoke(() => _ = RefreshAsync(force: false, announce: false));
        };

        _timer = new DispatcherTimer(DispatcherPriority.Normal);
        _timer.Tick += (_, _) => Tick();
        Tick();
        _timer.Start();

        _ = RefreshAsync(force: false, announce: false);
    }

    // ---------------------------------------------------------------- taskbar / bar window

    /// <summary>
    /// Hidden top-level window that outlives the bar: "TaskbarCreated" is broadcast only to
    /// top-level windows, and an embedded bar is destroyed together with Explorer's taskbar.
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
            PrayerService.Log("TaskbarCreated: re-attaching");
            Dispatcher.BeginInvoke(() => { RecreateBar(); Tick(); }, DispatcherPriority.Background);
        }
        else if (msg is Native.WM_DISPLAYCHANGE or Native.WM_SETTINGCHANGE or Native.WM_DPICHANGED)
        {
            Dispatcher.BeginInvoke(() => _bar?.Reposition(), DispatcherPriority.Background);
        }
        return IntPtr.Zero;
    }

    private void CreateBar()
    {
        PrayerService.Log("Creating bar window");
        _bar = new BarWindow(allowAttach: true);
        _bar.Closed += OnBarClosed;
        _bar.MouseRightButtonUp += (_, _) => _tray?.ShowMenu(System.Windows.Forms.Cursor.Position);
        _bar.Show();
        UpdateBarToolTip();
    }

    private void OnBarClosed(object? sender, EventArgs e)
    {
        // An embedded bar dies with Explorer; it is rebuilt on TaskbarCreated or on the next tick.
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

    // ---------------------------------------------------------------- schedule

    private void BuildSchedule(DateOnly today)
    {
        _scheduleDate = today;
        (_todayAll, _todayFromApi) = _service.GetDay(today);
        var (tomorrow, _) = _service.GetDay(today.AddDays(1));
        _upcoming = _todayAll.Concat(tomorrow)
            .Where(p => CountdownPrayers.Contains(p.Prayer))
            .OrderBy(p => p.Time)
            .ToList();
        _notified.RemoveWhere(t => t < DateTimeOffset.Now.AddDays(-1));
        UpdateBarToolTip();
    }

    private async Task RefreshAsync(bool force, bool announce)
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            bool ok = await _service.RefreshAsync(_service.CityToday(DateTimeOffset.Now), force);
            BuildSchedule(_service.CityToday(DateTimeOffset.Now));
            Tick();
            if (announce)
            {
                Notifier.Show(
                    ok ? "تم تحديث أوقات الصلاة" : "تعذر تحديث أوقات الصلاة",
                    ok ? $"{_settings.City} — Aladhan (Diyanet)" : "سيتم استخدام الأوقات المحسوبة محلياً.",
                    _tray?.NotifyIcon);
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    // ---------------------------------------------------------------- 1-second tick

    private void Tick()
    {
        var now = DateTimeOffset.Now;

        // Re-align to just after the next whole second so the countdown never skips a digit.
        if (_timer != null) _timer.Interval = TimeSpan.FromMilliseconds(1000 - now.Millisecond + 15);

        // Midnight (city time) or resume from sleep on a later day: recalculate.
        var today = _service.CityToday(now);
        if (today != _scheduleDate) BuildSchedule(today);

        var next = _upcoming.FirstOrDefault(p => p.Time > now);
        if (next == default)
        {
            BuildSchedule(today);
            next = _upcoming.FirstOrDefault(p => p.Time > now);
            if (next == default) return;
        }

        var remaining = TimeSpan.FromSeconds(Math.Ceiling((next.Time - now).TotalSeconds));
        var name = Arabic.Name(next.Prayer);
        var countdown = string.Create(CultureInfo.InvariantCulture,
            $"{(int)remaining.TotalHours:00}:{remaining.Minutes:00}:{remaining.Seconds:00}");

        MaybeNotify(next, remaining);

        if (_service.LastFetchFailedAt is { } failed && DateTime.Now - failed > RetryInterval)
            _ = RefreshAsync(force: false, announce: false);

        if (_bar == null || _bar.IsOrphaned || _bar.DpiMismatch)
        {
            RecreateBar();
            if (_bar == null) return;
        }
        _bar.SetText($"{name} ⏳ {countdown}", remaining < WarningThreshold);
        _bar.Reposition();

        _tray?.SetStatus($"{name} بعد {countdown}", $"PrayerBar — {name} {FormatTime(next.Time)}");
    }

    private void MaybeNotify(PrayerTime next, TimeSpan remaining)
    {
        int minutes = _settings.NotifyMinutes;
        if (minutes <= 0 || remaining <= TimeSpan.Zero || remaining > TimeSpan.FromMinutes(minutes)) return;
        if (!_notified.Add(next.Time)) return;

        int left = (int)Math.Ceiling(remaining.TotalMinutes);
        Notifier.Show(
            $"اقترب وقت {Arabic.Name(next.Prayer)}",
            $"بعد {MinutesText(left)} — {FormatTime(next.Time)}",
            _tray?.NotifyIcon);
    }

    private static string MinutesText(int n) => n switch
    {
        1 => "دقيقة واحدة",
        2 => "دقيقتين",
        >= 3 and <= 10 => $"{n} دقائق",
        _ => $"{n} دقيقة",
    };

    private string FormatTime(DateTimeOffset t) =>
        TimeZoneInfo.ConvertTime(t, _settings.TimeZone).ToString("HH:mm", CultureInfo.InvariantCulture);

    private void UpdateBarToolTip()
    {
        if (_bar == null || _todayAll.Count == 0) return;
        var sb = new StringBuilder();
        sb.Append(_settings.City).Append(" — ").AppendLine(_scheduleDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        foreach (var p in _todayAll)
            sb.Append(Arabic.Name(p.Prayer)).Append("\t").AppendLine(FormatTime(p.Time));
        sb.Append(_todayFromApi ? "المصدر: Aladhan — ديانت" : "المصدر: حساب محلي (دون اتصال)");
        _bar.SetToolTip(sb.ToString());
    }

    // ---------------------------------------------------------------- settings

    private void ShowSettings()
    {
        if (_settingsWindow != null)
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(_settings);
        try
        {
            if (_settingsWindow.ShowDialog() == true && _settingsWindow.Result is { } updated)
            {
                bool locationChanged = updated.City != _settings.City || updated.Country != _settings.Country;
                _settings = updated;
                _service.UpdateSettings(updated);
                _scheduleDate = default;
                if (locationChanged) _notified.Clear();
                Tick();
                _ = RefreshAsync(force: false, announce: locationChanged);
            }
        }
        finally
        {
            _settingsWindow = null;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _timer?.Stop();
        _tray?.Dispose();
        if (_bar != null)
        {
            _bar.Closed -= OnBarClosed;
            _bar.Close();
        }
        _messageWindow?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
