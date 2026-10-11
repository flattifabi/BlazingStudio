using BlazorStudio.Core.Model;
using BlazorStudio.Core.Razor;

namespace BlazorStudio.Core.Editing;

/// <summary>
/// Einfügen in RenderFragment-Parameter einer Komponente („Slots“): ChildContent, TitleContent, DialogActions …
/// Hält die Blazor-Regel ein: Sobald ein Parameter-Element benutzt wird, muss der übrige Inhalt in &lt;ChildContent&gt; stehen.
/// </summary>
public static class SlotEditor
{
    public const string ChildContent = "ChildContent";

    /// <summary>Kinder der Komponente, die Parameter-Elemente sind (&lt;TitleContent&gt; …).</summary>
    public static IEnumerable<DesignNode> ParameterChildren(DesignNode component, IReadOnlyCollection<string> fragments) =>
        component.Children.Where(c => c.Kind == NodeKind.Component && fragments.Contains(c.Name));

    /// <summary>Wird die Komponente in der expliziten Form (&lt;ChildContent&gt;, &lt;TitleContent&gt; …) geschrieben?</summary>
    public static bool IsExplicit(DesignNode component, IReadOnlyCollection<string> fragments) => ParameterChildren(component, fragments).Any();

    /// <summary>
    /// Einfügepunkt für einen Slot. Fehlende Parameter-Elemente werden angelegt; freier Inhalt wandert bei Bedarf in &lt;ChildContent&gt;.
    /// </summary>
    /// <param name="fragments">Alle RenderFragment-Parameter der Komponente (aus dem Katalog), in Deklarationsreihenfolge.</param>
    public static (DesignNode Parent, int Index) Resolve(DesignNode component, string? slot, IReadOnlyList<string> fragments)
    {
        slot = string.IsNullOrEmpty(slot) ? ChildContent : slot;
        var known = fragments.Count > 0 ? fragments : new[] { ChildContent };
        var explicitForm = IsExplicit(component, known);

        // Normalfall: Inhalt direkt in der Komponente
        if (slot == ChildContent && !explicitForm) return (component, component.Children.Count);

        if (ParameterChildren(component, known).FirstOrDefault(c => c.Name == slot) is { } existing)
            return (existing, existing.Children.Count);

        if (!explicitForm)
        {
            // bisherigen Inhalt nach <ChildContent> verschieben (nur, wenn es echten Inhalt gibt)
            var content = component.Children.Where(c => !IsBlank(c)).ToList();
            foreach (var c in component.Children.ToList()) c.Detach();
            if (content.Count > 0)
            {
                var cc = NewSlot(ChildContent);
                foreach (var c in content) cc.AddChild(c);
                component.AddChild(cc);
            }
        }

        var node = NewSlot(slot);
        component.AddChild(node, OrderedIndex(component, slot, known));
        component.SelfClosingInSource = false;
        return (node, 0);
    }

    /// <summary>Neue Knoten in einen Slot einfügen.</summary>
    public static void Insert(DesignNode component, string? slot, IReadOnlyList<string> fragments, IReadOnlyList<DesignNode> nodes)
    {
        var (parent, index) = Resolve(component, slot, fragments);
        foreach (var n in nodes)
        {
            n.Detach();
            parent.AddChild(n, index++);
        }
    }

    /// <summary>Leere Parameter-Elemente entfernen; bleibt nur &lt;ChildContent&gt;, wieder die kurze Form schreiben.</summary>
    public static void Tidy(DesignNode component, IReadOnlyCollection<string> fragments)
    {
        foreach (var p in ParameterChildren(component, fragments).Where(p => p.Children.All(IsBlank)).ToList()) p.Detach();
        var rest = ParameterChildren(component, fragments).ToList();
        if (rest.Count == 1 && rest[0].Name == ChildContent && component.Children.All(c => c == rest[0] || IsBlank(c)) && !rest[0].Attributes.Any())
        {
            var cc = rest[0];
            var idx = cc.IndexInParent;
            cc.Detach();
            foreach (var c in cc.Children.ToList())
            {
                c.Detach();
                component.AddChild(c, idx++);
            }
        }
    }

    private static int OrderedIndex(DesignNode component, string slot, IReadOnlyList<string> fragments)
    {
        var order = fragments.ToList();
        var mine = order.IndexOf(slot);
        if (mine < 0) return component.Children.Count;
        for (var i = 0; i < component.Children.Count; i++)
        {
            var c = component.Children[i];
            var theirs = order.IndexOf(c.Name);
            if (c.Kind == NodeKind.Component && theirs > mine) return i;
        }
        return component.Children.Count;
    }

    private static DesignNode NewSlot(string name) => RazorParser.ParseFragment("<" + name + "></" + name + ">")[0];

    private static bool IsBlank(DesignNode n) => n.Kind == NodeKind.Text && string.IsNullOrWhiteSpace(n.Text);
}
