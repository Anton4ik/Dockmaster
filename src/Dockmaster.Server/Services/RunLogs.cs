using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;

namespace Dockmaster.Server.Services;

/// <summary>
/// The output of the project runs going on now (deploys, start/stop…), by deployment id, so a page that was left or a
/// second browser can pick up the log where it is and follow it to the end. A finished run is in the database.
/// </summary>
public sealed class RunLogs
{
    private sealed class Live
    {
        public readonly StringBuilder Text = new();
        public readonly List<Channel<string>> Watchers = [];
        public readonly TaskCompletionSource<(bool Ok, string Message)> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>A run being watched: what it printed so far, the lines still to come (ends with the run), and its outcome.</summary>
    public sealed record Watcher(string SoFar, ChannelReader<string> Next, Task<(bool Ok, string Message)> Result, Action Stop);

    private readonly ConcurrentDictionary<int, Live> _runs = new();

    public void Start(int deploymentId) => _runs[deploymentId] = new Live();

    public void Line(int deploymentId, string line)
    {
        if (!_runs.TryGetValue(deploymentId, out var run)) return;
        lock (run)
        {
            run.Text.AppendLine(line);
            foreach (var w in run.Watchers) w.Writer.TryWrite(line);
        }
    }

    public void End(int deploymentId, bool ok, string message)
    {
        if (!_runs.TryGetValue(deploymentId, out var run)) return;
        lock (run)
        {
            run.Result.TrySetResult((ok, message));
            foreach (var w in run.Watchers) w.Writer.TryComplete();
            run.Watchers.Clear();
        }
        _runs.TryRemove(deploymentId, out _);
    }

    /// <summary>Null when the run is not going on (any more): its log is in the database then.</summary>
    public Watcher? Watch(int deploymentId)
    {
        if (!_runs.TryGetValue(deploymentId, out var run)) return null;
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        lock (run)
        {
            // it may have ended between the lookup and the lock: then there is nothing more to wait for
            if (run.Result.Task.IsCompleted) channel.Writer.TryComplete();
            else run.Watchers.Add(channel);
            return new Watcher(run.Text.ToString(), channel.Reader, run.Result.Task, () => { lock (run) run.Watchers.Remove(channel); });
        }
    }
}
