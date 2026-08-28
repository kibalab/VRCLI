using System.Globalization;
using System.ComponentModel;
using System.Text;

namespace KibaLab.WorldDeployment;

public sealed record InteractiveWizardResult(
    string[] Arguments,
    IReadOnlyDictionary<string, string> TemporarySecrets);

public sealed record InteractiveTwoFactorAnswer(string Method, string Code);

internal sealed record InteractiveAccount(VrchatUser User, string LoginHint, string Description);

public static class InteractiveWizard
{
    private static WizardTerminalScreen? activeScreen;

    public static bool IsWizardInvocation(string[] args) =>
        args.Length is 1 or 2 &&
        IsInteractiveCommand(args[0]) &&
        (args.Length == 1 || string.Equals(args[1], "--tui", StringComparison.OrdinalIgnoreCase));

    public static bool ShouldStart(string[] args)
    {
        if (!IsWizardInvocation(args) || Console.IsInputRedirected || Console.IsOutputRedirected) return false;
        string[] ciVariables = ["CI", "GITHUB_ACTIONS", "JENKINS_URL", "GITLAB_CI", "TF_BUILD", "TEAMCITY_VERSION", "BUILDKITE"];
        return !ciVariables.Any(name => IsTruthy(Environment.GetEnvironmentVariable(name)));
    }

    public static async Task<InteractiveWizardResult?> RunAsync(
        string[] invocation,
        CancellationToken cancellationToken = default)
    {
        using WizardTerminalScreen screen = new(cancellationToken);
        activeScreen = screen;
        try
        {
            OperationMode operation = ParseOperation(invocation[0]);
            if (operation == OperationMode.Meta)
                throw new InvalidOperationException("Use InteractiveMetadataEditor for metadata sessions.");
            screen.SetRoute(operation switch
            {
                OperationMode.Check => ["ACCOUNT", "PREFLIGHT", "REVIEW"],
                _ => ["ACCOUNT", "DEPLOYMENT", "REVIEW"]
            });
            TerminalProgressRenderer.ShouldUse(TerminalMode.Tui, false);
            WriteHeader();

            WriteSection("01", "ACCOUNT", "Verify the VRChat account before configuring the operation.");
            Dictionary<string, string> temporarySecrets = new(StringComparer.Ordinal);
            InteractiveAccount account = await AuthenticateInteractiveAsync(
                screen,
                temporarySecrets,
                cancellationToken);
            string username = account.LoginHint;
            string authenticationDescription = account.Description;
            activeScreen?.AddSummary("Account", "Verified  " + account.User.DisplayName);

            return operation switch
            {
                OperationMode.Check => RunCheckWizard(
                    screen, username, authenticationDescription, temporarySecrets),
                _ => RunDeploymentWizard(
                    screen, username, authenticationDescription, temporarySecrets)
            };
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            activeScreen = null;
        }
    }

    public static string CancellationMessage(string[] invocation)
    {
        OperationMode operation = invocation.Length > 0 && IsInteractiveCommand(invocation[0])
            ? ParseOperation(invocation[0])
            : OperationMode.Deploy;
        return operation switch
        {
            OperationMode.Meta => "Metadata update cancelled. No world record was changed.",
            OperationMode.Check => "Preflight check cancelled. No build or upload was started.",
            _ => "Deployment cancelled. No build or upload was started."
        };
    }

    private static async Task<InteractiveAccount> AuthenticateInteractiveAsync(
        WizardTerminalScreen screen,
        Dictionary<string, string> temporarySecrets,
        CancellationToken cancellationToken)
    {
        VrchatSessionStore store = new();
        IReadOnlyList<SavedVrchatSession> savedSessions;
        try
        {
            savedSessions = store.List();
        }
        catch (Win32Exception exception)
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
            screen.SetBusy("Validating the saved session for " + saved.DisplayName + "…");
            try
            {
                using VrchatApiClient api = new();
                VrchatUser user = await api.ResumeSessionAsync(saved.Tokens, cancellationToken);
                VrchatSessionTokens refreshed = api.ExportSession();
                SaveSession(store, saved with
                {
                    DisplayName = user.DisplayName,
                    Tokens = refreshed,
                    LastUsed = DateTimeOffset.UtcNow
                }, screen);
                AddSessionSecrets(temporarySecrets, refreshed);
                return new InteractiveAccount(user, saved.LoginHint, "Saved session · " + user.DisplayName);
            }
            catch (VrchatApiException exception)
            {
                try
                {
                    store.Delete(saved.UserId);
                }
                catch (Win32Exception)
                {
                }
                screen.SetNotice(saved.DisplayName + " session expired · " + exception.Message);
                savedSessions = savedSessions.Where(session => session.UserId != saved.UserId).ToArray();
            }
            catch (HttpRequestException exception)
            {
                throw new VrchatCredentialException("The saved session could not be checked: " + exception.Message);
            }
        }

        string username = PromptRequired(
            "VRChat username or account email",
            Environment.GetEnvironmentVariable(DeploymentEnvironment.Username));
        string password = Environment.GetEnvironmentVariable(DeploymentEnvironment.Password) ??
                          PromptSecret("VRChat password", required: true)!;
        screen.SetBusy("Signing in and verifying the account with VRChat…");
        try
        {
            using VrchatApiClient api = new();
            VrchatUser user = await VrchatAuthentication.SignInAsync(
                api,
                username,
                password,
                null,
                null,
                Environment.GetEnvironmentVariable(DeploymentEnvironment.TotpSecret),
                methods => Task.FromResult(PromptForTwoFactorChallenge(methods)),
                screen.SetBusy,
                cancellationToken);
            VrchatSessionTokens tokens = api.ExportSession();
            AddSessionSecrets(temporarySecrets, tokens);
            SaveSession(
                store,
                new SavedVrchatSession(
                    user.Id,
                    user.DisplayName,
                    username,
                    tokens,
                    DateTimeOffset.UtcNow),
                screen);
            return new InteractiveAccount(user, username, "Signed in now · " + user.DisplayName);
        }
        catch (VrchatApiException exception)
        {
            throw new VrchatCredentialException(exception.Message);
        }
        catch (HttpRequestException exception)
        {
            throw new VrchatCredentialException("VRChat sign-in could not be completed: " + exception.Message);
        }
        finally
        {
            password = string.Empty;
        }
    }

    private static void AddSessionSecrets(
        IDictionary<string, string> temporarySecrets,
        VrchatSessionTokens tokens)
    {
        temporarySecrets[DeploymentEnvironment.AuthToken] = tokens.AuthToken;
        if (!string.IsNullOrWhiteSpace(tokens.TwoFactorToken))
            temporarySecrets[DeploymentEnvironment.TwoFactorToken] = tokens.TwoFactorToken;
    }

    private static void SaveSession(
        VrchatSessionStore store,
        SavedVrchatSession session,
        WizardTerminalScreen screen)
    {
        try
        {
            store.Save(session);
        }
        catch (Win32Exception exception)
        {
            screen.SetNotice("Signed in, but the session could not be saved · " + exception.Message);
        }
    }

    private static InteractiveWizardResult? RunDeploymentWizard(
        WizardTerminalScreen screen,
        string username,
        string authenticationDescription,
        Dictionary<string, string> temporarySecrets)
    {
        while (true)
        {
            WriteSection("02", "DEPLOYMENT", "Choose the project, target world, scene, and platform.");
            string projectPath = PromptRequired("Unity project path", Directory.GetCurrentDirectory(), IsUnityProject);
            activeScreen?.AddSummary("Project", projectPath);
            string scene = PromptScene(projectPath);
            activeScreen?.AddSummary("Scene", scene);
            int mode = PromptChoice("Deployment mode", ["Update an existing world", "Create a new private world"]);
            activeScreen?.AddSummary("Mode", mode == 0 ? "Update existing world" : "Create new private world");
            int platform = PromptChoice("Target platform", ["StandaloneWindows64", "Android (Quest)"]);
            activeScreen?.AddSummary("Platform", platform == 0 ? "StandaloneWindows64" : "Android (Quest)");

            List<string> arguments = CreateArguments("deploy", username);
            Add(arguments, "--project", projectPath);
            Add(arguments, "--platform", platform == 0 ? "StandaloneWindows64" : "Android");
            Add(arguments, "--scene", scene);

            string targetDescription;
            if (mode == 0)
            {
                string blueprint = PromptOptionalBlueprint();
                if (!string.IsNullOrWhiteSpace(blueprint)) Add(arguments, "--blueprint", blueprint);
                targetDescription = string.IsNullOrWhiteSpace(blueprint) ? "Scene PipelineManager" : blueprint;
                activeScreen?.AddSummary("Target", targetDescription);
            }
            else
            {
                string name = PromptRequired("World name");
                string description = Prompt("Description");
                string thumbnail = PromptRequired("Thumbnail path", null, File.Exists);
                int capacity = PromptInteger("Maximum capacity", 32, 1, int.MaxValue);
                int recommended = PromptInteger("Recommended capacity", Math.Min(16, capacity), 1, capacity);
                string blueprintOutput = Prompt("Blueprint output file", Path.Combine(projectPath, "blueprint.txt"));
                arguments.Add("--new");
                Add(arguments, "--title", name);
                if (!string.IsNullOrWhiteSpace(description)) Add(arguments, "--description", description);
                Add(arguments, "--thumbnail", thumbnail);
                Add(arguments, "--capacity", capacity.ToString(CultureInfo.InvariantCulture));
                Add(arguments, "--recommended-capacity", recommended.ToString(CultureInfo.InvariantCulture));
                if (!string.IsNullOrWhiteSpace(blueprintOutput)) Add(arguments, "--blueprint-output", blueprintOutput);
                targetDescription = name + " (new private world)";
                activeScreen?.AddSummary("Target", targetDescription);
            }

            bool acceptsOwnership = PromptYesNo("I certify that I have the rights to upload this content", true);
            if (!acceptsOwnership)
            {
                activeScreen?.SetNotice("Deployment cancelled because content ownership was not certified.");
                ClearSecrets(temporarySecrets);
                return null;
            }
            arguments.Add("--yes");
            activeScreen?.AddSummary("Ownership", "Confirmed");

            WriteReview(
                "Confirm the deployment plan before anything is uploaded.",
                [
                    ("Account", username),
                    ("Auth", authenticationDescription),
                    ("Target", targetDescription),
                    ("Project", projectPath),
                    ("Scene", scene),
                    ("Platform", platform == 0 ? "StandaloneWindows64" : "Android")
                ]);

            if (!PromptYesNo("Start build and upload now", true))
            {
                continue;
            }
            screen.RetainForOperation();
            return new InteractiveWizardResult(arguments.ToArray(), temporarySecrets);
        }
    }

    private static InteractiveWizardResult? RunCheckWizard(
        WizardTerminalScreen screen,
        string username,
        string authenticationDescription,
        Dictionary<string, string> temporarySecrets)
    {
        WriteSection("02", "PREFLIGHT", "Choose the project, scene, and platform to inspect without uploading.");
        string projectPath = PromptRequired("Unity project path", Directory.GetCurrentDirectory(), IsUnityProject);
        activeScreen?.AddSummary("Project", projectPath);
        string scene = PromptScene(projectPath);
        activeScreen?.AddSummary("Scene", scene);
        int platform = PromptChoice("Target platform", ["StandaloneWindows64", "Android (Quest)"]);
        string platformName = platform == 0 ? "StandaloneWindows64" : "Android";
        activeScreen?.AddSummary("Platform", platformName);

        string blueprint = PromptOptionalBlueprint();
        activeScreen?.AddSummary("Target", string.IsNullOrWhiteSpace(blueprint) ? "Scene PipelineManager" : blueprint);

        List<string> arguments = CreateArguments("check", username);
        Add(arguments, "--project", projectPath);
        Add(arguments, "--scene", scene);
        Add(arguments, "--platform", platformName);
        if (!string.IsNullOrWhiteSpace(blueprint)) Add(arguments, "--blueprint", blueprint);

        WriteReview(
            "Confirm the read-only preflight plan. No bundle or server update will be created.",
            [
                ("Account", username),
                ("Auth", authenticationDescription),
                ("Target", string.IsNullOrWhiteSpace(blueprint) ? "Scene PipelineManager" : blueprint),
                ("Project", projectPath),
                ("Scene", scene),
                ("Platform", platformName)
            ]);
        if (!PromptYesNo("Run the preflight check now", true))
        {
            ClearSecrets(temporarySecrets);
            return null;
        }

        screen.RetainForOperation();
        return new InteractiveWizardResult(arguments.ToArray(), temporarySecrets);
    }

    private static string PromptOptionalBlueprint()
    {
        while (true)
        {
            string blueprint = Prompt("Blueprint override (blank uses the scene)");
            if (string.IsNullOrWhiteSpace(blueprint) || blueprint.StartsWith("wrld_", StringComparison.Ordinal))
                return blueprint;
            activeScreen?.SetNotice("Enter a world ID beginning with wrld_, or leave it blank.");
        }
    }

    public static InteractiveTwoFactorAnswer PromptForTwoFactorChallenge(IReadOnlyList<string> methods)
    {
        List<(string Method, string Label)> choices = new();
        if (methods.Contains("totp", StringComparer.OrdinalIgnoreCase))
            choices.Add(("totp", "Authenticator app code"));
        if (methods.Contains("emailOtp", StringComparer.OrdinalIgnoreCase))
            choices.Add(("emailOtp", "Email one-time code"));
        if (methods.Contains("otp", StringComparer.OrdinalIgnoreCase))
            choices.Add(("otp", "Recovery code"));
        if (choices.Count == 0) throw new InvalidOperationException("VRChat requested an unsupported two-factor method.");

        int selected = choices.Count == 1
            ? 0
            : PromptChoice("Verification method", choices.Select(choice => choice.Label).ToArray());
        string method = choices[selected].Method;
        string code = method == "totp"
            ? PromptSixDigitCode("Current authenticator code")
            : PromptSecret(choices[selected].Label, required: true)!;
        return new InteractiveTwoFactorAnswer(method, code);
    }

    public static void ApplySecrets(IReadOnlyDictionary<string, string> secrets)
    {
        foreach ((string name, string value) in secrets) Environment.SetEnvironmentVariable(name, value);
    }

    public static void ClearSecrets(IReadOnlyDictionary<string, string> secrets)
    {
        foreach (string name in secrets.Keys) Environment.SetEnvironmentVariable(name, null);
    }

    private static void WriteHeader()
    {
        if (activeScreen != null)
        {
            activeScreen.Enter();
            return;
        }
        int width = LayoutWidth();
        Console.WriteLine();
        Console.WriteLine("  " + Paint("╭─ ◆ VRCLI " + new string('─', Math.Max(1, width - 10)) + "╮", "36;1"));
        Console.WriteLine("  " + Paint("│", "36") + "  VRChat world deployment".PadRight(width) + Paint("│", "36"));
        Console.WriteLine("  " + Paint("│", "36") + Paint("  ACCOUNT  →  DEPLOYMENT  →  REVIEW".PadRight(width), "90") + Paint("│", "36"));
        Console.WriteLine("  " + Paint("╰" + new string('─', width) + "╯", "36"));
        Console.WriteLine();
        Console.WriteLine(Paint("  Secrets stay hidden and are cleared when VRCLI exits.", "90"));
        Console.WriteLine();
    }

    private static void WriteSection(string number, string title, string description)
    {
        if (activeScreen != null)
        {
            activeScreen.SetSection(number, title, description);
            return;
        }
        Console.WriteLine("  " + Paint(number, "36;1") + "  " + Paint(title, "1"));
        Console.WriteLine(Paint("      " + description, "90"));
        Console.WriteLine(Paint("  │", "90"));
    }

    private static void WriteReview(
        string description,
        IReadOnlyList<(string Label, string Value)> rows)
    {
        if (activeScreen != null)
        {
            activeScreen.SetSection("03", "REVIEW", description);
            activeScreen.ShowReview(rows);
            return;
        }
        int width = LayoutWidth();
        Console.WriteLine();
        Console.WriteLine("  " + Paint("03", "36;1") + "  " + Paint("REVIEW", "1"));
        Console.WriteLine(Paint("      " + description, "90"));
        Console.WriteLine();
        Console.WriteLine("  " + Paint("╭" + new string('─', width) + "╮", "90"));
        foreach ((string label, string value) in rows) WriteReviewRow(label, value, width);
        Console.WriteLine("  " + Paint("╰" + new string('─', width) + "╯", "90"));
        Console.WriteLine();
    }

    private static void WriteReviewRow(string label, string value, int width)
    {
        string prefix = "  " + label.PadRight(10);
        string content = prefix + Truncate(value, Math.Max(8, width - prefix.Length));
        Console.WriteLine("  " + Paint("│", "90") + content.PadRight(width) + Paint("│", "90"));
    }

    private static string Paint(string value, string code) =>
        Environment.GetEnvironmentVariable("NO_COLOR") == null
            ? "\x1b[" + code + "m" + value + "\x1b[0m"
            : value;

    private static int LayoutWidth()
    {
        try
        {
            return Math.Clamp(Console.WindowWidth - 6, 38, 72);
        }
        catch (IOException)
        {
            return 64;
        }
    }

    private static string Truncate(string value, int width) => value.Length <= width
        ? value
        : value[..Math.Max(1, width - 1)] + "…";

    private static string PromptSixDigitCode(string label)
    {
        while (true)
        {
            string code = PromptSecret(label, required: true)!;
            if (code.Length == 6 && code.All(char.IsDigit)) return code;
            if (activeScreen != null)
                activeScreen.SetNotice("Enter the 6-digit code shown by the authenticator.");
            else
                Console.WriteLine("  │  " + Paint("!", "33;1") + " Enter the 6-digit code shown by the authenticator.");
        }
    }

    private static string PromptRequired(string label, string? defaultValue = null, Func<string, bool>? validate = null)
    {
        while (true)
        {
            string value = Prompt(label, defaultValue);
            if (!string.IsNullOrWhiteSpace(value) && (validate == null || validate(value))) return value;
            if (activeScreen != null)
                activeScreen.SetNotice("Enter a valid value.");
            else
                Console.WriteLine("  │  " + Paint("!", "33;1") + " Enter a valid value.");
        }
    }

    private static string Prompt(string label, string? defaultValue = null)
    {
        if (activeScreen != null) return activeScreen.ReadText(label, defaultValue, secret: false);
        Console.Write("  │  " + Paint("›", "36;1") + " " + label);
        if (!string.IsNullOrWhiteSpace(defaultValue)) Console.Write(" [" + defaultValue + "]");
        Console.Write(": ");
        string value = (Console.ReadLine() ?? string.Empty).Trim().Trim('"');
        return string.IsNullOrWhiteSpace(value) ? defaultValue ?? string.Empty : value;
    }

    private static string? PromptSecret(string label, bool required)
    {
        if (activeScreen != null)
        {
            while (true)
            {
                string value = activeScreen.ReadText(label, null, secret: true);
                if (value.Length > 0 || !required) return value.Length == 0 ? null : value;
                activeScreen.SetNotice("A value is required.");
            }
        }
        while (true)
        {
            Console.Write("  │  " + Paint("›", "36;1") + " " + label + ": ");
            StringBuilder value = new();
            while (true)
            {
                ConsoleKeyInfo key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter) break;
                if (key.Key == ConsoleKey.Backspace && value.Length > 0)
                {
                    value.Length--;
                    Console.Write("\b \b");
                }
                else if (!char.IsControl(key.KeyChar))
                {
                    value.Append(key.KeyChar);
                    Console.Write("•");
                }
            }
            Console.WriteLine();
            if (value.Length > 0) return value.ToString();
            if (!required) return null;
            Console.WriteLine("  │  " + Paint("!", "33;1") + " A value is required.");
        }
    }

    private static int PromptChoice(string label, IReadOnlyList<string> choices)
    {
        if (activeScreen != null) return activeScreen.ReadChoice(label, choices);
        Console.WriteLine("  │  " + Paint("?", "36;1") + " " + label);
        for (int index = 0; index < choices.Count; index++)
            Console.WriteLine("  │    " + Paint("[" + (index + 1) + "]", "36") + " " + choices[index]);
        while (true)
        {
            Console.Write("  │  " + Paint("›", "36;1") + " Select [1-" + choices.Count + "]: ");
            string value = Console.ReadLine() ?? string.Empty;
            if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int selected) &&
                selected >= 1 && selected <= choices.Count)
                return selected - 1;
            Console.WriteLine("  │  " + Paint("!", "33;1") + " Select one of the listed choices.");
        }
    }

    private static string PromptScene(string projectPath)
    {
        IReadOnlyList<string> scenes = ProjectInspector.FindProjectScenes(projectPath);
        string? defaultScene = ProjectInspector.FindFirstEnabledScene(projectPath);
        if (string.IsNullOrWhiteSpace(defaultScene) && scenes.Count == 1) defaultScene = scenes[0];

        if (scenes.Count > 0)
        {
            if (activeScreen != null)
            {
                List<string> candidates = ["Project scenes"];
                candidates.AddRange(scenes.Take(10).Select(candidate => "· " + candidate));
                if (scenes.Count > 10) candidates.Add("· … and " + (scenes.Count - 10) + " more");
                activeScreen.SetContext(candidates);
            }
            else
            {
                Console.WriteLine("  │  " + Paint("Project scenes", "90;1"));
                foreach (string candidate in scenes.Take(10)) Console.WriteLine(Paint("  │    · " + candidate, "90"));
                if (scenes.Count > 10) Console.WriteLine(Paint("  │    · … and " + (scenes.Count - 10) + " more", "90"));
            }
        }

        return PromptRequired(
            "Build scene (Assets/...unity)",
            defaultScene,
            value => IsExistingProjectScene(projectPath, value));
    }

    private static bool IsExistingProjectScene(string projectPath, string scenePath)
    {
        try
        {
            string normalized = ProjectInspector.NormalizeScenePath(projectPath, scenePath);
            return File.Exists(Path.Combine(projectPath, normalized.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsUnityProject(string projectPath) =>
        ProjectInspector.Inspect(projectPath, null, requireScene: false).IsValid;

    private static int PromptInteger(string label, int defaultValue, int minimum, int maximum)
    {
        while (true)
        {
            string value = Prompt(label, defaultValue.ToString(CultureInfo.InvariantCulture));
            if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) &&
                parsed >= minimum && parsed <= maximum)
                return parsed;
            if (activeScreen != null)
                activeScreen.SetNotice("Enter a number from " + minimum + " to " + maximum + ".");
            else
                Console.WriteLine("  │  " + Paint("!", "33;1") + " Enter a number from " + minimum + " to " + maximum + ".");
        }
    }

    private static bool PromptYesNo(string label, bool defaultValue)
    {
        if (activeScreen != null) return activeScreen.ReadYesNo(label, defaultValue);
        string suffix = defaultValue ? " [Y/n]: " : " [y/N]: ";
        while (true)
        {
            Console.Write("  │  " + Paint("›", "36;1") + " " + label + suffix);
            string value = (Console.ReadLine() ?? string.Empty).Trim();
            if (value.Length == 0) return defaultValue;
            if (string.Equals(value, "y", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(value, "n", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "no", StringComparison.OrdinalIgnoreCase)) return false;
        }
    }

    private static void Add(List<string> arguments, string option, string value)
    {
        arguments.Add(option);
        arguments.Add(value);
    }

    private static List<string> CreateArguments(string command, string username)
    {
        List<string> arguments = [command, "--tui"];
        Add(arguments, "--login", username);
        return arguments;
    }

    private static bool IsInteractiveCommand(string value) =>
        string.Equals(value, "deploy", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "meta", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "check", StringComparison.OrdinalIgnoreCase);

    private static OperationMode ParseOperation(string value)
    {
        if (string.Equals(value, "meta", StringComparison.OrdinalIgnoreCase)) return OperationMode.Meta;
        if (string.Equals(value, "check", StringComparison.OrdinalIgnoreCase)) return OperationMode.Check;
        return OperationMode.Deploy;
    }

    private static bool IsTruthy(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !string.Equals(value, "0", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
}
