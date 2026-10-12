using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace BlazorStudio.Core.NuGet;

/// <summary>Eine Paketquelle (Feed) aus einer NuGet.Config – nuget.org, interner Server oder lokaler Ordner.</summary>
public sealed record PackageSource(string Name, string Url)
{
    public bool Enabled { get; init; } = true;

    public string? UserName { get; init; }

    /// <summary>Entschlüsseltes Passwort bzw. Token (nur im Speicher).</summary>
    public string? Password { get; init; }

    /// <summary>NuGet.Config, in der die Quelle definiert ist.</summary>
    public string? ConfigFile { get; init; }

    public bool IsLocal => !Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    public bool IsNuGetOrg => Uri.TryCreate(Url, UriKind.Absolute, out var u) && !IsLocal
                              && (u.Host.Equals("nuget.org", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".nuget.org", StringComparison.OrdinalIgnoreCase));

    public bool HasCredentials => !string.IsNullOrEmpty(UserName) || !string.IsNullOrEmpty(Password);
}

/// <summary>
/// Liest und schreibt NuGet.Config-Dateien wie dotnet/Visual Studio: Quellen aus der Benutzer-Config und allen
/// nuget.config vom Projektordner aufwärts, inkl. deaktivierter Quellen und Zugangsdaten.
/// </summary>
public static class NuGetConfig
{
    public const string NuGetOrgName = "nuget.org";
    public const string NuGetOrgUrl = "https://api.nuget.org/v3/index.json";

    // gleiche Entropie wie NuGet (EncryptionUtility), damit dotnet/VS die Passwörter lesen können
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NuGet");

    public static PackageSource NuGetOrg { get; } = new(NuGetOrgName, NuGetOrgUrl);

    /// <summary>%APPDATA%\NuGet\NuGet.Config bzw. ~/.nuget/NuGet/NuGet.Config.</summary>
    public static string UserConfigPath { get; } = OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NuGet", "NuGet.Config")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "NuGet", "NuGet.Config");

    /// <summary>Wirksame Quellen für ein Projektverzeichnis (nähere Config gewinnt).</summary>
    public static IReadOnlyList<PackageSource> Load(string? startDir, string? userConfig = null)
    {
        userConfig ??= UserConfigPath;
        var files = new List<string>();
        if (File.Exists(userConfig)) files.Add(userConfig);
        files.AddRange(FindProjectConfigs(startDir).Where(f => !SamePath(f, userConfig)).Reverse());

        var sources = new List<PackageSource>();
        var disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var creds = new Dictionary<string, (string? User, string? Password)>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            XElement root;
            try { root = XDocument.Load(file).Root!; }
            catch (Exception e) when (e is IOException or XmlException or UnauthorizedAccessException) { continue; }
            var dir = Path.GetDirectoryName(file)!;

            foreach (var e in Section(root, "packageSources")?.Elements() ?? Enumerable.Empty<XElement>())
            {
                if (e.Name.LocalName == "clear") { sources.Clear(); continue; }
                if (e.Name.LocalName != "add" || e.Attribute("key")?.Value is not { Length: > 0 } key || e.Attribute("value")?.Value is not { Length: > 0 } value) continue;
                var source = new PackageSource(key, ResolveUrl(value, dir)) { ConfigFile = file };
                var i = sources.FindIndex(s => s.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
                if (i >= 0) sources[i] = source; else sources.Add(source);
            }
            foreach (var e in Section(root, "disabledPackageSources")?.Elements() ?? Enumerable.Empty<XElement>())
            {
                if (e.Name.LocalName == "clear") { disabled.Clear(); continue; }
                if (e.Name.LocalName != "add" || e.Attribute("key")?.Value is not { } key) continue;
                if (string.Equals(e.Attribute("value")?.Value, "true", StringComparison.OrdinalIgnoreCase)) disabled.Add(key); else disabled.Remove(key);
            }
            foreach (var e in Section(root, "packageSourceCredentials")?.Elements() ?? Enumerable.Empty<XElement>())
            {
                if (e.Name.LocalName == "clear") { creds.Clear(); continue; }
                string? Get(string k) => e.Elements().FirstOrDefault(a => string.Equals(a.Attribute("key")?.Value, k, StringComparison.OrdinalIgnoreCase))?.Attribute("value")?.Value;
                var password = Get("ClearTextPassword") ?? Decrypt(Get("Password"));
                creds[XmlConvert.DecodeName(e.Name.LocalName)] = (Get("Username"), password);
            }
        }

        // ohne jede Config verhält sich dotnet wie mit nuget.org
        if (files.Count == 0) sources.Add(NuGetOrg);

        return sources.Select(s => s with
        {
            Enabled = !disabled.Contains(s.Name),
            UserName = creds.TryGetValue(s.Name, out var c) ? c.User : null,
            Password = c.Password,
        }).ToList();
    }

    /// <summary>
    /// Quelle in der Benutzer-Config anlegen oder ändern. <paramref name="credentialsOnly"/>: Quelle stammt aus einer
    /// Projekt-Config, nur Zugangsdaten werden gespeichert. <paramref name="password"/>: null behält das alte Passwort,
    /// "" entfernt es. Unter Windows wird es wie von dotnet verschlüsselt (DPAPI), sonst im Klartext gespeichert.
    /// </summary>
    public static void Save(PackageSource source, string? password, string? previousName = null, bool credentialsOnly = false, string? userConfig = null)
    {
        userConfig ??= UserConfigPath;
        var doc = LoadForEdit(userConfig);
        var root = doc.Root!;
        var oldName = previousName ?? source.Name;

        // altes (ggf. verschlüsseltes) Passwort übernehmen, wenn kein neues angegeben ist
        var oldCred = FindCredentials(root, oldName);
        var keptPassword = password is null
            ? oldCred?.Elements().FirstOrDefault(a => a.Attribute("key")?.Value is { } k
                && (k.Equals("Password", StringComparison.OrdinalIgnoreCase) || k.Equals("ClearTextPassword", StringComparison.OrdinalIgnoreCase)))
            : null;
        oldCred?.Remove();

        if (!credentialsOnly)
        {
            if (!oldName.Equals(source.Name, StringComparison.OrdinalIgnoreCase)) Remove(root, oldName);
            var sources = EnsureSection(root, "packageSources");
            var add = sources.Elements("add").FirstOrDefault(e => string.Equals(e.Attribute("key")?.Value, source.Name, StringComparison.OrdinalIgnoreCase));
            if (add is null) sources.Add(add = new XElement("add"));
            add.SetAttributeValue("key", source.Name);
            add.SetAttributeValue("value", source.Url);
            SetDisabled(root, source.Name, !source.Enabled);
        }

        var cred = new XElement(XmlConvert.EncodeLocalName(source.Name));
        if (!string.IsNullOrEmpty(source.UserName)) cred.Add(Entry("Username", source.UserName));
        if (!string.IsNullOrEmpty(password))
            cred.Add(OperatingSystem.IsWindows() ? Entry("Password", Encrypt(password)) : Entry("ClearTextPassword", password));
        else if (keptPassword is not null)
            cred.Add(new XElement(keptPassword));
        if (cred.HasElements) EnsureSection(root, "packageSourceCredentials").Add(cred);
        Write(doc, userConfig);
    }

    /// <summary>Quelle samt Zugangsdaten aus der Benutzer-Config entfernen.</summary>
    public static void Delete(string name, string? userConfig = null)
    {
        userConfig ??= UserConfigPath;
        var doc = LoadForEdit(userConfig);
        Remove(doc.Root!, name);
        Write(doc, userConfig);
    }

    /// <summary>Quelle (de)aktivieren – wirkt über disabledPackageSources auch für Quellen aus Projekt-Configs.</summary>
    public static void SetEnabled(string name, bool enabled, string? userConfig = null)
    {
        userConfig ??= UserConfigPath;
        var doc = LoadForEdit(userConfig);
        SetDisabled(doc.Root!, name, !enabled);
        Write(doc, userConfig);
    }

    // ------------------------------------------------------------------

    private static IEnumerable<string> FindProjectConfigs(string? startDir)
    {
        for (var d = startDir; !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d))
        {
            // gleiche Schreibweisen wie NuGet (unter Linux zählt die Groß-/Kleinschreibung)
            var file = new[] { "nuget.config", "NuGet.config", "NuGet.Config" }.Select(n => Path.Combine(d, n)).FirstOrDefault(File.Exists);
            if (file is not null) yield return file;
        }
    }

    private static string ResolveUrl(string value, string configDir)
    {
        value = Environment.ExpandEnvironmentVariables(value);
        if (value.Contains("://", StringComparison.Ordinal) || Path.IsPathRooted(value)) return value;
        return Path.GetFullPath(Path.Combine(configDir, value));
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static XElement? Section(XElement root, string name) => root.Elements().FirstOrDefault(e => e.Name.LocalName == name);

    private static XElement EnsureSection(XElement root, string name)
    {
        var s = Section(root, name);
        if (s is null) root.Add(s = new XElement(name));
        return s;
    }

    private static XElement? FindCredentials(XElement root, string name) =>
        Section(root, "packageSourceCredentials")?.Elements().FirstOrDefault(e => XmlConvert.DecodeName(e.Name.LocalName).Equals(name, StringComparison.OrdinalIgnoreCase));

    private static XElement Entry(string key, string value) => new("add", new XAttribute("key", key), new XAttribute("value", value));

    private static void SetDisabled(XElement root, string name, bool disabled)
    {
        var section = Section(root, "disabledPackageSources");
        section?.Elements("add").Where(e => string.Equals(e.Attribute("key")?.Value, name, StringComparison.OrdinalIgnoreCase)).Remove();
        if (disabled) EnsureSection(root, "disabledPackageSources").Add(Entry(name, "true"));
    }

    private static void Remove(XElement root, string name)
    {
        Section(root, "packageSources")?.Elements("add").Where(e => string.Equals(e.Attribute("key")?.Value, name, StringComparison.OrdinalIgnoreCase)).Remove();
        FindCredentials(root, name)?.Remove();
        SetDisabled(root, name, false);
    }

    private static XDocument LoadForEdit(string path)
    {
        if (File.Exists(path))
        {
            var doc = XDocument.Load(path);
            if (doc.Root?.Name.LocalName == "configuration") return doc;
        }
        // neue Benutzer-Config wie von dotnet angelegt: nuget.org bleibt verfügbar
        return new XDocument(
            new XElement("configuration",
                new XElement("packageSources",
                    new XElement("add", new XAttribute("key", NuGetOrgName), new XAttribute("value", NuGetOrgUrl), new XAttribute("protocolVersion", "3")))));
    }

    private static void Write(XDocument doc, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var w = XmlWriter.Create(path, new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) });
        doc.Save(w);
    }

    private static string Encrypt(string value)
    {
        if (!OperatingSystem.IsWindows()) return value;
        return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser));
    }

    private static string? Decrypt(string? value)
    {
        if (string.IsNullOrEmpty(value) || !OperatingSystem.IsWindows()) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), Entropy, DataProtectionScope.CurrentUser)); }
        catch (Exception e) when (e is CryptographicException or FormatException) { return null; }
    }
}
