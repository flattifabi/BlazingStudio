using BlazorStudio.Core.Editing;
using BlazorStudio.Core.Model;
using BlazorStudio.Core.Razor;
using Xunit;

namespace BlazorStudio.Core.Tests;

public class SlotEditorTests
{
    private static readonly string[] Panel = { "TitleContent", "ChildContent" };

    private static (RazorDocument Doc, DesignNode Comp) Parse(string src, string name)
    {
        var doc = RazorParser.Parse(src).Document;
        return (doc, doc.Root.Descendants().First(n => n.Name == name));
    }

    private static string Write(RazorDocument doc) => RazorWriter.Write(doc).Text.Replace("\r\n", "\n").Trim();

    [Fact]
    public void ChildContent_into_empty_self_closing_component()
    {
        var (doc, stack) = Parse("<MudStack />", "MudStack");
        SlotEditor.Insert(stack, null, new[] { "ChildContent" }, RazorParser.ParseFragment("<MudButton>Ok</MudButton>"));
        var text = Write(doc);
        Assert.Contains("<MudStack>", text);
        Assert.Contains("<MudButton>Ok</MudButton>", text);
        Assert.Contains("</MudStack>", text);
        Assert.DoesNotContain("ChildContent", text);
    }

    [Fact]
    public void Second_slot_moves_existing_content_into_ChildContent()
    {
        var (doc, panel) = Parse("<MudExpansionPanel>\n    <p>Inhalt</p>\n</MudExpansionPanel>", "MudExpansionPanel");
        SlotEditor.Insert(panel, "TitleContent", Panel, RazorParser.ParseFragment("<b>Titel</b>"));
        var text = Write(doc);
        var title = text.IndexOf("<TitleContent>", StringComparison.Ordinal);
        var child = text.IndexOf("<ChildContent>", StringComparison.Ordinal);
        Assert.True(title > 0 && child > title, text); // Reihenfolge wie deklariert
        Assert.Contains("<b>Titel</b>", text[title..child]);
        Assert.Contains("<p>Inhalt</p>", text[child..]);
    }

    [Fact]
    public void Inside_explicit_component_goes_into_existing_slot()
    {
        var (doc, panel) = Parse("<MudExpansionPanel>\n    <TitleContent>T</TitleContent>\n    <ChildContent><p>A</p></ChildContent>\n</MudExpansionPanel>", "MudExpansionPanel");
        SlotEditor.Insert(panel, null, Panel, RazorParser.ParseFragment("<p>B</p>"));
        var text = Write(doc);
        Assert.Matches(@"<ChildContent>[\s\S]*<p>A</p>[\s\S]*<p>B</p>[\s\S]*</ChildContent>", text);
        Assert.Equal(2, panel.Children.Count(c => c.Kind == NodeKind.Component));
    }

    [Fact]
    public void Tidy_returns_to_short_form()
    {
        var (doc, panel) = Parse("<MudExpansionPanel>\n    <TitleContent></TitleContent>\n    <ChildContent><p>A</p></ChildContent>\n</MudExpansionPanel>", "MudExpansionPanel");
        SlotEditor.Tidy(panel, Panel);
        var text = Write(doc);
        Assert.DoesNotContain("TitleContent", text);
        Assert.DoesNotContain("ChildContent", text);
        Assert.Contains("<p>A</p>", text);
    }
}
