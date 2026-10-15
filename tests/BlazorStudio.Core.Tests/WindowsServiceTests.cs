using BlazorStudio.Deploy;
using BlazorStudio.Deploy.Pipelines;

namespace BlazorStudio.Core.Tests;

public class WindowsServiceTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\App\\App.exe\" --port 5", "C:\\Program Files\\App\\App.exe", "--port 5")]
    [InlineData("C:\\svc\\Worker.exe", "C:\\svc\\Worker.exe", "")]
    [InlineData("C:\\Program Files\\Old Svc\\svc.exe -k run", "C:\\Program Files\\Old Svc\\svc.exe", "-k run")]
    [InlineData("", "", "")]
    public void Split_command_line(string input, string exe, string args) =>
        Assert.Equal((exe, args), ServiceCommandLine.Split(input));

    [Fact]
    public void Build_always_quotes_the_program()
    {
        Assert.Equal("\"C:\\a b\\x.exe\" --x 1", ServiceCommandLine.Build("C:\\a b\\x.exe", " --x 1 "));
        Assert.Equal("\"C:\\x.exe\"", ServiceCommandLine.Build("C:\\x.exe", null));
    }

    [Theory]
    [InlineData("Shop.Worker", true)]
    [InlineData("shop_worker-2", true)]
    [InlineData("-bad", false)]
    [InlineData("has space", false)]
    [InlineData("", false)]
    public void Names(string name, bool ok) => Assert.Equal(ok, ServiceCommandLine.IsValidName(name));

    [Fact]
    public void Finds_program_in_package()
    {
        var dir = Directory.CreateTempSubdirectory("bs-svc").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "Worker.dll"), "");
            File.WriteAllText(Path.Combine(dir, "Worker.runtimeconfig.json"), "{}");
            // Framework-abhängig ohne apphost → dotnet + dll
            Assert.Equal(("Worker.dll", (string?)null), ServiceCommandLine.FindProgram(dir, null));
            Assert.Equal("\"C:\\dotnet\\dotnet.exe\" \"" + Path.Combine(dir, "Worker.dll") + "\" --x",
                ServiceCommandLine.CommandFor(dir, "Worker.dll", "--x", "C:\\dotnet\\dotnet.exe"));

            // apphost vorhanden → .exe, auch wenn noch andere .exe herumliegen
            File.WriteAllText(Path.Combine(dir, "Worker.exe"), "");
            File.WriteAllText(Path.Combine(dir, "createdump.exe"), "");
            Assert.Equal(("Worker.exe", (string?)null), ServiceCommandLine.FindProgram(dir, null));
            Assert.Equal("\"" + Path.Combine(dir, "Worker.exe") + "\"", ServiceCommandLine.CommandFor(dir, "Worker.exe", null, "dotnet.exe"));

            // Ausdrücklich angegeben
            Assert.Equal(("createdump.exe", (string?)null), ServiceCommandLine.FindProgram(dir, "createdump.exe"));
            Assert.NotNull(ServiceCommandLine.FindProgram(dir, "../evil.exe").Error);
            Assert.NotNull(ServiceCommandLine.FindProgram(dir, "missing.exe").Error);

            // Zwei Apps → nachfragen
            File.WriteAllText(Path.Combine(dir, "Other.exe"), "");
            File.WriteAllText(Path.Combine(dir, "Other.runtimeconfig.json"), "{}");
            Assert.Contains("Mehrere", ServiceCommandLine.FindProgram(dir, null).Error);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Service_fields_survive_the_form()
    {
        var r = new DeployRequest { Site = "Worker", Version = "1", Target = DeployTargets.Service, ServiceExe = "Worker.exe", ServiceAccount = "NetworkService", ServiceStartType = "delayed" };
        var form = r.ToForm().ToDictionary(k => k.Key, k => k.Value);
        var back = DeployRequest.FromForm(k => form.TryGetValue(k, out var v) ? v : null);
        Assert.True(back.IsService);
        Assert.Equal("Worker.exe", back.ServiceExe);
        Assert.Equal("NetworkService", back.ServiceAccount);
        Assert.Equal("delayed", back.ServiceStartType);
        Assert.False(DeployRequest.FromForm(_ => null).IsService);
        Assert.DoesNotContain("target", new DeployRequest { Site = "S", Version = "1" }.ToForm().Select(k => k.Key));
    }

    [Theory]
    [InlineData("LocalService", @"NT AUTHORITY\LocalService")]
    [InlineData("networkservice", @"NT AUTHORITY\NetworkService")]
    [InlineData("LocalSystem", "LocalSystem")]
    [InlineData(@"FIRMA\svc-shop", @"FIRMA\svc-shop")]
    public void Accounts(string input, string sc) => Assert.Equal(sc, ServiceCommandLine.ScAccount(input));

    [Fact]
    public void Pipeline_step_as_service()
    {
        var step = StepCatalog.Create(StepKind.DeployIis);
        step.Settings["servers"] = "https://app01:8790";
        step.Settings["target"] = "service";
        step.Settings["site"] = "ShopWorker";
        step.Settings["account"] = "NetworkService";
        step.Settings["appPath"] = "/ignored";
        var p = new PipelineDefinition { Provider = PipelineProvider.GitLab, Stages = { new PipelineStage { Steps = { step } } } };
        var yaml = PipelineGenerator.Generate(p);
        Assert.Contains("-Service", yaml);
        Assert.Contains("-Account", yaml);
        Assert.DoesNotContain("-AppPath", yaml);
        step.Settings["site"] = "bad name";
        Assert.Contains(PipelineGenerator.Validate(p), w => w.Contains("Dienstname"));
    }
}
