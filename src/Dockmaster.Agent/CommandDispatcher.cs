using Dockmaster.Agent.Docker;
using Dockmaster.Protocol;

namespace Dockmaster.Agent;

/// <summary>Runs one command from the server on the service that handles it and puts the answer into an <see cref="Outcome"/>.</summary>
public sealed class CommandDispatcher
{
    private readonly DockerService _docker;
    private readonly Deployer _deployer;
    private readonly ProjectImporter _importer;
    private readonly AgentUpdater _updater;
    private readonly ContainerRecreator _recreator;
    private readonly ImageStore _images;
    private readonly NetworkStore _networks;
    private readonly VolumeStore _volumes;
    private readonly ExecStore _exec;
    private readonly VolumeTransfer _transfer;
    private readonly ProjectLocks _locks;
    private readonly MeshManager _mesh;
    private readonly ILogger<CommandDispatcher> _log;

    public CommandDispatcher(DockerService docker, Deployer deployer, ProjectImporter importer, AgentUpdater updater, ContainerRecreator recreator,
        ImageStore images, NetworkStore networks, VolumeStore volumes, ExecStore exec, VolumeTransfer transfer, ProjectLocks locks, MeshManager mesh, ILogger<CommandDispatcher> log)
    {
        _docker = docker;
        _deployer = deployer;
        _importer = importer;
        _updater = updater;
        _recreator = recreator;
        _images = images;
        _networks = networks;
        _volumes = volumes;
        _exec = exec;
        _transfer = transfer;
        _locks = locks;
        _mesh = mesh;
        _log = log;
    }

    /// <summary>Keys and window sizes for a running terminal.</summary>
    public Task ExecInputAsync(ExecInput input) => _exec.InputAsync(input);

    /// <summary>Bytes for a running volume import.</summary>
    public Task DataAsync(DataChunk chunk) => _transfer.DataAsync(chunk);

    /// <summary>Chunks of a running volume export the server has passed on.</summary>
    public void DataAck(DataAck ack) => _transfer.Ack(ack);

    /// <summary>The answer to a command: what goes into its CommandResult.</summary>
    public sealed record Outcome(bool Ok, string Message, string Payload = "");

    /// <summary>Reads that only matter to whoever asked; everything else changes the host and must not stop half way.</summary>
    public static bool EndsWithSession(Command cmd) =>
        cmd.BodyCase is Command.BodyOneofCase.ContainerLogs or Command.BodyOneofCase.ListImages or Command.BodyOneofCase.InspectContainer or Command.BodyOneofCase.ListNetworks or Command.BodyOneofCase.ListVolumes or Command.BodyOneofCase.StartExec or Command.BodyOneofCase.ExportVolume or Command.BodyOneofCase.ImportVolume or Command.BodyOneofCase.BrowseVolume or Command.BodyOneofCase.Ping or Command.BodyOneofCase.MeshBench or Command.BodyOneofCase.MeshTune;

    public async Task<Outcome> DispatchAsync(Command cmd, Func<AgentMessage, Task> send, CancellationToken ct)
    {
        // best effort: a dead connection must not fail the command it reports on
        async Task Progress(string line)
        {
            try { await send(new AgentMessage { Progress = new CommandProgress { CommandId = cmd.Id, Line = line } }); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { _log.LogDebug("progress of {Command} not delivered: {Line} ({Error})", cmd.Id, line, ex.Message); }
        }
        static Outcome From((bool ok, string message) r) => new(r.ok, r.message);
        switch (cmd.BodyCase)
        {
            case Command.BodyOneofCase.Ping:
                return new Outcome(true, "pong");
            case Command.BodyOneofCase.Deploy:
                return From(await _deployer.DeployAsync(cmd.Deploy, Progress, ct));
            case Command.BodyOneofCase.ProjectAction:
                return From(await _deployer.ProjectActionAsync(cmd.ProjectAction, Progress, ct));
            case Command.BodyOneofCase.ImportProject:
                return new Outcome(true, "imported", ProjectImporter.ToPayload(await _importer.ImportAsync(cmd.ImportProject.Project, Progress, ct)));
            case Command.BodyOneofCase.RecreateContainer:
                return From(await _recreator.RecreateAsync(cmd.RecreateContainer, Progress, ct));
            case Command.BodyOneofCase.PruneImages:
                // all unused images: never while a deploy has pulled an image it has not started yet
                return new Outcome(true, cmd.PruneImages.AllUnused
                    ? await _locks.ExclusiveAsync(() => _images.PruneAsync(true, ct), ct)
                    : await _images.PruneAsync(false, ct));
            case Command.BodyOneofCase.ContainerNetwork:
                return new Outcome(true, await _networks.ContainerNetworkAsync(cmd.ContainerNetwork.Container, cmd.ContainerNetwork.Network, cmd.ContainerNetwork.Connect, ct));
            case Command.BodyOneofCase.ListNetworks:
                return new Outcome(true, "networks", await _networks.ListJsonAsync(ct));
            case Command.BodyOneofCase.ExportVolume:
                return new Outcome(true, await _transfer.ExportAsync(cmd.Id, cmd.ExportVolume, send, ct));
            case Command.BodyOneofCase.ImportVolume:
                return new Outcome(true, await _transfer.ImportAsync(cmd.Id, cmd.ImportVolume, send, ct));
            case Command.BodyOneofCase.BrowseVolume:
                return new Outcome(true, "entries", await _transfer.BrowseAsync(cmd.BrowseVolume, ct));
            case Command.BodyOneofCase.StartExec:
                return new Outcome(true, await _exec.RunAsync(cmd.Id, cmd.StartExec, send, ct));
            case Command.BodyOneofCase.CreateNetwork:
                return new Outcome(true, await _networks.CreateAsync(cmd.CreateNetwork, ct));
            case Command.BodyOneofCase.RemoveNetworks:
                return new Outcome(true, "removed", await _networks.RemoveJsonAsync(cmd.RemoveNetworks.Names, ct));
            case Command.BodyOneofCase.ListVolumes:
                return new Outcome(true, "volumes", await _volumes.ListJsonAsync(cmd.ListVolumes, ct));
            case Command.BodyOneofCase.CreateVolume:
                return new Outcome(true, await _volumes.CreateAsync(cmd.CreateVolume, Progress, ct));
            case Command.BodyOneofCase.RemoveVolumes:
                return new Outcome(true, "removed", await _volumes.RemoveJsonAsync(cmd.RemoveVolumes.Names, ct));
            case Command.BodyOneofCase.InspectContainer:
                return new Outcome(true, "inspect", await _docker.InspectJsonAsync(cmd.InspectContainer.Container, ct));
            case Command.BodyOneofCase.ListImages:
                return new Outcome(true, "images", await _images.ListJsonAsync(ct));
            case Command.BodyOneofCase.RemoveImages:
                return new Outcome(true, "removed", await _images.RemoveJsonAsync(cmd.RemoveImages.Ids, ct));
            case Command.BodyOneofCase.SelfUpdate:
                return new Outcome(true, await _updater.UpdateAsync(Progress, ct));
            case Command.BodyOneofCase.MeshJoin:
                return new Outcome(true, "ready to join", await _mesh.JoinAsync(cmd.MeshJoin, Progress, ct));
            case Command.BodyOneofCase.MeshApply:
                return new Outcome(true, await _mesh.ApplyAsync(cmd.MeshApply, Progress, ct));
            case Command.BodyOneofCase.MeshLeave:
                return new Outcome(true, await _mesh.LeaveAsync(Progress, ct));
            case Command.BodyOneofCase.MeshBench:
                return new Outcome(true, cmd.MeshBench.Server ? "served" : "measured", await _mesh.BenchAsync(cmd.MeshBench, ct));
            case Command.BodyOneofCase.MeshTune:
                return new Outcome(true, "measured", await _mesh.TuneAsync(ct));
            case Command.BodyOneofCase.ContainerUpdate:
                await _docker.UpdateResourcesAsync(cmd.ContainerUpdate.ContainerId, cmd.ContainerUpdate.Memory, cmd.ContainerUpdate.Cpus, ct);
                return new Outcome(true, "limits updated");
            case Command.BodyOneofCase.ContainerRestartPolicy:
            {
                var r = cmd.ContainerRestartPolicy;
                return new Outcome(true, await _docker.UpdateRestartPolicyAsync(r.ContainerId, r.Policy, r.MaxRetries, ct));
            }
            case Command.BodyOneofCase.ContainerAction:
                await _docker.ContainerActionAsync(cmd.ContainerAction.ContainerId, cmd.ContainerAction.Op, ct);
                return new Outcome(true, "done");
            case Command.BodyOneofCase.ContainerLogs:
            {
                var l = cmd.ContainerLogs;
                await _docker.StreamLogsAsync(l.ContainerId, l.Tail, l.Follow, l.Since,
                    text => send(new AgentMessage { Logs = new LogChunk { CommandId = cmd.Id, ContainerId = l.ContainerId, Text = text } }), ct);
                await send(new AgentMessage { Logs = new LogChunk { CommandId = cmd.Id, ContainerId = l.ContainerId, Eof = true } });
                return new Outcome(true, "eof");
            }
            default:
                return new Outcome(false, WireText.UnknownCommand);
        }
    }
}
