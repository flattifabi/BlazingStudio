using System.Text.Json;

namespace BlazorStudio.Core.Assistant;

/// <summary>Ein Ereignis aus der Ausgabe von <c>claude -p --output-format stream-json</c>.</summary>
public abstract record ClaudeEvent;

/// <summary>Sitzung gestartet (system/init).</summary>
public sealed record ClaudeInit(string SessionId, string? Model, string? Cwd) : ClaudeEvent;

/// <summary>Teiltext während der Antwort (stream_event / text_delta).</summary>
public sealed record ClaudeTextDelta(string Text) : ClaudeEvent;

/// <summary>Fertiger Textblock der Antwort.</summary>
public sealed record ClaudeText(string Text) : ClaudeEvent;

/// <summary>Claude ruft ein Werkzeug auf (Datei lesen/ändern, Befehl …).</summary>
public sealed record ClaudeToolUse(string Id, string Name, string Summary, string? FilePath) : ClaudeEvent;

/// <summary>Ergebnis eines Werkzeugs.</summary>
public sealed record ClaudeToolResult(string ToolUseId, bool IsError, string Text) : ClaudeEvent;

/// <summary>Erneuter Versuch nach einem API-Fehler (z. B. Limit erreicht).</summary>
public sealed record ClaudeRetry(int Attempt, string? Error) : ClaudeEvent;

/// <summary>Abgelehnte Berechtigung.</summary>
public sealed record ClaudeDenial(string ToolName, string Summary, string Rule);

/// <summary>Ende der Anfrage.</summary>
public sealed record ClaudeResult(bool IsError, string? Text, string? SessionId, long DurationMs, int Turns, IReadOnlyList<ClaudeDenial> Denials) : ClaudeEvent;

/// <summary>Liest die stream-json-Zeilen von Claude Code. Unbekannte Zeilen werden übersprungen.</summary>
public static class ClaudeStream
{
    public static ClaudeEvent[] Parse(string line)
    {
        var l = line.Trim();
        if (!l.StartsWith('{')) return Array.Empty<ClaudeEvent>();
        try
        {
            using var doc = JsonDocument.Parse(l);
            var root = doc.RootElement;
            return Str(root, "type") switch
            {
                "system" when Str(root, "subtype") == "init" => new ClaudeEvent[] { new ClaudeInit(Str(root, "session_id") ?? "", Str(root, "model"), Str(root, "cwd")) },
                "system" when Str(root, "subtype") == "api_retry" => new ClaudeEvent[] { new ClaudeRetry(root.TryGetProperty("attempt", out var a) && a.TryGetInt32(out var n) ? n : 0, Str(root, "error")) },
                "stream_event" => Delta(root),
                "assistant" => Assistant(root),
                "user" => ToolResults(root),
                "result" => new ClaudeEvent[] { Result(root) },
                _ => Array.Empty<ClaudeEvent>(),
            };
        }
        catch (JsonException)
        {
            return Array.Empty<ClaudeEvent>();
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static ClaudeEvent[] Delta(JsonElement root)
    {
        if (!root.TryGetProperty("event", out var ev) || Str(ev, "type") != "content_block_delta") return Array.Empty<ClaudeEvent>();
        if (!ev.TryGetProperty("delta", out var d) || Str(d, "type") != "text_delta") return Array.Empty<ClaudeEvent>();
        // Teiltexte von Unteragenten (parent_tool_use_id gesetzt) nicht in die Hauptantwort mischen
        if (root.TryGetProperty("parent_tool_use_id", out var parent) && parent.ValueKind == JsonValueKind.String) return Array.Empty<ClaudeEvent>();
        return Str(d, "text") is { Length: > 0 } t ? new ClaudeEvent[] { new ClaudeTextDelta(t) } : Array.Empty<ClaudeEvent>();
    }

    private static IEnumerable<JsonElement> Content(JsonElement root) =>
        root.TryGetProperty("message", out var m) && m.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.Array
            ? c.EnumerateArray().ToList()
            : new List<JsonElement>();

    private static ClaudeEvent[] Assistant(JsonElement root)
    {
        var sub = root.TryGetProperty("parent_tool_use_id", out var parent) && parent.ValueKind == JsonValueKind.String;
        var list = new List<ClaudeEvent>();
        foreach (var block in Content(root))
        {
            switch (Str(block, "type"))
            {
                case "text" when !sub && Str(block, "text") is { Length: > 0 } t:
                    list.Add(new ClaudeText(t));
                    break;
                case "tool_use":
                    var name = Str(block, "name") ?? "?";
                    var input = block.TryGetProperty("input", out var i) ? i : default;
                    list.Add(new ClaudeToolUse(Str(block, "id") ?? "", name, Summarize(name, input), FileOf(input)));
                    break;
            }
        }
        return list.ToArray();
    }

    private static ClaudeEvent[] ToolResults(JsonElement root)
    {
        var list = new List<ClaudeEvent>();
        foreach (var block in Content(root))
        {
            if (Str(block, "type") != "tool_result") continue;
            var isError = block.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
            string text = "";
            if (block.TryGetProperty("content", out var c))
            {
                text = c.ValueKind switch
                {
                    JsonValueKind.String => c.GetString() ?? "",
                    JsonValueKind.Array => string.Join("\n", c.EnumerateArray().Select(x => Str(x, "text")).Where(x => x is not null)),
                    _ => "",
                };
            }
            list.Add(new ClaudeToolResult(Str(block, "tool_use_id") ?? "", isError, text.Length > 4000 ? text[..4000] + " …" : text));
        }
        return list.ToArray();
    }

    private static ClaudeResult Result(JsonElement root)
    {
        var denials = new List<ClaudeDenial>();
        if (root.TryGetProperty("permission_denials", out var pd) && pd.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in pd.EnumerateArray())
            {
                var name = Str(d, "tool_name") ?? "?";
                var input = d.TryGetProperty("tool_input", out var i) ? i : default;
                denials.Add(new ClaudeDenial(name, Summarize(name, input), RuleFor(name, input)));
            }
        }
        var isError = (root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True) || (Str(root, "subtype") is { } st && st != "success");
        return new ClaudeResult(isError, Str(root, "result"), Str(root, "session_id"),
            root.TryGetProperty("duration_ms", out var ms) && ms.TryGetInt64(out var dur) ? dur : 0,
            root.TryGetProperty("num_turns", out var nt) && nt.TryGetInt32(out var turns) ? turns : 0,
            denials.DistinctBy(x => x.Rule).ToList());
    }

    private static string? FileOf(JsonElement input) =>
        input.ValueKind == JsonValueKind.Object ? Str(input, "file_path") ?? Str(input, "notebook_path") ?? Str(input, "path") : null;

    /// <summary>Kurzbeschreibung eines Werkzeugaufrufs für die Anzeige.</summary>
    public static string Summarize(string tool, JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return "";
        var s = tool switch
        {
            "Bash" or "PowerShell" => Str(input, "command"),
            "Read" or "Write" or "Edit" or "MultiEdit" or "NotebookEdit" => Str(input, "file_path") ?? Str(input, "notebook_path"),
            "Grep" => Str(input, "pattern") is { } p ? p + (Str(input, "path") is { } gp ? "  (" + gp + ")" : "") : null,
            "Glob" => Str(input, "pattern"),
            "WebFetch" => Str(input, "url"),
            "WebSearch" => Str(input, "query"),
            "Task" or "Agent" => Str(input, "description"),
            "TodoWrite" => "Aufgabenliste",
            _ => null,
        } ?? Str(input, "description") ?? "";
        s = s.Replace('\n', ' ').Trim();
        return s.Length > 160 ? s[..160] + " …" : s;
    }

    /// <summary>
    /// Berechtigungsregel, um einen abgelehnten Aufruf künftig zu erlauben: bei Bash/PowerShell der Befehl mit
    /// Platzhalter nach dem ersten Wort („dotnet *“), sonst das Werkzeug selbst.
    /// </summary>
    public static string RuleFor(string tool, JsonElement input)
    {
        if (tool is "Bash" or "PowerShell" && input.ValueKind == JsonValueKind.Object && Str(input, "command") is { Length: > 0 } cmd)
        {
            var words = cmd.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            var prefix = words.Length >= 2 && !words[1].StartsWith('-') ? words[0] + " " + words[1] : words[0];
            return $"{tool}({prefix} *)";
        }
        return tool;
    }
}
