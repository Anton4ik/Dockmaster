using Dockmaster.Server.Services;

namespace Dockmaster.Tests;

public class ComposeMountsTests
{
    // raw literals follow the checkout's line endings: the editor keeps whatever the file has, the asserts expect \n
    private static readonly string Yaml = """
        services:
          db:
            image: postgres:17
            volumes:
              - pgdata:/var/lib/postgresql/data   # the database
              - type: bind
                source: ./init
                target: /docker-entrypoint-initdb.d
                bind:
                  propagation: rprivate
          web:
            image: nginx
        volumes:
          pgdata:
          shared:
            name: shared-files
            external: true

        """.ReplaceLineEndings("\n");

    private static MountSummary[] Mounts(string yaml, string service) => ComposeServices.Parse(yaml, "shop").Single(s => s.Name == service).Mounts;

    [Fact]
    public void Reads_short_and_long_entries_with_the_volume_names_docker_uses()
    {
        var m = Mounts(Yaml, "db");
        Assert.Equal(new MountSummary("volume", "pgdata", "/var/lib/postgresql/data", false, true, "shop_pgdata"), m[0]);
        Assert.Equal(("bind", "./init", false), (m[1].Type, m[1].Source, m[1].Editable)); // bind options the UI does not show
        Assert.Empty(Mounts(Yaml, "web"));
    }

    [Theory]
    [InlineData("/cache", "anonymous", "", "/cache", false, true)]
    [InlineData("./conf:/etc/nginx/conf.d:ro", "bind", "./conf", "/etc/nginx/conf.d", true, true)]
    [InlineData("/srv/www:/usr/share/nginx/html:rw", "bind", "/srv/www", "/usr/share/nginx/html", false, true)]
    [InlineData("data:/data:nocopy", "volume", "data", "/data", false, false)]
    [InlineData("/a:/b:z", "bind", "/a", "/b", false, false)]
    public void Reads_the_short_syntax(string entry, string type, string source, string target, bool ro, bool editable)
    {
        var m = Mounts($"services:\n  s:\n    image: x\n    volumes:\n      - \"{entry}\"\n", "s").Single();
        Assert.Equal((type, source, target, ro, editable), (m.Type, m.Source, m.Target, m.ReadOnly, m.Editable));
    }

    [Fact]
    public void An_external_volume_keeps_its_own_name()
    {
        var yaml = ComposeEditor.SetMounts(Yaml, "shop", "web", [new MountEdit(null, "volume", "shared", "/srv", true)]);
        Assert.Equal(new MountSummary("volume", "shared", "/srv", true, true, "shared-files", true), Mounts(yaml, "web").Single());
    }

    [Fact]
    public void Adds_a_list_and_declares_a_new_volume()
    {
        var yaml = ComposeEditor.SetMounts(Yaml, "shop", "web", [new MountEdit(null, "volume", "html", "/usr/share/nginx/html", false)]);
        Assert.Contains("""
              web:
                image: nginx
                volumes:
                  - "html:/usr/share/nginx/html"
            volumes:
              pgdata:
            """.ReplaceLineEndings("\n"), yaml);
        Assert.EndsWith("""
                external: true
              html: {}

            """.ReplaceLineEndings("\n"), yaml);
    }

    [Fact]
    public void Keeps_entries_as_written_and_adds_behind_them()
    {
        var yaml = ComposeEditor.SetMounts(Yaml, "shop", "db", [
            new MountEdit(0, "", "", "", false),
            new MountEdit(1, "", "", "", false),
            new MountEdit(null, "bind", "/backup", "/backup", true),
        ]);
        Assert.Contains("""
                volumes:
                  - pgdata:/var/lib/postgresql/data   # the database
                  - type: bind
                    source: ./init
                    target: /docker-entrypoint-initdb.d
                    bind:
                      propagation: rprivate
                  - "/backup:/backup:ro"
              web:
            """.ReplaceLineEndings("\n"), yaml);
    }

    [Fact]
    public void Removing_the_last_mount_of_a_volume_takes_its_declaration_along()
    {
        var yaml = ComposeEditor.SetMounts(Yaml, "shop", "db", [new MountEdit(1, "", "", "", false)]);
        Assert.DoesNotContain("pgdata", yaml);
        Assert.Contains("volumes:\n  shared:", yaml);
        // no mounts left: the list goes, and a volumes section left empty goes too
        var bare = "services:\n  db:\n    image: x\n    volumes:\n      - data:/data\nvolumes:\n  data: {}\n";
        Assert.Equal("services:\n  db:\n    image: x\n", ComposeEditor.SetMounts(bare, "p", "db", []));
    }

    [Fact]
    public void An_existing_volume_of_the_server_is_declared_external()
    {
        var yaml = ComposeEditor.SetMounts("services:\n  app:\n    image: x\n", "p", "app", [new MountEdit(null, "volume", "legacy_data", "/data", false, "legacy_data")]);
        Assert.Equal("services:\n  app:\n    image: x\n    volumes:\n      - \"legacy_data:/data\"\nvolumes:\n  legacy_data:\n    name: \"legacy_data\"\n    external: true\n", yaml);
        Assert.Equal(("legacy_data", true), (Mounts(yaml, "app")[0].VolumeName, Mounts(yaml, "app")[0].External));
    }

    [Fact]
    public void Changes_one_entry_of_a_flow_list()
    {
        var yaml = ComposeEditor.SetMounts("services:\n  app:\n    image: x\n    volumes: [\"./a:/a\", \"./b:/b\"]\n", "p", "app",
            [new MountEdit(0, "", "", "", false), new MountEdit(null, "bind", "./b", "/b", true)]);
        Assert.Contains("volumes: [\"./a:/a\", \"./b:/b:ro\"]", yaml);
    }

    [Theory]
    [InlineData("volume", "data", "data", null)]
    [InlineData("volume", "data", "data/x", "is not a path")]
    [InlineData("volume", "my data", "/data2", "is not a volume name")]
    [InlineData("bind", "srv/app", "/app", "is not a folder")]
    [InlineData("bind", "/srv/app", "/var/lib/postgresql/data", "two mounts")]
    public void Checks_what_the_page_sends(string type, string source, string target, string? error)
    {
        var current = Mounts(Yaml, "db");
        var got = ComposeMounts.Validate([new MountEdit(0, "", "", "", false), new MountEdit(null, type, source, target == "data" ? "/data" : target, false)], current);
        if (error == null) Assert.Null(got);
        else Assert.Contains(error, got);
    }
}
