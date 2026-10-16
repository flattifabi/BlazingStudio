using BlazorStudio.Core.Assistant;

namespace BlazorStudio.Core.Tests;

public class ClaudeWebTests
{
    [Theory]
    [InlineData("https://github.com/flattifabi/BlazingStudio.git", "flattifabi/BlazingStudio")]
    [InlineData("https://github.com/flattifabi/BlazingStudio", "flattifabi/BlazingStudio")]
    [InlineData("git@github.com:team/shop-app.git", "team/shop-app")]
    [InlineData("https://user@github.com/a/b.c/", "a/b.c")]
    [InlineData("https://gitlab.com/a/b.git", null)]
    [InlineData("", null)]
    public void Repo_from_remote(string url, string? repo) => Assert.Equal(repo, ClaudeWeb.GitHubRepo(url));

    [Fact]
    public void Prompt_contains_context()
    {
        var p = ClaudeWeb.TaskPrompt("a/b", "main", "src/Cart.cs", "  Füge Rabatt hinzu ");
        Assert.Equal("Repository: a/b, Branch: main\nDatei im Editor: src/Cart.cs\n\nFüge Rabatt hinzu", p);
        Assert.Equal("Nur Text", ClaudeWeb.TaskPrompt(null, "HEAD", null, "Nur Text"));
    }

    [Theory]
    [InlineData("origin/claude/fix-login", true)]
    [InlineData("origin/main", false)]
    [InlineData("claude/x", false)]
    public void Claude_branches(string name, bool yes) => Assert.Equal(yes, ClaudeWeb.IsClaudeBranch(name));
}
