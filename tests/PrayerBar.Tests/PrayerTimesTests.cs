using System.IO;
using Xunit;

namespace PrayerBar.Tests;

/// <summary>
/// The offline calculator is checked against real Aladhan responses (Data/*.json, fetched with
/// the same coordinates/method/school), one month each, covering every special case the
/// calculator implements: Isha as minutes, Maghrib as angle or minutes, built-in offsets,
/// Hanafi Asr, DST, and high latitudes in June.
/// </summary>
public class PrayerTimesTests
{
    private static Dictionary<DateOnly, List<PrayerTime>> Load(string name, string tzId = "Europe/Istanbul") =>
        PrayerService.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", name + ".json")),
            TimeZoneInfo.FindSystemTimeZoneById(tzId));

    [Fact]
    public void Parses_zone_abbreviations_using_the_city_time_zone()
    {
        // The London fixture uses "(BST)" suffixes; BST is UTC+1 in June.
        var month = Load("london-m3-2026-06", "Europe/London");
        var dhuhr = month[new DateOnly(2026, 6, 1)].Single(p => p.Prayer == Prayer.Dhuhr).Time;
        Assert.Equal(TimeSpan.FromHours(1), dhuhr.Offset);
    }

    [Fact]
    public void Parses_aladhan_month()
    {
        var month = Load("gaziantep-2026-10");
        Assert.Equal(31, month.Count);
        var fajr = month[new DateOnly(2026, 10, 14)].Single(p => p.Prayer == Prayer.Fajr).Time;
        Assert.Equal(new DateTimeOffset(2026, 10, 14, 5, 11, 0, TimeSpan.FromHours(3)), fajr);
    }

    [Theory]
    [InlineData("gaziantep-2026-10", 37.0662, 37.3833, "Europe/Istanbul", 13, AsrSchool.Shafi)]
    [InlineData("gaziantep-m13-2026-03", 37.0594, 37.3825, "Europe/Istanbul", 13, AsrSchool.Shafi)]
    [InlineData("riyadh-m4-2026-10", 24.6877, 46.7219, "Asia/Riyadh", 4, AsrSchool.Shafi)]
    [InlineData("dubai-m16-2026-10", 25.0772, 55.3093, "Asia/Dubai", 16, AsrSchool.Shafi)]
    [InlineData("casablanca-m21-2026-10", 33.5883, -7.6114, "Africa/Casablanca", 21, AsrSchool.Shafi)]
    [InlineData("lisbon-m22-2026-10", 38.7167, -9.1333, "Europe/Lisbon", 22, AsrSchool.Shafi)]
    [InlineData("tehran-m7-2026-10", 35.6944, 51.4215, "Asia/Tehran", 7, AsrSchool.Shafi)]
    [InlineData("amman-m23-2026-10", 31.9552, 35.945, "Asia/Amman", 23, AsrSchool.Shafi)]
    [InlineData("karachi-m1-hanafi-2026-10", 24.8608, 67.0104, "Asia/Karachi", 1, AsrSchool.Hanafi)]
    [InlineData("london-m3-2026-06", 51.5085, -0.1257, "Europe/London", 3, AsrSchool.Shafi)]
    [InlineData("oslo-m3-2026-06", 59.9127, 10.7461, "Europe/Oslo", 3, AsrSchool.Shafi)]
    public void Offline_calculation_matches_aladhan_within_one_minute(
        string fixture, double lat, double lng, string tzId, int method, AsrSchool school)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(tzId);
        var failures = new List<string>();
        foreach (var (date, apiTimes) in Load(fixture, tzId))
        {
            var calc = PrayerCalculator.Compute(date, lat, lng, tz, CalculationMethods.Get(method), school);
            foreach (var api in apiTimes)
            {
                var mine = calc.Single(c => c.Prayer == api.Prayer).Time;
                if (Math.Abs((mine - api.Time).TotalMinutes) > 1)
                    failures.Add($"{date} {api.Prayer}: api {api.Time:HH:mm} calc {mine:HH:mm}");
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(10)));
    }
}
