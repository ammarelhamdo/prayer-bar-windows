using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrayerBar;

/// <summary>How the countdown is laid out on the taskbar. Stored by name in settings.json.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<BarLayout>))]
public enum BarLayout { TwoLines, OneLine }

/// <summary>Text weight of the taskbar label. Stored by name in settings.json.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<BarWeight>))]
public enum BarWeight { Bold, ExtraBold, Normal }

/// <summary>
/// User settings, stored as JSON in %AppData%\PrayerBar\settings.json.
/// Property names are kept stable so older files still load (unknown/missing keys use defaults).
/// </summary>
public sealed class AppSettings
{
    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PrayerBar");

    private static string FilePath => Path.Combine(DataDir, "settings.json");

    /// <summary>UI language: "ar" or "en".</summary>
    public string Language { get; set; } = "ar";

    /// <summary>ISO 3166-1 alpha-2 code of the selected country (matches the catalog).</summary>
    public string Country { get; set; } = "TR";

    /// <summary>English name of the selected city (catalog key within the country).</summary>
    public string City { get; set; } = "Gaziantep";

    // Copied from the catalog entry when a city is chosen, so the app still works if the
    // catalog changes later (or a user city file is removed).
    public double Latitude { get; set; } = 37.0662;
    public double Longitude { get; set; } = 37.3833;
    public string TimeZoneId { get; set; } = "Europe/Istanbul";

    /// <summary>Aladhan method id chosen in "Advanced"; null = automatic (country default).</summary>
    public int? MethodOverride { get; set; }

    public AsrSchool AsrSchool { get; set; } = AsrSchool.Shafi;

    /// <summary>Minutes before each prayer to show a toast; 0 disables notifications.</summary>
    public int NotifyMinutes { get; set; } = 10;

    /// <summary>
    /// Embed the label inside the taskbar (SetParent). When false, or if embedding fails,
    /// a TopMost window that follows the taskbar is used instead.
    /// </summary>
    public bool EmbedInTaskbar { get; set; } = true;

    /// <summary>Two lines (name above, larger countdown below) or one line.</summary>
    public BarLayout BarLayout { get; set; } = BarLayout.TwoLines;

    public BarWeight BarWeight { get; set; } = BarWeight.Bold;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { /* corrupt file: fall back to defaults */ }
        return new AppSettings();
    }

    /// <summary>Writes atomically (temp file + move) so a crash never leaves a half-written file.</summary>
    public void Save()
    {
        Directory.CreateDirectory(DataDir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, FilePath, overwrite: true);
    }

    [JsonIgnore]
    public bool IsArabic => Language != "en";

    /// <summary>The city's time zone; falls back to the PC's zone if the id is unknown.</summary>
    /// <remarks>Cached: resolving an IANA id goes through ICU and is too slow to repeat on
    /// every 1-second tick.</remarks>
    [JsonIgnore]
    public TimeZoneInfo TimeZone
    {
        get
        {
            if (_tz == null || _tzId != TimeZoneId)
            {
                _tzId = TimeZoneId;
                try { _tz = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId); }
                catch { _tz = TimeZoneInfo.Local; }
            }
            return _tz;
        }
    }

    private TimeZoneInfo? _tz;
    private string? _tzId;

    /// <summary>Method actually used: the Advanced override, else the country's default
    /// from the catalog (Turkey = 13 / Diyanet), else Muslim World League.</summary>
    [JsonIgnore]
    public CalculationMethod Method =>
        CalculationMethods.Get(MethodOverride ?? CountryDefaultMethod(Country));

    public static int CountryDefaultMethod(string countryCode) =>
        CityCatalog.Instance.FindCountry(countryCode)?.Method
        ?? (countryCode.Equals("TR", StringComparison.OrdinalIgnoreCase) ? 13 : CalculationMethods.Default.Id);

    /// <summary>Copies a catalog city's identity, coordinates and time zone into the settings.</summary>
    public void SetCity(Country country, City city)
    {
        Country = country.Code;
        City = city.En;
        Latitude = city.Lat;
        Longitude = city.Lng;
        if (!string.IsNullOrEmpty(city.Tz)) TimeZoneId = city.Tz;
    }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
