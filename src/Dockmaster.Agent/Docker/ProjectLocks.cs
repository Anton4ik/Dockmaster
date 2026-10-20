using System.Collections.Concurrent;

namespace Dockmaster.Agent.Docker;

/// <summary>
/// Coordinates work on the host: deploys and actions of one project run one after another, different projects in
/// parallel. Host-wide work that must not overlap any of them (removing all unused images, which would take an image
/// a running deploy just pulled) waits until no project is busy and holds new ones back meanwhile.
/// </summary>
public sealed class ProjectLocks
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _projects = new();
    private readonly SemaphoreSlim _hostWide = new(1, 1);
    private readonly object _gate = new();
    private int _busy;
    private TaskCompletionSource _idle = Idle();

    private static TaskCompletionSource Idle()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult();
        return tcs;
    }

    public async Task<IDisposable> EnterAsync(string project, Func<string, Task> progress, CancellationToken ct)
    {
        // host-wide work in progress: wait for it, then count this project as busy
        await _hostWide.WaitAsync(ct);
        lock (_gate)
        {
            if (_busy++ == 0) _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        _hostWide.Release();

        var sem = _projects.GetOrAdd(project, _ => new SemaphoreSlim(1, 1));
        try
        {
            if (!sem.Wait(0))
            {
                await progress($"waiting for another deploy or action of {project} to finish…");
                await sem.WaitAsync(ct);
            }
        }
        catch
        {
            Leave();
            throw;
        }
        return new Releaser(() => { sem.Release(); Leave(); });
    }

    /// <summary>Runs work that must not overlap any project work.</summary>
    public async Task<T> ExclusiveAsync<T>(Func<Task<T>> work, CancellationToken ct)
    {
        await _hostWide.WaitAsync(ct);
        try
        {
            Task idle;
            lock (_gate) idle = _idle.Task;
            await idle.WaitAsync(ct);
            return await work();
        }
        finally { _hostWide.Release(); }
    }

    private void Leave()
    {
        lock (_gate)
        {
            if (--_busy == 0) _idle.TrySetResult();
        }
    }

    private sealed class Releaser(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
