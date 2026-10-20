namespace Dockmaster.Agent;

/// <summary>
/// A wait between rounds of periodic work whose length the server sets while the work runs (see Pace in the
/// protocol). Shortening it ends the current wait, so a page that opens gets fresh data at once; lengthening it
/// takes effect after the current wait.
/// </summary>
public sealed class Pacing(TimeSpan initial = default)
{
    private readonly object _lock = new();
    private TimeSpan _every = initial;
    private CancellationTokenSource? _waiting;

    public TimeSpan Every { get { lock (_lock) return _every; } }

    public void Set(TimeSpan every)
    {
        CancellationTokenSource? cut = null;
        lock (_lock)
        {
            if (every == _every) return;
            if (every < _every) cut = _waiting;
            _every = every;
        }
        // a shorter pace: the wait going on ends now (the next one has the new length)
        try { cut?.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Waits the current length, or less when the pace is shortened meanwhile; throws only for <paramref name="ct"/>.</summary>
    public async Task WaitAsync(CancellationToken ct)
    {
        TimeSpan every;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_lock)
        {
            every = _every;
            _waiting = cts;
        }
        try { await Task.Delay(every, cts.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        finally
        {
            lock (_lock) if (ReferenceEquals(_waiting, cts)) _waiting = null;
            cts.Dispose();
        }
    }
}
