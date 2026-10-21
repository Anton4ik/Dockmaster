using Dockmaster.Server.Services;

namespace Dockmaster.Tests;

public class ServiceFormTests
{
    private static readonly string Yaml = """
        services:
          web:
            image: nginx:1.27   # front
            ports: ["8080:80"]
            environment:
              - MODE=prod
          db:
            image: postgres:17
            environment:
              POSTGRES_USER: app
              POSTGRES_PASSWORD: ${DB_PASS}
        volumes:
          pgdata: {}

        """.ReplaceLineEndings("\n");

    private static ServiceSummary Svc(string yaml, string name) => ComposeServices.Parse(yaml, "p").Single(s => s.Name == name);

    [Fact]
    public void Reads_ports_environment_and_restart()
    {
        Assert.Equal(["8080:80"], Svc(Yaml, "web").PortList!);
        Assert.Equal([new EnvVar("MODE", "prod")], Svc(Yaml, "web").Environment!);
        Assert.Equal([new EnvVar("POSTGRES_USER", "app"), new EnvVar("POSTGRES_PASSWORD", "${DB_PASS}")], Svc(Yaml, "db").Environment!);
    }

    [Fact]
    public void Edits_keep_the_style_the_file_uses()
    {
        var y = ComposeEditor.SetEnvironment(Yaml, "web", [new EnvVar("MODE", "dev"), new EnvVar("DEBUG", "1")]);
        Assert.Contains("    environment:\n      - \"MODE=dev\"\n      - \"DEBUG=1\"\n  db:", y);
        y = ComposeEditor.SetEnvironment(y, "db", [new EnvVar("POSTGRES_USER", "shop")]);
        Assert.Contains("    environment:\n      POSTGRES_USER: \"shop\"\nvolumes:", y);
        y = ComposeEditor.SetPorts(y, "web", ["80:80", "443:443"]);
        Assert.Contains("ports: [\"80:80\", \"443:443\"]\n", y);
        y = ComposeEditor.SetImage(y, "web", "nginx:1.28");
        Assert.Contains("image: nginx:1.28   # front", y);
        Assert.Equal([new EnvVar("POSTGRES_USER", "shop")], Svc(y, "db").Environment!);
    }

    [Fact]
    public void A_new_service_goes_at_the_end_of_services_and_can_be_removed_again()
    {
        var y = ComposeEditor.AddService(Yaml, "cache", "redis:7", "unless-stopped", ["6379"], [new EnvVar("TZ", "Europe/Riga")]);
        Assert.Contains("""
                  POSTGRES_PASSWORD: ${DB_PASS}
              cache:
                image: redis:7
                restart: unless-stopped
                ports:
                  - "6379"
                environment:
                  TZ: "Europe/Riga"
            volumes:
            """.ReplaceLineEndings("\n"), y);
        y = ComposeEditor.SetMounts(y, "p", "cache", [new MountEdit(null, "volume", "cache-data", "/data", false)]);
        Assert.Contains("cache-data: {}", y);
        // removing it takes its volume's declaration along
        var removed = ComposeEditor.RemoveService(ComposeEditor.SetMounts(y, "p", "cache", []), "cache");
        Assert.Equal(Yaml, removed);
        Assert.Throws<InvalidOperationException>(() => ComposeEditor.AddService(Yaml, "web", "x", "", [], []));
    }

    [Theory]
    [InlineData("8080:80", true)]
    [InlineData("80", true)]
    [InlineData("53:53/udp", true)]
    [InlineData("127.0.0.1:8080:80", true)]
    [InlineData("99999:80", false)]
    [InlineData("web", false)]
    public void Checks_ports(string port, bool ok) => Assert.Equal(ok, ComposeEditor.ValidatePort(port) == null);

    [Theory]
    [InlineData("nginx", true)]
    [InlineData("nginx:1.27", true)]
    [InlineData("registry.example.com:5000/team/app:2.1-rc1", true)]
    [InlineData("ghcr.io/owner/app@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", true)]
    [InlineData("${REGISTRY}/app:${TAG:-latest}", true)]
    [InlineData("{a: b}", false)]
    [InlineData("*img", false)]
    [InlineData("&a nginx", false)]
    [InlineData("!!binary x", false)]
    [InlineData("|", false)]
    [InlineData("nginx # latest", false)]
    [InlineData("Nginx", false)]
    public void Checks_images(string image, bool ok) => Assert.Equal(ok, ComposeEditor.ValidateImage(image) == null);

    [Fact]
    public void Env_values_with_control_characters_are_refused()
    {
        Assert.NotNull(ComposeEditor.ValidateEnv([new EnvVar("A", "x\ry")]));
        Assert.NotNull(ComposeEditor.ValidateEnv([new EnvVar("A", "x\u001by")]));
        Assert.Null(ComposeEditor.ValidateEnv([new EnvVar("A", "pa$$w \"quoted\" \\ ok")]));
    }

    private static readonly string Anchors = """
        x-env: &env
          MODE: prod
        x-ports: &ports
          - "80:80"
        x-labels: &lbl
          team: web
        x-image: &img nginx:1.27
        services:
          web:
            image: *img
            environment: *env
            ports: *ports
            labels: *lbl
            volumes:
              - ./files/nginx.conf:/etc/nginx/nginx.conf
          api:
            environment: &apienv
              A: "1"
          worker:
            <<: &base
              image: app:1
            environment: *apienv

        """.ReplaceLineEndings("\n");

    [Fact]
    public void A_value_that_is_a_yaml_alias_or_anchor_is_left_to_a_person()
    {
        string Refused(Func<string, string> edit)
        {
            Assert.False(ComposeEditor.TryEdit(Anchors, edit, out _, out var error));
            return error;
        }
        Assert.Contains("alias", Refused(y => ComposeEditor.SetEnvironment(y, "web", [new EnvVar("MODE", "dev")])));
        Assert.Contains("alias", Refused(y => ComposeEditor.SetPorts(y, "web", ["8080:80"])));
        Assert.Contains("alias", Refused(y => ComposeEditor.SetImage(y, "web", "nginx:1.28")));
        Assert.Contains("alias", Refused(y => ComposeEditor.SetImageTag(y, "web", "1.28")));
        Assert.Contains("alias", Refused(y => ComposeEditor.SetLabel(y, "web", "a", "b")));
        // the anchor is shared: worker's environment would change along
        Assert.Contains("anchor", Refused(y => ComposeEditor.SetEnvironment(y, "api", [new EnvVar("A", "2")])));
        // a key the merge brings would be replaced as a whole by a new one
        Assert.Contains("merge", Refused(y => ComposeEditor.SetImage(y, "worker", "app:2")));
        // what does not touch an alias still goes
        Assert.True(ComposeEditor.TryEdit(Anchors, y => ComposeEditor.SetRestart(y, "web", "always", 0), out var ok, out _));
        Assert.Contains("    restart: always\n  api:", ok);
    }

    [Fact]
    public void An_edit_that_breaks_the_file_or_its_services_is_refused()
    {
        Assert.NotNull(ComposeEditor.Broken(Yaml, Yaml + "services:\n  x: {}\n"));
        Assert.NotNull(ComposeEditor.Broken(Yaml, Yaml.Replace("  db:", "  other:")));
        Assert.Null(ComposeEditor.Broken(Yaml, Yaml.Replace("  db:", "  other:"), adds: "other", removes: "db"));
        Assert.False(ComposeEditor.TryEdit(Yaml, y => y + "  oops: [\n", out _, out _));
    }
}
