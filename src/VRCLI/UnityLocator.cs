namespace KibaLab.WorldDeployment;

public static class UnityLocator
{
    public static string? Find(string version, string? explicitPath)
    {
        return Candidates(
                version,
                explicitPath,
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                OperatingSystem.IsMacOS())
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Select(Path.GetFullPath)
            .FirstOrDefault(File.Exists);
    }

    internal static IReadOnlyList<string> Candidates(
        string version,
        string? explicitPath,
        string programFiles,
        string userProfile,
        bool isMacOS)
    {
        List<string?> candidates = new()
        {
            explicitPath,
            Environment.GetEnvironmentVariable("UNITY_EDITOR_PATH")
        };
        if (isMacOS)
        {
            candidates.Add(Path.Combine(
                Path.DirectorySeparatorChar.ToString(),
                "Applications", "Unity", "Hub", "Editor", version,
                "Unity.app", "Contents", "MacOS", "Unity"));
            candidates.Add(Path.Combine(
                userProfile, "Applications", "Unity", "Hub", "Editor", version,
                "Unity.app", "Contents", "MacOS", "Unity"));
        }
        else
        {
            candidates.Add(Path.Combine(programFiles, "Unity", "Hub", "Editor", version, "Editor", "Unity.exe"));
            candidates.Add(Path.Combine(programFiles, "Unity", "Editor", "Unity.exe"));
        }
        return candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Select(candidate => candidate!)
            .ToArray();
    }
}
