using BlazorStudio.Core.Highlighting;

namespace BlazorStudio.Core.Tests;

public class HighlighterTests
{
    private static string TypeAt(string text, IReadOnlyList<(int Start, int Length, int Type, int Mods)> tokens, string word, int occurrence = 0)
    {
        var idx = -1;
        for (var i = 0; i <= occurrence; i++) idx = text.IndexOf(word, idx + 1, StringComparison.Ordinal);
        var t = tokens.FirstOrDefault(x => x.Start == idx);
        return t.Length == 0 ? "none" : SemanticHighlighter.Legend[t.Type];
    }

    [Fact]
    public async Task Classifies_csharp_semantically()
    {
        const string code = """
            namespace Shop;
            public class Product { public string Name { get; set; } = ""; private int _count; }
            public static class Ext
            {
                public static int Count(this Product p, int factor)
                {
                    var local = p.Name.Length * factor;
                    return local + 42;
                }
            }
            """;
        using var h = new SemanticHighlighter();
        h.Load(Path.GetTempPath(), Array.Empty<string>(), Array.Empty<string>());
        var tokens = await h.ClassifyCSharpAsync(Path.Combine(Path.GetTempPath(), "x.cs"), code);
        Assert.Equal("class", TypeAt(code, tokens, "Product"));
        Assert.Equal("property", TypeAt(code, tokens, "Name"));
        Assert.Equal("field", TypeAt(code, tokens, "_count"));
        Assert.Equal("method", TypeAt(code, tokens, "Count"));
        Assert.Equal("parameter", TypeAt(code, tokens, "factor"));
        Assert.Equal("variable", TypeAt(code, tokens, "local"));
        Assert.Equal("number", TypeAt(code, tokens, "42"));
        Assert.Equal("property", TypeAt(code, tokens, "Length"));

        var data = SemanticHighlighter.Encode(code, tokens);
        Assert.Equal(0, data.Length % 5);
    }

    [Fact]
    public async Task Classifies_razor_code_blocks_at_original_positions()
    {
        const string razor = "<p>@count</p>\n@code {\n    private int count;\n    private void Increment() => count++;\n}\n";
        var start = razor.IndexOf('{') + 1;
        var end = razor.LastIndexOf('}');
        using var h = new SemanticHighlighter();
        h.Load(Path.GetTempPath(), Array.Empty<string>(), Array.Empty<string>());
        var tokens = await h.ClassifyRazorAsync(Path.Combine(Path.GetTempPath(), "Counter.razor"), razor, "Counter", new[] { (start, end) });
        Assert.Equal("method", TypeAt(razor, tokens, "Increment"));
        Assert.Equal("field", TypeAt(razor, tokens, "count", 2));
    }
}
