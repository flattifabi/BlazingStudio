using BlazorStudio.Core.Design;
using Xunit;

namespace BlazorStudio.Core.Tests;

public class ShadowProjectTests
{
    private static (string Src, ShadowProject Shadow) Setup()
    {
        var root = Path.Combine(Path.GetTempPath(), "bs_shadow_" + Guid.NewGuid().ToString("N")[..6]);
        var src = Path.Combine(root, "src");
        Directory.CreateDirectory(Path.Combine(src, "Components", "Pages"));
        Directory.CreateDirectory(Path.Combine(src, "bin"));
        File.WriteAllText(Path.Combine(src, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />");
        File.WriteAllText(Path.Combine(src, "Components", "App.razor"), "<html><body>\n<Routes />\n</body></html>");
        File.WriteAllText(Path.Combine(src, "Components", "Pages", "Home.razor"), "@page \"/\"\n<h1>Hi</h1>");
        File.WriteAllText(Path.Combine(src, "bin", "x.dll"), "x");
        File.WriteAllText(Path.Combine(src, "shop.db"), "v1");
        File.WriteAllText(Path.Combine(src, "Program.cs"), "var builder = WebApplication.CreateBuilder(args);\nvar app = builder.Build();\napp.Run();\n");
        return (src, new ShadowProject(Path.Combine(src, "App.csproj"), Path.Combine(root, "shadow")) { BridgeScriptContent = "// bridge" });
    }

    [Fact]
    public void Mirrors_sources_injects_bridge_and_skips_bin()
    {
        var (src, sh) = Setup();
        Assert.True(sh.SyncAll() > 0);
        Assert.True(File.Exists(Path.Combine(sh.ShadowDir, "Components", "Pages", "Home.razor")));
        Assert.False(Directory.Exists(Path.Combine(sh.ShadowDir, "bin")));
        Assert.Contains("<script src=\"_bs/design.js\"></script>\n</body>", File.ReadAllText(Path.Combine(sh.ShadowDir, "Components", "App.razor")));
        Assert.Equal("// bridge", File.ReadAllText(Path.Combine(sh.ShadowDir, "wwwroot", "_bs", "design.js")));
        Assert.Contains("@page \"/_bs/preview\"", File.ReadAllText(Path.Combine(sh.ShadowDir, "_bs", "_BsPreview.razor")));
        var program = File.ReadAllText(Path.Combine(sh.ShadowDir, "Program.cs"));
        Assert.Contains("app.Use(async (bsCtx, bsNext)", program);
        Assert.True(program.IndexOf("app.Use(async", StringComparison.Ordinal) < program.IndexOf("app.Run()", StringComparison.Ordinal));
        // Original unverändert
        Assert.DoesNotContain("_bs", File.ReadAllText(Path.Combine(src, "Components", "App.razor")));
        // zweiter Abgleich ohne Änderungen
        Assert.Equal(0, sh.SyncAll());
    }

    [Fact]
    public void Overrides_restores_deletes_and_keeps_databases()
    {
        var (src, sh) = Setup();
        sh.SyncAll();
        var home = Path.Combine(src, "Components", "Pages", "Home.razor");
        Assert.True(sh.WriteText(home, "@page \"/\"\n<h1>Unsaved</h1>"));
        Assert.Contains("Unsaved", File.ReadAllText(sh.ToShadow(home)));
        Assert.True(sh.Restore(home));
        Assert.Contains("Hi", File.ReadAllText(sh.ToShadow(home)));

        // Datenbank, die die laufende App offen hält, wird nicht überschrieben
        File.WriteAllText(Path.Combine(sh.ShadowDir, "shop.db"), "app-data");
        File.WriteAllText(Path.Combine(src, "shop.db"), "v2");
        sh.SyncAll();
        Assert.Equal("app-data", File.ReadAllText(Path.Combine(sh.ShadowDir, "shop.db")));

        File.Delete(home);
        sh.SyncAll();
        Assert.False(File.Exists(sh.ToShadow(home)));
        Assert.True(File.Exists(Path.Combine(sh.ShadowDir, "wwwroot", "_bs", "design.js")));
    }

    [Fact]
    public void Points_references_outside_the_project_to_the_original()
    {
        var root = Path.Combine(Path.GetTempPath(), "bs_shadowref_" + Guid.NewGuid().ToString("N")[..6]);
        var app = Path.Combine(root, "src", "App");
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(Path.Combine(root, "src", "Models"));
        File.WriteAllText(Path.Combine(root, "Directory.Build.props"), "<Project><PropertyGroup><LangVersion>latest</LangVersion></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(root, "nuget.config"), "<configuration><packageSources><add key=\"intern\" value=\"feed\" /><add key=\"org\" value=\"https://api.nuget.org/v3/index.json\" /></packageSources><packageSourceCredentials><intern><add key=\"Username\" value=\"max\" /></intern></packageSourceCredentials></configuration>");
        File.WriteAllText(Path.Combine(app, "App.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <ProjectReference Include="..\Models\Models.csproj" />
                <Compile Include="..\Shared\*.cs" Link="Shared" Condition="Exists('..\Shared')" />
                <Compile Include="Local\*.cs" />
                <Reference Include="Legacy"><HintPath>$(MSBuildProjectDirectory)\..\lib\Legacy.dll</HintPath></Reference>
              </ItemGroup>
            </Project>
            """);
        var sh = new ShadowProject(Path.Combine(app, "App.csproj"), Path.Combine(root, "shadow"));
        sh.SyncAll();

        var csproj = File.ReadAllText(sh.ShadowProjectFile);
        var models = Path.Combine(root, "src", "Models", "Models.csproj");
        Assert.Contains($"Include=\"{models}\"", csproj);
        Assert.Contains($"Include=\"{Path.Combine(root, "src", "Shared", "*.cs")}\"", csproj);
        Assert.Contains($"Exists('{Path.Combine(root, "src", "Shared")}')", csproj);
        Assert.Contains($"<HintPath>{Path.Combine(root, "src", "lib", "Legacy.dll")}</HintPath>", csproj);
        Assert.Contains("Include=\"Local\\*.cs\"", csproj); // innerhalb des Projekts bleibt relativ

        // Dateien von weiter oben: MSBuild per Import, nuget.config mit absoluten Ordnerpfaden
        Assert.Contains($"<Import Project=\"{Path.Combine(root, "Directory.Build.props")}\" />", File.ReadAllText(Path.Combine(sh.ShadowDir, "Directory.Build.props")));
        var nuget = File.ReadAllText(Path.Combine(sh.ShadowDir, "nuget.config"));
        Assert.Contains($"value=\"{Path.Combine(root, "feed")}\"", nuget.Replace("value=\"feed\"", "value=\"feed\""));
        Assert.Contains("value=\"max\"", nuget);
        Assert.Contains("value=\"https://api.nuget.org/v3/index.json\"", nuget);

        // zweiter Abgleich: nichts geändert, generierte Dateien bleiben
        Assert.Equal(0, sh.SyncAll());
        Assert.True(File.Exists(Path.Combine(sh.ShadowDir, "Directory.Build.props")));
        Directory.Delete(root, true);
    }
}
