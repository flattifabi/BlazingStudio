using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;

namespace BlazorStudio.Core.NuGet;

public sealed record PackageSummary(
    string Id,
    string Version,
    string? Description,
    string? Authors,
    long TotalDownloads,
    string? IconUrl,
    string? ProjectUrl,
    bool Verified,
    IReadOnlyList<string> Tags,
    string? Source = null);

public sealed record PackageDetails(
    string Id,
    string Version,
    string? Description,
    string? Authors,
    string? ProjectUrl,
    string? LicenseExpression,
    string? LicenseUrl,
    DateTimeOffset? Published,
    IReadOnlyList<string> Dependencies);

public sealed record InstalledPackage(string Id, string Version);

/// <summary>Fehler einer einzelnen Quelle – die anderen Quellen liefern trotzdem Ergebnisse.</summary>
public sealed record SourceError(PackageSource Source, Exception Error);

public sealed record SearchResults(IReadOnlyList<PackageSummary> Packages, IReadOnlyList<SourceError> Errors);

/// <summary>Zugriff auf NuGet-Feeds (V3-API, auch mit Benutzer/Passwort, oder lokale Ordner) und die PackageReferences einer .csproj.</summary>
public sealed class NuGetClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, Task<Endpoints>> _endpoints = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (DateTime Stamp, Nuspec? Spec)> _local = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (DateTime Built, Task<List<Nuspec>> Index)> _folders = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan FolderIndexAge = TimeSpan.FromSeconds(30);
    private IReadOnlyList<PackageSource> _sources = new[] { NuGetConfig.NuGetOrg };

    public NuGetClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(20) };
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("BlazorStudio/1.0");
    }

    /// <summary>Alle konfigurierten Quellen (auch deaktivierte).</summary>
    public IReadOnlyList<PackageSource> Sources
    {
        get => _sources;
        set
        {
            _sources = value;
            _endpoints.Clear();
        }
    }

    /// <summary>Suchbegriff für nuget.org, wenn das Suchfeld leer ist (eigene Feeds listen dann alle Pakete).</summary>
    public string EmptyQueryOnNuGetOrg { get; set; } = "";

    private sealed record Endpoints(string? Search, string? FlatContainer);

    private IEnumerable<PackageSource> Active(string? source) =>
        _sources.Where(s => s.Enabled && (source is null || s.Name.Equals(source, StringComparison.OrdinalIgnoreCase)));

    // ------------------------------------------------------------------
    // HTTP
    // ------------------------------------------------------------------

    private async Task<HttpResponseMessage> SendAsync(PackageSource source, string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        // Zugangsdaten nur an den Host des Feeds schicken
        if (source.HasCredentials && Uri.TryCreate(source.Url, UriKind.Absolute, out var feed) && Uri.TryCreate(url, UriKind.Absolute, out var target)
            && feed.Host.Equals(target.Host, StringComparison.OrdinalIgnoreCase))
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{source.UserName}:{source.Password}"));
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
        }
        return await _http.SendAsync(req, ct);
    }

    /// <summary>Inhalt als Text; null bei 404.</summary>
    private async Task<string?> GetStringAsync(PackageSource source, string url, CancellationToken ct)
    {
        using var r = await SendAsync(source, url, ct);
        if (r.StatusCode == HttpStatusCode.NotFound) return null;
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadAsStringAsync(ct);
    }

    private Task<Endpoints> GetEndpointsAsync(PackageSource source, CancellationToken ct)
    {
        var task = _endpoints.GetOrAdd(source.Url, _ => LoadEndpointsAsync(source, CancellationToken.None));
        // fehlgeschlagene Abfragen nicht cachen (z. B. falsches Passwort, offline)
        if (task.IsFaulted || task.IsCanceled) _endpoints.TryRemove(new KeyValuePair<string, Task<Endpoints>>(source.Url, task));
        return task.WaitAsync(ct);
    }

    private async Task<Endpoints> LoadEndpointsAsync(PackageSource source, CancellationToken ct)
    {
        try
        {
            var json = await GetStringAsync(source, source.Url, ct)
                       ?? throw new HttpRequestException("404 (Not Found)", null, HttpStatusCode.NotFound);
            JsonDocument doc;
            try { doc = JsonDocument.Parse(json); }
            catch (JsonException) { throw new InvalidDataException("No NuGet V3 service index (…/v3/index.json)."); }
            using (doc)
            {
                if (!doc.RootElement.TryGetProperty("resources", out var resources) || resources.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("No NuGet V3 service index (…/v3/index.json).");
                string? search = null, flat = null;
                foreach (var r in resources.EnumerateArray())
                {
                    var type = r.TryGetProperty("@type", out var t) ? t.ValueKind == JsonValueKind.Array ? string.Join(" ", t.EnumerateArray().Select(x => x.GetString())) : t.GetString() ?? "" : "";
                    var id = r.TryGetProperty("@id", out var i) ? i.GetString() : null;
                    if (id is null) continue;
                    if (search is null && type.StartsWith("SearchQueryService", StringComparison.Ordinal)) search = id;
                    if (flat is null && type.StartsWith("PackageBaseAddress/3.0.0", StringComparison.Ordinal)) flat = id.EndsWith('/') ? id : id + "/";
                }
                if (source.IsNuGetOrg)
                {
                    search ??= "https://azuresearch-usnc.nuget.org/query";
                    flat ??= "https://api.nuget.org/v3-flatcontainer/";
                }
                return new Endpoints(search, flat);
            }
        }
        catch
        {
            _endpoints.TryRemove(source.Url, out _);
            throw;
        }
    }

    /// <summary>Verbindung zu einer Quelle prüfen (Service-Index bzw. Ordner). Wirft bei Fehlern.</summary>
    public async Task TestAsync(PackageSource source, CancellationToken ct = default)
    {
        if (source.IsLocal)
        {
            if (!Directory.Exists(source.Url)) throw new DirectoryNotFoundException(source.Url);
            return;
        }
        _endpoints.TryRemove(source.Url, out _);
        await LoadEndpointsAsync(source, ct);
        _endpoints.TryRemove(source.Url, out _);
    }

    // ------------------------------------------------------------------
    // Suche, Versionen, Details
    // ------------------------------------------------------------------

    /// <summary>
    /// Sucht in allen aktiven Quellen (oder nur in <paramref name="source"/>). Eigene Feeds zuerst, doppelte Ids
    /// erscheinen nur einmal. Fehler einzelner Quellen landen in <see cref="SearchResults.Errors"/>.
    /// </summary>
    public async Task<SearchResults> SearchAsync(string query, bool prerelease, int take = 30, string? source = null, CancellationToken ct = default)
    {
        var tasks = Active(source).Select(async s =>
        {
            try
            {
                // je Quelle im Hintergrund: Ordner, DNS, große Antworten blockieren sonst die Oberfläche
                return (Source: s, Items: await Task.Run(() => SearchSourceAsync(s, query, prerelease, take, ct), ct), Error: (Exception?)null);
            }
            catch (Exception e) when (IsFeedError(e, ct))
            {
                return (Source: s, Items: (IReadOnlyList<PackageSummary>)Array.Empty<PackageSummary>(), Error: e);
            }
        });
        var results = await Task.WhenAll(tasks);
        ct.ThrowIfCancellationRequested();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var packages = results.OrderBy(r => r.Source.IsNuGetOrg)
            .SelectMany(r => r.Items)
            .Where(p => seen.Add(p.Id))
            .ToList();
        return new SearchResults(packages, results.Where(r => r.Error is not null).Select(r => new SourceError(r.Source, r.Error!)).ToList());
    }

    private async Task<IReadOnlyList<PackageSummary>> SearchSourceAsync(PackageSource source, string query, bool prerelease, int take, CancellationToken ct)
    {
        if (source.IsLocal) return await SearchLocalAsync(source, query, prerelease, take, ct);
        var ep = await GetEndpointsAsync(source, ct);
        if (ep.Search is null) return Array.Empty<PackageSummary>();
        if (query.Length == 0 && source.IsNuGetOrg) query = EmptyQueryOnNuGetOrg;
        var url = $"{ep.Search}{(ep.Search.Contains('?') ? '&' : '?')}q={Uri.EscapeDataString(query)}&take={take}&prerelease={(prerelease ? "true" : "false")}&semVerLevel=2.0.0";
        var json = await GetStringAsync(source, url, ct);
        var result = json is null ? null : JsonSerializer.Deserialize<SearchResponse>(json, Json);
        return result?.Data.Select(d => new PackageSummary(
            d.Id, d.Version, d.Description, d.Authors is { Count: > 0 } a ? string.Join(", ", a) : null,
            d.TotalDownloads, d.IconUrl, d.ProjectUrl, d.Verified, d.Tags ?? new List<string>(), source.Name)).ToList()
            ?? (IReadOnlyList<PackageSummary>)Array.Empty<PackageSummary>();
    }

    /// <summary>Alle veröffentlichten Versionen aus allen aktiven Quellen (oder nur <paramref name="source"/>), neueste zuerst.</summary>
    public async Task<IReadOnlyList<string>> GetVersionsAsync(string id, bool prerelease, string? source = null, CancellationToken ct = default)
    {
        var tasks = Active(source).Select(async s =>
        {
            try { return await Task.Run(() => GetSourceVersionsAsync(s, id, ct), ct); }
            catch (Exception e) when (IsFeedError(e, ct)) { return Array.Empty<string>(); }
        });
        var list = (await Task.WhenAll(tasks)).SelectMany(v => v)
            .Where(v => prerelease || !v.Contains('-'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        list.Sort((a, b) => CompareVersions(b, a));
        return list;
    }

    private async Task<IReadOnlyList<string>> GetSourceVersionsAsync(PackageSource source, string id, CancellationToken ct)
    {
        if (source.IsLocal) return (await LocalPackagesAsync(source, ct)).Where(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase)).Select(p => p.Version).ToList();
        var ep = await GetEndpointsAsync(source, ct);
        if (ep.FlatContainer is null) return Array.Empty<string>();
        var json = await GetStringAsync(source, $"{ep.FlatContainer}{id.ToLowerInvariant()}/index.json", ct);
        return json is null ? Array.Empty<string>() : JsonSerializer.Deserialize<VersionsResponse>(json, Json)?.Versions ?? new List<string>();
    }

    public async Task<string?> GetLatestAsync(string id, bool prerelease, CancellationToken ct = default) =>
        (await GetVersionsAsync(id, prerelease, ct: ct)).FirstOrDefault();

    /// <summary>Metadaten aus der .nuspec der Version – zuerst aus <paramref name="source"/>, sonst aus den übrigen Quellen.</summary>
    public async Task<PackageDetails?> GetDetailsAsync(string id, string version, string? source = null, CancellationToken ct = default)
    {
        foreach (var s in Active(null).OrderBy(s => source is null || !s.Name.Equals(source, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                if (await Task.Run(() => GetNuspecAsync(s, id, version, ct), ct) is { } spec)
                    return new PackageDetails(spec.Id, spec.Version, spec.Description, spec.Authors, spec.ProjectUrl,
                        spec.LicenseExpression, spec.LicenseUrl, null, spec.Dependencies);
            }
            catch (Exception e) when (IsFeedError(e, ct)) { }
        }
        return null;
    }

    private async Task<Nuspec?> GetNuspecAsync(PackageSource source, string id, string version, CancellationToken ct)
    {
        if (source.IsLocal)
            return (await LocalPackagesAsync(source, ct)).FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase) && CompareVersions(p.Version, version) == 0);
        var ep = await GetEndpointsAsync(source, ct);
        if (ep.FlatContainer is null) return null;
        var lid = id.ToLowerInvariant();
        var xml = await GetStringAsync(source, $"{ep.FlatContainer}{lid}/{version.ToLowerInvariant()}/{lid}.nuspec", ct);
        return xml is null ? null : ParseNuspec(XDocument.Parse(xml));
    }

    private static bool IsFeedError(Exception e, CancellationToken ct) =>
        e is HttpRequestException or JsonException or IOException or InvalidDataException or UnauthorizedAccessException or XmlException or NotSupportedException
        || (e is TaskCanceledException && !ct.IsCancellationRequested);

    // ------------------------------------------------------------------
    // Lokale Ordner-Feeds (*.nupkg, flach oder id/version/)
    // ------------------------------------------------------------------

    private async Task<IReadOnlyList<PackageSummary>> SearchLocalAsync(PackageSource source, string query, bool prerelease, int take, CancellationToken ct) =>
        (await LocalPackagesAsync(source, ct))
            .Where(p => prerelease || !p.Version.Contains('-'))
            .Where(p => query.Length == 0 || p.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || p.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) == true
                        || p.Tags.Any(t => t.Equals(query, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(p => p.Version, Comparer<string>.Create(CompareVersions)).First())
            .OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .Take(take)
            .Select(p => new PackageSummary(p.Id, p.Version, p.Description, p.Authors, 0, p.IconUrl, p.ProjectUrl, false, p.Tags, source.Name))
            .ToList();

    /// <summary>
    /// Paketliste eines Ordners – im Hintergrund gelesen (Netzlaufwerke, große Ordner blockieren sonst die Oberfläche)
    /// und für kurze Zeit für alle Abfragen geteilt, statt pro Paket neu zu scannen.
    /// </summary>
    private Task<List<Nuspec>> LocalPackagesAsync(PackageSource source, CancellationToken ct)
    {
        Task<List<Nuspec>> index;
        lock (_folders)
        {
            var now = DateTime.UtcNow;
            if (!_folders.TryGetValue(source.Url, out var entry)
                || (entry.Index.IsCompleted && now - entry.Built > FolderIndexAge))
                _folders[source.Url] = entry = (now, Task.Run(() => IndexFolder(source.Url)));
            index = entry.Index;
        }
        return index.WaitAsync(ct);
    }

    private List<Nuspec> IndexFolder(string folder)
    {
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException(folder);
        var result = new List<Nuspec>();
        foreach (var file in NupkgFiles(folder, depth: 2))
        {
            if (file.EndsWith(".symbols.nupkg", StringComparison.OrdinalIgnoreCase)) continue;
            var stamp = File.GetLastWriteTimeUtc(file);
            if (!_local.TryGetValue(file, out var cached) || cached.Stamp != stamp)
                _local[file] = cached = (stamp, ReadNupkg(file));
            if (cached.Spec is not null) result.Add(cached.Spec);
        }
        return result;
    }

    /// <summary>Layouts von NuGet-Ordnerfeeds: flach oder id/version/ – tiefer wird nicht gesucht.</summary>
    private static IEnumerable<string> NupkgFiles(string dir, int depth)
    {
        string[] files, dirs;
        try
        {
            files = Directory.GetFiles(dir, "*.nupkg");
            dirs = depth > 0 ? Directory.GetDirectories(dir) : Array.Empty<string>();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (var f in files) yield return f;
        foreach (var d in dirs)
            foreach (var f in NupkgFiles(d, depth - 1))
                yield return f;
    }

    private static Nuspec? ReadNupkg(string file)
    {
        try
        {
            using var zip = ZipFile.OpenRead(file);
            var entry = zip.Entries.FirstOrDefault(e => !e.FullName.Contains('/') && e.Name.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
            if (entry is null) return null;
            using var stream = entry.Open();
            return ParseNuspec(XDocument.Load(stream));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or XmlException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed record Nuspec(
        string Id, string Version, string? Description, string? Authors, string? ProjectUrl, string? IconUrl,
        string? LicenseExpression, string? LicenseUrl, IReadOnlyList<string> Tags, IReadOnlyList<string> Dependencies);

    private static Nuspec? ParseNuspec(XDocument doc)
    {
        var meta = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata");
        if (meta is null) return null;
        string? Get(string name) => meta.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();
        var id = Get("id");
        var version = Get("version");
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(version)) return null;
        var license = meta.Elements().FirstOrDefault(e => e.Name.LocalName == "license");
        var deps = meta.Descendants().Where(e => e.Name.LocalName == "dependency")
            .Select(e => $"{e.Attribute("id")?.Value} {e.Attribute("version")?.Value}".Trim())
            .Distinct().Take(40).ToList();
        var tags = (Get("tags") ?? "").Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
        return new Nuspec(id, version, Get("description"), Get("authors"), Get("projectUrl"), Get("iconUrl"),
            license?.Attribute("type")?.Value == "expression" ? license.Value.Trim() : null, Get("licenseUrl"), tags, deps);
    }

    // ------------------------------------------------------------------
    // .csproj
    // ------------------------------------------------------------------

    public static IReadOnlyList<InstalledPackage> ReadInstalled(string csproj)
    {
        try
        {
            var doc = XDocument.Load(csproj);
            var central = ReadCentralVersions(Path.GetDirectoryName(csproj)!);
            return doc.Descendants().Where(e => e.Name.LocalName == "PackageReference")
                .Select(e =>
                {
                    var id = e.Attribute("Include")?.Value ?? e.Attribute("Update")?.Value ?? "";
                    var v = e.Attribute("Version")?.Value ?? e.Elements().FirstOrDefault(x => x.Name.LocalName == "Version")?.Value
                            ?? central.GetValueOrDefault(id) ?? "";
                    return new InstalledPackage(id, v);
                })
                .Where(p => p.Id.Length > 0)
                .OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception e) when (e is IOException or System.Xml.XmlException)
        {
            return Array.Empty<InstalledPackage>();
        }
    }

    /// <summary>Directory.Packages.props (Central Package Management) in übergeordneten Ordnern.</summary>
    private static Dictionary<string, string> ReadCentralVersions(string dir)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var d = dir; d is not null; d = Path.GetDirectoryName(d))
        {
            var props = Path.Combine(d, "Directory.Packages.props");
            if (!File.Exists(props)) continue;
            try
            {
                foreach (var e in XDocument.Load(props).Descendants().Where(x => x.Name.LocalName == "PackageVersion"))
                {
                    var id = e.Attribute("Include")?.Value;
                    var v = e.Attribute("Version")?.Value;
                    if (id is not null && v is not null) result.TryAdd(id, v);
                }
            }
            catch (System.Xml.XmlException) { }
            break;
        }
        return result;
    }

    /// <summary>SemVer-Vergleich (ausreichend für NuGet-Versionen).</summary>
    public static int CompareVersions(string a, string b)
    {
        static (Version v, string pre) Split(string s)
        {
            var dash = s.IndexOf('-');
            var core = (dash < 0 ? s : s[..dash]).Split('+')[0];
            var pre = dash < 0 ? "" : s[(dash + 1)..].Split('+')[0];
            var parts = core.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).Concat(new[] { 0, 0, 0, 0 }).Take(4).ToArray();
            return (new Version(parts[0], parts[1], parts[2], parts[3]), pre);
        }
        var (va, pa) = Split(a);
        var (vb, pb) = Split(b);
        var c = va.CompareTo(vb);
        if (c != 0) return c;
        if (pa.Length == 0 && pb.Length == 0) return 0;
        if (pa.Length == 0) return 1;
        if (pb.Length == 0) return -1;
        var sa = pa.Split('.');
        var sb = pb.Split('.');
        for (var i = 0; i < Math.Max(sa.Length, sb.Length); i++)
        {
            if (i >= sa.Length) return -1;
            if (i >= sb.Length) return 1;
            var na = int.TryParse(sa[i], out var x);
            var nb = int.TryParse(sb[i], out var y);
            c = na && nb ? x.CompareTo(y) : na ? -1 : nb ? 1 : string.CompareOrdinal(sa[i], sb[i]);
            if (c != 0) return c;
        }
        return 0;
    }

    private sealed class SearchResponse
    {
        public List<SearchItem> Data { get; set; } = new();
    }

    private sealed class SearchItem
    {
        public string Id { get; set; } = "";
        public string Version { get; set; } = "";
        public string? Description { get; set; }
        public List<string>? Authors { get; set; }
        public long TotalDownloads { get; set; }
        public string? IconUrl { get; set; }
        public string? ProjectUrl { get; set; }
        public bool Verified { get; set; }
        public List<string>? Tags { get; set; }

        [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }
    }

    private sealed class VersionsResponse
    {
        public List<string> Versions { get; set; } = new();
    }
}
