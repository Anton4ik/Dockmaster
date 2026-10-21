using Dockmaster.Agent;
using Dockmaster.Agent.Docker;

// the agent's image also runs as a helper that lists a folder of a volume (see VolumeTransfer)
if (args is [VolumeTransfer.ListDirArg, var dir]) return VolumeTransfer.ListDir(dir);

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddEnvironmentVariables("DOCKMASTER_");
builder.Services.Configure<AgentOptions>(builder.Configuration);
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });

builder.Services.AddSingleton<DockerService>();
builder.Services.AddSingleton<HostStatsCollector>();
builder.Services.AddSingleton<ComposeRunner>();
builder.Services.AddSingleton<Deployer>();
builder.Services.AddSingleton<ProjectImporter>();
builder.Services.AddSingleton<AgentUpdater>();
builder.Services.AddSingleton<ContainerRecreator>();
builder.Services.AddSingleton<ImageStore>();
builder.Services.AddSingleton<NetworkStore>();
builder.Services.AddSingleton<VolumeStore>();
builder.Services.AddSingleton<ExecStore>();
builder.Services.AddSingleton<ExitWatcher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ExitWatcher>());
builder.Services.AddSingleton<VolumeTransfer>();
builder.Services.AddSingleton<ProjectLocks>();
builder.Services.AddSingleton<ImagePuller>();
builder.Services.AddSingleton<MeshManager>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MeshManager>());
builder.Services.AddSingleton<CommandDispatcher>();
builder.Services.AddHostedService<HubClient>();

var opts = builder.Configuration.Get<AgentOptions>() ?? new AgentOptions();
if (string.IsNullOrWhiteSpace(opts.ServerUrl) || string.IsNullOrWhiteSpace(opts.Token))
{
    Console.Error.WriteLine("DOCKMASTER_SERVERURL and DOCKMASTER_TOKEN are required.");
    return 2;
}

var host = builder.Build();
// helpers of transfers an earlier run did not finish (it stopped or crashed half way); before any new one starts
using (var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
    await host.Services.GetRequiredService<VolumeStore>().RemoveLeftoverHelpersAsync(cleanup.Token);
await host.RunAsync();
return 0;
