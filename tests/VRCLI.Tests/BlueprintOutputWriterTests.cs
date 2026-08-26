using KibaLab.WorldDeployment;

namespace WorldDeployment.Tests;

public sealed class BlueprintOutputWriterTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "vrcli-blueprint-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CreatesDirectoryAndAtomicallyReplacesBlueprint()
    {
        string path = Path.Combine(directory, "nested", "blueprint.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "old-value");

        await BlueprintOutputWriter.WriteAsync(path, "wrld_replacement");

        Assert.Equal("wrld_replacement", (await File.ReadAllTextAsync(path)).Trim());
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp-*"));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
