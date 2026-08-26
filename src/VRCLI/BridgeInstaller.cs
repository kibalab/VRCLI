namespace KibaLab.VRCLI;

public static class BridgeInstaller
{
    public static string InstallIfMissing(string projectPath, string applicationDirectory)
    {
        string destination = Path.Combine(projectPath, "Packages", "com.kibalab.vrcli");
        string destinationManifest = Path.Combine(destination, "package.json");
        string source = Path.Combine(applicationDirectory, "UnityBridge");
        string sourceManifest = Path.Combine(source, "package.json");
        if (!File.Exists(sourceManifest))
        {
            throw new InvalidOperationException($"Bundled Unity bridge was not found at {source}. Re-publish or reinstall VRCLI.");
        }

        if (File.Exists(destinationManifest))
        {
            CopyChangedFiles(source, destination);
            return destination;
        }

        if (Directory.Exists(destination))
        {
            throw new InvalidOperationException($"A partial or invalid Unity bridge directory already exists at {destination}. Remove it and retry.");
        }

        string staging = destination + ".installing-" + Guid.NewGuid().ToString("N");
        try
        {
            CopyDirectory(source, staging);
            Directory.Move(staging, destination);
            return destination;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    private static void CopyChangedFiles(string source, string destination)
    {
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string destinationFile = Path.Combine(destination, Path.GetRelativePath(source, file));
            if (File.Exists(destinationFile) && FilesEqual(file, destinationFile)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.Copy(file, destinationFile, true);
        }
    }

    private static bool FilesEqual(string left, string right)
    {
        FileInfo leftInfo = new(left);
        FileInfo rightInfo = new(right);
        if (leftInfo.Length != rightInfo.Length) return false;

        const int bufferSize = 81920;
        byte[] leftBuffer = new byte[bufferSize];
        byte[] rightBuffer = new byte[bufferSize];
        using FileStream leftStream = File.OpenRead(left);
        using FileStream rightStream = File.OpenRead(right);
        while (true)
        {
            int leftRead = leftStream.Read(leftBuffer, 0, leftBuffer.Length);
            int rightRead = rightStream.Read(rightBuffer, 0, rightBuffer.Length);
            if (leftRead != rightRead) return false;
            if (leftRead == 0) return true;
            if (!leftBuffer.AsSpan(0, leftRead).SequenceEqual(rightBuffer.AsSpan(0, rightRead))) return false;
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string destinationFile = Path.Combine(destination, Path.GetRelativePath(source, file));
            File.Copy(file, destinationFile, false);
        }
    }
}
