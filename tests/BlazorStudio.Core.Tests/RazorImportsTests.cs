using BlazorStudio.Core.Razor;

namespace BlazorStudio.Core.Tests;

public class RazorImportsTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "Shop");

    private static string P(params string[] parts) => Path.Combine(new[] { Root }.Concat(parts).ToArray());

    private static Func<string, string?> Files(Dictionary<string, string> files) =>
        path => files.TryGetValue(Path.GetFullPath(path), out var t) ? t : null;

    [Fact]
    public void Imports_follow_the_folder_hierarchy_only()
    {
        var files = new Dictionary<string, string>
        {
            [P("_Imports.razor")] = "@using Microsoft.AspNetCore.Components.Web\n@using MudBlazor;\n",
            [P("Components", "Pages", "_Imports.razor")] = "@using Shop.Data\n",
            [P("Components", "Admin", "_Imports.razor")] = "@using Shop.Admin\n",
        };
        var page = P("Components", "Pages", "Home.razor");
        var set = RazorImports.Imported(page, "@page \"/\"\n@using Shop.Local\n", Root, Files(files), new[] { "Shop.Global" });
        Assert.Contains("MudBlazor", set);
        Assert.Contains("Shop.Data", set);
        Assert.Contains("Shop.Local", set);
        Assert.Contains("Shop.Global", set);
        Assert.Contains("Microsoft.AspNetCore.Components", set);
        Assert.DoesNotContain("Shop.Admin", set);
    }

    [Fact]
    public void Namespace_comes_from_directive_code_behind_imports_or_folder()
    {
        var files = new Dictionary<string, string>
        {
            [P("Components", "_Imports.razor")] = "@namespace Shop.UI\n",
            [P("Components", "Shared", "Card.razor")] = "<div />",
            [P("Components", "Shared", "Own.razor")] = "@namespace Custom.Place\n<div />",
            [P("Components", "Shared", "Coded.razor")] = "<div />",
            [P("Components", "Shared", "Coded.razor.cs")] = "namespace Shop.Coded;\npublic partial class Coded {}",
            [P("Pages", "Home.razor")] = "<div />",
        };
        var read = Files(files);
        Assert.Equal("Shop.UI.Shared", RazorImports.NamespaceOfFile(P("Components", "Shared", "Card.razor"), Root, "Shop", read));
        Assert.Equal("Custom.Place", RazorImports.NamespaceOfFile(P("Components", "Shared", "Own.razor"), Root, "Shop", read));
        Assert.Equal("Shop.Coded", RazorImports.NamespaceOfFile(P("Components", "Shared", "Coded.razor"), Root, "Shop", read));
        Assert.Equal("Shop.Pages", RazorImports.NamespaceOfFile(P("Pages", "Home.razor"), Root, "Shop", read));
    }

    [Fact]
    public void Components_in_own_or_parent_namespace_need_no_using()
    {
        var imported = new HashSet<string> { "MudBlazor" };
        Assert.True(RazorImports.IsVisible("Shop.Components.Pages", "Shop.Components.Pages", imported));
        Assert.True(RazorImports.IsVisible("Shop.Components", "Shop.Components.Pages", imported));
        Assert.True(RazorImports.IsVisible("MudBlazor", "Shop.Components.Pages", imported));
        Assert.False(RazorImports.IsVisible("Shop.Components.Shared", "Shop.Components.Pages", imported));
        // „Shop.Comp“ ist kein Eltern-Namespace von „Shop.Components“
        Assert.False(RazorImports.IsVisible("Shop.Comp", "Shop.Components", imported));
    }

    [Fact]
    public void Global_and_project_usings_are_read()
    {
        Assert.Equal(new[] { "Shop.Models", "MudBlazor" }, RazorImports.GlobalUsings("global using Shop.Models;\nusing X;\n  global using MudBlazor ;").ToArray());
        const string csproj = """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <ItemGroup>
                <Using Include="Shop.Services" />
                <Using Include="System.Math" Static="true" />
                <Using Include="Foo.Bar" Alias="FB" />
              </ItemGroup>
            </Project>
            """;
        Assert.Equal(new[] { "Shop.Services" }, RazorImports.ProjectUsings(csproj).ToArray());
    }

    [Fact]
    public void Built_in_components_know_their_namespace()
    {
        Assert.Equal("Microsoft.AspNetCore.Components.Forms", RazorImports.BlazorNamespace("EditForm"));
        Assert.Equal("Microsoft.AspNetCore.Components.Routing", RazorImports.BlazorNamespace("NavLink"));
        Assert.Equal("Microsoft.AspNetCore.Components.Web.Virtualization", RazorImports.BlazorNamespace("Virtualize"));
        Assert.Equal("Microsoft.AspNetCore.Components", RazorImports.BlazorNamespace("CascadingValue"));
        Assert.Equal("Microsoft.AspNetCore.Components.Forms", BlazorStudio.Core.Catalog.BuiltInCatalog.Find("blazor:InputText")!.Namespace);
    }
}
