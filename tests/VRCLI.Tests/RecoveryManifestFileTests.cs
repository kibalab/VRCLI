using System.Text.Json;
using KibaLab.WorldDeployment;

namespace WorldDeployment.Tests;

public sealed class RecoveryManifestFileTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "vrcli-recovery-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void RestoresDeploymentSettingsFromARecoveryManifest()
    {
        string project = Path.Combine(root, "project");
        string bundle = Path.Combine(root, "avatar.vrca");
        Directory.CreateDirectory(project);
        File.WriteAllText(bundle, "bundle");
        string manifestPath = Write(new RecoveryManifest
        {
            ProjectPath = project,
            ContentType = "Avatar",
            Blueprint = "avtr_example",
            Title = "Recovered avatar",
            Description = "Description",
            ThumbnailPath = Path.Combine(root, "thumbnail.png"),
            Tags = ["author_tag_test"],
            UpdateTitle = true,
            UpdateDescription = true,
            UpdateThumbnail = true,
            UpdateTags = true,
            Platform = "StandaloneWindows64",
            ScenePath = "Assets/Main.unity",
            TargetPath = "Avatars/KIBA_",
            BundlePath = bundle
        });

        ParseResult parsed = new CommandLineParser().Parse(
        [
            "deploy", "--resume", manifestPath, "--login", "owner", "--password", "password", "--yes", "--plain"
        ]);

        Assert.Null(parsed.Error);
        Assert.NotNull(parsed.Options!.Recovery);
        Assert.Equal(project, parsed.Options.ProjectPath);
        Assert.Equal("avtr_example", parsed.Options.BlueprintId);
        Assert.Equal("Avatars/KIBA_", parsed.Options.TargetPath);
        Assert.Equal("Recovered avatar", parsed.Options.Title);
        Assert.Equal(["author_tag_test"], parsed.Options.Tags);
    }

    [Fact]
    public void RejectsMissingRecoveryBundles()
    {
        string project = Path.Combine(root, "project-missing");
        Directory.CreateDirectory(project);
        string manifestPath = Write(new RecoveryManifest
        {
            ProjectPath = project,
            ContentType = "World",
            Blueprint = "wrld_example",
            Platform = "Android",
            BundlePath = Path.Combine(root, "missing.vrcw"),
            Signature = "signature"
        });

        ParseResult parsed = new CommandLineParser().Parse(
            ["deploy", "--resume", manifestPath, "--login", "owner", "--password", "password"]);

        Assert.Contains("recovery bundle does not exist", parsed.Error);
    }

    private string Write(RecoveryManifest manifest)
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            IncludeFields = true
        }));
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
