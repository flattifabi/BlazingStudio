using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using BlazorStudio.Deploy;

namespace BlazorStudio.Agent;

/// <summary>
/// Remote bearbeitete web.config/appsettings*.json („Überschreibungen“) und die stdout-Logs einer Site.
/// Überschreibungen liegen unter &lt;DeployRoot&gt;\&lt;Site[_App]&gt;\_config\ und werden bei jedem Deployment
/// über die Dateien aus dem Paket gelegt; in jeder web.config wird stdout eingeschaltet.
/// </summary>
public sealed class SiteConfigService(IisManager iis, VersionStore store, ILogger<SiteConfigService> log)
{
    private static readonly UTF8Encoding Utf8 = new(false);

    private string OverrideDir(string site, string appPath) => Path.Combine(store.TargetDirectory(site, appPath), SiteConfig.OverrideFolder);

    public string LogDir(string site, string appPath) => Path.Combine(store.TargetDirectory(site, appPath), SiteConfig.LogFolder);

    private static string Require(string file) =>
        SiteConfig.IsAllowedFile(file) ? file : throw new ArgumentException($"„{file}“ kann nicht bearbeitet werden (nur web.config und appsettings*.json).");

    /// <summary>Ordner, aus dem die Anwendung gerade läuft (physischer Pfad der Anwendung).</summary>
    private string? LiveDir(string site, string appPath)
    {
        var app = iis.FindApplication(site, appPath) ?? throw new NotFoundException($"Anwendung {site}{appPath} gibt es nicht.");
        var dir = Environment.ExpandEnvironmentVariables(app.PhysicalPath);
        return Directory.Exists(dir) ? dir : null;
    }

    public List<SiteConfigFile> List(string site, string appPath)
    {
        var live = LiveDir(site, appPath);
        var overrides = OverrideDir(site, appPath);
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase) { SiteConfig.WebConfig, SiteConfig.AppSettings };
        foreach (var dir in new[] { live, overrides })
        {
            if (dir is null || !Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir))
                if (SiteConfig.IsAllowedFile(Path.GetFileName(f))) names.Add(Path.GetFileName(f));
        }
        return names.Select(n =>
        {
            var o = new FileInfo(Path.Combine(overrides, n));
            var l = live is null ? null : new FileInfo(Path.Combine(live, n));
            var shown = o.Exists ? o : l is { Exists: true } ? l : null;
            return new SiteConfigFile
            {
                Name = n,
                InActive = l is { Exists: true },
                HasOverride = o.Exists,
                Size = shown?.Length ?? 0,
                Modified = shown?.LastWriteTimeUtc,
            };
        }).OrderBy(f => f.Name.Equals(SiteConfig.WebConfig, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(f => f.Name.Length).ThenBy(f => f.Name).ToList();
    }

    public SiteConfigContent Read(string site, string appPath, string file)
    {
        Require(file);
        var o = Path.Combine(OverrideDir(site, appPath), file);
        if (File.Exists(o)) return new SiteConfigContent { Name = file, Content = File.ReadAllText(o), HasOverride = true, Source = "override" };
        var live = LiveDir(site, appPath);
        if (live is not null && File.Exists(Path.Combine(live, file)))
            return new SiteConfigContent { Name = file, Content = File.ReadAllText(Path.Combine(live, file)), Source = "active" };
        return new SiteConfigContent { Name = file, Content = file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? "{\n}\n" : "", Source = "new" };
    }

    public async Task<SiteConfigWriteResult> WriteAsync(string site, string appPath, string file, SiteConfigWrite write)
    {
        Require(file);
        var content = write.Content ?? "";
        if (SiteConfig.Validate(file, content) is { } error) throw new ArgumentException(error);
        var result = new SiteConfigWriteResult();
        var live = LiveDir(site, appPath);
        if (IsWebConfig(file))
        {
            var (patched, r) = SiteConfig.EnsureStdout(content, StdoutFile(site, appPath));
            if (r == StdoutPatch.Updated)
            {
                content = patched;
                result.Notes.Add("stdoutLogEnabled ist eingeschaltet (Log: " + StdoutFile(site, appPath) + "*.log).");
            }
            if (r != StdoutPatch.NoAspNetCore) PrepareLogDir(site, appPath);
        }
        var dir = OverrideDir(site, appPath);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, file), content, Utf8);
        result.Content = content;
        log.LogInformation("Konfiguration {Site}{App}/{File} gespeichert", site, appPath, file);

        if (write.Apply && live is not null)
        {
            await File.WriteAllTextAsync(Path.Combine(live, file), content, Utf8);
            result.Applied = true;
            // web.config: IIS startet die App selbst neu. appsettings: sicherheitshalber recyceln.
            if (!IsWebConfig(file) && iis.FindApplication(site, appPath)?.Pool is { Length: > 0 } pool)
            {
                await iis.RecycleAsync(pool);
                result.Recycled = true;
            }
        }
        return result;
    }

    public void DeleteOverride(string site, string appPath, string file)
    {
        Require(file);
        var path = Path.Combine(OverrideDir(site, appPath), file);
        if (!File.Exists(path)) throw new NotFoundException($"Für {file} gibt es keine Überschreibung.");
        File.Delete(path);
    }

    /// <summary>Beim Deployment: Überschreibungen in den Versionsordner kopieren, dann stdout einschalten.</summary>
    public void PrepareVersion(string site, string appPath, string versionDir, Action<string, string> logLine)
    {
        var dir = OverrideDir(site, appPath);
        if (Directory.Exists(dir))
        {
            var copied = new List<string>();
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                var name = Path.GetFileName(f);
                if (!SiteConfig.IsAllowedFile(name)) continue;
                File.Copy(f, Path.Combine(versionDir, name), overwrite: true);
                copied.Add(name);
            }
            if (copied.Count > 0) logLine("Eigene Konfiguration übernommen: " + string.Join(", ", copied), "info");
        }

        var webConfig = Path.Combine(versionDir, SiteConfig.WebConfig);
        if (!File.Exists(webConfig)) return;
        try
        {
            var (xml, r) = SiteConfig.EnsureStdout(File.ReadAllText(webConfig), StdoutFile(site, appPath));
            if (r == StdoutPatch.NoAspNetCore) return;
            PrepareLogDir(site, appPath);
            if (r == StdoutPatch.Updated) File.WriteAllText(webConfig, xml, Utf8);
            logLine("stdout-Log eingeschaltet: " + StdoutFile(site, appPath) + "*.log", "info");
        }
        catch (System.Xml.XmlException e)
        {
            logLine("web.config ist kein gültiges XML – stdout nicht eingeschaltet: " + e.Message, "warn");
        }
    }

    // ---------------- Logs ----------------

    public List<SiteLogFile> Logs(string site, string appPath)
    {
        var dir = LogDir(site, appPath);
        if (!Directory.Exists(dir)) return new();
        return new DirectoryInfo(dir).EnumerateFiles("*.log")
            .Select(f => new SiteLogFile { Name = f.Name, Size = f.Length, Modified = f.LastWriteTimeUtc })
            .OrderByDescending(f => f.Modified).Take(100).ToList();
    }

    public SiteConfigContent ReadLog(string site, string appPath, string file)
    {
        if (!SiteConfig.IsLogFile(file)) throw new ArgumentException($"Ungültiger Log-Name: {file}");
        var path = Path.Combine(LogDir(site, appPath), file);
        if (!File.Exists(path)) throw new NotFoundException($"Log {file} gibt es nicht.");
        // Die App schreibt noch hinein → mit ReadWrite-Freigabe öffnen
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var start = Math.Max(0, fs.Length - SiteConfig.MaxLogBytes);
        fs.Seek(start, SeekOrigin.Begin);
        using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();
        if (start > 0) text = $"… (nur die letzten {SiteConfig.MaxLogBytes / 1024} KB)\n" + text[(text.IndexOf('\n') + 1)..];
        return new SiteConfigContent { Name = file, Content = text, Source = "log" };
    }

    private string StdoutFile(string site, string appPath) => Path.Combine(LogDir(site, appPath), "stdout");

    /// <summary>Log-Ordner anlegen und dem App-Pool (Mitglied von IIS_IUSRS) Schreibrechte geben.</summary>
    private void PrepareLogDir(string site, string appPath)
    {
        var dir = LogDir(site, appPath);
        Directory.CreateDirectory(dir);
        try
        {
            var info = new DirectoryInfo(dir);
            var acl = info.GetAccessControl();
            var iisUsers = new SecurityIdentifier("S-1-5-32-568"); // IIS_IUSRS
            var has = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).OfType<FileSystemAccessRule>()
                .Any(r => r.IdentityReference == iisUsers && r.AccessControlType == AccessControlType.Allow && r.FileSystemRights.HasFlag(FileSystemRights.Modify));
            if (has) return;
            acl.AddAccessRule(new FileSystemAccessRule(iisUsers, FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            info.SetAccessControl(acl);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or IdentityNotMappedException or PlatformNotSupportedException)
        {
            log.LogWarning("Schreibrechte für IIS_IUSRS auf {Dir} konnten nicht gesetzt werden: {Error}", dir, e.Message);
        }
    }

    private static bool IsWebConfig(string file) => file.Equals(SiteConfig.WebConfig, StringComparison.OrdinalIgnoreCase);
}
