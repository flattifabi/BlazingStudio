using System.ComponentModel;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using BlazorStudio.Deploy;
using Microsoft.Web.Administration;
using Microsoft.Win32;

namespace BlazorStudio.Agent;

/// <summary>Lesen und Ändern der IIS-Konfiguration über Microsoft.Web.Administration.</summary>
public sealed class IisManager(SystemSampler sampler, VersionStore store, ILogger<IisManager> log)
{
    // ServerManager ist nicht threadsicher; Schreibzugriffe nacheinander
    private readonly SemaphoreSlim _write = new(1, 1);

    public static readonly string[] Identities = { "ApplicationPoolIdentity", "NetworkService", "LocalService", "LocalSystem", "SpecificUser" };

    private static ServerManager Open()
    {
        try
        {
            return new ServerManager();
        }
        catch (Exception e) when (e is COMException or FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException)
        {
            throw new IisUnavailableException("IIS ist auf diesem Server nicht installiert oder nicht erreichbar: " + e.Message, e);
        }
    }

    public static bool IisInstalled => File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "inetsrv", "config", "applicationHost.config"));

    public static string? IisVersion
    {
        get
        {
            if (!OperatingSystem.IsWindows()) return null;
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\InetStp");
            return key?.GetValue("MajorVersion") is int major ? $"{major}.{key.GetValue("MinorVersion") ?? 0}" : null;
        }
    }

    public static string W3SvcState
    {
        get
        {
            try
            {
                using var sc = new ServiceController("W3SVC");
                return sc.Status.ToString();
            }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception)
            {
                return "NotInstalled";
            }
        }
    }

    private static string StateOf(Func<ObjectState> get)
    {
        try { return get().ToString(); }
        catch (COMException) { return "Unknown"; }
    }

    // ------------------------------------------------------------------
    // App-Pools
    // ------------------------------------------------------------------

    public List<AppPoolInfo> GetPools()
    {
        using var sm = Open();
        var apps = sm.Sites.SelectMany(s => s.Applications.Select(a => (Site: s.Name, a.Path, a.ApplicationPoolName))).ToList();
        return sm.ApplicationPools.Select(p => ToInfo(p, apps)).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private AppPoolInfo ToInfo(ApplicationPool p, List<(string Site, string Path, string Pool)> apps)
    {
        var info = new AppPoolInfo
        {
            Name = p.Name,
            State = StateOf(() => p.State),
            ManagedRuntimeVersion = p.ManagedRuntimeVersion ?? "",
            PipelineMode = p.ManagedPipelineMode.ToString(),
            Enable32Bit = p.Enable32BitAppOnWin64,
            StartMode = p.StartMode.ToString(),
            AutoStart = p.AutoStart,
            IdentityType = p.ProcessModel.IdentityType.ToString(),
            UserName = p.ProcessModel.IdentityType == ProcessModelIdentityType.SpecificUser ? p.ProcessModel.UserName : null,
            IdleTimeoutMinutes = (int)p.ProcessModel.IdleTimeout.TotalMinutes,
            RecycleMinutes = (int)p.Recycling.PeriodicRestart.Time.TotalMinutes,
            QueueLength = (int)p.QueueLength,
            CpuLimitPercent = p.Cpu.Limit / 1000.0,
            Applications = apps.Where(a => string.Equals(a.Pool, p.Name, StringComparison.OrdinalIgnoreCase)).Select(a => a.Site + (a.Path == "/" ? "" : a.Path)).ToList(),
        };
        try
        {
            foreach (var wp in p.WorkerProcesses)
            {
                var sample = sampler.Process(wp.ProcessId);
                int? requests = null;
                try { requests = wp.GetRequests(0).Count; }
                catch (Exception e) when (e is COMException or NotImplementedException or UnauthorizedAccessException) { }
                info.WorkerProcesses.Add(new WorkerProcessInfo
                {
                    ProcessId = wp.ProcessId,
                    State = wp.State.ToString(),
                    CpuPercent = sample?.CpuPercent ?? 0,
                    WorkingSetBytes = sample?.WorkingSet ?? 0,
                    PrivateBytes = sample?.PrivateBytes ?? 0,
                    ThreadCount = sample?.Threads ?? 0,
                    StartTime = sample?.StartTime,
                    ActiveRequests = requests,
                });
            }
        }
        catch (COMException)
        {
            // WAS gestoppt
        }
        return info;
    }

    public async Task PoolActionAsync(string name, string action)
    {
        await _write.WaitAsync();
        try
        {
            using var sm = Open();
            var p = sm.ApplicationPools[name] ?? throw new NotFoundException($"App-Pool „{name}“ gibt es nicht.");
            switch (action.ToLowerInvariant())
            {
                case "start":
                    if (p.State != ObjectState.Started) p.Start();
                    break;
                case "stop":
                    if (p.State != ObjectState.Stopped) p.Stop();
                    break;
                case "recycle":
                    if (p.State == ObjectState.Started) p.Recycle();
                    else p.Start();
                    break;
                default:
                    throw new ArgumentException($"Unbekannte Aktion: {action}");
            }
            log.LogInformation("App-Pool {Pool}: {Action}", name, action);
        }
        finally
        {
            _write.Release();
        }
    }

    public async Task SavePoolAsync(PoolSettings s, bool create)
    {
        if (string.IsNullOrWhiteSpace(s.Name)) throw new ArgumentException("Name fehlt.");
        await _write.WaitAsync();
        try
        {
            using var sm = Open();
            var p = sm.ApplicationPools[s.Name];
            if (create)
            {
                if (p is not null) throw new InvalidOperationException($"App-Pool „{s.Name}“ gibt es schon.");
                p = sm.ApplicationPools.Add(s.Name.Trim());
                p.ManagedRuntimeVersion = s.ManagedRuntimeVersion ?? "";
                p.ManagedPipelineMode = ManagedPipelineMode.Integrated;
                p.AutoStart = true;
            }
            else if (p is null)
            {
                throw new NotFoundException($"App-Pool „{s.Name}“ gibt es nicht.");
            }
            Apply(p, s);
            sm.CommitChanges();
            log.LogInformation("App-Pool {Pool} {Mode}", s.Name, create ? "angelegt" : "geändert");
        }
        finally
        {
            _write.Release();
        }
    }

    private static void Apply(ApplicationPool p, PoolSettings s)
    {
        if (s.ManagedRuntimeVersion is not null) p.ManagedRuntimeVersion = s.ManagedRuntimeVersion;
        if (s.PipelineMode is { } pm) p.ManagedPipelineMode = Enum.Parse<ManagedPipelineMode>(pm, true);
        if (s.Enable32Bit is { } e32) p.Enable32BitAppOnWin64 = e32;
        if (s.StartMode is { } sm) p.StartMode = Enum.Parse<StartMode>(sm, true);
        if (s.AutoStart is { } a) p.AutoStart = a;
        if (s.IdentityType is { } it)
        {
            var type = Enum.Parse<ProcessModelIdentityType>(it, true);
            p.ProcessModel.IdentityType = type;
            if (type == ProcessModelIdentityType.SpecificUser)
            {
                if (string.IsNullOrWhiteSpace(s.UserName)) throw new ArgumentException("Für „SpecificUser“ wird ein Benutzername benötigt.");
                p.ProcessModel.UserName = s.UserName;
                if (!string.IsNullOrEmpty(s.Password)) p.ProcessModel.Password = s.Password;
            }
        }
        if (s.IdleTimeoutMinutes is { } idle) p.ProcessModel.IdleTimeout = TimeSpan.FromMinutes(Math.Clamp(idle, 0, 43200));
        if (s.RecycleMinutes is { } rec) p.Recycling.PeriodicRestart.Time = TimeSpan.FromMinutes(Math.Clamp(rec, 0, 432000));
        if (s.QueueLength is { } q) p.QueueLength = Math.Clamp(q, 10, 65535);
        if (s.CpuLimitPercent is { } cpu) p.Cpu.Limit = (long)Math.Round(Math.Clamp(cpu, 0, 100) * 1000);
    }

    public async Task DeletePoolAsync(string name)
    {
        await _write.WaitAsync();
        try
        {
            using var sm = Open();
            var p = sm.ApplicationPools[name] ?? throw new NotFoundException($"App-Pool „{name}“ gibt es nicht.");
            var used = sm.Sites.SelectMany(s => s.Applications.Where(a => string.Equals(a.ApplicationPoolName, name, StringComparison.OrdinalIgnoreCase)).Select(a => s.Name + a.Path)).ToList();
            if (used.Count > 0) throw new InvalidOperationException($"App-Pool „{name}“ wird noch verwendet: {string.Join(", ", used)}");
            sm.ApplicationPools.Remove(p);
            sm.CommitChanges();
        }
        finally
        {
            _write.Release();
        }
    }

    // ------------------------------------------------------------------
    // Sites
    // ------------------------------------------------------------------

    public List<SiteInfo> GetSites()
    {
        using var sm = Open();
        return sm.Sites.Select(s => new SiteInfo
        {
            Id = s.Id,
            Name = s.Name,
            State = StateOf(() => s.State),
            AutoStart = s.ServerAutoStart,
            Bindings = s.Bindings.Select(b => new BindingInfo
            {
                Protocol = b.Protocol,
                BindingInformation = b.BindingInformation,
                Host = b.Host ?? "",
                Port = BindingParser.Split(b.BindingInformation).Port,
                Url = b.Protocol is "http" or "https" ? BindingParser.ToUrl(b.Protocol, b.BindingInformation) : "",
            }).ToList(),
            Applications = s.Applications.Select(a =>
            {
                var path = a.VirtualDirectories["/"]?.PhysicalPath ?? "";
                return new SiteApplicationInfo
                {
                    Path = a.Path,
                    Pool = a.ApplicationPoolName,
                    PhysicalPath = path,
                    ActiveVersion = store.VersionFromPath(s.Name, a.Path, path),
                };
            }).OrderBy(a => a.Path, StringComparer.OrdinalIgnoreCase).ToList(),
        }).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public SiteApplicationInfo? FindApplication(string site, string appPath) =>
        GetSites().FirstOrDefault(s => s.Name.Equals(site, StringComparison.OrdinalIgnoreCase))
            ?.Applications.FirstOrDefault(a => a.Path.Equals(appPath, StringComparison.OrdinalIgnoreCase));

    public bool SiteExists(string site)
    {
        using var sm = Open();
        return sm.Sites[site] is not null;
    }

    public async Task SiteActionAsync(string name, string action)
    {
        await _write.WaitAsync();
        try
        {
            using var sm = Open();
            var s = sm.Sites[name] ?? throw new NotFoundException($"Site „{name}“ gibt es nicht.");
            switch (action.ToLowerInvariant())
            {
                case "start":
                    s.Start();
                    break;
                case "stop":
                    s.Stop();
                    break;
                default:
                    throw new ArgumentException($"Unbekannte Aktion: {action}");
            }
            log.LogInformation("Site {Site}: {Action}", name, action);
        }
        finally
        {
            _write.Release();
        }
    }

    /// <summary>Legt Site (und bei Bedarf Pool) an. Ohne Pfad: leerer Ordner in der Versionsablage.</summary>
    public async Task CreateSiteAsync(CreateSiteRequest r)
    {
        var name = r.Name.Trim();
        if (name.Length == 0) throw new ArgumentException("Name fehlt.");
        if (!BindingParser.TryParse(r.Binding, out var protocol, out var info))
            throw new ArgumentException($"Bindung „{r.Binding}“ nicht verstanden. Beispiele: http://*:8080, http://shop.local, *:80:shop.local");
        if (protocol == "https")
            throw new ArgumentException("HTTPS-Bindungen brauchen ein Zertifikat – lege die Site mit HTTP an und ergänze HTTPS im IIS-Manager.");
        var path = string.IsNullOrWhiteSpace(r.PhysicalPath) ? Path.Combine(store.TargetDirectory(name, "/"), "_initial") : r.PhysicalPath.Trim();
        Directory.CreateDirectory(path);

        await _write.WaitAsync();
        try
        {
            using var sm = Open();
            if (sm.Sites[name] is not null) throw new InvalidOperationException($"Site „{name}“ gibt es schon.");
            if (sm.Sites.SelectMany(s => s.Bindings).Any(b => b.Protocol == protocol && string.Equals(b.BindingInformation, info, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Die Bindung {info} wird bereits von einer anderen Site verwendet.");
            var poolName = string.IsNullOrWhiteSpace(r.Pool) ? name : r.Pool.Trim();
            if (sm.ApplicationPools[poolName] is null)
            {
                var p = sm.ApplicationPools.Add(poolName);
                p.ManagedRuntimeVersion = r.ManagedRuntimeVersion ?? "";
                p.ManagedPipelineMode = ManagedPipelineMode.Integrated;
                p.AutoStart = true;
            }
            var site = sm.Sites.Add(name, protocol, info, path);
            site.ServerAutoStart = true;
            site.Applications["/"].ApplicationPoolName = poolName;
            sm.CommitChanges();
            log.LogInformation("Site {Site} angelegt ({Binding}, Pool {Pool})", name, info, poolName);
        }
        finally
        {
            _write.Release();
        }
    }

    public async Task DeleteSiteAsync(string name, bool removeFiles)
    {
        List<string> appPaths;
        await _write.WaitAsync();
        try
        {
            using var sm = Open();
            var s = sm.Sites[name] ?? throw new NotFoundException($"Site „{name}“ gibt es nicht.");
            appPaths = s.Applications.Select(a => a.Path).ToList();
            sm.Sites.Remove(s);
            sm.CommitChanges();
            log.LogInformation("Site {Site} gelöscht", name);
        }
        finally
        {
            _write.Release();
        }
        if (!removeFiles) return;
        foreach (var app in appPaths)
        {
            var dir = store.TargetDirectory(name, app);
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log.LogWarning("Ordner {Dir} konnte nicht gelöscht werden: {Error}", dir, e.Message);
            }
        }
    }

    /// <summary>Physischen Pfad einer Anwendung umstellen (das eigentliche „Umschalten“ einer Version).</summary>
    public async Task<(string OldPath, string Pool)> SetPhysicalPathAsync(string site, string appPath, string newPath)
    {
        await _write.WaitAsync();
        try
        {
            using var sm = Open();
            var s = sm.Sites[site] ?? throw new NotFoundException($"Site „{site}“ gibt es nicht.");
            var app = s.Applications.FirstOrDefault(a => a.Path.Equals(appPath, StringComparison.OrdinalIgnoreCase))
                      ?? throw new NotFoundException($"Anwendung „{appPath}“ gibt es in „{site}“ nicht.");
            var vdir = app.VirtualDirectories["/"] ?? throw new NotFoundException($"„{site}{appPath}“ hat kein virtuelles Stammverzeichnis.");
            var old = vdir.PhysicalPath;
            vdir.PhysicalPath = newPath;
            sm.CommitChanges();
            return (old, app.ApplicationPoolName);
        }
        finally
        {
            _write.Release();
        }
    }

    /// <summary>Anwendung in einer bestehenden Site anlegen (für Deployments in Unterpfade).</summary>
    public async Task CreateApplicationAsync(string site, string appPath, string physicalPath, string? pool)
    {
        await _write.WaitAsync();
        try
        {
            using var sm = Open();
            var s = sm.Sites[site] ?? throw new NotFoundException($"Site „{site}“ gibt es nicht.");
            if (s.Applications.Any(a => a.Path.Equals(appPath, StringComparison.OrdinalIgnoreCase))) return;
            var app = s.Applications.Add(appPath, physicalPath);
            var poolName = string.IsNullOrWhiteSpace(pool) ? s.Applications["/"].ApplicationPoolName : pool.Trim();
            if (sm.ApplicationPools[poolName] is null)
            {
                var p = sm.ApplicationPools.Add(poolName);
                p.ManagedRuntimeVersion = "";
                p.AutoStart = true;
            }
            app.ApplicationPoolName = poolName;
            sm.CommitChanges();
        }
        finally
        {
            _write.Release();
        }
    }

    /// <summary>Pool neu starten (nach dem Umschalten) – gestoppte Pools werden gestartet.</summary>
    public Task RecycleAsync(string pool) => PoolActionAsync(pool, "recycle");

    public (int Pools, int PoolsStarted, int Sites, int SitesStarted) Counts()
    {
        using var sm = Open();
        return (sm.ApplicationPools.Count, sm.ApplicationPools.Count(p => StateOf(() => p.State) == "Started"),
                sm.Sites.Count, sm.Sites.Count(s => StateOf(() => s.State) == "Started"));
    }
}
