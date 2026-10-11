using System.Text.Json;
using System.Text.RegularExpressions;

namespace BlazorStudio.Core.Build;

/// <summary>Liest das Nötigste aus .csproj und launchSettings.json (Ausgabe-DLL, URLs, Umgebung).</summary>
public static partial class ProjectInfo
{
    public static string? TargetFramework(string csproj)
    {
        var text = File.ReadAllText(csproj);
        var m = TfmRegex().Match(text);
        if (m.Success) return m.Groups[1].Value.Trim();
        var multi = TfmsRegex().Match(text);
        return multi.Success ? multi.Groups[1].Value.Split(';')[0].Trim() : null;
    }

    public static string AssemblyName(string csproj)
    {
        var text = File.ReadAllText(csproj);
        var m = AssemblyRegex().Match(text);
        return m.Success ? m.Groups[1].Value.Trim() : Path.GetFileNameWithoutExtension(csproj);
    }

    public static bool IsWebAssembly(string csproj) =>
        File.ReadAllText(csproj).Contains("Microsoft.NET.Sdk.BlazorWebAssembly", StringComparison.OrdinalIgnoreCase);

    public static string? OutputDll(string csproj, string configuration = "Debug")
    {
        var tfm = TargetFramework(csproj);
        if (tfm is null) return null;
        var dir = Path.GetDirectoryName(csproj)!;
        var dll = Path.Combine(dir, "bin", configuration, tfm, AssemblyName(csproj) + ".dll");
        return dll;
    }

    public sealed record LaunchProfile(string Name, string? ApplicationUrl, Dictionary<string, string> Environment);

    public static IReadOnlyList<LaunchProfile> LaunchProfiles(string csproj)
    {
        var path = Path.Combine(Path.GetDirectoryName(csproj)!, "Properties", "launchSettings.json");
        var list = new List<LaunchProfile>();
        if (!File.Exists(path)) return list;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (!doc.RootElement.TryGetProperty("profiles", out var profiles)) return list;
            foreach (var p in profiles.EnumerateObject())
            {
                if (p.Value.TryGetProperty("commandName", out var cmd) && cmd.GetString() != "Project") continue;
                var url = p.Value.TryGetProperty("applicationUrl", out var u) ? u.GetString() : null;
                var env = new Dictionary<string, string>();
                if (p.Value.TryGetProperty("environmentVariables", out var e))
                    foreach (var kv in e.EnumerateObject()) env[kv.Name] = kv.Value.GetString() ?? "";
                list.Add(new LaunchProfile(p.Name, url, env));
            }
        }
        catch (JsonException) { }
        return list;
    }

    [GeneratedRegex(@"<TargetFramework>([^<]+)</TargetFramework>")]
    private static partial Regex TfmRegex();

    [GeneratedRegex(@"<TargetFrameworks>([^<]+)</TargetFrameworks>")]
    private static partial Regex TfmsRegex();

    [GeneratedRegex(@"<AssemblyName>([^<]+)</AssemblyName>")]
    private static partial Regex AssemblyRegex();
}
