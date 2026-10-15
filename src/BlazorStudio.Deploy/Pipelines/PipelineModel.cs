using System.Text.Json.Serialization;

namespace BlazorStudio.Deploy.Pipelines;

public enum PipelineProvider { GitHub, AzureDevOps, GitLab }

public enum StepKind { Restore, Build, Test, Publish, DeployIis, Script }

/// <summary>Wann eine Stufe läuft.</summary>
public enum StageCondition { Always, Branch, Tag, PullRequest }

/// <summary>
/// Pipeline, wie sie der Pipeline-Builder bearbeitet. Wird als JSON unter .blazorstudio/pipelines/ gespeichert
/// und daraus YAML für GitHub Actions, Azure DevOps oder GitLab CI erzeugt.
/// </summary>
public sealed class PipelineDefinition
{
    public int SchemaVersion { get; set; } = 1;

    public string Name { get; set; } = "CI";

    public PipelineProvider Provider { get; set; } = PipelineProvider.GitHub;

    public PipelineTriggers Triggers { get; set; } = new();

    /// <summary>z. B. "10.0.x".</summary>
    public string DotnetVersion { get; set; } = "10.0.x";

    /// <summary>Umgebungsvariablen für alle Stufen (keine Geheimnisse!).</summary>
    public List<PipelineVariable> Variables { get; set; } = new();

    public List<PipelineStage> Stages { get; set; } = new();

    /// <summary>Datei im Repository, in die das YAML geschrieben wird (leer = Standard des Anbieters).</summary>
    public string? OutputPath { get; set; }

    [JsonIgnore]
    public string DefaultOutputPath => Provider switch
    {
        PipelineProvider.AzureDevOps => Slug(Name) == "ci" ? "azure-pipelines.yml" : $"azure-pipelines-{Slug(Name)}.yml",
        PipelineProvider.GitLab => ".gitlab-ci.yml",
        _ => $".github/workflows/{Slug(Name)}.yml",
    };

    [JsonIgnore]
    public string EffectiveOutputPath => string.IsNullOrWhiteSpace(OutputPath) ? DefaultOutputPath : OutputPath.Trim().Replace('\\', '/');

    [JsonIgnore]
    public bool UsesDeployScript => Stages.Any(s => s.Steps.Any(x => x.Enabled && x.Kind == StepKind.DeployIis));

    public static string Slug(string name)
    {
        var s = new string((name ?? "").ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        while (s.Contains("--", StringComparison.Ordinal)) s = s.Replace("--", "-", StringComparison.Ordinal);
        s = s.Trim('-');
        return s.Length == 0 ? "pipeline" : s;
    }
}

public sealed class PipelineTriggers
{
    /// <summary>Push auf diese Branches (leer = kein Push-Trigger).</summary>
    public List<string> Branches { get; set; } = new() { "main" };

    /// <summary>Tags, z. B. "v*" (leer = keine).</summary>
    public List<string> Tags { get; set; } = new();

    public bool PullRequests { get; set; } = true;

    /// <summary>Manuell starten (workflow_dispatch / „Run pipeline“ / „Run pipeline“).</summary>
    public bool Manual { get; set; } = true;

    /// <summary>Zeitplan als Cron (UTC), leer = keiner.</summary>
    public string? Schedule { get; set; }

    /// <summary>Nur bei Änderungen in diesen Pfaden (leer = immer).</summary>
    public List<string> Paths { get; set; } = new();
}

public sealed class PipelineVariable
{
    public string Name { get; set; } = "";

    public string Value { get; set; } = "";
}

public sealed class PipelineStage
{
    public string Id { get; set; } = "build";

    public string Name { get; set; } = "Build";

    /// <summary>"windows-latest", "ubuntu-latest" oder ein Label eines eigenen Agents.</summary>
    public string Runner { get; set; } = "windows-latest";

    public StageCondition Condition { get; set; } = StageCondition.Always;

    /// <summary>Branch bei <see cref="StageCondition.Branch"/>.</summary>
    public string? ConditionValue { get; set; }

    /// <summary>Umgebung (Freigaben/Schutzregeln des Anbieters), z. B. "Production".</summary>
    public string? Environment { get; set; }

    /// <summary>Nur nach manueller Freigabe (GitLab: when: manual; GitHub/Azure über die Umgebung).</summary>
    public bool ManualApproval { get; set; }

    public List<PipelineStep> Steps { get; set; } = new();
}

/// <summary>Ein Schritt. Die Einstellungen hängen von <see cref="Kind"/> ab (siehe <see cref="StepCatalog"/>).</summary>
public sealed class PipelineStep
{
    public StepKind Kind { get; set; }

    /// <summary>Eigener Anzeigename (leer = Standard).</summary>
    public string? Name { get; set; }

    public bool Enabled { get; set; } = true;

    public Dictionary<string, string> Settings { get; set; } = new();

    public string Get(string key) => Settings.TryGetValue(key, out var v) && v is not null ? v : StepCatalog.Default(Kind, key);

    public bool GetBool(string key) => Get(key).Equals("true", StringComparison.OrdinalIgnoreCase);

    public void Set(string key, string? value)
    {
        if (value is null) Settings.Remove(key);
        else Settings[key] = value;
    }

    public PipelineStep Clone() => new() { Kind = Kind, Name = Name, Enabled = Enabled, Settings = new(Settings) };
}

/// <summary>Beschreibung der Einstellungen eines Schritts (für das Formular im Builder).</summary>
public sealed record StepField(string Key, string Default, StepFieldType Type = StepFieldType.Text, string[]? Options = null);

public enum StepFieldType { Text, Bool, Choice, Multiline, Project, Servers, Secret }

public static class StepCatalog
{
    public static readonly IReadOnlyDictionary<StepKind, StepField[]> Fields = new Dictionary<StepKind, StepField[]>
    {
        [StepKind.Restore] = new[]
        {
            new StepField("project", "", StepFieldType.Project),
        },
        [StepKind.Build] = new[]
        {
            new StepField("project", "", StepFieldType.Project),
            new StepField("configuration", "Release", StepFieldType.Choice, new[] { "Release", "Debug" }),
            new StepField("args", ""),
        },
        [StepKind.Test] = new[]
        {
            new StepField("project", "", StepFieldType.Project),
            new StepField("configuration", "Release", StepFieldType.Choice, new[] { "Release", "Debug" }),
            new StepField("filter", ""),
            new StepField("coverage", "false", StepFieldType.Bool),
            new StepField("publishResults", "true", StepFieldType.Bool),
        },
        [StepKind.Publish] = new[]
        {
            new StepField("project", "", StepFieldType.Project),
            new StepField("configuration", "Release", StepFieldType.Choice, new[] { "Release", "Debug" }),
            new StepField("runtime", "", StepFieldType.Choice, new[] { "", "win-x64", "win-x86", "win-arm64", "linux-x64", "linux-arm64" }),
            new StepField("selfContained", "false", StepFieldType.Bool),
            new StepField("output", "publish"),
            new StepField("artifact", "app"),
        },
        [StepKind.DeployIis] = new[]
        {
            new StepField("servers", "", StepFieldType.Servers),
            new StepField("target", "iis", StepFieldType.Choice, new[] { "iis", "service" }),
            new StepField("site", ""),
            new StepField("appPath", "/"),
            new StepField("package", "app"),
            new StepField("version", "{run}"),
            new StepField("healthUrl", ""),
            new StepField("rollback", "true", StepFieldType.Bool),
            new StepField("createSite", "false", StepFieldType.Bool),
            new StepField("binding", ""),
            new StepField("pool", ""),
            new StepField("persist", ""),
            new StepField("exe", ""),
            new StepField("account", ""),
            new StepField("secret", "BLAZORSTUDIO_AGENT_KEY", StepFieldType.Secret),
            new StepField("thumbprint", ""),
        },
        [StepKind.Script] = new[]
        {
            new StepField("shell", "pwsh", StepFieldType.Choice, new[] { "pwsh", "bash", "cmd" }),
            new StepField("script", "", StepFieldType.Multiline),
        },
    };

    public static string Default(StepKind kind, string key) =>
        Fields.TryGetValue(kind, out var f) ? f.FirstOrDefault(x => x.Key == key)?.Default ?? "" : "";

    public static PipelineStep Create(StepKind kind, string? project = null)
    {
        var step = new PipelineStep { Kind = kind };
        if (project is not null && Fields[kind].Any(f => f.Type == StepFieldType.Project)) step.Settings["project"] = project;
        return step;
    }
}
