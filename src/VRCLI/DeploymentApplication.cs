using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace KibaLab.WorldDeployment;

public sealed class DeploymentApplication(
    TextWriter output,
    TextWriter error,
    string? newBlueprintOverride = null)
{
    private static readonly JsonSerializerOptions ResultJsonOptions = new() { WriteIndented = true };
    private readonly CommandLineParser parser = new();

    public DeploymentResult? LastResult { get; private set; }

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        ParseResult parsed = parser.Parse(args, newBlueprintOverride);
        if (parsed.ShowHelp)
        {
            await output.WriteLineAsync(HelpText);
            return ExitCodes.Success;
        }

        if (parsed.Error != null)
        {
            await error.WriteLineAsync("VRCLI: " + parsed.Error);
            await error.WriteLineAsync("Run 'VRCLI.exe --help' for usage.");
            return ExitCodes.InvalidArguments;
        }

        DeployOptions options = parsed.Options!;
        if (options.ThumbnailPath != null && !File.Exists(options.ThumbnailPath))
        {
            await error.WriteLineAsync($"VRCLI: Thumbnail was not found: {options.ThumbnailPath}");
            return ExitCodes.ProjectInvalid;
        }

        ProjectInspectionResult project = ProjectInspector.Inspect(options.ProjectPath, options.ScenePath);
        if (!project.IsValid)
        {
            await error.WriteLineAsync("VRCLI: " + project.Error);
            return ExitCodes.ProjectInvalid;
        }

        string? unityPath = UnityLocator.Find(project.UnityVersion!, options.UnityPath);
        if (unityPath == null)
        {
            await error.WriteLineAsync($"VRCLI: Unity {project.UnityVersion} was not found. Use --unity or UNITY_EDITOR_PATH.");
            return ExitCodes.ProjectInvalid;
        }

        ConcurrentBag<string> secrets = new(
        [
            options.Password,
            options.TwoFactorCode ?? string.Empty,
            options.TotpSecret ?? string.Empty
        ]);
        TerminalProgressRenderer? terminalUi = null;
        if (TerminalProgressRenderer.ShouldUse(options.TerminalMode, options.Verbose))
        {
            terminalUi = new TerminalProgressRenderer(output, cancellationToken: cancellationToken);
            string projectName = Path.GetFileName(options.ProjectPath.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar));
            string targetName = options.IsNew ? "New world · " + options.Title : options.BlueprintId;
            terminalUi.SetOverview(
                projectName,
                targetName,
                project.ScenePath ?? "First enabled build scene",
                options.Platform.ToString());
        }
        if (options.InteractiveTwoFactor && (terminalUi == null || Console.IsInputRedirected))
        {
            await error.WriteLineAsync(
                "VRCLI: --interactive-two-factor requires a local interactive terminal. " +
                "For CI, set VRCLI_TOTP_SECRET instead.");
            return ExitCodes.InvalidArguments;
        }
        if (terminalUi != null)
        {
            terminalUi.Start();
        }
        await ReportAsync(terminalUi, "BOOT", "Deployment request validated; preparing dependencies and Unity bridge.", true);

        if (!options.SkipVpmResolve)
        {
            try
            {
                int vpmExitCode = await ResolveVpmAsync(options, secrets, terminalUi, cancellationToken);
                if (vpmExitCode != ExitCodes.Success)
                {
                    if (terminalUi != null) await terminalUi.FinishAsync(false);
                    return vpmExitCode;
                }
            }
            catch (OperationCanceledException)
            {
                if (terminalUi != null) await terminalUi.FinishAsync(false);
                await error.WriteLineAsync("VRCLI: Cancelled.");
                return ExitCodes.TimedOut;
            }
        }
        else
        {
            await ReportAsync(terminalUi, "DEPENDENCIES", "VPM dependency restore skipped.", true);
        }

        try
        {
            await ReportAsync(terminalUi, "BRIDGE", "Preparing the Unity bridge package.", true);
            string bridge = BridgeInstaller.InstallIfMissing(options.ProjectPath, AppContext.BaseDirectory);
            await ReportAsync(terminalUi, "BRIDGE", "Unity bridge ready: " + bridge);

            UnityProjectConfigurationResult configuration =
                UnityProjectConfigurator.EnsureVrchatWorldSdkDefines(options.ProjectPath, options.Platform);
            string configurationMessage = configuration.Changed
                ? $"Initialized VRChat SDK defines for {configuration.TargetGroup}: " +
                  string.Join(", ", configuration.AddedDefines)
                : $"VRChat SDK defines ready for {configuration.TargetGroup}.";
            await ReportAsync(terminalUi, "BRIDGE", configurationMessage);
        }
        catch (Exception exception)
        {
            if (terminalUi != null) await terminalUi.FinishAsync(false);
            await error.WriteLineAsync("VRCLI: " + exception.Message);
            return ExitCodes.ProjectInvalid;
        }

        string resultFile = Path.Combine(Path.GetTempPath(), $"vrcli-result-{Guid.NewGuid():N}.json");
        InteractiveTwoFactorServer? twoFactorServer = null;
        CancellationTokenSource? twoFactorCancellation = null;
        Task? twoFactorTask = null;
        try
        {
            await ReportAsync(
                terminalUi,
                "UNITY",
                "Launching Unity and compiling project scripts. Authentication follows compilation.",
                true);
            if (options.InteractiveTwoFactor && terminalUi != null)
            {
                twoFactorServer = new InteractiveTwoFactorServer(terminalUi, secrets);
                twoFactorCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                twoFactorTask = twoFactorServer.RunAsync(twoFactorCancellation.Token);
            }

            ProcessStartInfo unity = CreateUnityStartInfo(
                unityPath,
                options,
                project.ScenePath,
                resultFile,
                twoFactorServer?.PipeName);
            ChildProcessResult result = await ChildProcess.RunAsync(
                unity,
                output,
                error,
                options.Timeout,
                secrets,
                cancellationToken,
                terminalUi);
            if (result.TimedOut)
            {
                if (terminalUi != null) await terminalUi.FinishAsync(false);
                await error.WriteLineAsync($"VRCLI: Unity timed out after {options.Timeout.TotalSeconds:0} seconds.");
                return ExitCodes.TimedOut;
            }

            DeploymentResult? bridgeResult = await ReadResultAsync(resultFile);
            LastResult = bridgeResult;
            if (terminalUi != null) await terminalUi.FinishAsync(bridgeResult?.Success == true);
            if (bridgeResult != null)
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(bridgeResult, ResultJsonOptions));
                if (bridgeResult.Success && options.BlueprintOutputPath != null && !string.IsNullOrWhiteSpace(bridgeResult.WorldId))
                {
                    try
                    {
                        await BlueprintOutputWriter.WriteAsync(options.BlueprintOutputPath, bridgeResult.WorldId);
                        await output.WriteLineAsync($"[VRCLI] Blueprint ID written to {options.BlueprintOutputPath}");
                    }
                    catch (Exception exception)
                    {
                        await error.WriteLineAsync($"VRCLI: Upload succeeded, but the Blueprint output could not be written: {exception.Message}");
                        return ExitCodes.UnexpectedError;
                    }
                }
                return bridgeResult.ExitCode;
            }

            return result.ExitCode == 0 ? ExitCodes.UnexpectedError : result.ExitCode;
        }
        catch (OperationCanceledException)
        {
            if (terminalUi != null) await terminalUi.FinishAsync(false);
            await error.WriteLineAsync("VRCLI: Cancelled.");
            return ExitCodes.TimedOut;
        }
        catch (Exception exception)
        {
            if (terminalUi != null) await terminalUi.FinishAsync(false);
            await error.WriteLineAsync("VRCLI: " + exception.Message);
            return ExitCodes.UnexpectedError;
        }
        finally
        {
            if (twoFactorCancellation != null)
            {
                await twoFactorCancellation.CancelAsync();
                twoFactorCancellation.Dispose();
            }
            if (twoFactorServer != null) await twoFactorServer.DisposeAsync();
            if (twoFactorTask != null)
            {
                try
                {
                    await twoFactorTask;
                }
                catch (OperationCanceledException)
                {
                }
            }
            if (File.Exists(resultFile)) File.Delete(resultFile);
        }
    }

    private async Task<int> ResolveVpmAsync(
        DeployOptions options,
        IReadOnlyCollection<string> secrets,
        TerminalProgressRenderer? terminalUi,
        CancellationToken cancellationToken)
    {
        await ReportAsync(terminalUi, "DEPENDENCIES", "Restoring VPM project dependencies...", true);
        ProcessStartInfo vpm = new("vpm") { WorkingDirectory = options.ProjectPath };
        vpm.ArgumentList.Add("resolve");
        vpm.ArgumentList.Add("project");
        vpm.ArgumentList.Add(options.ProjectPath);

        try
        {
            ChildProcessResult result = await ChildProcess.RunAsync(
                vpm,
                output,
                error,
                TimeSpan.FromMinutes(10),
                secrets,
                cancellationToken,
                terminalUi);
            if (result.ExitCode == 0)
            {
                await ReportAsync(terminalUi, "DEPENDENCIES", "VPM dependency restore completed.");
                return ExitCodes.Success;
            }
            return ExitCodes.DependencyRestoreFailed;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            await error.WriteLineAsync("VRCLI: Unable to run VPM CLI. Install vrchat.vpm.cli or use --skip-vpm-resolve.");
            return ExitCodes.DependencyRestoreFailed;
        }
    }

    private static ProcessStartInfo CreateUnityStartInfo(
        string unityPath,
        DeployOptions options,
        string? scenePath,
        string resultFile,
        string? twoFactorPipe)
    {
        ProcessStartInfo startInfo = new(unityPath) { WorkingDirectory = options.ProjectPath };
        Add(startInfo, "-batchmode");
        Add(startInfo, "-accept-apiupdate");
        Add(startInfo, "-projectPath", options.ProjectPath);
        Add(startInfo, "-buildTarget", options.Platform == BuildPlatform.Android ? "Android" : "Win64");
        Add(startInfo, "-executeMethod", "KibaLab.WorldDeployment.Editor.DeploymentEntryPoint.Run");
        Add(startInfo, "-logFile", "-");

        startInfo.Environment[DeploymentEnvironment.BlueprintId] = options.BlueprintId;
        startInfo.Environment[DeploymentEnvironment.IsNew] = options.IsNew ? "true" : "false";
        startInfo.Environment[DeploymentEnvironment.Username] = options.Username;
        startInfo.Environment[DeploymentEnvironment.Password] = options.Password;
        if (!string.IsNullOrWhiteSpace(options.TwoFactorCode))
            startInfo.Environment[DeploymentEnvironment.TwoFactorCode] = options.TwoFactorCode;
        if (!string.IsNullOrWhiteSpace(options.TotpSecret))
            startInfo.Environment[DeploymentEnvironment.TotpSecret] = options.TotpSecret;
        if (!string.IsNullOrWhiteSpace(twoFactorPipe))
            startInfo.Environment[DeploymentEnvironment.TwoFactorPipe] = twoFactorPipe;
        startInfo.Environment[DeploymentEnvironment.Platform] = options.Platform.ToString();
        startInfo.Environment[DeploymentEnvironment.ResultFile] = resultFile;
        startInfo.Environment[DeploymentEnvironment.OwnershipAccepted] = options.OwnershipAccepted ? "true" : "false";
        if (scenePath != null) startInfo.Environment[DeploymentEnvironment.Scene] = scenePath;
        if (options.Title != null) startInfo.Environment[DeploymentEnvironment.Title] = options.Title;
        if (options.Description != null) startInfo.Environment[DeploymentEnvironment.Description] = options.Description;
        if (options.ThumbnailPath != null) startInfo.Environment[DeploymentEnvironment.Thumbnail] = options.ThumbnailPath;
        if (options.IsNew || options.HasCapacity)
            startInfo.Environment[DeploymentEnvironment.Capacity] = options.Capacity.ToString(CultureInfo.InvariantCulture);
        if (options.IsNew || options.HasRecommendedCapacity)
            startInfo.Environment[DeploymentEnvironment.RecommendedCapacity] = options.RecommendedCapacity.ToString(CultureInfo.InvariantCulture);
        if (options.IsNew || options.HasTags)
            startInfo.Environment[DeploymentEnvironment.Tags] = string.Join("|", options.Tags);
        startInfo.Environment[DeploymentEnvironment.UpdateTitle] = !options.IsNew && options.Title != null ? "true" : "false";
        startInfo.Environment[DeploymentEnvironment.UpdateDescription] = !options.IsNew && options.Description != null ? "true" : "false";
        startInfo.Environment[DeploymentEnvironment.UpdateThumbnail] = !options.IsNew && options.ThumbnailPath != null ? "true" : "false";
        startInfo.Environment[DeploymentEnvironment.UpdateCapacity] = !options.IsNew && options.HasCapacity ? "true" : "false";
        startInfo.Environment[DeploymentEnvironment.UpdateRecommendedCapacity] = !options.IsNew && options.HasRecommendedCapacity ? "true" : "false";
        startInfo.Environment[DeploymentEnvironment.UpdateTags] = !options.IsNew && options.HasTags ? "true" : "false";
        return startInfo;
    }

    private static void Add(ProcessStartInfo startInfo, params string[] values)
    {
        foreach (string value in values) startInfo.ArgumentList.Add(value);
    }

    private Task ReportAsync(
        TerminalProgressRenderer? terminalUi,
        string area,
        string message,
        bool startsPhase = false)
    {
        if (terminalUi != null)
        {
            terminalUi.Report(area, message, startsPhase);
            return Task.CompletedTask;
        }

        return output.WriteLineAsync($"[VRCLI][{area}] {message}");
    }

    private static async Task<DeploymentResult?> ReadResultAsync(string resultFile)
    {
        if (!File.Exists(resultFile)) return null;
        await using FileStream stream = File.OpenRead(resultFile);
        return await JsonSerializer.DeserializeAsync<DeploymentResult>(stream);
    }

    public const string HelpText = """
VRCLI parameters

  --project <directory>          Unity project directory; default current directory
  --blueprint <wrld_id>          Upload an existing world Blueprint
  --new                          Create and upload a new private world
  --scene <Assets/...unity>      Scene to build; auto-detected when unambiguous
  --platform <platform>          StandaloneWindows64 or Android; default StandaloneWindows64
  --login <username-or-email>    VRChat account; default VRCLI_USERNAME
  --password <password>          Account password; default VRCLI_PASSWORD; visible in shell history
  --title <name>                 World display name; required with --new
  --description <text>           Set the world description
  --thumbnail <image>            Required for a new world; replaces an existing thumbnail
  --capacity <1+>                Set the maximum player capacity; new-world default 32
  --recommended-capacity <1+>    Set the recommended player capacity; new-world default 16
  --tag <tag>                    Repeatable; merged with existing tags when updating
  --blueprint-output <file>      Save a newly generated wrld_ ID
  --config <file>                Configuration file; default ./vrcli.json when present
  --plain                        Append-only output for scripts and CI
  --yes                          Certify content ownership when required
  --tui                          Force the interactive terminal display
  --unity <Unity.exe>            Override Unity executable discovery
  --timeout <seconds>            Deployment timeout; default 3600
  --skip-vpm-resolve             Do not resolve VPM dependencies
  --verbose                      Print detailed logs
  --help                         Show this parameter list
""";
}

public sealed record DeploymentResult(
    bool Success,
    int ExitCode,
    string? WorldId,
    bool Created,
    string? Platform,
    string? Stage,
    string? Message);
