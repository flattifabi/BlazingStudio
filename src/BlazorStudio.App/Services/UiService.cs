namespace BlazorStudio.App.Services;

public enum ToastKind { Info, Success, Warning, Error }

public sealed record Toast(Guid Id, string Text, ToastKind Kind);

public sealed class DialogRequest
{
    public required string Title { get; init; }

    public string? Message { get; init; }

    /// <summary>null = reine Bestätigung, sonst Eingabefeld mit Startwert.</summary>
    public string? InputValue { get; set; }

    public string? InputPlaceholder { get; init; }

    public required string OkText { get; init; }

    public string? CancelText { get; init; }

    /// <summary>Optionaler dritter Knopf (z. B. „Nicht speichern“).</summary>
    public string? AltText { get; init; }

    public bool Danger { get; init; }

    public TaskCompletionSource<DialogResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public enum DialogResult { Ok, Alt, Cancel }

/// <summary>UI-Zustand, der von überall ausgelöst wird: Toasts, Dialoge, Befehlspalette, Overlays.</summary>
public sealed class UiService
{
    private readonly List<Toast> _toasts = new();

    public event Action? Changed;

    public IReadOnlyList<Toast> Toasts => _toasts;

    public DialogRequest? Dialog { get; private set; }

    public bool CommandPaletteOpen { get; private set; }

    public string CommandPaletteMode { get; private set; } = "";

    /// <summary>Welches Overlay offen ist: "newComponent", "newProject", "settings", "about" oder null.</summary>
    public string? Overlay { get; private set; }

    public void ShowToast(string text, ToastKind kind = ToastKind.Info, int ms = 3800)
    {
        var t = new Toast(Guid.NewGuid(), text, kind);
        _toasts.Add(t);
        Changed?.Invoke();
        _ = Task.Delay(ms).ContinueWith(_ =>
        {
            _toasts.Remove(t);
            Changed?.Invoke();
        });
    }

    public void DismissToast(Guid id)
    {
        _toasts.RemoveAll(t => t.Id == id);
        Changed?.Invoke();
    }

    public async Task<DialogResult> ShowDialogAsync(DialogRequest request)
    {
        Dialog?.Completion.TrySetResult(DialogResult.Cancel);
        Dialog = request;
        Changed?.Invoke();
        var r = await request.Completion.Task;
        if (Dialog == request) Dialog = null;
        Changed?.Invoke();
        return r;
    }

    public void CloseDialog(DialogResult result) => Dialog?.Completion.TrySetResult(result);

    public void OpenCommandPalette(string mode = "")
    {
        CommandPaletteOpen = true;
        CommandPaletteMode = mode;
        Changed?.Invoke();
    }

    public void CloseCommandPalette()
    {
        CommandPaletteOpen = false;
        Changed?.Invoke();
    }

    public void OpenOverlay(string name)
    {
        Overlay = name;
        Changed?.Invoke();
    }

    public void CloseOverlay()
    {
        Overlay = null;
        Changed?.Invoke();
    }

    /// <summary>Offenes Kontextmenü (generisch, siehe PopupMenu).</summary>
    public MenuRequest? Menu { get; private set; }

    public void ShowMenu(double x, double y, IEnumerable<MenuEntry> items)
    {
        // Doppelte/äußere Trenner entfernen
        var list = new List<MenuEntry>();
        foreach (var it in items)
        {
            if (it.IsSeparator && (list.Count == 0 || list[^1].IsSeparator)) continue;
            list.Add(it);
        }
        while (list.Count > 0 && list[^1].IsSeparator) list.RemoveAt(list.Count - 1);
        Menu = new MenuRequest(x, y, list);
        Changed?.Invoke();
    }

    public void CloseMenu()
    {
        if (Menu is null) return;
        Menu = null;
        Changed?.Invoke();
    }
}
