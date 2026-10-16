using System.Text.Json;
using BlazorStudio.Core.Assistant;

namespace BlazorStudio.Core.Tests;

public class ClaudeStreamTests
{
    [Fact]
    public void Parses_init_deltas_text_tools_and_result()
    {
        Assert.Equal(new ClaudeInit("s1", "claude-x", "C:\\p"),
            Assert.Single(ClaudeStream.Parse("""{"type":"system","subtype":"init","session_id":"s1","model":"claude-x","cwd":"C:\\p","tools":[]}""")));

        Assert.Equal(new ClaudeTextDelta("Hal"),
            Assert.Single(ClaudeStream.Parse("""{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hal"}},"parent_tool_use_id":null}""")));
        // Teiltexte von Unteragenten gehören nicht in die Antwort
        Assert.Empty(ClaudeStream.Parse("""{"type":"stream_event","event":{"type":"content_block_delta","delta":{"type":"text_delta","text":"x"}},"parent_tool_use_id":"t1"}"""));

        var assistant = ClaudeStream.Parse("""{"type":"assistant","message":{"content":[{"type":"text","text":"Ich ändere die Datei."},{"type":"tool_use","id":"t1","name":"Edit","input":{"file_path":"C:\\p\\Cart.cs","old_string":"a","new_string":"b"}}]},"parent_tool_use_id":null}""");
        Assert.Equal(new ClaudeText("Ich ändere die Datei."), assistant[0]);
        var tool = Assert.IsType<ClaudeToolUse>(assistant[1]);
        Assert.Equal(("Edit", "C:\\p\\Cart.cs", "C:\\p\\Cart.cs"), (tool.Name, tool.Summary, tool.FilePath));

        var result = Assert.IsType<ClaudeToolResult>(Assert.Single(ClaudeStream.Parse("""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":[{"type":"text","text":"ok"}],"is_error":false}]}}""")));
        Assert.Equal(("t1", false, "ok"), (result.ToolUseId, result.IsError, result.Text));

        var end = Assert.IsType<ClaudeResult>(Assert.Single(ClaudeStream.Parse("""{"type":"result","subtype":"success","is_error":false,"result":"Fertig","session_id":"s1","duration_ms":4200,"num_turns":3,"permission_denials":[{"tool_name":"Bash","tool_use_id":"t2","tool_input":{"command":"dotnet ef database update --project X"}},{"tool_name":"Bash","tool_use_id":"t3","tool_input":{"command":"dotnet ef migrations add Y"}}]}""")));
        Assert.False(end.IsError);
        Assert.Equal(("Fertig", "s1", 4200L, 3), (end.Text, end.SessionId, end.DurationMs, end.Turns));
        // Gleiche Regel nur einmal
        Assert.Equal("Bash(dotnet ef *)", Assert.Single(end.Denials).Rule);
    }

    [Fact]
    public void Error_results_and_garbage()
    {
        var r = Assert.IsType<ClaudeResult>(Assert.Single(ClaudeStream.Parse("""{"type":"result","subtype":"error_max_turns","is_error":false}""")));
        Assert.True(r.IsError);
        Assert.Empty(ClaudeStream.Parse("not json"));
        Assert.Empty(ClaudeStream.Parse("{broken"));
        Assert.Empty(ClaudeStream.Parse("""{"type":"something_new"}"""));
    }

    [Theory]
    [InlineData("Bash", """{"command":"dotnet build Shop.sln"}""", "Bash(dotnet build *)")]
    [InlineData("Bash", """{"command":"git -C x status"}""", "Bash(git *)")]
    [InlineData("Bash", """{"command":"ls"}""", "Bash(ls *)")]
    [InlineData("WebFetch", """{"url":"https://x"}""", "WebFetch")]
    public void Rules(string tool, string input, string rule)
    {
        using var doc = JsonDocument.Parse(input);
        Assert.Equal(rule, ClaudeStream.RuleFor(tool, doc.RootElement));
    }
}
