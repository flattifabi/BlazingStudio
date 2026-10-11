using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Tags;
using Microsoft.CodeAnalysis.Text;

namespace BlazorStudio.Core.Highlighting;

/// <summary>Ein C#-Vorschlag (Roslyn) inkl. optional einzufügendem using.</summary>
public sealed record CSharpCompletionItem(
    string Label,
    string Kind,
    string InsertText,
    string? Detail,
    string SortText,
    string FilterText,
    string? ImportNamespace,
    bool Preselect = false);

public sealed record CSharpCompletionList(IReadOnlyList<CSharpCompletionItem> Items, int ReplaceStart, int ReplaceEnd);

/// <summary>
/// C#-Vervollständigung wie in Rider/VS: Roslyn-CompletionService auf dem Projekt-Workspace, inkl. Typen und
/// Extension-Methoden aus Namespaces, die noch nicht importiert sind (z. B. <c>AddMudServices</c> aus
/// <c>MudBlazor.Services</c>) – die Auswahl fügt das using automatisch ein.
/// </summary>
public sealed partial class SemanticHighlighter
{
    private readonly Dictionary<string, CompletionItem> _lastRoslynItems = new();
    private Document? _lastCompletionDoc;

    /// <summary>Vorschläge an <paramref name="position"/> in einem (ggf. generierten) C#-Dokument.</summary>
    public async Task<CSharpCompletionList?> CompleteAsync(string path, string text, int position, bool explicitInvoke, CancellationToken ct = default)
    {
        var doc = await UpsertAsync(path, text);
        if (doc is null) return null;
        var service = CompletionService.GetService(doc);
        if (service is null) return null;
        position = Math.Clamp(position, 0, text.Length);

        var trigger = explicitInvoke || position == 0
            ? CompletionTrigger.Invoke
            : CompletionTrigger.CreateInsertionTrigger(text[position - 1]);
        var list = await service.GetCompletionsAsync(doc, position, trigger, cancellationToken: ct);

        // Bereich des aktuellen Bezeichners
        var start = position;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_')) start--;
        var end = position;
        while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_')) end++;
        var prefix = text[start..position];

        var items = new List<CSharpCompletionItem>();
        _lastRoslynItems.Clear();
        _lastCompletionDoc = doc;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (list is not null)
        {
            if (!list.ItemsList.Any() && !explicitInvoke && trigger.Kind == CompletionTriggerKind.Insertion && !char.IsLetterOrDigit(text[position - 1]) && text[position - 1] != '.')
                return new CSharpCompletionList(items, start, end);
            foreach (var it in list.ItemsList)
            {
                var insert = it.Properties.TryGetValue("InsertionText", out var ins) && !string.IsNullOrEmpty(ins) ? ins : it.DisplayText;
                var label = it.DisplayTextPrefix + it.DisplayText + it.DisplayTextSuffix;
                var kind = KindOf(it.Tags);
                var key = label + "|" + kind;
                if (!seen.Add(key)) continue;
                var id = items.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                _lastRoslynItems[id] = it;
                // Vorschläge aus nicht importierten Namespaces (Roslyn: ImportCompletionProvider) → using mitliefern
                var import = IsImportItem(it) ? it.InlineDescription : null;
                items.Add(new CSharpCompletionItem(label, kind, insert, import is null && it.InlineDescription.Length > 0 ? it.InlineDescription : null,
                    Rank(it, prefix) + (import is null ? "" : "~"), it.FilterText, import, it.Rules.MatchPriority > 0 && it.Rules.SelectionBehavior == CompletionItemSelectionBehavior.HardSelection));
            }
        }

        return new CSharpCompletionList(items, start, end);
    }

    /// <summary>Beschreibung (Signatur + XML-Doku) für einen Eintrag der letzten Liste.</summary>
    public async Task<string?> DescribeAsync(int index, CancellationToken ct = default)
    {
        var key = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (_lastCompletionDoc is not { } doc || !_lastRoslynItems.TryGetValue(key, out var item)) return null;
        var service = CompletionService.GetService(doc);
        if (service is null) return null;
        var desc = await service.GetDescriptionAsync(doc, item, ct);
        return desc?.Text;
    }

    private static string Rank(CompletionItem it, string prefix)
    {
        // Treffer mit passendem Anfang zuerst, dann Roslyns eigene Sortierung
        var starts = prefix.Length > 0 && it.FilterText.StartsWith(prefix, StringComparison.Ordinal) ? "0" : prefix.Length > 0 && it.FilterText.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? "1" : "2";
        var local = it.Tags.Contains(WellKnownTags.Local) || it.Tags.Contains(WellKnownTags.Parameter) ? "0" : it.Tags.Contains(WellKnownTags.Keyword) ? "3" : it.Tags.Contains(WellKnownTags.Snippet) ? "4" : "1";
        return starts + local + it.SortText;
    }

    private static string KindOf(IReadOnlyCollection<string> tags)
    {
        foreach (var t in tags)
        {
            switch (t)
            {
                case WellKnownTags.Class: return "class";
                case WellKnownTags.Structure: return "struct";
                case WellKnownTags.Interface: return "interface";
                case WellKnownTags.Enum: return "enum";
                case WellKnownTags.EnumMember: return "enumMember";
                case WellKnownTags.Delegate: return "delegate";
                case WellKnownTags.ExtensionMethod: return "extension";
                case WellKnownTags.Method: return "method";
                case WellKnownTags.Property: return "property";
                case WellKnownTags.Field: return "field";
                case WellKnownTags.Event: return "event";
                case WellKnownTags.Constant: return "constant";
                case WellKnownTags.Local: return "local";
                case WellKnownTags.Parameter: return "parameter";
                case WellKnownTags.RangeVariable: return "local";
                case WellKnownTags.Namespace: return "namespace";
                case WellKnownTags.TypeParameter: return "typeParameter";
                case WellKnownTags.Keyword: return "keyword";
                case WellKnownTags.Snippet: return "snippet";
            }
        }
        return "text";
    }

    private static bool IsImportItem(CompletionItem it) =>
        it.InlineDescription.Length > 0
        && NamespaceLike().IsMatch(it.InlineDescription)
        && (it.Properties.ContainsKey("MethodKey") || it.Properties.ContainsKey("ReceiverKey") || it.Properties.ContainsKey("TypeAritySuffixName")
            || it.Properties.ContainsKey("AttributeFullName") || it.Properties.ContainsKey("SymbolKeyData"));

    [System.Text.RegularExpressions.GeneratedRegex(@"^[A-Za-z_][\w]*(\.[A-Za-z_][\w]*)*$")]
    private static partial System.Text.RegularExpressions.Regex NamespaceLike();

    /// <summary>Position für ein neues using in einer .cs-Datei (nach dem letzten using am Dateianfang).</summary>
    public static (int Offset, string Text) UsingInsertion(string text, string ns)
    {
        var tree = CSharpSyntaxTree.ParseText(text);
        var root = (CompilationUnitSyntax)tree.GetRoot();
        var usings = root.Usings;
        if (usings.Count > 0)
        {
            // alphabetisch einsortieren, sonst ans Ende
            var before = usings.FirstOrDefault(u => u.StaticKeyword.IsKind(SyntaxKind.None) && u.Alias is null && string.CompareOrdinal(u.Name?.ToString(), ns) > 0 && !(u.Name?.ToString().StartsWith("System", StringComparison.Ordinal) == true && !ns.StartsWith("System", StringComparison.Ordinal)));
            if (before is not null) return (text.LastIndexOf('\n', Math.Max(0, before.SpanStart - 1)) + 1, "using " + ns + ";" + Environment.NewLine);
            return (usings.Last().FullSpan.End, "using " + ns + ";" + Environment.NewLine);
        }
        return (0, "using " + ns + ";" + Environment.NewLine + Environment.NewLine);
    }
}
