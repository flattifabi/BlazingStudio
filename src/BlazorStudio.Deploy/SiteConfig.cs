using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace BlazorStudio.Deploy;

/// <summary>Ergebnis von <see cref="SiteConfig.EnsureStdout"/>.</summary>
public enum StdoutPatch
{
    /// <summary>stdout war schon eingeschaltet und zeigt auf einen festen Pfad.</summary>
    Unchanged,

    Updated,

    /// <summary>Kein &lt;aspNetCore&gt;-Element – keine ASP.NET-Core-App (z. B. statische Seite).</summary>
    NoAspNetCore,
}

/// <summary>
/// Konfigurationsdateien einer Site, die remote bearbeitet werden können (web.config, appsettings*.json).
/// Bearbeitete Dateien liegen als „Überschreibung“ neben den Versionen und werden bei jedem Deployment
/// über die Dateien aus dem Paket gelegt.
/// </summary>
public static partial class SiteConfig
{
    /// <summary>Ordner der Überschreibungen in &lt;DeployRoot&gt;\&lt;Site[_App]&gt;\ (beginnt mit „_“ – ist also nie eine Version).</summary>
    public const string OverrideFolder = "_config";

    /// <summary>Ordner der stdout-Logs (bleibt über Versionen hinweg erhalten).</summary>
    public const string LogFolder = "_logs";

    public const string WebConfig = "web.config";

    public const string AppSettings = "appsettings.json";

    /// <summary>Höchstens so viel wird von einem Log zurückgegeben (das Ende der Datei).</summary>
    public const int MaxLogBytes = 512 * 1024;

    [GeneratedRegex(@"^(web\.config|appsettings(\.[A-Za-z0-9_\-]{1,40})?\.json)$", RegexOptions.IgnoreCase)]
    private static partial Regex FilePattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.\-]{0,120}\.log$", RegexOptions.IgnoreCase)]
    private static partial Regex LogPattern();

    /// <summary>Nur web.config und appsettings[.Name].json – nie ein Pfad.</summary>
    public static bool IsAllowedFile(string? name) => name is not null && FilePattern().IsMatch(name);

    public static bool IsLogFile(string? name) => name is not null && LogPattern().IsMatch(name) && !name.Contains("..", StringComparison.Ordinal);

    /// <summary>Prüft, ob der Inhalt gültiges XML (web.config) bzw. JSON (appsettings) ist. Null = in Ordnung.</summary>
    public static string? Validate(string name, string content)
    {
        if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var _ = JsonDocument.Parse(content, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                return null;
            }
            catch (JsonException e)
            {
                return $"{name}: kein gültiges JSON – {e.Message}";
            }
        }
        try
        {
            var doc = XDocument.Parse(content);
            if (doc.Root?.Name.LocalName != "configuration") return $"{name}: Wurzelelement muss <configuration> sein.";
            return null;
        }
        catch (XmlException e)
        {
            return $"{name}: kein gültiges XML – {e.Message}";
        }
    }

    /// <summary>
    /// Schaltet in allen &lt;aspNetCore&gt;-Elementen stdoutLogEnabled ein. Ein fehlender oder relativer
    /// stdoutLogFile (Standard „.\logs\stdout“ – läge im Versionsordner, auf den der App-Pool meist nicht schreiben darf)
    /// wird auf <paramref name="logFile"/> gesetzt; ein absoluter Pfad bleibt.
    /// </summary>
    public static (string Xml, StdoutPatch Result) EnsureStdout(string xml, string logFile)
    {
        var doc = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        var modules = doc.Descendants().Where(e => e.Name.LocalName == "aspNetCore").ToList();
        if (modules.Count == 0) return (xml, StdoutPatch.NoAspNetCore);
        var changed = false;
        foreach (var m in modules)
        {
            if (!string.Equals((string?)m.Attribute("stdoutLogEnabled"), "true", StringComparison.OrdinalIgnoreCase))
            {
                m.SetAttributeValue("stdoutLogEnabled", "true");
                changed = true;
            }
            var file = (string?)m.Attribute("stdoutLogFile");
            if (string.IsNullOrWhiteSpace(file) || !IsAbsoluteWindowsPath(file))
            {
                m.SetAttributeValue("stdoutLogFile", logFile);
                changed = true;
            }
        }
        if (!changed) return (xml, StdoutPatch.Unchanged);
        var newline = xml.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var sb = new StringBuilder();
        // Whitespace zwischen Deklaration und Wurzel ist (mit PreserveWhitespace) schon ein eigener Knoten
        if (doc.Declaration is { } decl) sb.Append(decl).Append(doc.FirstNode is XText ? "" : newline);
        foreach (var node in doc.Nodes()) sb.Append(node.ToString(SaveOptions.DisableFormatting));
        if (xml.EndsWith('\n') && sb[^1] != '\n') sb.Append(newline);
        return (sb.ToString(), StdoutPatch.Updated);
    }

    /// <summary>„C:\…“, „\\server\…“ oder Umgebungsvariable am Anfang („%SystemDrive%\…“).</summary>
    public static bool IsAbsoluteWindowsPath(string path)
    {
        var p = path.Trim();
        return (p.Length >= 3 && char.IsAsciiLetter(p[0]) && p[1] == ':' && p[2] is '\\' or '/')
               || p.StartsWith(@"\\", StringComparison.Ordinal)
               || p.StartsWith('%');
    }
}

/// <summary>Eine bearbeitbare Konfigurationsdatei (GET /api/sites/{name}/config).</summary>
public sealed class SiteConfigFile
{
    public string Name { get; set; } = "";

    /// <summary>Liegt in der aktiven Version bzw. im physischen Pfad.</summary>
    public bool InActive { get; set; }

    /// <summary>Remote bearbeitet – wird bei jedem Deployment übernommen.</summary>
    public bool HasOverride { get; set; }

    public long Size { get; set; }

    public DateTime? Modified { get; set; }
}

public sealed class SiteConfigContent
{
    public string Name { get; set; } = "";

    public string Content { get; set; } = "";

    public bool HasOverride { get; set; }

    /// <summary>override, active oder new (Datei gibt es noch nicht).</summary>
    public string Source { get; set; } = "";
}

/// <summary>PUT /api/sites/{name}/config/{file}.</summary>
public sealed class SiteConfigWrite
{
    public string Content { get; set; } = "";

    /// <summary>Sofort auch in die laufende Version schreiben (sonst erst beim nächsten Deployment).</summary>
    public bool Apply { get; set; } = true;
}

public sealed class SiteConfigWriteResult
{
    /// <summary>In die laufende Version geschrieben.</summary>
    public bool Applied { get; set; }

    public bool Recycled { get; set; }

    /// <summary>Gespeicherter Inhalt (bei web.config ggf. mit eingeschaltetem stdout).</summary>
    public string Content { get; set; } = "";

    public List<string> Notes { get; set; } = new();
}

/// <summary>Authentifizierung einer Site/Anwendung (GET/PUT /api/sites/{name}/auth).</summary>
public sealed class SiteAuthentication
{
    public bool Anonymous { get; set; }

    public bool Windows { get; set; }

    public bool Basic { get; set; }

    public bool Digest { get; set; }

    /// <summary>Verfahren, deren IIS-Modul nicht installiert ist (anonymous, windows, basic, digest).</summary>
    public List<string> Unavailable { get; set; } = new();
}

public sealed class SiteLogFile
{
    public string Name { get; set; } = "";

    public long Size { get; set; }

    public DateTime Modified { get; set; }
}
