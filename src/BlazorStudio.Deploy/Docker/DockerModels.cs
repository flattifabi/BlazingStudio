using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlazorStudio.Deploy.Docker;

/// <summary>Wie das Image entsteht.</summary>
public enum DockerBuildMode
{
    /// <summary>Dockerfile, falls vorhanden, sonst .NET SDK.</summary>
    Auto,

    Dockerfile,

    /// <summary><c>dotnet publish /t:PublishContainer</c> – kein Dockerfile nötig.</summary>
    DotnetSdk,
}

/// <summary>
/// Alles, was zum Starten eines Containers gehört (ohne Image). Wird als Label am Container gespeichert,
/// damit ein Umschalten auf eine andere Version denselben Container mit anderem Image neu starten kann.
/// </summary>
public sealed class DockerRunSpec
{
    /// <summary>„Host:Container“, z. B. „8080:8080“ oder „127.0.0.1:5000:8080“.</summary>
    public List<string> Ports { get; set; } = new();

    /// <summary>„NAME=Wert“.</summary>
    public List<string> Environment { get; set; } = new();

    /// <summary>„Host-Pfad oder Volume:Container-Pfad[:ro]“.</summary>
    public List<string> Volumes { get; set; } = new();

    /// <summary>no, always, unless-stopped, on-failure.</summary>
    public string RestartPolicy { get; set; } = "unless-stopped";

    public string? Network { get; set; }

    /// <summary>Weitere Argumente für <c>docker run</c> (eines pro Eintrag).</summary>
    public List<string> ExtraArgs { get; set; } = new();
}

/// <summary>Zeile aus <c>docker ps -a --format "{{json .}}"</c>.</summary>
public sealed class DockerContainer
{
    [JsonPropertyName("ID")] public string Id { get; set; } = "";

    [JsonPropertyName("Names")] public string Names { get; set; } = "";

    [JsonPropertyName("Image")] public string Image { get; set; } = "";

    /// <summary>running, exited, created, paused, restarting, dead.</summary>
    [JsonPropertyName("State")] public string State { get; set; } = "";

    [JsonPropertyName("Status")] public string Status { get; set; } = "";

    [JsonPropertyName("Ports")] public string Ports { get; set; } = "";

    [JsonPropertyName("CreatedAt")] public string CreatedAt { get; set; } = "";

    [JsonPropertyName("RunningFor")] public string RunningFor { get; set; } = "";

    [JsonPropertyName("Labels")] public string Labels { get; set; } = "";

    public string Name => Names.Split(',')[0].Trim();

    public bool IsRunning => State.Equals("running", StringComparison.OrdinalIgnoreCase);

    /// <summary>Labels als Wörterbuch (Docker liefert „a=b,c=d“).</summary>
    public Dictionary<string, string> LabelMap => DockerCli.ParseLabels(Labels);

    public bool IsManaged => LabelMap.ContainsKey(DockerCli.LabelManaged);

    public string? Version => LabelMap.TryGetValue(DockerCli.LabelVersion, out var v) ? v : null;

    /// <summary>Messwerte aus <c>docker stats</c> (nur laufende Container).</summary>
    public DockerStats? Stats { get; set; }
}

/// <summary>Zeile aus <c>docker stats --no-stream --format "{{json .}}"</c>.</summary>
public sealed class DockerStats
{
    [JsonPropertyName("ID")] public string Id { get; set; } = "";

    [JsonPropertyName("Name")] public string Name { get; set; } = "";

    [JsonPropertyName("CPUPerc")] public string CpuPercent { get; set; } = "";

    [JsonPropertyName("MemUsage")] public string MemoryUsage { get; set; } = "";

    [JsonPropertyName("MemPerc")] public string MemoryPercent { get; set; } = "";

    [JsonPropertyName("NetIO")] public string NetIo { get; set; } = "";
}

/// <summary>Zeile aus <c>docker images --format "{{json .}}"</c>.</summary>
public sealed class DockerImage
{
    [JsonPropertyName("ID")] public string Id { get; set; } = "";

    [JsonPropertyName("Repository")] public string Repository { get; set; } = "";

    [JsonPropertyName("Tag")] public string Tag { get; set; } = "";

    [JsonPropertyName("CreatedAt")] public string CreatedAt { get; set; } = "";

    [JsonPropertyName("CreatedSince")] public string CreatedSince { get; set; } = "";

    [JsonPropertyName("Size")] public string Size { get; set; } = "";

    public string Reference => Repository + ":" + Tag;

    /// <summary>Zeitpunkt aus „2026-10-14 16:39:01 +0200 CEST“.</summary>
    public DateTimeOffset? Created => DockerCli.ParseDockerTime(CreatedAt);
}

/// <summary>Ausgewählte Felder aus <c>docker info --format "{{json .}}"</c>.</summary>
public sealed class DockerInfo
{
    public string Name { get; set; } = "";

    public string ServerVersion { get; set; } = "";

    public string OperatingSystem { get; set; } = "";

    public string OSType { get; set; } = "";

    public string Architecture { get; set; } = "";

    [JsonPropertyName("NCPU")] public int Cpus { get; set; }

    public long MemTotal { get; set; }

    public int Containers { get; set; }

    public int ContainersRunning { get; set; }

    public int ContainersStopped { get; set; }

    public int Images { get; set; }

    /// <summary>Warnungen oder Fehler des Servers (z. B. „Cannot connect to the Docker daemon“).</summary>
    public List<string>? Warnings { get; set; }

    public List<string>? ServerErrors { get; set; }
}

public static class DockerJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };
}
