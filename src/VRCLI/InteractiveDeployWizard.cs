using System.Globalization;
using System.Text;

namespace KibaLab.WorldDeployment;

public sealed record InteractiveWizardResult(
    string[] Arguments,
    IReadOnlyDictionary<string, string> TemporarySecrets);

public sealed record InteractiveTwoFactorAnswer(string Method, string Code);

public static class InteractiveDeployWizard
{
    private static WizardTerminalScreen? activeScreen;

    public static bool IsWizardInvocation(string[] args) =>
        (args.Length == 1 && string.Equals(args[0], "deploy", StringComparison.OrdinalIgnoreCase)) ||
        (args.Length == 2 && string.Equals(args[0], "deploy", StringComparison.OrdinalIgnoreCase) &&
         string.Equals(args[1], "--tui", StringComparison.OrdinalIgnoreCase));

    public static bool ShouldStart(string[] args)
    {
        if (!IsWizardInvocation(args) || Console.IsInputRedirected || Console.IsOutputRedirected) return false;
        string[] ciVariables = ["CI", "GITHUB_ACTIONS", "JENKINS_URL", "GITLAB_CI", "TF_BUILD", "TEAMCITY_VERSION", "BUILDKITE"];
        return !ciVariables.Any(name => IsTruthy(Environment.GetEnvironmentVariable(name)));
    }

    public static InteractiveWizardResult? Run(CancellationToken cancellationToken = default)
    {
        using WizardTerminalScreen screen = new(cancellationToken);
        activeScreen = screen;
        try
        {
            TerminalProgressRenderer.ShouldUse(TerminalMode.Tui, false);
            WriteHeader();

            WriteSection("01", "ACCOUNT", "Verify the publisher identity before configuring the build.");
            string username = PromptRequired("VRChat username or account email");
            Dictionary<string, string> temporarySecrets = new(StringComparer.Ordinal);
            string password;
            string? configuredPassword = Environment.GetEnvironmentVariable(DeploymentEnvironment.Password);
            if (string.IsNullOrEmpty(configuredPassword))
            {
                password = PromptSecret("VRChat password", required: true)!;
                temporarySecrets[DeploymentEnvironment.Password] = password;
            }
            else
            {
                password = configuredPassword;
            }

            activeScreen?.SetBusy("Validating username/email and password with VRChat…");
            VrchatCredentialValidationResult credentialValidation;
            try
            {
                credentialValidation = VrchatCredentialValidator.ValidateAsync(
                        username,
                        password,
                        cancellationToken: cancellationToken)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (VrchatCredentialException)
            {
                throw;
            }
            string accountName = credentialValidation.DisplayName ?? username;
            string validationMessage = credentialValidation.IsFullyAuthenticated
                ? "Account verified  " + accountName
                : "Password accepted  · identity will be verified during sign-in";
            if (activeScreen != null)
                activeScreen.AddSummary("Account", validationMessage);
            else
                Console.WriteLine("  │  " + Paint("✓", "32;1") + " " + validationMessage);

            bool hasTotpSecret = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(DeploymentEnvironment.TotpSecret));
            string authenticationDescription = hasTotpSecret
                ? "saved session → automatic TOTP only if requested"
                : "saved session → ask only if VRChat requests verification";

            WriteSection("02", "DEPLOYMENT", "Choose the project, target world, scene, and platform.");
            string projectPath = PromptRequired("Unity project path", Directory.GetCurrentDirectory(), Directory.Exists);
            activeScreen?.AddSummary("Project", projectPath);
            string scene = PromptScene(projectPath);
            activeScreen?.AddSummary("Scene", scene);
            int mode = PromptChoice("Deployment mode", ["Update an existing world", "Create a new private world"]);
            activeScreen?.AddSummary("Mode", mode == 0 ? "Update existing world" : "Create new private world");
            int platform = PromptChoice("Target platform", ["StandaloneWindows64", "Android (Quest)"]);
            activeScreen?.AddSummary("Platform", platform == 0 ? "StandaloneWindows64" : "Android (Quest)");

            List<string> arguments = ["deploy", "--project", projectPath, "--platform", platform == 0 ? "StandaloneWindows64" : "Android", "--tui"];
            Add(arguments, "--scene", scene);
            Add(arguments, "--login", username);
            if (!hasTotpSecret)
                arguments.Add("--interactive-two-factor");

            string targetDescription;
            if (mode == 0)
            {
                string blueprint = PromptRequired("Blueprint ID (wrld_...)", null, value => value.StartsWith("wrld_", StringComparison.Ordinal));
                Add(arguments, "--blueprint", blueprint);
                targetDescription = blueprint;
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
                targetDescription,
                projectPath,
                scene,
                platform == 0 ? "StandaloneWindows64" : "Android",
                username,
                authenticationDescription);

            if (!PromptYesNo("Start build and upload now", false))
            {
                ClearSecrets(temporarySecrets);
                return null;
            }
            screen.RetainForDeployment();
            return new InteractiveWizardResult(arguments.ToArray(), temporarySecrets);
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
        string target,
        string project,
        string scene,
        string platform,
        string username,
        string authentication)
    {
        if (activeScreen != null)
        {
            activeScreen.SetSection("03", "REVIEW", "Confirm the deployment plan before anything is uploaded.");
            activeScreen.ShowReview(
            [
                ("Account", username),
                ("Auth", authentication),
                ("Target", target),
                ("Project", project),
                ("Scene", scene),
                ("Platform", platform)
            ]);
            return;
        }
        int width = LayoutWidth();
        Console.WriteLine();
        Console.WriteLine("  " + Paint("03", "36;1") + "  " + Paint("REVIEW", "1"));
        Console.WriteLine(Paint("      Confirm the deployment plan before anything is uploaded.", "90"));
        Console.WriteLine();
        Console.WriteLine("  " + Paint("╭" + new string('─', width) + "╮", "90"));
        WriteReviewRow("Account", username, width);
        WriteReviewRow("Auth", authentication, width);
        WriteReviewRow("Target", target, width);
        WriteReviewRow("Project", project, width);
        WriteReviewRow("Scene", scene, width);
        WriteReviewRow("Platform", platform, width);
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

    private static bool IsTruthy(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !string.Equals(value, "0", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
}
