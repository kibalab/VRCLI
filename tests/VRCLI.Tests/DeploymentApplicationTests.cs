using System.Text.Json;
using KibaLab.WorldDeployment;

namespace WorldDeployment.Tests;

public sealed class DeploymentApplicationTests
{
    [Fact]
    public async Task JsonModeReturnsOneResultForArgumentFailure()
    {
        StringWriter output = new();
        StringWriter error = new();
        DeploymentApplication application = new(output, error);

        int exitCode = await application.RunAsync(
            ["deploy", "--json", "--unknown"],
            CancellationToken.None);

        Assert.Equal(ExitCodes.InvalidArguments, exitCode);
        using JsonDocument result = JsonDocument.Parse(output.ToString());
        Assert.False(result.RootElement.GetProperty("Success").GetBoolean());
        Assert.Equal("arguments", result.RootElement.GetProperty("Stage").GetString());
        Assert.DoesNotContain("VRCLI:", output.ToString());
        Assert.Contains("Unknown option", error.ToString());
    }

    [Fact]
    public async Task JsonModeReturnsOneResultForInvalidProject()
    {
        string missingProject = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        StringWriter output = new();
        StringWriter error = new();
        DeploymentApplication application = new(output, error);

        int exitCode = await application.RunAsync(
            [
                "deploy",
                "--json",
                "--project", missingProject,
                "--login", "example",
                "--password", "example"
            ],
            CancellationToken.None);

        Assert.Equal(ExitCodes.ProjectInvalid, exitCode);
        using JsonDocument result = JsonDocument.Parse(output.ToString());
        Assert.Equal("project", result.RootElement.GetProperty("Stage").GetString());
        Assert.Equal(ExitCodes.ProjectInvalid, result.RootElement.GetProperty("ExitCode").GetInt32());
        Assert.DoesNotContain(Branding.LogoText, output.ToString());
    }
}
