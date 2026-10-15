using BlazorStudio.Deploy;

namespace BlazorStudio.Core.Tests;

public class PersistentPathsTests
{
    [Theory]
    [InlineData("app.db", "app.db")]
    [InlineData(@"App_Data\shop.db", "App_Data/shop.db")]
    [InlineData("/wwwroot/uploads/", "wwwroot/uploads")]
    [InlineData(" data / app.db ", "data/app.db")]
    [InlineData("../secret", null)]
    [InlineData(@"C:\data\app.db", null)]
    [InlineData("web.config", null)]
    [InlineData("app_offline.htm", null)]
    [InlineData("a/./b", null)]
    [InlineData("", null)]
    public void Normalize(string input, string? expected) => Assert.Equal(expected, PersistentPaths.Normalize(input));

    [Fact]
    public void Parse_splits_dedupes_and_reports_invalid()
    {
        var list = PersistentPaths.Parse("app.db, App_Data\n# Kommentar\napp.DB;../x", out var invalid);
        Assert.Equal(new[] { "app.db", "App_Data" }, list);
        Assert.Equal(new[] { "../x" }, invalid);
        Assert.Throws<ArgumentException>(() => PersistentPaths.Validate(new[] { "ok.db", "C:/nope" }));
    }

    [Fact]
    public void Form_round_trip_keeps_null_and_empty_apart()
    {
        DeployRequest Round(DeployRequest r)
        {
            var form = r.ToForm().ToDictionary(k => k.Key, k => k.Value);
            return DeployRequest.FromForm(k => form.TryGetValue(k, out var v) ? v : null);
        }
        Assert.Null(Round(new DeployRequest { Site = "S", Version = "1" }).Persist);
        Assert.Equal("", Round(new DeployRequest { Site = "S", Version = "1", Persist = "" }).Persist);
        Assert.Equal("app.db,App_Data", Round(new DeployRequest { Site = "S", Version = "1", Persist = "app.db,App_Data" }).Persist);
    }

    [Fact]
    public async Task Move_carries_files_with_sqlite_companions_and_folders_and_can_undo()
    {
        var root = Path.Combine(Path.GetTempPath(), "bs-persist-" + Guid.NewGuid().ToString("N")[..6]);
        var oldDir = Path.Combine(root, "1.0");
        var newDir = Path.Combine(root, "1.1");
        try
        {
            Directory.CreateDirectory(Path.Combine(oldDir, "App_Data"));
            Directory.CreateDirectory(Path.Combine(newDir, "App_Data"));
            File.WriteAllText(Path.Combine(oldDir, "app.db"), "live");
            File.WriteAllText(Path.Combine(oldDir, "app.db-wal"), "wal");
            File.WriteAllText(Path.Combine(oldDir, "App_Data", "upload.png"), "img");
            // Aus dem Paket: leere Start-DB, eine veraltete -shm und ein Ordner mit anderem Inhalt
            File.WriteAllText(Path.Combine(newDir, "app.db"), "seed");
            File.WriteAllText(Path.Combine(newDir, "app.db-shm"), "stale");
            File.WriteAllText(Path.Combine(newDir, "App_Data", "seed.txt"), "seed");
            var log = new List<string>();

            var move = await PersistentMove.RunAsync(oldDir, newDir, new[] { "app.db", "App_Data", "missing.db" }, (t, _) => log.Add(t), TimeSpan.FromSeconds(2));

            Assert.Equal("live", File.ReadAllText(Path.Combine(newDir, "app.db")));
            Assert.Equal("wal", File.ReadAllText(Path.Combine(newDir, "app.db-wal")));
            Assert.False(File.Exists(Path.Combine(newDir, "app.db-shm")));
            Assert.True(File.Exists(Path.Combine(newDir, "App_Data", "upload.png")));
            Assert.False(File.Exists(Path.Combine(newDir, "App_Data", "seed.txt")));
            Assert.False(File.Exists(Path.Combine(oldDir, "app.db")));
            Assert.Contains(log, l => l.Contains("missing.db"));

            move.Undo((t, _) => log.Add(t));
            Assert.Equal("live", File.ReadAllText(Path.Combine(oldDir, "app.db")));
            Assert.Equal("wal", File.ReadAllText(Path.Combine(oldDir, "app.db-wal")));
            Assert.True(File.Exists(Path.Combine(oldDir, "App_Data", "upload.png")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
