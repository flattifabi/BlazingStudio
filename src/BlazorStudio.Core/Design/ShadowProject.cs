using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace BlazorStudio.Core.Design;

/// <summary>
/// Spiegel eines Projektordners für den Live-Designer. Enthält den aktuellen Stand inkl. ungespeicherter Editor-Inhalte,
/// instrumentierte .razor-Dateien und eine Design-Brücke (Skript, Vorschau-Seite). Das Originalprojekt wird nie verändert.
/// </summary>
public sealed partial class ShadowProject
{
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".git", ".vs", ".idea", "node_modules", "TestResults" };

    /// <summary>Dateien, die nach dem ersten Kopieren nicht mehr überschrieben werden (laufende App hält sie offen).</summary>
    private static readonly HashSet<string> CopyOnceExt = new(StringComparer.OrdinalIgnoreCase) { ".db", ".sqlite", ".sqlite3", ".mdf", ".ldf", ".db-shm", ".db-wal" };

    public const string BridgeFolder = "_bs";
    public const string BridgeScript = "_bs/design.js";

    public ShadowProject(string projectFile, string? shadowRoot = null)
    {
        ProjectFile = Path.GetFullPath(projectFile);
        SourceDir = Path.GetDirectoryName(ProjectFile)!;
        ShadowDir = shadowRoot ?? DefaultShadowDir(ProjectFile);
    }

    public string ProjectFile { get; }

    public string SourceDir { get; }

    public string ShadowDir { get; }

    public string ShadowProjectFile => Path.Combine(ShadowDir, Path.GetFileName(ProjectFile));

    /// <summary>Inhalt des Brücken-Skripts (wird nach wwwroot/_bs/design.js geschrieben).</summary>
    public string BridgeScriptContent { get; set; } = "";

    /// <summary>Rendermodus-Ausdruck für die Vorschau-Seite (null = statisch).</summary>
    public string? PreviewRenderMode { get; set; }

    public static string DefaultShadowDir(string projectFile)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(projectFile).ToLowerInvariant())))[..10];
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BlazorStudio", "design", Path.GetFileNameWithoutExtension(projectFile) + "-" + hash);
    }

    public string ToShadow(string sourcePath) => Path.Combine(ShadowDir, Path.GetRelativePath(SourceDir, sourcePath));

    public string Relative(string sourcePath) => Path.GetRelativePath(SourceDir, sourcePath).Replace('\\', '/');

    public bool Contains(string sourcePath)
    {
        var full = Path.GetFullPath(sourcePath);
        if (!full.StartsWith(SourceDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
        var rel = Path.GetRelativePath(SourceDir, full);
        return !rel.Split(Path.DirectorySeparatorChar).Any(SkipDirs.Contains);
    }

    /// <summary>
    /// Gleicht den kompletten Ordner ab. <paramref name="overrides"/>: Quelltext-Pfad → Inhalt (ungespeicherte Editor-Inhalte,
    /// bereits instrumentiert). Liefert die Anzahl geänderter Dateien.
    /// </summary>
    public int SyncAll(IReadOnlyDictionary<string, string>? overrides = null)
    {
        Directory.CreateDirectory(ShadowDir);
        var changed = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in EnumerateSource(SourceDir))
        {
            var target = ToShadow(file);
            seen.Add(target);
            if (overrides is not null && overrides.TryGetValue(file, out var text))
            {
                if (WriteText(file, text)) changed++;
            }
            else if (CopyFile(file, target)) changed++;
        }
        // Gelöschte Dateien auch im Spiegel entfernen (bin/obj und die Brücke bleiben)
        foreach (var file in EnumerateSource(ShadowDir))
        {
            var rel = Path.GetRelativePath(ShadowDir, file);
            if (rel.StartsWith(BridgeFolder, StringComparison.OrdinalIgnoreCase) || rel.Replace('\\', '/').StartsWith("wwwroot/" + BridgeFolder + "/", StringComparison.OrdinalIgnoreCase)) continue;
            if (CopyOnceExt.Contains(Path.GetExtension(file))) continue;
            if (!seen.Contains(file)) TryDelete(file);
        }
        InstallBridge();
        return changed;
    }

    /// <summary>Datei mit neuem Inhalt in den Spiegel schreiben (nur wenn sich etwas ändert). Liefert true bei Änderung.</summary>
    public bool WriteText(string sourcePath, string text)
    {
        if (!Contains(sourcePath)) return false;
        text = Transform(sourcePath, text);
        var target = ToShadow(sourcePath);
        try
        {
            if (File.Exists(target) && File.ReadAllText(target) == text) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, text, new UTF8Encoding(true));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Datei aus dem Original übernehmen (z. B. nach dem Schließen eines Editors).</summary>
    public bool Restore(string sourcePath)
    {
        if (!Contains(sourcePath)) return false;
        if (!File.Exists(sourcePath))
        {
            TryDelete(ToShadow(sourcePath));
            return true;
        }
        return CopyFile(sourcePath, ToShadow(sourcePath));
    }

    private bool CopyFile(string source, string target)
    {
        try
        {
            var ext = Path.GetExtension(source);
            if (CopyOnceExt.Contains(ext) && File.Exists(target)) return false;
            if (IsTextToTransform(source))
            {
                var text = File.ReadAllText(source);
                return WriteText(source, text);
            }
            var si = new FileInfo(source);
            var ti = new FileInfo(target);
            if (ti.Exists && ti.Length == si.Length && ti.LastWriteTimeUtc == si.LastWriteTimeUtc) return false;
            Directory.CreateDirectory(ti.DirectoryName!);
            File.Copy(source, target, overwrite: true);
            File.SetLastWriteTimeUtc(target, si.LastWriteTimeUtc);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool IsTextToTransform(string path)
    {
        var name = Path.GetFileName(path);
        if (name.Equals("Program.cs", StringComparison.OrdinalIgnoreCase) && string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), SourceDir, StringComparison.OrdinalIgnoreCase)) return true;
        return name.Equals("App.razor", StringComparison.OrdinalIgnoreCase) || name.Equals("index.html", StringComparison.OrdinalIgnoreCase) && path.Replace('\\', '/').Contains("/wwwroot/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Middleware für die Schatten-App: entfernt nur die Einbett-Sperren (X-Frame-Options, CSP frame-ancestors),
    /// damit der Designer die App in seinem Rahmen zeigen darf.
    /// </summary>
    private const string FrameMiddleware =
        "\n// Blazor Studio (Live-Designer): Einbetten im Designer erlauben\n" +
        "{0}.Use(async (bsCtx, bsNext) => {{ bsCtx.Response.OnStarting(() => {{ bsCtx.Response.Headers.Remove(\"X-Frame-Options\"); " +
        "var bsCsp = bsCtx.Response.Headers.ContentSecurityPolicy.ToString(); if (bsCsp.Length > 0) {{ var bsRest = string.Join(\";\", bsCsp.Split(';').Where(d => !d.Trim().StartsWith(\"frame-ancestors\", System.StringComparison.OrdinalIgnoreCase))).Trim(); " +
        "if (bsRest.Length == 0) bsCtx.Response.Headers.Remove(\"Content-Security-Policy\"); else bsCtx.Response.Headers.ContentSecurityPolicy = bsRest; }} return System.Threading.Tasks.Task.CompletedTask; }}); await bsNext(); }});\n";

    /// <summary>Brücken-Skript in App.razor (Web App) bzw. wwwroot/index.html (WebAssembly) einhängen.</summary>
    private string Transform(string sourcePath, string text)
    {
        if (!IsTextToTransform(sourcePath)) return text;
        if (Path.GetFileName(sourcePath).Equals("Program.cs", StringComparison.OrdinalIgnoreCase))
        {
            if (text.Contains("Blazor Studio (Live-Designer)", StringComparison.Ordinal)) return text;
            var m = AppBuild().Match(text);
            return m.Success ? text.Insert(m.Index + m.Length, string.Format(System.Globalization.CultureInfo.InvariantCulture, FrameMiddleware, m.Groups["app"].Value)) : text;
        }
        if (text.Contains(BridgeScript, StringComparison.Ordinal)) return text;
        var body = BodyEnd().Match(text);
        if (!body.Success) return text;
        var tag = "<script src=\"" + BridgeScript + "\"></script>";
        return text.Insert(body.Index, tag + "\n");
    }

    /// <summary>Design-Brücke: Skript sowie eine Vorschau-Seite für Komponenten ohne @page (auch für Tooltips).</summary>
    public void InstallBridge()
    {
        var www = Path.Combine(ShadowDir, "wwwroot", BridgeFolder);
        Directory.CreateDirectory(www);
        WriteIfChanged(Path.Combine(www, "design.js"), BridgeScriptContent);
        var comp = Path.Combine(ShadowDir, BridgeFolder);
        Directory.CreateDirectory(comp);
        WriteIfChanged(Path.Combine(comp, "_BsPreview.razor"), PreviewPage(PreviewRenderMode));
        WriteIfChanged(Path.Combine(comp, "_BsEmptyLayout.razor"), EmptyLayout);
    }

    private static void WriteIfChanged(string path, string content)
    {
        try
        {
            if (File.Exists(path) && File.ReadAllText(path) == content) return;
            File.WriteAllText(path, content, new UTF8Encoding(true));
        }
        catch (IOException) { }
    }

    private static IEnumerable<string> EnumerateSource(string root)
    {
        if (!Directory.Exists(root)) yield break;
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] files, dirs;
            try
            {
                files = Directory.GetFiles(dir);
                dirs = Directory.GetDirectories(dir);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var f in files) yield return f;
            foreach (var d in dirs)
            {
                var name = Path.GetFileName(d);
                if (SkipDirs.Contains(name)) continue;
                if (dir == root && name.Equals(BridgeFolder, StringComparison.OrdinalIgnoreCase)) continue;
                stack.Push(d);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    [GeneratedRegex(@"</body\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex BodyEnd();

    /// <summary>var app = builder.Build(); (Name der Variablen beliebig)</summary>
    [GeneratedRegex(@"(?:var|WebApplication)\s+(?<app>\w+)\s*=\s*\w+\.Build\(\)\s*;")]
    private static partial Regex AppBuild();

    private const string EmptyLayout = """
        @namespace BlazorStudioDesign
        @using Microsoft.AspNetCore.Components.Web
        @inherits LayoutComponentBase
        @* Leeres Layout für Komponenten-Vorschauen; bekannte Provider (Theme, Popover …) der Bibliotheken werden eingebunden. *@
        @foreach (var t in Providers)
        {
            <DynamicComponent Type="t" />
        }
        <div style="padding:16px">@Body</div>

        @code {
            private static readonly string[] ProviderNames =
            {
                "MudBlazor.MudThemeProvider", "MudBlazor.MudPopoverProvider", "MudBlazor.MudDialogProvider", "MudBlazor.MudSnackbarProvider",
                "Radzen.Blazor.RadzenComponents", "Radzen.Blazor.RadzenDialog", "Radzen.Blazor.RadzenNotification",
            };

            private static List<Type>? _providers;

            private static List<Type> Providers => _providers ??= ProviderNames
                .Select(n => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(n, false)).FirstOrDefault(t => t is not null))
                .Where(t => t is not null).Select(t => t!).ToList();
        }
        """;

    /// <summary>Seite /_bs/preview?c=Voller.Typname&amp;p=Base64Json&amp;t=Text rendert eine Komponente mit Parametern.</summary>
    public static string PreviewPage(string? renderMode) => """
        @page "/_bs/preview"
        @namespace BlazorStudioDesign
        @layout _BsEmptyLayout
        """ + (renderMode is null ? "" : "\n@rendermode " + renderMode) + """

        @using System.Reflection
        @using System.Text.Json
        @using Microsoft.AspNetCore.Components.Web
        @using Microsoft.AspNetCore.Components.Forms

        <ErrorBoundary @key="C + P + T + H">
            <ChildContent>
                @if (H is not null)
                {
                    @((MarkupString)H)
                }
                else if (_type is not null && _needsForm)
                {
                    <EditForm Model="_model">
                        <DynamicComponent Type="_type" Parameters="_parameters" />
                    </EditForm>
                }
                else if (_type is not null)
                {
                    <DynamicComponent Type="_type" Parameters="_parameters" />
                }
                else if (C is not null)
                {
                    <p style="font:13px system-ui;color:#888">Komponente „@C“ nicht gefunden.</p>
                }
            </ChildContent>
            <ErrorContent>
                <p style="font:12px system-ui;color:#999">Keine Vorschau möglich (@context.GetType().Name).</p>
            </ErrorContent>
        </ErrorBoundary>

        @code {
            [SupplyParameterFromQuery(Name = "c")] public string? C { get; set; }
            [SupplyParameterFromQuery(Name = "p")] public string? P { get; set; }
            [SupplyParameterFromQuery(Name = "t")] public string? T { get; set; }
            [SupplyParameterFromQuery(Name = "h")] public string? H { get; set; }

            private Type? _type;
            private bool _needsForm;
            private readonly PreviewModel _model = new();

            private sealed class PreviewModel { public string? Value { get; set; } = "Text"; public bool Flag { get; set; } public int Number { get; set; } = 1; public DateTime? Date { get; set; } = DateTime.Today; }
            private Dictionary<string, object?> _parameters = new();

            protected override void OnParametersSet()
            {
                _type = null;
                _parameters = new();
                if (string.IsNullOrEmpty(C)) return;
                var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                var type = assemblies.Select(a => { try { return a.GetType(C, false); } catch { return null; } }).FirstOrDefault(t => t is not null);
                // Nur der Name (NavLink, InputText, eigene Komponenten): passenden IComponent-Typ suchen
                type ??= assemblies.SelectMany(a => { try { return a.GetExportedTypes(); } catch { return Array.Empty<Type>(); } })
                    .Where(t => typeof(IComponent).IsAssignableFrom(t) && !t.IsAbstract && (t.Name == C || t.Name.StartsWith(C + "`", StringComparison.Ordinal)))
                    .OrderBy(t => t.Namespace?.StartsWith("Microsoft", StringComparison.Ordinal) == true ? 1 : 0).FirstOrDefault();
                if (type is null) return;
                _needsForm = typeof(Microsoft.AspNetCore.Components.Forms.InputBase<string>).Assembly == type.Assembly && type.Namespace == "Microsoft.AspNetCore.Components.Forms" && type.Name.StartsWith("Input", StringComparison.Ordinal);
                if (type.IsGenericTypeDefinition)
                {
                    try { type = type.MakeGenericType(type.GetGenericArguments().Select(_ => typeof(string)).ToArray()); }
                    catch (ArgumentException) { return; }
                }
                var values = new Dictionary<string, string>();
                if (!string.IsNullOrEmpty(P))
                {
                    try
                    {
                        var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(P.Replace('-', '+').Replace('_', '/').PadRight(P.Length + (4 - P.Length % 4) % 4, '=')));
                        values = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
                    }
                    catch (Exception) { }
                }
                foreach (var (name, raw) in values)
                {
                    var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                    if (prop is null || prop.GetCustomAttribute<ParameterAttribute>() is null) continue;
                    if (TryConvert(raw, prop.PropertyType, out var v)) _parameters[name] = v;
                }
                if (_needsForm) BindToModel(type);
                if (!string.IsNullOrEmpty(T) && type.GetProperty("ChildContent") is { } cc && cc.PropertyType == typeof(RenderFragment))
                    _parameters["ChildContent"] = (RenderFragment)(b => b.AddContent(0, T));
                _type = type;
            }

            /// <summary>InputText & Co.: Value + ValueExpression auf eine passende Eigenschaft des Vorschau-Modells setzen.</summary>
            private void BindToModel(Type type)
            {
                var valueProp = type.GetProperty("Value");
                if (valueProp is null) return;
                var vt = valueProp.PropertyType;
                var member = vt == typeof(string) ? nameof(PreviewModel.Value) : vt == typeof(bool) ? nameof(PreviewModel.Flag) : vt == typeof(int) ? nameof(PreviewModel.Number) : vt == typeof(DateTime?) ? nameof(PreviewModel.Date) : null;
                if (member is null) return;
                var model = System.Linq.Expressions.Expression.Constant(_model);
                var body = System.Linq.Expressions.Expression.Property(model, member);
                var lambda = System.Linq.Expressions.Expression.Lambda(typeof(Func<>).MakeGenericType(vt), body);
                _parameters["Value"] = typeof(PreviewModel).GetProperty(member)!.GetValue(_model);
                _parameters["ValueExpression"] = lambda;
            }

            private static bool TryConvert(string raw, Type target, out object? value)
            {
                value = null;
                var t = Nullable.GetUnderlyingType(target) ?? target;
                try
                {
                    if (t == typeof(string)) { value = raw; return true; }
                    if (t == typeof(bool)) { value = raw is "" or "true" || raw == "True"; return true; }
                    if (t.IsEnum)
                    {
                        var name = raw.Contains('.') ? raw[(raw.LastIndexOf('.') + 1)..] : raw;
                        value = Enum.Parse(t, name, ignoreCase: true);
                        return true;
                    }
                    if (t == typeof(int) || t == typeof(long) || t == typeof(double) || t == typeof(decimal) || t == typeof(float))
                    {
                        value = Convert.ChangeType(raw, t, System.Globalization.CultureInfo.InvariantCulture);
                        return true;
                    }
                }
                catch (Exception) { }
                return false;
            }
        }
        """;
}
