using System.Text;
using System.Text.RegularExpressions;

namespace BlazorStudio.Core.Assistant;

/// <summary>
/// Claude Code im Web (claude.ai/code): läuft in der Cloud mit dem Claude-Abo und arbeitet auf dem GitHub-Repository.
/// Blazor Studio öffnet nur die Seite, übergibt den Auftrag über die Zwischenablage und holt das Ergebnis per Git ab.
/// </summary>
public static partial class ClaudeWeb
{
    public const string Url = "https://claude.ai/code";

    /// <summary>Präfix der Branches, die Claude Code im Web anlegt.</summary>
    public const string BranchPrefix = "claude/";

    [GeneratedRegex(@"github\.com[:/](?<owner>[A-Za-z0-9_.-]+)/(?<repo>[A-Za-z0-9_.-]+?)(\.git)?/?$", RegexOptions.IgnoreCase)]
    private static partial Regex GitHubPattern();

    /// <summary>„owner/repo“ aus einer GitHub-Remote-URL (https oder ssh), sonst null.</summary>
    public static string? GitHubRepo(string? remoteUrl)
    {
        var m = GitHubPattern().Match((remoteUrl ?? "").Trim());
        return m.Success ? m.Groups["owner"].Value + "/" + m.Groups["repo"].Value : null;
    }

    /// <summary>Auftrag für Claude Code im Web mit Repository, Branch und aktiver Datei.</summary>
    public static string TaskPrompt(string? repo, string? branch, string? activeFile, string task)
    {
        var sb = new StringBuilder();
        if (repo is not null) sb.Append("Repository: ").Append(repo);
        if (branch is { Length: > 0 } && branch != "HEAD") sb.Append(repo is null ? "" : ", ").Append("Branch: ").Append(branch);
        if (sb.Length > 0) sb.Append('\n');
        if (activeFile is { Length: > 0 }) sb.Append("Datei im Editor: ").Append(activeFile).Append('\n');
        if (sb.Length > 0) sb.Append('\n');
        sb.Append(task.Trim());
        return sb.ToString();
    }

    /// <summary>Remote-Branches von Claude Code im Web („origin/claude/…“).</summary>
    public static bool IsClaudeBranch(string remoteBranch)
    {
        var slash = remoteBranch.IndexOf('/');
        return slash > 0 && remoteBranch[(slash + 1)..].StartsWith(BranchPrefix, StringComparison.Ordinal);
    }
}
