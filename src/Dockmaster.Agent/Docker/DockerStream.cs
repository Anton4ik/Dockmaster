using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace Dockmaster.Agent.Docker;

/// <summary>
/// The output of a container or an exec (logs, attach, exec start) and, when the connection was taken over
/// (hijacked), its stdin. Without a TTY Docker multiplexes stdout and stderr into one stream of frames — an 8-byte
/// header (stream: 1 stdout, 2 stderr; then the length, big-endian) before each chunk; with a TTY it is the raw bytes.
/// </summary>
public sealed class DockerStream : IDisposable
{
    public enum Target { StandardIn = 0, StandardOut = 1, StandardError = 2 }

    public readonly record struct ReadResult(int Count, Target Target, bool EOF);

    private readonly Stream _stream;
    private readonly Socket? _socket;
    private readonly bool _multiplexed;
    private readonly byte[] _header = new byte[8];
    // what is left of the current frame
    private int _remaining;
    private Target _target = Target.StandardOut;

    /// <param name="socket">the socket under a hijacked unix / tcp connection, to close the writing half (stdin's end)</param>
    public DockerStream(Stream stream, bool multiplexed, Socket? socket = null)
    {
        _stream = stream;
        _multiplexed = multiplexed;
        _socket = socket;
    }

    /// <summary>The next bytes of output and whose they are; EOF when the stream ended.</summary>
    public async Task<ReadResult> ReadOutputAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        if (!_multiplexed)
        {
            var n = await _stream.ReadAsync(buffer.AsMemory(offset, count), ct);
            return new ReadResult(n, Target.StandardOut, n == 0);
        }
        while (_remaining == 0)
        {
            if (!await FillAsync(_header, ct)) return new ReadResult(0, _target, true);
            _target = (Target)_header[0];
            _remaining = (int)BinaryPrimitives.ReadUInt32BigEndian(_header.AsSpan(4));
        }
        var read = await _stream.ReadAsync(buffer.AsMemory(offset, Math.Min(count, _remaining)), ct);
        if (read == 0) return new ReadResult(0, _target, true);
        _remaining -= read;
        return new ReadResult(read, _target, false);
    }

    /// <summary>
    /// The output as text, a piece at a time as it comes (stdout and stderr together), until EOF or cancel; a
    /// multi-byte character that a read cut in half is kept for the next piece.
    /// </summary>
    public async Task ReadTextAsync(Func<string, Task> onText, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        var decoder = Encoding.UTF8.GetDecoder();
        while (true)
        {
            var r = await ReadOutputAsync(buffer, 0, buffer.Length, ct);
            if (r.EOF) break;
            var n = decoder.GetChars(buffer, 0, r.Count, chars, 0);
            if (n > 0) await onText(new string(chars, 0, n));
        }
    }

    /// <summary>Reads the whole output: stdout and stderr apart.</summary>
    public async Task<(string stdout, string stderr)> ReadOutputToEndAsync(CancellationToken ct)
    {
        var stdout = new MemoryStream();
        var stderr = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var r = await ReadOutputAsync(buffer, 0, buffer.Length, ct);
            if (r.EOF) break;
            (r.Target == Target.StandardError ? stderr : stdout).Write(buffer, 0, r.Count);
        }
        return (Encoding.UTF8.GetString(stdout.GetBuffer(), 0, (int)stdout.Length), Encoding.UTF8.GetString(stderr.GetBuffer(), 0, (int)stderr.Length));
    }

    /// <summary>Into the container's stdin (an attach or exec with stdin).</summary>
    public async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        await _stream.WriteAsync(buffer.AsMemory(offset, count), ct);
        await _stream.FlushAsync(ct);
    }

    /// <summary>The end of stdin: the container reads EOF, the output keeps coming until it is done.</summary>
    public void CloseWrite()
    {
        if (_socket != null) _socket.Shutdown(SocketShutdown.Send);
        else throw new NotSupportedException("this connection cannot close only its writing half");
    }

    private async Task<bool> FillAsync(byte[] buf, CancellationToken ct)
    {
        var got = 0;
        while (got < buf.Length)
        {
            var n = await _stream.ReadAsync(buf.AsMemory(got), ct);
            if (n == 0) return false;
            got += n;
        }
        return true;
    }

    public void Dispose()
    {
        _stream.Dispose();
        _socket?.Dispose();
    }
}
