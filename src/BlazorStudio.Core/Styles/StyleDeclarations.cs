using System.Text;

namespace BlazorStudio.Core.Styles;

/// <summary>Geordnete Liste von CSS-Deklarationen (Inhalt eines style-Attributs oder einer Regel).</summary>
public sealed class StyleDeclarations
{
    private readonly List<KeyValuePair<string, string>> _items = new();

    public IReadOnlyList<KeyValuePair<string, string>> Items => _items;

    public int Count => _items.Count;

    public static StyleDeclarations Parse(string? css)
    {
        var s = new StyleDeclarations();
        if (string.IsNullOrWhiteSpace(css)) return s;
        foreach (var part in SplitTopLevel(css, ';'))
        {
            var colon = IndexOfTopLevel(part, ':');
            if (colon <= 0) continue;
            var prop = part[..colon].Trim().ToLowerInvariant();
            var value = part[(colon + 1)..].Trim();
            if (prop.Length == 0 || value.Length == 0) continue;
            s.Set(prop, value);
        }
        return s;
    }

    public string? Get(string property)
    {
        var i = _items.FindIndex(k => k.Key == property);
        return i < 0 ? null : _items[i].Value;
    }

    public void Set(string property, string? value)
    {
        property = property.Trim().ToLowerInvariant();
        var i = _items.FindIndex(k => k.Key == property);
        if (string.IsNullOrWhiteSpace(value))
        {
            if (i >= 0) _items.RemoveAt(i);
            return;
        }
        var kv = new KeyValuePair<string, string>(property, value.Trim());
        if (i >= 0) _items[i] = kv;
        else _items.Add(kv);
    }

    public void Remove(string property) => Set(property, null);

    public bool Has(string property) => Get(property) is not null;

    public override string ToString() => string.Join("; ", _items.Select(k => $"{k.Key}: {k.Value}"));

    /// <summary>Als Block für eine CSS-Regel mit Einrückung.</summary>
    public string ToBlock(string indent = "    ")
    {
        var sb = new StringBuilder();
        foreach (var k in _items) sb.Append(indent).Append(k.Key).Append(": ").Append(k.Value).Append(";\n");
        return sb.ToString();
    }

    internal static List<string> SplitTopLevel(string s, char sep)
    {
        var list = new List<string>();
        var depth = 0;
        char quote = '\0';
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                continue;
            }
            if (c is '"' or '\'') quote = c;
            else if (c == '(') depth++;
            else if (c == ')') depth = Math.Max(0, depth - 1);
            else if (c == sep && depth == 0)
            {
                list.Add(s[start..i]);
                start = i + 1;
            }
        }
        list.Add(s[start..]);
        return list;
    }

    private static int IndexOfTopLevel(string s, char ch)
    {
        var depth = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '(') depth++;
            else if (s[i] == ')') depth--;
            else if (s[i] == ch && depth == 0) return i;
        }
        return -1;
    }
}

/// <summary>Vier Werte (oben, rechts, unten, links) für margin/padding/inset.</summary>
public sealed record BoxValues(string Top, string Right, string Bottom, string Left)
{
    public static BoxValues Empty { get; } = new("", "", "", "");

    /// <summary>Liest die Kurzschreibweise und überschreibt mit Einzelwerten (margin-top ...).</summary>
    public static BoxValues From(StyleDeclarations style, string prefix)
    {
        var b = ParseShorthand(style.Get(prefix));
        return new BoxValues(
            style.Get(prefix + "-top") ?? b.Top,
            style.Get(prefix + "-right") ?? b.Right,
            style.Get(prefix + "-bottom") ?? b.Bottom,
            style.Get(prefix + "-left") ?? b.Left);
    }

    public static BoxValues ParseShorthand(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Empty;
        var p = StyleDeclarations.SplitTopLevel(value.Trim(), ' ').Where(x => x.Length > 0).ToArray();
        return p.Length switch
        {
            1 => new BoxValues(p[0], p[0], p[0], p[0]),
            2 => new BoxValues(p[0], p[1], p[0], p[1]),
            3 => new BoxValues(p[0], p[1], p[2], p[1]),
            >= 4 => new BoxValues(p[0], p[1], p[2], p[3]),
            _ => Empty,
        };
    }

    /// <summary>Schreibt die Werte als möglichst kurze Kurzschreibweise zurück.</summary>
    public void ApplyTo(StyleDeclarations style, string prefix)
    {
        foreach (var side in new[] { "-top", "-right", "-bottom", "-left" }) style.Remove(prefix + side);
        var vals = new[] { Top, Right, Bottom, Left }.Select(v => string.IsNullOrWhiteSpace(v) ? "" : v.Trim()).ToArray();
        if (vals.All(v => v.Length == 0))
        {
            style.Remove(prefix);
            return;
        }
        for (var i = 0; i < 4; i++) if (vals[i].Length == 0) vals[i] = "0";
        var (t, r, b, l) = (vals[0], vals[1], vals[2], vals[3]);
        string shorthand;
        if (t == r && r == b && b == l) shorthand = t;
        else if (t == b && r == l) shorthand = $"{t} {r}";
        else if (r == l) shorthand = $"{t} {r} {b}";
        else shorthand = $"{t} {r} {b} {l}";
        style.Set(prefix, shorthand);
    }

    public BoxValues With(int side, string value) => side switch
    {
        0 => this with { Top = value },
        1 => this with { Right = value },
        2 => this with { Bottom = value },
        _ => this with { Left = value },
    };

    public string this[int side] => side switch { 0 => Top, 1 => Right, 2 => Bottom, _ => Left };
}

public static class CssValue
{
    /// <summary>Hängt "px" an reine Zahlen an (außer 0, Gewichte, line-height ...).</summary>
    public static string? WithUnit(string? value, string property)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        if (UnitlessProperties.Contains(property)) return v;
        if (double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
            return d == 0 ? "0" : v + "px";
        return v;
    }

    /// <summary>Zerlegt "16px" in (16, "px").</summary>
    public static (double? Number, string Unit) Split(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return (null, "");
        var v = value.Trim();
        var i = 0;
        while (i < v.Length && (char.IsDigit(v[i]) || v[i] is '.' or '-' or '+')) i++;
        if (i == 0) return (null, v);
        return double.TryParse(v[..i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)
            ? (d, v[i..])
            : (null, v);
    }

    public static bool IsHexColor(string? v) =>
        v is not null && v.Length is 4 or 7 && v[0] == '#' && v.Skip(1).All(Uri.IsHexDigit);

    /// <summary>Für &lt;input type=color&gt;: #rgb → #rrggbb, sonst null.</summary>
    public static string? ToInputColor(string? v)
    {
        if (!IsHexColor(v)) return null;
        if (v!.Length == 7) return v.ToLowerInvariant();
        return ("#" + new string(v.Skip(1).SelectMany(c => new[] { c, c }).ToArray())).ToLowerInvariant();
    }

    private static readonly HashSet<string> UnitlessProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "font-weight", "line-height", "opacity", "z-index", "flex-grow", "flex-shrink", "order", "flex",
        "grid-column", "grid-row", "aspect-ratio", "scale", "zoom",
    };
}
