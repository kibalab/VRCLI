using KibaLab.WorldDeployment;

namespace WorldDeployment.Tests;

public sealed class CompilerDiagnosticCollectorTests
{
    [Fact]
    public async Task CollectsAndDeduplicatesCSharpDiagnostics()
    {
        CompilerDiagnosticCollector collector = new();
        const string warning = "Assets\\World.cs(4,2): warning CS0618: Old API";
        const string error = "Packages\\Bridge.cs(8,1): error CS0246: Missing type";

        await collector.OnLineAsync(warning, false, CancellationToken.None);
        await collector.OnLineAsync(warning, false, CancellationToken.None);
        await collector.OnLineAsync(error, true, CancellationToken.None);
        await collector.OnLineAsync("ordinary Unity output", false, CancellationToken.None);

        Assert.Equal([warning], collector.Warnings);
        Assert.Equal([error], collector.Errors);
    }
}
