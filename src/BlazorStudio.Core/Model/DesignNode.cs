namespace BlazorStudio.Core.Model;

public enum NodeKind
{
    /// <summary>Normales HTML-Element (div, button, ...).</summary>
    Element,
    /// <summary>Razor-Komponente (großgeschrieben, z. B. NavLink, Counter).</summary>
    Component,
    /// <summary>Reiner Text.</summary>
    Text,
    /// <summary>Razor-Ausdruck wie @name oder @(a + b).</summary>
    Expression,
    /// <summary>HTML- oder Razor-Kommentar.</summary>
    Comment,
    /// <summary>Strukturierter Kontrollblock (@if, @foreach, ...) mit Markup im Rumpf.</summary>
    Control,
    /// <summary>Undurchsichtiger C#-Block, wird unverändert übernommen.</summary>
    Code,
}

public sealed class NodeAttribute
{
    public NodeAttribute(string name, string? value, char quote = '"')
    {
        Name = name;
        Value = value;
        Quote = quote;
    }

    public string Name { get; set; }

    /// <summary>null = Attribut ohne Wert (z. B. disabled).</summary>
    public string? Value { get; set; }

    public char Quote { get; set; }

    public NodeAttribute Clone() => new(Name, Value, Quote);
}

public sealed class DesignNode
{
    private static long _counter;

    public static string NewId() => "n" + Interlocked.Increment(ref _counter).ToString("x");

    public string Id { get; private set; } = NewId();

    public NodeKind Kind { get; set; }

    /// <summary>Tag- bzw. Komponentenname. Bei Control der Schlüssel (if, foreach, ...).</summary>
    public string Name { get; set; } = "";

    /// <summary>Text, Ausdruck, Kommentar, roher Code oder (bei Control) der Kopf, z. B. "foreach (var x in xs)".</summary>
    public string Text { get; set; } = "";

    /// <summary>Bei Control: true für else / else if / catch / finally.</summary>
    public bool IsContinuation { get; set; }

    /// <summary>Inhalt von script/style wird nicht geparst.</summary>
    public bool IsRawContent { get; set; }

    /// <summary>War im Quelltext als &lt;x /&gt; geschrieben.</summary>
    public bool SelfClosingInSource { get; set; }

    public List<NodeAttribute> Attributes { get; } = new();

    public List<DesignNode> Children { get; } = new();

    public DesignNode? Parent { get; set; }

    public int SourceStart { get; set; } = -1;

    public int SourceEnd { get; set; } = -1;

    public static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr",
    };

    public bool IsVoid => Kind == NodeKind.Element && VoidElements.Contains(Name);

    public bool IsTagLike => Kind is NodeKind.Element or NodeKind.Component;

    /// <summary>Kann dieser Knoten Kinder aufnehmen (für Drag &amp; Drop)?</summary>
    public bool CanHaveChildren => Kind switch
    {
        NodeKind.Element => !IsVoid && !IsRawContent,
        NodeKind.Component => true,
        NodeKind.Control => true,
        _ => false,
    };

    public static DesignNode CreateRoot() => new() { Kind = NodeKind.Element, Name = "#root" };

    public bool IsRoot => Name == "#root";

    public static DesignNode Element(string name) =>
        new() { Kind = IsComponentName(name) ? NodeKind.Component : NodeKind.Element, Name = name };

    public static DesignNode TextNode(string text) => new() { Kind = NodeKind.Text, Text = text };

    public static bool IsComponentName(string name) =>
        name.Length > 0 && (char.IsUpper(name[0]) || name.Contains('.'));

    // ---------- Attribute ----------

    public NodeAttribute? FindAttribute(string name) =>
        Attributes.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    public string? GetAttribute(string name) => FindAttribute(name)?.Value;

    public bool HasAttribute(string name) => FindAttribute(name) is not null;

    /// <summary>Setzt ein Attribut. value == null und removeIfNull entfernt es.</summary>
    public void SetAttribute(string name, string? value, bool removeIfNull = true)
    {
        var existing = FindAttribute(name);
        if (value is null && removeIfNull)
        {
            if (existing is not null) Attributes.Remove(existing);
            return;
        }

        if (existing is null)
            Attributes.Add(new NodeAttribute(name, value));
        else
            existing.Value = value;
    }

    public void RemoveAttribute(string name) => SetAttribute(name, null);

    public void RenameAttribute(string oldName, string newName)
    {
        var a = FindAttribute(oldName);
        if (a is not null) a.Name = newName;
    }

    // ---------- Klassen & Styles ----------

    public IReadOnlyList<string> Classes =>
        (GetAttribute("class") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public void SetClasses(IEnumerable<string> classes)
    {
        var joined = string.Join(' ', classes.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct());
        SetAttribute("class", joined.Length == 0 ? null : joined);
    }

    public void AddClass(string cls) => SetClasses(Classes.Append(cls.Trim()));

    public void RemoveClass(string cls) => SetClasses(Classes.Where(c => c != cls));

    public Styles.StyleDeclarations GetStyle() => Styles.StyleDeclarations.Parse(GetAttribute("style"));

    public void SetStyle(Styles.StyleDeclarations style)
    {
        var s = style.ToString();
        SetAttribute("style", s.Length == 0 ? null : s);
    }

    public void SetStyleProperty(string property, string? value)
    {
        var style = GetStyle();
        style.Set(property, value);
        SetStyle(style);
    }

    // ---------- Baum ----------

    public void AddChild(DesignNode child, int index = -1)
    {
        child.Parent = this;
        if (index < 0 || index > Children.Count) Children.Add(child);
        else Children.Insert(index, child);
    }

    public void Detach()
    {
        Parent?.Children.Remove(this);
        Parent = null;
    }

    public int IndexInParent => Parent?.Children.IndexOf(this) ?? -1;

    public IEnumerable<DesignNode> Descendants()
    {
        foreach (var c in Children)
        {
            yield return c;
            foreach (var d in c.Descendants()) yield return d;
        }
    }

    public IEnumerable<DesignNode> Ancestors()
    {
        var p = Parent;
        while (p is not null)
        {
            yield return p;
            p = p.Parent;
        }
    }

    public bool IsAncestorOf(DesignNode other) => other.Ancestors().Contains(this);

    public DesignNode? Find(string id)
    {
        if (Id == id) return this;
        foreach (var c in Children)
        {
            var f = c.Find(id);
            if (f is not null) return f;
        }
        return null;
    }

    /// <summary>Kopie. keepIds=false vergibt neue Ids (z. B. beim Duplizieren).</summary>
    public DesignNode Clone(bool keepIds = false)
    {
        var n = new DesignNode
        {
            Kind = Kind,
            Name = Name,
            Text = Text,
            IsContinuation = IsContinuation,
            IsRawContent = IsRawContent,
        };
        if (keepIds) n.Id = Id;
        foreach (var a in Attributes) n.Attributes.Add(a.Clone());
        foreach (var c in Children) n.AddChild(c.Clone(keepIds));
        return n;
    }

    /// <summary>Hat nur Text/Ausdrucks-Kinder (für Inline-Bearbeitung im Designer).</summary>
    public bool IsTextOnly =>
        IsTagLike && Children.Count > 0 && Children.All(c => c.Kind is NodeKind.Text);

    public string InnerText => string.Concat(Children.Where(c => c.Kind is NodeKind.Text or NodeKind.Expression).Select(c => c.Text));

    /// <summary>Kurzer Anzeigename für Baum und Auswahl.</summary>
    public string DisplayName => Kind switch
    {
        NodeKind.Text => "Text",
        NodeKind.Expression => Text,
        NodeKind.Comment => "Kommentar",
        NodeKind.Code => "@{ Code }",
        NodeKind.Control => "@" + Text,
        _ => Name,
    };

    public override string ToString() => DisplayName;
}
