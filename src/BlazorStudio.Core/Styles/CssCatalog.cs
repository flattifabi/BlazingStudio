namespace BlazorStudio.Core.Styles;

public sealed record CssProperty(string Name, string Group, IReadOnlyList<string> Values);

/// <summary>Häufige CSS-Eigenschaften mit typischen Werten (für Vervollständigung und Auswahlfelder).</summary>
public static class CssCatalog
{
    private static readonly string[] Global = { "inherit", "initial", "unset" };

    public static IReadOnlyList<CssProperty> Properties { get; } = new List<CssProperty>
    {
        P("display", "layout", "block", "inline", "inline-block", "flex", "inline-flex", "grid", "inline-grid", "contents", "none"),
        P("position", "layout", "static", "relative", "absolute", "fixed", "sticky"),
        P("top", "layout", "0", "auto"), P("right", "layout", "0", "auto"), P("bottom", "layout", "0", "auto"), P("left", "layout", "0", "auto"),
        P("inset", "layout", "0", "auto"),
        P("z-index", "layout", "0", "1", "10", "100", "auto"),
        P("float", "layout", "left", "right", "none"),
        P("overflow", "layout", "visible", "hidden", "auto", "scroll", "clip"),
        P("overflow-x", "layout", "visible", "hidden", "auto", "scroll"),
        P("overflow-y", "layout", "visible", "hidden", "auto", "scroll"),
        P("visibility", "layout", "visible", "hidden", "collapse"),
        P("box-sizing", "layout", "border-box", "content-box"),

        P("flex-direction", "flex", "row", "row-reverse", "column", "column-reverse"),
        P("flex-wrap", "flex", "nowrap", "wrap", "wrap-reverse"),
        P("justify-content", "flex", "flex-start", "center", "flex-end", "space-between", "space-around", "space-evenly", "stretch"),
        P("align-items", "flex", "stretch", "flex-start", "center", "flex-end", "baseline"),
        P("align-content", "flex", "stretch", "flex-start", "center", "flex-end", "space-between", "space-around"),
        P("align-self", "flex", "auto", "stretch", "flex-start", "center", "flex-end", "baseline"),
        P("justify-items", "grid", "stretch", "start", "center", "end"),
        P("place-items", "grid", "center", "start", "end", "stretch"),
        P("gap", "flex", "4px", "8px", "12px", "16px", "24px", "32px"),
        P("row-gap", "flex", "8px", "16px"), P("column-gap", "flex", "8px", "16px"),
        P("flex", "flex", "1", "auto", "none", "1 1 0"),
        P("flex-grow", "flex", "0", "1"), P("flex-shrink", "flex", "0", "1"), P("flex-basis", "flex", "auto", "0", "100%"),
        P("order", "flex", "0", "1", "-1"),
        P("grid-template-columns", "grid", "1fr 1fr", "repeat(2, 1fr)", "repeat(3, 1fr)", "repeat(4, 1fr)", "repeat(auto-fill, minmax(200px, 1fr))"),
        P("grid-template-rows", "grid", "auto", "1fr", "auto 1fr auto"),
        P("grid-column", "grid", "span 2", "1 / -1"), P("grid-row", "grid", "span 2", "1 / -1"),

        P("width", "size", "auto", "100%", "fit-content", "max-content", "min-content"),
        P("height", "size", "auto", "100%", "100vh", "fit-content"),
        P("min-width", "size", "0", "100%"), P("max-width", "size", "none", "100%", "1200px", "960px", "640px"),
        P("min-height", "size", "0", "100vh"), P("max-height", "size", "none", "100%"),
        P("aspect-ratio", "size", "1", "16 / 9", "4 / 3"),

        P("margin", "spacing", "0", "auto", "0 auto", "8px", "16px"),
        P("margin-top", "spacing", "0", "auto"), P("margin-right", "spacing", "0", "auto"), P("margin-bottom", "spacing", "0", "auto"), P("margin-left", "spacing", "0", "auto"),
        P("padding", "spacing", "0", "8px", "16px", "8px 16px", "24px"),
        P("padding-top", "spacing", "0"), P("padding-right", "spacing", "0"), P("padding-bottom", "spacing", "0"), P("padding-left", "spacing", "0"),

        P("font-family", "typography", "system-ui, sans-serif", "Georgia, serif", "ui-monospace, monospace", "inherit"),
        P("font-size", "typography", "12px", "14px", "16px", "18px", "24px", "32px", "1rem", "1.25rem", "2rem", "small", "large"),
        P("font-weight", "typography", "300", "400", "500", "600", "700", "800", "normal", "bold"),
        P("font-style", "typography", "normal", "italic"),
        P("line-height", "typography", "1", "1.2", "1.5", "1.75", "normal"),
        P("letter-spacing", "typography", "normal", "-0.02em", "0.05em"),
        P("text-align", "typography", "left", "center", "right", "justify", "start", "end"),
        P("text-decoration", "typography", "none", "underline", "line-through"),
        P("text-transform", "typography", "none", "uppercase", "lowercase", "capitalize"),
        P("white-space", "typography", "normal", "nowrap", "pre", "pre-wrap"),
        P("text-overflow", "typography", "clip", "ellipsis"),
        P("word-break", "typography", "normal", "break-all", "break-word"),
        P("color", "typography", "inherit", "currentColor", "#000000", "#ffffff"),

        P("background", "fill", "none", "transparent"),
        P("background-color", "fill", "transparent", "#ffffff", "#000000", "currentColor"),
        P("background-image", "fill", "none", "linear-gradient(135deg, #4c8dff, #14b8a6)", "url()"),
        P("background-size", "fill", "auto", "cover", "contain"),
        P("background-position", "fill", "center", "top", "bottom"),
        P("background-repeat", "fill", "no-repeat", "repeat", "repeat-x", "repeat-y"),
        P("opacity", "fill", "1", "0.75", "0.5", "0.25", "0"),

        P("border", "border", "none", "1px solid #e5e7eb", "1px solid currentColor"),
        P("border-top", "border", "none", "1px solid #e5e7eb"), P("border-bottom", "border", "none", "1px solid #e5e7eb"),
        P("border-left", "border", "none"), P("border-right", "border", "none"),
        P("border-width", "border", "0", "1px", "2px"),
        P("border-style", "border", "none", "solid", "dashed", "dotted"),
        P("border-color", "border", "transparent", "currentColor", "#e5e7eb"),
        P("border-radius", "border", "0", "4px", "8px", "12px", "16px", "9999px", "50%"),
        P("outline", "border", "none", "2px solid currentColor"),

        P("box-shadow", "effects", "none", "0 1px 2px rgba(0,0,0,.08)", "0 4px 14px rgba(0,0,0,.12)", "0 12px 32px rgba(0,0,0,.18)"),
        P("transition", "effects", "all .2s ease", "opacity .2s", "transform .2s"),
        P("transform", "effects", "none", "translateY(-2px)", "scale(1.05)", "rotate(45deg)"),
        P("filter", "effects", "none", "blur(4px)", "grayscale(1)", "brightness(1.1)"),
        P("backdrop-filter", "effects", "none", "blur(8px)"),
        P("cursor", "effects", "auto", "pointer", "default", "text", "move", "not-allowed", "grab"),
        P("pointer-events", "effects", "auto", "none"),
        P("user-select", "effects", "auto", "none", "text"),
        P("object-fit", "effects", "fill", "contain", "cover", "none"),
        P("list-style", "effects", "none", "disc", "decimal"),
    };

    private static readonly Dictionary<string, CssProperty> ByName = Properties.ToDictionary(p => p.Name);

    public static CssProperty? Find(string name) => ByName.GetValueOrDefault(name.Trim().ToLowerInvariant());

    public static IReadOnlyList<string> ValuesFor(string property) =>
        Find(property)?.Values.Concat(Global).ToList() ?? (IReadOnlyList<string>)Global;

    private static CssProperty P(string name, string group, params string[] values) => new(name, group, values);

    /// <summary>Häufige Bootstrap-Klassen, falls das Projekt Bootstrap per CDN einbindet.</summary>
    public static IReadOnlyList<string> CommonUtilityClasses { get; } = new[]
    {
        "container", "container-fluid", "row", "col", "col-6", "col-md-6", "col-lg-4", "d-flex", "d-grid", "d-none", "d-block",
        "flex-column", "justify-content-center", "justify-content-between", "align-items-center", "gap-2", "gap-3",
        "m-0", "mt-3", "mb-3", "my-4", "mx-auto", "p-2", "p-3", "p-4", "px-3", "py-2",
        "text-center", "text-muted", "text-primary", "fw-bold", "fs-4", "lead", "small",
        "btn", "btn-primary", "btn-secondary", "btn-outline-primary", "btn-sm", "btn-lg",
        "card", "card-body", "card-title", "card-text", "shadow", "shadow-sm", "rounded", "rounded-3", "border",
        "form-control", "form-label", "form-check", "form-check-input", "form-select", "alert", "alert-info", "badge", "bg-light", "bg-primary",
        "table", "table-striped", "w-100", "h-100", "img-fluid",
    };
}
