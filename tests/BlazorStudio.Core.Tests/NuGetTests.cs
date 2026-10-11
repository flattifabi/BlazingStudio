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
}
