namespace KibaLab.WorldDeployment;

public static class ExitCodes
{
    public const int Success = 0;
    public const int InvalidArguments = 2;
    public const int ProjectInvalid = 10;
    public const int DependencyRestoreFailed = 20;
    public const int AuthenticationFailed = 30;
    public const int BuildFailed = 40;
    public const int UploadFailed = 50;
    public const int OwnershipFailed = 60;
    public const int NetworkFailed = 70;
    public const int TimedOut = 124;
    public const int UnexpectedError = 125;
}

