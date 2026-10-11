using BlazorStudio.Core.Design;
using BlazorStudio.Core.Editing;
using BlazorStudio.Core.Razor;
using Xunit;

namespace BlazorStudio.Core.Tests;

public class DesignInstrumenterTests
{
    private static string Run(string src, Func<string, IReadOnlyCollection<string>?>? fragments = null)
    {
        var doc = RazorParser.Parse(src).Document;
        var spans = DocumentEditor.SpansFromSource(doc);
        return DesignInstrumenter.Instrument(src, doc, spans, "p_", fragments);
    }

    [Fact]
    public void Marks_slots_and_present_parameter_elements()
    {
        const string src = "<MudExpansionPanel>\n    <TitleContent>T</TitleContent>\n    <ChildContent><p>A</p></ChildContent>\n</MudExpansionPanel>\n";
        var r = Run(src, n => n == "MudExpansionPanel" ? new[] { "TitleContent", "ChildContent" } : null);
        Assert.Contains("data-bs-name=\"MudExpansionPanel\" data-bs-container=\"1\" data-bs-slots=\"TitleContent,ChildContent\" data-bs-present=\"TitleContent,ChildContent\"", r);
        // Parameter-Elemente selbst werden nicht eingerahmt, ihr Inhalt schon
        Assert.DoesNotContain("data-bs-name=\"TitleContent\"", r);
        Assert.Contains("<p data-bs-id=\"p_", r);
    }

    [Fact]
    public void Marks_elements_and_wraps_components_without_touching_whitespace()
    {
        const string src = "@page \"/\"\n\n<section class=\"hero\">\n    <h1>Hi</h1>   <MudButton Color=\"Color.Primary\">Go</MudButton>\n</section>\n@code { int x; }\n";
        var r = Run(src, n => n == "MudButton" ? new[] { "ChildContent" } : null);
        Assert.Contains("<section data-bs-id=\"p_", r);
        Assert.Contains("<h1 data-bs-id=\"p_", r);
        Assert.Matches(@"<template data-bs-s=""p_\w+"" data-bs-name=""MudButton"" data-bs-container=""1"" data-bs-slots=""ChildContent""></template><MudButton Color=""Color.Primary"">Go</MudButton><template data-bs-e=""p_\w+""></template>", r);
        // Rest bleibt exakt erhalten
        var stripped = System.Text.RegularExpressions.Regex.Replace(r, @" data-bs-id=""p_\w+""|<template data-bs-[se]=""p_\w+""[^>]*></template>", "");
        Assert.Equal(src, stripped);
    }

    [Fact]
    public void Does_not_wrap_render_fragment_parameters()
    {
        const string src = "<MudTable Items=\"items\"><HeaderContent><MudTh>Name</MudTh></HeaderContent><RowTemplate><MudTd>@context.Name</MudTd></RowTemplate></MudTable>";
        var r = Run(src, n => n switch
        {
            "MudTable" => new[] { "HeaderContent", "RowTemplate", "ChildContent" },
            "MudTh" or "MudTd" => new[] { "ChildContent" },
            _ => null,
        });
        // die Tabelle selbst ist eingerahmt, HeaderContent/RowTemplate nicht, ihre Kinder schon
        Assert.StartsWith("<template data-bs-s=", r);
        Assert.Contains("<HeaderContent><template data-bs-s=", r);
        Assert.DoesNotContain("<template data-bs-s=\"p_\" ></template><HeaderContent>", r);
        Assert.DoesNotMatch(@"</template><HeaderContent", r);
        Assert.DoesNotMatch(@"</template><RowTemplate", r);
    }

    [Fact]
    public void Skips_head_content_and_scripts()
    {
        const string src = "<html><head><link rel=\"stylesheet\" href=\"app.css\" /><HeadOutlet /></head><body><Routes /><script src=\"x.js\"></script></body></html>";
        var r = Run(src, _ => Array.Empty<string>());
        Assert.Contains("<link rel=\"stylesheet\" href=\"app.css\" /><HeadOutlet />", r);
        Assert.Contains("<script src=\"x.js\">", r);
        Assert.Contains("<body data-bs-id=", r);
        Assert.Contains("<template data-bs-s=\"p_", r); // Routes im Body
    }

    [Fact]
    public void Prefix_is_stable_per_file()
    {
        Assert.Equal(DesignInstrumenter.PrefixFor("Components/Pages/Home.razor"), DesignInstrumenter.PrefixFor("components\\pages\\home.razor"));
        Assert.NotEqual(DesignInstrumenter.PrefixFor("A.razor"), DesignInstrumenter.PrefixFor("B.razor"));
    }
}
