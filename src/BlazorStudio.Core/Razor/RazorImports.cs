using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace BlazorStudio.Core.Razor;

/// <summary>
/// Welche Namespaces sind in einer .razor-Datei sichtbar, und welcher fehlt für eine Komponente?
/// Grundlage für das automatische <c>@using</c> beim Einfügen (Toolbox, Einfügen, Vervollständigung) – wie in Rider.
/// </summary>
public static partial class RazorImports
{
    /// <summary>In jeder Komponente automatisch importiert (Razor-SDK).</summary>
    public static readonly IReadOnlyList<string> Implicit = new[]
    {
        "System", "System.Collections.Generic", "System.Linq", "System.Threading.Tasks", "Microsoft.AspNetCore.Components",
    };

    private static readonly Dictionary<string, string> BlazorNamespaces = new(StringComparer.Ordinal)
    {
        ["EditForm"] = Forms, ["InputText"] = Forms, ["InputTextArea"] = Forms, ["InputNumber"] = Forms, ["InputCheckbox"] = Forms,
        ["InputDate"] = Forms, ["InputSelect"] = Forms, ["InputRadio"] = Forms, ["InputRadioGroup"] = Forms, ["InputFile"] = Forms,
        ["DataAnnotationsValidator"] = Forms, ["ValidationSummary"] = Forms, ["ValidationMessage"] = Forms, ["AntiforgeryToken"] = Forms,
        ["NavLink"] = Routing, ["Router"] = Routing, ["NavigationLock"] = Routing, ["FocusOnNavigate"] = Web,
        ["PageTitle"] = Web, ["HeadContent"] = Web, ["HeadOutlet"] = Web, ["ErrorBoundary"] = Web,
        ["Virtualize"] = "Microsoft.AspNetCore.Components.Web.Virtualization",
        ["AuthorizeView"] = Auth, ["CascadingAuthenticationState"] = Auth, ["AuthorizeRouteView"] = Auth,
        ["QuickGrid"] = Grid, ["PropertyColumn"] = Grid, ["TemplateColumn"] = Grid, ["Paginator"] = Grid,
        ["SectionOutlet"] = "Microsoft.AspNetCore.Components.Sections", ["SectionContent"] = "Microsoft.AspNetCore.Components.Sections",
    };

    private const string Forms = "Microsoft.AspNetCore.Components.Forms";
    private const string Routing = "Microsoft.AspNetCore.Components.Routing";
    private const string Web = "Microsoft.AspNetCore.Components.Web";
    private const string Auth = "Microsoft.AspNetCore.Components.Authorization";
    private const string Grid = "Microsoft.AspNetCore.Components.QuickGrid";

    /// <summary>Namespace einer eingebauten Blazor-Komponente (EditForm → …Forms).</summary>
    public static string BlazorNamespace(string tagName) =>
        BlazorNamespaces.TryGetValue(tagName, out var ns) ? ns : "Microsoft.AspNetCore.Components";

    [GeneratedRegex(@"^[ \t]*@using[ \t]+([A-Za-z_][\w.]*)[ \t]*;?[ \t]*$", RegexOptions.Multiline)]
    private static partial Regex UsingDirective();

    [GeneratedRegex(@"^[ \t]*@namespace[ \t]+([A-Za-z_][\w.]*)", RegexOptions.Multiline)]
    private static partial Regex NamespaceDirective();

    [GeneratedRegex(@"^\s*global\s+using\s+([A-Za-z_][\w.]*)\s*;", RegexOptions.Multiline)]
    private static partial Regex GlobalUsing();

    [GeneratedRegex(@"^\s*namespace\s+([A-Za-z_][\w.]*)", RegexOptions.Multiline)]
    private static partial Regex CSharpNamespace();

    /// <summary>„@using X“-Einträge (ohne static/Alias).</summary>
    public static IEnumerable<string> Usings(string razorText) =>
        UsingDirective().Matches(razorText).Select(m => m.Groups[1].Value);

    /// <summary>namespace X; bzw. namespace X { … } aus C#-Quelltext.</summary>
    public static string? CSharpNamespaceOf(string csharp) =>
        CSharpNamespace().Match(csharp) is { Success: true } m ? m.Groups[1].Value : null;

    public static string? NamespaceOf(string razorText) =>
        NamespaceDirective().Match(razorText) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>„global using X;“ aus C#-Quelltext.</summary>
    public static IEnumerable<string> GlobalUsings(string csharp) =>
        GlobalUsing().Matches(csharp).Select(m => m.Groups[1].Value);

    /// <summary>&lt;Using Include="X" /&gt; in der Projektdatei (ohne Alias/Static/Remove).</summary>
    public static IEnumerable<string> ProjectUsings(string csprojXml)
    {
        XDocument doc;
        try { doc = XDocument.Parse(csprojXml); }
        catch (System.Xml.XmlException) { yield break; }
        foreach (var u in doc.Descendants().Where(e => e.Name.LocalName == "Using"))
        {
            if (u.Attribute("Alias") is not null || string.Equals((string?)u.Attribute("Static"), "true", StringComparison.OrdinalIgnoreCase)) continue;
            if ((string?)u.Attribute("Include") is { Length: > 0 } ns) yield return ns.Trim();
        }
    }

    /// <summary>
    /// Ordner von <paramref name="projectRoot"/> bis zum Ordner der Datei – in dieser Reihenfolge gelten die _Imports.razor.
    /// </summary>
    public static List<string> ImportFolders(string razorPath, string projectRoot)
    {
        var root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var dirs = new List<string>();
        for (var d = Path.GetDirectoryName(Path.GetFullPath(razorPath)); d is not null && d.Length >= root.Length; d = Path.GetDirectoryName(d))
        {
            dirs.Insert(0, d);
            if (string.Equals(d, root, StringComparison.OrdinalIgnoreCase)) break;
        }
        return dirs;
    }

    /// <summary>
    /// Alle in der Datei sichtbaren Namespaces: implizite, _Imports.razor (Projektordner → Dateiordner),
    /// die Datei selbst und globale (global using / &lt;Using&gt;).
    /// </summary>
    public static HashSet<string> Imported(string razorPath, string razorText, string projectRoot, Func<string, string?> read, IEnumerable<string>? globals = null)
    {
        var set = new HashSet<string>(Implicit, StringComparer.Ordinal);
        foreach (var dir in ImportFolders(razorPath, projectRoot))
        {
            var imp = Path.Combine(dir, "_Imports.razor");
            if (string.Equals(imp, Path.GetFullPath(razorPath), StringComparison.OrdinalIgnoreCase)) continue;
            if (read(imp) is { } text) set.UnionWith(Usings(text));
        }
        set.UnionWith(Usings(razorText));
        if (globals is not null) set.UnionWith(globals);
        return set;
    }

    /// <summary>
    /// Namespace einer .razor-Datei: @namespace in der Datei → namespace im Code-Behind → @namespace einer _Imports.razor
    /// (plus Unterordner) → Projekt-Stammnamespace plus Ordner.
    /// </summary>
    public static string NamespaceOfFile(string razorPath, string projectRoot, string rootNamespace, Func<string, string?> read)
    {
        var full = Path.GetFullPath(razorPath);
        if (read(full) is { } own && NamespaceOf(own) is { } direct) return direct;
        if (read(full + ".cs") is { } cb && CSharpNamespace().Match(cb) is { Success: true } m) return m.Groups[1].Value;
        var folder = Path.GetDirectoryName(full)!;
        foreach (var dir in Enumerable.Reverse(ImportFolders(full, projectRoot)))
        {
            if (read(Path.Combine(dir, "_Imports.razor")) is { } imp && NamespaceOf(imp) is { } baseNs)
                return baseNs + Suffix(dir, folder);
        }
        return rootNamespace + Suffix(Path.GetFullPath(projectRoot), folder);
    }

    private static string Suffix(string from, string to)
    {
        var rel = Path.GetRelativePath(from, to);
        if (rel == "." || rel.StartsWith("..", StringComparison.Ordinal)) return "";
        return string.Concat(rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Where(p => p.Length > 0)
            .Select(p => "." + SafeIdentifier(p)));
    }

    private static string SafeIdentifier(string folder)
    {
        var s = new string(folder.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());
        return s.Length > 0 && char.IsDigit(s[0]) ? "_" + s : s;
    }

    /// <summary>
    /// Ist eine Komponente aus <paramref name="componentNamespace"/> ohne @using sichtbar?
    /// Razor findet Komponenten im eigenen Namespace, in übergeordneten Namespaces und in importierten.
    /// </summary>
    public static bool IsVisible(string componentNamespace, string currentNamespace, IReadOnlySet<string> imported) =>
        string.IsNullOrEmpty(componentNamespace)
        || imported.Contains(componentNamespace)
        || string.Equals(componentNamespace, currentNamespace, StringComparison.Ordinal)
        || currentNamespace.StartsWith(componentNamespace + ".", StringComparison.Ordinal);
}
