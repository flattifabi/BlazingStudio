using System.Text;
using System.Text.RegularExpressions;

namespace BlazorStudio.Core.Templates;

/// <summary>Eine Vorlage für „Neue Datei“ (wie in Rider/VS).</summary>
/// <param name="Id">Schlüssel, z. B. "class".</param>
/// <param name="Group">Gruppe für die Anzeige: csharp, blazor, web, data, other.</param>
/// <param name="Extension">Endung der Hauptdatei (mit Punkt).</param>
/// <param name="DefaultName">Vorschlag für den Namen.</param>
public sealed record FileTemplate(string Id, string Group, string Icon, string Extension, string DefaultName, bool IsCode = true);

/// <summary>Eine zu erzeugende Datei (Pfad relativ zum Zielordner).</summary>
public sealed record GeneratedFile(string RelativePath, string Content);

public sealed class TemplateOptions
{
    /// <summary>Namespace für C#-Dateien (aus Projekt-RootNamespace + Ordner).</summary>
    public string Namespace { get; init; } = "App";

    /// <summary>Für Razor-Komponenten: .razor.cs-Code-Behind anlegen.</summary>
    public bool CodeBehind { get; init; }

    /// <summary>Für Razor-Komponenten: scoped .razor.css anlegen.</summary>
    public bool ScopedCss { get; init; }

    /// <summary>Für Seiten: Route (ohne führendes /).</summary>
    public string? Route { get; init; }

    /// <summary>Nullable-Kontext aktiv (dann `= default!;` / `string?` passend).</summary>
    public bool Nullable { get; init; } = true;
}

/// <summary>Eingebaute Datei-Vorlagen: C#, Blazor, Web, Daten, Konfiguration.</summary>
public static partial class FileTemplates
{
    public static IReadOnlyList<FileTemplate> All { get; } = new[]
    {
        new FileTemplate("class", "csharp", "braces", ".cs", "MyClass"),
        new FileTemplate("interface", "csharp", "braces", ".cs", "IMyService"),
        new FileTemplate("record", "csharp", "braces", ".cs", "MyRecord"),
        new FileTemplate("enum", "csharp", "list", ".cs", "MyEnum"),
        new FileTemplate("struct", "csharp", "braces", ".cs", "MyStruct"),
        new FileTemplate("service", "csharp", "zap", ".cs", "MyService"),
        new FileTemplate("controller", "csharp", "globe", ".cs", "ProductsController"),
        new FileTemplate("dbcontext", "csharp", "database", ".cs", "AppDbContext"),
        new FileTemplate("test", "csharp", "checkc", ".cs", "MyClassTests"),
        new FileTemplate("component", "blazor", "puzzle", ".razor", "MyComponent"),
        new FileTemplate("page", "blazor", "page", ".razor", "MyPage"),
        new FileTemplate("layout", "blazor", "design", ".razor", "MyLayout"),
        new FileTemplate("codebehind", "blazor", "braces", ".razor.cs", "MyComponent"),
        new FileTemplate("scopedcss", "blazor", "drop", ".razor.css", "MyComponent", IsCode: false),
        new FileTemplate("imports", "blazor", "at", ".razor", "_Imports", IsCode: false),
        new FileTemplate("css", "web", "drop", ".css", "styles", IsCode: false),
        new FileTemplate("js", "web", "code", ".js", "script", IsCode: false),
        new FileTemplate("jsmodule", "web", "code", ".razor.js", "MyComponent", IsCode: false),
        new FileTemplate("html", "web", "globe", ".html", "index", IsCode: false),
        new FileTemplate("json", "data", "braces", ".json", "data", IsCode: false),
        new FileTemplate("appsettings", "data", "gear", ".json", "appsettings.Development", IsCode: false),
        new FileTemplate("xml", "data", "code", ".xml", "data", IsCode: false),
        new FileTemplate("markdown", "other", "text", ".md", "README", IsCode: false),
        new FileTemplate("text", "other", "text", ".txt", "notes", IsCode: false),
        new FileTemplate("editorconfig", "other", "gear", "", ".editorconfig", IsCode: false),
        new FileTemplate("gitignore", "other", "git", "", ".gitignore", IsCode: false),
    };

    public static FileTemplate? Find(string id) => All.FirstOrDefault(t => t.Id == id);

    /// <summary>Gültiger C#-Bezeichner aus einem Dateinamen ("my-page 2" → "MyPage2").</summary>
    public static string ToIdentifier(string name)
    {
        var parts = IdentifierSplit().Split(name).Where(p => p.Length > 0).ToList();
        if (parts.Count == 0) return "Item";
        var sb = new StringBuilder();
        foreach (var p in parts) sb.Append(char.ToUpperInvariant(p[0])).Append(p.AsSpan(1));
        if (char.IsDigit(sb[0])) sb.Insert(0, '_');
        return sb.ToString();
    }

    /// <summary>Dateiname mit Endung; Namen mit bereits passender Endung bleiben unverändert.</summary>
    public static string FileName(FileTemplate t, string name)
    {
        name = name.Trim();
        if (t.Extension.Length == 0) return name.Length == 0 ? t.DefaultName : name;
        if (name.EndsWith(t.Extension, StringComparison.OrdinalIgnoreCase)) return name;
        // "Foo.razor" bei Code-Behind-Vorlage → "Foo.razor.cs"
        foreach (var ext in new[] { ".razor", ".cs", ".css", ".js", ".json", ".html", ".md", ".txt", ".xml" })
        {
            if (t.Extension.EndsWith(ext, StringComparison.OrdinalIgnoreCase) && t.Extension.Length > ext.Length && name.EndsWith(t.Extension[..^ext.Length], StringComparison.OrdinalIgnoreCase))
                return name + ext;
        }
        return name + t.Extension;
    }

    /// <summary>Erzeugt die Datei(en) einer Vorlage.</summary>
    public static IReadOnlyList<GeneratedFile> Generate(FileTemplate t, string name, TemplateOptions o)
    {
        var file = FileName(t, name);
        var baseName = file.Length > t.Extension.Length && t.Extension.Length > 0 ? file[..^t.Extension.Length] : file;
        var id = ToIdentifier(t.Id is "codebehind" or "scopedcss" or "jsmodule" ? baseName.Split('.')[0] : baseName);
        var ns = o.Namespace;
        var list = new List<GeneratedFile>();
        switch (t.Id)
        {
            case "class":
                list.Add(new(file, Cs(ns, $"public class {id}\n{{\n}}\n")));
                break;
            case "interface":
                list.Add(new(file, Cs(ns, $"public interface {id}\n{{\n}}\n")));
                break;
            case "record":
                list.Add(new(file, Cs(ns, $"public record {id}(string Name);\n")));
                break;
            case "enum":
                list.Add(new(file, Cs(ns, $"public enum {id}\n{{\n    None,\n}}\n")));
                break;
            case "struct":
                list.Add(new(file, Cs(ns, $"public readonly record struct {id}(int Value);\n")));
                break;
            case "service":
            {
                var iface = id.StartsWith('I') && id.Length > 1 && char.IsUpper(id[1]) ? id : "I" + id;
                var impl = iface == id ? id[1..] : id;
                list.Add(new(file, Cs(ns,
                    $"public interface {iface}\n{{\n    Task<string> GetAsync(CancellationToken ct = default);\n}}\n\n" +
                    $"// Register in Program.cs: builder.Services.AddScoped<{iface}, {impl}>();\n" +
                    $"public sealed class {impl} : {iface}\n{{\n    public Task<string> GetAsync(CancellationToken ct = default) => Task.FromResult(\"\");\n}}\n")));
                break;
            }
            case "controller":
            {
                var route = id.EndsWith("Controller", StringComparison.Ordinal) ? id[..^10] : id;
                list.Add(new(file, Cs(ns,
                    $"[ApiController]\n[Route(\"api/[controller]\")]\npublic class {id} : ControllerBase\n{{\n" +
                    "    [HttpGet]\n    public IActionResult Get() => Ok(Array.Empty<object>());\n\n" +
                    "    [HttpGet(\"{id:int}\")]\n    public IActionResult Get(int id) => NotFound();\n\n" +
                    "    [HttpPost]\n    public IActionResult Post([FromBody] object value) => CreatedAtAction(nameof(Get), new { id = 1 }, value);\n}\n",
                    "Microsoft.AspNetCore.Mvc")));
                _ = route;
                break;
            }
            case "dbcontext":
                list.Add(new(file, Cs(ns,
                    $"public class {id}(DbContextOptions<{id}> options) : DbContext(options)\n{{\n" +
                    "    // public DbSet<Product> Products => Set<Product>();\n\n" +
                    "    protected override void OnModelCreating(ModelBuilder modelBuilder)\n    {\n        base.OnModelCreating(modelBuilder);\n    }\n}\n",
                    "Microsoft.EntityFrameworkCore")));
                break;
            case "test":
                list.Add(new(file, Cs(ns, $"public class {id}\n{{\n    [Fact]\n    public void Works()\n    {{\n        Assert.True(true);\n    }}\n}}\n", "Xunit")));
                break;
            case "component":
            case "page":
            case "layout":
            {
                var sb = new StringBuilder();
                if (t.Id == "page") sb.Append("@page \"/").Append((o.Route ?? Kebab(id)).TrimStart('/')).Append("\"\n\n<PageTitle>").Append(id).Append("</PageTitle>\n\n");
                if (t.Id == "layout") sb.Append("@inherits LayoutComponentBase\n\n<div class=\"layout\">\n    <main>\n        @Body\n    </main>\n</div>\n");
                else sb.Append("<h3>").Append(id).Append("</h3>\n");
                if (!o.CodeBehind && t.Id != "layout") sb.Append("\n@code {\n    [Parameter] public string? Title { get; set; }\n}\n");
                list.Add(new(file, sb.ToString()));
                if (o.CodeBehind)
                {
                    var body = t.Id == "layout" ? "" : "    [Parameter] public string? Title { get; set; }\n";
                    list.Add(new(file + ".cs", Cs(ns, $"public partial class {id}\n{{\n{body}}}\n", "Microsoft.AspNetCore.Components")));
                }
                if (o.ScopedCss) list.Add(new(file + ".css", t.Id == "layout" ? ".layout {\n    display: flex;\n    min-height: 100vh;\n}\n" : "h3 {\n    margin: 0;\n}\n"));
                break;
            }
            case "codebehind":
                list.Add(new(file, Cs(ns, $"public partial class {id}\n{{\n    protected override void OnInitialized()\n    {{\n    }}\n}}\n", "Microsoft.AspNetCore.Components")));
                break;
            case "scopedcss":
                list.Add(new(file, "/* Scoped to this component (CSS isolation) */\n\n"));
                break;
            case "imports":
                list.Add(new(file, "@using Microsoft.AspNetCore.Components.Web\n@using " + ns + "\n"));
                break;
            case "css":
                list.Add(new(file, ":root {\n}\n"));
                break;
            case "js":
                list.Add(new(file, "\"use strict\";\n\n"));
                break;
            case "jsmodule":
                list.Add(new(file, "// Collocated JS module. Load it with:\n// await JS.InvokeAsync<IJSObjectReference>(\"import\", \"./" + baseName + ".razor.js\");\n\nexport function init(element) {\n}\n"));
                break;
            case "html":
                list.Add(new(file, $"<!DOCTYPE html>\n<html lang=\"de\">\n<head>\n    <meta charset=\"utf-8\" />\n    <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\" />\n    <title>{baseName}</title>\n</head>\n<body>\n</body>\n</html>\n"));
                break;
            case "json":
                list.Add(new(file, "{\n}\n"));
                break;
            case "appsettings":
                list.Add(new(file, "{\n  \"Logging\": {\n    \"LogLevel\": {\n      \"Default\": \"Information\",\n      \"Microsoft.AspNetCore\": \"Warning\"\n    }\n  }\n}\n"));
                break;
            case "xml":
                list.Add(new(file, "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<root>\n</root>\n"));
                break;
            case "markdown":
                list.Add(new(file, "# " + baseName + "\n\n"));
                break;
            case "editorconfig":
                list.Add(new(file, "root = true\n\n[*]\nindent_style = space\nindent_size = 4\ninsert_final_newline = true\ncharset = utf-8\n\n[*.{json,yml,yaml,xml,csproj}]\nindent_size = 2\n"));
                break;
            case "gitignore":
                list.Add(new(file, "bin/\nobj/\n.vs/\n.idea/\n*.user\n"));
                break;
            default:
                list.Add(new(file, ""));
                break;
        }
        return list;
    }

    private static string Cs(string ns, string body, params string[] usings)
    {
        var sb = new StringBuilder();
        foreach (var u in usings) sb.Append("using ").Append(u).Append(";\n");
        if (usings.Length > 0) sb.Append('\n');
        sb.Append("namespace ").Append(ns).Append(";\n\n").Append(body);
        return sb.ToString();
    }

    private static string Kebab(string name)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0) sb.Append('-');
            sb.Append(char.ToLowerInvariant(name[i]));
        }
        return sb.ToString();
    }

    /// <summary>Namespace für einen Ordner: RootNamespace + relative Ordner (Sonderzeichen → _).</summary>
    public static string NamespaceFor(string rootNamespace, string projectDir, string folder)
    {
        var rel = Path.GetRelativePath(projectDir, folder);
        if (rel == "." || rel.StartsWith("..", StringComparison.Ordinal)) return rootNamespace;
        var parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Where(p => p.Length > 0).Select(p => NsPart().Replace(p, "_"));
        return rootNamespace + "." + string.Join('.', parts.Select(p => char.IsDigit(p[0]) ? "_" + p : p));
    }

    [GeneratedRegex(@"[^A-Za-z0-9_]+")]
    private static partial Regex IdentifierSplit();

    [GeneratedRegex(@"[^A-Za-z0-9_]")]
    private static partial Regex NsPart();
}

/// <summary>Projektvorlagen für „Neues Projekt zur Solution hinzufügen“ (dotnet new).</summary>
public sealed record ProjectTemplate(string ShortName, string Icon, string Group);

public static class ProjectTemplates
{
    public static IReadOnlyList<ProjectTemplate> All { get; } = new[]
    {
        new ProjectTemplate("blazor", "globe", "web"),
        new ProjectTemplate("blazorwasm", "globe", "web"),
        new ProjectTemplate("razorclasslib", "puzzle", "web"),
        new ProjectTemplate("webapi", "zap", "web"),
        new ProjectTemplate("classlib", "cube", "lib"),
        new ProjectTemplate("console", "terminal", "app"),
        new ProjectTemplate("worker", "refresh", "app"),
        new ProjectTemplate("xunit", "checkc", "test"),
        new ProjectTemplate("nunit", "checkc", "test"),
        new ProjectTemplate("mstest", "checkc", "test"),
    };
}
