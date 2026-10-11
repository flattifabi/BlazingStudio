using System.Diagnostics;
using BlazorStudio.Core.Catalog;
using Xunit;
using Xunit.Abstractions;

namespace BlazorStudio.Core.Tests;

/// <summary>Scannt jede Komponenten-Bibliothek im lokalen NuGet-Cache – keine darf den Scan abbrechen.</summary>
public class LibraryScanAllTests(ITestOutputHelper output)
{
    private static readonly string[] Hints = { "blazor", "razor", "mud", "radzen", "fluent", "syncfusion", "blazorise", "antdesign", "havit", "telerik", "devexpress", "bit.", "sotsera" };

    [Fact]
    public void Scans_every_cached_component_library_without_throwing()
    {
        var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        if (!Directory.Exists(cache)) return;
        var sw = Stopwatch.StartNew();
        var scanned = 0;
        foreach (var pkgDir in Directory.GetDirectories(cache).Where(d => Hints.Any(h => Path.GetFileName(d).Contains(h, StringComparison.OrdinalIgnoreCase))))
        {
            var id = Path.GetFileName(pkgDir);
            if (id.StartsWith("microsoft.", StringComparison.OrdinalIgnoreCase)) continue;
            var versionDir = Directory.GetDirectories(pkgDir).OrderByDescending(v => Path.GetFileName(v), Comparer<string>.Create(BlazorStudio.Core.NuGet.NuGetClient.CompareVersions)).FirstOrDefault();
            var lib = versionDir is null ? null : Path.Combine(versionDir, "lib");
            if (lib is null || !Directory.Exists(lib)) continue;
            var tfm = Directory.GetDirectories(lib).OrderByDescending(d => d).FirstOrDefault();
            if (tfm is null) continue;
            var dlls = Directory.GetFiles(tfm, "*.dll");
            if (dlls.Length == 0) continue;
            var libs = LibraryScanner.Scan(new[] { (id, Path.GetFileName(versionDir!), (IReadOnlyList<string>)dlls) });
            output.WriteLine($"{id} {Path.GetFileName(versionDir)}: {libs.Sum(l => l.Components.Count)} Komponenten");
            scanned++;
        }
        output.WriteLine($"{scanned} Pakete in {sw.Elapsed.TotalSeconds:0.0}s");
    }

    [Fact]
    public void Friendly_names_for_nested_generic_types()
    {
        Assert.Equal("List<int>", LibraryScanner.FriendlyName(typeof(List<int>)));
        Assert.Equal("int?", LibraryScanner.FriendlyName(typeof(int?)));
        Assert.Equal("string[]", LibraryScanner.FriendlyName(typeof(string[])));
        // Dictionary<,>.KeyCollection ist generisch, hat aber keinen ` im Namen
        Assert.Equal("KeyCollection", LibraryScanner.FriendlyName(typeof(Dictionary<string, int>.KeyCollection)));
    }
}
