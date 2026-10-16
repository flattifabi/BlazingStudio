using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BlazorStudio.Core.Assistant;

namespace BlazorStudio.App.Services;

public enum ClaudeEntryKind { User, Assistant, Tool, Info, Error }

/// <summary>Ein Eintrag im Claude-Verlauf.</summary>
public sealed class ClaudeEntry
{
    public required ClaudeEntryKind Kind { get; init; }

    public string Text { get; set; } = "";

    /// <summary>Werkzeug: Name (Read, Edit, Bash …).</summary>
    public string? Tool { get; init; }

    public string? ToolUseId { get; init; }

    public string? FilePath { get; init; }

    /// <summary>Werkzeug: Ausgabe (gekürzt).</summary>
    public string? Output { get; set; }

    public bool Failed { get; set; }

    /// <summary>Noch im Entstehen (Teiltexte).</summary>
    public bool Streaming { get; set; }

    /// <summary>Abgelehnte Aufrufe am Ende einer Anfrage (zum Erlauben).</summary>
    public List<ClaudeDenial>? Denials { get; set; }
}

public sealed class ClaudeStatus
{
    public bool Installed { get; init; }

    public string? Version { get; init; }

    public bool LoggedIn { get; init; }

    /// <summary>claude.ai, api_key, oauth_token, …</summary>
    public string? AuthMethod { get; init; }

    public string? Account { get; init; }

    public string? Error { get; init; }
}

/// <summary>
/// Claude im IDE: nutzt die vom Benutzer installierte Claude-Code-CLI (<c>claude</c>) mit dessen eigener Anmeldung
/// (Abo oder API-Schlüssel). Studio liest oder speichert keine Zugangsdaten – die Anmeldung läuft vollständig über
/// <c>claude auth login</c> in einem Konsolenfenster. Anfragen laufen als <c>claude -p … --output-format stream-json</c>
/// im Projektordner; die Sitzung wird mit <c>--resume</c> fortgesetzt.
/// </summary>
public sealed class ClaudeService : IDisposable
{
    private readonly Workspace _ws;
    private readonly SettingsService _settings;
    private readonly IHostBridge _host;
    private readonly Localizer _l;
    private Process? _process;
    private ClaudeEntry? _streaming;

    public ClaudeService(Workspace ws, SettingsService settings, IHostBridge host, Localizer l)
    {
        _ws = ws;
        _settings = settings;
        _host = host;
        _l = l;
    }

    public event Action? Changed;

    public List<ClaudeEntry> Entries { get; } = new();

    public ClaudeStatus? Status { get; private set; }

    public bool Checking { get; private set; }

    public bool Busy { get; private set; }

    public string? SessionId { get; private set; }

    public string? Model { get; private set; }

    private void Notify() => _ = _host.InvokeOnUiAsync(() => Changed?.Invoke());

    // ------------------------------------------------------------------
    // Programm finden
    // ------------------------------------------------------------------

    /// <summary>(Programm, Argumente davor) – claude.exe direkt, bei npm-Installation node + cli.js (keine .cmd: cmd.exe würde Sonderzeichen im Prompt deuten).</summary>
    public static (string File, string[] Prefix)? Resolve()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "claude.exe" } : new[] { "claude" };
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        dirs.Add(Path.Combine(home, ".local", "bin"));
        foreach (var dir in dirs)
        {
            foreach (var n in names)
            {
                try
                {
                    var f = Path.Combine(dir.Trim('"'), n);
                    if (File.Exists(f)) return (f, Array.Empty<string>());
                }
                catch (ArgumentException)
                {
                }
            }
        }
        // npm-Installation: %APPDATA%\npm\node_modules\@anthropic-ai\claude-code\cli.js
        var npm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm");
        var cli = Path.Combine(npm, "node_modules", "@anthropic-ai", "claude-code", "cli.js");
        if (File.Exists(cli))
        {
            var node = dirs.Select(d => { try { return Path.Combine(d.Trim('"'), OperatingSystem.IsWindows() ? "node.exe" : "node"); } catch (ArgumentException) { return ""; } })
                .FirstOrDefault(File.Exists);
            if (node is not null) return (node, new[] { cli });
        }
        return null;
    }

    private static ProcessStartInfo StartInfo((string File, string[] Prefix) exe, IEnumerable<string> args, string cwd)
    {
        var psi = new ProcessStartInfo(exe.File)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in exe.Prefix.Concat(args)) psi.ArgumentList.Add(a);
        return psi;
    }

    private static async Task<(int Code, string Output)> RunOnceAsync((string File, string[] Prefix) exe, params string[] args)
    {
        using var p = Process.Start(StartInfo(exe, args, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)))!;
        p.StandardInput.Close();
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch (InvalidOperationException) { }
            return (-1, "timeout");
        }
        return (p.ExitCode, (await output) + (await error));
    }

    // ------------------------------------------------------------------
    // Status, Anmeldung, Installation
    // ------------------------------------------------------------------

    public async Task CheckAsync()
    {
        if (Checking) return;
        Checking = true;
        Notify();
        try
        {
            Status = await Task.Run(CheckCoreAsync);
        }
        finally
        {
            Checking = false;
            Notify();
        }
    }

    private static async Task<ClaudeStatus> CheckCoreAsync()
    {
        var exe = Resolve();
        if (exe is null) return new ClaudeStatus { Installed = false };
        try
        {
            var (_, version) = await RunOnceAsync(exe.Value, "--version");
            var (code, auth) = await RunOnceAsync(exe.Value, "auth", "status");
            string? method = null, account = null;
            var json = auth.IndexOf('{') is var i and >= 0 ? auth[i..] : null;
            if (json is not null)
            {
                try
                {
                    using var doc = JsonDocument.Parse(json[..(json.LastIndexOf('}') + 1)]);
                    var r = doc.RootElement;
                    method = r.TryGetProperty("authMethod", out var m) ? m.GetString() : null;
                    foreach (var key in new[] { "email", "account", "organization", "orgName" })
                        if (account is null && r.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String) account = v.GetString();
                }
                catch (JsonException)
                {
                }
            }
            return new ClaudeStatus
            {
                Installed = true,
                Version = version.Trim().Split('\n')[0].Trim(),
                LoggedIn = code == 0 && method is not "none",
                AuthMethod = method,
                Account = account,
            };
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
        {
            return new ClaudeStatus { Installed = true, Error = e.Message };
        }
    }

    /// <summary>Anmeldung im eigenen Konsolenfenster – die Anmeldung läuft vollständig über Claude Code selbst.</summary>
    public void Login() => OpenConsole("auth login", keepOpen: true);

    public void Logout() => OpenConsole("auth logout", keepOpen: true);

    /// <summary>Interaktives Claude im Projektordner (echtes Konsolenfenster – das integrierte Terminal ist kein TTY).</summary>
    public void OpenInteractive() => OpenConsole("", keepOpen: true, cwd: _ws.RootDirectory);

    private void OpenConsole(string args, bool keepOpen, string? cwd = null)
    {
        var exe = Resolve() ?? throw new InvalidOperationException(_l["claude.notInstalled"]);
        var dir = cwd ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var command = "\"" + exe.File + "\"" + string.Concat(exe.Prefix.Select(p => " \"" + p + "\"")) + (args.Length > 0 ? " " + args : "");
        if (OperatingSystem.IsWindows())
        {
            var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = true, WorkingDirectory = dir };
            psi.ArgumentList.Add(keepOpen ? "/k" : "/c");
            psi.ArgumentList.Add(command);
            Process.Start(psi);
        }
        else
        {
            Process.Start(new ProcessStartInfo("x-terminal-emulator", "-e " + command) { UseShellExecute = true, WorkingDirectory = dir });
        }
    }

    /// <summary>Offizielles Installationsskript von Anthropic in einem PowerShell-Fenster (sichtbar für den Benutzer).</summary>
    public void Install()
    {
        var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = true };
        psi.ArgumentList.Add("-NoExit");
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add("irm https://claude.ai/install.ps1 | iex; Write-Host ''; Write-Host 'Fertig - jetzt in Blazor Studio auf \"Erneut prüfen\" klicken.'");
        Process.Start(psi);
    }

    // ------------------------------------------------------------------
    // Chat
    // ------------------------------------------------------------------

    public void NewChat()
    {
        if (Busy) return;
        Entries.Clear();
        SessionId = null;
        _streaming = null;
        Notify();
    }

    public void Stop()
    {
        try { _process?.Kill(entireProcessTree: true); }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception) { }
    }

    /// <summary>Kontext für Claude: wo im IDE der Benutzer gerade ist.</summary>
    private string Context()
    {
        var sb = new StringBuilder("You are running inside Blazor Studio, a visual IDE for Blazor/.NET (designer + code editor). ");
        if (_ws.Solution?.Path is { } sln) sb.Append("Solution: ").Append(sln).Append(". ");
        if (_ws.StartupItem?.Path is { } proj) sb.Append("Startup project: ").Append(proj).Append(". ");
        if (_ws.Active is { Remote: null } doc)
            sb.Append("The user currently has ").Append(doc.Path).Append(" open in the editor (cursor at line ").Append(_ws.CursorLine).Append("). ");
        sb.Append("Files you change are reloaded in the IDE automatically. Reply in the language the user writes in.");
        return sb.ToString();
    }

    public async Task SendAsync(string prompt)
    {
        prompt = prompt.Trim();
        if (prompt.Length == 0 || Busy) return;
        var exe = Resolve();
        if (exe is null)
        {
            Entries.Add(new ClaudeEntry { Kind = ClaudeEntryKind.Error, Text = _l["claude.notInstalled"] });
            Notify();
            return;
        }
        var cwd = _ws.RootDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // Ungespeicherte Änderungen zuerst speichern – Claude arbeitet auf den Dateien
        _ws.SaveAll();
        Entries.Add(new ClaudeEntry { Kind = ClaudeEntryKind.User, Text = prompt });
        Busy = true;
        _streaming = null;
        Notify();

        var s = _settings.Current;
        var args = new List<string> { "-p", "--output-format", "stream-json", "--verbose", "--include-partial-messages" };
        if (SessionId is not null) args.AddRange(new[] { "--resume", SessionId });
        var mode = s.ClaudeMode is "plan" or "acceptEdits" or "bypassPermissions" ? s.ClaudeMode : "acceptEdits";
        args.AddRange(new[] { "--permission-mode", mode });
        if (!string.IsNullOrWhiteSpace(s.ClaudeModel)) args.AddRange(new[] { "--model", s.ClaudeModel.Trim() });
        args.AddRange(new[] { "--append-system-prompt", Context() });
        // Kein Mensch zum Nachfragen: was nicht erlaubt ist, wird abgelehnt und kann danach im Panel erlaubt werden
        if (s.ClaudeAllowedTools.Count > 0)
        {
            args.Add("--allowedTools");
            args.AddRange(s.ClaudeAllowedTools);
        }
        // Prompt zuletzt hinter „--“: beginnt er mit „-“, wäre er sonst eine Option
        args.Add("--");
        args.Add(prompt);

        var stderr = new StringBuilder();
        try
        {
            var psi = StartInfo(exe.Value, args, cwd);
            // Ein evtl. gesetzter API-Schlüssel hätte bei -p Vorrang vor dem Abo – nur, wenn der Benutzer per Abo angemeldet ist
            if (Status?.AuthMethod == "claude.ai") psi.Environment.Remove("ANTHROPIC_API_KEY");
            using var p = _process = Process.Start(psi) ?? throw new InvalidOperationException("claude konnte nicht gestartet werden.");
            p.StandardInput.Close();
            var errTask = Task.Run(async () =>
            {
                while (await p.StandardError.ReadLineAsync() is { } line) lock (stderr) stderr.AppendLine(line);
            });
            while (await p.StandardOutput.ReadLineAsync() is { } line)
            {
                foreach (var ev in ClaudeStream.Parse(line))
                    await _host.InvokeOnUiAsync(() => Apply(ev));
            }
            await p.WaitForExitAsync();
            await errTask;
            if (p.ExitCode != 0 && !Entries.Any(e => e.Kind == ClaudeEntryKind.Info && e.Denials is not null) && Entries.LastOrDefault()?.Kind != ClaudeEntryKind.Error)
            {
                var err = stderr.ToString().Trim();
                if (err.Length > 0 || p.ExitCode is not (143 or -1 or 137))
                    await _host.InvokeOnUiAsync(() => Entries.Add(new ClaudeEntry { Kind = ClaudeEntryKind.Error, Text = err.Length > 0 ? err : _l.F("claude.exitCode", p.ExitCode) }));
            }
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
        {
            await _host.InvokeOnUiAsync(() => Entries.Add(new ClaudeEntry { Kind = ClaudeEntryKind.Error, Text = e.Message }));
        }
        finally
        {
            _process = null;
            Busy = false;
            if (_streaming is not null) _streaming.Streaming = false;
            _streaming = null;
            // Geänderte Dateien in offenen Editoren neu laden
            await _host.InvokeOnUiAsync(() =>
            {
                _ws.ReloadAllFromDisk();
                Changed?.Invoke();
            });
        }
    }

    private void Apply(ClaudeEvent ev)
    {
        switch (ev)
        {
            case ClaudeInit init:
                SessionId = init.SessionId.Length > 0 ? init.SessionId : SessionId;
                Model = init.Model;
                break;
            case ClaudeTextDelta d:
                if (_streaming is null)
                {
                    _streaming = new ClaudeEntry { Kind = ClaudeEntryKind.Assistant, Streaming = true };
                    Entries.Add(_streaming);
                }
                _streaming.Text += d.Text;
                break;
            case ClaudeText t:
                // Fertiger Block ersetzt die Teiltexte (kein doppelter Text)
                if (_streaming is not null)
                {
                    _streaming.Text = t.Text;
                    _streaming.Streaming = false;
                    _streaming = null;
                }
                else
                {
                    Entries.Add(new ClaudeEntry { Kind = ClaudeEntryKind.Assistant, Text = t.Text });
                }
                break;
            case ClaudeToolUse u:
                if (_streaming is not null) { _streaming.Streaming = false; _streaming = null; }
                Entries.Add(new ClaudeEntry { Kind = ClaudeEntryKind.Tool, Tool = u.Name, ToolUseId = u.Id, Text = u.Summary, FilePath = u.FilePath });
                break;
            case ClaudeToolResult r:
                if (Entries.LastOrDefault(e => e.ToolUseId == r.ToolUseId) is { } tool)
                {
                    tool.Output = r.Text;
                    tool.Failed = r.IsError;
                }
                break;
            case ClaudeRetry retry:
                Entries.Add(new ClaudeEntry { Kind = ClaudeEntryKind.Info, Text = _l.F("claude.retry", retry.Attempt, retry.Error ?? "") });
                break;
            case ClaudeResult res:
                if (res.SessionId is { Length: > 0 } sid) SessionId = sid;
                if (res.IsError)
                    Entries.Add(new ClaudeEntry { Kind = ClaudeEntryKind.Error, Text = res.Text ?? _l["claude.failed"] });
                if (res.Denials.Count > 0)
                    Entries.Add(new ClaudeEntry { Kind = ClaudeEntryKind.Info, Text = _l["claude.denied"], Denials = res.Denials.ToList() });
                Entries.Add(new ClaudeEntry { Kind = ClaudeEntryKind.Info, Text = _l.F("claude.done", (res.DurationMs / 1000.0).ToString("0.0", System.Globalization.CultureInfo.CurrentCulture), res.Turns) });
                break;
        }
        Changed?.Invoke();
    }

    /// <summary>Abgelehnte Aufrufe dauerhaft erlauben und Claude weitermachen lassen.</summary>
    public async Task AllowAndContinueAsync(ClaudeEntry entry)
    {
        if (entry.Denials is not { Count: > 0 } denials || Busy) return;
        foreach (var d in denials)
            if (!_settings.Current.ClaudeAllowedTools.Contains(d.Rule, StringComparer.Ordinal)) _settings.Current.ClaudeAllowedTools.Add(d.Rule);
        _settings.Save();
        entry.Denials = null;
        await SendAsync(_l["claude.continuePrompt"]);
    }

    public void Dispose() => Stop();
}
