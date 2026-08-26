using System.Text;
using KibaLab.VRCLI;

namespace VRCLI.Tests;

public sealed class UnityProjectConfiguratorTests : IDisposable
{
    private readonly string projectPath = Path.Combine(
        Path.GetTempPath(),
        "vrcli-config-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void AddsStandaloneDefinesToEmptySettingsMapAndIsIdempotent()
    {
        CreateProject("  scriptingDefineSymbols: {}\r\n  additionalCompilerArguments: {}\r\n", true);

        UnityProjectConfigurationResult first = UnityProjectConfigurator.EnsureVrchatWorldSdkDefines(
            projectPath,
            VrcliPlatform.StandaloneWindows64);
        UnityProjectConfigurationResult second = UnityProjectConfigurator.EnsureVrchatWorldSdkDefines(
            projectPath,
            VrcliPlatform.StandaloneWindows64);

        string settings = File.ReadAllText(SettingsPath);
        Assert.True(first.Changed);
        Assert.Equal(["UDON", "VRC_SDK_VRCSDK3"], first.AddedDefines);
        Assert.False(second.Changed);
        Assert.Contains("scriptingDefineSymbols:\r\n    Standalone: UDON;VRC_SDK_VRCSDK3\r\n", settings);
        Assert.True(File.ReadAllBytes(SettingsPath).AsSpan().StartsWith(Encoding.UTF8.Preamble));
    }

    [Fact]
    public void PreservesExistingDefinesAndAddsOnlyMissingAndroidDefine()
    {
        CreateProject(
            "  scriptingDefineSymbols:\n" +
            "    Android: UNITY_POST_PROCESSING_STACK_V2;UDON\n" +
            "    Standalone: CUSTOM\n" +
            "  additionalCompilerArguments: {}\n");

        UnityProjectConfigurationResult result = UnityProjectConfigurator.EnsureVrchatWorldSdkDefines(
            projectPath,
            VrcliPlatform.Android);

        string settings = File.ReadAllText(SettingsPath);
        Assert.True(result.Changed);
        Assert.Equal(["VRC_SDK_VRCSDK3"], result.AddedDefines);
        Assert.Contains("Android: UNITY_POST_PROCESSING_STACK_V2;UDON;VRC_SDK_VRCSDK3", settings);
        Assert.Contains("Standalone: CUSTOM", settings);
    }

    [Fact]
    public void FailsClearlyWhenWorldsSdkIsUnavailable()
    {
        Directory.CreateDirectory(Path.Combine(projectPath, "ProjectSettings"));
        File.WriteAllText(SettingsPath, "  scriptingDefineSymbols: {}\n");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            UnityProjectConfigurator.EnsureVrchatWorldSdkDefines(projectPath, VrcliPlatform.Android));

        Assert.Contains("VRChat Worlds SDK", exception.Message);
    }

    private string SettingsPath => Path.Combine(projectPath, "ProjectSettings", "ProjectSettings.asset");

    private void CreateProject(string settings, bool bom = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        Directory.CreateDirectory(Path.Combine(
            projectPath,
            "Packages",
            "com.vrchat.worlds",
            "Editor",
            "VRCSDK"));
        File.WriteAllText(
            Path.Combine(projectPath, "Packages", "com.vrchat.worlds", "Editor", "VRCSDK", "VRC.SDK3.Editor.asmdef"),
            "{}");
        File.WriteAllText(SettingsPath, settings, new UTF8Encoding(bom));
    }

    public void Dispose()
    {
        if (Directory.Exists(projectPath)) Directory.Delete(projectPath, true);
    }
}
