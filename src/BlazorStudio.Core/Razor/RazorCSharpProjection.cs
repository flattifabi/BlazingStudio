using System.Text;
using System.Text.RegularExpressions;

namespace BlazorStudio.Core.Razor;

/// <summary>Ergebnis: generierte C#-Datei und die Cursorposition darin.</summary>
/// <param name="Map">Abschnitte (generiert, original, Länge), die 1:1 aus der .razor-Datei stammen – für die Rückrechnung von Positionen.</param>
public sealed record RazorProjection(string Code, int Position, RazorCSharpContext Context, IReadOnlyList<(int Gen, int Orig, int Len)>? Map = null)
{
    /// <summary>Generierte Position → Position in der .razor-Datei (null, wenn generierter Code).</summary>
    public int? ToOriginal(int gen)
    {
        if (Map is null) return null;
        foreach (var (g, o, l) in Map)
            if (gen >= g && gen <= g + l) return o + (gen - g);
        return null;
    }
}

public enum RazorCSharpContext { CodeBlock, Expression, Statement, AttributeValue, Type, Using, Attribute }

/// <summary>
/// Bildet die C#-Anteile einer .razor-Datei auf eine virtuelle Klasse ab – ähnlich wie der Razor-Compiler –,
/// damit Roslyn an jeder C#-Stelle Vorschläge liefern kann: @code-Blöcke, @(…)/@ausdruck, @if/@foreach-Köpfe,
/// @{ }-Blöcke, C#-Attributwerte (@onclick, @bind, Komponenten-Parameter, @rendermode …) und Direktiven
/// (@using, @inject, @inherits, @rendermode, @layout, @attribute, @typeparam).
/// </summary>
public static partial class RazorCSharpProjection
{
    /// <param name="text">Inhalt der .razor-Datei.</param>
    /// <param name="offset">Cursor.</param>
    /// <param name="className">Klassenname (Dateiname).</param>
    /// <param name="ns">Namespace der Komponente.</param>
    /// <param name="importUsings">@using-Einträge aus den _Imports.razor-Dateien (ohne „@using “).</param>
    /// <param name="isCSharpAttribute">Ob ein Attributwert C# ist (z. B. Komponenten-Parameter, die keine Strings sind).</param>
    public static RazorProjection? Project(string text, int offset, string className, string ns, IEnumerable<string> importUsings, Func<string, string, bool>? isCSharpAttribute = null)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        var usings = new List<string>(importUsings);
        var injects = new List<string>();
        var attributes = new List<string>();
        string? inherits = null;
        var implements = new List<string>();
        var typeParams = new List<string>();
        string? fileNs = null;

        var body = new StringBuilder();          // Inhalt der Render-Methode
        var codeBlocks = new List<(int Start, int End)>();
        var open = 0;                             // offene { im Render-Body
        (string Snippet, RazorCSharpContext Ctx, string Prefix)? cursor = null;
        string? cursorDirective = null;           // Direktive, in der der Cursor steht

        var i = 0;
        while (i < text.Length)
        {
            if (i >= offset && cursor is null && cursorDirective is null) break;
            var c = text[i];

            // Razor-Kommentar
            if (c == '@' && At(text, i, "@*"))
            {
                var e = text.IndexOf("*@", i + 2, StringComparison.Ordinal);
                if (e < 0 || e + 2 > offset && i < offset) return null;
                i = e + 2;
                continue;
            }
            if (c == '<' && At(text, i, "<!--"))
            {
                var e = text.IndexOf("-->", i + 4, StringComparison.Ordinal);
                if (e < 0 || e + 3 > offset && i < offset) return null;
                i = e + 3;
                continue;
            }

            // Tag
            if (c == '<' && i + 1 < text.Length && (char.IsLetter(text[i + 1]) || text[i + 1] == '/'))
            {
                var end = TagEnd(text, i);
                if (end < 0 || end >= offset && i < offset)
                {
                    // Cursor im Tag → nur Attributwert interessant
                    var tagText = text[i..offset];
                    var av = AttributeValueAtEnd(tagText);
                    if (av is null) return null;
                    var (tag, attr, value) = av.Value;
                    if (!IsCSharpValue(tag, attr, value, isCSharpAttribute)) return null;
                    var expr = value.StartsWith("@(", StringComparison.Ordinal) ? value[2..] : value.TrimStart('@');
                    cursor = (expr, RazorCSharpContext.AttributeValue, attr.StartsWith("@on", StringComparison.Ordinal) || attr.Contains(":after") ? "System.Action __a = " : "var __v = ");
                    break;
                }
                // Tag vollständig vor dem Cursor: C#-Attributwerte ignorieren (keine Bindungen)
                i = end + 1;
                continue;
            }

            if (c == '@' && i + 1 < text.Length)
            {
                var rest = text.AsSpan(i + 1);
                // @@ = Escape
                if (rest[0] == '@') { i += 2; continue; }

                // @code / @functions
                var m = CodeDirective().Match(text, i);
                if (m.Success && m.Index == i)
                {
                    var brace = i + m.Length - 1;
                    var e = CSharpScanner.SkipBalanced(text, brace, '{', '}');
                    var endIdx = e < 0 ? text.Length : e;
                    codeBlocks.Add((brace + 1, Math.Max(brace + 1, endIdx - 1)));
                    if (offset > brace && offset < endIdx)
                    {
                        cursor = ("", RazorCSharpContext.CodeBlock, "");
                    }
                    i = endIdx;
                    continue;
                }

                // Direktiven am Zeilenanfang
                if (LineStart(text, i))
                {
                    var dm = Directive().Match(text, i);
                    if (dm.Success && dm.Index == i)
                    {
                        var kw = dm.Groups[1].Value;
                        var lineEnd = text.IndexOf('\n', i);
                        if (lineEnd < 0) lineEnd = text.Length;
                        var valueStart = i + dm.Length;
                        var value = text[valueStart..lineEnd].TrimEnd('\r');
                        if (offset >= valueStart && offset <= lineEnd)
                        {
                            cursorDirective = kw;
                            var typed = text[valueStart..offset];
                            cursor = kw switch
                            {
                                "using" => (typed, RazorCSharpContext.Using, ""),
                                "inject" => typed.Contains(' ') ? null : (typed, RazorCSharpContext.Type, ""),
                                "inherits" or "layout" or "implements" => (typed, RazorCSharpContext.Type, ""),
                                "attribute" => (typed.TrimStart('[').TrimStart(), RazorCSharpContext.Attribute, ""),
                                "rendermode" or "page" or "namespace" or "preservewhitespace" => kw == "rendermode" ? (typed, RazorCSharpContext.Expression, "var __r = ") : null,
                                _ => null,
                            };
                            if (cursor is null) return null;
                            break;
                        }
                        switch (kw)
                        {
                            case "using": usings.Add(value.Trim()); break;
                            case "inject": injects.Add(value.Trim()); break;
                            case "inherits": inherits = value.Trim(); break;
                            case "implements": implements.Add(value.Trim()); break;
                            case "typeparam": typeParams.Add(value.Trim().Split(' ', 2)[0]); break;
                            case "namespace": fileNs = value.Trim(); break;
                            case "attribute": attributes.Add(value.Trim()); break;
                        }
                        i = lineEnd;
                        continue;
                    }
                }

                // @{ … }
                if (rest[0] == '{')
                {
                    var e = CSharpScanner.SkipBalanced(text, i + 1, '{', '}');
                    var inner = e < 0 || e > offset && i < offset ? text[(i + 2)..offset] : text[(i + 2)..(e - 1)];
                    if (e < 0 || e > offset)
                    {
                        cursor = (StripMarkup(inner), RazorCSharpContext.Statement, "");
                        break;
                    }
                    body.Append("{ ").Append(StripMarkup(inner)).Append(" }\n");
                    i = e;
                    continue;
                }

                // @( … )
                if (rest[0] == '(')
                {
                    var e = CSharpScanner.SkipBalanced(text, i + 1, '(', ')');
                    if (e < 0 || e > offset)
                    {
                        cursor = (text[(i + 2)..offset], RazorCSharpContext.Expression, "var __x = ");
                        break;
                    }
                    i = e;
                    continue;
                }

                // @if / @foreach … Blockköpfe
                var bm = BlockKeyword().Match(text, i);
                if (bm.Success && bm.Index == i)
                {
                    var kw = bm.Groups[1].Value;
                    var p = i + bm.Length - 1; // auf '('
                    var pe = CSharpScanner.SkipBalanced(text, p, '(', ')');
                    if (pe < 0 || pe > offset)
                    {
                        cursor = (text[(i + 1)..offset], RazorCSharpContext.Statement, "");
                        break;
                    }
                    var header = text[(i + 1)..pe];
                    var brace = text.IndexOf('{', pe);
                    if (brace < 0 || text[pe..brace].Trim().Length > 0) { i = pe; continue; }
                    body.Append(kw == "switch" ? "{ var __sw = " + header[6..].Trim() + ";\n" : header + " {\n");
                    open++;
                    i = brace + 1;
                    continue;
                }

                // implizite Ausdrücke @a.b.c / @Methode(x)
                var im = ImplicitExpr().Match(text, i);
                if (im.Success && im.Index == i)
                {
                    var exprEnd = i + im.Length;
                    // Klammern/Indexer mitnehmen
                    while (exprEnd < text.Length && text[exprEnd] is '(' or '[')
                    {
                        var close = text[exprEnd] == '(' ? ')' : ']';
                        var e = CSharpScanner.SkipBalanced(text, exprEnd, text[exprEnd], close);
                        if (e < 0 || e > offset) { exprEnd = -1; break; }
                        exprEnd = e;
                        var more = ImplicitTail().Match(text, exprEnd);
                        if (more.Success && more.Index == exprEnd) exprEnd += more.Length;
                    }
                    if (exprEnd < 0 || exprEnd >= offset && i < offset)
                    {
                        cursor = (text[(i + 1)..offset], RazorCSharpContext.Expression, "var __x = ");
                        break;
                    }
                    i = exprEnd;
                    continue;
                }
                if (i + 1 == offset)
                {
                    cursor = ("", RazorCSharpContext.Expression, "var __x = ");
                    break;
                }
            }

            // Schließende Klammer eines @if/@foreach-Blocks auf Markup-Ebene
            if (c == '}' && open > 0)
            {
                body.Append("}\n");
                open--;
                i++;
                var em = ElseClause().Match(text, i);
                if (em.Success)
                {
                    var j = i + em.Length;
                    if (em.Groups[1].Success)
                    {
                        // else if (…) {
                        var pStart = j - 1;
                        var pe = CSharpScanner.SkipBalanced(text, pStart, '(', ')');
                        if (pe < 0 || pe > offset)
                        {
                            if (pStart < offset) { cursor = ("if " + text[pStart..offset], RazorCSharpContext.Statement, ""); break; }
                            continue;
                        }
                        var brace = text.IndexOf('{', pe);
                        if (brace < 0 || text[pe..brace].Trim().Length > 0) continue;
                        body.Append("if ").Append(text, pStart, pe - pStart).Append(" {\n");
                        open++;
                        i = brace + 1;
                    }
                    else
                    {
                        var brace = text.IndexOf('{', j);
                        if (brace < 0 || text[j..brace].Trim().Length > 0) continue;
                        body.Append("{\n");
                        open++;
                        i = brace + 1;
                    }
                }
                continue;
            }
            i++;
        }

        if (cursor is null) return null;

        // Rest der Datei: @code-Blöcke und Direktiven hinter dem Cursor gehören trotzdem zur Klasse
        var scanFrom = Math.Max(i, offset);
        foreach (Match m in CodeDirective().Matches(text))
        {
            if (m.Index < scanFrom || codeBlocks.Any(b => m.Index < b.End)) continue;
            var brace = m.Index + m.Length - 1;
            var e = CSharpScanner.SkipBalanced(text, brace, '{', '}');
            codeBlocks.Add((brace + 1, Math.Max(brace + 1, (e < 0 ? text.Length : e) - 1)));
        }
        foreach (Match m in Directive().Matches(text))
        {
            if (m.Index < scanFrom || !LineStart(text, m.Index) || codeBlocks.Any(b => m.Index >= b.Start && m.Index < b.End)) continue;
            var lineEnd = text.IndexOf('\n', m.Index);
            var value = text[(m.Index + m.Length)..(lineEnd < 0 ? text.Length : lineEnd)].Trim();
            switch (m.Groups[1].Value)
            {
                case "using": usings.Add(value); break;
                case "inject": injects.Add(value); break;
                case "inherits": inherits = value; break;
                case "implements": implements.Add(value); break;
                case "typeparam": typeParams.Add(value.Split(' ', 2)[0]); break;
                case "attribute": attributes.Add(value); break;
            }
        }

        // ---------- C# zusammensetzen ----------
        var sb = new StringBuilder();
        foreach (var u in usings.Where(u => u.Length > 0).Distinct())
        {
            if (cursor.Value.Ctx == RazorCSharpContext.Using) break;
            sb.Append("using ").Append(u.TrimEnd(';')).Append(";\n");
        }
        int pos;
        if (cursor.Value.Ctx == RazorCSharpContext.Using)
        {
            sb.Append("using ");
            pos = sb.Length + cursor.Value.Snippet.Length;
            sb.Append(cursor.Value.Snippet).Append(";\n");
            foreach (var u in usings.Where(u => u.Length > 0).Distinct()) sb.Append("using ").Append(u.TrimEnd(';')).Append(";\n");
            sb.Append("namespace ").Append(fileNs ?? ns).Append(" { }\n");
            return new RazorProjection(sb.ToString(), pos, RazorCSharpContext.Using);
        }

        sb.Append("namespace ").Append(fileNs ?? ns).Append("\n{\n");
        foreach (var a in attributes) sb.Append(a).Append('\n');
        if (cursor.Value.Ctx == RazorCSharpContext.Attribute)
        {
            sb.Append('[');
            pos = sb.Length + cursor.Value.Snippet.Length;
            sb.Append(cursor.Value.Snippet).Append("]\n");
        }
        else pos = -1;

        sb.Append("public partial class ").Append(className);
        if (typeParams.Count > 0) sb.Append('<').Append(string.Join(", ", typeParams)).Append('>');
        var bases = new List<string> { inherits ?? "global::Microsoft.AspNetCore.Components.ComponentBase" };
        bases.AddRange(implements);
        sb.Append(" : ").Append(string.Join(", ", bases)).Append("\n{\n");

        foreach (var inj in injects)
        {
            var parts = inj.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            sb.Append("[global::Microsoft.AspNetCore.Components.InjectAttribute] private ").Append(string.Join(' ', parts[..^1])).Append(' ').Append(parts[^1]).Append(" { get; set; } = default!;\n");
        }
        if (cursor.Value.Ctx == RazorCSharpContext.Type && cursorDirective is "inject" or "inherits" or "implements" or "layout" or "typeparam")
        {
            sb.Append("private ");
            pos = sb.Length + cursor.Value.Snippet.Length;
            sb.Append(cursor.Value.Snippet).Append(" __t;\n");
        }

        // Render-Methode
        sb.Append("private void __Render(global::Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder __builder)\n{\n");
        sb.Append(body);
        var map = new List<(int Gen, int Orig, int Len)>();
        if (cursor.Value.Ctx is RazorCSharpContext.Expression or RazorCSharpContext.Statement or RazorCSharpContext.AttributeValue)
        {
            sb.Append(cursor.Value.Prefix);
            pos = sb.Length + cursor.Value.Snippet.Length;
            var snippetStart = sb.Length;
            sb.Append(cursor.Value.Snippet);
            // Rest des aktuellen Bezeichners hinter dem Cursor
            var tail = offset;
            while (tail < text.Length && (char.IsLetterOrDigit(text[tail]) || text[tail] == '_')) tail++;
            sb.Append(text, offset, tail - offset);
            if (offset - cursor.Value.Snippet.Length >= 0 && string.CompareOrdinal(text, offset - cursor.Value.Snippet.Length, cursor.Value.Snippet, 0, cursor.Value.Snippet.Length) == 0)
                map.Add((snippetStart, offset - cursor.Value.Snippet.Length, cursor.Value.Snippet.Length + tail - offset));
            sb.Append("\n;");
        }
        for (var k = 0; k < open; k++) sb.Append("}\n");
        sb.Append("\n}\n");

        // @code-Blöcke
        foreach (var (s, e) in codeBlocks)
        {
            if (cursor.Value.Ctx == RazorCSharpContext.CodeBlock && offset >= s && offset <= e)
            {
                pos = sb.Length + (offset - s);
            }
            map.Add((sb.Length, s, e - s));
            sb.Append(text, s, e - s).Append('\n');
        }
        sb.Append("}\n}\n");
        return pos < 0 ? null : new RazorProjection(sb.ToString(), pos, cursor.Value.Ctx, map);
    }

    /// <summary>Position und Text für ein neues @using in der .razor-Datei (nach dem letzten @using bzw. den Kopf-Direktiven).</summary>
    public static (int Offset, string Text) UsingInsertion(string text, string ns)
    {
        var firstMarkup = text.IndexOf('<');
        if (firstMarkup < 0) firstMarkup = text.Length;
        Match? lastUsing = null, lastHead = null;
        foreach (Match m in HeadDirective().Matches(text))
        {
            if (m.Groups[1].Value == "using") lastUsing = m;
            else if (m.Index < firstMarkup) lastHead = m;
        }
        var anchor = lastUsing ?? lastHead;
        if (anchor is null) return (0, "@using " + ns + "\n");
        var end = anchor.Index + anchor.Length;
        var nl = end > 0 && text[end - 1] != '\n' ? "\n" : "";
        return (end, nl + "@using " + ns + "\n");
    }

    private static bool IsCSharpValue(string tag, string attr, string value, Func<string, string, bool>? isCSharpAttribute)
    {
        if (value.StartsWith('@')) return true;
        if (attr.StartsWith('@'))
        {
            if (attr == "@formname") return false;
            var colon = attr.IndexOf(':');
            return colon < 0 || attr[(colon + 1)..] is not ("event" or "format" or "culture");
        }
        return tag.Length > 0 && char.IsUpper(tag[0]) && isCSharpAttribute?.Invoke(tag, attr) == true;
    }

    /// <summary>Liegt das Ende von <paramref name="tagText"/> in einem Attributwert? Liefert (Tag, Attribut, bisheriger Wert).</summary>
    public static (string Tag, string Attr, string Value)? AttributeValueAtEnd(string tagText)
    {
        var tm = TagName().Match(tagText);
        if (!tm.Success) return null;
        var i = tm.Length;
        string? attr = null;
        while (i < tagText.Length)
        {
            var c = tagText[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            var ns = i;
            while (i < tagText.Length && !char.IsWhiteSpace(tagText[i]) && tagText[i] is not '=' and not '>' and not '/') i++;
            attr = tagText[ns..i];
            while (i < tagText.Length && char.IsWhiteSpace(tagText[i])) i++;
            if (i >= tagText.Length || tagText[i] != '=') { if (i < tagText.Length && tagText[i] == '/') i++; continue; }
            i++;
            while (i < tagText.Length && char.IsWhiteSpace(tagText[i])) i++;
            if (i >= tagText.Length) return (tm.Groups[1].Value, attr, "");
            var q = tagText[i];
            if (q is '"' or '\'')
            {
                var close = tagText.IndexOf(q, i + 1);
                if (close < 0) return (tm.Groups[1].Value, attr, tagText[(i + 1)..]);
                i = close + 1;
            }
            else
            {
                var vs = i;
                while (i < tagText.Length && !char.IsWhiteSpace(tagText[i]) && tagText[i] != '>') i++;
                if (i >= tagText.Length) return (tm.Groups[1].Value, attr, tagText[vs..]);
            }
        }
        return null;
    }

    private static int TagEnd(string text, int start)
    {
        char? quote = null;
        for (var i = start + 1; i < text.Length; i++)
        {
            var c = text[i];
            if (quote is not null)
            {
                if (c == '@' && i + 1 < text.Length && CSharpScanner.IsIdentStart(text[i + 1])) { i = Math.Max(i + 1, CSharpScanner.SkipImplicitExpression(text, i + 1)) - 1; continue; }
                if (c == quote) quote = null;
                continue;
            }
            if (c is '"' or '\'') { var prev = text[i - 1]; if (prev == '=' || char.IsWhiteSpace(prev)) quote = c; continue; }
            if (c == '>') return i;
            if (c == '<' && i > start + 1) return i - 1; // kaputtes Tag
        }
        return -1;
    }

    private static string StripMarkup(string s) => MarkupTag().Replace(s, " ");

    private static bool At(string text, int i, string what) => string.CompareOrdinal(text, i, what, 0, what.Length) == 0;

    private static bool LineStart(string text, int i)
    {
        for (var k = i - 1; k >= 0; k--)
        {
            if (text[k] == '\n') return true;
            if (!char.IsWhiteSpace(text[k])) return false;
        }
        return true;
    }

    [GeneratedRegex(@"@(?:code|functions)\s*\{")]
    private static partial Regex CodeDirective();

    [GeneratedRegex(@"@(using|inject|inherits|implements|layout|typeparam|namespace|attribute|rendermode|page|preservewhitespace)\b[ \t]*")]
    private static partial Regex Directive();

    [GeneratedRegex(@"@(if|foreach|for|while|switch|lock|using)\s*\(")]
    private static partial Regex BlockKeyword();

    [GeneratedRegex(@"@[A-Za-z_]\w*(?:(?:\?\.|\.|!)[A-Za-z_]\w*)*\.?")]
    private static partial Regex ImplicitExpr();

    [GeneratedRegex(@"(?:(?:\?\.|\.)[A-Za-z_]\w*)+")]
    private static partial Regex ImplicitTail();

    [GeneratedRegex(@"\G\s*else\b(\s*if\s*\()?")]
    private static partial Regex ElseClause();

    [GeneratedRegex(@"^@(using|page|rendermode|layout|inherits|namespace|inject)\b[^\n]*\n?", RegexOptions.Multiline)]
    private static partial Regex HeadDirective();

    [GeneratedRegex(@"^<([\w.\-:]+)")]
    private static partial Regex TagName();

    [GeneratedRegex(@"</?[A-Za-z][^<>]*>")]
    private static partial Regex MarkupTag();

    /// <summary>Innenbereiche von @code { … } / @functions { … }.</summary>
    public static List<(int Start, int End)> CodeRegions(string text)
    {
        var list = new List<(int, int)>();
        foreach (Match m in CodeDirective().Matches(text))
        {
            var brace = m.Index + m.Length - 1;
            var end = CSharpScanner.SkipBalanced(text, brace, '{', '}');
            if (end < 0) end = text.Length;
            if (list.Any(r => brace < r.Item2)) continue;
            list.Add((brace + 1, Math.Max(brace + 1, end - 1)));
        }
        return list;
    }

    /// <summary>
    /// Partielle Klasse einer .razor-Datei für Navigation, Verwendungen, Umbenennen und Farben:
    /// @code-Blöcke, @inject-Eigenschaften und alle C#-Ausdrücke aus dem Markup (in einer Hilfsmethode).
    /// Liefert Code und Abschnitts-Zuordnung.
    /// </summary>
    public static RazorProjection ClassFor(string text, string className, string ns, IEnumerable<string> usings, bool withBase = true)
    {
        var regions = CodeRegions(text);
        bool InCode(int at) => regions.Any(r => at >= r.Start && at < r.End);
        var allUsings = usings.ToList();
        string? inherits = null, fileNs = null;
        var implements = new List<string>();
        var typeParams = new List<string>();
        var injects = new List<(int Start, int Len)>();
        foreach (Match m in Directive().Matches(text))
        {
            if (!LineStart(text, m.Index) || InCode(m.Index)) continue;
            var vs = m.Index + m.Length;
            var lineEnd = text.IndexOf('\n', vs);
            if (lineEnd < 0) lineEnd = text.Length;
            var raw = text[vs..lineEnd];
            var value = raw.Trim();
            switch (m.Groups[1].Value)
            {
                case "using": allUsings.Add(value); break;
                case "inherits": inherits = value; break;
                case "implements": implements.Add(value); break;
                case "typeparam": typeParams.Add(value.Split(' ', 2)[0]); break;
                case "namespace": fileNs = value; break;
                case "inject": injects.Add((vs + (raw.Length - raw.TrimStart().Length), value.Length)); break;
            }
        }

        var sb = new StringBuilder();
        foreach (var u in allUsings.Where(u => u.Length > 0).Distinct()) sb.Append("using ").Append(u.TrimEnd(';')).Append(";\n");
        // Dateiweiter Namespace: Member stehen wie in @code mit 4 Leerzeichen – Roslyns Formatierer (Quick-Fixes)
        // rückt den Code der .razor-Datei dann nicht um
        sb.Append("namespace ").Append(fileNs ?? ns).Append(";\npublic partial class ").Append(className);
        if (typeParams.Count > 0) sb.Append('<').Append(string.Join(", ", typeParams)).Append('>');
        if (withBase)
        {
            var bases = new List<string> { inherits ?? "global::Microsoft.AspNetCore.Components.ComponentBase" };
            bases.AddRange(implements);
            sb.Append(" : ").Append(string.Join(", ", bases));
        }
        sb.Append("\n{\n");
        var map = new List<(int Gen, int Orig, int Len)>();
        foreach (var (start, len) in injects)
        {
            var value = text.Substring(start, len);
            if (value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length < 2) continue;
            sb.Append("[global::Microsoft.AspNetCore.Components.InjectAttribute] private ");
            map.Add((sb.Length, start, len));
            sb.Append(value).Append(" { get; set; } = default!;\n");
        }
        foreach (var (s, e) in regions)
        {
            map.Add((sb.Length, s, e - s));
            sb.Append(text, s, e - s).Append('\n');
        }
        // C# aus dem Markup – nur für Symbolbezüge, Fehler darin sind egal
        var spans = MarkupSpans(text).Where(x => !InCode(x.Start)).ToList();
        if (spans.Count > 0)
        {
            sb.Append("#pragma warning disable\nprivate void __BsMarkup()\n{\n");
            foreach (var (s, e, kind) in spans)
            {
                switch (kind)
                {
                    case MarkupSpanKind.Block:
                        sb.Append("{\n"); map.Add((sb.Length, s + 1, Math.Max(0, e - s - 2))); sb.Append(text, s + 1, Math.Max(0, e - s - 2)).Append("\n}\n");
                        break;
                    case MarkupSpanKind.Head:
                        map.Add((sb.Length, s, e - s)); sb.Append(text, s, e - s).Append(" { }\n");
                        break;
                    default:
                        sb.Append("_ = "); map.Add((sb.Length, s, e - s)); sb.Append(text, s, e - s).Append(";\n");
                        break;
                }
            }
            sb.Append("}\n#pragma warning restore\n");
        }
        sb.Append("}\n");
        return new RazorProjection(sb.ToString(), 0, RazorCSharpContext.CodeBlock, map);
    }

    public enum MarkupSpanKind { Expression, Block, Head }

    /// <summary>C#-Bereiche im Markup: implizite/explizite Ausdrücke, Blockköpfe (if/foreach …), @{ }, C#-Attributwerte.</summary>
    public static IEnumerable<(int Start, int End, MarkupSpanKind Kind)> MarkupSpans(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '@' || i + 1 >= text.Length) continue;
            if (text[i + 1] == '@') { i++; continue; }
            if (text[i + 1] == '*')
            {
                var close = text.IndexOf("*@", i + 2, StringComparison.Ordinal);
                if (close < 0) yield break;
                i = close + 1;
                continue;
            }
            if (i > 0 && char.IsLetterOrDigit(text[i - 1])) continue; // E-Mail-Adresse
            var c = text[i + 1];
            if (c == '(' || c == '{')
            {
                var e = CSharpScanner.SkipBalanced(text, i + 1, c, c == '(' ? ')' : '}');
                if (e > 0)
                {
                    yield return (i + 1, e, c == '{' ? MarkupSpanKind.Block : MarkupSpanKind.Expression);
                    i = e - 1;
                }
                continue;
            }
            var hm = MarkupBlockHead().Match(text, i);
            if (hm.Success && hm.Index == i)
            {
                var p = text.IndexOf('(', i);
                var e = CSharpScanner.SkipBalanced(text, p, '(', ')');
                if (e > 0) yield return (i + 1, e, MarkupSpanKind.Head);
                continue;
            }
            if (CSharpScanner.IsIdentStart(c))
            {
                var e = CSharpScanner.SkipImplicitExpression(text, i + 1);
                var word = text[(i + 1)..e];
                if (word is "page" or "inject" or "using" or "rendermode" or "layout" or "inherits" or "implements" or "namespace" or "attribute"
                    or "typeparam" or "code" or "functions" or "preservewhitespace" or "else" or "do" or "try" or "section") { i = e - 1; continue; }
                // Attributwert: @onclick="Save", @bind-Value="Model.Name" → ganzen Wert nehmen
                var ne = e;
                while (ne < text.Length && (char.IsLetterOrDigit(text[ne]) || text[ne] is '-' or ':' or '_')) ne++;
                var attr = MarkupAttrValueAfter().Match(text, ne);
                if (attr.Success && attr.Index == ne)
                {
                    var q = attr.Groups[1].Value[0];
                    var vs = ne + attr.Length;
                    var ve = text.IndexOf(q, vs);
                    if (ve > vs)
                    {
                        var v = text[vs..ve];
                        if (v.StartsWith('@')) vs++;
                        if (ve > vs) yield return (vs, ve, MarkupSpanKind.Expression);
                        i = ve;
                        continue;
                    }
                }
                yield return (i + 1, e, MarkupSpanKind.Expression);
                i = e - 1;
            }
        }
    }

    [GeneratedRegex(@"@(if|foreach|for|while|switch|using|lock)\s*\(")]
    private static partial Regex MarkupBlockHead();

    [GeneratedRegex(@"\G\s*=\s*([""'])")]
    private static partial Regex MarkupAttrValueAfter();
}
