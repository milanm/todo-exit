using System.Globalization;
using TodoExit.Core.Model;

namespace TodoExit.Core;

/// <summary>
/// Turns a To Do recurrence into a phrase in Todoist's English natural-language date syntax.
/// A non-null caveat means the phrase only approximates the original rule; it goes into the loss report.
/// </summary>
/// <remarks>
/// These phrases are based on Todoist's date syntax. They are untested against a real Todoist account
/// or import. Keep every phrase in this class so they can be checked and fixed in one place.
/// </remarks>
public static class RecurrenceText
{
    private static readonly DayOfWeek[] WeekOrder =
    [
        DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
        DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday,
    ];

    private static readonly string[] MonthNames =
        ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

    /// <param name="recurrence">The recurrence to describe.</param>
    /// <param name="dueDate">The task's due date, used to detect mismatches. Optional.</param>
    /// <param name="startingFrom">
    /// When set, " starting yyyy-MM-dd" is placed after the rule and before any " ending" part,
    /// e.g. "every day starting 2026-11-25 ending 2027-06-30". The Todoist exporter passes the due date.
    /// </param>
    public static (string Text, string? Caveat) Describe(Recurrence recurrence, DateOnly? dueDate = null, DateOnly? startingFrom = null)
    {
        ArgumentNullException.ThrowIfNull(recurrence);
        var (text, caveat) = DescribePattern(recurrence, dueDate);

        var caveats = new List<string>();
        if (caveat is not null)
        {
            caveats.Add(caveat);
        }

        if (startingFrom is { } start && text.Length > 0)
        {
            text += " starting " + start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        switch (recurrence.RangeType.ToLowerInvariant())
        {
            case "enddate" when recurrence.RangeEndDate is { } end && text.Length > 0:
                text += " ending " + end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                break;
            case "numbered":
                caveats.Add(recurrence.NumberOfOccurrences > 0
                    ? $"the series ends after {recurrence.NumberOfOccurrences} occurrences in To Do; the occurrence count is not carried"
                    : "the series ends after a fixed number of occurrences in To Do; the occurrence count is not carried");
                break;
        }

        return (text, caveats.Count == 0 ? null : string.Join("; ", caveats));
    }

    private static (string Text, string? Caveat) DescribePattern(Recurrence r, DateOnly? due)
    {
        var n = r.Interval < 1 ? 1 : r.Interval;
        switch (r.PatternType.ToLowerInvariant())
        {
            case "daily":
                return (n == 1 ? "every day" : $"every {n} days", null);

            case "weekly":
                return Weekly(r, n);

            case "absolutemonthly":
            {
                var day = r.DayOfMonth > 0 ? r.DayOfMonth : due?.Day ?? 1;
                if (n == 1)
                {
                    return ($"every {Ordinal(day)}", null);
                }

                string? caveat = null;
                if (r.DayOfMonth > 0 && due is { } d && d.Day != r.DayOfMonth)
                {
                    caveat = $"To Do repeats on the {Ordinal(r.DayOfMonth)} every {n} months; Todoist will repeat on the due date's day ({Ordinal(d.Day)})";
                }

                return ($"every {n} months", caveat);
            }

            case "relativemonthly":
            {
                var (phrase, dayCaveat) = RelativeDay(r);
                var text = "every " + phrase;
                var caveats = new List<string>();
                if (dayCaveat is not null)
                {
                    caveats.Add(dayCaveat);
                }

                if (n > 1)
                {
                    caveats.Add($"To Do repeats on the {phrase} every {n} months; Todoist will repeat it every month");
                }

                return (text, caveats.Count == 0 ? null : string.Join("; ", caveats));
            }

            case "absoluteyearly":
            {
                var month = r.Month is >= 1 and <= 12 ? r.Month : due?.Month ?? 1;
                var day = r.DayOfMonth > 0 ? r.DayOfMonth : due?.Day ?? 1;
                var text = $"every {day} {MonthNames[month - 1]}";
                return n == 1
                    ? (text, null)
                    : (text, $"To Do repeats every {n} years; Todoist will repeat it every year");
            }

            case "relativeyearly":
            {
                var (phrase, _) = RelativeDay(r);
                var monthName = r.Month is >= 1 and <= 12
                    ? CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(r.Month)
                    : "the same month";
                var every = n == 1 ? "every year" : $"every {n} years";
                return ("every year", $"To Do repeats on the {phrase} of {monthName} {every}; Todoist will repeat it every year on the due date");
            }

            default:
                return (string.Empty, $"unknown recurrence pattern '{r.PatternType}' was not carried");
        }
    }

    private static (string Text, string? Caveat) Weekly(Recurrence r, int n)
    {
        var days = WeekOrder.Where(d => r.DaysOfWeek.Contains(d)).ToList();
        if (n == 1)
        {
            if (days.Count == 0)
            {
                return ("every week", null);
            }

            if (days.Count == 7)
            {
                return ("every day", null);
            }

            if (days.Count == 5 && !days.Contains(DayOfWeek.Saturday) && !days.Contains(DayOfWeek.Sunday))
            {
                return ("every weekday", null);
            }

            return ("every " + string.Join(", ", days.Select(Short)), null);
        }

        if (days.Count <= 1)
        {
            return ($"every {n} weeks", null);
        }

        return ($"every {n} weeks",
            $"To Do repeats every {n} weeks on {string.Join(", ", days.Select(Short))}; Todoist will repeat on one day only");
    }

    private static (string Phrase, string? Caveat) RelativeDay(Recurrence r)
    {
        var index = (r.Index ?? "first").ToLowerInvariant() switch
        {
            "first" => "1st",
            "second" => "2nd",
            "third" => "3rd",
            "fourth" => "4th",
            "last" => "last",
            _ => "1st",
        };

        var days = WeekOrder.Where(d => r.DaysOfWeek.Contains(d)).ToList();
        var day = days.Count > 0 ? Short(days[0]) : "mon";
        string? caveat = days.Count > 1
            ? $"To Do lists several days ({string.Join(", ", days.Select(Short))}); only {day} is carried"
            : null;
        return ($"{index} {day}", caveat);
    }

    public static string Short(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "mon",
        DayOfWeek.Tuesday => "tue",
        DayOfWeek.Wednesday => "wed",
        DayOfWeek.Thursday => "thu",
        DayOfWeek.Friday => "fri",
        DayOfWeek.Saturday => "sat",
        _ => "sun",
    };

    public static string Ordinal(int n)
    {
        var suffix = (n % 100) is 11 or 12 or 13
            ? "th"
            : (n % 10) switch
            {
                1 => "st",
                2 => "nd",
                3 => "rd",
                _ => "th",
            };
        return n.ToString(CultureInfo.InvariantCulture) + suffix;
    }
}
