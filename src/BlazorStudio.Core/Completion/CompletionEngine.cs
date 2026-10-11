using System.Text.RegularExpressions;
using BlazorStudio.Core.Catalog;
using BlazorStudio.Core.Razor;
using BlazorStudio.Core.Styles;

namespace BlazorStudio.Core.Completion;

public enum CompletionKind { Element, Component, Attribute, Parameter, Event, Directive, Class, CssProperty, CssValue, Member, Method, Keyword, Snippet, Token }

public sealed record CompletionItem(
    string Label,
    CompletionKind Kind,
    string InsertText,
    string? Detail = null,
    string? Documentation = null,
    bool IsSnippet = false,
    string? SortText = null);

public sealed record CompletionResult(IReadOnlyList<CompletionItem> Items, int ReplaceLength);

public sealed class CompletionContext
{
    public required string Text { get; init; }

    public required int Offset { get; init; }

    /// <summary>Alle bekannten Komponenten (Blazor + Projekt).</summary>
    public IReadOnlyList<ComponentDefinition> Components { get; init; } = Array.Empty<ComponentDefinition>();

    public IReadOnlyList<CodeMember> Members { get; init; } = Array.Empty<CodeMember>();

    public CssIndex? Css { get; init; }
}

/// <summary>Kontextabhängige Vorschläge für .razor-Dateien.</summary>
public static partial class CompletionEngine
{
    private static readonly string[] RazorKeywords =
    {
        "page", "code", "using", "inject", "inherits", "layout", "implements", "attribute", "rendermode", "typeparam", "namespace",
        "if", "foreach", "for", "while", "switch", "try",
    };

    public static CompletionResult Complete(CompletionContext ctx)
    {
        var text = ctx.Text;
        var offset = Math.Clamp(ctx.Offset, 0, text.Length);
        var before = text[..offset];

        // In @code { } liefern wir nichts (Monaco-Wortvorschläge reichen dort).
        if (IsInsideCodeBlock(before)) return Members(ctx, WordBefore(before, allowAt: false), atPrefix: false);

        var lt = before.LastIndexOf('<');
        var gt = before.LastIndexOf('>');
        if (lt > gt)
        {
            var tagText = before[lt..];
            var m = TagNameRegex().Match(tagText);
            if (m.Success)
            {
                var closing = tagText.StartsWith("</", StringComparison.Ordinal);
                var prefix = m.Groups["name"].Value;
                return new CompletionResult(TagNames(ctx, prefix, closing, before), prefix.Length);
            }

            var tagName = OpenTagName().Match(tagText).Groups[1].Value;
            var value = AttrValueRegex().Match(tagText);
            if (value.Success)
            {
                var attr = value.Groups["attr"].Value;
                var current = value.Groups["val"].Value;
                return AttributeValues(ctx, tagName, attr, current);
            }

            var word = WordBefore(before, allowAt: true);
            var existing = ExistingAttributes(tagText);
            return new CompletionResult(AttributeNames(ctx, tagName, word, existing), word.Length);
        }

        // Razor-Übergang @...
        var w = WordBefore(before, allowAt: true);
        if (w.StartsWith('@'))
        {
            var items = RazorKeywords.Select(k => new CompletionItem("@" + k, CompletionKind.Keyword, KeywordSnippet(k), IsSnippet: true, SortText: "1" + k)).ToList();
            items.AddRange(Members(ctx, w[1..], atPrefix: true).Items);
            return new CompletionResult(items, w.Length);
        }

        return new CompletionResult(Array.Empty<CompletionItem>(), 0);
    }

    private static string KeywordSnippet(string k) => k switch
    {
        "code" => "@code {\n\t$0\n}",
        "if" => "@if (${1:condition})\n{\n\t$0\n}",
        "foreach" => "@foreach (var ${1:item} in ${2:items})\n{\n\t$0\n}",
        "for" => "@for (var ${1:i} = 0; ${1:i} < ${2:count}; ${1:i}++)\n{\n\t$0\n}",
        "while" => "@while (${1:condition})\n{\n\t$0\n}",
        "switch" => "@switch (${1:value})\n{\n\tcase ${2:1}:\n\t\t$0\n\t\tbreak;\n}",
        "try" => "@try\n{\n\t$0\n}\ncatch (Exception ex)\n{\n}",
        "page" => "@page \"/${1:route}\"",
        "inject" => "@inject ${1:Service} ${2:Name}",
        "using" => "@using ${1:Namespace}",
        "layout" => "@layout ${1:MainLayout}",
        "inherits" => "@inherits ${1:ComponentBase}",
        "rendermode" => "@rendermode ${1|InteractiveServer,InteractiveWebAssembly,InteractiveAuto|}",
        _ => "@" + k + " ",
    };

    private static bool IsInsideCodeBlock(string before)
    {
        var idx = Math.Max(before.LastIndexOf("@code", StringComparison.Ordinal), before.LastIndexOf("@functions", StringComparison.Ordinal));
        if (idx < 0) return false;
        var brace = before.IndexOf('{', idx);
        if (brace < 0) return false;
        var end = CSharpScanner.SkipBalanced(before + "\0", brace, '{', '}');
        return end < 0 || end > before.Length;
    }

    private static string WordBefore(string before, bool allowAt)
    {
        var i = before.Length;
        while (i > 0)
        {
            var c = before[i - 1];
            if (char.IsLetterOrDigit(c) || c is '_' or '-' or ':' || (allowAt && c == '@')) i--;
            else break;
        }
        return before[i..];
    }

    private static HashSet<string> ExistingAttributes(string tagText)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in AttrNameRegex().Matches(tagText)) set.Add(m.Groups[1].Value);
        return set;
    }

    // ------------------------------------------------------------------

    private static List<CompletionItem> TagNames(CompletionContext ctx, string prefix, bool closing, string before)
    {
        var items = new List<CompletionItem>();
        if (closing)
        {
            var open = FindUnclosedTag(before);
            if (open is not null) items.Add(new CompletionItem(open, CompletionKind.Element, open + ">", SortText: "0"));
            return items;
        }

        foreach (var tag in HtmlSchema.CommonTags)
        {
            var isVoid = Model.DesignNode.VoidElements.Contains(tag);
            items.Add(new CompletionItem(tag, CompletionKind.Element,
                isVoid ? tag + " $0/>" : tag + ">$0</" + tag + ">", "HTML", IsSnippet: true, SortText: "2" + tag));
        }

        foreach (var c in ctx.Components.Where(c => c.Source is ComponentSource.Blazor or ComponentSource.Project))
        {
            var required = c.Parameters.Where(p => p.IsRequired).ToList();
            var attrs = string.Concat(required.Select((p, i) => $" {p.Name}=\"${{{i + 1}}}\""));
            var insert = c.AcceptsChildren
                ? $"{c.TagName}{attrs}>$0</{c.TagName}>"
                : $"{c.TagName}{attrs} $0/>";
            var doc = c.Parameters.Count == 0 ? null : string.Join("\n", c.Parameters.Select(p => $"{p.Name}: {p.Type}"));
            items.Add(new CompletionItem(c.TagName, CompletionKind.Component, insert,
                c.Source == ComponentSource.Project ? "Komponente · " + (c.Keywords.Length > 0 ? c.Keywords : "Projekt") : "Blazor", doc, IsSnippet: true,
                SortText: (c.Source == ComponentSource.Project ? "0" : "1") + c.TagName));
        }
        return items;
    }

    private static string? FindUnclosedTag(string before)
    {
        var stack = new Stack<string>();
        foreach (Match m in AnyTagRegex().Matches(before))
        {
            var name = m.Groups["name"].Value;
            if (m.Value.EndsWith("/>", StringComparison.Ordinal) || Model.DesignNode.VoidElements.Contains(name)) continue;
            if (m.Groups["close"].Success)
            {
                while (stack.Count > 0 && stack.Pop() != name) { }
            }
            else stack.Push(name);
        }
        return stack.Count > 0 ? stack.Peek() : null;
    }

    private static List<CompletionItem> AttributeNames(CompletionContext ctx, string tag, string word, HashSet<string> existing)
    {
        var items = new List<CompletionItem>();
        var comp = ctx.Components.FirstOrDefault(c => c.TagName == tag && c.Source is ComponentSource.Blazor or ComponentSource.Project);

        if (comp is not null)
        {
            foreach (var p in comp.Parameters)
            {
                if (existing.Contains(p.Name)) continue;
                var insert = p.Editor switch
                {
                    ValueEditor.Bool => $"{p.Name}=\"${{1|true,false|}}\"",
                    ValueEditor.Enum when p.Options is { Count: > 0 } => $"{p.Name}=\"${{1|{string.Join(',', p.Options.Select(o => (p.EnumPrefix ?? "") + o))}|}}\"",
                    _ => $"{p.Name}=\"$1\"",
                };
                items.Add(new CompletionItem(p.Name, p.Editor == ValueEditor.Callback ? CompletionKind.Event : CompletionKind.Parameter, insert,
                    p.Type, p.Description, IsSnippet: true, SortText: "0" + p.Name));
                if (p.IsBindable && !existing.Contains("@bind-" + p.Name))
                    items.Add(new CompletionItem("@bind-" + p.Name, CompletionKind.Directive, $"@bind-{p.Name}=\"$1\"", "two-way @bind", IsSnippet: true, SortText: "0@" + p.Name));
            }
        }
        else
        {
            foreach (var a in HtmlSchema.AttributesFor(tag))
            {
                if (existing.Contains(a.Name)) continue;
                var insert = a.Editor == ValueEditor.Bool ? a.Name : a.Options is { Count: > 0 }
                    ? $"{a.Name}=\"${{1|{string.Join(',', a.Options)}|}}\""
                    : $"{a.Name}=\"$1\"";
                items.Add(new CompletionItem(a.Name, CompletionKind.Attribute, insert, "HTML", IsSnippet: true, SortText: "1" + a.Name));
            }
            foreach (var e in HtmlSchema.Events)
            {
                var n = "@" + e.Name;
                if (existing.Contains(n)) continue;
                items.Add(new CompletionItem(n, CompletionKind.Event, $"{n}=\"$1\"", e.ArgsType, IsSnippet: true, SortText: "2" + e.Name));
            }
            foreach (var d in HtmlSchema.DirectiveAttributes)
            {
                if (existing.Contains(d)) continue;
                items.Add(new CompletionItem(d, CompletionKind.Directive, $"{d}=\"$1\"", "Blazor", IsSnippet: true, SortText: "3" + d));
            }
        }

        if (!existing.Contains("class")) items.Add(new CompletionItem("class", CompletionKind.Attribute, "class=\"$1\"", IsSnippet: true, SortText: "0class"));
        if (!existing.Contains("style")) items.Add(new CompletionItem("style", CompletionKind.Attribute, "style=\"$1\"", IsSnippet: true, SortText: "0style"));
        return items;
    }

    private static CompletionResult AttributeValues(CompletionContext ctx, string tag, string attr, string current)
    {
        if (attr.Equals("class", StringComparison.OrdinalIgnoreCase))
        {
            var word = current.Split(' ').Last();
            var items = (ctx.Css?.Search(word, 200) ?? Enumerable.Empty<CssClassInfo>())
                .Select(c => new CompletionItem(c.Name, CompletionKind.Class, c.Name, c.SourceFile, c.Preview, SortText: c.Name.StartsWith(word, StringComparison.OrdinalIgnoreCase) ? "0" + c.Name : "1" + c.Name))
                .ToList();
            return new CompletionResult(items, word.Length);
        }

        if (attr.Equals("style", StringComparison.OrdinalIgnoreCase))
        {
            var segment = current.Split(';').Last();
            var colon = segment.IndexOf(':');
            if (colon < 0)
            {
                var word = segment.TrimStart();
                var items = CssCatalog.Properties.Select(p => new CompletionItem(p.Name, CompletionKind.CssProperty, p.Name + ": $0", p.Group, IsSnippet: true)).ToList();
                return new CompletionResult(items, word.Length);
            }
            var prop = segment[..colon].Trim();
            var valuePart = segment[(colon + 1)..].TrimStart();
            var values = CssCatalog.ValuesFor(prop).Select(v => new CompletionItem(v, CompletionKind.CssValue, v)).ToList();
            if (ctx.Css is not null)
                values.AddRange(ctx.Css.Tokens.Select(t => new CompletionItem($"var({t.Name})", CompletionKind.Token, $"var({t.Name})", t.Value, t.SourceFile)));
            return new CompletionResult(values, valuePart.Length);
        }

        var comp = ctx.Components.FirstOrDefault(c => c.TagName == tag && c.Source is ComponentSource.Blazor or ComponentSource.Project);
        var param = comp?.FindParameter(attr.StartsWith("@bind-", StringComparison.Ordinal) ? attr[6..] : attr);
        var isEvent = attr.StartsWith("@on", StringComparison.Ordinal) || param?.Editor == ValueEditor.Callback;

        if (isEvent)
        {
            var items = ctx.Members.Where(m => m.Kind == CodeMemberKind.Method)
                .Select(m => new CompletionItem(m.Name, CompletionKind.Method, m.Name, m.Signature, m.Summary)).ToList();
            return new CompletionResult(items, current.Length);
        }

        if (param is { Editor: ValueEditor.Enum, Options: not null })
            return new CompletionResult(param.Options.Select(o => new CompletionItem((param.EnumPrefix ?? "") + o, CompletionKind.CssValue, (param.EnumPrefix ?? "") + o)).ToList(), current.Length);
        if (param is { Editor: ValueEditor.Bool })
            return new CompletionResult(new[] { new CompletionItem("true", CompletionKind.Keyword, "true"), new CompletionItem("false", CompletionKind.Keyword, "false") }, current.Length);

        var attrDef = HtmlSchema.AttributesFor(tag).FirstOrDefault(a => a.Name.Equals(attr, StringComparison.OrdinalIgnoreCase));
        if (attrDef?.Options is { Count: > 0 })
            return new CompletionResult(attrDef.Options.Select(o => new CompletionItem(o, CompletionKind.CssValue, o)).ToList(), current.Length);

        if (attr.StartsWith('@') || param is not null || current.StartsWith('@'))
        {
            var prefix = current.TrimStart('@');
            var items = ctx.Members.Where(m => m.Kind != CodeMemberKind.Method)
                .Select(m => new CompletionItem(m.Name, CompletionKind.Member, (current.StartsWith('@') ? "@" : "") + m.Name, m.Type, m.Summary)).ToList();
            return new CompletionResult(items, current.Length);
        }

        return new CompletionResult(Array.Empty<CompletionItem>(), 0);
    }

    private static CompletionResult Members(CompletionContext ctx, string prefix, bool atPrefix)
    {
        var items = ctx.Members.Select(m => new CompletionItem(
            m.Name,
            m.Kind == CodeMemberKind.Method ? CompletionKind.Method : CompletionKind.Member,
            (atPrefix ? "@" : "") + m.Name + (m.Kind == CodeMemberKind.Method ? "($0)" : ""),
            m.Kind == CodeMemberKind.Method ? m.Type + " " + m.Name + m.Signature : m.Type,
            m.Summary,
            IsSnippet: m.Kind == CodeMemberKind.Method,
            SortText: "0" + m.Name)).ToList();
        return new CompletionResult(items, prefix.Length + (atPrefix ? 1 : 0));
    }

    [GeneratedRegex(@"^</?(?<name>[\w.\-]*)$")]
    private static partial Regex TagNameRegex();

    [GeneratedRegex(@"^<([\w.\-:]+)")]
    private static partial Regex OpenTagName();

    [GeneratedRegex(@"(?<attr>[@\w.\-:]+)\s*=\s*[""'](?<val>[^""']*)$")]
    private static partial Regex AttrValueRegex();

    [GeneratedRegex(@"\s([@\w.\-:]+)(?=\s*=|\s|/?>|$)")]
    private static partial Regex AttrNameRegex();

    [GeneratedRegex(@"<(?<close>/)?(?<name>[A-Za-z][\w.\-]*)[^<>]*?/?>")]
    private static partial Regex AnyTagRegex();
}
