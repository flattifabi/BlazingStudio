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
