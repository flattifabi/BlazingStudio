using System.ComponentModel;
using System.Diagnostics;
using System.ServiceProcess;
using System.Text.RegularExpressions;
using BlazorStudio.Deploy;
using Microsoft.Win32;

namespace BlazorStudio.Agent;

/// <summary>
/// Windows-Dienste anlegen, umkonfigurieren, starten/stoppen und auflisten. Versionen liegen unter
/// &lt;DeployRoot&gt;\_services\&lt;Dienst&gt;\&lt;Version&gt; – umgeschaltet wird über den Programmpfad des Dienstes.
/// </summary>
public sealed partial class ServiceManager(AgentOptions options, SystemSampler sampler, ILogger<ServiceManager> log)
{
    private readonly SemaphoreSlim _write = new(1, 1);

    public VersionStore Store { get; } = new(Path.Combine(options.DeployRoot, "_services"));

    public static string DotnetPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");

    public string OverrideDir(string name) => Path.Combine(Store.TargetDirectory(name, "/"), SiteConfig.OverrideFolder);

    public static void RequireName(string name)
    {
        if (!ServiceCommandLine.IsValidName(name)) throw new ArgumentException($"Ungültiger Dienstname „{name}“ – erlaubt sind Buchstaben, Ziffern und _ . - (max. 80 Zeichen).");
    }

    public bool Exists(string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name);
        return key?.GetValue("ImagePath") is not null;
    }

    /// <summary>Dienste des Agents (Programm in der Versionsablage) oder mit <paramref name="all"/> alle Win32-Dienste.</summary>
    public List<ServiceInfo> List(bool all)
    {
        var result = new List<ServiceInfo>();
        foreach (var sc in ServiceController.GetServices())
        {
            using (sc)
            {
                var info = Read(sc);
                if (info is null || (!all && !info.Managed)) continue;
                result.Add(info);
            }
        }
        return result.OrderByDescending(s => s.Managed).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public ServiceInfo? Get(string name)
    {
        if (!Exists(name)) return null;
        using var sc = new ServiceController(name);
        return Read(sc);
    }

    private ServiceInfo? Read(ServiceController sc)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + sc.ServiceName);
            if (key?.GetValue("ImagePath") is not string imagePath) return null;
            var command = Environment.ExpandEnvironmentVariables(imagePath);
            var dir = ProgramDirectory(command);
            var start = key.GetValue("Start") is int s ? s : 3;
            var delayed = key.GetValue("DelayedAutostart") is int d && d == 1;
            var info = new ServiceInfo
            {
                Name = sc.ServiceName,
                DisplayName = sc.DisplayName,
                Description = key.GetValue("Description") as string,
                State = sc.Status.ToString(),
                StartType = start switch { 2 when delayed => "delayed", 2 => "auto", 4 => "disabled", 3 => "manual", _ => "system" },
                Account = key.GetValue("ObjectName") as string ?? "",
                CommandLine = command,
                Directory = dir,
                Managed = dir.Length > 0 && dir.StartsWith(Store.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            };
            if (info.Managed) info.ActiveVersion = Store.VersionFromPath(sc.ServiceName, "/", dir);
            if (sc.Status == ServiceControllerStatus.Running && info.Managed && ProcessIdOf(sc.ServiceName) is { } pid)
            {
                info.ProcessId = pid;
                if (sampler.Process(pid) is { } p)
                {
                    info.CpuPercent = p.CpuPercent;
                    info.WorkingSetBytes = p.WorkingSet;
                }
            }
            return info;
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>Ordner des Programms; bei „dotnet.exe "…\App.dll"“ der Ordner der .dll.</summary>
    public static string ProgramDirectory(string commandLine)
    {
        var (exe, args) = ServiceCommandLine.Split(commandLine);
        if (Path.GetFileName(exe).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            var (dll, _) = ServiceCommandLine.Split(args);
            if (dll.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) exe = dll;
        }
        try { return exe.Length == 0 ? "" : Path.GetDirectoryName(Path.GetFullPath(exe)) ?? ""; }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return ""; }
    }

    [GeneratedRegex(@"PID\s*:\s*(\d+)")]
    private static partial Regex PidPattern();

    private static int? ProcessIdOf(string name)
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("queryex");
            psi.ArgumentList.Add(name);
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            var m = PidPattern().Match(output);
            return m.Success && int.TryParse(m.Groups[1].Value, out var pid) && pid > 0 ? pid : null;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------
    // Ändern (sc.exe – die Dienststeuerung übernimmt Änderungen sofort, Registry-Schreiben erst nach Neustart)
    // ------------------------------------------------------------------

    private static async Task<string> ScAsync(params string[] args)
    {
        var psi = new ProcessStartInfo("sc.exe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("sc.exe konnte nicht gestartet werden.");
        var output = await p.StandardOutput.ReadToEndAsync();
        var error = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0)
        {
            var text = (output + " " + error).Trim();
            var line = text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? $"Exit-Code {p.ExitCode}";
            throw new InvalidOperationException($"sc {args[0]} fehlgeschlagen: {line}");
        }
        return output;
    }

    public async Task CreateAsync(string name, string commandLine, string? displayName, string? description, string? account, string? password, string? startType)
    {
        RequireName(name);
        await _write.WaitAsync();
        try
        {
            var args = new List<string> { "create", name, "binPath=", commandLine, "start=", ServiceCommandLine.ScStartType(startType) };
            if (!string.IsNullOrWhiteSpace(displayName)) args.AddRange(new[] { "DisplayName=", displayName.Trim() });
            if (!string.IsNullOrWhiteSpace(account))
            {
                args.AddRange(new[] { "obj=", ServiceCommandLine.ScAccount(account) });
                if (!string.IsNullOrEmpty(password)) args.AddRange(new[] { "password=", password });
            }
            await ScAsync(args.ToArray());
            if (!string.IsNullOrWhiteSpace(description)) await ScAsync("description", name, description.Trim());
            // Bei Absturz automatisch neu starten (5 s, 5 s, 30 s; Zähler nach einem Tag zurücksetzen)
            await ScAsync("failure", name, "reset=", "86400", "actions=", "restart/5000/restart/5000/restart/30000");
            log.LogInformation("Dienst {Service} angelegt: {Command}", name, commandLine);
        }
        finally
        {
            _write.Release();
        }
    }

    /// <summary>Nur gesetzte Werte ändern.</summary>
    public async Task ConfigureAsync(string name, string? commandLine = null, string? displayName = null, string? description = null,
        string? account = null, string? password = null, string? startType = null)
    {
        await _write.WaitAsync();
        try
        {
            var args = new List<string> { "config", name };
            if (commandLine is not null) args.AddRange(new[] { "binPath=", commandLine });
            if (!string.IsNullOrWhiteSpace(startType)) args.AddRange(new[] { "start=", ServiceCommandLine.ScStartType(startType) });
            if (!string.IsNullOrWhiteSpace(displayName)) args.AddRange(new[] { "DisplayName=", displayName.Trim() });
            if (!string.IsNullOrWhiteSpace(account))
            {
                args.AddRange(new[] { "obj=", ServiceCommandLine.ScAccount(account) });
                if (!string.IsNullOrEmpty(password)) args.AddRange(new[] { "password=", password });
            }
            if (args.Count > 2) await ScAsync(args.ToArray());
            if (!string.IsNullOrWhiteSpace(description)) await ScAsync("description", name, description.Trim());
        }
        finally
        {
            _write.Release();
        }
    }

    public async Task StartAsync(string name, TimeSpan timeout)
    {
        using var sc = new ServiceController(name);
        if (sc.Status == ServiceControllerStatus.Running) return;
        if (sc.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.Paused)
        {
            try { sc.Start(); }
            catch (InvalidOperationException e)
            {
                throw new InvalidOperationException($"Dienst {name} startet nicht: {e.InnerException?.Message ?? e.Message}" + Hint1053(e), e);
            }
        }
        await Task.Run(() =>
        {
            try { sc.WaitForStatus(ServiceControllerStatus.Running, timeout); }
            catch (System.ServiceProcess.TimeoutException)
            {
                sc.Refresh();
                throw new InvalidOperationException($"Dienst {name} läuft nach {timeout.TotalSeconds:0} s nicht (Status: {sc.Status}).");
            }
        });
    }

    private static string Hint1053(Exception e) =>
        (e.InnerException as Win32Exception)?.NativeErrorCode == 1053
            ? " – Die App meldet sich nicht beim Dienststeuerungs-Manager. In Program.cs fehlt vermutlich builder.Services.AddWindowsService() bzw. UseWindowsService() (Paket Microsoft.Extensions.Hosting.WindowsServices)."
            : "";

    /// <summary>Stoppen; hängt der Dienst, wird der Prozess nach dem Zeitlimit beendet.</summary>
    public async Task StopAsync(string name, TimeSpan timeout)
    {
        using var sc = new ServiceController(name);
        if (sc.Status == ServiceControllerStatus.Stopped) return;
        var pid = ProcessIdOf(name);
        if (sc.Status != ServiceControllerStatus.StopPending)
        {
            try { sc.Stop(); }
            catch (InvalidOperationException) { }
        }
        var stopped = await Task.Run(() =>
        {
            try
            {
                sc.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
                return true;
            }
            catch (System.ServiceProcess.TimeoutException)
            {
                return false;
            }
        });
        if (stopped) return;
        if (pid is { } id)
        {
            log.LogWarning("Dienst {Service} hält nicht an – Prozess {Pid} wird beendet", name, id);
            try { Process.GetProcessById(id).Kill(entireProcessTree: true); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception) { }
            await Task.Run(() =>
            {
                try { sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15)); }
                catch (System.ServiceProcess.TimeoutException) { }
            });
        }
        sc.Refresh();
        if (sc.Status != ServiceControllerStatus.Stopped) throw new InvalidOperationException($"Dienst {name} lässt sich nicht stoppen (Status: {sc.Status}).");
    }

    public async Task ActionAsync(string name, string action)
    {
        if (!Exists(name)) throw new NotFoundException($"Dienst „{name}“ gibt es nicht.");
        switch (action.ToLowerInvariant())
        {
            case "start":
                await StartAsync(name, TimeSpan.FromSeconds(60));
                break;
            case "stop":
                await StopAsync(name, TimeSpan.FromSeconds(60));
                break;
            case "restart":
                await StopAsync(name, TimeSpan.FromSeconds(60));
                await StartAsync(name, TimeSpan.FromSeconds(60));
                break;
            default:
                throw new ArgumentException($"Unbekannte Aktion: {action}");
        }
        log.LogInformation("Dienst {Service}: {Action}", name, action);
    }

    public async Task DeleteAsync(string name, bool removeFiles)
    {
        var info = Get(name) ?? throw new NotFoundException($"Dienst „{name}“ gibt es nicht.");
        if (!info.Managed) throw new InvalidOperationException($"Dienst „{name}“ wurde nicht vom Agent ausgerollt und wird hier nicht gelöscht.");
        await StopAsync(name, TimeSpan.FromSeconds(60));
        await ScAsync("delete", name);
        log.LogInformation("Dienst {Service} gelöscht", name);
        if (!removeFiles) return;
        var dir = Store.TargetDirectory(name, "/");
        for (var i = 0; i < 10 && Directory.Exists(dir); i++)
        {
            try { Directory.Delete(dir, true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { await Task.Delay(500); }
        }
    }

    /// <summary>Kurz warten und prüfen, ob der Dienst nach dem Start auch läuft (Absturz beim Hochfahren).</summary>
    public async Task<bool> StaysRunningAsync(string name, TimeSpan period)
    {
        var until = DateTime.UtcNow + period;
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(1000);
            using var sc = new ServiceController(name);
            if (sc.Status != ServiceControllerStatus.Running) return false;
        }
        return true;
    }
}
