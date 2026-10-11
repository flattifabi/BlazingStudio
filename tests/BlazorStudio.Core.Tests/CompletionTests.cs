using BlazorStudio.Core.Highlighting;
using BlazorStudio.Core.Razor;
using Xunit;

namespace BlazorStudio.Core.Tests;

public class CompletionTests
{
    private static readonly string MudDll = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages", "mudblazor", "9.11.0", "lib", "net10.0", "MudBlazor.dll");

    private static string[] Usings => new[]
    {
        "System", "System.Linq", "System.Collections.Generic", "Microsoft.AspNetCore.Components", "Microsoft.AspNetCore.Components.Web",
        "static Microsoft.AspNetCore.Components.Web.RenderMode",
    };

    private static SemanticHighlighter Create()
    {
        var h = new SemanticHighlighter();
        var dir = Path.Combine(Path.GetTempPath(), "bs_cmp_" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        h.Load(dir, Array.Empty<string>(), File.Exists(MudDll) ? new[] { MudDll } : Array.Empty<string>());
        return h;
    }

    [Fact]
    public async Task SuggestsUnimportedExtensionMethodWithUsing()
    {
        if (!File.Exists(MudDll)) return;
        using var h = Create();
        var code = "var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder(args);\nbuilder.Services.AddMud";
        var list = await h.CompleteAsync(@"C:\x\Program.cs", code, code.Length, explicitInvoke: true);
        var item = Assert.Single(list!.Items, i => i.Label == "AddMudServices");
        Assert.Equal("MudBlazor.Services", item.ImportNamespace);
        Assert.Equal(code.Length - "AddMud".Length, list.ReplaceStart);
    }

    [Fact]
    public async Task MemberAccessListsProperties()
    {
        using var h = Create();
        var code = "class P { public string Name { get; set; } = \"\"; void M() { var p = new P(); p. } }";
        var pos = code.IndexOf("p. ", StringComparison.Ordinal) + 2;
        var list = await h.CompleteAsync(@"C:\x\P.cs", code, pos, explicitInvoke: false);
        Assert.Contains(list!.Items, i => i.Label == "Name" && i.Kind == "property");
    }

    [Fact]
    public async Task RazorRenderModeDirective()
    {
        using var h = Create();
        var razor = "@page \"/\"\n@rendermode Inter\n<h1>Hi</h1>";
        var offset = razor.IndexOf("Inter", StringComparison.Ordinal) + 5;
        var proj = RazorCSharpProjection.Project(razor, offset, "Home", "Demo.Pages", Usings);
        Assert.NotNull(proj);
        var list = await h.CompleteAsync(@"C:\x\Home.razor.__complete.cs", proj!.Code, proj.Position, true);
        Assert.Contains(list!.Items, i => i.Label == "InteractiveServer");
    }

    [Fact]
    public async Task RazorCodeBlockAndAttributeValue()
    {
        using var h = Create();
        var razor = "<button @onclick=\"() => Cou\">x</button>\n@foreach (var item in Items)\n{\n    <span>@item.</span>\n}\n@code {\n    private int Counter;\n    private List<string> Items = new();\n    void Go() { Cou }\n}";
        var o1 = razor.IndexOf("Cou\"", StringComparison.Ordinal) + 3;
        var p1 = RazorCSharpProjection.Project(razor, o1, "Home", "Demo", Usings)!;
        Assert.Equal(RazorCSharpContext.AttributeValue, p1.Context);
        Assert.Contains((await h.CompleteAsync(@"C:\x\H.razor.__complete.cs", p1.Code, p1.Position, true))!.Items, i => i.Label == "Counter");

        var o2 = razor.IndexOf("@item.", StringComparison.Ordinal) + 6;
        var p2 = RazorCSharpProjection.Project(razor, o2, "Home", "Demo", Usings)!;
        Assert.Contains((await h.CompleteAsync(@"C:\x\H.razor.__complete.cs", p2.Code, p2.Position, true))!.Items, i => i.Label == "Length");

        var o3 = razor.IndexOf("{ Cou }", StringComparison.Ordinal) + 5;
        var p3 = RazorCSharpProjection.Project(razor, o3, "Home", "Demo", Usings)!;
        Assert.Equal(RazorCSharpContext.CodeBlock, p3.Context);
        Assert.Contains((await h.CompleteAsync(@"C:\x\H.razor.__complete.cs", p3.Code, p3.Position, true))!.Items, i => i.Label == "Counter");
    }

    [Fact]
    public void UsingInsertionPositions()
    {
        var (o, t) = RazorCSharpProjection.UsingInsertion("@page \"/\"\n@using A\n<h1/>", "MudBlazor");
        Assert.Equal("@page \"/\"\n@using A\n".Length, o);
        Assert.Equal("@using MudBlazor\n", t);
        var (o2, t2) = SemanticHighlighter.UsingInsertion("using System;\nusing Zeta;\nclass A {}", "MudBlazor.Services");
        Assert.Equal("using System;\n".Length, o2);
        Assert.StartsWith("using MudBlazor.Services;", t2);
    }
}
