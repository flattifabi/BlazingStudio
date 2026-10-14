using System.Text;

namespace BlazorStudio.Deploy.Pipelines;

/// <summary>Erzeugt aus einer <see cref="PipelineDefinition"/> das YAML des jeweiligen Anbieters.</summary>
public static class PipelineGenerator
{
    /// <summary>Ablageort des Deploy-Skripts im Repository (wird beim Speichern mitgeschrieben).</summary>
    public const string ScriptPath = ".blazorstudio/Deploy-BlazorStudio.ps1";

    public const string AgentKeyVariable = "BLAZORSTUDIO_AGENT_KEY";

    public static string Generate(PipelineDefinition p) => p.Provider switch
    {
        PipelineProvider.AzureDevOps => AzureDevOps(p),
        PipelineProvider.GitLab => GitLab(p),
        _ => GitHub(p),
    };

    // ==================================================================
    // Hinweise
    // ==================================================================

    /// <summary>Hinweise zu Lücken in der Pipeline (kein Fehler, aber vermutlich nicht gewollt).</summary>
    public static List<string> Validate(PipelineDefinition p)
    {
        var warnings = new List<string>();
        if (p.Stages.Count == 0) warnings.Add("Die Pipeline hat keine Stufe.");
        var published = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var stage in p.Stages)
        {
            var steps = stage.Steps.Where(s => s.Enabled).ToList();
            if (steps.Count == 0) warnings.Add($"Stufe „{stage.Name}“ hat keine aktiven Schritte.");
            foreach (var s in steps)
            {
                switch (s.Kind)
                {
                    case StepKind.Publish when s.Get("artifact").Length > 0:
                        published.Add(s.Get("artifact"));
                        break;
                    case StepKind.DeployIis:
                        if (s.Get("site").Trim().Length == 0) warnings.Add($"„{stage.Name}“: IIS-Deployment ohne Site-Namen.");
                        if (Servers(s).Count == 0) warnings.Add($"„{stage.Name}“: IIS-Deployment ohne Server.");
                        if (!published.Contains(s.Get("package")) && !steps.Any(x => x.Kind == StepKind.Publish))
                            warnings.Add($"„{stage.Name}“: Paket „{s.Get("package")}“ wird vorher nirgends veröffentlicht (Schritt „Publish“ mit diesem Artefaktnamen).");
                        if (Servers(s).Any(x => x.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
                            warnings.Add($"„{stage.Name}“: Server über HTTP – der API-Schlüssel geht unverschlüsselt übers Netz. Im internen Netz ok, sonst HTTPS verwenden.");
                        if (s.GetBool("createSite") && s.Get("binding").Trim().Length == 0)
                            warnings.Add($"„{stage.Name}“: „Site anlegen“ braucht eine Bindung, z. B. http://*:8080.");
                        break;
                    case StepKind.Script when s.Get("script").Trim().Length == 0:
                        warnings.Add($"„{stage.Name}“: Skript-Schritt ist leer.");
                        break;
                }
            }
        }
        if (p.Triggers.Branches.Count == 0 && p.Triggers.Tags.Count == 0 && !p.Triggers.PullRequests && !p.Triggers.Manual && string.IsNullOrWhiteSpace(p.Triggers.Schedule))
            warnings.Add("Kein Auslöser gewählt – die Pipeline startet nie.");
        return warnings;
    }

    // ==================================================================
    // GitHub Actions
    // ==================================================================

    private static string GitHub(PipelineDefinition p)
    {
        var y = new Yaml();
        y.Line($"name: {Q(p.Name)}");
        y.Line("");
        y.Line("on:");
        y.In(() =>
        {
            var t = p.Triggers;
            if (t.Branches.Count > 0 || t.Tags.Count > 0)
            {
                y.Line("push:");
                y.In(() =>
                {
                    if (t.Branches.Count > 0) y.Line($"branches: {List(t.Branches)}");
                    if (t.Tags.Count > 0) y.Line($"tags: {List(t.Tags)}");
                    if (t.Paths.Count > 0) y.Line($"paths: {List(t.Paths)}");
                });
            }
            if (t.PullRequests)
            {
                y.Line("pull_request:");
                y.In(() =>
                {
                    if (t.Branches.Count > 0) y.Line($"branches: {List(t.Branches)}");
                    if (t.Paths.Count > 0) y.Line($"paths: {List(t.Paths)}");
                });
            }
            if (!string.IsNullOrWhiteSpace(t.Schedule))
            {
                y.Line("schedule:");
                y.In(() => y.Line($"- cron: {Q(t.Schedule.Trim())}"));
            }
            if (t.Manual || (t.Branches.Count == 0 && t.Tags.Count == 0 && !t.PullRequests && string.IsNullOrWhiteSpace(t.Schedule)))
                y.Line("workflow_dispatch:");
        });
        y.Line("");
        y.Line("permissions:");
        y.In(() => y.Line("contents: read"));
        y.Line("");
        Env(y, p, "env:", v => Q(v));
        y.Line("");
        y.Line("jobs:");
        y.In(() =>
        {
            string? previous = null;
            foreach (var stage in p.Stages)
            {
                var id = JobId(stage.Id);
                y.Line($"{id}:");
                y.In(() =>
                {
                    y.Line($"name: {Q(stage.Name)}");
                    y.Line($"runs-on: {Q(stage.Runner)}");
                    if (previous is not null) y.Line($"needs: {previous}");
                    var cond = stage.Condition switch
                    {
                        StageCondition.Branch => $"github.ref == 'refs/heads/{Branch(stage)}'",
                        StageCondition.Tag => "startsWith(github.ref, 'refs/tags/')",
                        StageCondition.PullRequest => "github.event_name == 'pull_request'",
                        _ => null,
                    };
                    // Übersprungene Vorstufen (z. B. Staging bei einem Tag) dürfen spätere Stufen nicht blockieren
                    if (previous is not null) y.Line("if: ${{ !failure() && !cancelled()" + (cond is null ? "" : " && " + cond) + " }}");
                    else if (cond is not null) y.Line($"if: {cond}");
                    var env = EnvironmentName(stage);
                    if (env is not null)
                    {
                        if (stage.ManualApproval) y.Line("# Freigabe: in GitHub unter Settings → Environments → „" + env + "“ Required reviewers eintragen");
                        y.Line($"environment: {Q(env)}");
                    }
                    y.Line("steps:");
                    y.In(() => GitHubSteps(y, p, stage));
                });
                previous = id;
                y.Line("");
            }
        });
        return y.ToString();
    }

    private static void GitHubSteps(Yaml y, PipelineDefinition p, PipelineStage stage)
    {
        var steps = stage.Steps.Where(s => s.Enabled).ToList();
        y.Line("- uses: actions/checkout@v4");
        if (steps.Any(UsesDotnet))
        {
            y.Line("- uses: actions/setup-dotnet@v4");
            y.In(() =>
            {
                y.Line("with:");
                y.In(() => y.Line($"dotnet-version: {Q(p.DotnetVersion)}"));
            });
        }
        foreach (var artifact in NeededArtifacts(stage))
        {
            y.Line("- uses: actions/download-artifact@v4");
            y.In(() =>
            {
                y.Line("with:");
                y.In(() =>
                {
                    y.Line($"name: {Q(artifact)}");
                    y.Line($"path: {Q("artifacts/" + artifact)}");
                });
            });
        }
        for (var i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            var before = steps.Take(i).ToList();
            switch (s.Kind)
            {
                case StepKind.Script:
                    RunStep(y, Title(s), s.Get("shell") switch { "bash" => "bash", "cmd" => "cmd", _ => "pwsh" }, s.Get("script"));
                    break;
                case StepKind.DeployIis:
                    y.Line($"- name: {Q(Title(s))}");
                    y.In(() =>
                    {
                        y.Line("shell: pwsh");
                        y.Line("env:");
                        y.In(() => y.Line($"{AgentKeyVariable}: ${{{{ secrets.{Secret(s)} }}}}"));
                        Block(y, "run", DeployCommand(p, stage, s, PipelineProvider.GitHub));
                    });
                    break;
                default:
                    RunStep(y, Title(s), "pwsh", DotnetCommand(s, before));
                    break;
            }
            if (s.Kind == StepKind.Test && s.GetBool("publishResults"))
            {
                y.Line("- uses: actions/upload-artifact@v4");
                y.In(() =>
                {
                    y.Line("if: always()");
                    y.Line("with:");
                    y.In(() =>
                    {
                        y.Line($"name: {Q("test-results-" + JobId(stage.Id))}");
                        y.Line("path: TestResults");
                    });
                });
            }
            if (s.Kind == StepKind.Publish && s.Get("artifact").Length > 0)
            {
                y.Line("- uses: actions/upload-artifact@v4");
                y.In(() =>
                {
                    y.Line("with:");
                    y.In(() =>
                    {
                        y.Line($"name: {Q(s.Get("artifact"))}");
                        y.Line($"path: {Q(s.Get("output"))}");
                    });
                });
            }
        }
    }

    private static void RunStep(Yaml y, string name, string shell, string command)
    {
        y.Line($"- name: {Q(name)}");
        y.In(() =>
        {
            y.Line($"shell: {shell}");
            Block(y, "run", command);
        });
    }

    // ==================================================================
    // Azure DevOps
    // ==================================================================

    private static string AzureDevOps(PipelineDefinition p)
    {
        var y = new Yaml();
        y.Line($"# {p.Name}");
        y.Line($"name: $(Date:yyyyMMdd)$(Rev:.r)");
        y.Line("");
        var t = p.Triggers;
        if (t.Branches.Count > 0 || t.Tags.Count > 0)
        {
            y.Line("trigger:");
            y.In(() =>
            {
                if (t.Branches.Count > 0) Include(y, "branches", t.Branches);
                if (t.Tags.Count > 0) Include(y, "tags", t.Tags);
                if (t.Paths.Count > 0) Include(y, "paths", t.Paths);
            });
        }
        else
        {
            y.Line("trigger: none");
        }
        if (t.PullRequests)
        {
            y.Line("pr:");
            y.In(() =>
            {
                Include(y, "branches", t.Branches.Count > 0 ? t.Branches : new List<string> { "*" });
                if (t.Paths.Count > 0) Include(y, "paths", t.Paths);
            });
        }
        else
        {
            y.Line("pr: none");
        }
        if (!string.IsNullOrWhiteSpace(t.Schedule))
        {
            y.Line("schedules:");
            y.In(() =>
            {
                y.Line($"- cron: {Q(t.Schedule.Trim())}");
                y.In(() =>
                {
                    y.Line("displayName: Zeitplan");
                    Include(y, "branches", t.Branches.Count > 0 ? t.Branches : new List<string> { "main" });
                    y.Line("always: true");
                });
            });
        }
        y.Line("");
        Env(y, p, "variables:", v => Q(v));
        y.Line("");
        y.Line("stages:");
        y.In(() =>
        {
            string? previous = null;
            foreach (var stage in p.Stages)
            {
                var id = JobId(stage.Id);
                y.Line($"- stage: {id}");
                y.In(() =>
                {
                    y.Line($"displayName: {Q(stage.Name)}");
                    if (previous is not null) y.Line($"dependsOn: {previous}");
                    var cond = stage.Condition switch
                    {
                        StageCondition.Branch => $"eq(variables['Build.SourceBranch'], 'refs/heads/{Branch(stage)}')",
                        StageCondition.Tag => "startsWith(variables['Build.SourceBranch'], 'refs/tags/')",
                        StageCondition.PullRequest => "eq(variables['Build.Reason'], 'PullRequest')",
                        _ => null,
                    };
                    // Übersprungene Vorstufen dürfen spätere Stufen nicht blockieren
                    if (previous is not null) y.Line("condition: and(not(failed()), not(canceled())" + (cond is null ? "" : ", " + cond) + ")");
                    else if (cond is not null) y.Line($"condition: {cond}");
                    y.Line("jobs:");
                    y.In(() =>
                    {
                        var env = EnvironmentName(stage);
                        if (env is not null)
                        {
                            y.Line($"- deployment: {id}");
                            y.In(() =>
                            {
                                y.Line($"displayName: {Q(stage.Name)}");
                                if (stage.ManualApproval) y.Line("# Freigabe: in Azure DevOps unter Pipelines → Environments → „" + env + "“ → Approvals and checks");
                                y.Line($"environment: {Q(env)}");
                                AzurePool(y, stage);
                                y.Line("strategy:");
                                y.In(() =>
                                {
                                    y.Line("runOnce:");
                                    y.In(() =>
                                    {
                                        y.Line("deploy:");
                                        y.In(() =>
                                        {
                                            y.Line("steps:");
                                            y.In(() => AzureSteps(y, p, stage));
                                        });
                                    });
                                });
                            });
                        }
                        else
                        {
                            y.Line($"- job: {id}");
                            y.In(() =>
                            {
                                y.Line($"displayName: {Q(stage.Name)}");
                                AzurePool(y, stage);
                                y.Line("steps:");
                                y.In(() => AzureSteps(y, p, stage));
                            });
                        }
                    });
                });
                previous = id;
                y.Line("");
            }
        });
        return y.ToString();
    }

    private static void AzurePool(Yaml y, PipelineStage stage)
    {
        y.Line("pool:");
        y.In(() =>
        {
            if (stage.Runner.EndsWith("-latest", StringComparison.OrdinalIgnoreCase) || stage.Runner.Contains("-20", StringComparison.Ordinal))
                y.Line($"vmImage: {Q(stage.Runner)}");
            else
                y.Line($"name: {Q(stage.Runner)}");
        });
    }

    private static void AzureSteps(Yaml y, PipelineDefinition p, PipelineStage stage)
    {
        var steps = stage.Steps.Where(s => s.Enabled).ToList();
        y.Line("- checkout: self");
        if (steps.Any(UsesDotnet))
        {
            y.Line("- task: UseDotNet@2");
            y.In(() =>
            {
                y.Line("displayName: .NET SDK");
                y.Line("inputs:");
                y.In(() =>
                {
                    y.Line("packageType: sdk");
                    y.Line($"version: {Q(p.DotnetVersion)}");
                });
            });
        }
        foreach (var artifact in NeededArtifacts(stage))
        {
            y.Line("- download: current");
            y.In(() => y.Line($"artifact: {Q(artifact)}"));
        }
        for (var i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            var before = steps.Take(i).ToList();
            switch (s.Kind)
            {
                case StepKind.Script:
                    var key = s.Get("shell") switch { "bash" => "bash", "cmd" => "script", _ => "pwsh" };
                    Block(y, "- " + key, s.Get("script"));
                    y.In(() => y.Line($"displayName: {Q(Title(s))}"), 2);
                    break;
                case StepKind.DeployIis:
                    Block(y, "- pwsh", DeployCommand(p, stage, s, PipelineProvider.AzureDevOps));
                    y.In(() =>
                    {
                        y.Line($"displayName: {Q(Title(s))}");
                        y.Line("env:");
                        y.In(() => y.Line($"{AgentKeyVariable}: $({Secret(s)})"));
                    }, 2);
                    break;
                default:
                    Block(y, "- pwsh", DotnetCommand(s, before));
                    y.In(() => y.Line($"displayName: {Q(Title(s))}"), 2);
                    break;
            }
            if (s.Kind == StepKind.Test && s.GetBool("publishResults"))
            {
                y.Line("- task: PublishTestResults@2");
                y.In(() =>
                {
                    y.Line("displayName: Testergebnisse");
                    y.Line("condition: succeededOrFailed()");
                    y.Line("inputs:");
                    y.In(() =>
                    {
                        y.Line("testResultsFormat: VSTest");
                        y.Line("testResultsFiles: '**/*.trx'");
                        y.Line("searchFolder: TestResults");
                    });
                });
            }
            if (s.Kind == StepKind.Publish && s.Get("artifact").Length > 0)
            {
                y.Line($"- publish: {Q(s.Get("output"))}");
                y.In(() => y.Line($"artifact: {Q(s.Get("artifact"))}"));
            }
        }
    }

    private static void Include(Yaml y, string key, List<string> values)
    {
        y.Line($"{key}:");
        y.In(() => y.Line($"include: {List(values)}"));
    }

    // ==================================================================
    // GitLab CI
    // ==================================================================

    private static string GitLab(PipelineDefinition p)
    {
        var y = new Yaml();
        y.Line($"# {p.Name}");
        var stageIds = p.Stages.Select(s => JobId(s.Id)).Distinct().ToList();
        y.Line($"stages: [{string.Join(", ", stageIds)}]");
        y.Line("");
        Env(y, p, "variables:", v => Q(v));
        y.Line("");
        var t = p.Triggers;
        y.Line("workflow:");
        y.In(() =>
        {
            y.Line("rules:");
            y.In(() =>
            {
                if (t.PullRequests) y.Line("- if: $CI_PIPELINE_SOURCE == \"merge_request_event\"");
                foreach (var b in t.Branches) y.Line($"- if: $CI_COMMIT_BRANCH {GitLabMatch(b)}");
                foreach (var tag in t.Tags) y.Line($"- if: $CI_COMMIT_TAG {GitLabMatch(tag)}");
                if (t.Manual) y.Line("- if: $CI_PIPELINE_SOURCE == \"web\"");
                if (!string.IsNullOrWhiteSpace(t.Schedule))
                {
                    y.Line($"# Zeitplan ({t.Schedule.Trim()}) in GitLab unter Build → Pipeline schedules anlegen");
                    y.Line("- if: $CI_PIPELINE_SOURCE == \"schedule\"");
                }
            });
        });
        y.Line("");
        var versionMajor = p.DotnetVersion.Replace(".x", "", StringComparison.Ordinal);
        foreach (var stage in p.Stages)
        {
            var id = JobId(stage.Id);
            var windows = stage.Runner.Contains("windows", StringComparison.OrdinalIgnoreCase);
            y.Line($"{id}:");
            y.In(() =>
            {
                y.Line($"stage: {id}");
                if (windows)
                {
                    y.Line("# Windows-Runner mit installiertem .NET SDK und PowerShell");
                    y.Line($"tags: [{(stage.Runner.Equals("windows-latest", StringComparison.OrdinalIgnoreCase) ? "windows" : stage.Runner)}]");
                }
                else
                {
                    y.Line($"image: mcr.microsoft.com/dotnet/sdk:{versionMajor}");
                    if (!stage.Runner.EndsWith("-latest", StringComparison.OrdinalIgnoreCase)) y.Line($"tags: [{stage.Runner}]");
                }
                var rule = stage.Condition switch
                {
                    StageCondition.Branch => $"$CI_COMMIT_BRANCH == \"{Branch(stage)}\"",
                    StageCondition.Tag => "$CI_COMMIT_TAG",
                    StageCondition.PullRequest => "$CI_PIPELINE_SOURCE == \"merge_request_event\"",
                    _ => null,
                };
                if (rule is not null || stage.ManualApproval)
                {
                    y.Line("rules:");
                    y.In(() =>
                    {
                        y.Line($"- if: {rule ?? "$CI_PIPELINE_SOURCE"}");
                        if (stage.ManualApproval) y.In(() => y.Line("when: manual"), 2);
                    });
                }
                if (EnvironmentName(stage) is { } env)
                {
                    y.Line("environment:");
                    y.In(() => y.Line($"name: {Q(env)}"));
                }
                var steps = stage.Steps.Where(s => s.Enabled).ToList();
                y.Line("script:");
                y.In(() =>
                {
                    for (var i = 0; i < steps.Count; i++)
                    {
                        var s = steps[i];
                        var cmd = s.Kind switch
                        {
                            StepKind.Script => s.Get("script"),
                            StepKind.DeployIis => windows ? DeployCommand(p, stage, s, PipelineProvider.GitLab) : DeployCommandBash(p, stage, s),
                            _ => DotnetCommand(s, steps.Take(i).ToList()),
                        };
                        y.Line($"# {Title(s)}");
                        if (cmd.Contains('\n')) Block(y, "-", cmd);
                        else y.Line("- " + QIfNeeded(cmd));
                    }
                });
                if (steps.Any(s => s.Kind == StepKind.DeployIis))
                {
                    y.Line("# API-Schlüssel als geschützte, maskierte CI/CD-Variable " + string.Join(", ", steps.Where(s => s.Kind == StepKind.DeployIis).Select(Secret).Distinct()) + " anlegen");
                }
                var outputs = steps.Where(s => s.Kind == StepKind.Publish && s.Get("artifact").Length > 0).Select(s => s.Get("output").TrimEnd('/') + "/").ToList();
                var tests = steps.Any(s => s.Kind == StepKind.Test && s.GetBool("publishResults"));
                if (outputs.Count > 0 || tests)
                {
                    y.Line("artifacts:");
                    y.In(() =>
                    {
                        if (tests) y.Line("when: always");
                        y.Line($"paths: {List(outputs.Concat(tests ? new[] { "TestResults/" } : Array.Empty<string>()).ToList())}");
                        y.Line("expire_in: 1 week");
                    });
                }
            });
            y.Line("");
        }
        return y.ToString();
    }

    private static string GitLabMatch(string pattern) =>
        pattern.Contains('*')
            ? "=~ /^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal).Replace("/", "\\/", StringComparison.Ordinal) + "$/"
            : $"== \"{pattern}\"";

    // ==================================================================
    // Befehle
    // ==================================================================

    private static bool UsesDotnet(PipelineStep s) => s.Kind is StepKind.Restore or StepKind.Build or StepKind.Test or StepKind.Publish;

    public static string Title(PipelineStep s) => !string.IsNullOrWhiteSpace(s.Name) ? s.Name.Trim() : s.Kind switch
    {
        StepKind.Restore => "Restore",
        StepKind.Build => "Build",
        StepKind.Test => "Test",
        StepKind.Publish => "Publish",
        StepKind.DeployIis => "Deploy IIS" + (s.Get("site").Length > 0 ? " – " + s.Get("site") : ""),
        _ => "Skript",
    };

    /// <summary>dotnet-Aufruf eines Schritts. before = vorherige Schritte derselben Stufe (für --no-restore/--no-build).</summary>
    public static string DotnetCommand(PipelineStep s, IReadOnlyList<PipelineStep> before)
    {
        var project = s.Get("project").Trim();
        var proj = project.Length == 0 ? "" : " " + Arg(project);
        var restored = before.Any(b => b.Kind == StepKind.Restore);
        var built = before.Any(b => b.Kind == StepKind.Build && b.Get("configuration") == s.Get("configuration"));
        var sb = new StringBuilder();
        switch (s.Kind)
        {
            case StepKind.Restore:
                sb.Append("dotnet restore").Append(proj);
                break;
            case StepKind.Build:
                sb.Append("dotnet build").Append(proj).Append(" -c ").Append(s.Get("configuration"));
                if (restored) sb.Append(" --no-restore");
                if (s.Get("args").Trim().Length > 0) sb.Append(' ').Append(s.Get("args").Trim());
                break;
            case StepKind.Test:
                sb.Append("dotnet test").Append(proj).Append(" -c ").Append(s.Get("configuration"));
                if (built) sb.Append(" --no-build");
                else if (restored) sb.Append(" --no-restore");
                if (s.Get("filter").Trim().Length > 0) sb.Append(" --filter ").Append(Arg(s.Get("filter").Trim(), force: true));
                if (s.GetBool("coverage")) sb.Append(" --collect \"XPlat Code Coverage\"");
                if (s.GetBool("publishResults")) sb.Append(" --logger trx --results-directory TestResults");
                break;
            case StepKind.Publish:
                sb.Append("dotnet publish").Append(proj).Append(" -c ").Append(s.Get("configuration"));
                if (s.Get("runtime").Length > 0)
                    sb.Append(" -r ").Append(s.Get("runtime")).Append(" --self-contained ").Append(s.GetBool("selfContained") ? "true" : "false");
                else if (built) sb.Append(" --no-build");
                else if (restored) sb.Append(" --no-restore");
                sb.Append(" -o ").Append(Arg(s.Get("output")));
                break;
        }
        return sb.ToString();
    }

    public static List<string> Servers(PipelineStep s) =>
        s.Get("servers").Split(new[] { ',', ';', '\n', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static string Secret(PipelineStep s) => s.Get("secret").Trim() is { Length: > 0 } n ? n : AgentKeyVariable;

    /// <summary>Wo das Paket im Deploy-Schritt liegt: im selben Job veröffentlicht oder als Artefakt geladen.</summary>
    private static string PackagePath(PipelineDefinition p, PipelineStage stage, PipelineStep deploy, PipelineProvider provider)
    {
        var name = deploy.Get("package");
        var local = stage.Steps.Where(s => s.Enabled && s.Kind == StepKind.Publish)
            .FirstOrDefault(s => s.Get("artifact").Equals(name, StringComparison.OrdinalIgnoreCase) || name.Length == 0);
        if (local is not null) return local.Get("output");
        return provider switch
        {
            PipelineProvider.AzureDevOps => $"$(Pipeline.Workspace)/{name}",
            // GitLab übernimmt Artefakte früherer Stufen am ursprünglichen Ort
            PipelineProvider.GitLab => p.Stages.SelectMany(x => x.Steps).FirstOrDefault(s => s.Kind == StepKind.Publish && s.Get("artifact").Equals(name, StringComparison.OrdinalIgnoreCase))?.Get("output") ?? name,
            _ => $"artifacts/{name}",
        };
    }

    /// <summary>Artefakte, die eine Stufe aus früheren Stufen braucht (GitHub/Azure laden sie explizit).</summary>
    private static List<string> NeededArtifacts(PipelineStage stage)
    {
        var local = stage.Steps.Where(s => s.Enabled && s.Kind == StepKind.Publish).Select(s => s.Get("artifact")).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return stage.Steps.Where(s => s.Enabled && s.Kind == StepKind.DeployIis)
            .Select(s => s.Get("package"))
            .Where(n => n.Length > 0 && !local.Contains(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>PowerShell-Aufruf des Deploy-Skripts (GitHub, Azure DevOps, GitLab auf Windows).</summary>
    public static string DeployCommand(PipelineDefinition p, PipelineStage stage, PipelineStep s, PipelineProvider provider)
    {
        var sb = new StringBuilder();
        sb.Append("$version = ").Append(VersionExpression(s.Get("version"), provider)).Append('\n');
        sb.Append("& ./").Append(ScriptPath).Append(" `\n");
        sb.Append("  -Server ").Append(string.Join(",", Servers(s).Select(x => PsQ(x)))).Append(" `\n");
        sb.Append("  -Package ").Append(PsQ(PackagePath(p, stage, s, provider), allowMacro: provider == PipelineProvider.AzureDevOps)).Append(" `\n");
        sb.Append("  -Site ").Append(PsQ(s.Get("site").Trim())).Append(" `\n");
        var app = DeployRequest.NormalizeAppPath(s.Get("appPath"));
        if (app != "/") sb.Append("  -AppPath ").Append(PsQ(app)).Append(" `\n");
        if (s.Get("healthUrl").Trim().Length > 0) sb.Append("  -HealthUrl ").Append(PsQ(s.Get("healthUrl").Trim())).Append(" `\n");
        if (!s.GetBool("rollback")) sb.Append("  -NoRollback `\n");
        if (s.GetBool("createSite"))
        {
            sb.Append("  -CreateSite `\n");
            if (s.Get("binding").Trim().Length > 0) sb.Append("  -Binding ").Append(PsQ(s.Get("binding").Trim())).Append(" `\n");
        }
        if (s.Get("pool").Trim().Length > 0) sb.Append("  -Pool ").Append(PsQ(s.Get("pool").Trim())).Append(" `\n");
        if (s.Get("thumbprint").Trim().Length > 0) sb.Append("  -CertThumbprint ").Append(PsQ(s.Get("thumbprint").Trim())).Append(" `\n");
        sb.Append("  -Version $version");
        return sb.ToString();
    }

    /// <summary>Deploy-Aufruf für GitLab-Linux-Runner (bash ruft pwsh auf).</summary>
    private static string DeployCommandBash(PipelineDefinition p, PipelineStage stage, PipelineStep s)
    {
        var args = new List<string>
        {
            "-Server " + string.Join(",", Servers(s)),
            "-Package " + ShQ(PackagePath(p, stage, s, PipelineProvider.GitLab)),
            "-Site " + ShQ(s.Get("site").Trim()),
        };
        var app = DeployRequest.NormalizeAppPath(s.Get("appPath"));
        if (app != "/") args.Add("-AppPath " + ShQ(app));
        if (s.Get("healthUrl").Trim().Length > 0) args.Add("-HealthUrl " + ShQ(s.Get("healthUrl").Trim()));
        if (!s.GetBool("rollback")) args.Add("-NoRollback");
        if (s.GetBool("createSite"))
        {
            args.Add("-CreateSite");
            if (s.Get("binding").Trim().Length > 0) args.Add("-Binding " + ShQ(s.Get("binding").Trim()));
        }
        if (s.Get("pool").Trim().Length > 0) args.Add("-Pool " + ShQ(s.Get("pool").Trim()));
        if (s.Get("thumbprint").Trim().Length > 0) args.Add("-CertThumbprint " + ShQ(s.Get("thumbprint").Trim()));
        var secret = Secret(s);
        var sb = new StringBuilder();
        if (secret != AgentKeyVariable) sb.Append($"export {AgentKeyVariable}=\"${secret}\"\n");
        sb.Append("pwsh -NoProfile -File ").Append(ScriptPath).Append(" \\\n");
        foreach (var a in args) sb.Append("  ").Append(a).Append(" \\\n");
        sb.Append("  -Version \"").Append(VersionBash(s.Get("version"))).Append('"');
        return sb.ToString();
    }

    /// <summary>
    /// Versionsvorlage als PowerShell-Ausdruck. Platzhalter: {run} Build-Nummer, {sha} Commit (7 Zeichen),
    /// {branch} Branch/Tag, {date} yyyyMMdd, {time} HHmm.
    /// </summary>
    public static string VersionExpression(string template, PipelineProvider provider)
    {
        var (run, sha, branch) = provider switch
        {
            PipelineProvider.AzureDevOps => ("BUILD_BUILDID", "BUILD_SOURCEVERSION", "BUILD_SOURCEBRANCHNAME"),
            PipelineProvider.GitLab => ("CI_PIPELINE_IID", "CI_COMMIT_SHA", "CI_COMMIT_REF_SLUG"),
            _ => ("GITHUB_RUN_NUMBER", "GITHUB_SHA", "GITHUB_REF_NAME"),
        };
        var t = string.IsNullOrWhiteSpace(template) ? "{run}" : template.Trim();
        var sb = new StringBuilder("\"");
        for (var i = 0; i < t.Length; i++)
        {
            if (t[i] == '{')
            {
                var end = t.IndexOf('}', i);
                if (end > i)
                {
                    var token = t[(i + 1)..end].ToLowerInvariant();
                    var expr = token switch
                    {
                        "run" => $"$($env:{run})",
                        "sha" => $"$($env:{sha}.Substring(0, 7))",
                        "branch" => $"$($env:{branch} -replace '[^A-Za-z0-9._-]', '-')",
                        "date" => "$(Get-Date -Format yyyyMMdd)",
                        "time" => "$(Get-Date -Format HHmm)",
                        _ => null,
                    };
                    if (expr is not null)
                    {
                        sb.Append(expr);
                        i = end;
                        continue;
                    }
                }
            }
            var c = t[i];
            if (c is '"' or '$' or '`') sb.Append('`');
            sb.Append(c);
        }
        return sb.Append('"').ToString();
    }

    private static string VersionBash(string template)
    {
        var t = string.IsNullOrWhiteSpace(template) ? "{run}" : template.Trim();
        return t.Replace("{run}", "${CI_PIPELINE_IID}", StringComparison.OrdinalIgnoreCase)
                .Replace("{sha}", "${CI_COMMIT_SHORT_SHA}", StringComparison.OrdinalIgnoreCase)
                .Replace("{branch}", "${CI_COMMIT_REF_SLUG}", StringComparison.OrdinalIgnoreCase)
                .Replace("{date}", "$(date +%Y%m%d)", StringComparison.OrdinalIgnoreCase)
                .Replace("{time}", "$(date +%H%M)", StringComparison.OrdinalIgnoreCase);
    }

    // ==================================================================
    // Hilfen
    // ==================================================================

    private static string? EnvironmentName(PipelineStage stage) =>
        !string.IsNullOrWhiteSpace(stage.Environment) ? stage.Environment.Trim() : stage.ManualApproval ? stage.Name : null;

    private static string Branch(PipelineStage stage) => string.IsNullOrWhiteSpace(stage.ConditionValue) ? "main" : stage.ConditionValue.Trim();

    public static string JobId(string id)
    {
        var s = new string((id ?? "").ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray()).Trim('_');
        if (s.Length == 0) s = "stage";
        return char.IsDigit(s[0]) ? "s_" + s : s;
    }

    private static void Env(Yaml y, PipelineDefinition p, string header, Func<string, string> quote)
    {
        y.Line(header);
        y.In(() =>
        {
            y.Line("DOTNET_NOLOGO: 'true'");
            y.Line("DOTNET_CLI_TELEMETRY_OPTOUT: 'true'");
            foreach (var v in p.Variables.Where(v => !string.IsNullOrWhiteSpace(v.Name)))
                y.Line($"{v.Name.Trim()}: {quote(v.Value)}");
        });
    }

    /// <summary>Mehrzeiliger Block „key: |“.</summary>
    private static void Block(Yaml y, string key, string text)
    {
        var lines = text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        if (lines.Length == 1 && !NeedsQuote(lines[0]))
        {
            y.Line($"{key}: {lines[0]}");
            return;
        }
        if (key == "-")
        {
            y.Line("- |");
        }
        else
        {
            y.Line($"{key}: |");
        }
        // Inhalt muss tiefer stehen als der Schlüssel: bei „- key: |“ liegt der Schlüssel zwei Spalten hinter dem Strich
        y.In(() =>
        {
            foreach (var l in lines) y.Line(l);
        }, key.StartsWith("- ", StringComparison.Ordinal) ? 4 : 2);
    }

    private static bool NeedsQuote(string s) =>
        s.Length == 0 || s.Contains(": ", StringComparison.Ordinal) || s.Contains(" #", StringComparison.Ordinal) ||
        "!&*{}[]|>'\"%@`,?#".Contains(s[0]) || s.EndsWith(':') || s.Trim() != s;

    private static string QIfNeeded(string s) => NeedsQuote(s) ? Q(s) : s;

    /// <summary>YAML in einfachen Anführungszeichen.</summary>
    public static string Q(string s) => "'" + (s ?? "").Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string List(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(Q)) + "]";

    private static string PsQ(string s, bool allowMacro = false) =>
        allowMacro && s.Contains("$(", StringComparison.Ordinal) ? "\"" + s.Replace("\"", "`\"", StringComparison.Ordinal) + "\"" : "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string ShQ(string s) => "'" + s.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static string Arg(string s, bool force = false) => force || s.Contains(' ') ? "\"" + s.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"" : s;

    private sealed class Yaml
    {
        private readonly StringBuilder _sb = new();
        private int _indent;

        public void Line(string text)
        {
            if (text.Length == 0)
            {
                _sb.Append('\n');
                return;
            }
            _sb.Append(' ', _indent).Append(text).Append('\n');
        }

        public void In(Action body, int by = 2)
        {
            _indent += by;
            try { body(); }
            finally { _indent -= by; }
        }

        public override string ToString()
        {
            // Mehrfache Leerzeilen am Ende vermeiden
            return _sb.ToString().TrimEnd('\n') + "\n";
        }
    }
}
