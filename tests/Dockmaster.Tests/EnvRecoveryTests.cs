using System.Text.Json.Nodes;
using Dockmaster.Agent.Docker;

namespace Dockmaster.Tests;

public class EnvRecoveryTests
{
    [Theory]
    [InlineData("${DB_PASS}", "s3cret", "DB_PASS=s3cret")]
    [InlineData("Host=${DB_HOST};Port=5432;Password=${DB_PASS:-x}", "Host=pg;Port=5432;Password=a;b", "DB_HOST=pg|DB_PASS=a;b")]
    [InlineData("${REGISTRY}/app:${TAG}", "reg.example.com:5000/app:1.2.3", "REGISTRY=reg.example.com:5000|TAG=1.2.3")]
    [InlineData("price $$5 for ${USER}", "price $5 for bob", "USER=bob")]
    [InlineData("$DB_PASSWORD", "s3cret", "DB_PASSWORD=s3cret")]
    [InlineData("postgres://$USER:${PASS}@db", "postgres://app:pw@db", "PASS=pw|USER=app")]
    public void Matches_templates(string template, string value, string expected)
    {
        var got = EnvRecovery.Match(template, value)!;
        Assert.Equal(expected, string.Join("|", got.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}")));
    }

    [Fact]
    public void Rejects_values_that_do_not_fit_the_template()
    {
        Assert.Null(EnvRecovery.Match("Host=${H};Port=1", "something else"));
        Assert.Null(EnvRecovery.Match("${A}-${A}", "x-y"));
    }

    [Fact]
    public void Recovers_from_services_and_reports_the_rest()
    {
        var config = JsonNode.Parse("""
            {"services":{
              "api":{"image":"${REGISTRY}/api:${TAG}","environment":{"ConnectionStrings__Db":"Host=${PG_HOST};Password=${PG_PASS}","MODE":"prod"},
                     "ports":[{"published":"${API_PORT}","target":8080}]},
              "db":{"image":"postgres:17","environment":{"POSTGRES_PASSWORD":"${PG_PASS}"}}
            }}
            """)!.AsObject();
        var actual = new Dictionary<string, EnvRecovery.Actual>
        {
            ["api"] = new(new() { ["ConnectionStrings__Db"] = "Host=postgres;Password=pw", ["MODE"] = "prod" }, "reg:5000/api:7", [], [], []),
            ["db"] = new(new() { ["POSTGRES_PASSWORD"] = "pw" }, "postgres:17", [], [], []),
        };
        var r = EnvRecovery.Recover(config, actual, new HashSet<string> { "TAG" });
        Assert.Equal("PG_HOST=postgres|PG_PASS=pw|REGISTRY=reg:5000", string.Join("|", r.Values.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}")));
        Assert.Equal(["API_PORT"], r.Unresolved);
    }

    [Fact]
    public void Recovers_from_commands_and_ignores_escaped_dollars()
    {
        var config = JsonNode.Parse("""
            {"services":{"redis":{"image":"redis:7","command":["redis-server","--requirepass","${REDIS_PASSWORD}"],
              "healthcheck":{"test":["CMD-SHELL","redis-cli -a ${REDIS_PASSWORD} ping | awk '{print $${1}}'"]}}}}
            """)!.AsObject();
        var actual = new Dictionary<string, EnvRecovery.Actual>
        {
            ["redis"] = new([], "redis:7", ["redis-server", "--requirepass", "hunter2"], [], ["CMD-SHELL", "redis-cli -a hunter2 ping | awk '{print $1}'"]),
        };
        var r = EnvRecovery.Recover(config, actual, new HashSet<string>());
        Assert.Equal("hunter2", r.Values["REDIS_PASSWORD"]);
        Assert.Empty(r.Unresolved);
    }

    [Theory]
    [InlineData("plain", "K=plain")]
    [InlineData("with space", "K='with space'")]
    [InlineData("it's", "K=\"it's\"")]
    public void Writes_env_lines(string value, string line) => Assert.Equal(line, EnvRecovery.Line("K", value));
}
