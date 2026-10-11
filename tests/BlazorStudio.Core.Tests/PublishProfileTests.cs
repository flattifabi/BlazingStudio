using BlazorStudio.Core.Build;

namespace BlazorStudio.Core.Tests;

public class PublishProfileTests
{
    [Fact]
    public void Saves_and_loads_visual_studio_compatible_pubxml_and_keeps_unknown_properties()
    {
        var dir = Directory.CreateTempSubdirectory("bs-pub");
        try
        {
            var csproj = Path.Combine(dir.FullName, "Web.csproj");
            File.WriteAllText(csproj, "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />");
            var profilesDir = PublishProfile.ProfilesDirectory(csproj);
            Directory.CreateDirectory(profilesDir);
            File.WriteAllText(Path.Combine(profilesDir, "Existing.pubxml"), """
                <?xml version="1.0" encoding="utf-8"?>
                <Project>
                  <PropertyGroup>
                    <WebPublishMethod>FileSystem</WebPublishMethod>
                    <PublishUrl>C:\out\</PublishUrl>
                    <LastUsedBuildConfiguration>Release</LastUsedBuildConfiguration>
                    <ProjectGuid>abc</ProjectGuid>
                  </PropertyGroup>
                </Project>
                """);

            var existing = Assert.Single(PublishProfile.LoadAll(csproj));
            Assert.Equal(PublishTarget.Folder, existing.Target);
            Assert.Equal(@"C:\out\", existing.OutputPath);
            existing.RuntimeIdentifier = "linux-x64";
            existing.SelfContained = true;
            existing.SingleFile = true;
            existing.Save(csproj);
            var xml = File.ReadAllText(existing.FilePath!);
            Assert.Contains("<ProjectGuid>abc</ProjectGuid>", xml);
            Assert.Contains("<RuntimeIdentifier>linux-x64</RuntimeIdentifier>", xml);
            Assert.Contains("<PublishSingleFile>true</PublishSingleFile>", xml);

            var args = existing.BuildArguments(csproj);
            Assert.Contains("-p:PublishProfile=Existing", args);
            Assert.Contains("--self-contained", args);

            var docker = new PublishProfile { Name = "Docker", Target = PublishTarget.Container, ContainerRepository = "shop", ContainerTag = "1.0" };
            docker.Save(csproj);
            var reloaded = PublishProfile.LoadAll(csproj).Single(p => p.Name == "Docker");
            Assert.Equal(PublishTarget.Container, reloaded.Target);
            Assert.Contains("-t:PublishContainer", reloaded.BuildArguments(csproj));
        }
        finally
        {
            dir.Delete(true);
        }
    }
}
