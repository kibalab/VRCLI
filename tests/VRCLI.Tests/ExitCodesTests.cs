using KibaLab.WorldDeployment;

namespace WorldDeployment.Tests;

public sealed class ExitCodesTests
{
    [Fact]
    public void CancellationIsDistinctFromTimeout()
    {
        Assert.Equal(124, ExitCodes.TimedOut);
        Assert.Equal(130, ExitCodes.Canceled);
        Assert.NotEqual(ExitCodes.TimedOut, ExitCodes.Canceled);
    }
}
