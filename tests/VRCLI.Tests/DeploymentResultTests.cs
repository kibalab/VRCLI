using System.Text.Json;
using KibaLab.WorldDeployment;

namespace WorldDeployment.Tests;

public sealed class DeploymentResultTests
{
    [Fact]
    public void SerializesWorldTargetAsBlueprint()
    {
        DeploymentResult result = new(
            true,
            ExitCodes.Success,
            "wrld_example",
            false,
            "StandaloneWindows64",
            "complete",
            "Deployment completed.");

        string json = JsonSerializer.Serialize(result);

        Assert.Contains("\"Blueprint\":\"wrld_example\"", json);
        Assert.DoesNotContain("WorldId", json);
    }

    [Fact]
    public void SerializesDetectedAvatarContentType()
    {
        DeploymentResult result = new(
            true,
            ExitCodes.Success,
            "avtr_example",
            true,
            "Android",
            "complete",
            "Avatar created.",
            ContentType: "Avatar");

        string json = JsonSerializer.Serialize(result);

        Assert.Contains("\"Blueprint\":\"avtr_example\"", json);
        Assert.Contains("\"ContentType\":\"Avatar\"", json);
    }

    [Fact]
    public void SerializesAvatarCandidatesForNonInteractiveSelection()
    {
        DeploymentResult result = new(
            false,
            ExitCodes.InvalidArguments,
            null,
            false,
            "StandaloneWindows64",
            "target-selection",
            "Multiple avatars were found.",
            ContentType: "Avatar",
            Targets:
            [
                new ContentTarget("KIBA_", "Avatars/KIBA_", "avtr_existing"),
                new ContentTarget("Fallback", "Avatars/Fallback", null)
            ]);

        string json = JsonSerializer.Serialize(result);

        Assert.Contains("\"Selector\":\"Avatars/KIBA_\"", json);
        Assert.Contains("\"Blueprint\":\"avtr_existing\"", json);
        Assert.Contains("\"Selector\":\"Avatars/Fallback\"", json);
    }
}
