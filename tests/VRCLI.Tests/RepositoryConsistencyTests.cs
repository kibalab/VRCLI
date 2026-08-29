using System.Text.Json;
using System.Xml.Linq;

namespace KibaLab.WorldDeployment.Tests;

public sealed class RepositoryConsistencyTests
{
    [Fact]
    public void CliAndUnityPackageVersionsMatch()
    {
        string root = FindRepositoryRoot();
        XDocument buildProperties = XDocument.Load(Path.Combine(root, "Directory.Build.props"));
        string? cliVersion = buildProperties.Descendants("VersionPrefix").SingleOrDefault()?.Value;

        using JsonDocument package = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(root, "Packages", "com.kibalab.vrcli", "package.json")));
        string? packageVersion = package.RootElement.GetProperty("version").GetString();

        Assert.False(string.IsNullOrWhiteSpace(cliVersion));
        Assert.Equal(cliVersion, packageVersion);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")) &&
                File.Exists(Path.Combine(directory.FullName, "VRCLI.sln")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the repository root.");
    }
}
