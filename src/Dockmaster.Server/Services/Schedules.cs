using System.Globalization;
using System.Text.RegularExpressions;
using Cronos;

namespace Dockmaster.Server.Services;

/// <summary>
/// x-dockmaster.schedule of a service: a job. It is not kept running — Dockmaster starts its container when the cron
/// expression says so, the app does its work and exits, and the container waits for the next time.
/// </summary>
/// <param name="Cron">five fields, "0 3 * * *" (minute hour day month weekday), or @daily, @hourly…</param>
/// <param name="TimeZone">IANA name the times are in ("Europe/Riga"); null = UTC</param>
/// <param name="Timeout">x-dockmaster.timeout as written ("1h", "30m"): a run still going after it is stopped; null = never</param>
public sealed record ScheduleSpec(string Cron, string? TimeZone, string? Timeout)
{
    /// <summary>The timeout as a duration; null when none or unreadable.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public TimeSpan? TimeoutSpan => Schedules.ParseDuration(Timeout);
}

/// <summary>Reading cron expressions, the next times they name and the words for them.</summary>
public static partial class Schedules
{
    public const int MinTimeoutMinutes = 1;
    public const int MaxTimeoutDays = 7;
    private static readonly string[] DayShort = ["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"];
    private static readonly string[] DayNames = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

    public static bool TryParse(string? cron, out CronExpression expression)
    {
        expression = null!;
        if (string.IsNullOrWhiteSpace(cron)) return false;
        try { expression = CronExpression.Parse(cron.Trim(), CronFormat.Standard); return true; }
        catch (CronFormatException) { return false; }
    }

    /// <summary>What is wrong with a schedule as a form sends it; null when nothing.</summary>
    public static string? Validate(string? cron, string? timeZone, string? timeout)
    {
        if (!TryParse(cron, out _)) return "the schedule is a cron expression of five fields, e.g. 0 3 * * * (minute hour day month weekday)";
        // a name this server cannot resolve is an error only where it resolves names at all (tzdata installed):
        // without it the schedule still works, in UTC, and the pages say so
        if (!string.IsNullOrWhiteSpace(timeZone) && HasZoneData)
        {
            Zone(timeZone, out var problem);
            if (problem != null) return problem.Replace(" — UTC is used", "");
        }
        if (!string.IsNullOrWhiteSpace(timeout))
        {
            if (ParseDuration(timeout) is not { } t) return "the timeout is a duration like 30m, 2h or 1h30m";
            if (t < TimeSpan.FromMinutes(MinTimeoutMinutes) || t > TimeSpan.FromDays(MaxTimeoutDays)) return $"the timeout is between {MinTimeoutMinutes} minute and {MaxTimeoutDays} days";
        }
        return null;
    }

    /// <summary>Whether this server resolves IANA names at all (Linux with tzdata; not Windows in invariant mode).</summary>
    public static readonly bool HasZoneData = Resolves("Europe/London");

    private static bool Resolves(string id)
    {
        try { TimeZoneInfo.FindSystemTimeZoneById(id); return true; }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return false; }
    }

    /// <summary>
    /// The time zone by its IANA name; UTC with a <paramref name="problem"/> when this server does not know it (a
    /// machine without tzdata, or a name that does not exist).
    /// </summary>
    public static TimeZoneInfo Zone(string? id, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(id) || id.Trim().Equals("UTC", StringComparison.OrdinalIgnoreCase)) return TimeZoneInfo.Utc;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id.Trim()); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            problem = $"time zone '{id.Trim()}' is not known to this server — UTC is used";
            return TimeZoneInfo.Utc;
        }
    }

    /// <summary>The first time after <paramref name="afterUtc"/> the schedule names, or null (an expression that never matches).</summary>
    public static DateTime? Next(ScheduleSpec spec, DateTime afterUtc) =>
        TryParse(spec.Cron, out var e) ? e.GetNextOccurrence(DateTime.SpecifyKind(afterUtc, DateTimeKind.Utc), Zone(spec.TimeZone, out _)) : null;

    /// <summary>The next <paramref name="count"/> times after <paramref name="afterUtc"/>.</summary>
    public static List<DateTime> Next(string cron, string? timeZone, DateTime afterUtc, int count)
    {
        var list = new List<DateTime>();
        if (!TryParse(cron, out var e)) return list;
        var zone = Zone(timeZone, out _);
        var at = DateTime.SpecifyKind(afterUtc, DateTimeKind.Utc);
        for (var i = 0; i < count; i++)
        {
            if (e.GetNextOccurrence(at, zone) is not { } next) break;
            list.Add(next);
            at = next;
        }
        return list;
    }

    /// <summary>The times a schedule named in (<paramref name="fromUtc"/>, <paramref name="toUtc"/>], oldest first.</summary>
    public static List<DateTime> Between(ScheduleSpec spec, DateTime fromUtc, DateTime toUtc)
    {
        var list = new List<DateTime>();
        if (!TryParse(spec.Cron, out var e) || toUtc <= fromUtc) return list;
        var zone = Zone(spec.TimeZone, out _);
        list.AddRange(e.GetOccurrences(DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc), DateTime.SpecifyKind(toUtc, DateTimeKind.Utc), zone, fromInclusive: false, toInclusive: true));
        return list;
    }

    /// <summary>
    /// A cron expression in words, for the common shapes: "every 15 minutes", "every day at 03:00", "Mon–Fri at 08:30",
    /// "every Monday and Thursday at 03:00", "on day 1 of the month at 03:00"; the expression itself otherwise.
    /// </summary>
    public static string Describe(string cron)
    {
        cron = cron.Trim();
        switch (cron.ToLowerInvariant())
        {
            case "@hourly": return "every hour";
            case "@daily" or "@midnight": return "every day at 00:00";
            case "@weekly": return "every Sunday at 00:00";
            case "@monthly": return "on day 1 of the month at 00:00";
            case "@yearly" or "@annually": return "on 1 January at 00:00";
            case "@every_minute": return "every minute";
        }
        var f = Regex.Split(cron, @"\s+");
        if (f.Length != 5) return cron;
        var (min, hour, day, month, dow) = (f[0], f[1], f[2], f[3], f[4].ToUpperInvariant());
        if (month != "*") return cron;
        if (day == "*" && dow == "*")
        {
            if (min == "*" && hour == "*") return "every minute";
            if (EveryN(min) is { } n && hour == "*") return n == 1 ? "every minute" : $"every {n} minutes";
            if (Number(min) is { } m1 && hour == "*") return m1 == 0 ? "every hour" : $"every hour at :{m1:00}";
            if (Number(min) is { } m0 && EveryN(hour) is { } h) return h == 1 ? (m0 == 0 ? "every hour" : $"every hour at :{m0:00}") : $"every {h} hours" + (m0 == 0 ? "" : $" at :{m0:00}");
            if (Time(min, hour) is { } t) return $"every day at {t}";
            if (Times(min, hour) is { } ts) return $"every day at {ts}";
            return cron;
        }
        if (day == "*" && Time(min, hour) is { } at)
        {
            if (dow is "1-5" or "MON-FRI") return $"Mon–Fri at {at}";
            if (dow is "0,6" or "6,0" or "SAT,SUN" or "SUN,SAT" or "6,7") return $"Sat and Sun at {at}";
            var days = dow.Split(',').Select(d => d.Trim())
                .Select(d => int.TryParse(d, out var i) && i is >= 0 and <= 7 ? DayNames[i % 7] : Array.IndexOf(DayShort, d) is var k and >= 0 ? DayNames[k] : null).ToList();
            if (days.Count > 0 && days.All(d => d != null)) return $"every {Join(days!)} at {at}";
            return cron;
        }
        if (dow == "*" && Number(day) is { } d0 && Time(min, hour) is { } t2) return $"on day {d0} of the month at {t2}";
        return cron;
    }

    private static string Join(IReadOnlyList<string> items) => items.Count switch
    {
        1 => items[0],
        2 => $"{items[0]} and {items[1]}",
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };

    private static int? Number(string field) => int.TryParse(field, out var n) ? n : null;
    /// <summary>"0 9,19" → "09:00 and 19:00"; null for anything but one minute and a list of hours.</summary>
    private static string? Times(string min, string hour)
    {
        var hours = hour.Split(',');
        if (hours.Length < 2) return null;
        var times = hours.Select(h => Time(min, h)).ToList();
        return times.All(t => t != null) ? Join(times!) : null;
    }
    private static int? EveryN(string field) => field.StartsWith("*/") && int.TryParse(field[2..], out var n) && n > 0 ? n : null;
    private static string? Time(string min, string hour) => Number(min) is { } m && Number(hour) is { } h && m is >= 0 and < 60 && h is >= 0 and < 24 ? $"{h:00}:{m:00}" : null;

    /// <summary>"30m", "2h", "1h30m", "90s", "1d", or a bare number of minutes; null when unreadable or zero.</summary>
    public static TimeSpan? ParseDuration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim().ToLowerInvariant();
        if (int.TryParse(text, out var minutes)) return minutes > 0 ? TimeSpan.FromMinutes(minutes) : null;
        var m = DurationRegex().Match(text);
        if (!m.Success) return null;
        var total = TimeSpan.Zero;
        foreach (Capture c in m.Groups["part"].Captures)
        {
            var n = double.Parse(c.Value[..^1], CultureInfo.InvariantCulture);
            total += c.Value[^1] switch { 'd' => TimeSpan.FromDays(n), 'h' => TimeSpan.FromHours(n), 'm' => TimeSpan.FromMinutes(n), _ => TimeSpan.FromSeconds(n) };
        }
        return total > TimeSpan.Zero ? total : null;
    }

    /// <summary>A duration the way it is written in a compose file: "1h30m", "45m", "2d".</summary>
    public static string Duration(TimeSpan t)
    {
        var parts = new List<string>();
        if (t.Days > 0) parts.Add($"{t.Days}d");
        if (t.Hours > 0) parts.Add($"{t.Hours}h");
        if (t.Minutes > 0) parts.Add($"{t.Minutes}m");
        if (t.Seconds > 0) parts.Add($"{t.Seconds}s");
        return parts.Count == 0 ? "0s" : string.Concat(parts);
    }

    [GeneratedRegex(@"^(?<part>\d+(?:\.\d+)?[dhms])+$")] private static partial Regex DurationRegex();
}
