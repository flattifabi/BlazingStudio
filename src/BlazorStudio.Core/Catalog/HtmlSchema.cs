namespace BlazorStudio.Core.Catalog;

public sealed record EventDefinition(string Name, string ArgsType, string Group);

public sealed record AttributeDefinition(string Name, ValueEditor Editor, IReadOnlyList<string>? Options = null);

/// <summary>Wissen über HTML-Elemente: Attribute, passende Blazor-Events, Bindings.</summary>
public static class HtmlSchema
{
    public static IReadOnlyList<AttributeDefinition> GlobalAttributes { get; } = new[]
    {
        new AttributeDefinition("id", ValueEditor.Text),
        new AttributeDefinition("title", ValueEditor.Text),
        new AttributeDefinition("hidden", ValueEditor.Bool),
        new AttributeDefinition("tabindex", ValueEditor.Number),
        new AttributeDefinition("role", ValueEditor.Text),
        new AttributeDefinition("aria-label", ValueEditor.Text),
    };

    private static readonly Dictionary<string, AttributeDefinition[]> ElementAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["a"] = new[] { A("href", ValueEditor.Url), A("target", ValueEditor.Enum, "_self", "_blank", "_parent", "_top"), A("rel", ValueEditor.Text), A("download", ValueEditor.Bool) },
        ["img"] = new[] { A("src", ValueEditor.Url), A("alt", ValueEditor.Text), A("width", ValueEditor.Number), A("height", ValueEditor.Number), A("loading", ValueEditor.Enum, "lazy", "eager") },
        ["input"] = new[]
        {
            A("type", ValueEditor.Enum, "text", "email", "password", "number", "tel", "url", "search", "date", "time", "datetime-local", "checkbox", "radio", "range", "color", "file", "hidden"),
            A("placeholder", ValueEditor.Text), A("value", ValueEditor.Text), A("name", ValueEditor.Text),
            A("disabled", ValueEditor.Bool), A("readonly", ValueEditor.Bool), A("required", ValueEditor.Bool), A("checked", ValueEditor.Bool),
            A("min", ValueEditor.Text), A("max", ValueEditor.Text), A("step", ValueEditor.Text), A("autocomplete", ValueEditor.Enum, "on", "off", "email", "name", "current-password", "new-password"),
        },
        ["button"] = new[] { A("type", ValueEditor.Enum, "button", "submit", "reset"), A("disabled", ValueEditor.Bool), A("name", ValueEditor.Text), A("value", ValueEditor.Text) },
        ["textarea"] = new[] { A("placeholder", ValueEditor.Text), A("rows", ValueEditor.Number), A("cols", ValueEditor.Number), A("disabled", ValueEditor.Bool), A("readonly", ValueEditor.Bool), A("required", ValueEditor.Bool), A("name", ValueEditor.Text) },
        ["select"] = new[] { A("disabled", ValueEditor.Bool), A("multiple", ValueEditor.Bool), A("required", ValueEditor.Bool), A("name", ValueEditor.Text) },
        ["option"] = new[] { A("value", ValueEditor.Text), A("selected", ValueEditor.Bool), A("disabled", ValueEditor.Bool) },
        ["label"] = new[] { A("for", ValueEditor.Text) },
        ["form"] = new[] { A("method", ValueEditor.Enum, "get", "post"), A("action", ValueEditor.Url), A("novalidate", ValueEditor.Bool) },
        ["iframe"] = new[] { A("src", ValueEditor.Url), A("width", ValueEditor.Number), A("height", ValueEditor.Number), A("allowfullscreen", ValueEditor.Bool) },
        ["video"] = new[] { A("src", ValueEditor.Url), A("poster", ValueEditor.Url), A("controls", ValueEditor.Bool), A("autoplay", ValueEditor.Bool), A("muted", ValueEditor.Bool), A("loop", ValueEditor.Bool) },
        ["audio"] = new[] { A("src", ValueEditor.Url), A("controls", ValueEditor.Bool), A("autoplay", ValueEditor.Bool), A("loop", ValueEditor.Bool) },
        ["td"] = new[] { A("colspan", ValueEditor.Number), A("rowspan", ValueEditor.Number) },
        ["th"] = new[] { A("colspan", ValueEditor.Number), A("rowspan", ValueEditor.Number), A("scope", ValueEditor.Enum, "col", "row") },
        ["ol"] = new[] { A("start", ValueEditor.Number), A("reversed", ValueEditor.Bool) },
        ["details"] = new[] { A("open", ValueEditor.Bool) },
        ["dialog"] = new[] { A("open", ValueEditor.Bool) },
    };

    public static IReadOnlyList<AttributeDefinition> AttributesFor(string tag) =>
        (ElementAttributes.TryGetValue(tag, out var a) ? a : Array.Empty<AttributeDefinition>()).Concat(GlobalAttributes).ToList();

    public static IReadOnlyList<EventDefinition> Events { get; } = new[]
    {
        E("onclick", "MouseEventArgs", "mouse"), E("ondblclick", "MouseEventArgs", "mouse"),
        E("onmousedown", "MouseEventArgs", "mouse"), E("onmouseup", "MouseEventArgs", "mouse"),
        E("onmouseover", "MouseEventArgs", "mouse"), E("onmouseout", "MouseEventArgs", "mouse"),
        E("onmousemove", "MouseEventArgs", "mouse"), E("oncontextmenu", "MouseEventArgs", "mouse"),
        E("onkeydown", "KeyboardEventArgs", "keyboard"), E("onkeyup", "KeyboardEventArgs", "keyboard"), E("onkeypress", "KeyboardEventArgs", "keyboard"),
        E("onfocus", "FocusEventArgs", "focus"), E("onblur", "FocusEventArgs", "focus"),
        E("onfocusin", "FocusEventArgs", "focus"), E("onfocusout", "FocusEventArgs", "focus"),
        E("oninput", "ChangeEventArgs", "input"), E("onchange", "ChangeEventArgs", "input"), E("oninvalid", "EventArgs", "input"),
        E("onsubmit", "EventArgs", "form"), E("onreset", "EventArgs", "form"),
        E("oncopy", "ClipboardEventArgs", "clipboard"), E("oncut", "ClipboardEventArgs", "clipboard"), E("onpaste", "ClipboardEventArgs", "clipboard"),
        E("ondragstart", "DragEventArgs", "drag"), E("ondragover", "DragEventArgs", "drag"), E("ondrop", "DragEventArgs", "drag"), E("ondragend", "DragEventArgs", "drag"),
        E("onpointerdown", "PointerEventArgs", "pointer"), E("onpointerup", "PointerEventArgs", "pointer"), E("onpointermove", "PointerEventArgs", "pointer"),
        E("ontouchstart", "TouchEventArgs", "pointer"), E("ontouchend", "TouchEventArgs", "pointer"),
        E("onscroll", "EventArgs", "other"), E("onwheel", "WheelEventArgs", "other"),
        E("onload", "ProgressEventArgs", "media"), E("onerror", "ErrorEventArgs", "media"),
        E("onplay", "EventArgs", "media"), E("onpause", "EventArgs", "media"), E("onended", "EventArgs", "media"),
    };

    private static readonly HashSet<string> FormFields = new(StringComparer.OrdinalIgnoreCase) { "input", "textarea", "select" };

    /// <summary>Welche Event-Gruppen für ein Element sinnvoll sind (in Anzeigereihenfolge).</summary>
    public static IReadOnlyList<string> EventGroupsFor(string tag)
    {
        var groups = new List<string> { "mouse", "focus", "keyboard" };
        if (FormFields.Contains(tag)) groups.Insert(0, "input");
        if (string.Equals(tag, "form", StringComparison.OrdinalIgnoreCase)) groups.Insert(0, "form");
        if (tag is "img" or "video" or "audio" or "iframe") groups.Add("media");
        groups.Add("clipboard");
        groups.Add("drag");
        groups.Add("pointer");
        groups.Add("other");
        return groups;
    }

    public static EventDefinition? FindEvent(string attributeName)
    {
        var n = attributeName.TrimStart('@');
        var colon = n.IndexOf(':');
        if (colon >= 0) n = n[..colon];
        return Events.FirstOrDefault(e => e.Name == n);
    }

    /// <summary>Darf das Element @bind verwenden und mit welchem Wert-Attribut?</summary>
    public static bool SupportsBind(string tag) => FormFields.Contains(tag);

    public static IReadOnlyList<string> DirectiveAttributes { get; } = new[]
    {
        "@bind", "@bind:event", "@bind:format", "@bind:after", "@bind:get", "@bind:set", "@ref", "@key", "@attributes", "@rendermode", "@formname",
    };

    public static IReadOnlyList<string> CommonTags { get; } = new[]
    {
        "div", "section", "header", "footer", "main", "nav", "article", "aside", "h1", "h2", "h3", "h4", "h5", "h6", "p", "span", "a",
        "strong", "em", "small", "code", "pre", "blockquote", "ul", "ol", "li", "dl", "dt", "dd", "img", "figure", "figcaption", "picture",
        "video", "audio", "iframe", "button", "input", "textarea", "select", "option", "label", "form", "fieldset", "legend",
        "table", "thead", "tbody", "tr", "th", "td", "hr", "br", "details", "summary", "dialog", "svg", "canvas",
    };

    private static AttributeDefinition A(string n, ValueEditor e, params string[] options) => new(n, e, options.Length == 0 ? null : options);

    private static EventDefinition E(string n, string a, string g) => new(n, a, g);
}
