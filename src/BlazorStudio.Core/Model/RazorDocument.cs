namespace BlazorStudio.Core.Model;

/// <summary>Eine .razor-Datei: Direktiven oben, Markup-Baum, @code-Blöcke unten.</summary>
public sealed class RazorDocument
{
    public List<string> Directives { get; } = new();

    public DesignNode Root { get; } = DesignNode.CreateRoot();

    /// <summary>Komplette @code { ... } bzw. @functions { ... } Blöcke, roh.</summary>
    public List<string> CodeBlocks { get; } = new();

    public DesignNode? Find(string? id) => id is null ? null : Root.Find(id);

    public IEnumerable<DesignNode> AllNodes() => Root.Descendants();

    public string? PageRoute
    {
        get
        {
            foreach (var d in Directives)
            {
                var t = d.Trim();
                if (!t.StartsWith("@page", StringComparison.Ordinal)) continue;
                var q1 = t.IndexOf('"');
                var q2 = q1 >= 0 ? t.IndexOf('"', q1 + 1) : -1;
                if (q1 >= 0 && q2 > q1) return t[(q1 + 1)..q2];
            }
            return null;
        }
    }

    public string? InheritsType =>
        Directives.Select(d => d.Trim()).FirstOrDefault(d => d.StartsWith("@inherits", StringComparison.Ordinal))?[9..].Trim();

    /// <summary>@inject-Direktiven als (Typ, Name).</summary>
    public IEnumerable<(string Type, string Name)> Injections
    {
        get
        {
            foreach (var d in Directives)
            {
                var t = d.Trim();
                if (!t.StartsWith("@inject ", StringComparison.Ordinal)) continue;
                var parts = t[8..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2) yield return (string.Join(' ', parts[..^1]), parts[^1]);
            }
        }
    }
}
