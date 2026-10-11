using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace BlazorStudio.Core.Build;

public enum DiagnosticSeverity { Error, Warning, Info }

public sealed record BuildDiagnostic(string File, int Line, int Column, DiagnosticSeverity Severity, string Code, string Message, string? Project);

/// <summary>Startet einen Prozess (dotnet build/run/watch) und streamt die Ausgabe zeilenweise.</summary>
public sealed partial class ProcessRunner : IDisposable
{
    private Process? _process;

    public event Action<string, bool>? Output;

    public event Action<int>? Exited;

    public bool IsRunning => _process is { HasExited: false };

    public int? ProcessId => IsRunning ? _process!.Id : null;

    public Task<int> RunAsync(string fileName, IEnumerable<string> arguments, string workingDirectory,
        IDictionary<string, string>? environment = null, CancellationToken cancellationToken = default)
    {
        if (IsRunning) throw new InvalidOperationException("Process already running.");

        var psi = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        psi.Environment["DOTNET_CLI_UI_LANGUAGE"] = psi.Environment.TryGetValue("DOTNET_CLI_UI_LANGUAGE", out var lang) && lang is not null ? lang : "en";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["DOTNET_WATCH_SUPPRESS_EMOJIS"] = "0";
        if (environment is not null)
            foreach (var kv in environment) psi.Environment[kv.Key] = kv.Value;

        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) Output?.Invoke(e.Data, false); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) Output?.Invoke(e.Data, true); };
        p.Exited += (_, _) =>
        {
            int code;
            try { code = p.ExitCode; } catch { code = -1; }
            // Ausgabe-Puffer leeren lassen
            try { p.WaitForExit(); } catch { }
            Exited?.Invoke(code);
            tcs.TrySetResult(code);
        };

        _process = p;
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        if (cancellationToken.CanBeCanceled)
            cancellationToken.Register(Stop);

        return tcs.Task;
    }

    /// <summary>Text an stdin senden (z. B. Strg+R für dotnet watch).</summary>
    public void SendInput(string text)
    {
        if (!IsRunning) return;
        try
        {
            _process!.StandardInput.Write(text);
            _process.StandardInput.Flush();
        }
        catch (IOException) { }
    }

    public void Stop()
    {
        var p = _process;
        if (p is null) return;
        try
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    public void Dispose()
    {
        Stop();
        _process?.Dispose();
        _process = null;
    }

    // ------------------------------------------------------------------

    /// <summary>Erkennt MSBuild-Diagnosen: Datei(Zeile,Spalte): error CS0103: Text [Projekt]</summary>
    public static BuildDiagnostic? ParseDiagnostic(string line)
    {
        var m = DiagnosticRegex().Match(line);
        if (!m.Success) return null;
        var sev = m.Groups["sev"].Value switch
        {
            "error" => DiagnosticSeverity.Error,
            "warning" => DiagnosticSeverity.Warning,
            _ => DiagnosticSeverity.Info,
        };
        var project = m.Groups["proj"].Success ? m.Groups["proj"].Value : null;
        return new BuildDiagnostic(m.Groups["file"].Value.Trim(), int.Parse(m.Groups["line"].Value), int.Parse(m.Groups["col"].Value),
            sev, m.Groups["code"].Value, m.Groups["msg"].Value.Trim(), project);
    }

    /// <summary>"Now listening on: https://localhost:5001"</summary>
    public static string? ParseListeningUrl(string line)
    {
        var m = ListeningRegex().Match(line);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    [GeneratedRegex(@"^\s*(?<file>[^\(\r\n]+?)\((?<line>\d+),(?<col>\d+)(?:,\d+,\d+)?\)\s*:\s*(?<sev>error|warning|info)\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<msg>.*?)(?:\s+\[(?<proj>[^\]]+)\])?\s*$")]
    private static partial Regex DiagnosticRegex();

    [GeneratedRegex(@"Now listening on:\s*(\S+)", RegexOptions.IgnoreCase)]
    private static partial Regex ListeningRegex();
}
