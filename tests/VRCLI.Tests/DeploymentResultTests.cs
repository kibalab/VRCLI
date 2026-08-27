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
}
