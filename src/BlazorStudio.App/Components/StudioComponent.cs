using BlazorStudio.App.Services;
using Microsoft.AspNetCore.Components;

namespace BlazorStudio.App.Components;

/// <summary>
/// Basis für Bereiche, die auf globale Zustandsänderungen reagieren müssen
/// (Projekt, Dokumente, Build/Debug, Sprache, Einstellungen).
/// </summary>
public abstract class StudioComponent : ComponentBase, IDisposable
{
    [Inject] protected Workspace Ws { get; set; } = default!;

    [Inject] protected UiService Ui { get; set; } = default!;

    [Inject] protected RunService Run { get; set; } = default!;

    [Inject] protected DebugService Debug { get; set; } = default!;

    [Inject] protected Localizer L { get; set; } = default!;

    [Inject] protected SettingsService Settings { get; set; } = default!;

    protected override void OnInitialized()
    {
        Ws.Changed += Refresh;
        Ui.Changed += Refresh;
        Run.Changed += Refresh;
        Debug.Changed += Refresh;
        L.Changed += Refresh;
        Settings.Changed += Refresh;
        OnStudioInitialized();
    }

    protected virtual void OnStudioInitialized() { }

    protected void Refresh() => InvokeAsync(StateHasChanged);

    public virtual void Dispose()
    {
        Ws.Changed -= Refresh;
        Ui.Changed -= Refresh;
        Run.Changed -= Refresh;
        Debug.Changed -= Refresh;
        L.Changed -= Refresh;
        Settings.Changed -= Refresh;
    }
}
