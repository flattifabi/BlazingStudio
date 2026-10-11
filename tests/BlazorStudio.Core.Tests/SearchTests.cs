using BlazorStudio.Core.Search;
using Xunit;

namespace BlazorStudio.Core.Tests;

public class SearchTests
{
    private static string Sandbox()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bs_search_" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(dir, "Pages"));
        Directory.CreateDirectory(Path.Combine(dir, "obj"));
        File.WriteAllText(Path.Combine(dir, "Program.cs"), "var count = 1;\nCount++;\n// counter\n");
        File.WriteAllText(Path.Combine(dir, "Pages", "Home.razor"), "<h1>@Count</h1>\n@code { int Count; }\n");
        File.WriteAllText(Path.Combine(dir, "obj", "gen.cs"), "Count Count Count");
        File.WriteAllBytes(Path.Combine(dir, "data.bin"), new byte[] { 67, 0, 111, 117, 110, 116 });
        return dir;
    }

    [Fact]
    public void Finds_case_insensitive_and_skips_obj_and_binaries()
    {
        var dir = Sandbox();
        var r = FileSearch.Search(new[] { dir }, new SearchOptions("count"));
        Assert.Null(r.Error);
        Assert.DoesNotContain(r.Hits, h => h.File.Contains("obj"));
        Assert.DoesNotContain(r.Hits, h => h.File.EndsWith(".bin"));
        Assert.Equal(5, r.Hits.Count); // count, Count, counter, @Count, Count (code)
        var second = r.Hits.First(h => h.File.EndsWith("Program.cs") && h.Line == 2);
        Assert.Equal(1, second.Column);
        Assert.Equal("Count++;", second.LineText);
    }

    [Fact]
    public void Match_case_whole_word_and_mask()
    {
        var dir = Sandbox();
        var r = FileSearch.Search(new[] { dir }, new SearchOptions("Count", MatchCase: true, WholeWord: true, FileMask: "*.razor"));
        Assert.Equal(2, r.Hits.Count);
        Assert.All(r.Hits, h => Assert.EndsWith(".razor", h.File));
        var none = FileSearch.Search(new[] { dir }, new SearchOptions("count", WholeWord: true, FileMask: "*.cs;!Program.cs"));
        Assert.Empty(none.Hits);
    }

    [Fact]
    public void Regex_search_and_replace_with_groups()
    {
        var o = new SearchOptions(@"int (\w+);", UseRegex: true);
        var (text, n) = FileSearch.ReplaceText("@code { int Count; int Total; }", o, "private int $1 = 0;");
        Assert.Equal(2, n);
        Assert.Equal("@code { private int Count = 0; private int Total = 0; }", text);
        var bad = FileSearch.Search(new[] { Path.GetTempPath() }, new SearchOptions("(", UseRegex: true));
        Assert.NotNull(bad.Error);
    }

    [Fact]
    public void Plain_replace_is_literal()
    {
        var (text, n) = FileSearch.ReplaceText("a.b a.b axb", new SearchOptions("a.b"), "$1");
        Assert.Equal(2, n);
        Assert.Equal("$1 $1 axb", text);
    }
}
