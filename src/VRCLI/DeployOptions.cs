namespace KibaLab.WorldDeployment;

public sealed record DeployOptions(
    OperationMode Operation,
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
    IReadOnlyList<string> RemovedTags,
    bool HasRemovedTags,
    string? BlueprintOutputPath,
    string Username,
    string Password,
    BuildPlatform Platform,
    string? ScenePath,
    string? TargetPath,
    string? UnityPath,
    string? TwoFactorCode,
    string? TwoFactorMethod,
    string? TotpSecret,
    bool InteractiveTwoFactor,
    TimeSpan Timeout,
    bool SkipVpmResolve,
    bool OwnershipAccepted,
    bool Verbose,
    TerminalMode TerminalMode,
    RecoveryManifest? Recovery = null);

public enum OperationMode
{
    Deploy,
    Meta,
    Check
}

public enum BuildPlatform
{
    StandaloneWindows64,
    Android
}

public enum TerminalMode
{
    Auto,
    Tui,
    Plain,
    Json
}
