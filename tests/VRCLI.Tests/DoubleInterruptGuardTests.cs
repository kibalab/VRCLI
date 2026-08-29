using KibaLab.WorldDeployment;

namespace WorldDeployment.Tests;

public sealed class DoubleInterruptGuardTests
{
    [Fact]
    public void SecondInterruptInsideWindowCancels()
    {
        DoubleInterruptGuard guard = new(TimeSpan.FromSeconds(30));
        DateTimeOffset start = DateTimeOffset.UtcNow;

        Assert.False(guard.Register(start));
        Assert.True(guard.Register(start.AddSeconds(29)));
    }

    [Fact]
    public void ExpiredInterruptStartsANewWindow()
    {
        DoubleInterruptGuard guard = new(TimeSpan.FromSeconds(30));
        DateTimeOffset start = DateTimeOffset.UtcNow;

        Assert.False(guard.Register(start));
        Assert.False(guard.Register(start.AddSeconds(31)));
        Assert.True(guard.Register(start.AddSeconds(32)));
    }

    [Fact]
    public void SuccessfulPairResetsTheGuard()
    {
        DoubleInterruptGuard guard = new(TimeSpan.FromSeconds(30));
        DateTimeOffset start = DateTimeOffset.UtcNow;

        Assert.False(guard.Register(start));
        Assert.True(guard.Register(start.AddSeconds(1)));
        Assert.False(guard.Register(start.AddSeconds(2)));
    }
}
