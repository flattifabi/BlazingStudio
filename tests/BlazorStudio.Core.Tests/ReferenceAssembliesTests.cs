using BlazorStudio.Core.Highlighting;
using Xunit;

namespace BlazorStudio.Core.Tests;

public class ReferenceAssembliesTests
{
    [Theory]
    [InlineData("net10.0", 10)]
    [InlineData("net8.0-windows", 8)]
    [InlineData("netstandard2.0", null)]
    [InlineData(null, null)]
    public void Parses_target_framework_major(string? tfm, int? expected) => Assert.Equal(expected, ReferenceAssemblies.MajorOf(tfm));

    [Fact]
    public void Finds_core_and_aspnetcore_references_from_sdk()
    {
        if (ReferenceAssemblies.DotnetRoot() is null) return; // kein SDK installiert
        var refs = ReferenceAssemblies.Find("net10.0");
        var names = refs.Select(Path.GetFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("System.Runtime.dll", names);
        Assert.Contains("Microsoft.AspNetCore.dll", names);
        Assert.Contains("Microsoft.AspNetCore.Components.dll", names);
        Assert.Contains("Microsoft.Extensions.DependencyInjection.Abstractions.dll", names);
        // keine Duplikate
        Assert.Equal(names.Count, refs.Count);
    }

    [Fact]
    public async Task Classifies_top_level_program_with_web_types()
    {
        if (ReferenceAssemblies.DotnetRoot() is null) return;
        using var h = new SemanticHighlighter();
        var dir = Path.Combine(Path.GetTempPath(), "bs_refs_" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        h.Load(dir, Array.Empty<string>(), Array.Empty<string>(), "net10.0");
        const string code = "var builder = WebApplication.CreateBuilder(args);\nbuilder.Services.AddRazorComponents();\nvar app = builder.Build();\napp.UseHsts();\n";
        var tokens = await h.ClassifyCSharpAsync(Path.Combine(dir, "Program.cs"), code);
        string? TypeAt(string word)
        {
            var i = code.IndexOf(word, StringComparison.Ordinal);
            var t = tokens.FirstOrDefault(x => x.Start == i);
            return t.Length == 0 ? null : SemanticHighlighter.Legend[t.Type];
        }
        Assert.Equal("class", TypeAt("WebApplication"));
        Assert.Equal("method", TypeAt("CreateBuilder"));
        Assert.Equal("property", TypeAt("Services"));
        Assert.Equal("method", TypeAt("AddRazorComponents"));
        Assert.Equal("method", TypeAt("UseHsts"));
    }
}
