using System.Xml.Linq;

namespace BlazorStudio.Core.Build;

public enum PublishTarget { Folder, Zip, Container, WebDeploy, Other }

/// <summary>
/// Ein Publish-Profil (Properties/PublishProfiles/*.pubxml) – kompatibel mit Visual Studio und Rider.
/// Unbekannte Eigenschaften bleiben beim Speichern erhalten.
/// </summary>
public sealed class PublishProfile
{
    private XDocument? _source;

    public string Name { get; set; } = "FolderProfile";

    public string? FilePath { get; private set; }

    public PublishTarget Target { get; set; } = PublishTarget.Folder;

    public string Configuration { get; set; } = "Release";

    public string? TargetFramework { get; set; }

    /// <summary>null = portabel (alle Plattformen).</summary>
    public string? RuntimeIdentifier { get; set; }

    public bool SelfContained { get; set; }

    public bool SingleFile { get; set; }

    public bool Trimmed { get; set; }

    public bool ReadyToRun { get; set; }

    public bool DeleteExistingFiles { get; set; } = true;

    /// <summary>Zielordner (relativ zum Projekt oder absolut).</summary>
    public string OutputPath { get; set; } = @"bin\Release\publish\";

    public string? ContainerRepository { get; set; }

    public string? ContainerTag { get; set; } = "latest";

    public string? ContainerRegistry { get; set; }

    /// <summary>Für Web Deploy (IIS): Server-URL und Site.</summary>
    public string? MsDeployServiceUrl { get; set; }

    public string? DeployIisAppPath { get; set; }

    public string? UserName { get; set; }

    public static IReadOnlyList<string> RuntimeIdentifiers { get; } = new[]
    {
        "win-x64", "win-x86", "win-arm64", "linux-x64", "linux-arm64", "linux-musl-x64", "osx-x64", "osx-arm64",
    };

    public static string ProfilesDirectory(string csproj) => Path.Combine(Path.GetDirectoryName(csproj)!, "Properties", "PublishProfiles");

    public static IReadOnlyList<PublishProfile> LoadAll(string csproj)
    {
        var dir = ProfilesDirectory(csproj);
        if (!Directory.Exists(dir)) return Array.Empty<PublishProfile>();
        return Directory.GetFiles(dir, "*.pubxml").OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Select(Load).Where(p => p is not null).Select(p => p!).ToList();
    }

    public static PublishProfile? Load(string path)
    {
        try
        {
            var doc = XDocument.Load(path);
            string? P(string name) => doc.Descendants().FirstOrDefault(e => e.Name.LocalName == name && e.Parent?.Name.LocalName == "PropertyGroup")?.Value.Trim();
            bool B(string name) => string.Equals(P(name), "true", StringComparison.OrdinalIgnoreCase);
            var method = P("WebPublishMethod") ?? "";
            var provider = P("PublishProvider") ?? "";
            var target = B("BlazorStudioZip") ? PublishTarget.Zip
                : provider.Contains("Container", StringComparison.OrdinalIgnoreCase) || P("ContainerRepository") is not null ? PublishTarget.Container
                : method.Equals("MSDeploy", StringComparison.OrdinalIgnoreCase) ? PublishTarget.WebDeploy
                : method.Equals("FileSystem", StringComparison.OrdinalIgnoreCase) || method.Length == 0 ? PublishTarget.Folder
                : PublishTarget.Other;
            return new PublishProfile
            {
                _source = doc,
                FilePath = path,
                Name = Path.GetFileNameWithoutExtension(path),
                Target = target,
                Configuration = P("LastUsedBuildConfiguration") ?? P("Configuration") ?? "Release",
                TargetFramework = P("TargetFramework"),
                RuntimeIdentifier = P("RuntimeIdentifier"),
                SelfContained = B("SelfContained"),
                SingleFile = B("PublishSingleFile"),
                Trimmed = B("PublishTrimmed"),
                ReadyToRun = B("PublishReadyToRun"),
                DeleteExistingFiles = P("DeleteExistingFiles") is not { } d || d.Equals("true", StringComparison.OrdinalIgnoreCase),
                OutputPath = P("PublishUrl") ?? P("PublishDir") ?? @"bin\Release\publish\",
                ContainerRepository = P("ContainerRepository"),
                ContainerTag = P("ContainerImageTags") ?? P("ContainerImageTag") ?? "latest",
                ContainerRegistry = P("ContainerRegistry"),
                MsDeployServiceUrl = P("MSDeployServiceURL"),
                DeployIisAppPath = P("DeployIisAppPath"),
                UserName = P("UserName"),
            };
        }
        catch (Exception e) when (e is IOException or System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>Speichert als .pubxml. Vorhandene, hier nicht bekannte Einträge bleiben erhalten.</summary>
    public string Save(string csproj)
    {
        var dir = ProfilesDirectory(csproj);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Name + ".pubxml");
        if (FilePath is not null && !string.Equals(FilePath, path, StringComparison.OrdinalIgnoreCase) && File.Exists(FilePath))
            File.Move(FilePath, path, overwrite: true);

        var doc = _source ?? new XDocument(new XElement("Project", new XElement("PropertyGroup")));
        var ns = doc.Root!.Name.Namespace;
        var group = doc.Root.Elements().FirstOrDefault(e => e.Name.LocalName == "PropertyGroup") ?? new XElement(ns + "PropertyGroup");
        if (group.Parent is null) doc.Root.Add(group);

        void Set(string name, string? value)
        {
            var el = group.Elements().FirstOrDefault(e => e.Name.LocalName == name);
            if (value is null) { el?.Remove(); return; }
            if (el is null) group.Add(new XElement(ns + name, value));
            else el.Value = value;
        }

        static string Bool(bool b) => b ? "true" : "false";

        switch (Target)
        {
            case PublishTarget.Folder or PublishTarget.Zip:
                Set("WebPublishMethod", "FileSystem");
                Set("PublishProvider", "FileSystem");
                Set("PublishUrl", OutputPath);
                Set("DeleteExistingFiles", Bool(DeleteExistingFiles));
                Set("BlazorStudioZip", Target == PublishTarget.Zip ? "true" : null);
                break;
            case PublishTarget.Container:
                Set("WebPublishMethod", "Custom");
                Set("PublishProvider", "ContainerRegistry");
                Set("ContainerRepository", ContainerRepository);
                Set("ContainerImageTags", ContainerTag);
                Set("ContainerRegistry", string.IsNullOrWhiteSpace(ContainerRegistry) ? null : ContainerRegistry);
                Set("BlazorStudioZip", null);
                break;
            case PublishTarget.WebDeploy:
                Set("WebPublishMethod", "MSDeploy");
                Set("MSDeployServiceURL", MsDeployServiceUrl);
                Set("DeployIisAppPath", DeployIisAppPath);
                Set("UserName", UserName);
                Set("MSDeployPublishMethod", "WMSVC");
                Set("SkipExtraFilesOnServer", Bool(!DeleteExistingFiles));
                break;
        }
        Set("LastUsedBuildConfiguration", Configuration);
        Set("LastUsedPlatform", "Any CPU");
        Set("ExcludeApp_Data", "false");
        Set("TargetFramework", TargetFramework);
        Set("RuntimeIdentifier", string.IsNullOrWhiteSpace(RuntimeIdentifier) ? null : RuntimeIdentifier);
        Set("SelfContained", RuntimeIdentifier is null ? null : Bool(SelfContained));
        Set("PublishSingleFile", RuntimeIdentifier is null || !SingleFile ? null : "true");
        Set("PublishTrimmed", RuntimeIdentifier is null || !Trimmed ? null : "true");
        Set("PublishReadyToRun", RuntimeIdentifier is null || !ReadyToRun ? null : "true");

        doc.Save(path);
        _source = doc;
        FilePath = path;
        return path;
    }

    public void Delete()
    {
        if (FilePath is not null && File.Exists(FilePath)) File.Delete(FilePath);
        var user = FilePath + ".user";
        if (File.Exists(user)) File.Delete(user);
    }

    /// <summary>Absoluter Zielordner.</summary>
    public string ResolveOutput(string csproj) =>
        Path.GetFullPath(Path.IsPathRooted(OutputPath) ? OutputPath : Path.Combine(Path.GetDirectoryName(csproj)!, OutputPath));

    /// <summary>Argumente für dotnet publish.</summary>
    public List<string> BuildArguments(string csproj, string? password = null)
    {
        var args = new List<string> { "publish", csproj, "-c", Configuration, "-nologo" };
        if (Target is PublishTarget.Container)
        {
            args.Add("-t:PublishContainer");
            if (!string.IsNullOrWhiteSpace(ContainerRepository)) args.Add($"-p:ContainerRepository={ContainerRepository}");
            if (!string.IsNullOrWhiteSpace(ContainerTag)) args.Add($"-p:ContainerImageTags={ContainerTag}");
            if (!string.IsNullOrWhiteSpace(ContainerRegistry)) args.Add($"-p:ContainerRegistry={ContainerRegistry}");
            args.Add("--os");
            args.Add("linux");
            args.Add("--arch");
            args.Add(RuntimeIdentifier?.EndsWith("arm64", StringComparison.Ordinal) == true ? "arm64" : "x64");
        }
        else
        {
            if (FilePath is not null) args.Add($"-p:PublishProfile={Name}");
            if (Target is PublishTarget.Folder or PublishTarget.Zip) { args.Add("-o"); args.Add(ResolveOutput(csproj)); }
            if (!string.IsNullOrWhiteSpace(RuntimeIdentifier))
            {
                args.Add("-r");
                args.Add(RuntimeIdentifier);
                args.Add(SelfContained ? "--self-contained" : "--no-self-contained");
            }
            if (Target == PublishTarget.WebDeploy && password is not null) args.Add($"-p:Password={password}");
        }
        if (!string.IsNullOrWhiteSpace(TargetFramework)) { args.Add("-f"); args.Add(TargetFramework); }
        return args;
    }
}
