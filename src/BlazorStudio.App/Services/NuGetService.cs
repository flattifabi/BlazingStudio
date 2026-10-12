using BlazorStudio.Core.NuGet;

namespace BlazorStudio.App.Services;

public sealed record InstalledInfo(string Id, string Version, string? Latest)
{
    public bool HasUpdate => Latest is not null && Version.Length > 0 && NuGetClient.CompareVersions(Latest, Version) > 0;
}

/// <summary>NuGet-Pakete des geöffneten Projekts verwalten.</summary>
public sealed class NuGetService
{
    private readonly Workspace _ws;
    private readonly RunService _run;
    private readonly UiService _ui;
    private readonly Localizer _l;
    private readonly IHostBridge _host;
    private List<InstalledInfo> _installed = new();

    public NuGetService(Workspace ws, RunService run, UiService ui, Localizer l, IHostBridge host)
    {
        _ws = ws;
        _run = run;
        _ui = ui;
        _l = l;
        _host = host;
    }

    public NuGetClient Client { get; } = new() { EmptyQueryOnNuGetOrg = "blazor" };

    /// <summary>Alle Paketquellen aus den NuGet.Config-Dateien (auch deaktivierte).</summary>
    public IReadOnlyList<PackageSource> Sources => Client.Sources;

    /// <summary>Quelle, in der gesucht wird (null = alle aktiven).</summary>
    public string? SourceFilter { get; set; }

    public IReadOnlyList<InstalledInfo> Installed => _installed;

    public int UpdateCount => _installed.Count(i => i.HasUpdate);

    /// <summary>Paket-Id, an der gerade gearbeitet wird (Installieren/Entfernen).</summary>
    public string? BusyPackage { get; private set; }

    public bool IncludePrerelease { get; set; }

    public event Action? Changed;

    /// <summary>Projekt, dessen Pakete verwaltet werden (Standard: Startprojekt).</summary>
    public string? TargetProject { get; set; }

    private string? Csproj => TargetProject is not null && File.Exists(TargetProject) ? TargetProject : _ws.Project?.ProjectFile;

    public string? CurrentProject => Csproj;

    public bool IsInstalled(string id) => _installed.Any(i => i.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public InstalledInfo? Find(string id) => _installed.FirstOrDefault(i => i.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>PackageReferences lesen und (optional) neueste Versionen nachschlagen.</summary>
    public async Task RefreshAsync(bool checkUpdates = true)
    {
        ReloadSources();
        if (Csproj is null)
        {
            _installed = new List<InstalledInfo>();
            Changed?.Invoke();
            return;
        }
        var local = NuGetClient.ReadInstalled(Csproj);
        _installed = local.Select(p => new InstalledInfo(p.Id, p.Version, Find(p.Id)?.Latest)).ToList();
        Changed?.Invoke();
        if (!checkUpdates) return;

        var tasks = local.Select(async p =>
        {
            try
            {
                var prerelease = IncludePrerelease || p.Version.Contains('-');
                return (p.Id, Latest: await Client.GetLatestAsync(p.Id, prerelease));
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                return (p.Id, Latest: (string?)null);
            }
        });
        var latest = (await Task.WhenAll(tasks)).ToDictionary(x => x.Id, x => x.Latest, StringComparer.OrdinalIgnoreCase);
        await _host.InvokeOnUiAsync(() =>
        {
            _installed = _installed.Select(i => i with { Latest = latest.GetValueOrDefault(i.Id) ?? i.Latest }).ToList();
            Changed?.Invoke();
        });
    }

    // ------------------------------------------------------------------
    // Paketquellen
    // ------------------------------------------------------------------

    /// <summary>Quellen neu einlesen (Benutzer-Config + nuget.config vom Projekt aufwärts).</summary>
    public void ReloadSources()
    {
        var sources = NuGetConfig.Load(Csproj is null ? null : Path.GetDirectoryName(Csproj));
        if (!sources.SequenceEqual(Client.Sources)) Client.Sources = sources;
        if (SourceFilter is not null && !sources.Any(s => s.Enabled && s.Name.Equals(SourceFilter, StringComparison.OrdinalIgnoreCase))) SourceFilter = null;
    }

    /// <summary>Quelle steht in der Benutzer-Config (bearbeitbar) statt in einer nuget.config des Projekts.</summary>
    public static bool IsUserSource(PackageSource s) =>
        s.ConfigFile is null || string.Equals(Path.GetFullPath(s.ConfigFile), Path.GetFullPath(NuGetConfig.UserConfigPath), StringComparison.OrdinalIgnoreCase);

    /// <summary>Quelle speichern. Bei Quellen aus einer Projekt-Config nur die Zugangsdaten (Benutzer-Config).</summary>
    public void SaveSource(PackageSource source, string? password, string? previousName)
    {
        var existing = Sources.FirstOrDefault(s => s.Name.Equals(previousName ?? source.Name, StringComparison.OrdinalIgnoreCase));
        NuGetConfig.Save(source, password, previousName, credentialsOnly: existing is not null && !IsUserSource(existing));
        AfterSourcesChanged();
    }

    public void DeleteSource(string name)
    {
        NuGetConfig.Delete(name);
        AfterSourcesChanged();
    }

    public void SetSourceEnabled(string name, bool enabled)
    {
        NuGetConfig.SetEnabled(name, enabled);
        AfterSourcesChanged();
    }

    private void AfterSourcesChanged()
    {
        ReloadSources();
        Changed?.Invoke();
        _ = RefreshAsync();
    }

    public Task<bool> InstallAsync(string id, string? version) =>
        RunAsync(id, version is null
            ? new[] { "add", Csproj!, "package", id }
            : new[] { "add", Csproj!, "package", id, "--version", version },
            _l.F("nuget.installed", id, version ?? ""));

    public Task<bool> UninstallAsync(string id) =>
        RunAsync(id, new[] { "remove", Csproj!, "package", id }, _l.F("nuget.removed", id));

    public async Task UpdateAllAsync()
    {
        foreach (var p in _installed.Where(i => i.HasUpdate).ToList())
            if (!await InstallAsync(p.Id, p.Latest)) break;
    }

    private async Task<bool> RunAsync(string id, string[] args, string successText)
    {
        if (Csproj is null || BusyPackage is not null) return false;
        _ws.SaveAll();
        BusyPackage = id;
        Changed?.Invoke();
        int code;
        try
        {
            code = await _run.ExecAsync(Path.GetDirectoryName(Csproj)!, args);
        }
        finally
        {
            BusyPackage = null;
        }
        if (code == 0)
        {
            _ui.ShowToast(successText, ToastKind.Success);
            _ws.Rescan();
        }
        else
        {
            _ui.ShowToast(_l.F("nuget.failed", id), ToastKind.Error, 7000);
        }
        await RefreshAsync(checkUpdates: false);
        // neueste Versionen im Hintergrund aktualisieren
        _ = RefreshAsync();
        return code == 0;
    }
}
