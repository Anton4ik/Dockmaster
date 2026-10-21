using Dockmaster.Server.Services;
using L = Dockmaster.Server.Services.ComposeEditor.ServiceLimits;

namespace Dockmaster.Tests;

public class ComposeEditorTests
{
    private static ServiceSummary Svc(string yaml, string name) => ComposeServices.Parse(yaml).Single(s => s.Name == name);

    [Fact]
    public void Replaces_existing_values_and_keeps_comments()
    {
        var yaml = """
            services:
              web:
                image: nginx
                deploy:
                  replicas: 2   # two of them
                  resources:
                    limits:
                      memory: 128M
                      cpus: "0.5"
              db:
                image: postgres
            """;
        var result = ComposeEditor.SetLimits(yaml, "web", new L(3, "256M", "1.5"));
        Assert.Contains("replicas: 3   # two of them", result);
        Assert.Contains("memory: 256M", result);
        Assert.Contains("cpus: \"1.5\"", result);
        Assert.Contains("  db:\n    image: postgres", result.ReplaceLineEndings("\n"));
        var s = Svc(result, "web");
        Assert.Equal((3, "256M", "1.5"), (s.Replicas, s.MemoryLimit, s.CpuLimit));
    }

    [Fact]
    public void Inserts_missing_deploy_block_inside_the_right_service()
    {
        var yaml = """
            services:
              web:
                image: nginx
                ports:
                  - "80:80"
              db:
                image: postgres
            """;
        var result = ComposeEditor.SetLimits(yaml, "web", new L(2, "512M", "2"));
        var web = Svc(result, "web");
        Assert.Equal((2, "512M", "2"), (web.Replicas, web.MemoryLimit, web.CpuLimit));
        Assert.Equal(1, web.Ports);
        var db = Svc(result, "db");
        Assert.Equal((1, (string?)null), (db.Replicas, db.MemoryLimit));
    }

    [Fact]
    public void More_replicas_drop_a_container_name_equal_to_the_service()
    {
        var yaml = "services:\n  web:\n    image: nginx\n    container_name: web\n    restart: always\n";
        var result = ComposeEditor.SetLimits(yaml, "web", new L(2, null, null));
        var s = Svc(result, "web");
        Assert.Equal((2, false), (s.Replicas, s.HasContainerName));
        Assert.Contains("restart: always", result);
        // one replica keeps the name
        Assert.True(Svc(ComposeEditor.SetLimits(yaml, "web", new L(1, null, null)), "web").HasContainerName);
    }

    [Fact]
    public void More_replicas_drop_another_container_name_too()
    {
        var yaml = "services:\n  web:\n    image: nginx\n    container_name: frontend\n";
        Assert.True(ComposeEditor.TryEdit(yaml, y => ComposeEditor.SetLimits(y, "web", new L(3, null, null)), out var result, out _));
        var s = Svc(result, "web");
        Assert.Equal((3, null), (s.Replicas, s.ContainerName));
    }

    [Fact]
    public void Autoscale_auto_drops_the_container_name_recommend_keeps_it()
    {
        var yaml = "services:\n  web:\n    image: nginx\n    container_name: web\n";
        Assert.Null(Svc(ComposeEditor.SetAutoscale(yaml, "web", new AutoscaleSpec(3, 70, [], true)), "web").ContainerName);
        Assert.Equal("web", Svc(ComposeEditor.SetAutoscale(yaml, "web", new AutoscaleSpec(3, 70, [], false)), "web").ContainerName);
    }

    [Fact]
    public void Inserts_into_the_last_service_without_trailing_newline()
    {
        var yaml = "services:\n  web:\n    image: nginx";
        var result = ComposeEditor.SetLimits(yaml, "web", new L(4, null, null));
        Assert.Equal(4, Svc(result, "web").Replicas);
    }

    [Fact]
    public void Adds_limits_next_to_existing_deploy_keys()
    {
        var yaml = """
            services:
              api:
                image: api
                deploy:
                  replicas: 2
                  update_config:
                    parallelism: 1
                    delay: 5s
                restart: always
            """;
        var result = ComposeEditor.SetLimits(yaml, "api", new L(2, "1G", null));
        var s = Svc(result, "api");
        Assert.Equal(("1G", "5s", 2), (s.MemoryLimit, s.UpdateDelay, s.Replicas));
        Assert.Contains("restart: always", result);
    }

    [Fact]
    public void Empty_value_removes_the_limit()
    {
        var yaml = """
            services:
              web:
                image: nginx
                deploy:
                  resources:
                    limits:
                      memory: 128M
                      cpus: "0.5"
            """;
        var result = ComposeEditor.SetLimits(yaml, "web", new L(null, "", "0.25"));
        var s = Svc(result, "web");
        Assert.Null(s.MemoryLimit);
        Assert.Equal("0.25", s.CpuLimit);
    }

    [Fact]
    public void Removing_the_last_limit_drops_the_emptied_parents()
    {
        var yaml = """
            services:
              web:
                image: nginx
                deploy:
                  resources:
                    limits:
                      memory: 64M
                restart: always

            """;
        var result = ComposeEditor.SetLimits(yaml, "web", new L(null, "", null));
        Assert.Equal("services:\n  web:\n    image: nginx\n    restart: always\n", result.ReplaceLineEndings("\n"));
        // replicas keep the deploy block alive
        var withReplicas = """
            services:
              web:
                image: nginx
                deploy:
                  replicas: 2
                  resources:
                    limits:
                      memory: 64M

            """;
        Assert.Equal("services:\n  web:\n    image: nginx\n    deploy:\n      replicas: 2\n", ComposeEditor.SetLimits(withReplicas, "web", new L(null, "", null)).ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Uses_legacy_service_level_keys_when_present()
    {
        var yaml = """
            services:
              web:
                image: nginx
                mem_limit: 100m
                cpus: 0.5
            """;
        var result = ComposeEditor.SetLimits(yaml, "web", new L(null, "300m", "1"));
        Assert.Contains("mem_limit: 300m", result);
        Assert.Contains("cpus: \"1\"", result);
        Assert.DoesNotContain("resources", result);
    }

    [Fact]
    public void Fills_an_empty_deploy_key()
    {
        var yaml = "services:\n  web:\n    image: nginx\n    deploy:\n    restart: always\n";
        var result = ComposeEditor.SetLimits(yaml, "web", new L(3, null, null));
        Assert.Equal(3, Svc(result, "web").Replicas);
        Assert.Contains("restart: always", result);
    }

    [Fact]
    public void Keeps_crlf_line_endings()
    {
        var yaml = "services:\r\n  web:\r\n    image: nginx\r\n";
        var result = ComposeEditor.SetLimits(yaml, "web", new L(2, "64M", null));
        Assert.DoesNotContain("\n", result.Replace("\r\n", ""));
        Assert.Equal((2, "64M"), (Svc(result, "web").Replicas, Svc(result, "web").MemoryLimit));
    }

    [Fact]
    public void Unknown_service_and_flow_mappings_are_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => ComposeEditor.SetLimits("services:\n  web:\n    image: x\n", "nope", new L(1, null, null)));
        Assert.Throws<InvalidOperationException>(() => ComposeEditor.SetLimits("services:\n  web: {image: x}\n", "web", new L(2, null, null)));
    }

    [Fact]
    public void Null_leaves_the_other_limit_alone()
    {
        var yaml = """
            services:
              web:
                image: nginx
                deploy:
                  resources:
                    limits:
                      memory: 128M
                      cpus: "0.5"
            """;
        var s = Svc(ComposeEditor.SetLimits(yaml, "web", new L(null, "256M", null)), "web");
        Assert.Equal(("256M", "0.5"), (s.MemoryLimit, s.CpuLimit));
    }

    [Fact]
    public void Placement_is_written_under_x_dockmaster_and_removed_with_it()
    {
        var yaml = "services:\n  web:\n    image: nginx\n  db:\n    image: postgres\n";
        var placed = ComposeEditor.SetPlacement(yaml, "db", ["beta box"]);
        Assert.EndsWith("  db:\n    image: postgres\n    x-dockmaster:\n      agent: \"beta box\"\n", placed);
        Assert.Equal(["beta box"], ComposeServices.Parse(placed).Single(s => s.Name == "db").AgentNames);
        var moved = ComposeEditor.SetPlacement(placed, "db", ["gamma"]);
        Assert.Contains("agent: \"gamma\"", moved);
        Assert.DoesNotContain("beta box", moved);
        Assert.Equal(yaml, ComposeEditor.SetPlacement(moved, "db", []));
        // an existing x-dockmaster block with other keys keeps them
        var withRolling = "services:\n  web:\n    image: nginx\n    x-dockmaster:\n      rolling: true\n";
        var r = ComposeEditor.SetPlacement(withRolling, "web", ["beta"]);
        Assert.Equal("services:\n  web:\n    image: nginx\n    x-dockmaster:\n      rolling: true\n      agent: \"beta\"\n", r);
        Assert.Equal(withRolling, ComposeEditor.SetPlacement(r, "web", []));
    }

    [Fact]
    public void Several_servers_are_written_as_a_list_and_one_goes_back_to_agent()
    {
        var yaml = "services:\n  web:\n    image: nginx\n    deploy:\n      replicas: 4\n";
        var spread = ComposeEditor.SetPlacement(yaml, "web", ["alpha", "beta"]);
        Assert.EndsWith("    x-dockmaster:\n      agents: [\"alpha\", \"beta\"]\n", spread);
        Assert.Equal(["alpha", "beta"], ComposeServices.Parse(spread).Single().AgentNames);
        var one = ComposeEditor.SetPlacement(spread, "web", ["beta"]);
        Assert.Contains("agent: \"beta\"", one);
        Assert.DoesNotContain("agents", one);
        Assert.Equal(yaml, ComposeEditor.SetPlacement(one, "web", []));
        // a block list written by hand is replaced as well
        var block = yaml + "    x-dockmaster:\n      agents:\n        - alpha\n        - beta\n";
        Assert.Equal(["gamma"], ComposeServices.Parse(ComposeEditor.SetPlacement(block, "web", ["gamma"])).Single().AgentNames);
    }

    [Theory]
    [InlineData("512M", 512L << 20)]
    [InlineData("1.5g", 3L << 29)]
    [InlineData("300mb", 300L << 20)]
    [InlineData("268435456", 268435456L)]
    [InlineData("64k", 64L << 10)]
    public void Parses_memory(string s, long bytes) => Assert.Equal(bytes, ComposeEditor.ParseMemory(s));

    [Theory]
    [InlineData("512M", null)]
    [InlineData("1.5g", null)]
    [InlineData("268435456", null)]
    [InlineData("lots", "memory")]
    [InlineData("150", "150M")]
    [InlineData("1m", "at least 6M")]
    public void Validates_memory(string memory, string? error)
    {
        var e = ComposeEditor.Validate(new L(1, memory, null));
        if (error == null) Assert.Null(e); else Assert.Contains(error, e);
    }

    [Fact]
    public void Sets_the_restart_policy()
    {
        var yaml = """
            services:
              web:
                image: nginx
                restart: always   # keep it up
              db:
                image: postgres
            """;
        var web = ComposeEditor.SetRestart(yaml, "web", "unless-stopped", 0);
        Assert.Contains("restart: unless-stopped   # keep it up", web);
        var db = ComposeEditor.SetRestart(yaml, "db", "on-failure", 3).Replace("\r\n", "\n");
        Assert.Contains("  db:\n    image: postgres\n    restart: on-failure:3", db);
        // bare no is YAML false: written quoted
        Assert.Contains("restart: \"no\"", ComposeEditor.SetRestart(yaml, "web", "no", 0));
    }
}
