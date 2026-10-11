using BlazorStudio.Core.EfCore;
using BlazorStudio.Core.Project;

namespace BlazorStudio.App.Services;

/// <summary>Entity Framework Core: DbContexts, Migrationen, Datenbank, Scaffolding über dotnet-ef.</summary>
public sealed class EfService
{
    private readonly Workspace _ws;
    private readonly RunService _run;
    private readonly UiService _ui;
    private readonly Localizer _l;
    private readonly IHostBridge _host;

    public EfService(Workspace ws, RunService run, UiService ui, Localizer l, IHostBridge host)
    {
        _ws = ws;
        _run = run;
        _ui = ui;
        _l = l;
        _host = host;
        Cli.Output += (line, err) => _run.Write(line, err ? OutputKind.Error : line.StartsWith('❯') ? OutputKind.Command : OutputKind.Normal);
    }

    public EfCli Cli { get; } = new();

    public string? ToolVersion { get; private set; }

    public bool ToolChecked { get; private set; }

    public IReadOnlyList<DbContextSource> Contexts { get; private set; } = Array.Empty<DbContextSource>();

    public DbContextSource? Selected { get; private set; }

    public IReadOnlyList<EfMigration> Migrations { get; private set; } = Array.Empty<EfMigration>();

    public EfContextInfo? Info { get; private set; }

    public string? Busy { get; private set; }

    public string? LastError { get; private set; }

    public event Action? Changed;

    public string? StartupProject => _ws.Project?.ProjectFile;

    /// <summary>Hat das Startprojekt Microsoft.EntityFrameworkCore.Design?</summary>
    public bool HasDesignPackage
    {
        get
        {
            var startup = _ws.StartupItem?.Project;
            return startup?.ResolvedPackages.Any(p => p.Id.Equals("Microsoft.EntityFrameworkCore.Design", StringComparison.OrdinalIgnoreCase)) == true
                   || startup?.PackageReferences.Any(p => p.Id.Equals("Microsoft.EntityFrameworkCore.Design", StringComparison.OrdinalIgnoreCase)) == true;
        }
    }

    public bool UsesEfCore =>
        _ws.Solution?.AllProjects.Any(p => p.Project?.ResolvedPackages.Any(r => r.Id.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)) == true) == true;

    public async Task InitializeAsync()
    {
        FindContexts();
        if (!ToolChecked)
        {
            ToolVersion = await Cli.GetToolVersionAsync(_ws.RootDirectory ?? Environment.CurrentDirectory);
            ToolChecked = true;
        }
        Changed?.Invoke();
        if (Selected is not null && ToolVersion is not null) await LoadMigrationsAsync();
    }

    public void FindContexts()
    {
        var projects = _ws.Solution?.AllProjects.Where(p => p.Project is not null)
            .Select(p => (p.Path, ProjectIndex.EnumerateFiles(Path.GetDirectoryName(p.Path)!).Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))))
            .ToList() ?? new();
        Contexts = EfCli.FindContexts(projects.Select(p => (p.Path, p.Item2)));
        if (Selected is null || !Contexts.Any(c => c.Name == Selected.Name && c.ProjectPath == Selected.ProjectPath))
            Select(Contexts.FirstOrDefault());
    }

    public void Select(DbContextSource? ctx)
    {
        Selected = ctx;
        Migrations = Array.Empty<EfMigration>();
        Info = null;
        Configure();
        Changed?.Invoke();
    }

    private void Configure()
    {
        Cli.ContextProject = Selected?.ProjectPath;
        Cli.StartupProject = StartupProject ?? Selected?.ProjectPath;
        Cli.Context = Selected is null ? null : Selected.Namespace is null ? Selected.Name : Selected.Namespace + "." + Selected.Name;
    }

    public async Task InstallToolAsync()
    {
        await Do("tool", async () =>
        {
            var code = await _run.ExecAsync(_ws.RootDirectory ?? Environment.CurrentDirectory, "tool", "install", "--global", "dotnet-ef");
            if (code != 0) code = await _run.ExecAsync(_ws.RootDirectory ?? Environment.CurrentDirectory, "tool", "update", "--global", "dotnet-ef");
            ToolVersion = await Cli.GetToolVersionAsync(_ws.RootDirectory ?? Environment.CurrentDirectory);
            return code == 0 ? null : _l["ef.toolInstallFailed"];
        });
    }

    public async Task LoadMigrationsAsync()
    {
        if (Selected is null) return;
        Configure();
        await Do("list", async () =>
        {
            Migrations = await Cli.ListMigrationsAsync();
            Info = await Cli.GetContextInfoAsync();
            return null;
        }, quiet: true);
    }

    public Task AddMigrationAsync(string name, string? outputDir) => Run("add", () => Cli.AddMigrationAsync(name, outputDir), _l.F("ef.migrationAdded", name), reload: true, rescan: true);

    public Task RemoveMigrationAsync(bool force) => Run("remove", () => Cli.RemoveMigrationAsync(force), _l["ef.migrationRemoved"], reload: true, rescan: true);

    public Task UpdateDatabaseAsync(string? target) => Run("update", () => Cli.UpdateDatabaseAsync(target), _l["ef.databaseUpdated"], reload: true);

    public Task DropDatabaseAsync() => Run("drop", Cli.DropDatabaseAsync, _l["ef.databaseDropped"], reload: true);

    public Task OptimizeAsync() => Run("optimize", Cli.OptimizeAsync, _l["ef.optimized"], rescan: true);

    public async Task CheckPendingAsync()
    {
        Configure();
        await Do("pending", async () =>
        {
            var r = await Cli.HasPendingChangesAsync();
            _ui.ShowToast(r.Success ? _l["ef.noPending"] : _l["ef.pending"], r.Success ? ToastKind.Success : ToastKind.Warning, 5000);
            return null;
        });
    }

    /// <summary>SQL-Skript erzeugen und als Tab öffnen.</summary>
    public async Task ScriptAsync(bool migrations, string? from, string? to, bool idempotent)
    {
        if (Selected is null) return;
        Configure();
        var dir = Path.Combine(Path.GetDirectoryName(Selected.ProjectPath)!, "obj", "ef-scripts");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"{Selected.Name}-{(migrations ? "migrations" : "create")}-{DateTime.Now:yyyyMMdd-HHmmss}.sql");
        await Do("script", async () =>
        {
            var r = migrations ? await Cli.ScriptMigrationsAsync(from, to, idempotent, file) : await Cli.ScriptContextAsync(file);
            if (r.Success && File.Exists(file)) await _host.InvokeOnUiAsync(() => _ws.Open(file));
            return r.Success ? null : string.Join('\n', r.Errors.TakeLast(3));
        });
    }

    public Task ScaffoldAsync(ScaffoldOptions options, string targetProject)
    {
        Cli.ContextProject = targetProject;
        Cli.StartupProject = StartupProject ?? targetProject;
        Cli.Context = null;
        return Run("scaffold", () => Cli.ScaffoldAsync(options), _l["ef.scaffolded"], rescan: true, findContexts: true);
    }

    private async Task Run(string what, Func<Task<EfResult>> action, string success, bool reload = false, bool rescan = false, bool findContexts = false)
    {
        if (what != "scaffold") Configure();
        _ws.SaveAll();
        await Do(what, async () =>
        {
            var r = await action();
            if (!r.Success) return string.Join('\n', r.Errors.TakeLast(4).DefaultIfEmpty(_l["ef.failed"]));
            _ui.ShowToast(success, ToastKind.Success);
            if (rescan) await _host.InvokeOnUiAsync(_ws.Rescan);
            if (findContexts) FindContexts();
            if (reload) { Migrations = await Cli.ListMigrationsAsync(); }
            return null;
        });
        Configure();
    }

    private async Task Do(string what, Func<Task<string?>> action, bool quiet = false)
    {
        if (Busy is not null) return;
        Busy = what;
        LastError = null;
        Changed?.Invoke();
        try
        {
            LastError = await action();
            if (LastError is not null && !quiet) _ui.ShowToast(_l["ef.failed"], ToastKind.Error, 6000);
        }
        finally
        {
            Busy = null;
            await _host.InvokeOnUiAsync(() => Changed?.Invoke());
        }
    }
}
