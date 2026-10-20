using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dockmaster.Server.Data;

namespace Dockmaster.Server.Services;

/// <summary>Browses a Docker Registry v2 (catalog, tags). Supports Basic auth and Bearer token challenges.</summary>
public sealed partial class RegistryClient
{
    private readonly IHttpClientFactory _http;
    private readonly SecretProtector _secrets;

    public RegistryClient(IHttpClientFactory http, SecretProtector secrets)
    {
        _http = http;
        _secrets = secrets;
    }

    /// <param name="Trusted">
    /// an admin entered this registry (under Registries): its token service may be on a private address, like the
    /// registry itself. Otherwise (a host read from an image name) it has to be on a public one.
    /// </param>
    public sealed record Creds(string Url, string Username, string Password, bool Insecure, bool Trusted = false);

    public Creds For(RegistryEntity r) => new(r.Url, r.Username, _secrets.Unprotect(r.PasswordProtected), r.Insecure, Trusted: true);

    /// <summary>The most pages a catalog or tag list is read in (1000 names each).</summary>
    public const int MaxPages = 100;

    private string Base(Creds c) => (c.Insecure ? "http://" : "https://") + c.Url.TrimEnd('/') + "/v2/";

    public async Task<List<string>> CatalogAsync(Creds c, CancellationToken ct)
    {
        var repos = new List<string>();
        var url = Base(c) + "_catalog?n=1000";
        for (var page = 0; url != null && page < MaxPages; page++)
        {
            using var doc = await GetJsonAsync(c, url, "registry:catalog:*", ct);
            if (doc.Data.RootElement.TryGetProperty("repositories", out var arr))
                repos.AddRange(arr.EnumerateArray().Select(e => e.GetString()!).Where(s => s != null));
            url = doc.Next;
        }
        repos.Sort(StringComparer.Ordinal);
        return repos;
    }

    public async Task<List<string>> TagsAsync(Creds c, string repo, CancellationToken ct)
    {
        var tags = new List<string>();
        var url = Base(c) + repo + "/tags/list?n=1000";
        for (var page = 0; url != null && page < MaxPages; page++)
        {
            using var doc = await GetJsonAsync(c, url, $"repository:{repo}:pull", ct);
            if (doc.Data.RootElement.TryGetProperty("tags", out var arr) && arr.ValueKind == JsonValueKind.Array)
                tags.AddRange(arr.EnumerateArray().Select(e => e.GetString()!).Where(s => s != null));
            url = doc.Next;
        }
        tags.Sort(new TagComparer());
        return tags;
    }

    public async Task<(bool ok, string message)> TestAsync(Creds c, CancellationToken ct)
    {
        try
        {
            using var _ = await GetJsonAsync(c, Base(c), "", ct);
            return (true, "OK");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private sealed class JsonPage : IDisposable
    {
        public required JsonDocument Data { get; init; }
        public string? Next { get; init; }
        public void Dispose() => Data.Dispose();
    }

    private async Task<JsonPage> GetJsonAsync(Creds c, string url, string scope, CancellationToken ct)
    {
        using var resp = await SendWithAuthAsync(HttpMethod.Get, url, JsonTypes, c, scope, ct);
        // the registry's own text stays out of the message: it is shown to users and may be anything the host answers
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"{(int)resp.StatusCode} {resp.ReasonPhrase}", null, resp.StatusCode);
        var text = await resp.Content.ReadAsStringAsync(ct);
        var doc = text.Length == 0 ? JsonDocument.Parse("{}") : JsonDocument.Parse(text);
        string? next = null;
        if (resp.Headers.TryGetValues("Link", out var links))
        {
            var m = LinkRegex().Match(string.Join(",", links));
            if (m.Success) next = NextPage(url, m.Groups[1].Value);
        }
        return new JsonPage { Data = doc, Next = next };
    }

    /// <summary>The next page a Link header names, on the same scheme, host and port as this one; null otherwise.</summary>
    public static string? NextPage(string url, string link)
    {
        var here = new Uri(url);
        return Uri.TryCreate(here, link, out var next) && Uri.Compare(here, next, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0
            ? next.ToString()
            : null;
    }

    /// <summary>
    /// The token service a Bearer challenge names, if it may be asked: an absolute http(s) address, https when the
    /// registry is; null otherwise.
    /// </summary>
    public static Uri? RealmUri(string registryUrl, string realm) =>
        Uri.TryCreate(realm, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp && new Uri(registryUrl).Scheme == Uri.UriSchemeHttp)
            ? u
            : null;

    private static readonly string[] JsonTypes = ["application/json"];

    private static readonly string[] ManifestTypes =
    [
        "application/vnd.oci.image.index.v1+json", "application/vnd.docker.distribution.manifest.list.v2+json",
        "application/vnd.oci.image.manifest.v1+json", "application/vnd.docker.distribution.manifest.v2+json",
    ];

    /// <summary>Digest the registry currently serves for repo:tag (HEAD on the manifest; does not count as a pull on Docker Hub).</summary>
    public async Task<string> ManifestDigestAsync(Creds c, string repo, string tag, CancellationToken ct)
    {
        using var resp = await SendWithAuthAsync(HttpMethod.Head, Base(c) + repo + "/manifests/" + tag, ManifestTypes, c, $"repository:{repo}:pull", ct);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"{(int)resp.StatusCode} {resp.ReasonPhrase}", null, resp.StatusCode);
        return resp.Headers.TryGetValues("Docker-Content-Digest", out var d) ? d.First() : throw new HttpRequestException("registry sent no digest");
    }

    /// <summary>
    /// Sends a request with Basic auth when there is a login. A registry that answers 401 with a Bearer challenge
    /// (Docker Hub, GHCR, most others) gets the request again with a token from its auth service.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithAuthAsync(HttpMethod method, string url, string[] accept, Creds c, string scope, CancellationToken ct)
    {
        var client = _http.CreateClient("registry");
        var basic = c.Username.Length > 0 ? Convert.ToBase64String(Encoding.UTF8.GetBytes($"{c.Username}:{c.Password}")) : null;
        var resp = await SendAsync(client, method, url, accept, basic != null ? new AuthenticationHeaderValue("Basic", basic) : null, ct);
        if (resp.StatusCode != HttpStatusCode.Unauthorized || resp.Headers.WwwAuthenticate.FirstOrDefault(h => h.Scheme == "Bearer") is not { } challenge) return resp;
        string token;
        using (resp) token = await FetchBearerAsync(client, url, challenge.Parameter ?? "", basic, scope, !c.Trusted, ct);
        return await SendAsync(client, method, url, accept, new AuthenticationHeaderValue("Bearer", token), ct);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string url, string[] accept, AuthenticationHeaderValue? auth, CancellationToken ct)
    {
        var req = new HttpRequestMessage(method, url);
        if (auth != null) req.Headers.Authorization = auth;
        foreach (var type in accept) req.Headers.Accept.ParseAdd(type);
        return await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private static async Task<string> FetchBearerAsync(HttpClient client, string registryUrl, string challenge, string? basic, string scope, bool publicOnly, CancellationToken ct)
    {
        var p = ParamRegex().Matches(challenge).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
        var realm = p.GetValueOrDefault("realm") ?? throw new InvalidOperationException("bearer challenge without realm");
        if (RealmUri(registryUrl, realm) is null) throw new InvalidOperationException("the registry named a token service that is not an https address");
        var q = new List<string>();
        if (p.TryGetValue("service", out var svc)) q.Add("service=" + Uri.EscapeDataString(svc));
        var sc = p.GetValueOrDefault("scope") ?? scope;
        if (!string.IsNullOrEmpty(sc)) q.Add("scope=" + Uri.EscapeDataString(sc));
        var url = realm + (q.Count > 0 ? (realm.Contains('?') ? "&" : "?") + string.Join("&", q) : "");
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Options.Set(RegistryHttp.PublicOnly, publicOnly);
        if (basic != null) req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        using var resp = await client.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"token endpoint {(int)resp.StatusCode}", null, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("token", out var t)) return t.GetString()!;
        if (doc.RootElement.TryGetProperty("access_token", out var at)) return at.GetString()!;
        throw new InvalidOperationException("token endpoint returned no token");
    }

    [GeneratedRegex("<([^>]+)>;\\s*rel=\"next\"")] private static partial Regex LinkRegex();
    [GeneratedRegex("(\\w+)=\"([^\"]*)\"")] private static partial Regex ParamRegex();

    /// <summary>latest first, then semver-ish descending, then plain strings.</summary>
    private sealed class TagComparer : IComparer<string>
    {
        public int Compare(string? a, string? b)
        {
            if (a == b) return 0;
            if (a == "latest") return -1;
            if (b == "latest") return 1;
            var va = Version.TryParse(a!.TrimStart('v'), out var x) ? x : null;
            var vb = Version.TryParse(b!.TrimStart('v'), out var y) ? y : null;
            if (va != null && vb != null) return vb.CompareTo(va);
            if (va != null) return -1;
            if (vb != null) return 1;
            return string.CompareOrdinal(b, a);
        }
    }
}
