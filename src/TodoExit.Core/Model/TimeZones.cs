namespace TodoExit.Core.Model;

/// <summary>Resolves the time zone names Graph uses (usually "UTC"; sometimes Windows or IANA ids).</summary>
public static class TimeZones
{
    /// <summary>Returns the zone for an id, or UTC when the id is empty or unknown.</summary>
    public static TimeZoneInfo ResolveOrUtc(string? id)
    {
        return TryResolve(id, out var zone) ? zone : TimeZoneInfo.Utc;
    }

    public static bool TryResolve(string? id, out TimeZoneInfo zone)
    {
        zone = TimeZoneInfo.Utc;
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        var name = id.Trim();
        const string prefix = "tzone://Microsoft/";
        if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            name = name[prefix.Length..];
        }

        if (name.Equals("UTC", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Utc", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Etc/UTC", StringComparison.OrdinalIgnoreCase)
            || name.Equals("GMT", StringComparison.OrdinalIgnoreCase))
        {
            zone = TimeZoneInfo.Utc;
            return true;
        }

        // FindSystemTimeZoneById accepts both IANA and Windows ids on .NET 6+ when ICU is available.
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(name);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
        }
        catch (InvalidTimeZoneException)
        {
        }

        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(name, out var iana))
        {
            try
            {
                zone = TimeZoneInfo.FindSystemTimeZoneById(iana);
                return true;
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return false;
    }

    /// <summary>Interprets a wall-clock time in <paramref name="source"/> and converts it to <paramref name="target"/>.</summary>
    public static DateTime Convert(DateTime wallClock, TimeZoneInfo source, TimeZoneInfo target)
    {
        var unspecified = DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified);
        DateTime utc;
        if (source == TimeZoneInfo.Utc)
        {
            utc = DateTime.SpecifyKind(unspecified, DateTimeKind.Utc);
        }
        else
        {
            if (source.IsInvalidTime(unspecified))
            {
                // Inside a spring-forward gap: move forward by the gap length.
                unspecified = unspecified.AddHours(1);
            }

            utc = TimeZoneInfo.ConvertTimeToUtc(unspecified, source);
        }

        var converted = TimeZoneInfo.ConvertTimeFromUtc(utc, target);
        return DateTime.SpecifyKind(converted, DateTimeKind.Unspecified);
    }
}
