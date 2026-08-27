using System.Text.Json;
using KibaLab.WorldDeployment.Editor;

namespace KibaLab.WorldDeployment;

public sealed record MetadataExecutionResult(int ExitCode, DeploymentResult Result);

public sealed class MetadataApplication(TextWriter output, TextWriter error)
{
    private static readonly JsonSerializerOptions ResultJsonOptions = new() { WriteIndented = true };

    public async Task<MetadataExecutionResult> RunAsync(
        DeployOptions options,
        CancellationToken cancellationToken = default)
    {
        TerminalProgressRenderer? terminalUi = null;
        if (TerminalProgressRenderer.ShouldUse(options.TerminalMode, options.Verbose))
        {
            terminalUi = new TerminalProgressRenderer(output, OperationMode.Meta, cancellationToken: cancellationToken);
            terminalUi.SetOverview("Direct VRChat API", options.BlueprintId, "Unity is not required", "World record");
            terminalUi.Start();
        }

        try
        {
            await ReportAsync(terminalUi, "BOOT", "Metadata request validated; Unity will not be started.", true);
            using VrchatApiClient api = new();
            VrchatUser user;
            try
            {
                user = await VrchatAuthentication.SignInAsync(
                    api,
                    options.Username,
                    options.Password,
                    options.TwoFactorCode,
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
                throw new VrchatApiException("The requested values are already present; there is nothing to update.");

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
                await output.WriteLineAsync("[VRCLI][META] Applied changes:");
                foreach (MetadataChange change in applied) await output.WriteLineAsync("[VRCLI][META] " + FormatChange(change));
            }

            DeploymentResult success = new(
                true,
                ExitCodes.Success,
                updated.Id,
                false,
                null,
                "metadata",
                "World metadata updated without starting Unity.",
                Changes: applied);
            await output.WriteLineAsync(JsonSerializer.Serialize(success, ResultJsonOptions));
            return new MetadataExecutionResult(ExitCodes.Success, success);
        }
        catch (OperationCanceledException)
        {
            if (terminalUi != null) await terminalUi.FinishAsync(false);
            await error.WriteLineAsync("VRCLI: Cancelled.");
            DeploymentResult cancelled = Failure(ExitCodes.TimedOut, "cancelled", "Metadata update cancelled.");
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
    }

    public static WorldMetadataSnapshot ApplyOptions(WorldMetadataSnapshot current, DeployOptions options)
    {
        IReadOnlyList<string> tags = options.HasTags
            ? current.Tags.Concat(options.Tags).Distinct(StringComparer.Ordinal).ToArray()
            : current.Tags;
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

    private DeploymentResult Failure(int exitCode, string stage, string message) => new(
        false,
        exitCode,
        null,
        false,
        null,
        stage,
        message);

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

    private void Report(
        TerminalProgressRenderer? terminalUi,
        string area,
        string message,
        bool startsPhase = false)
    {
        if (terminalUi != null) terminalUi.Report(area, message, startsPhase);
        else output.WriteLine($"[VRCLI][{area}] {message}");
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
            method = PreferredMethod(methods);
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

    private static string PreferredMethod(IReadOnlyList<string> methods)
    {
        if (methods.Contains("emailOtp", StringComparer.OrdinalIgnoreCase)) return "emailOtp";
        if (methods.Contains("totp", StringComparer.OrdinalIgnoreCase)) return "totp";
        if (methods.Contains("otp", StringComparer.OrdinalIgnoreCase)) return "otp";
        throw new VrchatApiException("VRChat requested an unsupported two-factor method: " + string.Join(", ", methods));
    }
}

public sealed class VrchatAuthenticationException(string message) : VrchatApiException(message);
