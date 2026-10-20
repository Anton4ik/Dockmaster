using Dockmaster.Agent.Docker;
using Dockmaster.Server.Services;

namespace Dockmaster.Tests;

public class ExecPidTests
{
    [Fact]
    public void Takes_the_pid_off_the_start()
    {
        Assert.True(ExecStore.TryTakePid("\u001b]7777;42\u0007# ", out var pid, out var rest));
        Assert.Equal(42, pid);
        Assert.Equal("# ", rest);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\u001b]77")]
    [InlineData("\u001b]7777;4")]
    public void Waits_while_the_pid_may_still_come(string head) => Assert.False(ExecStore.TryTakePid(head, out _, out _));

    [Fact]
    public void Leaves_other_output_as_it_is()
    {
        Assert.True(ExecStore.TryTakePid("root@box:/# ", out var pid, out var rest));
        Assert.Null(pid);
        Assert.Equal("root@box:/# ", rest);
    }
}

public sealed class RestoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dm-restore-" + Guid.NewGuid().ToString("N"));

    public RestoreTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    private void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(_dir, path))!);
        File.WriteAllText(Path.Combine(_dir, path), text);
    }

    private string Read(string path) => File.ReadAllText(Path.Combine(_dir, path));

    [Fact]
    public void Drops_a_restore_not_marked_ready()
    {
        Write("dockmaster.db", "current");
        Write("restore-pending/dockmaster.db", "half");
        BackupService.ApplyPendingRestore(_dir, _ => { });
        Assert.Equal("current", Read("dockmaster.db"));
        Assert.False(Directory.Exists(Path.Combine(_dir, "restore-pending")));
    }

    [Fact]
    public void Replaces_only_what_the_backup_has()
    {
        Write("dockmaster.db", "current");
        Write("dockmaster.db-wal", "wal");
        Write("agent-tls.pfx", "cert");
        Write("keys/key-1.xml", "key");
        Write("restore-pending/dockmaster.db", "restored");
        Write("restore-pending/READY", "");
        BackupService.ApplyPendingRestore(_dir, _ => { });
        Assert.Equal("restored", Read("dockmaster.db"));
        Assert.False(File.Exists(Path.Combine(_dir, "dockmaster.db-wal")));
        Assert.Equal("cert", Read("agent-tls.pfx"));
        Assert.Equal("key", Read("keys/key-1.xml"));
        Assert.False(File.Exists(Path.Combine(_dir, "READY")));
        Assert.False(Directory.Exists(Path.Combine(_dir, "restore-pending")));
    }

    [Fact]
    public void Keeps_the_last_three_folders_from_before()
    {
        foreach (var d in new[] { "20260101-000000", "20260102-000000", "20260103-000000", "20260104-000000" })
            Directory.CreateDirectory(Path.Combine(_dir, "before-restore-" + d));
        Write("restore-pending/dockmaster.db", "restored");
        Write("restore-pending/READY", "");
        BackupService.ApplyPendingRestore(_dir, _ => { });
        var left = Directory.GetDirectories(_dir, "before-restore-*").Select(Path.GetFileName).Order().ToList();
        Assert.Equal(3, left.Count);
        Assert.DoesNotContain("before-restore-20260101-000000", left);
        Assert.DoesNotContain("before-restore-20260102-000000", left);
    }
}
