using Xunit;

namespace PrayerBar.Tests;

public class CityCatalogTests
{
    private static CityCatalog Catalog => CityCatalog.Instance;

    [Fact]
    public void Bundled_catalog_loads_with_both_languages()
    {
        Assert.True(Catalog.Countries.Count > 200);
        Assert.All(Catalog.Countries, c =>
        {
            Assert.False(string.IsNullOrWhiteSpace(c.En));
            Assert.False(string.IsNullOrWhiteSpace(c.Ar));
            Assert.NotEmpty(c.Cities);
            Assert.All(c.Cities, city => Assert.False(string.IsNullOrWhiteSpace(city.Tz)));
        });
    }

    [Fact]
    public void Turkey_uses_diyanet_and_has_gaziantep()
    {
        var tr = Catalog.FindCountry("TR")!;
        Assert.Equal(13, tr.Method);
        var city = Catalog.FindCity("TR", "Gaziantep")!;
        Assert.Equal("غازي عنتاب", city.Ar);
        Assert.Equal("Europe/Istanbul", city.Tz);
    }

    [Theory]
    [InlineData("SA", 4)]
    [InlineData("EG", 5)]
    [InlineData("PK", 1)]
    [InlineData("US", 2)]
    public void Countries_get_aladhan_default_method(string code, int method) =>
        Assert.Equal(method, Catalog.FindCountry(code)!.Method);

    [Fact]
    public void Every_country_method_is_known() =>
        Assert.All(Catalog.Countries, c => Assert.Contains(CalculationMethods.All, m => m.Id == c.Method));

    [Theory]
    [InlineData("Şanlıurfa", "sanliurfa")]
    [InlineData("إسطنبول", "اسطنبول")]
    [InlineData("مكة المكرّمة", "مكه المكرمه")]
    public void Fold_ignores_accents_and_arabic_letter_variants(string input, string expected) =>
        Assert.Equal(CityCatalog.Fold(expected), CityCatalog.Fold(input));

    [Fact]
    public void Default_settings_resolve_to_a_catalog_city()
    {
        var s = new AppSettings();
        Assert.NotNull(Catalog.FindCity(s.Country, s.City));
        Assert.Equal(13, s.Method.Id);
    }
}
