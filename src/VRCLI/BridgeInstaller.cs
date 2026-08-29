using System.Buffers;

namespace KibaLab.WorldDeployment;

public static class BridgeInstaller
{
    public static string InstallIfMissing(string projectPath, string applicationDirectory)
    {
        string destination = Path.Combine(projectPath, "Packages", "com.kibalab.vrcli");
        string staging = destination + ".installing";
        string backup = destination + ".backup";
        string destinationManifest = Path.Combine(destination, "package.json");
        string source = Path.Combine(applicationDirectory, "UnityBridge");
        string sourceManifest = Path.Combine(source, "package.json");
        if (!File.Exists(sourceManifest))
        {
            throw new InvalidOperationException($"Bundled Unity bridge was not found at {source}. Re-publish or reinstall VRCLI.");
        }

        RecoverInterruptedSwap(destination, staging, backup);

        if (File.Exists(destinationManifest))
        {
            ReplaceAtomically(source, destination, staging, backup);
            return destination;
        }

        if (Directory.Exists(destination))
        {
            throw new InvalidOperationException($"A partial or invalid Unity bridge directory already exists at {destination}. Remove it and retry.");
        }

        try
        {
            Synchronize(source, staging);
            Directory.Move(staging, destination);
            return destination;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    private static void ReplaceAtomically(
        string source,
        string destination,
        string staging,
        string backup)
    {
        Synchronize(source, staging);
        Directory.Move(destination, backup);
        try
        {
            Directory.Move(staging, destination);
        }
        catch
        {
            if (!Directory.Exists(destination) && Directory.Exists(backup))
                Directory.Move(backup, destination);
            throw;
        }

        try
        {
            Directory.Delete(backup, true);
        }
        catch
        {
            // The completed destination is authoritative. A later run removes the stale backup.
        }
    }

    private static void RecoverInterruptedSwap(string destination, string staging, string backup)
    {
        if (!Directory.Exists(destination) && Directory.Exists(backup))
            Directory.Move(backup, destination);
        else if (Directory.Exists(destination) && Directory.Exists(backup))
            Directory.Delete(backup, true);

        if (Directory.Exists(staging)) Directory.Delete(staging, true);
    }

    private static void Synchronize(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        HashSet<string> sourceFiles = new(StringComparer.OrdinalIgnoreCase);

        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(source, file);
            sourceFiles.Add(relativePath);
            string destinationFile = Path.Combine(destination, relativePath);
            if (File.Exists(destinationFile) && FilesEqual(file, destinationFile)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.Copy(file, destinationFile, true);
        }

        foreach (string file in Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories))
        {
            if (!sourceFiles.Contains(Path.GetRelativePath(destination, file))) File.Delete(file);
        }

        foreach (string directory in Directory.EnumerateDirectories(destination, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
    }

    private static bool FilesEqual(string left, string right)
    {
        FileInfo leftInfo = new(left);
        FileInfo rightInfo = new(right);
        if (leftInfo.Length != rightInfo.Length) return false;

        const int bufferSize = 81920;
        byte[] leftBuffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        byte[] rightBuffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            using FileStream leftStream = File.OpenRead(left);
            using FileStream rightStream = File.OpenRead(right);
            while (true)
            {
                int leftRead = leftStream.Read(leftBuffer, 0, bufferSize);
                int rightRead = rightStream.Read(rightBuffer, 0, bufferSize);
                if (leftRead != rightRead) return false;
                if (leftRead == 0) return true;
                if (!leftBuffer.AsSpan(0, leftRead).SequenceEqual(rightBuffer.AsSpan(0, rightRead))) return false;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(leftBuffer);
            ArrayPool<byte>.Shared.Return(rightBuffer);
        }
    }
}
