namespace BlazorStudio.Core.Razor;

/// <summary>Kleine Hilfen, um in Razor-Text C#-Bereiche (Klammern, Strings, Kommentare) zu überspringen.</summary>
public static class CSharpScanner
{
    /// <summary>
    /// text[start] muss 'open' sein. Liefert den Index direkt NACH der passenden schließenden Klammer
    /// oder -1, wenn keine gefunden wird.
    /// </summary>
    public static int SkipBalanced(string text, int start, char open, char close)
    {
        if (start >= text.Length || text[start] != open) return -1;
        var depth = 0;
        var i = start;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                var nl = text.IndexOf('\n', i);
                i = nl < 0 ? text.Length : nl + 1;
                continue;
            }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 2;
                continue;
            }
            if (c == '@' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("*@", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 2;
                continue;
            }
            if (c == '"' || (c == '@' && i + 1 < text.Length && text[i + 1] == '"') ||
                (c == '$' && i + 1 < text.Length && (text[i + 1] == '"' || text[i + 1] == '@')))
            {
                i = SkipString(text, i);
                continue;
            }
            if (c == '\'')
            {
                var after = TrySkipCharLiteral(text, i);
                if (after > 0)
                {
                    i = after;
                    continue;
                }
            }
            if (c == open) depth++;
            else if (c == close)
            {
                depth--;
                if (depth == 0) return i + 1;
            }
            i++;
        }
        return -1;
    }

    /// <summary>Überspringt einen C#-String ab text[i] (", @", $", $@", @$", """).</summary>
    public static int SkipString(string text, int i)
    {
        var verbatim = false;
        var interpolated = false;
        while (i < text.Length && text[i] != '"')
        {
            if (text[i] == '@') verbatim = true;
            if (text[i] == '$') interpolated = true;
            i++;
        }
        if (i >= text.Length) return text.Length;

        // Raw string literal """ ... """
        if (i + 2 < text.Length && text[i + 1] == '"' && text[i + 2] == '"')
        {
            var q = 0;
            while (i < text.Length && text[i] == '"') { q++; i++; }
            var fence = new string('"', q);
            var end = text.IndexOf(fence, i, StringComparison.Ordinal);
            return end < 0 ? text.Length : end + q;
        }

        i++; // öffnendes "
        var braceDepth = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (interpolated && c == '{')
            {
                if (i + 1 < text.Length && text[i + 1] == '{') { i += 2; continue; }
                braceDepth++;
            }
            else if (interpolated && c == '}' && braceDepth > 0)
            {
                braceDepth--;
            }
            else if (braceDepth > 0 && c == '"')
            {
                i = SkipString(text, i);
                continue;
            }
            else if (!verbatim && c == '\\')
            {
                i += 2;
                continue;
            }
            else if (c == '"')
            {
                if (verbatim && i + 1 < text.Length && text[i + 1] == '"') { i += 2; continue; }
                return i + 1;
            }
            else if (!verbatim && c == '\n')
            {
                return i; // unterminiert, lieber abbrechen
            }
            i++;
        }
        return text.Length;
    }

    /// <summary>Nur echte Zeichenliterale ('a', '\n', 'A'), damit "Don't" im Markup nicht stört.</summary>
    private static int TrySkipCharLiteral(string text, int i)
    {
        if (i + 2 < text.Length && text[i + 1] != '\\' && text[i + 2] == '\'') return i + 3;
        if (i + 3 < text.Length && text[i + 1] == '\\')
        {
            for (var j = i + 3; j < Math.Min(text.Length, i + 10); j++)
                if (text[j] == '\'') return j + 1;
        }
        return -1;
    }

    public static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_';

    public static bool IsIdentPart(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>
    /// Liest einen impliziten Razor-Ausdruck ab text[i] (nach dem @): name.member(args)[idx]?.x ...
    /// Liefert das Ende (exklusiv).
    /// </summary>
    public static int SkipImplicitExpression(string text, int i)
    {
        var start = i;
        while (i < text.Length)
        {
            if (IsIdentStart(text[i]) || (i > start && char.IsDigit(text[i])))
            {
                while (i < text.Length && IsIdentPart(text[i])) i++;
            }
            else break;

            // Generics wie Foo<T> lassen wir weg (mehrdeutig mit Markup).
            while (i < text.Length && (text[i] == '(' || text[i] == '['))
            {
                var end = SkipBalanced(text, i, text[i], text[i] == '(' ? ')' : ']');
                if (end < 0) return i;
                i = end;
            }

            if (i + 1 < text.Length && text[i] == '.' && IsIdentStart(text[i + 1])) { i++; continue; }
            if (i + 2 < text.Length && text[i] == '?' && text[i + 1] == '.' && IsIdentStart(text[i + 2])) { i += 2; continue; }
            if (i + 1 < text.Length && text[i] == '!' && text[i + 1] == '.') { i += 2; continue; }
            break;
        }
        return i;
    }

    public static int SkipWhitespace(string text, int i)
    {
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        return i;
    }
}
