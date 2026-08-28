using System.Text;

namespace KibaLab.WorldDeployment;

public static class UnityProjectConfigurator
{
    private static readonly string[] RequiredWorldDefines = ["UDON", "VRC_SDK_VRCSDK3"];

    public static UnityProjectConfigurationResult EnsureVrchatSdkConfiguration(
        string projectPath,
        BuildPlatform platform,
        ProjectContentType contentType)
    {
        EnsureSdkEditorAssemblyExists(projectPath, contentType);

        string settingsPath = Path.Combine(projectPath, "ProjectSettings", "ProjectSettings.asset");
        if (!File.Exists(settingsPath))
        {
            throw new InvalidOperationException("ProjectSettings/ProjectSettings.asset is missing.");
        }

        byte[] originalBytes = File.ReadAllBytes(settingsPath);
        bool hasBom = originalBytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
        string original = Encoding.UTF8.GetString(
            originalBytes,
            hasBom ? Encoding.UTF8.Preamble.Length : 0,
            originalBytes.Length - (hasBom ? Encoding.UTF8.Preamble.Length : 0));
        string newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        bool endsWithNewline = original.EndsWith(newline, StringComparison.Ordinal);
        List<string> lines = original.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        if (endsWithNewline && lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);

        int defineLine = lines.FindIndex(line => line.StartsWith("  scriptingDefineSymbols:", StringComparison.Ordinal));
        if (defineLine < 0)
        {
            throw new InvalidOperationException(
                "Unable to locate scriptingDefineSymbols in ProjectSettings/ProjectSettings.asset.");
        }

        string target = platform == BuildPlatform.Android ? "Android" : "Standalone";
        string targetPrefix = "    " + target + ":";
        int nextSection = FindNextSection(lines, defineLine + 1);
        int targetLine = -1;
        for (int index = defineLine + 1; index < nextSection; index++)
        {
            if (lines[index].StartsWith(targetPrefix, StringComparison.Ordinal))
            {
                targetLine = index;
                break;
            }
        }

        List<string> existing = targetLine >= 0
            ? ParseDefines(lines[targetLine][targetPrefix.Length..])
            : [];
        string[] requiredDefines = contentType == ProjectContentType.World
            ? RequiredWorldDefines
            : ["VRC_SDK_VRCSDK3"];
        string[] added = requiredDefines
            .Where(required => !existing.Contains(required, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        if (added.Length == 0)
        {
            return new UnityProjectConfigurationResult(false, target, []);
        }

        existing.AddRange(added);
        string replacement = targetPrefix + " " + string.Join(';', existing);
        if (targetLine >= 0)
        {
            lines[targetLine] = replacement;
        }
        else if (lines[defineLine].TrimEnd().EndsWith("{}", StringComparison.Ordinal))
        {
            lines[defineLine] = "  scriptingDefineSymbols:";
            lines.Insert(defineLine + 1, replacement);
        }
        else
        {
            lines.Insert(nextSection, replacement);
        }

        string updated = string.Join(newline, lines) + (endsWithNewline ? newline : string.Empty);
        string temporaryPath = settingsPath + ".vrcli-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, updated, new UTF8Encoding(hasBom));
            File.Move(temporaryPath, settingsPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }

        return new UnityProjectConfigurationResult(true, target, added);
    }

    private static void EnsureSdkEditorAssemblyExists(string projectPath, ProjectContentType contentType)
    {
        string packageName = contentType == ProjectContentType.World ? "com.vrchat.worlds" : "com.vrchat.avatars";
        string assemblyName = contentType == ProjectContentType.World ? "VRC.SDK3.Editor.asmdef" : "VRC.SDK3A.Editor.asmdef";
        string embedded = Path.Combine(
            projectPath,
            "Packages",
            packageName,
            "Editor",
            "VRCSDK",
            contentType == ProjectContentType.Avatar ? "SDK3A" : string.Empty,
            assemblyName);
        if (File.Exists(embedded)) return;

        string packageCache = Path.Combine(projectPath, "Library", "PackageCache");
        if (Directory.Exists(packageCache) && Directory.EnumerateDirectories(packageCache, packageName + "@*")
                .Any(directory => File.Exists(Path.Combine(
                    directory,
                    "Editor",
                    "VRCSDK",
                    contentType == ProjectContentType.Avatar ? "SDK3A" : string.Empty,
                    assemblyName))))
        {
            return;
        }

        throw new InvalidOperationException(
            "VRChat " + contentType + " SDK editor package is unavailable after VPM restore. " +
            "Install " + packageName + " 3.9.0 or newer in the project and retry.");
    }

    private static int FindNextSection(IReadOnlyList<string> lines, int start)
    {
        for (int index = start; index < lines.Count; index++)
        {
            string line = lines[index];
            if (line.Length >= 2 && line.StartsWith("  ", StringComparison.Ordinal) &&
                (line.Length == 2 || line[2] != ' '))
            {
                return index;
            }
        }
        return lines.Count;
    }

    private static List<string> ParseDefines(string value) => value
        .Trim()
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
}

public sealed record UnityProjectConfigurationResult(
    bool Changed,
    string TargetGroup,
    IReadOnlyList<string> AddedDefines);
