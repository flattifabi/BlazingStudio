using System.Text;
using BlazorStudio.Core.Model;

namespace BlazorStudio.Core.Razor;

public sealed class RazorWriteResult
{
    public required string Text { get; init; }

    /// <summary>Knoten-Id → Bereich im erzeugten Text.</summary>
    public required Dictionary<string, (int Start, int End)> Spans { get; init; }
}

/// <summary>Schreibt ein RazorDocument formatiert zurück (4 Leerzeichen Einrückung).</summary>
public sealed class RazorWriter
{
    private static readonly HashSet<string> InlineElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "abbr", "b", "bdi", "br", "cite", "code", "em", "i", "kbd", "mark", "q", "s", "small",
        "span", "strong", "sub", "sup", "time", "u", "var", "img", "label",
    };

    private const int InlineLimit = 110;
    private const string Indent = "    ";

    private readonly StringBuilder _sb = new();
    private readonly Dictionary<string, (int, int)> _spans = new();

    public static RazorWriteResult Write(RazorDocument doc)
    {
        var w = new RazorWriter();
        w.WriteDocument(doc);
        return new RazorWriteResult { Text = w._sb.ToString(), Spans = w._spans };
    }

    public static string WriteNodes(IEnumerable<DesignNode> nodes, int depth = 0)
    {
        var w = new RazorWriter();
        w.WriteChildren(nodes.ToList(), depth, codeContext: false);
        return w._sb.ToString().TrimEnd();
    }

    private void WriteDocument(RazorDocument doc)
    {
        foreach (var d in doc.Directives) _sb.Append(d.Trim()).Append('\n');
        if (doc.Directives.Count > 0 && doc.Root.Children.Count > 0) _sb.Append('\n');

        WriteChildren(doc.Root.Children, 0, codeContext: false);

        foreach (var block in doc.CodeBlocks)
        {
            TrimTrailingBlankLines();
            _sb.Append('\n');
            if (_sb.Length > 1) _sb.Append('\n');
            _sb.Append(NormalizeBlock(block, 0));
            _sb.Append('\n');
        }

        // genau ein Zeilenumbruch am Ende
        var text = _sb.ToString().TrimEnd('\n', ' ', '\r') + "\n";
        _sb.Clear().Append(text.TrimStart('\n'));
    }

    private void TrimTrailingBlankLines()
    {
        while (_sb.Length > 0 && (_sb[^1] == '\n' || _sb[^1] == ' ')) _sb.Length--;
    }

    private void Line(int depth) => _sb.Append(string.Concat(Enumerable.Repeat(Indent, depth)));

    private void WriteChildren(List<DesignNode> children, int depth, bool codeContext)
    {
        DesignNode? prev = null;
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];

            // Leerzeilen zwischen großen Blöcken auf oberster Ebene
            if (depth == 0 && prev is not null && (IsBlockish(prev) || IsBlockish(child)) && !child.IsContinuation)
                _sb.Append('\n');

            if (child.Kind == NodeKind.Control)
            {
                i = WriteControlChain(children, i, depth);
                prev = children[i];
                continue;
            }

            if (codeContext && child.Kind == NodeKind.Text)
            {
                // Text direkt im Code-Block ist eigentlich C#
                Line(depth);
                var s = _sb.Length;
                _sb.Append(child.Text.Trim());
                _spans[child.Id] = (s, _sb.Length);
                _sb.Append('\n');
                prev = child;
                continue;
            }

            if (child.Kind == NodeKind.Text && child.Text.Trim().Length == 0)
            {
                continue;
            }

            Line(depth);
            WriteNode(child, depth, inline: false);
            _sb.Append('\n');
            prev = child;
        }
    }

    private static bool IsBlockish(DesignNode n) =>
        n.Kind is NodeKind.Control or NodeKind.Code ||
        (n.IsTagLike && n.Children.Any(c => c.IsTagLike || c.Kind == NodeKind.Control));

    /// <summary>Schreibt @if {...} else if {...} else {...}. Liefert den Index des letzten Gliedes.</summary>
    private int WriteControlChain(List<DesignNode> children, int i, int depth)
    {
        var first = children[i];
        Line(depth);
        var chainStart = _sb.Length;
        _sb.Append('@').Append(first.Text.Trim()).Append('\n');
        Line(depth);
        _sb.Append("{\n");
        var nodeStart = chainStart;
        WriteChildren(first.Children, depth + 1, codeContext: true);
        Line(depth);
        _sb.Append('}');
        _spans[first.Id] = (nodeStart, _sb.Length);

        while (i + 1 < children.Count && children[i + 1].Kind == NodeKind.Control && children[i + 1].IsContinuation)
        {
            i++;
            var cont = children[i];
            _sb.Append('\n');
            Line(depth);
            var s = _sb.Length;
            _sb.Append(cont.Text.Trim()).Append('\n');
            Line(depth);
            _sb.Append("{\n");
            WriteChildren(cont.Children, depth + 1, codeContext: true);
            Line(depth);
            _sb.Append('}');
            _spans[cont.Id] = (s, _sb.Length);
        }
        _sb.Append('\n');
        return i;
    }

    private void WriteNode(DesignNode node, int depth, bool inline)
    {
        var start = _sb.Length;
        switch (node.Kind)
        {
            case NodeKind.Text:
                _sb.Append(inline ? node.Text : node.Text.Trim());
                break;
            case NodeKind.Expression:
                _sb.Append(node.Text);
                break;
            case NodeKind.Comment:
                _sb.Append(node.Text.Trim());
                break;
            case NodeKind.Code:
                _sb.Append(NormalizeBlock(node.Text, depth));
                break;
            case NodeKind.Control:
                // Kommt nur inline vor, wenn ein Control einzeln eingebettet ist
                _sb.Append('@').Append(node.Text.Trim()).Append(" { ");
                foreach (var c in node.Children) WriteNode(c, depth, inline: true);
                _sb.Append(" }");
                break;
            default:
                WriteElement(node, depth);
                break;
        }
        _spans[node.Id] = (start, _sb.Length);
    }

    private void WriteElement(DesignNode node, int depth)
    {
        _sb.Append('<').Append(node.Name);
        foreach (var a in node.Attributes)
        {
            _sb.Append(' ').Append(a.Name);
            if (a.Value is not null)
            {
                var q = a.Quote;
                // Nur wechseln, wenn das Zeichen den Wert wirklich beenden würde (nicht in @(…)/@x["…"])
                if (!QuoteSafe(a.Value, q))
                {
                    var other = q == '"' ? '\'' : '"';
                    if (QuoteSafe(a.Value, other)) q = other;
                }
                _sb.Append('=').Append(q).Append(a.Value).Append(q);
            }
        }

        if (node.Children.Count == 0)
        {
            if (node.IsVoid || node.Kind == NodeKind.Component)
            {
                _sb.Append(" />");
                return;
            }
            _sb.Append("></").Append(node.Name).Append('>');
            return;
        }

        _sb.Append('>');

        if (node.IsRawContent)
        {
            var raw = string.Concat(node.Children.Select(c => c.Text));
            var trimmed = raw.Trim('\n', '\r');
            if (!trimmed.Contains('\n'))
            {
                _sb.Append(trimmed.Trim());
            }
            else
            {
                _sb.Append('\n').Append(Reindent(trimmed, depth + 1)).Append('\n');
                Line(depth);
            }
            _sb.Append("</").Append(node.Name).Append('>');
            return;
        }

        if (CanInline(node))
        {
            var children = node.Children;
            for (var i = 0; i < children.Count; i++)
            {
                var c = children[i];
                if (c.Kind == NodeKind.Text)
                {
                    var t = c.Text;
                    if (i == 0) t = t.TrimStart();
                    if (i == children.Count - 1) t = t.TrimEnd();
                    var s = _sb.Length;
                    _sb.Append(t);
                    _spans[c.Id] = (s, _sb.Length);
                }
                else
                {
                    WriteNode(c, depth, inline: true);
                }
            }
            _sb.Append("</").Append(node.Name).Append('>');
            return;
        }

        _sb.Append('\n');
        WriteChildren(node.Children, depth + 1, codeContext: false);
        Line(depth);
        _sb.Append("</").Append(node.Name).Append('>');
    }

    private static bool CanInline(DesignNode node)
    {
        if (!node.Children.All(IsInlineNode)) return false;
        var len = node.Name.Length * 2 + 5 + node.Attributes.Sum(a => a.Name.Length + (a.Value?.Length ?? 0) + 4);
        len += node.Children.Sum(EstimateLength);
        return len <= InlineLimit;
    }

    private static bool IsInlineNode(DesignNode n) => n.Kind switch
    {
        NodeKind.Text => !n.Text.Contains('\n'),
        NodeKind.Expression => !n.Text.Contains('\n'),
        NodeKind.Element => InlineElements.Contains(n.Name) && n.Children.All(IsInlineNode),
        _ => false,
    };

    private static int EstimateLength(DesignNode n) => n.Kind switch
    {
        NodeKind.Text or NodeKind.Expression => n.Text.Length,
        _ => n.Name.Length * 2 + 5 + n.Attributes.Sum(a => a.Name.Length + (a.Value?.Length ?? 0) + 4) + n.Children.Sum(EstimateLength),
    };

    /// <summary>Mehrzeiligen Block (Code) einrücken: gemeinsame Einrückung ab Zeile 2 entfernen und neu setzen.</summary>
    private static string NormalizeBlock(string block, int depth)
    {
        var lines = block.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 1) return block.Trim();
        var rest = lines.Skip(1).Where(l => l.Trim().Length > 0).ToList();
        var common = rest.Count == 0 ? 0 : rest.Min(l => l.Length - l.TrimStart().Length);
        // Die letzte Zeile (meist "}") bestimmt die Basis, wenn sie weniger eingerückt ist
        var last = lines[^1];
        if (last.Trim().Length > 0) common = Math.Min(common, last.Length - last.TrimStart().Length);
        var pad = string.Concat(Enumerable.Repeat(Indent, depth));
        var sb = new StringBuilder(lines[0].Trim());
        foreach (var l in lines.Skip(1))
        {
            sb.Append('\n');
            if (l.Trim().Length == 0) continue;
            sb.Append(pad).Append(l.Length >= common ? l[common..].TrimEnd() : l.Trim());
        }
        return sb.ToString();
    }

    private static string Reindent(string text, int depth)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var nonEmpty = lines.Where(l => l.Trim().Length > 0).ToList();
        var common = nonEmpty.Count == 0 ? 0 : nonEmpty.Min(l => l.Length - l.TrimStart().Length);
        var pad = string.Concat(Enumerable.Repeat(Indent, depth));
        return string.Join('\n', lines.Select(l => l.Trim().Length == 0 ? "" : pad + l[common..].TrimEnd()));
    }

    /// <summary>Lässt sich <paramref name="value"/> mit diesem Anführungszeichen schreiben, ohne dass der Parser den Wert vorzeitig beendet?</summary>
    private static bool QuoteSafe(string value, char q)
    {
        var i = 0;
        while (i < value.Length)
        {
            var c = value[i];
            if (c == '@' && i + 1 < value.Length && value[i + 1] == '(')
            {
                var e = CSharpScanner.SkipBalanced(value, i + 1, '(', ')');
                if (e < 0) return false;
                i = e;
                continue;
            }
            if (c == '@' && i + 1 < value.Length && CSharpScanner.IsIdentStart(value[i + 1]))
            {
                i = Math.Max(i + 1, CSharpScanner.SkipImplicitExpression(value, i + 1));
                continue;
            }
            if (c == q) return false;
            i++;
        }
        return true;
    }
}
