using System.Runtime.Versioning;
using System.Text.Json.Serialization;
using BlazorStudio.Agent;
using BlazorStudio.Deploy;
using Microsoft.AspNetCore.Http.Features;

[assembly: SupportedOSPlatform("windows")]

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
builder.Host.UseWindowsService(o => o.ServiceName = "BlazorStudioAgent");

var options = builder.Configuration.GetSection("Agent").Get<AgentOptions>() ?? new AgentOptions();
options.Normalize();
builder.Services.AddSingleton(options);

builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = options.MaxPackageMegabytes * 1024L * 1024L);
builder.Services.Configure<FormOptions>(f => f.MultipartBodyLengthLimit = options.MaxPackageMegabytes * 1024L * 1024L);
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddSingleton(new VersionStore(options.DeployRoot));
builder.Services.AddSingleton(new DeploymentJournal(Path.Combine(options.DataDirectory, "deployments.json")));
builder.Services.AddSingleton<SystemSampler>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SystemSampler>());
builder.Services.AddSingleton<IisManager>();
builder.Services.AddSingleton<SiteConfigService>();
builder.Services.AddSingleton<ServiceManager>();
builder.Services.AddSingleton<ServiceConfigService>();
builder.Services.AddSingleton<MemoryService>();
builder.Services.AddSingleton<DeploymentService>();
builder.Services.AddHttpClient("health").ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    // Health-Checks gehen meist an localhost mit dem Zertifikat des öffentlichen Hostnamens
    ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    AllowAutoRedirect = false,
});

var app = builder.Build();

if (string.IsNullOrWhiteSpace(options.ApiKey))
    app.Logger.LogError("Kein API-Schlüssel konfiguriert (Agent:ApiKey in appsettings.json). Alle Anfragen werden abgelehnt – install-service.ps1 erzeugt einen.");

app.UseExceptionHandler(e => e.Run(async ctx =>
{
    var ex = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    var (status, message) = ApiErrors.Map(ex);
    ctx.Response.StatusCode = status;
    await ctx.Response.WriteAsJsonAsync(new ApiError { Error = message });
}));
app.UseMiddleware<ApiKeyMiddleware>();

AgentEndpoints.Map(app);

app.Run();
