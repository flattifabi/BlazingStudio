using BlazorStudio.Deploy;

namespace BlazorStudio.Core.Tests;

public class SiteConfigTests
{
    private const string Log = @"C:\inetpub\deployments\Shop\_logs\stdout";

    private const string Published = """
        <?xml version="1.0" encoding="utf-8"?>
        <configuration>
          <location path="." inheritInChildApplications="false">
            <system.webServer>
              <handlers>
                <add name="aspNetCore" path="*" verb="*" modules="AspNetCoreModuleV2" resourceType="Unspecified" />
              </handlers>
              <aspNetCore processPath="dotnet" arguments=".\Shop.dll" stdoutLogEnabled="false" stdoutLogFile=".\logs\stdout" hostingModel="inprocess" />
            </system.webServer>
          </location>
        </configuration>
        <!--ProjectGuid: 1234-->
        """;

    [Fact]
    public void Stdout_is_enabled_and_log_moved_to_stable_folder()
    {
        var (xml, result) = SiteConfig.EnsureStdout(Published, Log);
        Assert.Equal(StdoutPatch.Updated, result);
        Assert.Contains("stdoutLogEnabled=\"true\"", xml);
        Assert.Contains($"stdoutLogFile=\"{Log}\"", xml);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<configuration>", xml.Replace("\r\n", "\n"));
        Assert.EndsWith("</configuration>\n<!--ProjectGuid: 1234-->", xml.Replace("\r\n", "\n"));
        Assert.Contains("<!--ProjectGuid: 1234-->", xml);
        Assert.Contains("hostingModel=\"inprocess\"", xml);
        // Formatierung bleibt erhalten
        Assert.Contains("\n      <handlers>", xml.Replace("\r\n", "\n"));
        Assert.Null(SiteConfig.Validate("web.config", xml));
    }

    [Fact]
    public void Already_enabled_with_absolute_path_is_unchanged()
    {
        var input = Published.Replace("stdoutLogEnabled=\"false\"", "stdoutLogEnabled=\"true\"").Replace(@".\logs\stdout", @"D:\logs\shop\stdout");
        var (xml, result) = SiteConfig.EnsureStdout(input, Log);
        Assert.Equal(StdoutPatch.Unchanged, result);
        Assert.Same(input, xml);
    }

    [Fact]
    public void Missing_attributes_are_added()
    {
        var (xml, result) = SiteConfig.EnsureStdout("<configuration><system.webServer><aspNetCore processPath=\"x.exe\" /></system.webServer></configuration>", Log);
        Assert.Equal(StdoutPatch.Updated, result);
        Assert.Contains("stdoutLogEnabled=\"true\"", xml);
        Assert.Contains(Log, xml);
    }

    [Fact]
    public void Without_aspNetCore_nothing_changes()
    {
        const string input = "<configuration><system.webServer><defaultDocument /></system.webServer></configuration>";
        Assert.Equal((input, StdoutPatch.NoAspNetCore), SiteConfig.EnsureStdout(input, Log));
    }

    [Theory]
    [InlineData("web.config", true)]
    [InlineData("Web.Config", true)]
    [InlineData("appsettings.json", true)]
    [InlineData("appsettings.Production.json", true)]
    [InlineData("appsettings.my-env_2.json", true)]
    [InlineData("appsettings..json", false)]
    [InlineData("..\\web.config", false)]
    [InlineData("sub/appsettings.json", false)]
    [InlineData("Shop.dll", false)]
    [InlineData("", false)]
    public void Only_config_files_are_editable(string name, bool allowed) => Assert.Equal(allowed, SiteConfig.IsAllowedFile(name));

    [Theory]
    [InlineData("stdout_20261014_101500_1234.log", true)]
    [InlineData("..\\x.log", false)]
    [InlineData("a/b.log", false)]
    [InlineData("stdout.txt", false)]
    public void Log_names_are_checked(string name, bool ok) => Assert.Equal(ok, SiteConfig.IsLogFile(name));

    [Fact]
    public void Validation_reports_broken_files()
    {
        Assert.Null(SiteConfig.Validate("appsettings.json", "{ // Kommentar\n \"A\": 1, }"));
        Assert.NotNull(SiteConfig.Validate("appsettings.json", "{ \"A\": }"));
        Assert.NotNull(SiteConfig.Validate("web.config", "<configuration>"));
        Assert.NotNull(SiteConfig.Validate("web.config", "<foo />"));
        Assert.Null(SiteConfig.Validate("web.config", "<configuration />"));
    }

    [Theory]
    [InlineData(@"C:\logs\stdout", true)]
    [InlineData(@"\\server\share\stdout", true)]
    [InlineData(@"%SystemDrive%\logs\stdout", true)]
    [InlineData(@".\logs\stdout", false)]
    [InlineData(@"logs\stdout", false)]
    public void Absolute_paths(string path, bool absolute) => Assert.Equal(absolute, SiteConfig.IsAbsoluteWindowsPath(path));
}
