using BlazorStudio.Core.Razor;
using Xunit;

namespace BlazorStudio.Core.Tests;

public class CodeInspectorUsingTests
{
    [Fact]
    public void Handler_with_mouse_event_args_gets_web_using()
    {
        var stub = CodeInspector.CreateHandlerStub("OnClick", "MouseEventArgs", async: false);
        Assert.Contains("Microsoft.AspNetCore.Components.Web", CodeInspector.RequiredUsings(stub));
        const string file = "using DemoShop.Models;\r\n\r\nnamespace DemoShop.Pages;\r\n\r\npublic partial class Home\r\n{\r\n}\r\n";
        var updated = CodeInspector.EnsureUsing(file, "Microsoft.AspNetCore.Components.Web");
        Assert.StartsWith("using DemoShop.Models;\r\nusing Microsoft.AspNetCore.Components.Web;\r\n", updated);
        // zweimal einfügen ändert nichts
        Assert.Equal(updated, CodeInspector.EnsureUsing(updated, "Microsoft.AspNetCore.Components.Web"));
    }

    [Fact]
    public void Handler_without_args_needs_nothing()
    {
        Assert.Empty(CodeInspector.RequiredUsings(CodeInspector.CreateHandlerStub("Save", null, async: true)));
    }
}
