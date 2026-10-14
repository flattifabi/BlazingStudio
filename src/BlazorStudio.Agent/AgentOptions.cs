using System.Security.Cryptography;
using System.Text;
using BlazorStudio.Deploy;

namespace BlazorStudio.Agent;

/// <summary>Abschnitt "Agent" in appsettings.json.</summary>
public sealed class AgentOptions
{
    public string ApiKey { get; set; } = "";

    /// <summary>Ablage der Versionen: &lt;DeployRoot&gt;\&lt;Site&gt;\&lt;Version&gt;.</summary>
    public string DeployRoot { get; set; } = @"C:\inetpub\deployments";

    /// <summary>Verlauf usw.; leer = %ProgramData%\BlazorStudio\Agent.</summary>
    public string DataDirectory { get; set; } = "";

    /// <summary>So viele Versionen je Site bleiben liegen (aktive und vorherige immer).</summary>
    public int KeepVersions { get; set; } = 5;

    public int MaxPackageMegabytes { get; set; } = 2048;

    /// <summary>So lange versucht der Health-Check, eine erfolgreiche Antwort zu bekommen.</summary>
    public int HealthCheckSeconds { get; set; } = 60;

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(DataDirectory))
            DataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BlazorStudio", "Agent");
        DeployRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(DeployRoot));
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(DeployRoot);
        KeepVersions = Math.Clamp(KeepVersions, 1, 100);
        MaxPackageMegabytes = Math.Clamp(MaxPackageMegabytes, 10, 16 * 1024);
        HealthCheckSeconds = Math.Clamp(HealthCheckSeconds, 5, 600);
    }
}

/// <summary>Prüft den Header X-Api-Key (zeitkonstant). /api/ping ist offen, damit Studio „offline“ von „falscher Schlüssel“ unterscheiden kann.</summary>
public sealed class ApiKeyMiddleware(RequestDelegate next, AgentOptions options)
{
    private readonly byte[] _expected = SHA256.HashData(Encoding.UTF8.GetBytes(options.ApiKey ?? ""));

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (ctx.Request.Path.StartsWithSegments("/api/ping"))
        {
            await next(ctx);
            return;
        }
        var given = ctx.Request.Headers[AgentDefaults.ApiKeyHeader].ToString();
        var ok = !string.IsNullOrWhiteSpace(options.ApiKey) && given.Length > 0 &&
                 CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(given)), _expected);
        if (!ok)
        {
            // Bremst Durchprobieren von Schlüsseln
            await Task.Delay(400);
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new ApiError
            {
                Error = string.IsNullOrWhiteSpace(options.ApiKey) ? "Auf dem Server ist kein API-Schlüssel eingerichtet." : "API-Schlüssel ungültig.",
            });
            return;
        }
        await next(ctx);
    }
}

/// <summary>Fehler aus Agent-Code in HTTP-Status übersetzen.</summary>
public static class ApiErrors
{
    public static (int Status, string Message) Map(Exception? e) => e switch
    {
        null => (500, "Unbekannter Fehler"),
        BadHttpRequestException b => (b.StatusCode, b.Message),
        NotFoundException => (404, e.Message),
        ArgumentException => (400, e.Message),
        InvalidOperationException => (409, e.Message),
        InvalidDataException => (400, e.Message),
        IisUnavailableException => (503, e.Message),
        UnauthorizedAccessException => (500, "Zugriff verweigert – läuft der Dienst als LocalSystem bzw. Administrator? " + e.Message),
        System.Runtime.InteropServices.COMException c => (500, $"IIS: {c.Message} (0x{c.HResult:X8})"),
        _ => (500, e.Message),
    };
}

public sealed class NotFoundException(string message) : Exception(message);

public sealed class IisUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
