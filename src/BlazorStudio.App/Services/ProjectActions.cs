using System.Runtime.InteropServices;
using BlazorStudio.App.Components;
using BlazorStudio.Core.Build;
using BlazorStudio.Core.Solution;
using BlazorStudio.Core.Templates;

namespace BlazorStudio.App.Services;

/// <summary>
/// Aktionen aus dem Solution-Explorer wie in Rider/VS: Build/Rebuild/Clean, Entladen/Neu laden,
/// Projekte und Verweise verwalten, Dateien/Ordner anlegen, umbenennen, löschen (Papierkorb).
/// </summary>
public sealed class ProjectActions
{
    private readonly Workspace _ws;
    private readonly RunService _run;
    private readonly UiService _ui;
    private readonly Localizer _l;
    private readonly SettingsService _settings;
    private readonly IHostBridge _host;
    private readonly TerminalService _term;

    public ProjectActions(Workspace ws, RunService run, UiService ui, Localizer l, SettingsService settings, IHostBridge host, TerminalService term)
    {
        _ws = ws;
        _run = run;
        _ui = ui;
        _l = l;
        _settings = settings;
        _host = host;
        _term = term;
    }

    /// <summary>Gibt es eine echte .sln/.slnx-Datei (sonst ist die Projektmappe nur virtuell)?</summary>
    public bool HasSolutionFile => _ws.Solution is { } s && File.Exists(s.Path);

    // ------------------------------------------------------------------
    // Build
    // ------------------------------------------------------------------

    public async Task BuildAsync(string target, BuildAction action)
    {
        BottomPanel.Show?.Invoke(BottomTab.Output);
        var ok = await _run.RunBuildActionAsync(target, action);
        var name = Path.GetFileNameWithoutExtension(target);
        _ui.ShowToast(ok ? _l.F("build.ok", _l["build." + action], name) : _l.F("build.failed", _l["build." + action], name), ok ? ToastKind.Success : ToastKind.Error);
        if (!ok && _run.ErrorCount > 0) BottomPanel.Show?.Invoke(BottomTab.Problems);
        if (action is BuildAction.Restore or BuildAction.Build or BuildAction.Rebuild or BuildAction.CleanBuild) _ws.Rescan();
    }

    /// <summary>Ziel für Solution-weite Aktionen: .sln/.slnx oder (ohne Solution-Datei) das Startprojekt.</summary>
    public string? SolutionTarget => HasSolutionFile ? _ws.Solution!.Path : _ws.Project?.ProjectFile;

    // ------------------------------------------------------------------
    // Entladen / Neu laden
    // ------------------------------------------------------------------

    private List<string> UnloadedList()
    {
        var key = _ws.Solution?.Path ?? "";
        if (!_settings.Current.UnloadedProjects.TryGetValue(key, out var list)) _settings.Current.UnloadedProjects[key] = list = new List<string>();
        return list;
    }

    public bool IsUnloaded(SolutionItem item) =>
        _ws.Solution is { } s && _settings.Current.UnloadedProjects.TryGetValue(s.Path, out var l) && l.Contains(item.Path, StringComparer.OrdinalIgnoreCase);

    public async Task UnloadAsync(SolutionItem item)
    {
        if (_ws.StartupItem == item)
        {
            var other = _ws.Solution!.AllProjects.FirstOrDefault(p => p != item && p.Project is not null && !IsUnloaded(p));
            if (other is null)
            {
                _ui.ShowToast(_l["sol.cantUnloadStartup"], ToastKind.Warning);
                return;
            }
            await _ws.SetStartupProjectAsync(other);
        }
        // Offene Dateien des Projekts schließen (fragt bei ungespeicherten Änderungen)
        var dir = Path.GetDirectoryName(item.Path)! + Path.DirectorySeparatorChar;
        foreach (var d in _ws.Documents.Where(d => d.Path.StartsWith(dir, StringComparison.OrdinalIgnoreCase)).ToList())
            if (!await _ws.CloseAsync(d)) return;
        var list = UnloadedList();
        if (!list.Contains(item.Path, StringComparer.OrdinalIgnoreCase)) list.Add(item.Path);
        _settings.Save();
        _ws.NotifyChanged();
        _ui.ShowToast(_l.F("sol.unloaded", item.Name));
    }

    public void Reload(SolutionItem item)
    {
        UnloadedList().RemoveAll(p => p.Equals(item.Path, StringComparison.OrdinalIgnoreCase));
        _settings.Save();
        if (File.Exists(item.Path)) item.Project = ProjectModel.Load(item.Path);
        _ws.Rescan();
        _ui.ShowToast(_l.F("sol.reloaded", item.Name), ToastKind.Success);
    }

    // ------------------------------------------------------------------
    // Projekte in der Solution
    // ------------------------------------------------------------------

    /// <summary>Stellt sicher, dass es eine .slnx gibt (für Ordner ohne Projektmappe) und liefert ihren Pfad.</summary>
    private async Task<string?> EnsureSolutionFileAsync()
    {
        if (HasSolutionFile) return _ws.Solution!.Path;
        var dir = _ws.RootDirectory;
        if (dir is null) return null;
        var name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar));
        if (await _run.ExecAsync(dir, "new", "sln", "-n", name, "--format", "slnx") != 0) return null;
        var sln = Path.Combine(dir, name + ".slnx");
        foreach (var p in _ws.Solution?.AllProjects.Where(p => File.Exists(p.Path)) ?? Enumerable.Empty<SolutionItem>())
            await _run.ExecAsync(dir, "sln", sln, "add", p.Path);
        return File.Exists(sln) ? sln : null;
    }

    public async Task AddExistingProjectAsync()
    {
        var file = await _host.PickFileAsync(_l["sol.addExisting"], "C# (*.csproj)|*.csproj", _ws.RootDirectory);
        if (file is null) return;
        var sln = await EnsureSolutionFileAsync();
        if (sln is null) return;
        BottomPanel.Show?.Invoke(BottomTab.Output);
        if (await _run.ExecAsync(Path.GetDirectoryName(sln)!, "sln", sln, "add", file) == 0) await ReloadSolutionAsync(sln);
    }

    public async Task NewProjectAsync(string template, string name)
    {
        var sln = await EnsureSolutionFileAsync();
        if (sln is null) return;
        var root = Path.GetDirectoryName(sln)!;
        var dir = Path.Combine(root, name);
        if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any())
        {
            _ui.ShowToast(_l.F("sol.folderExists", name), ToastKind.Error);
            return;
        }
        BottomPanel.Show?.Invoke(BottomTab.Output);
        if (await _run.ExecAsync(root, "new", template, "-n", name, "-o", dir) != 0) return;
        var csproj = Directory.GetFiles(dir, "*.csproj").FirstOrDefault();
        if (csproj is null) return;
        if (await _run.ExecAsync(root, "sln", sln, "add", csproj) == 0)
        {
            await ReloadSolutionAsync(sln);
            _ui.ShowToast(_l.F("sol.projectCreated", name), ToastKind.Success);
        }
    }

    public async Task RemoveFromSolutionAsync(SolutionItem item)
    {
        if (!HasSolutionFile) return;
        var ok = await _ui.ShowDialogAsync(new DialogRequest
        {
            Title = _l["sol.removeProject"],
            Message = _l.F("sol.removeProjectText", item.Name),
            OkText = _l["action.remove"],
            CancelText = _l["action.cancel"],
            Danger = true,
        });
        if (ok != DialogResult.Ok) return;
        var sln = _ws.Solution!.Path;
        if (await _run.ExecAsync(Path.GetDirectoryName(sln)!, "sln", sln, "remove", item.Path) == 0) await ReloadSolutionAsync(sln);
    }

    public async Task AddProjectReferenceAsync(SolutionItem project, SolutionItem target)
    {
        BottomPanel.Show?.Invoke(BottomTab.Output);
        if (await _run.ExecAsync(Path.GetDirectoryName(project.Path)!, "add", project.Path, "reference", target.Path) == 0) RefreshProject(project);
    }

    public async Task RemoveProjectReferenceAsync(SolutionItem project, string reference)
    {
        if (await _run.ExecAsync(Path.GetDirectoryName(project.Path)!, "remove", project.Path, "reference", reference) == 0) RefreshProject(project);
    }

    public async Task RemovePackageAsync(SolutionItem project, string packageId)
    {
        var ok = await _ui.ShowDialogAsync(new DialogRequest
        {
            Title = _l["sol.removePackage"],
            Message = _l.F("sol.removePackageText", packageId, project.Name),
            OkText = _l["action.remove"],
            CancelText = _l["action.cancel"],
            Danger = true,
        });
        if (ok != DialogResult.Ok) return;
        BottomPanel.Show?.Invoke(BottomTab.Output);
        if (await _run.ExecAsync(Path.GetDirectoryName(project.Path)!, "remove", project.Path, "package", packageId) == 0) RefreshProject(project);
    }

    // ------------------------------------------------------------------
    // Projektversion (<Version> in der .csproj)
    // ------------------------------------------------------------------

    private string? ProjectText(string csproj) =>
        _ws.Documents.FirstOrDefault(d => d.Path.Equals(csproj, StringComparison.OrdinalIgnoreCase))?.Text
        ?? (File.Exists(csproj) ? File.ReadAllText(csproj) : null);

    /// <summary>Version laut Projektdatei; null = nicht gesetzt (MSBuild nimmt dann 1.0.0).</summary>
    public string? VersionOf(string csproj) => ProjectText(csproj) is { } t ? ProjectVersion.Read(t) : null;

    /// <summary>Setzt &lt;Version&gt; – in der geöffneten Datei (mit Rückgängig) oder direkt auf der Platte.</summary>
    public bool SetVersion(string csproj, string version)
    {
        var text = ProjectText(csproj);
        if (text is null) return false;
        string updated;
        try
        {
            updated = ProjectVersion.Write(text, version);
        }
        catch (Exception e) when (e is ArgumentException or InvalidDataException)
        {
            _ui.ShowToast(e.Message, ToastKind.Error, 5000);
            return false;
        }
        if (!ProjectVersion.IsWellFormed(updated))
        {
            _ui.ShowToast(_l["ver.invalidXml"], ToastKind.Error, 5000);
            return false;
        }
        if (_ws.Documents.FirstOrDefault(d => d.Path.Equals(csproj, StringComparison.OrdinalIgnoreCase)) is { } doc)
        {
            doc.SetText(updated, ChangeOrigin.Code);
            _ws.Save(doc);
        }
        else
        {
            File.WriteAllText(csproj, updated);
        }
        if (_ws.Solution?.AllProjects.FirstOrDefault(p => p.Path.Equals(csproj, StringComparison.OrdinalIgnoreCase)) is { } item) RefreshProject(item);
        _ui.ShowToast(_l.F("ver.changed", Path.GetFileNameWithoutExtension(csproj), version.Trim()), ToastKind.Success, 2400);
        return true;
    }

    /// <summary>Dialog „Version ändern“ (mit Vorschlag: nächste Patch-Version).</summary>
    public async Task ChangeVersionAsync(string? csproj)
    {
        if (csproj is null || !File.Exists(csproj)) return;
        var current = VersionOf(csproj);
        var request = new DialogRequest
        {
            Title = _l.F("ver.title", Path.GetFileNameWithoutExtension(csproj)),
            Message = _l.F("ver.text", current ?? _l["ver.notSet"], ProjectVersion.Bump(current, VersionPart.Patch), ProjectVersion.Bump(current, VersionPart.Minor), ProjectVersion.Bump(current, VersionPart.Major)),
            InputValue = ProjectVersion.Bump(current, VersionPart.Patch),
            InputPlaceholder = "1.2.3",
            OkText = _l["action.save"],
            CancelText = _l["action.cancel"],
        };
        if (await _ui.ShowDialogAsync(request) != DialogResult.Ok || string.IsNullOrWhiteSpace(request.InputValue)) return;
        SetVersion(csproj, request.InputValue);
    }

    private void RefreshProject(SolutionItem project)
    {
        if (File.Exists(project.Path)) project.Project = ProjectModel.Load(project.Path);
        _ws.Rescan();
    }

    /// <summary>Projektmappe neu einlesen (nach dotnet sln add/remove), Startprojekt und offene Dateien bleiben.</summary>
    public async Task ReloadSolutionAsync(string? path = null)
    {
        await _ws.ReloadSolutionAsync(path);
    }

    // ------------------------------------------------------------------
    // Dateien & Ordner
    // ------------------------------------------------------------------

    /// <summary>Namespace für einen Ordner aus dem Projekt, zu dem er gehört.</summary>
    public string NamespaceFor(string folder)
    {
        var proj = ProjectOf(folder);
        var root = proj?.Project?.RootNamespace ?? proj?.Name ?? _ws.Project?.Name ?? "App";
        var dir = proj is not null ? Path.GetDirectoryName(proj.Path)! : _ws.RootDirectory ?? folder;
        return FileTemplates.NamespaceFor(root, dir, folder);
    }

    public SolutionItem? ProjectOf(string path) =>
        _ws.Solution?.AllProjects.Where(p => p.Project is not null && (path + Path.DirectorySeparatorChar).StartsWith(Path.GetDirectoryName(p.Path)! + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.Path.Length).FirstOrDefault();

    /// <summary>Datei(en) aus einer Vorlage anlegen und die Hauptdatei öffnen.</summary>
    public string? CreateFromTemplate(string folder, FileTemplate template, string name, TemplateOptions options)
    {
        var files = FileTemplates.Generate(template, name, options);
        var existing = files.Select(f => Path.Combine(folder, f.RelativePath)).Where(File.Exists).ToList();
        if (existing.Count > 0)
        {
            _ui.ShowToast(_l.F("newFile.exists", Path.GetFileName(existing[0])), ToastKind.Error);
            return null;
        }
        Directory.CreateDirectory(folder);
        foreach (var f in files)
        {
            var full = Path.Combine(folder, f.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, f.Content.Replace("\n", Environment.NewLine), new System.Text.UTF8Encoding(true));
        }
        var main = Path.Combine(folder, files[0].RelativePath);
        _ws.Rescan();
        _ws.Open(main);
        return main;
    }

    public async Task NewFolderAsync(string parent)
    {
        var req = new DialogRequest { Title = _l["sol.newFolder"], InputValue = "", InputPlaceholder = _l["sol.folderName"], OkText = _l["action.create"], CancelText = _l["action.cancel"] };
        if (await _ui.ShowDialogAsync(req) != DialogResult.Ok) return;
        var name = (req.InputValue ?? "").Trim();
        if (!ValidName(name)) return;
        Directory.CreateDirectory(Path.Combine(parent, name));
        _ws.Rescan();
    }

    /// <summary>Datei oder Ordner umbenennen; zugehörige Dateien (Foo.razor.cs/.css/.js) wandern mit.</summary>
    public async Task RenameAsync(string path)
    {
        var isDir = Directory.Exists(path);
        var oldName = Path.GetFileName(path);
        var req = new DialogRequest { Title = _l["sol.rename"], InputValue = oldName, OkText = _l["sol.rename"], CancelText = _l["action.cancel"] };
        if (await _ui.ShowDialogAsync(req) != DialogResult.Ok) return;
        var newName = (req.InputValue ?? "").Trim();
        if (newName == oldName || !ValidName(newName)) return;
        var target = Path.Combine(Path.GetDirectoryName(path)!, newName);
        if (File.Exists(target) || Directory.Exists(target))
        {
            _ui.ShowToast(_l.F("newFile.exists", newName), ToastKind.Error);
            return;
        }

        // Betroffene offene Dokumente vorher schließen
        var moves = new List<(string From, string To)> { (path, target) };
        if (!isDir)
        {
            foreach (var suffix in new[] { ".cs", ".css", ".js" })
            {
                if (File.Exists(path + suffix) && !File.Exists(target + suffix)) moves.Add((path + suffix, target + suffix));
            }
        }
        var reopen = new List<string>();
        foreach (var d in _ws.Documents.ToList())
        {
            foreach (var (from, to) in moves)
            {
                var hit = isDir ? d.Path.StartsWith(from + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) : d.Path.Equals(from, StringComparison.OrdinalIgnoreCase);
                if (!hit) continue;
                if (d.IsDirty) _ws.Save(d);
                reopen.Add(to + d.Path[from.Length..]);
                await _ws.CloseAsync(d);
            }
        }
        try
        {
            foreach (var (from, to) in moves)
            {
                if (isDir) Directory.Move(from, to);
                else File.Move(from, to);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _ui.ShowToast(e.Message, ToastKind.Error);
            return;
        }
        _ws.Rescan();
        foreach (var p in reopen.Where(File.Exists)) _ws.Open(p);
        if (!isDir && oldName.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) && newName.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            _ui.ShowToast(_l["sol.renameHint"], ToastKind.Info, 6000);
    }

    /// <summary>In den Papierkorb verschieben (wiederherstellbar), nach Rückfrage.</summary>
    public async Task DeleteAsync(string path)
    {
        var isDir = Directory.Exists(path);
        var companions = isDir ? new List<string>() : new[] { ".cs", ".css", ".js" }.Select(s => path + s).Where(File.Exists).ToList();
        var msg = _l.F(isDir ? "sol.deleteFolderText" : "sol.deleteFileText", Path.GetFileName(path));
        if (companions.Count > 0) msg += "\n" + _l.F("sol.deleteCompanions", string.Join(", ", companions.Select(Path.GetFileName)));
        var ok = await _ui.ShowDialogAsync(new DialogRequest { Title = _l["sol.delete"], Message = msg, OkText = _l["sol.toRecycleBin"], CancelText = _l["action.cancel"], Danger = true });
        if (ok != DialogResult.Ok) return;
        foreach (var d in _ws.Documents.Where(d => d.Path.Equals(path, StringComparison.OrdinalIgnoreCase) || companions.Contains(d.Path, StringComparer.OrdinalIgnoreCase)
                                                  || isDir && d.Path.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            d.MarkSaved();
            await _ws.CloseAsync(d);
        }
        foreach (var p in companions.Prepend(path))
        {
            if (!RecycleBin.Send(p)) _ui.ShowToast(_l.F("sol.deleteFailed", Path.GetFileName(p)), ToastKind.Error);
        }
        _ws.Rescan();
    }

    public void CopyPath(string path, bool relative)
    {
        var text = relative && _ws.RootDirectory is { } root ? Path.GetRelativePath(root, path) : path;
        _host.SetClipboard(text);
        _ui.ShowToast(_l["sol.pathCopied"], ToastKind.Success, 1400);
    }

    public void OpenTerminal(string dir)
    {
        _term.Open(Directory.Exists(dir) ? dir : Path.GetDirectoryName(dir));
        BottomPanel.Show?.Invoke(BottomTab.Terminal);
    }

    private bool ValidName(string name)
    {
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..")
        {
            _ui.ShowToast(_l["sol.invalidName"], ToastKind.Error);
            return false;
        }
        return true;
    }
}

/// <summary>Löschen in den Windows-Papierkorb (SHFileOperation mit FOF_ALLOWUNDO).</summary>
internal static class RecycleBin
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400;

    public static bool Send(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return true;
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = Path.GetFullPath(path) + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
        };
        return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted;
    }
}
