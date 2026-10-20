using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Dockmaster.Server.Data;
using Dockmaster.Server.Services;

namespace Dockmaster.Tests;

public class TemplateTests
{
    private static readonly List<TemplateVariable> Vars =
    [
        // JSON needs a pattern of its own: the default takes a word
        new("SETTINGS", Pattern: "[^']*", Required: true),
        new("BOTS", Pattern: "[0-9]{1,3}", Default: "150"),
        new("MOTD"),
    ];

    [Fact]
    public void Values_are_the_callers_else_the_defaults_and_unknown_or_missing_ones_are_refused()
    {
        var (values, error) = Templates.Resolve(Vars, new Dictionary<string, string> { ["SETTINGS"] = """{"events":true}""" });
        Assert.Null(error);
        Assert.Equal("150", values!["BOTS"]);
        Assert.False(values.ContainsKey("MOTD"));

        Assert.Contains("required", Templates.Resolve(Vars, null).Error);
        Assert.Contains("no variable BOTZ", Templates.Resolve(Vars, new Dictionary<string, string> { ["SETTINGS"] = "x", ["BOTZ"] = "1" }).Error);
        Assert.Contains("does not match", Templates.Resolve(Vars, new Dictionary<string, string> { ["SETTINGS"] = "x", ["BOTS"] = "1000" }).Error);
    }

    [Fact]
    public void A_required_variable_set_to_nothing_is_missing() =>
        Assert.Contains("required", Templates.Resolve(Vars, new Dictionary<string, string> { ["SETTINGS"] = "" }).Error);

    [Fact]
    public void A_pattern_cannot_break_out_of_matching_the_whole_value()
    {
        // wrapped as ^(?:a)|(.*)$ it would compile and let anything through
        Assert.NotNull(Templates.ValidateVariables([new("A", Pattern: "a)|(.*")]));
        Assert.Contains("not a valid", Templates.Resolve([new("A", Pattern: "a)|(.*")], new Dictionary<string, string> { ["A"] = "zzz" }).Error);
        // written with anchors of its own, it still works
        Assert.Null(Templates.Resolve([new("A", Pattern: "^[a-z]+$")], new Dictionary<string, string> { ["A"] = "abc" }).Error);
    }

    [Fact]
    public void Stored_values_of_variables_the_template_no_longer_has_are_dropped()
    {
        var kept = Templates.Known([new("A")], new Dictionary<string, string> { ["A"] = "1", ["GONE"] = "2" });
        Assert.Equal(["A"], kept.Keys);
    }

    [Theory]
    [InlineData("x' \nevil: true")]
    [InlineData("a\nb")]
    [InlineData("it's")]
    // compose reads \' as a quote inside a single-quoted value: the value would run on into the next line
    [InlineData("abc\\")]
    public void A_value_can_never_end_its_env_line(string value)
    {
        // even a variable whose pattern lets anything through
        var open = new List<TemplateVariable> { new("ANY", Pattern: ".*") };
        Assert.NotNull(Templates.Resolve(open, new Dictionary<string, string> { ["ANY"] = value }).Error);
    }

    [Theory]
    [InlineData("`whoami`")]
    [InlineData("${HOME}")]
    [InlineData("a\\b")]
    // `${DATA}:/data` must not become the docker socket, a parent folder or a port mapping
    [InlineData("/var/run/docker.sock")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("../etc")]
    [InlineData("0.0.0.0:22")]
    [InlineData("-v")]
    [InlineData("a b")]
    public void The_default_pattern_refuses_what_a_shell_or_compose_would_read_into(string value) =>
        Assert.NotNull(Templates.Resolve([new("V")], new Dictionary<string, string> { ["V"] = value }).Error);

    [Theory]
    [InlineData("abc123")]
    [InlineData("my-bot_2.0")]
    [InlineData("")]
    public void The_default_pattern_takes_a_word(string value) =>
        Assert.Null(Templates.Resolve([new("V")], new Dictionary<string, string> { ["V"] = value }).Error);

    [Fact]
    public void Variables_in_host_paths_ports_and_images_are_pointed_out()
    {
        const string yaml = """
            services:
              app:
                image: "nginx:${TAG}"
                environment:
                  MOTD: ${MOTD}
                volumes:
                  - ${DATA}:/data
                ports: ["${PORT:-8080}:80"]
            volumes:
              data:
                driver_opts: {type: none, o: bind, device: $SRC}
            """;
        var warnings = Templates.Warnings(yaml, [new("TAG"), new("MOTD"), new("DATA"), new("PORT"), new("SRC")]);
        Assert.Equal(4, warnings.Count);
        Assert.Contains(warnings, w => w.StartsWith("service app, image: variable TAG"));
        Assert.Contains(warnings, w => w.StartsWith("service app, volumes: variable DATA"));
        Assert.Contains(warnings, w => w.StartsWith("service app, ports: variable PORT"));
        Assert.Contains(warnings, w => w.StartsWith("volume data: variable SRC"));
        // INSTANCE is Dockmaster's own: no warning
        Assert.Empty(Templates.Warnings("services:\n  app:\n    image: nginx\n    volumes: [\"${INSTANCE}:/data\"]\n", [new("MOTD")]));
    }

    [Fact]
    public void The_env_keeps_the_templates_lines_and_puts_the_instances_values_last_and_literal()
    {
        var env = Templates.RenderEnv("REGISTRY=nas:5000\nBOTS=10\n# note", "game", "abc", new Dictionary<string, string> { ["BOTS"] = "40", ["SETTINGS"] = """{"a":1}""" });
        Assert.Contains("REGISTRY=nas:5000", env);
        Assert.Contains("# note", env);
        Assert.DoesNotContain("BOTS=10", env);
        Assert.Contains("BOTS='40'", env);
        Assert.Contains("""SETTINGS='{"a":1}'""", env);
        Assert.Contains("INSTANCE='abc'", env);
        Assert.Contains("TEMPLATE='game'", env);
    }

    [Fact]
    public void Rules_an_admin_writes_are_checked()
    {
        Assert.Null(Templates.ValidateVariables(Vars));
        Assert.NotNull(Templates.ValidateVariables([new("INSTANCE")]));
        Assert.NotNull(Templates.ValidateVariables([new("A"), new("A")]));
        Assert.NotNull(Templates.ValidateVariables([new("1A")]));
        Assert.NotNull(Templates.ValidateVariables([new("A", Pattern: "(")]));
        Assert.NotNull(Templates.ValidateVariables([new("A", Pattern: "[0-9]+", Default: "x")]));
    }

    [Theory]
    [InlineData("abc", true)]
    [InlineData("a-1", true)]
    [InlineData("1abc", true)]
    [InlineData("-a", false)]
    [InlineData("A", false)]
    [InlineData("a_b", false)]
    [InlineData("a.b", false)]
    public void Instance_names(string name, bool ok) => Assert.Equal(ok, Templates.ValidateInstanceName(name) == null);

    [Fact]
    public void A_key_is_stored_as_its_hash_only()
    {
        var (key, hash, hint) = Templates.NewKey();
        Assert.StartsWith(Templates.KeyPrefix, key);
        Assert.Equal(hash, Templates.HashKey(key));
        Assert.DoesNotContain(key, hash);
        Assert.Equal(key[^4..], hint);
        Assert.NotEqual(key, Templates.NewKey().Key);
    }

    [Fact]
    public void A_new_instance_goes_to_the_server_with_the_most_memory_free()
    {
        Assert.Equal(2, Templates.PickAgent([(1, 0.80, 0), (2, 0.30, 5), (3, 0.50, 0)]));
        // about as full: the one with fewer instances
        Assert.Equal(3, Templates.PickAgent([(1, 0.301, 4), (3, 0.299, 1)]));
        Assert.Null(Templates.PickAgent([]));
    }

    [Fact]
    public void Instance_name_back_from_the_project()
    {
        Assert.Equal("abc", Templates.InstanceName("brawltown-game", Templates.ProjectName("brawltown-game", "abc")));
        Assert.Equal("other", Templates.InstanceName("brawltown-game", "other"));
    }

    /// <summary>An older database gets the tables and columns; templates, keys and instances round-trip.</summary>
    [Fact]
    public async Task Templates_keys_and_instances_are_stored()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await using var db = new DockmasterDb(new DbContextOptionsBuilder<DockmasterDb>().UseSqlite(conn).Options);
        await db.Database.EnsureCreatedAsync();
        await db.UpgradeSchemaAsync();
        await db.UpgradeSchemaAsync();

        var t = new TemplateEntity { Name = "game", ComposeYaml = "services: {game: {image: x}}", Variables = Templates.SerializeVariables(Vars), AgentIds = "2,3" };
        db.Templates.Add(t);
        await db.SaveChangesAsync();
        db.ApiKeys.Add(new ApiKeyEntity { Name = "backend", KeyHash = Templates.HashKey("dmk_x"), Hint = "_x", TemplateIds = t.Id.ToString() });
        db.Projects.Add(new ProjectEntity { Name = "game-a", AgentId = 2, ComposeYaml = t.ComposeYaml, TemplateId = t.Id, InstanceVariables = """{"BOTS":"5"}""" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var back = await db.Templates.SingleAsync();
        Assert.Equal([2, 3], back.AllowedAgentIds);
        Assert.Equal(3, Templates.ParseVariables(back.Variables).Count);
        Assert.Equal([t.Id], (await db.ApiKeys.SingleAsync()).AllowedTemplateIds);
        var p = await db.Projects.SingleAsync(x => x.TemplateId == t.Id);
        Assert.Equal("5", Templates.ParseValues(p.InstanceVariables)["BOTS"]);
    }
}
