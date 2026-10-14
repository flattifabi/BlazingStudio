using BlazorStudio.Core.Highlighting;
using BlazorStudio.Core.Razor;

namespace BlazorStudio.Core.Tests;

public class CodeActionTests
{
    private static SemanticHighlighter Create()
    {
        var h = new SemanticHighlighter();
        var dir = Path.Combine(Path.GetTempPath(), "bs_ca_" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        h.Load(dir, Array.Empty<string>(), Array.Empty<string>());
        return h;
    }

    private static string Apply(string text, IEnumerable<TextEditAt> edits)
    {
        foreach (var e in edits.OrderByDescending(e => e.Offset)) text = text[..e.Offset] + e.NewText + text[(e.Offset + e.Length)..];
        return text;
    }

    private const string Types = """
        using System;
        public interface IRepo { void Save(int id); string Name { get; } }
        public abstract class Shape { public abstract double Area(); public virtual string Describe() => "shape"; }

        """;

    [Fact]
    public async Task Implements_interface_with_ctrl_dot()
    {
        using var h = Create();
        var code = Types + "public class Repo : IRepo\n{\n}\n";
        var pos = code.IndexOf("IRepo\n", StringComparison.Ordinal);
        var actions = await h.GetCodeActionsAsync("/x/Repo.cs", code, pos, pos, withRefactorings: false);
        var implement = actions.First(a => a.Title == "Implement interface");
        Assert.Equal("quickfix", implement.Kind);
        var result = await h.ApplyCodeActionAsync(implement.Id, code);
        Assert.Null(result.Error);
        var updated = Apply(code, result.Edits);
        Assert.Contains("public void Save(int id)", updated);
        Assert.Contains("public string Name", updated);
    }

    [Fact]
    public async Task Implements_abstract_class()
    {
        using var h = Create();
        var code = Types + "public class Circle : Shape\n{\n}\n";
        var pos = code.IndexOf("Circle", StringComparison.Ordinal);
        var actions = await h.GetCodeActionsAsync("/x/Circle.cs", code, pos, pos, withRefactorings: false);
        var implement = actions.First(a => a.Title.Contains("abstract", StringComparison.OrdinalIgnoreCase));
        var updated = Apply(code, (await h.ApplyCodeActionAsync(implement.Id, code)).Edits);
        Assert.Contains("public override double Area()", updated);
    }

    [Fact]
    public async Task Refactorings_include_generate_overrides()
    {
        using var h = Create();
        var code = Types + "public class Square : Shape\n{\n    public override double Area() => 1;\n}\n";
        var pos = code.IndexOf("Square", StringComparison.Ordinal);
        var actions = await h.GetCodeActionsAsync("/x/Square.cs", code, pos, pos, withRefactorings: true);
        Assert.Contains(actions, a => a.Kind == "refactor");
        Assert.DoesNotContain(actions, a => a.Title.StartsWith("Suppress", StringComparison.Ordinal) || a.Title.StartsWith("Configure", StringComparison.Ordinal));
        // Aktionen mit VS-Dialog oder Datei-Operationen sind ausgeblendet
        Assert.DoesNotContain(actions, a => a.Title.EndsWith("...", StringComparison.Ordinal) || a.Title.StartsWith("Move type", StringComparison.Ordinal));
        // alle angebotenen Aktionen lassen sich anwenden
        foreach (var a in actions)
        {
            var r = await h.ApplyCodeActionAsync(a.Id, code);
            Assert.True(r.Error is null || r.Error.Contains("weitere Dateien", StringComparison.Ordinal), a.Title + ": " + r.Error);
        }
    }

    [Fact]
    public async Task Override_completion_inserts_whole_member()
    {
        using var h = Create();
        var code = Types + "public class Box : Shape\n{\n    public override \n}\n";
        var pos = code.IndexOf("override ", StringComparison.Ordinal) + "override ".Length;
        // Leerzeichen nach „override“ öffnet die Liste von selbst
        var list = await h.CompleteAsync("/x/Box.cs", code, pos, explicitInvoke: false);
        var area = Assert.Single(list!.Items, i => i.Label.StartsWith("Area", StringComparison.Ordinal));
        Assert.NotNull(area.Edit);
        Assert.Contains("double Area()", area.Edit!.NewText);
        Assert.Contains(list.Items, i => i.Label.StartsWith("Describe", StringComparison.Ordinal));
        Assert.DoesNotContain(list.Items, i => i.Label == "Console");
    }

    [Fact]
    public async Task Space_elsewhere_does_not_open_completion()
    {
        using var h = Create();
        var code = "class A { void M() { var x = 1; } }";
        var pos = code.IndexOf("var ", StringComparison.Ordinal) + 4;
        var list = await h.CompleteAsync("/x/A.cs", code, pos, explicitInvoke: false);
        Assert.Empty(list!.Items);
    }

    [Fact]
    public async Task Works_in_razor_code_blocks()
    {
        using var h = Create();
        var razor = "<p>Hi</p>\n@code {\n    private class Repo : System.IDisposable\n    {\n    }\n}\n";
        var proj = RazorCSharpProjection.ClassFor(razor, "Page", "App", new[] { "System" });
        var pos = razor.IndexOf("IDisposable", StringComparison.Ordinal);
        var actions = await h.GetCodeActionsAsync("/x/Page.razor", razor, pos, pos, withRefactorings: false, proj);
        var implement = actions.First(a => a.Title == "Implement interface");
        var result = await h.ApplyCodeActionAsync(implement.Id, razor);
        Assert.Null(result.Error);
        var updated = Apply(razor, result.Edits);
        Assert.Contains("public void Dispose()", updated);
        Assert.StartsWith("<p>Hi</p>\n@code {", updated);
    }
}
