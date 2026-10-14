using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BlazorStudio.Deploy.Docker;

/// <summary>
/// Bausteine für Aufrufe der Docker-CLI: Argumente für <c>docker run</c>, Labels, Image-Namen und das Lesen der
/// JSON-Ausgaben. Ausgeführt wird in Studio – hier nur, was sich ohne Docker testen lässt.
/// </summary>
public static partial class DockerCli
{
    public const string LabelManaged = "blazorstudio.managed";
    public const string LabelVersion = "blazorstudio.version";
    public const string LabelProject = "blazorstudio.project";
    public const string LabelRun = "blazorstudio.run";
    public const string LabelDeployedAt = "blazorstudio.deployed";

    /// <summary>Suffix des vorherigen Containers (bleibt gestoppt liegen – Zurückschalten in Sekunden).</summary>
    public const string PreviousSuffix = "-previous";

    public static readonly string[] RestartPolicies = { "no", "always", "unless-stopped", "on-failure" };

    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,127}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^[a-z0-9]+([._-][a-z0-9]+)*(/[a-z0-9]+([._-][a-z0-9]+)*)*$")]
    private static partial Regex RepositoryPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"^((\d{1,3}\.){3}\d{1,3}:)?\d{1,5}(-\d{1,5})?:\d{1,5}(-\d{1,5})?(/(tcp|udp))?$")]
    private static partial Regex PortPattern();

    /// <summary>Gültiger Containername (Buchstaben, Ziffern, _ . -).</summary>
    public static bool IsValidName(string? name) => name is not null && NamePattern().IsMatch(name);

    /// <summary>Gültiger Repository-Name (klein geschrieben, z. B. „shop“ oder „team/shop“).</summary>
    public static bool IsValidRepository(string? repo) => repo is not null && repo.Length <= 200 && RepositoryPattern().IsMatch(repo);

    public static bool IsValidTag(string? tag) => tag is not null && TagPattern().IsMatch(tag);

    public static bool IsValidPort(string? port) => port is not null && PortPattern().IsMatch(port.Trim());

    /// <summary>Repository-Vorschlag aus einem Projektnamen: „Fro.BlazorTest2“ → „fro.blazortest2“.</summary>
    public static string SuggestRepository(string projectName)
    {
        var s = new string(projectName.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '-').ToArray());
        s = Regex.Replace(s, "[._-]{2,}", "-").Trim('.', '_', '-');
        return s.Length == 0 ? "app" : s;
    }

    /// <summary>Tag aus einer beliebigen Version (ungültige Zeichen → „-“).</summary>
    public static string ToTag(string version)
    {
        var s = new string(version.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' ? c : '-').ToArray()).TrimStart('.', '-');
        if (s.Length == 0) s = "latest";
        return s.Length > 128 ? s[..128] : s;
    }

    /// <summary>„registry/repo:tag“ bzw. „repo:tag“ ohne Registry.</summary>
    public static string ImageReference(string? registry, string repository, string tag)
    {
        var reg = (registry ?? "").Trim().TrimEnd('/');
        return (reg.Length > 0 ? reg + "/" : "") + repository + ":" + tag;
    }

    /// <summary>Repository-Teil einer Referenz („reg:5000/team/shop:1.0“ → „reg:5000/team/shop“).</summary>
    public static string RepositoryOf(string imageReference)
    {
        var at = imageReference.IndexOf('@');
        if (at >= 0) imageReference = imageReference[..at];
        var slash = imageReference.LastIndexOf('/');
        var colon = imageReference.LastIndexOf(':');
        return colon > slash ? imageReference[..colon] : imageReference;
    }

    public static string TagOf(string imageReference)
    {
        var slash = imageReference.LastIndexOf('/');
        var colon = imageReference.LastIndexOf(':');
        return colon > slash ? imageReference[(colon + 1)..] : "latest";
    }

    /// <summary>Argumente für <c>docker run -d</c> mit allen Einstellungen und den Studio-Labels.</summary>
    public static List<string> RunArguments(string containerName, string image, DockerRunSpec spec, string version, string? project, DateTimeOffset deployedAt)
    {
        var args = new List<string> { "run", "-d", "--name", containerName };
        foreach (var p in spec.Ports.Where(p => !string.IsNullOrWhiteSpace(p))) { args.Add("-p"); args.Add(p.Trim()); }
        foreach (var e in spec.Environment.Where(e => !string.IsNullOrWhiteSpace(e) && e.Contains('='))) { args.Add("-e"); args.Add(e.Trim()); }
        foreach (var v in spec.Volumes.Where(v => !string.IsNullOrWhiteSpace(v))) { args.Add("-v"); args.Add(v.Trim()); }
        if (!string.IsNullOrWhiteSpace(spec.RestartPolicy) && spec.RestartPolicy != "no") { args.Add("--restart"); args.Add(spec.RestartPolicy.Trim()); }
        if (!string.IsNullOrWhiteSpace(spec.Network)) { args.Add("--network"); args.Add(spec.Network.Trim()); }
        args.AddRange(new[] { "--label", LabelManaged + "=true" });
        args.AddRange(new[] { "--label", LabelVersion + "=" + version });
        if (!string.IsNullOrWhiteSpace(project)) args.AddRange(new[] { "--label", LabelProject + "=" + project });
        args.AddRange(new[] { "--label", LabelDeployedAt + "=" + deployedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) });
        args.AddRange(new[] { "--label", LabelRun + "=" + EncodeSpec(spec) });
        args.AddRange(spec.ExtraArgs.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()));
        args.Add(image);
        return args;
    }

    /// <summary>RunSpec als Base64url (passt in ein Label, ohne Komma – Docker trennt Labels mit Komma).</summary>
    public static string EncodeSpec(DockerRunSpec spec) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(spec))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static DockerRunSpec? DecodeSpec(string? encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded)) return null;
        try
        {
            var b64 = encoded.Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            return JsonSerializer.Deserialize<DockerRunSpec>(Encoding.UTF8.GetString(Convert.FromBase64String(b64)));
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            return null;
        }
    }

    /// <summary>„a=b,c=d“ → Wörterbuch (Werte dürfen „=“ enthalten).</summary>
    public static Dictionary<string, string> ParseLabels(string? labels)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(labels)) return map;
        foreach (var part in labels.Split(','))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            map[part[..eq].Trim()] = part[(eq + 1)..];
        }
        return map;
    }

    /// <summary>Ausgabe mit einem JSON-Objekt pro Zeile lesen (fehlerhafte Zeilen überspringen).</summary>
    public static List<T> ParseJsonLines<T>(string output)
    {
        var list = new List<T>();
        foreach (var line in output.Split('\n'))
        {
            var l = line.Trim();
            if (!l.StartsWith('{')) continue;
            try
            {
                if (JsonSerializer.Deserialize<T>(l, DockerJson.Options) is { } item) list.Add(item);
            }
            catch (JsonException)
            {
            }
        }
        return list;
    }

    /// <summary>„2026-10-14 16:39:01 +0200 CEST“ → Zeitpunkt.</summary>
    public static DateTimeOffset? ParseDockerTime(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3 && DateTimeOffset.TryParseExact(parts[0] + " " + parts[1] + " " + parts[2], "yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
            return t;
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t2) ? t2 : null;
    }

    /// <summary>„12.5%“ → 12.5.</summary>
    public static double ParsePercent(string? text) =>
        double.TryParse((text ?? "").Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    /// <summary>Docker-Host-Adresse prüfen: leer (lokal), ssh://user@host[:port], tcp://host:port, npipe://…, unix://….</summary>
    public static bool IsValidEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return true;
        return Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "ssh" or "tcp" or "npipe" or "unix" && (uri.Scheme is "npipe" or "unix" || uri.Host.Length > 0);
    }

    /// <summary>Für die HTTP-Prüfung nach dem Start: Host-Teil eines Endpoints („ssh://deploy@web01“ → „web01“, lokal → „localhost“).</summary>
    public static string HostNameOf(string? endpoint) =>
        Uri.TryCreate(endpoint ?? "", UriKind.Absolute, out var uri) && uri.Host.Length > 0 && uri.Scheme is "ssh" or "tcp" ? uri.Host : "localhost";

    /// <summary>Globale Argumente für einen entfernten Docker-Host (leer = lokaler Docker).</summary>
    public static List<string> HostArguments(string? endpoint) =>
        string.IsNullOrWhiteSpace(endpoint) ? new List<string>() : new List<string> { "-H", endpoint.Trim() };

    /// <summary>Erster veröffentlichter Host-Port („8080:80“ → 8080, „127.0.0.1:5000:8080“ → 5000, „8000-8001:80-81“ → 8000).</summary>
    public static int? FirstHostPort(DockerRunSpec spec)
    {
        foreach (var p in spec.Ports)
        {
            if (!IsValidPort(p)) continue;
            var parts = p.Trim().Split('/')[0].Split(':');
            var host = parts.Length == 3 ? parts[1] : parts[0];
            if (int.TryParse(host.Split('-')[0], NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and < 65536) return port;
        }
        return null;
    }

    /// <summary>URL für die Prüfung nach dem Start (null, wenn kein Port veröffentlicht oder kein Pfad gesetzt ist).</summary>
    public static string? HealthUrl(string? endpoint, DockerRunSpec spec, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || FirstHostPort(spec) is not { } port) return null;
        var p = path.Trim();
        if (p.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || p.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return p;
        return $"http://{HostNameOf(endpoint)}:{port}{(p.StartsWith('/') ? p : "/" + p)}";
    }

    /// <summary>Zusätzliche run-Argumente: eine Zeile darf mehrere, durch Leerzeichen getrennte Argumente enthalten.</summary>
    public static List<string> SplitArguments(string? text) =>
        Lines(text).SelectMany(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();

    /// <summary>Zeilen „KEY=VALUE“, „8080:8080“ … aus einem Textfeld (leere und #-Zeilen ignoriert).</summary>
    public static List<string> Lines(string? text) =>
        (text ?? "").Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();

    /// <summary>Dockerfile-Kontext: Visual-Studio-Dockerfiles kopieren Pfade relativ zur Projektmappe.</summary>
    public static string BuildContext(string dockerfile, string projectFile, string? solutionDirectory)
    {
        var projectDir = Path.GetDirectoryName(projectFile)!;
        if (solutionDirectory is null || string.Equals(Path.GetFullPath(solutionDirectory), Path.GetFullPath(projectDir), StringComparison.OrdinalIgnoreCase)) return projectDir;
        string text;
        try { text = File.ReadAllText(dockerfile); }
        catch (IOException) { return projectDir; }
        var relative = Path.GetRelativePath(solutionDirectory, projectFile).Replace('\\', '/');
        return text.Contains(relative, StringComparison.OrdinalIgnoreCase) ? solutionDirectory : projectDir;
    }

    /// <summary>Dockerfile des Projekts (neben der .csproj) oder null.</summary>
    public static string? FindDockerfile(string projectFile)
    {
        var dir = Path.GetDirectoryName(projectFile)!;
        var file = Path.Combine(dir, "Dockerfile");
        return File.Exists(file) ? file : null;
    }

    /// <summary>Argumente für <c>dotnet publish /t:PublishContainer</c>.</summary>
    public static List<string> SdkPublishArguments(string projectFile, string repository, string tag, string? registry, string configuration, string? runtimeIdentifier)
    {
        var args = new List<string>
        {
            "publish", projectFile, "-c", configuration, "-nologo", "/t:PublishContainer",
            "-p:ContainerRepository=" + repository,
            "-p:ContainerImageTag=" + tag,
        };
        if (!string.IsNullOrWhiteSpace(registry)) args.Add("-p:ContainerRegistry=" + registry.Trim().TrimEnd('/'));
        args.Add("--os");
        args.Add("linux");
        if (!string.IsNullOrWhiteSpace(runtimeIdentifier) && runtimeIdentifier.Contains("arm64", StringComparison.Ordinal))
        {
            args.Add("--arch");
            args.Add("arm64");
        }
        return args;
    }
}
