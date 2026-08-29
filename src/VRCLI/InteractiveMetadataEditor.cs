using System.Globalization;
using System.ComponentModel;

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
        screen.SetRoute("ACCOUNT", "CONTENT", "EDIT");
        screen.Enter();
        int savedUpdates = 0;

        try
        {
            screen.SetSection("01", "ACCOUNT", "Sign in once; this session is reused until you exit.");
            (VrchatApiClient signedInApi, VrchatUser user) = await OpenAccountSessionAsync(screen, cancellationToken);
            using VrchatApiClient api = signedInApi;
            screen.AddSummary("Account", user.DisplayName + "  ·  " + user.Id);

            while (true)
            {
                screen.SetSection("02", "CONTENT", "Open an existing world or avatar owned by the signed-in account.");
                screen.AddSummary("Account", user.DisplayName + "  ·  session active");
                string blueprint = ReadRequired(
                    screen,
                    "Blueprint ID (wrld_... or avtr_...)",
                    validate: value => value.StartsWith("wrld_", StringComparison.Ordinal) ||
                                       value.StartsWith("avtr_", StringComparison.Ordinal));
                screen.SetBusy("Loading the current content metadata…");
                try
                {
                    bool chooseAnother;
                    if (blueprint.StartsWith("wrld_", StringComparison.Ordinal))
                    {
                        WorldMetadataSnapshot current = await api.GetWorldAsync(blueprint, cancellationToken);
                        api.EnsureOwner(current);
                        chooseAnother = await EditWorldAsync(
                            screen,
                            api,
                            user,
                            current,
                            () => savedUpdates++,
                            cancellationToken);
                    }
                    else
                    {
                        AvatarMetadataSnapshot current = await api.GetAvatarAsync(blueprint, cancellationToken);
                        api.EnsureOwner(current);
                        chooseAnother = await EditAvatarAsync(
                            screen,
                            api,
                            user,
                            current,
                            () => savedUpdates++,
                            cancellationToken);
                    }
                    if (!chooseAnother) break;
                }
                catch (VrchatApiException exception)
                {
                    screen.SetContext(["Could not open content · " + exception.Message, "The authenticated session is still active."]);
                    int action = screen.ReadChoice("Content could not be opened", ["Try another Blueprint ID", "Exit metadata session"]);
                    if (action == 0) continue;
                    break;
                }
            }

            screen.SetSection("03", "EDIT", "Metadata session complete.");
            screen.ShowReview(
            [
                ("Account", user.DisplayName),
                ("Updates", savedUpdates.ToString(CultureInfo.InvariantCulture)),
                ("Session", "Saved in " + VrchatSessionStore.StorageDescription)
            ]);
            await Task.Delay(500, cancellationToken);
            return ExitCodes.Success;
        }
        catch (OperationCanceledException)
        {
            return ExitCodes.Canceled;
        }
        catch (Exception exception) when (exception is VrchatApiException or HttpRequestException or TaskCanceledException)
        {
            screen.SetNotice(exception.Message);
            await Task.Delay(900, CancellationToken.None);
            return exception is VrchatAuthenticationException
                ? ExitCodes.AuthenticationFailed
                : ExitCodes.UploadFailed;
        }
    }

    private static async Task<(VrchatApiClient Api, VrchatUser User)> OpenAccountSessionAsync(
        WizardTerminalScreen screen,
        CancellationToken cancellationToken)
    {
        VrchatSessionStore store = new();
        IReadOnlyList<SavedVrchatSession> savedSessions;
        try
        {
            savedSessions = store.List();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or InvalidDataException)
        {
            savedSessions = [];
            screen.SetNotice("Saved sessions could not be read · " + exception.Message);
        }

        while (savedSessions.Count > 0)
        {
            string[] choices = savedSessions
                .Select(session => session.DisplayName + "  ·  saved session")
                .Append("Sign in with another account")
                .ToArray();
            int selected = screen.ReadChoice("Choose a VRChat account", choices);
            if (selected == savedSessions.Count) break;

            SavedVrchatSession saved = savedSessions[selected];
            VrchatApiClient api = new();
            screen.SetBusy("Validating the saved session for " + saved.DisplayName + "…");
            try
            {
                VrchatUser user = await api.ResumeSessionAsync(saved.Tokens, cancellationToken);
                TrySaveSession(
                    store,
                    saved with
                    {
                        DisplayName = user.DisplayName,
                        Tokens = api.ExportSession(),
                        LastUsed = DateTimeOffset.UtcNow
                    },
                    screen);
                return (api, user);
            }
            catch (VrchatApiException exception)
            {
                api.Dispose();
                try
                {
                    store.Delete(saved.UserId);
                }
                catch (Exception storeException) when (storeException is Win32Exception or InvalidOperationException or InvalidDataException)
                {
                }
                screen.SetNotice(saved.DisplayName + " session expired · " + exception.Message);
                savedSessions = savedSessions.Where(session => session.UserId != saved.UserId).ToArray();
            }
            catch (HttpRequestException exception)
            {
                api.Dispose();
                throw new VrchatAuthenticationException(
                    "The saved session could not be checked: " + exception.Message);
            }
            catch
            {
                api.Dispose();
                throw;
            }
        }

        string username = ReadRequired(
            screen,
            "VRChat username or account email",
            Environment.GetEnvironmentVariable(DeploymentEnvironment.Username));
        string password = Environment.GetEnvironmentVariable(DeploymentEnvironment.Password) ??
                          ReadRequired(screen, "VRChat password", secret: true);
        VrchatApiClient signedIn = new();
        try
        {
            screen.SetBusy("Signing in and verifying the account with VRChat…");
            VrchatUser user = await VrchatAuthentication.SignInAsync(
                signedIn,
                username,
                password,
                null,
                null,
                Environment.GetEnvironmentVariable(DeploymentEnvironment.TotpSecret),
                methods => Task.FromResult(ReadTwoFactor(screen, methods)),
                screen.SetBusy,
                cancellationToken);
            TrySaveSession(
                store,
                new SavedVrchatSession(
                    user.Id,
                    user.DisplayName,
                    username,
                    signedIn.ExportSession(),
                    DateTimeOffset.UtcNow),
                screen);
            return (signedIn, user);
        }
        catch (Exception exception) when (exception is VrchatApiException or HttpRequestException)
        {
            signedIn.Dispose();
            throw new VrchatAuthenticationException(exception.Message);
        }
        finally
        {
            password = string.Empty;
        }
    }

    private static void TrySaveSession(
        VrchatSessionStore store,
        SavedVrchatSession session,
        WizardTerminalScreen screen)
    {
        try
        {
            store.Save(session);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or InvalidDataException)
        {
            screen.SetNotice("Signed in, but the session could not be saved · " + exception.Message);
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
                    MarkChanged("Manage tags", !current.Tags.SequenceEqual(draft.Tags, StringComparer.Ordinal), draft.Tags.Count + " total"),
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
                        if (thumbnailPath != null)
                        {
                            int action = screen.ReadChoice(
                                "Pending thumbnail change",
                                ["Choose a different image", "Discard thumbnail change", "Keep current draft"]);
                            if (action == 1)
                            {
                                thumbnailPath = null;
                                detailLog = ["Draft · Thumbnail change discarded; server image is unchanged."];
                                break;
                            }
                            if (action == 2) break;
                        }
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
                        (draft, detailLog) = EditTags(screen, draft);
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

    private static async Task<bool> EditAvatarAsync(
        WizardTerminalScreen screen,
        VrchatApiClient api,
        VrchatUser user,
        AvatarMetadataSnapshot initial,
        Action onSaved,
        CancellationToken cancellationToken)
    {
        AvatarMetadataSnapshot current = initial;
        AvatarMetadataSnapshot draft = current;
        string? thumbnailPath = null;
        IReadOnlyList<string> detailLog = [$"Loaded {current.Title} · server version {current.Version}"];

        while (true)
        {
            screen.SetSection("03", "EDIT", "Choose a field directly. Save, then continue editing without signing in again.");
            screen.ShowReview(
            [
                ("Account", user.DisplayName + "  ·  session active"),
                ("Avatar", draft.Title + "  ·  " + draft.Id),
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
                    MarkChanged("Manage tags", !current.Tags.SequenceEqual(draft.Tags, StringComparer.Ordinal), draft.Tags.Count + " total"),
                    "Save pending changes",
                    "Choose another content",
                    "Exit metadata session"
                ]);

            switch (selected)
            {
                case 0:
                    {
                        string value = ReadRequired(screen, "Avatar title", draft.Title);
                        detailLog = DraftLog("Title", draft.Title, value);
                        draft = draft with { Title = value };
                        break;
                    }
                case 1:
                    {
                        string value = screen.ReadText("Description (empty clears it)", null, secret: false, acceptEmpty: true);
                        detailLog = DraftLog("Description", draft.Description, value);
                        draft = draft with { Description = value };
                        break;
                    }
                case 2:
                    {
                        if (thumbnailPath != null)
                        {
                            int action = screen.ReadChoice(
                                "Pending thumbnail change",
                                ["Choose a different image", "Discard thumbnail change", "Keep current draft"]);
                            if (action == 1)
                            {
                                thumbnailPath = null;
                                detailLog = ["Draft · Thumbnail change discarded; server image is unchanged."];
                                break;
                            }
                            if (action == 2) break;
                        }
                        string value = ReadRequired(screen, "PNG or JPEG path", thumbnailPath, IsImageFile);
                        thumbnailPath = Path.GetFullPath(value);
                        detailLog = ["Draft · Thumbnail: " + (current.ImageUrl ?? "(none)"), "      → " + thumbnailPath];
                        break;
                    }
                case 3:
                    {
                        int action = screen.ReadChoice("Tag management", ["Add tags", "Remove one tag", "Replace all tags", "Back to metadata"]);
                        if (action == 3) break;
                        IReadOnlyList<string> tags = draft.Tags;
                        if (action == 0)
                            tags = tags.Concat(ParseTags(ReadRequired(screen, "Tags to add (comma-separated)"))).Distinct(StringComparer.Ordinal).ToArray();
                        else if (action == 1 && tags.Count > 0)
                        {
                            int tag = screen.ReadChoice("Tag to remove", tags.Append("Back").ToArray());
                            if (tag < tags.Count) tags = tags.Where((_, index) => index != tag).ToArray();
                        }
                        else if (action == 2)
                            tags = ParseTags(screen.ReadText("Replacement tags (comma-separated; empty clears all)", null, false, true));
                        detailLog = DraftLog("Tags", DisplayTags(draft.Tags), DisplayTags(tags));
                        draft = draft with { Tags = tags };
                        break;
                    }
                case 4:
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
                        screen.SetBusy("Updating avatar metadata without starting Unity…");
                        try
                        {
                            AvatarMetadataSnapshot updated = await api.UpdateAvatarAsync(
                                current,
                                draft,
                                thumbnailPath,
                                screen.SetBusy,
                                cancellationToken);
                            IReadOnlyList<MetadataChange> applied = VrchatApiClient.Compare(current, updated);
                            if (thumbnailPath != null)
                                applied = applied.Append(new MetadataChange("Thumbnail", current.ImageUrl ?? "(none)", updated.ImageUrl ?? Path.GetFileName(thumbnailPath))).ToArray();
                            detailLog = applied.Select(change => "Saved · " + MetadataApplication.FormatChange(change))
                                .Append("Server version " + current.Version + " → " + updated.Version)
                                .Take(4)
                                .ToArray();
                            current = updated;
                            draft = updated;
                            thumbnailPath = null;
                            onSaved();
                        }
                        catch (VrchatApiException exception)
                        {
                            detailLog = ["Save failed · " + exception.Message, "The login session and pending draft are still active; choose Save to retry."];
                        }
                        break;
                    }
                case 5:
                    if (PendingCount(current, draft, thumbnailPath) == 0 ||
                        screen.ReadYesNo("Discard pending changes and choose another content", false))
                        return true;
                    detailLog = ["Pending changes kept. Save or discard them before changing content."];
                    break;
                case 6:
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

    private static (WorldMetadataSnapshot Draft, IReadOnlyList<string> Log) EditTags(
        WizardTerminalScreen screen,
        WorldMetadataSnapshot draft)
    {
        int action = screen.ReadChoice(
            "Tag management",
            ["Add tags", "Remove one tag", "Replace all tags", "Back to metadata"]);
        if (action == 3) return (draft, ["Draft · Tags unchanged."]);

        if (action == 0)
        {
            string value = ReadRequired(screen, "Tags to add (comma-separated)");
            string[] added = ParseTags(value);
            IReadOnlyList<string> updated = draft.Tags.Concat(added).Distinct(StringComparer.Ordinal).ToArray();
            return (draft with { Tags = updated }, ["Draft · Added tags: " + DisplayTags(added)]);
        }

        if (action == 1)
        {
            if (draft.Tags.Count == 0) return (draft, ["Draft · This world has no tags to remove."]);
            int selected = screen.ReadChoice("Tag to remove", draft.Tags.Append("Back").ToArray());
            if (selected == draft.Tags.Count) return (draft, ["Draft · Tags unchanged."]);
            string removed = draft.Tags[selected];
            IReadOnlyList<string> updated = draft.Tags.Where(tag => tag != removed).ToArray();
            return (draft with { Tags = updated }, ["Draft · Removed tag: " + removed]);
        }

        string replacement = screen.ReadText(
            "Replacement tags (comma-separated; empty clears all)",
            null,
            secret: false,
            acceptEmpty: true);
        string[] tags = ParseTags(replacement);
        return (draft with { Tags = tags }, ["Draft · Replaced tags: " + DisplayTags(tags)]);
    }

    private static string[] ParseTags(string value) => value
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(tag => !tag.Contains('|'))
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    private static string DisplayTags(IReadOnlyCollection<string> tags) =>
        tags.Count == 0 ? "(none)" : string.Join(", ", tags);

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

    private static int PendingCount(AvatarMetadataSnapshot current, AvatarMetadataSnapshot draft, string? thumbnailPath) =>
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
