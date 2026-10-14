using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlazorStudio.Deploy.Pipelines;

/// <summary>Vorlagen zum Start im Pipeline-Builder.</summary>
public static class PipelineTemplates
{
    public sealed record Template(string Id, string Icon, Func<string?, string?, PipelineDefinition> Create);

    /// <param name="project">Projekt/Solution relativ zum Repository (für Build/Test).</param>
    /// <param name="webProject">Web-Projekt relativ zum Repository (für Publish).</param>
    public static IReadOnlyList<Template> All { get; } = new[]
    {
        new Template("ci", "hammer", (project, _) => new PipelineDefinition
        {
            Name = "CI",
            Stages = { BuildStage(project, null) },
        }),
        new Template("iis", "globe", (project, web) => new PipelineDefinition
        {
            Name = "Deploy",
            Triggers = new PipelineTriggers { Branches = new() { "main" }, PullRequests = true, Manual = true },
            Stages =
            {
                BuildStage(project, web ?? project),
                DeployStage("deploy", "Deploy", "Production", StageCondition.Branch, "main", approval: false),
            },
        }),
        new Template("stages", "layers", (project, web) => new PipelineDefinition
        {
            Name = "Release",
            Triggers = new PipelineTriggers { Branches = new() { "main" }, Tags = new() { "v*" }, PullRequests = true, Manual = true },
            Stages =
            {
                BuildStage(project, web ?? project),
                DeployStage("staging", "Staging", "Staging", StageCondition.Branch, "main", approval: false),
                DeployStage("production", "Produktion", "Production", StageCondition.Tag, null, approval: true),
            },
        }),
        new Template("empty", "square", (_, _) => new PipelineDefinition
        {
            Name = "Pipeline",
            Stages = { new PipelineStage { Id = "build", Name = "Build" } },
        }),
    };

    private static PipelineStage BuildStage(string? project, string? publish)
    {
        var stage = new PipelineStage
        {
            Id = "build",
            Name = "Build",
            Steps =
            {
                StepCatalog.Create(StepKind.Restore, project),
                StepCatalog.Create(StepKind.Build, project),
                StepCatalog.Create(StepKind.Test, project),
            },
        };
        if (publish is not null) stage.Steps.Add(StepCatalog.Create(StepKind.Publish, publish));
        return stage;
    }

    private static PipelineStage DeployStage(string id, string name, string environment, StageCondition condition, string? value, bool approval)
    {
        var deploy = StepCatalog.Create(StepKind.DeployIis);
        deploy.Settings["version"] = condition == StageCondition.Tag ? "{branch}" : "{date}.{run}";
        return new PipelineStage
        {
            Id = id,
            Name = name,
            Environment = environment,
            Condition = condition,
            ConditionValue = value,
            ManualApproval = approval,
            Steps = { deploy },
        };
    }
}

/// <summary>Speichern/Laden der Pipeline-Definitionen im Repository (.blazorstudio/pipelines/*.json).</summary>
public static class PipelineStore
{
    public const string Folder = ".blazorstudio/pipelines";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(PipelineDefinition p) => JsonSerializer.Serialize(p, Json);

    public static PipelineDefinition Deserialize(string json) =>
        JsonSerializer.Deserialize<PipelineDefinition>(json, Json) ?? throw new InvalidDataException("Leere Pipeline-Datei.");

    public static string DefinitionPath(string repoRoot, PipelineDefinition p) =>
        Path.Combine(repoRoot, Folder.Replace('/', Path.DirectorySeparatorChar), PipelineDefinition.Slug(p.Name) + ".json");

    public static List<(string Path, PipelineDefinition Pipeline)> LoadAll(string repoRoot)
    {
        var dir = Path.Combine(repoRoot, Folder.Replace('/', Path.DirectorySeparatorChar));
        var list = new List<(string, PipelineDefinition)>();
        if (!Directory.Exists(dir)) return list;
        foreach (var f in Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                list.Add((f, Deserialize(File.ReadAllText(f))));
            }
            catch (Exception e) when (e is JsonException or InvalidDataException or IOException)
            {
                // kaputte Datei überspringen
            }
        }
        return list;
    }

    /// <summary>
    /// Schreibt Definition (JSON), YAML und – falls ein IIS-Deployment vorkommt – das Deploy-Skript.
    /// previousPath: bisherige JSON-Datei (wird bei Umbenennung entfernt). Gibt die geschriebenen Dateien zurück.
    /// </summary>
    public static List<string> Save(string repoRoot, PipelineDefinition p, string? previousPath = null)
    {
        var written = new List<string>();
        var json = DefinitionPath(repoRoot, p);
        Directory.CreateDirectory(Path.GetDirectoryName(json)!);
        File.WriteAllText(json, Serialize(p));
        written.Add(json);
        if (previousPath is not null && !string.Equals(Path.GetFullPath(previousPath), Path.GetFullPath(json), StringComparison.OrdinalIgnoreCase) && File.Exists(previousPath))
            File.Delete(previousPath);

        var yaml = Path.GetFullPath(Path.Combine(repoRoot, p.EffectiveOutputPath));
        var root = Path.GetFullPath(repoRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!yaml.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Die YAML-Datei muss im Repository liegen.");
        Directory.CreateDirectory(Path.GetDirectoryName(yaml)!);
        File.WriteAllText(yaml, "# Erzeugt mit dem Pipeline-Builder von Blazor Studio (" + Folder + "/" + Path.GetFileName(json) + ").\n" +
                                "# Änderungen am besten dort vornehmen – beim nächsten Speichern wird diese Datei überschrieben.\n" +
                                PipelineGenerator.Generate(p));
        written.Add(yaml);

        if (p.UsesDeployScript)
        {
            var script = Path.Combine(repoRoot, PipelineGenerator.ScriptPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(script)!);
            File.WriteAllText(script, DeployScripts.DeployScript);
            written.Add(script);
        }
        return written;
    }
}
