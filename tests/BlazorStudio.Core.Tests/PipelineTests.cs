using BlazorStudio.Deploy.Pipelines;

namespace BlazorStudio.Core.Tests;

public class PipelineTests
{
    private static PipelineDefinition Release(PipelineProvider provider)
    {
        var p = PipelineTemplates.All.Single(t => t.Id == "stages").Create("Shop.sln", "src/Shop/Shop.csproj");
        p.Provider = provider;
        foreach (var s in p.Stages.SelectMany(x => x.Steps).Where(x => x.Kind == StepKind.DeployIis))
        {
            s.Settings["servers"] = "https://web01:8790, https://web02:8790";
            s.Settings["site"] = "Shop";
        }
        return p;
    }

    [Fact]
    public void GitHub_release_pipeline_has_artifacts_conditions_and_secret()
    {
        var yaml = PipelineGenerator.Generate(Release(PipelineProvider.GitHub));
        Assert.Contains("branches: ['main']", yaml);
        Assert.Contains("tags: ['v*']", yaml);
        Assert.Contains("dotnet publish src/Shop/Shop.csproj -c Release --no-build -o publish", yaml);
        Assert.Contains("uses: actions/upload-artifact@v4", yaml);
        Assert.Contains("uses: actions/download-artifact@v4", yaml);
        // Übersprungene Staging-Stufe darf Produktion bei Tags nicht blockieren
        Assert.Contains("if: ${{ !failure() && !cancelled() && startsWith(github.ref, 'refs/tags/') }}", yaml);
        Assert.Contains("BLAZORSTUDIO_AGENT_KEY: ${{ secrets.BLAZORSTUDIO_AGENT_KEY }}", yaml);
        Assert.Contains("-Server 'https://web01:8790','https://web02:8790'", yaml);
        Assert.Contains("environment: 'Production'", yaml);
    }

    [Fact]
    public void Azure_uses_deployment_jobs_and_pipeline_workspace()
    {
        var yaml = PipelineGenerator.Generate(Release(PipelineProvider.AzureDevOps));
        Assert.Contains("- deployment: production", yaml);
        Assert.Contains("environment: 'Production'", yaml);
        Assert.Contains("-Package \"$(Pipeline.Workspace)/app\"", yaml);
        Assert.Contains("BLAZORSTUDIO_AGENT_KEY: $(BLAZORSTUDIO_AGENT_KEY)", yaml);
        Assert.Contains("condition: and(not(failed()), not(canceled()), startsWith(variables['Build.SourceBranch'], 'refs/tags/'))", yaml);
    }

    [Fact]
    public void GitLab_uses_stages_rules_and_manual_approval()
    {
        var yaml = PipelineGenerator.Generate(Release(PipelineProvider.GitLab));
        Assert.Contains("stages: [build, staging, production]", yaml);
        Assert.Contains("- if: $CI_COMMIT_TAG =~ /^v.*$/", yaml);
        Assert.Contains("when: manual", yaml);
        Assert.Contains("paths: ['publish/', 'TestResults/']", yaml);
    }

    [Fact]
    public void Version_placeholders_become_powershell_expressions()
    {
        Assert.Equal("\"1.0.$($env:GITHUB_RUN_NUMBER)\"", PipelineGenerator.VersionExpression("1.0.{run}", PipelineProvider.GitHub));
        Assert.Equal("\"$($env:BUILD_SOURCEVERSION.Substring(0, 7))\"", PipelineGenerator.VersionExpression("{sha}", PipelineProvider.AzureDevOps));
        // Literale $ und " werden maskiert
        Assert.Equal("\"a`$b`\"c\"", PipelineGenerator.VersionExpression("a$b\"c", PipelineProvider.GitHub));
    }

    [Fact]
    public void Build_and_test_skip_redundant_work()
    {
        var restore = StepCatalog.Create(StepKind.Restore);
        var build = StepCatalog.Create(StepKind.Build, "My App.csproj");
        var test = StepCatalog.Create(StepKind.Test);
        Assert.Equal("dotnet build \"My App.csproj\" -c Release --no-restore", PipelineGenerator.DotnetCommand(build, new[] { restore }));
        Assert.Equal("dotnet test -c Release --no-build --logger trx --results-directory TestResults", PipelineGenerator.DotnetCommand(test, new[] { restore, build }));
    }

    [Fact]
    public void Validation_reports_missing_pieces()
    {
        var p = new PipelineDefinition { Stages = { new PipelineStage { Steps = { StepCatalog.Create(StepKind.DeployIis) } } } };
        p.Stages[0].Steps[0].Settings["servers"] = "http://web01:8790";
        var w = PipelineGenerator.Validate(p);
        Assert.Contains(w, x => x.Contains("Site-Namen"));
        Assert.Contains(w, x => x.Contains("veröffentlicht"));
        Assert.Contains(w, x => x.Contains("HTTP"));
        Assert.DoesNotContain(PipelineGenerator.Validate(Release(PipelineProvider.GitHub)), x => !x.Contains("HTTP"));
    }

    [Fact]
    public void Templates_do_not_duplicate_default_branch()
    {
        foreach (var t in PipelineTemplates.All)
        {
            var p = t.Create(null, null);
            Assert.Equal(p.Triggers.Branches.Distinct().Count(), p.Triggers.Branches.Count);
        }
    }

    [Fact]
    public void Store_writes_definition_yaml_and_script()
    {
        var root = Path.Combine(Path.GetTempPath(), "bs-pl-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var p = Release(PipelineProvider.GitHub);
            var files = PipelineStore.Save(root, p);
            Assert.Equal(3, files.Count);
            Assert.True(File.Exists(Path.Combine(root, ".github", "workflows", "release.yml")));
            Assert.True(File.Exists(Path.Combine(root, ".blazorstudio", "Deploy-BlazorStudio.ps1")));
            var loaded = PipelineStore.LoadAll(root).Single().Pipeline;
            Assert.Equal(p.Stages.Count, loaded.Stages.Count);
            Assert.Equal("Shop", loaded.Stages[1].Steps[0].Get("site"));
            Assert.Equal(PipelineGenerator.Generate(p), PipelineGenerator.Generate(loaded));

            p.OutputPath = "../outside.yml";
            Assert.Throws<ArgumentException>(() => PipelineStore.Save(root, p));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
