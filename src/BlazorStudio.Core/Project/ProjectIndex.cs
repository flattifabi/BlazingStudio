using System.Text.RegularExpressions;
using BlazorStudio.Core.Catalog;
using BlazorStudio.Core.Razor;
using BlazorStudio.Core.Styles;

namespace BlazorStudio.Core.Project;

public sealed class FileNode
{
    public required string Name { get; init; }

    public required string FullPath { get; init; }

    public bool IsDirectory { get; init; }

    public List<FileNode> Children { get; } = new();

    public string Extension => IsDirectory ? "" : Path.GetExtension(Name).ToLowerInvariant();

    /// <summary>Home.razor.cs / Home.razor.css werden unter Home.razor angezeigt.</summary>
    public List<FileNode> Nested { get; } = new();
}

public sealed class ProjectComponentInfo
{
    public required string Name { get; init; }

    public required string FilePath { get; init; }

    public string? CodeBehindPath { get; init; }

    public string? ScopedCssPath { get; init; }

    public string? Route { get; init; }

    public required IReadOnlyList<CodeMember> Members { get; init; }

    public required ComponentDefinition Definition { get; init; }
}

/// <summary>Alles, was über ein geöffnetes Blazor-Projekt bekannt ist.</summary>
public sealed partial class ProjectIndex
{
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".vs", ".vscode", ".idea", "node_modules", "TestResults", "publish",
    };

    public ProjectIndex(string rootDirectory, string? projectFile)
    {
        RootDirectory = rootDirectory;
        ProjectFile = projectFile;
    }

    public string RootDirectory { get; }

    public string? ProjectFile { get; }

    public string Name => ProjectFile is null ? Path.GetFileName(RootDirectory) : Path.GetFileNameWithoutExtension(ProjectFile);

    public string? WwwRoot
    {
        get
        {
            var p = Path.Combine(RootDirectory, "wwwroot");
            return Directory.Exists(p) ? p : null;
        }
    }

    public FileNode Tree { get; private set; } = new() { Name = "", FullPath = "", IsDirectory = true };

    public Dictionary<string, ProjectComponentInfo> Components { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, EnumInfo> Enums { get; } = new(StringComparer.Ordinal);

    public CssIndex Css { get; } = new();

    /// <summary>Alle CSS-Dateien, die in der Designer-Vorschau geladen werden (Pfad → Inhalt).</summary>
    public List<(string Path, string Content)> PreviewStyles { get; } = new();

    public static string? FindProjectFile(string directory)
    {
        if (!Directory.Exists(directory)) return null;
        var direct = Directory.GetFiles(directory, "*.csproj");
        if (direct.Length > 0) return direct.OrderBy(f => f.Length).First();
        foreach (var sub in Directory.GetDirectories(directory))
        {
            if (IgnoredDirectories.Contains(Path.GetFileName(sub))) continue;
            var nested = Directory.GetFiles(sub, "*.csproj");
            if (nested.Length > 0) return nested[0];
        }
        return null;
    }

    public static ProjectIndex Load(string directory)
    {
        var csproj = FindProjectFile(directory);
        var root = csproj is null ? directory : Path.GetDirectoryName(csproj)!;
        var index = new ProjectIndex(root, csproj);
        index.Rescan();
        return index;
    }

    public void Rescan()
    {
        Tree = BuildTree(RootDirectory);
        Components.Clear();
        Enums.Clear();
        Css.Clear();
        PreviewStyles.Clear();

        var files = EnumerateFiles(RootDirectory).ToList();

        foreach (var cs in files.Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
        {
            var text = SafeRead(cs);
            if (text is null || !text.Contains("enum ", StringComparison.Ordinal)) continue;
            foreach (var e in CodeInspector.FindEnums(text)) Enums.TryAdd(e.Name, e);
        }

        foreach (var razor in files.Where(f => f.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)))
        {
            var info = ReadComponent(razor);
            if (info is not null) Components[info.Name] = info;
        }

        // CSS: zuerst Bibliotheken (Bootstrap), dann eigene Styles, zuletzt scoped CSS
        var cssFiles = files.Where(f => f.EndsWith(".css", StringComparison.OrdinalIgnoreCase) && !f.EndsWith(".min.css.map", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "lib" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || IsMainLibraryCss(f))
            .OrderBy(f => f.Contains(Path.DirectorySeparatorChar + "lib" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? 0 : f.EndsWith(".razor.css", StringComparison.OrdinalIgnoreCase) ? 2 : 1)
            .ToList();
        foreach (var css in cssFiles)
        {
            var text = SafeRead(css);
            if (text is null) continue;
            Css.AddCss(text, Path.GetRelativePath(RootDirectory, css));
            PreviewStyles.Add((css, text));
        }

        if (!Css.Classes.Any(c => c.Name == "btn") && UsesBootstrapCdn(files))
            Css.AddKnownClasses(CssCatalog.CommonUtilityClasses, "bootstrap (CDN)");
    }

    private static bool IsMainLibraryCss(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        return name is "bootstrap.min.css" or "bootstrap.css";
    }

    private bool UsesBootstrapCdn(IEnumerable<string> files) =>
        files.Where(f => f.EndsWith("App.razor", StringComparison.OrdinalIgnoreCase) || f.EndsWith("index.html", StringComparison.OrdinalIgnoreCase) || f.EndsWith("_Host.cshtml", StringComparison.OrdinalIgnoreCase))
            .Select(SafeRead)
            .Any(t => t is not null && t.Contains("bootstrap", StringComparison.OrdinalIgnoreCase));

    /// <summary>Komponente aus einer .razor-Datei (plus Code-Behind) lesen.</summary>
    public ProjectComponentInfo? ReadComponent(string razorPath)
    {
        var text = SafeRead(razorPath);
        if (text is null) return null;
        var name = Path.GetFileNameWithoutExtension(razorPath);
        if (name.Equals("_Imports", StringComparison.OrdinalIgnoreCase)) return null;

        var parse = RazorParser.Parse(text);
        var members = new List<CodeMember>();
        if (parse.Document.CodeBlocks.Count > 0)
            members.AddRange(CodeInspector.AnalyzeCodeBlock(string.Join('\n', parse.Document.CodeBlocks), Path.GetFileName(razorPath)));

        var codeBehind = razorPath + ".cs";
        if (File.Exists(codeBehind))
        {
            var cs = SafeRead(codeBehind);
            if (cs is not null) members.AddRange(CodeInspector.AnalyzeFile(cs, name, Path.GetFileName(codeBehind)));
        }
        else codeBehind = null!;

        var scopedCss = razorPath + ".css";

        var parameters = members.Where(m => m.IsParameter).Select(ToParameter).ToList();
        // Name + NameChanged → bindbar
        parameters = parameters.Select(p =>
            parameters.Any(o => o.Name == p.Name + "Changed" && o.Editor == ValueEditor.Callback) ? p with { IsBindable = true } : p).ToList();

        var def = new ComponentDefinition
        {
            Key = "project:" + name,
            TagName = name,
            Label = name,
            Category = "project",
            Icon = parse.Document.PageRoute is null ? "puzzle" : "page",
            Template = $"<{name} />",
            Source = ComponentSource.Project,
            Parameters = parameters,
            AcceptsChildren = parameters.Any(p => p.Name == "ChildContent"),
            FilePath = razorPath,
            Keywords = Path.GetRelativePath(RootDirectory, razorPath),
        };

        return new ProjectComponentInfo
        {
            Name = name,
            FilePath = razorPath,
            CodeBehindPath = codeBehind,
            ScopedCssPath = File.Exists(scopedCss) ? scopedCss : null,
            Route = parse.Document.PageRoute,
            Members = members,
            Definition = def,
        };
    }

    private ParameterDefinition ToParameter(CodeMember m)
    {
        var type = m.BareType;
        var editor = ParameterDefinition.EditorFor(type);
        IReadOnlyList<string>? options = null;
        string? prefix = null;
        if (editor == ValueEditor.Expression && Enums.TryGetValue(type, out var en))
        {
            editor = ValueEditor.Enum;
            options = en.Values;
            prefix = type + ".";
        }
        return new ParameterDefinition(m.Name, m.Type, editor, options, m.IsRequired, m.Summary, false, prefix);
    }

    public ProjectComponentInfo? ComponentForFile(string path) =>
        Components.Values.FirstOrDefault(c => string.Equals(c.FilePath, path, StringComparison.OrdinalIgnoreCase));

    public void UpdateComponent(string razorPath)
    {
        var info = ReadComponent(razorPath);
        if (info is null) return;
        Components[info.Name] = info;
    }

    // ------------------------------------------------------------------

    public static IEnumerable<string> EnumerateFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] files, dirs;
            try
            {
                files = Directory.GetFiles(dir);
                dirs = Directory.GetDirectories(dir);
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var f in files) yield return f;
            foreach (var d in dirs)
                if (!IgnoredDirectories.Contains(Path.GetFileName(d)) && !Path.GetFileName(d).StartsWith('.'))
                    stack.Push(d);
        }
    }

    public static FileNode BuildTree(string root)
    {
        var node = new FileNode { Name = Path.GetFileName(root), FullPath = root, IsDirectory = true };
        Fill(node);
        return node;
    }

    private static void Fill(FileNode dir)
    {
        string[] dirs, files;
        try
        {
            dirs = Directory.GetDirectories(dir.FullPath);
            files = Directory.GetFiles(dir.FullPath);
        }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }

        foreach (var d in dirs.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(d);
            if (IgnoredDirectories.Contains(name) || name.StartsWith('.')) continue;
            var child = new FileNode { Name = name, FullPath = d, IsDirectory = true };
            Fill(child);
            dir.Children.Add(child);
        }

        var fileNodes = files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Select(f => new FileNode { Name = Path.GetFileName(f), FullPath = f })
            .ToList();
        var byName = fileNodes.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var f in fileNodes)
        {
            // Home.razor.cs / Home.razor.css / Home.razor.js unter Home.razor einhängen
            var m = NestedRegex().Match(f.Name);
            if (m.Success && byName.TryGetValue(m.Groups[1].Value, out var owner) && owner != f)
            {
                owner.Nested.Add(f);
                continue;
            }
            dir.Children.Add(f);
        }
    }

    public static string? SafeRead(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 8 * 1024 * 1024) return null;
            return File.ReadAllText(path);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    [GeneratedRegex(@"^(.+\.(?:razor|cshtml))\.(?:cs|css|js)$", RegexOptions.IgnoreCase)]
    private static partial Regex NestedRegex();
}
