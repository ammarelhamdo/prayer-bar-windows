using System.Text.Json;
using Xunit;

namespace PrayerBar.Tests;

public class SettingsTests
{
    [Fact]
    public void Appearance_is_stored_by_name_and_computed_values_are_not_stored()
    {
        var json = JsonSerializer.Serialize(new AppSettings { BarLayout = BarLayout.OneLine, BarWeight = BarWeight.ExtraBold });
        Assert.Contains("\"BarLayout\":\"OneLine\"", json);
        Assert.Contains("\"BarWeight\":\"ExtraBold\"", json);
        Assert.DoesNotContain("\"Method\"", json);
        Assert.DoesNotContain("\"TimeZone\"", json);
    }

    [Fact]
    public void Older_settings_files_load_with_new_defaults()
    {
        // The format written by the first version of the app (no language/appearance keys).
        var old = """{ "City": "Gaziantep", "Country": "TR", "NotifyMinutes": 10, "EmbedInTaskbar": true }""";
        var s = JsonSerializer.Deserialize<AppSettings>(old)!;
        Assert.Equal("ar", s.Language);
        Assert.Equal(BarLayout.TwoLines, s.BarLayout);
        Assert.Equal(BarWeight.Bold, s.BarWeight);
        Assert.Equal(13, s.Method.Id);
    }
}
