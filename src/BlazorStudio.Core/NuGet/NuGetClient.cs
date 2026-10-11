using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    IReadOnlyList<string> Tags);

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

/// <summary>Zugriff auf nuget.org (V3-API) und die PackageReferences einer .csproj.</summary>
public sealed class NuGetClient
{
    private const string ServiceIndex = "https://api.nuget.org/v3/index.json";
    private const string FlatContainer = "https://api.nuget.org/v3-flatcontainer/";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private string? _searchUrl;
    private string? _registrationUrl;

    public NuGetClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("BlazorStudio/1.0");
    }

    private async Task EnsureServicesAsync(CancellationToken ct)
    {
        if (_searchUrl is not null) return;
        using var doc = JsonDocument.Parse(await _http.GetStringAsync(ServiceIndex, ct));
        foreach (var r in doc.RootElement.GetProperty("resources").EnumerateArray())
        {
            var type = r.GetProperty("@type").GetString() ?? "";
            var id = r.GetProperty("@id").GetString();
            if (_searchUrl is null && type.StartsWith("SearchQueryService", StringComparison.Ordinal)) _searchUrl = id;
            if (_registrationUrl is null && type.StartsWith("RegistrationsBaseUrl/3.6.0", StringComparison.Ordinal)) _registrationUrl = id;
        }
        _searchUrl ??= "https://azuresearch-usnc.nuget.org/query";
        _registrationUrl ??= "https://api.nuget.org/v3/registration5-gz-semver2/";
    }

    public async Task<IReadOnlyList<PackageSummary>> SearchAsync(string query, bool prerelease, int take = 30, CancellationToken ct = default)
    {
        await EnsureServicesAsync(ct);
        var url = $"{_searchUrl}?q={Uri.EscapeDataString(query)}&take={take}&prerelease={(prerelease ? "true" : "false")}&semVerLevel=2.0.0";
        var result = await _http.GetFromJsonAsync<SearchResponse>(url, Json, ct);
        return result?.Data.Select(d => new PackageSummary(
            d.Id, d.Version, d.Description, d.Authors is { Count: > 0 } a ? string.Join(", ", a) : null,
            d.TotalDownloads, d.IconUrl, d.ProjectUrl, d.Verified, d.Tags ?? new List<string>())).ToList()
            ?? (IReadOnlyList<PackageSummary>)Array.Empty<PackageSummary>();
    }

    /// <summary>Alle veröffentlichten Versionen, neueste zuerst.</summary>
    public async Task<IReadOnlyList<string>> GetVersionsAsync(string id, bool prerelease, CancellationToken ct = default)
    {
        var url = $"{FlatContainer}{id.ToLowerInvariant()}/index.json";
        try
        {
            var r = await _http.GetFromJsonAsync<VersionsResponse>(url, Json, ct);
            var list = (r?.Versions ?? new List<string>()).Where(v => prerelease || !v.Contains('-')).ToList();
            list.Sort((a, b) => CompareVersions(b, a));
            return list;
        }
        catch (HttpRequestException)
        {
            return Array.Empty<string>();
        }
    }

    public async Task<string?> GetLatestAsync(string id, bool prerelease, CancellationToken ct = default) =>
        (await GetVersionsAsync(id, prerelease, ct)).FirstOrDefault();

    /// <summary>Metadaten aus der .nuspec der Version.</summary>
    public async Task<PackageDetails?> GetDetailsAsync(string id, string version, CancellationToken ct = default)
    {
        var lid = id.ToLowerInvariant();
        var lv = version.ToLowerInvariant();
        try
        {
            var xml = await _http.GetStringAsync($"{FlatContainer}{lid}/{lv}/{lid}.nuspec", ct);
            var doc = XDocument.Parse(xml);
            var meta = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata");
            if (meta is null) return null;
            string? Get(string name) => meta.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();
            var license = meta.Elements().FirstOrDefault(e => e.Name.LocalName == "license");
            var deps = meta.Descendants().Where(e => e.Name.LocalName == "dependency")
                .Select(e => $"{e.Attribute("id")?.Value} {e.Attribute("version")?.Value}".Trim())
                .Distinct().Take(40).ToList();
            return new PackageDetails(
                Get("id") ?? id, Get("version") ?? version, Get("description"), Get("authors"), Get("projectUrl"),
                license?.Attribute("type")?.Value == "expression" ? license.Value.Trim() : null, Get("licenseUrl"), null, deps);
        }
        catch (HttpRequestException)
        {
            return null;
        }
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
