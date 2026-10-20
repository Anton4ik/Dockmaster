using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Dockmaster.Agent;
using Dockmaster.Agent.Docker;

namespace Dockmaster.Tests;

public class ProjectFilesTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "dm-files-" + Guid.NewGuid().ToString("N"));
    private readonly ComposeRunner _compose;

    public ProjectFilesTests() => _compose = new ComposeRunner(Options.Create(new AgentOptions { DataDir = _dataDir }), NullLogger<ComposeRunner>.Instance);

    public void Dispose() { if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, true); }

    [Fact]
    public void Secrets_are_variables_of_the_compose_file_too()
    {
        _compose.PrepareProject("shop", "services: {}", "", new Dictionary<string, string> { ["Db_Password"] = "p@ss $word;x=1", ["not-a-variable"] = "y" });

        var vars = ComposeRunner.SecretVariables(_compose.ProjectDir("shop")).ToList();

        Assert.Equal([("Db_Password", "p@ss $word;x=1")], vars);
    }

    [Fact]
    public void A_secret_cannot_point_compose_at_another_daemon()
    {
        _compose.PrepareProject("shop", "services: {}", "", new Dictionary<string, string>
        {
            ["DOCKER_HOST"] = "tcp://attacker:2375", ["docker_config"] = "/tmp/x", ["PATH"] = "/tmp/evil", ["HTTPS_PROXY"] = "http://attacker:3128", ["Db_Password"] = "p",
        });

        Assert.Equal([("Db_Password", "p")], ComposeRunner.SecretVariables(_compose.ProjectDir("shop")).ToList());
        var psi = _compose.StartInfo("docker", ["compose", "-f", Path.Combine(_compose.ProjectDir("shop"), "docker-compose.yml"), "-p", "shop", "up"]);
        Assert.Equal("unix:///var/run/docker.sock", psi.Environment["DOCKER_HOST"]);
        Assert.Equal("p", psi.Environment["Db_Password"]);
        Assert.NotEqual("/tmp/evil", psi.Environment["PATH"]);
        Assert.False(psi.Environment.ContainsKey("docker_config"));
    }

    [Fact]
    public void The_server_refuses_secrets_compose_reads_itself()
    {
        Assert.NotNull(Dockmaster.Server.Services.ProjectService.ValidateSecretName("DOCKER_HOST"));
        Assert.NotNull(Dockmaster.Server.Services.ProjectService.ValidateSecretName("https_proxy"));
        Assert.NotNull(Dockmaster.Server.Services.ProjectService.ValidateSecretName("LD_PRELOAD"));
        Assert.Null(Dockmaster.Server.Services.ProjectService.ValidateSecretName("Db_Password"));
        Assert.Null(Dockmaster.Server.Services.ProjectService.ValidateSecretName("DOCKERHUB_TOKEN"));
    }

    [Fact]
    public void A_deploy_knows_the_secrets_used_as_variables_that_the_env_does_not_set()
    {
        const string compose = "services:\n  api:\n    environment:\n      A: ${Db_Password}\n      B: ${Api_Key:-none}\n      C: $Token\n      D: ${InEnv}\n";
        var used = Dockmaster.Server.Services.ProjectService.SecretVariablesUsed(compose, ["Db_Password", "Api_Key", "Token", "Unused", "InEnv", "Db"], "InEnv=1\n");
        Assert.Equal(["Api_Key", "Db_Password", "Token"], used);
    }

    [Fact]
    public void Down_keeps_the_data_of_relative_bind_mounts()
    {
        _compose.PrepareProject("shop", "services: {}", "A=1", new Dictionary<string, string> { ["db_password"] = "x" });
        var dir = _compose.ProjectDir("shop");
        Directory.CreateDirectory(Path.Combine(dir, "data"));
        File.WriteAllText(Path.Combine(dir, "data", "ibdata1"), "rows");

        var kept = _compose.RemoveProjectFiles("shop");

        Assert.Contains("data", kept);
        Assert.True(File.Exists(Path.Combine(dir, "data", "ibdata1")));
        Assert.False(File.Exists(Path.Combine(dir, "docker-compose.yml")));
        Assert.False(File.Exists(Path.Combine(dir, ".env")));
        Assert.False(Directory.Exists(Path.Combine(dir, "secrets")));
    }

    [Fact]
    public void Project_files_are_written_and_the_ones_taken_out_removed()
    {
        _compose.PrepareProject("web", "services: {}", "", new Dictionary<string, string>(), new Dictionary<string, string> { ["nginx.conf"] = "a", ["old.conf"] = "b" });
        _compose.PrepareProject("web", "services: {}", "", new Dictionary<string, string>(), new Dictionary<string, string> { ["nginx.conf"] = "c" });
        var dir = Path.Combine(_compose.ProjectDir("web"), "files");
        Assert.Equal(["nginx.conf"], Directory.GetFiles(dir).Select(Path.GetFileName));
        Assert.Equal("c", File.ReadAllText(Path.Combine(dir, "nginx.conf")));
        Assert.Null(_compose.RemoveProjectFiles("web"));
    }

    [Fact]
    public void A_persons_own_files_in_the_files_folder_are_never_removed()
    {
        var dir = Path.Combine(_compose.ProjectDir("web"), "files");
        Directory.CreateDirectory(Path.Combine(dir, "uploads"));
        File.WriteAllText(Path.Combine(dir, "uploads", "a.jpg"), "img");
        File.WriteAllText(Path.Combine(dir, "notes.txt"), "mine");

        _compose.PrepareProject("web", "services: {}", "", new Dictionary<string, string>());
        _compose.PrepareProject("web", "services: {}", "", new Dictionary<string, string>(), new Dictionary<string, string> { ["nginx.conf"] = "a" });
        _compose.PrepareProject("web", "services: {}", "", new Dictionary<string, string>(), new Dictionary<string, string>());
        Assert.False(File.Exists(Path.Combine(dir, "nginx.conf")));
        Assert.True(File.Exists(Path.Combine(dir, "notes.txt")));

        _compose.PrepareProject("web", "services: {}", "", new Dictionary<string, string>(), new Dictionary<string, string> { ["nginx.conf"] = "a" });
        Assert.EndsWith(": files", _compose.RemoveProjectFiles("web"));
        Assert.Equal(["notes.txt"], Directory.GetFiles(dir).Select(Path.GetFileName));
        Assert.True(File.Exists(Path.Combine(dir, "uploads", "a.jpg")));
    }

    [Fact]
    public void Links_are_replaced_or_refused_never_written_through()
    {
        var outside = Path.Combine(_dataDir, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "target"), "host file");
        var dir = _compose.ProjectDir("web");
        Directory.CreateDirectory(Path.Combine(dir, "files"));
        try { File.CreateSymbolicLink(Path.Combine(dir, "files", "nginx.conf"), Path.Combine(outside, "target")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; } // no right to make links here (Windows without developer mode)

        _compose.PrepareProject("web", "services: {}", "", new Dictionary<string, string>(), new Dictionary<string, string> { ["nginx.conf"] = "a" });
        Assert.Equal("host file", File.ReadAllText(Path.Combine(outside, "target")));
        Assert.Null(new FileInfo(Path.Combine(dir, "files", "nginx.conf")).LinkTarget);

        Directory.Delete(Path.Combine(dir, "files"), true);
        Directory.CreateSymbolicLink(Path.Combine(dir, "files"), outside);
        Assert.Throws<InvalidOperationException>(() => _compose.PrepareProject("web", "services: {}", "", new Dictionary<string, string>(), new Dictionary<string, string> { ["target"] = "a" }));
        Assert.Equal("host file", File.ReadAllText(Path.Combine(outside, "target")));
        _compose.RemoveProjectFiles("web");
        Assert.True(File.Exists(Path.Combine(outside, "target")));
    }

    [Fact]
    public void Down_removes_the_folder_when_only_its_own_files_were_there()
    {
        _compose.PrepareProject("web", "services: {}", "", new Dictionary<string, string>());
        Assert.Null(_compose.RemoveProjectFiles("web"));
        Assert.False(Directory.Exists(_compose.ProjectDir("web")));
    }
}
