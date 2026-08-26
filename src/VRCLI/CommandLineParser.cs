using System.Globalization;
using System.Text.Json;

namespace KibaLab.VRCLI;

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
        "yes",
        "new"
    };

    private static readonly HashSet<string> KnownOptions = new(BooleanOptions, StringComparer.OrdinalIgnoreCase)
    {
        "scene",
        "blueprint",
        "blueprint-output",
        "description",
        "thumbnail",
        "capacity",
        "recommended-capacity",
        "tag",
        "password",
        "platform",
        "two-factor-code",
        "unity",
        "timeout",
        "config",
        "project",
        "new",
        "login",
        "title"
    };

    public ParseResult Parse(string[] args, TextReader input, string? createBlueprintOverride = null)
    {
        if (args.Length == 0)
        {
            return ParseResult.Help();
        }

        int index = string.Equals(args[0], "deploy", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        Dictionary<string, string?> values = new(StringComparer.OrdinalIgnoreCase);
        List<string> tags = new();
        bool tagsSpecified = false;

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

            if (string.Equals(key, "tag", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    return ParseResult.Failure("Option --tag requires a value.");
                }

                string tag = args[index + 1].Trim();
                if (tag.Length == 0 || tag.Contains('|') || tag.Contains(','))
                {
                    return ParseResult.Failure("--tag must be non-empty and cannot contain '|' or ','.");
                }
                tags.Add(tag);
                tagsSpecified = true;
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

        SetMissing(values, "project", Environment.GetEnvironmentVariable("VRCLI_PROJECT"));
        SetMissing(values, "login", Environment.GetEnvironmentVariable("VRCLI_USERNAME"));
        SetMissing(values, "platform", Environment.GetEnvironmentVariable("VRCLI_PLATFORM"));
        if (!values.ContainsKey("new"))
            SetMissing(values, "blueprint", Environment.GetEnvironmentVariable("VRCLI_BLUEPRINT_ID"));

        if (!TryApplyProjectConfig(values, tags, ref tagsSpecified, out string? configDirectory, out string? configError))
        {
            return ParseResult.Failure(configError!);
        }

        string? projectPath = Get(values, "project") ??
                              Environment.GetEnvironmentVariable("VRCLI_PROJECT") ??
                              configDirectory ??
                              Directory.GetCurrentDirectory();
        bool createWorld = values.ContainsKey("new");
        string? blueprint = Get(values, "blueprint") ??
                            (createWorld ? null : Environment.GetEnvironmentVariable("VRCLI_BLUEPRINT_ID"));
        string? username = Get(values, "login") ?? Environment.GetEnvironmentVariable("VRCLI_USERNAME");
        string? platformValue = Get(values, "platform") ??
                                Environment.GetEnvironmentVariable("VRCLI_PLATFORM") ??
                                "StandaloneWindows64";

        if (string.IsNullOrWhiteSpace(username))
        {
            return ParseResult.Failure("Set VRCLI_USERNAME or provide --login <username-or-email>.");
        }

        if (createWorld == !string.IsNullOrWhiteSpace(blueprint))
        {
            return ParseResult.Failure(
                "Choose one target: --blueprint <wrld_id>, --new, VRCLI_BLUEPRINT_ID, or a vrcli.json setting.");
        }

        string? worldName = Get(values, "title");
        string? worldDescription = Get(values, "description");
        string? thumbnailPath = Get(values, "thumbnail");
        if (createWorld)
        {
            List<string> createMissing = new();
            if (string.IsNullOrWhiteSpace(worldName)) createMissing.Add("--title");
            if (string.IsNullOrWhiteSpace(thumbnailPath)) createMissing.Add("--thumbnail");
            if (createMissing.Count > 0)
            {
                return ParseResult.Failure(
                    "New worlds require --new, --title <name>, and --thumbnail <image>. Missing: " +
                    string.Join(", ", createMissing));
            }

            if (createBlueprintOverride != null &&
                !createBlueprintOverride.StartsWith("wrld_", StringComparison.Ordinal))
            {
                return ParseResult.Failure("The internally preserved new-world Blueprint ID is invalid.");
            }
            blueprint = createBlueprintOverride ?? "wrld_" + Guid.NewGuid();
        }
        if (!blueprint!.StartsWith("wrld_", StringComparison.Ordinal))
        {
            return ParseResult.Failure("--blueprint must be a VRChat world ID beginning with 'wrld_'.");
        }

        bool capacitySpecified = values.ContainsKey("capacity");
        bool recommendedCapacitySpecified = values.ContainsKey("recommended-capacity");
        if (!TryParseCapacity(
                Get(values, "capacity"),
                Get(values, "recommended-capacity"),
                createWorld,
                out int capacity,
                out int recommendedCapacity,
                out string? capacityError))
        {
            return ParseResult.Failure(capacityError!);
        }

        if (!TryParsePlatform(platformValue, out VrcliPlatform platform))
        {
            return ParseResult.Failure("--platform must be StandaloneWindows64 or Android.");
        }

        string? password = Get(values, "password") ?? Environment.GetEnvironmentVariable("VRCLI_PASSWORD");

        if (string.IsNullOrEmpty(password))
        {
            return ParseResult.Failure("Set VRCLI_PASSWORD or provide --password <password>.");
        }

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
            : Get(values, "two-factor-code") ?? Environment.GetEnvironmentVariable("VRCLI_TWO_FACTOR_CODE");
        string? totpSecret = interactiveTwoFactor || !string.IsNullOrWhiteSpace(twoFactorCode)
            ? null
            : Environment.GetEnvironmentVariable("VRCLI_TOTP_SECRET");
        if (!string.IsNullOrWhiteSpace(twoFactorCode) && !string.IsNullOrWhiteSpace(totpSecret))
        {
            return ParseResult.Failure("Use only one of --two-factor-code/VRCLI_TWO_FACTOR_CODE or VRCLI_TOTP_SECRET.");
        }
        if (interactiveTwoFactor && values.ContainsKey("two-factor-code"))
        {
            return ParseResult.Failure("Use --interactive-two-factor by itself, without another two-factor option.");
        }

        DeployOptions options = new(
            fullProjectPath,
            blueprint,
            createWorld,
            worldName,
            worldDescription,
            fullThumbnailPath,
            capacity,
            recommendedCapacity,
            capacitySpecified,
            recommendedCapacitySpecified,
            tags.Distinct(StringComparer.Ordinal).ToArray(),
            tagsSpecified,
            blueprintOutputPath,
            username!,
            password,
            platform,
            Get(values, "scene"),
            Get(values, "unity"),
            twoFactorCode,
            totpSecret,
            interactiveTwoFactor,
            timeout,
            values.ContainsKey("skip-vpm-resolve"),
            values.ContainsKey("yes"),
            values.ContainsKey("verbose"),
            values.ContainsKey("tui") ? TerminalMode.Tui : values.ContainsKey("plain") ? TerminalMode.Plain : TerminalMode.Auto);

        return ParseResult.Success(options);
    }

    private static bool TryApplyProjectConfig(
        Dictionary<string, string?> values,
        List<string> tags,
        ref bool tagsSpecified,
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
            VrcliProjectConfig config = VrcliProjectConfig.Load(fullConfigPath);
            SetMissing(values, "project", ResolveFromConfig(config.Project, configDirectory));
            if (!values.ContainsKey("new"))
                SetMissing(values, "blueprint", config.Blueprint);
            if (!values.ContainsKey("blueprint") && config.NewWorld == true)
                values["new"] = "true";
            SetMissing(values, "scene", config.Scene);
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
            if (!tagsSpecified && config.Tags != null)
            {
                tags.AddRange(config.Tags);
                tagsSpecified = true;
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

    private static bool TryParsePlatform(string value, out VrcliPlatform platform)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "standalonewindows64":
                platform = VrcliPlatform.StandaloneWindows64;
                return true;
            case "android":
                platform = VrcliPlatform.Android;
                return true;
            default:
                platform = default;
                return false;
        }
    }

    private static bool TryParseCapacity(
        string? capacityValue,
        string? recommendedValue,
        bool createWorld,
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

        recommendedCapacity = createWorld ? Math.Min(16, capacity) : 16;
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
