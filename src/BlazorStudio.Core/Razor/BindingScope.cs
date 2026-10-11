using System.Text.RegularExpressions;
using BlazorStudio.Core.Model;

namespace BlazorStudio.Core.Razor;

public enum BindingSourceKind { Member, Method, LoopVariable, Injected, Context }

public sealed record BindingCandidate(string Expression, string Type, BindingSourceKind Kind, string Source, string? Signature = null)
{
    public bool IsMethod => Kind == BindingSourceKind.Method;
}

/// <summary>Welche Werte und Methoden sind an einer Stelle im Markup für Bindings verfügbar?</summary>
public static partial class BindingScope
{
    /// <summary>
    /// members: Member der aktuellen Komponente (@code + .razor.cs).
    /// node: der ausgewählte Knoten (für Schleifenvariablen der umgebenden @foreach-Blöcke).
    /// </summary>
    public static List<BindingCandidate> Collect(RazorDocument doc, DesignNode? node, IEnumerable<CodeMember> members)
    {
        var memberList = members.ToList();
        var list = new List<BindingCandidate>();

        // Schleifen- und Kontextvariablen (innen zuerst)
        if (node is not null)
        {
            foreach (var anc in new[] { node }.Concat(node.Ancestors()))
            {
                if (anc.Kind == NodeKind.Control)
                {
                    foreach (var v in LoopVariables(anc.Text, memberList))
                        list.Add(v);
                }
                if (anc.Kind == NodeKind.Component && anc != node)
                {
                    var ctx = anc.GetAttribute("Context");
                    if (!string.IsNullOrWhiteSpace(ctx)) list.Add(new BindingCandidate(ctx.Trim(), "var", BindingSourceKind.Context, anc.Name));
                }
            }
        }

        foreach (var m in memberList)
        {
            if (m.Kind == CodeMemberKind.Method)
                list.Add(new BindingCandidate(m.Name, m.Type, BindingSourceKind.Method, m.Source, m.Signature));
            else
                list.Add(new BindingCandidate(m.Name, m.Type, BindingSourceKind.Member, m.Source));
        }

        foreach (var (type, name) in doc.Injections)
            list.Add(new BindingCandidate(name, type, BindingSourceKind.Injected, "@inject"));

        return list
            .GroupBy(c => c.Expression + (c.IsMethod ? "()" : ""))
            .Select(g => g.First())
            .ToList();
    }

    /// <summary>Kandidaten für einen Wert eines bestimmten Typs, passende zuerst.</summary>
    public static IEnumerable<BindingCandidate> ForValue(IEnumerable<BindingCandidate> all, string? type)
    {
        var bare = Normalize(type);
        return all.Where(c => !c.IsMethod)
            .OrderBy(c => Normalize(c.Type) == bare ? 0 : bare is "object" or "TValue" or "" ? 1 : c.Type == "var" ? 1 : 2)
            .ThenBy(c => c.Kind == BindingSourceKind.LoopVariable ? 0 : 1)
            .ThenBy(c => c.Expression, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Methoden, die als Handler in Frage kommen.</summary>
    public static IEnumerable<BindingCandidate> Methods(IEnumerable<BindingCandidate> all) =>
        all.Where(c => c.IsMethod).OrderBy(c => c.Expression, StringComparer.OrdinalIgnoreCase);

    public static bool TypeMatches(string? candidate, string? wanted)
    {
        var a = Normalize(candidate);
        var b = Normalize(wanted);
        return a == b || b is "" or "object" or "TValue" or "TItem" || a == "var";
    }

    private static string Normalize(string? t) => (t ?? "").Replace(" ", "").TrimEnd('?');

    private static IEnumerable<BindingCandidate> LoopVariables(string header, List<CodeMember> members)
    {
        var m = ForeachRegex().Match(header);
        if (m.Success)
        {
            var declared = m.Groups["type"].Value;
            var name = m.Groups["name"].Value;
            var collection = m.Groups["coll"].Value.Trim();
            var type = declared == "var" ? ElementTypeOf(collection, members) ?? "var" : declared;
            yield return new BindingCandidate(name, type, BindingSourceKind.LoopVariable, "@foreach");
            yield break;
        }
        var f = ForRegex().Match(header);
        if (f.Success)
            yield return new BindingCandidate(f.Groups["name"].Value, f.Groups["type"].Value == "var" ? "int" : f.Groups["type"].Value, BindingSourceKind.LoopVariable, "@for");
    }

    private static string? ElementTypeOf(string collection, List<CodeMember> members)
    {
        var root = collection.Split('.', '(', '[')[0];
        var member = members.FirstOrDefault(x => x.Name == root);
        if (member is null || collection != root) return null;
        var t = member.BareType;
        if (t.EndsWith("[]", StringComparison.Ordinal)) return t[..^2];
        var lt = t.IndexOf('<');
        var gt = t.LastIndexOf('>');
        if (lt > 0 && gt > lt)
        {
            var inner = t[(lt + 1)..gt];
            return inner.Contains(',') ? null : inner.Trim();
        }
        return null;
    }

    [GeneratedRegex(@"^foreach\s*\(\s*(?<type>[\w<>\[\],\.\?]+)\s+(?<name>\w+)\s+in\s+(?<coll>.+)\)\s*$")]
    private static partial Regex ForeachRegex();

    [GeneratedRegex(@"^for\s*\(\s*(?<type>[\w\.]+)\s+(?<name>\w+)\s*=")]
    private static partial Regex ForRegex();
}
