using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace PrayerBar;

/// <summary>
/// Settings dialog. Works on a draft copy of <see cref="AppSettings"/>; nothing is applied
/// until Save. The language switch is the exception: it re-labels this window (and the bar)
/// immediately so the user sees the result, and is reverted if the dialog is cancelled.
/// </summary>
public partial class SettingsWindow : Window
{
    private const int AutoMethod = -1;
    private static readonly int[] NotifyChoices = { 0, 5, 10, 15, 20, 30, 45, 60 };

    private readonly AppSettings _draft;
    private readonly bool _originalArabic;
    private bool _loading = true;

    /// <summary>Saved settings, or null if the dialog was cancelled.</summary>
    public AppSettings? Result { get; private set; }

    /// <summary>Raised when the language toggle changes (so the app can re-label the bar/tray live).</summary>
    public event Action? LanguagePreviewChanged;

    public SettingsWindow(AppSettings current)
    {
        _draft = current.Clone();
        _originalArabic = Loc.Arabic;
        InitializeComponent();

        (current.IsArabic ? LangAr : LangEn).IsChecked = true;
        (current.AsrSchool == AsrSchool.Hanafi ? AsrHanafi : AsrShafi).IsChecked = true;
        EmbedSwitch.IsChecked = current.EmbedInTaskbar;
        AdvancedToggle.IsChecked = current.MethodOverride != null || current.AsrSchool != AsrSchool.Shafi;
        NotifyPicker.Searchable = false;

        ApplyLanguage();
        _loading = false;

        // Esc with a dropdown open closes the dropdown only, not the whole dialog (the Cancel
        // button is IsCancel, which would otherwise win).
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Escape) return;
            var open = new[] { CountryPicker, CityPicker, MethodPicker, NotifyPicker }.FirstOrDefault(p => p.IsOpen);
            if (open == null) return;
            open.Close();
            e.Handled = true;
        };

        SourceInitialized += (_, _) => Theme.ApplyTitleBar(this);
        // Opened from the tray or the no-activate bar, our process usually is not the foreground
        // one, so Windows would open the dialog *behind* the active app. Start TopMost (always
        // allowed), then drop TopMost once shown and ask for the foreground.
        Topmost = true;
        ContentRendered += (_, _) =>
        {
            Topmost = false;
            Activate();
            Native.SetForegroundWindow(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        };
        Theme.Changed += OnThemeChanged;
        Closed += (_, _) =>
        {
            Theme.Changed -= OnThemeChanged;
            if (Result == null && Loc.Arabic != _originalArabic)
            {
                Loc.Arabic = _originalArabic; // cancelled: undo the live language preview
                LanguagePreviewChanged?.Invoke();
            }
        };
    }

    private void OnThemeChanged() => Theme.ApplyTitleBar(this);

    // ------------------------------------------------------------------ texts & lists

    /// <summary>Sets every label and rebuilds the picker lists in the current language,
    /// keeping the current selections.</summary>
    private void ApplyLanguage()
    {
        FlowDirection = Loc.Flow;
        Title = Loc.T("Settings.Title");
        HeaderText.Text = Loc.T("Settings.Header");
        LanguageLabel.Text = Loc.T("Settings.Language");
        CountryLabel.Text = Loc.T("Settings.Country");
        CityLabel.Text = Loc.T("Settings.City");
        NotifyLabel.Text = Loc.T("Settings.Notify");
        AdvancedToggle.Content = Loc.T("Settings.Advanced");
        MethodLabel.Text = Loc.T("Settings.Method");
        AsrLabel.Text = Loc.T("Settings.Asr");
        AsrShafi.Content = Loc.T("Settings.Shafi");
        AsrHanafi.Content = Loc.T("Settings.Hanafi");
        EmbedSwitch.Content = Loc.T("Settings.Embed");
        SaveButton.Content = Loc.T("Settings.Save");
        CancelButton.Content = Loc.T("Settings.Cancel");

        foreach (var p in new[] { CountryPicker, CityPicker, MethodPicker, NotifyPicker })
        {
            p.PlaceholderText = Loc.T("Settings.Search");
            p.EmptyText = Loc.T("Settings.NoResults");
        }

        bool ar = Loc.Arabic;
        var compare = StringComparer.Create(ar ? new CultureInfo("ar") : CultureInfo.InvariantCulture, true);

        // Countries sorted alphabetically in the display language; the other language is shown
        // dimmed beside it and is searchable too ("تركيا" or "Turkey" both find Turkey).
        CountryPicker.Items = CityCatalog.Instance.Countries
            .Select(c => new PickerItem(c, c.Name(ar), c.Name(!ar)))
            .OrderBy(i => i.Primary, compare)
            .ToList();
        CountryPicker.SelectedValue = CityCatalog.Instance.FindCountry(_draft.Country);
        FillCities();

        var notify = NotifyChoices.Contains(_draft.NotifyMinutes)
            ? NotifyChoices
            : NotifyChoices.Append(_draft.NotifyMinutes).Order().ToArray();
        NotifyPicker.Items = notify
            .Select(n => new PickerItem(n, n == 0 ? Loc.T("Settings.NotifyOff") : Loc.Minutes(n)))
            .ToList();
        NotifyPicker.SelectedValue = _draft.NotifyMinutes;

        FillMethods();
        UpdatePreview();
    }

    /// <summary>Cities of the selected country, largest first (the catalog's order).</summary>
    private void FillCities()
    {
        bool ar = Loc.Arabic;
        var country = CountryPicker.SelectedValue as Country;
        CityPicker.Items = country?.Cities.Select(c => new PickerItem(c, c.Name(ar), c.Name(!ar) == c.Name(ar) ? null : c.Name(!ar))).ToList()
                           ?? new List<PickerItem>();
        CityPicker.SelectedValue = country?.Cities.FirstOrDefault(c => c.En.Equals(_draft.City, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>"Automatic — (country default)" followed by every Aladhan method.</summary>
    private void FillMethods()
    {
        bool ar = Loc.Arabic;
        var auto = CalculationMethods.Get(AppSettings.CountryDefaultMethod(_draft.Country));
        var items = new List<PickerItem> { new(AutoMethod, Loc.T("Settings.MethodAuto", auto.Name(ar))) };
        items.AddRange(CalculationMethods.All.Select(m => new PickerItem(m.Id, m.Name(ar))));
        MethodPicker.Items = items;
        MethodPicker.SelectedValue = _draft.MethodOverride ?? AutoMethod;
    }

    /// <summary>Today's times for the current choices, so the effect of a change is visible
    /// before saving. Computed offline (instant); may differ from the API by a minute.</summary>
    private void UpdatePreview()
    {
        if (CityPicker.SelectedValue is not City)
        {
            PreviewText.Text = "";
            return;
        }
        var tz = _draft.TimeZone;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.Now, tz).DateTime);
        var times = PrayerCalculator.Compute(today, _draft.Latitude, _draft.Longitude, tz, _draft.Method, _draft.AsrSchool);
        PreviewText.Text = Loc.T("Settings.Preview") + ":  " + string.Join("  ·  ", times.Select(t =>
            $"{Loc.Name(t.Prayer)} {TimeZoneInfo.ConvertTime(t.Time, tz).ToString("HH:mm", CultureInfo.InvariantCulture)}"));
    }

    // ------------------------------------------------------------------ events

    private void OnLanguageChanged(object sender, RoutedEventArgs e)
    {
        _draft.Language = LangEn.IsChecked == true ? "en" : "ar";
        if (_loading) return;
        Loc.Arabic = _draft.IsArabic;
        ApplyLanguage();
        LanguagePreviewChanged?.Invoke();
    }

    private void OnCountryChanged(object? sender, EventArgs e)
    {
        if (CountryPicker.SelectedValue is not Country country) return;
        // Default to the country's largest city; the user usually refines it next.
        var first = country.Cities.FirstOrDefault();
        if (first != null) _draft.SetCity(country, first);
        FillCities();
        FillMethods(); // the "Automatic" label names the new country's method
        UpdatePreview();
        CityPicker.Focus();
    }

    private void OnCityChanged(object? sender, EventArgs e)
    {
        if (CountryPicker.SelectedValue is Country country && CityPicker.SelectedValue is City city)
            _draft.SetCity(country, city);
        UpdatePreview();
    }

    private void OnMethodChanged(object? sender, EventArgs e)
    {
        _draft.MethodOverride = MethodPicker.SelectedValue is int id && id != AutoMethod ? id : null;
        UpdatePreview();
    }

    private void OnAsrChanged(object sender, RoutedEventArgs e)
    {
        _draft.AsrSchool = AsrHanafi.IsChecked == true ? AsrSchool.Hanafi : AsrSchool.Shafi;
        if (!_loading) UpdatePreview();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (CityPicker.SelectedValue is not City)
        {
            StatusText.Text = Loc.T("Settings.PickCity");
            StatusText.Visibility = Visibility.Visible;
            return;
        }
        _draft.NotifyMinutes = NotifyPicker.SelectedValue is int n ? n : _draft.NotifyMinutes;
        _draft.EmbedInTaskbar = EmbedSwitch.IsChecked == true;
        try
        {
            _draft.Save();
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            StatusText.Visibility = Visibility.Visible;
            return;
        }
        Result = _draft;
        DialogResult = true;
    }
}
