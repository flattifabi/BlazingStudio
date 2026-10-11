using System.Diagnostics;
using System.Text;

namespace BlazorStudio.App.Services;

public enum TermLineKind { Out, Err, Cmd, Info }

public sealed record TermLine(string Text, TermLineKind Kind);

/// <summary>Eine Shell-Sitzung (PowerShell über stdin, wie das Terminal in Rider).</summary>
public sealed class TerminalSession : IDisposable
{
    private const string DoneMarker = "\u0001BSDONE|";
    private Process? _process;
    private readonly List<TermLine> _lines = new();
    private readonly object _gate = new();

    public TerminalSession(string name, string cwd)
    {
        Name = name;
        Cwd = cwd;
    }

    public string Name { get; set; }

    public string Cwd { get; private set; }

    public bool Busy { get; private set; }

    public List<string> History { get; } = new();

    public event Action? Changed;

    public IReadOnlyList<TermLine> Lines
    {
        get { lock (_gate) return _lines.ToList(); }
    }

    public string Prompt => "PS " + Cwd + "> ";

    public void Start()
    {
        var psi = new ProcessStartInfo(FindShell())
        {
            WorkingDirectory = Directory.Exists(Cwd) ? Cwd : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", "-" }) psi.ArgumentList.Add(a);
        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = System.Globalization.CultureInfo.CurrentUICulture.Name;
        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) => OnData(e.Data, TermLineKind.Out);
        _process.ErrorDataReceived += (_, e) => OnData(e.Data, TermLineKind.Err);
        _process.Exited += (_, _) =>
        {
            Busy = false;
            Add(new TermLine("[Prozess beendet]", TermLineKind.Info));
        };
        _process.Start();
        _process.StandardInput.AutoFlush = true;
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        Write("[Console]::OutputEncoding = [Text.Encoding]::UTF8; $ProgressPreference = 'SilentlyContinue'");
        Done();
    }

    private static string FindShell()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            try
            {
                var p = Path.Combine(dir.Trim(), "pwsh.exe");
                if (File.Exists(p)) return p;
            }
            catch (ArgumentException)
            {
            }
        }
        return "powershell.exe";
    }

    public void Execute(string command)
    {
        if (_process is null || _process.HasExited) Start();
        if (command.Trim() is "cls" or "clear" or "Clear-Host")
        {
            Clear();
            return;
        }
        Add(new TermLine(Prompt + command, TermLineKind.Cmd));
        if (command.Trim().Length > 0 && (History.Count == 0 || History[^1] != command)) History.Add(command);
        Busy = true;
        Write(command);
        Done();
    }

    /// <summary>Strg+C: laufenden Befehl abbrechen (Shell neu starten, Verzeichnis bleibt).</summary>
    public void Interrupt()
    {
        if (!Busy) return;
        Kill();
        Add(new TermLine("^C", TermLineKind.Info));
        Start();
    }

    public void Clear()
    {
        lock (_gate) _lines.Clear();
        Changed?.Invoke();
    }

    private void Write(string s)
    {
        try { _process?.StandardInput.WriteLine(s); }
        catch (IOException) { }
    }

    private void Done() => Write("Write-Output ([char]1 + 'BSDONE|' + $PWD.Path)");

    private void OnData(string? data, TermLineKind kind)
    {
        if (data is null) return;
        var i = data.IndexOf(DoneMarker, StringComparison.Ordinal);
        if (i >= 0)
        {
            if (i > 0) Add(new TermLine(data[..i], kind), notify: false);
            Cwd = data[(i + DoneMarker.Length)..].Trim();
            Busy = false;
            Changed?.Invoke();
            return;
        }
        Add(new TermLine(data, kind));
    }

    private void Add(TermLine line, bool notify = true)
    {
        lock (_gate)
        {
            _lines.Add(line);
            if (_lines.Count > 5000) _lines.RemoveRange(0, 1000);
        }
        if (notify) Changed?.Invoke();
    }

    private void Kill()
    {
        try
        {
            if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        _process?.Dispose();
        _process = null;
        Busy = false;
    }

    public void Dispose() => Kill();
}

/// <summary>Verwaltet die Terminal-Tabs.</summary>
public sealed class TerminalService : IDisposable
{
    private readonly Workspace _ws;
    private int _counter;

    public TerminalService(Workspace ws) => _ws = ws;

    public List<TerminalSession> Sessions { get; } = new();

    public TerminalSession? Active { get; private set; }

    public event Action? Changed;

    /// <summary>Neues Terminal (optional in einem bestimmten Ordner, z. B. „Terminal hier öffnen“).</summary>
    public TerminalSession Open(string? cwd = null)
    {
        _counter++;
        var dir = cwd ?? _ws.RootDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var s = new TerminalSession(_counter == 1 ? "Local" : "Local (" + _counter + ")", dir);
        s.Changed += () => Changed?.Invoke();
        s.Start();
        Sessions.Add(s);
        Active = s;
        Changed?.Invoke();
        return s;
    }

    public TerminalSession EnsureActive() => Active ?? Open();

    public void Select(TerminalSession s)
    {
        Active = s;
        Changed?.Invoke();
    }

    public void Close(TerminalSession s)
    {
        s.Dispose();
        Sessions.Remove(s);
        if (Active == s) Active = Sessions.LastOrDefault();
        Changed?.Invoke();
    }

    public void Dispose()
    {
        foreach (var s in Sessions) s.Dispose();
    }
}
