using KibaLab.WorldDeployment;

namespace WorldDeployment.Tests;

public sealed class BridgeInstallerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "vrcli-bridge-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void InstallsBundledBridgeIntoProjectPackages()
    {
        string applicationDirectory = Path.Combine(root, "app");
        string source = Path.Combine(applicationDirectory, "UnityBridge");
        string project = Path.Combine(root, "project");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(Path.Combine(project, "Packages"));
        File.WriteAllText(Path.Combine(source, "package.json"), "{}");
        File.WriteAllText(Path.Combine(source, "Bridge.cs"), "// bridge");

        string installed = BridgeInstaller.InstallIfMissing(project, applicationDirectory);

        Assert.Equal(Path.Combine(project, "Packages", "com.kibalab.vrcli"), installed);
        Assert.True(File.Exists(Path.Combine(installed, "Bridge.cs")));
    }

    [Fact]
    public void UpdatesChangedBundledBridgeFiles()
    {
        string applicationDirectory = Path.Combine(root, "app-update");
        string source = Path.Combine(applicationDirectory, "UnityBridge");
        string project = Path.Combine(root, "project-update");
        string installed = Path.Combine(project, "Packages", "com.kibalab.vrcli");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(installed);
        File.WriteAllText(Path.Combine(source, "package.json"), "{}");
        File.WriteAllText(Path.Combine(source, "Bridge.cs"), "// new");
        File.WriteAllText(Path.Combine(installed, "package.json"), "{}");
        File.WriteAllText(Path.Combine(installed, "Bridge.cs"), "// old");

        BridgeInstaller.InstallIfMissing(project, applicationDirectory);

        Assert.Equal("// new", File.ReadAllText(Path.Combine(installed, "Bridge.cs")));
    }

    [Fact]
    public void RemovesFilesThatAreNoLongerBundled()
    {
        string applicationDirectory = Path.Combine(root, "app-sync");
        string source = Path.Combine(applicationDirectory, "UnityBridge");
        string project = Path.Combine(root, "project-sync");
        string installed = Path.Combine(project, "Packages", "com.kibalab.vrcli");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(installed);
        File.WriteAllText(Path.Combine(source, "package.json"), "{}");
        File.WriteAllText(Path.Combine(installed, "package.json"), "{}");
        File.WriteAllText(Path.Combine(installed, "Obsolete.cs"), "// obsolete");

        BridgeInstaller.InstallIfMissing(project, applicationDirectory);

        Assert.False(File.Exists(Path.Combine(installed, "Obsolete.cs")));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
