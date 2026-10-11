using System.Net;
using System.Text.RegularExpressions;
using BlazorStudio.App.Services;
using BlazorStudio.Core.Model;
using BlazorStudio.Core.Styles;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace BlazorStudio.App.Components;

/// <summary>
/// Rendert den Dokumentbaum als echtes HTML in die Designer-Fläche.
/// Bearbeitbare Knoten bekommen data-bs-*-Attribute, die das JavaScript für Auswahl und Drag &amp; Drop nutzt.
/// </summary>
public sealed partial class CanvasRenderer(Workspace ws, Localizer l)
{
    private const int MaxComponentDepth = 4;

    private static readonly HashSet<string> TextTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "h1", "h2", "h3", "h4", "h5", "h6", "span", "a", "button", "label", "li", "td", "th", "strong", "em", "b", "i",
        "small", "code", "blockquote", "figcaption", "dt", "dd", "summary", "legend", "option", "caption", "mark", "q", "cite",
    };

    private static readonly HashSet<string> PassThroughAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "class", "style", "type", "placeholder", "value", "alt", "title", "width", "height", "rows", "cols", "colspan", "rowspan",
        "disabled", "checked", "selected", "multiple", "open", "controls", "poster", "role", "aria-label", "min", "max", "step",
        "start", "reversed", "for", "dir", "lang", "loading",
        // SVG
        "viewbox", "d", "fill", "stroke", "stroke-width", "stroke-linecap", "stroke-linejoin", "cx", "cy", "r", "rx", "ry", "x", "y",
        "x1", "x2", "y1", "y2", "points", "xmlns", "transform", "opacity", "fill-rule", "clip-rule", "preserveaspectratio",
    };

    private sealed record Ctx(
        bool Selectable,
        IReadOnlyDictionary<string, string>? Params,
        IReadOnlyList<DesignNode>? ChildContent,
        int Depth,
        ImmutableStack<string> Components);

    private sealed class ImmutableStack<T>(IEnumerable<T> items)
    {
        private readonly List<T> _items = items.ToList();

        public static ImmutableStack<T> Empty { get; } = new(Array.Empty<T>());

        public bool Contains(T item) => _items.Contains(item);

        public ImmutableStack<T> Push(T item) => new(_items.Append(item));
    }

    public RenderFragment Render(RazorDocument doc) => b =>
    {
        var ctx = new Ctx(true, null, null, 0, ImmutableStack<string>.Empty);
        var nodes = doc.Root.Children;
        if (nodes.Count(n => n.Kind is not (NodeKind.Comment or NodeKind.Text)) == 0)
        {
            b.OpenElement(0, "div");
            b.AddAttribute(1, "class", "bs-page-empty");
            b.AddMarkupContent(2, $"<b>{WebUtility.HtmlEncode(l["canvas.emptyTitle"])}</b>{WebUtility.HtmlEncode(l["canvas.emptyText"])}");
            b.CloseElement();
        }
        RenderChildren(b, nodes, ctx);
    };

    private void RenderChildren(RenderTreeBuilder b, IReadOnlyList<DesignNode> nodes, Ctx ctx)
    {
        for (var i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            // In Vorschauen von Komponenten nur den ersten Zweig eines if/else zeigen
            if (!ctx.Selectable && n.IsContinuation) continue;
            b.OpenRegion(i);
            RenderNode(b, n, ctx);
            b.CloseRegion();
        }
    }

    private void RenderNode(RenderTreeBuilder b, DesignNode n, Ctx ctx)
    {
        switch (n.Kind)
        {
            case NodeKind.Text:
                b.AddMarkupContent(0, n.Text.Replace("@@", "@"));
                break;
            case NodeKind.Expression:
                RenderExpression(b, n, ctx);
                break;
            case NodeKind.Comment:
                break;
            case NodeKind.Code:
                if (!ctx.Selectable) break;
                b.OpenElement(0, "div");
                Mark(b, n, ctx, "C#", container: false);
                b.AddAttribute(10, "class", "bs-code");
                b.AddContent(11, Preview(n.Text));
                b.CloseElement();
                break;
            case NodeKind.Control:
                if (!ctx.Selectable)
                {
                    RenderChildren(b, n.Children, ctx);
                    break;
                }
                b.OpenElement(0, "div");
                Mark(b, n, ctx, "@" + n.Text, container: true);
                b.AddAttribute(10, "class", "bs-control");
                if (n.Children.Count == 0) b.AddAttribute(11, "data-bs-empty", l["canvas.dropHere"]);
                b.OpenElement(12, "span");
                b.AddAttribute(13, "class", "bs-control-label");
                b.AddContent(14, "@" + n.Text);
                b.CloseElement();
                RenderChildren(b, n.Children, ctx);
                b.CloseElement();
                break;
            case NodeKind.Component:
                RenderComponent(b, n, ctx);
                break;
            default:
                RenderElement(b, n, ctx, n.Name);
                break;
        }
    }

    private void RenderExpression(RenderTreeBuilder b, DesignNode n, Ctx ctx)
    {
        var expr = n.Text.Trim();
        var name = expr.TrimStart('@').Trim('(', ')');

        if (ctx.Params is not null)
        {
            if ((name == "ChildContent" || name == "Body") && ctx.ChildContent is not null)
            {
                RenderChildren(b, ctx.ChildContent, ctx with { Selectable = true, Params = null, ChildContent = null });
                return;
            }
            if (ctx.Params.TryGetValue(name, out var value))
            {
                if (value.StartsWith('@')) Chip(b, value);
                else b.AddContent(0, value);
                return;
            }
        }
        if (name is "ChildContent" or "Body")
        {
            b.OpenElement(0, "span");
            b.AddAttribute(1, "class", "bs-meta");
            b.AddContent(2, "@" + name);
            b.CloseElement();
            return;
        }
        Chip(b, expr);
    }

    private static void Chip(RenderTreeBuilder b, string text)
    {
        b.OpenElement(0, "span");
        b.AddAttribute(1, "class", "bs-expr");
        b.AddContent(2, text.Length > 48 ? text[..47] + "…" : text);
        b.CloseElement();
    }

    private void Mark(RenderTreeBuilder b, DesignNode n, Ctx ctx, string? label = null, bool? container = null, bool text = false)
    {
        if (!ctx.Selectable) return;
        b.AddAttribute(1, "data-bs-id", n.Id);
        b.AddAttribute(2, "data-bs-label", label ?? LabelOf(n));
        b.AddAttribute(3, "draggable", "true");
        if (container ?? n.CanHaveChildren) b.AddAttribute(4, "data-bs-container", "1");
        if (text) b.AddAttribute(5, "data-bs-text", "1");
    }

    // ------------------------------------------------------------------
    // HTML-Elemente
    // ------------------------------------------------------------------

    private void RenderElement(RenderTreeBuilder b, DesignNode n, Ctx ctx, string tag, string? extraClass = null, IEnumerable<KeyValuePair<string, object>>? extra = null)
    {
        var lower = tag.ToLowerInvariant();
        if (lower is "script" or "head" or "meta" or "link" or "base" or "title") return;
        if (lower == "style")
        {
            var css = string.Concat(n.Children.Select(c => c.Text));
            b.OpenElement(0, "style");
            b.AddMarkupContent(1, CssScoper.Scope(css, Workspace.CanvasScope));
            b.CloseElement();
            return;
        }
        if (lower is "html" or "body")
        {
            RenderChildren(b, n.Children, ctx);
            return;
        }
        if (lower is "headoutlet" or "routes") return;

        var isText = TextTags.Contains(lower) && (n.Children.Count == 0 || n.IsTextOnly);
        b.OpenElement(0, SafeTag(lower));
        Mark(b, n, ctx, text: isText && ctx.Selectable);

        var attrs = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        string? cls = null;
        foreach (var a in n.Attributes)
        {
            var name = a.Name.ToLowerInvariant();
            if (name.StartsWith('@') || name.StartsWith("on", StringComparison.Ordinal) && !PassThroughAttributes.Contains(name)) continue;
            var value = a.Value is null ? null : Substitute(a.Value, ctx);
            switch (name)
            {
                case "class":
                    cls = StaticPart(value);
                    continue;
                case "style":
                    var st = StaticPart(value);
                    if (!string.IsNullOrWhiteSpace(st)) attrs["style"] = st;
                    continue;
                case "href":
                    if (value is not null) attrs["data-href"] = value;
                    continue;
                case "src":
                    var url = ws.PreviewUrl(value);
                    if (url is not null) attrs["src"] = url;
                    else if (lower == "img") attrs["data-bs-placeholder"] = "1";
                    continue;
                case "id" or "name" or "action" or "target" or "form":
                    continue;
            }
            if (!PassThroughAttributes.Contains(name) && !name.StartsWith("data-", StringComparison.Ordinal) && !name.StartsWith("aria-", StringComparison.Ordinal)) continue;
            if (value is null) attrs[name] = true;
            else if (!value.Contains('@')) attrs[name] = value;
        }
        if (lower == "img" && !n.HasAttribute("src")) attrs["data-bs-placeholder"] = "1";
        if (lower == "button") attrs["type"] = "button";
        if (lower is "input" or "textarea" or "select" or "button" or "a") attrs["tabindex"] = "-1";
        if (lower is "input" or "textarea") attrs["readonly"] = true;
        if (extra is not null) foreach (var kv in extra) attrs[kv.Key] = kv.Value;

        var finalClass = string.Join(' ', new[] { cls, extraClass }.Where(c => !string.IsNullOrWhiteSpace(c)));
        if (finalClass.Length > 0) attrs["class"] = finalClass;

        if (ctx.Selectable && n.CanHaveChildren && n.Children.Count == 0 && !TextTags.Contains(lower) && lower is not ("textarea" or "select" or "svg" or "video" or "audio" or "iframe" or "canvas"))
            attrs["data-bs-empty"] = l["canvas.dropHere"];

        foreach (var kv in attrs) b.AddAttribute(20, kv.Key, kv.Value);

        if (!n.IsVoid && !n.IsRawContent)
        {
            if (lower == "textarea")
                b.AddContent(40, string.Concat(n.Children.Where(c => c.Kind == NodeKind.Text).Select(c => WebUtility.HtmlDecode(c.Text))));
            else
                RenderChildren(b, n.Children, ctx);
        }
        b.CloseElement();
    }

    private static string SafeTag(string tag) => TagRegex().IsMatch(tag) ? tag : "div";

    /// <summary>Entfernt Razor-Ausdrücke aus class/style (nur der statische Teil wird gezeigt).</summary>
    private static string? StaticPart(string? value)
    {
        if (value is null) return null;
        if (!value.Contains('@')) return value;
        var s = ExplicitExprRegex().Replace(value, " ");
        s = ImplicitExprRegex().Replace(s, " ");
        return string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>In Komponenten-Vorschauen @Param durch den übergebenen Wert ersetzen.</summary>
    private static string Substitute(string value, Ctx ctx)
    {
        if (ctx.Params is null || !value.Contains('@')) return value;
        return ImplicitExprRegex().Replace(value, m =>
        {
            var key = m.Value.TrimStart('@');
            return ctx.Params.TryGetValue(key, out var v) && !v.StartsWith('@') ? v : m.Value;
        });
    }

    private static string Preview(string code)
    {
        var lines = code.Replace("\r\n", "\n").Split('\n').Select(x => x.TrimEnd()).Where(x => x.Length > 0).Take(4).ToList();
        return string.Join('\n', lines) + (code.Count(c => c == '\n') > 4 ? "\n…" : "");
    }

    // ------------------------------------------------------------------
    // Komponenten
    // ------------------------------------------------------------------

    private void RenderComponent(RenderTreeBuilder b, DesignNode n, Ctx ctx)
    {
        var name = n.Name;
        var attrs = n.Attributes.ToDictionary(a => a.Name, a => a.Value ?? "true", StringComparer.Ordinal);

        switch (name)
        {
            case "InputText":
                RenderElement(b, n, ctx, "input", extra: Attrs(("type", "text"), ("value", BoundPreview(attrs, "Value"))));
                return;
            case "InputNumber":
                RenderElement(b, n, ctx, "input", extra: Attrs(("type", "number"), ("value", BoundPreview(attrs, "Value"))));
                return;
            case "InputDate":
                RenderElement(b, n, ctx, "input", extra: Attrs(("type", "date")));
                return;
            case "InputCheckbox":
                RenderElement(b, n, ctx, "input", extra: Attrs(("type", "checkbox")));
                return;
            case "InputFile":
                RenderElement(b, n, ctx, "input", extra: Attrs(("type", "file")));
                return;
            case "InputTextArea":
                RenderElement(b, n, ctx, "textarea");
                return;
            case "InputSelect":
                RenderElement(b, n, ctx, "select");
                return;
            case "InputRadio":
                RenderElement(b, n, ctx, "input", extra: Attrs(("type", "radio")));
                return;
            case "EditForm":
                RenderElement(b, n, ctx, "form");
                return;
            case "NavLink":
                RenderElement(b, n, ctx, "a");
                return;
            case "InputRadioGroup" or "CascadingValue" or "CascadingAuthenticationState" or "AuthorizeView" or "Authorized" or "NotAuthorized"
                or "Authorizing" or "ErrorBoundary" or "ChildContent" or "ErrorContent" or "Router" or "Found" or "NotFound" or "LayoutView":
                RenderWrapper(b, n, ctx, name);
                return;
            case "PageTitle" or "HeadContent" or "SectionContent" or "SectionOutlet" or "DataAnnotationsValidator" or "ValidationSummary"
                or "ValidationMessage" or "FocusOnNavigate" or "RouteView" or "AntiforgeryToken" or "HeadOutlet" or "ImportMap":
                RenderMeta(b, n, ctx);
                return;
        }

        if (ws.DefinitionForNode(n) is { Source: Core.Catalog.ComponentSource.Library } lib)
        {
            RenderLibrary(b, n, ctx, lib);
            return;
        }

        // Projekt-Komponente: echte Vorschau aus der .razor-Datei
        if (ctx.Depth < MaxComponentDepth && !ctx.Components.Contains(name) && ws.ComponentPreview(name) is { } preview)
        {
            var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var a in n.Attributes)
            {
                var key = a.Name.StartsWith("@bind-", StringComparison.Ordinal) ? a.Name[6..] : a.Name;
                if (key.StartsWith('@')) continue;
                var v = a.Value ?? "true";
                parameters[key] = a.Name.StartsWith("@bind-", StringComparison.Ordinal) && !v.StartsWith('@') ? "@" + v : PreviewValue(v);
            }

            b.OpenElement(0, "div");
            var def = ws.DefinitionForNode(n);
            Mark(b, n, ctx, container: def?.AcceptsChildren ?? n.Children.Count > 0);
            b.AddAttribute(10, "class", "bs-comp-host");
            if (ctx.Selectable)
            {
                b.OpenElement(11, "span");
                b.AddAttribute(12, "class", "bs-comp-label");
                b.AddContent(13, name);
                b.CloseElement();
            }
            var inner = new Ctx(false, parameters, ctx.Selectable ? n.Children : ctx.ChildContent, ctx.Depth + 1, ctx.Components.Push(name));
            // Kinder ohne @ChildContent-Platzhalter trotzdem zeigen
            var usesChild = preview.AllNodes().Any(x => x.Kind == NodeKind.Expression && x.Text.Contains("ChildContent", StringComparison.Ordinal));
            RenderChildren(b, preview.Root.Children, inner);
            if (!usesChild && n.Children.Count > 0) RenderChildren(b, n.Children, ctx);
            b.CloseElement();
            return;
        }

        RenderWrapper(b, n, ctx, name);
    }

    /// <summary>
    /// Bibliotheks-Komponenten (MudBlazor, Radzen …) lassen sich ohne Kompilieren nicht echt rendern –
    /// daher eine HTML-Annäherung nach Name und Parametern (Label, Text, Variant, Color …).
    /// Exakt sieht man sie in der Live-Ansicht.
    /// </summary>
    private void RenderLibrary(RenderTreeBuilder b, DesignNode n, Ctx ctx, Core.Catalog.ComponentDefinition def)
    {
        var name = n.Name;
        string? Attr(string a) => n.GetAttribute(a) is { } v && !v.StartsWith('@') ? v : null;
        bool Is(params string[] parts) => parts.Any(p => name.Contains(p, StringComparison.Ordinal));
        var label = Attr("Label") ?? Attr("Text") ?? Attr("Title") ?? Attr("Placeholder");
        var color = (Attr("Color") ?? "").Split('.').Last().ToLowerInvariant();
        var variant = (Attr("Variant") ?? "").Split('.').Last().ToLowerInvariant();
        var userClass = StaticPart(n.GetAttribute("Class") ?? n.GetAttribute("class"));
        var userStyle = StaticPart(n.GetAttribute("Style") ?? n.GetAttribute("style"));

        void Open(string tag, string cls, bool container, string? extraStyle = null)
        {
            b.OpenElement(0, tag);
            Mark(b, n, ctx, container: container, text: !container && def.AcceptsChildren);
            b.AddAttribute(10, "class", ("bs-lib " + cls + " " + (color.Length > 0 ? "c-" + color : "") + " " + (variant.Length > 0 ? "v-" + variant : "") + " " + userClass).Trim());
            var style = (extraStyle ?? "") + (userStyle ?? "");
            if (!string.IsNullOrWhiteSpace(style)) b.AddAttribute(11, "style", style);
            b.AddAttribute(12, "data-bs-lib", def.Library ?? "");
            if (container && n.Children.Count == 0 && ctx.Selectable) b.AddAttribute(13, "data-bs-empty", name);
        }

        void Children()
        {
            if (n.Children.Count > 0) RenderChildren(b, n.Children, ctx);
        }

        if (Is("Button", "Fab"))
        {
            Open("button", "lib-btn", container: false);
            b.AddAttribute(20, "type", "button");
            if (n.Children.Count > 0) Children();
            else b.AddContent(21, label ?? (Is("Icon") ? "★" : name));
        }
        else if (Is("Switch", "Checkbox", "CheckBox", "Radio"))
        {
            Open("label", "lib-check", container: false);
            b.OpenElement(20, "input");
            b.AddAttribute(21, "type", Is("Radio") ? "radio" : "checkbox");
            b.AddAttribute(22, "tabindex", "-1");
            if (Is("Switch")) b.AddAttribute(23, "class", "lib-switch");
            b.CloseElement();
            b.AddContent(24, label ?? "");
            Children();
        }
        else if (Is("TextField", "TextBox", "Input", "NumericField", "Numeric", "Picker", "Autocomplete", "Select", "DropDown", "Dropdown", "ComboBox", "TextArea"))
        {
            Open("div", "lib-field", container: false);
            if (label is not null)
            {
                b.OpenElement(20, "label");
                b.AddContent(21, label);
                b.CloseElement();
            }
            b.OpenElement(22, Is("TextArea") || Attr("Lines") is not null ? "textarea" : "input");
            b.AddAttribute(23, "readonly", true);
            b.AddAttribute(24, "tabindex", "-1");
            b.AddAttribute(25, "placeholder", Attr("Placeholder") ?? (Is("Select", "DropDown", "Dropdown", "Combo") ? "▾" : ""));
            b.CloseElement();
        }
        else if (Is("Typography") || name.EndsWith("Text", StringComparison.Ordinal) || name.EndsWith("Heading", StringComparison.Ordinal))
        {
            var typo = (Attr("Typo") ?? Attr("TextStyle") ?? "").Split('.').Last().ToLowerInvariant();
            var tag = typo is "h1" or "h2" or "h3" or "h4" or "h5" or "h6" ? typo : typo.StartsWith("subtitle", StringComparison.Ordinal) ? "h6" : "p";
            Open(tag, "lib-text", container: false);
            if (n.Children.Count > 0) Children();
            else b.AddContent(20, label ?? name);
        }
        else if (Is("Card", "Paper", "Panel", "Alert", "Expansion"))
        {
            Open("div", Is("Alert") ? "lib-alert" : "lib-card", container: true);
            if (label is not null && Is("Expansion", "Panel"))
            {
                b.OpenElement(20, "div");
                b.AddAttribute(21, "class", "lib-card-title");
                b.AddContent(22, label);
                b.CloseElement();
            }
            Children();
        }
        else if (Is("Stack", "Flex", "Row", "Toolbar", "AppBar", "Group"))
        {
            var row = Attr("Row") is "true" || Is("Row", "Toolbar", "AppBar", "Group") || n.HasAttribute("Row") && n.GetAttribute("Row") is null;
            var gap = int.TryParse(Attr("Spacing") ?? Attr("Gap"), out var g) ? g * 4 : 12;
            Open("div", (Is("AppBar", "Toolbar") ? "lib-bar " : "") + "lib-stack", container: true,
                $"display:flex;flex-direction:{(row ? "row" : "column")};gap:{gap}px;{(row ? "align-items:center;" : "")}");
            Children();
        }
        else if (name.EndsWith("Grid", StringComparison.Ordinal) && !Is("Data"))
        {
            Open("div", "lib-grid", container: true);
            Children();
        }
        else if (name.EndsWith("Item", StringComparison.Ordinal) && Is("Grid", "Item"))
        {
            var span = int.TryParse(Attr("md") ?? Attr("xs") ?? Attr("sm"), out var s) ? s : 12;
            Open("div", "lib-item", container: true, $"grid-column: span {Math.Clamp(span, 1, 12)};");
            Children();
        }
        else if (Is("Container", "Layout", "Main"))
        {
            Open("div", "lib-container", container: true);
            Children();
        }
        else if (Is("Divider"))
        {
            Open("hr", "lib-divider", container: false);
        }
        else if (Is("Spacer"))
        {
            Open("div", "lib-spacer", container: false);
        }
        else if (Is("Icon", "Avatar"))
        {
            Open("span", Is("Avatar") ? "lib-avatar" : "lib-icon", container: false);
            b.AddContent(20, Is("Avatar") ? (n.InnerText.Trim() is { Length: > 0 } t ? t[..1] : "A") : "★");
        }
        else if (Is("Chip", "Badge", "Tag"))
        {
            Open("span", "lib-chip", container: false);
            if (n.Children.Count > 0) Children();
            else b.AddContent(20, label ?? name);
        }
        else if (Is("Table", "DataGrid", "Grid"))
        {
            Open("div", "lib-table", container: false);
            b.OpenElement(20, "table");
            for (var r = 0; r < 4; r++)
            {
                b.OpenElement(21, "tr");
                for (var c = 0; c < 4; c++)
                {
                    b.OpenElement(22, r == 0 ? "th" : "td");
                    b.AddContent(23, r == 0 ? $"{name} {c + 1}" : "…");
                    b.CloseElement();
                }
                b.CloseElement();
            }
            b.CloseElement();
        }
        else if (Is("Link", "NavLink", "NavItem"))
        {
            Open("a", "lib-link", container: false);
            if (n.Children.Count > 0) Children();
            else b.AddContent(20, label ?? name);
        }
        else if (Is("Image", "Img"))
        {
            Open("img", "lib-image", container: false);
            var url = ws.PreviewUrl(Attr("Src"));
            if (url is not null) b.AddAttribute(20, "src", url);
            else b.AddAttribute(21, "data-bs-placeholder", "1");
        }
        else if (Is("Progress", "Skeleton", "Loader", "Spinner"))
        {
            Open("div", Is("Skeleton") ? "lib-skeleton" : "lib-progress", container: false);
        }
        else if (Is("Tabs", "Tab", "Nav", "Menu", "List", "Drawer", "Dialog", "Popover", "Tooltip", "Form", "Field", "Carousel", "Timeline", "Breadcrumbs"))
        {
            Open("div", "lib-box", container: true);
            b.OpenElement(20, "span");
            b.AddAttribute(21, "class", "bs-comp-label");
            b.AddContent(22, name + (label is not null ? ": " + label : ""));
            b.CloseElement();
            Children();
        }
        else
        {
            RenderWrapper(b, n, ctx, name);
            return;
        }
        b.CloseElement();
    }

    private static string PreviewValue(string v)
    {
        var t = v.Trim();
        if (t.StartsWith('@')) return t;
        // Enum-Werte und C#-Literale etwas lesbarer
        return t;
    }

    private static string? BoundPreview(Dictionary<string, string> attrs, string param)
    {
        if (attrs.TryGetValue("@bind-" + param, out var b)) return "@" + b.TrimStart('@');
        return attrs.TryGetValue(param, out var v) ? v : null;
    }

    private static IEnumerable<KeyValuePair<string, object>> Attrs(params (string Key, string? Value)[] items) =>
        items.Where(i => i.Value is not null && !i.Value.Contains('@')).Select(i => new KeyValuePair<string, object>(i.Key, i.Value!));

    private void RenderWrapper(RenderTreeBuilder b, DesignNode n, Ctx ctx, string label)
    {
        b.OpenElement(0, "div");
        Mark(b, n, ctx, container: true);
        b.AddAttribute(10, "class", "bs-comp");
        var style = StaticPart(n.GetAttribute("style"));
        if (!string.IsNullOrWhiteSpace(style)) b.AddAttribute(11, "style", style);
        if (n.Children.Count == 0 && ctx.Selectable) b.AddAttribute(12, "data-bs-empty", l["canvas.dropHere"]);
        b.OpenElement(13, "span");
        b.AddAttribute(14, "class", "bs-comp-label");
        b.AddContent(15, label);
        b.CloseElement();
        RenderChildren(b, n.Children, ctx);
        b.CloseElement();
    }

    private void RenderMeta(RenderTreeBuilder b, DesignNode n, Ctx ctx)
    {
        b.OpenElement(0, "div");
        Mark(b, n, ctx, container: false);
        b.AddAttribute(10, "class", "bs-meta");
        var text = n.InnerText.Trim();
        b.AddContent(11, text.Length > 0 ? $"{n.Name}: {text}" : n.Name);
        b.CloseElement();
    }

    // ------------------------------------------------------------------

    /// <summary>Anzeigename: tag.class oder Komponentenname.</summary>
    public static string LabelOf(DesignNode n)
    {
        switch (n.Kind)
        {
            case NodeKind.Text: return "\"" + (n.Text.Trim().Length > 18 ? n.Text.Trim()[..17] + "…" : n.Text.Trim()) + "\"";
            case NodeKind.Expression: return n.Text.Length > 24 ? n.Text[..23] + "…" : n.Text;
            case NodeKind.Control: return "@" + n.Name;
            case NodeKind.Code: return "@{ }";
            case NodeKind.Comment: return "<!-- -->";
        }
        var cls = n.Classes.Where(c => !c.Contains('@')).Take(2).ToList();
        return n.Name + (cls.Count > 0 ? "." + string.Join('.', cls) : "");
    }

    [GeneratedRegex(@"^[a-z][a-z0-9-]*$")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"@\((?:[^()]|\([^()]*\))*\)")]
    private static partial Regex ExplicitExprRegex();

    [GeneratedRegex(@"@[A-Za-z_][\w.]*(?:\([^)]*\))?")]
    private static partial Regex ImplicitExprRegex();
}
