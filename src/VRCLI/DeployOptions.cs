namespace KibaLab.VRCLI;

public sealed record DeployOptions(
    string ProjectPath,
    string BlueprintId,
    bool CreateWorld,
    string? WorldName,
    string? WorldDescription,
    string? ThumbnailPath,
    int Capacity,
    int RecommendedCapacity,
    bool CapacitySpecified,
    bool RecommendedCapacitySpecified,
    IReadOnlyList<string> Tags,
    bool TagsSpecified,
    string? BlueprintOutputPath,
    string Username,
    string Password,
    VrcliPlatform Platform,
    string? ScenePath,
    string? UnityPath,
    string? TwoFactorCode,
    string? TotpSecret,
    bool InteractiveTwoFactor,
    TimeSpan Timeout,
    bool SkipVpmResolve,
    bool AcceptContentOwnership,
    bool Verbose,
    TerminalMode TerminalMode);

public enum VrcliPlatform
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
