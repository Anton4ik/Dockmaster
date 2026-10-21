using System.Threading.RateLimiting;

namespace Dockmaster.Server.Access;

/// <summary>
/// Failed sign-ins per address and name: past <see cref="Failures"/> in <see cref="Window"/> that name is refused from
/// that address until the window ends. Only failures count, so people signing in often are never slowed down.
/// </summary>
public sealed class LoginThrottle : IDisposable
{
    public const int Failures = 10;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    private readonly PartitionedRateLimiter<string> _failed = PartitionedRateLimiter.Create<string, string>(key =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions { PermitLimit = Failures, Window = Window, QueueLimit = 0 }));

    private static string Key(HttpContext ctx, string name) => $"{ctx.Connection.RemoteIpAddress}|{name.ToLowerInvariant()}";

    /// <summary>True when this address used up its failures for this name.</summary>
    public bool Blocked(HttpContext ctx, string name)
    {
        // asking for no permit only tells whether any are left
        using var lease = _failed.AttemptAcquire(Key(ctx, name), 0);
        return !lease.IsAcquired;
    }

    public void Failed(HttpContext ctx, string name) => _failed.AttemptAcquire(Key(ctx, name)).Dispose();

    public void Dispose() => _failed.Dispose();
}
