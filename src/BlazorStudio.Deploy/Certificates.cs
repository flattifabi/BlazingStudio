using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BlazorStudio.Deploy;

/// <summary>Alles, was der Agent über Zertifikate auf dem Server weiß (GET /api/certificates).</summary>
public sealed class CertificateOverview
{
    public string MachineName { get; set; } = "";

    public DateTime ScannedAt { get; set; }

    public WinAcmeInfo WinAcme { get; set; } = new();

    public List<CertificateInfo> Certificates { get; set; } = new();

    public List<AcmeRenewalInfo> Renewals { get; set; } = new();

    /// <summary>Gefundene nginx-Installation (nginx.exe) – null, wenn keine.</summary>
    public string? NginxExe { get; set; }

    /// <summary>Gelesene nginx-Konfigurationsdateien.</summary>
    public List<string> NginxConfigs { get; set; } = new();

    /// <summary>Hinweise aus dem Scan (Dateien nicht lesbar usw.).</summary>
    public List<string> Notes { get; set; } = new();
}

public sealed class WinAcmeInfo
{
    public bool Installed { get; set; }

    public string? ExePath { get; set; }

    public string? Version { get; set; }

    public string? SettingsPath { get; set; }

    /// <summary>Ordner mit den *.renewal.json.</summary>
    public string? ConfigPath { get; set; }

    public string? BaseUri { get; set; }

    /// <summary>ScheduledTask.RenewalDays – nach so vielen Tagen wird erneuert.</summary>
    public int? RenewalDays { get; set; }

    /// <summary>ScheduledTask.RenewalMinimumValidDays – spätestens so viele Tage vor Ablauf.</summary>
    public int? RenewalMinimumValidDays { get; set; }

    /// <summary>Order.DefaultValidDays – gewünschte Laufzeit (muss die CA unterstützen).</summary>
    public int? DefaultValidDays { get; set; }

    public string? TaskName { get; set; }

    public string? TaskState { get; set; }

    public DateTime? TaskLastRun { get; set; }

    /// <summary>0 = erfolgreich.</summary>
    public long? TaskLastResult { get; set; }

    public DateTime? TaskNextRun { get; set; }
}

/// <summary>Wo ein Zertifikat verwendet wird.</summary>
public sealed class CertificateUsage
{
    /// <summary>iis, nginx, service, winacme.</summary>
    public string Kind { get; set; } = "";

    /// <summary>Site, Dienstname, nginx-Datei, Erneuerungs-ID.</summary>
    public string Target { get; set; } = "";

    /// <summary>Bindung, server_name, Pfad …</summary>
    public string? Detail { get; set; }

    /// <summary>Hostname:Port, unter dem das Zertifikat ausgeliefert werden sollte.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Fingerabdruck, den der Server unter <see cref="Endpoint"/> tatsächlich ausliefert (null = nicht geprüft/erreichbar).</summary>
    public string? ServedThumbprint { get; set; }
}

public sealed class CertificateInfo
{
    public string Thumbprint { get; set; } = "";

    public string Subject { get; set; } = "";

    public List<string> DnsNames { get; set; } = new();

    public string Issuer { get; set; } = "";

    public DateTime NotBefore { get; set; }

    public DateTime NotAfter { get; set; }

    public bool HasPrivateKey { get; set; }

    public string? FriendlyName { get; set; }

    /// <summary>„store:My“, „store:WebHosting“ oder „file“.</summary>
    public string Source { get; set; } = "";

    /// <summary>Dateipfad bei <see cref="Source"/> = file.</summary>
    public string? Path { get; set; }

    /// <summary>Datei konnte nicht gelesen werden (z. B. PFX mit unbekanntem Passwort).</summary>
    public string? Error { get; set; }

    /// <summary>Zugehörige win-acme-Erneuerung.</summary>
    public string? RenewalId { get; set; }

    public List<CertificateUsage> Usages { get; set; } = new();

    public int ValidityDays => (int)Math.Round((NotAfter - NotBefore).TotalDays);

    public string DisplayName => DnsNames.Count > 0 ? DnsNames[0] : Subject.StartsWith("CN=", StringComparison.OrdinalIgnoreCase) ? Subject[3..].Split(',')[0] : Subject;
}

/// <summary>Eine win-acme-Erneuerung (*.renewal.json).</summary>
public sealed class AcmeRenewalInfo
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public List<string> Hosts { get; set; } = new();

    public DateTime? LastRun { get; set; }

    public bool? LastSuccess { get; set; }

    public string? LastError { get; set; }

    public DateTime? LastSuccessAt { get; set; }

    public DateTime? Expires { get; set; }

    public List<string> Thumbprints { get; set; } = new();

    /// <summary>Wann win-acme laut Einstellungen erneuert.</summary>
    public DateTime? Due { get; set; }

    /// <summary>Ablageorte, z. B. „Zertifikatsspeicher“, „PEM: C:\certs“.</summary>
    public List<string> Stores { get; set; } = new();

    /// <summary>Ordner/Dateien der PEM-/PFX-Ablage.</summary>
    public List<string> StorePaths { get; set; } = new();

    /// <summary>Installationsschritte, z. B. „IIS“, „Skript: …“.</summary>
    public List<string> Installations { get; set; } = new();

    public string? Validation { get; set; }

    public string File { get; set; } = "";
}

/// <summary>Firmenrichtlinie: Laufzeit und Erneuerung.</summary>
public sealed class CertificatePolicy
{
    /// <summary>Maximale Laufzeit eines Zertifikats in Tagen (z. B. 30).</summary>
    public int ValidDays { get; set; } = 30;

    /// <summary>So viele Tage vor Ablauf wird spätestens erneuert / gewarnt.</summary>
    public int RenewBeforeDays { get; set; } = 7;

    /// <summary>Laufzeit bei der CA anfordern (win-acme Order.DefaultValidDays → notAfter). Nur, wenn die CA das unterstützt.</summary>
    public bool RequestValidDays { get; set; }

    /// <summary>Werte für win-acme: nach (Laufzeit − Vorlauf) Tagen erneuern, spätestens Vorlauf Tage vor Ablauf.</summary>
    public (int RenewalDays, int MinimumValidDays) ForWinAcme()
    {
        var before = Math.Clamp(RenewBeforeDays, 1, Math.Max(1, ValidDays - 1));
        return (Math.Max(1, ValidDays - before), before);
    }

    public void Normalize()
    {
        ValidDays = Math.Clamp(ValidDays, 2, 3650);
        RenewBeforeDays = Math.Clamp(RenewBeforeDays, 1, ValidDays - 1);
    }

    public CertificateState Evaluate(CertificateInfo c, DateTime nowUtc)
    {
        if (c.Error is not null && c.NotAfter == default) return CertificateState.Unknown;
        if (c.NotAfter <= nowUtc) return CertificateState.Expired;
        if ((c.NotAfter - nowUtc).TotalDays <= RenewBeforeDays) return CertificateState.ExpiresSoon;
        // Eine Stunde Spielraum: CAs datieren notBefore oft etwas zurück.
        if (c.ValidityDays > ValidDays + 1) return CertificateState.TooLong;
        if (c.Usages.Any(u => u.ServedThumbprint is { Length: > 0 } t && !t.Equals(c.Thumbprint, StringComparison.OrdinalIgnoreCase))) return CertificateState.Stale;
        return CertificateState.Ok;
    }
}

public enum CertificateState
{
    Ok,
    /// <summary>Laufzeit länger als die Richtlinie erlaubt.</summary>
    TooLong,
    /// <summary>Unter einem Endpunkt wird ein anderes Zertifikat ausgeliefert (z. B. nginx nicht neu geladen).</summary>
    Stale,
    ExpiresSoon,
    Expired,
    Unknown,
}

/// <summary>Neues Zertifikat über win-acme bestellen (POST /api/certificates/renewals).</summary>
public sealed class AcmeOrderRequest
{
    public List<string> Hosts { get; set; } = new();

    public string? FriendlyName { get; set; }

    /// <summary>selfhosting (HTTP über http.sys), filesystem (Webroot), none (eigene Argumente).</summary>
    public string Validation { get; set; } = "selfhosting";

    public int? ValidationPort { get; set; }

    public string? WebRoot { get; set; }

    /// <summary>Im Windows-Zertifikatsspeicher ablegen (nötig für IIS).</summary>
    public bool CertificateStore { get; set; } = true;

    /// <summary>IIS-Site (ID), deren https-Bindungen das Zertifikat bekommen.</summary>
    public long? IisSiteId { get; set; }

    /// <summary>Ordner für PEM-Dateien (nginx).</summary>
    public string? PemPath { get; set; }

    /// <summary>Ordner für die PFX-Datei (Dienste/Kestrel).</summary>
    public string? PfxPath { get; set; }

    /// <summary>Passwort der PFX-Datei – win-acme speichert es verschlüsselt; Studio speichert es nicht.</summary>
    public string? PfxPassword { get; set; }

    /// <summary>Nach jeder Erneuerung diesen Windows-Dienst neu starten.</summary>
    public string? RestartService { get; set; }

    /// <summary>Nach jeder Erneuerung nginx neu laden.</summary>
    public bool ReloadNginx { get; set; }

    /// <summary>Zusätzliche win-acme-Argumente (Profis).</summary>
    public string? ExtraArguments { get; set; }
}

/// <summary>Ein win-acme-Lauf, den der Agent gestartet hat.</summary>
public sealed class CertificateJob
{
    public string Id { get; set; } = "";

    /// <summary>renew, create, cancel, nginx.</summary>
    public string Kind { get; set; } = "";

    public string Title { get; set; } = "";

    /// <summary>running, succeeded, failed.</summary>
    public string State { get; set; } = "running";

    public DateTime Started { get; set; }

    public DateTime? Finished { get; set; }

    public int? ExitCode { get; set; }

    /// <summary>Aufruf ohne Passwörter.</summary>
    public string CommandLine { get; set; } = "";

    public List<string> Log { get; set; } = new();

    public bool IsFinished => State != "running";
}

/// <summary>win-acme-Aufrufe zusammenbauen (testbar, ohne Prozessstart).</summary>
public static class WinAcmeArguments
{
    public const string Masked = "********";

    public static List<string> Renew(string id) => new() { "--renew", "--id", id, "--force", "--nocache" };

    public static List<string> Cancel(string id) => new() { "--cancel", "--id", id };

    /// <summary>Argumente für eine neue Erneuerung. <paramref name="hookScript"/> = Cert-Hook.ps1 des Agents, <paramref name="nginxExe"/> für das Neuladen.</summary>
    public static List<string> Create(AcmeOrderRequest r, string? hookScript, string? nginxExe)
    {
        var hosts = r.Hosts.Select(h => h.Trim().ToLowerInvariant()).Where(h => h.Length > 0).Distinct().ToList();
        if (hosts.Count == 0) throw new ArgumentException("Mindestens ein Hostname ist nötig.");
        foreach (var h in hosts)
            if (!IsHostName(h)) throw new ArgumentException($"„{h}“ ist kein gültiger Hostname.");

        var args = new List<string> { "--source", "manual", "--host", string.Join(",", hosts) };
        if (!string.IsNullOrWhiteSpace(r.FriendlyName)) args.AddRange(new[] { "--friendlyname", r.FriendlyName.Trim() });

        switch (r.Validation)
        {
            case "selfhosting":
                args.AddRange(new[] { "--validation", "selfhosting" });
                if (r.ValidationPort is { } port and > 0 and < 65536) args.AddRange(new[] { "--validationport", port.ToString(CultureInfo.InvariantCulture) });
                break;
            case "filesystem":
                if (string.IsNullOrWhiteSpace(r.WebRoot)) throw new ArgumentException("Für die Prüfung über das Dateisystem fehlt der Webroot-Ordner.");
                args.AddRange(new[] { "--validation", "filesystem", "--webroot", r.WebRoot.Trim() });
                break;
            case "none":
                break;
            default:
                throw new ArgumentException($"Unbekannte Prüfung „{r.Validation}“.");
        }

        var stores = new List<string>();
        if (r.CertificateStore || r.IisSiteId is not null) stores.Add("certificatestore");
        if (!string.IsNullOrWhiteSpace(r.PemPath)) stores.Add("pemfiles");
        if (!string.IsNullOrWhiteSpace(r.PfxPath)) stores.Add("pfxfile");
        if (stores.Count == 0) throw new ArgumentException("Bitte mindestens einen Ablageort wählen (Zertifikatsspeicher, PEM oder PFX).");
        args.AddRange(new[] { "--store", string.Join(",", stores) });
        if (!string.IsNullOrWhiteSpace(r.PemPath)) args.AddRange(new[] { "--pemfilespath", r.PemPath.Trim() });
        if (!string.IsNullOrWhiteSpace(r.PfxPath))
        {
            args.AddRange(new[] { "--pfxfilepath", r.PfxPath.Trim() });
            if (!string.IsNullOrEmpty(r.PfxPassword)) args.AddRange(new[] { "--pfxpassword", r.PfxPassword });
        }

        var installations = new List<string>();
        if (r.IisSiteId is { } site) installations.Add("iis");
        var hook = HookParameters(r, nginxExe);
        if (hook.Length > 0)
        {
            if (string.IsNullOrWhiteSpace(hookScript)) throw new InvalidOperationException("Cert-Hook.ps1 des Agents fehlt.");
            installations.Add("script");
        }
        if (installations.Count > 0)
        {
            args.AddRange(new[] { "--installation", string.Join(",", installations) });
            if (r.IisSiteId is { } id) args.AddRange(new[] { "--installationsiteid", id.ToString(CultureInfo.InvariantCulture) });
            if (hook.Length > 0) args.AddRange(new[] { "--script", hookScript!, "--scriptparameters", hook });
        }
        else
        {
            args.AddRange(new[] { "--installation", "none" });
        }

        if (!string.IsNullOrWhiteSpace(r.ExtraArguments)) args.AddRange(SplitArguments(r.ExtraArguments));
        args.Add("--accepttos");
        return args;
    }

    /// <summary>Parameter für Cert-Hook.ps1 (Dienst neu starten, nginx neu laden).</summary>
    public static string HookParameters(AcmeOrderRequest r, string? nginxExe)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(r.RestartService))
        {
            var svc = r.RestartService.Trim();
            if (svc.Contains('\'') || svc.Contains('"')) throw new ArgumentException("Ungültiger Dienstname.");
            parts.Add($"-Service '{svc}'");
        }
        if (r.ReloadNginx)
        {
            if (string.IsNullOrWhiteSpace(nginxExe)) throw new ArgumentException("Auf diesem Server wurde kein nginx gefunden.");
            parts.Add($"-Nginx '{nginxExe.Replace("'", "''")}'");
        }
        return string.Join(" ", parts);
    }

    /// <summary>Aufruf zum Anzeigen/Protokollieren – Passwörter werden ersetzt.</summary>
    public static string Display(string exe, IReadOnlyList<string> args)
    {
        var sb = new StringBuilder(Quote(exe));
        for (var i = 0; i < args.Count; i++)
        {
            sb.Append(' ');
            var secret = i > 0 && args[i - 1].Equals("--pfxpassword", StringComparison.OrdinalIgnoreCase);
            sb.Append(secret ? Masked : Quote(args[i]));
        }
        return sb.ToString();
    }

    private static string Quote(string s) => s.Length > 0 && !s.Any(c => char.IsWhiteSpace(c) || c == '"') ? s : "\"" + s.Replace("\"", "\\\"") + "\"";

    public static bool IsHostName(string h) =>
        h.Length is > 0 and <= 253 && h.Split('.').All((p) => p.Length is > 0 and <= 63 && (p == "*" || p.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) && !p.StartsWith('-') && !p.EndsWith('-'));

    /// <summary>Einfaches Aufteilen wie in der Kommandozeile (Anführungszeichen gruppieren).</summary>
    public static List<string> SplitArguments(string text)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        var any = false;
        foreach (var c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
                any = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any || sb.Length > 0) result.Add(sb.ToString());
                sb.Clear();
                any = false;
            }
            else
            {
                sb.Append(c);
            }
        }
        if (any || sb.Length > 0) result.Add(sb.ToString());
        return result;
    }
}

/// <summary>Liest *.renewal.json von win-acme (2.1/2.2), ohne verschlüsselte Werte anzufassen.</summary>
public static class WinAcmeRenewalParser
{
    private static readonly JsonDocumentOptions Options = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    public static AcmeRenewalInfo Parse(string json, string file)
    {
        var root = JsonNode.Parse(json, documentOptions: Options) as JsonObject ?? throw new JsonException("Kein JSON-Objekt.");
        var info = new AcmeRenewalInfo
        {
            File = file,
            Id = Str(root, "Id") ?? System.IO.Path.GetFileName(file).Split('.')[0],
            Name = Str(root, "LastFriendlyName") ?? "",
        };

        // Hostnamen aus dem Ziel (Manual: CommonName/AlternativeNames, IIS: Host/IncludeHosts)
        if (Prop(root, "TargetPluginOptions") is { } target)
        {
            var hosts = new List<string>();
            foreach (var key in new[] { "CommonName", "Host", "AlternativeNames", "IncludeHosts" })
                foreach (var s in Strings(Prop(target, key)))
                    hosts.AddRange(s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            info.Hosts = hosts.Select(h => h.ToLowerInvariant()).Distinct().ToList();
        }
        if (info.Hosts.Count == 0 && info.Name.Length > 0)
        {
            // „[Manual] shop.example.com, www.example.com @ 2026/1/1“
            var name = info.Name.Split(" @ ")[0];
            if (name.StartsWith('[') && name.IndexOf(']') is > 0 and var close) name = name[(close + 1)..];
            info.Hosts = name.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(WinAcmeArguments.IsHostName).ToList();
        }
        if (Prop(root, "ValidationPluginOptions") is JsonObject validation)
            info.Validation = PluginName(validation, "ValidationPlugins");

        foreach (var (key, value) in root)
        {
            if (key.StartsWith("StorePluginOptions", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var store in Objects(value))
                {
                    var type = PluginName(store, "StorePlugins") ?? "?";
                    var path = Str(store, "Path");
                    var label = type switch
                    {
                        var t when t.Contains("PemFiles", StringComparison.OrdinalIgnoreCase) => "PEM",
                        var t when t.Contains("PfxFile", StringComparison.OrdinalIgnoreCase) => "PFX",
                        var t when t.Contains("CentralSsl", StringComparison.OrdinalIgnoreCase) => "CentralSSL",
                        var t when t.Contains("CertificateStore", StringComparison.OrdinalIgnoreCase) => "Zertifikatsspeicher",
                        var t => t,
                    };
                    info.Stores.Add(path is { Length: > 0 } ? $"{label}: {path}" : label + (Str(store, "StoreName") is { } sn ? $" ({sn})" : ""));
                    if (path is { Length: > 0 }) info.StorePaths.Add(path);
                }
            }
            else if (key.StartsWith("InstallationPluginOptions", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var inst in Objects(value))
                {
                    var type = PluginName(inst, "InstallationPlugins") ?? "?";
                    if (Str(inst, "Script") is { Length: > 0 } script)
                        info.Installations.Add($"Skript: {script[(script.LastIndexOfAny(new[] { '\\', '/' }) + 1)..]} {Str(inst, "ScriptParameters")}".TrimEnd());
                    else if (type.Contains("IIS", StringComparison.OrdinalIgnoreCase))
                        info.Installations.Add("IIS" + (Prop(inst, "SiteId") is { } id ? $" (Site {id})" : ""));
                    else if (!type.Equals("Null", StringComparison.OrdinalIgnoreCase) && !type.Contains("None", StringComparison.OrdinalIgnoreCase))
                        info.Installations.Add(type);
                }
            }
        }

        if (Prop(root, "History") is JsonArray history && history.Count > 0)
        {
            var entries = history.OfType<JsonObject>().Select(h => (Node: h, Date: Date(Prop(h, "Date")))).OrderBy(e => e.Date ?? DateTime.MinValue).ToList();
            var last = entries[^1].Node;
            info.LastRun = entries[^1].Date;
            info.LastSuccess = Bool(last, "Success");
            var errors = new List<string>();
            CollectErrors(last, errors);
            info.LastError = errors.Count > 0 ? string.Join(" · ", errors.Distinct()) : null;
            var success = entries.LastOrDefault(e => Bool(e.Node, "Success") == true);
            if (success.Node is { } ok)
            {
                info.LastSuccessAt = success.Date;
                var thumbs = new List<string>();
                DateTime? expires = null;
                Walk(ok, (k, v) =>
                {
                    if (k.StartsWith("Thumbprint", StringComparison.OrdinalIgnoreCase)) thumbs.AddRange(Strings(v).Where(t => t.Length >= 32));
                    if (k.Equals("ExpireDate", StringComparison.OrdinalIgnoreCase) && Date(v) is { } d && (expires is null || d > expires)) expires = d;
                });
                info.Thumbprints = thumbs.Select(t => t.ToUpperInvariant()).Distinct().ToList();
                info.Expires = expires;
            }
        }
        return info;
    }

    /// <summary>Fälligkeit wie win-acme: nach RenewalDays seit der letzten erfolgreichen Erneuerung, spätestens MinimumValidDays vor Ablauf.</summary>
    public static DateTime? Due(AcmeRenewalInfo r, int? renewalDays, int? minimumValidDays)
    {
        if (r.LastSuccessAt is not { } last) return null;
        DateTime? due = last.AddDays(renewalDays ?? 55);
        if (minimumValidDays is { } min && r.Expires is { } exp && exp.AddDays(-min) < due) due = exp.AddDays(-min);
        return due;
    }

    private static void CollectErrors(JsonNode? node, List<string> errors) => Walk(node, (k, v) =>
    {
        if (k.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
            errors.AddRange(Strings(v).Where(s => s.Length > 0));
    });

    private static void Walk(JsonNode? node, Action<string, JsonNode?> visit)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (k, v) in o)
                {
                    visit(k, v);
                    if (v is JsonObject or JsonArray) Walk(v, visit);
                }
                break;
            case JsonArray a:
                foreach (var item in a) Walk(item, visit);
                break;
        }
    }

    private static JsonNode? Prop(JsonNode? node, string name) =>
        node is JsonObject o ? o.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value : null;

    private static string? Str(JsonNode? node, string name) => Prop(node, name) is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool? Bool(JsonNode? node, string name) => Prop(node, name) is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    private static DateTime? Date(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<DateTime>(out var d)) return d.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : d.ToUniversalTime();
        return v.TryGetValue<string>(out var s) && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var p) ? p : null;
    }

    private static IEnumerable<string> Strings(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => new[] { s },
        JsonArray a => a.SelectMany(Strings),
        _ => Array.Empty<string>(),
    };

    private static IEnumerable<JsonObject> Objects(JsonNode? node) => node switch
    {
        JsonObject o => new[] { o },
        JsonArray a => a.OfType<JsonObject>(),
        _ => Array.Empty<JsonObject>(),
    };

    /// <summary>„PKISharp.WACS.Plugins.StorePlugins.PemFilesOptions, wacs“ → „PemFiles“.</summary>
    private static string? PluginName(JsonObject o, string ns)
    {
        var type = Str(o, "$type");
        if (type is null) return Str(o, "Name");
        var name = type.Split(',')[0];
        name = name[(name.LastIndexOf('.') + 1)..];
        return name.EndsWith("Options", StringComparison.Ordinal) ? name[..^"Options".Length] : name;
    }
}

/// <summary>Einfacher nginx-Konfigurationsleser: server-Blöcke mit server_name, listen und ssl_certificate sowie include.</summary>
public static class NginxConfigParser
{
    public sealed class Server
    {
        public List<string> Names { get; } = new();
        public List<string> Certificates { get; } = new();
        public List<int> SslPorts { get; } = new();
    }

    public sealed class Result
    {
        public List<Server> Servers { get; } = new();
        public List<string> Includes { get; } = new();
        /// <summary>ssl_certificate außerhalb eines server-Blocks (http-Ebene, gilt als Standard).</summary>
        public List<string> DefaultCertificates { get; } = new();
    }

    public static Result Parse(string text)
    {
        var result = new Result();
        var stack = new Stack<(string Name, Server? Server)>();
        var tokens = new List<string>();
        foreach (var (token, kind) in Tokenize(text))
        {
            switch (kind)
            {
                case '{':
                    var block = tokens.Count > 0 ? tokens[0] : "";
                    stack.Push((block, block == "server" ? new Server() : null));
                    tokens.Clear();
                    break;
                case '}':
                    if (stack.Count > 0 && stack.Pop() is { Server: { } done }) result.Servers.Add(done);
                    tokens.Clear();
                    break;
                case ';':
                    Statement(tokens, stack.Count > 0 ? stack.FirstOrDefault(s => s.Server is not null).Server : null, result);
                    tokens.Clear();
                    break;
                default:
                    tokens.Add(token);
                    break;
            }
        }
        return result;
    }

    private static void Statement(List<string> t, Server? server, Result result)
    {
        if (t.Count < 2) return;
        switch (t[0])
        {
            case "include":
                result.Includes.Add(t[1]);
                break;
            case "server_name" when server is not null:
                server.Names.AddRange(t.Skip(1).Where(n => n != "_" && n.Length > 0));
                break;
            case "ssl_certificate":
                if (t[1].StartsWith("data:", StringComparison.Ordinal)) return;
                (server?.Certificates ?? result.DefaultCertificates).Add(t[1]);
                break;
            case "listen" when server is not null:
                if (t.Skip(2).Contains("ssl") || t.Skip(2).Contains("quic"))
                {
                    var addr = t[1];
                    var port = addr.LastIndexOf(':') is >= 0 and var i && !addr.EndsWith(']') ? addr[(i + 1)..] : addr;
                    if (int.TryParse(port, out var p)) server.SslPorts.Add(p);
                }
                break;
        }
    }

    private static IEnumerable<(string Token, char Kind)> Tokenize(string text)
    {
        var sb = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '#')
            {
                while (i < text.Length && text[i] != '\n') i++;
                continue;
            }
            if (c is '"' or '\'')
            {
                var quote = c;
                i++;
                while (i < text.Length && text[i] != quote)
                {
                    if (text[i] == '\\' && i + 1 < text.Length) i++;
                    sb.Append(text[i++]);
                }
                i++;
                yield return (sb.ToString(), 't');
                sb.Clear();
                continue;
            }
            if (char.IsWhiteSpace(c) || c is ';' or '{' or '}')
            {
                if (sb.Length > 0)
                {
                    yield return (sb.ToString(), 't');
                    sb.Clear();
                }
                if (c is ';' or '{' or '}') yield return ("", c);
                i++;
                continue;
            }
            sb.Append(c);
            i++;
        }
        if (sb.Length > 0) yield return (sb.ToString(), 't');
    }
}

/// <summary>Zertifikate in appsettings*.json (Kestrel:Certificates und Kestrel:Endpoints:*:Certificate).</summary>
public static class KestrelCertificates
{
    public sealed record Reference(string Path, string? Password, string? KeyPath, string Section);

    public static List<Reference> Find(string json)
    {
        var list = new List<Reference>();
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException)
        {
            return list;
        }
        if (root is not JsonObject o || Child(o, "Kestrel") is not JsonObject kestrel) return list;
        if (Child(kestrel, "Certificates") is JsonObject certs)
            foreach (var (name, cert) in certs)
                Add(cert, "Kestrel:Certificates:" + name);
        if (Child(kestrel, "Endpoints") is JsonObject endpoints)
            foreach (var (name, ep) in endpoints)
                if (ep is JsonObject e && Child(e, "Certificate") is { } cert)
                    Add(cert, $"Kestrel:Endpoints:{name}:Certificate");
        return list;

        void Add(JsonNode? cert, string section)
        {
            if (cert is not JsonObject c || Value(c, "Path") is not { Length: > 0 } path) return;
            list.Add(new Reference(path, Value(c, "Password"), Value(c, "KeyPath"), section));
        }
    }

    private static JsonNode? Child(JsonObject o, string name) => o.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    private static string? Value(JsonObject o, string name) => Child(o, name) is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
