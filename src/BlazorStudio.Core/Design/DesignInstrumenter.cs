using System.Text;
using BlazorStudio.Core.Model;

namespace BlazorStudio.Core.Design;

/// <summary>
/// Versieht eine .razor-Datei mit unsichtbaren Markierungen, damit der Live-Designer jedes gerenderte Element
/// einem Knoten zuordnen kann – ohne den Quelltext sonst zu verändern (Leerzeichen bleiben exakt erhalten):
/// <list type="bullet">
/// <item>HTML-Elemente bekommen <c>data-bs-id="…"</c>.</item>
/// <item>Komponenten werden von <c>&lt;template data-bs-s="…"&gt;</c> … <c>&lt;template data-bs-e="…"&gt;</c> eingerahmt
/// (Komponenten lehnen unbekannte Attribute oft mit einer Ausnahme ab; template-Elemente sind unsichtbar).</item>
/// </list>
/// </summary>
public static class DesignInstrumenter
{
    /// <summary>Elemente, die nie markiert werden (Inhalt nicht sichtbar oder Markierung stört).</summary>
    private static readonly HashSet<string> SkipElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "html", "head", "meta", "link", "base", "title", "script", "style", "noscript", "template", "text",
    };

    /// <param name="text">Exakter Dateitext.</param>
    /// <param name="doc">Geparstes Dokument zu <paramref name="text"/>.</param>
    /// <param name="spans">Knoten-Id → (Start, Ende) in <paramref name="text"/>.</param>
    /// <param name="prefix">Präfix pro Datei, damit Ids verschiedener Dateien nicht kollidieren.</param>
    /// <param name="renderFragmentParameters">
    /// Für einen Komponenten-Namen die Namen seiner RenderFragment-Parameter (z. B. MudTable → HeaderContent, RowTemplate);
    /// null, wenn die Komponente unbekannt ist.
    /// </param>
    public static string Instrument(string text, RazorDocument doc, IReadOnlyDictionary<string, (int Start, int End)> spans, string prefix,
        Func<string, IReadOnlyCollection<string>?>? renderFragmentParameters = null)
    {
        var inserts = new List<(int Pos, int Order, string Text)>();
        Walk(doc.Root, inheritedHead: false);
        if (inserts.Count == 0) return text;

        // von hinten einfügen, damit die Positionen gültig bleiben (bei gleicher Position: Ende-Marker vor Start-Marker)
        var sb = new StringBuilder(text);
        foreach (var (pos, _, ins) in inserts.OrderByDescending(i => i.Pos).ThenBy(i => i.Order))
            sb.Insert(pos, ins);
        return sb.ToString();

        void Walk(DesignNode node, bool inheritedHead)
        {
            var explicitParams = node.Kind == NodeKind.Component && HasParameterChildren(node);
            foreach (var c in node.Children)
            {
                var inHead = inheritedHead || node.Kind == NodeKind.Element && node.Name.Equals("head", StringComparison.OrdinalIgnoreCase);
                if (!spans.TryGetValue(c.Id, out var span) || span.Start < 0 || span.End > text.Length || span.End <= span.Start)
                {
                    Walk(c, inHead);
                    continue;
                }
                var id = prefix + c.Id;
                switch (c.Kind)
                {
                    case NodeKind.Element when !inHead && !SkipElements.Contains(c.Name) && !c.Attributes.Any(a => a.Name == "data-bs-id"):
                    {
                        var at = span.Start + 1 + c.Name.Length;
                        if (span.Start + 1 + c.Name.Length <= text.Length && text[span.Start] == '<' && string.CompareOrdinal(text, span.Start + 1, c.Name, 0, c.Name.Length) == 0)
                            inserts.Add((at, 1, " data-bs-id=\"" + id + "\""));
                        break;
                    }
                    case NodeKind.Component when !inHead && !explicitParams && !IsParameterElement(node, c):
                    {
                        var frags = renderFragmentParameters?.Invoke(c.Name);
                        var container = frags?.Contains("ChildContent") == true || c.Children.Count > 0;
                        inserts.Add((span.Start, 2, "<template data-bs-s=\"" + id + "\" data-bs-name=\"" + c.Name + "\"" + (container ? " data-bs-container=\"1\"" : "") + "></template>"));
                        inserts.Add((span.End, 0, "<template data-bs-e=\"" + id + "\"></template>"));
                        break;
                    }
                }
                Walk(c, inHead);
            }
        }

        bool HasParameterChildren(DesignNode component) => component.Children.Any(ch => IsParameterElement(component, ch));

        bool IsParameterElement(DesignNode parent, DesignNode child)
        {
            if (parent.Kind != NodeKind.Component || child.Kind != NodeKind.Component) return false;
            var fragments = renderFragmentParameters?.Invoke(parent.Name);
            if (fragments is not null) return fragments.Contains(child.Name);
            // Unbekannte Eltern-Komponente: ein Kind, das selbst keine bekannte Komponente ist, ist vermutlich ein Parameter
            return renderFragmentParameters?.Invoke(child.Name) is null && child.Name is "ChildContent" or "Body" || child.Attributes.Any(a => a.Name == "Context");
        }
    }

    /// <summary>Kurzes, stabiles Präfix für eine Datei (z. B. "f3a9_").</summary>
    public static string PrefixFor(string relativePath)
    {
        unchecked
        {
            var h = 23;
            foreach (var ch in relativePath.Replace('\\', '/').ToLowerInvariant()) h = h * 31 + ch;
            return "f" + ((uint)h % 0xFFFF).ToString("x") + "_";
        }
    }
}
