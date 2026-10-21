using Dockmaster.Server.Services;
using YamlDotNet.RepresentationModel;

namespace Dockmaster.Tests;

public class ComposeNetworkTests
{
    private static List<string> Networks(string yaml, string service)
    {
        var ys = new YamlStream();
        ys.Load(new StringReader(yaml));
        var svc = (YamlMappingNode)((YamlMappingNode)((YamlMappingNode)ys.Documents[0].RootNode)["services"])[service];
        return svc.Children.TryGetValue(new YamlScalarNode("networks"), out var n)
            ? n switch
            {
                YamlSequenceNode s => s.Children.Select(c => ((YamlScalarNode)c).Value!).ToList(),
                YamlMappingNode m => m.Children.Keys.Select(k => ((YamlScalarNode)k).Value!).ToList(),
                _ => [],
            }
            : [];
    }

    [Theory]
    [InlineData("app_default", "default")]
    [InlineData("my_network", "mine")]
    [InlineData("proxy_network", "proxy_network")]
    [InlineData("app_back", "back")]
    [InlineData("elsewhere", null)]
    public void Maps_docker_names_to_compose_keys(string docker, string? key)
    {
        var yaml = """
            services:
              web:
                image: nginx
            networks:
              mine:
                name: my_network
                external: true
              proxy_network:
                external: true
              back: {}
            """;
        Assert.Equal(key, ComposeEditor.NetworkKey(yaml, "app", docker));
    }

    [Fact]
    public void A_service_without_networks_keeps_the_default_one_and_the_new_network_is_declared()
    {
        var yaml = "services:\n  web:\n    image: nginx\n";
        var result = ComposeEditor.SetServiceNetwork(yaml, "app", "web", "shared", attach: true);
        Assert.Equal(["default", "shared"], Networks(result, "web"));
        Assert.Contains("networks:\n  shared:\n    name: \"shared\"\n    external: true", result.ReplaceLineEndings("\n"));
        Assert.Equal("shared", ComposeEditor.NetworkKey(result, "app", "shared"));
    }

    [Fact]
    public void The_map_form_gains_and_loses_an_entry()
    {
        var yaml = """
            services:
              web:
                image: nginx
                networks:
                  my_network: null
                  proxy_network: null
            networks:
              my_network:
                external: true
                name: my_network
              proxy_network:
                external: true
                name: proxy_network
            """;
        var detached = ComposeEditor.SetServiceNetwork(yaml, "app", "web", "proxy_network", attach: false);
        Assert.Equal(["my_network"], Networks(detached, "web"));
        Assert.Contains("  proxy_network:\n    external: true", detached.ReplaceLineEndings("\n")); // the declaration stays

        var attached = ComposeEditor.SetServiceNetwork(detached, "app", "web", "proxy_network", attach: true);
        Assert.Equal(["my_network", "proxy_network"], Networks(attached, "web"));
    }

    [Fact]
    public void The_list_form_is_kept_and_a_new_declaration_joins_the_existing_block()
    {
        var yaml = """
            services:
              web:
                image: nginx
                networks:
                  - back
            networks:
              back: {}
            """;
        var result = ComposeEditor.SetServiceNetwork(yaml, "app", "web", "traefik", attach: true);
        Assert.Equal(["back", "traefik"], Networks(result, "web"));
        Assert.Equal("traefik", ComposeEditor.NetworkKey(result, "app", "traefik"));
        Assert.Equal("back", ComposeEditor.NetworkKey(result, "app", "app_back"));
    }

    [Fact]
    public void The_last_network_is_not_removed()
    {
        var yaml = "services:\n  web:\n    image: nginx\n    networks:\n      - back\nnetworks:\n  back: {}\n";
        var ex = Assert.Throws<InvalidOperationException>(() => ComposeEditor.SetServiceNetwork(yaml, "app", "web", "app_back", attach: false));
        Assert.Contains("last network", ex.Message);
    }

    [Fact]
    public void Unchanged_when_already_there_or_already_gone()
    {
        var yaml = "services:\n  web:\n    image: nginx\n    networks:\n      - back\n      - front\nnetworks:\n  back: {}\n  front: {}\n";
        Assert.Equal(yaml, ComposeEditor.SetServiceNetwork(yaml, "app", "web", "app_back", attach: true));
        Assert.Equal(yaml, ComposeEditor.SetServiceNetwork(yaml, "app", "web", "unknown", attach: false));
    }
}
