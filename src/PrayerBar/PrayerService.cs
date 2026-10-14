using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace PrayerBar;

/// <summary>
/// Supplies prayer times per day: Aladhan monthly calendar (method 13, cached on disk) when available,
/// otherwise the local Diyanet-parameter calculation.
/// </summary>
public sealed class PrayerService
{
    private const int Method = 13;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly Dictionary<DateOnly, List<PrayerTime>> _days = new();
    private readonly HashSet<(int Year, int Month)> _loadedMonths = new();
    private AppSettings _settings;

    public PrayerService(AppSettings settings) => _settings = settings;

    public DateTime? LastFetchFailedAt { get; private set; }

    public static string CacheDir => Path.Combine(AppSettings.DataDir, "cache");

    public void UpdateSettings(AppSettings settings)
    {
        _settings = settings;
        _days.Clear();
        _loadedMonths.Clear();
    }

    /// <summary>Current date in the configured city's time zone.</summary>
    public DateOnly CityToday(DateTimeOffset now) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, _settings.TimeZone).DateTime);

    /// <summary>Times for a city-local date, and whether they came from the API.</summary>
    public (List<PrayerTime> Times, bool FromApi) GetDay(DateOnly date)
    {
        EnsureMonthLoadedFromDisk(date.Year, date.Month);
        if (_days.TryGetValue(date, out var times)) return (times, true);
        return (PrayerCalculator.Compute(date, _settings.Latitude, _settings.Longitude, _settings.TimeZone), false);
    }

    /// <summary>
    /// Downloads (or re-downloads when <paramref name="force"/>) the months that contain
    /// <paramref name="today"/> and tomorrow. Returns false if any download failed.
    /// </summary>
    public async Task<bool> RefreshAsync(DateOnly today, bool force)
    {
        var months = new HashSet<(int, int)> { (today.Year, today.Month) };
        var tomorrow = today.AddDays(1);
        months.Add((tomorrow.Year, tomorrow.Month));

        bool ok = true;
        foreach (var (y, m) in months)
        {
            if (!force && File.Exists(CachePath(y, m))) continue;
            try
            {
                var url = $"https://api.aladhan.com/v1/calendarByCity/{y}/{m}" +
                          $"?city={Uri.EscapeDataString(_settings.City)}" +
                          $"&country={Uri.EscapeDataString(_settings.Country)}&method={Method}";
                var json = await Http.GetStringAsync(url);
                var parsed = Parse(json); // validate before replacing the cache
                if (parsed.Count == 0) throw new InvalidDataException("Empty calendar");

                Directory.CreateDirectory(CacheDir);
                var path = CachePath(y, m);
                await File.WriteAllTextAsync(path + ".tmp", json);
                File.Move(path + ".tmp", path, overwrite: true);
                lock (_days) _loadedMonths.Remove((y, m));
            }
            catch (Exception ex)
            {
                ok = false;
                LastFetchFailedAt = DateTime.Now;
                Log($"Fetch {y}-{m:00} failed: {ex.Message}");
            }
        }
        if (ok) LastFetchFailedAt = null;
        return ok;
    }

    private string CachePath(int year, int month)
    {
        static string Safe(string s) => string.Concat(s.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_'));
        return Path.Combine(CacheDir, $"{Safe(_settings.City)}_{Safe(_settings.Country)}_m{Method}_{year:0000}-{month:00}.json");
    }

    private void EnsureMonthLoadedFromDisk(int year, int month)
    {
        lock (_days)
        {
            if (_loadedMonths.Contains((year, month))) return;
            _loadedMonths.Add((year, month));
            var path = CachePath(year, month);
            if (!File.Exists(path)) return;
            try
            {
                foreach (var (date, times) in Parse(File.ReadAllText(path)))
                    _days[date] = times;
            }
            catch (Exception ex)
            {
                Log($"Cache {path} unreadable: {ex.Message}");
            }
        }
    }

    private static readonly (string Key, Prayer Prayer)[] Keys =
    {
        ("Fajr", Prayer.Fajr), ("Sunrise", Prayer.Sunrise), ("Dhuhr", Prayer.Dhuhr),
        ("Asr", Prayer.Asr), ("Maghrib", Prayer.Maghrib), ("Isha", Prayer.Isha),
    };

    internal static Dictionary<DateOnly, List<PrayerTime>> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = new Dictionary<DateOnly, List<PrayerTime>>();
        foreach (var day in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            var dateStr = day.GetProperty("date").GetProperty("gregorian").GetProperty("date").GetString()!;
            var date = DateOnly.ParseExact(dateStr, "dd-MM-yyyy", CultureInfo.InvariantCulture);
            var timings = day.GetProperty("timings");
            var list = new List<PrayerTime>(Keys.Length);
            foreach (var (key, prayer) in Keys)
                list.Add(new PrayerTime(prayer, ParseTiming(date, timings.GetProperty(key).GetString()!)));
            result[date] = list;
        }
        return result;
    }

    /// <summary>Parses Aladhan's "05:11 (+03)" / "05:11 (+0530)" format.</summary>
    private static DateTimeOffset ParseTiming(DateOnly date, string value)
    {
        var parts = value.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var hm = TimeOnly.ParseExact(parts[0], "HH:mm", CultureInfo.InvariantCulture);
        var offset = TimeSpan.Zero;
        if (parts.Length > 1)
        {
            var o = parts[1].Trim('(', ')', ' ');
            int sign = o.StartsWith('-') ? -1 : 1;
            o = o.TrimStart('+', '-');
            int hh = int.Parse(o[..2], CultureInfo.InvariantCulture);
            int mm = o.Length >= 4 ? int.Parse(o[^2..], CultureInfo.InvariantCulture) : 0;
            offset = sign * new TimeSpan(hh, mm, 0);
        }
        return new DateTimeOffset(date.Year, date.Month, date.Day, hm.Hour, hm.Minute, 0, offset);
    }

    public static async Task<(double Lat, double Lng, string TimeZone, string Name)?> GeocodeAsync(string city, string country)
    {
        var url = $"https://geocoding-api.open-meteo.com/v1/search?count=1&language=en&format=json&name={Uri.EscapeDataString(city)}";
        if (country.Trim().Length == 2) url += $"&countryCode={Uri.EscapeDataString(country.Trim().ToUpperInvariant())}";
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url).ConfigureAwait(false));
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0) return null;
        var r = results[0];
        return (r.GetProperty("latitude").GetDouble(), r.GetProperty("longitude").GetDouble(),
                r.TryGetProperty("timezone", out var tz) ? tz.GetString() ?? "" : "",
                r.GetProperty("name").GetString() ?? city);
    }

    internal static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataDir);
            var path = Path.Combine(AppSettings.DataDir, "log.txt");
            if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.Delete(path);
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch { }
    }
}
