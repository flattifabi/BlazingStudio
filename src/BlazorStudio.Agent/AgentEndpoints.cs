using System.Reflection;
using BlazorStudio.Deploy;

namespace BlazorStudio.Agent;

/// <summary>REST-API des Agents. Alles außer /api/ping verlangt den Header X-Api-Key.</summary>
public static class AgentEndpoints
{
    public static string Version { get; } =
        typeof(AgentEndpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    private static readonly DateTime Started = DateTime.UtcNow;

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/ping", () => new PingResponse { Agent = AgentDefaults.AgentName, Version = Version, MachineName = Environment.MachineName });

        api.MapGet("/info", (SystemSampler sampler, AgentOptions options, DeploymentJournal journal) =>
        {
            var (cpu, mem) = sampler.History();
            var info = new AgentInfo
            {
                MachineName = Environment.MachineName,
                OsVersion = Environment.OSVersion.VersionString,
                AgentVersion = Version,
                IisInstalled = IisManager.IisInstalled,
                IisVersion = IisManager.IisVersion,
                IisServiceState = IisManager.W3SvcState,
                UptimeSeconds = Environment.TickCount64 / 1000.0,
                ProcessorCount = Environment.ProcessorCount,
                CpuPercent = sampler.CpuPercent,
                MemoryUsedBytes = sampler.MemoryUsedBytes,
                MemoryTotalBytes = sampler.MemoryTotalBytes,
                CpuHistory = cpu,
                MemoryHistory = mem,
                DeployRoot = options.DeployRoot,
                KeepVersions = options.KeepVersions,
                RunningDeployments = journal.Running,
            };
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(options.DeployRoot)!);
                info.DiskFreeBytes = drive.AvailableFreeSpace;
                info.DiskTotalBytes = drive.TotalSize;
            }
            catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { }
            if (info.IisInstalled)
            {
                try
                {
                    (info.Pools, info.PoolsStarted, info.Sites, info.SitesStarted) = app.Services.GetRequiredService<IisManager>().Counts();
                }
                catch (Exception e) when (e is IisUnavailableException or System.Runtime.InteropServices.COMException) { }
            }
            return info;
        });

        // ---------------- App-Pools ----------------
        api.MapGet("/pools", (IisManager iis) => iis.GetPools());
        api.MapPost("/pools", async (IisManager iis, PoolSettings s) =>
        {
            await iis.SavePoolAsync(s, create: true);
            return Results.NoContent();
        });
        api.MapPut("/pools/{name}", async (IisManager iis, string name, PoolSettings s) =>
        {
            s.Name = name;
            await iis.SavePoolAsync(s, create: false);
            return Results.NoContent();
        });
        api.MapDelete("/pools/{name}", async (IisManager iis, string name) =>
        {
            await iis.DeletePoolAsync(name);
            return Results.NoContent();
        });
        api.MapPost("/pools/{name}/{action:regex(^(start|stop|recycle)$)}", async (IisManager iis, string name, string action) =>
        {
            await iis.PoolActionAsync(name, action);
            return Results.NoContent();
        });

        // ---------------- Sites ----------------
        api.MapGet("/sites", (IisManager iis) => iis.GetSites());
        api.MapPost("/sites", async (IisManager iis, CreateSiteRequest r) =>
        {
            await iis.CreateSiteAsync(r);
            return Results.NoContent();
        });
        api.MapDelete("/sites/{name}", async (IisManager iis, string name, bool? removeFiles) =>
        {
            await iis.DeleteSiteAsync(name, removeFiles == true);
            return Results.NoContent();
        });
        api.MapPost("/sites/{name}/{action:regex(^(start|stop)$)}", async (IisManager iis, string name, string action) =>
        {
            await iis.SiteActionAsync(name, action);
            return Results.NoContent();
        });
        api.MapGet("/sites/{name}/versions", (IisManager iis, VersionStore store, string name, string? app) =>
        {
            var appPath = DeployRequest.NormalizeAppPath(app);
            var active = iis.FindApplication(name, appPath)?.ActiveVersion;
            return store.List(name, appPath, active);
        });
        api.MapPost("/sites/{name}/activate", async (DeploymentService deployments, HttpContext ctx, string name, ActivateRequest r) =>
            await deployments.ActivateAsync(name, r, ctx.Request.Headers.UserAgent.ToString().StartsWith("BlazorStudio", StringComparison.Ordinal) ? "Blazor Studio" : "API"));
        api.MapDelete("/sites/{name}/versions/{version}", (DeploymentService deployments, string name, string version, string? app) =>
        {
            deployments.DeleteVersion(name, app ?? "/", version);
            return Results.NoContent();
        });

        // ---------------- Authentifizierung, Konfiguration, Logs ----------------
        api.MapGet("/sites/{name}/auth", (IisManager iis, string name, string? app) =>
            iis.GetAuthentication(name, DeployRequest.NormalizeAppPath(app)));
        api.MapPut("/sites/{name}/auth", async (IisManager iis, string name, string? app, SiteAuthentication auth) =>
        {
            await iis.SetAuthenticationAsync(name, DeployRequest.NormalizeAppPath(app), auth);
            return Results.NoContent();
        });
        api.MapGet("/sites/{name}/config", (SiteConfigService config, string name, string? app) =>
            config.List(name, DeployRequest.NormalizeAppPath(app)));
        api.MapGet("/sites/{name}/config/{file}", (SiteConfigService config, string name, string file, string? app) =>
            config.Read(name, DeployRequest.NormalizeAppPath(app), file));
        api.MapPut("/sites/{name}/config/{file}", async (SiteConfigService config, string name, string file, string? app, SiteConfigWrite write) =>
            await config.WriteAsync(name, DeployRequest.NormalizeAppPath(app), file, write));
        api.MapDelete("/sites/{name}/config/{file}", (SiteConfigService config, string name, string file, string? app) =>
        {
            config.DeleteOverride(name, DeployRequest.NormalizeAppPath(app), file);
            return Results.NoContent();
        });
        api.MapGet("/sites/{name}/persist", (SiteConfigService config, string name, string? app) =>
            new PersistentSettings { Paths = config.GetPersistent(name, DeployRequest.NormalizeAppPath(app)) });
        api.MapPut("/sites/{name}/persist", (SiteConfigService config, string name, string? app, PersistentSettings settings) =>
            new PersistentSettings { Paths = config.SetPersistent(name, DeployRequest.NormalizeAppPath(app), settings.Paths) });
        api.MapGet("/sites/{name}/logs", (SiteConfigService config, string name, string? app) =>
            config.Logs(name, DeployRequest.NormalizeAppPath(app)));
        api.MapGet("/sites/{name}/logs/{file}", (SiteConfigService config, string name, string file, string? app) =>
            config.ReadLog(name, DeployRequest.NormalizeAppPath(app), file));

        // ---------------- Windows-Dienste ----------------
        api.MapGet("/services", (ServiceManager services, bool? all) => services.List(all == true));
        api.MapGet("/services/{name}", (ServiceManager services, string name) =>
            services.Get(name) is { } s ? Results.Ok(s) : Results.NotFound(new ApiError { Error = $"Dienst „{name}“ gibt es nicht." }));
        api.MapPost("/services/{name}/{action:regex(^(start|stop|restart)$)}", async (ServiceManager services, string name, string action) =>
        {
            await services.ActionAsync(name, action);
            return Results.NoContent();
        });
        api.MapDelete("/services/{name}", async (ServiceManager services, string name, bool? removeFiles) =>
        {
            await services.DeleteAsync(name, removeFiles == true);
            return Results.NoContent();
        });
        api.MapGet("/services/{name}/versions", (ServiceManager services, string name) =>
            services.Store.List(name, "/", services.Get(name)?.ActiveVersion));
        api.MapPost("/services/{name}/activate", async (DeploymentService deployments, HttpContext ctx, string name, ServiceActivateRequest r) =>
            await deployments.ActivateServiceAsync(name, r, ctx.Request.Headers.UserAgent.ToString().StartsWith("BlazorStudio", StringComparison.Ordinal) ? "Blazor Studio" : "API"));
        api.MapDelete("/services/{name}/versions/{version}", (DeploymentService deployments, string name, string version) =>
        {
            deployments.DeleteServiceVersion(name, version);
            return Results.NoContent();
        });
        api.MapGet("/services/{name}/persist", (ServiceConfigService config, string name) => new PersistentSettings { Paths = config.GetPersistent(name) });
        api.MapPut("/services/{name}/persist", (ServiceConfigService config, string name, PersistentSettings settings) =>
            new PersistentSettings { Paths = config.SetPersistent(name, settings.Paths) });
        api.MapGet("/services/{name}/config", (ServiceConfigService config, string name) => config.List(name));
        api.MapGet("/services/{name}/config/{file}", (ServiceConfigService config, string name, string file) => config.Read(name, file));
        api.MapPut("/services/{name}/config/{file}", async (ServiceConfigService config, string name, string file, SiteConfigWrite write) =>
            await config.WriteAsync(name, file, write));
        api.MapDelete("/services/{name}/config/{file}", (ServiceConfigService config, string name, string file) =>
        {
            config.DeleteOverride(name, file);
            return Results.NoContent();
        });

        // ---------------- Deployments ----------------
        api.MapGet("/deployments", (DeploymentJournal journal, string? site, int? take) => journal.List(site, take ?? 50));
        api.MapGet("/deployments/{id}", (DeploymentJournal journal, string id) =>
            journal.Get(id) is { } r ? Results.Ok(r) : Results.NotFound(new ApiError { Error = $"Deployment {id} gibt es nicht." }));
        api.MapPost("/deployments", async (HttpRequest request, DeploymentService deployments, AgentOptions options) =>
        {
            if (!request.HasFormContentType) throw new ArgumentException("Erwartet multipart/form-data mit dem Feld „package“ (ZIP).");
            var form = await request.ReadFormAsync();
            var file = form.Files.GetFile(AgentDefaults.PackageField) ?? throw new ArgumentException("Feld „package“ (ZIP-Datei) fehlt.");
            var req = DeployRequest.FromForm(k => form.TryGetValue(k, out var v) ? v.ToString() : null);

            var incoming = Path.Combine(options.DataDirectory, "incoming");
            Directory.CreateDirectory(incoming);
            var temp = Path.Combine(incoming, Guid.NewGuid().ToString("N") + ".zip");
            await using (var target = File.Create(temp))
                await file.CopyToAsync(target);
            try
            {
                using var _ = System.IO.Compression.ZipFile.OpenRead(temp);
            }
            catch (InvalidDataException)
            {
                File.Delete(temp);
                throw new ArgumentException("Das hochgeladene Paket ist keine gültige ZIP-Datei.");
            }
            try
            {
                var record = deployments.Start(req, temp, file.Length);
                return Results.Accepted($"/api/deployments/{record.Id}", record);
            }
            catch
            {
                File.Delete(temp);
                throw;
            }
        }).DisableAntiforgery();
    }
}
