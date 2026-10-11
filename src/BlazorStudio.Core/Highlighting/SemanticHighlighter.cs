using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;

namespace BlazorStudio.Core.Highlighting;

/// <summary>
/// Semantische Einfärbung wie in Rider: Roslyn ordnet jeden Bezeichner ein (Klasse, Methode, Property, Feld,
/// Parameter, lokale Variable …). Alle .cs-Dateien des Projekts liegen in einem Roslyn-Workspace, damit auch
/// Typen aus anderen Dateien und aus Paketen erkannt werden.
/// </summary>
public sealed partial class SemanticHighlighter : IDisposable
{
    /// <summary>Token-Typen in der Reihenfolge der Monaco-Legende.</summary>
    public static readonly string[] Legend =
    {
        "namespace", "class", "struct", "interface", "enum", "enumMember", "typeParameter", "delegate", "method",
        "property", "field", "event", "parameter", "variable", "keyword", "string", "number", "comment", "regexp", "operator",
        "label", "constant", "record", "xmlDocTag", "preprocessor", "controlKeyword", "stringEscape",
    };

    public static readonly string[] Modifiers = { "static", "declaration", "extension" };

    private static readonly ConcurrentDictionary<string, MetadataReference> RefCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private AdhocWorkspace? _workspace;
    private ProjectId? _projectId;
    private readonly Dictionary<string, DocumentId> _docs = new(StringComparer.OrdinalIgnoreCase);
    private string? _loadedFor;

    /// <summary>Workspace für ein Projekt aufbauen (Quelldateien + Referenzen). Mehrfachaufruf ist billig.</summary>
    public void Load(string projectDirectory, IEnumerable<string> sourceFiles, IEnumerable<string> referenceDlls, string? targetFramework = null)
    {
        var files = sourceFiles.ToList();
        var key = projectDirectory + "|" + targetFramework + "|" + files.Count + "|" + string.Join(';', referenceDlls.Take(3));
        if (key == _loadedFor && _workspace is not null) return;

        _lock.Wait();
        try
        {
            _workspace?.Dispose();
            _docs.Clear();
            var host = MefHostServices.Create(MefHostServices.DefaultAssemblies);
            _workspace = new AdhocWorkspace(host);
            var refs = PlatformReferences(targetFramework).Concat(referenceDlls).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(ToReference).Where(r => r is not null).Select(r => r!).ToList();
            var info = ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Create(), "Highlight", "Highlight", LanguageNames.CSharp,
                compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable),
                parseOptions: new CSharpParseOptions(LanguageVersion.Preview),
                metadataReferences: refs);
            var project = _workspace.AddProject(info);
            _projectId = project.Id;
            var solution = _workspace.CurrentSolution;
            // Globale Usings wie im SDK (ImplicitUsings)
            // Gibt es die vom SDK erzeugte GlobalUsings.g.cs (obj/…), kommt sie über sourceFiles; sonst die üblichen ImplicitUsings
            var implicitUsings = files.Any(f => f.EndsWith(".GlobalUsings.g.cs", StringComparison.OrdinalIgnoreCase))
                ? "global using System;"
                : "global using System; global using System.Collections.Generic; global using System.IO; global using System.Linq; global using System.Net.Http; global using System.Threading; global using System.Threading.Tasks;"
                  + " global using System.Net.Http.Json; global using Microsoft.AspNetCore.Builder; global using Microsoft.AspNetCore.Hosting; global using Microsoft.AspNetCore.Http;"
                  + " global using Microsoft.AspNetCore.Routing; global using Microsoft.Extensions.Configuration; global using Microsoft.Extensions.DependencyInjection;"
                  + " global using Microsoft.Extensions.Hosting; global using Microsoft.Extensions.Logging;";
            solution = solution.AddDocument(DocumentId.CreateNewId(_projectId), "__ImplicitUsings.cs", SourceText.From(implicitUsings));
            foreach (var f in files)
            {
                string text;
                try { text = File.ReadAllText(f); }
                catch (IOException) { continue; }
                var id = DocumentId.CreateNewId(_projectId);
                solution = solution.AddDocument(id, Path.GetFileName(f), SourceText.From(text), filePath: f);
                _docs[f] = id;
            }
            _workspace.TryApplyChanges(solution);
            _loadedFor = key;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Klassifiziert eine .cs-Datei (aktueller Editor-Text). Liefert (Start, Länge, Typ, Modifier-Bits).</summary>
    public async Task<IReadOnlyList<(int Start, int Length, int Type, int Mods)>> ClassifyCSharpAsync(string path, string text)
    {
        var doc = await UpsertAsync(path, text);
        return doc is null ? Array.Empty<(int, int, int, int)>() : await ClassifyAsync(doc, 0, text.Length, 0);
    }

    /// <summary>
    /// Klassifiziert die @code-Blöcke einer .razor-Datei: Der Inhalt wird als partielle Klasse eingebettet,
    /// die Bereiche werden auf die Positionen in der .razor-Datei zurückgerechnet.
    /// </summary>
    public async Task<IReadOnlyList<(int Start, int Length, int Type, int Mods)>> ClassifyRazorAsync(string path, string text, string className, IReadOnlyList<(int Start, int End)> codeRegions)
    {
        if (codeRegions.Count == 0) return Array.Empty<(int, int, int, int)>();
        // Alle Regionen nacheinander in eine Klasse; Offset-Tabelle für die Rückrechnung
        var sb = new System.Text.StringBuilder("public partial class " + className + " : Microsoft.AspNetCore.Components.ComponentBase {\n");
        var map = new List<(int Generated, int Original, int Length)>();
        foreach (var (start, end) in codeRegions)
        {
            map.Add((sb.Length, start, end - start));
            sb.Append(text, start, end - start).Append('\n');
        }
        sb.Append('}');
        var doc = await UpsertAsync(path + ".__razor.cs", sb.ToString());
        if (doc is null) return Array.Empty<(int, int, int, int)>();
        var result = new List<(int, int, int, int)>();
        foreach (var (gen, orig, len) in map)
        {
            foreach (var t in await ClassifyAsync(doc, gen, len, orig - gen)) result.Add(t);
        }
        return result;
    }

    private async Task<Document?> UpsertAsync(string path, string text)
    {
        if (_workspace is null || _projectId is null) return null;
        await _lock.WaitAsync();
        try
        {
            var solution = _workspace.CurrentSolution;
            if (_docs.TryGetValue(path, out var id) && solution.GetDocument(id) is { } existing)
            {
                var current = await existing.GetTextAsync();
                if (current.ToString() != text)
                {
                    solution = solution.WithDocumentText(id, SourceText.From(text));
                    _workspace.TryApplyChanges(solution);
                }
            }
            else
            {
                id = DocumentId.CreateNewId(_projectId);
                solution = solution.AddDocument(id, Path.GetFileName(path), SourceText.From(text), filePath: path);
                _workspace.TryApplyChanges(solution);
                _docs[path] = id;
            }
            return _workspace.CurrentSolution.GetDocument(id);
        }
        finally
        {
            _lock.Release();
        }
    }

    private static async Task<IReadOnlyList<(int Start, int Length, int Type, int Mods)>> ClassifyAsync(Document doc, int start, int length, int shift)
    {
        var spans = await Classifier.GetClassifiedSpansAsync(doc, new TextSpan(start, length));
        var result = new List<(int, int, int, int)>();
        // „static symbol“ ist eine Zusatz-Klassifizierung auf demselben Bereich
        var statics = spans.Where(s => s.ClassificationType == ClassificationTypeNames.StaticSymbol).Select(s => s.TextSpan).ToHashSet();
        foreach (var s in spans)
        {
            var type = Map(s.ClassificationType);
            if (type < 0) continue;
            var mods = statics.Contains(s.TextSpan) ? 1 : 0;
            if (s.ClassificationType == ClassificationTypeNames.ExtensionMethodName) mods |= 4;
            result.Add((s.TextSpan.Start + shift, s.TextSpan.Length, type, mods));
        }
        // Überlappungen entfernen (Monaco erwartet sortierte, disjunkte Tokens)
        result.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        var clean = new List<(int, int, int, int)>();
        var lastEnd = -1;
        foreach (var t in result)
        {
            if (t.Item1 < lastEnd) continue;
            clean.Add(t);
            lastEnd = t.Item1 + t.Item2;
        }
        return clean;
    }

    private static int Map(string c) => c switch
    {
        ClassificationTypeNames.NamespaceName => 0,
        ClassificationTypeNames.ClassName => 1,
        ClassificationTypeNames.RecordClassName => 22,
        ClassificationTypeNames.StructName or ClassificationTypeNames.RecordStructName => 2,
        ClassificationTypeNames.InterfaceName => 3,
        ClassificationTypeNames.EnumName => 4,
        ClassificationTypeNames.EnumMemberName => 5,
        ClassificationTypeNames.TypeParameterName => 6,
        ClassificationTypeNames.DelegateName => 7,
        ClassificationTypeNames.MethodName or ClassificationTypeNames.ExtensionMethodName => 8,
        ClassificationTypeNames.PropertyName => 9,
        ClassificationTypeNames.FieldName => 10,
        ClassificationTypeNames.EventName => 11,
        ClassificationTypeNames.ParameterName => 12,
        ClassificationTypeNames.LocalName => 13,
        ClassificationTypeNames.Keyword => 14,
        ClassificationTypeNames.ControlKeyword => 25,
        ClassificationTypeNames.StringLiteral or ClassificationTypeNames.VerbatimStringLiteral => 15,
        ClassificationTypeNames.StringEscapeCharacter => 26,
        ClassificationTypeNames.NumericLiteral => 16,
        ClassificationTypeNames.Comment => 17,
        ClassificationTypeNames.Operator or ClassificationTypeNames.OperatorOverloaded => 19,
        ClassificationTypeNames.LabelName => 20,
        ClassificationTypeNames.ConstantName => 21,
        ClassificationTypeNames.PreprocessorKeyword or ClassificationTypeNames.PreprocessorText => 24,
        _ when c.StartsWith("xml doc comment", StringComparison.Ordinal) => c.EndsWith("delimiter", StringComparison.Ordinal) || c.EndsWith("name", StringComparison.Ordinal) ? 23 : 17,
        _ => -1,
    };

    /// <summary>Monaco-Format: [deltaLine, deltaStart, length, type, modifiers]*.</summary>
    public static int[] Encode(string text, IReadOnlyList<(int Start, int Length, int Type, int Mods)> tokens)
    {
        var lineStarts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++) if (text[i] == '\n') lineStarts.Add(i + 1);
        var data = new List<int>(tokens.Count * 5);
        int prevLine = 0, prevChar = 0;
        foreach (var (start, length, type, mods) in tokens)
        {
            var line = UpperBound(lineStarts, start) - 1;
            var ch = start - lineStarts[line];
            var lineEnd = line + 1 < lineStarts.Count ? lineStarts[line + 1] - 1 : text.Length;
            var len = Math.Min(length, Math.Max(0, lineEnd - start));
            if (len <= 0) continue;
            data.Add(line - prevLine);
            data.Add(line == prevLine ? ch - prevChar : ch);
            data.Add(len);
            data.Add(type);
            data.Add(mods);
            prevLine = line;
            prevChar = ch;
        }
        return data.ToArray();
    }

    private static int UpperBound(List<int> list, int value)
    {
        int lo = 0, hi = list.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (list[mid] <= value) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private static MetadataReference? ToReference(string path)
    {
        if (!File.Exists(path)) return null;
        return RefCache.GetOrAdd(path, p =>
        {
            try { return MetadataReference.CreateFromFile(p); }
            catch (Exception) { return null!; }
        });
    }

    /// <summary>
    /// .NET- und ASP.NET-Core-Referenzen (für Typen wie ComponentBase, WebApplication, List&lt;T&gt; …) aus dem
    /// installierten SDK – nicht aus der Runtime von Blazor Studio, die im Setup ohne ASP.NET Core ausgeliefert wird.
    /// </summary>
    public static IEnumerable<string> PlatformReferences(string? targetFramework = null) => ReferenceAssemblies.Find(targetFramework);

    public void Dispose() => _workspace?.Dispose();
}
