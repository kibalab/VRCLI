namespace KibaLab.WorldDeployment;

public sealed record DeployOptions(
    string ProjectPath,
    string BlueprintId,
    bool IsNew,
    string? Title,
    string? Description,
    string? ThumbnailPath,
    int Capacity,
    int RecommendedCapacity,
    bool HasCapacity,
    bool HasRecommendedCapacity,
    IReadOnlyList<string> Tags,
    bool HasTags,
    string? BlueprintOutputPath,
    string Username,
    string Password,
    BuildPlatform Platform,
    string? ScenePath,
    string? UnityPath,
    string? TwoFactorCode,
    string? TotpSecret,
    bool InteractiveTwoFactor,
    TimeSpan Timeout,
    bool SkipVpmResolve,
    bool OwnershipAccepted,
    bool Verbose,
    TerminalMode TerminalMode);

public enum BuildPlatform
{
    StandaloneWindows64,
    Android
}

public enum TerminalMode
{
    Auto,
    Tui,
    Plain
}
