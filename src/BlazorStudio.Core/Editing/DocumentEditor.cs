using BlazorStudio.Core.Catalog;
using BlazorStudio.Core.Model;
using BlazorStudio.Core.Razor;
using BlazorStudio.Core.Styles;

namespace BlazorStudio.Core.Editing;

public enum DropPosition { Before, After, Inside }

/// <summary>Strukturelle Änderungen am Dokumentbaum (Einfügen, Verschieben, Löschen ...).</summary>
public static class DocumentEditor
{
    /// <summary>Fügt die Vorlage einer Toolbox-Definition ein. Liefert den ersten neuen Knoten.</summary>
    public static DesignNode? InsertTemplate(RazorDocument doc, string template, DesignNode? target, DropPosition position)
    {
        var nodes = RazorParser.ParseFragment(template);
        if (nodes.Count == 0) return null;
        Insert(doc, nodes, target, position);
        return nodes[0];
    }

    public static void Insert(RazorDocument doc, IReadOnlyList<DesignNode> nodes, DesignNode? target, DropPosition position)
    {
        var (parent, index) = ResolveInsertPoint(doc, target, position);
        foreach (var n in nodes)
        {
            n.Detach();
            parent.AddChild(n, index);
            index++;
        }
    }

    public static (DesignNode Parent, int Index) ResolveInsertPoint(RazorDocument doc, DesignNode? target, DropPosition position)
    {
        if (target is null || target.IsRoot) return (doc.Root, doc.Root.Children.Count);
        if (position == DropPosition.Inside && target.CanHaveChildren) return (target, target.Children.Count);
        var parent = target.Parent ?? doc.Root;
        var idx = parent.Children.IndexOf(target);
        if (position == DropPosition.After)
        {
            idx++;
            // nicht zwischen if und else einfügen
            while (idx < parent.Children.Count && parent.Children[idx].IsContinuation) idx++;
        }
        return (parent, idx);
    }

    public static bool Move(RazorDocument doc, DesignNode node, DesignNode? target, DropPosition position)
    {
        if (node.IsRoot) return false;
        if (target is not null && (target == node || node.IsAncestorOf(target))) return false;

        // Ketten (if/else) gemeinsam verschieben
        var group = ChainOf(node);
        if (target is not null && group.Contains(target)) return false;
        foreach (var n in group) n.Detach();
        Insert(doc, group, target, position);
        return true;
    }

    /// <summary>Ein @if mitsamt seinen else-Zweigen.</summary>
    public static List<DesignNode> ChainOf(DesignNode node)
    {
        var list = new List<DesignNode> { node };
        if (node.Kind != NodeKind.Control || node.IsContinuation || node.Parent is null) return list;
        var siblings = node.Parent.Children;
        for (var i = siblings.IndexOf(node) + 1; i < siblings.Count && siblings[i].IsContinuation; i++)
            list.Add(siblings[i]);
        return list;
    }

    public static DesignNode? Delete(DesignNode node)
    {
        if (node.IsRoot) return null;
        var parent = node.Parent;
        var idx = node.IndexInParent;
        foreach (var n in ChainOf(node)) n.Detach();
        if (parent is null) return null;
        if (parent.Children.Count == 0) return parent.IsRoot ? null : parent;
        return parent.Children[Math.Min(idx, parent.Children.Count - 1)];
    }

    public static DesignNode? Duplicate(DesignNode node)
    {
        if (node.IsRoot || node.Parent is null) return null;
        var chain = ChainOf(node);
        var copies = chain.Select(n => n.Clone()).ToList();
        var parent = node.Parent;
        var idx = parent.Children.IndexOf(chain[^1]) + 1;
        foreach (var c in copies) parent.AddChild(c, idx++);
        return copies[0];
    }

    /// <summary>Umschließt den Knoten mit einem neuen Element (z. B. div).</summary>
    public static DesignNode Wrap(DesignNode node, string tag, string? style = null)
    {
        var parent = node.Parent!;
        var idx = parent.Children.IndexOf(node);
        var wrapper = DesignNode.Element(tag);
        if (style is not null) wrapper.SetAttribute("style", style);
        foreach (var n in ChainOf(node)) n.Detach();
        parent.AddChild(wrapper, idx);
        wrapper.AddChild(node);
        return wrapper;
    }

    /// <summary>Entfernt das Element, behält aber die Kinder an seiner Stelle.</summary>
    public static void Unwrap(DesignNode node)
    {
        if (node.Parent is null || node.IsRoot) return;
        var parent = node.Parent;
        var idx = parent.Children.IndexOf(node);
        node.Detach();
        foreach (var c in node.Children.ToList())
        {
            c.Detach();
            parent.AddChild(c, idx++);
        }
    }

    /// <summary>Verschiebt um eine Position unter den Geschwistern.</summary>
    public static bool Nudge(DesignNode node, int delta)
    {
        var parent = node.Parent;
        if (parent is null) return false;
        var idx = parent.Children.IndexOf(node);
        var newIdx = idx + delta;
        if (newIdx < 0 || newIdx >= parent.Children.Count) return false;
        parent.Children.RemoveAt(idx);
        parent.Children.Insert(newIdx, node);
        return true;
    }

    /// <summary>Ersetzt den Textinhalt eines Elements (nur wenn es keine Element-Kinder hat).</summary>
    public static void SetText(DesignNode node, string text)
    {
        foreach (var c in node.Children.Where(c => c.Kind is NodeKind.Text).ToList()) c.Detach();
        if (node.Children.Count == 0 || node.Children.All(c => c.Kind == NodeKind.Expression))
        {
            if (text.Length > 0) node.AddChild(DesignNode.TextNode(text), 0);
        }
    }

    /// <summary>Wert eines Komponenten-Parameters als Literal setzen (string → "x", bool → true, enum → Typ.Wert).</summary>
    public static void SetParameterLiteral(DesignNode node, ParameterDefinition p, string? value)
    {
        node.RemoveAttribute("@bind-" + p.Name);
        if (string.IsNullOrEmpty(value))
        {
            node.RemoveAttribute(p.Name);
            return;
        }
        node.SetAttribute(p.Name, value);
    }

    /// <summary>Parameter an einen Ausdruck binden. twoWay → @bind-Name.</summary>
    public static void BindParameter(DesignNode node, string name, string expression, bool twoWay, bool isStringParameter)
    {
        node.RemoveAttribute(name);
        node.RemoveAttribute("@bind-" + name);
        if (string.IsNullOrWhiteSpace(expression)) return;
        if (twoWay)
            node.SetAttribute("@bind-" + name, expression);
        else
            node.SetAttribute(name, "@" + Unwrap(expression));
    }

    private static string Unwrap(string e)
    {
        e = e.Trim().TrimStart('@');
        return e.Contains(' ') && !e.StartsWith('(') ? "(" + e + ")" : e;
    }

    /// <summary>Ist der Attributwert ein Binding (an Member) statt eines Literals?</summary>
    public static bool IsBoundValue(string? value, ParameterDefinition? p)
    {
        if (value is null) return false;
        var v = value.Trim();
        if (v.StartsWith('@')) return true;
        if (p is null) return false;
        return p.Editor switch
        {
            ValueEditor.Bool => v is not ("true" or "false"),
            ValueEditor.Number => !double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _),
            ValueEditor.Enum => p.EnumPrefix is not null && !v.StartsWith(p.EnumPrefix, StringComparison.Ordinal),
            ValueEditor.Expression => true,
            _ => false,
        };
    }

    /// <summary>Inline-Styles in eine CSS-Klasse auslagern. Liefert den CSS-Regeltext.</summary>
    public static string ExtractStyleToClass(DesignNode node, string className)
    {
        var style = node.GetStyle();
        var rule = "." + className + " {\n" + style.ToBlock() + "}\n";
        node.RemoveAttribute("style");
        node.AddClass(className);
        return rule;
    }

    /// <summary>Pfad (Indizes) eines Knotens, um Auswahl nach erneutem Parsen wiederherzustellen.</summary>
    public static List<int> PathOf(DesignNode node)
    {
        var path = new List<int>();
        var n = node;
        while (n.Parent is not null)
        {
            path.Insert(0, n.Parent.Children.IndexOf(n));
            n = n.Parent;
        }
        return path;
    }

    public static DesignNode? FromPath(RazorDocument doc, IReadOnlyList<int>? path)
    {
        if (path is null) return null;
        var n = doc.Root;
        foreach (var i in path)
        {
            if (i < 0 || i >= n.Children.Count) return n.IsRoot ? null : n;
            n = n.Children[i];
        }
        return n.IsRoot ? null : n;
    }

    /// <summary>Tiefster Knoten, dessen Quelltextbereich die Position enthält.</summary>
    public static DesignNode? NodeAt(RazorDocument doc, IReadOnlyDictionary<string, (int Start, int End)> spans, int offset)
    {
        DesignNode? best = null;
        var bestLen = int.MaxValue;
        foreach (var n in doc.AllNodes())
        {
            if (!spans.TryGetValue(n.Id, out var s)) continue;
            if (offset < s.Start || offset > s.End) continue;
            var len = s.End - s.Start;
            if (len < bestLen && n.Kind != NodeKind.Text)
            {
                best = n;
                bestLen = len;
            }
        }
        return best;
    }

    /// <summary>Spans aus den Quellpositionen des Parsers.</summary>
    public static Dictionary<string, (int, int)> SpansFromSource(RazorDocument doc)
    {
        var d = new Dictionary<string, (int, int)>();
        foreach (var n in doc.AllNodes())
            if (n.SourceStart >= 0 && n.SourceEnd >= n.SourceStart) d[n.Id] = (n.SourceStart, n.SourceEnd);
        return d;
    }

    public static string SuggestClassName(DesignNode node)
    {
        var baseName = node.Kind == NodeKind.Component ? node.Name.ToLowerInvariant() : node.Name switch
        {
            "div" => "box",
            "section" => "section",
            "button" => "button",
            "a" => "link",
            "p" => "text",
            _ => node.Name.ToLowerInvariant(),
        };
        return baseName + "-style";
    }

    public static StyleDeclarations StyleOf(DesignNode node) => node.GetStyle();
}
