using System.Diagnostics;
using Dockmaster.Server.Services;

namespace Dockmaster.Tests;

public class YamlAliasTests
{
    // each level holds nine aliases of the one before: 9^8 = 43 million nodes once expanded, from about 1 KB
    private static string Laughs(int depth)
    {
        var sb = new System.Text.StringBuilder("x-a0: &a0 [lol, lol, lol, lol, lol, lol, lol, lol, lol]\n");
        for (var i = 1; i <= depth; i++)
            sb.Append($"x-a{i}: &a{i} [{string.Join(", ", Enumerable.Repeat($"*a{i - 1}", 9))}]\n");
        sb.Append($"services:\n  web:\n    image: nginx\n    labels: *a{depth}\n");
        return sb.ToString();
    }

    [Fact]
    public void Nested_aliases_are_refused_before_they_expand()
    {
        var yaml = Laughs(8);
        var watch = Stopwatch.StartNew();
        Assert.Empty(ComposeServices.Parse(yaml));
        Assert.False(ComposeEditor.TryEdit(yaml, y => ComposeEditor.SetLimits(y, "web", new ComposeEditor.ServiceLimits(2, null, null)), out _, out var error));
        Assert.Contains("aliases expand", error);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void A_few_aliases_still_load()
    {
        var yaml = "x-env: &env\n  TZ: UTC\n" + Laughs(2) + "    environment: *env\n";
        var web = Assert.Single(ComposeServices.Parse(yaml));
        Assert.Equal("web", web.Name);
        Assert.Equal("UTC", Assert.Single(web.Environment!).Value);
    }

    [Fact]
    public void A_mapping_key_that_is_not_a_plain_value_is_refused()
    {
        const string yaml = """
            services:
              ? [web, db]
              : image: nginx
            """;
        Assert.Empty(ComposeServices.Parse(yaml));
        Assert.False(ComposeEditor.TryEdit(yaml, y => y, out _, out var error));
        Assert.Contains("plain value", error);
    }

    [Fact]
    public void An_alias_to_a_mapping_used_as_a_key_is_refused()
    {
        const string yaml = """
            x-m: &m {a: 1}
            services:
              *m : {image: nginx}
            """;
        Assert.Empty(ComposeServices.Parse(yaml));
    }

    [Fact]
    public void An_alias_to_a_plain_value_may_be_a_key()
    {
        const string yaml = """
            x-name: &name web
            services:
              *name : {image: nginx}
            """;
        Assert.Equal("web", Assert.Single(ComposeServices.Parse(yaml)).Name);
    }
}
