using BlazorStudio.Core.Catalog;
using BlazorStudio.Core.Project;
using BlazorStudio.Core.Razor;

namespace BlazorStudio.App.Services;

/// <summary>
/// Richtet bekannte Komponenten-Bibliotheken im Projekt ein (Services, CSS/JS, @using, Provider).
/// Für unbekannte Bibliotheken werden nur die Namespaces in _Imports.razor ergänzt.
/// </summary>
public static class LibrarySetup
{
    private sealed record Recipe(string ServicesUsing, string ServicesCall, string[] HeadLinks, string[] BodyScripts, string[] Usings, string[] LayoutProviders);

    private static readonly Dictionary<string, Recipe> Recipes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MudBlazor"] = new("using MudBlazor.Services;", "builder.Services.AddMudServices();",
            new[] { "<link href=\"https://fonts.googleapis.com/css?family=Roboto:300,400,500,700&display=swap\" rel=\"stylesheet\" />", "<link href=\"_content/MudBlazor/MudBlazor.min.css\" rel=\"stylesheet\" />" },
            new[] { "<script src=\"_content/MudBlazor/MudBlazor.min.js\"></script>" },
            new[] { "MudBlazor" },
            new[] { "<MudThemeProvider />", "<MudPopoverProvider />", "<MudDialogProvider />", "<MudSnackbarProvider />" }),
        ["Radzen.Blazor"] = new("using Radzen;", "builder.Services.AddRadzenComponents();",
            new[] { "<link rel=\"stylesheet\" href=\"_content/Radzen.Blazor/css/material-base.css\" />" },
            new[] { "<script src=\"_content/Radzen.Blazor/Radzen.Blazor.js\"></script>" },
            new[] { "Radzen", "Radzen.Blazor" },
            new[] { "<RadzenComponents />" }),
    };

    /// <summary>Ist die Bibliothek bereits eingerichtet? null = keine Einrichtung nötig/bekannt außer Namespaces.</summary>
    public static bool IsSetUp(ProjectIndex project, ComponentLibrary lib)
    {
        var imports = ReadImports(project);
        var usingsOk = lib.Namespaces.Take(1).All(ns => imports.Contains("@using " + ns, StringComparison.Ordinal));
        if (!Recipes.TryGetValue(lib.PackageId, out var r)) return usingsOk;
        var program = Read(Path.Combine(project.RootDirectory, "Program.cs"));
        return usingsOk && program.Contains(r.ServicesCall.Split('(')[0].Replace("builder.Services.", ""), StringComparison.Ordinal);
    }

    public static bool HasRecipe(string packageId) => Recipes.ContainsKey(packageId);

    private static IEnumerable<string> AllProviders => Recipes.Values.SelectMany(r => r.LayoutProviders);

    /// <summary>
    /// Ältere Versionen haben Provider (z. B. &lt;RadzenComponents /&gt;) teils in den @code-Block geschrieben –
    /// dann baut das Projekt nicht mehr. Beim Öffnen reparieren. Liefert die reparierte Datei.
    /// </summary>
    public static string? RepairLayout(Workspace ws, ProjectIndex project)
    {
        var layout = FindFile(project.RootDirectory, "MainLayout.razor");
        if (layout is null) return null;
        var text = Text(ws, layout);
        if (LayoutProviders.Repair(text, AllProviders) is not { } fixedText) return null;
        Write(ws, layout, fixedText);
        return layout;
    }

    /// <summary>Führt die Einrichtung durch. Liefert die geänderten Dateien.</summary>
    public static List<string> Apply(Workspace ws, ProjectIndex project, ComponentLibrary lib)
    {
        var changed = new List<string>();
        var root = project.RootDirectory;
        Recipes.TryGetValue(lib.PackageId, out var r);

        // _Imports.razor
        var importsPath = FindFile(root, "_Imports.razor") ?? Path.Combine(root, "_Imports.razor");
        var imports = Text(ws, importsPath);
        var usings = (r?.Usings ?? lib.Namespaces.Take(1).ToArray()).Where(ns => !imports.Contains("@using " + ns, StringComparison.Ordinal)).ToList();
        if (usings.Count > 0)
        {
            imports = imports.TrimEnd('\r', '\n') + "\n" + string.Join("\n", usings.Select(u => "@using " + u)) + "\n";
            Write(ws, importsPath, imports);
            changed.Add(importsPath);
        }
        if (r is null) return changed;

        // Program.cs
        var programPath = Path.Combine(root, "Program.cs");
        var program = Text(ws, programPath);
        if (program.Length > 0 && !program.Contains(r.ServicesCall, StringComparison.Ordinal))
        {
            if (!program.Contains(r.ServicesUsing, StringComparison.Ordinal)) program = r.ServicesUsing + "\n" + program;
            var build = program.IndexOf("var app = builder.Build();", StringComparison.Ordinal);
            if (build < 0) build = program.IndexOf("builder.Build()", StringComparison.Ordinal);
            if (build >= 0)
            {
                var lineStart = program.LastIndexOf('\n', Math.Max(0, build - 1)) + 1;
                program = program[..lineStart] + r.ServicesCall + "\n\n" + program[lineStart..];
                Write(ws, programPath, program);
                changed.Add(programPath);
            }
        }

        // App.razor (Blazor Web App) bzw. wwwroot/index.html (WebAssembly)
        var host = FindFile(root, "App.razor") is { } app && Text(ws, app).Contains("<head", StringComparison.OrdinalIgnoreCase) ? app
                   : File.Exists(Path.Combine(root, "wwwroot", "index.html")) ? Path.Combine(root, "wwwroot", "index.html") : null;
        if (host is not null)
        {
            var html = Text(ws, host);
            var original = html;
            foreach (var link in r.HeadLinks.Where(l => !html.Contains(Attr(l), StringComparison.Ordinal)))
            {
                var head = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
                if (head >= 0) html = html.Insert(head, "    " + link + "\n");
            }
            foreach (var script in r.BodyScripts.Where(s => !html.Contains(Attr(s), StringComparison.Ordinal)))
            {
                var body = html.IndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                if (body >= 0) html = html.Insert(body, "    " + script + "\n");
            }
            if (html != original)
            {
                Write(ws, host, html);
                changed.Add(host);
            }
        }

        // Provider ins Hauptlayout (hinter Direktiven/vorhandene Provider, nie in @code)
        var layout = FindFile(root, "MainLayout.razor");
        if (layout is not null)
        {
            var text = Text(ws, layout);
            var updated = LayoutProviders.Repair(text, AllProviders) ?? text;
            updated = LayoutProviders.Insert(updated, r.LayoutProviders);
            if (updated != text)
            {
                Write(ws, layout, updated);
                changed.Add(layout);
            }
        }
        return changed;
    }

    private static string Attr(string tag)
    {
        var i = tag.IndexOf("href=\"", StringComparison.Ordinal);
        if (i < 0) i = tag.IndexOf("src=\"", StringComparison.Ordinal);
        if (i < 0) return tag;
        var start = tag.IndexOf('"', i) + 1;
        var end = tag.IndexOf('"', start);
        return tag[start..end];
    }

    private static string ReadImports(ProjectIndex project) =>
        string.Join("\n", ProjectIndex.EnumerateFiles(project.RootDirectory)
            .Where(f => Path.GetFileName(f).Equals("_Imports.razor", StringComparison.OrdinalIgnoreCase))
            .Select(Read));

    private static string? FindFile(string root, string name) =>
        ProjectIndex.EnumerateFiles(root).Where(f => Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Length).FirstOrDefault();

    private static string Read(string path) => Workspace.ReadText(path) ?? "";

    private static string Text(Workspace ws, string path) =>
        ws.Documents.FirstOrDefault(d => d.Path.Equals(path, StringComparison.OrdinalIgnoreCase))?.Text ?? Read(path);

    /// <summary>Offene Dokumente im Editor ändern (rückgängig machbar), sonst direkt speichern.</summary>
    private static void Write(Workspace ws, string path, string text)
    {
        var open = ws.Documents.FirstOrDefault(d => d.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (open is not null) open.SetText(text, ChangeOrigin.Code);
        else File.WriteAllText(path, text);
    }
}
