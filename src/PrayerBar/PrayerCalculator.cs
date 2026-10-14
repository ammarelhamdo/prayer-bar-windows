namespace PrayerBar;

public enum Prayer { Fajr, Sunrise, Dhuhr, Asr, Maghrib, Isha }

public readonly record struct PrayerTime(Prayer Prayer, DateTimeOffset Time);

/// <summary>
/// Offline prayer-time calculation: a C# port of the PrayTimes.org algorithm (the same model
/// Aladhan uses), driven by a <see cref="CalculationMethod"/>. Defaults match Aladhan's API:
/// angle-based high-latitude rule, 0.833° sunrise/sunset refraction, results rounded to the minute.
/// </summary>
/// <remarks>
/// Order of operations follows PrayTimes so results line up with the API:
/// 1. solve each time from the sun's position (one refinement pass);
/// 2. shift from local solar time to the zone's clock time;
/// 3. high-latitude correction for angle-based Fajr/Isha/Maghrib;
/// 4. minute-based Maghrib (after sunset) and Isha (after Maghrib);
/// 5. the method's built-in tune offsets, then rounding.
/// </remarks>
public static class PrayerCalculator
{
    private const double RiseSetAngle = 0.833;

    /// <summary>Times for one calendar day at a location.</summary>
    /// <param name="date">Calendar date in the location's own time zone.</param>
    /// <param name="lat">Latitude in degrees (north positive).</param>
    /// <param name="lng">Longitude in degrees (east positive).</param>
    /// <param name="tz">The location's time zone (DST is resolved for that date).</param>
    /// <param name="method">Angles / delays / offsets to use.</param>
    /// <param name="school">Asr shadow factor.</param>
    /// <returns>Six times (Fajr, Sunrise, Dhuhr, Asr, Maghrib, Isha) as absolute instants.</returns>
    public static List<PrayerTime> Compute(DateOnly date, double lat, double lng, TimeZoneInfo tz,
        CalculationMethod method, AsrSchool school = AsrSchool.Shafi)
    {
        var utcOffset = tz.GetUtcOffset(new DateTime(date.Year, date.Month, date.Day, 12, 0, 0, DateTimeKind.Unspecified));
        double jDate = Julian(date.Year, date.Month, date.Day) - lng / (15 * 24.0);
        double asrFactor = school == AsrSchool.Hanafi ? 2 : 1;

        // 1. Solve from initial guesses (hours / 24 = fraction of the day), as PrayTimes does.
        double fajr = SunAngleTime(jDate, lat, method.FajrAngle, 5 / 24.0, ccw: true);
        double sunrise = SunAngleTime(jDate, lat, RiseSetAngle, 6 / 24.0, ccw: true);
        double dhuhr = MidDay(jDate, 12 / 24.0);
        double asr = AsrTime(jDate, lat, asrFactor, 13 / 24.0);
        double sunset = SunAngleTime(jDate, lat, RiseSetAngle, 18 / 24.0, ccw: false);
        double maghrib = method.MaghribAngle is { } ma ? SunAngleTime(jDate, lat, ma, 18 / 24.0, ccw: false) : sunset;
        double isha = method.IshaAngle is { } ia ? SunAngleTime(jDate, lat, ia, 18 / 24.0, ccw: false) : double.NaN;

        // 2. Local solar time -> clock time of the zone.
        double shift = utcOffset.TotalHours - lng / 15;
        fajr += shift; sunrise += shift; dhuhr += shift; asr += shift; sunset += shift; maghrib += shift; isha += shift;

        // 3. Angle-based high-latitude rule: twilight may never reach the angle (NaN) or the
        //    result may eat too much of the night; clamp to (angle / 60) of the night.
        double night = TimeDiff(sunset, sunrise);
        fajr = AdjustHighLat(fajr, sunrise, method.FajrAngle / 60.0 * night, ccw: true);
        if (method.IshaAngle is { } ishaAngle)
            isha = AdjustHighLat(isha, sunset, ishaAngle / 60.0 * night, ccw: false);
        if (method.MaghribAngle is { } maghribAngle)
            maghrib = AdjustHighLat(maghrib, sunset, maghribAngle / 60.0 * night, ccw: false);

        // 4. Minute-based definitions.
        if (method.MaghribAngle is null) maghrib = sunset + method.MaghribMinutes / 60.0;
        if (method.IshaAngle is null) isha = maghrib + method.IshaMinutes / 60.0;

        var hours = new (Prayer Prayer, double Hours)[]
        {
            (Prayer.Fajr, fajr), (Prayer.Sunrise, sunrise), (Prayer.Dhuhr, dhuhr),
            (Prayer.Asr, asr), (Prayer.Maghrib, maghrib), (Prayer.Isha, isha),
        };

        // 5. Tune offsets, then round to the nearest minute (Aladhan does the same).
        var midnight = new DateTimeOffset(date.Year, date.Month, date.Day, 0, 0, 0, utcOffset);
        var result = new List<PrayerTime>(hours.Length);
        foreach (var (prayer, h) in hours)
        {
            int offset = method.Offsets.TryGetValue(prayer, out var o) ? o : 0;
            double minutes = Math.Round(h * 60 + offset, MidpointRounding.AwayFromZero);
            result.Add(new PrayerTime(prayer, midnight.AddMinutes(minutes)));
        }
        return result;
    }

    private static double AdjustHighLat(double time, double baseTime, double portion, bool ccw)
    {
        double diff = ccw ? TimeDiff(time, baseTime) : TimeDiff(baseTime, time);
        if (double.IsNaN(time) || diff > portion)
            time = baseTime + (ccw ? -portion : portion);
        return time;
    }

    /// <summary>Solar noon (hours) for the day, from the equation of time.</summary>
    private static double MidDay(double jDate, double dayFraction)
    {
        double eqt = SunPosition(jDate + dayFraction).Equation;
        return FixHour(12 - eqt);
    }

    /// <summary>Time (hours) when the sun is <paramref name="angle"/>° below the horizon,
    /// before noon if <paramref name="ccw"/> else after. NaN if it never gets that low.</summary>
    private static double SunAngleTime(double jDate, double lat, double angle, double dayFraction, bool ccw)
    {
        double decl = SunPosition(jDate + dayFraction).Declination;
        double noon = MidDay(jDate, dayFraction);
        double t = 1 / 15.0 * ArcCos((-Sin(angle) - Sin(decl) * Sin(lat)) / (Cos(decl) * Cos(lat)));
        return noon + (ccw ? -t : t);
    }

    /// <summary>Asr: when an object's shadow equals its noon shadow plus factor x its height.</summary>
    private static double AsrTime(double jDate, double lat, double factor, double dayFraction)
    {
        double decl = SunPosition(jDate + dayFraction).Declination;
        double angle = -ArcCot(factor + Tan(Math.Abs(lat - decl)));
        return SunAngleTime(jDate, lat, angle, dayFraction, ccw: false);
    }

    /// <summary>Sun declination (degrees) and equation of time (hours) for a Julian date.
    /// Low-precision formulae from the U.S. Naval Observatory, accurate to ~1 arc-minute.</summary>
    private static (double Declination, double Equation) SunPosition(double jd)
    {
        double d = jd - 2451545.0;
        double g = FixAngle(357.529 + 0.98560028 * d);
        double q = FixAngle(280.459 + 0.98564736 * d);
        double l = FixAngle(q + 1.915 * Sin(g) + 0.020 * Sin(2 * g));
        double e = 23.439 - 0.00000036 * d;
        double ra = ArcTan2(Cos(e) * Sin(l), Cos(l)) / 15;
        double eqt = q / 15 - FixHour(ra);
        double decl = ArcSin(Sin(e) * Sin(l));
        return (decl, eqt);
    }

    private static double Julian(int year, int month, int day)
    {
        if (month <= 2) { year -= 1; month += 12; }
        double a = Math.Floor(year / 100.0);
        double b = 2 - a + Math.Floor(a / 4);
        return Math.Floor(365.25 * (year + 4716)) + Math.Floor(30.6001 * (month + 1)) + day + b - 1524.5;
    }

    private static double TimeDiff(double t1, double t2) => FixHour(t2 - t1);
    private static double FixAngle(double a) => Fix(a, 360);
    private static double FixHour(double a) => Fix(a, 24);
    private static double Fix(double a, double b) { a -= b * Math.Floor(a / b); return a < 0 ? a + b : a; }

    // Degree-based trigonometry helpers.
    private static double Dtr(double d) => d * Math.PI / 180;
    private static double Rtd(double r) => r * 180 / Math.PI;
    private static double Sin(double d) => Math.Sin(Dtr(d));
    private static double Cos(double d) => Math.Cos(Dtr(d));
    private static double Tan(double d) => Math.Tan(Dtr(d));
    private static double ArcSin(double x) => Rtd(Math.Asin(x));
    private static double ArcCos(double x) => Rtd(Math.Acos(x));
    private static double ArcTan2(double y, double x) => Rtd(Math.Atan2(y, x));
    private static double ArcCot(double x) => Rtd(Math.Atan(1 / x));
}
