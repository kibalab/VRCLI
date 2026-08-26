using KibaLab.WorldDeployment;

namespace WorldDeployment.Tests;

public sealed class InteractiveDeployWizardTests
{
    [Theory]
    [InlineData("deploy")]
    [InlineData("deploy", "--tui")]
    public void RecognizesWizardInvocation(params string[] args)
    {
        Assert.True(InteractiveDeployWizard.IsWizardInvocation(args));
    }

    [Fact]
    public void DoesNotInterceptConfiguredDeployment()
    {
        Assert.False(InteractiveDeployWizard.IsWizardInvocation(["deploy", "--project", "."]));
    }

}
