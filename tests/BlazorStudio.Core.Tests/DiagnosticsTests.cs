using BlazorStudio.Core.Highlighting;
using BlazorStudio.Core.Razor;
using Xunit;

namespace BlazorStudio.Core.Tests;

public class DiagnosticsTests
{
    private static readonly string[] Usings = { "System", "Microsoft.AspNetCore.Components" };

    [Fact]
    public async Task Reports_compiler_errors_in_cs_files()
    {
        var dir = Directory.CreateTempSubdirectory("bs-diag").FullName;
        using var h = new SemanticHighlighter();
        h.Load(dir, Array.Empty<string>(), Array.Empty<string>(), "net10.0");
        const string code = "namespace Shop;\npublic class Cart\n{\n    public void Add()\n    {\n        int count = \"eins\";\n        Totl = 3;\n    }\n    public int Total { get; set; }\n}\n";
        var path = Path.Combine(dir, "Cart.cs");
        var found = await h.DiagnoseCSharpAsync(path, code);

        var conversion = Assert.Single(found, d => d.Code == "CS0029");
        Assert.Equal(code.IndexOf("\"eins\"", StringComparison.Ordinal), conversion.Start);
        var unknown = Assert.Single(found, d => d.Code == "CS0103");
        Assert.Equal("Totl", code[unknown.Start..unknown.End]);

        Assert.Empty(await h.DiagnoseCSharpAsync(path, code.Replace("\"eins\"", "1").Replace("Totl", "Total")));
    }

    [Fact]
    public async Task Files_with_missing_references_only_show_syntax_errors()
    {
        var dir = Directory.CreateTempSubdirectory("bs-diag").FullName;
        using var h = new SemanticHighlighter();
        h.Load(dir, Array.Empty<string>(), Array.Empty<string>(), "net10.0");
        // Basisklasse und Typ aus einem Paket, das der Workspace nicht kennt: geerbte Member wären sonst alle „rot“
        const string code = "using Some.Package;\npublic class Page : PackageBase\n{\n    void M()\n    {\n        StateHasChanged();\n        Helper.Run();\n        int x = 1\n    }\n}\n";
        var found = await h.DiagnoseCSharpAsync(Path.Combine(dir, "Page.cs"), code);
        Assert.Equal("CS1002", Assert.Single(found).Code); // fehlendes Semikolon bleibt sichtbar
    }

    [Fact]
    public async Task Reports_errors_in_razor_code_blocks_at_the_razor_position()
    {
        var dir = Directory.CreateTempSubdirectory("bs-diag").FullName;
        using var h = new SemanticHighlighter();
        h.Load(dir, Array.Empty<string>(), Array.Empty<string>(), "net10.0");
        const string razor = "<h1>@Title</h1>\n<p>@notDeclaredInMarkup</p>\n@code {\n    private string Title = \"Hi\";\n    private void Save()\n    {\n        coutner++;\n    }\n}\n";
        var path = Path.Combine(dir, "Home.razor");
        var proj = RazorCSharpProjection.ClassFor(razor, "Home", "Shop.Pages", Usings);
        var found = await h.DiagnoseRazorAsync(path, razor, proj);

        var d = Assert.Single(found);
        Assert.Equal("CS0103", d.Code);
        Assert.Equal("coutner", razor[d.Start..d.End]);
    }

    [Fact]
    public async Task Skips_code_blocks_with_render_fragment_markup()
    {
        var dir = Directory.CreateTempSubdirectory("bs-diag").FullName;
        using var h = new SemanticHighlighter();
        h.Load(dir, Array.Empty<string>(), Array.Empty<string>(), "net10.0");
        const string razor = "@code {\n    private RenderFragment Row => @<tr><td>x</td></tr>;\n}\n";
        var proj = RazorCSharpProjection.ClassFor(razor, "Grid", "Shop", Usings);
        Assert.Empty(await h.DiagnoseRazorAsync(Path.Combine(dir, "Grid.razor"), razor, proj));
    }

    [Fact]
    public async Task Solution_wide_analysis_counts_errors_per_file_including_changes_elsewhere()
    {
        var dir = Directory.CreateTempSubdirectory("bs-diag").FullName;
        var cart = Path.Combine(dir, "Cart.cs");
        var shop = Path.Combine(dir, "Shop.cs");
        var obj = Path.Combine(dir, "obj", "Gen.g.cs");
        File.WriteAllText(cart, "namespace Shop;\npublic class Cart { public int Total { get; set; } }\n");
        File.WriteAllText(shop, "namespace Shop;\npublic class Use { int M(Cart c) => c.Total; }\n");
        Directory.CreateDirectory(Path.GetDirectoryName(obj)!);
        File.WriteAllText(obj, "namespace Shop; public class Gen { int x = \"kaputt\"; }\n");
        using var h = new SemanticHighlighter();
        h.Load(dir, new[] { cart, shop, obj }, Array.Empty<string>(), "net10.0");

        Assert.Empty(await h.DiagnoseAllCSharpAsync());

        // Umbenennen in Cart.cs (ungespeichert im Editor) → Fehler in Shop.cs
        await h.DiagnoseCSharpAsync(cart, "namespace Shop;\npublic class Cart { public int Sum { get; set; } }\n");
        var all = await h.DiagnoseAllCSharpAsync();
        Assert.Equal(1, Assert.Single(all).Value);
        Assert.Equal(shop, all.Keys.Single(), ignoreCase: true);
    }
}
