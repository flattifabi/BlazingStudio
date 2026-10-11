using System.Text;
using System.Text.RegularExpressions;

namespace BlazorStudio.Core.Styles;

public sealed record CssClassInfo(string Name, string SourceFile, string? Preview);

public sealed record CssToken(string Name, string Value, string SourceFile)
{
    public bool IsColor => Value.StartsWith('#') || Value.StartsWith("rgb", StringComparison.OrdinalIgnoreCase) ||
                           Value.StartsWith("hsl", StringComparison.OrdinalIgnoreCase) || Value.StartsWith("oklch", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Sammelt Klassen und CSS-Variablen (Design-Tokens) aus CSS-Dateien.</summary>
public sealed partial class CssIndex
{
    private const int MaxClasses = 6000;

    private readonly Dictionary<string, CssClassInfo> _classes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CssToken> _tokens = new(StringComparer.Ordinal);

    public IReadOnlyCollection<CssClassInfo> Classes => _classes.Values;

    public IReadOnlyCollection<CssToken> Tokens => _tokens.Values;

    public void Clear()
    {
        _classes.Clear();
        _tokens.Clear();
    }

    public void AddCss(string css, string sourceFile)
    {
        css = StripComments(css);
        foreach (var (selector, body) in EnumerateRules(css))
        {
            if (selector.StartsWith('@')) continue;
            foreach (Match m in ClassRegex().Matches(selector))
            {
                if (_classes.Count >= MaxClasses) break;
                var name = m.Groups[1].Value;
                if (_classes.ContainsKey(name)) continue;
                var preview = selector.Trim() == "." + name ? Compact(body) : null;
                _classes[name] = new CssClassInfo(name, sourceFile, preview);
            }
            if (selector.Contains(":root") || selector.Trim() is "html" or "body" or "*")
            {
                foreach (var d in StyleDeclarations.Parse(body).Items)
                    if (d.Key.StartsWith("--", StringComparison.Ordinal))
                        _tokens[d.Key] = new CssToken(d.Key, d.Value, sourceFile);
            }
        }
    }

    public void AddKnownClasses(IEnumerable<string> names, string source)
    {
        foreach (var n in names) _classes.TryAdd(n, new CssClassInfo(n, source, null));
    }

    public IEnumerable<CssClassInfo> Search(string prefix, int max = 60)
    {
        var p = prefix.Trim();
        return _classes.Values
            .Where(c => p.Length == 0 || c.Name.Contains(p, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(c => c.SourceFile.Contains("lib", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(c => c.Name.Length)
            .Take(max);
    }

    public CssClassInfo? Find(string name) => _classes.GetValueOrDefault(name);

    private static string? Compact(string body)
    {
        var s = StyleDeclarations.Parse(body).ToString();
        return s.Length == 0 ? null : s.Length > 120 ? s[..117] + "…" : s;
    }

    internal static string StripComments(string css) => CommentRegex().Replace(css, "");

    /// <summary>Liefert (Selektor, Rumpf) für alle Regeln, auch in @media verschachtelt.</summary>
    internal static IEnumerable<(string Selector, string Body)> EnumerateRules(string css)
    {
        var i = 0;
        while (i < css.Length)
        {
            var open = css.IndexOf('{', i);
            if (open < 0) yield break;
            var selector = css[i..open].Trim();
            // @import; o. ä. vor dem Selektor überspringen
            var semi = selector.LastIndexOf(';');
            if (semi >= 0) selector = selector[(semi + 1)..].Trim();
            var close = FindClose(css, open);
            var body = css[(open + 1)..Math.Max(open + 1, close)];
            if (selector.StartsWith("@media", StringComparison.OrdinalIgnoreCase) ||
                selector.StartsWith("@supports", StringComparison.OrdinalIgnoreCase) ||
                selector.StartsWith("@container", StringComparison.OrdinalIgnoreCase) ||
                selector.StartsWith("@layer", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var r in EnumerateRules(body)) yield return r;
            }
            else
            {
                yield return (selector, body);
            }
            i = close + 1;
        }
    }

    internal static int FindClose(string css, int open)
    {
        var depth = 0;
        for (var j = open; j < css.Length; j++)
        {
            if (css[j] == '{') depth++;
            else if (css[j] == '}')
            {
                depth--;
                if (depth == 0) return j;
            }
        }
        return css.Length - 1;
    }

    [GeneratedRegex(@"\.(-?[_a-zA-Z][_a-zA-Z0-9-]*)")]
    private static partial Regex ClassRegex();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex CommentRegex();
}

/// <summary>
/// Schreibt Projekt-CSS so um, dass es nur innerhalb der Designer-Fläche wirkt
/// (html/body/:root → Scope, alle anderen Selektoren mit Scope-Präfix, url() auf den Projekt-Host).
/// </summary>
public static partial class CssScoper
{
    /// <summary>
    /// scope: Selektor des Vorschau-Containers. rewriteUrl: relative url() umschreiben.
    /// Breiten-Media-Queries werden zu Container-Queries, damit die Gerätebreite im Designer wirkt;
    /// :hover/:focus/:active greifen zusätzlich über data-bs-state, damit Zustände in der Vorschau sichtbar sind.
    /// </summary>
    public static string Scope(string css, string scope, Func<string, string>? rewriteUrl = null)
    {
        css = CssIndex.StripComments(css);
        var sb = new StringBuilder(css.Length + 1024);
        ScopeInto(sb, css, scope);
        var result = sb.ToString();
        result = StateRegex().Replace(result, m => $":is(:{m.Groups[1].Value},[data-bs-state~=\"{m.Groups[1].Value}\"])");
        if (rewriteUrl is not null)
        {
            result = UrlRegex().Replace(result, m =>
            {
                var url = m.Groups[2].Value;
                if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || url.Contains("://") || url.StartsWith('#')) return m.Value;
                return $"url(\"{rewriteUrl(url)}\")";
            });
        }
        return result;
    }

    private static void ScopeInto(StringBuilder sb, string css, string scope)
    {
        var i = 0;
        while (i < css.Length)
        {
            var open = css.IndexOf('{', i);
            if (open < 0) break;
            var head = css[i..open];
            // Anweisungen wie @import / @charset vor der Regel
            var semi = head.LastIndexOf(';');
            if (semi >= 0)
            {
                var statements = head[..(semi + 1)];
                if (!statements.TrimStart().StartsWith("@import", StringComparison.OrdinalIgnoreCase)) sb.Append(statements);
                head = head[(semi + 1)..];
            }
            var selector = head.Trim();
            var close = CssIndex.FindClose(css, open);
            var body = css[(open + 1)..Math.Max(open + 1, close)];

            if (selector.StartsWith("@media", StringComparison.OrdinalIgnoreCase) ||
                selector.StartsWith("@supports", StringComparison.OrdinalIgnoreCase) ||
                selector.StartsWith("@container", StringComparison.OrdinalIgnoreCase) ||
                selector.StartsWith("@layer", StringComparison.OrdinalIgnoreCase))
            {
                sb.Append(MediaToContainer(selector)).Append("{\n");
                ScopeInto(sb, body, scope);
                sb.Append("}\n");
            }
            else if (selector.StartsWith('@'))
            {
                // @keyframes, @font-face ... unverändert
                sb.Append(selector).Append('{').Append(body).Append("}\n");
            }
            else
            {
                var parts = StyleDeclarations.SplitTopLevel(selector, ',')
                    .Select(s => ScopeSelector(s.Trim(), scope))
                    .Where(s => s.Length > 0);
                sb.Append(string.Join(", ", parts)).Append('{').Append(body).Append("}\n");
            }
            i = close + 1;
        }
    }

    private static string ScopeSelector(string sel, string scope)
    {
        if (sel.Length == 0) return "";
        sel = sel.Replace("::deep", "").Replace("  ", " ").Trim();
        foreach (var root in new[] { ":root", "html", "body" })
        {
            if (sel == root) return scope;
            if (sel.StartsWith(root + " ", StringComparison.OrdinalIgnoreCase) ||
                sel.StartsWith(root + ">", StringComparison.OrdinalIgnoreCase) ||
                sel.StartsWith(root + ".", StringComparison.OrdinalIgnoreCase) ||
                sel.StartsWith(root + ":", StringComparison.OrdinalIgnoreCase) ||
                sel.StartsWith(root + "[", StringComparison.OrdinalIgnoreCase))
            {
                var rest = sel[root.Length..];
                return rest.StartsWith(' ') || rest.StartsWith('>') ? scope + rest : scope + rest;
            }
        }
        if (sel.StartsWith("html body", StringComparison.OrdinalIgnoreCase)) return scope + sel[9..];
        return scope + " " + sel;
    }

    /// <summary>@media (min-width: 768px) → @container (min-width: 768px), wenn nur Breiten abgefragt werden.</summary>
    public static string MediaToContainer(string atRule)
    {
        if (!atRule.StartsWith("@media", StringComparison.OrdinalIgnoreCase)) return atRule;
        var cond = atRule[6..].Trim();
        var lower = cond.ToLowerInvariant();
        foreach (var prefix in new[] { "only screen and", "screen and", "all and" })
            if (lower.StartsWith(prefix, StringComparison.Ordinal))
            {
                cond = cond[prefix.Length..].Trim();
                lower = cond.ToLowerInvariant();
            }
        if (!lower.Contains("width") || lower.Contains(',') ||
            ForbiddenMediaRegex().IsMatch(lower)) return atRule;
        return "@container " + cond.Replace("device-width", "width");
    }

    [GeneratedRegex(@"url\(\s*(['""]?)([^'"")]+)\1\s*\)")]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"height|orientation|prefers|hover|pointer|print|resolution|aspect|color|monochrome|scan|grid|update")]
    private static partial Regex ForbiddenMediaRegex();

    [GeneratedRegex(@"(?<!:):(hover|focus|active|focus-visible|focus-within)(?![\w-])")]
    private static partial Regex StateRegex();
}
