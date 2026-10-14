using System.Collections.Concurrent;
using BlazorStudio.Deploy;

namespace BlazorStudio.Agent;

/// <summary>
/// Führt Deployments im Hintergrund aus: Version ablegen → IIS-Anwendung umschalten → Pool recyceln →
/// Health-Check → bei Fehler Rollback → alte Versionen aufräumen. Pro Site/Anwendung läuft immer nur eins.
/// </summary>
public sealed class DeploymentService(IisManager iis, VersionStore store, DeploymentJournal journal, AgentOptions options, IHttpClientFactory http, ILogger<DeploymentService> log)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    private SemaphoreSlim LockFor(string site, string appPath) => _locks.GetOrAdd(VersionStore.TargetFolderName(site, appPath), _ => new SemaphoreSlim(1, 1));

    /// <summary>Prüft die Eingaben, legt den Verlaufseintrag an und startet das Deployment. Das Paket (temporäre Datei) gehört danach dem Dienst.</summary>
    public DeploymentRecord Start(DeployRequest req, string packageFile, long packageBytes)
    {
        if (string.IsNullOrWhiteSpace(req.Site)) throw new ArgumentException("Site fehlt.");
        if (!VersionStore.IsValidVersion(req.Version))
            throw new ArgumentException($"Ungültige Version „{req.Version}“ – erlaubt sind Buchstaben, Ziffern und . _ + - (max. 64 Zeichen).");
        if (req.CreateSite && !iis.SiteExists(req.Site) && !BindingParser.TryParse(req.Binding, out _, out _))
            throw new ArgumentException("Zum Anlegen der Site wird eine Bindung benötigt, z. B. http://*:8080.");

        var record = new DeploymentRecord
        {
            Id = DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..6],
            Site = req.Site,
            AppPath = req.AppPath,
            Version = req.Version,
            State = DeploymentState.Queued,
            Source = req.Source,
            Commit = req.Commit,
            Notes = req.Notes,
            HealthUrl = string.IsNullOrWhiteSpace(req.HealthUrl) ? null : req.HealthUrl.Trim(),
            StartedAt = DateTime.UtcNow,
            PackageBytes = packageBytes,
        };
        journal.Add(record);
        journal.Log(record, $"Paket empfangen ({packageBytes / 1024.0 / 1024.0:0.0} MB) von {req.Source ?? "unbekannt"}");
        _ = Task.Run(() => RunAsync(record, req, packageFile));
        return journal.Get(record.Id)!;
    }

    private async Task RunAsync(DeploymentRecord record, DeployRequest req, string packageFile)
    {
        var gate = LockFor(req.Site, req.AppPath);
        if (gate.CurrentCount == 0) journal.Log(record, "Wartet auf ein laufendes Deployment derselben Anwendung …");
        await gate.WaitAsync();
        try
        {
            journal.Update(record, r => r.State = DeploymentState.Running);
            await ExecuteAsync(record, req, packageFile);
        }
        catch (Exception e)
        {
            log.LogError(e, "Deployment {Id} fehlgeschlagen", record.Id);
            var (_, message) = ApiErrors.Map(e);
            journal.Log(record, message, "error");
            journal.Update(record, r =>
            {
                if (r.State != DeploymentState.RolledBack) r.State = DeploymentState.Failed;
                r.Error ??= message;
            });
        }
        finally
        {
            journal.Update(record, r => r.FinishedAt = DateTime.UtcNow);
            gate.Release();
            try { File.Delete(packageFile); } catch (IOException) { }
        }
    }

    private async Task ExecuteAsync(DeploymentRecord record, DeployRequest req, string packageFile)
    {
        var site = req.Site;
        var appPath = req.AppPath;

        // 1. Site/Anwendung vorhanden? (Neue Sites werden erst nach dem Entpacken angelegt – direkt auf dem Versionsordner)
        var siteExists = iis.SiteExists(site);
        if (!siteExists && !req.CreateSite)
            throw new NotFoundException($"Site „{site}“ gibt es auf diesem Server nicht (Option „Site anlegen“ verwenden).");
        var app = siteExists ? iis.FindApplication(site, appPath) : null;
        var previous = app?.ActiveVersion;
        var previousPath = app?.PhysicalPath;
        journal.Update(record, r => r.PreviousVersion = previous);
        if (app is not null)
            journal.Log(record, previous is not null ? $"Aktuell aktiv: {previous}" : $"Aktueller Ordner: {previousPath}");

        // 2. Version ablegen
        if (store.Exists(site, appPath, req.Version))
        {
            if (string.Equals(previous, req.Version, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Version {req.Version} ist bereits aktiv. Bitte eine neue Versionsbezeichnung verwenden.");
            // Nicht aktiv = Rest eines abgebrochenen Versuchs oder bewusst erneut ausgerollt → ersetzen
            journal.Log(record, $"Vorhandene (nicht aktive) Version {req.Version} wird ersetzt", "warn");
        }
        journal.Log(record, $"Entpacken nach {store.VersionDirectory(site, appPath, req.Version)}");
        var installed = await Task.Run(() => store.Install(site, appPath, packageFile, new VersionMetadata
        {
            Version = req.Version,
            DeployedAt = DateTime.UtcNow,
            Source = req.Source,
            Commit = req.Commit,
            Notes = req.Notes,
            DeploymentId = record.Id,
        }, overwrite: true));
        journal.Log(record, $"{installed.Files} Dateien, {installed.SizeBytes / 1024.0 / 1024.0:0.0} MB");
        if (!File.Exists(Path.Combine(installed.Path, "web.config")) && !File.Exists(Path.Combine(installed.Path, "index.html")))
            journal.Log(record, "Keine web.config im Paket – ist das wirklich ein Publish-Ordner?", "warn");

        if (!req.Activate)
        {
            journal.Log(record, "Abgelegt, aber nicht aktiviert (Option „nur hochladen“).", "ok");
            journal.Update(record, r => r.State = DeploymentState.Succeeded);
            return;
        }

        // 3. Umschalten
        string pool;
        if (!siteExists)
        {
            journal.Log(record, $"Site „{site}“ wird angelegt ({req.Binding}) auf {installed.Path}");
            await iis.CreateSiteAsync(new CreateSiteRequest { Name = site, Binding = req.Binding!, Pool = req.Pool, ManagedRuntimeVersion = req.ManagedRuntimeVersion, PhysicalPath = installed.Path });
            if (appPath != "/") await iis.CreateApplicationAsync(site, appPath, installed.Path, req.Pool);
        }
        else if (app is null)
        {
            journal.Log(record, $"Anwendung {appPath} wird in „{site}“ angelegt");
            await iis.CreateApplicationAsync(site, appPath, installed.Path, req.Pool);
        }
        else
        {
            await iis.SetPhysicalPathAsync(site, appPath, installed.Path);
        }

        // Nachlesen: hat IIS den Pfad wirklich übernommen?
        var now = iis.FindApplication(site, appPath);
        if (now is null || !SamePath(now.PhysicalPath, installed.Path))
            throw new InvalidOperationException($"IIS hat den physischen Pfad nicht übernommen (aktuell: {now?.PhysicalPath ?? "–"}, erwartet: {installed.Path}).");
        pool = now.Pool;
        store.MarkActivated(site, appPath, req.Version, DateTime.UtcNow);
        journal.Log(record, $"Umgeschaltet auf {req.Version} – physischer Pfad: {now.PhysicalPath}");
        if (pool.Length > 0)
        {
            await iis.RecycleAsync(pool);
            journal.Log(record, $"App-Pool „{pool}“ recycelt");
        }

        // 4. Health-Check
        if (record.HealthUrl is { } url)
        {
            var (ok, detail) = await HealthCheckAsync(record, url);
            if (!ok)
            {
                journal.Update(record, r => r.Error = $"Health-Check fehlgeschlagen: {detail}");
                if (req.Rollback && previousPath is not null)
                {
                    journal.Log(record, $"Health-Check fehlgeschlagen ({detail}) – Rollback auf {previous ?? previousPath}", "error");
                    await iis.SetPhysicalPathAsync(site, appPath, previousPath);
                    if (previous is not null) store.MarkActivated(site, appPath, previous, DateTime.UtcNow);
                    if (pool.Length > 0) await iis.RecycleAsync(pool);
                    journal.Log(record, "Rollback abgeschlossen", "warn");
                    journal.Update(record, r => r.State = DeploymentState.RolledBack);
                    return;
                }
                throw new InvalidOperationException($"Health-Check fehlgeschlagen: {detail}");
            }
            journal.Log(record, $"Health-Check OK ({detail})", "ok");
        }

        // 5. Aufräumen
        var removed = store.Prune(site, appPath, options.KeepVersions, req.Version);
        if (removed.Count > 0) journal.Log(record, $"Alte Versionen entfernt: {string.Join(", ", removed)}");

        journal.Log(record, $"Version {req.Version} ist aktiv.", "ok");
        journal.Update(record, r => r.State = DeploymentState.Succeeded);
    }

    private async Task<(bool Ok, string Detail)> HealthCheckAsync(DeploymentRecord record, string url)
    {
        journal.Log(record, $"Health-Check: {url}");
        var client = http.CreateClient("health");
        client.Timeout = TimeSpan.FromSeconds(15);
        var deadline = DateTime.UtcNow.AddSeconds(options.HealthCheckSeconds);
        var last = "";
        var attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                using var resp = await client.GetAsync(url);
                var code = (int)resp.StatusCode;
                last = $"HTTP {code}";
                if (code < 400) return (true, last);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                last = e.InnerException?.Message ?? e.Message;
            }
            if (DateTime.UtcNow >= deadline) return (false, $"{last} nach {attempt} Versuchen");
            if (attempt == 1 || attempt % 5 == 0) journal.Log(record, $"  noch nicht bereit: {last}");
            await Task.Delay(2000);
        }
    }

    private static bool SamePath(string? a, string? b)
    {
        static string N(string? p) => Path.GetFullPath(Environment.ExpandEnvironmentVariables(p ?? "")).TrimEnd('\\', '/');
        try { return string.Equals(N(a), N(b), StringComparison.OrdinalIgnoreCase); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    /// <summary>Manuelles Umschalten auf eine abgelegte Version (auch Rollback). Wird im Verlauf festgehalten.</summary>
    public async Task<DeploymentRecord> ActivateAsync(string site, ActivateRequest req, string? source)
    {
        var appPath = DeployRequest.NormalizeAppPath(req.AppPath);
        if (!VersionStore.IsValidVersion(req.Version) || !store.Exists(site, appPath, req.Version))
            throw new NotFoundException($"Version {req.Version} gibt es für {site}{(appPath == "/" ? "" : appPath)} nicht.");
        var app = iis.FindApplication(site, appPath) ?? throw new NotFoundException($"Anwendung {site}{appPath} gibt es nicht.");
        var record = new DeploymentRecord
        {
            Id = DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..6],
            Site = site,
            AppPath = appPath,
            Version = req.Version,
            PreviousVersion = app.ActiveVersion,
            State = DeploymentState.Running,
            Source = source ?? "Umschaltung",
            Notes = "Version umgeschaltet",
            StartedAt = DateTime.UtcNow,
        };
        journal.Add(record);
        var gate = LockFor(site, appPath);
        await gate.WaitAsync();
        try
        {
            var (_, pool) = await iis.SetPhysicalPathAsync(site, appPath, store.VersionDirectory(site, appPath, req.Version));
            store.MarkActivated(site, appPath, req.Version, DateTime.UtcNow);
            journal.Log(record, $"Umgeschaltet von {app.ActiveVersion ?? app.PhysicalPath} auf {req.Version}");
            if (req.Recycle && pool.Length > 0)
            {
                await iis.RecycleAsync(pool);
                journal.Log(record, $"App-Pool „{pool}“ recycelt");
            }
            journal.Update(record, r => r.State = DeploymentState.Succeeded);
        }
        catch (Exception e)
        {
            journal.Log(record, e.Message, "error");
            journal.Update(record, r =>
            {
                r.State = DeploymentState.Failed;
                r.Error = e.Message;
            });
            throw;
        }
        finally
        {
            journal.Update(record, r => r.FinishedAt = DateTime.UtcNow);
            gate.Release();
        }
        return journal.Get(record.Id)!;
    }

    public void DeleteVersion(string site, string appPath, string version)
    {
        appPath = DeployRequest.NormalizeAppPath(appPath);
        if (!VersionStore.IsValidVersion(version) || !store.Exists(site, appPath, version)) throw new NotFoundException($"Version {version} gibt es nicht.");
        var active = iis.FindApplication(site, appPath)?.ActiveVersion;
        if (string.Equals(active, version, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Die aktive Version kann nicht gelöscht werden – erst auf eine andere umschalten.");
        store.Delete(site, appPath, version);
    }
}
