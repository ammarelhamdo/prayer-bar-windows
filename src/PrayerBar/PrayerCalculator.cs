namespace PrayerBar;

public enum Prayer { Fajr, Sunrise, Dhuhr, Asr, Maghrib, Isha }

public readonly record struct PrayerTime(Prayer Prayer, DateTimeOffset Time);

/// <summary>
/// Offline prayer-time calculation (the PrayTimes.org algorithm) with the parameters Aladhan uses
/// for method 13 / Diyanet: Fajr 18°, Isha 17°, Shafi Asr, angle-based high-latitude rule and
/// Diyanet's temkin offsets (sunrise -7, dhuhr +5, asr +4, maghrib +7 minutes).
/// </summary>
public static class PrayerCalculator
{
    private const double FajrAngle = 18;
    private const double IshaAngle = 17;
    private const double RiseSetAngle = 0.833;
    private const double AsrFactor = 1; // Shafi / standard

    private static readonly Dictionary<Prayer, int> OffsetMinutes = new()
    {
        [Prayer.Fajr] = 0,
        [Prayer.Sunrise] = -7,
        [Prayer.Dhuhr] = 5,
        [Prayer.Asr] = 4,
        [Prayer.Maghrib] = 7,
        [Prayer.Isha] = 0,
    };

    public static List<PrayerTime> Compute(DateOnly date, double lat, double lng, TimeZoneInfo tz)
    {
        var utcOffset = tz.GetUtcOffset(new DateTime(date.Year, date.Month, date.Day, 12, 0, 0, DateTimeKind.Unspecified));
        double jDate = Julian(date.Year, date.Month, date.Day) - lng / (15 * 24.0);

        // Initial guesses (hours), refined in one pass as in PrayTimes.
        double fajr = 5, sunrise = 6, dhuhr = 12, asr = 13, sunset = 18, isha = 18;
        fajr = SunAngleTime(jDate, lat, FajrAngle, fajr / 24, ccw: true);
        sunrise = SunAngleTime(jDate, lat, RiseSetAngle, sunrise / 24, ccw: true);
        dhuhr = MidDay(jDate, dhuhr / 24);
        asr = AsrTime(jDate, lat, AsrFactor, asr / 24);
        sunset = SunAngleTime(jDate, lat, RiseSetAngle, sunset / 24, ccw: false);
        isha = SunAngleTime(jDate, lat, IshaAngle, isha / 24, ccw: false);

        double shift = utcOffset.TotalHours - lng / 15;
        fajr += shift; sunrise += shift; dhuhr += shift; asr += shift; sunset += shift; isha += shift;

        // High-latitude adjustment (angle-based), applied when the twilight angle is never reached
        // or yields an implausibly long night portion.
        double night = TimeDiff(sunset, sunrise);
        fajr = AdjustHighLat(fajr, sunrise, FajrAngle / 60.0 * night, night, ccw: true);
        isha = AdjustHighLat(isha, sunset, IshaAngle / 60.0 * night, night, ccw: false);

        var hours = new Dictionary<Prayer, double>
        {
            [Prayer.Fajr] = fajr,
            [Prayer.Sunrise] = sunrise,
            [Prayer.Dhuhr] = dhuhr,
            [Prayer.Asr] = asr,
            [Prayer.Maghrib] = sunset,
            [Prayer.Isha] = isha,
        };

        var midnight = new DateTimeOffset(date.Year, date.Month, date.Day, 0, 0, 0, utcOffset);
        var result = new List<PrayerTime>(6);
        foreach (var (prayer, h) in hours)
        {
            // Aladhan rounds to the nearest minute after applying offsets.
            double minutes = Math.Round(h * 60 + OffsetMinutes[prayer], MidpointRounding.AwayFromZero);
            result.Add(new PrayerTime(prayer, midnight.AddMinutes(minutes)));
        }
        return result;
    }

    private static double AdjustHighLat(double time, double baseTime, double portion, double night, bool ccw)
    {
        double diff = ccw ? TimeDiff(time, baseTime) : TimeDiff(baseTime, time);
        if (double.IsNaN(time) || diff > portion)
            time = baseTime + (ccw ? -portion : portion);
        return time;
    }

    private static double MidDay(double jDate, double dayFraction)
    {
        double eqt = SunPosition(jDate + dayFraction).Equation;
        return FixHour(12 - eqt);
    }

    private static double SunAngleTime(double jDate, double lat, double angle, double dayFraction, bool ccw)
    {
        double decl = SunPosition(jDate + dayFraction).Declination;
        double noon = MidDay(jDate, dayFraction);
        double t = 1 / 15.0 * ArcCos((-Sin(angle) - Sin(decl) * Sin(lat)) / (Cos(decl) * Cos(lat)));
        return noon + (ccw ? -t : t);
    }

    private static double AsrTime(double jDate, double lat, double factor, double dayFraction)
    {
        double decl = SunPosition(jDate + dayFraction).Declination;
        double angle = -ArcCot(factor + Tan(Math.Abs(lat - decl)));
        return SunAngleTime(jDate, lat, angle, dayFraction, ccw: false);
    }

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
