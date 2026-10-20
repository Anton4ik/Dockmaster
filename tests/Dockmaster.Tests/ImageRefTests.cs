using Microsoft.AspNetCore.DataProtection;
using Dockmaster.Server.Data;
using Dockmaster.Server.Services;

namespace Dockmaster.Tests;

public class ImageRefTests
{
    [Theory]
    [InlineData("nginx", "registry-1.docker.io", "library/nginx", "latest")]
    [InlineData("nginx:1.27-alpine", "registry-1.docker.io", "library/nginx", "1.27-alpine")]
    [InlineData("grafana/grafana:12.4.11", "registry-1.docker.io", "grafana/grafana", "12.4.11")]
    [InlineData("docker.io/library/redis:7", "registry-1.docker.io", "library/redis", "7")]
    [InlineData("ghcr.io/google/cadvisor:v0.60.6", "ghcr.io", "google/cadvisor", "v0.60.6")]
    [InlineData("registry.example.com:5000/dockmaster/dockmaster-server:latest", "registry.example.com:5000", "dockmaster/dockmaster-server", "latest")]
    [InlineData("localhost:5000/app", "localhost:5000", "app", "latest")]
    public void Parses_references(string image, string host, string repo, string tag) =>
        Assert.Equal((host, repo, tag), ImageRef.Parse(image));

    [Theory]
    [InlineData("sha256:93c4d7de4917")]
    [InlineData("nginx@sha256:abcdef")]
    [InlineData("")]
    public void Ignores_ids_and_digests(string image) => Assert.Null(ImageRef.Parse(image));
}

public class RegistryMatchTests
{
    private static readonly List<RegistryEntity> Registries =
    [
        new() { Id = 1, Name = "NAS", Url = "registry.example.com:5000", Aliases = "registry-alias.example.com:5001", Username = "u" },
        new() { Id = 2, Name = "Hub", Url = "docker.io", Username = "hub" },
    ];

    [Theory]
    [InlineData("registry.example.com:5000", 1)]
    [InlineData("registry-alias.example.com:5001", 1)]
    [InlineData(ImageRef.DockerHub, 2)]
    [InlineData("ghcr.io", null)]
    public void Finds_the_registry_under_any_of_its_addresses(string host, int? id) =>
        Assert.Equal(id, ImageRef.RegistryFor(Registries, host)?.Id);

    [Theory]
    [InlineData("nginx:alpine", null)]
    [InlineData("grafana/grafana", null)]
    [InlineData("localhost/app", "localhost")]
    [InlineData("registry:5000/team/app:1", "registry:5000")]
    public void Explicit_host_only_when_the_first_segment_looks_like_one(string image, string? host) =>
        Assert.Equal(host, ImageRef.ExplicitHost(image));

    [Fact]
    public void Docker_hub_images_get_the_docker_hub_login()
    {
        var client = new RegistryClient(null!, new SecretProtector(new EphemeralDataProtectionProvider()));
        var creds = ImageRef.Credentials(["grafana/grafana:12", "redis", "registry-alias.example.com:5001/team/api:latest", "ghcr.io/x/y"], Registries, client);
        Assert.Equal(["docker.io", "registry-alias.example.com:5001"], creds.Select(c => c.Server).Order());
    }
}
