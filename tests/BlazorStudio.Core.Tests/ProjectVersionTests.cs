using BlazorStudio.Core.Build;

namespace BlazorStudio.Core.Tests;

public class ProjectVersionTests
{
    [Fact]
    public void Replaces_existing_version_only()
    {
        const string csproj = "<Project Sdk=\"Microsoft.NET.Sdk.Web\">\r\n  <PropertyGroup>\r\n    <TargetFramework>net10.0</TargetFramework>\r\n    <Version>1.0.0</Version>\r\n  </PropertyGroup>\r\n</Project>\r\n";
        Assert.Equal("1.0.0", ProjectVersion.Read(csproj));
        var updated = ProjectVersion.Write(csproj, "1.2.0");
        Assert.Equal(csproj.Replace("1.0.0", "1.2.0"), updated);
    }

    [Fact]
    public void Updates_version_prefix()
    {
        const string csproj = "<Project><PropertyGroup><VersionPrefix>2.1.0</VersionPrefix><VersionSuffix>beta</VersionSuffix></PropertyGroup></Project>";
        Assert.Equal("2.1.0", ProjectVersion.Read(csproj));
        Assert.Contains("<VersionPrefix>2.2.0</VersionPrefix>", ProjectVersion.Write(csproj, "2.2.0"));
    }

    [Fact]
    public void Inserts_version_with_matching_indentation()
    {
        const string csproj = "<Project Sdk=\"Microsoft.NET.Sdk.Web\">\n\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n\n</Project>\n";
        Assert.Null(ProjectVersion.Read(csproj));
        var updated = ProjectVersion.Write(csproj, "1.0.1");
        Assert.Contains("  <PropertyGroup>\n    <Version>1.0.1</Version>\n    <TargetFramework>", updated);
        Assert.True(ProjectVersion.IsWellFormed(updated));
        Assert.Equal("1.0.1", ProjectVersion.Read(updated));
    }

    [Fact]
    public void Creates_property_group_when_missing()
    {
        var updated = ProjectVersion.Write("<Project Sdk=\"Microsoft.NET.Sdk\">\n</Project>\n", "3.0.0");
        Assert.True(ProjectVersion.IsWellFormed(updated));
        Assert.Equal("3.0.0", ProjectVersion.Read(updated));
    }

    [Theory]
    [InlineData("1.2.3", VersionPart.Patch, "1.2.4")]
    [InlineData("1.2.3", VersionPart.Minor, "1.3.0")]
    [InlineData("1.2.3-beta.1", VersionPart.Major, "2.0.0")]
    [InlineData(null, VersionPart.Patch, "1.0.1")]
    [InlineData("2.0", VersionPart.Patch, "2.0.1")]
    public void Bumps(string? version, VersionPart part, string expected) => Assert.Equal(expected, ProjectVersion.Bump(version, part));

    [Theory]
    [InlineData("1.2.3", true)]
    [InlineData("1.2.3-beta.1", true)]
    [InlineData("1.2", true)]
    [InlineData("1.2.3.4", true)]
    [InlineData("v1.2", false)]
    [InlineData("1..2", false)]
    [InlineData("", false)]
    public void Validates(string version, bool ok)
    {
        Assert.Equal(ok, ProjectVersion.IsValid(version));
        if (!ok) Assert.Throws<ArgumentException>(() => ProjectVersion.Write("<Project></Project>", version));
    }
}
