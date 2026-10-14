using System.Text.Json;

namespace BlazorStudio.App.Services;

public sealed class AppSettings
{
    public string Theme { get; set; } = "dark";

    /// <summary>"dark", "light" oder "system".</summary>
    public string ThemeMode { get; set; } = "dark";

    public string Accent { get; set; } = "#4c8dff";

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
