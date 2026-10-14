using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BlazorStudio.Deploy;

/// <summary>Metadaten einer abgelegten Version (liegt als &lt;Version&gt;.json neben dem Versionsordner).</summary>
public sealed class VersionMetadata
{
    public string Version { get; set; } = "";

    public DateTime DeployedAt { get; set; }

    public DateTime? ActivatedAt { get; set; }

    public string? Source { get; set; }

    public string? Commit { get; set; }

    public string? Notes { get; set; }

    public string? DeploymentId { get; set; }

    public long SizeBytes { get; set; }

    public int Files { get; set; }
}

/// <summary>
/// Ablage der ausgerollten Versionen auf dem Server:
/// <code>
/// &lt;Root&gt;\&lt;Site[_App]&gt;\&lt;Version&gt;\      Inhalt (physischer Pfad der IIS-Anwendung)
/// &lt;Root&gt;\&lt;Site[_App]&gt;\&lt;Version&gt;.json  Metadaten (außerhalb des Web-Ordners, wird also nie ausgeliefert)
/// </code>
/// Umschalten auf eine Version = physischen Pfad der Anwendung auf ihren Ordner setzen.
/// </summary>
public sealed partial class VersionStore(string root)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string Root { get; } = Path.GetFullPath(root);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._+\-]{0,63}$")]
    private static partial Regex VersionPattern();

    /// <summary>Erlaubt sind Buchstaben, Ziffern und . _ + - (max. 64 Zeichen), kein ".." – damit nie ein Pfad entsteht.</summary>
    public static bool IsValidVersion(string? version) =>
        version is not null && VersionPattern().IsMatch(version) && !version.Contains("..", StringComparison.Ordinal);

    /// <summary>Ordnername für Site + Anwendungspfad: "Default Web Site" + "/shop" → "Default Web Site_shop".</summary>
    public static string TargetFolderName(string site, string appPath)
    {
        var app = DeployRequest.NormalizeAppPath(appPath).Trim('/');
        var name = app.Length == 0 ? site : site + "_" + app.Replace('/', '_');
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }).ToHashSet();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim(' ', '.');
        return clean.Length == 0 ? "_" : clean;
    }

    public string TargetDirectory(string site, string appPath) => Path.Combine(Root, TargetFolderName(site, appPath));

    public string VersionDirectory(string site, string appPath, string version)
    {
        if (!IsValidVersion(version)) throw new ArgumentException($"Ungültige Version: {version}", nameof(version));
        return Path.Combine(TargetDirectory(site, appPath), version);
    }

    private static string MetadataPath(string versionDirectory) => versionDirectory.TrimEnd(Path.DirectorySeparatorChar) + ".json";

    public bool Exists(string site, string appPath, string version) => Directory.Exists(VersionDirectory(site, appPath, version));

    /// <summary>Welche Version liegt unter diesem physischen Pfad? null = Ordner stammt nicht aus der Ablage.</summary>
    public string? VersionFromPath(string site, string appPath, string? physicalPath)
    {
        if (string.IsNullOrWhiteSpace(physicalPath)) return null;
        string full;
        try { full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(physicalPath)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        var target = TargetDirectory(site, appPath);
        var parent = Path.GetDirectoryName(full);
        if (parent is null || !string.Equals(parent, target, StringComparison.OrdinalIgnoreCase)) return null;
        var version = Path.GetFileName(full);
        return IsValidVersion(version) ? version : null;
    }

    public VersionMetadata? ReadMetadata(string site, string appPath, string version)
    {
        var file = MetadataPath(VersionDirectory(site, appPath, version));
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<VersionMetadata>(File.ReadAllText(file), Json) : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void WriteMetadata(string site, string appPath, VersionMetadata meta) =>
        File.WriteAllText(MetadataPath(VersionDirectory(site, appPath, meta.Version)), JsonSerializer.Serialize(meta, Json));

    /// <summary>Aktivierungszeitpunkt festhalten (daraus ergibt sich die „vorherige“ Version für Rollbacks).</summary>
    public void MarkActivated(string site, string appPath, string version, DateTime at)
    {
        var meta = ReadMetadata(site, appPath, version) ?? new VersionMetadata { Version = version, DeployedAt = Directory.GetCreationTimeUtc(VersionDirectory(site, appPath, version)) };
        meta.ActivatedAt = at;
        WriteMetadata(site, appPath, meta);
    }

    /// <summary>Alle Versionen, neueste zuerst. activeVersion markiert die aktive, die zuletzt davor aktive gilt als „vorherige“.</summary>
    public List<SiteVersion> List(string site, string appPath, string? activeVersion)
    {
        var dir = TargetDirectory(site, appPath);
        if (!Directory.Exists(dir)) return new();
        var list = new List<SiteVersion>();
        foreach (var d in Directory.GetDirectories(dir))
        {
            var version = Path.GetFileName(d);
            if (!IsValidVersion(version)) continue;
            var meta = ReadMetadata(site, appPath, version);
            list.Add(new SiteVersion
            {
                Version = version,
                Path = d,
                DeployedAt = meta?.DeployedAt ?? Directory.GetCreationTimeUtc(d),
                ActivatedAt = meta?.ActivatedAt,
                Source = meta?.Source,
                Commit = meta?.Commit,
                Notes = meta?.Notes,
                DeploymentId = meta?.DeploymentId,
                SizeBytes = meta?.SizeBytes ?? 0,
                Files = meta?.Files ?? 0,
                IsActive = string.Equals(version, activeVersion, StringComparison.OrdinalIgnoreCase),
            });
        }
        var previous = list.Where(v => !v.IsActive && v.ActivatedAt is not null).MaxBy(v => v.ActivatedAt);
        if (previous is not null) previous.IsPrevious = true;
        return list.OrderByDescending(v => v.DeployedAt).ToList();
    }

    /// <summary>
    /// Entpackt ein Paket als neue Version. Liegt alles in einem einzigen Oberordner (z. B. „publish/“),
    /// wird dieser abgestreift. Bestehende Versionen werden nur mit overwrite ersetzt.
    /// </summary>
    public SiteVersion Install(string site, string appPath, string zipPath, VersionMetadata meta, bool overwrite)
    {
        var target = VersionDirectory(site, appPath, meta.Version);
        if (Directory.Exists(target))
        {
            if (!overwrite) throw new InvalidOperationException($"Version {meta.Version} existiert bereits.");
            Directory.Delete(target, true);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var staging = target + ".incoming-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            ExtractSafe(zipPath, staging);
            var content = SingleTopFolder(staging) ?? staging;
            Directory.Move(content, target);
            if (content != staging) Directory.Delete(staging, true);
        }
        catch
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch (IOException) { }
            throw;
        }

        var files = Directory.GetFiles(target, "*", SearchOption.AllDirectories);
        meta.Files = files.Length;
        meta.SizeBytes = files.Sum(f => new FileInfo(f).Length);
        WriteMetadata(site, appPath, meta);
        return List(site, appPath, null).First(v => v.Version.Equals(meta.Version, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Entpacken mit Schutz gegen Pfade außerhalb des Zielordners („Zip Slip“).</summary>
    public static void ExtractSafe(string zipPath, string destination)
    {
        var dest = Path.GetFullPath(destination);
        var destWithSep = dest.EndsWith(Path.DirectorySeparatorChar) ? dest : dest + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(dest);
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            var full = Path.GetFullPath(Path.Combine(dest, name));
            if (!full.StartsWith(destWithSep, StringComparison.OrdinalIgnoreCase) && !string.Equals(full, dest, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Unzulässiger Pfad im Paket: {entry.FullName}");
            if (name.EndsWith('/'))
            {
                Directory.CreateDirectory(full);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            entry.ExtractToFile(full, overwrite: true);
        }
    }

    private static string? SingleTopFolder(string dir)
    {
        if (Directory.GetFiles(dir).Length > 0) return null;
        var dirs = Directory.GetDirectories(dir);
        return dirs.Length == 1 ? dirs[0] : null;
    }

    /// <summary>Löscht eine Version (nie die aktive – das prüft der Aufrufer).</summary>
    public void Delete(string site, string appPath, string version)
    {
        var dir = VersionDirectory(site, appPath, version);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        var meta = MetadataPath(dir);
        if (File.Exists(meta)) File.Delete(meta);
    }

    /// <summary>
    /// Behält die neuesten <paramref name="keep"/> Versionen plus aktive und vorherige; löscht den Rest.
    /// Gesperrte Ordner werden übersprungen. Gibt die gelöschten Versionen zurück.
    /// </summary>
    public List<string> Prune(string site, string appPath, int keep, string? activeVersion)
    {
        var removed = new List<string>();
        if (keep <= 0) return removed;
        var all = List(site, appPath, activeVersion);
        var keepSet = all.Take(keep).Select(v => v.Version).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var v in all)
        {
            if (keepSet.Contains(v.Version) || v.IsActive || v.IsPrevious) continue;
            try
            {
                Delete(site, appPath, v.Version);
                removed.Add(v.Version);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Dateien noch in Benutzung – beim nächsten Mal
            }
        }
        return removed;
    }
}
