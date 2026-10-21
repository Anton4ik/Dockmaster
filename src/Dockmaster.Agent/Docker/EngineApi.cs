using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dockmaster.Agent.Docker;

/// <summary>
/// The agent's client of the Docker Engine API: HTTP over the engine's socket, answers read with System.Text.Json.
/// Requests share pooled connections; the ones that take a connection over (attach, exec with a terminal) open their
/// own and speak HTTP on it by hand up to the switch (see <see cref="HijackAsync"/>), like the docker CLI.
/// Replaces Docker.DotNet, which opened a new connection for every request (a heartbeat of 27 containers cost the agent
/// ~110 ms of CPU through it, ~27 ms this way), mapped answers onto models of an older API that silently dropped
/// what they did not know (a healthcheck's start_interval was lost on every rolling update), and did not resize a
/// terminal after it started.
/// </summary>
public sealed partial class EngineApi : IDisposable
{
    /// <summary>A request that is not a stream, a wait or a hijack gives up after this (Docker.DotNet's default too).</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(100);

    // Docker writes null for a number or flag it has no value for (a running exec's ExitCode): that reads as 0 / false
    internal static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, Converters = { new NullAsDefault<long>(), new NullAsDefault<bool>() } };

    private sealed class NullAsDefault<T> : System.Text.Json.Serialization.JsonConverter<T> where T : struct
    {
        public override bool HandleNull => true;

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.Null ? default : JsonSerializer.Deserialize<T>(ref reader);

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => JsonSerializer.Serialize(writer, value);
    }

    private readonly HttpClient _http;
    private readonly Func<CancellationToken, Task<(Stream stream, Socket? socket)>> _connect;
    private readonly string _host;

    public EngineApi(string endpoint)
    {
        var uri = new Uri(endpoint);
        switch (uri.Scheme)
        {
            case "unix":
                var path = uri.LocalPath;
                _connect = async ct =>
                {
                    var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    try { await s.ConnectAsync(new UnixDomainSocketEndPoint(path), ct); }
                    catch { s.Dispose(); throw; }
                    return (new NetworkStream(s, ownsSocket: true), s);
                };
                _host = "docker";
                break;
            case "npipe":
                // npipe://./pipe/docker_engine
                var segments = uri.Segments.Select(x => x.Trim('/')).Where(x => x.Length > 0).ToArray();
                var pipe = segments.Length > 1 && segments[0] == "pipe" ? string.Join('/', segments[1..]) : "docker_engine";
                var server = uri.Host is { Length: > 0 } h ? h : ".";
                _connect = async ct =>
                {
                    var p = new NamedPipeClientStream(server, pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
                    try { await p.ConnectAsync(ct); }
                    catch { await p.DisposeAsync(); throw; }
                    return (p, null);
                };
                _host = "docker";
                break;
            case "tcp" or "http":
                var (host, port) = (uri.Host, uri.Port > 0 ? uri.Port : 2375);
                _connect = async ct =>
                {
                    var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try { await s.ConnectAsync(host, port, ct); }
                    catch { s.Dispose(); throw; }
                    return (new NetworkStream(s, ownsSocket: true), s);
                };
                _host = $"{host}:{port}";
                break;
            default:
                throw new ArgumentException($"docker endpoint {endpoint}: only unix://, npipe:// and tcp:// are supported");
        }
        var handler = new SocketsHttpHandler
        {
            // docker keeps an idle connection open; one heartbeat to the next is up to half a minute
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 16,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectCallback = async (_, ct) => (await _connect(ct)).stream,
        };
        // the calls are bounded by their own tokens (a container that does not answer must not hold up the others)
        _http = new HttpClient(handler) { BaseAddress = new Uri($"http://{_host}/"), Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>A path segment or a query value, escaped.</summary>
    private static string E(string s) => Uri.EscapeDataString(s);

    /// <summary>The filters query parameter: {"label": ["a=b"], ...}.</summary>
    private static string Filters(IDictionary<string, string[]> filters) => E(JsonSerializer.Serialize(filters));

    /// <summary>
    /// Sends a request and answers with its headers read (the body is still on the connection). Failures throw
    /// <see cref="DockerApiException"/> with Docker's message; <paramref name="notFound"/>: a 404 is not an error,
    /// null comes back instead. Bounded only by <paramref name="ct"/>: for the streams (logs, events).
    /// </summary>
    private async Task<HttpResponseMessage?> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct, bool notFound = false)
    {
        using var req = new HttpRequestMessage(method, path);
        if (body != null) req.Content = new StringContent(body is JsonNode n ? n.ToJsonString() : JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.IsSuccessStatusCode || resp.StatusCode == HttpStatusCode.NotModified) return resp;
        using (resp)
        {
            if (notFound && resp.StatusCode == HttpStatusCode.NotFound) return null;
            throw await ErrorAsync(resp, ct);
        }
    }

    /// <summary>
    /// A request that answers at once, its whole answer taken by <paramref name="read"/>. Headers and body together
    /// must come within <see cref="RequestTimeout"/> (a <see cref="TimeoutException"/> otherwise), except for a
    /// <paramref name="wait"/>: a request docker answers when something is done (a container's exit, a prune).
    /// A 404 with <paramref name="notFound"/> is the default value (null).
    /// </summary>
    private async Task<T> ReadAsync<T>(HttpMethod method, string path, object? body, Func<HttpResponseMessage, CancellationToken, Task<T>> read, CancellationToken ct, bool notFound = false, bool wait = false)
    {
        using var timeout = wait ? null : CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout?.CancelAfter(RequestTimeout);
        var token = timeout?.Token ?? ct;
        try
        {
            using var resp = await SendAsync(method, path, body, token, notFound);
            return resp == null ? default! : await read(resp, token);
        }
        catch (OperationCanceledException) when (timeout?.IsCancellationRequested == true && !ct.IsCancellationRequested)
        {
            throw new TimeoutException($"docker did not answer {method} /{path} within {RequestTimeout.TotalSeconds:0}s");
        }
    }

    // the answers are read off the response stream as they come: no string of the whole body first (a heartbeat
    // reads a stats answer per container and the container list, every few seconds)
    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage resp, CancellationToken ct) =>
        await JsonSerializer.DeserializeAsync<T>(await resp.Content.ReadAsStreamAsync(ct), Json, ct);

    private static async Task<JsonDocument?> ReadDocAsync(HttpResponseMessage resp, CancellationToken ct) =>
        await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), default, ct);

    private static async Task<JsonNode?> ReadNodeAsync(HttpResponseMessage resp, CancellationToken ct) =>
        await JsonNode.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

    private Task<T?> GetAsync<T>(string path, CancellationToken ct, bool notFound = false) where T : class =>
        ReadAsync(HttpMethod.Get, path, null, ReadJsonAsync<T>, ct, notFound);

    /// <summary>The answer as a document to read fields off (dispose it); null for a 404.</summary>
    private Task<JsonDocument?> GetDocAsync(string path, CancellationToken ct) => ReadAsync(HttpMethod.Get, path, null, ReadDocAsync, ct, notFound: true);

    /// <summary>The answer as a mutable object (to send back changed); null for a 404.</summary>
    private async Task<JsonObject?> GetNodeAsync(string path, CancellationToken ct) =>
        await ReadAsync(HttpMethod.Get, path, null, ReadNodeAsync, ct, notFound: true) as JsonObject;

    /// <summary>A request whose answer is not needed (or is empty); the status code.</summary>
    private Task<HttpStatusCode> CallAsync(HttpMethod method, string path, object? body, CancellationToken ct) =>
        ReadAsync(method, path, body, static (resp, _) => Task.FromResult(resp.StatusCode), ct);

    private async Task<T> CallAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct, bool wait = false) =>
        (await ReadAsync(method, path, body, ReadJsonAsync<T>, ct, wait: wait))!;

    /// <summary>Docker's own message ({"message": "..."}), which is what a person wants to read.</summary>
    private static async Task<DockerApiException> ErrorAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var body = "";
        try { body = (await resp.Content.ReadAsStringAsync(ct)).Trim(); } catch (Exception) when (!ct.IsCancellationRequested) { }
        return new DockerApiException(resp.StatusCode, MessageOf(body) ?? $"docker answered {(int)resp.StatusCode} {resp.ReasonPhrase}");
    }

    private static string? MessageOf(string body)
    {
        if (body.Length == 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("message", out var m) && m.GetString() is { Length: > 0 } text) return text;
        }
        catch (JsonException) { }
        return body;
    }

    /// <summary>
    /// A request after which Docker takes the connection over: the container's (or exec's) stdin, stdout and stderr
    /// go over it both ways. Spoken by hand on a connection of its own (HttpClient cannot hand out the raw connection);
    /// asks for the upgrade like the docker CLI, an engine that only switches without it (200) is fine too.
    /// </summary>
    private async Task<DockerStream> HijackAsync(string path, object? body, bool multiplexed, CancellationToken ct)
    {
        var (stream, socket) = await _connect(ct);
        try
        {
            var payload = body == null ? [] : Encoding.UTF8.GetBytes(body is JsonNode n ? n.ToJsonString() : JsonSerializer.Serialize(body));
            var head = $"POST /{path} HTTP/1.1\r\nHost: {_host}\r\nUser-Agent: Dockmaster\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\n" +
                       "Connection: Upgrade\r\nUpgrade: tcp\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
            if (payload.Length > 0) await stream.WriteAsync(payload, ct);
            await stream.FlushAsync(ct);

            var (status, headers, rest) = await ReadHeadAsync(stream, ct);
            if (status is 101 or 200) return new DockerStream(new PrefixedStream(rest, stream), multiplexed, socket);

            // an error: the body follows (a JSON message), up to its length
            var length = headers.TryGetValue("content-length", out var l) && int.TryParse(l, out var len) ? len : -1;
            var error = new MemoryStream();
            error.Write(rest);
            var buf = new byte[4096];
            while (length < 0 || error.Length < length)
            {
                var read = await stream.ReadAsync(buf, ct);
                if (read == 0) break;
                error.Write(buf, 0, read);
            }
            var text = Encoding.UTF8.GetString(error.ToArray());
            throw new DockerApiException((HttpStatusCode)status, MessageOf(text.Trim()) ?? $"docker answered {status}");
        }
        catch
        {
            await stream.DisposeAsync();
            socket?.Dispose();
            throw;
        }
    }

    /// <summary>The status line and headers of an answer; the bytes read past them belong to the stream.</summary>
    private static async Task<(int status, Dictionary<string, string> headers, byte[] rest)> ReadHeadAsync(Stream stream, CancellationToken ct)
    {
        var buf = new byte[8192];
        var got = 0;
        while (true)
        {
            if (got == buf.Length) throw new IOException("docker's answer has too long a head");
            var n = await stream.ReadAsync(buf.AsMemory(got), ct);
            if (n == 0) throw new IOException("docker closed the connection before it answered");
            got += n;
            var end = buf.AsSpan(0, got).IndexOf("\r\n\r\n"u8);
            if (end < 0) continue;
            var lines = Encoding.ASCII.GetString(buf, 0, end).Split("\r\n");
            var parts = lines[0].Split(' ', 3);
            if (parts.Length < 2 || !int.TryParse(parts[1], out var status)) throw new IOException($"docker answered '{lines[0]}'");
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1))
                if (line.IndexOf(':') is > 0 and var colon) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            return (status, headers, buf[(end + 4)..got]);
        }
    }

    public void Dispose() => _http.Dispose();

    /// <summary>Bytes already read off the connection first, then the connection; writes go straight to it.</summary>
    private sealed class PrefixedStream(byte[] prefix, Stream inner) : Stream
    {
        private int _offset;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_offset < prefix.Length)
            {
                var n = Math.Min(buffer.Length, prefix.Length - _offset);
                prefix.AsMemory(_offset, n).CopyTo(buffer);
                _offset += n;
                return n;
            }
            return await inner.ReadAsync(buffer, ct);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, default).GetAwaiter().GetResult();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => inner.WriteAsync(buffer, ct);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.WriteAsync(buffer, offset, count, ct);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override void Flush() => inner.Flush();
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
