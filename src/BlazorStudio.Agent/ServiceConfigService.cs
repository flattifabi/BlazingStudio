using System.Text;
using System.Text.Json;
using BlazorStudio.Deploy;

namespace BlazorStudio.Agent;

/// <summary>
/// appsettings*.json-Überschreibungen und persistente Dateien eines Windows-Dienstes – wie bei IIS-Sites
/// (<see cref="SiteConfigService"/>), nur ohne web.config. Ablage: &lt;DeployRoot&gt;\_services\&lt;Dienst&gt;\_config\.
/// </summary>
public sealed class ServiceConfigService(ServiceManager services, ILogger<ServiceConfigService> log)
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private const string PersistFile = "_persistent.json";

    private static string Require(string file) =>
        SiteConfig.IsAllowedFile(file) && !file.Equals(SiteConfig.WebConfig, StringComparison.OrdinalIgnoreCase)
            ? file
            : throw new ArgumentException($"„{file}“ kann bei Diensten nicht bearbeitet werden (nur appsettings*.json).");

    private string? LiveDir(string name)
    {
        var info = services.Get(name) ?? throw new NotFoundException($"Dienst „{name}“ gibt es nicht.");
        return info.Directory.Length > 0 && Directory.Exists(info.Directory) ? info.Directory : null;
    }

    public List<SiteConfigFile> List(string name)
    {
        var live = LiveDir(name);
        var overrides = services.OverrideDir(name);
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase) { SiteConfig.AppSettings };
        foreach (var dir in new[] { live, overrides })
        {
            if (dir is null || !Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                var n = Path.GetFileName(f);
                if (SiteConfig.IsAllowedFile(n) && !n.Equals(SiteConfig.WebConfig, StringComparison.OrdinalIgnoreCase)) names.Add(n);
            }
        }
        return names.Select(n =>
        {
            var o = new FileInfo(Path.Combine(overrides, n));
            var l = live is null ? null : new FileInfo(Path.Combine(live, n));
            var shown = o.Exists ? o : l is { Exists: true } ? l : null;
            return new SiteConfigFile { Name = n, InActive = l is { Exists: true }, HasOverride = o.Exists, Size = shown?.Length ?? 0, Modified = shown?.LastWriteTimeUtc };
        }).OrderBy(f => f.Name.Length).ThenBy(f => f.Name).ToList();
    }

    public SiteConfigContent Read(string name, string file)
    {
        Require(file);
        var o = Path.Combine(services.OverrideDir(name), file);
        if (File.Exists(o)) return new SiteConfigContent { Name = file, Content = File.ReadAllText(o), HasOverride = true, Source = "override" };
        var live = LiveDir(name);
        if (live is not null && File.Exists(Path.Combine(live, file)))
            return new SiteConfigContent { Name = file, Content = File.ReadAllText(Path.Combine(live, file)), Source = "active" };
        return new SiteConfigContent { Name = file, Content = "{\n}\n", Source = "new" };
    }

    public async Task<SiteConfigWriteResult> WriteAsync(string name, string file, SiteConfigWrite write)
    {
        Require(file);
        var content = write.Content ?? "";
        if (SiteConfig.Validate(file, content) is { } error) throw new ArgumentException(error);
        var live = LiveDir(name);
        var dir = services.OverrideDir(name);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, file), content, Utf8);
        log.LogInformation("Konfiguration Dienst {Service}/{File} gespeichert", name, file);
        var result = new SiteConfigWriteResult { Content = content };
        if (write.Apply && live is not null)
        {
            await File.WriteAllTextAsync(Path.Combine(live, file), content, Utf8);
            result.Applied = true;
            if (services.Get(name)?.State == "Running")
            {
                await services.ActionAsync(name, "restart");
                result.Recycled = true;
                result.Notes.Add("Dienst neu gestartet.");
            }
        }
        return result;
    }

    public void DeleteOverride(string name, string file)
    {
        Require(file);
        var path = Path.Combine(services.OverrideDir(name), file);
        if (!File.Exists(path)) throw new NotFoundException($"Für {file} gibt es keine Überschreibung.");
        File.Delete(path);
    }

    /// <summary>Beim Deployment/Umschalten: eigene appsettings in den Versionsordner kopieren.</summary>
    public void PrepareVersion(string name, string versionDir, Action<string, string> logLine)
    {
        var dir = services.OverrideDir(name);
        if (!Directory.Exists(dir)) return;
        var copied = new List<string>();
        foreach (var f in Directory.EnumerateFiles(dir))
        {
            var n = Path.GetFileName(f);
            if (!SiteConfig.IsAllowedFile(n) || n.Equals(SiteConfig.WebConfig, StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(f, Path.Combine(versionDir, n), overwrite: true);
            copied.Add(n);
        }
        if (copied.Count > 0) logLine("Eigene Konfiguration übernommen: " + string.Join(", ", copied), "info");
    }

    public List<string> GetPersistent(string name)
    {
        var path = Path.Combine(services.OverrideDir(name), PersistFile);
        if (!File.Exists(path)) return new();
        try
        {
            var settings = JsonSerializer.Deserialize<PersistentSettings>(File.ReadAllText(path), Json);
            return PersistentPaths.Parse(string.Join('\n', settings?.Paths ?? new()), out _);
        }
        catch (JsonException)
        {
            return new();
        }
    }

    public List<string> SetPersistent(string name, IEnumerable<string> paths)
    {
        var list = PersistentPaths.Validate(paths);
        var dir = services.OverrideDir(name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, PersistFile), JsonSerializer.Serialize(new PersistentSettings { Paths = list }, Json), Utf8);
        log.LogInformation("Persistente Pfade Dienst {Service}: {Paths}", name, string.Join(", ", list));
        return list;
    }
}
