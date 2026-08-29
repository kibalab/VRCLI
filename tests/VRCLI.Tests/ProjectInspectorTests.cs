using KibaLab.WorldDeployment;

namespace WorldDeployment.Tests;

public sealed class ProjectInspectorTests : IDisposable
{
    private readonly string projectPath = Path.Combine(Path.GetTempPath(), "vrcli-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void MetadataInspectionDoesNotRequireAScene()
    {
        CreateProject();

        ProjectInspectionResult result = ProjectInspector.Inspect(projectPath, null, requireScene: false);

        Assert.True(result.IsValid, result.Error);
        Assert.Null(result.ScenePath);
        Assert.Equal(ProjectContentType.World, result.ContentType);
    }

    [Fact]
    public void DetectsAvatarSdkProject()
    {
        CreateProject(ProjectContentType.Avatar);

        ProjectInspectionResult result = ProjectInspector.Inspect(projectPath, null, requireScene: false);

        Assert.True(result.IsValid, result.Error);
        Assert.Equal(ProjectContentType.Avatar, result.ContentType);
    }

    [Fact]
    public void RejectsAmbiguousSdkProject()
    {
        CreateProject();
        File.WriteAllText(
            Path.Combine(projectPath, "Packages", "vpm-manifest.json"),
            "{\"dependencies\":{\"com.vrchat.worlds\":\"3.9.0\",\"com.vrchat.avatars\":\"3.9.0\"}}");

        ProjectInspectionResult result = ProjectInspector.Inspect(projectPath, null, requireScene: false);

        Assert.False(result.IsValid);
        Assert.Contains("Both VRChat Worlds and Avatars", result.Error);
    }

    [Fact]
    public void FindsAvatarBlueprintsInSceneYaml()
    {
        CreateProject(ProjectContentType.Avatar);
        string scene = Path.Combine(projectPath, "Assets", "Scenes", "Avatar.unity");
        File.WriteAllText(scene, "  blueprintId: avtr_one\n  blueprintId: avtr_one\n  blueprintId: avtr_two\n");

        IReadOnlyList<string> blueprints = ProjectInspector.FindSceneBlueprints(
            projectPath,
            "Assets/Scenes/Avatar.unity",
            ProjectContentType.Avatar);

        Assert.Equal(["avtr_one", "avtr_two"], blueprints);
    }

    [Fact]
    public void ReadsUnityVersionAndNormalizesScene()
    {
        CreateProject();
        File.WriteAllText(Path.Combine(projectPath, "Assets", "Scenes", "Main.unity"), string.Empty);

        ProjectInspectionResult result = ProjectInspector.Inspect(projectPath, "Assets\\Scenes\\Main.unity");

        Assert.True(result.IsValid, result.Error);
        Assert.Equal("2022.3.22f1", result.UnityVersion);
        Assert.Equal("Assets/Scenes/Main.unity", result.ScenePath);
    }

    [Fact]
    public void ResolvesFirstEnabledBuildSceneWhenSceneIsOmitted()
    {
        CreateProject();
        File.WriteAllText(Path.Combine(projectPath, "Assets", "Scenes", "Disabled.unity"), string.Empty);
        File.WriteAllText(Path.Combine(projectPath, "Assets", "Scenes", "Main.unity"), string.Empty);
        File.WriteAllText(
            Path.Combine(projectPath, "ProjectSettings", "EditorBuildSettings.asset"),
            "EditorBuildSettings:\n" +
            "  m_Scenes:\n" +
            "  - enabled: 0\n" +
            "    path: Assets/Scenes/Disabled.unity\n" +
            "  - enabled: 1\n" +
            "    path: Assets/Scenes/Main.unity\n");

        ProjectInspectionResult result = ProjectInspector.Inspect(projectPath, null);

        Assert.True(result.IsValid, result.Error);
        Assert.Equal("Assets/Scenes/Main.unity", result.ScenePath);
    }

    [Fact]
    public void ResolvesOnlyProjectSceneWhenBuildSettingsIsEmpty()
    {
        CreateProject();
        File.WriteAllText(Path.Combine(projectPath, "Assets", "Scenes", "Only.unity"), string.Empty);
        File.WriteAllText(
            Path.Combine(projectPath, "ProjectSettings", "EditorBuildSettings.asset"),
            "EditorBuildSettings:\n  m_Scenes: []\n");

        ProjectInspectionResult result = ProjectInspector.Inspect(projectPath, null);

        Assert.True(result.IsValid, result.Error);
        Assert.Equal("Assets/Scenes/Only.unity", result.ScenePath);
    }

    [Fact]
    public void RequiresExplicitSceneWhenMultipleScenesAreNotEnabled()
    {
        CreateProject();
        File.WriteAllText(Path.Combine(projectPath, "Assets", "Scenes", "One.unity"), string.Empty);
        File.WriteAllText(Path.Combine(projectPath, "Assets", "Scenes", "Two.unity"), string.Empty);

        ProjectInspectionResult result = ProjectInspector.Inspect(projectPath, null);

        Assert.False(result.IsValid);
        Assert.Contains("multiple scenes", result.Error);
        Assert.Contains("--scene", result.Error);
    }

    private void CreateProject(ProjectContentType contentType = ProjectContentType.World)
    {
        Directory.CreateDirectory(Path.Combine(projectPath, "Assets", "Scenes"));
        Directory.CreateDirectory(Path.Combine(projectPath, "Packages"));
        Directory.CreateDirectory(Path.Combine(projectPath, "ProjectSettings"));
        string packageName = contentType == ProjectContentType.World ? "com.vrchat.worlds" : "com.vrchat.avatars";
        File.WriteAllText(
            Path.Combine(projectPath, "Packages", "vpm-manifest.json"),
            "{\"dependencies\":{\"" + packageName + "\":\"3.9.0\"}}");
        File.WriteAllText(
            Path.Combine(projectPath, "ProjectSettings", "ProjectVersion.txt"),
            "m_EditorVersion: 2022.3.22f1\n");
    }

    public void Dispose()
    {
        if (Directory.Exists(projectPath)) Directory.Delete(projectPath, true);
    }
}
