using System.Text.RegularExpressions;

namespace BlazorStudio.Core.Highlighting;

/// <summary>
/// Findet die Referenz-Assemblies des installierten .NET SDK (packs/Microsoft.NETCore.App.Ref,
/// Microsoft.AspNetCore.App.Ref …) passend zum Target-Framework des Projekts – wie der echte Compiler.
/// Unabhängig davon, mit welcher Runtime Blazor Studio selbst läuft (die installierte App ist self-contained
/// und enthält z. B. kein ASP.NET Core).
/// </summary>
public static partial class ReferenceAssemblies
{
    private static readonly string[] Packs = { "Microsoft.NETCore.App.Ref", "Microsoft.AspNetCore.App.Ref" };

    /// <summary>Installationsordner von dotnet (DOTNET_ROOT, PATH, Programme), sonst null.</summary>
    public static string? DotnetRoot()
    {
        var candidates = new List<string?>
        {
            Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            Environment.GetEnvironmentVariable("DOTNET_ROOT(x64)"),
        };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                var exe = Path.Combine(dir.Trim().Trim('"'), OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
                if (!File.Exists(exe)) continue;
                var target = new FileInfo(exe).LinkTarget is { } link ? Path.GetFullPath(link, dir) : exe;
                candidates.Add(Path.GetDirectoryName(target));
            }
            catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
            {
            }
        }
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "dotnet"));
        candidates.Add("/usr/share/dotnet");
        candidates.Add("/usr/local/share/dotnet");
        return candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c) && (Directory.Exists(Path.Combine(c!, "packs")) || Directory.Exists(Path.Combine(c!, "shared"))));
    }

    /// <summary>
    /// Referenz-DLLs für ein Target-Framework (z. B. "net10.0", "net8.0-windows"). Reihenfolge:
    /// SDK-Referenzpakete → geteilte Runtimes von dotnet → Runtime der laufenden App (Notlösung).
    /// </summary>
    public static IReadOnlyList<string> Find(string? targetFramework, bool includeAspNetCore = true)
    {
        var major = MajorOf(targetFramework);
        var root = DotnetRoot();
        var result = new List<string>();
        if (root is not null)
        {
            foreach (var pack in includeAspNetCore ? Packs : Packs.Take(1))
            {
                var dir = Path.Combine(root, "packs", pack);
                var version = PickVersion(dir, major);
                if (version is null) continue;
                var refDir = Path.Combine(dir, version, "ref");
                var tfmDir = Directory.Exists(refDir)
                    ? Directory.GetDirectories(refDir).OrderByDescending(d => MajorOf(Path.GetFileName(d)) == major ? 1 : 0).ThenByDescending(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase).FirstOrDefault()
                    : null;
                if (tfmDir is not null) result.AddRange(Directory.GetFiles(tfmDir, "*.dll"));
            }
            if (result.Count == 0)
            {
                // Kein SDK-Referenzpaket (nur Runtime installiert): geteilte Runtimes verwenden
                foreach (var fw in includeAspNetCore ? new[] { "Microsoft.NETCore.App", "Microsoft.AspNetCore.App" } : new[] { "Microsoft.NETCore.App" })
                {
                    var dir = Path.Combine(root, "shared", fw);
                    var version = PickVersion(dir, major);
                    if (version is not null) result.AddRange(Directory.GetFiles(Path.Combine(dir, version), "*.dll").Where(IsManaged));
                }
            }
        }
        if (result.Count == 0)
        {
            var runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
            result.AddRange(Directory.GetFiles(runtimeDir, "*.dll").Where(IsManaged));
        }
        // gleiche Assembly nur einmal (AspNetCore.Ref enthält manche Microsoft.Extensions-DLLs, NETCore.Ref nicht)
        return result.GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
    }

    private static string? PickVersion(string packDir, int? major)
    {
        if (!Directory.Exists(packDir)) return null;
        var versions = Directory.GetDirectories(packDir).Select(Path.GetFileName).Where(v => v is not null && VersionRegex().IsMatch(v!)).Select(v => v!).ToList();
        if (versions.Count == 0) return null;
        var cmp = Comparer<string>.Create(NuGet.NuGetClient.CompareVersions);
        var stable = versions.Where(v => !v.Contains('-')).ToList();
        IEnumerable<string> pool = major is { } m && versions.Any(v => v.StartsWith(m + ".", StringComparison.Ordinal))
            ? versions.Where(v => v.StartsWith(m + ".", StringComparison.Ordinal))
            : versions;
        var list = pool.ToList();
        var best = list.Where(v => !v.Contains('-')).OrderByDescending(v => v, cmp).FirstOrDefault()
                   ?? list.OrderByDescending(v => v, cmp).FirstOrDefault();
        return best ?? stable.OrderByDescending(v => v, cmp).FirstOrDefault();
    }

    /// <summary>"net10.0-windows" → 10, "net8.0" → 8, sonst null.</summary>
    public static int? MajorOf(string? tfm)
    {
        if (string.IsNullOrEmpty(tfm)) return null;
        var m = TfmRegex().Match(tfm);
        return m.Success && int.TryParse(m.Groups[1].Value, out var v) ? v : null;
    }

    private static bool IsManaged(string f)
    {
        var n = Path.GetFileName(f);
        return !n.StartsWith("api-ms-", StringComparison.OrdinalIgnoreCase) && !n.Contains("Native", StringComparison.Ordinal)
               && !n.StartsWith("mscordaccore", StringComparison.OrdinalIgnoreCase) && !n.StartsWith("msquic", StringComparison.OrdinalIgnoreCase)
               && n is not ("clrjit.dll" or "coreclr.dll" or "hostpolicy.dll" or "clrgc.dll" or "clrgcexp.dll" or "hostfxr.dll" or "clretwrc.dll" or "mscorrc.dll" or "createdump.exe");
    }

    [GeneratedRegex(@"^net(\d+)\.\d+")]
    private static partial Regex TfmRegex();

    [GeneratedRegex(@"^\d+\.\d+\.\d+")]
    private static partial Regex VersionRegex();
}
