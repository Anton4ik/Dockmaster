using Dockmaster.Server.Services;

namespace Dockmaster.Tests;

public class ScheduleTests
{
    private static ServiceSummary Svc(string yaml, string name) => ComposeServices.Parse(yaml).Single(s => s.Name == name);

    [Fact]
    public void Cron_expressions_in_words()
    {
        Assert.Equal("every 15 minutes", Schedules.Describe("*/15 * * * *"));
        Assert.Equal("every hour", Schedules.Describe("0 * * * *"));
        Assert.Equal("every 6 hours", Schedules.Describe("0 */6 * * *"));
        Assert.Equal("every day at 03:00", Schedules.Describe("0 3 * * *"));
        Assert.Equal("every day at 09:00 and 19:00", Schedules.Describe("0 9,19 * * *"));
        Assert.Equal("Mon–Fri at 08:30", Schedules.Describe("30 8 * * 1-5"));
        Assert.Equal("every Monday and Thursday at 03:00", Schedules.Describe("0 3 * * 1,4"));
        Assert.Equal("every Sunday at 04:00", Schedules.Describe("0 4 * * SUN"));
        Assert.Equal("on day 1 of the month at 02:00", Schedules.Describe("0 2 1 * *"));
        Assert.Equal("every day at 00:00", Schedules.Describe("@daily"));
        // shapes with no short words keep the expression
        Assert.Equal("5 4 * 3 *", Schedules.Describe("5 4 * 3 *"));
    }

    [Fact]
    public void Next_times_follow_the_time_zone()
    {
        var utc = new DateTime(2026, 9, 30, 10, 0, 0, DateTimeKind.Utc);
        var next = Schedules.Next("0 3 * * *", null, utc, 2);
        Assert.Equal([new DateTime(2026, 10, 1, 3, 0, 0), new DateTime(2026, 10, 2, 3, 0, 0)], next);
        // a fixed offset zone is known everywhere: 03:00 at +03:00 is 00:00 UTC
        var plus3 = TimeZoneInfo.CreateCustomTimeZone("plus3", TimeSpan.FromHours(3), "plus3", "plus3");
        Assert.Equal(new DateTime(2026, 10, 1, 0, 0, 0), Cronos.CronExpression.Parse("0 3 * * *").GetNextOccurrence(utc, plus3));
        Assert.Empty(Schedules.Between(new ScheduleSpec("0 3 * * *", null, null), utc, utc.AddHours(1)));
        Assert.Single(Schedules.Between(new ScheduleSpec("0 3 * * *", null, null), utc, utc.AddDays(1)));
        // an unknown zone falls back to UTC and says so
        Schedules.Zone("Mars/Olympus", out var problem);
        Assert.Contains("not known", problem);
    }

    [Fact]
    public void Durations_and_validation()
    {
        Assert.Equal(TimeSpan.FromMinutes(90), Schedules.ParseDuration("1h30m"));
        Assert.Equal(TimeSpan.FromMinutes(45), Schedules.ParseDuration("45"));
        Assert.Equal(TimeSpan.FromDays(2), Schedules.ParseDuration("2d"));
        Assert.Null(Schedules.ParseDuration("soon"));
        Assert.Equal("1h30m", Schedules.Duration(TimeSpan.FromMinutes(90)));
        Assert.Null(Schedules.Validate("0 3 * * *", null, "2h"));
        Assert.Null(Schedules.Validate("@hourly", "UTC", null));
        Assert.Contains("cron", Schedules.Validate("every day", null, null));
        Assert.Contains("duration", Schedules.Validate("0 3 * * *", null, "later"));
        Assert.Contains("between", Schedules.Validate("0 3 * * *", null, "30d"));
        // a made-up zone is refused where the server resolves names at all; without tzdata it is a warning, UTC is used
        if (Schedules.HasZoneData) Assert.Contains("not known", Schedules.Validate("0 3 * * *", "Mars/Olympus", null));
        else Assert.Null(Schedules.Validate("0 3 * * *", "Mars/Olympus", null));
    }

    [Fact]
    public void Schedule_is_read_from_the_compose_file()
    {
        var yaml = """
            services:
              backup:
                image: backup:1
                restart: "no"
                x-dockmaster:
                  schedule: "0 3 * * *"
                  timezone: Europe/Riga
                  timeout: 1h
              web:
                image: nginx
                x-dockmaster:
                  schedule: whenever
            """;
        var backup = Svc(yaml, "backup");
        Assert.True(backup.IsJob);
        Assert.Equal(new ScheduleSpec("0 3 * * *", "Europe/Riga", "1h"), backup.Schedule);
        Assert.Equal(TimeSpan.FromHours(1), backup.Schedule!.TimeoutSpan);
        // a schedule that is no cron expression is no job
        Assert.False(Svc(yaml, "web").IsJob);
    }

    [Fact]
    public void Writing_a_schedule_turns_docker_restarts_off()
    {
        var yaml = """
            services:
              backup:
                image: backup:1   # nightly
                restart: unless-stopped
              db:
                image: postgres
            """;
        var result = ComposeEditor.SetSchedule(yaml, "backup", new ScheduleSpec("0 3 * * *", "Europe/Riga", "1h"));
        var s = Svc(result, "backup");
        Assert.Equal(new ScheduleSpec("0 3 * * *", "Europe/Riga", "1h"), s.Schedule);
        Assert.Equal("no", s.Restart);
        Assert.Contains("image: backup:1   # nightly", result);
        Assert.Contains("schedule: \"0 3 * * *\"", result);
        Assert.Contains("  db:\n    image: postgres", result.ReplaceLineEndings("\n"));
        Assert.Null(ComposeEditor.Broken(yaml, result));

        // on-failure stays: retries of a failed run
        var retrying = ComposeEditor.SetSchedule(yaml.Replace("unless-stopped", "on-failure:3"), "backup", new ScheduleSpec("@hourly", null, null));
        Assert.Equal("on-failure:3", Svc(retrying, "backup").Restart);
        Assert.Null(Svc(retrying, "backup").Schedule!.TimeZone);

        // changed: the old timezone and timeout go when the new schedule has none
        var changed = ComposeEditor.SetSchedule(result, "backup", new ScheduleSpec("*/30 * * * *", null, null));
        Assert.Equal(new ScheduleSpec("*/30 * * * *", null, null), Svc(changed, "backup").Schedule);
        Assert.DoesNotContain("timezone", changed);
        Assert.DoesNotContain("timeout", changed);

        // off: the keys go, the emptied x-dockmaster block too, restart stays as it is
        var off = ComposeEditor.SetSchedule(result, "backup", null);
        Assert.Null(Svc(off, "backup").Schedule);
        Assert.DoesNotContain("x-dockmaster", off);
        Assert.Equal("no", Svc(off, "backup").Restart);
        Assert.Contains("restart: \"no\"", off);
    }

    [Fact]
    public void A_job_with_several_replicas_is_refused()
    {
        var yaml = """
            services:
              worker:
                image: worker
                deploy:
                  replicas: 3
            """;
        var ex = Assert.Throws<InvalidOperationException>(() => ComposeEditor.SetSchedule(yaml, "worker", new ScheduleSpec("@hourly", null, null)));
        Assert.Contains("replicas", ex.Message);
        Assert.False(ComposeEditor.TryEdit(yaml, y => ComposeEditor.SetSchedule(y, "worker", new ScheduleSpec("@hourly", null, null)), out _, out var refused));
        Assert.Contains("one container at a time", refused);
    }
}
