namespace BlazorStudio.Core.Catalog;

public enum ValueEditor
{
    Text,
    Bool,
    Number,
    Enum,
    Color,
    Url,
    /// <summary>EventCallback / Action / Func: Auswahl einer Methode.</summary>
    Callback,
    /// <summary>RenderFragment: Inhalt kommt aus dem Designer.</summary>
    Fragment,
    /// <summary>Beliebiger Typ: nur per Binding an einen Member.</summary>
    Expression,
}

public sealed record ParameterDefinition(
    string Name,
    string Type,
    ValueEditor Editor,
    IReadOnlyList<string>? Options = null,
    bool IsRequired = false,
    string? Description = null,
    /// <summary>Gibt es {Name}Changed? Dann ist @bind-{Name} möglich.</summary>
    bool IsBindable = false,
    /// <summary>Für Enum-Parameter: Präfix für die Werte, z. B. "NavLinkMatch.".</summary>
    string? EnumPrefix = null)
{
    public static ValueEditor EditorFor(string type)
    {
        var t = type.Trim().TrimEnd('?');
        if (t is "bool" or "Boolean") return ValueEditor.Bool;
        if (t is "int" or "long" or "short" or "byte" or "double" or "float" or "decimal" or "Int32" or "Int64" or "Double" or "Decimal") return ValueEditor.Number;
        if (t is "string" or "String") return ValueEditor.Text;
        if (t.StartsWith("EventCallback", StringComparison.Ordinal) || t.StartsWith("Action", StringComparison.Ordinal) || t.StartsWith("Func<", StringComparison.Ordinal)) return ValueEditor.Callback;
        if (t.StartsWith("RenderFragment", StringComparison.Ordinal)) return ValueEditor.Fragment;
        return ValueEditor.Expression;
    }

    /// <summary>Argument-Typ eines Callbacks, z. B. EventCallback&lt;MouseEventArgs&gt; → MouseEventArgs.</summary>
    public string? CallbackArgument
    {
        get
        {
            var lt = Type.IndexOf('<');
            var gt = Type.LastIndexOf('>');
            return lt > 0 && gt > lt ? Type[(lt + 1)..gt] : null;
        }
    }

    /// <summary>Muss der Wert als Literal in Anführungszeichen ohne @ geschrieben werden (string)?</summary>
    public bool IsStringLike => Editor is ValueEditor.Text or ValueEditor.Url or ValueEditor.Color;
}

public enum ComponentSource { Html, Blazor, Project, Template, Library }

public sealed class ComponentDefinition
{
    /// <summary>Eindeutiger Schlüssel, z. B. "html:button", "blazor:InputText", "project:ProductCard", "tpl:card".</summary>
    public required string Key { get; init; }

    /// <summary>Tag- bzw. Komponentenname (bei Vorlagen der Name des äußeren Elements).</summary>
    public required string TagName { get; init; }

    /// <summary>Lokalisierungsschlüssel für die Anzeige; fehlt er, wird Label benutzt.</summary>
    public string? LabelKey { get; init; }

    public required string Label { get; init; }

    public required string Category { get; init; }

    public string Icon { get; init; } = "square";

    public required string Template { get; init; }

    public ComponentSource Source { get; init; }

    public IReadOnlyList<ParameterDefinition> Parameters { get; init; } = Array.Empty<ParameterDefinition>();

    public bool AcceptsChildren { get; init; }

    public string? FilePath { get; init; }

    public string? Namespace { get; init; }

    /// <summary>NuGet-Paket bei Bibliotheks-Komponenten (z. B. MudBlazor).</summary>
    public string? Library { get; init; }

    /// <summary>Zusätzliche Suchbegriffe.</summary>
    public string Keywords { get; init; } = "";

    /// <summary>Beschreibung (z. B. aus der XML-Dokumentation der Bibliothek).</summary>
    public string? Summary { get; init; }

    /// <summary>Voller Typname (für die Live-Vorschau), falls bekannt.</summary>
    public string? TypeName { get; init; }

    public ParameterDefinition? FindParameter(string name) =>
        Parameters.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));
}
