using System.IO.Compression;
using System.Net;
using System.Text;
using BlazorStudio.Core.NuGet;

namespace BlazorStudio.Core.Tests;

public class NuGetTests
{
    [Theory]
    [InlineData("1.2.10", "1.2.9", 1)]
    [InlineData("8.0.0", "8.0.0-rc.2", 1)]
    [InlineData("8.0.0-rc.10", "8.0.0-rc.2", 1)]
    [InlineData("8.0.0-alpha", "8.0.0-beta", -1)]
    [InlineData("10.0.0", "10.0", 0)]
    public void Compares_semantic_versions(string a, string b, int expected) =>
        Assert.Equal(expected, Math.Sign(NuGetClient.CompareVersions(a, b)));

    [Fact]
    public void Reads_package_references_including_central_versions()
    {
        var dir = Directory.CreateTempSubdirectory("bs-nuget");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "Directory.Packages.props"),
                "<Project><ItemGroup><PackageVersion Include=\"Serilog\" Version=\"4.0.1\" /></ItemGroup></Project>");
            var csproj = Path.Combine(dir.FullName, "App.csproj");
            File.WriteAllText(csproj, """
                <Project Sdk="Microsoft.NET.Sdk.Web">
                  <ItemGroup>
                    <PackageReference Include="MudBlazor" Version="8.5.0" />
                    <PackageReference Include="Serilog" />
                    <PackageReference Include="Humanizer"><Version>2.14.1</Version></PackageReference>
                  </ItemGroup>
                </Project>
                """);
            var list = NuGetClient.ReadInstalled(csproj);
            Assert.Equal(new[] { "Humanizer", "MudBlazor", "Serilog" }, list.Select(p => p.Id));
            Assert.Equal("2.14.1", list[0].Version);
            Assert.Equal("4.0.1", list[2].Version);
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void Merges_user_and_project_configs_with_credentials_and_disabled_sources()
    {
        var dir = Directory.CreateTempSubdirectory("bs-nugetcfg");
        try
        {
            var user = Path.Combine(dir.FullName, "user", "NuGet.Config");
            Directory.CreateDirectory(Path.GetDirectoryName(user)!);
            File.WriteAllText(user, """
                <configuration>
                  <packageSources>
                    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
                    <add key="Old" value="https://old.example/v3/index.json" />
                  </packageSources>
                  <packageSourceCredentials>
                    <Firma_x0020_Feed>
                      <add key="Username" value="max" />
                      <add key="ClearTextPassword" value="geheim" />
                    </Firma_x0020_Feed>
                  </packageSourceCredentials>
                  <disabledPackageSources><add key="Old" value="true" /></disabledPackageSources>
                </configuration>
                """);
            var repo = Path.Combine(dir.FullName, "repo");
            var project = Path.Combine(repo, "src", "App");
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(repo, "nuget.config"), """
                <configuration>
                  <packageSources>
                    <add key="Firma Feed" value="https://pkgs.example/v3/index.json" />
                    <add key="Lokal" value="packages" />
                  </packageSources>
                </configuration>
                """);

            var sources = NuGetConfig.Load(project, user);
            Assert.Equal(new[] { "nuget.org", "Old", "Firma Feed", "Lokal" }, sources.Select(s => s.Name));
            Assert.False(sources[1].Enabled);
            Assert.Equal("max", sources[2].UserName);
            Assert.Equal("geheim", sources[2].Password);
            Assert.True(sources[3].IsLocal);
            Assert.Equal(Path.Combine(repo, "packages"), sources[3].Url);

            // <clear /> in der Projekt-Config verwirft die Benutzer-Quellen
            File.WriteAllText(Path.Combine(project, "NuGet.Config"), """
                <configuration><packageSources><clear /><add key="Nur" value="https://only.example/v3/index.json" /></packageSources></configuration>
                """);
            Assert.Equal(new[] { "Nur" }, NuGetConfig.Load(project, user).Select(s => s.Name));
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void Saves_edits_and_deletes_sources_in_the_user_config()
    {
        var dir = Directory.CreateTempSubdirectory("bs-nugetsave");
        try
        {
            var user = Path.Combine(dir.FullName, "NuGet", "NuGet.Config");
            NuGetConfig.Save(new PackageSource("Firma Feed", "https://pkgs.example/v3/index.json") { UserName = "max" }, "geheim", userConfig: user);

            var s = NuGetConfig.Load(null, user);
            Assert.Equal(new[] { "nuget.org", "Firma Feed" }, s.Select(x => x.Name));
            Assert.Equal("max", s[1].UserName);
            if (!OperatingSystem.IsWindows()) Assert.Equal("geheim", s[1].Password);
            Assert.Contains("Firma_x0020_Feed", File.ReadAllText(user));

            // umbenennen, Passwort unverändert lassen, deaktivieren
            NuGetConfig.Save(new PackageSource("Intern", "https://pkgs.example/v3/index.json") { UserName = "moritz", Enabled = false }, null, "Firma Feed", userConfig: user);
            s = NuGetConfig.Load(null, user);
            Assert.Equal(new[] { "nuget.org", "Intern" }, s.Select(x => x.Name));
            Assert.Equal("moritz", s[1].UserName);
            Assert.False(s[1].Enabled);
            if (!OperatingSystem.IsWindows()) Assert.Equal("geheim", s[1].Password);
            Assert.DoesNotContain("Firma", File.ReadAllText(user));

            NuGetConfig.SetEnabled("Intern", true, user);
            Assert.True(NuGetConfig.Load(null, user)[1].Enabled);

            // nur Zugangsdaten für eine Quelle aus einer Projekt-Config
            NuGetConfig.Save(new PackageSource("Projektfeed", "https://x.example/v3/index.json") { UserName = "ci" }, "token", credentialsOnly: true, userConfig: user);
            var text = File.ReadAllText(user);
            Assert.DoesNotContain("https://x.example", text);
            Assert.Contains("<Projektfeed>", text);

            NuGetConfig.Delete("Intern", user);
            Assert.Equal(new[] { "nuget.org" }, NuGetConfig.Load(null, user).Select(x => x.Name));
            Assert.DoesNotContain("moritz", File.ReadAllText(user));
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public async Task Searches_local_folder_feeds()
    {
        var dir = Directory.CreateTempSubdirectory("bs-nugetlocal");
        try
        {
            WriteNupkg(Path.Combine(dir.FullName, "firma.ui.1.0.0.nupkg"), "Firma.UI", "1.0.0", "Komponenten der Firma");
            Directory.CreateDirectory(Path.Combine(dir.FullName, "firma.ui", "1.2.0-beta"));
            WriteNupkg(Path.Combine(dir.FullName, "firma.ui", "1.2.0-beta", "firma.ui.1.2.0-beta.nupkg"), "Firma.UI", "1.2.0-beta", "Komponenten der Firma");
            WriteNupkg(Path.Combine(dir.FullName, "firma.data.2.0.0.nupkg"), "Firma.Data", "2.0.0", "Datenzugriff");

            var client = new NuGetClient { Sources = new[] { new PackageSource("Lokal", dir.FullName) } };
            var all = await client.SearchAsync("", prerelease: false);
            Assert.Empty(all.Errors);
            Assert.Equal(new[] { "Firma.Data", "Firma.UI" }, all.Packages.Select(p => p.Id));
            Assert.Equal("1.0.0", all.Packages[1].Version);
            Assert.Equal("Lokal", all.Packages[1].Source);

            var ui = await client.SearchAsync("ui", prerelease: true);
            Assert.Equal("1.2.0-beta", Assert.Single(ui.Packages).Version);
            Assert.Equal(new[] { "1.2.0-beta", "1.0.0" }, await client.GetVersionsAsync("firma.ui", prerelease: true));
            Assert.Equal("Komponenten der Firma", (await client.GetDetailsAsync("Firma.UI", "1.0.0"))?.Description);

            var missing = new NuGetClient { Sources = new[] { new PackageSource("Weg", Path.Combine(dir.FullName, "nope")) } };
            Assert.IsType<DirectoryNotFoundException>(Assert.Single((await missing.SearchAsync("x", false)).Errors).Error);
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public async Task Sends_basic_auth_only_to_the_feed_host()
    {
        var handler = new FakeFeed();
        var client = new NuGetClient(new HttpClient(handler))
        {
            Sources = new[] { new PackageSource("Firma", "https://pkgs.example/v3/index.json") { UserName = "max", Password = "geheim" } },
        };
        var result = await client.SearchAsync("", prerelease: false);
        Assert.Empty(result.Errors);
        Assert.Equal("Firma.UI", Assert.Single(result.Packages).Id);
        Assert.Equal(new[] { "2.0.0", "1.0.0" }, await client.GetVersionsAsync("Firma.UI", false));
        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("max:geheim"));
        Assert.All(handler.Requests.Where(r => r.Host == "pkgs.example"), r => Assert.Equal(expected, r.Auth));
        Assert.All(handler.Requests.Where(r => r.Host != "pkgs.example"), r => Assert.Null(r.Auth));

        // falsches Passwort: Fehler der Quelle statt Ausnahme
        var denied = new NuGetClient(new HttpClient(handler))
        {
            Sources = new[] { new PackageSource("Firma", "https://pkgs.example/v3/index.json") { UserName = "max", Password = "falsch" } },
        };
        var error = Assert.Single((await denied.SearchAsync("", false)).Errors);
        Assert.Equal(HttpStatusCode.Unauthorized, Assert.IsType<HttpRequestException>(error.Error).StatusCode);
    }

    private static void WriteNupkg(string path, string id, string version, string description)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var w = new StreamWriter(zip.CreateEntry(id.ToLowerInvariant() + ".nuspec").Open());
        w.Write($"""
            <?xml version="1.0"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata><id>{id}</id><version>{version}</version><authors>Firma</authors><description>{description}</description></metadata>
            </package>
            """);
    }

    private sealed class FakeFeed : HttpMessageHandler
    {
        public List<(string Host, string? Auth)> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var auth = request.Headers.Authorization?.ToString();
            Requests.Add((request.RequestUri!.Host, auth));
            var ok = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("max:geheim"));
            if (request.RequestUri.Host == "pkgs.example" && auth != ok)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            var body = request.RequestUri.AbsolutePath switch
            {
                "/v3/index.json" => """
                    {"version":"3.0.0","resources":[
                      {"@id":"https://pkgs.example/query","@type":"SearchQueryService/3.5.0"},
                      {"@id":"https://pkgs.example/flat","@type":"PackageBaseAddress/3.0.0"}]}
                    """,
                "/query" => """{"data":[{"id":"Firma.UI","version":"2.0.0","description":"UI","authors":["Firma"],"iconUrl":"https://cdn.example/icon.png"}]}""",
                "/flat/firma.ui/index.json" => """{"versions":["1.0.0","2.0.0","2.1.0-beta"]}""",
                _ => null,
            };
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
