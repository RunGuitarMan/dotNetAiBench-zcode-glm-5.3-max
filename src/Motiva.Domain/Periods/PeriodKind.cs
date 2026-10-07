using NodaTime;

namespace Motiva.Domain.Periods;

public enum PeriodKind
{
    Day = 0,
    Month = 1,
    Quarter = 2,
    HalfYear = 3,
    Year = 4,
}

/// <summary>
/// Calendar periods of a company (B13): day/month/quarter/half-year/year, bound to the
/// company calendar — never to the first completion. Quarters start Jan/Apr/Jul/Oct,
/// half-years Jan/Jul. A period is [start, end): the start instant is included, the end
/// excluded. The first period of a campaign that starts mid-period is shorter, and the
/// goal/reward are not reduced.
/// </summary>
public static class PeriodCalendar
{
    /// <summary>Returns the start of the calendar period containing <paramref name="instant"/>.</summary>
    public static Instant PeriodStart(Instant instant, PeriodKind kind, DateTimeZone zone)
    {
        var local = instant.InZone(zone);
        var date = local.Date;
        var startOfYear = new LocalDate(date.Year, 1, 1);
        var periodStart = kind switch
        {
            PeriodKind.Day => date,
            PeriodKind.Month => new LocalDate(date.Year, date.Month, 1),
            PeriodKind.Quarter => new LocalDate(date.Year, (((date.Month - 1) / 3) * 3) + 1, 1),
            PeriodKind.HalfYear => new LocalDate(date.Year, date.Month <= 6 ? 1 : 7, 1),
            PeriodKind.Year => startOfYear,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        return periodStart.AtMidnight().InZoneLeniently(zone).ToInstant();
    }

    /// <summary>Returns the exclusive end of the calendar period starting at <paramref name="periodStart"/>.</summary>
    public static Instant NextPeriodStart(Instant periodStart, PeriodKind kind, DateTimeZone zone)
    {
        var local = periodStart.InZone(zone);
        var date = local.Date;
        var next = kind switch
        {
            PeriodKind.Day => date.PlusDays(1),
            PeriodKind.Month => date.PlusMonths(1),
            PeriodKind.Quarter => date.PlusMonths(3),
            PeriodKind.HalfYear => date.PlusMonths(6),
            PeriodKind.Year => new LocalDate(date.Year + 1, 1, 1),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        return next.AtMidnight().InZoneLeniently(zone).ToInstant();
    }

    /// <summary>
    /// The actual period interval for a task acceptance: the calendar period clamped by the
    /// campaign window on both sides (B13.5). The returned start identifies the period for
    /// completion uniqueness.
    /// </summary>
    public static (Instant Start, Instant End) EffectivePeriod(
        Instant acceptedAt, PeriodKind kind, DateTimeZone zone, Instant campaignStartsAt, Instant campaignEndsAt)
    {
        var calendarStart = PeriodStart(acceptedAt, kind, zone);
        var calendarEnd = NextPeriodStart(calendarStart, kind, zone);
        var start = Instant.Max(calendarStart, campaignStartsAt);
        var end = Instant.Min(calendarEnd, campaignEndsAt);
        return (start, end);
    }
}
