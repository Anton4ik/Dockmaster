using System.Text.Json.Nodes;
using Dockmaster.Agent.Docker;

namespace Dockmaster.Tests;

public class ContainerCloneTests
{
    private static JsonObject Container(string config, string? networks = null) => JsonNode.Parse($$"""
        {
          "Id": "0123456789abcdef0123",
          "Name": "/audiobookshelf",
          "Config": {{config}},
          "HostConfig": { "NetworkMode": "bridge", "Memory": 67108864, "SomethingNew": { "kept": true } },
          "NetworkSettings": { "Networks": {{networks ?? """{ "bridge": { "Aliases": null } }"""}} }
        }
        """)!.AsObject();

    private static JsonObject Image(string config) => JsonNode.Parse($$"""{ "Id": "sha256:1", "Config": {{config}} }""")!.AsObject();

    private static string[] Strings(JsonNode? n) => n!.AsArray().Select(x => x!.GetValue<string>()).ToArray();

    // audiobookshelf ≤ 2.2x: WORKDIR /, from 2.3x on: WORKDIR /app — a clone kept / and node looked for /index.js
    [Fact]
    public void Leaves_what_came_from_the_old_image_to_the_new_one()
    {
        var oldImage = Image("""
            { "WorkingDir": "/", "Cmd": ["node", "index.js"], "Entrypoint": ["tini", "--"], "User": "",
              "Env": ["PATH=/usr/bin", "NODE_ENV=production"], "ExposedPorts": { "80/tcp": {} },
              "Healthcheck": { "Test": ["CMD", "curl", "-f", "http://localhost"] }, "Labels": { "org.version": "2.2" } }
            """);
        var old = Container("""
            { "Hostname": "0123456789ab", "WorkingDir": "/", "Cmd": ["node", "index.js"], "Entrypoint": ["tini", "--"], "User": "",
              "Env": ["PATH=/usr/bin", "NODE_ENV=production", "TZ=Europe/Riga"], "ExposedPorts": { "80/tcp": {} },
              "Healthcheck": { "Test": ["CMD", "curl", "-f", "http://localhost"] }, "Labels": { "org.version": "2.2", "mine": "x" },
              "Image": "advplyr/audiobookshelf:latest" }
            """);

        var p = ContainerClone.Parameters(old, oldImage, "audiobookshelf", "advplyr/audiobookshelf:latest");

        Assert.Null(p["WorkingDir"]);
        Assert.Null(p["Cmd"]);
        Assert.Null(p["Entrypoint"]);
        Assert.Null(p["Healthcheck"]);
        Assert.Null(p["ExposedPorts"]);
        Assert.Null(p["Hostname"]);
        Assert.Null(p["User"]);
        Assert.Equal(["TZ=Europe/Riga"], Strings(p["Env"]));
        Assert.Equal("""{"mine":"x"}""", p["Labels"]!.ToJsonString());
        Assert.Equal("advplyr/audiobookshelf:latest", p["Image"]!.GetValue<string>());
    }

    [Fact]
    public void Keeps_what_the_container_set_itself()
    {
        var oldImage = Image("""{ "WorkingDir": "/app", "Cmd": ["node", "index.js"], "Entrypoint": ["tini", "--"], "ExposedPorts": { "80/tcp": {} } }""");
        var old = Container("""
            { "WorkingDir": "/srv", "Cmd": ["node", "index.js", "--debug"], "Entrypoint": ["tini", "--"], "User": "1000:1000",
              "ExposedPorts": { "80/tcp": {}, "9000/tcp": {} },
              "Healthcheck": { "Test": ["CMD", "true"], "Interval": 30000000000, "StartInterval": 1000000000 } }
            """);

        var p = ContainerClone.Parameters(old, oldImage, "app", "app:latest");

        Assert.Equal("/srv", p["WorkingDir"]!.GetValue<string>());
        Assert.Equal(["node", "index.js", "--debug"], Strings(p["Cmd"]));
        Assert.Null(p["Entrypoint"]);
        Assert.Equal("1000:1000", p["User"]!.GetValue<string>());
        Assert.Equal("""{"9000/tcp":{}}""", p["ExposedPorts"]!.ToJsonString());
        // fields the agent knows nothing about go along as they are
        Assert.Equal(1000000000, p["Healthcheck"]!["StartInterval"]!.GetValue<long>());
    }

    [Fact]
    public void Own_entrypoint_keeps_the_command_too()
    {
        // Docker drops the image's command once the entrypoint is overridden, so an equal command is still the container's
        var oldImage = Image("""{ "Cmd": ["serve"], "Entrypoint": ["/entry.sh"] }""");
        var old = Container("""{ "Cmd": ["serve"], "Entrypoint": ["/bin/sh", "-c"] }""");

        var p = ContainerClone.Parameters(old, oldImage, "app", "app:latest");

        Assert.Equal(["/bin/sh", "-c"], Strings(p["Entrypoint"]));
        Assert.Equal(["serve"], Strings(p["Cmd"]));
    }

    [Fact]
    public void Without_the_old_image_everything_is_kept()
    {
        var old = Container("""{ "WorkingDir": "/", "Cmd": ["node", "index.js"], "Entrypoint": ["tini", "--"], "Env": ["PATH=/usr/bin"] }""");

        var p = ContainerClone.Parameters(old, null, "app", "app:latest");

        Assert.Equal("/", p["WorkingDir"]!.GetValue<string>());
        Assert.Equal(["node", "index.js"], Strings(p["Cmd"]));
        Assert.Equal(["tini", "--"], Strings(p["Entrypoint"]));
        Assert.Equal(["PATH=/usr/bin"], Strings(p["Env"]));
    }

    [Fact]
    public void The_host_config_goes_back_whole()
    {
        var p = ContainerClone.Parameters(Container("{}"), null, "app", "app:latest");
        Assert.Equal(67108864, p["HostConfig"]!["Memory"]!.GetValue<long>());
        Assert.True(p["HostConfig"]!["SomethingNew"]!["kept"]!.GetValue<bool>());
    }

    [Fact]
    public void Joins_the_first_network_on_create_and_the_others_after_with_their_aliases()
    {
        var old = Container("{}", """
            {
              "web_default": { "Aliases": ["web-web-3", "web", "0123456789ab"] },
              "public": { "Aliases": ["site"] }
            }
            """);
        old["HostConfig"]!["NetworkMode"] = "web_default";

        var p = ContainerClone.Parameters(old, null, "web-web-1", "web:latest", aliasFrom: "web-web-3");
        var first = p["NetworkingConfig"]!["EndpointsConfig"]!.AsObject();
        Assert.Equal(["web_default"], first.Select(e => e.Key));
        // compose's name of the made container becomes the slot's; Docker's own short id alias is not passed on
        Assert.Equal(["web-web-1", "web"], Strings(first["web_default"]!["Aliases"]));

        var others = ContainerClone.OtherNetworks(old, "web-web-1", "web-web-3").ToList();
        Assert.Equal(["public"], others.Select(o => o.network));
        Assert.Equal(["site"], Strings(others[0].endpoint["Aliases"]));
    }

    [Fact]
    public void Own_networking_joins_nothing()
    {
        var old = Container("{}", """{ "host": {} }""");
        old["HostConfig"]!["NetworkMode"] = "host";
        var p = ContainerClone.Parameters(old, null, "app", "app:latest");
        Assert.Null(p["NetworkingConfig"]);
        Assert.Empty(ContainerClone.OtherNetworks(old, "app"));
    }

    [Fact]
    public void An_agent_update_adds_the_hosts_cgroup_tree_once()
    {
        var create = new JsonObject { ["HostConfig"] = new JsonObject { ["Binds"] = new JsonArray("/var/run/docker.sock:/var/run/docker.sock") } };
        Assert.True(AgentUpdater.AddHostMounts(create));
        Assert.False(AgentUpdater.AddHostMounts(create));
        Assert.Equal(["/var/run/docker.sock:/var/run/docker.sock", "/sys/fs/cgroup:/host/cgroup:ro"],
            create["HostConfig"]!["Binds"]!.AsArray().Select(b => b!.GetValue<string>()));

        // mounted already, by a mount of its own
        var mounted = new JsonObject { ["HostConfig"] = new JsonObject { ["Mounts"] = new JsonArray(new JsonObject { ["Target"] = "/host/cgroup" }) } };
        Assert.False(AgentUpdater.AddHostMounts(mounted));
        Assert.Null(mounted["HostConfig"]!["Binds"]);
    }
}
