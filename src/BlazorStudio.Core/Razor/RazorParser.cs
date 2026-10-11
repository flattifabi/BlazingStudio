using System.Text;
using BlazorStudio.Core.Model;

namespace BlazorStudio.Core.Razor;

public enum RazorDiagnosticCode
{
    UnclosedTag,
    UnexpectedCloseTag,
    UnclosedBlock,
    UnclosedAttribute,
    UnclosedComment,
}

public sealed record RazorDiagnostic(RazorDiagnosticCode Code, int Start, int End, string Argument, bool IsError = true);

public sealed class RazorParseResult
{
    public required RazorDocument Document { get; init; }

    public List<RazorDiagnostic> Diagnostics { get; } = new();

    public bool HasErrors => Diagnostics.Any(d => d.IsError);
}

/// <summary>
/// Fehlertoleranter Parser für .razor-Dateien. Ziel ist nicht der volle Razor-Compiler, sondern ein Baum,
/// den der Designer bearbeiten und verlustarm zurückschreiben kann. Unbekannte C#-Konstrukte bleiben als
/// Code-Knoten unverändert erhalten.
/// </summary>
public sealed class RazorParser
{
    private static readonly HashSet<string> Directives = new(StringComparer.Ordinal)
    {
        "page", "using", "inject", "inherits", "layout", "attribute", "implements", "namespace",
        "typeparam", "rendermode", "preservewhitespace", "model", "addTagHelper",
    };

    private static readonly HashSet<string> StructuredControls = new(StringComparer.Ordinal)
    {
        "if", "foreach", "for", "while", "lock",
    };

    private static readonly HashSet<string> OpaqueControls = new(StringComparer.Ordinal)
    {
        "switch", "try", "do",
    };

    private static readonly HashSet<string> RawTextElements = new(StringComparer.OrdinalIgnoreCase) { "script", "style" };

    private readonly string _t;
    private readonly int _base;
    private readonly RazorParseResult _result;
    private readonly RazorDocument _doc;
    private readonly List<string> _openTags = new();
    private int _p;

    private RazorParser(string text, int baseOffset, RazorParseResult result)
    {
        _t = text;
        _base = baseOffset;
        _result = result;
        _doc = result.Document;
    }

    public static RazorParseResult Parse(string text)
    {
        var result = new RazorParseResult { Document = new RazorDocument() };
        var parser = new RazorParser(text ?? "", 0, result);
        parser.ParseContent(result.Document.Root, closeTag: null, topLevel: true, codeContext: false);
        return result;
    }

    /// <summary>Parst ein Markup-Fragment (z. B. eine Vorlage aus der Toolbox) in eine Knotenliste.</summary>
    public static List<DesignNode> ParseFragment(string markup)
    {
        var result = new RazorParseResult { Document = new RazorDocument() };
        var parser = new RazorParser(markup, 0, result);
        var holder = DesignNode.CreateRoot();
        parser.ParseContent(holder, null, topLevel: false, codeContext: false);
        var list = holder.Children.ToList();
        foreach (var n in list) n.Parent = null;
        return list;
    }

    private bool Eof => _p >= _t.Length;

    private char Cur => _p < _t.Length ? _t[_p] : '\0';

    private char Peek(int o = 1) => _p + o < _t.Length ? _t[_p + o] : '\0';

    private bool At(string s) => string.CompareOrdinal(_t, _p, s, 0, s.Length) == 0;

    private void Diag(RazorDiagnosticCode code, int start, int end, string arg = "", bool error = true) =>
        _result.Diagnostics.Add(new RazorDiagnostic(code, _base + start, _base + end, arg, error));

    // ------------------------------------------------------------------
    // Inhalt
    // ------------------------------------------------------------------

    /// <summary>Liefert true, wenn der passende Close-Tag gelesen wurde.</summary>
    private bool ParseContent(DesignNode parent, string? closeTag, bool topLevel, bool codeContext)
    {
        var text = new StringBuilder();
        var textStart = -1;

        void FlushText()
        {
            if (textStart < 0) return;
            var raw = text.ToString();
            text.Clear();
            var start = textStart;
            textStart = -1;
            if (codeContext)
            {
                var code = raw.Trim();
                if (code.Length == 0) return;
                var lead = raw.Length - raw.TrimStart().Length;
                parent.AddChild(new DesignNode
                {
                    Kind = NodeKind.Code, Text = code,
                    SourceStart = _base + start + lead, SourceEnd = _base + start + lead + code.Length,
                });
                return;
            }

            var normalized = CollapseWhitespace(raw);
            if (normalized.Length == 0) return;
            if (normalized == " " && raw.Contains('\n')) return; // reine Einrückung
            parent.AddChild(new DesignNode
            {
                Kind = NodeKind.Text, Text = normalized, SourceStart = _base + start, SourceEnd = _base + start + raw.Length,
            });
        }

        void AppendText(char c)
        {
            if (textStart < 0) textStart = _p;
            text.Append(c);
            _p++;
        }

        while (!Eof)
        {
            var c = Cur;

            if (codeContext)
            {
                // In C#-Bereichen: Strings und Kommentare überspringen, Markup nur am Anweisungsbeginn.
                if (c == '"' || (c == '@' && Peek() == '"') || (c == '$' && (Peek() == '"' || Peek() == '@')))
                {
                    var end = CSharpScanner.SkipString(_t, _p);
                    if (textStart < 0) textStart = _p;
                    text.Append(_t, _p, end - _p);
                    _p = end;
                    continue;
                }
                if (c == '/' && Peek() == '/')
                {
                    var nl = _t.IndexOf('\n', _p);
                    var end = nl < 0 ? _t.Length : nl;
                    if (textStart < 0) textStart = _p;
                    text.Append(_t, _p, end - _p);
                    _p = end;
                    continue;
                }
                if (c == '@' && Peek() == '*')
                {
                    FlushText();
                    ParseRazorComment(parent);
                    continue;
                }
                if (c == '@' && Peek() == ':')
                {
                    // @: Textzeile im Code
                    FlushText();
                    var nl = _t.IndexOf('\n', _p);
                    var end = nl < 0 ? _t.Length : nl;
                    parent.AddChild(new DesignNode { Kind = NodeKind.Code, Text = _t[_p..end].TrimEnd(), SourceStart = _base + _p, SourceEnd = _base + end });
                    _p = end;
                    continue;
                }
                if (c == '<' && (CSharpScanner.IsIdentStart(Peek()) || Peek() == '!') && IsStatementStart())
                {
                    FlushText();
                    if (At("<!--")) ParseHtmlComment(parent);
                    else ParseElement(parent);
                    continue;
                }
                if (c == '@' && CSharpScanner.IsIdentStart(Peek()) && IsStatementStart())
                {
                    // @if usw. innerhalb eines Code-Blocks ist erlaubt, wird als Code behandelt
                }
                AppendText(c);
                continue;
            }

            if (c == '<')
            {
                if (Peek() == '/')
                {
                    var save = _p;
                    var name = ReadCloseTagName(out var closeEnd);
                    if (name is not null)
                    {
                        if (closeTag is not null && string.Equals(name, closeTag, StringComparison.OrdinalIgnoreCase))
                        {
                            FlushText();
                            _p = closeEnd;
                            return true;
                        }

                        if (_openTags.Any(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase)))
                        {
                            // Schließt einen äußeren Tag: hier implizit beenden.
                            FlushText();
                            _p = save;
                            return false;
                        }

                        FlushText();
                        Diag(RazorDiagnosticCode.UnexpectedCloseTag, save, closeEnd, name);
                        _p = closeEnd;
                        continue;
                    }
                    _p = save;
                    AppendText(c);
                    continue;
                }
                if (At("<!--"))
                {
                    FlushText();
                    ParseHtmlComment(parent);
                    continue;
                }
                if (CSharpScanner.IsIdentStart(Peek()))
                {
                    FlushText();
                    ParseElement(parent);
                    continue;
                }
                AppendText(c);
                continue;
            }

            if (c == '@')
            {
                var next = Peek();
                if (next == '@')
                {
                    if (textStart < 0) textStart = _p;
                    text.Append("@@");
                    _p += 2;
                    continue;
                }
                if (next == '*')
                {
                    FlushText();
                    ParseRazorComment(parent);
                    continue;
                }
                if (next == '(')
                {
                    FlushText();
                    var start = _p;
                    var end = CSharpScanner.SkipBalanced(_t, _p + 1, '(', ')');
                    if (end < 0)
                    {
                        Diag(RazorDiagnosticCode.UnclosedBlock, start, _t.Length, "@(");
                        end = _t.Length;
                    }
                    parent.AddChild(new DesignNode { Kind = NodeKind.Expression, Text = _t[start..end], SourceStart = _base + start, SourceEnd = _base + end });
                    _p = end;
                    continue;
                }
                if (next == '{')
                {
                    FlushText();
                    var start = _p;
                    var end = CSharpScanner.SkipBalanced(_t, _p + 1, '{', '}');
                    if (end < 0)
                    {
                        Diag(RazorDiagnosticCode.UnclosedBlock, start, _t.Length, "@{");
                        end = _t.Length;
                    }
                    parent.AddChild(new DesignNode { Kind = NodeKind.Code, Text = _t[start..end], SourceStart = _base + start, SourceEnd = _base + end });
                    _p = end;
                    continue;
                }
                var prev = _p > 0 ? _t[_p - 1] : ' ';
                if (CSharpScanner.IsIdentStart(next) && !char.IsLetterOrDigit(prev))
                {
                    FlushText();
                    ParseTransition(parent, topLevel);
                    continue;
                }
                AppendText(c);
                continue;
            }

            AppendText(c);
        }

        FlushText();
        if (closeTag is not null)
            return false;
        return true;
    }

    /// <summary>Steht vor _p (in dieser Zeile bzw. nach { } ;) nur Leerraum?</summary>
    private bool IsStatementStart()
    {
        var i = _p - 1;
        while (i >= 0 && _t[i] is ' ' or '\t' or '\r') i--;
        if (i < 0) return true;
        return _t[i] is '\n' or '{' or '}' or ';';
    }

    private static string CollapseWhitespace(string s)
    {
        var sb = new StringBuilder(s.Length);
        var ws = false;
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch))
            {
                ws = true;
                continue;
            }
            if (ws) sb.Append(' ');
            ws = false;
            sb.Append(ch);
        }
        if (ws) sb.Append(' ');
        return sb.ToString();
    }

    // ------------------------------------------------------------------
    // @-Übergänge
    // ------------------------------------------------------------------

    private void ParseTransition(DesignNode parent, bool topLevel)
    {
        var start = _p;
        var i = _p + 1;
        while (i < _t.Length && CSharpScanner.IsIdentPart(_t[i])) i++;
        var word = _t[(_p + 1)..i];

        if (topLevel && parent.IsRoot && Directives.Contains(word) && !(word == "using" && NextNonWs(i) == '('))
        {
            var nl = _t.IndexOf('\n', i);
            var end = nl < 0 ? _t.Length : nl;
            _doc.Directives.Add(_t[start..end].TrimEnd());
            _p = end;
            return;
        }

        if (word is "code" or "functions")
        {
            var brace = CSharpScanner.SkipWhitespace(_t, i);
            if (brace < _t.Length && _t[brace] == '{')
            {
                var end = CSharpScanner.SkipBalanced(_t, brace, '{', '}');
                if (end < 0)
                {
                    Diag(RazorDiagnosticCode.UnclosedBlock, start, _t.Length, "@" + word);
                    end = _t.Length;
                }
                _doc.CodeBlocks.Add(_t[start..end]);
                _p = end;
                return;
            }
        }

        if (StructuredControls.Contains(word))
        {
            ParseControl(parent, word, start, i);
            return;
        }

        if (OpaqueControls.Contains(word) || (word == "using" && NextNonWs(i) == '('))
        {
            ParseOpaqueControl(parent, word, start, i);
            return;
        }

        if (word == "await")
        {
            var j = CSharpScanner.SkipWhitespace(_t, i);
            var end = CSharpScanner.SkipImplicitExpression(_t, j);
            parent.AddChild(new DesignNode { Kind = NodeKind.Expression, Text = _t[start..end], SourceStart = _base + start, SourceEnd = _base + end });
            _p = end;
            return;
        }

        var exprEnd = CSharpScanner.SkipImplicitExpression(_t, _p + 1);
        // Satzzeichen am Ende nicht mitnehmen (z. B. "@name." im Fließtext)
        parent.AddChild(new DesignNode { Kind = NodeKind.Expression, Text = _t[start..exprEnd], SourceStart = _base + start, SourceEnd = _base + exprEnd });
        _p = exprEnd;
    }

    private char NextNonWs(int i)
    {
        i = CSharpScanner.SkipWhitespace(_t, i);
        return i < _t.Length ? _t[i] : '\0';
    }

    private void ParseControl(DesignNode parent, string keyword, int start, int afterKeyword)
    {
        var i = afterKeyword;
        string header;
        var j = CSharpScanner.SkipWhitespace(_t, i);
        if (j < _t.Length && _t[j] == '(')
        {
            var end = CSharpScanner.SkipBalanced(_t, j, '(', ')');
            if (end < 0)
            {
                Diag(RazorDiagnosticCode.UnclosedBlock, start, _t.Length, "@" + keyword);
                AddRaw(parent, start, _t.Length);
                return;
            }
            header = keyword + " " + _t[j..end];
            i = end;
        }
        else
        {
            // Unerwartete Form: als Ausdruck behandeln
            var end = CSharpScanner.SkipImplicitExpression(_t, start + 1);
            parent.AddChild(new DesignNode { Kind = NodeKind.Expression, Text = _t[start..end], SourceStart = _base + start, SourceEnd = _base + end });
            _p = end;
            return;
        }

        var node = ParseControlBody(parent, header, start, i, isContinuation: false);
        if (node is null) return;

        if (keyword != "if") return;

        // else if / else
        while (true)
        {
            var k = CSharpScanner.SkipWhitespace(_t, _p);
            if (!IsWordAt(k, "else")) break;
            var segStart = k;
            k += 4;
            var m = CSharpScanner.SkipWhitespace(_t, k);
            string contHeader;
            if (IsWordAt(m, "if"))
            {
                var paren = CSharpScanner.SkipWhitespace(_t, m + 2);
                if (paren >= _t.Length || _t[paren] != '(') break;
                var end = CSharpScanner.SkipBalanced(_t, paren, '(', ')');
                if (end < 0) break;
                contHeader = "else if " + _t[paren..end];
                k = end;
            }
            else
            {
                contHeader = "else";
            }
            if (ParseControlBody(parent, contHeader, segStart, k, isContinuation: true) is null) break;
        }
    }

    private bool IsWordAt(int k, string word) =>
        k + word.Length <= _t.Length &&
        string.CompareOrdinal(_t, k, word, 0, word.Length) == 0 &&
        (k + word.Length == _t.Length || !CSharpScanner.IsIdentPart(_t[k + word.Length]));

    private DesignNode? ParseControlBody(DesignNode parent, string header, int start, int afterHeader, bool isContinuation)
    {
        var brace = CSharpScanner.SkipWhitespace(_t, afterHeader);
        if (brace >= _t.Length || _t[brace] != '{')
        {
            AddRaw(parent, start, afterHeader);
            return null;
        }
        var end = CSharpScanner.SkipBalanced(_t, brace, '{', '}');
        if (end < 0)
        {
            Diag(RazorDiagnosticCode.UnclosedBlock, start, _t.Length, "@" + header);
            end = _t.Length;
        }

        var node = new DesignNode
        {
            Kind = NodeKind.Control,
            Name = header.Split(' ', 2)[0] == "else" ? (header.StartsWith("else if", StringComparison.Ordinal) ? "else if" : "else") : header.Split(' ', 2)[0],
            Text = header,
            IsContinuation = isContinuation,
            SourceStart = _base + start,
            SourceEnd = _base + end,
        };
        parent.AddChild(node);

        var innerStart = brace + 1;
        var innerEnd = Math.Max(innerStart, end - 1);
        var inner = _t[innerStart..innerEnd];
        var sub = new RazorParser(inner, _base + innerStart, _result);
        sub._openTags.AddRange(_openTags);
        sub.ParseContent(node, null, topLevel: false, codeContext: true);
        _p = end;
        return node;
    }

    private void ParseOpaqueControl(DesignNode parent, string keyword, int start, int afterKeyword)
    {
        var i = afterKeyword;
        // Kopf in Klammern (switch, using)
        var j = CSharpScanner.SkipWhitespace(_t, i);
        if (j < _t.Length && _t[j] == '(')
        {
            var e = CSharpScanner.SkipBalanced(_t, j, '(', ')');
            if (e < 0) { AddRaw(parent, start, _t.Length); return; }
            i = e;
        }
        j = CSharpScanner.SkipWhitespace(_t, i);
        if (j >= _t.Length || _t[j] != '{') { AddRaw(parent, start, i); return; }
        var end = CSharpScanner.SkipBalanced(_t, j, '{', '}');
        if (end < 0) { Diag(RazorDiagnosticCode.UnclosedBlock, start, _t.Length, "@" + keyword); end = _t.Length; }

        // Fortsetzungen: catch/finally bzw. while(...);
        while (true)
        {
            var k = CSharpScanner.SkipWhitespace(_t, end);
            if (keyword == "try" && (IsWordAt(k, "catch") || IsWordAt(k, "finally")))
            {
                k += IsWordAt(k, "catch") ? 5 : 7;
                k = CSharpScanner.SkipWhitespace(_t, k);
                if (k < _t.Length && _t[k] == '(')
                {
                    var e = CSharpScanner.SkipBalanced(_t, k, '(', ')');
                    if (e < 0) break;
                    k = CSharpScanner.SkipWhitespace(_t, e);
                }
                if (k >= _t.Length || _t[k] != '{') break;
                var e2 = CSharpScanner.SkipBalanced(_t, k, '{', '}');
                if (e2 < 0) break;
                end = e2;
                continue;
            }
            if (keyword == "do" && IsWordAt(k, "while"))
            {
                var semi = _t.IndexOf(';', k);
                if (semi > 0) end = semi + 1;
            }
            break;
        }
        AddRaw(parent, start, end);
    }

    private void AddRaw(DesignNode parent, int start, int end)
    {
        parent.AddChild(new DesignNode { Kind = NodeKind.Code, Text = _t[start..end], SourceStart = _base + start, SourceEnd = _base + end });
        _p = end;
    }

    // ------------------------------------------------------------------
    // Kommentare
    // ------------------------------------------------------------------

    private void ParseRazorComment(DesignNode parent)
    {
        var start = _p;
        var end = _t.IndexOf("*@", _p + 2, StringComparison.Ordinal);
        if (end < 0)
        {
            Diag(RazorDiagnosticCode.UnclosedComment, start, _t.Length, "@*");
            end = _t.Length;
        }
        else end += 2;
        parent.AddChild(new DesignNode { Kind = NodeKind.Comment, Text = _t[start..end], SourceStart = _base + start, SourceEnd = _base + end });
        _p = end;
    }

    private void ParseHtmlComment(DesignNode parent)
    {
        var start = _p;
        var end = _t.IndexOf("-->", _p + 4, StringComparison.Ordinal);
        if (end < 0)
        {
            Diag(RazorDiagnosticCode.UnclosedComment, start, _t.Length, "<!--");
            end = _t.Length;
        }
        else end += 3;
        parent.AddChild(new DesignNode { Kind = NodeKind.Comment, Text = _t[start..end], SourceStart = _base + start, SourceEnd = _base + end });
        _p = end;
    }

    // ------------------------------------------------------------------
    // Elemente
    // ------------------------------------------------------------------

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ':';

    private string? ReadCloseTagName(out int end)
    {
        end = _p;
        var i = _p + 2;
        var s = i;
        while (i < _t.Length && IsNameChar(_t[i])) i++;
        if (i == s) return null;
        var name = _t[s..i];
        i = CSharpScanner.SkipWhitespace(_t, i);
        if (i < _t.Length && _t[i] == '>')
        {
            end = i + 1;
            return name;
        }
        return null;
    }

    private void ParseElement(DesignNode parent)
    {
        var start = _p;
        _p++; // <
        var ns = _p;
        while (!Eof && IsNameChar(Cur)) _p++;
        var name = _t[ns.._p];
        var node = DesignNode.Element(name);
        node.SourceStart = _base + start;
        parent.AddChild(node);

        var selfClosing = false;
        var closed = false;
        while (!Eof)
        {
            _p = CSharpScanner.SkipWhitespace(_t, _p);
            if (Eof) break;
            if (Cur == '>')
            {
                _p++;
                closed = true;
                break;
            }
            if (Cur == '/' && Peek() == '>')
            {
                _p += 2;
                selfClosing = true;
                closed = true;
                break;
            }
            if (!ParseAttribute(node))
            {
                _p++; // unbekanntes Zeichen überspringen
            }
        }

        if (!closed)
        {
            Diag(RazorDiagnosticCode.UnclosedTag, start, _t.Length, name);
            node.SourceEnd = _base + _t.Length;
            return;
        }

        node.SelfClosingInSource = selfClosing;
        if (selfClosing || node.IsVoid)
        {
            node.SourceEnd = _base + _p;
            return;
        }

        if (RawTextElements.Contains(name))
        {
            var closeIdx = _t.IndexOf("</" + name, _p, StringComparison.OrdinalIgnoreCase);
            var contentEnd = closeIdx < 0 ? _t.Length : closeIdx;
            var raw = _t[_p..contentEnd];
            node.IsRawContent = true;
            if (raw.Trim().Length > 0)
                node.AddChild(new DesignNode { Kind = NodeKind.Text, Text = raw, SourceStart = _base + _p, SourceEnd = _base + contentEnd });
            if (closeIdx < 0)
            {
                Diag(RazorDiagnosticCode.UnclosedTag, start, _t.Length, name);
                _p = _t.Length;
            }
            else
            {
                var gt = _t.IndexOf('>', closeIdx);
                _p = gt < 0 ? _t.Length : gt + 1;
            }
            node.SourceEnd = _base + _p;
            return;
        }

        _openTags.Add(name);
        var ok = ParseContent(node, name, topLevel: false, codeContext: false);
        _openTags.RemoveAt(_openTags.Count - 1);
        if (!ok) Diag(RazorDiagnosticCode.UnclosedTag, start, Math.Min(_t.Length, start + name.Length + 1), name);
        node.SourceEnd = _base + _p;
    }

    private bool ParseAttribute(DesignNode node)
    {
        var ns = _p;
        while (!Eof && !char.IsWhiteSpace(Cur) && Cur != '=' && Cur != '>' && !(Cur == '/' && Peek() == '>') && Cur != '"' && Cur != '\'')
        {
            if (Cur == '@' && Peek() == '(')
            {
                var e = CSharpScanner.SkipBalanced(_t, _p + 1, '(', ')');
                _p = e < 0 ? _t.Length : e;
                continue;
            }
            _p++;
        }
        if (_p == ns) return false;
        var name = _t[ns.._p];

        var save = _p;
        _p = CSharpScanner.SkipWhitespace(_t, _p);
        if (Cur != '=')
        {
            _p = save;
            node.Attributes.Add(new NodeAttribute(name, null));
            return true;
        }
        _p++;
        _p = CSharpScanner.SkipWhitespace(_t, _p);

        if (Cur is '"' or '\'')
        {
            var q = Cur;
            var vs = ++_p;
            while (!Eof && Cur != q)
            {
                if (Cur == '@' && Peek() == '(')
                {
                    var e = CSharpScanner.SkipBalanced(_t, _p + 1, '(', ')');
                    _p = e < 0 ? _t.Length : e;
                    continue;
                }
                // Impliziter Ausdruck mit Strings in Klammern, z. B. "@Assets["app.css"]" oder "@L["key"]"
                if (Cur == '@' && _p + 1 < _t.Length && CSharpScanner.IsIdentStart(_t[_p + 1]))
                {
                    var e = CSharpScanner.SkipImplicitExpression(_t, _p + 1);
                    _p = Math.Max(_p + 1, e);
                    continue;
                }
                _p++;
            }
            if (Eof)
            {
                Diag(RazorDiagnosticCode.UnclosedAttribute, ns, _t.Length, name);
                node.Attributes.Add(new NodeAttribute(name, _t[vs..], q));
                return true;
            }
            node.Attributes.Add(new NodeAttribute(name, _t[vs.._p], q));
            _p++;
            return true;
        }

        // Unquoted
        var us = _p;
        while (!Eof && !char.IsWhiteSpace(Cur) && Cur != '>' && !(Cur == '/' && Peek() == '>'))
        {
            if (Cur == '(' || (Cur == '@' && Peek() == '('))
            {
                var open = Cur == '(' ? _p : _p + 1;
                var e = CSharpScanner.SkipBalanced(_t, open, '(', ')');
                _p = e < 0 ? _t.Length : e;
                continue;
            }
            _p++;
        }
        node.Attributes.Add(new NodeAttribute(name, _t[us.._p], '"'));
        return true;
    }
}
