using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlazorStudio.Deploy;

/// <summary>Fehler bei einem Aufruf des Agents.</summary>
public sealed class AgentException(string message, HttpStatusCode? status = null, Exception? inner = null) : Exception(message, inner)
{
    public HttpStatusCode? Status { get; } = status;

    public bool IsUnauthorized => Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    /// <summary>Server nicht erreichbar (keine HTTP-Antwort).</summary>
    public bool IsUnreachable => Status is null;
}

/// <summary>HTTP-Client für die REST-API von BlazorStudio.Agent.</summary>
public sealed class AgentClient : IDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(20);

    private readonly HttpClient _http;

    /// <param name="certificateThumbprint">
    /// Optional: Zertifikat mit diesem SHA1-Fingerabdruck wird auch ohne vertrauenswürdige Kette akzeptiert
    /// (selbstsignierte Zertifikate im internen Netz). Alle anderen bleiben gesperrt.
    /// </param>
    public AgentClient(Uri baseAddress, string apiKey, string? certificateThumbprint = null, HttpMessageHandler? handler = null)
    {
        BaseAddress = baseAddress;
        handler ??= CreateHandler(certificateThumbprint);
        _http = new HttpClient(handler) { BaseAddress = baseAddress, Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.Add(AgentDefaults.ApiKeyHeader, apiKey);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("BlazorStudio/1.0");
    }

    public Uri BaseAddress { get; }

    public static Uri BuildAddress(string host, int port, bool https)
    {
        var h = host.Trim();
        // IPv6 ohne Klammern
        if (h.Contains(':') && !h.StartsWith('[')) h = "[" + h + "]";
        return new UriBuilder(https ? "https" : "http", h, port <= 0 ? AgentDefaults.Port : port, "/").Uri;
    }

    public static string NormalizeThumbprint(string? thumbprint) =>
        new string((thumbprint ?? "").Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();

    private static SocketsHttpHandler CreateHandler(string? thumbprint)
    {
        var pinned = NormalizeThumbprint(thumbprint);
        var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(6), PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        if (pinned.Length > 0)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = (_, cert, _, errors) =>
                errors == SslPolicyErrors.None ||
                (cert is not null && string.Equals(new X509Certificate2(cert).Thumbprint, pinned, StringComparison.OrdinalIgnoreCase));
        }
        return handler;
    }

    // ------------------------------------------------------------------
    // Allgemein
    // ------------------------------------------------------------------

    public Task<PingResponse> PingAsync(CancellationToken ct = default) => GetAsync<PingResponse>("api/ping", ct);

    public Task<AgentInfo> GetInfoAsync(CancellationToken ct = default) => GetAsync<AgentInfo>("api/info", ct);

    // ------------------------------------------------------------------
    // App-Pools
    // ------------------------------------------------------------------

    public Task<List<AppPoolInfo>> GetPoolsAsync(CancellationToken ct = default) => GetAsync<List<AppPoolInfo>>("api/pools", ct);

    /// <param name="action">start, stop oder recycle.</param>
    public Task PoolActionAsync(string pool, string action, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"api/pools/{Esc(pool)}/{action}", null, ct);

    public Task CreatePoolAsync(PoolSettings settings, CancellationToken ct = default) => SendAsync(HttpMethod.Post, "api/pools", settings, ct);

    public Task UpdatePoolAsync(PoolSettings settings, CancellationToken ct = default) => SendAsync(HttpMethod.Put, $"api/pools/{Esc(settings.Name)}", settings, ct);

    public Task DeletePoolAsync(string pool, CancellationToken ct = default) => SendAsync(HttpMethod.Delete, $"api/pools/{Esc(pool)}", null, ct);

    // ------------------------------------------------------------------
    // Sites & Versionen
    // ------------------------------------------------------------------

    public Task<List<SiteInfo>> GetSitesAsync(CancellationToken ct = default) => GetAsync<List<SiteInfo>>("api/sites", ct);

    /// <param name="action">start oder stop.</param>
    public Task SiteActionAsync(string site, string action, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"api/sites/{Esc(site)}/{action}", null, ct);

    public Task CreateSiteAsync(CreateSiteRequest request, CancellationToken ct = default) => SendAsync(HttpMethod.Post, "api/sites", request, ct);

    public Task DeleteSiteAsync(string site, bool removeFiles, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"api/sites/{Esc(site)}?removeFiles={(removeFiles ? "true" : "false")}", null, ct);

    public Task<List<SiteVersion>> GetVersionsAsync(string site, string appPath = "/", CancellationToken ct = default) =>
        GetAsync<List<SiteVersion>>($"api/sites/{Esc(site)}/versions?app={Uri.EscapeDataString(appPath)}", ct);

    public Task ActivateAsync(string site, ActivateRequest request, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"api/sites/{Esc(site)}/activate", request, ct);

    public Task DeleteVersionAsync(string site, string appPath, string version, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"api/sites/{Esc(site)}/versions/{Esc(version)}?app={Uri.EscapeDataString(appPath)}", null, ct);

    // ------------------------------------------------------------------
    // Authentifizierung, Konfigurationsdateien, Logs
    // ------------------------------------------------------------------

    private static string App(string appPath) => "app=" + Uri.EscapeDataString(DeployRequest.NormalizeAppPath(appPath));

    public Task<SiteAuthentication> GetAuthenticationAsync(string site, string appPath, CancellationToken ct = default) =>
        GetAsync<SiteAuthentication>($"api/sites/{Esc(site)}/auth?{App(appPath)}", ct);

    public Task SetAuthenticationAsync(string site, string appPath, SiteAuthentication auth, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Put, $"api/sites/{Esc(site)}/auth?{App(appPath)}", auth, ct);

    public Task<List<SiteConfigFile>> GetConfigFilesAsync(string site, string appPath, CancellationToken ct = default) =>
        GetAsync<List<SiteConfigFile>>($"api/sites/{Esc(site)}/config?{App(appPath)}", ct);

    public Task<SiteConfigContent> GetConfigFileAsync(string site, string appPath, string file, CancellationToken ct = default) =>
        GetAsync<SiteConfigContent>($"api/sites/{Esc(site)}/config/{Esc(file)}?{App(appPath)}", ct);

    public Task<SiteConfigWriteResult> SaveConfigFileAsync(string site, string appPath, string file, SiteConfigWrite write, CancellationToken ct = default) =>
        SendAsync<SiteConfigWriteResult>(HttpMethod.Put, $"api/sites/{Esc(site)}/config/{Esc(file)}?{App(appPath)}", write, ct);

    /// <summary>Überschreibung entfernen – ab dem nächsten Deployment gilt wieder die Datei aus dem Paket.</summary>
    public Task DeleteConfigOverrideAsync(string site, string appPath, string file, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"api/sites/{Esc(site)}/config/{Esc(file)}?{App(appPath)}", null, ct);

    public Task<List<SiteLogFile>> GetLogFilesAsync(string site, string appPath, CancellationToken ct = default) =>
        GetAsync<List<SiteLogFile>>($"api/sites/{Esc(site)}/logs?{App(appPath)}", ct);

    /// <summary>Ende eines stdout-Logs (höchstens <see cref="SiteConfig.MaxLogBytes"/>).</summary>
    public Task<SiteConfigContent> GetLogAsync(string site, string appPath, string file, CancellationToken ct = default) =>
        GetAsync<SiteConfigContent>($"api/sites/{Esc(site)}/logs/{Esc(file)}?{App(appPath)}", ct);

    // ------------------------------------------------------------------
    // Deployments
    // ------------------------------------------------------------------

    public Task<List<DeploymentRecord>> GetDeploymentsAsync(string? site = null, int take = 50, CancellationToken ct = default) =>
        GetAsync<List<DeploymentRecord>>($"api/deployments?take={take}" + (site is null ? "" : "&site=" + Uri.EscapeDataString(site)), ct);

    public Task<DeploymentRecord> GetDeploymentAsync(string id, CancellationToken ct = default) => GetAsync<DeploymentRecord>($"api/deployments/{Esc(id)}", ct);

    /// <summary>Lädt das Paket hoch und startet das Deployment (läuft danach auf dem Server weiter).</summary>
    public async Task<DeploymentRecord> StartDeploymentAsync(DeployRequest request, Stream package, string fileName, IProgress<long>? uploaded = null, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        foreach (var (k, v) in request.ToForm()) form.Add(new StringContent(v), k);
        var file = new StreamContent(uploaded is null ? package : new ProgressStream(package, uploaded), 81920);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
        form.Add(file, AgentDefaults.PackageField, fileName);
        try
        {
            using var resp = await _http.PostAsync("api/deployments", form, ct);
            await EnsureAsync(resp, ct);
            return (await resp.Content.ReadFromJsonAsync<DeploymentRecord>(Json, ct))!;
        }
        catch (HttpRequestException e)
        {
            throw Unreachable(e);
        }
    }

    /// <summary>Fragt den Stand ab, bis das Deployment fertig ist.</summary>
    public async Task<DeploymentRecord> WaitForDeploymentAsync(string id, Action<DeploymentRecord>? onUpdate = null, CancellationToken ct = default)
    {
        while (true)
        {
            var r = await GetDeploymentAsync(id, ct);
            onUpdate?.Invoke(r);
            if (r.IsFinished) return r;
            await Task.Delay(1000, ct);
        }
    }

    // ------------------------------------------------------------------

    private static string Esc(string s) => Uri.EscapeDataString(s);

    private async Task<T> GetAsync<T>(string url, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(CallTimeout);
        try
        {
            using var resp = await _http.GetAsync(url, cts.Token);
            await EnsureAsync(resp, cts.Token);
            return (await resp.Content.ReadFromJsonAsync<T>(Json, cts.Token))!;
        }
        catch (HttpRequestException e)
        {
            throw Unreachable(e);
        }
        catch (OperationCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new AgentException("Zeitüberschreitung – der Agent antwortet nicht.", null, e);
        }
        catch (JsonException e)
        {
            throw new AgentException("Unerwartete Antwort – ist unter dieser Adresse wirklich der Blazor Studio Agent?", HttpStatusCode.OK, e);
        }
    }

    private async Task SendAsync(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Pools/Sites anlegen oder stoppen kann dauern
        cts.CancelAfter(TimeSpan.FromSeconds(90));
        using var req = new HttpRequestMessage(method, url);
        if (body is not null) req.Content = JsonContent.Create(body, body.GetType(), options: Json);
        try
        {
            using var resp = await _http.SendAsync(req, cts.Token);
            await EnsureAsync(resp, cts.Token);
        }
        catch (HttpRequestException e)
        {
            throw Unreachable(e);
        }
        catch (OperationCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new AgentException("Zeitüberschreitung – der Agent antwortet nicht.", null, e);
        }
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string url, object? body, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(90));
        using var req = new HttpRequestMessage(method, url);
        if (body is not null) req.Content = JsonContent.Create(body, body.GetType(), options: Json);
        try
        {
            using var resp = await _http.SendAsync(req, cts.Token);
            await EnsureAsync(resp, cts.Token);
            return (await resp.Content.ReadFromJsonAsync<T>(Json, cts.Token))!;
        }
        catch (HttpRequestException e)
        {
            throw Unreachable(e);
        }
        catch (OperationCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new AgentException("Zeitüberschreitung – der Agent antwortet nicht.", null, e);
        }
        catch (JsonException e)
        {
            throw new AgentException("Unerwartete Antwort vom Agent – ist er aktuell?", HttpStatusCode.OK, e);
        }
    }

    private static AgentException Unreachable(HttpRequestException e) =>
        e.StatusCode is { } code
            ? new AgentException(e.Message, code, e)
            : new AgentException(e.InnerException?.Message ?? e.Message, null, e);

    private static async Task EnsureAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        string? message = null;
        try
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (text.TrimStart().StartsWith('{')) message = JsonSerializer.Deserialize<ApiError>(text, Json)?.Error;
            else if (text.Length is > 0 and < 400) message = text;
        }
        catch (Exception e) when (e is JsonException or IOException or HttpRequestException)
        {
        }
        message = resp.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => message is { Length: > 0 } ? message : "API-Schlüssel abgelehnt.",
            _ => message is { Length: > 0 } ? message : $"{(int)resp.StatusCode} {resp.ReasonPhrase}",
        };
        throw new AgentException(message, resp.StatusCode);
    }

    public void Dispose() => _http.Dispose();

    /// <summary>Meldet beim Lesen die übertragenen Bytes (für den Upload-Fortschritt).</summary>
    private sealed class ProgressStream(Stream inner, IProgress<long> progress) : Stream
    {
        private long _total;

        public override bool CanRead => true;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set
            {
                inner.Position = value;
                _total = value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => Report(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => Report(await inner.ReadAsync(buffer, ct));

        private int Report(int n)
        {
            _total += n;
            progress.Report(_total);
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
