using TodoExit.Core.Model;

namespace TodoExit.Core.Tests;

public class RecurrenceTextTests
{
    private static Recurrence R(string type, int interval = 1, DayOfWeek[]? days = null, int dayOfMonth = 0,
        int month = 0, string? index = null, string range = "noEnd", DateOnly? end = null, int count = 0) => new()
    {
        PatternType = type,
        Interval = interval,
        DaysOfWeek = days ?? [],
        DayOfMonth = dayOfMonth,
        Month = month,
        Index = index,
        RangeType = range,
        RangeEndDate = end,
        NumberOfOccurrences = count,
    };

    [Theory]
    [InlineData(1, "every day")]
    [InlineData(3, "every 3 days")]
    public void Daily(int interval, string expected)
    {
        Assert.Equal((expected, null), RecurrenceText.Describe(R("daily", interval)));
    }

    [Fact]
    public void Weekly_lists_days_in_monday_first_order()
    {
        var result = RecurrenceText.Describe(R("weekly", days: [DayOfWeek.Wednesday, DayOfWeek.Monday]));
        Assert.Equal(("every mon, wed", (string?)null), result);
    }

    [Fact]
    public void Weekly_sunday_comes_last()
    {
        var result = RecurrenceText.Describe(R("weekly", days: [DayOfWeek.Sunday, DayOfWeek.Saturday, DayOfWeek.Tuesday]));
        Assert.Equal("every tue, sat, sun", result.Text);
    }

    [Fact]
    public void Weekly_all_weekdays_is_every_weekday()
    {
        var days = new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday };
        Assert.Equal(("every weekday", (string?)null), RecurrenceText.Describe(R("weekly", days: days)));
    }

    [Fact]
    public void Weekly_all_seven_days_is_every_day()
    {
        Assert.Equal(("every day", (string?)null), RecurrenceText.Describe(R("weekly", days: Enum.GetValues<DayOfWeek>())));
    }

    [Fact]
    public void Weekly_interval_with_one_day_has_no_caveat()
    {
        Assert.Equal(("every 2 weeks", (string?)null), RecurrenceText.Describe(R("weekly", 2, [DayOfWeek.Friday])));
    }

    [Fact]
    public void Weekly_interval_with_several_days_names_lost_days()
    {
        var (text, caveat) = RecurrenceText.Describe(R("weekly", 2, [DayOfWeek.Thursday, DayOfWeek.Tuesday]));
        Assert.Equal("every 2 weeks", text);
        Assert.NotNull(caveat);
        Assert.Contains("tue, thu", caveat);
    }

    [Theory]
    [InlineData(1, "every 1st")]
    [InlineData(2, "every 2nd")]
    [InlineData(3, "every 3rd")]
    [InlineData(4, "every 4th")]
    [InlineData(11, "every 11th")]
    [InlineData(12, "every 12th")]
    [InlineData(13, "every 13th")]
    [InlineData(15, "every 15th")]
    [InlineData(21, "every 21st")]
    [InlineData(22, "every 22nd")]
    [InlineData(23, "every 23rd")]
    [InlineData(31, "every 31st")]
    public void Absolute_monthly_uses_ordinal(int day, string expected)
    {
        Assert.Equal((expected, null), RecurrenceText.Describe(R("absoluteMonthly", dayOfMonth: day)));
    }

    [Fact]
    public void Absolute_monthly_interval_caveat_when_day_differs_from_due_date()
    {
        var (text, caveat) = RecurrenceText.Describe(R("absoluteMonthly", 3, dayOfMonth: 15), new DateOnly(2026, 11, 20));
        Assert.Equal("every 3 months", text);
        Assert.NotNull(caveat);
    }

    [Fact]
    public void Absolute_monthly_interval_no_caveat_when_day_matches_or_unknown()
    {
        Assert.Equal(("every 3 months", (string?)null),
            RecurrenceText.Describe(R("absoluteMonthly", 3, dayOfMonth: 15), new DateOnly(2026, 11, 15)));
        Assert.Equal(("every 3 months", (string?)null),
            RecurrenceText.Describe(R("absoluteMonthly", 3, dayOfMonth: 15)));
    }

    [Theory]
    [InlineData("first", DayOfWeek.Monday, "every 1st mon")]
    [InlineData("second", DayOfWeek.Tuesday, "every 2nd tue")]
    [InlineData("third", DayOfWeek.Wednesday, "every 3rd wed")]
    [InlineData("fourth", DayOfWeek.Thursday, "every 4th thu")]
    [InlineData("last", DayOfWeek.Friday, "every last fri")]
    public void Relative_monthly(string index, DayOfWeek day, string expected)
    {
        Assert.Equal((expected, null), RecurrenceText.Describe(R("relativeMonthly", days: [day], index: index)));
    }

    [Fact]
    public void Relative_monthly_interval_adds_caveat()
    {
        var (text, caveat) = RecurrenceText.Describe(R("relativeMonthly", 2, [DayOfWeek.Tuesday], index: "second"));
        Assert.Equal("every 2nd tue", text);
        Assert.NotNull(caveat);
    }

    [Fact]
    public void Absolute_yearly()
    {
        Assert.Equal(("every 8 mar", (string?)null), RecurrenceText.Describe(R("absoluteYearly", dayOfMonth: 8, month: 3)));
        var (text, caveat) = RecurrenceText.Describe(R("absoluteYearly", 2, dayOfMonth: 8, month: 3));
        Assert.Equal("every 8 mar", text);
        Assert.NotNull(caveat);
    }

    [Fact]
    public void Relative_yearly_is_every_year_with_caveat_describing_rule()
    {
        var (text, caveat) = RecurrenceText.Describe(R("relativeYearly", days: [DayOfWeek.Sunday], month: 10, index: "last"));
        Assert.Equal("every year", text);
        Assert.NotNull(caveat);
        Assert.Contains("last sun", caveat);
        Assert.Contains("October", caveat);
    }

    [Fact]
    public void End_date_range_is_appended()
    {
        var result = RecurrenceText.Describe(R("daily", range: "endDate", end: new DateOnly(2027, 6, 30)));
        Assert.Equal(("every day ending 2027-06-30", (string?)null), result);
    }

    [Fact]
    public void Starting_date_goes_before_ending()
    {
        var result = RecurrenceText.Describe(
            R("daily", range: "endDate", end: new DateOnly(2027, 6, 30)),
            startingFrom: new DateOnly(2026, 11, 25));
        Assert.Equal(("every day starting 2026-11-25 ending 2027-06-30", (string?)null), result);
    }

    [Fact]
    public void Starting_date_without_end()
    {
        var result = RecurrenceText.Describe(R("weekly", days: [DayOfWeek.Monday]), startingFrom: new DateOnly(2026, 11, 23));
        Assert.Equal("every mon starting 2026-11-23", result.Text);
    }

    [Fact]
    public void Numbered_range_adds_caveat()
    {
        var (text, caveat) = RecurrenceText.Describe(R("daily", range: "numbered", count: 10));
        Assert.Equal("every day", text);
        Assert.NotNull(caveat);
        Assert.Contains("10", caveat);
    }

    [Fact]
    public void Unknown_pattern_returns_empty_text_with_caveat()
    {
        var (text, caveat) = RecurrenceText.Describe(R("hourly"));
        Assert.Equal(string.Empty, text);
        Assert.NotNull(caveat);
    }
}
