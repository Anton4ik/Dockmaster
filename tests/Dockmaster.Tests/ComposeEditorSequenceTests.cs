using Dockmaster.Server.Services;

namespace Dockmaster.Tests;

public class ComposeEditorSequenceTests
{
    // raw literals follow the checkout's line endings: the editor keeps whatever the file has, the asserts expect \n
    private static readonly string Yaml = """
        services:
          web:
            image: nginx   # front
            expose:
              - "80"
              - 8080
            ports: [443]
          db:
            image: postgres

        """.ReplaceLineEndings("\n");

    [Fact]
    public void Replaces_a_block_list_in_place()
    {
        var r = ComposeEditor.SetSequence(Yaml, "web", "expose", ["81", "53/udp"]);
        Assert.Equal("""
            services:
              web:
                image: nginx   # front
                expose:
                  - "81"
                  - "53/udp"
                ports: [443]
              db:
                image: postgres

            """.ReplaceLineEndings("\n"), r);
        Assert.Equal(["81", "53/udp"], ComposeServices.Parse(r).Single(s => s.Name == "web").Expose);
    }

    [Fact]
    public void Replaces_a_flow_list_as_a_flow_list()
    {
        var r = ComposeEditor.SetSequence(Yaml, "web", "ports", ["80:80", "443:443"]);
        Assert.Contains("ports: [\"80:80\", \"443:443\"]\n", r);
        Assert.Contains("expose:\n      - \"80\"", r);
    }

    [Fact]
    public void Adds_a_missing_list_at_the_end_of_the_service()
    {
        var r = ComposeEditor.SetSequence(Yaml, "db", "expose", ["5432"]);
        Assert.EndsWith("  db:\n    image: postgres\n    expose:\n      - \"5432\"\n", r);
        Assert.Equal(Yaml, ComposeEditor.SetSequence(Yaml, "db", "expose", []));
        // a file without a final newline gets one before the block
        var noNewline = Yaml.TrimEnd('\n');
        Assert.EndsWith("    image: postgres\n    expose:\n      - \"5432\"\n", ComposeEditor.SetSequence(noNewline, "db", "expose", ["5432"]));
    }

    [Fact]
    public void An_empty_list_removes_the_key()
    {
        var r = ComposeEditor.SetSequence(Yaml, "web", "expose", []);
        Assert.Equal("""
            services:
              web:
                image: nginx   # front
                ports: [443]
              db:
                image: postgres

            """.ReplaceLineEndings("\n"), r);
        Assert.DoesNotContain("ports", ComposeEditor.SetSequence(Yaml, "web", "ports", []));
    }

    [Fact]
    public void Keeps_crlf()
    {
        var crlf = Yaml.Replace("\n", "\r\n");
        var r = ComposeEditor.SetSequence(crlf, "db", "expose", ["5432"]);
        Assert.DoesNotContain("\n\n", r.ReplaceLineEndings("\n"));
        Assert.EndsWith("    expose:\r\n      - \"5432\"\r\n", r);
    }
}
