using System.Text.Json;
using KibaLab.WorldDeployment.Editor;

namespace KibaLab.WorldDeployment;

public sealed record MetadataExecutionResult(int ExitCode, DeploymentResult Result);

public sealed class MetadataApplication(
    TextWriter output,
    TextWriter error,
    Func<VrchatApiClient>? apiFactory = null)
{
    private static readonly JsonSerializerOptions ResultJsonOptions = new() { WriteIndented = true };
    private TextWriter logOutput = null!;

    public async Task<MetadataExecutionResult> RunAsync(
        DeployOptions options,
        CancellationToken cancellationToken = default)
    {
        logOutput = options.TerminalMode == TerminalMode.Json ? error : output;
        TerminalProgressRenderer? terminalUi = null;
        if (TerminalProgressRenderer.ShouldUse(options.TerminalMode, options.Verbose))
        {
            terminalUi = new TerminalProgressRenderer(output, OperationMode.Meta, cancellationToken: cancellationToken);
            terminalUi.SetOverview("Direct VRChat API", options.BlueprintId, "Unity is not required", "World record");
            terminalUi.Start();
        }

        try
        {
            using VrchatApiClient api = apiFactory?.Invoke() ?? new VrchatApiClient();
            VrchatUser user;
            try
            {
                await ReportAsync(terminalUi, "AUTH", "Validating account credentials with VRChat.", true);
                user = await VrchatAuthentication.SignInAsync(
                    api,
                    options.Username,
                    options.Password,
                    options.TwoFactorCode,
                    options.TwoFactorMethod,
                    options.TotpSecret,
                    options.InteractiveTwoFactor
                        ? methods => Task.FromResult(terminalUi != null
                            ? terminalUi.PromptForTwoFactor(methods)
                            : InteractiveWizard.PromptForTwoFactorChallenge(methods))
                        : null,
                    message => Report(terminalUi, "AUTH", message),
                    cancellationToken);
            }
            catch (VrchatApiException exception)
            {
                throw new VrchatAuthenticationException(exception.Message);
            }
            await ReportAsync(terminalUi, "AUTH", $"Signed in as {user.DisplayName} ({user.Id}).");
            await ReportAsync(terminalUi, "BOOT", "Metadata request validated; Unity will not be started.", true);

            if (options.ThumbnailPath != null && !File.Exists(options.ThumbnailPath))
                throw new VrchatApiException("Thumbnail was not found: " + options.ThumbnailPath);

            await ReportAsync(terminalUi, "CONTEXT", "Loading " + options.BlueprintId + " from VRChat.", true);
            WorldMetadataSnapshot current = await api.GetWorldAsync(options.BlueprintId, cancellationToken);
            api.EnsureOwner(current);
            await ReportAsync(
                terminalUi,
                "CONTEXT",
                $"World loaded: {current.Title} · version {current.Version} · owner verified.");

            WorldMetadataSnapshot desired = ApplyOptions(current, options);
            IReadOnlyList<MetadataChange> changes = VrchatApiClient.Compare(current, desired, options.ThumbnailPath);
            if (changes.Count == 0)
            {
                await ReportAsync(terminalUi, "WORLD", "World metadata is already up to date.", true);
                if (terminalUi != null) await terminalUi.FinishAsync(true);
                DeploymentResult unchanged = Versioned(new DeploymentResult(
                    true,
                    ExitCodes.Success,
                    current.Id,
                    false,
                    null,
                    "metadata",
                    "World metadata is already up to date; no server update was needed.",
                    Changes: []));
                await output.WriteLineAsync(JsonSerializer.Serialize(unchanged, ResultJsonOptions));
                return new MetadataExecutionResult(ExitCodes.Success, unchanged);
            }

            await ReportAsync(terminalUi, "WORLD", "Planned metadata changes:", true);
            foreach (MetadataChange change in changes)
                await ReportAsync(terminalUi, "WORLD", FormatChange(change));

            WorldMetadataSnapshot updated = await api.UpdateWorldAsync(
                current,
                desired,
                options.ThumbnailPath,
                message => Report(terminalUi, "UPLOAD", message, true),
                cancellationToken);
            IReadOnlyList<MetadataChange> applied = VrchatApiClient.Compare(current, updated);
            if (options.ThumbnailPath != null)
            {
                MetadataChange? plannedThumbnail = changes.FirstOrDefault(change => change.Field == "Thumbnail");
                if (plannedThumbnail != null)
                    applied = applied.Append(plannedThumbnail with { After = updated.ImageUrl ?? plannedThumbnail.After }).ToArray();
            }

            await ReportAsync(terminalUi, "UPLOAD", "Metadata update completed; server version " + updated.Version + ".");
            if (terminalUi != null) await terminalUi.FinishAsync(true);
            else
            {
                await logOutput.WriteLineAsync("[VRCLI][META] Applied changes:");
                foreach (MetadataChange change in applied) await logOutput.WriteLineAsync("[VRCLI][META] " + FormatChange(change));
            }

            DeploymentResult success = Versioned(new DeploymentResult(
                true,
                ExitCodes.Success,
                updated.Id,
                false,
                null,
                "metadata",
                "World metadata updated without starting Unity.",
                Changes: applied,
                PreviousVersion: current.Version,
                ServerVersion: updated.Version,
                Verified: true,
                VerificationMessage: "The metadata update response was verified."));
            await output.WriteLineAsync(JsonSerializer.Serialize(success, ResultJsonOptions));
            return new MetadataExecutionResult(ExitCodes.Success, success);
        }
        catch (OperationCanceledException)
        {
            if (terminalUi != null) await terminalUi.FinishAsync(false);
            await error.WriteLineAsync("VRCLI: Cancelled.");
            DeploymentResult cancelled = Failure(ExitCodes.Canceled, "cancelled", "Metadata update cancelled.");
            await output.WriteLineAsync(JsonSerializer.Serialize(cancelled, ResultJsonOptions));
            return new MetadataExecutionResult(cancelled.ExitCode, cancelled);
        }
        catch (Exception exception) when (exception is VrchatApiException or HttpRequestException or TaskCanceledException)
        {
            if (terminalUi != null) await terminalUi.FinishAsync(false);
            string stage = exception is VrchatAuthenticationException ? "authentication" : "metadata";
            int exitCode = stage == "authentication" ? ExitCodes.AuthenticationFailed : ExitCodes.UploadFailed;
            await error.WriteLineAsync("VRCLI: " + exception.Message);
            DeploymentResult failure = Failure(exitCode, stage, exception.Message);
            await output.WriteLineAsync(JsonSerializer.Serialize(failure, ResultJsonOptions));
            return new MetadataExecutionResult(exitCode, failure);
        }
        catch (Exception exception)
        {
            if (terminalUi != null) await terminalUi.FinishAsync(false);
            await error.WriteLineAsync("VRCLI: " + exception.Message);
            DeploymentResult failure = Failure(ExitCodes.UnexpectedError, "unexpected", exception.Message);
            await output.WriteLineAsync(JsonSerializer.Serialize(failure, ResultJsonOptions));
            return new MetadataExecutionResult(failure.ExitCode, failure);
        }
    }

    public static WorldMetadataSnapshot ApplyOptions(WorldMetadataSnapshot current, DeployOptions options)
    {
        IReadOnlyList<string> tags = current.Tags;
        if (options.HasTags)
            tags = tags.Concat(options.Tags).Distinct(StringComparer.Ordinal).ToArray();
        if (options.HasRemovedTags)
            tags = tags.Except(options.RemovedTags, StringComparer.Ordinal).ToArray();
        WorldMetadataSnapshot desired = current with
        {
            Title = options.Title ?? current.Title,
            Description = options.Description ?? current.Description,
            Capacity = options.HasCapacity ? options.Capacity : current.Capacity,
            RecommendedCapacity = options.HasRecommendedCapacity ? options.RecommendedCapacity : current.RecommendedCapacity,
            Tags = tags
        };
        if (desired.RecommendedCapacity > desired.Capacity)
            throw new VrchatApiException("Recommended capacity cannot exceed maximum capacity.");
        return desired;
    }

    public static string FormatChange(MetadataChange change) =>
        $"{change.Field}: {Display(change.Before)}  →  {Display(change.After)}";

    private DeploymentResult Failure(int exitCode, string stage, string message) => Versioned(new DeploymentResult(
        false,
        exitCode,
        null,
        false,
        null,
        stage,
        message));

    private static DeploymentResult Versioned(DeploymentResult result) =>
        result with { VrcliVersion = Branding.Version };

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
        return logOutput.WriteLineAsync($"[VRCLI][{area}] {message}");
    }

    private void Report(
        TerminalProgressRenderer? terminalUi,
        string area,
        string message,
        bool startsPhase = false)
    {
        if (terminalUi != null) terminalUi.Report(area, message, startsPhase);
        else logOutput.WriteLine($"[VRCLI][{area}] {message}");
    }

    private static string Display(string value)
    {
        string normalized = value.Replace("\r", string.Empty).Replace("\n", " ↵ ");
        if (normalized.Length == 0) return "(empty)";
        return normalized.Length <= 96 ? normalized : normalized[..95] + "…";
    }
}

public static class VrchatAuthentication
{
    public static async Task<VrchatUser> SignInAsync(
        VrchatApiClient api,
        string username,
        string password,
        string? twoFactorCode,
        string? twoFactorMethod,
        string? totpSecret,
        Func<IReadOnlyList<string>, Task<InteractiveTwoFactorAnswer>>? prompt,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Invoke("Validating username/email and password with VRChat");
        VrchatLoginChallenge challenge = await api.BeginLoginAsync(username, password, cancellationToken);
        if (!challenge.RequiresTwoFactor)
            return challenge.User ?? throw new VrchatApiException("VRChat did not return an authenticated user.");

        IReadOnlyList<string> methods = challenge.RequiredTwoFactorMethods;
        string method;
        string code;
        if (!string.IsNullOrWhiteSpace(totpSecret))
        {
            if (!methods.Contains("totp", StringComparer.OrdinalIgnoreCase))
                throw new VrchatApiException("A TOTP secret was provided, but VRChat requested: " + string.Join(", ", methods));
            method = "totp";
            long seconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() % 30;
            if (seconds >= 27) await Task.Delay(TimeSpan.FromSeconds(31 - seconds), cancellationToken);
            try
            {
                code = TotpGenerator.GenerateCode(totpSecret, DateTimeOffset.UtcNow);
            }
            catch (ArgumentException exception)
            {
                throw new VrchatApiException("The TOTP secret is invalid: " + exception.Message);
            }
            progress?.Invoke("Generated a TOTP code in memory");
        }
        else if (!string.IsNullOrWhiteSpace(twoFactorCode))
        {
            method = twoFactorMethod ?? throw new VrchatApiException("The two-factor method was not specified.");
            if (!methods.Contains(method, StringComparer.OrdinalIgnoreCase))
                throw new VrchatApiException(
                    "The requested two-factor method is unavailable. VRChat requested: " + string.Join(", ", methods));
            code = twoFactorCode;
        }
        else if (prompt != null)
        {
            InteractiveTwoFactorAnswer answer = await prompt(methods);
            method = answer.Method;
            code = answer.Code;
            if (string.IsNullOrWhiteSpace(code)) throw new OperationCanceledException("Two-factor verification cancelled.");
        }
        else
        {
            throw new VrchatApiException(
                "Two-factor authentication is required (" + string.Join(", ", methods) + "). " +
                "Provide --two-factor-code, VRCLI_TOTP_SECRET, or use the interactive meta session.");
        }

        progress?.Invoke("Submitting " + method + " verification");
        return await api.CompleteLoginAsync(method, code, cancellationToken);
    }

}

public sealed class VrchatAuthenticationException(string message) : VrchatApiException(message);
