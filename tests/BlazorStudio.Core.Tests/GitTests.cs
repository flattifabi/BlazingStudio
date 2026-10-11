using BlazorStudio.Core.Git;
using Xunit;

namespace BlazorStudio.Core.Tests;

public class GitTests
{
    [Fact]
    public void Parses_status_with_branch_and_changes()
    {
        var s = GitClient.ParseStatus("## main...origin/main [ahead 2, behind 1]\nM  src/A.cs\n M src/B.razor\nR  old.cs -> new.cs\n?? \"Neu Ordner/x.txt\"\nUU conflict.cs\n");
        Assert.Equal("main", s.Branch);
        Assert.Equal("origin/main", s.Upstream);
        Assert.Equal(2, s.Ahead);
        Assert.Equal(1, s.Behind);
        Assert.Equal(5, s.Changes.Count);
        Assert.True(s.Changes[0].HasStaged);
        Assert.False(s.Changes[0].HasUnstaged);
        Assert.True(s.Changes[1].HasUnstaged);
        Assert.Equal("old.cs", s.Changes[2].OldPath);
        Assert.Equal("new.cs", s.Changes[2].Path);
        Assert.True(s.Changes[3].IsUntracked);
        Assert.Equal("Neu Ordner/x.txt", s.Changes[3].Path);
        Assert.True(s.Changes[4].IsConflict);
    }

    [Fact]
    public void Parses_fresh_repo_and_log()
    {
        var s = GitClient.ParseStatus("## No commits yet on main\n?? a.txt\n");
        Assert.True(s.NoCommits);
        Assert.Equal("main", s.Branch);
        var log = GitClient.ParseLog("abc\u001fab\u001fFabi\u001f2 hours ago\u001fInitial\u001fHEAD -> main\u001e\ndef\u001fde\u001fFabi\u001f3 days ago\u001fFix\u001f\u001e");
        Assert.Equal(2, log.Count);
        Assert.Equal("HEAD -> main", log[0].Refs);
        Assert.Equal("Fix", log[1].Subject);
    }

    [Fact]
    public async Task Works_against_real_repository()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bs_git_" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        if (!(await GitClient.RunAsync(dir, "init", "-b", "main")).Ok) return; // git nicht installiert
        await GitClient.RunAsync(dir, "config", "user.email", "t@t");
        await GitClient.RunAsync(dir, "config", "user.name", "T");
        File.WriteAllText(Path.Combine(dir, "a.txt"), "hallo\n");
        var st = await GitClient.StatusAsync(dir);
        Assert.Single(st.Changes);
        await GitClient.RunAsync(dir, "add", "-A");
        Assert.True((await GitClient.RunAsync(dir, "commit", "-m", "init")).Ok);
        File.WriteAllText(Path.Combine(dir, "a.txt"), "hallo welt\n");
        st = await GitClient.StatusAsync(dir);
        var diff = await GitClient.DiffAsync(dir, st.Changes[0], staged: false);
        Assert.Contains("+hallo welt", diff);
        var branches = await GitClient.BranchesAsync(dir);
        Assert.Contains(branches, b => b.Name == "main" && b.IsCurrent);
        Assert.Single(await GitClient.LogAsync(dir));
    }
}
