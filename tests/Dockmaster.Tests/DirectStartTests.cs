using Dockmaster.Agent.Docker;

namespace Dockmaster.Tests;

public class DirectStartTests
{
    private const string Compose = """
        services:
          db:
            image: postgres
          cache:
            image: redis
          migrate:
            image: app
          job:
            image: app
            depends_on:
              db: {condition: service_healthy}
              cache:
                condition: service_started
                restart: true
          report:
            image: app
            depends_on: [cache]
            network_mode: service:db
            volumes_from: ["migrate:ro", "container:other"]
            links: ["db:database"]
          after:
            image: app
            depends_on:
              migrate: {condition: service_completed_successfully}
          alone:
            image: app
        """;

    private static ContainerListResponse C(string service, string state, string status, string oneoff = "False", int n = 1) => new()
    {
        ID = $"{service}-{n}-id",
        Names = [$"/app-{service}-{n}"],
        State = state,
        Status = status,
        Labels = new() { [DockerService.ProjectLabel] = "app", [DockerService.ServiceLabel] = service, ["com.docker.compose.oneoff"] = oneoff },
    };

    private static List<(string, string)>? Start(ContainerListResponse[] project, string service) =>
        Deployer.DirectStart(project, [service], s => ComposeSpec.Dependencies(Compose, s));

    [Fact]
    public void Dependencies_are_read_from_the_compose_file_with_what_compose_adds_by_itself()
    {
        Assert.Equal(new Dictionary<string, string> { ["db"] = "service_healthy", ["cache"] = "service_started" }, ComposeSpec.Dependencies(Compose, "job"));
        Assert.Equal(new Dictionary<string, string> { ["cache"] = "service_started", ["db"] = "service_started", ["migrate"] = "service_started" },
            ComposeSpec.Dependencies(Compose, "report"));
        Assert.Empty(ComposeSpec.Dependencies(Compose, "alone")!);
    }

    [Theory]
    [InlineData("services:\n  job:\n    extends: {file: base.yml, service: job}")]
    [InlineData("include: [other.yml]\nservices:\n  job:\n    image: app")]
    [InlineData("services:\n  job:\n    depends_on: [\"${DB}\"]")]
    [InlineData("services:\n  other:\n    image: app")]
    [InlineData("services: [")]
    public void Dependencies_the_file_cannot_tell_for_sure_are_unknown(string yaml) => Assert.Null(ComposeSpec.Dependencies(yaml, "job"));

    [Fact]
    public void A_job_whose_dependencies_are_ready_is_started_directly_every_replica()
    {
        var project = new[]
        {
            C("db", "running", "Up 3 hours (healthy)"),
            C("cache", "running", "Up 3 hours"),
            C("migrate", "exited", "Exited (0) 3 hours ago"),
            C("job", "exited", "Exited (0) 30 minutes ago"),
            C("job", "created", "Created", n: 2),
            // a `compose run` of it is not one of its containers
            C("job", "exited", "Exited (0) 2 days ago", oneoff: "True", n: 3),
            C("after", "exited", "Exited (0) 1 hour ago"),
        };
        Assert.Equal([("job-1-id", "app-job-1"), ("job-2-id", "app-job-2")], Start(project, "job"));
        Assert.Equal([("after-1-id", "app-after-1")], Start(project, "after"));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("starting")]
    [InlineData("unhealthy")]
    [InlineData("stopped")]
    [InlineData("failed")]
    public void Compose_does_it_when_it_would_do_more_or_say_more(string what)
    {
        var project = what switch
        {
            "missing" => new[] { C("db", "running", "Up 3 hours (healthy)"), C("cache", "running", "Up 3 hours") },
            "starting" => [C("db", "running", "Up 1 minute (health: starting)"), C("cache", "running", "Up 3 hours"), C("job", "exited", "Exited (0)")],
            "unhealthy" => [C("db", "running", "Up 3 hours (unhealthy)"), C("cache", "running", "Up 3 hours"), C("job", "exited", "Exited (0)")],
            "stopped" => [C("db", "running", "Up 3 hours (healthy)"), C("cache", "exited", "Exited (0)"), C("job", "exited", "Exited (0)")],
            _ => [C("migrate", "exited", "Exited (1) 1 hour ago"), C("after", "exited", "Exited (0)")],
        };
        Assert.Null(Start(project, what == "failed" ? "after" : "job"));
    }
}
