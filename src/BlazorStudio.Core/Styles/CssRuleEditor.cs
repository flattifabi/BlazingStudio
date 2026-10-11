using System.Text;

namespace BlazorStudio.Core.Styles;

/// <summary>Liest und ersetzt einzelne Regeln (optional in @media) in einer CSS-Datei, ohne den Rest anzufassen.</summary>
public static class CssRuleEditor
{
    private sealed record Block(string Head, int Start, int BodyStart, int BodyEnd, int End);

    public static StyleDeclarations Get(string css, string selector, string? media)
    {
        var scope = FindScope(css, media);
        if (scope is null) return new StyleDeclarations();
        var rule = FindRule(css, scope.Value.BodyStart, scope.Value.BodyEnd, selector);
        return rule is null ? new StyleDeclarations() : StyleDeclarations.Parse(css[rule.BodyStart..rule.BodyEnd]);
    }

    public static string Set(string css, string selector, string? media, StyleDeclarations declarations)
    {
        var scope = FindScope(css, media);
        if (scope is null)
        {
            if (declarations.Count == 0) return css;
            var sb = new StringBuilder(css.TrimEnd());
            if (sb.Length > 0) sb.Append("\n\n");
            if (media is null)
            {
                sb.Append(selector).Append(" {\n").Append(declarations.ToBlock()).Append("}\n");
            }
            else
            {
                sb.Append("@media ").Append(media).Append(" {\n    ").Append(selector).Append(" {\n")
                  .Append(declarations.ToBlock("        ")).Append("    }\n}\n");
            }
            return sb.ToString();
        }

        var (bodyStart, bodyEnd, mediaBlock) = scope.Value;
        var indent = media is null ? "" : "    ";
        var rule = FindRule(css, bodyStart, bodyEnd, selector);
        if (rule is null)
        {
            if (declarations.Count == 0) return css;
            var ruleText = indent + selector + " {\n" + declarations.ToBlock(indent + "    ") + indent + "}\n";
            if (media is null)
            {
                var head = css.TrimEnd();
                return (head.Length > 0 ? head + "\n\n" : "") + ruleText;
            }
            return css[..bodyEnd].TrimEnd() + "\n" + ruleText + css[bodyEnd..];
        }

        if (declarations.Count == 0)
        {
            var removed = css[..rule.Start].TrimEnd(' ', '\t') + css[rule.End..].TrimStart('\r', '\n');
            // leeren @media-Block entfernen
            if (mediaBlock is not null)
            {
                var again = FindScope(removed, media);
                if (again is { } s && removed[s.BodyStart..s.BodyEnd].Trim().Length == 0 && s.Media is not null)
                    removed = removed[..s.Media.Start].TrimEnd() + "\n" + removed[s.Media.End..].TrimStart('\r', '\n');
            }
            return removed;
        }

        var body = "\n" + declarations.ToBlock(indent + "    ") + indent;
        return css[..rule.BodyStart] + body + css[rule.BodyEnd..];
    }

    private static (int BodyStart, int BodyEnd, Block? Media)? FindScope(string css, string? media)
    {
        if (media is null) return (0, css.Length, null);
        var wanted = NormalizeMedia(media);
        foreach (var b in Blocks(css, 0, css.Length))
        {
            if (!b.Head.StartsWith("@media", StringComparison.OrdinalIgnoreCase)) continue;
            if (NormalizeMedia(b.Head[6..]) == wanted) return (b.BodyStart, b.BodyEnd, b);
        }
        return null;
    }

    private static Block? FindRule(string css, int from, int to, string selector)
    {
        var wanted = Normalize(selector);
        return Blocks(css, from, to).FirstOrDefault(b => !b.Head.StartsWith('@') && Normalize(b.Head) == wanted);
    }

    private static IEnumerable<Block> Blocks(string css, int from, int to)
    {
        var i = from;
        while (i < to)
        {
            var open = css.IndexOf('{', i, to - i);
            if (open < 0) yield break;
            var headStart = i;
            var head = css[headStart..open];
            var semi = head.LastIndexOf(';');
            var closeOfPrev = head.LastIndexOf('}');
            var cut = Math.Max(semi, closeOfPrev);
            if (cut >= 0) headStart += cut + 1;
            // Kommentare am Anfang überspringen
            var raw = css[headStart..open];
            var lead = raw.Length - raw.TrimStart().Length;
            headStart += lead;
            var close = CssIndex.FindClose(css, open);
            if (close >= to) close = to - 1;
            yield return new Block(css[headStart..open].Trim(), headStart, open + 1, close, Math.Min(css.Length, close + 1));
            i = close + 1;
        }
    }

    private static string NormalizeMedia(string s) => new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();

    private static string Normalize(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Replace(" ,", ",").Replace(", ", ",");
}
