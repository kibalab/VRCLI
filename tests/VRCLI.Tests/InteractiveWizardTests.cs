using KibaLab.WorldDeployment;

namespace WorldDeployment.Tests;

public sealed class InteractiveWizardTests
{
    [Theory]
    [InlineData("deploy")]
    [InlineData("deploy", "--tui")]
    [InlineData("meta")]
    [InlineData("meta", "--tui")]
    [InlineData("check")]
    [InlineData("check", "--tui")]
    public void RecognizesWizardInvocation(params string[] args)
    {
        Assert.True(InteractiveWizard.IsWizardInvocation(args));
    }

    [Theory]
    [InlineData("deploy")]
    [InlineData("meta")]
    [InlineData("check")]
    public void DoesNotInterceptConfiguredOperation(string command)
    {
        Assert.False(InteractiveWizard.IsWizardInvocation([command, "--project", "."]));
    }

    [Theory]
    [InlineData("deploy", "Deployment cancelled. No build or upload was started.")]
    [InlineData("meta", "Metadata update cancelled. No world record was changed.")]
    [InlineData("check", "Preflight check cancelled. No build or upload was started.")]
    public void DescribesCancellationForEachOperation(string command, string expected)
    {
        Assert.Equal(expected, InteractiveWizard.CancellationMessage([command]));
    }

}
