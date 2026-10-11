using BlazorStudio.Core.Styles;

namespace BlazorStudio.Core.Tests;

public class CssTests
{
    [Fact]
    public void Rule_editor_adds_updates_and_removes_rules()
    {
        var css = ".a { color: red; }\n";
        var hover = new StyleDeclarations();
        hover.Set("color", "blue");
        css = CssRuleEditor.Set(css, ".btn:hover", null, hover);
        Assert.Contains(".btn:hover {\n    color: blue;\n}", css);
        Assert.Equal("blue", CssRuleEditor.Get(css, ".btn:hover", null).Get("color"));

        var md = new StyleDeclarations();
        md.Set("padding", "24px");
        css = CssRuleEditor.Set(css, ".btn", "(min-width: 768px)", md);
        Assert.Contains("@media (min-width: 768px) {\n    .btn {\n        padding: 24px;\n    }\n}", css);
        Assert.Equal("24px", CssRuleEditor.Get(css, ".btn", "(min-width:768px)").Get("padding"));

        md.Set("margin", "0");
        css = CssRuleEditor.Set(css, ".btn", "(min-width: 768px)", md);
        Assert.Equal("0", CssRuleEditor.Get(css, ".btn", "(min-width: 768px)").Get("margin"));

        css = CssRuleEditor.Set(css, ".btn", "(min-width: 768px)", new StyleDeclarations());
        Assert.DoesNotContain("@media", css);
        Assert.Contains(".a { color: red; }", css);

        css = CssRuleEditor.Set(css, ".btn:hover", null, new StyleDeclarations());
        Assert.DoesNotContain("btn", css);
    }

    [Fact]
    public void Media_queries_become_container_queries_and_states_are_forceable()
    {
        Assert.Equal("@container (min-width: 768px)", CssScoper.MediaToContainer("@media (min-width: 768px)"));
        Assert.Equal("@container (max-width: 640.98px)", CssScoper.MediaToContainer("@media screen and (max-width: 640.98px)"));
        Assert.Equal("@media (prefers-color-scheme: dark)", CssScoper.MediaToContainer("@media (prefers-color-scheme: dark)"));
        var scoped = CssScoper.Scope(".btn:hover { color: red }", ".p");
        Assert.Contains(".p .btn:is(:hover,[data-bs-state~=\"hover\"])", scoped);
    }
}
