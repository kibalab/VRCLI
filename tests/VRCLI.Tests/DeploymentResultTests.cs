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

    [Fact]
    public void SerializesBuildProvenanceAndPhaseTimings()
    {
        DeploymentResult result = new(
            true,
            ExitCodes.Success,
            "wrld_example",
            false,
            "StandaloneWindows64",
            "complete",
            "Uploaded.",
            ContentType: "World",
            VrcliVersion: "0.19.0",
            UnityVersion: "2022.3.22f1",
            SdkVersion: "3.10.1",
            DurationMs: 1250,
            PhaseTimings: [new PhaseTiming("BUILD", 900)],
            Artifact: new BuildArtifact("bundle.vrcw", 1234, "abc123"),
            PreviousVersion: 7,
            ServerVersion: 8);

        string json = JsonSerializer.Serialize(result);

        Assert.Contains("\"VrcliVersion\":\"0.19.0\"", json);
        Assert.Contains("\"Phase\":\"BUILD\"", json);
        Assert.Contains("\"Sha256\":\"abc123\"", json);
        Assert.Contains("\"PreviousVersion\":7", json);
        Assert.Contains("\"ServerVersion\":8", json);
    }
}
