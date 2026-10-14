using System.IO.Compression;
using System.Net;
using System.Text;
using BlazorStudio.Deploy;

namespace BlazorStudio.Core.Tests;

public sealed class DeployTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bs-deploy-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public DeployTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string Zip(params (string Name, string Content)[] entries)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open());
            w.Write(content);
        }
        return path;
    }

    // ------------------------------------------------------------------
    // Bindungen
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("http://*:8080", "http", "*:8080:")]
    [InlineData("http://shop.local", "http", "*:80:shop.local")]
    [InlineData("https://Shop.Example.com:8443/", "https", "*:8443:shop.example.com")]
    [InlineData("http://10.0.0.5:81", "http", "10.0.0.5:81:")]
    [InlineData("*:80:shop.local", "http", "*:80:shop.local")]
    [InlineData("8080", "http", "*:8080:")]
    public void Bindings_are_parsed(string text, string protocol, string info)
    {
        Assert.True(BindingParser.TryParse(text, out var p, out var i));
        Assert.Equal(protocol, p);
        Assert.Equal(info, i);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ftp://x")]
    [InlineData("http://*:99999")]
    [InlineData("nonsense")]
    public void Invalid_bindings_are_rejected(string text) => Assert.False(BindingParser.TryParse(text, out _, out _));

    [Fact]
    public void Binding_url_uses_localhost_for_wildcards()
    {
        Assert.Equal("http://localhost:8080/", BindingParser.ToUrl("http", "*:8080:"));
        Assert.Equal("https://shop.local/", BindingParser.ToUrl("https", "*:443:shop.local"));
    }

    // ------------------------------------------------------------------
    // Versionsablage
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("1.4.0", true)]
    [InlineData("build-123", true)]
    [InlineData("20261014.1530+abc", true)]
    [InlineData("../evil", false)]
    [InlineData("a..b", false)]
    [InlineData("with space", false)]
    [InlineData("C:\\x", false)]
    [InlineData("", false)]
    public void Version_names_are_validated(string version, bool ok) => Assert.Equal(ok, VersionStore.IsValidVersion(version));

    [Fact]
    public void Target_folder_includes_application_path()
    {
        Assert.Equal("Shop", VersionStore.TargetFolderName("Shop", "/"));
        Assert.Equal("Default Web Site_api_v2", VersionStore.TargetFolderName("Default Web Site", "/api/v2/"));
    }

    [Fact]
    public void Install_strips_single_top_folder_and_writes_metadata_outside_web_root()
    {
        var store = new VersionStore(Path.Combine(_dir, "store"));
        var zip = Zip(("publish/web.config", "<configuration/>"), ("publish/wwwroot/app.css", "body{}"));
        var v = store.Install("Shop", "/", zip, new VersionMetadata { Version = "1.0.0", DeployedAt = DateTime.UtcNow, Commit = "abc" }, overwrite: false);

        Assert.True(File.Exists(Path.Combine(v.Path, "web.config")));
        Assert.True(File.Exists(Path.Combine(v.Path, "wwwroot", "app.css")));
        Assert.Equal(2, v.Files);
        Assert.False(File.Exists(Path.Combine(v.Path, "1.0.0.json")));
        Assert.True(File.Exists(v.Path + ".json"));
        Assert.Equal("abc", store.ReadMetadata("Shop", "/", "1.0.0")!.Commit);
        Assert.Throws<InvalidOperationException>(() => store.Install("Shop", "/", zip, new VersionMetadata { Version = "1.0.0" }, overwrite: false));
    }

    [Fact]
    public void Zip_slip_is_rejected()
    {
        var store = new VersionStore(Path.Combine(_dir, "store"));
        var zip = Zip(("../../evil.txt", "x"));
        Assert.Throws<InvalidDataException>(() => store.Install("Shop", "/", zip, new VersionMetadata { Version = "1.0.0" }, overwrite: false));
        Assert.False(File.Exists(Path.Combine(_dir, "evil.txt")));
        Assert.False(store.Exists("Shop", "/", "1.0.0"));
    }

    [Fact]
    public void Active_and_previous_versions_survive_pruning()
    {
        var store = new VersionStore(Path.Combine(_dir, "store"));
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 1; i <= 6; i++)
        {
            store.Install("Shop", "/", Zip(("web.config", "x")), new VersionMetadata { Version = $"1.0.{i}", DeployedAt = start.AddHours(i) }, overwrite: false);
        }
        // 1.0.1 war zuletzt vor 1.0.2 aktiv, 1.0.2 ist aktiv
        store.MarkActivated("Shop", "/", "1.0.1", start.AddDays(1));
        store.MarkActivated("Shop", "/", "1.0.2", start.AddDays(2));

        var list = store.List("Shop", "/", "1.0.2");
        Assert.True(list.Single(v => v.Version == "1.0.2").IsActive);
        Assert.True(list.Single(v => v.Version == "1.0.1").IsPrevious);

        var removed = store.Prune("Shop", "/", keep: 2, activeVersion: "1.0.2");
        Assert.Equal(new[] { "1.0.3", "1.0.4" }, removed.OrderBy(x => x));
        var left = store.List("Shop", "/", "1.0.2").Select(v => v.Version).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "1.0.1", "1.0.2", "1.0.5", "1.0.6" }, left);
    }

    [Fact]
    public void Version_is_recognized_from_physical_path()
    {
        var store = new VersionStore(Path.Combine(_dir, "store"));
        var path = Path.Combine(store.TargetDirectory("Shop", "/"), "2.1.0");
        Assert.Equal("2.1.0", store.VersionFromPath("Shop", "/", path + Path.DirectorySeparatorChar));
        Assert.Null(store.VersionFromPath("Shop", "/", Path.Combine(_dir, "elsewhere", "2.1.0")));
        Assert.Null(store.VersionFromPath("Shop", "/", null));
    }

    // ------------------------------------------------------------------
    // Verlauf
    // ------------------------------------------------------------------

    [Fact]
    public void Journal_persists_and_marks_interrupted_deployments_failed()
    {
        var file = Path.Combine(_dir, "data", "deployments.json");
        var journal = new DeploymentJournal(file);
        var r = new DeploymentRecord { Id = "a", Site = "Shop", Version = "1", State = DeploymentState.Running, StartedAt = DateTime.UtcNow };
        journal.Add(r);
        journal.Log(r, "läuft");
        Assert.Equal(1, journal.Running);

        var reopened = new DeploymentJournal(file);
        var again = reopened.Get("a")!;
        Assert.Equal(DeploymentState.Failed, again.State);
        Assert.Single(again.Log);
        Assert.Empty(reopened.List()[0].Log);
        Assert.Single(reopened.List("shop"));
        Assert.Empty(reopened.List("other"));
    }

    [Fact]
    public void Deploy_request_round_trips_through_form_fields()
    {
        var req = new DeployRequest { Site = "Shop", AppPath = "api/", Version = "1.2.3", CreateSite = true, Binding = "http://*:8080", HealthUrl = "http://localhost:8080/health", Rollback = false, Commit = "abc" };
        var form = req.ToForm().ToDictionary(k => k.Key, k => k.Value);
        var back = DeployRequest.FromForm(k => form.GetValueOrDefault(k));
        Assert.Equal("/api", back.AppPath);
        Assert.True(back.CreateSite);
        Assert.False(back.Rollback);
        Assert.True(back.Activate);
        Assert.Equal("http://*:8080", back.Binding);
        Assert.Equal("abc", back.Commit);
    }

    // ------------------------------------------------------------------
    // Client
    // ------------------------------------------------------------------

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Client_sends_key_and_reads_enums_as_strings()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, """{"id":"x","site":"Shop","state":"RolledBack","log":[{"text":"hi","level":"warn"}]}"""));
        using var client = new AgentClient(new Uri("http://web01:8790/"), "secret", handler: handler);
        var r = await client.GetDeploymentAsync("x");
        Assert.Equal(DeploymentState.RolledBack, r.State);
        Assert.True(r.IsFinished);
        Assert.Equal("secret", handler.Requests[0].Headers.GetValues(AgentDefaults.ApiKeyHeader).Single());
        Assert.Equal("/api/deployments/x", handler.Requests[0].RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Client_maps_errors()
    {
        var handler = new FakeHandler(r => r.RequestUri!.AbsolutePath.Contains("pools")
            ? Json(HttpStatusCode.Conflict, """{"error":"wird noch verwendet"}""")
            : Json(HttpStatusCode.Unauthorized, """{"error":"API-Schlüssel ungültig."}"""));
        using var client = new AgentClient(new Uri("http://web01:8790/"), "x", handler: handler);
        var e1 = await Assert.ThrowsAsync<AgentException>(() => client.GetInfoAsync());
        Assert.True(e1.IsUnauthorized);
        var e2 = await Assert.ThrowsAsync<AgentException>(() => client.DeletePoolAsync("Shop Pool"));
        Assert.Equal("wird noch verwendet", e2.Message);
        Assert.Equal("/api/pools/Shop%20Pool", handler.Requests[^1].RequestUri!.AbsolutePath);
    }

    [Fact]
    public void Address_and_thumbprint_are_normalized()
    {
        Assert.Equal("https://web01:9000/", AgentClient.BuildAddress("web01", 9000, true).ToString());
        Assert.Equal("http://[::1]:8790/", AgentClient.BuildAddress("::1", 0, false).ToString());
        Assert.Equal("AB12CD", AgentClient.NormalizeThumbprint("ab:12 cd"));
    }

    [Fact]
    public void Deploy_script_is_embedded()
    {
        var script = DeployScripts.DeployScript;
        Assert.Contains("BLAZORSTUDIO_AGENT_KEY", script);
        Assert.Contains("/api/deployments", script);
    }

    [Theory]
    [InlineData("1.6.1", "1.6.2", true)]
    [InlineData("1.6.0", "1.6.2", true)]
    [InlineData("1.5.9", "1.6.2", true)]
    [InlineData("1.6.2", "1.6.2", false)]
    [InlineData("1.6.2.0", "1.6.2", false)]
    [InlineData("1.6.3+abc", "1.6.2", false)]
    [InlineData("1.7", "1.6.2", false)]
    [InlineData("", "1.6.2", true)]
    [InlineData("kaputt", "1.6.2", true)]
    public void Agent_version_comparison_includes_patch(string agent, string minimum, bool older) =>
        Assert.Equal(older, AgentDefaults.IsOlderThan(agent, minimum));
}
