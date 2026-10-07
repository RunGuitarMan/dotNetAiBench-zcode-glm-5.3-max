using Motiva.Domain;
using Motiva.Domain.Audiences;
using Motiva.Domain.Competitions;
using Motiva.Domain.Periods;
using Motiva.Domain.Progress;
using NodaTime;
using Xunit;

namespace Motiva.Tests.Unit;

/// <summary>Domain rules unit tests: expectations derive from B-rules and §9 examples, never
/// from the current output of the application.</summary>
public sealed class PeriodCalendarTests
{
    private static readonly DateTimeZone Tallinn = DateTimeZoneProviders.Tzdb["Europe/Tallinn"];

    private static Instant At(int year, int month, int day, int hour, int minute = 0, int second = 0)
        => new LocalDateTime(year, month, day, hour, minute, second).InZoneLeniently(Tallinn).ToInstant();

    [Fact]
    public void Day_periods_follow_tallinn_midnight_e11()
    {
        var lateEvening = At(2026, 4, 11, 23, 59, 50);
        var justAfterMidnight = At(2026, 4, 12, 0, 0, 1);
        var day1 = PeriodCalendar.PeriodStart(lateEvening, PeriodKind.Day, Tallinn);
        var day2 = PeriodCalendar.PeriodStart(justAfterMidnight, PeriodKind.Day, Tallinn);
        Assert.NotEqual(day1, day2);
        Assert.Equal(At(2026, 4, 11, 0, 0, 0), day1);
        Assert.Equal(At(2026, 4, 12, 0, 0, 0), day2);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(3, 1)]
    [InlineData(4, 4)]
    [InlineData(7, 7)]
    [InlineData(11, 10)]
    public void Quarters_start_jan_apr_jul_oct(int month, int expectedStartMonth)
    {
        var instant = At(2026, month, 15, 12);
        var start = PeriodCalendar.PeriodStart(instant, PeriodKind.Quarter, Tallinn);
        Assert.Equal(new LocalDate(2026, expectedStartMonth, 1), start.InZone(Tallinn).Date);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(6, 1)]
    [InlineData(7, 7)]
    [InlineData(12, 7)]
    public void Half_years_start_jan_and_jul(int month, int expectedStartMonth)
    {
        var instant = At(2026, month, 15, 12);
        var start = PeriodCalendar.PeriodStart(instant, PeriodKind.HalfYear, Tallinn);
        Assert.Equal(new LocalDate(2026, expectedStartMonth, 1), start.InZone(Tallinn).Date);
    }

    [Fact]
    public void First_month_of_mid_month_campaign_is_shortened_edge01()
    {
        var campaignStart = At(2026, 3, 15, 0, 0, 0);
        var campaignEnd = At(2026, 6, 30, 21, 0, 0);
        var accepted = At(2026, 3, 20, 12);
        var (start, end) = PeriodCalendar.EffectivePeriod(accepted, PeriodKind.Month, Tallinn, campaignStart, campaignEnd);
        Assert.Equal(At(2026, 3, 15, 0, 0, 0), start);
        Assert.Equal(At(2026, 4, 1, 0, 0, 0), end);
    }

    [Fact]
    public void Period_start_is_included_end_excluded_b13d()
    {
        var campaignStart = At(2026, 1, 1, 0, 0, 0);
        var campaignEnd = At(2026, 12, 31, 21, 0, 0);
        var start = PeriodCalendar.PeriodStart(At(2026, 4, 1, 0, 0, 0), PeriodKind.Month, Tallinn);
        Assert.Equal(At(2026, 4, 1, 0, 0, 0), start);
        var next = PeriodCalendar.NextPeriodStart(start, PeriodKind.Month, Tallinn);
        Assert.Equal(At(2026, 5, 1, 0, 0, 0), next);
    }

    [Fact]
    public void Season_is_calendar_year_in_company_zone_b10()
    {
        Assert.Equal(2025, Seasons.SeasonOf(At(2025, 12, 30, 14), Tallinn));
        Assert.Equal(2026, Seasons.SeasonOf(At(2026, 1, 5, 10), Tallinn));
    }
}

public sealed class ProgressRuleTests
{
    [Fact]
    public void Min_rule_caps_at_goal_e03()
    {
        // goal 3, was 2, +5 -> current 3, credited 1
        var (current, credited) = ProgressRule.Apply(3, 2, 5);
        Assert.Equal(3, current);
        Assert.Equal(1, credited);
    }

    [Fact]
    public void Event_after_goal_credits_zero_b15c()
    {
        var (current, credited) = ProgressRule.Apply(3, 3, 1);
        Assert.Equal(3, current);
        Assert.Equal(0, credited);
    }

    [Theory]
    [InlineData(0, 0, 1)]
    [InlineData(3, -1, 1)]
    [InlineData(3, 0, 0)]
    [InlineData(3, 0, -2)]
    public void Invalid_inputs_are_rejected(long goal, long current, long delta)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ProgressRule.Apply(goal, current, delta));
    }
}

public sealed class RankingTests
{
    [Fact]
    public void Equal_scores_share_place_100_100_90_e10_and_b32()
    {
        var rows = Ranking.Rank(new[] { (90, 90L), (100, 100L), (101, 100) });
        // input: masterId 90 score 90; masterId 100 score 100; masterId 101 score 100
        Assert.Equal(1, rows[0].Place);
        Assert.Equal(1, rows[1].Place);
        Assert.Equal(3, rows[2].Place);
        Assert.Equal(100, rows[0].MasterId);
        Assert.Equal(101, rows[1].MasterId); // equal score -> ascending masterId
        Assert.Equal(90, rows[2].MasterId);
    }

    [Fact]
    public void Zero_score_participant_gets_last_place_edge04()
    {
        var rows = Ranking.Rank(new[] { (1, 100L), (2, 100L), (3, 90), (4, 0) });
        Assert.Equal(1, rows[0].Place);
        Assert.Equal(1, rows[1].Place);
        Assert.Equal(3, rows[2].Place);
        Assert.Equal(4, rows[3].Place);
        Assert.Equal(0, rows[3].Score);
    }
}

public sealed class AudienceRuleTests
{
    private static HashSet<string> Tags(params string[] tags) => new(tags);

    [Fact]
    public void Empty_sets_do_not_restrict_b11b()
    {
        Assert.True(AudienceRule.Unrestricted.Matches(Tags()));
        Assert.True(AudienceRule.Unrestricted.Matches(Tags("vip")));
    }

    [Fact]
    public void Any_requires_at_least_one_match()
    {
        var rule = new AudienceRule(new HashSet<string> { "a", "b" }, new HashSet<string>(), new HashSet<string>());
        Assert.True(rule.Matches(Tags("b")));
        Assert.False(rule.Matches(Tags("c")));
    }

    [Fact]
    public void All_requires_every_tag()
    {
        var rule = new AudienceRule(new HashSet<string>(), new HashSet<string> { "x", "y" }, new HashSet<string>());
        Assert.True(rule.Matches(Tags("x", "y", "z")));
        Assert.False(rule.Matches(Tags("x")));
    }

    [Fact]
    public void Forbidding_tag_excludes_b11b()
    {
        var rule = new AudienceRule(new HashSet<string>(), new HashSet<string>(), new HashSet<string> { "blocked" });
        Assert.False(rule.Matches(Tags("vip", "blocked")));
        Assert.True(rule.Matches(Tags("vip")));
    }
}

public sealed class SafeMathTests
{
    [Fact]
    public void Overflow_is_detected_not_wrapped()
    {
        Assert.False(SafeMath.TryAdd(long.MaxValue, 1, out _));
        Assert.False(SafeMath.TrySubtract(long.MinValue, 1, out _));
        Assert.False(SafeMath.TryMultiply(long.MaxValue / 2 + 1, 2, out _));
        Assert.True(SafeMath.TryAdd(1, 2, out var sum));
        Assert.Equal(3, sum);
    }
}

public sealed class CodesTests
{
    [Theory]
    [InlineData(" STAR ", "STAR")]
    [InlineData("star", "STAR")]
    [InlineData("a-b_C9", "A-B_C9")]
    public void Code_normalizes_trims_and_keeps_case_insensitive_uniqueness(string raw, string expected)
    {
        Assert.True(Codes.TryNormalizeCode(raw, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("с кириллицей")]
    [InlineData("has space")]
    [InlineData("more-than-40-characters-aaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Invalid_codes_are_rejected(string raw)
    {
        Assert.False(Codes.TryNormalizeCode(raw, out _));
    }

    [Theory]
    [InlineData("UPPER")]
    [InlineData("too-long-tag-123456789012345678901234567890123")]
    [InlineData("under_score")]
    [InlineData("")]
    public void Invalid_tags_are_rejected(string tag)
    {
        Assert.False(Codes.IsValidTag(tag));
    }

    [Fact]
    public void External_numbers_are_exact_ascii_1_100()
    {
        Assert.True(Codes.IsValidExternalNumber("S-1"));
        Assert.True(Codes.IsValidExternalNumber(new string('x', 100)));
        Assert.False(Codes.IsValidExternalNumber(new string('x', 101)));
        Assert.False(Codes.IsValidExternalNumber("с пробелом"));
    }
}
