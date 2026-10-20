using System.Buffers.Binary;
using System.Text;
using Dockmaster.Agent.Docker;

namespace Dockmaster.Tests;

public class DockerStreamTests
{
    /// <summary>A multiplexed frame as docker writes it: stream type, three zero bytes, big-endian length, the bytes.</summary>
    private static byte[] Frame(byte target, byte[] payload)
    {
        var frame = new byte[8 + payload.Length];
        frame[0] = target;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4), (uint)payload.Length);
        payload.CopyTo(frame, 8);
        return frame;
    }

    [Fact]
    public async Task Text_is_read_off_the_frames_with_a_character_cut_in_two_kept_whole()
    {
        var text = "привет, мир";
        var bytes = Encoding.UTF8.GetBytes(text);
        // the first frame ends in the middle of a two-byte character
        var frames = Frame(1, bytes[..3]).Concat(Frame(2, bytes[3..])).ToArray();
        using var stream = new DockerStream(new MemoryStream(frames), multiplexed: true);
        var pieces = new List<string>();
        await stream.ReadTextAsync(p => { pieces.Add(p); return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal(text, string.Concat(pieces));
        Assert.All(pieces, p => Assert.DoesNotContain('�', p));
    }

    [Fact]
    public async Task A_tty_stream_is_the_raw_bytes()
    {
        using var stream = new DockerStream(new MemoryStream(Encoding.UTF8.GetBytes("$ ls\n")), multiplexed: false);
        var pieces = new List<string>();
        await stream.ReadTextAsync(p => { pieces.Add(p); return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal("$ ls\n", string.Concat(pieces));
    }

    [Fact]
    public async Task Stdout_and_stderr_are_kept_apart_when_read_to_the_end()
    {
        var frames = Frame(1, "out"u8.ToArray()).Concat(Frame(2, "err"u8.ToArray())).Concat(Frame(1, "!"u8.ToArray())).ToArray();
        using var stream = new DockerStream(new MemoryStream(frames), multiplexed: true);
        var (stdout, stderr) = await stream.ReadOutputToEndAsync(CancellationToken.None);
        Assert.Equal("out!", stdout);
        Assert.Equal("err", stderr);
    }
}
