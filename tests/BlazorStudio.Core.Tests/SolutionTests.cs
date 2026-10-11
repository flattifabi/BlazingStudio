using BlazorStudio.Core.Solution;

namespace BlazorStudio.Core.Tests;

public class SolutionTests
{
    [Fact]
    public void Parses_classic_sln_with_solution_folders()
    {
        var dir = Directory.CreateTempSubdirectory("bs-sln");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir.FullName, "src", "Web"));
            File.WriteAllText(Path.Combine(dir.FullName, "src", "Web", "Web.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>" +
                "<ItemGroup><PackageReference Include=\"MudBlazor\" Version=\"8.0.0\" /><ProjectReference Include=\"..\\Lib\\Lib.csproj\" /></ItemGroup></Project>");
            var sln = Path.Combine(dir.FullName, "App.sln");
            File.WriteAllText(sln, """
                Microsoft Visual Studio Solution File, Format Version 12.00
                Project("{2150E333-8FDC-42A3-9474-1A3956D46DE8}") = "src", "src", "{11111111-1111-1111-1111-111111111111}"
                	ProjectSection(SolutionItems) = preProject
                		README.md = README.md
                	EndProjectSection
                EndProject
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Web", "src\Web\Web.csproj", "{22222222-2222-2222-2222-222222222222}"
                EndProject
                Global
                	GlobalSection(NestedProjects) = preSolution
                		{22222222-2222-2222-2222-222222222222} = {11111111-1111-1111-1111-111111111111}
                	EndGlobalSection
                EndGlobal
                """);
            var model = SolutionModel.Load(sln);
            var folder = Assert.Single(model.Root);
            Assert.True(folder.IsFolder);
            Assert.Single(folder.Files);
            var web = Assert.Single(folder.Children);
            Assert.Equal("Web", web.Name);
            Assert.NotNull(web.Project);
            Assert.Equal("net10.0", web.Project!.TargetFrameworks.Single());
            Assert.Equal("web", web.Project.Kind);
            Assert.Contains("Microsoft.AspNetCore.App", web.Project.FrameworkReferences);
            Assert.Equal("MudBlazor", web.Project.PackageReferences.Single().Id);
            Assert.EndsWith(Path.Combine("src", "Lib", "Lib.csproj"), web.Project.ProjectReferences.Single());
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void Parses_own_slnx_and_resolves_assets()
    {
        var repo = AppContext.BaseDirectory;
        while (repo is not null && !File.Exists(Path.Combine(repo, "BlazorStudio.slnx"))) repo = Path.GetDirectoryName(repo);
        Assert.NotNull(repo);
        var model = SolutionModel.Load(Path.Combine(repo!, "BlazorStudio.slnx"));
        var names = model.AllProjects.Select(p => p.Name).ToList();
        Assert.Contains("BlazorStudio.Core", names);
        Assert.Contains("BlazorStudio.App", names);
        var core = model.AllProjects.First(p => p.Name == "BlazorStudio.Core").Project!;
        Assert.Contains(core.PackageReferences, p => p.Id == "Microsoft.CodeAnalysis.CSharp");
        Assert.Contains(core.ResolvedPackages, p => p.IsTransitive && p.Id == "Microsoft.CodeAnalysis.Common");
        var tests = model.AllProjects.First(p => p.Name == "BlazorStudio.Core.Tests").Project!;
        Assert.Equal("test", tests.Kind);
    }
}
