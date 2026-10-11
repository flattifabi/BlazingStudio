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
}
