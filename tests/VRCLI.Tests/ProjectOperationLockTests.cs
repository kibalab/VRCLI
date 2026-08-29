using KibaLab.WorldDeployment;

namespace WorldDeployment.Tests;

public sealed class ProjectOperationLockTests : IDisposable
{
    private readonly string projectPath = Path.Combine(
        Path.GetTempPath(),
        "vrcli-project-lock-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void RejectsASecondOperationForTheSameProject()
    {
        using ProjectOperationLock first = ProjectOperationLock.Acquire(projectPath, OperationMode.Deploy);

        ProjectLockedException exception = Assert.Throws<ProjectLockedException>(
            () => ProjectOperationLock.Acquire(projectPath, OperationMode.Check));

        Assert.Contains("separate project workspaces", exception.Message);
    }

    [Fact]
    public void AllowsAnotherOperationAfterRelease()
    {
        using (ProjectOperationLock.Acquire(projectPath, OperationMode.Deploy))
        {
        }

        using ProjectOperationLock second = ProjectOperationLock.Acquire(projectPath, OperationMode.Check);
        Assert.True(File.Exists(Path.Combine(projectPath, "Library", "VRCLI", "operation.lock")));
    }

    public void Dispose()
    {
        if (Directory.Exists(projectPath)) Directory.Delete(projectPath, true);
    }
}
