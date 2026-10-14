using System.IO;
using System.Text.Json;

namespace PrayerBar;

public sealed class AppSettings
{
    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PrayerBar");

    private static string FilePath => Path.Combine(DataDir, "settings.json");

    public string City { get; set; } = "Gaziantep";
    public string Country { get; set; } = "TR";

    /// <summary>Minutes before each prayer to show a toast; 0 disables notifications.</summary>
    public int NotifyMinutes { get; set; } = 10;

    // Used for the offline calculation. Aladhan's calendarByCity does not return reliable
    // coordinates, so these come from geocoding the city when it is changed in Settings.
    public double Latitude { get; set; } = 37.0662;
    public double Longitude { get; set; } = 37.3833;
    public string TimeZoneId { get; set; } = "Europe/Istanbul";

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

    public void Save()
    {
        Directory.CreateDirectory(DataDir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, FilePath, overwrite: true);
    }

    public TimeZoneInfo TimeZone
    {
        get
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId); }
            catch { return TimeZoneInfo.Local; }
        }
    }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
