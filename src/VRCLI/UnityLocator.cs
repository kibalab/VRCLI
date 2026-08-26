namespace KibaLab.WorldDeployment;

public static class UnityLocator
{
    public static string? Find(string version, string? explicitPath)
    {
        IEnumerable<string?> candidates = new[]
        {
            explicitPath,
            Environment.GetEnvironmentVariable("UNITY_EDITOR_PATH"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Unity", "Hub", "Editor", version, "Editor", "Unity.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Unity", "Editor", "Unity.exe")
        };

        return candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Select(candidate => Path.GetFullPath(candidate!))
            .FirstOrDefault(File.Exists);
    }
}

