using System.Globalization;
using System.Net.Http;
using System.Windows;

namespace PrayerBar;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _original;

    /// <summary>The saved settings, or null if the dialog was cancelled.</summary>
    public AppSettings? Result { get; private set; }

    public SettingsWindow(AppSettings current)
    {
        _original = current;
        InitializeComponent();
        CityBox.Text = current.City;
        CountryBox.Text = current.Country;
        NotifyBox.Text = current.NotifyMinutes.ToString(CultureInfo.InvariantCulture);
        CoordsText.Text = FormatCoords(current);
        Loaded += (_, _) => { CityBox.Focus(); CityBox.SelectAll(); };
    }

    private static string FormatCoords(AppSettings s) =>
        string.Create(CultureInfo.InvariantCulture, $"{s.Latitude:0.####}, {s.Longitude:0.####}  ({s.TimeZoneId})");

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        var city = CityBox.Text.Trim();
        var country = CountryBox.Text.Trim();
        if (city.Length == 0)
        {
            StatusText.Text = "يرجى إدخال اسم المدينة.";
            return;
        }
        if (!int.TryParse(NotifyBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes)
            || minutes < 0 || minutes > 120)
        {
            StatusText.Text = "مدة التنبيه يجب أن تكون رقماً بين 0 و 120.";
            return;
        }

        var updated = _original.Clone();
        updated.City = city;
        updated.Country = country;
        updated.NotifyMinutes = minutes;

        bool locationChanged =
            !string.Equals(city, _original.City, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(country, _original.Country, StringComparison.OrdinalIgnoreCase);

        if (locationChanged)
        {
            SaveButton.IsEnabled = false;
            StatusText.Text = "";
            try
            {
                var geo = await PrayerService.GeocodeAsync(city, country);
                if (geo is null)
                {
                    StatusText.Text = "لم يتم العثور على المدينة. تحقق من الاسم ورمز الدولة.";
                    return;
                }
                updated.Latitude = geo.Value.Lat;
                updated.Longitude = geo.Value.Lng;
                if (!string.IsNullOrEmpty(geo.Value.TimeZone)) updated.TimeZoneId = geo.Value.TimeZone;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Offline: accept the change; the API will supply times once connected.
                PrayerService.Log($"Geocode failed: {ex.Message}");
                var answer = MessageBox.Show(this,
                    "تعذر الاتصال لتحديد موقع المدينة. سيتم استخدام الإحداثيات السابقة للحساب دون اتصال حتى يعود الاتصال.\nهل تريد الحفظ؟",
                    "PrayerBar", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes) return;
            }
            finally
            {
                SaveButton.IsEnabled = true;
            }
        }

        try
        {
            updated.Save();
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            return;
        }
        Result = updated;
        DialogResult = true;
    }
}
