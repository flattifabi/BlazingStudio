using System.Text.RegularExpressions;

namespace BlazorStudio.Deploy;

public static class DeployTargets
{
    public const string Iis = "iis";
    public const string Service = "service";
}

/// <summary>Ein Windows-Dienst (GET /api/services).</summary>
public sealed class ServiceInfo
{
    public string Name { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public string? Description { get; set; }

    /// <summary>Running, Stopped, StartPending, StopPending, Paused …</summary>
    public string State { get; set; } = "";

    /// <summary>auto, delayed, manual, disabled.</summary>
    public string StartType { get; set; } = "";

    public string Account { get; set; } = "";

    /// <summary>Kompletter Befehl (ImagePath) – Programm und Argumente.</summary>
    public string CommandLine { get; set; } = "";

    /// <summary>Ordner des Programms.</summary>
    public string Directory { get; set; } = "";

    /// <summary>Aktive Version, wenn der Dienst aus der Versionsablage des Agents läuft.</summary>
    public string? ActiveVersion { get; set; }

    /// <summary>Vom Agent ausgerollt (Programm liegt in der Versionsablage).</summary>
    public bool Managed { get; set; }

    public int? ProcessId { get; set; }

    public double? CpuPercent { get; set; }

    public long? WorkingSetBytes { get; set; }
}

/// <summary>POST /api/services/{name}/activate.</summary>
public sealed class ServiceActivateRequest
{
    public string Version { get; set; } = "";
}

/// <summary>Kommandozeile eines Dienstes (ImagePath) zerlegen und bauen, Programm im Paket finden.</summary>
public static partial class ServiceCommandLine
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.\-]{0,79}$")]
    private static partial Regex NamePattern();

    /// <summary>Gültiger Dienstname (Buchstaben, Ziffern, _ . -; max. 80).</summary>
    public static bool IsValidName(string? name) => name is not null && NamePattern().IsMatch(name);

    public static readonly string[] StartTypes = { "auto", "delayed", "manual" };

    public static readonly string[] BuiltInAccounts = { "LocalSystem", "LocalService", "NetworkService" };

    /// <summary>„"C:\a b\x.exe" --port 5" → (C:\a b\x.exe, --port 5); auch ohne Anführungszeichen.</summary>
    public static (string Exe, string Arguments) Split(string? commandLine)
    {
        var s = (commandLine ?? "").Trim();
        if (s.Length == 0) return ("", "");
        if (s[0] == '"')
        {
            var end = s.IndexOf('"', 1);
            return end < 0 ? (s.Trim('"'), "") : (s[1..end], s[(end + 1)..].Trim());
        }
        // Ohne Anführungszeichen: bis zur Endung .exe (Pfade mit Leerzeichen kommen bei alten Diensten vor)
        var exe = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0) return (s[..(exe + 4)], s[(exe + 4)..].Trim());
        var space = s.IndexOf(' ');
        return space < 0 ? (s, "") : (s[..space], s[(space + 1)..].Trim());
    }

    /// <summary>Programm immer in Anführungszeichen (sonst Sicherheitslücke „unquoted service path“).</summary>
    public static string Build(string exe, string? arguments) =>
        "\"" + exe + "\"" + (string.IsNullOrWhiteSpace(arguments) ? "" : " " + arguments.Trim());

    /// <summary>
    /// Programm im Versionsordner, relativ: angegebene Datei (.exe oder .dll), sonst die eine .exe der App
    /// (neben einer *.runtimeconfig.json), sonst die eine .dll mit runtimeconfig (framework-abhängig ohne apphost → dotnet).
    /// </summary>
    public static (string? Program, string? Error) FindProgram(string versionDir, string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var rel = requested.Trim().Replace('\\', '/').TrimStart('/');
            if (rel.Contains(':') || rel.Split('/').Any(p => p == ".."))
                return (null, $"Programm „{requested}“ muss relativ zum Paket angegeben werden.");
            if (!File.Exists(Path.Combine(versionDir, rel.Replace('/', Path.DirectorySeparatorChar))))
                return (null, $"Programm „{requested}“ ist nicht im Paket.");
            return (rel, null);
        }
        var configs = System.IO.Directory.GetFiles(versionDir, "*.runtimeconfig.json")
            .Select(f => Path.GetFileName(f)[..^".runtimeconfig.json".Length]).ToList();
        var exes = System.IO.Directory.GetFiles(versionDir, "*.exe").Select(Path.GetFileName).Select(f => f!).ToList();
        var apphosts = exes.Where(e => configs.Contains(Path.GetFileNameWithoutExtension(e), StringComparer.OrdinalIgnoreCase)).ToList();
        if (apphosts.Count == 1) return (apphosts[0], null);
        if (apphosts.Count == 0 && exes.Count == 1) return (exes[0], null);
        if (apphosts.Count == 0 && exes.Count == 0 && configs.Count == 1) return (configs[0] + ".dll", null);
        var candidates = apphosts.Count > 0 ? apphosts : exes;
        return (null, candidates.Count == 0
            ? "Im Paket wurde kein Programm gefunden (keine .exe und keine .dll mit runtimeconfig)."
            : "Mehrere Programme im Paket – bitte das Programm angeben: " + string.Join(", ", candidates));
    }

    /// <summary>Befehl des Dienstes für eine Version: .exe direkt, .dll über dotnet.</summary>
    public static string CommandFor(string versionDir, string program, string? arguments, string dotnetPath)
    {
        var full = Path.Combine(versionDir, program.Replace('/', Path.DirectorySeparatorChar));
        return program.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? Build(dotnetPath, Quote(full) + (string.IsNullOrWhiteSpace(arguments) ? "" : " " + arguments.Trim()))
            : Build(full, arguments);
    }

    private static string Quote(string path) => "\"" + path + "\"";

    /// <summary>Kontoname für sc.exe: LocalService → „NT AUTHORITY\LocalService“.</summary>
    public static string ScAccount(string account) => account.Trim() switch
    {
        var a when a.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase) => "LocalSystem",
        var a when a.Equals("LocalService", StringComparison.OrdinalIgnoreCase) => @"NT AUTHORITY\LocalService",
        var a when a.Equals("NetworkService", StringComparison.OrdinalIgnoreCase) => @"NT AUTHORITY\NetworkService",
        var a => a,
    };

    /// <summary>sc.exe-Starttyp.</summary>
    public static string ScStartType(string? startType) => (startType ?? "auto").Trim().ToLowerInvariant() switch
    {
        "manual" or "demand" => "demand",
        "delayed" or "delayed-auto" => "delayed-auto",
        "disabled" => "disabled",
        _ => "auto",
    };
}
