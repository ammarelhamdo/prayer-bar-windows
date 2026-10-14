namespace PrayerBar;

/// <summary>Asr juristic school: Shafi/Maliki/Hanbali (shadow = 1x) or Hanafi (shadow = 2x).</summary>
public enum AsrSchool { Shafi = 0, Hanafi = 1 }

/// <summary>
/// One Aladhan calculation method, with the parameters needed to reproduce it offline.
/// Values mirror https://api.aladhan.com/v1/methods plus the built-in minute offsets Aladhan
/// reports in <c>meta.offset</c> for some methods (Turkey, Dubai, Morocco, Portugal).
/// </summary>
/// <param name="Id">Aladhan's <c>method</c> query value.</param>
/// <param name="FajrAngle">Sun depression angle for Fajr, in degrees.</param>
/// <param name="IshaAngle">Depression angle for Isha; null when Isha is a fixed delay after Maghrib.</param>
/// <param name="IshaMinutes">Minutes after Maghrib for Isha (used when <paramref name="IshaAngle"/> is null).</param>
/// <param name="MaghribAngle">Depression angle for Maghrib (Shia methods); null = sunset.</param>
/// <param name="MaghribMinutes">Minutes after sunset for Maghrib (when no angle is given).</param>
/// <param name="Offsets">Built-in tune offsets in minutes, applied last (like Aladhan does).</param>
public sealed record CalculationMethod(
    int Id,
    string NameEn,
    string NameAr,
    double FajrAngle,
    double? IshaAngle,
    int IshaMinutes,
    double? MaghribAngle,
    int MaghribMinutes,
    IReadOnlyDictionary<Prayer, int> Offsets)
{
    public string Name(bool arabic) => arabic ? NameAr : NameEn;
}

public static class CalculationMethods
{
    private static readonly IReadOnlyDictionary<Prayer, int> NoOffsets = new Dictionary<Prayer, int>();

    private static CalculationMethod M(int id, string en, string ar, double fajr, double? isha,
        int ishaMin = 0, double? maghribAngle = null, int maghribMin = 0,
        IReadOnlyDictionary<Prayer, int>? offsets = null) =>
        new(id, en, ar, fajr, isha, ishaMin, maghribAngle, maghribMin, offsets ?? NoOffsets);

    /// <summary>All methods Aladhan supports, ordered by id. (15 "Moonsighting Committee" uses a
    /// seasonal model; offline it is approximated with 18°/18°.)</summary>
    public static IReadOnlyList<CalculationMethod> All { get; } = new[]
    {
        M(0,  "Shia Ithna-Ashari (Qum)",               "الشيعة الاثنا عشرية (قم)",          16,   14,  maghribAngle: 4),
        M(1,  "University of Islamic Sciences, Karachi", "جامعة العلوم الإسلامية، كراتشي",   18,   18),
        M(2,  "ISNA (North America)",                  "الجمعية الإسلامية لأمريكا الشمالية", 15,   15),
        M(3,  "Muslim World League",                   "رابطة العالم الإسلامي",             18,   17),
        M(4,  "Umm Al-Qura, Makkah",                   "أم القرى، مكة المكرمة",             18.5, null, ishaMin: 90),
        M(5,  "Egyptian General Authority of Survey",  "الهيئة المصرية العامة للمساحة",     19.5, 17.5),
        M(7,  "University of Tehran",                  "جامعة طهران",                       17.7, 14,  maghribAngle: 4.5),
        M(8,  "Gulf Region",                           "منطقة الخليج",                      19.5, null, ishaMin: 90),
        M(9,  "Kuwait",                                "الكويت",                            18,   17.5),
        M(10, "Qatar",                                 "قطر",                               18,   null, ishaMin: 90),
        M(11, "Singapore (MUIS)",                      "سنغافورة",                          20,   18),
        M(12, "UOIF (France)",                         "اتحاد المنظمات الإسلامية في فرنسا", 12,   12),
        M(13, "Diyanet (Turkey)",                      "رئاسة الشؤون الدينية التركية (ديانت)", 18, 17, offsets: new Dictionary<Prayer, int>
        {
            [Prayer.Sunrise] = -7, [Prayer.Dhuhr] = 5, [Prayer.Asr] = 4, [Prayer.Maghrib] = 7,
        }),
        M(14, "Spiritual Administration of Muslims of Russia", "الإدارة الدينية لمسلمي روسيا", 16, 15),
        M(15, "Moonsighting Committee Worldwide",      "لجنة رؤية الهلال العالمية",          18,   18),
        M(16, "Dubai",                                 "دبي",                               18.2, 18.2, offsets: new Dictionary<Prayer, int>
        {
            [Prayer.Dhuhr] = 3, [Prayer.Maghrib] = 3,
        }),
        M(17, "JAKIM (Malaysia)",                      "جاكيم (ماليزيا)",                   20,   18),
        M(18, "Tunisia",                               "تونس",                              18,   18),
        M(19, "Algeria",                               "الجزائر",                           18,   17),
        M(20, "KEMENAG (Indonesia)",                   "كيميناغ (إندونيسيا)",              20,   18),
        M(21, "Morocco",                               "المغرب",                            19,   17, offsets: new Dictionary<Prayer, int>
        {
            [Prayer.Dhuhr] = 5, [Prayer.Maghrib] = 5,
        }),
        M(22, "Comunidade Islâmica de Lisboa",         "الجماعة الإسلامية في لشبونة",       18,   null, ishaMin: 77, maghribMin: 3,
            offsets: new Dictionary<Prayer, int> { [Prayer.Dhuhr] = 5 }),
        M(23, "Jordan",                                "الأردن",                            18,   18,  maghribMin: 5),
    };

    /// <summary>Muslim World League, Aladhan's fallback when nothing else applies.</summary>
    public static CalculationMethod Default => Get(3);

    public static CalculationMethod Get(int id) => All.FirstOrDefault(m => m.Id == id) ?? All.First(m => m.Id == 3);
}
