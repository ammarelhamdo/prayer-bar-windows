namespace PrayerBar;

public static class Arabic
{
    public static string Name(Prayer p) => p switch
    {
        Prayer.Fajr => "الفجر",
        Prayer.Sunrise => "الشروق",
        Prayer.Dhuhr => "الظهر",
        Prayer.Asr => "العصر",
        Prayer.Maghrib => "المغرب",
        Prayer.Isha => "العشاء",
        _ => p.ToString(),
    };
}
