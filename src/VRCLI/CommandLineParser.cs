using System.Globalization;
using System.Text.Json;

namespace KibaLab.WorldDeployment;

public sealed class CommandLineParser
{
    private static readonly HashSet<string> BooleanOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "help",
        "skip-vpm-resolve",
        "interactive-two-factor",
        "verbose",
        "tui",
        "plain",
        "json",
        "yes",
        "new"
    };

    private static readonly HashSet<string> KnownOptions = new(BooleanOptions, StringComparer.OrdinalIgnoreCase)
    {
        "scene",
        "target",
        "blueprint",
        "blueprint-output",
        "description",
        "thumbnail",
        "capacity",
        "recommended-capacity",
        "tag",
        "remove-tag",
        "password",
        "platform",
        "two-factor-code",
        "two-factor-method",
        "unity",
        "timeout",
        "config",
        "project",
        "new",
        "login",
        "title"
    };

    public ParseResult Parse(string[] args, string? newBlueprintOverride = null)
    {
        if (args.Length == 0)
        {
            return ParseResult.Help();
        }

        OperationMode operation;
        int index;
        if (TryParseOperation(args[0], out operation))
        {
            index = 1;
        }
        else if (args[0].StartsWith("--", StringComparison.Ordinal))
        {
            operation = OperationMode.Deploy;
            index = 0;
        }
        else
        {
            return ParseResult.Failure($"Unknown command: {args[0]}. Expected deploy, meta, or check.");
        }
        Dictionary<string, string?> values = new(StringComparer.OrdinalIgnoreCase);
        List<string> tags = new();
        List<string> removedTags = new();
        bool hasTags = false;
        bool hasRemovedTags = false;

        while (index < args.Length)
        {
            string token = args[index];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                return ParseResult.Failure($"Unknown command or argument: {token}");
            }

            string suppliedKey = token[2..];
            if (string.IsNullOrWhiteSpace(suppliedKey))
            {
                return ParseResult.Failure("An empty option was provided.");
            }

            if (!KnownOptions.Contains(suppliedKey))
            {
                return ParseResult.Failure($"Unknown option: --{suppliedKey}");
            }

            string key = suppliedKey.ToLowerInvariant();

            if (string.Equals(key, "tag", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "remove-tag", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    return ParseResult.Failure($"Option --{key} requires a value.");
                }

                string tag = args[index + 1].Trim();
                if (tag.Length == 0 || tag.Contains('|') || tag.Contains(','))
                {
                    return ParseResult.Failure($"--{key} must be non-empty and cannot contain '|' or ','.");
                }
                if (key == "tag")
                {
                    tags.Add(tag);
                    hasTags = true;
                }
                else
                {
                    removedTags.Add(tag);
                    hasRemovedTags = true;
                }
                index += 2;
                continue;
            }

            if (values.ContainsKey(key))
            {
                return ParseResult.Failure($"Option --{suppliedKey} conflicts with another option for the same setting.");
            }

            if (BooleanOptions.Contains(suppliedKey))
            {
                if (string.Equals(suppliedKey, "new", StringComparison.OrdinalIgnoreCase) &&
                    index + 1 < args.Length &&
                    !args[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    return ParseResult.Failure("--new is a flag. Provide the world name with --title <name>.");
                }
                values[key] = "true";
                index++;
                continue;
            }

            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                return ParseResult.Failure($"Option --{suppliedKey} requires a value.");
            }

            values[key] = args[index + 1];
            index += 2;
        }

        if (values.ContainsKey("help"))
        {
            return ParseResult.Help();
        }

        int outputModes = (values.ContainsKey("tui") ? 1 : 0) +
                          (values.ContainsKey("plain") ? 1 : 0) +
                          (values.ContainsKey("json") ? 1 : 0);
        if (outputModes > 1)
        {
            return ParseResult.Failure("Use only one output mode: --tui, --plain, or --json.");
        }

        SetMissing(values, "project", Environment.GetEnvironmentVariable(DeploymentEnvironment.Project));
        SetMissing(values, "login", Environment.GetEnvironmentVariable(DeploymentEnvironment.Username));
        SetMissing(values, "platform", Environment.GetEnvironmentVariable(DeploymentEnvironment.Platform));
        if (!values.ContainsKey("new"))
            SetMissing(values, "blueprint", Environment.GetEnvironmentVariable(DeploymentEnvironment.BlueprintId));

        if (!TryApplyProjectConfig(values, tags, ref hasTags, out string? configDirectory, out string? configError))
        {
            return ParseResult.Failure(configError!);
        }

        string projectPath = Get(values, "project") ?? configDirectory ?? Directory.GetCurrentDirectory();
        bool isNew = values.ContainsKey("new");
        string? blueprint = Get(values, "blueprint");
        string? username = Get(values, "login");
        string platformValue = Get(values, "platform") ?? "StandaloneWindows64";

        if (string.IsNullOrWhiteSpace(username))
        {
            return ParseResult.Failure("Set VRCLI_USERNAME or provide --login <username-or-email>.");
        }

        if (operation == OperationMode.Deploy && isNew && !string.IsNullOrWhiteSpace(blueprint))
        {
            return ParseResult.Failure(
                "Choose one target: --new cannot be combined with --blueprint, VRCLI_BLUEPRINT_ID, or a configured Blueprint ID.");
        }

        string? title = Get(values, "title");
        string? description = Get(values, "description");
        string? thumbnailPath = Get(values, "thumbnail");
        bool hasCapacity = values.ContainsKey("capacity");
        bool hasRecommendedCapacity = values.ContainsKey("recommended-capacity");
        bool hasMetadata = title != null || description != null || thumbnailPath != null ||
                           hasCapacity || hasRecommendedCapacity || hasTags || hasRemovedTags;

        if (operation != OperationMode.Deploy && isNew)
        {
            return ParseResult.Failure($"--new is only valid with the deploy command.");
        }
        if (operation == OperationMode.Meta)
        {
            if (values.ContainsKey("target"))
                return ParseResult.Failure("--target is only valid with deploy or check.");
            if (string.IsNullOrWhiteSpace(blueprint))
                return ParseResult.Failure("The meta command requires --blueprint <wrld_id>.");
            if (!blueprint.StartsWith("wrld_", StringComparison.Ordinal))
                return ParseResult.Failure("The meta command requires a world Blueprint ID beginning with 'wrld_'.");
            if (!hasMetadata)
            {
                return ParseResult.Failure(
                    "The meta command requires at least one metadata option: --title, --description, " +
                    "--thumbnail, --capacity, --recommended-capacity, --tag, or --remove-tag.");
            }
        }
        if (operation == OperationMode.Check && hasMetadata)
        {
            return ParseResult.Failure("World metadata options are not valid with the check command.");
        }
        if (operation == OperationMode.Deploy && hasRemovedTags)
        {
            return ParseResult.Failure("--remove-tag is only valid with the meta command.");
        }
        string? conflictingTag = tags.Intersect(removedTags, StringComparer.Ordinal).FirstOrDefault();
        if (conflictingTag != null)
        {
            return ParseResult.Failure($"Tag '{conflictingTag}' cannot be added and removed in the same command.");
        }
        if (operation != OperationMode.Deploy && values.ContainsKey("blueprint-output"))
        {
            return ParseResult.Failure("--blueprint-output is only valid with the deploy command.");
        }

        string? targetPath = Get(values, "target");
        if (targetPath != null && string.IsNullOrWhiteSpace(targetPath))
            return ParseResult.Failure("--target must be a non-empty Unity Hierarchy path.");

        if (isNew)
        {
            List<string> missingMetadata = new();
            if (string.IsNullOrWhiteSpace(title)) missingMetadata.Add("--title");
            if (string.IsNullOrWhiteSpace(thumbnailPath)) missingMetadata.Add("--thumbnail");
            if (missingMetadata.Count > 0)
            {
                return ParseResult.Failure(
                    "New worlds require --new, --title <name>, and --thumbnail <image>. Missing: " +
                    string.Join(", ", missingMetadata));
            }

            if (newBlueprintOverride != null &&
                !newBlueprintOverride.StartsWith("wrld_", StringComparison.Ordinal))
            {
                return ParseResult.Failure("The internally preserved new-world Blueprint ID is invalid.");
            }
            blueprint = newBlueprintOverride ?? "wrld_" + Guid.NewGuid();
        }
        if (!string.IsNullOrWhiteSpace(blueprint) &&
            !blueprint.StartsWith("wrld_", StringComparison.Ordinal) &&
            !blueprint.StartsWith("avtr_", StringComparison.Ordinal))
        {
            return ParseResult.Failure("--blueprint must be a VRChat content ID beginning with 'wrld_' or 'avtr_'.");
        }
        blueprint ??= string.Empty;

        if (!TryParseCapacity(
                Get(values, "capacity"),
                Get(values, "recommended-capacity"),
                isNew,
                out int capacity,
                out int recommendedCapacity,
                out string? capacityError))
        {
            return ParseResult.Failure(capacityError!);
        }

        if (!TryParsePlatform(platformValue, out BuildPlatform platform))
        {
            return ParseResult.Failure("--platform must be StandaloneWindows64 or Android.");
        }

        string? password = Get(values, "password") ?? Environment.GetEnvironmentVariable(DeploymentEnvironment.Password);
        bool hasSavedSession = !string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable(DeploymentEnvironment.AuthToken));

        if (string.IsNullOrEmpty(password) && !hasSavedSession)
        {
            return ParseResult.Failure("Set VRCLI_PASSWORD or provide --password <password>.");
        }
        password ??= string.Empty;

        if (!TryParseTimeout(Get(values, "timeout"), out TimeSpan timeout, out string? timeoutError))
        {
            return ParseResult.Failure(timeoutError!);
        }

        string fullProjectPath;
        try
        {
            fullProjectPath = Path.GetFullPath(projectPath!);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return ParseResult.Failure($"Invalid project path: {exception.Message}");
        }

        if (!TryGetFullPath(thumbnailPath, "thumbnail", out string? fullThumbnailPath, out string? thumbnailError))
        {
            return ParseResult.Failure(thumbnailError!);
        }

        if (!TryGetFullPath(Get(values, "blueprint-output"), "blueprint output", out string? blueprintOutputPath, out string? blueprintOutputError))
        {
            return ParseResult.Failure(blueprintOutputError!);
        }

        bool interactiveTwoFactor = values.ContainsKey("interactive-two-factor");
        string? twoFactorCode = interactiveTwoFactor
            ? null
            : Get(values, "two-factor-code") ?? Environment.GetEnvironmentVariable(DeploymentEnvironment.TwoFactorCode);
        string? twoFactorMethod = interactiveTwoFactor
            ? null
            : Get(values, "two-factor-method") ?? Environment.GetEnvironmentVariable(DeploymentEnvironment.TwoFactorMethod);
        string? totpSecret = interactiveTwoFactor || !string.IsNullOrWhiteSpace(twoFactorCode)
            ? null
            : Environment.GetEnvironmentVariable(DeploymentEnvironment.TotpSecret);
        if (!string.IsNullOrWhiteSpace(twoFactorCode) && !string.IsNullOrWhiteSpace(totpSecret))
        {
            return ParseResult.Failure("Use only one of --two-factor-code/VRCLI_TWO_FACTOR_CODE or VRCLI_TOTP_SECRET.");
        }
        if (interactiveTwoFactor && values.ContainsKey("two-factor-code"))
        {
            return ParseResult.Failure("Use --interactive-two-factor by itself, without another two-factor option.");
        }
        if (!string.IsNullOrWhiteSpace(twoFactorCode) && !TryParseTwoFactorMethod(twoFactorMethod, out twoFactorMethod))
        {
            return ParseResult.Failure(
                "--two-factor-code requires --two-factor-method <totp|emailOtp|otp> or VRCLI_TWO_FACTOR_METHOD.");
        }
        if (string.IsNullOrWhiteSpace(twoFactorCode) && !string.IsNullOrWhiteSpace(twoFactorMethod))
        {
            return ParseResult.Failure("--two-factor-method is only valid with --two-factor-code.");
        }

        DeployOptions options = new(
            operation,
            fullProjectPath,
            blueprint,
            isNew,
            title,
            description,
            fullThumbnailPath,
            capacity,
            recommendedCapacity,
            hasCapacity,
            hasRecommendedCapacity,
            tags.Distinct(StringComparer.Ordinal).ToArray(),
            hasTags,
            removedTags.Distinct(StringComparer.Ordinal).ToArray(),
            hasRemovedTags,
            blueprintOutputPath,
            username!,
            password,
            platform,
            Get(values, "scene"),
            targetPath,
            Get(values, "unity"),
            twoFactorCode,
            twoFactorMethod,
            totpSecret,
            interactiveTwoFactor,
            timeout,
            values.ContainsKey("skip-vpm-resolve"),
            values.ContainsKey("yes"),
            values.ContainsKey("verbose"),
            values.ContainsKey("tui") ? TerminalMode.Tui :
            values.ContainsKey("plain") ? TerminalMode.Plain :
            values.ContainsKey("json") ? TerminalMode.Json : TerminalMode.Auto);

        return ParseResult.Success(options);
    }

    private static bool TryParseOperation(string value, out OperationMode operation)
    {
        if (string.Equals(value, "deploy", StringComparison.OrdinalIgnoreCase))
        {
            operation = OperationMode.Deploy;
            return true;
        }
        if (string.Equals(value, "meta", StringComparison.OrdinalIgnoreCase))
        {
            operation = OperationMode.Meta;
            return true;
        }
        if (string.Equals(value, "check", StringComparison.OrdinalIgnoreCase))
        {
            operation = OperationMode.Check;
            return true;
        }
        operation = default;
        return false;
    }

    private static bool TryApplyProjectConfig(
        Dictionary<string, string?> values,
        List<string> tags,
        ref bool hasTags,
        out string? configDirectory,
        out string? error)
    {
        configDirectory = null;
        error = null;
        string? configPath = Get(values, "config");
        bool explicitConfig = configPath != null;
        try
        {
            if (configPath == null)
            {
                string? requestedProject = Get(values, "project");
                if (!string.IsNullOrWhiteSpace(requestedProject))
                {
                    string projectCandidate = Path.Combine(Path.GetFullPath(requestedProject), "vrcli.json");
                    if (File.Exists(projectCandidate)) configPath = projectCandidate;
                }

                string currentCandidate = Path.Combine(Directory.GetCurrentDirectory(), "vrcli.json");
                if (configPath == null && File.Exists(currentCandidate)) configPath = currentCandidate;
            }

            if (configPath == null) return true;
            string fullConfigPath = Path.GetFullPath(configPath);
            if (!File.Exists(fullConfigPath))
            {
                error = $"Configuration file was not found: {fullConfigPath}";
                return false;
            }

            configDirectory = Path.GetDirectoryName(fullConfigPath)!;
            ProjectConfig config = ProjectConfig.Load(fullConfigPath);
            SetMissing(values, "project", ResolveFromConfig(config.Project, configDirectory));
            if (!values.ContainsKey("new"))
                SetMissing(values, "blueprint", config.Blueprint);
            if (!values.ContainsKey("blueprint") && config.NewWorld == true)
                values["new"] = "true";
            SetMissing(values, "scene", config.Scene);
            SetMissing(values, "target", config.Target);
            SetMissing(values, "platform", config.Platform);
            SetMissing(values, "login", config.Login);
            SetMissing(values, "title", config.Title);
            SetMissing(values, "description", config.Description);
            SetMissing(values, "thumbnail", ResolveFromConfig(config.Thumbnail, configDirectory));
            SetMissing(values, "capacity", config.Capacity?.ToString(CultureInfo.InvariantCulture));
            SetMissing(values, "recommended-capacity", config.RecommendedCapacity?.ToString(CultureInfo.InvariantCulture));
            SetMissing(values, "blueprint-output", ResolveFromConfig(config.BlueprintOutput, configDirectory));
            SetMissing(values, "unity", ResolveFromConfig(config.Unity, configDirectory));
            SetMissing(values, "timeout", config.Timeout?.ToString(CultureInfo.InvariantCulture));
            if (config.Plain == true && !values.ContainsKey("tui")) values["plain"] = "true";
            if (config.Yes == true) values["yes"] = "true";
            if (config.SkipVpmResolve == true) values["skip-vpm-resolve"] = "true";
            if (!hasTags && config.Tags != null)
            {
                tags.AddRange(config.Tags);
                hasTags = true;
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            string source = explicitConfig ? $" '{configPath}'" : string.Empty;
            error = $"Unable to read VRCLI configuration{source}: {exception.Message}";
            return false;
        }
    }

    private static void SetMissing(IDictionary<string, string?> values, string key, string? value)
    {
        if (!values.ContainsKey(key) && value != null) values[key] = value;
    }

    private static string? ResolveFromConfig(string? value, string configDirectory)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        return Path.IsPathFullyQualified(value) ? value : Path.GetFullPath(Path.Combine(configDirectory, value));
    }

    private static bool TryParsePlatform(string value, out BuildPlatform platform)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "standalonewindows64":
                platform = BuildPlatform.StandaloneWindows64;
                return true;
            case "android":
                platform = BuildPlatform.Android;
                return true;
            default:
                platform = default;
                return false;
        }
    }

    private static bool TryParseTwoFactorMethod(string? value, out string? method)
    {
        method = value?.Trim().ToLowerInvariant() switch
        {
            "totp" => "totp",
            "emailotp" => "emailOtp",
            "otp" => "otp",
            _ => null
        };
        return method != null;
    }

    private static bool TryParseCapacity(
        string? capacityValue,
        string? recommendedValue,
        bool isNew,
        out int capacity,
        out int recommendedCapacity,
        out string? error)
    {
        error = null;
        capacity = 32;
        recommendedCapacity = 16;

        if (capacityValue != null &&
            (!int.TryParse(capacityValue, NumberStyles.None, CultureInfo.InvariantCulture, out capacity) || capacity < 1))
        {
            error = "--capacity must be an integer of at least 1.";
            return false;
        }

        recommendedCapacity = isNew ? Math.Min(16, capacity) : 16;
        if (recommendedValue != null &&
            (!int.TryParse(recommendedValue, NumberStyles.None, CultureInfo.InvariantCulture, out recommendedCapacity) ||
             recommendedCapacity < 1 || (capacityValue != null && recommendedCapacity > capacity)))
        {
            error = capacityValue == null
                ? "--recommended-capacity must be an integer of at least 1."
                : "--recommended-capacity must be an integer from 1 to --capacity.";
            return false;
        }

        return true;
    }

    private static bool TryGetFullPath(string? value, string label, out string? fullPath, out string? error)
    {
        fullPath = null;
        error = null;
        if (value == null) return true;

        try
        {
            fullPath = Path.GetFullPath(value);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"Invalid {label} path: {exception.Message}";
            return false;
        }
    }

    private static bool TryParseTimeout(string? value, out TimeSpan timeout, out string? error)
    {
        error = null;
        timeout = TimeSpan.FromMinutes(60);
        if (value == null) return true;

        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds) || seconds < 30)
        {
            error = "--timeout must be an integer of at least 30 seconds.";
            return false;
        }

        timeout = TimeSpan.FromSeconds(seconds);
        return true;
    }

    private static string? Get(IReadOnlyDictionary<string, string?> values, string key) =>
        values.TryGetValue(key, out string? value) ? value : null;
}

public sealed record ParseResult(DeployOptions? Options, string? Error, bool ShowHelp)
{
    public static ParseResult Success(DeployOptions options) => new(options, null, false);
    public static ParseResult Failure(string error) => new(null, error, false);
    public static ParseResult Help() => new(null, null, true);
}
