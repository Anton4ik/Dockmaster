using Dockmaster.Server.Services;

namespace Dockmaster.Tests;

public class ComposeFilesTests
{
    private const string Yaml = "services:\n  web:\n    image: nginx\n    volumes:\n      - ./files/nginx.conf:/etc/nginx/nginx.conf:ro\n  api:\n    image: api\n    labels: [\"traefik.enable=true\"]\n    volumes:\n      - ./files/app.json:/app/app.json\n  db:\n    image: postgres\n";

    [Fact]
    public void A_service_that_mounts_a_file_carries_a_label_made_of_its_content()
    {
        var files = new Dictionary<string, string> { ["nginx.conf"] = "worker_processes 1;", ["app.json"] = "{}" };
        var a = ComposeFiles.Stamp(Yaml, "p", files);
        var b = ComposeFiles.Stamp(Yaml, "p", new Dictionary<string, string>(files) { ["nginx.conf"] = "worker_processes 2;" });
        Assert.Matches("web:\n    image: nginx\n(.|\n)*labels:\n      dockmaster.files: \"[0-9a-f]{16}\"", a);
        Assert.Contains("\"traefik.enable=true\"", a);
        Assert.Matches("\"dockmaster.files=[0-9a-f]{16}\"", a);
        Assert.DoesNotContain("db:\n    image: postgres\n    labels", a);
        // another nginx.conf, another label for web only
        Assert.NotEqual(a, b);
        Assert.Equal(a.Split("  api:")[1], b.Split("  api:")[1]);
        // stamping again keeps one label
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(ComposeFiles.Stamp(a, "p", files), "dockmaster.files=").Cast<object>());
    }

    [Fact]
    public void Labels_written_as_an_alias_are_not_stamped()
    {
        var yaml = "x-labels: &lbl\n  team: web\nservices:\n  web:\n    image: nginx\n    labels: *lbl\n    volumes:\n      - ./files/nginx.conf:/etc/nginx/nginx.conf\n";
        var warnings = new List<string>();
        Assert.Equal(yaml, ComposeFiles.Stamp(yaml, "p", new Dictionary<string, string> { ["nginx.conf"] = "x" }, warnings.Add));
        Assert.Contains("alias", Assert.Single(warnings));
    }

    [Fact]
    public void A_mounted_file_the_project_does_not_have_is_flagged()
    {
        var services = ComposeServices.Parse(Yaml, "p");
        Assert.Contains("mounts ./files/app.json", Assert.Single(ComposeFiles.Missing(services, ["nginx.conf"])));
        Assert.Empty(ComposeFiles.Missing(services, ["nginx.conf", "app.json"]));
    }
}
