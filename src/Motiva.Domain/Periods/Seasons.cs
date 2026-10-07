using NodaTime;

namespace Motiva.Domain.Periods;

/// <summary>A season is a calendar year in the company time zone (B10.1).</summary>
public static class Seasons
{
    public static int SeasonOf(Instant instant, DateTimeZone zone)
    {
        return instant.InZone(zone).Year;
    }

    public static Instant SeasonStart(int season, DateTimeZone zone)
    {
        return new LocalDate(season, 1, 1).AtMidnight().InZoneLeniently(zone).ToInstant();
    }

    public static Instant SeasonEnd(int season, DateTimeZone zone)
    {
        // Exclusive end: the start of the next season. For Tallinn (offset without DST at
        // midnight in winter) the excluded end may coincide with the start of the next season.
        return new LocalDate(season + 1, 1, 1).AtMidnight().InZoneLeniently(zone).ToInstant();
    }
}
