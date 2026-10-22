using System.Text.Json;

namespace BlazorStudio.App.Services;

public sealed class AppSettings
{
    public string Theme { get; set; } = "dark";

    /// <summary>"dark", "light" oder "system".</summary>
    public string ThemeMode { get; set; } = "light";

    public string Accent { get; set; } = "#e2001a";

    /// <summary>"de" oder "en"; leer = Systemsprache.</summary>
    public string Language { get; set; } = "";

    public List<string> RecentProjects { get; set; } = new();

    /// <summary>Beim Start auf GitHub nach neuen Versionen suchen.</summary>
    public bool CheckForUpdates { get; set; } = true;

    public DateTime LastUpdateCheck { get; set; }

    /// <summary>Zuletzt gefundene Version (für die Anzeige ohne erneute Abfrage).</summary>
    public string? KnownUpdate { get; set; }

    /// <summary>Vom Nutzer übersprungene Version.</summary>
    public string? SkippedUpdate { get; set; }

    public string? DebuggerPath { get; set; }

    public bool UseHotReload { get; set; } = true;

    /// <summary>Design-Tab zeigt die echte App (Schatten-Kopie mit dotnet watch) statt der Nachzeichnung.</summary>
    public bool LiveDesign { get; set; } = true;

    /// <summary>Live-Designer: Seiten mit ihrem Layout (MainLayout …) zeigen oder nur die Seite selbst.</summary>
    public bool LiveShowLayout { get; set; } = true;

    public double WindowWidth { get; set; }

    public double WindowHeight { get; set; }

    public bool WindowMaximized { get; set; }

    public int SideWidth { get; set; } = 344;

    public int ToolboxWidth { get; set; } = 240;

    public bool ToolboxCollapsed { get; set; }

    /// <summary>Inhalt der linken Spalte: components (Komponenten), explorer (Projektmappe), claude.</summary>
    public string LeftPanel { get; set; } = "components";

    /// <summary>Build-Konfiguration für Build/Start (Debug oder Release).</summary>
    public string BuildConfiguration { get; set; } = "Debug";

    /// <summary>Firmenlogo in der Kopfzeile (Pfad zu PNG/SVG); leer = Blazor-Studio-Logo.</summary>
    public string? BrandLogo { get; set; }

    /// <summary>Produktname rechts in der Menüzeile (z. B. „Fronius Studio“); leer = „Blazor Studio“.</summary>
    public string? BrandName { get; set; }

    /// <summary>Stand des Oberflächen-Designs – beim Wechsel auf ein neues Design einmalig Standardwerte setzen.</summary>
    public int DesignVersion { get; set; }

    public int BottomHeight { get; set; } = 190;

    public bool BottomCollapsed { get; set; }

    public int EditorFontSize { get; set; } = 13;

    /// <summary>"rider" oder "studio".</summary>
    public string EditorScheme { get; set; } = "rider";

    /// <summary>Version der Editor-Farben; ältere Einstellungen werden einmalig auf das Rider-Schema gesetzt.</summary>
    public int EditorSchemeVersion { get; set; }

    public bool ShowMinimap { get; set; }

    public Dictionary<string, string> LastOpenFiles { get; set; } = new();

    /// <summary>Projektmappe -> Pfad des Startprojekts.</summary>
    public Dictionary<string, string> StartupProjects { get; set; } = new();

    /// <summary>Entladene Projekte je Projektmappe (wie „Unload Project“ in VS/Rider).</summary>
    public Dictionary<string, List<string>> UnloadedProjects { get; set; } = new();

    /// <summary>IIS-Server mit Blazor Studio Agent (API-Schlüssel per DPAPI verschlüsselt).</summary>
    public List<IisServerEntry> IisServers { get; set; } = new();

    /// <summary>Letzte IIS-Deployment-Einstellungen je Projektdatei.</summary>
    public Dictionary<string, IisDeployProfile> IisDeployProfiles { get; set; } = new();

    /// <summary>Claude-Panel: Berechtigungsmodus (plan, acceptEdits, bypassPermissions).</summary>
    public string ClaudeMode { get; set; } = "acceptEdits";

    /// <summary>Claude-Modell (leer = Standard von Claude Code), z. B. sonnet oder opus.</summary>
    public string? ClaudeModel { get; set; }

    /// <summary>Dauerhaft erlaubte Werkzeuge/Befehle, z. B. „Bash(dotnet build *)“.</summary>
    public List<string> ClaudeAllowedTools { get; set; } = new() { "Bash(dotnet build *)", "Bash(dotnet test *)", "Bash(git status *)", "Bash(git diff *)" };

    public List<DockerHostEntry> DockerHosts { get; set; } = new();

    /// <summary>Docker-Deploy-Einstellungen je Projektdatei.</summary>
    public Dictionary<string, DockerDeployProfile> DockerDeployProfiles { get; set; } = new();

    /// <summary>Zertifikatsrichtlinie der Firma (Laufzeit, Vorlauf) – Bewertung in Studio und Vorgabe für win-acme.</summary>
    public BlazorStudio.Deploy.CertificatePolicy CertificatePolicy { get; set; } = new();
}

public sealed class IisServerEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "";

    /// <summary>Rechnername oder IP-Adresse.</summary>
    public string Host { get; set; } = "";

    public int Port { get; set; } = BlazorStudio.Deploy.AgentDefaults.Port;

    public bool Https { get; set; }

    /// <summary>Umgebung zum Gruppieren, z. B. „Test“ oder „Produktion“.</summary>
    public string? Environment { get; set; }

    /// <summary>SHA1-Fingerabdruck eines selbstsignierten Agent-Zertifikats, dem vertraut wird.</summary>
    public string? CertificateThumbprint { get; set; }

    /// <summary>API-Schlüssel, unter Windows mit DPAPI (aktueller Benutzer) verschlüsselt.</summary>
    public string? ProtectedApiKey { get; set; }

    public Uri Address => BlazorStudio.Deploy.AgentClient.BuildAddress(Host, Port, Https);

    public string Display => string.IsNullOrWhiteSpace(Name) ? Host : Name;
}

public sealed class IisDeployProfile
{
    public List<string> ServerIds { get; set; } = new();

    /// <summary>iis (Site) oder service (Windows-Dienst).</summary>
    public string Target { get; set; } = BlazorStudio.Deploy.DeployTargets.Iis;

    public string ServiceName { get; set; } = "";

    /// <summary>Programm relativ zum Publish-Ordner; leer = automatisch.</summary>
    public string? ServiceExe { get; set; }

    public string? ServiceArguments { get; set; }

    public string? ServiceAccount { get; set; }

    public string? ServiceStartType { get; set; }

    public string? ServiceDisplayName { get; set; }

    public bool IsService => string.Equals(Target, BlazorStudio.Deploy.DeployTargets.Service, StringComparison.OrdinalIgnoreCase);

    public string Site { get; set; } = "";

    public string AppPath { get; set; } = "/";

    public string Configuration { get; set; } = "Release";

    public string? RuntimeIdentifier { get; set; }

    public bool SelfContained { get; set; }

    /// <summary>Vorlage, siehe <see cref="BlazorStudio.Deploy.Pipelines.PipelineGenerator.VersionExpression"/>; {version} = Projektversion.</summary>
    public string VersionTemplate { get; set; } = "{version}-{date}.{time}";

    public string? HealthUrl { get; set; }

    public bool Rollback { get; set; } = true;

    public bool CreateSite { get; set; }

    public string? Binding { get; set; }

    public string? Pool { get; set; }

    public string? Notes { get; set; }
}

/// <summary>Ein Docker-Host: lokal (leere Adresse) oder entfernt per ssh://benutzer@host bzw. tcp://host:2376.</summary>
public sealed class DockerHostEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "";

    /// <summary>Leer = lokaler Docker (Docker Desktop), sonst DOCKER_HOST-Adresse.</summary>
    public string Endpoint { get; set; } = "";

    public string? Environment { get; set; }

    public bool IsLocal => string.IsNullOrWhiteSpace(Endpoint);

    public string Display => !string.IsNullOrWhiteSpace(Name) ? Name : IsLocal ? "Docker (lokal)" : BlazorStudio.Deploy.Docker.DockerCli.HostNameOf(Endpoint);
}

public sealed class DockerDeployProfile
{
    public List<string> HostIds { get; set; } = new();

    public string ContainerName { get; set; } = "";

    /// <summary>Image-Name ohne Tag, z. B. „shop“ oder „team/shop“.</summary>
    public string Repository { get; set; } = "";

    /// <summary>Optional, z. B. „registry.firma.de:5000“ – dann wird einmal gebaut, gepusht und auf den Hosts gezogen.</summary>
    public string? Registry { get; set; }

    public BlazorStudio.Deploy.Docker.DockerBuildMode BuildMode { get; set; } = BlazorStudio.Deploy.Docker.DockerBuildMode.Auto;

    public string Configuration { get; set; } = "Release";

    public string VersionTemplate { get; set; } = "{version}-{date}.{time}";

    /// <summary>Eine Zuordnung pro Zeile („8080:8080“).</summary>
    public string Ports { get; set; } = "8080:8080";

    public string Environment { get; set; } = "ASPNETCORE_ENVIRONMENT=Production";

    public string Volumes { get; set; } = "";

    public string RestartPolicy { get; set; } = "unless-stopped";

    public string? Network { get; set; }

    public string ExtraArgs { get; set; } = "";

    /// <summary>Pfad (oder ganze URL) für die Prüfung nach dem Start; leer = nur „läuft der Container?“.</summary>
    public string? HealthPath { get; set; } = "/";

    public bool Rollback { get; set; } = true;

    /// <summary>So viele Images des Repositorys bleiben je Host liegen (für schnelles Zurückschalten).</summary>
    public int KeepImages { get; set; } = 5;
}

/// <summary>Speichert Einstellungen in %AppData%\BlazorStudio\settings.json.</summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _path;

    public SettingsService()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BlazorStudio");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "settings.json");
        Current = Load();
        // Ab 1.1: Code-Farben wie in Rider als Standard (frühere Versionen konnten "studio" gespeichert haben)
        if (Current.EditorSchemeVersion < 2)
        {
            Current.EditorScheme = "rider";
            Current.EditorSchemeVersion = 2;
            Save();
        }
    }

    public AppSettings Current { get; }

    public event Action? Changed;

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new AppSettings();
        }
        catch (JsonException) { }
        catch (IOException) { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(Current, Json));
        }
        catch (IOException) { }
        Changed?.Invoke();
    }

    public void AddRecent(string folder)
    {
        Current.RecentProjects.RemoveAll(p => string.Equals(p, folder, StringComparison.OrdinalIgnoreCase));
        Current.RecentProjects.Insert(0, folder);
        if (Current.RecentProjects.Count > 12) Current.RecentProjects.RemoveRange(12, Current.RecentProjects.Count - 12);
        Save();
    }
}
