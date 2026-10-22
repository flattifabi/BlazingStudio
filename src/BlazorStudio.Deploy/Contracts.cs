namespace BlazorStudio.Deploy;

/// <summary>Feste Werte der Agent-API.</summary>
public static class AgentDefaults
{
    public const int Port = 8790;

    public const string ApiKeyHeader = "X-Api-Key";

    public const string AgentName = "BlazorStudio.Agent";

    /// <summary>Formularfeld der hochgeladenen ZIP-Datei bei POST /api/deployments.</summary>
    public const string PackageField = "package";

    /// <summary>
    /// Ältester Agent, auf den Studio deployen darf. Ältere Agents legten neue Sites auf „_initial“ an
    /// und schalteten den physischen Pfad unter Umständen nicht um.
    /// </summary>
    public const string MinimumDeployVersion = "1.6.2";

    /// <summary>Ältester Agent, der Windows-Dienste ausrollen kann.</summary>
    public const string MinimumServiceVersion = "1.11.0";

    /// <summary>Ältester Agent mit Speicheranalyse (ClrMD).</summary>
    public const string MinimumMemoryVersion = "1.13.0";

    /// <summary>Ältester Agent mit Zertifikatsverwaltung (win-acme).</summary>
    public const string MinimumCertificateVersion = "1.14.0";

    /// <summary>True, wenn die Agent-Version (z. B. „1.6.1“) älter als <paramref name="minimum"/> ist. Unlesbare Versionen gelten als veraltet.</summary>
    public static bool IsOlderThan(string? agentVersion, string minimum)
    {
        if (!System.Version.TryParse(minimum, out var min)) return false;
        var text = (agentVersion ?? "").Split('+', '-')[0].Trim();
        return !System.Version.TryParse(text, out var v) || Normalize(v) < Normalize(min);
    }

    private static System.Version Normalize(System.Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
}

/// <summary>Antwort von GET /api/ping (ohne Anmeldung) – unterscheidet „offline“ von „falscher Schlüssel“.</summary>
public sealed class PingResponse
{
    public string Agent { get; set; } = "";

    public string Version { get; set; } = "";

    public string MachineName { get; set; } = "";
}

/// <summary>Zustand eines Servers (GET /api/info).</summary>
public sealed class AgentInfo
{
    public string MachineName { get; set; } = "";

    public string OsVersion { get; set; } = "";

    public string AgentVersion { get; set; } = "";

    public bool IisInstalled { get; set; }

    public string? IisVersion { get; set; }

    /// <summary>Zustand des Dienstes W3SVC (Running, Stopped …).</summary>
    public string IisServiceState { get; set; } = "";

    public double UptimeSeconds { get; set; }

    public int ProcessorCount { get; set; }

    public double CpuPercent { get; set; }

    public long MemoryUsedBytes { get; set; }

    public long MemoryTotalBytes { get; set; }

    /// <summary>CPU in Prozent, alt → neu (ca. alle 2 s ein Wert).</summary>
    public List<double> CpuHistory { get; set; } = new();

    /// <summary>Arbeitsspeicher in Prozent, alt → neu.</summary>
    public List<double> MemoryHistory { get; set; } = new();

    public string DeployRoot { get; set; } = "";

    public long DiskFreeBytes { get; set; }

    public long DiskTotalBytes { get; set; }

    public int KeepVersions { get; set; }

    public int Pools { get; set; }

    public int PoolsStarted { get; set; }

    public int Sites { get; set; }

    public int SitesStarted { get; set; }

    public int RunningDeployments { get; set; }
}

public sealed class WorkerProcessInfo
{
    public int ProcessId { get; set; }

    public string State { get; set; } = "";

    public double CpuPercent { get; set; }

    public long WorkingSetBytes { get; set; }

    public long PrivateBytes { get; set; }

    public int ThreadCount { get; set; }

    public DateTime? StartTime { get; set; }

    /// <summary>Laufende Anfragen (nur mit installiertem IIS-Feature „Request Monitor“).</summary>
    public int? ActiveRequests { get; set; }
}

public sealed class AppPoolInfo
{
    public string Name { get; set; } = "";

    /// <summary>Started, Stopped, Starting, Stopping, Unknown.</summary>
    public string State { get; set; } = "";

    /// <summary>"" = kein verwalteter Code (ASP.NET Core), sonst z. B. "v4.0".</summary>
    public string ManagedRuntimeVersion { get; set; } = "";

    /// <summary>Integrated oder Classic.</summary>
    public string PipelineMode { get; set; } = "Integrated";

    public bool Enable32Bit { get; set; }

    /// <summary>OnDemand oder AlwaysRunning.</summary>
    public string StartMode { get; set; } = "OnDemand";

    public bool AutoStart { get; set; }

    /// <summary>ApplicationPoolIdentity, NetworkService, LocalService, LocalSystem, SpecificUser.</summary>
    public string IdentityType { get; set; } = "ApplicationPoolIdentity";

    public string? UserName { get; set; }

    public int IdleTimeoutMinutes { get; set; }

    /// <summary>Regelmäßiges Recycling in Minuten (0 = aus).</summary>
    public int RecycleMinutes { get; set; }

    public int QueueLength { get; set; }

    /// <summary>CPU-Limit in Prozent (0 = aus).</summary>
    public double CpuLimitPercent { get; set; }

    /// <summary>Anwendungen in diesem Pool als „Site/Pfad“.</summary>
    public List<string> Applications { get; set; } = new();

    public List<WorkerProcessInfo> WorkerProcesses { get; set; } = new();

    public double CpuPercent => WorkerProcesses.Sum(w => w.CpuPercent);

    public long WorkingSetBytes => WorkerProcesses.Sum(w => w.WorkingSetBytes);

    public int? ActiveRequests => WorkerProcesses.Any(w => w.ActiveRequests is not null) ? WorkerProcesses.Sum(w => w.ActiveRequests ?? 0) : null;
}

/// <summary>Anlegen/Ändern eines App-Pools. null = unverändert (beim Anlegen: IIS-Standard bzw. ASP.NET-Core-tauglich).</summary>
public sealed class PoolSettings
{
    public string Name { get; set; } = "";

    public string? ManagedRuntimeVersion { get; set; }

    public string? PipelineMode { get; set; }

    public bool? Enable32Bit { get; set; }

    public string? StartMode { get; set; }

    public bool? AutoStart { get; set; }

    public string? IdentityType { get; set; }

    public string? UserName { get; set; }

    /// <summary>Nur bei SpecificUser; wird nie zurückgegeben.</summary>
    public string? Password { get; set; }

    public int? IdleTimeoutMinutes { get; set; }

    public int? RecycleMinutes { get; set; }

    public int? QueueLength { get; set; }

    public double? CpuLimitPercent { get; set; }

    public static PoolSettings From(AppPoolInfo p) => new()
    {
        Name = p.Name,
        ManagedRuntimeVersion = p.ManagedRuntimeVersion,
        PipelineMode = p.PipelineMode,
        Enable32Bit = p.Enable32Bit,
        StartMode = p.StartMode,
        AutoStart = p.AutoStart,
        IdentityType = p.IdentityType,
        UserName = p.UserName,
        IdleTimeoutMinutes = p.IdleTimeoutMinutes,
        RecycleMinutes = p.RecycleMinutes,
        QueueLength = p.QueueLength,
        CpuLimitPercent = p.CpuLimitPercent,
    };
}

public sealed class BindingInfo
{
    public string Protocol { get; set; } = "http";

    /// <summary>IIS-Format "IP:Port:Host", z. B. "*:80:shop.example.com".</summary>
    public string BindingInformation { get; set; } = "";

    public string Host { get; set; } = "";

    public int Port { get; set; }

    /// <summary>Aufrufbare Adresse („*“ wird zu „localhost“).</summary>
    public string Url { get; set; } = "";
}

public sealed class SiteApplicationInfo
{
    public string Path { get; set; } = "/";

    public string Pool { get; set; } = "";

    public string PhysicalPath { get; set; } = "";

    /// <summary>Aktive Version, wenn der Ordner aus der Versionsablage des Agents stammt.</summary>
    public string? ActiveVersion { get; set; }
}

public sealed class SiteInfo
{
    public long Id { get; set; }

    public string Name { get; set; } = "";

    /// <summary>Started, Stopped, Starting, Stopping, Unknown.</summary>
    public string State { get; set; } = "";

    public bool AutoStart { get; set; }

    public List<BindingInfo> Bindings { get; set; } = new();

    public List<SiteApplicationInfo> Applications { get; set; } = new();

    public SiteApplicationInfo? Root => Applications.FirstOrDefault(a => a.Path == "/");
}

public sealed class CreateSiteRequest
{
    public string Name { get; set; } = "";

    /// <summary>z. B. "http://*:8080" oder "*:80:shop.local" – siehe <see cref="BindingParser"/>.</summary>
    public string Binding { get; set; } = "";

    /// <summary>Leer = Ordner in der Versionsablage des Agents.</summary>
    public string? PhysicalPath { get; set; }

    /// <summary>Leer = Pool mit dem Namen der Site (wird angelegt).</summary>
    public string? Pool { get; set; }

    /// <summary>Für einen neu angelegten Pool: "" = kein verwalteter Code.</summary>
    public string ManagedRuntimeVersion { get; set; } = "";
}

public sealed class SiteVersion
{
    public string Version { get; set; } = "";

    public string Path { get; set; } = "";

    public DateTime DeployedAt { get; set; }

    public DateTime? ActivatedAt { get; set; }

    public string? Source { get; set; }

    public string? Commit { get; set; }

    public string? Notes { get; set; }

    public string? DeploymentId { get; set; }

    public long SizeBytes { get; set; }

    public int Files { get; set; }

    public bool IsActive { get; set; }

    /// <summary>Zuletzt vor der aktiven Version aktiv gewesen (Ziel eines Rollbacks).</summary>
    public bool IsPrevious { get; set; }
}

public sealed class ActivateRequest
{
    public string Version { get; set; } = "";

    public string AppPath { get; set; } = "/";

    public bool Recycle { get; set; } = true;
}

public enum DeploymentState { Queued, Running, Succeeded, Failed, RolledBack }

public sealed class DeploymentLogLine
{
    public DateTime At { get; set; }

    /// <summary>info, warn, error, ok.</summary>
    public string Level { get; set; } = "info";

    public string Text { get; set; } = "";
}

public sealed class DeploymentRecord
{
    public string Id { get; set; } = "";

    public string Site { get; set; } = "";

    public string AppPath { get; set; } = "/";

    public string Version { get; set; } = "";

    public string? PreviousVersion { get; set; }

    public DeploymentState State { get; set; }

    public string? Source { get; set; }

    public string? Commit { get; set; }

    public string? Notes { get; set; }

    public string? HealthUrl { get; set; }

    public string? Error { get; set; }

    public DateTime StartedAt { get; set; }

    public DateTime? FinishedAt { get; set; }

    public long PackageBytes { get; set; }

    /// <summary>iis (Standard) oder service (Windows-Dienst; dann ist <see cref="Site"/> der Dienstname).</summary>
    public string Target { get; set; } = DeployTargets.Iis;

    public List<DeploymentLogLine> Log { get; set; } = new();

    public bool IsFinished => State is DeploymentState.Succeeded or DeploymentState.Failed or DeploymentState.RolledBack;
}

/// <summary>Parameter eines Deployments (Formularfelder neben der ZIP-Datei).</summary>
public sealed class DeployRequest
{
    public string Site { get; set; } = "";

    public string AppPath { get; set; } = "/";

    public string Version { get; set; } = "";

    /// <summary>Site (und Pool) anlegen, wenn es sie noch nicht gibt.</summary>
    public bool CreateSite { get; set; }

    /// <summary>Bindung für eine neu angelegte Site, z. B. "http://*:8080".</summary>
    public string? Binding { get; set; }

    public string? Pool { get; set; }

    public string ManagedRuntimeVersion { get; set; } = "";

    /// <summary>Nach dem Umschalten aufrufen; Fehler ⇒ Rollback (wenn <see cref="Rollback"/>).</summary>
    public string? HealthUrl { get; set; }

    public bool Rollback { get; set; } = true;

    /// <summary>Gleichnamige (nicht aktive) Version ersetzen.</summary>
    public bool Overwrite { get; set; }

    /// <summary>Nur hochladen und entpacken, nicht aktivieren.</summary>
    public bool Activate { get; set; } = true;

    public string? Source { get; set; }

    public string? Commit { get; set; }

    public string? Notes { get; set; }

    /// <summary>
    /// Persistente Dateien/Ordner (Zeilen, Komma oder Semikolon getrennt), die von der laufenden in die neue Version
    /// wandern. Null = gespeicherte Liste des Servers unverändert lassen, sonst ersetzen.
    /// </summary>
    public string? Persist { get; set; }

    /// <summary>iis (Standard) oder service – bei service ist <see cref="Site"/> der Dienstname.</summary>
    public string Target { get; set; } = DeployTargets.Iis;

    /// <summary>Dienst: Programm relativ zum Paket (leer = automatisch: die eine .exe bzw. dotnet + .dll).</summary>
    public string? ServiceExe { get; set; }

    public string? ServiceArguments { get; set; }

    public string? ServiceDisplayName { get; set; }

    public string? ServiceDescription { get; set; }

    /// <summary>LocalSystem, LocalService, NetworkService oder DOMÄNE\Benutzer (nur beim Anlegen bzw. wenn gesetzt).</summary>
    public string? ServiceAccount { get; set; }

    public string? ServicePassword { get; set; }

    /// <summary>auto, delayed oder manual.</summary>
    public string? ServiceStartType { get; set; }

    public bool IsService => string.Equals(Target, DeployTargets.Service, StringComparison.OrdinalIgnoreCase);

    public IEnumerable<KeyValuePair<string, string>> ToForm()
    {
        yield return new("site", Site);
        yield return new("appPath", AppPath);
        yield return new("version", Version);
        yield return new("createSite", CreateSite ? "true" : "false");
        if (!string.IsNullOrWhiteSpace(Binding)) yield return new("binding", Binding);
        if (!string.IsNullOrWhiteSpace(Pool)) yield return new("pool", Pool);
        yield return new("runtime", ManagedRuntimeVersion);
        if (!string.IsNullOrWhiteSpace(HealthUrl)) yield return new("healthUrl", HealthUrl);
        yield return new("rollback", Rollback ? "true" : "false");
        yield return new("overwrite", Overwrite ? "true" : "false");
        yield return new("activate", Activate ? "true" : "false");
        if (!string.IsNullOrWhiteSpace(Source)) yield return new("source", Source);
        if (!string.IsNullOrWhiteSpace(Commit)) yield return new("commit", Commit);
        if (!string.IsNullOrWhiteSpace(Notes)) yield return new("notes", Notes);
        if (Persist is not null) yield return new("persist", Persist);
        if (IsService)
        {
            yield return new("target", DeployTargets.Service);
            if (!string.IsNullOrWhiteSpace(ServiceExe)) yield return new("serviceExe", ServiceExe);
            if (!string.IsNullOrWhiteSpace(ServiceArguments)) yield return new("serviceArgs", ServiceArguments);
            if (!string.IsNullOrWhiteSpace(ServiceDisplayName)) yield return new("serviceDisplayName", ServiceDisplayName);
            if (!string.IsNullOrWhiteSpace(ServiceDescription)) yield return new("serviceDescription", ServiceDescription);
            if (!string.IsNullOrWhiteSpace(ServiceAccount)) yield return new("serviceAccount", ServiceAccount);
            if (!string.IsNullOrWhiteSpace(ServicePassword)) yield return new("servicePassword", ServicePassword);
            if (!string.IsNullOrWhiteSpace(ServiceStartType)) yield return new("serviceStartType", ServiceStartType);
        }
    }

    public static DeployRequest FromForm(Func<string, string?> get)
    {
        static bool Bool(string? v, bool fallback) => v is null ? fallback : v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1" || v.Equals("on", StringComparison.OrdinalIgnoreCase);
        return new DeployRequest
        {
            Site = get("site")?.Trim() ?? "",
            AppPath = NormalizeAppPath(get("appPath")),
            Version = get("version")?.Trim() ?? "",
            CreateSite = Bool(get("createSite"), false),
            Binding = get("binding"),
            Pool = get("pool"),
            ManagedRuntimeVersion = get("runtime") ?? "",
            HealthUrl = get("healthUrl"),
            Rollback = Bool(get("rollback"), true),
            Overwrite = Bool(get("overwrite"), false),
            Activate = Bool(get("activate"), true),
            Source = get("source"),
            Commit = get("commit"),
            Notes = get("notes"),
            Persist = get("persist"),
            Target = string.Equals(get("target"), DeployTargets.Service, StringComparison.OrdinalIgnoreCase) ? DeployTargets.Service : DeployTargets.Iis,
            ServiceExe = get("serviceExe"),
            ServiceArguments = get("serviceArgs"),
            ServiceDisplayName = get("serviceDisplayName"),
            ServiceDescription = get("serviceDescription"),
            ServiceAccount = get("serviceAccount"),
            ServicePassword = get("servicePassword"),
            ServiceStartType = get("serviceStartType"),
        };
    }

    /// <summary>"shop", "/shop/", "" → "/shop" bzw. "/".</summary>
    public static string NormalizeAppPath(string? path)
    {
        var p = (path ?? "").Trim().Replace('\\', '/').Trim('/');
        return "/" + p;
    }
}

/// <summary>Fehlermeldung der API: { "error": "…" }.</summary>
public sealed class ApiError
{
    public string Error { get; set; } = "";
}
