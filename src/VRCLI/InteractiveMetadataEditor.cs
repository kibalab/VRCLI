using System.Globalization;

namespace KibaLab.WorldDeployment;

public static class InteractiveMetadataEditor
{
    public static bool ShouldStart(string[] args) =>
        args.Length is 1 or 2 &&
        string.Equals(args[0], "meta", StringComparison.OrdinalIgnoreCase) &&
        (args.Length == 1 || string.Equals(args[1], "--tui", StringComparison.OrdinalIgnoreCase)) &&
        InteractiveWizard.ShouldStart(args);

    public static async Task<int> RunAsync(
        string[] invocation,
        CancellationToken cancellationToken = default)
    {
        using WizardTerminalScreen screen = new(cancellationToken);
        screen.SetRoute("ACCOUNT", "WORLD", "EDIT");
        screen.Enter();
        int savedUpdates = 0;
        string password = string.Empty;

        try
        {
            screen.SetSection("01", "ACCOUNT", "Sign in once; this session is reused until you exit.");
            string username = ReadRequired(
                screen,
                "VRChat username or account email",
                Environment.GetEnvironmentVariable(DeploymentEnvironment.Username));
            password = Environment.GetEnvironmentVariable(DeploymentEnvironment.Password) ??
                       ReadRequired(screen, "VRChat password", secret: true);

            using VrchatApiClient api = new();
            screen.SetBusy("Signing in to VRChat…");
            VrchatUser user;
            try
            {
                user = await VrchatAuthentication.SignInAsync(
                    api,
                    username,
                    password,
                    null,
                    Environment.GetEnvironmentVariable(DeploymentEnvironment.TotpSecret),
                    methods => Task.FromResult(ReadTwoFactor(screen, methods)),
                    screen.SetBusy,
                    cancellationToken);
            }
            catch (VrchatApiException exception)
            {
                throw new VrchatAuthenticationException(exception.Message);
            }
            screen.AddSummary("Account", user.DisplayName + "  ·  " + user.Id);

            while (true)
            {
                screen.SetSection("02", "WORLD", "Open an existing world owned by the signed-in account.");
                screen.AddSummary("Account", user.DisplayName + "  ·  session active");
                string worldId = ReadRequired(
                    screen,
                    "Blueprint ID (wrld_...)",
                    validate: value => value.StartsWith("wrld_", StringComparison.Ordinal));
                screen.SetBusy("Loading the current world metadata…");
                WorldMetadataSnapshot current;
                try
                {
                    current = await api.GetWorldAsync(worldId, cancellationToken);
                    api.EnsureOwner(current);
                }
                catch (VrchatApiException exception)
                {
                    screen.SetContext(["Could not open world · " + exception.Message, "The authenticated session is still active."]);
                    int action = screen.ReadChoice("World could not be opened", ["Try another Blueprint ID", "Exit metadata session"]);
                    if (action == 0) continue;
                    break;
                }

                bool chooseAnotherWorld = await EditWorldAsync(
                    screen,
                    api,
                    user,
                    current,
                    () => savedUpdates++,
                    cancellationToken);
                if (!chooseAnotherWorld) break;
            }

            screen.SetSection("03", "EDIT", "Metadata session complete.");
            screen.ShowReview(
            [
                ("Account", user.DisplayName),
                ("Updates", savedUpdates.ToString(CultureInfo.InvariantCulture)),
                ("Session", "Signed out locally; credentials were not stored")
            ]);
            await Task.Delay(500, cancellationToken);
            return ExitCodes.Success;
        }
        catch (OperationCanceledException)
        {
            return ExitCodes.Success;
        }
        catch (Exception exception) when (exception is VrchatApiException or HttpRequestException or TaskCanceledException)
        {
            screen.SetNotice(exception.Message);
            await Task.Delay(900, CancellationToken.None);
            return exception is VrchatAuthenticationException
                ? ExitCodes.AuthenticationFailed
                : ExitCodes.UploadFailed;
        }
        finally
        {
            password = string.Empty;
        }
    }

    private static async Task<bool> EditWorldAsync(
        WizardTerminalScreen screen,
        VrchatApiClient api,
        VrchatUser user,
        WorldMetadataSnapshot initial,
        Action onSaved,
        CancellationToken cancellationToken)
    {
        WorldMetadataSnapshot current = initial;
        WorldMetadataSnapshot draft = current;
        string? thumbnailPath = null;
        IReadOnlyList<string> detailLog =
        [
            $"Loaded {current.Title} · server version {current.Version}",
            $"Capacity {current.RecommendedCapacity} recommended / {current.Capacity} maximum"
        ];

        while (true)
        {
            screen.SetSection("03", "EDIT", "Choose a field directly. Save, then continue editing without signing in again.");
            screen.ShowReview(
            [
                ("Account", user.DisplayName + "  ·  session active"),
                ("World", draft.Title + "  ·  " + draft.Id),
                ("Version", current.Version.ToString(CultureInfo.InvariantCulture)),
                ("Pending", PendingCount(current, draft, thumbnailPath) + " change(s)")
            ]);
            screen.SetContext(detailLog);

            int selected = screen.ReadChoice(
                "Metadata editor",
                [
                    MarkChanged("Title", current.Title != draft.Title, Short(draft.Title)),
                    MarkChanged("Description", current.Description != draft.Description, Short(draft.Description)),
                    MarkChanged("Thumbnail", thumbnailPath != null, thumbnailPath == null ? "unchanged" : Path.GetFileName(thumbnailPath)),
                    MarkChanged("Maximum capacity", current.Capacity != draft.Capacity, draft.Capacity.ToString(CultureInfo.InvariantCulture)),
                    MarkChanged("Recommended capacity", current.RecommendedCapacity != draft.RecommendedCapacity, draft.RecommendedCapacity.ToString(CultureInfo.InvariantCulture)),
                    MarkChanged("Add tags", !current.Tags.SequenceEqual(draft.Tags, StringComparer.Ordinal), draft.Tags.Count + " total"),
                    "Save pending changes",
                    "Choose another world",
                    "Exit metadata session"
                ]);

            switch (selected)
            {
                case 0:
                    {
                        string value = ReadRequired(screen, "World title", draft.Title);
                        detailLog = DraftLog("Title", draft.Title, value);
                        draft = draft with { Title = value };
                        break;
                    }
                case 1:
                    {
                        string value = screen.ReadText(
                            "Description (empty clears it)",
                            null,
                            secret: false,
                            acceptEmpty: true);
                        detailLog = DraftLog("Description", draft.Description, value);
                        draft = draft with { Description = value };
                        break;
                    }
                case 2:
                    {
                        string value = ReadRequired(screen, "PNG or JPEG path", thumbnailPath, IsImageFile);
                        thumbnailPath = Path.GetFullPath(value);
                        detailLog = ["Draft · Thumbnail: " + (current.ImageUrl ?? "(none)"), "      → " + thumbnailPath];
                        break;
                    }
                case 3:
                    {
                        int value = ReadInteger(screen, "Maximum capacity", draft.Capacity, Math.Max(1, draft.RecommendedCapacity));
                        detailLog = DraftLog("Maximum capacity", draft.Capacity.ToString(), value.ToString());
                        draft = draft with { Capacity = value };
                        break;
                    }
                case 4:
                    {
                        int value = ReadInteger(screen, "Recommended capacity", draft.RecommendedCapacity, 1, draft.Capacity);
                        detailLog = DraftLog("Recommended capacity", draft.RecommendedCapacity.ToString(), value.ToString());
                        draft = draft with { RecommendedCapacity = value };
                        break;
                    }
                case 5:
                    {
                        string value = ReadRequired(screen, "Tags to add (comma-separated)");
                        string[] added = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Where(tag => !tag.Contains('|'))
                            .Distinct(StringComparer.Ordinal)
                            .ToArray();
                        draft = draft with { Tags = draft.Tags.Concat(added).Distinct(StringComparer.Ordinal).ToArray() };
                        detailLog = ["Draft · Added tags: " + string.Join(", ", added)];
                        break;
                    }
                case 6:
                    {
                        IReadOnlyList<MetadataChange> planned = VrchatApiClient.Compare(current, draft, thumbnailPath);
                        if (planned.Count == 0)
                        {
                            detailLog = ["No pending changes. Choose a field before saving."];
                            break;
                        }

                        screen.SetContext(planned.Select(MetadataApplication.FormatChange));
                        if (!screen.ReadYesNo("Apply these changes now", true))
                        {
                            detailLog = ["Save cancelled; draft values are still available."];
                            break;
                        }

                        screen.SetBusy("Updating metadata without starting Unity…");
                        WorldMetadataSnapshot updated;
                        try
                        {
                            updated = await api.UpdateWorldAsync(
                                current,
                                draft,
                                thumbnailPath,
                                screen.SetBusy,
                                cancellationToken);
                        }
                        catch (VrchatApiException exception)
                        {
                            detailLog =
                            [
                                "Save failed · " + exception.Message,
                                "The login session and pending draft are still active; choose Save to retry."
                            ];
                            break;
                        }
                        IReadOnlyList<MetadataChange> applied = VrchatApiClient.Compare(current, updated);
                        if (thumbnailPath != null)
                        {
                            applied = applied.Append(new MetadataChange(
                                "Thumbnail",
                                current.ImageUrl ?? "(none)",
                                updated.ImageUrl ?? Path.GetFileName(thumbnailPath))).ToArray();
                        }
                        detailLog = applied.Select(change => "Saved · " + MetadataApplication.FormatChange(change))
                            .Append("Server version " + current.Version + " → " + updated.Version)
                            .Take(4)
                            .ToArray();
                        current = updated;
                        draft = updated;
                        thumbnailPath = null;
                        onSaved();
                        break;
                    }
                case 7:
                    if (PendingCount(current, draft, thumbnailPath) == 0 ||
                        screen.ReadYesNo("Discard pending changes and choose another world", false))
                        return true;
                    detailLog = ["Pending changes kept. Save or discard them before changing worlds."];
                    break;
                case 8:
                    if (PendingCount(current, draft, thumbnailPath) == 0 ||
                        screen.ReadYesNo("Discard pending changes and exit", false))
                        return false;
                    detailLog = ["Pending changes kept. Save them or choose Exit again to discard."];
                    break;
            }
        }
    }

    private static InteractiveTwoFactorAnswer ReadTwoFactor(
        WizardTerminalScreen screen,
        IReadOnlyList<string> methods)
    {
        List<(string Method, string Label)> choices = [];
        if (methods.Contains("totp", StringComparer.OrdinalIgnoreCase)) choices.Add(("totp", "Authenticator app code"));
        if (methods.Contains("emailOtp", StringComparer.OrdinalIgnoreCase)) choices.Add(("emailOtp", "Email one-time code"));
        if (methods.Contains("otp", StringComparer.OrdinalIgnoreCase)) choices.Add(("otp", "Recovery code"));
        if (choices.Count == 0) throw new VrchatApiException("VRChat requested an unsupported two-factor method.");
        int selected = choices.Count == 1 ? 0 : screen.ReadChoice("Verification method", choices.Select(item => item.Label).ToArray());
        string code = ReadRequired(screen, choices[selected].Label, secret: true);
        if (choices[selected].Method == "totp" && (code.Length != 6 || !code.All(char.IsDigit)))
            throw new VrchatApiException("The authenticator code must contain exactly six digits.");
        return new InteractiveTwoFactorAnswer(choices[selected].Method, code);
    }

    private static string ReadRequired(
        WizardTerminalScreen screen,
        string label,
        string? defaultValue = null,
        Func<string, bool>? validate = null,
        bool secret = false)
    {
        while (true)
        {
            string value = screen.ReadText(label, defaultValue, secret);
            if (!string.IsNullOrWhiteSpace(value) && (validate == null || validate(value))) return value;
            screen.SetNotice("Enter a valid value.");
        }
    }

    private static int ReadInteger(
        WizardTerminalScreen screen,
        string label,
        int defaultValue,
        int minimum,
        int maximum = int.MaxValue)
    {
        while (true)
        {
            string value = screen.ReadText(label, defaultValue.ToString(CultureInfo.InvariantCulture), secret: false);
            if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) &&
                parsed >= minimum && parsed <= maximum)
                return parsed;
            screen.SetNotice("Enter a number from " + minimum + " to " + maximum + ".");
        }
    }

    private static int PendingCount(WorldMetadataSnapshot current, WorldMetadataSnapshot draft, string? thumbnailPath) =>
        VrchatApiClient.Compare(current, draft, thumbnailPath).Count;

    private static string MarkChanged(string label, bool changed, string value) =>
        (changed ? "● " : "  ") + label + "  ·  " + value;

    private static string Short(string value)
    {
        string normalized = value.Replace("\r", string.Empty).Replace("\n", " ↵ ");
        if (normalized.Length == 0) return "(empty)";
        return normalized.Length <= 42 ? normalized : normalized[..41] + "…";
    }

    private static IReadOnlyList<string> DraftLog(string field, string before, string after) =>
        ["Draft · " + MetadataApplication.FormatChange(new MetadataChange(field, before, after))];

    private static bool IsImageFile(string path)
    {
        string extension = Path.GetExtension(path);
        return File.Exists(path) &&
               (string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase));
    }
}
