using System.Text.RegularExpressions;

namespace KibaLab.WorldDeployment;

public static partial class ProjectInspector
{
    public static ProjectInspectionResult Inspect(string projectPath, string? scenePath)
    {
        if (!Directory.Exists(projectPath))
        {
            return ProjectInspectionResult.Failure($"Project directory does not exist: {projectPath}");
        }

        string assetsPath = Path.Combine(projectPath, "Assets");
        string packagesPath = Path.Combine(projectPath, "Packages");
        string versionFile = Path.Combine(projectPath, "ProjectSettings", "ProjectVersion.txt");
        string vpmManifest = Path.Combine(packagesPath, "vpm-manifest.json");
        if (!Directory.Exists(assetsPath) || !Directory.Exists(packagesPath) || !File.Exists(versionFile))
        {
            return ProjectInspectionResult.Failure("The path is not a Unity project. Assets, Packages, or ProjectSettings/ProjectVersion.txt is missing.");
        }

        if (!File.Exists(vpmManifest))
        {
            return ProjectInspectionResult.Failure("Packages/vpm-manifest.json is missing. The project is not a VPM project.");
        }

        string versionText = File.ReadAllText(versionFile);
        Match match = UnityVersionRegex().Match(versionText);
        if (!match.Success)
        {
            return ProjectInspectionResult.Failure("Unable to read the Unity version from ProjectVersion.txt.");
        }

        string? normalizedScene = null;
        if (!string.IsNullOrWhiteSpace(scenePath))
        {
            try
            {
                normalizedScene = NormalizeScenePath(projectPath, scenePath);
            }
            catch (ArgumentException exception)
            {
                return ProjectInspectionResult.Failure(exception.Message);
            }

            string sceneFile = Path.Combine(projectPath, normalizedScene.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(sceneFile))
            {
                return ProjectInspectionResult.Failure($"Scene does not exist: {normalizedScene}");
            }
        }
        else
        {
            try
            {
                normalizedScene = ResolveDefaultScene(projectPath);
            }
            catch (InvalidOperationException exception)
            {
                return ProjectInspectionResult.Failure(exception.Message);
            }
        }

        return ProjectInspectionResult.Success(match.Groups[1].Value, normalizedScene);
    }

    public static string NormalizeScenePath(string projectPath, string scenePath)
    {
        string normalized = scenePath.Replace('\\', '/');
        if (Path.IsPathRooted(scenePath))
        {
            string fullProjectPath = Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string fullScenePath = Path.GetFullPath(scenePath);
            string relative = Path.GetRelativePath(fullProjectPath, fullScenePath).Replace('\\', '/');
            normalized = relative;
        }

        if (!normalized.StartsWith("Assets/", StringComparison.Ordinal) || !normalized.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("--scene must identify a .unity scene inside the project's Assets directory.");
        }

        if (normalized.Split('/').Any(part => part == ".."))
        {
            throw new ArgumentException("--scene cannot point outside the project.");
        }

        return normalized;
    }

    public static IReadOnlyList<string> FindProjectScenes(string projectPath)
    {
        string assetsPath = Path.Combine(projectPath, "Assets");
        if (!Directory.Exists(assetsPath)) return [];

        return Directory.EnumerateFiles(assetsPath, "*.unity", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(projectPath, path).Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string? FindFirstEnabledScene(string projectPath)
    {
        string settingsPath = Path.Combine(projectPath, "ProjectSettings", "EditorBuildSettings.asset");
        if (!File.Exists(settingsPath)) return null;

        bool enabled = false;
        foreach (string line in File.ReadLines(settingsPath))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("- enabled:", StringComparison.Ordinal))
            {
                enabled = trimmed["- enabled:".Length..].Trim() == "1";
                continue;
            }

            if (enabled && trimmed.StartsWith("path:", StringComparison.Ordinal))
            {
                string path = trimmed["path:".Length..].Trim().Trim('"', '\'');
                if (!string.IsNullOrWhiteSpace(path)) return path.Replace('\\', '/');
            }
        }
        return null;
    }

    private static string ResolveDefaultScene(string projectPath)
    {
        string? enabledScene = FindFirstEnabledScene(projectPath);
        if (!string.IsNullOrWhiteSpace(enabledScene))
        {
            string normalized = NormalizeScenePath(projectPath, enabledScene);
            string fullPath = Path.Combine(projectPath, normalized.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
            {
                throw new InvalidOperationException(
                    $"The first enabled build scene does not exist: {normalized}. Fix Editor Build Settings or use --scene.");
            }
            return normalized;
        }

        IReadOnlyList<string> scenes = FindProjectScenes(projectPath);
        if (scenes.Count == 1) return scenes[0];
        if (scenes.Count == 0)
        {
            throw new InvalidOperationException(
                "No scene was specified, Editor Build Settings has no enabled scene, and no .unity scene exists under Assets. Use --scene after creating a world scene.");
        }

        string examples = string.Join(", ", scenes.Take(3));
        throw new InvalidOperationException(
            $"No enabled build scene was found and the project contains multiple scenes ({examples}). Use --scene to choose one explicitly.");
    }

    [GeneratedRegex(@"(?m)^m_EditorVersion:\s*([^\r\n]+)\s*$")]
    private static partial Regex UnityVersionRegex();
}

public sealed record ProjectInspectionResult(bool IsValid, string? UnityVersion, string? ScenePath, string? Error)
{
    public static ProjectInspectionResult Success(string unityVersion, string? scenePath) => new(true, unityVersion, scenePath, null);
    public static ProjectInspectionResult Failure(string error) => new(false, null, null, error);
}
