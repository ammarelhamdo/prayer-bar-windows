using System.Globalization;
using System.Windows;

namespace PrayerBar;

/// <summary>
/// Minimal two-language string table (Arabic / English). Every user-visible string goes through
/// <see cref="T"/>, so switching language is just changing <see cref="Arabic"/> and re-reading
/// the strings; no resource assemblies or satellite DLLs, which keeps the single-file exe simple.
/// </summary>
public static class Loc
{
    /// <summary>Current UI language. Set from <see cref="AppSettings.Language"/>.</summary>
    public static bool Arabic { get; set; } = true;

    public static FlowDirection Flow => Arabic ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    /// <summary>"ar" if Windows' display language is Arabic, otherwise "en" (first-run default).</summary>
    public static string SystemDefaultLanguage =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ar" ? "ar" : "en";

    // key -> (English, Arabic)
    private static readonly Dictionary<string, (string En, string Ar)> Strings = new()
    {
        ["Fajr"] = ("Fajr", "الفجر"),
        ["Sunrise"] = ("Sunrise", "الشروق"),
        ["Dhuhr"] = ("Dhuhr", "الظهر"),
        ["Asr"] = ("Asr", "العصر"),
        ["Maghrib"] = ("Maghrib", "المغرب"),
        ["Isha"] = ("Isha", "العشاء"),

        // Tray menu
        ["Menu.Settings"] = ("Settings…", "الإعدادات…"),
        ["Menu.Startup"] = ("Run at startup", "التشغيل مع بدء ويندوز"),
        ["Menu.Refresh"] = ("Refresh times", "تحديث الأوقات"),
        ["Menu.Exit"] = ("Exit", "خروج"),
        ["Tray.In"] = ("{0} in {1}", "{0} بعد {1}"),

        // Toasts
        ["Toast.Soon"] = ("{0} is near", "اقترب وقت {0}"),
        ["Toast.SoonBody"] = ("In {0} — {1}", "بعد {0} — {1}"),
        ["Toast.Refreshed"] = ("Prayer times updated", "تم تحديث أوقات الصلاة"),
        ["Toast.RefreshFailed"] = ("Couldn't update prayer times", "تعذر تحديث أوقات الصلاة"),
        ["Toast.RefreshFailedBody"] = ("Using times calculated on this PC.", "سيتم استخدام الأوقات المحسوبة محلياً."),

        // Bar tooltip
        ["Tip.SourceApi"] = ("Source: Aladhan — {0}", "المصدر: Aladhan — {0}"),
        ["Tip.SourceLocal"] = ("Source: calculated offline — {0}", "المصدر: حساب محلي دون اتصال — {0}"),

        // Settings window
        ["Settings.Title"] = ("PrayerBar Settings", "إعدادات PrayerBar"),
        ["Settings.Header"] = ("Settings", "الإعدادات"),
        ["Settings.Language"] = ("Language", "اللغة"),
        ["Settings.Location"] = ("Location", "الموقع"),
        ["Settings.Country"] = ("Country", "الدولة"),
        ["Settings.City"] = ("City", "المدينة"),
        ["Settings.Search"] = ("Search…", "ابحث…"),
        ["Settings.NoResults"] = ("No matches", "لا توجد نتائج"),
        ["Settings.Notify"] = ("Notify before prayer", "التنبيه قبل الصلاة"),
        ["Settings.NotifyOff"] = ("Off", "إيقاف"),
        ["Settings.Advanced"] = ("Advanced", "خيارات متقدمة"),
        ["Settings.Method"] = ("Calculation method", "طريقة الحساب"),
        ["Settings.MethodAuto"] = ("Automatic — {0}", "تلقائي — {0}"),
        ["Settings.Asr"] = ("Asr", "العصر"),
        ["Settings.Shafi"] = ("Standard (Shafi, Maliki, Hanbali)", "الجمهور (شافعي، مالكي، حنبلي)"),
        ["Settings.Hanafi"] = ("Hanafi", "حنفي"),
        ["Settings.Embed"] = ("Embed in the taskbar", "دمج داخل شريط المهام"),
        ["Settings.Layout"] = ("Taskbar layout", "شكل العدّاد"),
        ["Settings.TwoLines"] = ("Two lines", "سطران"),
        ["Settings.OneLine"] = ("One line", "سطر واحد"),
        ["Settings.Weight"] = ("Text weight", "سماكة الخط"),
        ["Settings.WeightNormal"] = ("Normal", "عادي"),
        ["Settings.WeightBold"] = ("Bold", "عريض"),
        ["Settings.WeightExtra"] = ("Extra bold", "عريض جداً"),
        ["Settings.Preview"] = ("Today", "اليوم"),
        ["Settings.Save"] = ("Save", "حفظ"),
        ["Settings.Cancel"] = ("Cancel", "إلغاء"),
        ["Uninstall.Done"] = ("PrayerBar's settings, cache, startup entry and notification registration were removed. You can now delete PrayerBar.exe.",
                              "تمت إزالة إعدادات PrayerBar والذاكرة المؤقتة والتشغيل التلقائي وتسجيل الإشعارات. يمكنك الآن حذف PrayerBar.exe."),
        ["Uninstall.Running"] = ("PrayerBar is running. Choose Exit from its tray menu, then run --uninstall again.",
                                 "PrayerBar يعمل حالياً. اختر «خروج» من قائمة الأيقونة ثم أعد تشغيل ‎--uninstall."),
        ["Settings.PickCity"] = ("Please choose a city.", "يرجى اختيار المدينة."),
    };

    /// <summary>Localized string for <paramref name="key"/>, formatted with <paramref name="args"/>.</summary>
    public static string T(string key, params object[] args)
    {
        var s = Strings.TryGetValue(key, out var pair) ? (Arabic ? pair.Ar : pair.En) : key;
        return args.Length == 0 ? s : string.Format(CultureInfo.InvariantCulture, s, args);
    }

    public static string Name(Prayer p) => T(p.ToString());

    /// <summary>"5 minutes" with correct Arabic number agreement (1, 2, 3–10, 11+).</summary>
    public static string Minutes(int n)
    {
        if (!Arabic) return n == 1 ? "1 minute" : $"{n} minutes";
        return n switch
        {
            1 => "دقيقة واحدة",
            2 => "دقيقتين",
            >= 3 and <= 10 => $"{n} دقائق",
            _ => $"{n} دقيقة",
        };
    }
}
