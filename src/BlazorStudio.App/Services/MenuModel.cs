namespace BlazorStudio.App.Services;

/// <summary>Eintrag eines Kontextmenüs. <see cref="Children"/> ergibt ein Untermenü.</summary>
public sealed record MenuEntry(
    string Text,
    string? Icon = null,
    Func<Task>? Action = null,
    string? Keys = null,
    bool Danger = false,
    bool Disabled = false,
    IReadOnlyList<MenuEntry>? Children = null,
    bool Checked = false)
{
    public static MenuEntry Separator { get; } = new("-");

    public bool IsSeparator => Text == "-";

    public static MenuEntry Do(string text, string? icon, Action action, string? keys = null, bool danger = false, bool disabled = false) =>
        new(text, icon, () => { action(); return Task.CompletedTask; }, keys, danger, disabled);

    public static MenuEntry Do(string text, string? icon, Func<Task> action, string? keys = null, bool danger = false, bool disabled = false) =>
        new(text, icon, action, keys, danger, disabled);

    public static MenuEntry Sub(string text, string? icon, params MenuEntry[] children) => new(text, icon, Children: children);
}

public sealed record MenuRequest(double X, double Y, IReadOnlyList<MenuEntry> Items);
