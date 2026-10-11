using BlazorStudio.Core.Git;

namespace BlazorStudio.App.Services;

/// <summary>Git-Werkzeug: Status, Stage/Unstage, Commit, Push/Pull/Fetch, Branches, Stash, Log.</summary>
public sealed class GitService
{
    private readonly Workspace _ws;
    private readonly UiService _ui;
    private readonly Localizer _l;
    private string? _forDir;
    private CancellationTokenSource? _debounce;

    public GitService(Workspace ws, UiService ui, Localizer l)
    {
        _ws = ws;
        _ui = ui;
        _l = l;
        _ws.Changed += OnWorkspaceChanged;
    }

    public event Action? Changed;

    /// <summary>Wurzel des Repositories (null = kein Git-Repo).</summary>
    public string? Root { get; private set; }

    public bool Checked { get; private set; }

    public GitStatus Status { get; private set; } = GitStatus.Empty;

    public IReadOnlyList<GitBranch> Branches { get; private set; } = Array.Empty<GitBranch>();

    public IReadOnlyList<GitCommit> Log { get; private set; } = Array.Empty<GitCommit>();

    public IReadOnlyList<string> Stashes { get; private set; } = Array.Empty<string>();

    /// <summary>Name der laufenden Aktion (für Spinner), sonst null.</summary>
    public string? Busy { get; private set; }

    /// <summary>Protokoll der ausgeführten git-Befehle (Konsole im Git-Panel).</summary>
    public List<(string Command, string Output, bool Ok)> Console { get; } = new();

    private void OnWorkspaceChanged()
    {
        var dir = _ws.RootDirectory;
        if (dir != _forDir)
        {
            _forDir = dir;
            Root = null;
            Checked = false;
            Status = GitStatus.Empty;
            _ = RefreshAsync();
            return;
        }
        // Speichern ändert den Status → entprellt nachladen
        _debounce?.Cancel();
        var cts = _debounce = new CancellationTokenSource();
        _ = Task.Delay(700, cts.Token).ContinueWith(t => { if (!t.IsCanceled && Root is not null) _ = RefreshAsync(); }, TaskScheduler.Default);
    }

    public async Task RefreshAsync(bool withLog = false)
    {
        var dir = _ws.RootDirectory;
        if (dir is null) return;
        Root ??= await GitClient.FindRootAsync(dir);
        Checked = true;
        if (Root is null)
        {
            Changed?.Invoke();
            return;
        }
        Status = await GitClient.StatusAsync(Root);
        Branches = await GitClient.BranchesAsync(Root);
        var st = await GitClient.RunAsync(Root, "stash", "list", "--pretty=format:%gd: %s");
        Stashes = st.Ok ? st.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : Array.Empty<string>();
        if (withLog || Log.Count > 0) Log = await GitClient.LogAsync(Root);
        Changed?.Invoke();
    }

    /// <summary>git-Befehl ausführen, protokollieren und danach den Status neu laden.</summary>
    public async Task<bool> RunAsync(string label, params string[] args)
    {
        if (Root is null || Busy is not null) return false;
        Busy = label;
        Changed?.Invoke();
        try
        {
            var r = await GitClient.RunAsync(Root, args);
            Console.Add(("git " + string.Join(' ', args.Select(a => a.Contains(' ') ? "\"" + a + "\"" : a)), (r.Output + r.Error).Trim(), r.Ok));
            if (Console.Count > 200) Console.RemoveAt(0);
            if (!r.Ok) _ui.ShowToast(label + ": " + FirstLine(r.Message), ToastKind.Error, 7000);
            return r.Ok;
        }
        finally
        {
            Busy = null;
            await RefreshAsync();
        }
    }

    private static string FirstLine(string s)
    {
        var lines = s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.FirstOrDefault(l => l.StartsWith("error", StringComparison.OrdinalIgnoreCase) || l.StartsWith("fatal", StringComparison.OrdinalIgnoreCase)) ?? lines.FirstOrDefault() ?? s;
    }

    public Task<bool> InitAsync() => RunInitAsync();

    private async Task<bool> RunInitAsync()
    {
        var dir = _ws.RootDirectory;
        if (dir is null) return false;
        // Bei einer Solution den Solution-Ordner nehmen
        var target = dir;
        var r = await GitClient.RunAsync(target, "init", "-b", "main");
        if (!r.Ok)
        {
            _ui.ShowToast(r.Message, ToastKind.Error);
            return false;
        }
        var ignore = Path.Combine(target, ".gitignore");
        if (!File.Exists(ignore)) await File.WriteAllTextAsync(ignore, "bin/\nobj/\n.vs/\n.idea/\n*.user\n*.suo\n*.db-shm\n*.db-wal\n");
        Root = Path.GetFullPath(target);
        _ui.ShowToast(_l["git.initDone"], ToastKind.Success);
        await RefreshAsync(withLog: true);
        return true;
    }

    public Task StageAsync(IEnumerable<string> paths) => RunAsync(_l["git.stage"], new[] { "add", "--" }.Concat(paths).ToArray());

    public Task StageAllAsync() => RunAsync(_l["git.stageAll"], "add", "-A");

    public Task UnstageAsync(IEnumerable<string> paths) =>
        Status.NoCommits
            ? RunAsync(_l["git.unstage"], new[] { "rm", "--cached", "-q", "--" }.Concat(paths).ToArray())
            : RunAsync(_l["git.unstage"], new[] { "restore", "--staged", "--" }.Concat(paths).ToArray());

    public async Task DiscardAsync(GitChange change)
    {
        var ok = await _ui.ShowDialogAsync(new DialogRequest
        {
            Title = _l["git.discardTitle"],
            Message = _l.F("git.discardText", change.Path),
            OkText = _l["git.discard"],
            CancelText = _l["action.cancel"],
            Danger = true,
        });
        if (ok != DialogResult.Ok) return;
        if (change.IsUntracked) await RunAsync(_l["git.discard"], "clean", "-f", "--", change.Path);
        else await RunAsync(_l["git.discard"], "restore", "--source=HEAD", "--staged", "--worktree", "--", change.Path);
        _ws.ReloadFromDisk(Path.Combine(Root!, change.Path));
    }

    public async Task<bool> CommitAsync(string message, bool amend, bool push)
    {
        if (string.IsNullOrWhiteSpace(message) && !amend) return false;
        // Nichts gestaged? Dann wie Rider alle Änderungen übernehmen
        if (!Status.Changes.Any(c => c.HasStaged)) await RunAsync(_l["git.stageAll"], "add", "-A");
        var args = new List<string> { "commit", "-m", message.Trim().Length > 0 ? message.Trim() : "amend" };
        if (amend)
        {
            args.Add("--amend");
            if (message.Trim().Length == 0) { args.RemoveRange(1, 2); args.Add("--no-edit"); }
        }
        var ok = await RunAsync(_l["git.commit"], args.ToArray());
        if (ok) _ui.ShowToast(_l["git.committed"], ToastKind.Success);
        if (ok && push) await PushAsync();
        return ok;
    }

    public async Task PushAsync(bool force = false)
    {
        var args = new List<string> { "push" };
        if (force) args.Add("--force-with-lease");
        if (Status.Upstream is null && Status.Branch is { } b && b != "HEAD")
        {
            args.AddRange(new[] { "-u", "origin", b });
        }
        if (await RunAsync(_l["git.push"], args.ToArray())) _ui.ShowToast(_l["git.pushed"], ToastKind.Success);
    }

    public async Task PullAsync()
    {
        if (await RunAsync(_l["git.pull"], "pull", "--no-rebase")) _ui.ShowToast(_l["git.pulled"], ToastKind.Success);
        _ws.ReloadAllFromDisk();
    }

    public Task FetchAsync() => RunAsync(_l["git.fetch"], "fetch", "--all", "--prune");

    public async Task CheckoutAsync(GitBranch branch)
    {
        if (branch.IsRemote)
        {
            var local = branch.Name[(branch.Name.IndexOf('/') + 1)..];
            if (Branches.Any(b => !b.IsRemote && b.Name == local)) await RunAsync(_l["git.checkout"], "switch", local);
            else await RunAsync(_l["git.checkout"], "switch", "--track", branch.Name);
        }
        else await RunAsync(_l["git.checkout"], "switch", branch.Name);
        _ws.ReloadAllFromDisk();
    }

    public async Task CreateBranchAsync()
    {
        var req = new DialogRequest
        {
            Title = _l["git.newBranch"],
            Message = _l.F("git.newBranchFrom", Status.Branch ?? "HEAD"),
            InputValue = "",
            InputPlaceholder = "feature/…",
            OkText = _l["action.create"],
            CancelText = _l["action.cancel"],
        };
        if (await _ui.ShowDialogAsync(req) != DialogResult.Ok) return;
        var name = (req.InputValue ?? "").Trim().Replace(' ', '-');
        if (name.Length == 0) return;
        await RunAsync(_l["git.newBranch"], "switch", "-c", name);
    }

    public async Task DeleteBranchAsync(GitBranch branch)
    {
        var res = await _ui.ShowDialogAsync(new DialogRequest
        {
            Title = _l["git.deleteBranch"],
            Message = _l.F("git.deleteBranchText", branch.Name),
            OkText = _l["action.delete"],
            AltText = _l["git.forceDelete"],
            CancelText = _l["action.cancel"],
            Danger = true,
        });
        if (res == DialogResult.Cancel) return;
        if (branch.IsRemote)
        {
            var slash = branch.Name.IndexOf('/');
            await RunAsync(_l["git.deleteBranch"], "push", branch.Name[..slash], "--delete", branch.Name[(slash + 1)..]);
        }
        else await RunAsync(_l["git.deleteBranch"], "branch", res == DialogResult.Alt ? "-D" : "-d", branch.Name);
    }

    public async Task MergeAsync(GitBranch branch)
    {
        await RunAsync(_l["git.merge"], "merge", "--no-edit", branch.Name);
        _ws.ReloadAllFromDisk();
    }

    public async Task RebaseAsync(GitBranch branch)
    {
        await RunAsync(_l["git.rebase"], "rebase", branch.Name);
        _ws.ReloadAllFromDisk();
    }

    public async Task StashAsync()
    {
        var req = new DialogRequest
        {
            Title = _l["git.stash"],
            Message = _l["git.stashText"],
            InputValue = "",
            InputPlaceholder = "WIP",
            OkText = _l["git.stash"],
            CancelText = _l["action.cancel"],
        };
        if (await _ui.ShowDialogAsync(req) != DialogResult.Ok) return;
        var msg = (req.InputValue ?? "").Trim();
        var args = new List<string> { "stash", "push", "-u" };
        if (msg.Length > 0) args.AddRange(new[] { "-m", msg });
        await RunAsync(_l["git.stash"], args.ToArray());
        _ws.ReloadAllFromDisk();
    }

    public async Task StashPopAsync(string? stash = null)
    {
        var args = new List<string> { "stash", "pop" };
        if (stash is not null) args.Add(stash.Split(':')[0]);
        await RunAsync(_l["git.unstash"], args.ToArray());
        _ws.ReloadAllFromDisk();
    }

    public async Task<string> DiffAsync(GitChange change, bool staged) => Root is null ? "" : await GitClient.DiffAsync(Root, change, staged);

    public async Task<string> ShowCommitAsync(GitCommit commit)
    {
        if (Root is null) return "";
        var r = await GitClient.RunAsync(Root, "show", "--stat", "--patch", "--no-color", commit.Hash);
        return r.Output;
    }

    public async Task RevertAsync(GitCommit c) => await RunAsync(_l["git.revert"], "revert", "--no-edit", c.Hash);

    public async Task ResetToAsync(GitCommit c, bool hard)
    {
        if (hard)
        {
            var ok = await _ui.ShowDialogAsync(new DialogRequest { Title = _l["git.resetHard"], Message = _l.F("git.resetHardText", c.ShortHash), OkText = _l["git.resetHard"], CancelText = _l["action.cancel"], Danger = true });
            if (ok != DialogResult.Ok) return;
        }
        await RunAsync(_l["git.reset"], "reset", hard ? "--hard" : "--mixed", c.Hash);
        _ws.ReloadAllFromDisk();
    }

    public Task CherryPickAsync(GitCommit c) => RunAsync(_l["git.cherryPick"], "cherry-pick", c.Hash);

    public async Task CreateTagAsync(GitCommit c)
    {
        var req = new DialogRequest { Title = _l["git.tag"], InputValue = "", InputPlaceholder = "v1.0.0", OkText = _l["action.create"], CancelText = _l["action.cancel"] };
        if (await _ui.ShowDialogAsync(req) != DialogResult.Ok || string.IsNullOrWhiteSpace(req.InputValue)) return;
        await RunAsync(_l["git.tag"], "tag", req.InputValue.Trim(), c.Hash);
    }

    /// <summary>Remote-URL hinzufügen/ändern (für Push in ein neues Repo).</summary>
    public async Task SetRemoteAsync()
    {
        var current = Root is null ? null : (await GitClient.RunAsync(Root, "remote", "get-url", "origin")).Output.Trim();
        var req = new DialogRequest { Title = _l["git.remote"], Message = _l["git.remoteText"], InputValue = current ?? "", InputPlaceholder = "https://github.com/user/repo.git", OkText = _l["action.save"], CancelText = _l["action.cancel"] };
        if (await _ui.ShowDialogAsync(req) != DialogResult.Ok || string.IsNullOrWhiteSpace(req.InputValue)) return;
        if (string.IsNullOrEmpty(current)) await RunAsync(_l["git.remote"], "remote", "add", "origin", req.InputValue.Trim());
        else await RunAsync(_l["git.remote"], "remote", "set-url", "origin", req.InputValue.Trim());
    }
}
