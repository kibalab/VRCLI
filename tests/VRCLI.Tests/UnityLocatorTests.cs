using KibaLab.WorldDeployment;

namespace WorldDeployment.Tests;

public sealed class UnityLocatorTests
{
    [Fact]
    public void IncludesSystemAndUserUnityHubPathsOnMacOS()
    {
        IReadOnlyList<string> candidates = UnityLocator.Candidates(
            "2022.3.22f1",
            null,
            "/unused",
            "/Users/kiba",
            isMacOS: true);

        Assert.Contains(
            "/Applications/Unity/Hub/Editor/2022.3.22f1/Unity.app/Contents/MacOS/Unity",
            candidates.Select(path => path.Replace('\\', '/')));
        Assert.Contains(
            "/Users/kiba/Applications/Unity/Hub/Editor/2022.3.22f1/Unity.app/Contents/MacOS/Unity",
            candidates.Select(path => path.Replace('\\', '/')));
        Assert.DoesNotContain(candidates, path => path.EndsWith("Unity.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void IncludesUnityHubPathOnWindows()
    {
        IReadOnlyList<string> candidates = UnityLocator.Candidates(
            "2022.3.22f1",
            null,
            "C:\\Program Files",
            "C:\\Users\\kiba",
            isMacOS: false);

        Assert.Contains(candidates, path => path.Replace('\\', '/').EndsWith(
            "Unity/Hub/Editor/2022.3.22f1/Editor/Unity.exe",
            StringComparison.Ordinal));
    }
}
