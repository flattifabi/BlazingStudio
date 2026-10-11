using BlazorStudio.Core.Razor;
using Xunit;

namespace BlazorStudio.Core.Tests;

public class LayoutProvidersTests
{
    private static readonly string[] Known = { "<MudThemeProvider />", "<MudPopoverProvider />", "<MudDialogProvider />", "<MudSnackbarProvider />", "<RadzenComponents />" };

    [Fact]
    public void Repairs_provider_inside_code_block()
    {
        const string broken = "@inherits LayoutComponentBase\n\n<MudThemeProvider />\n<MudPopoverProvider />\n<MudDialogProvider />\n<MudSnackbarProvider @ref=\"_mudSnackbarProviderRef\" />\n\n<MudLayout>\n    <MudMainContent>@Body</MudMainContent>\n</MudLayout>\n\n@code {\n\n<RadzenComponents />\n    private MudSnackbarProvider _mudSnackbarProviderRef;\n}";
        var fixedText = LayoutProviders.Repair(broken, Known);
        Assert.NotNull(fixedText);
        var code = fixedText![fixedText.IndexOf("@code", StringComparison.Ordinal)..];
        Assert.DoesNotContain("RadzenComponents", code);
        Assert.Equal("@code {\n    private MudSnackbarProvider _mudSnackbarProviderRef;\n}", code);
        // direkt hinter den vorhandenen Providern
        Assert.Contains("<MudSnackbarProvider @ref=\"_mudSnackbarProviderRef\" />\n<RadzenComponents />\n", fixedText);
        Assert.Null(LayoutProviders.Repair(fixedText, Known));
    }

    [Fact]
    public void Inserts_after_directives_never_into_code()
    {
        const string layout = "@inherits LayoutComponentBase\n@using Foo\n<div class=\"page\">@Body</div>\n\n@code {\n    private int x;\n}\n";
        var result = LayoutProviders.Insert(layout, new[] { "<RadzenComponents />" });
        Assert.StartsWith("@inherits LayoutComponentBase\n@using Foo\n\n<RadzenComponents />\n<div class=\"page\">", result);
        Assert.Equal(result, LayoutProviders.Insert(result, new[] { "<RadzenComponents />" }));
    }

    [Fact]
    public void Inserts_next_to_existing_providers()
    {
        const string layout = "@inherits LayoutComponentBase\n\n<MudThemeProvider />\n\n<MudLayout>@Body</MudLayout>\n";
        var result = LayoutProviders.Insert(layout, new[] { "<MudThemeProvider />", "<MudDialogProvider />" });
        Assert.Equal("@inherits LayoutComponentBase\n\n<MudThemeProvider />\n<MudDialogProvider />\n\n<MudLayout>@Body</MudLayout>\n", result);
    }
}
