using BlazorStudio.Core.Catalog;
using BlazorStudio.Core.Model;
using BlazorStudio.Core.Project;
using BlazorStudio.Core.Razor;
using BlazorStudio.Core.Solution;
using BlazorStudio.Core.Styles;

namespace BlazorStudio.App.Services;

/// <summary>Geöffnetes Projekt, offene Dokumente und alles, was dateiübergreifend ist.</summary>
public sealed class Workspace : IDisposable
{
    public const string CanvasScope = ".bs-page";

    private readonly IHostBridge _host;
    private readonly SettingsService _settings;
    private readonly Localizer _l;
    private readonly UiService _ui;
    private readonly Dictionary<string, (int Version, RazorDocument Doc)> _previewCache = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _rescanDebounce;
    private string? _previewCss;

    public Workspace(IHostBridge host, SettingsService settings, Localizer l, UiService ui)
    {
        _host = host;
        _settings = settings;
        _l = l;
        _ui = ui;
    }

    public ProjectIndex? Project { get; private set; }

    public List<DocumentSession> Documents { get; } = new();

    public DocumentSession? Active { get; private set; }

    /// <summary>Allgemeine Änderungen (Tabs, Projekt, Index).</summary>
    public event Action? Changed;

    /// <summary>Projekt-CSS für die Vorschau hat sich geändert.</summary>
    public event Action? PreviewCssChanged;

    public bool HasProject => Project is not null;

    /// <summary>Steigt bei jedem Neueinlesen (für Caches wie Dateibäume).</summary>
    public int ScanVersion { get; private set; }

    public int CursorLine { get; private set; } = 1;

    public int CursorColumn { get; private set; } = 1;

    public event Action? CursorChanged;

    public void SetCursor(int line, int column)
    {
        CursorLine = line;
        CursorColumn = column;
        CursorChanged?.Invoke();
    }

    // ------------------------------------------------------------------
    // Projekt
    // ------------------------------------------------------------------

    /// <summary>Die geöffnete Projektmappe (bei einzelnen Projekten eine „virtuelle“ Mappe).</summary>
    public SolutionModel? Solution { get; private set; }

    /// <summary>Ordner, in dem Projektmappe bzw. Projekt liegt (für Dateiüberwachung).</summary>
    public string? RootDirectory => Solution?.Directory ?? Project?.RootDirectory;

    /// <summary>
    /// Öffnet einen Ordner, eine .sln/.slnx oder eine .csproj. Bei Projektmappen wird das zuletzt
    /// gewählte bzw. das erste Blazor-/Web-Projekt zum Startprojekt.
    /// </summary>
    public async Task<bool> OpenProjectAsync(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            _ui.ShowToast(_l.F("toast.folderMissing", path), ToastKind.Error);
            _settings.Current.RecentProjects.Remove(path);
            _settings.Save();
            return false;
        }

        if (!await CloseAllAsync()) return false;

        SolutionModel solution;
        try
        {
            solution = await Task.Run(() => LoadSolution(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            _ui.ShowToast(e.Message, ToastKind.Error);
            return false;
        }

        Solution = solution;
        _settings.AddRecent(path);
        StartWatcher(solution.Directory);

        var remembered = _settings.Current.StartupProjects.GetValueOrDefault(solution.Path);
        var projects = solution.AllProjects.Where(p => p.Project is not null).ToList();
        var startup = projects.FirstOrDefault(p => p.Path.Equals(remembered, StringComparison.OrdinalIgnoreCase))
                      ?? projects.FirstOrDefault(p => p.Project!.IsBlazor && p.Project.Kind == "web")
                      ?? projects.FirstOrDefault(p => p.Project!.IsBlazor)
                      ?? projects.FirstOrDefault(p => p.Project!.Kind == "web")
                      ?? projects.FirstOrDefault(p => p.Project!.Kind == "exe")
                      ?? projects.FirstOrDefault();

        if (startup is null)
        {
            // Ordner ohne .csproj: trotzdem öffnen (nur Dateien bearbeiten)
            await LoadIndexAsync(Directory.Exists(path) ? path : Path.GetDirectoryName(path)!, openStartFile: true);
            _ui.ShowToast(_l["toast.noCsproj"], ToastKind.Warning, 6000);
        }
        else
        {
            await LoadIndexAsync(Path.GetDirectoryName(startup.Path)!, openStartFile: true);
        }
        return true;
    }

    private static SolutionModel LoadSolution(string path)
    {
        if (File.Exists(path))
        {
            if (path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
                return SolutionModel.Load(path);
            if (path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                return SolutionModel.FromProjects(Path.GetDirectoryName(path)!, new[] { path });
            path = Path.GetDirectoryName(path)!;
        }
        var sln = Directory.GetFiles(path, "*.slnx").Concat(Directory.GetFiles(path, "*.sln")).FirstOrDefault();
        if (sln is not null) return SolutionModel.Load(sln);
        var projects = ProjectIndex.EnumerateFiles(path)
            .Where(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Where(f => Path.GetRelativePath(path, f).Count(c => c == Path.DirectorySeparatorChar) <= 3)
            .ToList();
        return SolutionModel.FromProjects(path, projects);
    }

    /// <summary>Index (Komponenten, CSS, Dateien) für das Startprojekt laden.</summary>
    private async Task LoadIndexAsync(string projectDirectory, bool openStartFile)
    {
        ProjectIndex index;
        try
        {
            index = await Task.Run(() => ProjectIndex.Load(projectDirectory));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _ui.ShowToast(e.Message, ToastKind.Error);
            return;
        }

        Project = index;
        ScanVersion++;
        _previewCache.Clear();
        _previewCss = null;
        _host.MapProjectAssets(index.WwwRoot);
        _ = ScanLibrariesAsync();
        _host.SetTitle($"{Solution?.Name ?? index.Name} – Blazor Studio");

        if (openStartFile)
        {
            var key = Solution?.Path ?? index.RootDirectory;
            var last = _settings.Current.LastOpenFiles.GetValueOrDefault(key) ?? _settings.Current.LastOpenFiles.GetValueOrDefault(index.RootDirectory);
            var start = last is not null && File.Exists(last)
                ? last
                : index.Components.Values.FirstOrDefault(c => c.Route == "/")?.FilePath ?? index.Components.Values.FirstOrDefault()?.FilePath;
            if (start is not null) Open(start);
        }

        Changed?.Invoke();
        PreviewCssChanged?.Invoke();
    }

    public SolutionItem? StartupItem =>
        Solution?.AllProjects.FirstOrDefault(p => p.Path.Equals(Project?.ProjectFile, StringComparison.OrdinalIgnoreCase));

    public void NotifyChanged() => Changed?.Invoke();

    /// <summary>Ist das Projekt in dieser Projektmappe entladen?</summary>
    public bool IsUnloaded(string projectPath) =>
        Solution is { } s && _settings.Current.UnloadedProjects.TryGetValue(s.Path, out var l) && l.Contains(projectPath, StringComparer.OrdinalIgnoreCase);

    /// <summary>Projektmappe neu einlesen (z. B. nach dotnet sln add/remove); offene Dateien bleiben offen.</summary>
    public async Task ReloadSolutionAsync(string? path = null)
    {
        var target = path ?? Solution?.Path;
        if (target is null) return;
        SolutionModel solution;
        try
        {
            solution = await Task.Run(() => LoadSolution(target));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            _ui.ShowToast(e.Message, ToastKind.Error);
            return;
        }
        var startup = Project?.ProjectFile;
        var moved = !string.Equals(Solution?.Path, solution.Path, StringComparison.OrdinalIgnoreCase);
        Solution = solution;
        if (moved)
        {
            _settings.AddRecent(solution.Path);
            StartWatcher(solution.Directory);
        }
        var keep = solution.AllProjects.FirstOrDefault(p => p.Path.Equals(startup, StringComparison.OrdinalIgnoreCase));
        if (keep is not null || Project is null && solution.AllProjects.All(p => p.Project is null)) Rescan();
        else if (solution.AllProjects.FirstOrDefault(p => p.Project is not null) is { } first) await LoadIndexAsync(Path.GetDirectoryName(first.Path)!, openStartFile: false);
        Changed?.Invoke();
    }

    public async Task SetStartupProjectAsync(SolutionItem item)
    {
        if (Solution is null || item.IsFolder) return;
        _settings.Current.StartupProjects[Solution.Path] = item.Path;
        _settings.Save();
        await LoadIndexAsync(Path.GetDirectoryName(item.Path)!, openStartFile: false);
        _ui.ShowToast(_l.F("toast.startupSet", item.Name), ToastKind.Success);
    }

    public async Task<bool> CloseProjectAsync()
    {
        if (!await CloseAllAsync()) return false;
        Project = null;
        Solution = null;
        _watcher?.Dispose();
        _watcher = null;
        _host.MapProjectAssets(null);
        _host.SetTitle("Blazor Studio");
        Changed?.Invoke();
        return true;
    }

    public void Rescan()
    {
        if (Project is null) return;
        if (Solution is not null)
        {
            try
            {
                foreach (var p in Solution.AllProjects.Where(p => File.Exists(p.Path)))
                    p.Project = ProjectModel.Load(p.Path);
            }
            catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { }
        }
        Project.Rescan();
        ScanVersion++;
        _ = ScanLibrariesAsync();
        // ungespeicherte CSS-Änderungen gelten in der Vorschau trotzdem
        _previewCss = null;
        _previewCache.Clear();
        Changed?.Invoke();
        PreviewCssChanged?.Invoke();
    }

    private void ScheduleRescan()
    {
        _rescanDebounce?.Cancel();
        var cts = _rescanDebounce = new CancellationTokenSource();
        _ = Task.Delay(600, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) _ = _host.InvokeOnUiAsync(Rescan);
        }, TaskScheduler.Default);
    }

    private void StartWatcher(string root)
    {
        _watcher?.Dispose();
        try
        {
            _watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true,
            };
        }
        catch (Exception e) when (e is IOException or ArgumentException)
        {
            return;
        }
        FileSystemEventHandler onChange = (_, e) => _ = _host.InvokeOnUiAsync(() => OnFileChanged(e.FullPath));
        _watcher.Changed += onChange;
        _watcher.Created += onChange;
        _watcher.Deleted += onChange;
        _watcher.Renamed += (_, e) => _ = _host.InvokeOnUiAsync(() => OnFileChanged(e.FullPath));
    }

    /// <summary>Offene, ungeänderte Dokumente nach externen Änderungen (git checkout/pull …) neu laden.</summary>
    public void ReloadFromDisk(string path) => OnFileChanged(Path.GetFullPath(path));

    public void ReloadAllFromDisk()
    {
        foreach (var d in Documents.ToList()) OnFileChanged(d.Path);
        ScheduleRescan();
    }

    private void OnFileChanged(string path)
    {
        var sep = Path.DirectorySeparatorChar;
        if (path.Contains($"{sep}bin{sep}") || path.Contains($"{sep}obj{sep}") || path.Contains($"{sep}.git") || path.Contains($"{sep}.vs{sep}")) return;

        var doc = Documents.FirstOrDefault(d => string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase));
        if (doc is not null && !doc.IsDirty && File.Exists(path))
        {
            try
            {
                var info = new FileInfo(path);
                if (info.LastWriteTimeUtc > doc.LastWriteOnDisk.AddMilliseconds(50))
                {
                    var text = ReadText(path);
                    if (text is not null && text != doc.Text)
                    {
                        doc.SetText(text, ChangeOrigin.External);
                        doc.MarkSaved();
                        doc.LastWriteOnDisk = info.LastWriteTimeUtc;
                    }
                }
            }
            catch (IOException) { }
        }
        ScheduleRescan();
    }

    // ------------------------------------------------------------------
    // Dokumente
    // ------------------------------------------------------------------

    public DocumentSession? Open(string path, int? line = null, bool activate = true)
    {
        var existing = Documents.FirstOrDefault(d => string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            var text = ReadText(path);
            if (text is null)
            {
                _ui.ShowToast(_l.F("toast.cannotOpen", Path.GetFileName(path)), ToastKind.Error);
                return null;
            }
            existing = new DocumentSession(path, text) { LastWriteOnDisk = SafeWriteTime(path) };
            existing.Changed += origin => OnDocumentChanged(existing, origin);
            var insertAt = Active is null ? Documents.Count : Documents.IndexOf(Active) + 1;
            Documents.Insert(Math.Min(insertAt, Documents.Count), existing);
        }
        if (line is not null)
        {
            existing.RevealLine = line;
            if (existing.Kind == DocumentKind.Razor && existing.View == ViewMode.Design) existing.View = ViewMode.Split;
        }
        if (activate) Activate(existing);
        else Changed?.Invoke();
        return existing;
    }

    public void Activate(DocumentSession? doc)
    {
        Active = doc;
        if (doc is not null && Project is not null)
        {
            _settings.Current.LastOpenFiles[Solution?.Path ?? Project.RootDirectory] = doc.Path;
        }
        Changed?.Invoke();
    }

    public async Task<bool> CloseAsync(DocumentSession doc)
    {
        if (doc.IsDirty)
        {
            var r = await _ui.ShowDialogAsync(new DialogRequest
            {
                Title = _l.F("dlg.unsavedTitle", doc.Name),
                Message = _l["dlg.unsavedText"],
                OkText = _l["action.save"],
                AltText = _l["action.dontSave"],
                CancelText = _l["action.cancel"],
            });
            if (r == DialogResult.Cancel) return false;
            if (r == DialogResult.Ok && !Save(doc)) return false;
        }
        var idx = Documents.IndexOf(doc);
        Documents.Remove(doc);
        if (Active == doc)
            Activate(Documents.Count == 0 ? null : Documents[Math.Clamp(idx - 1, 0, Documents.Count - 1)]);
        else Changed?.Invoke();
        if (doc.Kind == DocumentKind.Css) { _previewCss = null; PreviewCssChanged?.Invoke(); }
        return true;
    }

    public async Task<bool> CloseAllAsync()
    {
        foreach (var d in Documents.ToList())
            if (!await CloseAsync(d)) return false;
        return true;
    }

    public bool Save(DocumentSession doc)
    {
        try
        {
            File.WriteAllText(doc.Path, doc.Text);
            doc.LastWriteOnDisk = SafeWriteTime(doc.Path);
            doc.MarkSaved();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _ui.ShowToast(_l.F("toast.saveFailed", doc.Name, e.Message), ToastKind.Error, 6000);
            return false;
        }

        // Zugehörige Dateien (Code-Behind, scoped CSS), die der Designer geändert hat, mitspeichern
        if (doc.Kind == DocumentKind.Razor)
        {
            foreach (var related in Documents.Where(d => d != doc && d.IsDirty &&
                         (d.Path.Equals(doc.Path + ".cs", StringComparison.OrdinalIgnoreCase) || d.Path.Equals(doc.Path + ".css", StringComparison.OrdinalIgnoreCase))).ToList())
                Save(related);
            Project?.UpdateComponent(doc.Path);
        }
        Changed?.Invoke();
        return true;
    }

    public void SaveAll()
    {
        foreach (var d in Documents.Where(d => d.IsDirty).ToList()) Save(d);
    }

    private void OnDocumentChanged(DocumentSession doc, ChangeOrigin origin)
    {
        if (doc.Kind == DocumentKind.Css)
        {
            _previewCss = null;
            PreviewCssChanged?.Invoke();
        }
        if (doc.Kind == DocumentKind.Razor) _previewCache.Remove(doc.Path);
        // Nur bei Wechsel des „ungespeichert“-Zustands global benachrichtigen – sonst würde jede
        // Taste die ganze Oberfläche neu rendern. Inhaltsänderungen hören Designer & Co. direkt am Dokument.
        var dirty = doc.IsDirty;
        if (!_dirtyState.TryGetValue(doc, out var was) || was != dirty || origin is ChangeOrigin.Load or ChangeOrigin.External)
        {
            _dirtyState[doc] = dirty;
            Changed?.Invoke();
        }
        DocumentContentChanged?.Invoke(doc);
    }

    private readonly Dictionary<DocumentSession, bool> _dirtyState = new();

    /// <summary>Inhalt eines Dokuments hat sich geändert (häufig – nur für leichte Reaktionen).</summary>
    public event Action<DocumentSession>? DocumentContentChanged;

    public static string? ReadText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static DateTime SafeWriteTime(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch (IOException) { return DateTime.UtcNow; }
    }

    // ------------------------------------------------------------------
    // Katalog, Member, Code-Generierung
    // ------------------------------------------------------------------

    /// <summary>Alle Toolbox-Einträge: eingebaute + Projekt- + Bibliotheks-Komponenten.</summary>
    public IReadOnlyList<ComponentDefinition> AllComponents =>
        BuiltInCatalog.Items
            .Concat(Project?.Components.Values.Select(c => c.Definition) ?? Enumerable.Empty<ComponentDefinition>())
            .Concat(Libraries.SelectMany(l => l.Components))
            .ToList();

    /// <summary>Erkannte Komponenten-Bibliotheken (MudBlazor, Radzen …) des Startprojekts.</summary>
    public IReadOnlyList<ComponentLibrary> Libraries { get; private set; } = Array.Empty<ComponentLibrary>();

    public bool LibrariesLoading { get; private set; }

    private string? _libraryStamp;

    /// <summary>Roslyn-Workspace für die semantische Einfärbung (wie Rider).</summary>
    public BlazorStudio.Core.Highlighting.SemanticHighlighter Highlighter { get; } = new();

    private Task? _highlighterLoad;

    /// <summary>Lädt alle .cs-Dateien der Mappe und die Paket-Referenzen des Startprojekts (im Hintergrund).</summary>
    public Task EnsureHighlighterAsync()
    {
        if (_highlighterLoad is { IsCompleted: false }) return _highlighterLoad;
        var root = RootDirectory;
        if (root is null) return Task.CompletedTask;
        var refs = StartupItem?.Project?.ResolvedPackages.SelectMany(p => p.Assemblies).ToList() ?? new List<string>();
        var dirs = Solution?.AllProjects.Where(p => p.Project is not null && !IsUnloaded(p.Path)).Select(p => Path.GetDirectoryName(p.Path)!).ToList() ?? new List<string> { root };
        return _highlighterLoad = Task.Run(() =>
        {
            var files = dirs.SelectMany(ProjectIndex.EnumerateFiles).Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).Take(3000).ToList();
            // ImplicitUsings des SDK (obj/<Konfig>/<TFM>/<Projekt>.GlobalUsings.g.cs, jeweils die neueste)
            foreach (var d in dirs)
            {
                var obj = Path.Combine(d, "obj");
                if (!Directory.Exists(obj)) continue;
                try
                {
                    var gu = Directory.EnumerateFiles(obj, "*.GlobalUsings.g.cs", SearchOption.AllDirectories)
                        .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                    if (gu is not null) files.Add(gu);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
            Highlighter.Load(root, files, refs);
        });
    }

    /// <summary>Paket-DLLs im Hintergrund nach Komponenten durchsuchen (nur wenn sich Pakete geändert haben).</summary>
    public async Task ScanLibrariesAsync()
    {
        var project = StartupItem?.Project;
        if (project is null)
        {
            Libraries = Array.Empty<ComponentLibrary>();
            return;
        }
        var packages = project.ResolvedPackages.Select(p => (p.Id, p.Version, p.Assemblies)).ToList();
        var stamp = project.Path + "|" + string.Join(';', packages.Select(p => p.Id + "/" + p.Version));
        if (stamp == _libraryStamp) return;
        _libraryStamp = stamp;
        LibrariesLoading = true;
        Changed?.Invoke();
        try
        {
            Libraries = await Task.Run(() => LibraryScanner.Scan(packages));
        }
        catch (Exception e) when (e is IOException or BadImageFormatException or InvalidOperationException)
        {
            Libraries = Array.Empty<ComponentLibrary>();
        }
        LibrariesLoading = false;
        _previewCache.Clear();
        Changed?.Invoke();
    }

    public ComponentDefinition? DefinitionForNode(DesignNode node)
    {
        if (node.Kind != NodeKind.Component) return null;
        if (Project?.Components.TryGetValue(node.Name, out var p) == true) return p.Definition;
        return BuiltInCatalog.Items.FirstOrDefault(i => i.Source == ComponentSource.Blazor && i.TagName == node.Name)
               ?? Libraries.SelectMany(l => l.Components).FirstOrDefault(c => c.TagName == node.Name);
    }

    public string? CodeBehindPath(DocumentSession doc)
    {
        if (doc.Kind != DocumentKind.Razor) return null;
        var p = doc.Path + ".cs";
        return File.Exists(p) || Documents.Any(d => d.Path.Equals(p, StringComparison.OrdinalIgnoreCase)) ? p : null;
    }

    private static readonly string[] RazorDefaultUsings =
    {
        "System", "System.Collections.Generic", "System.Linq", "System.Net.Http", "System.Threading.Tasks",
        "Microsoft.AspNetCore.Components", "Microsoft.AspNetCore.Components.Forms", "Microsoft.AspNetCore.Components.Routing",
        "Microsoft.AspNetCore.Components.Web", "Microsoft.AspNetCore.Components.Web.Virtualization", "Microsoft.JSInterop",
    };

    /// <summary>Klassenname, Namespace und @using-Einträge (inkl. _Imports.razor) für eine .razor-Datei.</summary>
    public (string ClassName, string Namespace, List<string> Usings) RazorContextFor(DocumentSession doc)
    {
        var cls = Path.GetFileNameWithoutExtension(doc.Path);
        var proj = Solution?.AllProjects.Where(p => p.Project is not null && doc.Path.StartsWith(Path.GetDirectoryName(p.Path)! + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.Path.Length).FirstOrDefault();
        var root = proj is not null ? Path.GetDirectoryName(proj.Path)! : RootDirectory ?? Path.GetDirectoryName(doc.Path)!;
        string? ns = null;
        if (CodeBehindPath(doc) is { } cb)
        {
            var cbText = Documents.FirstOrDefault(d => d.Path.Equals(cb, StringComparison.OrdinalIgnoreCase))?.Text ?? (File.Exists(cb) ? File.ReadAllText(cb) : "");
            var m = System.Text.RegularExpressions.Regex.Match(cbText, @"^\s*namespace\s+([\w.]+)", System.Text.RegularExpressions.RegexOptions.Multiline);
            if (m.Success) ns = m.Groups[1].Value;
        }
        var usings = new List<string>(RazorDefaultUsings);
        // _Imports.razor vom Projektordner bis zum Ordner der Datei
        var dirs = new List<string>();
        for (var d = Path.GetDirectoryName(doc.Path); d is not null && d.Length >= root.Length; d = Path.GetDirectoryName(d)) dirs.Insert(0, d);
        foreach (var d in dirs)
        {
            var imp = Path.Combine(d, "_Imports.razor");
            if (!File.Exists(imp) || imp.Equals(doc.Path, StringComparison.OrdinalIgnoreCase)) continue;
            var text = Documents.FirstOrDefault(x => x.Path.Equals(imp, StringComparison.OrdinalIgnoreCase))?.Text ?? File.ReadAllText(imp);
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"^\s*@using\s+([^\r\n;]+)", System.Text.RegularExpressions.RegexOptions.Multiline))
                usings.Add(m.Groups[1].Value.Trim());
            var nsm = System.Text.RegularExpressions.Regex.Match(text, @"^\s*@namespace\s+([\w.]+)", System.Text.RegularExpressions.RegexOptions.Multiline);
            if (nsm.Success && ns is null) ns = nsm.Groups[1].Value + string.Concat(Path.GetRelativePath(d, Path.GetDirectoryName(doc.Path)!).Split(Path.DirectorySeparatorChar).Where(x => x != ".").Select(x => "." + x));
        }
        if (ns is null)
        {
            var rootNs = proj?.Project?.RootNamespace ?? Path.GetFileName(root);
            var rel = Path.GetRelativePath(root, Path.GetDirectoryName(doc.Path)!);
            ns = rootNs + (rel == "." ? "" : string.Concat(rel.Split(Path.DirectorySeparatorChar).Select(x => "." + x.Replace(' ', '_').Replace('-', '_'))));
        }
        return (cls, ns, usings);
    }

    /// <summary>Ist der Wert dieses Komponenten-Attributs C# (alles außer string-Parametern)?</summary>
    public bool IsCSharpParameter(string tag, string attr)
    {
        var def = AllComponents.FirstOrDefault(c => c.TagName == tag) ?? Libraries.SelectMany(l => l.Components).FirstOrDefault(c => c.TagName == tag);
        var name = attr.StartsWith("@bind-", StringComparison.Ordinal) ? attr[6..].Split(':')[0] : attr;
        var p = def?.FindParameter(name);
        if (p is null) return false;
        var t = p.Type.Trim().TrimEnd('?');
        return t is not ("string" or "String" or "System.String");
    }

    /// <summary>Member der Komponente aus @code und .razor.cs (offene Version bevorzugt).</summary>
    public IReadOnlyList<CodeMember> MembersFor(DocumentSession doc)
    {
        var cbPath = CodeBehindPath(doc);
        var cbSession = cbPath is null ? null : Documents.FirstOrDefault(x => x.Path.Equals(cbPath, StringComparison.OrdinalIgnoreCase));
        var stamp = $"{doc.Version}|{cbSession?.Version.ToString() ?? (cbPath is null ? "-" : SafeWriteTime(cbPath).Ticks.ToString())}";
        if (_memberCache.TryGetValue(doc.Path, out var cached) && cached.Stamp == stamp) return cached.Members;
        var members = AnalyzeMembers(doc);
        _memberCache[doc.Path] = (stamp, members);
        return members;
    }

    private readonly Dictionary<string, (string Stamp, IReadOnlyList<CodeMember> Members)> _memberCache = new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<CodeMember> AnalyzeMembers(DocumentSession doc)
    {
        var list = new List<CodeMember>();
        if (doc.Doc is { CodeBlocks.Count: > 0 } d)
            list.AddRange(CodeInspector.AnalyzeCodeBlock(string.Join('\n', d.CodeBlocks), "@code"));
        var cb = CodeBehindPath(doc);
        if (cb is not null)
        {
            var text = Documents.FirstOrDefault(x => x.Path.Equals(cb, StringComparison.OrdinalIgnoreCase))?.Text ?? ReadText(cb);
            if (text is not null)
                list.AddRange(CodeInspector.AnalyzeFile(text, Path.GetFileNameWithoutExtension(doc.Path), Path.GetFileName(cb)));
        }
        return list;
    }

    /// <summary>Legt eine Methode an: im Code-Behind, falls vorhanden, sonst im @code-Block.</summary>
    public string AddMethod(DocumentSession doc, string methodSource)
    {
        var cb = CodeBehindPath(doc);
        if (cb is not null)
        {
            var session = Open(cb, activate: false)!;
            var updated = CodeInspector.AddMethodToFile(session.Text, Path.GetFileNameWithoutExtension(doc.Path), methodSource);
            session.SetText(updated, ChangeOrigin.Code);
            return Path.GetFileName(cb);
        }
        doc.ReplaceCodeBlocks(CodeInspector.AddMethodToCodeBlocks(doc.Doc!.CodeBlocks, methodSource));
        return "@code";
    }

    public string AddField(DocumentSession doc, string fieldSource) => AddMethod(doc, fieldSource);

    /// <summary>Freier Methodenname: Basis, Basis2, Basis3 …</summary>
    public string UniqueMemberName(DocumentSession doc, string baseName)
    {
        var names = MembersFor(doc).Select(m => m.Name).ToHashSet();
        if (!names.Contains(baseName)) return baseName;
        for (var i = 2; ; i++) if (!names.Contains(baseName + i)) return baseName + i;
    }

    // ------------------------------------------------------------------
    // Scoped CSS (Zustände / Breakpoints)
    // ------------------------------------------------------------------

    public DocumentSession ScopedCssSession(DocumentSession razor)
    {
        var path = razor.Path + ".css";
        var open = Documents.FirstOrDefault(d => d.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (open is not null) return open;
        if (!File.Exists(path))
        {
            File.WriteAllText(path, "");
            ScheduleRescan();
        }
        return Open(path, activate: false)!;
    }

    // ------------------------------------------------------------------
    // Vorschau
    // ------------------------------------------------------------------

    /// <summary>Gescoptes Projekt-CSS für die Designer-Fläche (inkl. ungespeicherter Änderungen).</summary>
    public string PreviewCss
    {
        get
        {
            if (_previewCss is not null) return _previewCss;
            if (Project is null) return _previewCss = "";
            var sb = new System.Text.StringBuilder();
            var wwwroot = Project.WwwRoot;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, content) in Project.PreviewStyles)
            {
                seen.Add(path);
                var text = Documents.FirstOrDefault(d => d.Path.Equals(path, StringComparison.OrdinalIgnoreCase))?.Text ?? content;
                sb.Append(ScopeFile(path, text, wwwroot)).Append('\n');
            }
            foreach (var d in Documents.Where(d => d.Kind == DocumentKind.Css && !seen.Contains(d.Path)))
                sb.Append(ScopeFile(d.Path, d.Text, wwwroot)).Append('\n');
            return _previewCss = sb.ToString();
        }
    }

    private static string ScopeFile(string path, string css, string? wwwroot)
    {
        var dir = Path.GetDirectoryName(path)!;
        return CssScoper.Scope(css, CanvasScope, url => AssetUrl(Path.GetFullPath(Path.Combine(dir, url.Split('?', '#')[0])), wwwroot) ?? url);
    }

    /// <summary>Wandelt eine Datei unter wwwroot in eine Vorschau-URL um.</summary>
    public static string? AssetUrl(string fullPath, string? wwwroot)
    {
        if (wwwroot is null) return null;
        var rel = Path.GetRelativePath(wwwroot, fullPath);
        if (rel.StartsWith("..", StringComparison.Ordinal)) return null;
        return $"https://{WpfHostBridge.AssetsHost}/" + rel.Replace('\\', '/');
    }

    /// <summary>src/href aus Markup (relativ zu wwwroot) für die Vorschau.</summary>
    public string? PreviewUrl(string? src)
    {
        if (string.IsNullOrWhiteSpace(src) || src.Contains('@')) return null;
        if (src.StartsWith("http", StringComparison.OrdinalIgnoreCase) || src.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return src;
        var root = Project?.WwwRoot;
        if (root is null) return null;
        return AssetUrl(Path.GetFullPath(Path.Combine(root, src.TrimStart('/', '~').Replace('/', Path.DirectorySeparatorChar))), root);
    }

    /// <summary>Geparstes Markup einer Projekt-Komponente für die Vorschau (gecacht).</summary>
    public RazorDocument? ComponentPreview(string name)
    {
        if (Project?.Components.TryGetValue(name, out var info) != true) return null;
        var open = Documents.FirstOrDefault(d => d.Path.Equals(info!.FilePath, StringComparison.OrdinalIgnoreCase));
        if (open?.Doc is not null) return open.Doc;
        if (_previewCache.TryGetValue(info!.FilePath, out var cached)) return cached.Doc;
        var text = ReadText(info.FilePath);
        if (text is null) return null;
        var doc = RazorParser.Parse(text).Document;
        _previewCache[info.FilePath] = (0, doc);
        return doc;
    }

    // ------------------------------------------------------------------
    // Neue Dateien
    // ------------------------------------------------------------------

    public string? CreateComponent(string folder, string name, string? route, bool codeBehind, bool scopedCss)
    {
        if (Project is null) return null;
        name = name.Trim();
        if (name.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)) name = name[..^6];
        if (name.Length == 0 || !char.IsUpper(name[0]) || name.Any(c => !char.IsLetterOrDigit(c) && c != '_'))
        {
            _ui.ShowToast(_l["toast.badComponentName"], ToastKind.Warning, 5000);
            return null;
        }
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name + ".razor");
        if (File.Exists(path))
        {
            _ui.ShowToast(_l.F("toast.exists", name + ".razor"), ToastKind.Warning);
            return null;
        }

        var sb = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(route)) sb.Append($"@page \"/{route.Trim().TrimStart('/')}\"\n\n");
        sb.Append(!string.IsNullOrWhiteSpace(route) ? $"<PageTitle>{name}</PageTitle>\n\n" : "");
        sb.Append($"<div class=\"{ToKebab(name)}\">\n    <h3>{name}</h3>\n</div>\n");
        if (!codeBehind) sb.Append("\n@code {\n}\n");
        File.WriteAllText(path, sb.ToString());

        if (codeBehind)
        {
            var ns = GuessNamespace(folder);
            File.WriteAllText(path + ".cs",
                $"namespace {ns};\n\npublic partial class {name}\n{{\n}}\n");
        }
        if (scopedCss) File.WriteAllText(path + ".css", $".{ToKebab(name)} {{\n}}\n");

        Project.Rescan();
        Changed?.Invoke();
        Open(path);
        return path;
    }

    public string GuessNamespace(string folder)
    {
        if (Project is null) return "App";
        var rootNs = Project.ProjectFile is null ? Project.Name : Path.GetFileNameWithoutExtension(Project.ProjectFile);
        var rel = Path.GetRelativePath(Project.RootDirectory, folder);
        if (rel == "." || rel.StartsWith("..", StringComparison.Ordinal)) return rootNs;
        return rootNs + "." + string.Join('.', rel.Split(Path.DirectorySeparatorChar).Where(p => p.Length > 0));
    }

    public static string ToKebab(string name)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0) sb.Append('-');
            sb.Append(char.ToLowerInvariant(name[i]));
        }
        return sb.ToString();
    }

    public void Dispose() => _watcher?.Dispose();
}
