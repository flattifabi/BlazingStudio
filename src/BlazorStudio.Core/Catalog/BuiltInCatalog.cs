namespace BlazorStudio.Core.Catalog;

/// <summary>Toolbox-Einträge, die es immer gibt (HTML, Vorlagen, Blazor-Komponenten, Bausteine).</summary>
public static class BuiltInCatalog
{
    public static IReadOnlyList<string> Categories { get; } = new[] { "layout", "text", "form", "media", "blazor", "project" };

    public static IReadOnlyList<ComponentDefinition> Items { get; } = Build();

    /// <summary>Größere Bausteine (rechts unter „Bausteine“).</summary>
    public static IReadOnlyList<ComponentDefinition> Blocks { get; } = BuildBlocks();

    private static List<ComponentDefinition> Build()
    {
        var l = new List<ComponentDefinition>
        {
            // ---------- Layout ----------
            T("tpl:container", "div", "tb.container", "Container", "layout", "square", "<div style=\"padding: 16px\"></div>"),
            T("tpl:row", "div", "tb.row", "Flex row", "layout", "row", "<div style=\"display: flex; gap: 12px; align-items: center\"></div>"),
            T("tpl:stack", "div", "tb.stack", "Stack", "layout", "col", "<div style=\"display: flex; flex-direction: column; gap: 12px\"></div>"),
            T("tpl:grid", "div", "tb.grid", "Grid", "layout", "grid", "<div style=\"display: grid; grid-template-columns: repeat(3, 1fr); gap: 16px\"></div>"),
            T("tpl:card", "div", "tb.card", "Card", "layout", "card",
                "<div style=\"padding: 16px; border: 1px solid #e5e7eb; border-radius: 12px\">\n<h3>Titel</h3>\n<p>Beschreibung</p>\n</div>"),
            T("html:section", "section", "tb.section", "Section", "layout", "section", "<section style=\"padding: 48px 24px\"></section>"),
            T("html:header", "header", null, "header", "layout", "section", "<header></header>"),
            T("html:footer", "footer", null, "footer", "layout", "section", "<footer></footer>"),
            T("html:nav", "nav", null, "nav", "layout", "row", "<nav style=\"display: flex; gap: 16px\"></nav>"),
            T("html:main", "main", null, "main", "layout", "square", "<main></main>"),
            T("html:hr", "hr", "tb.divider", "Divider", "layout", "minus", "<hr />"),

            // ---------- Text ----------
            T("html:h1", "h1", "tb.heading1", "Heading 1", "text", "heading", "<h1>Überschrift</h1>"),
            T("html:h2", "h2", "tb.heading2", "Heading 2", "text", "heading", "<h2>Überschrift</h2>"),
            T("html:h3", "h3", "tb.heading3", "Heading 3", "text", "heading", "<h3>Überschrift</h3>"),
            T("html:p", "p", "tb.paragraph", "Paragraph", "text", "text", "<p>Text</p>"),
            T("html:span", "span", null, "span", "text", "text", "<span>Text</span>"),
            T("html:a", "a", "tb.link", "Link", "text", "link", "<a href=\"#\">Link</a>"),
            T("html:label", "label", "tb.label", "Label", "text", "type", "<label>Label</label>"),
            T("tpl:list", "ul", "tb.list", "List", "text", "list", "<ul>\n<li>Eintrag 1</li>\n<li>Eintrag 2</li>\n</ul>"),
            T("html:blockquote", "blockquote", "tb.quote", "Quote", "text", "quote", "<blockquote>Zitat</blockquote>"),
            T("html:code", "code", null, "code", "text", "code", "<code>code</code>"),

            // ---------- Formular ----------
            T("html:button", "button", "tb.button", "Button", "form", "button", "<button type=\"button\">Button</button>"),
            T("html:input", "input", "tb.input", "Input", "form", "input", "<input type=\"text\" placeholder=\"Eingabe\" />"),
            T("html:textarea", "textarea", "tb.textarea", "Text area", "form", "input", "<textarea rows=\"3\"></textarea>"),
            T("tpl:select", "select", "tb.select", "Select", "form", "select", "<select>\n<option value=\"1\">Option 1</option>\n<option value=\"2\">Option 2</option>\n</select>"),
            T("tpl:checkbox", "label", "tb.checkbox", "Checkbox", "form", "check", "<label><input type=\"checkbox\" /> Option</label>"),
            T("html:form", "form", "tb.form", "Form", "form", "form", "<form style=\"display: flex; flex-direction: column; gap: 12px\"></form>"),

            // ---------- Medien ----------
            T("html:img", "img", "tb.image", "Image", "media", "image", "<img src=\"\" alt=\"\" style=\"max-width: 100%\" />"),
            T("html:video", "video", "tb.video", "Video", "media", "video", "<video controls style=\"max-width: 100%\"></video>"),
            T("html:iframe", "iframe", null, "iframe", "media", "frame", "<iframe src=\"\" style=\"width: 100%; height: 320px; border: 0\"></iframe>"),
            T("tpl:svg", "svg", "tb.icon", "Icon", "media", "sparkle",
                "<svg width=\"24\" height=\"24\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\"><circle cx=\"12\" cy=\"12\" r=\"9\" /></svg>"),
        };

        // ---------- Blazor ----------
        l.AddRange(new[]
        {
            B("EditForm", "form", "<EditForm Model=\"@model\" OnValidSubmit=\"HandleValidSubmit\">\n<DataAnnotationsValidator />\n<button type=\"submit\">Senden</button>\n</EditForm>", true,
                P("Model", "object", ValueEditor.Expression, required: true),
                P("EditContext", "EditContext", ValueEditor.Expression),
                P("OnValidSubmit", "EventCallback<EditContext>", ValueEditor.Callback),
                P("OnInvalidSubmit", "EventCallback<EditContext>", ValueEditor.Callback),
                P("OnSubmit", "EventCallback<EditContext>", ValueEditor.Callback),
                P("FormName", "string", ValueEditor.Text),
                P("Enhance", "bool", ValueEditor.Bool)),
            B("InputText", "input", "<InputText @bind-Value=\"value\" />", false,
                P("Value", "string", ValueEditor.Text, bindable: true), P("DisplayName", "string", ValueEditor.Text)),
            B("InputTextArea", "input", "<InputTextArea @bind-Value=\"value\" />", false,
                P("Value", "string", ValueEditor.Text, bindable: true), P("DisplayName", "string", ValueEditor.Text)),
            B("InputNumber", "input", "<InputNumber @bind-Value=\"number\" />", false,
                P("Value", "TValue", ValueEditor.Number, bindable: true), P("DisplayName", "string", ValueEditor.Text)),
            B("InputCheckbox", "check", "<InputCheckbox @bind-Value=\"flag\" />", false,
                P("Value", "bool", ValueEditor.Bool, bindable: true), P("DisplayName", "string", ValueEditor.Text)),
            B("InputDate", "calendar", "<InputDate @bind-Value=\"date\" />", false,
                P("Value", "TValue", ValueEditor.Expression, bindable: true),
                P("Type", "InputDateType", ValueEditor.Enum, new[] { "Date", "DateTimeLocal", "Month", "Time" }, enumPrefix: "InputDateType."),
                P("DisplayName", "string", ValueEditor.Text)),
            B("InputSelect", "select", "<InputSelect @bind-Value=\"value\">\n<option value=\"\">Bitte wählen</option>\n</InputSelect>", true,
                P("Value", "TValue", ValueEditor.Expression, bindable: true), P("DisplayName", "string", ValueEditor.Text)),
            B("InputRadioGroup", "check", "<InputRadioGroup @bind-Value=\"value\"></InputRadioGroup>", true,
                P("Value", "TValue", ValueEditor.Expression, bindable: true), P("Name", "string", ValueEditor.Text)),
            B("InputFile", "upload", "<InputFile OnChange=\"OnFileSelected\" />", false,
                P("OnChange", "EventCallback<InputFileChangeEventArgs>", ValueEditor.Callback)),
            B("DataAnnotationsValidator", "check", "<DataAnnotationsValidator />", false),
            B("ValidationSummary", "alert", "<ValidationSummary />", false, P("Model", "object", ValueEditor.Expression)),
            B("ValidationMessage", "alert", "<ValidationMessage For=\"() => model.Name\" />", false, P("For", "Expression<Func<TValue>>", ValueEditor.Expression, required: true)),
            B("NavLink", "link", "<NavLink href=\"\" Match=\"NavLinkMatch.All\">Link</NavLink>", true,
                P("Match", "NavLinkMatch", ValueEditor.Enum, new[] { "Prefix", "All" }, enumPrefix: "NavLinkMatch."),
                P("ActiveClass", "string", ValueEditor.Text)),
            B("PageTitle", "type", "<PageTitle>Titel</PageTitle>", true),
            B("HeadContent", "code", "<HeadContent></HeadContent>", true),
            B("AuthorizeView", "lock", "<AuthorizeView>\n<Authorized>\n<p>Angemeldet</p>\n</Authorized>\n<NotAuthorized>\n<p>Nicht angemeldet</p>\n</NotAuthorized>\n</AuthorizeView>", true,
                P("Roles", "string", ValueEditor.Text), P("Policy", "string", ValueEditor.Text)),
            B("Virtualize", "list", "<Virtualize Items=\"items\" Context=\"item\">\n<div>@item</div>\n</Virtualize>", true,
                P("Items", "ICollection<TItem>", ValueEditor.Expression), P("ItemSize", "float", ValueEditor.Number), P("OverscanCount", "int", ValueEditor.Number)),
            B("ErrorBoundary", "alert", "<ErrorBoundary>\n<ChildContent></ChildContent>\n<ErrorContent><p>Fehler</p></ErrorContent>\n</ErrorBoundary>", true),
            new ComponentDefinition
            {
                Key = "razor:if", TagName = "@if", LabelKey = "tb.if", Label = "@if", Category = "blazor", Icon = "zap", Source = ComponentSource.Template,
                Template = "@if (true)\n{\n<p>Sichtbar</p>\n}", AcceptsChildren = true, Keywords = "condition bedingung",
            },
            new ComponentDefinition
            {
                Key = "razor:foreach", TagName = "@foreach", LabelKey = "tb.foreach", Label = "@foreach", Category = "blazor", Icon = "loop", Source = ComponentSource.Template,
                Template = "@foreach (var item in items)\n{\n<div>@item</div>\n}", AcceptsChildren = true, Keywords = "loop schleife",
            },
        });
        return l;
    }

    private static List<ComponentDefinition> BuildBlocks() => new()
    {
        T("block:hero", "section", "block.hero", "Hero with buttons", "blocks", "blocks",
            "<section style=\"padding: 64px 24px; text-align: center\">\n<h1 style=\"margin: 0 0 12px; font-size: 40px\">Willkommen</h1>\n<p style=\"margin: 0 auto 24px; max-width: 560px; color: #6b7280\">Eine kurze Beschreibung deines Angebots.</p>\n<div style=\"display: flex; gap: 12px; justify-content: center\">\n<button type=\"button\" style=\"padding: 10px 18px; border-radius: 8px; border: 0; background: #111827; color: #ffffff\">Los geht's</button>\n<button type=\"button\" style=\"padding: 10px 18px; border-radius: 8px; border: 1px solid #d1d5db; background: #ffffff\">Mehr erfahren</button>\n</div>\n</section>"),
        T("block:cards", "div", "block.cards", "Card grid (3 columns)", "blocks", "grid",
            "<div style=\"display: grid; grid-template-columns: repeat(3, 1fr); gap: 16px; padding: 24px\">\n<div style=\"padding: 16px; border: 1px solid #e5e7eb; border-radius: 12px\">\n<h3>Karte 1</h3>\n<p>Text</p>\n</div>\n<div style=\"padding: 16px; border: 1px solid #e5e7eb; border-radius: 12px\">\n<h3>Karte 2</h3>\n<p>Text</p>\n</div>\n<div style=\"padding: 16px; border: 1px solid #e5e7eb; border-radius: 12px\">\n<h3>Karte 3</h3>\n<p>Text</p>\n</div>\n</div>"),
        T("block:navbar", "nav", "block.navbar", "Navigation bar", "blocks", "row",
            "<nav style=\"display: flex; align-items: center; gap: 24px; padding: 14px 24px; border-bottom: 1px solid #e5e7eb\">\n<strong style=\"font-size: 18px\">Marke</strong>\n<NavLink href=\"\" Match=\"NavLinkMatch.All\">Start</NavLink>\n<NavLink href=\"about\">Über uns</NavLink>\n<NavLink href=\"contact\">Kontakt</NavLink>\n</nav>"),
        T("block:contact", "EditForm", "block.contact", "Contact form (EditForm)", "blocks", "form",
            "<EditForm Model=\"@contact\" OnValidSubmit=\"SendAsync\" style=\"display: flex; flex-direction: column; gap: 12px; max-width: 420px\">\n<DataAnnotationsValidator />\n<label>Name</label>\n<InputText @bind-Value=\"contact.Name\" />\n<label>E-Mail</label>\n<InputText @bind-Value=\"contact.Email\" />\n<label>Nachricht</label>\n<InputTextArea @bind-Value=\"contact.Message\" rows=\"4\" />\n<ValidationSummary />\n<button type=\"submit\">Senden</button>\n</EditForm>"),
        T("block:footer", "footer", "block.footer", "Footer", "blocks", "section",
            "<footer style=\"padding: 32px 24px; border-top: 1px solid #e5e7eb; color: #6b7280; display: flex; justify-content: space-between\">\n<span>© 2026 Firma</span>\n<span>Impressum · Datenschutz</span>\n</footer>"),
        T("block:list", "ul", "block.list", "Data list (@foreach)", "blocks", "loop",
            "<ul style=\"display: flex; flex-direction: column; gap: 8px; padding: 0; list-style: none\">\n@foreach (var item in items)\n{\n<li style=\"padding: 12px; border: 1px solid #e5e7eb; border-radius: 8px\">@item</li>\n}\n</ul>"),
    };

    private static ComponentDefinition T(string key, string tag, string? labelKey, string label, string cat, string icon, string template) => new()
    {
        Key = key, TagName = tag, LabelKey = labelKey, Label = label, Category = cat, Icon = icon, Template = template,
        Source = key.StartsWith("html:", StringComparison.Ordinal) ? ComponentSource.Html : ComponentSource.Template,
        AcceptsChildren = !Model.DesignNode.VoidElements.Contains(tag),
        Keywords = tag,
    };

    private static ComponentDefinition B(string name, string icon, string template, bool children, params ParameterDefinition[] parameters) => new()
    {
        Key = "blazor:" + name, TagName = name, Label = name, Category = "blazor", Icon = icon, Template = template,
        Source = ComponentSource.Blazor, AcceptsChildren = children, Parameters = parameters, Namespace = "Microsoft.AspNetCore.Components",
    };

    private static ParameterDefinition P(string name, string type, ValueEditor editor, string[]? options = null, bool required = false, bool bindable = false, string? enumPrefix = null) =>
        new(name, type, editor, options, required, null, bindable, enumPrefix);

    public static ComponentDefinition? Find(string key) =>
        Items.FirstOrDefault(i => i.Key == key) ?? Blocks.FirstOrDefault(i => i.Key == key);
}
