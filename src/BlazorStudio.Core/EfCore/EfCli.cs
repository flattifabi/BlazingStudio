using System.Text;
using System.Text.Json;
using BlazorStudio.Core.Build;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BlazorStudio.Core.EfCore;

public sealed record EfMigration(string Id, string Name, bool? Applied);

public sealed record EfContextInfo(string? Type, string? Provider, string? DatabaseName, string? DataSource, string? Options);

public sealed record EfEntity(string Name, string PropertyName, string File, int Line);

public sealed record DbContextSource(string Name, string? Namespace, string File, int Line, string ProjectPath, IReadOnlyList<EfEntity> Entities);

public sealed record EfResult(int ExitCode, string? Data, IReadOnlyList<string> Errors)
{
    public bool Success => ExitCode == 0;
}

public sealed record ScaffoldOptions(
    string Connection,
    string Provider,
    string OutputDir,
    string? ContextDir,
    string? ContextName,
    bool DataAnnotations,
    bool Force,
    bool NoOnConfiguring,
    IReadOnlyList<string> Tables);

/// <summary>
/// Ruft „dotnet ef“ auf (Migrationen, Datenbank, Scaffolding) und liest JSON-Ausgaben.
/// Mit --prefix-output trennt EF Daten (data:) von Log-Zeilen (info:/warn:/error:).
/// </summary>
public sealed class EfCli
{
    public static readonly IReadOnlyList<(string Package, string Label)> Providers = new[]
    {
        ("Microsoft.EntityFrameworkCore.SqlServer", "SQL Server"),
        ("Npgsql.EntityFrameworkCore.PostgreSQL", "PostgreSQL"),
        ("Microsoft.EntityFrameworkCore.Sqlite", "SQLite"),
        ("Pomelo.EntityFrameworkCore.MySql", "MySQL / MariaDB"),
        ("Oracle.EntityFrameworkCore", "Oracle"),
    };

    /// <summary>Jede Ausgabezeile (Text, IstFehler).</summary>
    public event Action<string, bool>? Output;

    public string? ContextProject { get; set; }

    public string? StartupProject { get; set; }

    public string? Context { get; set; }

    public string? Configuration { get; set; }

    /// <summary>Version von dotnet-ef oder null, wenn das Tool fehlt.</summary>
    public async Task<string?> GetToolVersionAsync(string workingDirectory)
    {
        var sb = new StringBuilder();
        using var r = new ProcessRunner();
        r.Output += (l, _) => sb.AppendLine(l);
        int code;
        try { code = await r.RunAsync("dotnet", new[] { "ef", "--version" }, workingDirectory); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
        if (code != 0) return null;
        var line = sb.ToString().Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0 && char.IsDigit(l[0]));
        return line ?? sb.ToString().Trim();
    }

    public Task<EfResult> RunAsync(params string[] args) => RunCoreAsync(args, json: false);

    public Task<EfResult> RunJsonAsync(params string[] args) => RunCoreAsync(args, json: true);

    private async Task<EfResult> RunCoreAsync(string[] args, bool json)
    {
        var all = new List<string> { "ef" };
        all.AddRange(args);
        if (ContextProject is not null) { all.Add("--project"); all.Add(ContextProject); }
        if (StartupProject is not null) { all.Add("--startup-project"); all.Add(StartupProject); }
        if (Context is not null && !args.Contains("scaffold")) { all.Add("--context"); all.Add(Context); }
        if (Configuration is not null) { all.Add("--configuration"); all.Add(Configuration); }
        if (json) all.Add("--json");
        all.Add("--prefix-output");
        all.Add("--no-color");

        var data = new StringBuilder();
        var errors = new List<string>();
        using var r = new ProcessRunner();
        r.Output += (line, isErr) =>
        {
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                data.AppendLine(line.Length > 9 ? line[9..] : "");
                return;
            }
            var err = isErr || line.StartsWith("error:", StringComparison.Ordinal);
            if (err) errors.Add(StripPrefix(line));
            Output?.Invoke(StripPrefix(line), err);
        };
        var cwd = Path.GetDirectoryName(StartupProject ?? ContextProject ?? Environment.CurrentDirectory) ?? Environment.CurrentDirectory;
        Output?.Invoke("❯ dotnet " + string.Join(' ', all.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)), false);
        int code;
        try
        {
            code = await r.RunAsync("dotnet", all, cwd, new Dictionary<string, string> { ["DOTNET_CLI_UI_LANGUAGE"] = "en" });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            errors.Add(e.Message);
            code = -1;
        }
        return new EfResult(code, data.Length == 0 ? null : data.ToString(), errors);
    }

    private static string StripPrefix(string line)
    {
        foreach (var p in new[] { "info:", "warn:", "error:", "verbose:", "data:" })
            if (line.StartsWith(p, StringComparison.Ordinal)) return line[p.Length..].TrimStart();
        return line;
    }

    // ------------------------------------------------------------------

    public async Task<IReadOnlyList<EfMigration>> ListMigrationsAsync()
    {
        var r = await RunJsonAsync("migrations", "list");
        if (!r.Success || r.Data is null) return Array.Empty<EfMigration>();
        try
        {
            using var doc = JsonDocument.Parse(r.Data);
            return doc.RootElement.EnumerateArray().Select(e => new EfMigration(
                e.GetProperty("id").GetString() ?? "",
                e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                e.TryGetProperty("applied", out var a) && a.ValueKind is JsonValueKind.True or JsonValueKind.False ? a.GetBoolean() : null)).ToList();
        }
        catch (JsonException)
        {
            return Array.Empty<EfMigration>();
        }
    }

    public async Task<EfContextInfo?> GetContextInfoAsync()
    {
        var r = await RunJsonAsync("dbcontext", "info");
        if (!r.Success || r.Data is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(r.Data);
            var e = doc.RootElement;
            string? S(string n) => e.TryGetProperty(n, out var v) ? v.ToString() : null;
            return new EfContextInfo(S("type"), S("providerName"), S("databaseName"), S("dataSource"), S("options"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public Task<EfResult> AddMigrationAsync(string name, string? outputDir) =>
        outputDir is null ? RunJsonAsync("migrations", "add", name) : RunJsonAsync("migrations", "add", name, "--output-dir", outputDir);

    public Task<EfResult> RemoveMigrationAsync(bool force) =>
        force ? RunJsonAsync("migrations", "remove", "--force") : RunJsonAsync("migrations", "remove");

    /// <summary>migration: Zielmigration, "0" = alles zurücknehmen, null = neueste.</summary>
    public Task<EfResult> UpdateDatabaseAsync(string? migration) =>
        migration is null ? RunAsync("database", "update") : RunAsync("database", "update", migration);

    public Task<EfResult> DropDatabaseAsync() => RunAsync("database", "drop", "--force");

    public Task<EfResult> ScriptMigrationsAsync(string? from, string? to, bool idempotent, string outputFile)
    {
        var args = new List<string> { "migrations", "script" };
        if (from is not null) args.Add(from);
        if (to is not null) { if (from is null) args.Add("0"); args.Add(to); }
        if (idempotent) args.Add("--idempotent");
        args.Add("--output");
        args.Add(outputFile);
        return RunAsync(args.ToArray());
    }

    public Task<EfResult> ScriptContextAsync(string outputFile) => RunAsync("dbcontext", "script", "--output", outputFile);

    public Task<EfResult> HasPendingChangesAsync() => RunAsync("migrations", "has-pending-model-changes");

    public Task<EfResult> OptimizeAsync() => RunAsync("dbcontext", "optimize");

    public Task<EfResult> ScaffoldAsync(ScaffoldOptions o)
    {
        var args = new List<string> { "dbcontext", "scaffold", o.Connection, o.Provider, "--output-dir", o.OutputDir };
        if (!string.IsNullOrWhiteSpace(o.ContextDir)) { args.Add("--context-dir"); args.Add(o.ContextDir); }
        if (!string.IsNullOrWhiteSpace(o.ContextName)) { args.Add("--context"); args.Add(o.ContextName); }
        if (o.DataAnnotations) args.Add("--data-annotations");
        if (o.Force) args.Add("--force");
        if (o.NoOnConfiguring) args.Add("--no-onconfiguring");
        foreach (var t in o.Tables) { args.Add("--table"); args.Add(t); }
        return RunJsonAsync(args.ToArray());
    }

    // ------------------------------------------------------------------
    // DbContexts im Quellcode finden (ohne Build)
    // ------------------------------------------------------------------

    public static IReadOnlyList<DbContextSource> FindContexts(IEnumerable<(string ProjectPath, IEnumerable<string> Files)> projects)
    {
        var result = new List<DbContextSource>();
        foreach (var (project, files) in projects)
        {
            foreach (var file in files)
            {
                string text;
                try { text = File.ReadAllText(file); }
                catch (IOException) { continue; }
                if (!text.Contains("DbContext", StringComparison.Ordinal)) continue;
                var tree = CSharpSyntaxTree.ParseText(text);
                var root = tree.GetRoot();
                foreach (var cls in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
                {
                    var bases = cls.BaseList?.Types.Select(t => t.Type.ToString()).ToList() ?? new List<string>();
                    if (!bases.Any(b => b == "DbContext" || b.EndsWith(".DbContext", StringComparison.Ordinal) || b.StartsWith("IdentityDbContext", StringComparison.Ordinal) ||
                                        b.StartsWith("ApiAuthorizationDbContext", StringComparison.Ordinal) || b.EndsWith("DbContext", StringComparison.Ordinal) && b != cls.Identifier.Text))
                        continue;
                    var ns = cls.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();
                    var entities = cls.Members.OfType<PropertyDeclarationSyntax>()
                        .Where(p => p.Type.ToString().StartsWith("DbSet<", StringComparison.Ordinal))
                        .Select(p =>
                        {
                            var t = p.Type.ToString();
                            return new EfEntity(t[6..^1], p.Identifier.Text, file, p.GetLocation().GetLineSpan().StartLinePosition.Line + 1);
                        }).ToList();
                    result.Add(new DbContextSource(cls.Identifier.Text, ns, file, cls.GetLocation().GetLineSpan().StartLinePosition.Line + 1, project, entities));
                }
            }
        }
        return result;
    }
}
