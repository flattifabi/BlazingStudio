using BlazorStudio.Core.Catalog;
using BlazorStudio.Core.Completion;
using Xunit;

namespace BlazorStudio.Core.Tests;

public class LibraryCompletionTests
{
    private static readonly ComponentDefinition MudButton = new()
    {
        Key = "lib:MudButton", TagName = "MudButton", Label = "MudButton", Category = "MudBlazor", Template = "<MudButton>Button</MudButton>",
        Source = ComponentSource.Library, Library = "MudBlazor", AcceptsChildren = true, Summary = "Ein Button.",
        Parameters = new[]
        {
            new ParameterDefinition("Variant", "Variant", ValueEditor.Enum, new[] { "Text", "Filled", "Outlined" }, EnumPrefix: "Variant."),
            new ParameterDefinition("Disabled", "bool", ValueEditor.Bool),
            new ParameterDefinition("OnClick", "EventCallback<MouseEventArgs>", ValueEditor.Callback),
        },
    };

    private static CompletionResult Complete(string text) =>
        CompletionEngine.Complete(new CompletionContext { Text = text, Offset = text.Length, Components = new[] { MudButton } });

    [Fact]
    public void Library_components_are_suggested_as_tags()
    {
        var r = Complete("<Mud");
        var item = Assert.Single(r.Items, i => i.Label == "MudButton");
        Assert.Equal("MudBlazor", item.Detail);
        Assert.StartsWith("MudButton>", item.InsertText);
    }

    [Fact]
    public void Library_component_parameters_and_values()
    {
        var r = Complete("<MudButton ");
        Assert.Contains(r.Items, i => i.Label == "Variant");
        Assert.Contains(r.Items, i => i.Label == "OnClick");
        Assert.Contains(r.Items, i => i.Label == "@ref");

        r = Complete("<MudButton Variant=\"");
        Assert.Equal(new[] { "Variant.Text", "Variant.Filled", "Variant.Outlined" }, r.Items.Select(i => i.Label));
        Assert.All(r.Items, i => Assert.StartsWith(" ", i.SortText));

        r = Complete("<MudButton Disabled=\"");
        Assert.Equal(new[] { "true", "false" }, r.Items.Select(i => i.Label));
    }
}
