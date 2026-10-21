using Dockmaster.Server.Services;

namespace Dockmaster.Tests;

public class ComposeEnvTests
{
    private const string Yaml = """
        x-common: &common
          SHARED_TOKEN: &tok "abc 123"   # shared
        services:
          db:
            image: postgres
            environment:
              POSTGRES_PASSWORD: hunter2
              EMPTY:
              QUOTED: 'it''s'
              <<: *common
          web:
            image: nginx
            environment:
              - API_KEY=s3cret
              - "OTHER=x y"
              - PASSTHROUGH
            labels:
              keep: me
          job:
            image: alpine
            environment:
              TOKEN: *tok
              CERT: |
                line one
                line two
              TZ: UTC
        """;

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Values_are_hidden_and_everything_else_stays(string newline)
    {
        var yaml = Yaml.ReplaceLineEndings(newline);
        var redacted = ComposeEnv.Redact(yaml);
        foreach (var secret in new[] { "hunter2", "it''s", "abc 123", "s3cret", "x y", "line one", "UTC" })
            Assert.DoesNotContain(secret, redacted);
        foreach (var kept in new[] { "image: postgres", "EMPTY:", "- PASSTHROUGH", "keep: me", "# shared", "&tok", "*tok", "<<: *common", "API_KEY=(hidden)" })
            Assert.Contains(kept, redacted);
        var job = ComposeServices.Parse(redacted).Single(s => s.Name == "job");
        Assert.All(job.Environment!, e => Assert.Equal(ComposeEnv.Hidden, e.Value));
    }

    [Fact]
    public void A_redacted_file_saved_back_is_the_original()
    {
        var redacted = ComposeEnv.Redact(Yaml);
        Assert.Equal(Yaml, ComposeEnv.Restore(redacted, Yaml).Yaml);
    }

    [Fact]
    public void Edits_around_hidden_values_keep_the_stored_ones()
    {
        var edited = ComposeEnv.Redact(Yaml).Replace("image: nginx", "image: nginx:1.27").Replace("TZ: (hidden)", "TZ: Europe/Berlin");
        var (yaml, error) = ComposeEnv.Restore(edited, Yaml);
        Assert.Null(error);
        Assert.Equal(Yaml.Replace("image: nginx", "image: nginx:1.27").Replace("TZ: UTC", "TZ: Europe/Berlin"), yaml);
    }

    [Fact]
    public void A_hidden_value_written_the_other_way_is_quoted()
    {
        var edited = ComposeEnv.Redact(Yaml).Replace("- API_KEY=(hidden)", "API_KEY: (hidden)").Replace("environment:\n      API_KEY", "environment:\n      API_KEY");
        // web's list turned into a mapping: still the stored value, quoted
        var mapped = edited.Replace("""
                  - API_KEY=(hidden)
                  - "OTHER=(hidden)"
                  - PASSTHROUGH
            """, "      API_KEY: (hidden)\n");
        var (yaml, error) = ComposeEnv.Restore(mapped, Yaml);
        Assert.Null(error);
        Assert.Equal("s3cret", ComposeServices.Parse(yaml!).Single(s => s.Name == "web").Environment!.Single(e => e.Name == "API_KEY").Value);
    }

    [Fact]
    public void A_hidden_value_under_a_new_name_is_refused()
    {
        var edited = ComposeEnv.Redact(Yaml).Replace("POSTGRES_PASSWORD: (hidden)", "PG_PASSWORD: (hidden)");
        Assert.Contains("PG_PASSWORD", ComposeEnv.Restore(edited, Yaml).Error);
    }

    [Fact]
    public void A_file_that_does_not_parse_is_not_shown()
    {
        Assert.Equal(ComposeEnv.Unreadable, ComposeEnv.Redact("services:\n  db: [\n    PASSWORD: x"));
        Assert.Equal("services: {}", ComposeEnv.Restore(ComposeEnv.Unreadable, "services: {}").Yaml);
    }
}
