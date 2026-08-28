using KibaLab.WorldDeployment;

namespace WorldDeployment.Tests;

public sealed class CommandLineParserTests
{
    [Fact]
    public void ParsesMetadataOnlyCommand()
    {
        ParseResult result = new CommandLineParser().Parse(
        [
            "meta",
            "--blueprint", "wrld_example",
            "--title", "Updated title",
            "--capacity", "64",
            "--login", "kibalab",
            "--password", "secret",
            "--plain"
        ]);

        Assert.Null(result.Error);
        Assert.Equal(OperationMode.Meta, result.Options?.Operation);
        Assert.Equal("wrld_example", result.Options?.BlueprintId);
        Assert.Equal("Updated title", result.Options?.Title);
        Assert.True(result.Options?.HasCapacity);
        Assert.Null(result.Options?.ScenePath);
    }

    [Fact]
    public void MetadataCommandRequiresAtLeastOneChange()
    {
        ParseResult result = new CommandLineParser().Parse(
        [
            "meta",
            "--blueprint", "wrld_example",
            "--login", "kibalab",
            "--password", "secret"
        ]);

        Assert.Contains("at least one metadata option", result.Error);
    }

    [Fact]
    public void ParsesCheckCommandWithoutBlueprint()
    {
        ParseResult result = new CommandLineParser().Parse(
        [
            "check",
            "--scene", "Assets/Scenes/Main.unity",
            "--platform", "Android",
            "--login", "kibalab",
            "--password", "secret",
            "--plain"
        ]);

        Assert.Null(result.Error);
        Assert.Equal(OperationMode.Check, result.Options?.Operation);
        Assert.Equal(string.Empty, result.Options?.BlueprintId);
        Assert.Equal(BuildPlatform.Android, result.Options?.Platform);
    }

    [Fact]
    public void ParsesExistingDeploymentWithoutBlueprintOverride()
    {
        ParseResult result = new CommandLineParser().Parse(
        [
            "deploy",
            "--project", ".",
            "--scene", "Assets/Scenes/Main.unity",
            "--platform", "StandaloneWindows64",
            "--login", "kibalab",
            "--password", "secret",
            "--plain"
        ]);

        Assert.Null(result.Error);
        Assert.Equal(OperationMode.Deploy, result.Options?.Operation);
        Assert.False(result.Options?.IsNew);
        Assert.Equal(string.Empty, result.Options?.BlueprintId);
    }

    [Fact]
    public void CheckCommandRejectsMetadataChanges()
    {
        ParseResult result = new CommandLineParser().Parse(
        [
            "check",
            "--title", "Should not change",
            "--login", "kibalab",
            "--password", "secret"
        ]);

        Assert.Contains("not valid with the check command", result.Error);
    }

    [Fact]
    public void RejectsUnknownCommand()
    {
        ParseResult result = new CommandLineParser().Parse(["publish"]);

        Assert.Contains("Expected deploy, meta, or check", result.Error);
    }

    [Fact]
    public void ParsesConcisePlainDeployment()
    {
        Environment.SetEnvironmentVariable("VRCLI_USERNAME", "kibalab");
        Environment.SetEnvironmentVariable("VRCLI_PASSWORD", "secret");
        try
        {
            CommandLineParser parser = new();
            ParseResult result = parser.Parse(
            [
                "deploy",
                "--blueprint",
                "wrld_example",
                "--platform",
                "Android",
                "--plain",
                "--yes"
            ]);

            Assert.Null(result.Error);
            Assert.Equal(Directory.GetCurrentDirectory(), result.Options?.ProjectPath);
            Assert.Equal(BuildPlatform.Android, result.Options?.Platform);
            Assert.Equal(TerminalMode.Plain, result.Options?.TerminalMode);
            Assert.True(result.Options?.OwnershipAccepted);
        }
        finally
        {
            Environment.SetEnvironmentVariable("VRCLI_USERNAME", null);
            Environment.SetEnvironmentVariable("VRCLI_PASSWORD", null);
        }
    }

    [Fact]
    public void UsesConventionalEnvironmentAndWindowsDefault()
    {
        Environment.SetEnvironmentVariable("VRCLI_USERNAME", "kibalab");
        Environment.SetEnvironmentVariable("VRCLI_PASSWORD", "secret");
        Environment.SetEnvironmentVariable("VRCLI_BLUEPRINT_ID", "wrld_from_environment");
        try
        {
            ParseResult result = new CommandLineParser().Parse(["deploy", "--plain"]);

            Assert.Null(result.Error);
            Assert.Equal("wrld_from_environment", result.Options?.BlueprintId);
            Assert.Equal(BuildPlatform.StandaloneWindows64, result.Options?.Platform);
        }
        finally
        {
            Environment.SetEnvironmentVariable("VRCLI_USERNAME", null);
            Environment.SetEnvironmentVariable("VRCLI_PASSWORD", null);
            Environment.SetEnvironmentVariable("VRCLI_BLUEPRINT_ID", null);
        }
    }

    [Fact]
    public void LoadsAConciseProjectConfiguration()
    {
        string directory = Path.Combine(Path.GetTempPath(), "vrcli-config-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string config = Path.Combine(directory, "vrcli.json");
        File.WriteAllText(config, """
            {
              "blueprint": "wrld_from_config",
              "scene": "Assets/Scenes/Main.unity",
              "platform": "Android",
              "plain": true,
              "yes": true
            }
            """);
        Environment.SetEnvironmentVariable("VRCLI_USERNAME", "kibalab");
        Environment.SetEnvironmentVariable("VRCLI_PASSWORD", "secret");
        try
        {
            ParseResult result = new CommandLineParser().Parse(
                ["deploy", "--config", config]);

            Assert.Null(result.Error);
            Assert.Equal(directory, result.Options?.ProjectPath);
            Assert.Equal("wrld_from_config", result.Options?.BlueprintId);
            Assert.Equal("Assets/Scenes/Main.unity", result.Options?.ScenePath);
            Assert.Equal(BuildPlatform.Android, result.Options?.Platform);
            Assert.Equal(TerminalMode.Plain, result.Options?.TerminalMode);
            Assert.True(result.Options?.OwnershipAccepted);
        }
        finally
        {
            Environment.SetEnvironmentVariable("VRCLI_USERNAME", null);
            Environment.SetEnvironmentVariable("VRCLI_PASSWORD", null);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LoadsNewWorldFlagAndTitleFromConfiguration()
    {
        string directory = Path.Combine(Path.GetTempPath(), "vrcli-new-config-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string config = Path.Combine(directory, "vrcli.json");
        File.WriteAllText(config, """
            {
              "new": true,
              "title": "Configured World",
              "thumbnail": "thumbnail.png"
            }
            """);
        Environment.SetEnvironmentVariable("VRCLI_USERNAME", "kibalab");
        Environment.SetEnvironmentVariable("VRCLI_PASSWORD", "secret");
        try
        {
            ParseResult result = new CommandLineParser().Parse(["deploy", "--config", config]);

            Assert.Null(result.Error);
            Assert.True(result.Options?.IsNew);
            Assert.Equal("Configured World", result.Options?.Title);
            Assert.Equal(Path.Combine(directory, "thumbnail.png"), result.Options?.ThumbnailPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("VRCLI_USERNAME", null);
            Environment.SetEnvironmentVariable("VRCLI_PASSWORD", null);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RejectsUnknownOrSecretConfigurationKeys()
    {
        string config = Path.Combine(Path.GetTempPath(), "vrcli-invalid-config-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(config, "{ \"blueprint\": \"wrld_example\", \"password\": \"must-not-live-here\" }");
        try
        {
            ParseResult result = new CommandLineParser().Parse(
                ["deploy", "--config", config, "--login", "kibalab", "--password", "secret"]);

            Assert.Contains("could not be mapped", result.Error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(config);
        }
    }

    [Fact]
    public void NewFlagUsesTitleMetadata()
    {
        CommandLineParser parser = new();
        ParseResult result = parser.Parse(
        [
            "deploy",
            "--new",
            "--title",
            "My World",
            "--thumbnail",
            "thumbnail.png",
            "--login",
            "kibalab",
            "--password",
            "secret",
            "--plain"
        ]);

        Assert.Null(result.Error);
        Assert.True(result.Options?.IsNew);
        Assert.Equal("My World", result.Options?.Title);
        Assert.Equal(BuildPlatform.StandaloneWindows64, result.Options?.Platform);
    }

    [Fact]
    public void ParsesRequestedCommandShape()
    {
        CommandLineParser parser = new();
        ParseResult result = parser.Parse(new[]
        {
            "--project", ".",
            "--scene", "Assets/Scenes/Main.unity",
            "--blueprint", "wrld_example",
            "--login", "kibalab",
            "--password", "1234",
            "--platform", "StandaloneWindows64"
        });

        Assert.Null(result.Error);
        Assert.NotNull(result.Options);
        Assert.Equal("kibalab", result.Options.Username);
        Assert.Equal(BuildPlatform.StandaloneWindows64, result.Options.Platform);
    }

    [Fact]
    public void RejectsRemovedIdAlias()
    {
        CommandLineParser parser = new();
        ParseResult result = parser.Parse(new[]
        {
            "deploy",
            "--project", ".",
            "--blueprint", "wrld_example",
            "--id", "kibalab",
            "--password", "1234",
            "--platform", "Android"
        });

        Assert.Contains("Unknown option", result.Error);
    }

    [Fact]
    public void ParsesPlainTerminalMode()
    {
        CommandLineParser parser = new();
        ParseResult result = parser.Parse(new[]
        {
            "--project", ".",
            "--blueprint", "wrld_example",
            "--login", "kibalab",
            "--password", "1234",
            "--platform", "Android",
            "--plain"
        });

        Assert.Null(result.Error);
        Assert.Equal(TerminalMode.Plain, result.Options?.TerminalMode);
    }

    [Fact]
    public void ParsesJsonTerminalMode()
    {
        CommandLineParser parser = new();
        ParseResult result = parser.Parse(new[]
        {
            "deploy",
            "--project", ".",
            "--login", "kibalab",
            "--password", "1234",
            "--json"
        });

        Assert.Null(result.Error);
        Assert.Equal(TerminalMode.Json, result.Options?.TerminalMode);
    }

    [Theory]
    [InlineData("--json", "--plain")]
    [InlineData("--json", "--tui")]
    [InlineData("--plain", "--tui")]
    public void RejectsConflictingOutputModes(string first, string second)
    {
        ParseResult result = new CommandLineParser().Parse(new[]
        {
            "deploy",
            "--project", ".",
            "--login", "kibalab",
            "--password", "1234",
            first,
            second
        });

        Assert.Contains("only one output mode", result.Error);
    }

    [Fact]
    public void InteractiveTwoFactorIgnoresDefaultTotpEnvironment()
    {
        Environment.SetEnvironmentVariable("VRCLI_TOTP_SECRET", "SHOULD_NOT_BE_USED");
        try
        {
            CommandLineParser parser = new();
            ParseResult result = parser.Parse(new[]
            {
                "--project", ".",
                "--blueprint", "wrld_example",
                "--login", "kibalab",
                "--password", "1234",
                "--platform", "Android",
                "--interactive-two-factor",
                "--tui"
            });

            Assert.Null(result.Error);
            Assert.True(result.Options?.InteractiveTwoFactor);
            Assert.Null(result.Options?.TotpSecret);
        }
        finally
        {
            Environment.SetEnvironmentVariable("VRCLI_TOTP_SECRET", null);
        }
    }

    [Fact]
    public void ExplicitCurrentCodeOverridesDefaultTotpEnvironment()
    {
        Environment.SetEnvironmentVariable("VRCLI_TOTP_SECRET", "DEFAULT_TOTP_SECRET");
        try
        {
            CommandLineParser parser = new();
            ParseResult result = parser.Parse(new[]
            {
                "--project", ".",
                "--blueprint", "wrld_example",
                "--login", "kibalab",
                "--password", "1234",
                "--platform", "Android",
                "--two-factor-code", "123456",
                "--two-factor-method", "totp"
            });

            Assert.Null(result.Error);
            Assert.Equal("123456", result.Options?.TwoFactorCode);
            Assert.Equal("totp", result.Options?.TwoFactorMethod);
            Assert.Null(result.Options?.TotpSecret);
        }
        finally
        {
            Environment.SetEnvironmentVariable("VRCLI_TOTP_SECRET", null);
        }
    }

    [Fact]
    public void CurrentCodeRequiresAnExplicitMethod()
    {
        ParseResult result = new CommandLineParser().Parse(new[]
        {
            "deploy",
            "--project", ".",
            "--login", "kibalab",
            "--password", "1234",
            "--two-factor-code", "123456"
        });

        Assert.Contains("--two-factor-method", result.Error);
    }

    [Theory]
    [InlineData("totp", "totp")]
    [InlineData("emailOtp", "emailOtp")]
    [InlineData("otp", "otp")]
    public void ParsesExplicitTwoFactorMethod(string supplied, string expected)
    {
        ParseResult result = new CommandLineParser().Parse(new[]
        {
            "deploy",
            "--project", ".",
            "--login", "kibalab",
            "--password", "1234",
            "--two-factor-code", "123456",
            "--two-factor-method", supplied
        });

        Assert.Null(result.Error);
        Assert.Equal(expected, result.Options?.TwoFactorMethod);
    }

    [Fact]
    public void SavedSessionDoesNotRequireAPassword()
    {
        Environment.SetEnvironmentVariable("VRCLI_AUTH_TOKEN", "saved-token");
        try
        {
            ParseResult result = new CommandLineParser().Parse(new[]
            {
                "deploy",
                "--project", ".",
                "--login", "kibalab"
            });

            Assert.Null(result.Error);
            Assert.Equal(string.Empty, result.Options?.Password);
        }
        finally
        {
            Environment.SetEnvironmentVariable("VRCLI_AUTH_TOKEN", null);
        }
    }

    [Fact]
    public void RejectsRemovedNoTuiAlias()
    {
        CommandLineParser parser = new();
        ParseResult result = parser.Parse(new[]
        {
            "--project", ".",
            "--blueprint", "wrld_example",
            "--login", "kibalab",
            "--password", "1234",
            "--platform", "Android",
            "--no-tui"
        });

        Assert.Contains("Unknown option", result.Error);
    }

    [Fact]
    public void RejectsNonWorldBlueprint()
    {
        CommandLineParser parser = new();
        ParseResult result = parser.Parse(new[]
        {
            "--project", ".",
            "--blueprint", "avtr_example",
            "--login", "kibalab",
            "--password", "1234",
            "--platform", "Android"
        });

        Assert.Contains("wrld_", result.Error);
    }

    [Fact]
    public void RejectsRemovedPasswordStdinOption()
    {
        CommandLineParser parser = new();
        ParseResult result = parser.Parse(new[]
        {
            "--project", ".",
            "--blueprint", "wrld_example",
            "--login", "kibalab",
            "--password-stdin",
            "--platform", "Android"
        });

        Assert.Contains("Unknown option", result.Error);
    }

    [Fact]
    public void ReadsTotpSecretFromFixedEnvironmentVariable()
    {
        const string variable = "VRCLI_TOTP_SECRET";
        Environment.SetEnvironmentVariable(variable, "JBSWY3DPEHPK3PXP");
        try
        {
            CommandLineParser parser = new();
            ParseResult result = parser.Parse(new[]
            {
                "--project", ".",
                "--blueprint", "wrld_example",
                "--login", "kibalab",
                "--password", "1234",
                "--platform", "Android"
            });

            Assert.Null(result.Error);
            Assert.Equal("JBSWY3DPEHPK3PXP", result.Options?.TotpSecret);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public void ParsesNewWorldOptionsAndAllocatesBlueprint()
    {
        CommandLineParser parser = new();
        ParseResult result = parser.Parse(new[]
        {
            "deploy",
            "--project", ".",
            "--new",
            "--title", "My New World",
            "--description", "A description",
            "--thumbnail", "thumbnail.png",
            "--capacity", "48",
            "--recommended-capacity", "24",
            "--tag", "author_tag_social",
            "--tag", "content_other",
            "--blueprint-output", "blueprint.txt",
            "--login", "kibalab",
            "--password", "1234",
            "--platform", "StandaloneWindows64"
        });

        Assert.Null(result.Error);
        Assert.NotNull(result.Options);
        Assert.True(result.Options.IsNew);
        Assert.StartsWith("wrld_", result.Options.BlueprintId);
        Assert.Equal("My New World", result.Options.Title);
        Assert.Equal(48, result.Options.Capacity);
        Assert.Equal(24, result.Options.RecommendedCapacity);
        Assert.Equal(new[] { "author_tag_social", "content_other" }, result.Options.Tags);
        Assert.NotNull(result.Options.ThumbnailPath);
        Assert.NotNull(result.Options.BlueprintOutputPath);
        Assert.True(Path.IsPathFullyQualified(result.Options.ThumbnailPath!));
        Assert.True(Path.IsPathFullyQualified(result.Options.BlueprintOutputPath!));
    }

    [Fact]
    public void PreservesNewWorldBlueprintDuringAuthenticationRetry()
    {
        CommandLineParser parser = new();
        ParseResult result = parser.Parse(new[]
        {
            "deploy",
            "--project", ".",
            "--new",
            "--title", "My New World",
            "--thumbnail", "thumbnail.png",
            "--login", "kibalab",
            "--password", "1234",
            "--platform", "StandaloneWindows64"
        }, "wrld_preserved_for_retry");

        Assert.Null(result.Error);
        Assert.Equal("wrld_preserved_for_retry", result.Options?.BlueprintId);
    }

    [Fact]
    public void RejectsBlueprintTogetherWithNew()
    {
        CommandLineParser parser = new();
        ParseResult result = parser.Parse(new[]
        {
            "--project", ".",
            "--new",
            "--blueprint", "wrld_example",
            "--title", "My World",
            "--thumbnail", "thumbnail.png",
            "--login", "kibalab",
            "--password", "1234",
            "--platform", "Android"
        });

        Assert.Contains("Choose one target", result.Error);
    }

    [Fact]
    public void RequiresTitleAndThumbnailForNewWorld()
    {
        CommandLineParser parser = new();
        ParseResult result = parser.Parse(new[]
        {
            "--project", ".",
            "--new",
            "--login", "kibalab",
            "--password", "1234",
            "--platform", "Android"
        });

        Assert.Contains("--title", result.Error);
        Assert.Contains("--thumbnail", result.Error);
    }

    [Fact]
    public void RejectsRecommendedCapacityAboveMaximum()
    {
        CommandLineParser parser = new();
        ParseResult result = parser.Parse(new[]
        {
            "--project", ".",
            "--new",
            "--title", "My World",
            "--thumbnail", "thumbnail.png",
            "--capacity", "10",
            "--recommended-capacity", "11",
            "--login", "kibalab",
            "--password", "1234",
            "--platform", "Android"
        });

        Assert.Contains("--capacity", result.Error);
    }

    [Fact]
    public void ParsesMetadataUpdatesForExistingWorld()
    {
        CommandLineParser parser = new();
        ParseResult result = parser.Parse(new[]
        {
            "--project", ".",
            "--blueprint", "wrld_example",
            "--title", "Updated World",
            "--description", "Updated description",
            "--thumbnail", "updated-thumbnail.png",
            "--capacity", "40",
            "--recommended-capacity", "20",
            "--tag", "author_tag_social",
            "--login", "kibalab",
            "--password", "1234",
            "--platform", "Android"
        });

        Assert.Null(result.Error);
        Assert.NotNull(result.Options);
        Assert.False(result.Options.IsNew);
        Assert.Equal("Updated World", result.Options.Title);
        Assert.Equal("Updated description", result.Options.Description);
        Assert.NotNull(result.Options.ThumbnailPath);
        Assert.Equal(40, result.Options.Capacity);
        Assert.Equal(20, result.Options.RecommendedCapacity);
        Assert.True(result.Options.HasCapacity);
        Assert.True(result.Options.HasRecommendedCapacity);
        Assert.True(result.Options.HasTags);
        Assert.Equal(["author_tag_social"], result.Options.Tags);
    }

    [Fact]
    public void LeavesExistingWorldMetadataUnchangedWhenNotSpecified()
    {
        ParseResult result = new CommandLineParser().Parse(new[]
        {
            "--blueprint", "wrld_example",
            "--login", "kibalab",
            "--password", "1234",
            "--platform", "StandaloneWindows64"
        });

        Assert.Null(result.Error);
        Assert.Null(result.Options?.Title);
        Assert.Null(result.Options?.Description);
        Assert.False(result.Options?.HasCapacity);
        Assert.False(result.Options?.HasRecommendedCapacity);
        Assert.False(result.Options?.HasTags);
    }

    [Fact]
    public void AcceptsRecommendedCapacityWithoutChangingMaximumCapacity()
    {
        ParseResult result = new CommandLineParser().Parse(new[]
        {
            "--blueprint", "wrld_example",
            "--recommended-capacity", "64",
            "--login", "kibalab",
            "--password", "1234",
            "--platform", "Android"
        });

        Assert.Null(result.Error);
        Assert.False(result.Options?.HasCapacity);
        Assert.True(result.Options?.HasRecommendedCapacity);
        Assert.Equal(64, result.Options?.RecommendedCapacity);
    }

    [Fact]
    public void LoginAndPasswordCanBeProvidedTogether()
    {
        ParseResult result = new CommandLineParser().Parse(new[]
        {
            "--blueprint", "wrld_example",
            "--login", "user@example.com",
            "--password", "secret",
            "--platform", "StandaloneWindows64"
        });

        Assert.Null(result.Error);
        Assert.Equal("user@example.com", result.Options?.Username);
        Assert.Equal("secret", result.Options?.Password);
    }

    [Fact]
    public void RejectsANamePlacedDirectlyAfterNewFlag()
    {
        ParseResult result = new CommandLineParser().Parse(new[]
        {
            "--new", "My World",
            "--thumbnail", "thumbnail.png",
            "--login", "kibalab",
            "--password", "1234"
        });

        Assert.Contains("--title", result.Error);
    }

    [Theory]
    [InlineData("--path")]
    [InlineData("--world")]
    [InlineData("--create")]
    [InlineData("--name")]
    [InlineData("--username")]
    [InlineData("--id")]
    [InlineData("--no-tui")]
    [InlineData("--accept-content-ownership")]
    [InlineData("--password-env")]
    [InlineData("--password-stdin")]
    [InlineData("--two-factor-code-env")]
    [InlineData("--totp-secret-env")]
    public void RejectsRemovedCompatibilityOptions(string option)
    {
        ParseResult result = new CommandLineParser().Parse(["deploy", option]);

        Assert.Equal($"Unknown option: {option}", result.Error);
    }

    [Theory]
    [InlineData("windows")]
    [InlineData("win")]
    [InlineData("pc")]
    [InlineData("quest")]
    public void RejectsRemovedPlatformAliases(string platform)
    {
        ParseResult result = new CommandLineParser().Parse(new[]
        {
            "--blueprint", "wrld_example",
            "--login", "kibalab",
            "--password", "1234",
            "--platform", platform
        });

        Assert.Contains("StandaloneWindows64 or Android", result.Error);
    }

    [Fact]
    public void AcceptsCapacityAboveEighty()
    {
        ParseResult result = new CommandLineParser().Parse(new[]
        {
            "--new",
            "--title", "Large World",
            "--thumbnail", "thumbnail.png",
            "--capacity", "256",
            "--recommended-capacity", "128",
            "--login", "kibalab",
            "--password", "1234",
            "--platform", "StandaloneWindows64"
        });

        Assert.Null(result.Error);
        Assert.Equal(256, result.Options?.Capacity);
        Assert.Equal(128, result.Options?.RecommendedCapacity);
    }
}
