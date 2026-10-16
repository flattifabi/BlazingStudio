using BlazorStudio.Deploy;

namespace BlazorStudio.Agent;

/// <summary>Deployment als Windows-Dienst: Version ablegen, Dienst stoppen, persistente Dateien mitnehmen, Programmpfad umstellen, starten, prüfen.</summary>
public sealed partial class DeploymentService
{
    private static readonly TimeSpan ServiceTimeout = TimeSpan.FromSeconds(60);

    private SemaphoreSlim ServiceLock(string name) => _locks.GetOrAdd("svc:" + name, _ => new SemaphoreSlim(1, 1));

    private async Task ExecuteServiceAsync(DeploymentRecord record, DeployRequest req, string packageFile)
    {
        var name = req.Site;
        var store = services.Store;
        void Log(string text, string level = "info") => journal.Log(record, text, level);

        var existing = services.Get(name);
        if (existing is { Managed: false })
            throw new InvalidOperationException($"Den Dienst „{name}“ gibt es schon, er wurde aber nicht vom Agent ausgerollt ({existing.CommandLine}). Bitte einen anderen Namen wählen oder den Dienst vorher entfernen.");
        var previous = existing?.ActiveVersion;
        journal.Update(record, r => r.PreviousVersion = previous);
        Log(existing is null ? $"Dienst „{name}“ gibt es noch nicht – wird angelegt" : $"Aktuell: {previous ?? existing.CommandLine} ({existing.State})");

        if (req.Persist is not null)
        {
            var parsed = PersistentPaths.Parse(req.Persist, out var invalid);
            if (invalid.Count > 0) throw new ArgumentException("Ungültige persistente Pfade: " + string.Join(", ", invalid));
            var list = serviceConfig.SetPersistent(name, parsed);
            Log(list.Count == 0 ? "Persistente Dateien: keine" : "Persistente Dateien: " + string.Join(", ", list));
        }

        // 1. Version ablegen
        if (store.Exists(name, "/", req.Version))
        {
            if (string.Equals(previous, req.Version, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Version {req.Version} ist bereits aktiv. Bitte eine neue Versionsbezeichnung verwenden.");
            Log($"Vorhandene (nicht aktive) Version {req.Version} wird ersetzt", "warn");
        }
        Log($"Entpacken nach {store.VersionDirectory(name, "/", req.Version)}");
        var installed = await Task.Run(() => store.Install(name, "/", packageFile, new VersionMetadata
        {
            Version = req.Version,
            DeployedAt = DateTime.UtcNow,
            Source = req.Source,
            Commit = req.Commit,
            Notes = req.Notes,
            DeploymentId = record.Id,
        }, overwrite: true));
        Log($"{installed.Files} Dateien, {installed.SizeBytes / 1024.0 / 1024.0:0.0} MB");

        // Programm bestimmen und in den Metadaten merken (für späteres Umschalten)
        var (program, error) = ServiceCommandLine.FindProgram(installed.Path, req.ServiceExe);
        if (program is null) throw new InvalidOperationException(error);
        var meta = store.ReadMetadata(name, "/", req.Version) ?? new VersionMetadata { Version = req.Version, DeployedAt = DateTime.UtcNow };
        meta.ServiceProgram = program;
        meta.ServiceArguments = string.IsNullOrWhiteSpace(req.ServiceArguments) ? null : req.ServiceArguments.Trim();
        store.WriteMetadata(name, "/", meta);
        Log($"Programm: {program}{(meta.ServiceArguments is { } a ? " " + a : "")}");
        serviceConfig.PrepareVersion(name, installed.Path, (t, l) => Log(t, l));

        if (!req.Activate)
        {
            Log("Abgelegt, aber nicht aktiviert (Option „nur hochladen“).", "ok");
            journal.Update(record, r => r.State = DeploymentState.Succeeded);
            return;
        }

        var command = ServiceCommandLine.CommandFor(installed.Path, program, meta.ServiceArguments, ServiceManager.DotnetPath);

        // 2. Anlegen oder umschalten
        if (existing is null)
        {
            Log($"Dienst wird angelegt: {command}");
            await services.CreateAsync(name, command, req.ServiceDisplayName ?? name, req.ServiceDescription,
                string.IsNullOrWhiteSpace(req.ServiceAccount) ? "LocalSystem" : req.ServiceAccount, req.ServicePassword, req.ServiceStartType);
            store.MarkActivated(name, "/", req.Version, DateTime.UtcNow);
            Log("Dienst wird gestartet …");
            await services.StartAsync(name, ServiceTimeout);
            var (ok, detail) = await CheckServiceAsync(record, name);
            if (!ok)
            {
                // Kein Vorgänger zum Zurückschalten – stoppen, damit er nicht in einer Neustart-Schleife hängt
                try { await services.StopAsync(name, ServiceTimeout); } catch (InvalidOperationException) { }
                throw new InvalidOperationException($"Der neue Dienst läuft nicht: {detail}");
            }
        }
        else
        {
            if (req.ServiceAccount is not null || req.ServiceStartType is not null || req.ServiceDisplayName is not null || req.ServiceDescription is not null)
                await services.ConfigureAsync(name, displayName: req.ServiceDisplayName, description: req.ServiceDescription,
                    account: req.ServiceAccount, password: req.ServicePassword, startType: req.ServiceStartType);

            var oldCommand = existing.CommandLine;
            var oldDir = existing.Directory;
            var moved = await SwitchServiceAsync(record, name, oldDir, installed.Path, command);
            store.MarkActivated(name, "/", req.Version, DateTime.UtcNow);

            var (ok, detail) = await CheckServiceAsync(record, name);
            if (!ok)
            {
                journal.Update(record, r => r.Error = detail);
                if (req.Rollback)
                {
                    Log($"Prüfung fehlgeschlagen ({detail}) – zurück auf {previous ?? oldCommand}", "error");
                    await services.StopAsync(name, ServiceTimeout);
                    moved?.Undo((t, l) => Log(t, l));
                    await services.ConfigureAsync(name, commandLine: oldCommand);
                    if (previous is not null) store.MarkActivated(name, "/", previous, DateTime.UtcNow);
                    await services.StartAsync(name, ServiceTimeout);
                    Log("Rollback abgeschlossen – vorherige Version läuft", "warn");
                    journal.Update(record, r => r.State = DeploymentState.RolledBack);
                    return;
                }
                throw new InvalidOperationException(detail);
            }
        }

        // 3. Aufräumen
        var removed = store.Prune(name, "/", options.KeepVersions, req.Version);
        if (removed.Count > 0) Log($"Alte Versionen entfernt: {string.Join(", ", removed)}");
        Log($"Version {req.Version} läuft als Dienst „{name}“.", "ok");
        journal.Update(record, r => r.State = DeploymentState.Succeeded);
    }

    /// <summary>Stoppen → persistente Dateien verschieben → Programmpfad umstellen → starten. Scheitert etwas, läuft der alte Stand wieder.</summary>
    private async Task<PersistentMove?> SwitchServiceAsync(DeploymentRecord record, string name, string fromDir, string toDir, string command)
    {
        void Log(string text, string level = "info") => journal.Log(record, text, level);
        var info = services.Get(name)!;
        var wasRunning = info.State == "Running";
        Log("Dienst wird gestoppt …");
        await services.StopAsync(name, ServiceTimeout);
        PersistentMove? moved = null;
        try
        {
            var paths = serviceConfig.GetPersistent(name);
            if (paths.Count > 0 && Directory.Exists(fromDir) && !string.Equals(Path.GetFullPath(fromDir), Path.GetFullPath(toDir), StringComparison.OrdinalIgnoreCase))
            {
                Log($"Persistente Dateien werden übernommen ({string.Join(", ", paths)})");
                moved = await PersistentMove.RunAsync(fromDir, toDir, paths, (t, l) => Log(t, l), TimeSpan.FromSeconds(30));
            }
            await services.ConfigureAsync(name, commandLine: command);
            Log($"Umgeschaltet: {command}");
            Log("Dienst wird gestartet …");
            await services.StartAsync(name, ServiceTimeout);
            return moved;
        }
        catch
        {
            // Alten Stand wiederherstellen
            try
            {
                await services.StopAsync(name, ServiceTimeout);
                moved?.Undo((t, l) => Log(t, l));
                await services.ConfigureAsync(name, commandLine: info.CommandLine);
                if (wasRunning) await services.StartAsync(name, ServiceTimeout);
                Log("Vorheriger Stand wiederhergestellt", "warn");
            }
            catch (Exception e) when (e is InvalidOperationException or IOException)
            {
                Log("Vorheriger Stand konnte nicht vollständig wiederhergestellt werden: " + e.Message, "error");
            }
            throw;
        }
    }

    /// <summary>Läuft der Dienst nach ein paar Sekunden noch? Danach optional die Health-URL.</summary>
    private async Task<(bool Ok, string Detail)> CheckServiceAsync(DeploymentRecord record, string name)
    {
        if (!await services.StaysRunningAsync(name, TimeSpan.FromSeconds(8)))
            return (false, $"Dienst „{name}“ ist kurz nach dem Start wieder beendet (Ereignisanzeige → Windows-Protokolle → Anwendung).");
        journal.Log(record, "Dienst läuft stabil");
        if (record.HealthUrl is not { } url) return (true, "läuft");
        var (ok, detail) = await HealthCheckAsync(record, url);
        if (ok) journal.Log(record, $"Health-Check OK ({detail})", "ok");
        return (ok, ok ? detail : $"Health-Check fehlgeschlagen: {detail}");
    }

    /// <summary>Manuelles Umschalten eines Dienstes auf eine abgelegte Version (auch Rollback).</summary>
    public async Task<DeploymentRecord> ActivateServiceAsync(string name, ServiceActivateRequest req, string? source)
    {
        var store = services.Store;
        if (!VersionStore.IsValidVersion(req.Version) || !store.Exists(name, "/", req.Version))
            throw new NotFoundException($"Version {req.Version} gibt es für den Dienst {name} nicht.");
        var info = services.Get(name) ?? throw new NotFoundException($"Dienst „{name}“ gibt es nicht.");
        var meta = store.ReadMetadata(name, "/", req.Version);
        var dir = store.VersionDirectory(name, "/", req.Version);
        var program = meta?.ServiceProgram ?? ServiceCommandLine.FindProgram(dir, null).Program
                      ?? throw new InvalidOperationException($"In Version {req.Version} wurde kein Programm gefunden.");
        var record = new DeploymentRecord
        {
            Id = DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..6],
            Site = name,
            Version = req.Version,
            PreviousVersion = info.ActiveVersion,
            State = DeploymentState.Running,
            Source = source ?? "Umschaltung",
            Notes = "Version umgeschaltet",
            StartedAt = DateTime.UtcNow,
            Target = DeployTargets.Service,
        };
        journal.Add(record);
        var gate = ServiceLock(name);
        await gate.WaitAsync();
        try
        {
            serviceConfig.PrepareVersion(name, dir, (t, l) => journal.Log(record, t, l));
            await SwitchServiceAsync(record, name, info.Directory, dir, ServiceCommandLine.CommandFor(dir, program, meta?.ServiceArguments, ServiceManager.DotnetPath));
            store.MarkActivated(name, "/", req.Version, DateTime.UtcNow);
            var (ok, detail) = await CheckServiceAsync(record, name);
            if (!ok) throw new InvalidOperationException(detail);
            journal.Log(record, $"Umgeschaltet von {info.ActiveVersion ?? info.CommandLine} auf {req.Version}", "ok");
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

    public void DeleteServiceVersion(string name, string version)
    {
        if (services.Get(name)?.ActiveVersion is { } active && string.Equals(active, version, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Die aktive Version kann nicht gelöscht werden.");
        services.Store.Delete(name, "/", version);
    }
}
