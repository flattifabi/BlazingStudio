using System.Diagnostics;
using System.Text;

namespace BlazorStudio.Core.Git;

public sealed record GitResult(int ExitCode, string Output, string Error)
{
    public bool Ok => ExitCode == 0;

    public string Message => (Error.Trim().Length > 0 ? Error : Output).Trim();
}

/// <summary>Eine geänderte Datei: X = Index (gestaged), Y = Arbeitsverzeichnis.</summary>
public sealed record GitChange(string Path, char Index, char Work, string? OldPath = null)
{
    public bool IsUntracked => Index == '?';

    public bool IsConflict => Index == 'U' || Work == 'U' || Index == 'A' && Work == 'A' || Index == 'D' && Work == 'D';

    public bool HasStaged => Index is not ' ' and not '?' && !IsConflict;

    public bool HasUnstaged => Work != ' ' || IsUntracked;

    /// <summary>Anzeige-Status für eine Seite: A/M/D/R/?/U.</summary>
    public char StatusFor(bool staged) => IsConflict ? 'U' : IsUntracked ? '?' : staged ? Index : Work;
}

public sealed record GitStatus(string? Branch, string? Upstream, int Ahead, int Behind, IReadOnlyList<GitChange> Changes, bool NoCommits)
{
    public static GitStatus Empty { get; } = new(null, null, 0, 0, Array.Empty<GitChange>(), false);
}

public sealed record GitBranch(string Name, bool IsCurrent, bool IsRemote, string? Upstream);

public sealed record GitCommit(string Hash, string ShortHash, string Author, string When, string Subject, string Refs);

/// <summary>Dünne Hülle um die git-CLI (muss im PATH sein) inkl. Parser für status/branch/log.</summary>
public static class GitClient
{
    public static async Task<GitResult> RunAsync(string repo, IEnumerable<string> args, CancellationToken ct = default, string? stdin = null)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.quotepath=false");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("color.ui=never");
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["LC_ALL"] = "C";
        try
        {
            using var p = Process.Start(psi)!;
            if (stdin is not null)
            {
                await p.StandardInput.WriteAsync(stdin);
                p.StandardInput.Close();
            }
            var outTask = p.StandardOutput.ReadToEndAsync(ct);
            var errTask = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            return new GitResult(p.ExitCode, await outTask, await errTask);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            return new GitResult(-1, "", "git: " + e.Message);
        }
    }

    public static Task<GitResult> RunAsync(string repo, params string[] args) => RunAsync(repo, args, CancellationToken.None);

    /// <summary>Repository-Wurzel (oder null, wenn kein Git-Repo).</summary>
    public static async Task<string?> FindRootAsync(string dir)
    {
        var r = await RunAsync(dir, "rev-parse", "--show-toplevel");
        return r.Ok ? Path.GetFullPath(r.Output.Trim()) : null;
    }

    public static async Task<GitStatus> StatusAsync(string repo)
    {
        var r = await RunAsync(repo, "status", "--porcelain=v1", "-b", "-uall");
        return r.Ok ? ParseStatus(r.Output) : GitStatus.Empty;
    }

    public static GitStatus ParseStatus(string output)
    {
        string? branch = null, upstream = null;
        int ahead = 0, behind = 0;
        var noCommits = false;
        var changes = new List<GitChange>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length < 3) continue;
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                var h = line[3..];
                if (h.StartsWith("No commits yet on ", StringComparison.Ordinal)) { noCommits = true; branch = h[18..].Trim(); continue; }
                if (h.StartsWith("HEAD (no branch)", StringComparison.Ordinal)) { branch = "HEAD"; continue; }
                var bracket = h.IndexOf(" [", StringComparison.Ordinal);
                var info = bracket >= 0 ? h[(bracket + 2)..].TrimEnd(']') : "";
                var names = bracket >= 0 ? h[..bracket] : h;
                var dots = names.IndexOf("...", StringComparison.Ordinal);
                branch = dots >= 0 ? names[..dots] : names;
                upstream = dots >= 0 ? names[(dots + 3)..] : null;
                foreach (var part in info.Split(',', StringSplitOptions.TrimEntries))
                {
                    if (part.StartsWith("ahead ", StringComparison.Ordinal)) int.TryParse(part[6..], out ahead);
                    else if (part.StartsWith("behind ", StringComparison.Ordinal)) int.TryParse(part[7..], out behind);
                }
                continue;
            }
            var x = line[0];
            var y = line[1];
            var path = Unquote(line[3..]);
            string? old = null;
            var arrow = path.IndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0 && x is 'R' or 'C')
            {
                old = Unquote(path[..arrow]);
                path = Unquote(path[(arrow + 4)..]);
            }
            changes.Add(new GitChange(path, x, y, old));
        }
        return new GitStatus(branch, upstream, ahead, behind, changes, noCommits);
    }

    public static async Task<IReadOnlyList<GitBranch>> BranchesAsync(string repo)
    {
        var list = new List<GitBranch>();
        var r = await RunAsync(repo, "branch", "--format=%(refname:short)\t%(HEAD)\t%(upstream:short)");
        if (r.Ok) list.AddRange(ParseBranches(r.Output, remote: false));
        var rr = await RunAsync(repo, "branch", "-r", "--format=%(refname:short)\t \t");
        if (rr.Ok) list.AddRange(ParseBranches(rr.Output, remote: true).Where(b => !b.Name.EndsWith("/HEAD", StringComparison.Ordinal) && b.Name.Contains('/')));
        return list;
    }

    public static IEnumerable<GitBranch> ParseBranches(string output, bool remote)
    {
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            var parts = line.Split('\t');
            var name = parts[0].Trim();
            if (name.Length == 0 || name.StartsWith('(')) continue;
            yield return new GitBranch(name, parts.Length > 1 && parts[1].Trim() == "*", remote, parts.Length > 2 && parts[2].Trim().Length > 0 ? parts[2].Trim() : null);
        }
    }

    public static async Task<IReadOnlyList<GitCommit>> LogAsync(string repo, int count = 150)
    {
        var r = await RunAsync(repo, "log", "-n", count.ToString(System.Globalization.CultureInfo.InvariantCulture), "--date=relative", "--pretty=format:%H%x1f%h%x1f%an%x1f%ad%x1f%s%x1f%D%x1e");
        return r.Ok ? ParseLog(r.Output) : Array.Empty<GitCommit>();
    }

    public static IReadOnlyList<GitCommit> ParseLog(string output) =>
        output.Split('\x1e', StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.Trim('\r', '\n').Split('\x1f'))
            .Where(p => p.Length >= 5)
            .Select(p => new GitCommit(p[0], p[1], p[2], p[3], p[4], p.Length > 5 ? p[5] : ""))
            .ToList();

    /// <summary>Unified Diff einer Datei (gestaged oder Arbeitsverzeichnis; unversionierte Dateien als komplett neu).</summary>
    public static async Task<string> DiffAsync(string repo, GitChange change, bool staged)
    {
        if (change.IsUntracked)
        {
            var full = Path.Combine(repo, change.Path);
            if (!File.Exists(full)) return "";
            var lines = File.ReadAllLines(full);
            return $"--- /dev/null\n+++ b/{change.Path}\n@@ -0,0 +1,{lines.Length} @@\n" + string.Join("\n", lines.Select(l => "+" + l));
        }
        var args = new List<string> { "diff", "--no-ext-diff" };
        if (staged) args.Add("--cached");
        args.Add("--");
        args.Add(change.Path);
        var r = await RunAsync(repo, args);
        return r.Output;
    }

    private static string Unquote(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') s = s[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
        return s;
    }
}
