using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace PrayerBar;

/// <summary>
/// Supplies prayer times per day. Source of truth is Aladhan's monthly calendar, cached on disk
/// as the raw JSON response; when a month is not cached (offline, first run, API down) the same
/// day is computed locally with <see cref="PrayerCalculator"/> using the same method and school.
/// </summary>
/// <remarks>
/// Requests use the city's catalog coordinates (<c>/v1/calendar?latitude&amp;longitude</c>) rather
/// than <c>calendarByCity</c>: Aladhan geocodes city names itself and can resolve them oddly,
/// while coordinates make the API and the offline calculation describe exactly the same place.
/// </remarks>
public sealed class PrayerService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    // Parsed months currently in memory, and which (year, month) files were already read.
    private readonly Dictionary<DateOnly, List<PrayerTime>> _days = new();
    private readonly HashSet<(int Year, int Month)> _loadedMonths = new();
    private AppSettings _settings;

    public PrayerService(AppSettings settings) => _settings = settings;

    /// <summary>Set when the last download failed; the app retries every 15 minutes.</summary>
    public DateTime? LastFetchFailedAt { get; private set; }

    public static string CacheDir => Path.Combine(AppSettings.DataDir, "cache");

    /// <summary>Switch city/method/school: drops in-memory data so the next lookup re-reads
    /// the cache files for the new key (or computes offline until they are downloaded).</summary>
    public void UpdateSettings(AppSettings settings)
    {
        _settings = settings;
        _days.Clear();
        _loadedMonths.Clear();
        LastFetchFailedAt = null;
    }

    /// <summary>Current date in the configured city's time zone (not the PC's).</summary>
    public DateOnly CityToday(DateTimeOffset now) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, _settings.TimeZone).DateTime);

    /// <summary>Times for a city-local date, and whether they came from the API cache.</summary>
    public (List<PrayerTime> Times, bool FromApi) GetDay(DateOnly date)
    {
        EnsureMonthLoadedFromDisk(date.Year, date.Month);
        if (_days.TryGetValue(date, out var times)) return (times, true);
        var computed = PrayerCalculator.Compute(date, _settings.Latitude, _settings.Longitude,
            _settings.TimeZone, _settings.Method, _settings.AsrSchool);
        return (computed, false);
    }

    /// <summary>
    /// Downloads the months containing <paramref name="today"/> and tomorrow (tomorrow's Fajr
    /// is needed after Isha on the last day of a month). Cached months are skipped unless
    /// <paramref name="force"/>. Returns false if any download failed.
    /// </summary>
    public async Task<bool> RefreshAsync(DateOnly today, bool force)
    {
        var months = new HashSet<(int, int)> { (today.Year, today.Month) };
        var tomorrow = today.AddDays(1);
        months.Add((tomorrow.Year, tomorrow.Month));

        bool ok = true;
        foreach (var (y, m) in months)
        {
            var path = CachePath(y, m);
            if (!force && File.Exists(path)) continue;
            try
            {
                var url = string.Create(CultureInfo.InvariantCulture,
                    $"https://api.aladhan.com/v1/calendar/{y}/{m}?latitude={_settings.Latitude}&longitude={_settings.Longitude}" +
                    $"&method={_settings.Method.Id}&school={(int)_settings.AsrSchool}" +
                    $"&timezonestring={Uri.EscapeDataString(_settings.TimeZoneId)}");
                var json = await Http.GetStringAsync(url);
                var parsed = Parse(json, _settings.TimeZone); // validate before replacing the cache
                if (parsed.Count == 0) throw new InvalidDataException("Empty calendar");

                Directory.CreateDirectory(CacheDir);
                await File.WriteAllTextAsync(path + ".tmp", json);
                File.Move(path + ".tmp", path, overwrite: true);
                _loadedMonths.Remove((y, m)); // re-read on next GetDay
            }
            catch (Exception ex)
            {
                ok = false;
                LastFetchFailedAt = DateTime.Now;
                Log($"Fetch {y}-{m:00} failed: {ex.Message}");
            }
        }
        if (ok) LastFetchFailedAt = null;
        PruneCache();
        return ok;
    }

    /// <summary>
    /// Cache file per (country, city, method, school, month): changing any of them must not
    /// reuse times computed for another configuration.
    /// </summary>
    private string CachePath(int year, int month)
    {
        static string Safe(string s) => string.Concat(s.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_'));
        return Path.Combine(CacheDir,
            $"{Safe(_settings.Country)}_{Safe(_settings.City)}_m{_settings.Method.Id}_s{(int)_settings.AsrSchool}_{year:0000}-{month:00}.json");
    }

    /// <summary>Deletes cache files not touched for 60 days (old months, cities no longer used).</summary>
    private static void PruneCache()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(CacheDir, "*.json"))
                if (File.GetLastWriteTime(file) < DateTime.Now.AddDays(-60)) File.Delete(file);
        }
        catch { }
    }

    private void EnsureMonthLoadedFromDisk(int year, int month)
    {
        if (!_loadedMonths.Add((year, month))) return;
        var path = CachePath(year, month);
        if (!File.Exists(path)) return;
        try
        {
            foreach (var (date, times) in Parse(File.ReadAllText(path), _settings.TimeZone))
                _days[date] = times;
        }
        catch (Exception ex)
        {
            Log($"Cache {path} unreadable: {ex.Message}");
        }
    }

    private static readonly (string Key, Prayer Prayer)[] Keys =
    {
        ("Fajr", Prayer.Fajr), ("Sunrise", Prayer.Sunrise), ("Dhuhr", Prayer.Dhuhr),
        ("Asr", Prayer.Asr), ("Maghrib", Prayer.Maghrib), ("Isha", Prayer.Isha),
    };

    /// <summary>Parses an Aladhan calendar response into per-day prayer times.</summary>
    /// <param name="json">Raw response of /v1/calendar or /v1/calendarByCity.</param>
    /// <param name="tz">The city's zone, used when a time carries a zone abbreviation
    /// instead of a numeric offset (see <see cref="ParseTiming"/>).</param>
    internal static Dictionary<DateOnly, List<PrayerTime>> Parse(string json, TimeZoneInfo tz)
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
                list.Add(new PrayerTime(prayer, ParseTiming(date, timings.GetProperty(key).GetString()!, tz)));

            // At high latitudes in summer Isha (even Maghrib) can fall after midnight; Aladhan still
            // lists it under the day it belongs to ("00:01"), so move such times to the next day.
            var dhuhr = list.Single(p => p.Prayer == Prayer.Dhuhr).Time;
            for (int i = 0; i < list.Count; i++)
                if (list[i].Prayer is Prayer.Maghrib or Prayer.Isha && list[i].Time < dhuhr)
                    list[i] = list[i] with { Time = list[i].Time.AddDays(1) };
            result[date] = list;
        }
        return result;
    }

    /// <summary>
    /// Parses one Aladhan time into an absolute instant. The suffix is either a numeric offset
    /// ("05:11 (+03)", "05:11 (+0530)") or, for many zones, an abbreviation ("05:11 (BST)",
    /// "05:11 (PKT)") that cannot be resolved reliably, so then the city's own TimeZoneInfo
    /// supplies the offset for that local date and time (DST included).
    /// </summary>
    private static DateTimeOffset ParseTiming(DateOnly date, string value, TimeZoneInfo tz)
    {
        var parts = value.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var hm = TimeOnly.ParseExact(parts[0], "HH:mm", CultureInfo.InvariantCulture);
        var local = new DateTime(date.Year, date.Month, date.Day, hm.Hour, hm.Minute, 0, DateTimeKind.Unspecified);

        if (parts.Length > 1 && TryParseOffset(parts[1].Trim('(', ')', ' '), out var offset))
            return new DateTimeOffset(local, offset);

        // A wall-clock time skipped by a DST jump does not exist; move it past the gap.
        if (tz.IsInvalidTime(local)) local = local.AddHours(1);
        return new DateTimeOffset(local, tz.GetUtcOffset(local));
    }

    private static bool TryParseOffset(string s, out TimeSpan offset)
    {
        offset = TimeSpan.Zero;
        if (s.Length < 3 || (s[0] != '+' && s[0] != '-') || !s[1..].All(char.IsAsciiDigit)) return false;
        int sign = s[0] == '-' ? -1 : 1;
        var digits = s[1..];
        int hh = int.Parse(digits[..2], CultureInfo.InvariantCulture);
        int mm = digits.Length >= 4 ? int.Parse(digits[2..4], CultureInfo.InvariantCulture) : 0;
        offset = sign * new TimeSpan(hh, mm, 0);
        return true;
    }

    /// <summary>Appends to %AppData%\PrayerBar\log.txt (truncated at 256 KB). Never throws.</summary>
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
