using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace BlazorStudio.Core.Solution;

/// <summary>Ein Eintrag der Projektmappe: Projekt oder Projektmappenordner.</summary>
public sealed class SolutionItem
{
    public required string Name { get; init; }

    /// <summary>Absoluter Pfad zur .csproj (bei Ordnern leer).</summary>
    public string Path { get; init; } = "";

    public bool IsFolder { get; init; }

    public string? Guid { get; init; }

    public List<SolutionItem> Children { get; } = new();

    /// <summary>Lose Dateien eines Projektmappenordners (Solution Items).</summary>
    public List<string> Files { get; } = new();

    public ProjectModel? Project { get; set; }
}

public sealed class SolutionModel
{
    public required string Path { get; init; }

    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);

    public string Directory => System.IO.Path.GetDirectoryName(Path)!;

    public List<SolutionItem> Root { get; } = new();

    public IEnumerable<SolutionItem> AllProjects => Flatten(Root).Where(i => !i.IsFolder);

    private static IEnumerable<SolutionItem> Flatten(IEnumerable<SolutionItem> items)
    {
        foreach (var i in items)
        {
            yield return i;
            foreach (var c in Flatten(i.Children)) yield return c;
        }
    }

    // ------------------------------------------------------------------

    public static SolutionModel Load(string path)
    {
        var model = path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) ? LoadSlnx(path) : LoadSln(path);
        foreach (var p in model.AllProjects)
            if (File.Exists(p.Path)) p.Project = ProjectModel.Load(p.Path);
        return model;
    }

    /// <summary>Für einen Ordner ohne .sln: alle .csproj darunter als „virtuelle“ Projektmappe.</summary>
    public static SolutionModel FromProjects(string directory, IEnumerable<string> projects)
    {
        var model = new SolutionModel { Path = System.IO.Path.Combine(directory, System.IO.Path.GetFileName(directory) + ".sln") };
        foreach (var p in projects.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            model.Root.Add(new SolutionItem { Name = System.IO.Path.GetFileNameWithoutExtension(p), Path = p, Project = ProjectModel.Load(p) });
        return model;
    }

    private static readonly Regex ProjectLine = new(
        "^Project\\(\"\\{(?<type>[^}]+)\\}\"\\)\\s*=\\s*\"(?<name>[^\"]*)\",\\s*\"(?<path>[^\"]*)\",\\s*\"\\{(?<guid>[^}]+)\\}\"",
        RegexOptions.Compiled);

    private const string FolderType = "2150E333-8FDC-42A3-9474-1A3956D46DE8";

    private static SolutionModel LoadSln(string path)
    {
        var model = new SolutionModel { Path = path };
        var dir = System.IO.Path.GetDirectoryName(path)!;
        var byGuid = new Dictionary<string, SolutionItem>(StringComparer.OrdinalIgnoreCase);
        var nesting = new List<(string Child, string Parent)>();
        var lines = File.ReadAllLines(path);
        SolutionItem? current = null;
        var inSolutionItems = false;
        var inNested = false;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            var m = ProjectLine.Match(line);
            if (m.Success)
            {
                var isFolder = m.Groups["type"].Value.Equals(FolderType, StringComparison.OrdinalIgnoreCase);
                var rel = m.Groups["path"].Value.Replace('\\', System.IO.Path.DirectorySeparatorChar);
                current = new SolutionItem
                {
                    Name = m.Groups["name"].Value,
                    Path = isFolder ? "" : System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, rel)),
                    IsFolder = isFolder,
                    Guid = m.Groups["guid"].Value,
                };
                byGuid[current.Guid] = current;
                continue;
            }
            if (line.StartsWith("ProjectSection(SolutionItems)", StringComparison.Ordinal)) { inSolutionItems = true; continue; }
            if (line.StartsWith("EndProjectSection", StringComparison.Ordinal)) { inSolutionItems = false; continue; }
            if (inSolutionItems && current is not null)
            {
                var eq = line.IndexOf('=');
                if (eq > 0) current.Files.Add(System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, line[..eq].Trim().Replace('\\', System.IO.Path.DirectorySeparatorChar))));
                continue;
            }
            if (line.StartsWith("EndProject", StringComparison.Ordinal)) { current = null; continue; }
            if (line.StartsWith("GlobalSection(NestedProjects)", StringComparison.Ordinal)) { inNested = true; continue; }
            if (inNested && line.StartsWith("EndGlobalSection", StringComparison.Ordinal)) { inNested = false; continue; }
            if (inNested)
            {
                var parts = line.Split('=', 2);
                if (parts.Length == 2) nesting.Add((parts[0].Trim().Trim('{', '}'), parts[1].Trim().Trim('{', '}')));
            }
        }

        var nested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (child, parent) in nesting)
        {
            if (byGuid.TryGetValue(child, out var c) && byGuid.TryGetValue(parent, out var p))
            {
                p.Children.Add(c);
                nested.Add(child);
            }
        }
        foreach (var item in byGuid.Values.Where(i => !nested.Contains(i.Guid!))) model.Root.Add(item);
        Sort(model.Root);
        return model;
    }

    private static SolutionModel LoadSlnx(string path)
    {
        var model = new SolutionModel { Path = path };
        var dir = System.IO.Path.GetDirectoryName(path)!;
        var doc = XDocument.Load(path);
        var root = doc.Root!;
        var folders = new Dictionary<string, SolutionItem>(StringComparer.OrdinalIgnoreCase);

        SolutionItem FolderFor(string? folderPath)
        {
            // "/src/Apps/" → verschachtelte Ordner
            var parts = (folderPath ?? "").Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            SolutionItem? parent = null;
            var key = "";
            foreach (var part in parts)
            {
                key += "/" + part;
                if (!folders.TryGetValue(key, out var f))
                {
                    f = new SolutionItem { Name = part, IsFolder = true };
                    folders[key] = f;
                    if (parent is null) model.Root.Add(f);
                    else parent.Children.Add(f);
                }
                parent = f;
            }
            return parent!;
        }

        void AddProject(XElement e, SolutionItem? folder)
        {
            var rel = e.Attribute("Path")?.Value;
            if (rel is null) return;
            var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, rel.Replace('\\', System.IO.Path.DirectorySeparatorChar)));
            var item = new SolutionItem { Name = System.IO.Path.GetFileNameWithoutExtension(full), Path = full };
            if (folder is null) model.Root.Add(item);
            else folder.Children.Add(item);
        }

        foreach (var e in root.Elements())
        {
            switch (e.Name.LocalName)
            {
                case "Project":
                    AddProject(e, null);
                    break;
                case "Folder":
                    var folder = FolderFor(e.Attribute("Name")?.Value);
                    foreach (var p in e.Elements().Where(x => x.Name.LocalName == "Project")) AddProject(p, folder);
                    foreach (var f in e.Elements().Where(x => x.Name.LocalName == "File"))
                        if (f.Attribute("Path")?.Value is { } fp) folder.Files.Add(System.IO.Path.GetFullPath(System.IO.Path.Combine(dir, fp)));
                    break;
            }
        }
        Sort(model.Root);
        return model;
    }

    private static void Sort(List<SolutionItem> items)
    {
        items.Sort((a, b) => a.IsFolder != b.IsFolder ? (a.IsFolder ? -1 : 1) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        foreach (var i in items) Sort(i.Children);
    }
}

// ======================================================================
// Projekt (.csproj) mit Abhängigkeiten
// ======================================================================

public sealed record PackageDependency(string Id, string Version, bool IsTransitive, IReadOnlyList<string> Dependencies, IReadOnlyList<string> Assemblies);

public sealed record AssemblyReference(string Name, string? Path, string? Version, string Source);

public sealed class ProjectModel
{
    public required string Path { get; init; }

    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);

    public string Directory => System.IO.Path.GetDirectoryName(Path)!;

    public string Sdk { get; private set; } = "";

    public IReadOnlyList<string> TargetFrameworks { get; private set; } = Array.Empty<string>();

    public string? OutputType { get; private set; }

    public string? AssemblyName { get; private set; }

    public string? RootNamespace { get; private set; }

    public bool IsBlazor => Sdk.Contains("BlazorWebAssembly", StringComparison.OrdinalIgnoreCase) ||
                            Sdk.Contains("Razor", StringComparison.OrdinalIgnoreCase) ||
                            (Sdk.Contains(".Web", StringComparison.OrdinalIgnoreCase) && System.IO.Directory.EnumerateFiles(Directory, "*.razor", SearchOption.AllDirectories).Any());

    public bool IsTest => PackageReferences.Any(p => p.Id is "Microsoft.NET.Test.Sdk" or "xunit" or "NUnit" or "MSTest.TestFramework" || p.Id.StartsWith("xunit.v3", StringComparison.Ordinal));

    public string Kind => IsTest ? "test" : Sdk.Contains("Web", StringComparison.OrdinalIgnoreCase) || Sdk.Contains("Blazor", StringComparison.OrdinalIgnoreCase) ? "web"
        : OutputType is "Exe" or "WinExe" ? "exe" : "lib";

    public List<PackageDependency> PackageReferences { get; } = new();

    /// <summary>Alle Pakete inkl. transitiver aus obj/project.assets.json.</summary>
    public List<PackageDependency> ResolvedPackages { get; } = new();

    public List<string> ProjectReferences { get; } = new();

    public List<AssemblyReference> AssemblyReferences { get; } = new();

    public List<string> FrameworkReferences { get; } = new();

    public List<string> Analyzers { get; } = new();

    public bool AssetsMissing { get; private set; }

    public static ProjectModel Load(string path)
    {
        var p = new ProjectModel { Path = path };
        try
        {
            p.Read();
        }
        catch (Exception e) when (e is IOException or System.Xml.XmlException or UnauthorizedAccessException)
        {
        }
        return p;
    }

    private void Read()
    {
        var doc = XDocument.Load(Path);
        var root = doc.Root!;
        Sdk = root.Attribute("Sdk")?.Value ?? root.Elements().FirstOrDefault(e => e.Name.LocalName == "Sdk")?.Attribute("Name")?.Value ?? "";
        string? Prop(string name) => root.Descendants().FirstOrDefault(e => e.Name.LocalName == name && e.Parent?.Name.LocalName == "PropertyGroup")?.Value.Trim();
        var tfms = Prop("TargetFrameworks") ?? Prop("TargetFramework") ?? "";
        TargetFrameworks = tfms.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        OutputType = Prop("OutputType");
        AssemblyName = Prop("AssemblyName") ?? Name;
        RootNamespace = Prop("RootNamespace") ?? Name;

        var central = ReadCentralVersions(Directory);
        foreach (var e in root.Descendants().Where(e => e.Name.LocalName == "PackageReference"))
        {
            var id = e.Attribute("Include")?.Value;
            if (id is null) continue;
            var v = e.Attribute("Version")?.Value ?? e.Elements().FirstOrDefault(x => x.Name.LocalName == "Version")?.Value ?? central.GetValueOrDefault(id) ?? "";
            PackageReferences.Add(new PackageDependency(id, v, false, Array.Empty<string>(), Array.Empty<string>()));
        }
        foreach (var e in root.Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
        {
            var inc = e.Attribute("Include")?.Value;
            if (inc is not null) ProjectReferences.Add(System.IO.Path.GetFullPath(System.IO.Path.Combine(Directory, inc.Replace('\\', System.IO.Path.DirectorySeparatorChar))));
        }
        foreach (var e in root.Descendants().Where(e => e.Name.LocalName == "Reference"))
        {
            var inc = e.Attribute("Include")?.Value;
            if (inc is null) continue;
            var hint = e.Elements().FirstOrDefault(x => x.Name.LocalName == "HintPath")?.Value;
            var full = hint is null ? null : System.IO.Path.GetFullPath(System.IO.Path.Combine(Directory, hint.Replace('\\', System.IO.Path.DirectorySeparatorChar)));
            AssemblyReferences.Add(new AssemblyReference(inc.Split(',')[0].Trim(), full, VersionOf(full) ?? VersionFromInclude(inc), "Reference"));
        }
        foreach (var e in root.Descendants().Where(e => e.Name.LocalName == "FrameworkReference"))
            if (e.Attribute("Include")?.Value is { } fr) FrameworkReferences.Add(fr);

        // Implizite Frameworks aus dem SDK
        if (!FrameworkReferences.Contains("Microsoft.NETCore.App")) FrameworkReferences.Insert(0, "Microsoft.NETCore.App");
        if ((Sdk.EndsWith(".Web", StringComparison.OrdinalIgnoreCase) || Sdk.Contains("Razor", StringComparison.OrdinalIgnoreCase) && !Sdk.Contains("BlazorWebAssembly", StringComparison.OrdinalIgnoreCase))
            && !FrameworkReferences.Contains("Microsoft.AspNetCore.App") && Sdk.EndsWith(".Web", StringComparison.OrdinalIgnoreCase))
            FrameworkReferences.Add("Microsoft.AspNetCore.App");
        if (root.Descendants().Any(e => e.Name.LocalName is "UseWPF" or "UseWindowsForms" && e.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)))
            FrameworkReferences.Add("Microsoft.WindowsDesktop.App");

        ReadAssets();
    }

    /// <summary>obj/project.assets.json: aufgelöste Pakete (direkt + transitiv) mit Assemblies.</summary>
    private void ReadAssets()
    {
        var assets = System.IO.Path.Combine(Directory, "obj", "project.assets.json");
        if (!File.Exists(assets))
        {
            AssetsMissing = true;
            return;
        }
        using var doc = JsonDocument.Parse(File.ReadAllText(assets));
        var root = doc.RootElement;
        if (!root.TryGetProperty("targets", out var targets)) return;
        var target = targets.EnumerateObject().FirstOrDefault(t => !t.Name.Contains('/'));
        if (target.Value.ValueKind != JsonValueKind.Object) return;

        var direct = PackageReferences.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string? packageFolder = null;
        if (root.TryGetProperty("packageFolders", out var folders))
            packageFolder = folders.EnumerateObject().FirstOrDefault().Name;

        foreach (var lib in target.Value.EnumerateObject())
        {
            var slash = lib.Name.IndexOf('/');
            if (slash < 0) continue;
            var id = lib.Name[..slash];
            var version = lib.Name[(slash + 1)..];
            var type = lib.Value.TryGetProperty("type", out var t) ? t.GetString() : "package";
            if (type == "project")
            {
                continue;
            }
            var deps = lib.Value.TryGetProperty("dependencies", out var d)
                ? d.EnumerateObject().Select(x => $"{x.Name} {x.Value.GetString()}").ToList()
                : new List<string>();
            var dlls = new List<string>();
            if (lib.Value.TryGetProperty("compile", out var compile))
            {
                foreach (var c in compile.EnumerateObject())
                {
                    if (!c.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                    dlls.Add(packageFolder is null ? c.Name : System.IO.Path.Combine(packageFolder, id.ToLowerInvariant(), version.ToLowerInvariant(), c.Name.Replace('/', System.IO.Path.DirectorySeparatorChar)));
                }
            }
            ResolvedPackages.Add(new PackageDependency(id, version, !direct.Contains(id), deps, dlls));
        }

        // Versionen der direkten Pakete aus der Auflösung übernehmen (z. B. bei Bereichen)
        for (var i = 0; i < PackageReferences.Count; i++)
        {
            var resolved = ResolvedPackages.FirstOrDefault(r => r.Id.Equals(PackageReferences[i].Id, StringComparison.OrdinalIgnoreCase));
            if (resolved is not null)
                PackageReferences[i] = resolved with { IsTransitive = false };
        }

        // Analyzer-Pakete
        foreach (var lib in target.Value.EnumerateObject())
        {
            if (lib.Value.TryGetProperty("build", out _) && lib.Name.Contains("Analyzers", StringComparison.OrdinalIgnoreCase))
                Analyzers.Add(lib.Name.Replace('/', ' '));
        }
    }

    public static string? VersionOf(string? dll)
    {
        if (dll is null || !File.Exists(dll)) return null;
        try
        {
            return System.Reflection.AssemblyName.GetAssemblyName(dll).Version?.ToString();
        }
        catch (Exception e) when (e is BadImageFormatException or IOException or FileLoadException)
        {
            return null;
        }
    }

    private static string? VersionFromInclude(string include)
    {
        var m = Regex.Match(include, @"Version=([\d.]+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static Dictionary<string, string> ReadCentralVersions(string dir)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var d = dir; d is not null; d = System.IO.Path.GetDirectoryName(d))
        {
            var props = System.IO.Path.Combine(d, "Directory.Packages.props");
            if (!File.Exists(props)) continue;
            try
            {
                foreach (var e in XDocument.Load(props).Descendants().Where(x => x.Name.LocalName == "PackageVersion"))
                    if (e.Attribute("Include")?.Value is { } id && e.Attribute("Version")?.Value is { } v) result.TryAdd(id, v);
            }
            catch (System.Xml.XmlException) { }
            break;
        }
        return result;
    }
}
