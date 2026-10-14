using System.IO;
using Xunit;

namespace PrayerBar.Tests;

public class PrayerTimesTests
{
    private static readonly TimeZoneInfo Istanbul = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul");

    private static Dictionary<DateOnly, List<PrayerTime>> LoadSample() =>
        PrayerService.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "gaziantep-2026-10.json")));

    [Fact]
    public void Parses_aladhan_month()
    {
        var month = LoadSample();
        Assert.Equal(31, month.Count);
        var fajr = month[new DateOnly(2026, 10, 14)].Single(p => p.Prayer == Prayer.Fajr).Time;
        Assert.Equal(new DateTimeOffset(2026, 10, 14, 5, 11, 0, TimeSpan.FromHours(3)), fajr);
    }

    [Fact]
    public void Offline_calculation_matches_aladhan_method_13_within_one_minute()
    {
        foreach (var (date, apiTimes) in LoadSample())
        {
            var calc = PrayerCalculator.Compute(date, 37.0662, 37.3833, Istanbul);
            foreach (var api in apiTimes)
            {
                var mine = calc.Single(c => c.Prayer == api.Prayer).Time;
                var diff = Math.Abs((mine - api.Time).TotalMinutes);
                Assert.True(diff <= 1, $"{date} {api.Prayer}: api {api.Time:HH:mm} calc {mine:HH:mm}");
            }
        }
    }
}
