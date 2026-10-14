using System.Globalization;
using System.Net.NetworkInformation;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace PrayerBar;

/// <summary>
/// Application entry and background lifecycle. There is no main window: the app lives in the
/// tray, owns the taskbar <see cref="BarWindow"/>, and drives everything from one 1-second timer.
/// </summary>
/// <remarks>
/// Lifecycle:
/// <list type="number">
/// <item>Single-instance check (named mutex) — a second launch exits immediately.</item>
/// <item>Load settings, apply language and theme, create tray icon and bar.</item>
/// <item>A hidden message-only window listens for "TaskbarCreated" (Explorer restarts).</item>
/// <item>Every second <see cref="Tick"/> updates the countdown, notifications, midnight rollover
///   and the bar's position.</item>
/// <item>Prayer times are downloaded in the background; until then (or offline) they are computed locally.</item>
/// </list>
/// ShutdownMode is OnExplicitShutdown (App.xaml) so closing Settings — or the bar being destroyed
/// with Explorer — never ends the process; only "Exit" does.
/// </remarks>
public partial class App : Application
{
    /// <summary>Prayers the countdown targets (Sunrise is informational: tooltip only).</summary>
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

    // The day currently shown, all its times (for the tooltip) and the countdown targets
    // (today's + tomorrow's prayers, so after Isha the next one is tomorrow's Fajr).
    private DateOnly _scheduleDate;
    private List<PrayerTime> _todayAll = new();
    private bool _todayFromApi;
    private List<PrayerTime> _upcoming = new();

    /// <summary>Prayer instants already announced, so each toast fires once.</summary>
    private readonly HashSet<DateTimeOffset> _notified = new();

    private bool _refreshing;
    private bool _refreshQueued;
    private bool _queuedForce;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // "Local\" scopes the mutex to this Windows session, so other users can run their own copy.
        _mutex = new Mutex(true, @"Local\PrayerBar.SingleInstance.7F3A2C1E", out bool createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        // A tiny transparent label: software rendering avoids the per-frame GPU read-back of
        // layered windows, which here cost ~10% CPU and leaked native memory (~1 MB/s).
        System.Windows.Media.RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

        DispatcherUnhandledException += (_, args) =>
        {
            PrayerService.Log("Unhandled: " + args.Exception);
            args.Handled = true; // a tray utility should keep running rather than crash
        };

        _settings = AppSettings.Load();
        Loc.Arabic = _settings.IsArabic;
        Theme.Initialize(this);
        Theme.Changed += () => _tray?.ApplyTheme();

        _service = new PrayerService(_settings);
        StartupManager.RefreshPathIfEnabled();

        _tray = new TrayIcon();
        _tray.SettingsRequested += ShowSettings;
        _tray.RefreshRequested += () => _ = RefreshAsync(force: true, announce: true);
        _tray.ExitRequested += () => Shutdown();

        CreateMessageWindow();
        CreateBar();

        // Coming back online after a failed download: try again right away.
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
            Dispatcher.BeginInvoke(() =>
            {
                // The 1-second tick may already have re-attached to the new taskbar.
                if (_bar != null && _bar.IsAttachedTo(Native.FindWindow("Shell_TrayWnd", null))) return;
                PrayerService.Log("TaskbarCreated: re-attaching");
                RecreateBar();
                Tick();
            }, DispatcherPriority.Background);
        }
        else if (msg is Native.WM_DISPLAYCHANGE or Native.WM_SETTINGCHANGE or Native.WM_DPICHANGED)
        {
            // Resolution / scaling / work-area changes: move now instead of waiting for the tick.
            Dispatcher.BeginInvoke(() => _bar?.Reposition(), DispatcherPriority.Background);
        }
        return IntPtr.Zero;
    }

    private void CreateBar()
    {
        PrayerService.Log("Creating bar window");
        _bar = new BarWindow(allowAttach: _settings.EmbedInTaskbar);
        _bar.Closed += OnBarClosed;
        _bar.MouseRightButtonUp += (_, _) => _tray?.ShowMenu(System.Windows.Forms.Cursor.Position);
        _bar.MouseLeftButtonUp += (_, _) => ShowSettings();
        _bar.Show();
        UpdateBarToolTip();
    }

    private void OnBarClosed(object? sender, EventArgs e)
    {
        // An embedded bar dies with Explorer; it is rebuilt on TaskbarCreated or on the next tick.
        if (ReferenceEquals(sender, _bar)) _bar = null;
    }

    /// <summary>Destroys and recreates the bar (Explorer restart, DPI change, embed toggle).
    /// Cheaper and more reliable than trying to repair a window whose parent vanished.</summary>
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

    /// <summary>Loads the day's times (cache or offline calculation) and the countdown targets.</summary>
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

    /// <summary>
    /// Downloads missing (or, with <paramref name="force"/>, all) months, then rebuilds the
    /// schedule so API times replace computed ones. Calls arriving while a download runs are
    /// queued (not dropped), so a city change during a refresh still fetches the new city.
    /// </summary>
    private async Task RefreshAsync(bool force, bool announce)
    {
        if (_refreshing)
        {
            _refreshQueued = true;
            _queuedForce |= force;
            return;
        }
        _refreshing = true;
        try
        {
            bool ok = await _service.RefreshAsync(_service.CityToday(DateTimeOffset.Now), force);
            BuildSchedule(_service.CityToday(DateTimeOffset.Now));
            Tick();
            if (announce)
            {
                Notifier.Show(
                    Loc.T(ok ? "Toast.Refreshed" : "Toast.RefreshFailed"),
                    ok ? $"{CityDisplayName()} — {_settings.Method.Name(Loc.Arabic)}" : Loc.T("Toast.RefreshFailedBody"),
                    _tray?.NotifyIcon);
            }
        }
        finally
        {
            _refreshing = false;
        }

        if (_refreshQueued)
        {
            bool f = _queuedForce;
            _refreshQueued = _queuedForce = false;
            await RefreshAsync(f, announce: false);
        }
    }

    // ---------------------------------------------------------------- 1-second tick

    /// <summary>
    /// The heartbeat (≈1 Hz, the only periodic work): date rollover, countdown text,
    /// notifications, retry of failed downloads, and keeping the bar placed on the taskbar.
    /// </summary>
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

        // Ceiling so the display reaches 00:00:00 exactly at the prayer time, not a second early.
        var remaining = TimeSpan.FromSeconds(Math.Ceiling((next.Time - now).TotalSeconds));
        var name = Loc.Name(next.Prayer);
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
        _bar.SetText($"{name} ⏳ {countdown}", remaining < WarningThreshold, Loc.Flow);
        _bar.Reposition();

        _tray?.SetStatus(Loc.T("Tray.In", name, countdown), $"PrayerBar — {name} {FormatTime(next.Time)}");
    }

    /// <summary>Shows one toast per prayer once it is within the configured lead time.</summary>
    private void MaybeNotify(PrayerTime next, TimeSpan remaining)
    {
        int minutes = _settings.NotifyMinutes;
        if (minutes <= 0 || remaining <= TimeSpan.Zero || remaining > TimeSpan.FromMinutes(minutes)) return;
        if (!_notified.Add(next.Time)) return;

        int left = (int)Math.Ceiling(remaining.TotalMinutes);
        Notifier.Show(
            Loc.T("Toast.Soon", Loc.Name(next.Prayer)),
            Loc.T("Toast.SoonBody", Loc.Minutes(left), FormatTime(next.Time)),
            _tray?.NotifyIcon);
    }

    /// <summary>HH:mm in the city's time zone (which may differ from the PC's).</summary>
    private string FormatTime(DateTimeOffset t) =>
        TimeZoneInfo.ConvertTime(t, _settings.TimeZone).ToString("HH:mm", CultureInfo.InvariantCulture);

    private string CityDisplayName() =>
        CityCatalog.Instance.FindCity(_settings.Country, _settings.City)?.Name(Loc.Arabic) ?? _settings.City;

    /// <summary>Hover text of the bar: city, date, all six times and where they came from.</summary>
    private void UpdateBarToolTip()
    {
        if (_bar == null || _todayAll.Count == 0) return;
        var sb = new StringBuilder();
        sb.Append(CityDisplayName()).Append(" — ").AppendLine(_scheduleDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        foreach (var p in _todayAll)
            sb.Append(Loc.Name(p.Prayer)).Append('\t').AppendLine(FormatTime(p.Time));
        var method = _settings.Method.Name(Loc.Arabic);
        sb.Append(Loc.T(_todayFromApi ? "Tip.SourceApi" : "Tip.SourceLocal", method));
        _bar.SetToolTip(sb.ToString());
    }

    /// <summary>Language changed (saved or previewed in Settings): re-label everything now.</summary>
    private void ApplyLanguage()
    {
        _tray?.ApplyLanguage();
        UpdateBarToolTip();
        Tick();
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
        _settingsWindow.LanguagePreviewChanged += ApplyLanguage;
        try
        {
            if (_settingsWindow.ShowDialog() == true && _settingsWindow.Result is { } updated)
                ApplySettings(updated);
        }
        finally
        {
            _settingsWindow = null;
        }
    }

    /// <summary>
    /// Applies saved settings immediately: the new city's times are computed offline at once
    /// (so the bar never shows the old city), then the month is re-downloaded with force so the
    /// cache for the new city/method/school is fresh.
    /// </summary>
    private void ApplySettings(AppSettings updated)
    {
        var old = _settings;
        bool timesChanged = updated.Country != old.Country || updated.City != old.City
            || updated.Method.Id != old.Method.Id || updated.AsrSchool != old.AsrSchool;

        _settings = updated;
        Loc.Arabic = updated.IsArabic;
        _service.UpdateSettings(updated);
        _scheduleDate = default;
        if (timesChanged) _notified.Clear();
        if (updated.EmbedInTaskbar != old.EmbedInTaskbar) RecreateBar();

        ApplyLanguage(); // also ticks, which rebuilds the schedule for the new city
        if (timesChanged) _ = RefreshAsync(force: true, announce: true);
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
