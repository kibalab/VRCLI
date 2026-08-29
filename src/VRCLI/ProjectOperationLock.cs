using System.Text;

namespace KibaLab.WorldDeployment;

public sealed class ProjectOperationLock : IDisposable
{
    private readonly FileStream stream;

    private ProjectOperationLock(FileStream stream)
    {
        this.stream = stream;
    }

    public static ProjectOperationLock Acquire(string projectPath, OperationMode operation)
    {
        string directory = Path.Combine(projectPath, "Library", "VRCLI");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "operation.lock");

        FileStream stream;
        try
        {
            stream = new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough);
        }
        catch (IOException exception)
        {
            throw new ProjectLockedException(
                "Another VRCLI operation is already using this Unity project. " +
                "Run parallel platform jobs in separate project workspaces.",
                exception);
        }

        try
        {
            string owner = $"pid={Environment.ProcessId}\noperation={operation}\nstarted={DateTimeOffset.UtcNow:O}\n";
            byte[] bytes = Encoding.UTF8.GetBytes(owner);
            stream.SetLength(0);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(true);
            return new ProjectOperationLock(stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public void Dispose() => stream.Dispose();
}

public sealed class ProjectLockedException(string message, Exception innerException)
    : IOException(message, innerException);
