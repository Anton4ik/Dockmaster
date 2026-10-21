using Dockmaster.Agent.Docker;
using Dockmaster.Protocol;

namespace Dockmaster.IntegrationTests;

/// <summary>A job's run: its container started on the server's word, without a docker CLI child process where that is enough.</summary>
[Collection("docker")]
public sealed class JobTests(DockerFixture d)
{
    [DockerFact]
    public async Task A_job_runs_through_the_engine_and_through_compose_when_its_dependency_is_down()
    {
        var project = d.Prefix + "-job";
        var compose = $$"""
            services:
              db:
                image: {{DockerFixture.Image}}
                command: ["sleep", "3600"]
                healthcheck:
                  test: ["CMD", "true"]
                  interval: 30s
                  start_period: 30s
                  start_interval: 1s
              job:
                image: {{DockerFixture.Image}}
                command: ["echo", "ran"]
                restart: "no"
                depends_on:
                  db: {condition: service_healthy}
                x-dockmaster:
                  schedule: "*/30 * * * *"
            """;
        var deployer = d.Get<Deployer>();
        var log = new List<string>();
        Task Log(string l) { lock (log) log.Add(l); return Task.CompletedTask; }
        var job = project + "-job-1";
        async Task<int> Runs() => (await DockerFixture.Must("logs", job)).Split('\n').Count(l => l.Trim() == "ran");
        async Task RunAndWait(int runs)
        {
            log.Clear();
            var (ok, message) = await deployer.ProjectActionAsync(new ProjectAction { Project = project, Op = ProjectOp.ProjectStart, Services = { "job" } }, Log, default);
            Assert.True(ok, message + "\n" + string.Join('\n', log));
            await DockerFixture.Until(async () => await Runs() == runs, TimeSpan.FromSeconds(30), () => $"run {runs} of {job}");
        }
        try
        {
            var (ok, message) = await deployer.DeployAsync(new DeployProject { Project = project, ComposeYaml = compose, HealthTimeoutSeconds = 120 }, Log, default);
            Assert.True(ok, message + "\n" + string.Join('\n', log));
            Assert.Equal(0, await Runs());

            // the db is healthy: the engine starts it, compose has nothing to say ("Starting", "Waiting" are its lines)
            await RunAndWait(1);
            Assert.Equal([$" Container {job}  Started"], log);
            await RunAndWait(2);

            // the db is down: compose starts it first and waits for it
            await DockerFixture.Must("stop", project + "-db-1");
            await RunAndWait(3);
            Assert.Contains(log, l => l.Contains("Waiting") || l.Contains("Starting"));
            Assert.Equal("true", (await DockerFixture.Must("inspect", "-f", "{{.State.Running}}", project + "-db-1")).Trim());
        }
        finally
        {
            await deployer.ProjectActionAsync(new ProjectAction { Project = project, Op = ProjectOp.ProjectDown }, _ => Task.CompletedTask, default);
        }
    }
}
