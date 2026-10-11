using BlazorStudio.Core.Highlighting;
using BlazorStudio.Core.Razor;
using Xunit;

namespace BlazorStudio.Core.Tests;

public class NavigationTests
{
    private const string Service = "namespace Shop;\npublic class CartService\n{\n    public int Count { get; private set; }\n    public void Add() => Count++;\n}\n";
    private const string Razor = "@page \"/\"\n<h1>@Total</h1>\n<button @onclick=\"AddOne\">+</button>\n@code {\n    private Shop.CartService cart = new();\n    private int Total => cart.Count;\n    private void AddOne() => cart.Add();\n}\n";

    private static async Task<(SemanticHighlighter H, string Dir)> Setup()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bs_nav_" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "CartService.cs"), Service);
        File.WriteAllText(Path.Combine(dir, "Home.razor"), Razor);
        var h = new SemanticHighlighter();
        h.Load(dir, new[] { Path.Combine(dir, "CartService.cs") }, Array.Empty<string>(), "net10.0");
        var proj = RazorCSharpProjection.ClassFor(Razor, "Home", "Shop.Pages", new[] { "System", "Microsoft.AspNetCore.Components" });
        await h.IndexRazorAsync(new[] { (Path.Combine(dir, "Home.razor"), proj) });
        return (h, dir);
    }

    [Fact]
    public async Task Go_to_definition_from_razor_code_into_cs_file()
    {
        var (h, dir) = await Setup();
        using var _ = h;
        var razor = Path.Combine(dir, "Home.razor");
        var proj = RazorCSharpProjection.ClassFor(Razor, "Home", "Shop.Pages", new[] { "System", "Microsoft.AspNetCore.Components" });
        await h.UpsertRazorAsync(razor, proj);
        // Cursor auf "Count" in "cart.Count"
        var orig = Razor.IndexOf("cart.Count", StringComparison.Ordinal) + 6;
        var gen = proj.Map!.Select(m => orig >= m.Orig && orig <= m.Orig + m.Len ? m.Gen + (orig - m.Orig) : -1).Max();
        var r = await h.DefinitionAsync(SemanticHighlighter.GeneratedPath(razor), proj.Code, gen);
        var loc = Assert.Single(r.Locations);
        Assert.EndsWith("CartService.cs", loc.Path);
        Assert.Equal(Service.IndexOf("Count {", StringComparison.Ordinal), loc.Offset);
        Assert.Equal("property", r.Kind);
    }

    [Fact]
    public async Task Finds_references_in_cs_and_razor_and_renames_both()
    {
        var (h, dir) = await Setup();
        using var _ = h;
        var cs = Path.Combine(dir, "CartService.cs");
        var pos = Service.IndexOf("Add()", StringComparison.Ordinal);
        var refs = await h.ReferencesAsync(cs, Service, pos);
        Assert.Contains(refs.Locations, l => l.Path.EndsWith("CartService.cs") && l.IsDefinition);
        var inRazor = Assert.Single(refs.Locations, l => l.Path.EndsWith("Home.razor"));
        Assert.Equal(Razor.IndexOf("cart.Add()", StringComparison.Ordinal) + 5, inRazor.Offset);

        var (error, old, edits) = await h.RenameAsync(cs, Service, pos, "Increment");
        Assert.Null(error);
        Assert.Equal("Add", old);
        var razorEdit = Assert.Single(edits, e => e.Path.EndsWith("Home.razor"));
        var applied = Apply(Razor, razorEdit.Edits);
        Assert.Contains("cart.Increment()", applied);
        Assert.Contains("public void Increment()", Apply(Service, Assert.Single(edits, e => e.Path.EndsWith("CartService.cs")).Edits));
    }

    [Fact]
    public async Task Markup_expressions_and_injects_are_indexed()
    {
        var (h, dir) = await Setup();
        using var _ = h;
        const string page = "@inject Shop.CartService Cart\n<p>@Cart.Count</p>\n<button @onclick=\"() => Cart.Add()\">+</button>\n@if (Cart.Count > 2) { <b>viel</b> }\n";
        var path = Path.Combine(dir, "Summary.razor");
        File.WriteAllText(path, page);
        await h.UpsertRazorAsync(path, RazorCSharpProjection.ClassFor(page, "Summary", "Shop.Pages", new[] { "System", "Microsoft.AspNetCore.Components" }));
        var cs = Path.Combine(dir, "CartService.cs");
        var count = await h.ReferencesAsync(cs, Service, Service.IndexOf("Count {", StringComparison.Ordinal));
        var inPage = count.Locations.Where(l => l.Path == path).Select(l => l.Offset).ToList();
        Assert.Contains(page.IndexOf("@Cart.Count", StringComparison.Ordinal) + 6, inPage);
        Assert.Contains(page.IndexOf("Cart.Count >", StringComparison.Ordinal) + 5, inPage);
        Assert.Single(count.Locations, l => l.IsDefinition);
        var add = await h.ReferencesAsync(cs, Service, Service.IndexOf("Add()", StringComparison.Ordinal));
        Assert.Contains(add.Locations, l => l.Path == path && l.Offset == page.IndexOf("Cart.Add", StringComparison.Ordinal) + 5);
        // F12 auf den @inject-Namen im Markup → @inject-Zeile
        var proj = RazorCSharpProjection.ClassFor(page, "Summary", "Shop.Pages", new[] { "System", "Microsoft.AspNetCore.Components" });
        var orig = page.IndexOf("@Cart.Count", StringComparison.Ordinal) + 2;
        var gen = proj.Map!.Where(m => orig >= m.Orig && orig <= m.Orig + m.Len).Select(m => m.Gen + (orig - m.Orig)).First();
        var def = await h.DefinitionAsync(SemanticHighlighter.GeneratedPath(path), proj.Code, gen);
        var loc = Assert.Single(def.Locations);
        Assert.Equal(path, loc.Path);
        Assert.Equal(page.IndexOf("Service Cart", StringComparison.Ordinal) + 8, loc.Offset);
    }

    [Fact]
    public void Finds_member_usages_in_markup()
    {
        var hits = RazorMarkupUsages.Find(Razor, "Total");
        Assert.Single(hits);
        Assert.Equal(Razor.IndexOf("@Total", StringComparison.Ordinal) + 1, hits[0]);
        var handler = RazorMarkupUsages.Find(Razor, "AddOne");
        Assert.Single(handler); // @onclick="AddOne" – nicht die Definition im @code-Block
        Assert.Equal(Razor.IndexOf("\"AddOne\"", StringComparison.Ordinal) + 1, handler[0]);
    }

    private static string Apply(string text, IReadOnlyList<TextEditAt> edits)
    {
        foreach (var e in edits.OrderByDescending(e => e.Offset)) text = text.Remove(e.Offset, e.Length).Insert(e.Offset, e.NewText);
        return text;
    }
}
