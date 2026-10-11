using BlazorStudio.Core.Catalog;
using BlazorStudio.Core.Solution;

namespace BlazorStudio.Core.Tests;

public class LibraryScannerTests
{
    [Fact]
    public void Finds_mudblazor_components_with_parameters()
    {
        var repo = AppContext.BaseDirectory;
        while (repo is not null && !File.Exists(Path.Combine(repo, "BlazorStudio.slnx"))) repo = Path.GetDirectoryName(repo);
        var csproj = Path.Combine(repo!, "samples", "DemoShop", "DemoShop.csproj");
        var project = ProjectModel.Load(csproj);
        if (!project.ResolvedPackages.Any(p => p.Id == "MudBlazor")) return; // Beispiel ohne MudBlazor

        var libs = LibraryScanner.Scan(project.ResolvedPackages.Select(p => (p.Id, p.Version, p.Assemblies)));
        var mud = Assert.Single(libs, l => l.PackageId == "MudBlazor");
        Assert.Contains("MudBlazor", mud.Namespaces);
        Assert.True(mud.Components.Count > 50, $"nur {mud.Components.Count} Komponenten");

        var button = mud.Components.Single(c => c.TagName == "MudButton");
        Assert.True(button.AcceptsChildren);
        Assert.Contains("<MudButton>Button</MudButton>", button.Template);
        var variant = button.FindParameter("Variant");
        Assert.NotNull(variant);
        Assert.Equal(ValueEditor.Enum, variant!.Editor);
        Assert.Contains("Filled", variant.Options!);
        Assert.Equal("Variant.", variant.EnumPrefix);
        Assert.Equal(ValueEditor.Callback, button.FindParameter("OnClick")!.Editor);

        var select = mud.Components.Single(c => c.TagName == "MudSelect");
        Assert.Contains("T=\"string\"", select.Template);
        Assert.True(select.FindParameter("Value")!.IsBindable);
    }
}
