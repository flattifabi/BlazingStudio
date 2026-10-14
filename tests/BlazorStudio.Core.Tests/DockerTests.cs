using BlazorStudio.Deploy.Docker;

namespace BlazorStudio.Core.Tests;

public class DockerTests
{
    [Fact]
    public void Run_arguments_contain_settings_and_labels()
    {
        var spec = new DockerRunSpec
        {
            Ports = { "8080:8080", " " },
            Environment = { "ASPNETCORE_ENVIRONMENT=Production", "ohne-gleich" },
            Volumes = { "shop-data:/app/data" },
            RestartPolicy = "unless-stopped",
            Network = "web",
        };
        var args = DockerCli.RunArguments("shop", "shop:1.2.0", spec, "1.2.0", "Shop", new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));
        Assert.Equal(new[] { "run", "-d", "--name", "shop", "-p", "8080:8080", "-e", "ASPNETCORE_ENVIRONMENT=Production", "-v", "shop-data:/app/data", "--restart", "unless-stopped", "--network", "web" }, args.Take(14));
        Assert.Contains(DockerCli.LabelVersion + "=1.2.0", args);
        Assert.Contains(DockerCli.LabelDeployedAt + "=2026-10-14T12:00:00Z", args);
        Assert.Equal("shop:1.2.0", args[^1]);
        Assert.DoesNotContain("ohne-gleich", args);
    }

    [Fact]
    public void Run_spec_round_trips_through_label_without_commas()
    {
        var spec = new DockerRunSpec { Ports = { "80:8080", "443:8443" }, Environment = { "A=1,2", "B=x=y" } };
        var encoded = DockerCli.EncodeSpec(spec);
        Assert.DoesNotContain(',', encoded);
        Assert.DoesNotContain('=', encoded);
        var labels = DockerCli.ParseLabels("com.docker.compose=1," + DockerCli.LabelRun + "=" + encoded + "," + DockerCli.LabelVersion + "=1.0");
        var back = DockerCli.DecodeSpec(labels[DockerCli.LabelRun]);
        Assert.Equal(spec.Ports, back!.Ports);
        Assert.Equal(spec.Environment, back.Environment);
        Assert.Equal("1.0", labels[DockerCli.LabelVersion]);
        Assert.Null(DockerCli.DecodeSpec("kaputt!"));
    }

    [Fact]
    public void Parses_cli_json_lines()
    {
        const string ps = """
            {"Command":"\"dotnet Shop.dll\"","CreatedAt":"2026-10-14 16:39:01 +0200 CEST","ID":"abc123","Image":"shop:1.2.0","Labels":"blazorstudio.managed=true,blazorstudio.version=1.2.0","Names":"shop","Ports":"0.0.0.0:8080->8080/tcp","RunningFor":"2 hours ago","State":"running","Status":"Up 2 hours"}
            WARNING: irgendwas
            {"ID":"def456","Image":"redis","Names":"cache","State":"exited","Status":"Exited (0) 3 days ago","Labels":""}
            """;
        var list = DockerCli.ParseJsonLines<DockerContainer>(ps);
        Assert.Equal(2, list.Count);
        Assert.True(list[0].IsRunning && list[0].IsManaged);
        Assert.Equal("1.2.0", list[0].Version);
        Assert.False(list[1].IsManaged);

        var img = DockerCli.ParseJsonLines<DockerImage>("""{"CreatedAt":"2026-10-14 16:39:01 +0200 CEST","ID":"sha","Repository":"shop","Tag":"1.2.0","Size":"220MB"}""").Single();
        Assert.Equal("shop:1.2.0", img.Reference);
        Assert.Equal(new DateTimeOffset(2026, 10, 14, 16, 39, 1, TimeSpan.FromHours(2)), img.Created);

        var info = System.Text.Json.JsonSerializer.Deserialize<DockerInfo>("""{"Name":"web01","ServerVersion":"27.3.1","NCPU":4,"MemTotal":8227315712,"ContainersRunning":3,"OSType":"linux"}""", DockerJson.Options)!;
        Assert.Equal(4, info.Cpus);
        Assert.Equal(3, info.ContainersRunning);
        Assert.Equal(12.5, DockerCli.ParsePercent("12.50%"));
    }

    [Theory]
    [InlineData("Fro.BlazorTest2", "fro.blazortest2")]
    [InlineData("My Shop!", "my-shop")]
    [InlineData("__", "app")]
    public void Suggests_valid_repository(string project, string expected)
    {
        Assert.Equal(expected, DockerCli.SuggestRepository(project));
        Assert.True(DockerCli.IsValidRepository(expected));
    }

    [Fact]
    public void Validates_names_tags_ports_and_endpoints()
    {
        Assert.True(DockerCli.IsValidName("shop-web_1"));
        Assert.False(DockerCli.IsValidName("-shop"));
        Assert.True(DockerCli.IsValidTag(DockerCli.ToTag("1.0.0+build/7")));
        Assert.Equal("1.0.0-build-7", DockerCli.ToTag("1.0.0+build/7"));
        Assert.True(DockerCli.IsValidPort("8080:8080"));
        Assert.True(DockerCli.IsValidPort("127.0.0.1:5000:8080/tcp"));
        Assert.False(DockerCli.IsValidPort("8080"));
        Assert.True(DockerCli.IsValidEndpoint(""));
        Assert.True(DockerCli.IsValidEndpoint("ssh://deploy@web01"));
        Assert.True(DockerCli.IsValidEndpoint("tcp://10.0.0.5:2376"));
        Assert.False(DockerCli.IsValidEndpoint("web01"));
        Assert.Equal("web01", DockerCli.HostNameOf("ssh://deploy@web01:22"));
        Assert.Equal("localhost", DockerCli.HostNameOf(""));
    }

    [Fact]
    public void Image_references_split_correctly()
    {
        Assert.Equal("reg.local:5000/team/shop:1.0", DockerCli.ImageReference("reg.local:5000/", "team/shop", "1.0"));
        Assert.Equal("shop:1.0", DockerCli.ImageReference(null, "shop", "1.0"));
        Assert.Equal("reg.local:5000/team/shop", DockerCli.RepositoryOf("reg.local:5000/team/shop:1.0"));
        Assert.Equal("reg.local:5000/shop", DockerCli.RepositoryOf("reg.local:5000/shop"));
        Assert.Equal("latest", DockerCli.TagOf("reg.local:5000/shop"));
        Assert.Equal("1.0", DockerCli.TagOf("shop:1.0"));
    }

    [Fact]
    public void Sdk_publish_and_build_context()
    {
        var args = DockerCli.SdkPublishArguments("/src/Shop/Shop.csproj", "shop", "1.0", "ghcr.io/me/", "Release", null);
        Assert.Contains("/t:PublishContainer", args);
        Assert.Contains("-p:ContainerRegistry=ghcr.io/me", args);

        var root = Path.Combine(Path.GetTempPath(), "bs-docker-" + Guid.NewGuid().ToString("N")[..6]);
        var proj = Path.Combine(root, "src", "Shop", "Shop.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(proj)!);
        File.WriteAllText(proj, "<Project />");
        var dockerfile = Path.Combine(Path.GetDirectoryName(proj)!, "Dockerfile");
        try
        {
            File.WriteAllText(dockerfile, "COPY [\"src/Shop/Shop.csproj\", \"src/Shop/\"]");
            Assert.Equal(dockerfile, DockerCli.FindDockerfile(proj));
            Assert.Equal(root, DockerCli.BuildContext(dockerfile, proj, root));
            File.WriteAllText(dockerfile, "COPY . .");
            Assert.Equal(Path.GetDirectoryName(proj), DockerCli.BuildContext(dockerfile, proj, root));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData("8080:80", 8080)]
    [InlineData("127.0.0.1:5000:8080", 5000)]
    [InlineData("8000-8001:80-81", 8000)]
    [InlineData("9000:9000/udp", 9000)]
    public void First_host_port(string port, int expected) =>
        Assert.Equal(expected, DockerCli.FirstHostPort(new DockerRunSpec { Ports = { port } }));

    [Fact]
    public void Health_url_uses_endpoint_host_and_published_port()
    {
        var spec = new DockerRunSpec { Ports = { "8081:8080" } };
        Assert.Equal("http://localhost:8081/health", DockerCli.HealthUrl(null, spec, "/health"));
        Assert.Equal("http://web01:8081/", DockerCli.HealthUrl("ssh://deploy@web01", spec, "/"));
        Assert.Equal("https://shop.local/ping", DockerCli.HealthUrl("tcp://10.0.0.5:2376", spec, "https://shop.local/ping"));
        Assert.Null(DockerCli.HealthUrl(null, spec, ""));
        Assert.Null(DockerCli.HealthUrl(null, new DockerRunSpec(), "/"));
    }

    [Fact]
    public void Host_and_extra_arguments()
    {
        Assert.Empty(DockerCli.HostArguments(" "));
        Assert.Equal(new[] { "-H", "ssh://u@h" }, DockerCli.HostArguments("ssh://u@h "));
        Assert.Equal(new[] { "--memory", "512m", "--cpus=1" }, DockerCli.SplitArguments("--memory 512m\n# Kommentar\n--cpus=1"));
    }
}
