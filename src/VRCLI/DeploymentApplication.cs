using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace KibaLab.WorldDeployment;

public sealed class DeploymentApplication(
    TextWriter output,
    TextWriter error,
    string? newBlueprintOverride = null,
    Func<IVrchatSessionStore>? sessionStoreFactory = null)
{
    private static readonly JsonSerializerOptions ResultJsonOptions = new() { WriteIndented = true };
    private readonly CommandLineParser parser = new();
    private bool jsonOutput;

    private TextWriter LogOutput => jsonOutput ? error : output;

    public DeploymentResult? LastResult { get; private set; }

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        jsonOutput = args.Any(argument => string.Equals(argument, "--json", StringComparison.OrdinalIgnoreCase));
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
            return await WriteFailureAsync(ExitCodes.InvalidArguments, "arguments", parsed.Error);
        }

        DeployOptions options = parsed.Options!;
        bool useTerminalUi = TerminalProgressRenderer.ShouldUse(options.TerminalMode, options.Verbose);
        if (!useTerminalUi && !jsonOutput) await Branding.WriteAsync(output);

        if (options.Operation == OperationMode.Meta)
        {
            MetadataExecutionResult metadata = await new MetadataApplication(output, error)
                .RunAsync(options, cancellationToken);
            LastResult = metadata.Result;
            return metadata.ExitCode;
        }

        ConcurrentBag<string> secrets = new(
        [
            options.Password,
            options.TwoFactorCode ?? string.Empty,
            options.TotpSecret ?? string.Empty
        ]);
        TerminalProgressRenderer? terminalUi = null;
        if (useTerminalUi)
        {
            terminalUi = new TerminalProgressRenderer(output, options.Operation, cancellationToken: cancellationToken);
            string projectName = Path.GetFileName(options.ProjectPath.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar));
            string targetName = options.IsNew
                ? "New world · " + options.Title
                : !string.IsNullOrWhiteSpace(options.TargetPath) ? options.TargetPath
                : string.IsNullOrWhiteSpace(options.BlueprintId) ? "Auto-detect from scene" : options.BlueprintId;
            terminalUi.SetOverview(
                projectName,
                targetName,
                options.ScenePath ?? "Auto-detect after sign-in",
                options.Platform.ToString());
        }
        if (options.InteractiveTwoFactor && (terminalUi == null || Console.IsInputRedirected))
        {
            await error.WriteLineAsync(
                "VRCLI: --interactive-two-factor requires a local interactive terminal. " +
                "For CI, set VRCLI_TOTP_SECRET instead.");
            return await WriteFailureAsync(
                ExitCodes.InvalidArguments,
                "arguments",
                "--interactive-two-factor requires a local interactive terminal.",
                options);
        }
        if (terminalUi != null)
        {
            terminalUi.Start();
        }

        VrchatUser authenticatedUser;
        VrchatSessionTokens sessionTokens;
        try
        {
            (authenticatedUser, sessionTokens) = await AuthenticateAsync(options, terminalUi, cancellationToken);
            secrets.Add(sessionTokens.AuthToken);
            secrets.Add(sessionTokens.TwoFactorToken ?? string.Empty);
        }
        catch (OperationCanceledException)
        {
            if (terminalUi != null) await terminalUi.FinishAsync(false);
            await error.WriteLineAsync("VRCLI: Cancelled.");
            return await WriteFailureAsync(ExitCodes.Canceled, "cancelled", "Authentication cancelled.", options);
        }
        catch (Exception exception) when (exception is VrchatApiException or HttpRequestException or TaskCanceledException)
        {
            if (terminalUi != null) await terminalUi.FinishAsync(false);
            await error.WriteLineAsync("VRCLI: " + exception.Message);
            return await WriteFailureAsync(ExitCodes.AuthenticationFailed, "authentication", exception.Message, options);
        }

        await ReportAsync(
            terminalUi,
            "AUTH",
            $"Signed in as {authenticatedUser.DisplayName} ({authenticatedUser.Id}).");

        if (options.ThumbnailPath != null && !File.Exists(options.ThumbnailPath))
        {
            string message = $"Thumbnail was not found: {options.ThumbnailPath}";
            if (terminalUi != null) await terminalUi.FinishAsync(false);
            await error.WriteLineAsync("VRCLI: " + message);
            return await WriteFailureAsync(ExitCodes.ProjectInvalid, "project", message, options);
        }

        ProjectInspectionResult project = ProjectInspector.Inspect(
            options.ProjectPath,
            options.ScenePath,
            requireScene: true);
        if (!project.IsValid)
        {
            if (terminalUi != null) await terminalUi.FinishAsync(false);
            await error.WriteLineAsync("VRCLI: " + project.Error);
            return await WriteFailureAsync(ExitCodes.ProjectInvalid, "project", project.Error!, options);
        }

        string? contentOptionError = ValidateContentOptions(options, project.ContentType!.Value);
        if (contentOptionError != null)
        {
            if (terminalUi != null) await terminalUi.FinishAsync(false);
            await error.WriteLineAsync("VRCLI: " + contentOptionError);
            return await WriteFailureAsync(ExitCodes.InvalidArguments, "arguments", contentOptionError, options, project.ContentType);
        }

        string? unityPath = UnityLocator.Find(project.UnityVersion!, options.UnityPath);
        if (unityPath == null)
        {
            string message = $"Unity {project.UnityVersion} was not found. Use --unity or UNITY_EDITOR_PATH.";
            if (terminalUi != null) await terminalUi.FinishAsync(false);
            await error.WriteLineAsync("VRCLI: " + message);
            return await WriteFailureAsync(ExitCodes.ProjectInvalid, "project", message, options);
        }

        ProjectOperationLock operationLock;
        try
        {
            operationLock = ProjectOperationLock.Acquire(options.ProjectPath, options.Operation);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (terminalUi != null) await terminalUi.FinishAsync(false);
            await error.WriteLineAsync("VRCLI: " + exception.Message);
            return await WriteFailureAsync(ExitCodes.ProjectInvalid, "project-lock", exception.Message, options, project.ContentType);
        }
        using ProjectOperationLock heldProjectLock = operationLock;
        await ReportAsync(terminalUi, "BOOT", "Exclusive project operation lock acquired.");

        terminalUi?.SetOverview(
            Path.GetFileName(options.ProjectPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            project.ContentType == ProjectContentType.Avatar
                ? !string.IsNullOrWhiteSpace(options.TargetPath) ? "Avatar · " + options.TargetPath
                : !string.IsNullOrWhiteSpace(options.BlueprintId) ? "Avatar · " + options.BlueprintId
                : "Avatar · Auto-select in Unity" :
            options.IsNew ? "New world · " + options.Title :
            string.IsNullOrWhiteSpace(options.BlueprintId) ? "Scene Blueprint" : options.BlueprintId,
            project.ScenePath ?? "First enabled build scene",
            options.Platform.ToString());
        await ReportAsync(terminalUi, "BOOT", project.ContentType + " " + options.Operation.ToString().ToLowerInvariant() + " request validated; preparing dependencies and Unity bridge.", true);

        if (!options.SkipVpmResolve)
        {
            try
            {
                int vpmExitCode = await ResolveVpmAsync(options, secrets, terminalUi, cancellationToken);
                if (vpmExitCode != ExitCodes.Success)
                {
                    if (terminalUi != null) await terminalUi.FinishAsync(false);
                    return await WriteFailureAsync(
                        vpmExitCode,
                        "dependencies",
                        "VPM dependency restore failed.",
                        options);
                }
            }
            catch (OperationCanceledException)
            {
                if (terminalUi != null) await terminalUi.FinishAsync(false);
                await error.WriteLineAsync("VRCLI: Cancelled.");
                return await WriteFailureAsync(ExitCodes.Canceled, "cancelled", "Operation cancelled.", options);
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
                UnityProjectConfigurator.EnsureVrchatSdkConfiguration(
                    options.ProjectPath,
                    options.Platform,
                    project.ContentType!.Value);
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
            return await WriteFailureAsync(ExitCodes.ProjectInvalid, "project", exception.Message, options);
        }

        string operationId = Guid.NewGuid().ToString("N");
        string recoveryDirectory = options.Recovery != null
            ? Path.GetDirectoryName(options.Recovery.BundlePath)!
            : Path.Combine(options.ProjectPath, "Library", "VRCLI", "recovery", operationId);
        string resultFile = Path.Combine(Path.GetTempPath(), $"vrcli-result-{operationId}.json");
        string? targetRequestFile = terminalUi == null || Console.IsInputRedirected
            ? null
            : Path.Combine(Path.GetTempPath(), $"vrcli-target-request-{operationId}.json");
        string? targetResponseFile = terminalUi == null || Console.IsInputRedirected
            ? null
            : Path.Combine(Path.GetTempPath(), $"vrcli-target-response-{operationId}.txt");
        try
        {
            await ReportAsync(
                terminalUi,
                "UNITY",
                "Launching Unity with the pre-verified VRChat session and compiling project scripts.",
                true);

            ProcessStartInfo unity = CreateUnityStartInfo(
                unityPath,
                options,
                project.ScenePath,
                resultFile,
                sessionTokens,
                project.ContentType!.Value,
                targetRequestFile,
                targetResponseFile,
                recoveryDirectory);
            IProcessLineObserver? terminalObserver = terminalUi;
            if (terminalUi != null && targetRequestFile != null && targetResponseFile != null)
            {
                terminalObserver = new TargetSelectionObserver(
                    targetRequestFile,
                    targetResponseFile,
                    terminalUi,
                    terminalUi);
            }
            CompilerDiagnosticCollector? compilerDiagnostics = options.Operation == OperationMode.Check
                ? new CompilerDiagnosticCollector(terminalObserver)
                : null;
            IProcessLineObserver? processObserver = compilerDiagnostics != null
                ? compilerDiagnostics
                : terminalObserver;
            ChildProcessResult result = await ChildProcess.RunAsync(
                unity,
                LogOutput,
                error,
                options.Timeout,
                secrets,
                cancellationToken,
                processObserver);
            if (result.TimedOut)
            {
                if (terminalUi != null) await terminalUi.FinishAsync(false);
                string message = $"Unity timed out after {options.Timeout.TotalSeconds:0} seconds.";
                await error.WriteLineAsync("VRCLI: " + message);
                return await WriteFailureAsync(ExitCodes.TimedOut, "timeout", message, options);
            }

            DeploymentResult? bridgeResult = await ReadResultAsync(resultFile);
            if (options.Operation == OperationMode.Check && compilerDiagnostics != null)
            {
                string[] compilerErrors = compilerDiagnostics.Errors.ToArray();
                string[] compilerWarnings = compilerDiagnostics.Warnings.ToArray();
                if (bridgeResult == null && compilerErrors.Length > 0)
                {
                    bridgeResult = new DeploymentResult(
                        false,
                        ExitCodes.BuildFailed,
                        string.IsNullOrWhiteSpace(options.BlueprintId) ? null : options.BlueprintId,
                        false,
                        options.Platform.ToString(),
                        "compilation",
                        "Unity compilation failed; SDK validation could not run.",
                        null,
                        null,
                        null,
                        compilerErrors,
                        compilerWarnings,
                        ContentType: project.ContentType.ToString());
                }
                else if (bridgeResult != null)
                {
                    bridgeResult = bridgeResult with
                    {
                        CompilerErrors = compilerErrors,
                        CompilerWarnings = compilerWarnings
                    };
                }
            }
            if (bridgeResult != null)
            {
                if (bridgeResult.Success && options.Operation == OperationMode.Deploy)
                {
                    await ReportAsync(terminalUi, "VERIFY", "Verifying the uploaded platform package with the VRChat server.", true);
                    try
                    {
                        using VrchatApiClient verificationApi = new();
                        await verificationApi.ResumeSessionAsync(sessionTokens, cancellationToken);
                        DeploymentVerification verification = await new DeploymentVerifier().VerifyAsync(
                            verificationApi,
                            bridgeResult,
                            authenticatedUser.Id,
                            options.Platform,
                            message => Report(terminalUi, "VERIFY", message),
                            cancellationToken);
                        bridgeResult = bridgeResult with
                        {
                            Success = verification.Success,
                            ExitCode = verification.Success ? ExitCodes.Success : ExitCodes.UploadFailed,
                            Stage = verification.Success ? "complete" : "verification",
                            Message = verification.Success ? bridgeResult.Message : verification.Message,
                            Verified = verification.Success,
                            VerificationMessage = verification.Message,
                            ServerVersion = verification.Content?.Version ?? bridgeResult.ServerVersion
                        };
                        await ReportAsync(terminalUi, "VERIFY", verification.Message);
                        if (verification.Success && !string.IsNullOrWhiteSpace(bridgeResult.Artifact?.RecoveryFile))
                        {
                            try
                            {
                                RecoveryManifestFile.Complete(bridgeResult.Artifact.RecoveryFile);
                                bridgeResult = bridgeResult with
                                {
                                    Artifact = bridgeResult.Artifact with { RecoveryFile = null }
                                };
                                await ReportAsync(terminalUi, "VERIFY", "Removed the verified deployment recovery files.");
                            }
                            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
                            {
                                await ReportAsync(terminalUi, "VERIFY", "Deployment is verified, but recovery cleanup was skipped: " + exception.Message);
                            }
                        }
                    }
                    catch (Exception exception) when (exception is VrchatApiException or HttpRequestException or TaskCanceledException)
                    {
                        bridgeResult = bridgeResult with
                        {
                            Success = false,
                            ExitCode = ExitCodes.NetworkFailed,
                            Stage = "verification",
                            Message = "Upload completed, but server verification failed: " + exception.Message,
                            Verified = false,
                            VerificationMessage = exception.Message
                        };
                    }
                }

                bool blueprintOutputWritten = false;
                if (bridgeResult.Success && options.BlueprintOutputPath != null && !string.IsNullOrWhiteSpace(bridgeResult.Blueprint))
                {
                    try
                    {
                        await BlueprintOutputWriter.WriteAsync(options.BlueprintOutputPath, bridgeResult.Blueprint);
                        blueprintOutputWritten = true;
                    }
                    catch (Exception exception)
                    {
                        string message =
                            "Content upload succeeded, but the Blueprint output could not be written: " + exception.Message;
                        await error.WriteLineAsync("VRCLI: " + message);
                        DeploymentResult partialFailure = bridgeResult with
                        {
                            Success = false,
                            ExitCode = ExitCodes.UnexpectedError,
                            Stage = "blueprint-output",
                            Message = message
                        };
                        if (terminalUi != null) await terminalUi.FinishAsync(false);
                        await WriteResultAsync(partialFailure);
                        return partialFailure.ExitCode;
                    }
                }

                LastResult = bridgeResult;
                if (terminalUi != null) await terminalUi.FinishAsync(bridgeResult.Success);
                if (blueprintOutputWritten && !jsonOutput)
                    await output.WriteLineAsync($"[VRCLI] Blueprint ID written to {options.BlueprintOutputPath}");
                await WriteResultAsync(bridgeResult);
                return bridgeResult.ExitCode;
            }

            int missingResultExitCode = result.ExitCode == 0 ? ExitCodes.UnexpectedError : result.ExitCode;
            if (terminalUi != null) await terminalUi.FinishAsync(false);
            return await WriteFailureAsync(
                missingResultExitCode,
                "unity",
                "Unity exited without producing a deployment result.",
                options);
        }
        catch (OperationCanceledException)
        {
            if (terminalUi != null) await terminalUi.FinishAsync(false);
            await error.WriteLineAsync("VRCLI: Cancelled.");
            return await WriteFailureAsync(ExitCodes.Canceled, "cancelled", "Operation cancelled.", options);
        }
        catch (Exception exception)
        {
            if (terminalUi != null) await terminalUi.FinishAsync(false);
            await error.WriteLineAsync("VRCLI: " + exception.Message);
            return await WriteFailureAsync(ExitCodes.UnexpectedError, "unexpected", exception.Message, options);
        }
        finally
        {
            if (File.Exists(resultFile)) File.Delete(resultFile);
            if (targetRequestFile != null && File.Exists(targetRequestFile)) File.Delete(targetRequestFile);
            if (targetResponseFile != null && File.Exists(targetResponseFile)) File.Delete(targetResponseFile);
        }
    }

    private async Task<(VrchatUser User, VrchatSessionTokens Tokens)> AuthenticateAsync(
        DeployOptions options,
        TerminalProgressRenderer? terminalUi,
        CancellationToken cancellationToken)
    {
        using VrchatApiClient api = new();
        string? authToken = Environment.GetEnvironmentVariable(DeploymentEnvironment.AuthToken);
        string? twoFactorToken = Environment.GetEnvironmentVariable(DeploymentEnvironment.TwoFactorToken);
        VrchatUser user;
        if (!string.IsNullOrWhiteSpace(authToken))
        {
            await ReportAsync(terminalUi, "AUTH", "Validating the selected saved session with VRChat.", true);
            user = await api.ResumeSessionAsync(
                new VrchatSessionTokens(authToken, twoFactorToken),
                cancellationToken);
        }
        else
        {
            if (string.IsNullOrEmpty(options.Password))
            {
                IVrchatSessionStore store = sessionStoreFactory?.Invoke() ?? new VrchatSessionStore();
                IReadOnlyList<SavedVrchatSession> matches = VrchatSessionStore.Match(store.List(), options.Username);
                if (matches.Count == 0)
                {
                    throw new VrchatApiException(
                        "No saved session matches --login. Provide --password/VRCLI_PASSWORD or sign in once through the TUI.");
                }
                if (matches.Count > 1)
                {
                    throw new VrchatApiException(
                        "More than one saved session matches --login. Use the exact VRChat user ID shown by 'vrcli auth list'.");
                }

                SavedVrchatSession saved = matches[0];
                await ReportAsync(terminalUi, "AUTH", "Validating the saved session for " + saved.DisplayName + ".", true);
                try
                {
                    user = await api.ResumeSessionAsync(saved.Tokens, cancellationToken);
                    store.Save(saved with
                    {
                        DisplayName = user.DisplayName,
                        Tokens = api.ExportSession(),
                        LastUsed = DateTimeOffset.UtcNow
                    });
                }
                catch (VrchatApiException)
                {
                    store.Delete(saved.UserId);
                    throw new VrchatApiException(
                        "The saved session for " + saved.DisplayName + " expired and was removed. Sign in again with --password or the TUI.");
                }
            }
            else
            {
                await ReportAsync(terminalUi, "AUTH", "Validating account credentials with VRChat.", true);
                user = await VrchatAuthentication.SignInAsync(
                    api,
                    options.Username,
                    options.Password,
                    options.TwoFactorCode,
                    options.TwoFactorMethod,
                    options.TotpSecret,
                    options.InteractiveTwoFactor && terminalUi != null
                        ? methods => Task.FromResult(terminalUi.PromptForTwoFactor(methods))
                        : null,
                    message => Report(terminalUi, "AUTH", message),
                    cancellationToken);
            }
        }
        return (user, api.ExportSession());
    }

    private static string? ValidateContentOptions(DeployOptions options, ProjectContentType contentType)
    {
        if (options.Operation == OperationMode.Meta) return null;

        if (contentType == ProjectContentType.World)
        {
            if (!string.IsNullOrWhiteSpace(options.TargetPath))
                return "--target is only valid for Avatar projects.";
            if (!string.IsNullOrWhiteSpace(options.BlueprintId) &&
                !options.BlueprintId.StartsWith("wrld_", StringComparison.Ordinal))
            {
                return "A World project requires a Blueprint ID beginning with 'wrld_'.";
            }
            return null;
        }

        if (options.IsNew)
            return "--new is only used for World projects. An Avatar is new when its scene PipelineManager has no Blueprint ID.";
        if (!string.IsNullOrWhiteSpace(options.BlueprintId) &&
            !options.BlueprintId.StartsWith("avtr_", StringComparison.Ordinal))
        {
            return "An Avatar project requires a Blueprint ID beginning with 'avtr_'.";
        }
        if (options.HasCapacity || options.HasRecommendedCapacity)
            return "--capacity and --recommended-capacity are only valid for World projects.";
        return null;
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
                LogOutput,
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
        VrchatSessionTokens sessionTokens,
        ProjectContentType contentType,
        string? targetRequestFile,
        string? targetResponseFile,
        string recoveryDirectory)
    {
        ProcessStartInfo startInfo = new(unityPath) { WorkingDirectory = options.ProjectPath };
        Add(startInfo, "-batchmode");
        Add(startInfo, "-accept-apiupdate");
        Add(startInfo, "-projectPath", options.ProjectPath);
        Add(startInfo, "-buildTarget", options.Platform == BuildPlatform.Android ? "Android" : "Win64");
        Add(startInfo, "-executeMethod", "KibaLab.WorldDeployment.Editor.DeploymentEntryPoint.Run");
        Add(startInfo, "-logFile", "-");

        startInfo.Environment[DeploymentEnvironment.BlueprintId] = options.BlueprintId;
        startInfo.Environment[DeploymentEnvironment.Operation] = options.Operation.ToString();
        startInfo.Environment[DeploymentEnvironment.ContentType] = contentType.ToString();
        startInfo.Environment[DeploymentEnvironment.IsNew] = options.IsNew ? "true" : "false";
        startInfo.Environment[DeploymentEnvironment.AuthToken] = sessionTokens.AuthToken;
        if (!string.IsNullOrWhiteSpace(sessionTokens.TwoFactorToken))
            startInfo.Environment[DeploymentEnvironment.TwoFactorToken] = sessionTokens.TwoFactorToken;
        startInfo.Environment[DeploymentEnvironment.Platform] = options.Platform.ToString();
        startInfo.Environment[DeploymentEnvironment.ResultFile] = resultFile;
        startInfo.Environment[DeploymentEnvironment.RecoveryDirectory] = recoveryDirectory;
        if (options.Recovery != null)
        {
            startInfo.Environment[DeploymentEnvironment.ResumeBundle] = options.Recovery.BundlePath;
            if (!string.IsNullOrWhiteSpace(options.Recovery.Signature))
                startInfo.Environment[DeploymentEnvironment.ResumeSignature] = options.Recovery.Signature;
        }
        if (!string.IsNullOrWhiteSpace(options.TargetPath))
            startInfo.Environment[DeploymentEnvironment.Target] = options.TargetPath;
        if (!string.IsNullOrWhiteSpace(targetRequestFile))
            startInfo.Environment[DeploymentEnvironment.TargetRequestFile] = targetRequestFile;
        if (!string.IsNullOrWhiteSpace(targetResponseFile))
            startInfo.Environment[DeploymentEnvironment.TargetResponseFile] = targetResponseFile;
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

        return LogOutput.WriteLineAsync($"[VRCLI][{area}] {message}");
    }

    private void Report(TerminalProgressRenderer? terminalUi, string area, string message)
    {
        if (terminalUi != null) terminalUi.Report(area, message);
        else LogOutput.WriteLine($"[VRCLI][{area}] {message}");
    }

    private async Task<int> WriteFailureAsync(
        int exitCode,
        string stage,
        string message,
        DeployOptions? options = null,
        ProjectContentType? contentType = null)
    {
        DeploymentResult result = new(
            false,
            exitCode,
            string.IsNullOrWhiteSpace(options?.BlueprintId) ? null : options.BlueprintId,
            false,
            options?.Operation == OperationMode.Meta ? null : options?.Platform.ToString(),
            stage,
            message,
            ContentType: contentType?.ToString());
        await WriteResultAsync(result);
        return exitCode;
    }

    private async Task WriteResultAsync(DeploymentResult result)
    {
        if (string.IsNullOrWhiteSpace(result.VrcliVersion))
            result = result with { VrcliVersion = Branding.Version };
        LastResult = result;
        await output.WriteLineAsync(JsonSerializer.Serialize(result, ResultJsonOptions));
    }

    private static async Task<DeploymentResult?> ReadResultAsync(string resultFile)
    {
        if (!File.Exists(resultFile)) return null;
        await using FileStream stream = File.OpenRead(resultFile);
        return await JsonSerializer.DeserializeAsync<DeploymentResult>(stream);
    }

    public static string HelpText => Branding.LogoText + """


VRCLI commands and parameters

  deploy                        Build and upload project content; auto-detects World or Avatar
  meta                          Update only an existing world's metadata
  check                         Check Unity compilation and SDK readiness; auto-detects content type
  auth list                     List saved VRChat sessions without exposing tokens
  auth logout <account>         Remove one saved session; use --all to remove every session

  --project <directory>         Unity project directory for deploy/check; default current directory
  --blueprint <content_id>      wrld_ or avtr_ override; deploy/check use the scene ID when omitted; meta requires wrld_
  --new                         Create a private world; deploy only
  --scene <Assets/...unity>     Scene to deploy or check; auto-detected when unambiguous
  --target <hierarchy/path>     Avatar GameObject to deploy/check when a scene contains several avatars
  --platform <platform>         Deploy/check target: StandaloneWindows64 or Android; default StandaloneWindows64
  --login <username-or-email>   VRChat account; default VRCLI_USERNAME
  --password <password>         Account password; default VRCLI_PASSWORD; visible in shell history
  --title <name>                Content name; required for a new world or avatar
  --description <text>          Content description; metadata option
  --thumbnail <image>           Content image; required for a new world or avatar
  --capacity <1+>               Maximum player capacity; metadata option; new-world default 32
  --recommended-capacity <1+>   Recommended player capacity; metadata option; new-world default 16
  --tag <tag>                   Repeatable metadata tag to add
  --remove-tag <tag>            Repeatable metadata tag to remove; meta only
  --blueprint-output <file>     Save the uploaded wrld_ or avtr_ ID; deploy only
  --resume <recovery.json>      Retry a preserved upload without rebuilding; deploy only
  --two-factor-code <code>      Current VRChat two-factor code
  --two-factor-method <method>  Code type: totp, emailOtp, or otp
  --interactive-two-factor      Prompt only when VRChat requests two-factor authentication
  --config <file>               Configuration file; default ./vrcli.json when present
  --plain                       Append-only output for scripts and CI
  --json                        Write one result object to stdout; logs go to stderr
  --yes                         Certify content ownership when deployment requires it
  --tui                         Force the interactive terminal display
  --unity <Unity.exe>           Override Unity executable discovery
  --timeout <seconds>           Operation timeout; default 3600
  --skip-vpm-resolve            Do not resolve VPM dependencies
  --verbose                     Print detailed logs
  --help                        Show this parameter list
""";
}

public sealed record DeploymentResult(
    bool Success,
    int ExitCode,
    string? Blueprint,
    bool Created,
    string? Platform,
    string? Stage,
    string? Message,
    IReadOnlyList<string>? Errors = null,
    IReadOnlyList<string>? Warnings = null,
    IReadOnlyList<string>? Information = null,
    IReadOnlyList<string>? CompilerErrors = null,
    IReadOnlyList<string>? CompilerWarnings = null,
    IReadOnlyList<MetadataChange>? Changes = null,
    string? ContentType = null,
    IReadOnlyList<ContentTarget>? Targets = null,
    string? VrcliVersion = null,
    string? UnityVersion = null,
    string? SdkVersion = null,
    long? DurationMs = null,
    IReadOnlyList<PhaseTiming>? PhaseTimings = null,
    BuildArtifact? Artifact = null,
    int? PreviousVersion = null,
    int? ServerVersion = null,
    string? ServerUpdatedAt = null,
    bool? Verified = null,
    string? VerificationMessage = null);

public sealed record ContentTarget(string Name, string Selector, string? Blueprint);
public sealed record PhaseTiming(string Phase, long DurationMs);
public sealed record BuildArtifact(string Path, long Size, string Sha256, string? RecoveryFile = null);
