using System.Text.RegularExpressions;
using BlazorStudio.Core.Catalog;
using BlazorStudio.Core.Editing;
using BlazorStudio.Core.Model;
using BlazorStudio.Core.Project;
using BlazorStudio.Core.Razor;

namespace BlazorStudio.App.Services;

public sealed record ContextMenuRequest(string NodeId, double X, double Y);

/// <summary>Alle Bearbeitungen im Designer – gemeinsam für Toolbox, Ebenen, Canvas, Menüs und Tastenkürzel.</summary>
public sealed partial class DesignActions
{
    private readonly Workspace _ws;
    private readonly UiService _ui;
    private readonly Localizer _l;
    private string? _clipboard;

    public DesignActions(Workspace ws, UiService ui, Localizer l)
    {
        _ws = ws;
        _ui = ui;
        _l = l;
    }

    public event Action<ContextMenuRequest?>? ContextMenu;

    private DocumentSession? Session => _ws.Active is { Kind: DocumentKind.Razor } s ? s : null;

    public bool CanEdit => Session is { HasErrors: false, Doc: not null };

    private bool Guard()
    {
        if (Session is null) return false;
        if (Session.HasErrors)
        {
            _ui.ShowToast(_l["designer.readonlyToast"], ToastKind.Warning);
            return false;
        }
        return true;
    }

    public void RequestContextMenu(string id, double x, double y)
    {
        Session?.Select(id);
        ContextMenu?.Invoke(new ContextMenuRequest(id, x, y));
    }

    public void CloseContextMenu() => ContextMenu?.Invoke(null);

    // ------------------------------------------------------------------

    /// <summary>Text der Toolbox-Vorlage in der aktuellen Sprache.</summary>
    public string LocalizeTemplate(string template) =>
        TemplateTextRegex().Replace(template, m => _l.TemplateText(m.Value));

    public DesignNode? Insert(string key, string? targetId, DropPosition pos) => Insert(key, targetId, pos, null);

    /// <param name="slot">RenderFragment-Parameter der Zielkomponente (TitleContent …); null = ChildContent.</param>
    public DesignNode? Insert(string key, string? targetId, DropPosition pos, string? slot)
    {
        if (!Guard()) return null;
        var def = _ws.AllComponents.FirstOrDefault(c => c.Key == key) ?? BuiltInCatalog.Find(key);
        if (def is null) return null;
        var s = Session!;
        var nodes = RazorParser.ParseFragment(LocalizeTemplate(def.Template));
        if (nodes.Count == 0) return null;
        // Fehlende @using (auch für Komponenten in der Vorlage, z. B. MudSelectItem) im selben Rückgängig-Schritt
        var usings = MissingUsings(nodes);
        s.Apply(d =>
        {
            InsertNodes(d, nodes, d.Find(targetId), pos, slot);
            AddUsings(d, usings);
        });
        AnnounceUsings(usings);
        s.SelectNode(nodes[0]);
        return nodes[0];
    }

    /// <summary>Klick in der Toolbox: hinter die Auswahl bzw. in einen leeren Container einfügen.</summary>
    public void InsertAtSelection(string key)
    {
        var s = Session;
        if (s?.Doc is null) return;
        var sel = s.Selected;
        if (sel is null) { Insert(key, null, DropPosition.Inside); return; }
        var inside = sel.CanHaveChildren && (sel.Children.Count == 0 || sel.Kind == NodeKind.Control) && !sel.IsTextOnly;
        Insert(key, sel.Id, inside ? DropPosition.Inside : DropPosition.After);
    }

    public void Move(string nodeId, string? targetId, DropPosition pos, string? slot = null)
    {
        if (!Guard()) return;
        var s = Session!;
        s.Apply(d =>
        {
            var node = d.Find(nodeId);
            var target = d.Find(targetId);
            if (node is null || node.IsRoot) return;
            if (target is not null && (target == node || node.IsAncestorOf(target))) return;
            var group = DocumentEditor.ChainOf(node);
            if (target is not null && group.Contains(target)) return;
            var oldOwner = SlotOwner(node.Parent);
            foreach (var n in group) n.Detach();
            InsertNodes(d, group, target, pos, slot);
            if (oldOwner is not null && FragmentsFor(oldOwner) is { } frags) SlotEditor.Tidy(oldOwner, frags);
        });
        s.Select(nodeId);
    }

    /// <summary>position: "before", "after", "inside" oder "inside:TitleContent" (Slot einer Komponente).</summary>
    public void Drop(string kind, string payload, string? targetId, string position)
    {
        string? slot = null;
        if (position.StartsWith("inside:", StringComparison.Ordinal)) { slot = position[7..]; position = "inside"; }
        var pos = position switch { "before" => DropPosition.Before, "after" => DropPosition.After, _ => DropPosition.Inside };
        if (kind == "component") Insert(payload, targetId, pos, slot);
        else if (kind == "node") Move(payload, targetId, pos, slot);
    }

    /// <summary>RenderFragment-Parameter einer Komponente; leer, wenn unbekannt.</summary>
    private IReadOnlyList<string>? FragmentsFor(DesignNode component) =>
        component.Kind == NodeKind.Component ? _ws.FragmentsOf(component.Name) : null;

    /// <summary>Ist <paramref name="node"/> ein Parameter-Element (&lt;TitleContent&gt;), die Komponente, zu der es gehört.</summary>
    private DesignNode? SlotOwner(DesignNode? node) =>
        node is { Kind: NodeKind.Component, Parent: { Kind: NodeKind.Component } owner } && FragmentsFor(owner) is { } f && f.Contains(node.Name) ? owner : null;

    /// <summary>Einfügen mit Rücksicht auf Slots: in Komponenten mit RenderFragments landet Inhalt im richtigen Parameter.</summary>
    private void InsertNodes(RazorDocument d, IReadOnlyList<DesignNode> nodes, DesignNode? target, DropPosition pos, string? slot)
    {
        // Vor/hinter einem Parameter-Element geht nicht – stattdessen in den ChildContent der Komponente
        if (target is not null && pos != DropPosition.Inside && SlotOwner(target) is { } owner)
        {
            target = owner;
            pos = DropPosition.Inside;
            slot ??= SlotEditor.ChildContent;
        }
        if (target is not null && pos == DropPosition.Inside && FragmentsFor(target) is { Count: > 0 } frags
            && (slot is not null || SlotEditor.IsExplicit(target, frags)))
        {
            SlotEditor.Insert(target, slot, frags, nodes);
            return;
        }
        DocumentEditor.Insert(d, nodes, target, pos);
    }

    public void Delete()
    {
        if (!Guard() || Session!.Selected is not { } sel) return;
        DesignNode? next = null;
        Session.Apply(_ => next = DocumentEditor.Delete(sel));
        Session.SelectNode(next);
    }

    public void Duplicate()
    {
        if (!Guard() || Session!.Selected is not { } sel) return;
        DesignNode? copy = null;
        Session.Apply(_ => copy = DocumentEditor.Duplicate(sel));
        Session.SelectNode(copy);
    }

    public string? Copy()
    {
        if (Session?.Selected is not { } sel) return null;
        _clipboard = RazorWriter.WriteNodes(DocumentEditor.ChainOf(sel));
        _ui.ShowToast(_l["toast.copied"], ToastKind.Success, 1600);
        return _clipboard;
    }

    public string? Cut()
    {
        var text = Copy();
        if (text is not null) Delete();
        return text;
    }

    public void Paste(string? external)
    {
        if (!Guard()) return;
        var markup = !string.IsNullOrWhiteSpace(external) && external.TrimStart().StartsWith('<') ? external : _clipboard;
        if (string.IsNullOrWhiteSpace(markup)) return;
        var s = Session!;
        var sel = s.Selected;
        var nodes = RazorParser.ParseFragment(markup);
        if (nodes.Count == 0) return;
        // Aus einer anderen Datei kopierte Komponenten brauchen evtl. deren @using
        var usings = MissingUsings(nodes);
        s.Apply(d =>
        {
            var inside = sel is not null && sel.CanHaveChildren && sel.Children.Count == 0;
            DocumentEditor.Insert(d, nodes, sel, sel is null || inside ? DropPosition.Inside : DropPosition.After);
            AddUsings(d, usings);
        });
        AnnounceUsings(usings);
        s.SelectNode(nodes[0]);
    }

    public void SelectParent()
    {
        var s = Session;
        if (s?.Selected is not { } sel) return;
        s.SelectNode(sel.Parent is { IsRoot: false } p ? p : null);
    }

    public void SelectSibling(int delta)
    {
        var s = Session;
        if (s?.Selected is not { Parent: { } parent } sel) return;
        var i = parent.Children.IndexOf(sel) + delta;
        if (i >= 0 && i < parent.Children.Count) s.SelectNode(parent.Children[i]);
    }

    public void SelectFirstChild()
    {
        var s = Session;
        if (s?.Selected is { Children.Count: > 0 } sel) s.SelectNode(sel.Children.FirstOrDefault(c => c.Kind != NodeKind.Text) ?? sel.Children[0]);
    }

    public void Nudge(int delta)
    {
        if (!Guard() || Session!.Selected is not { } sel) return;
        var id = sel.Id;
        Session.Apply(_ => DocumentEditor.Nudge(sel, delta));
        Session.Select(id);
    }

    public void Wrap(string tag = "div", string? style = null)
    {
        if (!Guard() || Session!.Selected is not { } sel) return;
        DesignNode? wrapper = null;
        Session.Apply(_ => wrapper = DocumentEditor.Wrap(sel, tag, style));
        Session.SelectNode(wrapper);
    }

    public void Unwrap()
    {
        if (!Guard() || Session!.Selected is not { } sel || !sel.IsTagLike) return;
        var first = sel.Children.FirstOrDefault();
        Session.Apply(_ => DocumentEditor.Unwrap(sel));
        Session.SelectNode(first);
    }

    public void SetText(string nodeId, string text)
    {
        if (!Guard()) return;
        Session!.Apply(d =>
        {
            var n = d.Find(nodeId);
            if (n is not null) DocumentEditor.SetText(n, text.Replace("\r", "").Replace('\n', ' '));
        }, "text:" + nodeId);
    }

    /// <summary>Attribut setzen (null entfernt). mergeKey bündelt Tippen in einem Feld.</summary>
    public void SetAttribute(DesignNode node, string name, string? value, bool minimized = false)
    {
        if (!Guard()) return;
        Session!.Apply(_ =>
        {
            if (minimized) node.SetAttribute(name, null, removeIfNull: value is null);
            else node.SetAttribute(name, string.IsNullOrEmpty(value) ? null : value);
        }, "attr:" + node.Id + ":" + name);
    }

    public void Mutate(DesignNode node, Action<DesignNode> change, string? mergeKey = null)
    {
        if (!Guard()) return;
        Session!.Apply(_ => change(node), mergeKey);
    }

    /// <summary>Neue Event-Methode anlegen und als Handler setzen.</summary>
    public string? CreateHandler(DesignNode node, string attributeName, string? argsType, string? suggestedName = null)
    {
        if (!Guard()) return null;
        var s = Session!;
        var baseName = suggestedName ?? HandlerName(node, attributeName);
        var name = _ws.UniqueMemberName(s, baseName);
        s.Apply(_ => node.SetAttribute(attributeName, name));
        var where = _ws.AddMethod(s, CodeInspector.CreateHandlerStub(name, argsType, async: false));
        _ui.ShowToast(_l.F("toast.methodCreated", name, where), ToastKind.Success, 2600);
        return name;
    }

    public string? CreateField(DesignNode node, string type, string baseName, string attributeName)
    {
        if (!Guard()) return null;
        var s = Session!;
        var name = _ws.UniqueMemberName(s, baseName);
        s.Apply(_ => node.SetAttribute(attributeName, name));
        var where = _ws.AddField(s, CodeInspector.CreateFieldStub(type, name));
        _ui.ShowToast(_l.F("toast.fieldCreated", name, where), ToastKind.Success, 2600);
        return name;
    }

    private static string HandlerName(DesignNode node, string attributeName)
    {
        var ev = attributeName.TrimStart('@');
        if (ev.StartsWith("on", StringComparison.Ordinal) && ev.Length > 2) ev = char.ToUpperInvariant(ev[2]) + ev[3..];
        if (ev.StartsWith("On", StringComparison.Ordinal) && ev.Length > 2) ev = ev[2..];
        var who = node.Kind == NodeKind.Component ? node.Name : char.ToUpperInvariant(node.Name[0]) + node.Name[1..];
        var id = node.GetAttribute("id");
        if (!string.IsNullOrWhiteSpace(id) && !id.Contains('@'))
            who = string.Concat(id.Split('-', '_', ' ').Where(p => p.Length > 0).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
        return "On" + who + ev;
    }

    /// <summary>Für Projekt-Komponenten aus anderen Ordnern ggf. @using ergänzen (best effort).</summary>
    // ------------------------------------------------------------------
    // Automatische @using-Einträge (wie Rider)
    // ------------------------------------------------------------------

    /// <summary>Namespaces, die für die Komponenten in diesen Knoten (samt Kindern) in der aktiven Datei fehlen.</summary>
    private List<string> MissingUsings(IEnumerable<DesignNode> nodes)
    {
        if (Session is not { } s) return new();
        var tags = nodes.SelectMany(n => n.Descendants().Prepend(n)).Where(n => n.Kind == NodeKind.Component).Select(n => n.Name);
        try
        {
            return _ws.MissingUsingsFor(s, tags);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new();
        }
    }

    /// <summary>@using hinter die letzten vorhandenen (bzw. hinter @page/@layout …) einfügen.</summary>
    public static void AddUsings(RazorDocument d, IReadOnlyList<string> namespaces)
    {
        foreach (var ns in namespaces)
        {
            var line = "@using " + ns;
            if (d.Directives.Any(x => x.Trim().TrimEnd(';') == line)) continue;
            var idx = d.Directives.FindLastIndex(x => x.TrimStart().StartsWith("@using", StringComparison.Ordinal));
            if (idx < 0) idx = d.Directives.FindLastIndex(x => x.TrimStart() is var t && (t.StartsWith("@page", StringComparison.Ordinal) || t.StartsWith("@rendermode", StringComparison.Ordinal)));
            d.Directives.Insert(idx + 1, line);
        }
    }

    private void AnnounceUsings(IReadOnlyList<string> namespaces)
    {
        if (namespaces.Count > 0) _ui.ShowToast(_l.F("designer.usingAdded", string.Join(", ", namespaces)), ToastKind.Info, 2600);
    }

    [GeneratedRegex(@"(?<=>)(Überschrift|Titel|Beschreibung|Text|Link|Label|Button|Option 1|Option 2|Option|Eintrag 1|Eintrag 2|Zitat|Senden|Sichtbar|Bitte wählen|Angemeldet|Nicht angemeldet|Fehler|Willkommen|Los geht's|Mehr erfahren|Karte 1|Karte 2|Karte 3|Marke|Start|Über uns|Kontakt|Name|E-Mail|Nachricht|Impressum · Datenschutz|© 2026 Firma|Eine kurze Beschreibung deines Angebots\.)(?=<| ?<)|(?<=placeholder="")Eingabe")]
    private static partial Regex TemplateTextRegex();
}
