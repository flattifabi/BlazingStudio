using System.Text.RegularExpressions;

namespace BlazorStudio.Core.Razor;

/// <summary>
/// Provider-Komponenten (&lt;MudThemeProvider /&gt;, &lt;RadzenComponents /&gt; …) an der richtigen Stelle ins Layout setzen:
/// hinter die Direktiven bzw. neben vorhandene Provider – nie in einen @code-Block.
/// </summary>
public static partial class LayoutProviders
{
    /// <summary>Fügt fehlende Provider ein. Liefert den neuen Text (unverändert, wenn nichts fehlt).</summary>
    public static string Insert(string text, IEnumerable<string> providers)
    {
        var nl = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var missing = providers.Where(p => !Contains(text, p)).ToList();
        if (missing.Count == 0) return text;
        var at = InsertOffset(text);
        var block = string.Concat(missing.Select(p => p + nl));
        // Leerzeile nach Direktiven, wenn direkt Markup folgt
        if (at > 0 && at <= text.Length && !LastLineIsProvider(text, at) && !text[..at].EndsWith(nl + nl, StringComparison.Ordinal)) block = nl + block;
        return text.Insert(at, block);
    }

    /// <summary>Provider, die versehentlich in @code { } gelandet sind, an die richtige Stelle verschieben. null = nichts zu tun.</summary>
    public static string? Repair(string text, IEnumerable<string> knownProviders)
    {
        var names = knownProviders.Select(TagName).Where(n => n.Length > 0).Distinct().ToList();
        var regions = RazorCSharpProjection.CodeRegions(text);
        var moved = new List<string>();
        var result = text;
        foreach (var (start, end) in regions.OrderByDescending(r => r.Start))
        {
            var inner = result[start..end];
            var cleaned = ProviderLine().Replace(inner, m =>
            {
                if (!names.Contains(m.Groups[1].Value)) return m.Value;
                moved.Insert(0, m.Groups[0].Value.Trim());
                return "";
            });
            if (cleaned != inner) result = result[..start] + cleaned + result[end..];
        }
        if (moved.Count == 0) return null;
        result = result.Replace("@code {\n\n    ", "@code {\n    ").Replace("@code {\r\n\r\n    ", "@code {\r\n    ");
        return Insert(result, moved.Distinct());
    }

    /// <summary>Hinter dem letzten vorhandenen Provider, sonst hinter dem Direktiven-Kopf.</summary>
    public static int InsertOffset(string text)
    {
        var regions = RazorCSharpProjection.CodeRegions(text);
        var offset = 0;
        var afterProvider = -1;
        var inHead = true;
        var pos = 0;
        while (pos < text.Length)
        {
            var lineEnd = text.IndexOf('\n', pos);
            var next = lineEnd < 0 ? text.Length : lineEnd + 1;
            var line = text[pos..next].Trim();
            var inCode = regions.Any(r => pos >= r.Start && pos < r.End);
            if (!inCode)
            {
                if (inHead && (line.Length == 0 || IsHeadDirective(line))) { if (line.Length > 0) offset = next; }
                else inHead = false;
                if (ProviderLine().IsMatch(line) && TagName(line).EndsWith("Provider", StringComparison.Ordinal) || line.StartsWith("<RadzenComponents", StringComparison.Ordinal))
                    afterProvider = next;
            }
            pos = next;
        }
        if (afterProvider >= 0) return afterProvider;
        if (offset > 0 && offset == text.Length && !text.EndsWith('\n')) return offset;
        return offset;
    }

    private static bool LastLineIsProvider(string text, int at)
    {
        var before = text[..at].TrimEnd();
        var ls = before.LastIndexOf('\n') + 1;
        var line = before[ls..].Trim();
        return line.StartsWith('<');
    }

    private static bool IsHeadDirective(string line) =>
        HeadDirective().IsMatch(line);

    private static bool Contains(string text, string provider) => text.Contains("<" + TagName(provider), StringComparison.Ordinal);

    private static string TagName(string tag)
    {
        var m = TagNameRx().Match(tag);
        return m.Success ? m.Groups[1].Value : "";
    }

    [GeneratedRegex(@"^[ \t]*<([A-Z][\w.]*)\b[^>]*/>[ \t]*\r?\n?", RegexOptions.Multiline)]
    private static partial Regex ProviderLine();

    [GeneratedRegex(@"^\s*<([A-Z][\w.]*)")]
    private static partial Regex TagNameRx();

    [GeneratedRegex(@"^@(inherits|using|inject|layout|implements|namespace|attribute|page|rendermode|typeparam|preservewhitespace)\b")]
    private static partial Regex HeadDirective();
}
